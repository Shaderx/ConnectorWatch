using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch;

/// <summary>
/// Read-only NVIDIA private power-monitor provider for the ASUS TUF RTX 5090
/// on the validated 616.56 driver path.
///
/// The private ABI is intentionally kept in this file.  The daemon only needs
/// the IVoltageSource contract and does not need to know about NVAPI buffers.
/// A612 is called once during construction to obtain the monitor mask and
/// channel metadata.  Every subsequent sample starts with a fresh zeroed A613
/// buffer and uses that mask as input.
/// </summary>
public sealed class DirectNvRails : IElectricalSource, IDisposable
{
    public const uint MetadataFunctionId = 0xC12EB19E;
    public const uint StatusFunctionId = 0xF40238EF;
    public const uint MetadataHeader = 0x00030CA8;
    public const uint StatusHeader = 0x0001059C;
    public const int MetadataSize = 0xCA8;
    public const int StatusSize = 0x59C;
    public const int DefaultTimeoutMilliseconds = 1500;
    public const string FreshnessMarker = "host-poll-time; native freshness unverified";

    // The NVAPI PCI identifier is DEVICE:VENDOR packed in one dword on the
    // installed Windows driver.  These are the exact validated target gates.
    public const uint TargetPciIdentifier = 0x2B8510DE;
    public const uint TargetSubsystemIdentifier = 0x89EE1043;
    public const string ExpectedDriverVersion = "616.56";

    private const uint NvApiInitializeId = 0x0150E828;
    private const uint NvApiUnloadId = 0xD22BDD7E;
    private const uint NvApiEnumPhysicalGpusId = 0xE5AC921F;
    private const uint NvApiPciIdentifiersId = 0x2DDFB66E;
    private const uint RequiredRailMask = (1u << 1) | (1u << 2);
    private const int MetadataRecordBase = 0x18;
    private const int MetadataRecordStride = 0x3C;
    private const int MetadataRecordCount = 32;
    private const int MetadataTypeOffset = 0x1C;
    private const int MetadataSourceOffset = 0x20;
    private const int StatusRecordBase = 0x08;
    private const int StatusRecordStride = 0x2C;
    private const int StatusCurrentOffset = 0x20;
    private const int StatusVoltageOffset = 0x24;
    private const uint PcieChannelSource = 255;
    private const uint HpwrChannelSource = 218;
    private const uint RailMetadataType = 8;
    private const double MinRailVoltage = 6.0;
    private const double MaxRailVoltage = 16.0;
    private const uint MaxRailCurrentMilliamps = 200_000;
    private const double MaxRailPowerWatts = 3_000.0;
    private const int CanaryBytes = 64;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiNoArgs();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiEnumeratePhysicalGpus([Out] IntPtr[] devices, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiGetPciIdentifiers(IntPtr device, out uint deviceId,
        out uint subsystemId, out uint revisionId, out uint externalDeviceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiPrivateCall(IntPtr device, IntPtr request);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetCount(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleByUuid([MarshalAs(UnmanagedType.LPStr)] string uuid,
        out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSystemGetDriverVersion([Out] StringBuilder version, uint length);

    private readonly object _gate = new();
    private readonly string _gpuUuid;
    private readonly int _timeoutMilliseconds;
    private readonly IntPtr _nvApiModule;
    private readonly NvApiNoArgs _nvApiUnload;
    private readonly NvApiPrivateCall _metadataCall;
    private readonly NvApiPrivateCall _statusCall;
    private readonly IntPtr _gpuHandle;
    private readonly DirectRailMetadata _metadata;
    private bool _nvApiInitialized;
    private bool _disposed;
    private bool _terminal;
    private bool _timedOut;

    /// <summary>
    /// Initializes the persistent NVAPI session and validates the target.
    /// The configured UUID is resolved through NVML.  NVAPI does not expose a
    /// UUID-to-handle bridge in this private path, so the provider also
    /// requires exactly one GPU in both NVML and NVAPI, with the validated
    /// NVAPI PCI identity. Multi-GPU systems fail closed instead of guessing.
    ///
    /// The watchdog timeout applies only to the private A612/A613 telemetry
    /// calls. NVML identity checks and public NVAPI setup calls are
    /// synchronous setup operations and have no worker watchdog.
    /// </summary>
    public DirectNvRails(string gpuUuid, int timeoutMilliseconds = DefaultTimeoutMilliseconds)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Direct NVAPI rails require Windows x64.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Direct NVAPI rails require a Windows x64 process.");
        if (string.IsNullOrWhiteSpace(gpuUuid))
            throw new ArgumentException("A configured NVML GPU UUID is required.", nameof(gpuUuid));
        if (timeoutMilliseconds < 100 || timeoutMilliseconds > 10_000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds),
                "The native-call watchdog must be between 100 and 10000 milliseconds.");

        _gpuUuid = gpuUuid.Trim();
        _timeoutMilliseconds = timeoutMilliseconds;
        IntPtr module = IntPtr.Zero;
        try
        {
            module = LoadSystem32Library("nvapi64.dll");
            _nvApiModule = module;

            var query = GetExport(module, "nvapi_QueryInterface");
            _nvApiUnload = Function<NvApiNoArgs>(query, NvApiUnloadId);
            var initialize = Function<NvApiNoArgs>(query, NvApiInitializeId);
            var enumerate = Function<NvApiEnumeratePhysicalGpus>(query, NvApiEnumPhysicalGpusId);
            var getPci = Function<NvApiGetPciIdentifiers>(query, NvApiPciIdentifiersId);
            _metadataCall = Function<NvApiPrivateCall>(query, MetadataFunctionId);
            _statusCall = Function<NvApiPrivateCall>(query, StatusFunctionId);

            ValidateNvmlIdentity(_gpuUuid);

            var initStatus = initialize();
            if (initStatus != 0)
                throw new DirectNvRailsException($"NVAPI initialization failed with status {initStatus}.");
            _nvApiInitialized = true;
            _gpuHandle = SelectUniqueTarget(enumerate, getPci);

            var metadataBuffer = CreateMetadataRequest();
            InvokePrivate(_metadataCall, metadataBuffer, "A612 metadata");
            _metadata = DecodeMetadata(metadataBuffer);
        }
        catch
        {
            // A timed-out native call may still execute on its background
            // worker.  In that case the provider and NVAPI module deliberately
            // stay loaded until process exit.
            if (!_timedOut)
                CleanupNativeState(module);
            throw;
        }
    }

    /// <summary>Convenience overload for the existing ConnectorWatch config.</summary>
    public DirectNvRails(Config config)
        : this(config?.GpuUuid ?? throw new ArgumentNullException(nameof(config)))
    {
    }

    public string Description =>
        $"direct NVIDIA rails (PCIe +12V and 12VHPWR; A612/A613; driver {ExpectedDriverVersion}; UUID {_gpuUuid})";

    public string GpuUuid => _gpuUuid;
    public DirectRailMetadata Metadata => _metadata;
    public bool IsTerminal => _terminal;
    public bool TimedOut => _timedOut;
    public bool TerminalOnFailure => true;

    /// <summary>
    /// Reads one typed sample. Both rails' voltage/current/power remain typed
    /// through the analysis boundary; power is explicitly marked as derived
    /// from the same rail's V×I values. The sample timestamp is the host poll
    /// time because the private payload has no validated hardware timestamp.
    /// </summary>
    public ElectricalSample ReadElectrical(DateTimeOffset now)
    {
        lock (_gate)
        {
            EnsureUsable();
            var statusBuffer = CreateStatusRequest(_metadata.Mask);
            try
            {
                InvokePrivate(_statusCall, statusBuffer, "A613 status");
                var sample = DecodeStatus(statusBuffer, _metadata, now);
                return ToElectricalSample(sample, Description);
            }
            catch (DirectNvRailsException)
            {
                _terminal = true;
                throw;
            }
            catch (Exception ex)
            {
                _terminal = true;
                throw new DirectNvRailsException("A613 status validation failed; monitoring is terminal.", ex);
            }
        }
    }

    /// <summary>
    /// Legacy adapter retained for existing daemon and package consumers. It
    /// projects the typed sample back into the original Voltage/Extras shape.
    /// New analysis code should use ReadElectrical instead of decoding Extras.
    /// </summary>
    public Voltage Read(DateTimeOffset now) => ReadElectrical(now).ToLegacyVoltage();

    /// <summary>
    /// Creates the exact zeroed A612 request.  This is public so the portable
    /// daemon's offline tests can exercise the ABI without loading NVAPI.
    /// </summary>
    public static byte[] CreateMetadataRequest()
    {
        var request = new byte[MetadataSize];
        WriteUInt32(request, 0, MetadataHeader);
        return request;
    }

    /// <summary>
    /// Creates the exact zeroed A613 request for a previously validated mask.
    /// </summary>
    public static byte[] CreateStatusRequest(uint mask)
    {
        if (mask == 0 || (mask & RequiredRailMask) != RequiredRailMask)
            throw new ArgumentException("The A613 mask must include channels 1 and 2.", nameof(mask));
        var request = new byte[StatusSize];
        WriteUInt32(request, 0, StatusHeader);
        WriteUInt32(request, 4, mask);
        return request;
    }

    /// <summary>
    /// Offline A612 decoder and ABI validator.  No native library is touched.
    /// </summary>
    public static DirectRailMetadata DecodeMetadata(ReadOnlySpan<byte> response)
    {
        RequireLength(response, MetadataSize, "A612 metadata");
        RequireHeader(response, 0, MetadataHeader, "A612 metadata");

        var flag = ReadUInt32(response, 4);
        if (flag != 1)
            throw new InvalidDataException($"A612 metadata flag at +0x04 was {flag}; expected 1.");

        var mask = ReadUInt32(response, 0x10);
        if (mask == 0 || (mask & RequiredRailMask) != RequiredRailMask)
            throw new InvalidDataException($"A612 metadata mask 0x{mask:X8} does not include channels 1 and 2.");

        // Only the first 32 records are the channel table.  The remaining
        // bytes in the 0xCA8 caller extent are other private metadata and must
        // never be interpreted as additional channels.
        var pcie = ReadMetadataChannel(response, 1, PcieChannelSource, "PCIe +12V", 1);
        var hpwr = ReadMetadataChannel(response, 2, HpwrChannelSource, "12VHPWR", 2);
        return new DirectRailMetadata(mask, pcie, hpwr, MetadataRecordCount);
    }

    /// <summary>
    /// Offline A613 decoder and ABI validator.  No native library is touched.
    /// </summary>
    public static DirectRailSample DecodeStatus(ReadOnlySpan<byte> response,
        DirectRailMetadata metadata, DateTimeOffset timestamp)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));
        RequireLength(response, StatusSize, "A613 status");
        RequireHeader(response, 0, StatusHeader, "A613 status");
        ValidateMetadataObject(metadata);

        var mask = ReadUInt32(response, 4);
        if (mask != metadata.Mask)
            throw new InvalidDataException($"A613 status mask 0x{mask:X8} differs from A612 mask 0x{metadata.Mask:X8}.");
        if ((mask & RequiredRailMask) != RequiredRailMask)
            throw new InvalidDataException($"A613 status mask 0x{mask:X8} does not include channels 1 and 2.");

        var pcie = DecodeChannel(response, metadata.Pcie12V, timestamp);
        var hpwr = DecodeChannel(response, metadata.TwelveVHpwr, timestamp);
        return new DirectRailSample(timestamp, mask, pcie, hpwr);
    }

    /// <summary>
    /// Converts an already validated offline sample to the daemon contract.
    /// This keeps the opaque +0x28 values and freshness marker testable without
    /// loading a native provider.
    /// </summary>
    public static Voltage ToVoltage(DirectRailSample sample)
    {
        if (sample is null) throw new ArgumentNullException(nameof(sample));
        var extras = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["pcie_12v_v"] = sample.Pcie12V.Volts,
            ["pcie_12v_a"] = sample.Pcie12V.Amps,
            ["pcie_12v_w"] = sample.Pcie12V.Watts,
            ["pcie_12v_raw_status_dword_28"] = sample.Pcie12V.RawStatusDword28,
            ["12vhpwr_v"] = sample.TwelveVHpwr.Volts,
            ["12vhpwr_a"] = sample.TwelveVHpwr.Amps,
            ["12vhpwr_w"] = sample.TwelveVHpwr.Watts,
            ["12vhpwr_raw_status_dword_28"] = sample.TwelveVHpwr.RawStatusDword28,
            ["freshness"] = FreshnessMarker,
        };
        return new Voltage(sample.Timestamp, sample.TwelveVHpwr.Volts, sample.TwelveVHpwr.Watts,
            JsonSerializer.Serialize(extras));
    }

    /// <summary>
    /// Converts an already validated direct sample into the typed electrical
    /// contract.  This method is offline-testable and does not load a native
    /// library.  The original Extras payload is retained verbatim through the
    /// legacy projection for additive compatibility.
    /// </summary>
    public static ElectricalSample ToElectricalSample(DirectRailSample sample,
        string source = "direct NVIDIA rails")
    {
        if (sample is null) throw new ArgumentNullException(nameof(sample));
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A source identity is required.", nameof(source));

        var freshness = FreshnessMetadata.HostPoll(sample.Timestamp, FreshnessMarker);
        var pcie = ElectricalRailSample.Create(
            sample.Pcie12V.Volts,
            sample.Pcie12V.Amps,
            sample.Pcie12V.Watts,
            ElectricalPowerProvenance.DerivedFromVoltageAndCurrent,
            source, sample.Timestamp, freshness);
        var connector = ElectricalRailSample.Create(
            sample.TwelveVHpwr.Volts,
            sample.TwelveVHpwr.Amps,
            sample.TwelveVHpwr.Watts,
            ElectricalPowerProvenance.DerivedFromVoltageAndCurrent,
            source, sample.Timestamp, freshness);
        var legacy = ToVoltage(sample);
        return new ElectricalSample(sample.Timestamp, source, connector, pcie,
            ElectricalPowerReading.Unsupported(source, sample.Timestamp, freshness,
                "Direct native samples do not provide an independent external power sensor."),
            freshness, legacy.Extras);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_timedOut) return;
            CleanupNativeState(_nvApiModule);
        }
        GC.SuppressFinalize(this);
    }

    private void EnsureUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DirectNvRails));
        if (_terminal)
            throw new DirectNvRailsException("Direct NVAPI rail monitoring is in a terminal state; restart the monitor process.");
    }

    private void InvokePrivate(NvApiPrivateCall entry, byte[] managedBuffer, string operation)
    {
        var worker = new NativeWorker(entry, _gpuHandle, managedBuffer);
        worker.Start();
        if (!worker.Join(_timeoutMilliseconds))
        {
            worker.AbandonAfterTimeout();
            _timedOut = true;
            _terminal = true;
            throw new DirectNvRailsException(
                $"{operation} exceeded the {_timeoutMilliseconds} ms native-call watchdog; monitoring must terminate and NVAPI remains loaded until process exit.");
        }

        if (worker.Error is not null)
        {
            _terminal = true;
            throw new DirectNvRailsException($"{operation} failed inside the native worker; monitoring is terminal.", worker.Error);
        }
        if (worker.GuardStatus != "pass")
        {
            _terminal = true;
            throw new DirectNvRailsException($"{operation} changed a native buffer canary ({worker.GuardStatus}); monitoring is terminal.");
        }
        if (worker.ReturnCode != 0)
        {
            _terminal = true;
            throw new DirectNvRailsException($"{operation} returned NVAPI status {worker.ReturnCode}; no retry will be attempted.");
        }
    }

    private static DirectRailChannelMetadata ReadMetadataChannel(ReadOnlySpan<byte> response,
        int recordIndex, uint source, string label, int channel)
    {
        var offset = MetadataRecordBase + recordIndex * MetadataRecordStride;
        var type = ReadUInt32(response, offset + MetadataTypeOffset);
        var recordSource = ReadUInt32(response, offset + MetadataSourceOffset);
        if (type != RailMetadataType || recordSource != source)
            throw new InvalidDataException(
                $"A612 metadata record {recordIndex} was not the required {label} entry " +
                $"(type/source {type}/{recordSource}; expected {RailMetadataType}/{source}).");
        return new DirectRailChannelMetadata(channel, recordIndex, type, recordSource);
    }

    private static void ValidateMetadataObject(DirectRailMetadata metadata)
    {
        if (metadata.RecordCount != MetadataRecordCount || metadata.Mask == 0 ||
            (metadata.Mask & RequiredRailMask) != RequiredRailMask ||
            metadata.Pcie12V.Channel != 1 || metadata.Pcie12V.RecordIndex != 1 ||
            metadata.Pcie12V.Type != RailMetadataType || metadata.Pcie12V.Source != PcieChannelSource ||
            metadata.TwelveVHpwr.Channel != 2 || metadata.TwelveVHpwr.RecordIndex != 2 ||
            metadata.TwelveVHpwr.Type != RailMetadataType || metadata.TwelveVHpwr.Source != HpwrChannelSource)
            throw new InvalidDataException("A612 metadata channel table does not match the validated channel contract.");
    }

    private static DirectRailChannelSample DecodeChannel(ReadOnlySpan<byte> response,
        DirectRailChannelMetadata metadata, DateTimeOffset timestamp)
    {
        if (metadata.Channel is < 0 or > 31)
            throw new InvalidDataException($"A613 channel index {metadata.Channel} is outside the 32-record status ABI.");
        var offset = StatusRecordBase + metadata.Channel * StatusRecordStride;
        var currentMilliamps = ReadUInt32(response, offset + StatusCurrentOffset);
        var voltageMicrovolts = ReadUInt32(response, offset + StatusVoltageOffset);
        // +0x28 is retained as an opaque status dword. The installed ABI
        // does not establish units or timestamp semantics for this field.
        var rawStatusDword28 = ReadUInt32(response, offset + 0x28);
        if (currentMilliamps > MaxRailCurrentMilliamps)
            throw new InvalidDataException($"A613 channel {metadata.Channel} current {currentMilliamps} mA is implausible.");
        if (voltageMicrovolts == 0)
            throw new InvalidDataException($"A613 channel {metadata.Channel} returned zero voltage.");

        var volts = voltageMicrovolts / 1_000_000.0;
        var amps = currentMilliamps / 1_000.0;
        var watts = currentMilliamps * (double)voltageMicrovolts / 1_000_000_000.0;
        if (!double.IsFinite(volts) || volts < MinRailVoltage || volts > MaxRailVoltage)
            throw new InvalidDataException($"A613 channel {metadata.Channel} voltage {volts} V is outside the input-rail bounds.");
        if (!double.IsFinite(amps) || !double.IsFinite(watts) || watts < 0 || watts > MaxRailPowerWatts)
            throw new InvalidDataException($"A613 channel {metadata.Channel} returned non-finite or implausible electrical values.");

        return new DirectRailChannelSample(metadata.Channel, metadata.Source, currentMilliamps,
            voltageMicrovolts, volts, amps, watts, timestamp)
        {
            RawStatusDword28 = rawStatusDword28,
        };
    }

    private static void RequireLength(ReadOnlySpan<byte> buffer, int expected, string operation)
    {
        if (buffer.Length != expected)
            throw new InvalidDataException($"{operation} buffer size was 0x{buffer.Length:X}; expected 0x{expected:X}.");
    }

    private static void RequireHeader(ReadOnlySpan<byte> buffer, int offset, uint expected, string operation)
    {
        var actual = ReadUInt32(buffer, offset);
        if (actual != expected)
            throw new InvalidDataException($"{operation} header was 0x{actual:X8}; expected 0x{expected:X8}.");
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset < 0 || offset > buffer.Length - sizeof(uint))
            throw new InvalidDataException($"Buffer read at +0x{offset:X} is outside the ABI extent.");
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, sizeof(uint)));
    }

    private static void WriteUInt32(Span<byte> buffer, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset, sizeof(uint)), value);
    }

    private static IntPtr LoadSystem32Library(string name)
    {
        var systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory))
            throw new DirectNvRailsException("Windows system directory is unavailable.");
        var path = Path.Combine(systemDirectory, name);
        if (!File.Exists(path))
            throw new DirectNvRailsException($"Required Windows driver library is missing: {path}");
        try
        {
            return NativeLibrary.Load(path);
        }
        catch (Exception ex)
        {
            throw new DirectNvRailsException($"Could not load {path}.", ex);
        }
    }

    private static IntPtr GetExport(IntPtr module, string name)
    {
        try
        {
            var export = NativeLibrary.GetExport(module, name);
            if (export == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return export;
        }
        catch (Exception ex)
        {
            throw new DirectNvRailsException($"NVAPI export {name} is unavailable.", ex);
        }
    }

    private static T Function<T>(IntPtr queryPointer, uint id) where T : Delegate
    {
        var query = Marshal.GetDelegateForFunctionPointer<NvApiQueryInterface>(queryPointer);
        var entry = query(id);
        if (entry == IntPtr.Zero)
            throw new DirectNvRailsException($"NVAPI interface 0x{id:X8} is unavailable.");
        return Marshal.GetDelegateForFunctionPointer<T>(entry);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NvApiQueryInterface(uint id);

    private static IntPtr SelectUniqueTarget(NvApiEnumeratePhysicalGpus enumerate,
        NvApiGetPciIdentifiers getPci)
    {
        var handles = new IntPtr[64];
        var status = enumerate(handles, out var count);
        if (status != 0 || count > handles.Length)
            throw new DirectNvRailsException($"NVAPI physical-GPU enumeration returned status {status} and count {count}.");
        RequireSingleGpu(status, count, "NVAPI");

        var matches = new List<IntPtr>();
        for (var i = 0; i < count; i++)
        {
            if (handles[i] == IntPtr.Zero) continue;
            var pciStatus = getPci(handles[i], out var pci, out var subsystem, out _, out _);
            if (pciStatus == 0 && pci == TargetPciIdentifier && subsystem == TargetSubsystemIdentifier)
                matches.Add(handles[i]);
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new DirectNvRailsException(
                $"No NVAPI adapter matched PCI 0x{TargetPciIdentifier:X8} and subsystem 0x{TargetSubsystemIdentifier:X8}."),
            _ => throw new DirectNvRailsException(
                $"{matches.Count} NVAPI adapters matched the target PCI identity; refusing to guess a GPU handle."),
        };
    }

    private static void ValidateNvmlIdentity(string uuid)
    {
        IntPtr module = IntPtr.Zero;
        var initialized = false;
        try
        {
            module = LoadSystem32Library("nvml.dll");
            var init = GetNvmlExport<NvmlInit>(module, "nvmlInit_v2");
            var shutdown = GetNvmlExport<NvmlShutdown>(module, "nvmlShutdown");
            var byUuid = GetNvmlExport<NvmlDeviceGetHandleByUuid>(module, "nvmlDeviceGetHandleByUUID");
            var getDriver = GetNvmlExport<NvmlSystemGetDriverVersion>(module, "nvmlSystemGetDriverVersion");

            var initStatus = init();
            if (initStatus != 0)
                throw new DirectNvRailsException($"NVML initialization failed with status {initStatus}.");
            initialized = true;

            var getCount = GetNvmlExport<NvmlDeviceGetCount>(module, "nvmlDeviceGetCount_v2");
            var countStatus = getCount(out var count);
            RequireSingleGpu(countStatus, count, "NVML");

            var uuidStatus = byUuid(uuid, out var device);
            if (uuidStatus != 0 || device == IntPtr.Zero)
                throw new DirectNvRailsException($"NVML could not resolve configured GPU UUID {uuid} (status {uuidStatus}).");

            var version = new StringBuilder(64);
            var driverStatus = getDriver(version, (uint)version.Capacity);
            if (driverStatus != 0 || !string.Equals(version.ToString(), ExpectedDriverVersion,
                    StringComparison.OrdinalIgnoreCase))
                throw new DirectNvRailsException(
                    $"The installed NVIDIA driver was '{version}' (status {driverStatus}); expected {ExpectedDriverVersion}.");

            shutdown();
            initialized = false;
        }
        finally
        {
            if (initialized)
            {
                // A failed UUID/version gate still needs a balanced NVML
                // shutdown.  A shutdown failure does not make the gate safer.
                try
                {
                    if (module != IntPtr.Zero)
                    {
                        var shutdown = GetNvmlExport<NvmlShutdown>(module, "nvmlShutdown");
                        shutdown();
                    }
                }
                catch
                {
                    // Preserve the original gate failure.
                }
            }
            if (module != IntPtr.Zero)
                NativeLibrary.Free(module);
        }
    }

    private static T GetNvmlExport<T>(IntPtr module, string name) where T : Delegate
    {
        try
        {
            var pointer = NativeLibrary.GetExport(module, name);
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }
        catch (Exception ex)
        {
            throw new DirectNvRailsException($"NVML export {name} is unavailable.", ex);
        }
    }

    internal static void RequireSingleGpu(int status, uint count, string api)
    {
        if (status != 0 || count != 1)
            throw new DirectNvRailsException(
                $"Direct rails require exactly one NVIDIA GPU in both NVML and NVAPI; {api} returned status {status}, count {count}. Multi-GPU systems are unsupported.");
    }

    private void CleanupNativeState(IntPtr module)
    {
        if (_nvApiInitialized)
        {
            try { _nvApiUnload(); }
            catch { /* cleanup must not hide the original failure */ }
            _nvApiInitialized = false;
        }
        if (module != IntPtr.Zero)
        {
            try { NativeLibrary.Free(module); }
            catch { /* cleanup must not hide the original failure */ }
        }
    }

    private sealed class NativeWorker
    {
        private readonly NvApiPrivateCall _entry;
        private readonly IntPtr _device;
        private readonly byte[] _buffer;
        private int _abandoned;

        public NativeWorker(NvApiPrivateCall entry, IntPtr device, byte[] buffer)
        {
            _entry = entry;
            _device = device;
            _buffer = buffer;
        }

        public int ReturnCode { get; private set; } = int.MinValue;
        public Exception? Error { get; private set; }
        public string GuardStatus { get; private set; } = "not-checked-error";
        private Task? _task;

        public void AbandonAfterTimeout() => Interlocked.Exchange(ref _abandoned, 1);

        public void Start()
        {
            _task = Task.Run(Work);
        }

        public bool Join(int timeoutMilliseconds)
        {
            var task = _task ?? throw new InvalidOperationException("The native worker has not started.");
            bool completed = false;
            try
            {
                // The timeout includes thread-pool scheduling time.  A timed
                // out native call remains in flight until the process exits.
                completed = task.Wait(timeoutMilliseconds);
                return completed;
            }
            finally
            {
                // Do not dispose an incomplete task: its Work delegate still
                // owns the unmanaged request buffer. A completed task has no
                // remaining wait state and can release its task resources.
                if (completed) task.Dispose();
            }
        }

        private void Work()
        {
            IntPtr allocation = IntPtr.Zero;
            try
            {
                var prefix = CreateCanary(0xA5);
                var suffix = CreateCanary(0x5A);
                allocation = Marshal.AllocHGlobal(checked(_buffer.Length + 2 * CanaryBytes));
                var payload = IntPtr.Add(allocation, CanaryBytes);
                Marshal.Copy(prefix, 0, allocation, CanaryBytes);
                Marshal.Copy(_buffer, 0, payload, _buffer.Length);
                Marshal.Copy(suffix, 0, IntPtr.Add(payload, _buffer.Length), CanaryBytes);

                ReturnCode = _entry(_device, payload);
                if (Volatile.Read(ref _abandoned) != 0) return;

                Marshal.Copy(payload, _buffer, 0, _buffer.Length);
                var observedPrefix = new byte[CanaryBytes];
                var observedSuffix = new byte[CanaryBytes];
                Marshal.Copy(allocation, observedPrefix, 0, CanaryBytes);
                Marshal.Copy(IntPtr.Add(payload, _buffer.Length), observedSuffix, 0, CanaryBytes);
                GuardStatus = ValidateCanary(prefix, observedPrefix) && ValidateCanary(suffix, observedSuffix)
                    ? "pass"
                    : "fail";
            }
            catch (Exception ex)
            {
                Error = ex;
                GuardStatus = "not-checked-error";
            }
            finally
            {
                if (allocation != IntPtr.Zero) Marshal.FreeHGlobal(allocation);
            }
        }

        private static byte[] CreateCanary(byte seed)
        {
            var bytes = new byte[CanaryBytes];
            RandomNumberGenerator.Fill(bytes);
            // Keep two independent nonzero sentinels even if a platform RNG
            // is temporarily unavailable or returns a mostly uniform block.
            bytes[0] ^= seed;
            bytes[^1] ^= (byte)(seed ^ 0xFF);
            return bytes;
        }

        private static bool ValidateCanary(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> observed) =>
            CryptographicOperations.FixedTimeEquals(expected, observed);
    }
}

public sealed class DirectNvRailsException : Exception
{
    public DirectNvRailsException(string message) : base(message) { }
    public DirectNvRailsException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed record DirectRailMetadata(
    uint Mask,
    DirectRailChannelMetadata Pcie12V,
    DirectRailChannelMetadata TwelveVHpwr,
    int RecordCount);

public sealed record DirectRailChannelMetadata(
    int Channel,
    int RecordIndex,
    uint Type,
    uint Source);

public sealed record DirectRailSample(
    DateTimeOffset Timestamp,
    uint Mask,
    DirectRailChannelSample Pcie12V,
    DirectRailChannelSample TwelveVHpwr)
{
    /// <summary>
    /// Timestamp is assigned by the host polling loop. The private status
    /// payload has no validated freshness or timestamp field.
    /// </summary>
    public string Freshness => DirectNvRails.FreshnessMarker;
}

public sealed record DirectRailChannelSample(
    int Channel,
    uint Source,
    uint CurrentMilliamps,
    uint VoltageMicrovolts,
    double Volts,
    double Amps,
    double Watts,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Opaque A613 status dword at record offset +0x28. Units and timestamp
    /// semantics are intentionally unspecified.
    /// </summary>
    public uint RawStatusDword28 { get; init; }
}
