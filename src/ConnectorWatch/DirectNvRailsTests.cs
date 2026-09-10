using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;

namespace ConnectorWatch;

/// <summary>
/// Offline ABI fixtures for DirectNvRails.  This class deliberately never
/// constructs DirectNvRails and therefore never loads NVAPI, NVML, or touches
/// a GPU.  The portable daemon can call Run from its normal self-test entry.
/// </summary>
public static class DirectNvRailsTests
{
    public static void Run()
    {
        // Public identity observation does not grant native-reader approval.
        // Exact acceptance and developer policy are exercised by DriverApprovalTests.
        DirectNvRails.ValidateDriverIdentityRead(0, "616.56");
        DirectNvRails.ValidateDriverIdentityRead(0, "616.92");
        DirectNvRails.ValidateDriverIdentityRead(0, "999.99");
        Check(new Config().ValidateDriverVersion, "driver validation defaults on");
        Check(!JsonSerializer.Deserialize<Config>("{\"ValidateDriverVersion\":false}")!.ValidateDriverVersion,
            "config supports explicit unvalidated developer mode");
        var identity = Program.BuildReferenceIdentity(new Config { GpuUuid = "GPU-fixture" }, "direct", "direct NVIDIA rails",
            AnalysisLoadSource.CONNECTOR_POWER, "616.92");
        Check(identity.Driver == "616.92", "reference identity records actual driver");
        bool missingObservedDriver = false;
        try { Program.BuildReferenceIdentity(new Config { GpuUuid = "GPU-fixture" }, "direct", "direct NVIDIA rails", AnalysisLoadSource.CONNECTOR_POWER); }
        catch (ArgumentException) { missingObservedDriver = true; }
        Check(missingObservedDriver, "direct reference cannot silently use a historical default driver");
        foreach (var (status, version) in new[] { (-1, "616.92"), (0, ""), (0, " ") })
        {
            bool rejected = false;
            try { DirectNvRails.ValidateDriverIdentityRead(status, version); }
            catch (DirectNvRailsException) { rejected = true; }
            Check(rejected, "failed identity reads always reject");
        }
        DirectNvRails.RequireSingleGpu(0, 1, "fixture");
        foreach (var (status, count) in new (int, uint)[] { (0, 0), (0, 2), (0, 64), (-1, 1) })
        {
            bool rejected = false;
            try { DirectNvRails.RequireSingleGpu(status, count, "fixture"); }
            catch (DirectNvRailsException) { rejected = true; }
            Check(rejected, $"single-GPU identity gate rejects status {status}, count {count}");
        }
        var metadataBuffer = DirectNvRails.CreateMetadataRequest();
        Write(metadataBuffer, 4, 1);
        Write(metadataBuffer, 0x10, 0x00007ABF);
        WriteMetadataRecord(metadataBuffer, 1, 8, 255);
        WriteMetadataRecord(metadataBuffer, 2, 8, 218);

        var metadata = DirectNvRails.DecodeMetadata(metadataBuffer);
        Check(metadata.Mask == 0x00007ABF, "metadata mask");
        Check(metadata.RecordCount == 32, "metadata channel record count");
        Check(metadata.Pcie12V.Channel == 1 && metadata.Pcie12V.Source == 255,
            "PCIe channel metadata");
        Check(metadata.TwelveVHpwr.Channel == 2 && metadata.TwelveVHpwr.Source == 218,
            "12VHPWR channel metadata");

        var statusBuffer = DirectNvRails.CreateStatusRequest(metadata.Mask);
        WriteStatusRecord(statusBuffer, 1, 453, 12_045_549, 22_396_125);
        WriteStatusRecord(statusBuffer, 2, 5_544, 12_030_060, 219_231_926);
        var timestamp = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        var sample = DirectNvRails.DecodeStatus(statusBuffer, metadata, timestamp);
        Check(Math.Abs(sample.Pcie12V.Volts - 12.045549) < 1e-9, "PCIe voltage decode");
        Check(Math.Abs(sample.Pcie12V.Watts - 5.456633697) < 1e-9, "PCIe power decode");
        Check(Math.Abs(sample.TwelveVHpwr.Volts - 12.030060) < 1e-9, "12VHPWR voltage decode");
        Check(Math.Abs(sample.TwelveVHpwr.Watts - 66.69465264) < 1e-9, "12VHPWR power decode");
        Check(sample.Pcie12V.RawStatusDword28 == 22_396_125, "PCIe opaque status dword");
        Check(sample.TwelveVHpwr.RawStatusDword28 == 219_231_926, "12VHPWR opaque status dword");
        Check(sample.Freshness == DirectNvRails.FreshnessMarker, "native freshness marker");
        Check(sample.Timestamp == timestamp, "sample timestamp");
        var voltage = DirectNvRails.ToVoltage(sample);
        using var extras = JsonDocument.Parse(voltage.Extras);
        Check(extras.RootElement.GetProperty("pcie_12v_raw_status_dword_28").GetUInt32() == 22_396_125,
            "PCIe opaque dword in Extras");
        Check(extras.RootElement.GetProperty("12vhpwr_raw_status_dword_28").GetUInt32() == 219_231_926,
            "12VHPWR opaque dword in Extras");
        Check(extras.RootElement.GetProperty("freshness").GetString() == DirectNvRails.FreshnessMarker,
            "freshness in Extras");

        ExpectInvalid(() => DirectNvRails.DecodeMetadata(Change(metadataBuffer, 0, 0x30CA9)),
            "metadata header mismatch");
        ExpectInvalid(() => DirectNvRails.DecodeMetadata(Change(metadataBuffer, 4, 0)),
            "metadata flag mismatch");
        ExpectInvalid(() => DirectNvRails.DecodeMetadata(Change(metadataBuffer,
            0x18 + 1 * 0x3C + 0x20, 218)), "moved channel metadata");
        ExpectInvalid(() => DirectNvRails.DecodeMetadata(Change(metadataBuffer,
            0x18 + 1 * 0x3C + 0x20, 218, 0x18 + 2 * 0x3C + 0x20, 255)),
            "swapped channel metadata");
        ExpectInvalid(() => DirectNvRails.DecodeStatus(Change(statusBuffer, 4, 0x7ABE), metadata, timestamp),
            "status mask mismatch");
        ExpectInvalid(() => DirectNvRails.DecodeStatus(Change(statusBuffer,
            8 + 2 * 0x2C + 0x24, 0), metadata, timestamp), "zero voltage");
        ExpectInvalid(() => DirectNvRails.DecodeStatus(Change(statusBuffer,
            8 + 1 * 0x2C + 0x24, 17_000_000), metadata, timestamp), "implausible voltage");
        ExpectInvalid(() => DirectNvRails.DecodeStatus(statusBuffer[..^1], metadata, timestamp),
            "status size mismatch");

        NativeWorkerTests();

        Console.WriteLine("PASS: direct NV rail offline ABI fixtures.");
    }

    private static void NativeWorkerTests()
    {
        // Exercise the revised worker pattern repeatedly without loading any
        // native library. A callback that leaves the guarded payload unchanged
        // still verifies the return-code and canary path.
        for (var i = 0; i < 1000; i++)
        {
            var worker = CreateWorker(_ => 0, new byte[DirectNvRails.StatusSize]);
            Start(worker);
            Check(Join(worker, 1000), "native worker completed");
            Check(ReturnCode(worker) == 0 && GuardStatus(worker) == "pass",
                "native worker result and canaries");
        }

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var finished = new ManualResetEventSlim(false);
        const int marker = unchecked((int)0x5A17C0DE);
        var observed = 0;
        var blocking = CreateWorker(request =>
        {
            entered.Set();
            if (!release.Wait(2000)) throw new TimeoutException("test callback was not released");
            Marshal.WriteInt32(request, marker);
            observed = Marshal.ReadInt32(request);
            finished.Set();
            return 0;
        }, new byte[DirectNvRails.StatusSize]);
        Start(blocking);
        Check(entered.Wait(1000), "blocking callback entered");
        Check(!Join(blocking, 25), "blocking callback watchdog timeout");
        Abandon(blocking);
        Check(!finished.IsSet, "timed-out callback remains in flight");
        release.Set();
        Check(finished.Wait(1000), "timed-out callback released safely");
        Check(observed == marker, "in-flight payload remains owned until callback returns");
        Check(Join(blocking, 1000), "timed-out task can be joined after completion");
    }

    private static readonly Type NativeWorkerType = typeof(DirectNvRails)
        .GetNestedType("NativeWorker", BindingFlags.NonPublic)!;
    private static readonly Type NativeWorkerDelegateType = typeof(DirectNvRails)
        .GetNestedType("NvApiPrivateCall", BindingFlags.NonPublic)!;

    private static object CreateWorker(Func<IntPtr, int> callback, byte[] buffer)
    {
        var adapter = new CallbackAdapter(callback);
        var entry = Delegate.CreateDelegate(NativeWorkerDelegateType, adapter, nameof(CallbackAdapter.Invoke));
        var constructor = NativeWorkerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            new[] { NativeWorkerDelegateType, typeof(IntPtr), typeof(byte[]) },
            modifiers: null)!;
        return constructor.Invoke(new object?[] { entry, IntPtr.Zero, buffer });
    }

    private static void Start(object worker) => NativeWorkerType.GetMethod("Start")!.Invoke(worker, null);

    private static bool Join(object worker, int timeoutMilliseconds) =>
        (bool)NativeWorkerType.GetMethod("Join", new[] { typeof(int) })!
            .Invoke(worker, new object[] { timeoutMilliseconds })!;

    private static void Abandon(object worker) => NativeWorkerType.GetMethod("AbandonAfterTimeout")!.Invoke(worker, null);

    private static int ReturnCode(object worker) =>
        (int)NativeWorkerType.GetProperty("ReturnCode")!.GetValue(worker)!;

    private static string GuardStatus(object worker) =>
        (string)NativeWorkerType.GetProperty("GuardStatus")!.GetValue(worker)!;

    private sealed class CallbackAdapter(Func<IntPtr, int> callback)
    {
        public int Invoke(IntPtr _, IntPtr request) => callback(request);
    }

    private static void WriteMetadataRecord(byte[] buffer, int index, uint type, uint source)
    {
        var offset = 0x18 + index * 0x3C;
        Write(buffer, offset + 0x1C, type);
        Write(buffer, offset + 0x20, source);
    }

    private static void WriteStatusRecord(byte[] buffer, int channel, uint milliamps,
        uint microvolts, uint rawStatusDword28)
    {
        var offset = 8 + channel * 0x2C;
        Write(buffer, offset + 0x20, milliamps);
        Write(buffer, offset + 0x24, microvolts);
        Write(buffer, offset + 0x28, rawStatusDword28);
    }

    private static byte[] Change(byte[] source, params int[] values)
    {
        var copy = source.ToArray();
        if ((values.Length & 1) != 0) throw new ArgumentException("offset/value pairs required", nameof(values));
        for (var i = 0; i < values.Length; i += 2) Write(copy, values[i], unchecked((uint)values[i + 1]));
        return copy;
    }

    private static void Write(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }

    private static void ExpectInvalid(Func<object> action, string name)
    {
        try
        {
            _ = action();
            throw new Exception("FAILED: expected invalid fixture: " + name);
        }
        catch (InvalidDataException)
        {
            // expected
        }
    }
}
