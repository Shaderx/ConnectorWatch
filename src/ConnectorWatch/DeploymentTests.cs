using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch;

public static class DeploymentTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); passed++; }
        var root = Path.Combine(Path.GetTempPath(), "ConnectorWatch-deployment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var portable = Path.Combine(root, "portable");
            var sourceData = Path.Combine(portable, "data");
            Directory.CreateDirectory(Path.Combine(sourceData, "history"));
            File.WriteAllText(Path.Combine(sourceData, "history", "events.csv"), "one\ntwo\n");
            var sourceConfig = Path.Combine(portable, "config.json");
            File.WriteAllText(sourceConfig, "{\"GpuUuid\":\"GPU-test\",\"DataDirectory\":\"data\"}");
            var installed = Path.Combine(root, "installed-state");
            var destinationConfig = Path.Combine(installed, "config.json");
            var destinationData = Path.Combine(installed, "data");
            Directory.CreateDirectory(destinationData);
            File.WriteAllText(destinationConfig, "{\"GpuUuid\":\"\",\"DataDirectory\":\"data\"}");
            var fakeStop = new RecordingStopper();
            var result = new PortableMigration(fakeStop).ImportAsync(new(sourceConfig, destinationConfig, destinationData))
                .GetAwaiter().GetResult();
            Check(fakeStop.DataDirectories.Contains(sourceData), "portable writer stopped before import");
            Check(File.ReadAllText(Path.Combine(destinationData, "history", "events.csv")) == "one\ntwo\n",
                "portable data imported through staging");
            Check(File.Exists(Path.Combine(result.BackupDirectory, "data", "history", "events.csv")) &&
                File.Exists(Path.Combine(result.BackupDirectory, "config.json")), "portable source backed up");
            Check(File.Exists(sourceConfig) && File.Exists(Path.Combine(sourceData, "history", "events.csv")),
                "portable original retained");
            Check(DeploymentPaths.ResolveDataDirectoryFromConfig(destinationConfig) == destinationData,
                "imported configuration points at external installed data");
            Check(!File.Exists(Path.Combine(destinationData, ".connectorwatch-migration.json")),
                "completed transaction marker removed");

            var recoveryState = Path.Combine(root, "recovery-state");
            var recoveryData = Path.Combine(recoveryState, "data");
            var recoveryConfig = Path.Combine(recoveryState, "config.json");
            Directory.CreateDirectory(Path.Combine(recoveryData, "history"));
            File.Copy(Path.Combine(sourceData, "history", "events.csv"), Path.Combine(recoveryData, "history", "events.csv"));
            var recoveryBackup = Path.Combine(recoveryState, "migration-backups", "fixture");
            Directory.CreateDirectory(recoveryBackup);
            File.WriteAllText(Path.Combine(recoveryData, ".connectorwatch-migration.json"), JsonSerializer.Serialize(new
            {
                SourceConfigSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceConfig))),
                SourceConfig = sourceConfig,
                SourceData = sourceData,
                DestinationConfig = recoveryConfig,
                DestinationData = recoveryData,
                BackupDirectory = recoveryBackup,
                Files = 1,
                Bytes = new FileInfo(Path.Combine(sourceData, "history", "events.csv")).Length,
            }));
            var recovered = new PortableMigration(new RecordingStopper()).ImportAsync(new(sourceConfig, recoveryConfig, recoveryData))
                .GetAwaiter().GetResult();
            Check(File.Exists(recoveryConfig) && !File.Exists(Path.Combine(recoveryData, ".connectorwatch-migration.json")) &&
                recovered.BackupDirectory == recoveryBackup, "interrupted migration publish verified and completed");

            var headless = Path.Combine(root, "headless");
            new NamedPipeDeploymentStopper().RequestStopAsync(headless, TimeSpan.FromMilliseconds(100), CancellationToken.None)
                .GetAwaiter().GetResult();
            DeploymentCommands.RequestGuiShutdownAsync(headless, TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
            Check(File.Exists(Path.Combine(headless, "monitor.lock")), "headless deployment stop accepts an unlocked data directory");

            var application = Path.Combine(root, "application");
            Directory.CreateDirectory(application);
            File.WriteAllText(Path.Combine(application, "ConnectorWatch.exe"), "fixture program");
            var applicationBackup = ApplicationDeploymentBackup.Create(application,
                Path.Combine(root, "application-backups"), application, "1.4.0");
            var applicationManifest = ApplicationDeploymentBackup.Verify(applicationBackup);
            Check(applicationManifest.Files.Length == 1 && File.Exists(Path.Combine(applicationBackup, "ConnectorWatch.exe")),
                "pre-upgrade application backup is hash verified");
            File.WriteAllText(Path.Combine(application, "ConnectorWatch.exe"), "interrupted replacement");
            var restore = ApplicationDeploymentBackup.Restore(applicationBackup);
            Check(File.ReadAllText(Path.Combine(application, "ConnectorWatch.exe")) == "fixture program" &&
                restore.ReplacedDirectory is not null && Directory.Exists(restore.ReplacedDirectory),
                "verified pre-upgrade application can be restored without discarding replaced files");

            var traversal = Path.Combine(root, "traversal");
            Directory.CreateDirectory(traversal);
            var traversalConfig = Path.Combine(traversal, "config.json");
            File.WriteAllText(traversalConfig, JsonSerializer.Serialize(new
            {
                DataDirectory = Path.Combine("..", "portable", "data"),
            }));
            bool rejected = false;
            try
            {
                new PortableMigration(new RecordingStopper()).ImportAsync(new(traversalConfig,
                    Path.Combine(root, "bad", "config.json"), Path.Combine(root, "bad", "data"))).GetAwaiter().GetResult();
            }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "parent traversal rejected before migration");
            Console.WriteLine($"PASS: {passed} deployment and portable migration checks.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    sealed class RecordingStopper : IDeploymentStopper
    {
        public List<string> DataDirectories { get; } = new();
        public Task RequestStopAsync(string dataDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            DataDirectories.Add(dataDirectory);
            return Task.CompletedTask;
        }
    }
}

public static class AppUpdateTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); passed++; }
        var root = Path.Combine(Path.GetTempPath(), "ConnectorWatch-app-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var installer = Encoding.UTF8.GetBytes("fixture installer bytes");
            var now = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo());
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload_type = "app-release",
                schema_version = 1,
                revision = 7,
                issued_utc = "2026-09-09T00:00:00Z",
                expires_utc = "2026-10-09T00:00:00Z",
                version = "1.5.0",
                installer_url = "https://updates.example/ConnectorWatch-Setup.exe",
                installer_sha256 = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(),
                installer_size = installer.Length,
                release_notes = "Authenticated update fixture.",
                minimum_data_schema = 1,
                maximum_data_schema = 4,
                rollback = (object?)null,
            });
            var envelope = SignEnvelope(payload, signingKey);
            var handler = new FixtureHandler(envelope, installer);
            var auth = new FixtureAuthenticode();
            using var http = new HttpClient(handler);
            var service = new AppUpdateService(http,
                new SignedMetadataVerifier(new[] { new SignedMetadataKey("app-2026", publicKey) }),
                new AppUpdateOptions(new Uri("https://updates.example/app-release.json"), root,
                    new HashSet<string> { new string('A', 64) }), auth);
            var check = service.CheckAsync("1.4.0", 3, now).GetAwaiter().GetResult();
            Check(check.Disposition == AppUpdateDisposition.Available && check.Release.ReleaseNotes.Contains("fixture"),
                "signed app metadata exposes version and release notes");
            var incompatible = service.CheckAsync("1.4.0", 9, now).GetAwaiter().GetResult();
            Check(incompatible.Disposition == AppUpdateDisposition.IncompatibleDataSchema,
                "signed release outside the current data schema is not installable");
            var downloaded = service.DownloadInstallerAsync(check).GetAwaiter().GetResult();
            Check(File.ReadAllBytes(downloaded).SequenceEqual(installer) && auth.Verifications == 1,
                "bounded installer is digest and publisher verified");

            File.WriteAllText(downloaded, "tampered");
            bool launchRejected = false;
            try { _ = service.LaunchInstaller(downloaded, check); }
            catch (InvalidDataException) { launchRejected = true; }
            Check(launchRejected, "installer is reverified immediately before launch");

            var forged = new AppUpdateCheckResult(AppUpdateDisposition.Available, check.Release,
                "forged", true, check.AuthenticatedPayloadSha256, check.AuthenticatedRevision, check.AuthenticatedExpiresUtc);
            bool forgedRejected = false;
            try { _ = service.DownloadInstallerAsync(forged).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { forgedRejected = true; }
            Check(forgedRejected, "download is bound to an authenticated check from the same service");

            var tamperedEnvelope = envelope.ToArray();
            tamperedEnvelope[^3] ^= 1;
            using var tamperedHttp = new HttpClient(new FixtureHandler(tamperedEnvelope, installer));
            var tamperedService = new AppUpdateService(tamperedHttp,
                new SignedMetadataVerifier(new[] { new SignedMetadataKey("app-2026", publicKey) }),
                new AppUpdateOptions(new Uri("https://updates.example/app-release.json"), Path.Combine(root, "tampered"),
                    new HashSet<string> { new string('A', 64) }), auth);
            bool metadataRejected = false;
            try { _ = tamperedService.CheckAsync("1.4.0", 3, now).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { metadataRejected = true; }
            Check(metadataRejected, "tampered app metadata rejected before payload use");

            var wrongPublisher = new FixtureAuthenticode { Reject = true };
            using var publisherHttp = new HttpClient(new FixtureHandler(envelope, installer));
            var publisherService = new AppUpdateService(publisherHttp,
                new SignedMetadataVerifier(new[] { new SignedMetadataKey("app-2026", publicKey) }),
                new AppUpdateOptions(new Uri("https://updates.example/app-release.json"), Path.Combine(root, "publisher"),
                    new HashSet<string> { new string('A', 64) }), wrongPublisher);
            var publisherCheck = publisherService.CheckAsync("1.4.0", 3, now).GetAwaiter().GetResult();
            bool publisherRejected = false;
            try { _ = publisherService.DownloadInstallerAsync(publisherCheck).GetAwaiter().GetResult(); }
            catch (CryptographicException) { publisherRejected = true; }
            Check(publisherRejected && !Directory.EnumerateFiles(Path.Combine(root, "publisher"), "*.partial").Any(),
                "wrong-publisher installer rejected and partial download removed");

            using var interruptedHttp = new HttpClient(new FixtureHandler(envelope, installer, interruptInstaller: true));
            var interruptedRoot = Path.Combine(root, "interrupted");
            var interruptedService = new AppUpdateService(interruptedHttp,
                new SignedMetadataVerifier(new[] { new SignedMetadataKey("app-2026", publicKey) }),
                new AppUpdateOptions(new Uri("https://updates.example/app-release.json"), interruptedRoot,
                    new HashSet<string> { new string('A', 64) }), auth);
            var interruptedCheck = interruptedService.CheckAsync("1.4.0", 3, now).GetAwaiter().GetResult();
            bool interruptionRejected = false;
            try { _ = interruptedService.DownloadInstallerAsync(interruptedCheck).GetAwaiter().GetResult(); }
            catch (IOException) { interruptionRejected = true; }
            Check(interruptionRejected && !Directory.EnumerateFiles(interruptedRoot, "*.partial").Any(),
                "interrupted installer download leaves no launchable or partial file");

            var clock = new MutableTimeProvider(now);
            using var expiringHttp = new HttpClient(new FixtureHandler(envelope, installer));
            var expiringService = new AppUpdateService(expiringHttp,
                new SignedMetadataVerifier(new[] { new SignedMetadataKey("app-2026", publicKey) }),
                new AppUpdateOptions(new Uri("https://updates.example/app-release.json"), Path.Combine(root, "expiring"),
                    new HashSet<string> { new string('A', 64) }), auth, clock);
            var expiringCheck = expiringService.CheckAsync("1.4.0", 3, now).GetAwaiter().GetResult();
            clock.UtcNow = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
            bool expiredOfferRejected = false;
            try { _ = expiringService.DownloadInstallerAsync(expiringCheck).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { expiredOfferRejected = true; }
            Check(expiredOfferRejected, "download revalidates authenticated offer expiry");

            var rollbackPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload_type = "app-release", schema_version = 1, revision = 8,
                issued_utc = "2026-09-09T00:00:00Z", expires_utc = "2026-10-09T00:00:00Z",
                version = "1.3.0", installer_url = "https://updates.example/ConnectorWatch-Setup.exe",
                installer_sha256 = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(), installer_size = installer.Length,
                release_notes = "Rollback fixture.", minimum_data_schema = 1, maximum_data_schema = 10,
                rollback = new { allowed = true, minimum_source_version = "1.4.0", maximum_source_version = "1.4.0", minimum_data_schema = 1, maximum_data_schema = 3 },
            });
            var rollbackEnvelope = SignEnvelope(rollbackPayload, signingKey);
            using var rollbackHttp = new HttpClient(new FixtureHandler(rollbackEnvelope, installer));
            var rollbackService = new AppUpdateService(rollbackHttp,
                new SignedMetadataVerifier(new[] { new SignedMetadataKey("app-2026", publicKey) }),
                new AppUpdateOptions(new Uri("https://updates.example/app-release.json"), root,
                    new HashSet<string> { new string('A', 64) }), auth);
            var rollback = rollbackService.CheckAsync("1.4.0", 3, now).GetAwaiter().GetResult();
            Check(rollback.Disposition == AppUpdateDisposition.RollbackNeedsConfirmation,
                "declared schema-safe rollback requires confirmation");
            bool unsafeRollbackRejected = false;
            try { _ = rollbackService.CheckAsync("1.4.0", 5, now).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { unsafeRollbackRejected = true; }
            Check(unsafeRollbackRejected, "signed rollback outside rollback data-schema bounds rejected");

            var appKeyVerifier = new SignedMetadataVerifier(new[] { new SignedMetadataKey("app-2026", publicKey) });
            var missingRollbackBoundPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload_type = "app-release", schema_version = 1, revision = 9,
                issued_utc = "2026-09-09T00:00:00Z", expires_utc = "2026-10-09T00:00:00Z",
                version = "1.3.0", installer_url = "https://updates.example/ConnectorWatch-Setup.exe",
                installer_sha256 = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(), installer_size = installer.Length,
                release_notes = "Invalid rollback fixture.", minimum_data_schema = 1, maximum_data_schema = 4,
                rollback = new { allowed = true, minimum_source_version = (string?)null, maximum_source_version = "1.5.0", minimum_data_schema = 1, maximum_data_schema = 4 },
            });
            bool missingRollbackBoundRejected = false;
            try
            {
                var verified = appKeyVerifier.VerifyEnvelope(SignEnvelope(missingRollbackBoundPayload, signingKey));
                _ = AppReleaseMetadataValidator.ValidateVerifiedRelease(verified);
            }
            catch (InvalidDataException) { missingRollbackBoundRejected = true; }
            Check(missingRollbackBoundRejected, "enabled rollback policy requires both source-version bounds");

            var reversedRollbackPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload_type = "app-release", schema_version = 1, revision = 10,
                issued_utc = "2026-09-09T00:00:00Z", expires_utc = "2026-10-09T00:00:00Z",
                version = "1.3.0", installer_url = "https://updates.example/ConnectorWatch-Setup.exe",
                installer_sha256 = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(), installer_size = installer.Length,
                release_notes = "Invalid rollback fixture.", minimum_data_schema = 1, maximum_data_schema = 4,
                rollback = new { allowed = true, minimum_source_version = "1.6.0", maximum_source_version = "1.4.0", minimum_data_schema = 1, maximum_data_schema = 4 },
            });
            bool reversedRollbackRejected = false;
            try
            {
                var verified = appKeyVerifier.VerifyEnvelope(SignEnvelope(reversedRollbackPayload, signingKey));
                _ = AppReleaseMetadataValidator.ValidateVerifiedRelease(verified);
            }
            catch (InvalidDataException) { reversedRollbackRejected = true; }
            Check(reversedRollbackRejected, "rollback source-version range must be ordered");

            var uncontainedRollbackSchemaPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload_type = "app-release", schema_version = 1, revision = 11,
                issued_utc = "2026-09-09T00:00:00Z", expires_utc = "2026-10-09T00:00:00Z",
                version = "1.3.0", installer_url = "https://updates.example/ConnectorWatch-Setup.exe",
                installer_sha256 = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(), installer_size = installer.Length,
                release_notes = "Invalid rollback fixture.", minimum_data_schema = 2, maximum_data_schema = 4,
                rollback = new { allowed = true, minimum_source_version = "1.4.0", maximum_source_version = "1.6.0", minimum_data_schema = 1, maximum_data_schema = 4 },
            });
            bool uncontainedRollbackSchemaRejected = false;
            try
            {
                var verified = appKeyVerifier.VerifyEnvelope(SignEnvelope(uncontainedRollbackSchemaPayload, signingKey));
                _ = AppReleaseMetadataValidator.ValidateVerifiedRelease(verified);
            }
            catch (InvalidDataException) { uncontainedRollbackSchemaRejected = true; }
            Check(uncontainedRollbackSchemaRejected, "rollback data-schema range must be contained by release support");

            bool oldMetadataRejected = false;
            try { _ = service.CheckAsync("1.4.0", 3, now).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { oldMetadataRejected = true; }
            Check(oldMetadataRejected, "older signed app metadata rejected after a higher revision");

            if (OperatingSystem.IsWindows())
            {
                var signedSystemFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(signedSystemFile));
                var systemPin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                var realVerifier = new WindowsAuthenticodeVerifier();
                realVerifier.Verify(signedSystemFile, new HashSet<string> { systemPin });
                bool realWrongPinRejected = false;
                try { realVerifier.Verify(signedSystemFile, new HashSet<string> { new string('0', 64) }); }
                catch (CryptographicException) { realWrongPinRejected = true; }
                var unsigned = Path.Combine(root, "unsigned.exe");
                File.WriteAllText(unsigned, "not a PE");
                bool unsignedRejected = false;
                try { realVerifier.Verify(unsigned, new HashSet<string> { systemPin }); }
                catch (CryptographicException) { unsignedRejected = true; }
                Check(realWrongPinRejected && unsignedRejected,
                    "real WinVerifyTrust accepts a trusted Windows binary and rejects wrong pin or unsigned input");

                var signTool = FindSignTool();
                if (signTool is null)
                {
                    Console.WriteLine("SKIP: disposable self-signed Authenticode tests require Windows SDK SignTool.");
                }
                else
                {
                var disposablePe = typeof(AppUpdateTests).Assembly.Location;
                using var selfSigner = CreateCodeSigningCertificate("ConnectorWatch disposable self-signed test signer");
                using var wrongSigner = CreateCodeSigningCertificate("ConnectorWatch disposable wrong test signer");
                var selfSignedPe = Path.Combine(root, "self-signed.exe");
                var wrongSignedPe = Path.Combine(root, "wrong-signer.exe");
                File.Copy(disposablePe, selfSignedPe);
                File.Copy(disposablePe, wrongSignedPe);
                SignAuthenticode(signTool, selfSignedPe, selfSigner, root, "self");
                SignAuthenticode(signTool, wrongSignedPe, wrongSigner, root, "wrong");
                var selfPin = selfSigner.GetCertHashString(HashAlgorithmName.SHA256);
                var wrongPin = wrongSigner.GetCertHashString(HashAlgorithmName.SHA256);
                var noPublicPins = new HashSet<string>();
                var selfPins = new HashSet<string> { selfPin };
                realVerifier.Verify(selfSignedPe, noPublicPins, selfPins);
                realVerifier.Verify(selfSignedPe, new HashSet<string> { systemPin }, selfPins);
                realVerifier.Verify(signedSystemFile, new HashSet<string> { systemPin }, selfPins);
                Check(true, "self-signed and public signers coexist during publisher migration");

                bool wrongSelfPinRejected = false;
                try { realVerifier.Verify(selfSignedPe, noPublicPins, new HashSet<string> { wrongPin }); }
                catch (CryptographicException) { wrongSelfPinRejected = true; }
                bool wrongSignerRejected = false;
                try { realVerifier.Verify(wrongSignedPe, noPublicPins, selfPins); }
                catch (CryptographicException) { wrongSignerRejected = true; }
                bool publicSignerCannotUseSelfSignedPolicy = false;
                try { realVerifier.Verify(signedSystemFile, noPublicPins, new HashSet<string> { systemPin }); }
                catch (CryptographicException) { publicSignerCannotUseSelfSignedPolicy = true; }
                Check(wrongSelfPinRejected && wrongSignerRejected && publicSignerCannotUseSelfSignedPolicy,
                    "application-scoped trust rejects wrong pins, wrong signers and non-self-signed certificates");

                var tamperedPe = Path.Combine(root, "tampered-self-signed.exe");
                File.Copy(selfSignedPe, tamperedPe);
                using (var tamper = new FileStream(tamperedPe, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    tamper.Position = 2;
                    int original = tamper.ReadByte();
                    tamper.Position = 2;
                    tamper.WriteByte((byte)(original ^ 0x01));
                }
                bool tamperedPeRejected = false;
                try { realVerifier.Verify(tamperedPe, noPublicPins, selfPins); }
                catch (CryptographicException) { tamperedPeRejected = true; }
                Check(tamperedPeRejected, "self-signed Authenticode verifies the actual PE digest after CMS verification");

                var damagedCmsPe = Path.Combine(root, "damaged-cms-self-signed.exe");
                File.Copy(selfSignedPe, damagedCmsPe);
                DamageEmbeddedCmsSignature(damagedCmsPe);
                string? damagedCmsError = null;
                try { realVerifier.Verify(damagedCmsPe, noPublicPins, selfPins); }
                catch (CryptographicException ex) { damagedCmsError = ex.Message; }
                Check(damagedCmsError?.Contains("PKCS#7 signature is invalid", StringComparison.Ordinal) == true,
                    "self-signed Authenticode rejects a damaged PKCS#7 signature outside the PE digest");

                bool emptyPoliciesRejected = false;
                try { realVerifier.Verify(selfSignedPe, noPublicPins); }
                catch (InvalidOperationException) { emptyPoliciesRejected = true; }
                bool overlappingPoliciesRejected = false;
                try { realVerifier.Verify(selfSignedPe, selfPins, selfPins); }
                catch (InvalidOperationException) { overlappingPoliciesRejected = true; }
                Check(emptyPoliciesRejected && overlappingPoliciesRejected,
                    "runtime trust policy requires a pin and rejects ambiguous public/self-signed policy overlap");

                var cliTrust = Path.Combine(root, "app-update-trust.json");
                File.WriteAllText(cliTrust, JsonSerializer.Serialize(new
                {
                    schema_version = 1,
                    metadata_url = "https://updates.example/app-release.json",
                    metadata_keys = Array.Empty<object>(),
                    publisher_certificate_sha256 = Array.Empty<string>(),
                    self_signed_publisher_certificate_sha256 = new[] { selfPin },
                }));
                var duplicateCliTrust = Path.Combine(root, "duplicate-app-update-trust.json");
                File.WriteAllText(duplicateCliTrust,
                    "{\"schema_version\":1,\"schema_version\":1,\"metadata_url\":\"https://updates.example/app-release.json\",\"metadata_keys\":[],\"publisher_certificate_sha256\":[],\"self_signed_publisher_certificate_sha256\":[\"" + selfPin + "\"]}");
                var unknownCliTrust = Path.Combine(root, "unknown-app-update-trust.json");
                File.WriteAllText(unknownCliTrust,
                    "{\"schema_version\":1,\"metadata_url\":\"https://updates.example/app-release.json\",\"metadata_keys\":[],\"publisher_certificate_sha256\":[],\"self_signed_publisher_certificate_sha256\":[\"" + selfPin + "\"],\"unexpected\":true}");
                var originalOutput = Console.Out;
                using var commandOutput = new StringWriter();
                Console.SetOut(commandOutput);
                int validCliExit;
                int invalidCliExit;
                int duplicateTrustExit;
                int unknownTrustExit;
                try
                {
                    Check(ReleaseSignatureVerificationCommand.TryHandle(
                        ["--verify-release-signature", "--file", selfSignedPe, "--app-trust", cliTrust], out validCliExit),
                        "release signature command dispatches");
                    Check(ReleaseSignatureVerificationCommand.TryHandle(
                        ["--verify-release-signature", "--file", tamperedPe, "--app-trust", cliTrust], out invalidCliExit),
                        "release signature command handles invalid input");
                    Check(ReleaseSignatureVerificationCommand.TryHandle(
                        ["--verify-release-signature", "--file", selfSignedPe, "--app-trust", duplicateCliTrust], out duplicateTrustExit),
                        "release signature command handles duplicate trust properties");
                    Check(ReleaseSignatureVerificationCommand.TryHandle(
                        ["--verify-release-signature", "--file", selfSignedPe, "--app-trust", unknownCliTrust], out unknownTrustExit),
                        "release signature command handles unknown trust properties");
                }
                finally { Console.SetOut(originalOutput); }
                var commandLines = commandOutput.ToString().Split(Environment.NewLine,
                    StringSplitOptions.RemoveEmptyEntries);
                Check(validCliExit == 0 && invalidCliExit != 0 && duplicateTrustExit != 0 && unknownTrustExit != 0 &&
                      commandLines.Length == 4 &&
                      JsonDocument.Parse(commandLines[0]).RootElement.GetProperty("valid").GetBoolean() &&
                      commandLines.Skip(1).All(line => !JsonDocument.Parse(line).RootElement.GetProperty("valid").GetBoolean()),
                    "release signature CLI returns machine-readable validity and process status");
                }
            }
            Console.WriteLine($"PASS: {passed} authenticated app update checks.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static byte[] SignEnvelope(byte[] payload, ECDsa key)
    {
        var digest = SHA256.HashData(payload);
        var signature = key.SignHash(digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            envelope_schema = 1,
            key_id = "app-2026",
            algorithm = SignedMetadataVerifier.Algorithm,
            payload_sha256 = Convert.ToHexString(digest).ToLowerInvariant(),
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(signature),
        });
    }

    static X509Certificate2 CreateCodeSigningCertificate(string commonName)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }

    static string? FindSignTool()
    {
        var configured = Environment.GetEnvironmentVariable("SIGNTOOL_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);
        var bin = @"C:\Program Files (x86)\Windows Kits\10\bin";
        if (!Directory.Exists(bin)) return null;
        return Directory.EnumerateDirectories(bin)
            .Select(directory => new
            {
                Path = Path.Combine(directory, "x64", "signtool.exe"),
                Version = Version.TryParse(Path.GetFileName(directory), out var version) ? version : new Version(),
            })
            .Where(candidate => File.Exists(candidate.Path))
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    static void DamageEmbeddedCmsSignature(string executable)
    {
        using var stream = new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 0x3c;
        int peHeader = reader.ReadInt32();
        stream.Position = peHeader + 24;
        ushort magic = reader.ReadUInt16();
        long securityDirectory = peHeader + 24 + (magic switch
        {
            0x10b => 96,
            0x20b => 112,
            _ => throw new InvalidDataException("Disposable signed test file is not a PE image."),
        }) + 4 * 8;
        stream.Position = securityDirectory;
        uint certificateOffset = reader.ReadUInt32();
        uint certificateTableSize = reader.ReadUInt32();
        if (certificateOffset == 0 || certificateTableSize < 16 || certificateOffset + certificateTableSize > stream.Length)
            throw new InvalidDataException("Disposable signed test file has no valid certificate table.");
        stream.Position = certificateOffset;
        uint certificateLength = reader.ReadUInt32();
        if (certificateLength < 16 || certificateLength > certificateTableSize)
            throw new InvalidDataException("Disposable signed test file has an invalid WIN_CERTIFICATE entry.");
        stream.Position = certificateOffset + 8;
        if (reader.ReadByte() != 0x30) throw new InvalidDataException("Embedded signature is not a DER sequence.");
        byte firstLength = reader.ReadByte();
        int lengthBytes = (firstLength & 0x80) == 0 ? 0 : firstLength & 0x7f;
        if (lengthBytes > 4) throw new InvalidDataException("Embedded signature has an invalid DER length.");
        long contentLength = lengthBytes == 0 ? firstLength : 0;
        for (int index = 0; index < lengthBytes; index++) contentLength = (contentLength << 8) | reader.ReadByte();
        long encodedLength = 2L + lengthBytes + contentLength;
        if (encodedLength <= 8 || encodedLength > certificateLength - 8)
            throw new InvalidDataException("Embedded signature length is outside WIN_CERTIFICATE.");
        stream.Position = certificateOffset + 8 + encodedLength - 1;
        int original = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(original ^ 0x01));
    }

    static void SignAuthenticode(string signTool, string executable, X509Certificate2 signer,
        string directory, string name)
    {
        var pfx = Path.Combine(directory, name + ".pfx");
        var password = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(pfx, signer.Export(X509ContentType.Pfx, password));
        var start = new System.Diagnostics.ProcessStartInfo(signTool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "sign", "/fd", "SHA256", "/f", pfx, "/p", password, executable })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new Exception("Could not start SignTool.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Exception($"SignTool failed ({process.ExitCode}, PFX exists={File.Exists(pfx)}): {output} {error}");
        File.Delete(pfx);
    }

    sealed class FixtureAuthenticode : IAuthenticodeVerifier
    {
        public int Verifications { get; private set; }
        public bool Reject { get; init; }
        public void Verify(string filePath, IReadOnlySet<string> allowedCertificateSha256,
            IReadOnlySet<string>? selfSignedCertificateSha256 = null)
        {
            if (Reject || !File.Exists(filePath) || allowedCertificateSha256.Count != 1) throw new CryptographicException();
            Verifications++;
        }
    }

    sealed class FixtureHandler(byte[] metadata, byte[] installer, bool interruptInstaller = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool isMetadata = request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal);
            var bytes = isMetadata ? metadata : installer;
            HttpContent content = !isMetadata && interruptInstaller
                ? new InterruptingContent(bytes)
                : new ByteArrayContent(bytes);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            if (!(!isMetadata && interruptInstaller)) response.Content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(response);
        }
    }

    sealed class InterruptingContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("fixture interruption");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new InterruptingStream(bytes));
    }

    sealed class InterruptingStream(byte[] bytes) : Stream
    {
        int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position > 0) throw new IOException("fixture interruption");
            int copied = Math.Min(Math.Min(count, 4), bytes.Length);
            Array.Copy(bytes, 0, buffer, offset, copied); position += copied; return copied;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position > 0) return ValueTask.FromException<int>(new IOException("fixture interruption"));
            int copied = Math.Min(Math.Min(buffer.Length, 4), bytes.Length);
            bytes.AsMemory(0, copied).CopyTo(buffer); position += copied; return ValueTask.FromResult(copied);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
