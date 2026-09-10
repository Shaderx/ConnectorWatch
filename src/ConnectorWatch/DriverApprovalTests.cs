using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch;

/// <summary>Offline signed-catalog security and unknown-to-approved-to-revoked pilot fixtures.</summary>
public static class DriverApprovalTests
{
    private static readonly DriverIdentity Identity = new("10DE", "2B85", "89EE1043",
        "windows", "x64", "999.1");

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = new SignedMetadataKey("fixture-p256",
            Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo()));
        var now = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var approved = Envelope(signingKey, CatalogPayload(1, now, now.AddDays(30), "approved"));
        var revoked = Envelope(signingKey, CatalogPayload(2, now.AddMinutes(1), now.AddDays(30), "revoked"));

        VerifierSecurityCases(signingKey, publicKey, approved, now);
        await CacheAndPolicyCases(signingKey, publicKey, approved, now);
        await OrderlyRestartCleanupCase(signingKey, publicKey, approved, now);
        await TransientStartupContentionFailsClosed(publicKey, revoked, now);
        BundledRollbackCase(signingKey, publicKey, approved, now);
        await PilotUnknownApproveRevoke(publicKey, approved, revoked, now);
        Console.WriteLine("PASS: signed driver approval catalog security and lifecycle fixtures.");
    }

    private static void VerifierSecurityCases(ECDsa signingKey, SignedMetadataKey publicKey,
        byte[] approved, DateTimeOffset now)
    {
        var verifier = new SignedMetadataVerifier(new[] { publicKey });
        var verified = verifier.VerifyEnvelope(approved);
        var catalog = DriverCatalogPayload.ParseVerified(verified,
            new HashSet<string> { DriverApprovalService.EmbeddedReaderProfile });
        Check(catalog.CatalogRevision == 1 && catalog.Entries.Count == 1, "valid signed catalog");

        var document = JsonDocument.Parse(approved);
        var payload = Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString()!);
        payload[^2] ^= 1;
        var tampered = RewriteEnvelope(document.RootElement, payload,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        ExpectInvalid(() => verifier.VerifyEnvelope(tampered), "tampered payload signature");
        ExpectInvalid(() => new SignedMetadataVerifier(Array.Empty<SignedMetadataKey>()).VerifyEnvelope(approved),
            "unknown signing key");
        ExpectInvalid(() => new SignedMetadataVerifier(new[]
            { new SignedMetadataKey(publicKey.KeyId, publicKey.SubjectPublicKeyInfoBase64, revoked: true) })
            .VerifyEnvelope(approved), "revoked signing key");

        using var nextSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nextPublicKey = new SignedMetadataKey("fixture-next-p256",
            Convert.ToBase64String(nextSigningKey.ExportSubjectPublicKeyInfo()));
        var nextEnvelope = Envelope(nextSigningKey,
            CatalogPayload(2, now, now.AddDays(30), "approved", "rotated key fixture"),
            nextPublicKey.KeyId);
        var dualKeyVerifier = new SignedMetadataVerifier(new[] { publicKey, nextPublicKey });
        var oldVerified = dualKeyVerifier.VerifyEnvelope(approved);
        var nextVerified = dualKeyVerifier.VerifyEnvelope(nextEnvelope);
        var oldHeader = SignedMetadataHeader.Parse(oldVerified, DriverCatalogPayload.PayloadType,
            "catalog_revision");
        var oldWatermark = SignedMetadataPolicy.NextWatermark(oldHeader, oldVerified, null);
        var nextHeader = SignedMetadataHeader.Parse(nextVerified, DriverCatalogPayload.PayloadType,
            "catalog_revision");
        SignedMetadataPolicy.Validate(nextHeader, nextVerified, oldWatermark, now.AddMinutes(1));
        var rotatedVerifier = new SignedMetadataVerifier(new[]
        {
            new SignedMetadataKey(publicKey.KeyId, publicKey.SubjectPublicKeyInfoBase64, revoked: true),
            nextPublicKey,
        });
        ExpectInvalid(() => rotatedVerifier.VerifyEnvelope(approved), "rotated old key revoked");
        _ = rotatedVerifier.VerifyEnvelope(nextEnvelope);

        var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(approved)
            .Replace("\"envelope_schema\":1", "\"envelope_schema\":1,\"envelope_schema\":1"));
        ExpectInvalid(() => verifier.VerifyEnvelope(duplicate), "duplicate envelope property");
        ExpectInvalid(() => verifier.VerifyEnvelope(new byte[SignedMetadataVerifier.MaximumEnvelopeBytes + 1]),
            "oversized envelope");
        var wrongScope = new DriverIdentity("10DE", "2B85", "89EE1043", "windows", "x64", "999.2");
        var decision = new DriverApprovalDecision(DriverApprovalState.Approved, true, "fixture", 1,
            now.AddDays(1), false, Identity, DriverApprovalService.EmbeddedReaderProfile,
            DriverApprovalService.EmbeddedReaderVersion, DriverApprovalService.ApplicationVersion);
        ExpectApprovalFailure(() => decision.RequireApplicable(wrongScope,
            DriverApprovalService.EmbeddedReaderProfile, DriverApprovalService.EmbeddedReaderVersion,
            DriverApprovalService.ApplicationVersion, now), "scope-bound assertion");
        ExpectApprovalFailure(() => decision.RequireApplicable(Identity,
            DriverApprovalService.EmbeddedReaderProfile, DriverApprovalService.EmbeddedReaderVersion,
            DriverApprovalService.ApplicationVersion, now.AddDays(2)), "expiry assertion");
    }

    private static async Task CacheAndPolicyCases(ECDsa signingKey, SignedMetadataKey publicKey,
        byte[] approved, DateTimeOffset now)
    {
        var folder = TemporaryDirectory();
        try
        {
            var handler = new QueueHandler();
            handler.Enqueue(approved);
            var clock = new FixtureTimeProvider(now.AddMinutes(2));
            using (var service = Service(folder, publicKey, handler, clock))
            {
                Check((await service.RefreshAsync(true)).Status == DriverCatalogRefreshStatus.Updated,
                    "signed catalog refresh");
                Check(service.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile, "1.0", "1.4.0").MayStart,
                    "exact approved scope starts");

                var old = Envelope(signingKey, CatalogPayload(1, now, now.AddDays(30), "revoked", "old"));
                handler.Enqueue(old);
                Check((await service.RefreshAsync(true)).Status == DriverCatalogRefreshStatus.Failed &&
                    service.CatalogRevision == 1, "same-revision equivocation rejected");

                var updateRequired = Envelope(signingKey,
                    CatalogPayload(2, now.AddMinutes(1), now.AddDays(30), "approved",
                        minimumAppVersion: "2.0", maximumAppVersionExclusive: "3.0"));
                handler.Enqueue(updateRequired);
                Check((await service.RefreshAsync(true)).Status == DriverCatalogRefreshStatus.Updated,
                    "higher catalog revision accepted");
                Check(service.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile, "1.0", "1.4.0").State ==
                    DriverApprovalState.RequiresNewerApp, "required application upgrade");

                handler.Enqueue(approved);
                Check((await service.RefreshAsync(true)).Status == DriverCatalogRefreshStatus.Failed &&
                    service.CatalogRevision == 2, "rollback rejected and verified cache retained");
            }

            using (var offline = Service(folder, publicKey, new ThrowingHandler(), clock))
                Check(offline.CatalogRevision == 2, "verified unexpired cache loads offline");

            var expiredClock = new FixtureTimeProvider(now.AddDays(31));
            using (var expired = Service(folder, publicKey, new ThrowingHandler(), expiredClock))
            {
                var decision = expired.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile, "1.0", "2.1");
                Check(decision.State == DriverApprovalState.ApprovalUnavailable && !decision.MayStart,
                    "expired offline cache pauses private acquisition");
                Check(expired.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile, "1.0", "2.1",
                    developerMode: true).IsUnvalidated, "developer mode labels missing approval bypass");
            }

            File.WriteAllText(Path.Combine(folder, "driver-catalog.watermark.json"), "{broken");
            var blockedHandler = new QueueHandler();
            blockedHandler.Enqueue(approved);
            using (var blocked = Service(folder, publicKey, blockedHandler, clock))
            {
                Check(!blocked.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile, "1.0", "1.4.0",
                        developerMode: true).MayStart,
                    "malformed rollback watermark fails closed even in developer mode");
                Check((await blocked.RefreshAsync(true)).Status == DriverCatalogRefreshStatus.Failed &&
                    blockedHandler.Requests == 0, "malformed rollback watermark blocks refresh");
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task PilotUnknownApproveRevoke(SignedMetadataKey publicKey,
        byte[] approved, byte[] revoked, DateTimeOffset now)
    {
        var folder = TemporaryDirectory();
        try
        {
            var handler = new QueueHandler();
            var releaseApproval = handler.Enqueue(approved, blocked: true);
            using var service = Service(folder, publicKey, handler, new FixtureTimeProvider(now.AddMinutes(2)));
            var created = 0;
            FakeElectricalSource? native = null;
            using var wrapper = new ApprovalRailSource("GPU-fixture", service, developer: false,
                () => Identity,
                assertion =>
                {
                    created++;
                    native = new FakeElectricalSource(() => assertion(Identity).RequireApplicable(Identity,
                        DirectNvRails.ReaderProfile, DirectNvRails.ReaderVersion, DirectNvRails.AppVersion,
                        now.AddMinutes(2)));
                    return native;
                });
            ExpectPaused(() => wrapper.ReadElectrical(now), DriverApprovalState.ApprovalUnavailable,
                "unknown driver cannot construct native reader");
            Check(created == 0, "unknown driver made no native private call");

            var approvalRefresh = service.RefreshAsync(true);
            releaseApproval.SetResult();
            Check((await approvalRefresh).Status == DriverCatalogRefreshStatus.Updated,
                "pilot approval refresh");
            Check(handler.Requests == 1, "concurrent startup and manual refresh share one request");
            _ = wrapper.ReadElectrical(now.AddSeconds(1));
            Check(created == 1 && native!.Reads == 1, "approved driver starts and samples");

            handler.Enqueue(revoked);
            Check((await service.RefreshAsync(true)).Status == DriverCatalogRefreshStatus.Updated,
                "pilot revocation refresh");
            ExpectPaused(() => wrapper.ReadElectrical(now.AddSeconds(2)), DriverApprovalState.Revoked,
                "revocation pauses reader");
            Check(native!.Disposed && native.Reads == 1, "revocation disposes reader before another sample");
            Check(!service.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile, "1.0", "1.4.0",
                developerMode: true).MayStart, "developer mode cannot bypass revocation");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task OrderlyRestartCleanupCase(ECDsa signingKey,
        SignedMetadataKey publicKey, byte[] approved, DateTimeOffset now)
    {
        var folder = TemporaryDirectory();
        try
        {
            var clock = new FixtureTimeProvider(now.AddMinutes(2));
            var seedHandler = new QueueHandler();
            seedHandler.Enqueue(approved);
            using (var seed = Service(folder, publicKey, seedHandler, clock))
                Check((await seed.RefreshAsync(true)).Status == DriverCatalogRefreshStatus.Updated,
                    "restart fixture seeded verified cache");

            var revisionTwo = Envelope(signingKey,
                CatalogPayload(2, now.AddMinutes(1), now.AddDays(30), "approved", "restart fixture"));

            var cooperativeHandler = new QueueHandler();
            cooperativeHandler.Enqueue(revisionTwo, blocked: true);
            var cooperative = Service(folder, publicKey, cooperativeHandler, clock,
                shutdownWaitTimeout: TimeSpan.FromMilliseconds(300));
            var cooperativeRefresh = cooperative.RefreshAsync(true);
            cooperative.Dispose();
            Check(cooperativeRefresh.IsCompleted, "dispose cancels and joins a cooperative refresh");
            await ExpectCanceled(cooperativeRefresh, "cooperative refresh canceled during disposal");

            var stubbornHandler = new QueueHandler();
            var releaseStubborn = stubbornHandler.Enqueue(revisionTwo, blocked: true,
                ignoreCancellation: true);
            var stubborn = Service(folder, publicKey, stubbornHandler, clock,
                shutdownWaitTimeout: TimeSpan.FromMilliseconds(300));
            var stubbornRefresh = stubborn.RefreshAsync(true);
            var elapsed = Stopwatch.StartNew();
            stubborn.Dispose();
            elapsed.Stop();
            Check(elapsed.Elapsed >= TimeSpan.FromMilliseconds(150) && elapsed.Elapsed < TimeSpan.FromSeconds(2) &&
                  !stubbornRefresh.IsCompleted,
                "dispose bounds its join when a transport ignores cancellation");
            releaseStubborn.SetResult();
            await ExpectCanceled(stubbornRefresh, "late response cannot survive disposal cancellation");

            // Simulate the retiring daemon still owning the cache transaction while its
            // replacement starts. The replacement must wait, then load the verified cache.
            var lockPath = Path.Combine(folder, "driver-catalog.lock");
            using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None);
            var restartingTask = Task.Run(() => Service(folder, publicKey, new ThrowingHandler(), clock,
                shutdownWaitTimeout: TimeSpan.FromSeconds(1)));
            await Task.Delay(100);
            Check(!restartingTask.IsCompleted, "replacement waits for transient startup cache lock contention");
            heldLock.Dispose();
            using var restarted = await restartingTask.WaitAsync(TimeSpan.FromSeconds(2));
            Check(restarted.CatalogRevision == 1 &&
                  restarted.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile, "1.0", "1.4.0").MayStart,
                "replacement loads cache after retiring writer releases startup lock");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task TransientStartupContentionFailsClosed(SignedMetadataKey publicKey,
        byte[] revoked, DateTimeOffset now)
    {
        var folder = TemporaryDirectory();
        try
        {
            var clock = new FixtureTimeProvider(now.AddMinutes(2));
            var seedHandler = new QueueHandler();
            seedHandler.Enqueue(revoked);
            using (var seed = Service(folder, publicKey, seedHandler, clock))
                Check((await seed.RefreshAsync(true)).Status == DriverCatalogRefreshStatus.Updated,
                    "contention fixture seeded revoked disk state");

            var lockPath = Path.Combine(folder, "driver-catalog.lock");
            using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None);
            var recoveryHandler = new QueueHandler();
            recoveryHandler.Enqueue(revoked);
            var pendingTask = Task.Run(() => Service(folder, publicKey, recoveryHandler, clock,
                shutdownWaitTimeout: TimeSpan.FromMilliseconds(300)));
            using var pending = await pendingTask.WaitAsync(TimeSpan.FromSeconds(2));
            var waiting = pending.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile,
                "1.0", "1.4.0", developerMode: true);
            Check(waiting.State == DriverApprovalState.ApprovalUnavailable && !waiting.MayStart &&
                  !waiting.IsUnvalidated,
                "developer mode cannot bypass unreconciled persistent state during startup contention");

            heldLock.Dispose();
            Check((await pending.RefreshAsync(true)).Status == DriverCatalogRefreshStatus.Updated,
                "refresh recovers after transient startup contention");
            var recovered = pending.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile,
                "1.0", "1.4.0", developerMode: true);
            Check(recovered.State == DriverApprovalState.Revoked && !recovered.MayStart,
                "reconciled revoked disk state remains non-bypassable");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void BundledRollbackCase(ECDsa signingKey, SignedMetadataKey publicKey,
        byte[] oldBundle, DateTimeOffset now)
    {
        var folder = TemporaryDirectory();
        try
        {
            var bundlePath = Path.Combine(folder, "bundled-driver-catalog.json");
            var newBundle = Envelope(signingKey,
                CatalogPayload(2, now.AddMinutes(1), now.AddDays(30), "approved", "new bundle"));
            File.WriteAllBytes(bundlePath, newBundle);
            using (var first = Service(folder, publicKey, new ThrowingHandler(),
                       new FixtureTimeProvider(now.AddMinutes(2)), bundlePath))
                Check(first.CatalogRevision == 2, "bundled catalog persisted before authorization");

            File.Delete(Path.Combine(folder, "driver-catalog.signed.json"));
            File.WriteAllBytes(bundlePath, oldBundle);
            using var rolledBack = Service(folder, publicKey, new ThrowingHandler(),
                new FixtureTimeProvider(now.AddMinutes(2)), bundlePath);
            Check(rolledBack.CatalogRevision == 0 &&
                !rolledBack.Decide(Identity, DriverApprovalService.EmbeddedReaderProfile, "1.0", "1.4.0",
                    developerMode: true).MayStart,
                "persistent watermark blocks an older bundled catalog after application rollback");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static DriverApprovalService Service(string folder, SignedMetadataKey publicKey,
        HttpMessageHandler handler, TimeProvider clock, string? bundledCatalogPath = null,
        TimeSpan? shutdownWaitTimeout = null) =>
        new(folder, new DriverApprovalOptions
        {
            CatalogEndpoint = new Uri("https://catalog.invalid/catalog-current.json"),
            TrustKeys = new[] { publicKey },
            MaximumAttempts = 1,
            BundledCatalogPath = bundledCatalogPath,
            ShutdownWaitTimeout = shutdownWaitTimeout ?? TimeSpan.FromSeconds(2),
        }, new HttpClient(handler), clock, startBackgroundRefresh: false);

    private static async Task ExpectCanceled(Task<DriverCatalogRefreshResult> task, string name)
    {
        try { _ = await task; throw new Exception("FAILED: expected cancellation: " + name); }
        catch (OperationCanceledException) { }
    }

    private static byte[] CatalogPayload(long revision, DateTimeOffset issued, DateTimeOffset expires,
        string decision, string rationale = "fixture decision", string minimumAppVersion = "1.4.0",
        string maximumAppVersionExclusive = "2.0.0") => JsonSerializer.SerializeToUtf8Bytes(new
        {
            payload_type = DriverCatalogPayload.PayloadType,
            schema_version = 1,
            catalog_revision = revision,
            issued_utc = Timestamp(issued),
            expires_utc = Timestamp(expires),
            entries = new[]
            {
                new
                {
                    vendor_id = Identity.VendorId,
                    device_id = Identity.DeviceId,
                    subsystem_id = Identity.SubsystemId,
                    os = Identity.Os,
                    architecture = Identity.Architecture,
                    driver_version = Identity.DriverVersion,
                    reader_profile = DriverApprovalService.EmbeddedReaderProfile,
                    minimum_reader_version = "1.0",
                    maximum_reader_version_exclusive = "2.0",
                    minimum_app_version = minimumAppVersion,
                    maximum_app_version_exclusive = maximumAppVersionExclusive,
                    decision,
                    evidence_sha256 = new string('a', 64),
                    decision_utc = Timestamp(issued),
                    rationale,
                }
            }
        });

    private static byte[] Envelope(ECDsa key, byte[] payload, string keyId = "fixture-p256")
    {
        var digest = SHA256.HashData(payload);
        var signature = key.SignHash(digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            envelope_schema = 1,
            key_id = keyId,
            algorithm = SignedMetadataVerifier.Algorithm,
            payload_sha256 = Convert.ToHexString(digest).ToLowerInvariant(),
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(signature),
        });
    }

    private static byte[] RewriteEnvelope(JsonElement envelope, byte[] payload, string digest) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            envelope_schema = 1,
            key_id = envelope.GetProperty("key_id").GetString(),
            algorithm = envelope.GetProperty("algorithm").GetString(),
            payload_sha256 = digest,
            payload = Convert.ToBase64String(payload),
            signature = envelope.GetProperty("signature").GetString(),
        });

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ConnectorWatch-driver-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }

    private static void ExpectInvalid(Action action, string name)
    {
        try { action(); throw new Exception("FAILED: expected invalid data: " + name); }
        catch (InvalidDataException) { }
    }

    private static void ExpectApprovalFailure(Action action, string name)
    {
        try { action(); throw new Exception("FAILED: expected approval failure: " + name); }
        catch (DriverApprovalException) { }
    }

    private static void ExpectPaused(Func<ElectricalSample> action, DriverApprovalState expected, string name)
    {
        try { _ = action(); throw new Exception("FAILED: expected paused reader: " + name); }
        catch (DriverApprovalPausedException ex) { Check(ex.Decision.State == expected, name); }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<(byte[] Envelope, TaskCompletionSource Release, bool IgnoreCancellation)> _responses = new();
        public int Requests { get; private set; }

        public TaskCompletionSource Enqueue(byte[] envelope, bool blocked = false,
            bool ignoreCancellation = false)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!blocked) release.SetResult();
            lock (_responses) _responses.Enqueue((envelope, release, ignoreCancellation));
            return release;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            (byte[] Envelope, TaskCompletionSource Release, bool IgnoreCancellation) response;
            lock (_responses)
            {
                Requests++;
                if (_responses.Count == 0) throw new HttpRequestException("No fixture response queued.");
                response = _responses.Dequeue();
            }
            if (response.IgnoreCancellation) await response.Release.Task;
            else await response.Release.Task.WaitAsync(cancellationToken);
            var message = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(response.Envelope),
            };
            message.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"fixture-{Requests}\"");
            return message;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new HttpRequestException("offline fixture");
    }

    private sealed class FixtureTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeElectricalSource(Action assertion) : IElectricalSource, IDisposable
    {
        public string Description => "approved fixture source";
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public ElectricalSample ReadElectrical(DateTimeOffset now)
        {
            assertion();
            Reads++;
            return ElectricalSample.FromLegacy(new Voltage(now, 12.0, 100.0, "{}"), Description);
        }
        public Voltage Read(DateTimeOffset now) => ReadElectrical(now).ToLegacyVoltage();
        public void Dispose() => Disposed = true;
    }
}
