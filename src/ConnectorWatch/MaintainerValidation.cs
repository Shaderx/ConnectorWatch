using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch;

public sealed record MaintainerProbeIdentity(
    uint PciDeviceId,
    uint SubsystemId,
    string DriverVersion,
    string Os,
    string Architecture,
    string GpuUuid);

public sealed record MaintainerNativeOperation(
    string Operation,
    byte[] Response,
    int ReturnCode,
    string GuardStatus,
    bool TimedOut,
    long DurationMilliseconds);

/// <summary>
/// Narrow diagnostic surface used only by the nonce-gated maintainer child.
/// Implementations retain the production watchdog, canaries and single-target
/// identity checks but deliberately bypass catalog approval so a revoked driver
/// can be investigated without weakening the normal reader path.
/// </summary>
public interface IMaintainerNativeProbe : IDisposable
{
    MaintainerProbeIdentity Identity { get; }
    MaintainerNativeOperation CaptureMetadata();
    MaintainerNativeOperation CaptureStatus(uint mask);
}

public sealed class MaintainerValidationRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string GpuUuid { get; set; } = "";
    public string ReaderProfile { get; set; } = "A612-A613-v1";
    public string ReaderVersion { get; set; } = DirectNvRails.ReaderVersion;
    public string AppVersion { get; set; } = DirectNvRails.AppVersion;
    public string ToolCommit { get; set; } = "";
    public string ApprovedMetadataResponseSha256 { get; set; } = "";
    public int SmokeSamples { get; set; } = 3;
    public int SampleIntervalMilliseconds { get; set; } = 250;
    public MaintainerComparisonOracle? Oracle { get; set; }
}

public sealed class MaintainerComparisonOracle
{
    public int SchemaVersion { get; set; } = 1;
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string SubsystemId { get; set; } = "";
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string ReaderProfile { get; set; } = "";
    public string ApprovedMetadataResponseSha256 { get; set; } = "";
    public double VoltageAbsoluteToleranceV { get; set; } = 0.15;
    public double CurrentAbsoluteToleranceA { get; set; } = 1.0;
    public double CurrentRelativeTolerance { get; set; } = 0.10;
    public List<MaintainerOraclePhase> Phases { get; set; } = [];
}

public sealed class MaintainerOraclePhase
{
    public string Name { get; set; } = "";
    public List<MaintainerOracleSample> Samples { get; set; } = [];
}

public sealed class MaintainerOracleSample
{
    public string StatusResponseBase64 { get; set; } = "";
    public int NativeReturnCode { get; set; }
    public string GuardStatus { get; set; } = "";
    public bool TimedOut { get; set; }
    public MaintainerIndependentReading Pcie12V { get; set; } = new();
    public MaintainerIndependentReading TwelveVHpwr { get; set; } = new();
}

public sealed class MaintainerIndependentReading
{
    public double VoltageV { get; set; }
    public double CurrentA { get; set; }
}

public sealed class MaintainerValidationEvidence
{
    public int SchemaVersion { get; set; } = 1;
    public string EvidenceType { get; set; } = "connectorwatch-driver-validation";
    public string RunId { get; set; } = "";
    public DateTimeOffset CapturedUtc { get; set; }
    public MaintainerValidationScope Scope { get; set; } = new();
    public List<MaintainerOperationEvidence> NativeOperations { get; set; } = [];
    public List<MaintainerSampleEvidence> SampleProgression { get; set; } = [];
    public List<MaintainerComparisonResult> IndependentComparisons { get; set; } = [];
    public List<MaintainerCheck> Checks { get; set; } = [];
    public string Outcome { get; set; } = "failed";
    public string ApprovalAuthority { get; set; } = "none; maintainer review and digest authorization required";
}

public sealed class MaintainerValidationScope
{
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string SubsystemId { get; set; } = "";
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string ReaderProfile { get; set; } = "";
    public string ReaderVersion { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string ToolCommit { get; set; } = "";
}

public sealed class MaintainerOperationEvidence
{
    public string Operation { get; set; } = "";
    public int ReturnCode { get; set; }
    public string GuardStatus { get; set; } = "";
    public bool TimedOut { get; set; }
    public long DurationMilliseconds { get; set; }
    public string ResponseSha256 { get; set; } = "";
    public string SanitizedResponseBase64 { get; set; } = "";
    public int RedactionCount { get; set; }
}

public sealed class MaintainerSampleEvidence
{
    public int Index { get; set; }
    public DateTimeOffset HostTimestampUtc { get; set; }
    public double PcieVoltageV { get; set; }
    public double PcieCurrentA { get; set; }
    public double TwelveVHpwrVoltageV { get; set; }
    public double TwelveVHpwrCurrentA { get; set; }
    public uint PcieRawStatusDword28 { get; set; }
    public uint TwelveVHpwrRawStatusDword28 { get; set; }
}

public sealed class MaintainerComparisonResult
{
    public string Phase { get; set; } = "";
    public string Rail { get; set; } = "";
    public int SampleCount { get; set; }
    public double NativeMeanVoltageV { get; set; }
    public double IndependentMeanVoltageV { get; set; }
    public double VoltageAbsoluteErrorV { get; set; }
    public double NativeMeanCurrentA { get; set; }
    public double IndependentMeanCurrentA { get; set; }
    public double CurrentAbsoluteErrorA { get; set; }
    public double CurrentRelativeError { get; set; }
    public bool Passed { get; set; }
}

public sealed class MaintainerCheck
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class MaintainerApprovalProposal
{
    public int SchemaVersion { get; set; } = 1;
    public string ProposalStatus { get; set; } = "maintainer_review_required";
    public string EvidenceSha256 { get; set; } = "";
    public string ScopeSha256 { get; set; } = "";
    public DriverCatalogEntryProposal ProposedEntry { get; set; } = new();
}

public sealed class DriverCatalogEntryProposal
{
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string SubsystemId { get; set; } = "";
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string ReaderProfile { get; set; } = "";
    public string MinimumReaderVersion { get; set; } = "";
    public string MaximumReaderVersionExclusive { get; set; } = "";
    public string MinimumAppVersion { get; set; } = "";
    public string MaximumAppVersionExclusive { get; set; } = "";
    public string Decision { get; set; } = "approved";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset DecisionUtc { get; set; }
    public string Rationale { get; set; } = "Validated native structure and independent idle/workload rail comparisons; maintainer authorization pending.";
}

public sealed record BootstrapDriverApproval(
    string DriverVersion,
    string ValidationLevel,
    string ValidationRecord,
    string Limitations,
    string PublicRationale);

public sealed record BootstrapDriverApprovalAttestation(
    string Approver,
    DateTimeOffset ApprovalUtc,
    string Reason,
    IReadOnlyList<BootstrapDriverApproval> Approvals);

/// <summary>
/// One-time migration policy for the two driver versions that predate the signed
/// catalog. This is deliberately separate from measured validation evidence and
/// cannot be used to add a future driver version.
/// </summary>
public static class BootstrapDriverApprovalPolicy
{
    private static readonly IReadOnlyDictionary<string, (string Level, string Record)> KnownDrivers =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["616.56"] = ("historical_physical_validation", "docs/VALIDATION.md"),
            ["616.92"] = ("structural_validation_without_independent_comparison", "docs/DRIVER-616.92.md"),
        };

    public static BootstrapDriverApprovalAttestation Parse(ReadOnlySpan<byte> utf8)
    {
        using var document = StrictJson.ParseObject(utf8, "bootstrap driver approval attestation");
        var root = document.RootElement;
        StrictJson.RequireOnlyProperties(root, "bootstrap driver approval attestation",
            "schema_version", "attestation_type", "policy", "approver", "approval_utc", "reason", "approvals");
        if (StrictJson.RequiredInt32(root, "schema_version") != 1 ||
            StrictJson.RequiredString(root, "attestation_type", 100) != "connectorwatch-bootstrap-driver-approvals" ||
            StrictJson.RequiredString(root, "policy", 100) != "one-time-initial-catalog-known-drivers")
            throw new InvalidDataException("Unsupported bootstrap driver approval attestation policy.");
        var approver = StrictJson.RequiredString(root, "approver", 80);
        if (approver != "Shaderx")
            throw new InvalidDataException("The bootstrap approval must identify the authenticated repository maintainer Shaderx.");
        var approvalUtc = StrictJson.RequiredUtcTimestamp(root, "approval_utc");
        if (approvalUtc.UtcDateTime.Date != new DateTime(2026, 9, 10))
            throw new InvalidDataException("The bootstrap attestation must retain the recorded 2026-09-10 approval date.");
        var reason = StrictJson.RequiredString(root, "reason", 500);
        var approvalsElement = root.GetProperty("approvals");
        if (approvalsElement.ValueKind != JsonValueKind.Array || approvalsElement.GetArrayLength() != KnownDrivers.Count)
            throw new InvalidDataException("The bootstrap attestation must contain exactly the two documented drivers.");

        var approvals = new List<BootstrapDriverApproval>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in approvalsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Each bootstrap driver approval must be an object.");
            StrictJson.RequireOnlyProperties(item, "bootstrap driver approval",
                "vendor_id", "device_id", "subsystem_id", "os", "architecture", "driver_version",
                "reader_profile", "minimum_reader_version", "maximum_reader_version_exclusive",
                "minimum_app_version", "maximum_app_version_exclusive", "validation_level",
                "validation_record", "limitations", "public_rationale");
            Require(item, "vendor_id", "10DE");
            Require(item, "device_id", "2B85");
            Require(item, "subsystem_id", "89EE1043");
            Require(item, "os", "windows");
            Require(item, "architecture", "x64");
            Require(item, "reader_profile", "A612-A613-v1");
            Require(item, "minimum_reader_version", "1.0.0");
            Require(item, "maximum_reader_version_exclusive", "1.1.0");
            Require(item, "minimum_app_version", "1.5.0");
            Require(item, "maximum_app_version_exclusive", "1.6.0");
            var driver = StrictJson.RequiredString(item, "driver_version", 32);
            if (!KnownDrivers.TryGetValue(driver, out var expected) || !seen.Add(driver))
                throw new InvalidDataException($"Driver '{driver}' is not a unique documented bootstrap driver.");
            var level = StrictJson.RequiredString(item, "validation_level", 100);
            var record = StrictJson.RequiredString(item, "validation_record", 200);
            if (level != expected.Level || record != expected.Record)
                throw new InvalidDataException($"Driver '{driver}' misstates its documented validation basis.");
            approvals.Add(new(driver, level, record,
                StrictJson.RequiredString(item, "limitations", 1000),
                StrictJson.RequiredString(item, "public_rationale", 1000)));
        }
        if (!seen.SetEquals(KnownDrivers.Keys))
            throw new InvalidDataException("The bootstrap attestation omitted a documented driver.");
        return new(approver, approvalUtc, reason, approvals);
    }

    private static void Require(JsonElement item, string property, string expected)
    {
        if (StrictJson.RequiredString(item, property, 100) != expected)
            throw new InvalidDataException($"Bootstrap scope property '{property}' is outside the fixed policy.");
    }
}

public static class MaintainerValidationCommand
{
    public const string ParentSwitch = "--maintainer-validate";
    public const string ChildSwitch = "--maintainer-validation-child";
    public const string CatalogValidationSwitch = "--validate-driver-catalog-payload";
    public const string CatalogEnvelopeValidationSwitch = "--validate-driver-catalog-envelope";
    public const string BootstrapAttestationValidationSwitch = "--validate-driver-approval-attestation";
    public const string SelfTestSwitch = "--maintainer-validation-self-test";
    private const string ChildTokenEnvironment = "CONNECTORWATCH_MAINTAINER_CHILD_TOKEN";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static bool TryHandle(string[] args,
        Func<string, IMaintainerNativeProbe> probeFactory, out int exitCode)
    {
        if (!args.Contains(ParentSwitch, StringComparer.Ordinal) &&
            !args.Contains(ChildSwitch, StringComparer.Ordinal) &&
            !args.Contains(CatalogValidationSwitch, StringComparer.Ordinal) &&
            !args.Contains(CatalogEnvelopeValidationSwitch, StringComparer.Ordinal) &&
            !args.Contains(BootstrapAttestationValidationSwitch, StringComparer.Ordinal) &&
            !args.Contains(SelfTestSwitch, StringComparer.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        exitCode = args.Contains(SelfTestSwitch, StringComparer.Ordinal)
            ? RunSelfTests()
            : args.Contains(CatalogEnvelopeValidationSwitch, StringComparer.Ordinal)
                ? ValidateCatalogEnvelope(args)
            : args.Contains(BootstrapAttestationValidationSwitch, StringComparer.Ordinal)
                ? ValidateBootstrapAttestation(args)
            : args.Contains(CatalogValidationSwitch, StringComparer.Ordinal)
                ? ValidateCatalogPayload(args)
            : args.Contains(ChildSwitch, StringComparer.Ordinal)
                ? RunChild(args, probeFactory)
                : RunParent(args);
        return true;
    }

    private static int RunSelfTests()
    {
        MaintainerValidationTests.Run();
        return 0;
    }

    private static int ValidateCatalogPayload(string[] args)
    {
        var path = Path.GetFullPath(RequiredOption(args, CatalogValidationSwitch));
        _ = DriverCatalogPayload.Parse(File.ReadAllBytes(path),
            new HashSet<string>(StringComparer.Ordinal) { DirectNvRails.ReaderProfile });
        Console.WriteLine("Catalog payload schema and supported profiles validated.");
        return 0;
    }

    private static int ValidateBootstrapAttestation(string[] args)
    {
        var path = Path.GetFullPath(RequiredOption(args, BootstrapAttestationValidationSwitch));
        _ = BootstrapDriverApprovalPolicy.Parse(File.ReadAllBytes(path));
        Console.WriteLine("One-time bootstrap attestation scope and limitations validated.");
        return 0;
    }

    private static int ValidateCatalogEnvelope(string[] args)
    {
        var path = Path.GetFullPath(RequiredOption(args, CatalogEnvelopeValidationSwitch));
        var keyId = RequiredOption(args, "--key-id");
        var publicKey = RequiredOption(args, "--public-key-spki-base64");
        var verified = new SignedMetadataVerifier([new SignedMetadataKey(keyId, publicKey)])
            .VerifyEnvelope(File.ReadAllBytes(path));
        _ = DriverCatalogPayload.ParseVerified(verified,
            new HashSet<string>(StringComparer.Ordinal) { DirectNvRails.ReaderProfile });
        Console.WriteLine("Catalog envelope signature, payload, schema and supported profiles validated.");
        return 0;
    }

    private static int RunParent(string[] args)
    {
        var outputRoot = Path.GetFullPath(RequiredOption(args, "--output"));
        Directory.CreateDirectory(outputRoot);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N");
        var runDirectory = Path.Combine(outputRoot, "validation-" + runId);
        Directory.CreateDirectory(runDirectory);

        MaintainerComparisonOracle? oracle = null;
        if (Option(args, "--oracle") is string oraclePath)
            oracle = Deserialize<MaintainerComparisonOracle>(File.ReadAllBytes(Path.GetFullPath(oraclePath)));
        var request = new MaintainerValidationRequest
        {
            GpuUuid = RequiredOption(args, "--gpu-uuid"),
            ReaderProfile = Option(args, "--reader-profile") ?? "A612-A613-v1",
            ReaderVersion = Option(args, "--reader-version") ?? DirectNvRails.ReaderVersion,
            AppVersion = Option(args, "--app-version") ?? DirectNvRails.AppVersion,
            ToolCommit = RequiredOption(args, "--tool-commit"),
            ApprovedMetadataResponseSha256 = RequiredOption(args, "--approved-metadata-sha256").ToLowerInvariant(),
            SmokeSamples = ParseInt(Option(args, "--samples"), 3, 2, 100),
            SampleIntervalMilliseconds = ParseInt(Option(args, "--interval-ms"), 250, 0, 10_000),
            Oracle = oracle,
        };
        ValidateRequest(request);

        var requestPath = Path.Combine(runDirectory, "request.private.json");
        File.WriteAllBytes(requestPath, Serialize(request));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var start = BuildChildStartInfo(requestPath, runDirectory, token);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated validation child.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timeoutSeconds = ParseInt(Option(args, "--child-timeout-seconds"), 120, 10, 600);
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            File.WriteAllText(Path.Combine(runDirectory, "child-timeout.txt"),
                $"Validation child exceeded {timeoutSeconds} seconds.{Environment.NewLine}");
            File.WriteAllText(Path.Combine(runDirectory, "child.stdout.txt"), stdout.GetAwaiter().GetResult());
            File.WriteAllText(Path.Combine(runDirectory, "child.stderr.txt"), stderr.GetAwaiter().GetResult());
            try { File.Delete(requestPath); } catch { /* reported directory remains private until manually reviewed */ }
            Console.Error.WriteLine("Maintainer validation child timed out. Evidence was not proposed.");
            return 2;
        }
        File.WriteAllText(Path.Combine(runDirectory, "child.stdout.txt"), stdout.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(runDirectory, "child.stderr.txt"), stderr.GetAwaiter().GetResult());
        try { File.Delete(requestPath); } catch { /* private input is excluded from public bundle even if cleanup fails */ }
        Console.WriteLine(runDirectory);
        return process.ExitCode;
    }

    private static ProcessStartInfo BuildChildStartInfo(string requestPath, string runDirectory, string token)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The current process path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = runDirectory,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(MaintainerValidationCommand).Assembly.Location);
        start.ArgumentList.Add(ChildSwitch);
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestPath);
        start.ArgumentList.Add("--run-directory");
        start.ArgumentList.Add(runDirectory);
        start.Environment[ChildTokenEnvironment] = token;
        start.Environment[ChildTokenEnvironment + "_SHA256"] = Sha256Hex(Encoding.UTF8.GetBytes(token));
        return start;
    }

    private static int RunChild(string[] args, Func<string, IMaintainerNativeProbe> probeFactory)
    {
        var token = Environment.GetEnvironmentVariable(ChildTokenEnvironment);
        var expected = Environment.GetEnvironmentVariable(ChildTokenEnvironment + "_SHA256");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expected) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Sha256Hex(Encoding.UTF8.GetBytes(token))),
                Encoding.ASCII.GetBytes(expected)))
            throw new InvalidOperationException("Maintainer validation child requires its parent-issued nonce.");

        var requestPath = Path.GetFullPath(RequiredOption(args, "--request"));
        var runDirectory = Path.GetFullPath(RequiredOption(args, "--run-directory"));
        if (!Path.GetDirectoryName(requestPath)!.Equals(runDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Private request must be inside the isolated run directory.");
        var request = Deserialize<MaintainerValidationRequest>(File.ReadAllBytes(requestPath));
        ValidateRequest(request);
        try
        {
            using var probe = probeFactory(request.GpuUuid);
            var evidence = Capture(request, probe, Path.GetFileName(runDirectory)["validation-".Length..]);
            var evidenceBytes = Serialize(evidence);
            var evidencePath = Path.Combine(runDirectory, "evidence.json");
            File.WriteAllBytes(evidencePath, evidenceBytes);
            if (evidence.Outcome == "full_validation_passed")
            {
                var evidenceHash = Sha256Hex(evidenceBytes);
                var proposal = BuildProposal(evidence, evidenceHash, ScopeSha256(evidence.Scope));
                File.WriteAllBytes(Path.Combine(runDirectory, "proposal.json"), Serialize(proposal));
            }
            else
            {
                File.WriteAllText(Path.Combine(runDirectory, "NO-APPROVAL-PROPOSAL.txt"),
                    evidence.Outcome == "provisional_structural_smoke_passed"
                        ? "Structural smoke evidence is provisional. Independent idle/workload comparisons are required before an approved catalog entry can be proposed.\n"
                        : "Validation failed. Failed evidence cannot produce an approved catalog proposal.\n");
            }
            return evidence.Outcome == "failed" ? 1 : 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(runDirectory, "validation-failure.txt"),
                ex.GetType().Name + ": " + SanitizeText(ex.Message, request.GpuUuid));
            return 1;
        }
    }

    internal static MaintainerValidationEvidence Capture(MaintainerValidationRequest request,
        IMaintainerNativeProbe probe, string runId)
    {
        ValidateRequest(request);
        var identity = probe.Identity;
        var evidence = new MaintainerValidationEvidence
        {
            RunId = runId,
            CapturedUtc = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Scope = Scope(identity, request),
        };
        evidence.Checks.Add(Check("target_identity", identity.PciDeviceId == DirectNvRails.TargetPciIdentifier &&
            identity.SubsystemId == DirectNvRails.TargetSubsystemIdentifier,
            $"observed {Hex(identity.PciDeviceId)}/{Hex(identity.SubsystemId)}"));
        evidence.Checks.Add(Check("platform", identity.Os.Equals("windows", StringComparison.OrdinalIgnoreCase) &&
            identity.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase),
            identity.Os + "/" + identity.Architecture));

        var metadataOperation = probe.CaptureMetadata();
        evidence.NativeOperations.Add(OperationEvidence(metadataOperation, identity.GpuUuid));
        DirectRailMetadata? metadata = null;
        var metadataOperationPassed = OperationPassed(metadataOperation);
        try
        {
            if (metadataOperationPassed)
                metadata = DirectNvRails.DecodeMetadata(metadataOperation.Response);
        }
        catch (Exception ex)
        {
            evidence.Checks.Add(Check("shipping_metadata_decoder", false, ex.Message));
        }
        if (!evidence.Checks.Any(c => c.Name == "shipping_metadata_decoder"))
            evidence.Checks.Add(Check("shipping_metadata_decoder", metadata is not null,
                metadata is null ? "native operation failed" : $"mask {Hex(metadata.Mask)}; records {metadata.RecordCount}"));
        evidence.Checks.Add(Check("last_approved_metadata_contract",
            request.ApprovedMetadataResponseSha256.Equals(
                Sha256Hex(metadataOperation.Response), StringComparison.OrdinalIgnoreCase),
            "observed A612 response must match the recorded last-approved response digest"));

        if (metadata is not null)
        {
            for (var index = 0; index < request.SmokeSamples; index++)
            {
                if (index > 0 && request.SampleIntervalMilliseconds > 0)
                    Thread.Sleep(request.SampleIntervalMilliseconds);
                var operation = probe.CaptureStatus(metadata.Mask);
                evidence.NativeOperations.Add(OperationEvidence(operation, identity.GpuUuid));
                try
                {
                    if (!OperationPassed(operation)) throw new InvalidDataException("native status operation did not pass");
                    var timestamp = DateTimeOffset.UtcNow;
                    var sample = DirectNvRails.DecodeStatus(operation.Response, metadata, timestamp);
                    evidence.SampleProgression.Add(SampleEvidence(index, sample));
                }
                catch (Exception ex)
                {
                    evidence.Checks.Add(Check("shipping_status_decoder_" + index, false, ex.Message));
                }
            }
        }
        var operationChecksPassed = evidence.NativeOperations.Count == request.SmokeSamples + 1 &&
            evidence.NativeOperations.All(o => o.ReturnCode == 0 && o.GuardStatus == "pass" && !o.TimedOut);
        evidence.Checks.Add(Check("native_return_guards_timeouts", operationChecksPassed,
            $"{evidence.NativeOperations.Count} operations captured"));
        var progressionPassed = evidence.SampleProgression.Count == request.SmokeSamples &&
            evidence.SampleProgression.Select(s => s.HostTimestampUtc).Zip(
                evidence.SampleProgression.Select(s => s.HostTimestampUtc).Skip(1), (a, b) => b >= a).All(x => x);
        evidence.Checks.Add(Check("bounded_sample_progression", progressionPassed,
            $"{evidence.SampleProgression.Count}/{request.SmokeSamples} samples decoded"));

        var structuralPassed = evidence.Checks.All(c => c.Passed);
        var comparisonsPassed = false;
        if (structuralPassed && request.Oracle is not null && metadata is not null)
        {
            comparisonsPassed = CompareOracle(request.Oracle, evidence.Scope, metadata,
                metadataOperation.Response,
                evidence.IndependentComparisons, evidence.Checks);
        }
        evidence.Outcome = !structuralPassed || evidence.Checks.Any(c => !c.Passed) ? "failed" :
            request.Oracle is null ? "provisional_structural_smoke_passed" :
            comparisonsPassed ? "full_validation_passed" : "failed";
        return evidence;
    }

    private static bool CompareOracle(MaintainerComparisonOracle oracle,
        MaintainerValidationScope scope, DirectRailMetadata metadata, byte[] metadataResponse,
        List<MaintainerComparisonResult> results, List<MaintainerCheck> checks)
    {
        var scopeMatch = oracle.SchemaVersion == 1 &&
            oracle.VendorId == scope.VendorId && oracle.DeviceId == scope.DeviceId &&
            oracle.SubsystemId == scope.SubsystemId &&
            oracle.Os.Equals(scope.Os, StringComparison.OrdinalIgnoreCase) &&
            oracle.Architecture.Equals(scope.Architecture, StringComparison.OrdinalIgnoreCase) &&
            oracle.DriverVersion == scope.DriverVersion && oracle.ReaderProfile == scope.ReaderProfile;
        checks.Add(Check("oracle_exact_scope", scopeMatch, "oracle scope must equal observed scope"));
        var metadataContractMatch = oracle.ApprovedMetadataResponseSha256.Length == 64 &&
            oracle.ApprovedMetadataResponseSha256.Equals(Sha256Hex(metadataResponse), StringComparison.OrdinalIgnoreCase);
        checks.Add(Check("oracle_metadata_contract", metadataContractMatch,
            "observed A612 response must match the recorded last-approved response digest"));
        var requiredPhases = new[] { "idle", "workload" };
        var phaseSet = oracle.Phases.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var phaseShape = requiredPhases.All(phaseSet.Contains) && oracle.Phases.Count == 2 &&
            oracle.Phases.All(p => p.Samples.Count >= 2) &&
            oracle.VoltageAbsoluteToleranceV is > 0 and <= 1 &&
            oracle.CurrentAbsoluteToleranceA is >= 0 and <= 20 &&
            oracle.CurrentRelativeTolerance is >= 0 and <= 0.5;
        checks.Add(Check("oracle_idle_workload_shape", phaseShape,
            "exactly idle and workload with at least two paired samples each"));
        if (!scopeMatch || !metadataContractMatch || !phaseShape) return false;

        foreach (var phase in oracle.Phases)
        {
            var decoded = new List<(DirectRailSample Native, MaintainerOracleSample Oracle)>();
            foreach (var pair in phase.Samples)
            {
                try
                {
                    if (pair.NativeReturnCode != 0 || pair.GuardStatus != "pass" || pair.TimedOut)
                        throw new InvalidDataException("recorded native operation did not pass its return/guard/timeout checks");
                    var raw = Convert.FromBase64String(pair.StatusResponseBase64);
                    decoded.Add((DirectNvRails.DecodeStatus(raw, metadata, DateTimeOffset.UnixEpoch), pair));
                }
                catch (Exception ex)
                {
                    checks.Add(Check("oracle_shipping_decoder_" + phase.Name, false, ex.Message));
                    return false;
                }
            }
            results.Add(CompareRail(phase.Name, "pcie_12v", decoded,
                x => x.Native.Pcie12V, x => x.Oracle.Pcie12V, oracle));
            results.Add(CompareRail(phase.Name, "12vhpwr", decoded,
                x => x.Native.TwelveVHpwr, x => x.Oracle.TwelveVHpwr, oracle));
        }
        var passed = results.Count == 4 && results.All(r => r.Passed);
        checks.Add(Check("independent_idle_workload_comparison", passed,
            $"{results.Count(r => r.Passed)}/{results.Count} rail/phase comparisons passed"));
        return passed;
    }

    private static MaintainerComparisonResult CompareRail(string phase, string rail,
        List<(DirectRailSample Native, MaintainerOracleSample Oracle)> samples,
        Func<(DirectRailSample Native, MaintainerOracleSample Oracle), DirectRailChannelSample> native,
        Func<(DirectRailSample Native, MaintainerOracleSample Oracle), MaintainerIndependentReading> independent,
        MaintainerComparisonOracle oracle)
    {
        var nativeV = samples.Average(x => native(x).Volts);
        var independentV = samples.Average(x => independent(x).VoltageV);
        var nativeA = samples.Average(x => native(x).Amps);
        var independentA = samples.Average(x => independent(x).CurrentA);
        var voltageError = Math.Abs(nativeV - independentV);
        var currentError = Math.Abs(nativeA - independentA);
        var relative = currentError / Math.Max(Math.Abs(independentA), 0.001);
        return new MaintainerComparisonResult
        {
            Phase = phase,
            Rail = rail,
            SampleCount = samples.Count,
            NativeMeanVoltageV = nativeV,
            IndependentMeanVoltageV = independentV,
            VoltageAbsoluteErrorV = voltageError,
            NativeMeanCurrentA = nativeA,
            IndependentMeanCurrentA = independentA,
            CurrentAbsoluteErrorA = currentError,
            CurrentRelativeError = relative,
            Passed = voltageError <= oracle.VoltageAbsoluteToleranceV &&
                (currentError <= oracle.CurrentAbsoluteToleranceA || relative <= oracle.CurrentRelativeTolerance),
        };
    }

    internal static MaintainerApprovalProposal BuildProposal(MaintainerValidationEvidence evidence,
        string evidenceHash, string scopeHash)
    {
        if (evidence.Outcome != "full_validation_passed" || evidence.Checks.Any(c => !c.Passed) ||
            evidence.IndependentComparisons.Count != 4 || evidence.IndependentComparisons.Any(c => !c.Passed))
            throw new InvalidOperationException("Only full passing evidence can produce an approved-entry proposal.");
        var scope = evidence.Scope;
        return new MaintainerApprovalProposal
        {
            EvidenceSha256 = evidenceHash,
            ScopeSha256 = scopeHash,
            ProposedEntry = new DriverCatalogEntryProposal
            {
                VendorId = scope.VendorId,
                DeviceId = scope.DeviceId,
                SubsystemId = scope.SubsystemId,
                Os = scope.Os,
                Architecture = scope.Architecture,
                DriverVersion = scope.DriverVersion,
                ReaderProfile = scope.ReaderProfile,
                MinimumReaderVersion = scope.ReaderVersion,
                MaximumReaderVersionExclusive = NextMinor(scope.ReaderVersion),
                MinimumAppVersion = scope.AppVersion,
                MaximumAppVersionExclusive = NextMajor(scope.AppVersion),
                EvidenceSha256 = evidenceHash,
                DecisionUtc = evidence.CapturedUtc,
            },
        };
    }

    private static string ScopeSha256(MaintainerValidationScope scope) => Sha256Hex(Encoding.UTF8.GetBytes(
        string.Join('|', scope.VendorId, scope.DeviceId, scope.SubsystemId, scope.Os,
            scope.Architecture, scope.DriverVersion, scope.ReaderProfile, scope.ReaderVersion,
            scope.AppVersion, scope.ToolCommit)));

    private static MaintainerValidationScope Scope(MaintainerProbeIdentity identity,
        MaintainerValidationRequest request) => new()
    {
        VendorId = HexIdentifier(identity.PciDeviceId & 0xFFFF, 4),
        DeviceId = HexIdentifier(identity.PciDeviceId >> 16, 4),
        SubsystemId = HexIdentifier(identity.SubsystemId, 8),
        Os = identity.Os.ToLowerInvariant(),
        Architecture = identity.Architecture.ToLowerInvariant(),
        DriverVersion = identity.DriverVersion,
        ReaderProfile = request.ReaderProfile,
        ReaderVersion = request.ReaderVersion,
        AppVersion = request.AppVersion,
        ToolCommit = request.ToolCommit.ToLowerInvariant(),
    };

    private static MaintainerOperationEvidence OperationEvidence(MaintainerNativeOperation operation,
        string gpuUuid)
    {
        var sanitized = operation.Response.ToArray();
        var redactions = Redact(sanitized, gpuUuid);
        return new MaintainerOperationEvidence
        {
            Operation = operation.Operation,
            ReturnCode = operation.ReturnCode,
            GuardStatus = operation.GuardStatus,
            TimedOut = operation.TimedOut,
            DurationMilliseconds = operation.DurationMilliseconds,
            ResponseSha256 = Sha256Hex(operation.Response),
            SanitizedResponseBase64 = Convert.ToBase64String(sanitized),
            RedactionCount = redactions,
        };
    }

    private static MaintainerSampleEvidence SampleEvidence(int index, DirectRailSample sample) => new()
    {
        Index = index,
        HostTimestampUtc = sample.Timestamp,
        PcieVoltageV = sample.Pcie12V.Volts,
        PcieCurrentA = sample.Pcie12V.Amps,
        TwelveVHpwrVoltageV = sample.TwelveVHpwr.Volts,
        TwelveVHpwrCurrentA = sample.TwelveVHpwr.Amps,
        PcieRawStatusDword28 = sample.Pcie12V.RawStatusDword28,
        TwelveVHpwrRawStatusDword28 = sample.TwelveVHpwr.RawStatusDword28,
    };

    private static int Redact(byte[] buffer, params string[] secrets)
    {
        var replacements = secrets.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) replacements.Add(profile);
        if (!string.IsNullOrWhiteSpace(Environment.UserName)) replacements.Add(Environment.UserName);
        var count = 0;
        foreach (var secret in replacements.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in new[] { secret, secret.ToUpperInvariant(), secret.ToLowerInvariant() }.Distinct(StringComparer.Ordinal))
            {
                foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
                {
                    var needle = encoding.GetBytes(candidate);
                    for (var i = 0; i <= buffer.Length - needle.Length; i++)
                    {
                        if (!buffer.AsSpan(i, needle.Length).SequenceEqual(needle)) continue;
                        buffer.AsSpan(i, needle.Length).Clear();
                        count++;
                    }
                }
            }
        }
        return count;
    }

    private static string SanitizeText(string text, string gpuUuid)
    {
        foreach (var value in new[] { gpuUuid, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName })
            if (!string.IsNullOrWhiteSpace(value)) text = text.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static bool OperationPassed(MaintainerNativeOperation operation) =>
        operation.ReturnCode == 0 && operation.GuardStatus == "pass" && !operation.TimedOut;

    private static MaintainerCheck Check(string name, bool passed, string detail) => new()
    {
        Name = name,
        Passed = passed,
        Detail = detail,
    };

    private static void ValidateRequest(MaintainerValidationRequest request)
    {
        if (request.SchemaVersion != 1 || string.IsNullOrWhiteSpace(request.GpuUuid) ||
            request.ReaderProfile != DirectNvRails.ReaderProfile ||
            request.ReaderVersion != DirectNvRails.ReaderVersion ||
            request.AppVersion != DirectNvRails.AppVersion ||
            request.ToolCommit.Length != 40 || request.ToolCommit.Any(c => !Uri.IsHexDigit(c)) ||
            request.ApprovedMetadataResponseSha256.Length != 64 ||
            request.ApprovedMetadataResponseSha256.Any(c => !Uri.IsHexDigit(c)) ||
            request.SmokeSamples is < 2 or > 100 || request.SampleIntervalMilliseconds is < 0 or > 10_000)
            throw new InvalidDataException("Invalid maintainer validation request or unsupported shipping profile.");
    }

    private static int ParseInt(string? value, int fallback, int min, int max)
    {
        if (value is null) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < min || parsed > max)
            throw new ArgumentOutOfRangeException(nameof(value), $"Value must be between {min} and {max}.");
        return parsed;
    }

    private static string NextMinor(string version)
    {
        var parts = version.Split('.').Select(int.Parse).ToArray();
        if (parts.Length is < 2 or > 3) throw new InvalidDataException("Reader version must be major.minor[.patch].");
        return $"{parts[0]}.{parts[1] + 1}.0";
    }

    private static string NextMajor(string version)
    {
        var parsed = Version.Parse(version);
        return $"{parsed.Major + 1}.0.0";
    }

    private static string Hex(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
    private static string HexIdentifier(uint value, int digits) =>
        value.ToString("X" + digits, CultureInfo.InvariantCulture);
    private static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    private static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, JsonOptions)
        ?? throw new InvalidDataException("JSON input was empty.");

    private static string RequiredOption(string[] args, string name) =>
        Option(args, name) ?? throw new ArgumentException("Missing required option " + name + ".");

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i] == name)
                return i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing value for " + name + ".");
        return null;
    }
}
