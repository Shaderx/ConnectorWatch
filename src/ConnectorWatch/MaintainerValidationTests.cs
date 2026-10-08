using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            var smoke = CaptureFixture(request, probe, "fixture-smoke");
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
            full = CaptureFixture(request, probe, "fixture-full");
        Check(full.Outcome == "full_validation_passed", "paired idle/workload comparison passes");
        Check(full.IndependentComparisons.Count == 4 && full.IndependentComparisons.All(c => c.Passed),
            "both rails pass both independent phases");
        Check(full.SensorValidation is { Response.Passed: true, Timing.Passed: true } &&
            full.SensorValidation.Timing.NativeSampleCount == 12 &&
            full.SensorValidation.Timing.NativeSampleSpanSeconds == 11,
            "response and monotonic timing evidence pass with measured metrics");
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        var sensorEvidenceBytes = JsonSerializer.SerializeToUtf8Bytes(full, jsonOptions);
        var sensorEvidenceHash = Convert.ToHexString(SHA256.HashData(sensorEvidenceBytes)).ToLowerInvariant();
        var runtimeEntry = new DriverCatalogEntry(new DriverIdentity("10DE", "2B85", "89EE1043",
                "windows", "x64", "999.99"), "A612-A613-v1", "1.0.0", "1.1.0",
            "1.5.0", "1.6.0", "approved", sensorEvidenceHash, DateTimeOffset.UtcNow, "fixture");
        var runtimeSnapshot = SensorValidationPolicy.SnapshotFromEvidence(sensorEvidenceBytes, sensorEvidenceHash,
            runtimeEntry, "A612-A613-v1", "1.0.0", DirectNvRails.AppVersion, 14);
        Check(runtimeSnapshot.EvidenceState == "VERIFIED" && runtimeSnapshot.ResponseState == "PASS" &&
            runtimeSnapshot.TimingState == "PASS" && runtimeSnapshot.CatalogRevision == 14 &&
            runtimeSnapshot.TimingBasis == SensorValidationPolicy.TimingBasis,
            "exact scoped evidence digest verifies into the runtime sensor-validation snapshot: " +
            runtimeSnapshot.EvidenceState + " " + runtimeSnapshot.Detail);
        var legacyEvidence = JsonNode.Parse(sensorEvidenceBytes)!.AsObject();
        legacyEvidence.Remove("sensor_validation");
        var legacyEvidenceBytes = JsonSerializer.SerializeToUtf8Bytes(legacyEvidence, jsonOptions);
        var legacyEvidenceHash = Convert.ToHexString(SHA256.HashData(legacyEvidenceBytes)).ToLowerInvariant();
        var legacySnapshot = SensorValidationPolicy.SnapshotFromEvidence(legacyEvidenceBytes, legacyEvidenceHash,
            runtimeEntry with { EvidenceSha256 = legacyEvidenceHash }, "A612-A613-v1", "1.0.0",
            DirectNvRails.AppVersion, 14);
        Check(legacySnapshot.EvidenceState == "LEGACY" && legacySnapshot.ResponseState == "UNKNOWN" &&
            legacySnapshot.TimingState == "UNKNOWN",
            "full older evidence without the optional sensor_validation field remains LEGACY");
        var invalidHash = new string('a', 64);
        Check(SensorValidationPolicy.SnapshotFromEvidence(sensorEvidenceBytes, invalidHash, runtimeEntry,
                "A612-A613-v1", "1.0.0", DirectNvRails.AppVersion, 14).EvidenceState == "REJECTED",
            "runtime sensor validation rejects evidence whose bytes do not match the catalog digest");
        var wrongScopeEntry = runtimeEntry with
        {
            Identity = new DriverIdentity("10DE", "2B85", "89EE1043", "windows", "x64", "999.98"),
        };
        Check(SensorValidationPolicy.SnapshotFromEvidence(sensorEvidenceBytes, sensorEvidenceHash, wrongScopeEntry,
                "A612-A613-v1", "1.0.0", DirectNvRails.AppVersion, 14).EvidenceState == "REJECTED",
            "runtime sensor validation rejects evidence outside the exact catalog driver scope");
        var failedTimingEvidence = JsonSerializer.Deserialize<MaintainerValidationEvidence>(
            sensorEvidenceBytes, jsonOptions)!;
        failedTimingEvidence.SensorValidation!.Timing.MaximumAbsoluteIntervalDeviationSeconds = 0.6;
        var failedTimingBytes = JsonSerializer.SerializeToUtf8Bytes(failedTimingEvidence, jsonOptions);
        var failedTimingHash = Convert.ToHexString(SHA256.HashData(failedTimingBytes)).ToLowerInvariant();
        var failedTimingSnapshot = SensorValidationPolicy.SnapshotFromEvidence(failedTimingBytes, failedTimingHash,
            runtimeEntry with { EvidenceSha256 = failedTimingHash }, "A612-A613-v1", "1.0.0", DirectNvRails.AppVersion, 14);
        Check(failedTimingSnapshot.ResponseState == "PASS" && failedTimingSnapshot.TimingState == "FAIL",
            "runtime trust recomputes measured timing limits instead of trusting the evidence PASS flag");
        foreach (var malformedField in new[] { "scope", "sensor_validation" })
        {
            var malformed = JsonNode.Parse(sensorEvidenceBytes)!.AsObject();
            malformed[malformedField] = new JsonArray();
            var malformedBytes = JsonSerializer.SerializeToUtf8Bytes(malformed, jsonOptions);
            var malformedHash = Convert.ToHexString(SHA256.HashData(malformedBytes)).ToLowerInvariant();
            Check(SensorValidationPolicy.SnapshotFromEvidence(malformedBytes, malformedHash,
                    runtimeEntry with { EvidenceSha256 = malformedHash }, "A612-A613-v1", "1.0.0",
                    DirectNvRails.AppVersion, 14).EvidenceState == "REJECTED",
                "malformed evidence container is rejected without interrupting acquisition: " + malformedField);
        }
        SensorEvidenceCacheRecovery(sensorEvidenceBytes, runtimeEntry);
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
            var failed = CaptureFixture(failedRequest, probe, "fixture-failed");
            Check(failed.Outcome == "failed", "independent mismatch fails validation");
            ExpectRejectedProposal(failed, "failed comparison cannot propose approval");
        }

        foreach (var failure in new[] { "reused timestamp", "stale poll" })
        {
            var timingRequest = Request();
            timingRequest.Oracle = Oracle(idle, workload);
            var phasePairs = timingRequest.Oracle.Phases[0].Samples;
            if (failure == "reused timestamp")
                phasePairs[1].IndependentTimestampUtc = phasePairs[0].IndependentTimestampUtc;
            else
                phasePairs[0].SourcePollTimestampUtc = phasePairs[0].IndependentTimestampUtc!.Value.AddSeconds(-2);
            using var probe = new FixtureProbe(metadata, idle, workload, idle);
            var failed = CaptureFixture(timingRequest, probe, "fixture-" + failure);
            Check(failed.Outcome == "failed" && failed.SensorValidation?.Timing.Passed == false,
                "oracle timing failure prevents full validation: " + failure);
            ExpectRejectedProposal(failed, "oracle timing failure cannot propose approval: " + failure);
        }
        var cadenceRequest = Request();
        cadenceRequest.Oracle = Oracle(idle, workload);
        using (var probe = new FixtureProbe(metadata, idle, workload, idle))
        {
            long cadenceTicks = 0;
            int cadenceIndex = 0;
            var failed = MaintainerValidationCommand.Capture(cadenceRequest, probe, "fixture-cadence",
                () => cadenceTicks += (long)(Stopwatch.Frequency * (++cadenceIndex == 6 ? 1.7 : 1.0)), _ => { });
            Check(failed.Outcome == "failed" && failed.SensorValidation?.Timing.Passed == false,
                "excessive measured cadence variance prevents full validation");
            ExpectRejectedProposal(failed, "failed cadence cannot propose approval");
        }

        using (var probe = new FixtureProbe(metadata, idle, workload, idle,
            statusReturnCode: -5, guardStatus: "pass"))
        {
            var failedNative = CaptureFixture(Request(), probe, "fixture-native-failed");
            Check(failedNative.Outcome == "failed" &&
                failedNative.NativeOperations.Skip(1).All(o => o.ReturnCode == -5),
                "native return-code failures remain visible and fail closed");
        }

        var bootstrap = BootstrapAttestation("616.56", "616.92");
        var bootstrapBytes = Encoding.UTF8.GetBytes(bootstrap);
        var parsedBootstrap = BootstrapDriverApprovalPolicy.Parse(bootstrapBytes);
        Check(parsedBootstrap.Approvals.Select(x => x.DriverVersion).SequenceEqual(["616.56", "616.92"]),
            "bootstrap policy accepts exactly the documented legacy drivers");
        var bootstrapHash = Convert.ToHexString(SHA256.HashData(bootstrapBytes)).ToLowerInvariant();
        var bootstrapEntry = new DriverCatalogEntry(new DriverIdentity("10DE", "2B85", "89EE1043",
                "windows", "x64", "616.56"), "A612-A613-v1", "1.0.0", "1.1.0",
            "1.5.0", "1.6.0", "approved", bootstrapHash, DateTimeOffset.UtcNow, "legacy fixture");
        var bootstrapSnapshot = SensorValidationPolicy.SnapshotFromEvidence(bootstrapBytes, bootstrapHash,
            bootstrapEntry, "A612-A613-v1", "1.0.0", DirectNvRails.AppVersion, 1);
        Check(bootstrapSnapshot.EvidenceState == "LEGACY" && bootstrapSnapshot.ResponseState == "UNKNOWN" &&
            bootstrapSnapshot.TimingState == "UNKNOWN",
            "fixed bootstrap approval evidence remains acquisition-usable without sensor-validation PASS");
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
        SmokeSamples = 12,
        SampleIntervalMilliseconds = 1000,
    };

    private static void SensorEvidenceCacheRecovery(byte[] evidence, DriverCatalogEntry entry)
    {
        var root = Path.Combine(Path.GetTempPath(), "ConnectorWatch-sensor-evidence-test-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "bundled");
        var local = Path.Combine(root, "driver-validation");
        var fileName = "evidence-" + entry.EvidenceSha256 + ".json";
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(local);
        try
        {
            File.WriteAllText(Path.Combine(bundled, fileName), "invalid bundled evidence");
            File.WriteAllBytes(Path.Combine(local, fileName), evidence);
            using var handler = new EvidenceHandler(evidence);
            using var client = new HttpClient(handler);
            var store = new SensorValidationEvidenceStore(root, bundled, client,
                TimeSpan.FromSeconds(2), TimeProvider.System);
            SensorValidationSnapshot Resolve() => store.Resolve(entry.Identity, entry,
                "A612-A613-v1", "1.0.0", DirectNvRails.AppVersion, 14, CancellationToken.None);
            Check(Resolve().ResponseState == "PASS" && handler.Requests == 0,
                "valid local evidence recovers a rejected bundled file without downloading");
            File.WriteAllBytes(Path.Combine(bundled, fileName), []);
            Check(Resolve().TimingState == "PASS" && handler.Requests == 0,
                "valid local evidence recovers an empty bundled file");
            File.WriteAllText(Path.Combine(local, fileName), "invalid local evidence");
            Check(Resolve().EvidenceState == "REJECTED",
                "invalid evidence remains rejected while its exact replacement downloads");
            var recovered = SpinWait.SpinUntil(() => Resolve().TimingState == "PASS", TimeSpan.FromSeconds(2));
            var recoveryState = Resolve();
            Check(recovered && handler.Requests == 1,
                "downloaded digest-verified evidence recovers rejected bundled and local files: " +
                $"requests={handler.Requests}, state={recoveryState.EvidenceState}, detail={recoveryState.Detail}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class EvidenceHandler(byte[] evidence) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(evidence),
            });
        }
    }

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
        PairingProvenance = new MaintainerOraclePairingProvenance
        {
            SourceName = "HWiNFO Shared Memory",
            SensorId = "E0002000",
            PairingMethod = "nearest_unused_by_read_timestamp",
            VoltageUnit = "V",
            PowerUnit = "W",
            CurrentDerivation = "rail_power_divided_by_rail_voltage",
        },
        Phases =
        [
            Phase("idle", idle, 0.5, 12.05, 1.5, 12.02),
            Phase("workload", workload, 4.0, 11.98, 25.0, 11.93),
        ],
    };

    private static MaintainerOraclePhase Phase(string name, byte[] response,
        double pcieA, double pcieV, double hpwrA, double hpwrV)
    {
        var phaseStart = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero)
            .AddHours(name == "idle" ? 0 : 1);
        return new MaintainerOraclePhase
        {
            Name = name,
            Samples = Enumerable.Range(0, 2).Select(index =>
            {
                var nativeTimestamp = phaseStart.AddSeconds(index);
                var independentTimestamp = nativeTimestamp.AddMilliseconds(100);
                return new MaintainerOracleSample
                {
                    StatusResponseBase64 = Convert.ToBase64String(response),
                    NativeReturnCode = 0,
                    GuardStatus = "pass",
                    PairId = $"{name}-{index}",
                    NativeTimestampUtc = nativeTimestamp,
                    IndependentTimestampUtc = independentTimestamp,
                    SourcePollTimestampUtc = independentTimestamp.AddMilliseconds(-500),
                    SourcePollPeriodMilliseconds = 500,
                    Pcie12V = new MaintainerIndependentReading { VoltageV = pcieV, CurrentA = pcieA },
                    TwelveVHpwr = new MaintainerIndependentReading { VoltageV = hpwrV, CurrentA = hpwrA },
                };
            }).ToList(),
        };
    }

    private static MaintainerValidationEvidence CaptureFixture(MaintainerValidationRequest request,
        FixtureProbe probe, string id)
    {
        long ticks = 0;
        return MaintainerValidationCommand.Capture(request, probe, id,
            () => ticks += Stopwatch.Frequency,
            _ => { });
    }

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
            statuses = new Queue<byte[]>(Enumerable.Range(0, 100)
                .Select(index => (index % 3) switch { 0 => first, 1 => second, _ => third }));
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
