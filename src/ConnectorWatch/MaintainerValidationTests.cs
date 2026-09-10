using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ConnectorWatch;

public static class MaintainerValidationTests
{
    public static void Run()
    {
        var metadata = MetadataFixture();
        var idle = StatusFixture(500, 12_050_000, 1_500, 12_020_000, 11, 12);
        var workload = StatusFixture(4_000, 11_980_000, 25_000, 11_930_000, 21, 22);
        var request = Request();

        using (var probe = new FixtureProbe(metadata, idle, workload, idle))
        {
            var smoke = MaintainerValidationCommand.Capture(request, probe, "fixture-smoke");
            Check(smoke.Outcome == "provisional_structural_smoke_passed",
                "structural smoke remains provisional");
            Check(!ContainsSensitiveText(smoke, FixtureProbe.Uuid),
                "public evidence sanitizes GPU UUID from raw buffers");
            Check(smoke.NativeOperations[0].RedactionCount > 0,
                "raw-response redactions are counted");
            ExpectRejectedProposal(smoke, "provisional evidence cannot propose approval");
        }

        request.Oracle = Oracle(idle, workload);
        MaintainerValidationEvidence full;
        using (var probe = new FixtureProbe(metadata, idle, workload, idle))
            full = MaintainerValidationCommand.Capture(request, probe, "fixture-full");
        Check(full.Outcome == "full_validation_passed", "paired idle/workload comparison passes");
        Check(full.IndependentComparisons.Count == 4 && full.IndependentComparisons.All(c => c.Passed),
            "both rails pass both independent phases");
        var proposal = MaintainerValidationCommand.BuildProposal(full, new string('a', 64), new string('b', 64));
        Check(proposal.ProposalStatus == "maintainer_review_required" &&
            proposal.ProposedEntry.Decision == "approved", "passing evidence yields review-only proposal");
        Check(proposal.ProposedEntry.VendorId == "10DE" && proposal.ProposedEntry.DeviceId == "2B85" &&
            proposal.ProposedEntry.SubsystemId == "89EE1043", "proposal preserves exact canonical PCI scope");
        Check(proposal.ProposedEntry.MinimumReaderVersion == "1.0.0" &&
            proposal.ProposedEntry.MaximumReaderVersionExclusive == "1.1.0", "reader range is bounded");

        var failedRequest = Request();
        failedRequest.Oracle = Oracle(idle, workload);
        failedRequest.Oracle.Phases.Single(p => p.Name == "workload").Samples[0]
            .TwelveVHpwr.VoltageV = 9.0;
        using (var probe = new FixtureProbe(metadata, idle, workload, idle))
        {
            var failed = MaintainerValidationCommand.Capture(failedRequest, probe, "fixture-failed");
            Check(failed.Outcome == "failed", "independent mismatch fails validation");
            ExpectRejectedProposal(failed, "failed comparison cannot propose approval");
        }

        using (var probe = new FixtureProbe(metadata, idle, workload, idle,
            statusReturnCode: -5, guardStatus: "pass"))
        {
            var failedNative = MaintainerValidationCommand.Capture(Request(), probe, "fixture-native-failed");
            Check(failedNative.Outcome == "failed" &&
                failedNative.NativeOperations.Skip(1).All(o => o.ReturnCode == -5),
                "native return-code failures remain visible and fail closed");
        }

        var bootstrap = BootstrapAttestation("616.56", "616.92");
        var parsedBootstrap = BootstrapDriverApprovalPolicy.Parse(Encoding.UTF8.GetBytes(bootstrap));
        Check(parsedBootstrap.Approvals.Select(x => x.DriverVersion).SequenceEqual(["616.56", "616.92"]),
            "bootstrap policy accepts exactly the documented legacy drivers");
        ExpectBootstrapRejected(BootstrapAttestation("616.56", "617.00"),
            "bootstrap policy rejects a future driver");
        ExpectBootstrapRejected(BootstrapAttestation("616.92", "616.92"),
            "bootstrap policy rejects duplicate scope and omitted historical driver");
        ExpectBootstrapRejected(bootstrap.Replace("\"device_id\":\"2B85\"", "\"device_id\":\"2B86\"",
                StringComparison.Ordinal),
            "bootstrap policy rejects widened hardware scope");
        ExpectBootstrapRejected(bootstrap.Replace("\"maximum_app_version_exclusive\":\"1.6.0\"",
                "\"maximum_app_version_exclusive\":\"2.0.0\"", StringComparison.Ordinal),
            "bootstrap policy rejects widened application range");

        Console.WriteLine("PASS: maintainer validation evidence and proposal gates.");
    }

    private static MaintainerValidationRequest Request() => new()
    {
        GpuUuid = FixtureProbe.Uuid,
        ToolCommit = new string('1', 40),
        ApprovedMetadataResponseSha256 = Convert.ToHexString(SHA256.HashData(MetadataFixture())).ToLowerInvariant(),
        SmokeSamples = 3,
        SampleIntervalMilliseconds = 0,
    };

    private static MaintainerComparisonOracle Oracle(byte[] idle, byte[] workload) => new()
    {
        VendorId = "10DE",
        DeviceId = "2B85",
        SubsystemId = "89EE1043",
        Os = "windows",
        Architecture = "x64",
        DriverVersion = "999.99",
        ReaderProfile = "A612-A613-v1",
        ApprovedMetadataResponseSha256 = Convert.ToHexString(SHA256.HashData(MetadataFixture())).ToLowerInvariant(),
        VoltageAbsoluteToleranceV = 0.05,
        CurrentAbsoluteToleranceA = 0.1,
        CurrentRelativeTolerance = 0.05,
        Phases =
        [
            Phase("idle", idle, 0.5, 12.05, 1.5, 12.02),
            Phase("workload", workload, 4.0, 11.98, 25.0, 11.93),
        ],
    };

    private static MaintainerOraclePhase Phase(string name, byte[] response,
        double pcieA, double pcieV, double hpwrA, double hpwrV) => new()
    {
        Name = name,
        Samples = Enumerable.Range(0, 2).Select(_ => new MaintainerOracleSample
        {
            StatusResponseBase64 = Convert.ToBase64String(response),
            NativeReturnCode = 0,
            GuardStatus = "pass",
            Pcie12V = new MaintainerIndependentReading { VoltageV = pcieV, CurrentA = pcieA },
            TwelveVHpwr = new MaintainerIndependentReading { VoltageV = hpwrV, CurrentA = hpwrA },
        }).ToList(),
    };

    private static byte[] MetadataFixture()
    {
        var buffer = DirectNvRails.CreateMetadataRequest();
        Write(buffer, 4, 1);
        Write(buffer, 0x10, 0x7ABF);
        WriteMetadata(buffer, 1, 8, 255);
        WriteMetadata(buffer, 2, 8, 218);
        Encoding.UTF8.GetBytes(FixtureProbe.Uuid).CopyTo(buffer, buffer.Length - 80);
        return buffer;
    }

    private static byte[] StatusFixture(uint pcieMilliamps, uint pcieMicrovolts,
        uint hpwrMilliamps, uint hpwrMicrovolts, uint pcieRaw, uint hpwrRaw)
    {
        var buffer = DirectNvRails.CreateStatusRequest(0x7ABF);
        WriteStatus(buffer, 1, pcieMilliamps, pcieMicrovolts, pcieRaw);
        WriteStatus(buffer, 2, hpwrMilliamps, hpwrMicrovolts, hpwrRaw);
        return buffer;
    }

    private static void WriteMetadata(byte[] buffer, int index, uint type, uint source)
    {
        var offset = 0x18 + index * 0x3C;
        Write(buffer, offset + 0x1C, type);
        Write(buffer, offset + 0x20, source);
    }

    private static void WriteStatus(byte[] buffer, int channel, uint milliamps,
        uint microvolts, uint raw)
    {
        var offset = 8 + channel * 0x2C;
        Write(buffer, offset + 0x20, milliamps);
        Write(buffer, offset + 0x24, microvolts);
        Write(buffer, offset + 0x28, raw);
    }

    private static void Write(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    private static bool ContainsSensitiveText(MaintainerValidationEvidence evidence, string secret)
    {
        foreach (var operation in evidence.NativeOperations)
        {
            var raw = Convert.FromBase64String(operation.SanitizedResponseBase64);
            if (Encoding.UTF8.GetString(raw).Contains(secret, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void ExpectRejectedProposal(MaintainerValidationEvidence evidence, string name)
    {
        try
        {
            _ = MaintainerValidationCommand.BuildProposal(evidence, new string('a', 64), new string('b', 64));
            throw new Exception("FAILED: " + name);
        }
        catch (InvalidOperationException)
        {
            // expected
        }
    }

    private static string BootstrapAttestation(string first, string second) => $$"""
        {"schema_version":1,"attestation_type":"connectorwatch-bootstrap-driver-approvals","policy":"one-time-initial-catalog-known-drivers","approver":"Shaderx","approval_utc":"2026-09-10T12:31:59Z","reason":"Explicit maintainer approval of the existing documented driver versions.","approvals":[
        {"vendor_id":"10DE","device_id":"2B85","subsystem_id":"89EE1043","os":"windows","architecture":"x64","driver_version":"{{first}}","reader_profile":"A612-A613-v1","minimum_reader_version":"1.0.0","maximum_reader_version_exclusive":"1.1.0","minimum_app_version":"1.5.0","maximum_app_version_exclusive":"1.6.0","validation_level":"historical_physical_validation","validation_record":"docs/VALIDATION.md","limitations":"Finite validation on the original machine.","public_rationale":"Maintainer-approved legacy driver with historical physical validation."},
        {"vendor_id":"10DE","device_id":"2B85","subsystem_id":"89EE1043","os":"windows","architecture":"x64","driver_version":"{{second}}","reader_profile":"A612-A613-v1","minimum_reader_version":"1.0.0","maximum_reader_version_exclusive":"1.1.0","minimum_app_version":"1.5.0","maximum_app_version_exclusive":"1.6.0","validation_level":"structural_validation_without_independent_comparison","validation_record":"docs/DRIVER-616.92.md","limitations":"No independent HWiNFO idle/workload comparison.","public_rationale":"Maintainer-approved existing driver from structural evidence; independent comparison was not performed."}]}
        """;

    private static void ExpectBootstrapRejected(string json, string name)
    {
        try
        {
            _ = BootstrapDriverApprovalPolicy.Parse(Encoding.UTF8.GetBytes(json));
            throw new Exception("FAILED: " + name);
        }
        catch (InvalidDataException)
        {
            // expected
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }

    private sealed class FixtureProbe : IMaintainerNativeProbe
    {
        public const string Uuid = "GPU-00000000-0000-0000-0000-000000000099";
        private readonly byte[] metadata;
        private readonly Queue<byte[]> statuses;
        private readonly int statusReturnCode;
        private readonly string guardStatus;

        public FixtureProbe(byte[] metadata, byte[] first, byte[] second, byte[] third,
            int statusReturnCode = 0, string guardStatus = "pass")
        {
            this.metadata = metadata;
            statuses = new Queue<byte[]>([first, second, third]);
            this.statusReturnCode = statusReturnCode;
            this.guardStatus = guardStatus;
        }

        public MaintainerProbeIdentity Identity { get; } = new(
            DirectNvRails.TargetPciIdentifier, DirectNvRails.TargetSubsystemIdentifier,
            "999.99", "windows", "x64", Uuid);

        public MaintainerNativeOperation CaptureMetadata() =>
            new("A612 metadata", metadata.ToArray(), 0, "pass", false, 4);

        public MaintainerNativeOperation CaptureStatus(uint mask) =>
            new("A613 status", statuses.Dequeue().ToArray(), statusReturnCode, guardStatus, false, 5);

        public void Dispose() { }
    }
}
