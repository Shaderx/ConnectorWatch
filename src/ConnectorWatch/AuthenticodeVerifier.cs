using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ConnectorWatch;

public interface IAuthenticodeVerifier
{
    void Verify(string filePath, IReadOnlySet<string> allowedCertificateSha256,
        IReadOnlySet<string>? selfSignedCertificateSha256 = null);
}

/// <summary>
/// Verifies publicly trusted Authenticode signers through WinVerifyTrust and explicitly pinned
/// self-signed signers through the Windows CMS and SIP implementations. The latter trust is
/// application-scoped and never installs a certificate into a Windows trust store.
/// </summary>
public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    const string SpcIndirectDataOid = "1.3.6.1.4.1.311.2.1.4";
    const uint Encoding = 0x00010001; // X509_ASN_ENCODING | PKCS_7_ASN_ENCODING
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public void Verify(string filePath, IReadOnlySet<string> allowedCertificateSha256,
        IReadOnlySet<string>? selfSignedCertificateSha256 = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        ArgumentNullException.ThrowIfNull(allowedCertificateSha256);
        var publicPins = NormalizePins(allowedCertificateSha256);
        var selfSignedPins = NormalizePins(selfSignedCertificateSha256 is null
            ? Enumerable.Empty<string>()
            : selfSignedCertificateSha256);
        if (publicPins.Count == 0 && selfSignedPins.Count == 0)
            throw new InvalidOperationException("No trusted application publisher certificate is configured.");
        if (publicPins.Overlaps(selfSignedPins))
            throw new InvalidOperationException("A publisher certificate pin cannot be both publicly trusted and application-scoped.");

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The signed application file was not found.", fullPath);

        string embeddedSignerPin;
        try
        {
            using var embeddedSigner = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
            embeddedSignerPin = NormalizeHex(embeddedSigner.GetCertHashString(HashAlgorithmName.SHA256));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            throw new CryptographicException("The file has no readable embedded Authenticode signer.", ex);
        }

        if (publicPins.Contains(embeddedSignerPin))
        {
            VerifyPubliclyTrusted(fullPath, publicPins);
            return;
        }
        if (!selfSignedPins.Contains(embeddedSignerPin))
            throw new CryptographicException("The installer signer is not a pinned ConnectorWatch publisher.");

        VerifyPinnedSelfSigned(fullPath, embeddedSignerPin);
    }

    internal static HashSet<string> NormalizePins(IEnumerable<string> pins) =>
        pins.Select(NormalizeHex).ToHashSet(StringComparer.Ordinal);

    internal static (HashSet<string> Public, HashSet<string> SelfSigned) NormalizeConfiguredPins(
        IEnumerable<string>? publicPins, IEnumerable<string>? selfSignedPins)
    {
        static HashSet<string> AddUnique(IEnumerable<string>? source, string label)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in source ?? Array.Empty<string>())
                if (!result.Add(NormalizeHex(value)))
                    throw new InvalidDataException($"The {label} publisher certificate pin list contains a duplicate.");
            return result;
        }

        var normalizedPublic = AddUnique(publicPins, "public");
        var normalizedSelfSigned = AddUnique(selfSignedPins, "self-signed");
        if (normalizedPublic.Overlaps(normalizedSelfSigned))
            throw new InvalidDataException("A publisher certificate pin cannot appear in both public and self-signed trust policies.");
        if (normalizedPublic.Count == 0 && normalizedSelfSigned.Count == 0)
            throw new InvalidDataException("At least one public or self-signed application publisher certificate pin is required.");
        return (normalizedPublic, normalizedSelfSigned);
    }

    internal static string NormalizeHex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = new string(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (normalized.Length != 64) throw new FormatException("Publisher certificate pins must be SHA-256 hex values.");
        return normalized;
    }

    static void VerifyPubliclyTrusted(string fullPath, IReadOnlySet<string> publicPins)
    {
        using var fileInfo = new WinTrustFileInfo(fullPath);
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trust = new WinTrustData(fileInfoPointer);
            int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, trust);
            if (result != 0) throw new CryptographicException($"Windows rejected the installer Authenticode signature (0x{result:X8}).");
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeCoTaskMem(fileInfoPointer);
        }

        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
        var pin = NormalizeHex(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        if (!publicPins.Contains(pin))
            throw new CryptographicException("The installer signer is valid but is not the pinned ConnectorWatch publisher.");
    }

    static void VerifyPinnedSelfSigned(string fullPath, string expectedSignerPin)
    {
        if (!CryptQueryObject(1, fullPath, 1u << 10, 1u << 1, 0,
                out _, out var contentType, out _, out var certificateStore, out var message, out _))
            throw NativeCryptographicFailure("Windows could not read the embedded Authenticode message");

        IntPtr signerContext = IntPtr.Zero;
        IntPtr indirectData = IntPtr.Zero;
        try
        {
            if (contentType != 10) throw new CryptographicException("The file does not contain an embedded Authenticode message.");
            uint signerCount = GetMessageUInt32(message, 5);
            if (signerCount != 1) throw new CryptographicException("The Authenticode message must contain exactly one primary signer.");

            uint signerIndex = 0;
            if (!CryptMsgGetAndVerifySigner(message, 1, [certificateStore], 0x4, out signerContext, ref signerIndex))
                throw NativeCryptographicFailure("The Authenticode PKCS#7 signature is invalid");
            if (signerIndex != 0) throw new CryptographicException("Windows verified an unexpected Authenticode signer.");

            using var signer = CopyCertificate(signerContext);
            var signerPin = NormalizeHex(signer.GetCertHashString(HashAlgorithmName.SHA256));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedSignerPin), Convert.FromHexString(signerPin)))
                throw new CryptographicException("The verified Authenticode signer does not match the pinned publisher.");
            ValidateSelfSignedCodeSigningCertificate(signer);

            var innerContentType = GetMessageString(message, 4);
            if (!string.Equals(innerContentType, SpcIndirectDataOid, StringComparison.Ordinal))
                throw new CryptographicException("The signed content is not Authenticode indirect data.");
            var content = GetMessageBytes(message, 2);
            uint decodedSize = 0;
            if (!CryptDecodeObjectEx(Encoding, SpcIndirectDataOid, content, (uint)content.Length,
                    0x8000, IntPtr.Zero, out indirectData, ref decodedSize) || indirectData == IntPtr.Zero)
                throw NativeCryptographicFailure("Windows could not decode the Authenticode indirect data");

            VerifyIndirectDataWithSip(fullPath, indirectData);
        }
        finally
        {
            if (indirectData != IntPtr.Zero) _ = LocalFree(indirectData);
            if (signerContext != IntPtr.Zero) _ = CertFreeCertificateContext(signerContext);
            if (message != IntPtr.Zero) _ = CryptMsgClose(message);
            if (certificateStore != IntPtr.Zero) _ = CertCloseStore(certificateStore, 0);
        }
    }

    static void ValidateSelfSignedCodeSigningCertificate(X509Certificate2 certificate)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            throw new CryptographicException("The pinned self-signed publisher certificate is outside its validity period.");
        if (!certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            throw new CryptographicException("The application-scoped publisher certificate is not self-signed.");

        var basicConstraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        if (basicConstraints is null || basicConstraints.CertificateAuthority)
            throw new CryptographicException("The application-scoped publisher certificate must be an end-entity certificate.");
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CodeSigningEku))
            throw new CryptographicException("The application-scoped publisher certificate is not valid for code signing.");

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.LocalDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigningEku));
        if (!chain.Build(certificate) || chain.ChainElements.Count != 1)
            throw new CryptographicException("The application-scoped publisher certificate failed self-signature or code-signing validation.");
    }

    static void VerifyIndirectDataWithSip(string fullPath, IntPtr indirectData)
    {
        if (!CryptSIPRetrieveSubjectGuid(fullPath, IntPtr.Zero, out var subjectGuid))
            throw NativeCryptographicFailure("Windows has no SIP for the signed file");
        var guidPointer = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        var pathPointer = Marshal.StringToHGlobalUni(fullPath);
        try
        {
            Marshal.StructureToPtr(subjectGuid, guidPointer, false);
            var subject = new SipSubjectInfo
            {
                Size = (uint)Marshal.SizeOf<SipSubjectInfo>(),
                SubjectType = guidPointer,
                FileHandle = new IntPtr(-1),
                FileName = pathPointer,
            };
            if (!CryptSIPVerifyIndirectData(ref subject, indirectData))
                throw NativeCryptographicFailure("The Authenticode PE digest does not match the file");
        }
        finally
        {
            Marshal.FreeHGlobal(pathPointer);
            Marshal.FreeHGlobal(guidPointer);
        }
    }

    static X509Certificate2 CopyCertificate(IntPtr certificateContext)
    {
        var native = Marshal.PtrToStructure<CertificateContext>(certificateContext);
        if (native.EncodedCertificate == IntPtr.Zero || native.EncodedCertificateSize == 0)
            throw new CryptographicException("Windows returned an empty Authenticode signer certificate.");
        var bytes = new byte[native.EncodedCertificateSize];
        Marshal.Copy(native.EncodedCertificate, bytes, 0, bytes.Length);
        return new X509Certificate2(bytes);
    }

    static uint GetMessageUInt32(IntPtr message, uint parameter)
    {
        uint size = sizeof(uint);
        var value = new byte[size];
        if (!CryptMsgGetParam(message, parameter, 0, value, ref size) || size != sizeof(uint))
            throw NativeCryptographicFailure("Windows could not read the Authenticode message");
        return BitConverter.ToUInt32(value);
    }

    static byte[] GetMessageBytes(IntPtr message, uint parameter)
    {
        uint size = 0;
        if (!CryptMsgGetParam(message, parameter, 0, null, ref size) || size == 0 || size > 1024 * 1024)
            throw NativeCryptographicFailure("Windows could not size the Authenticode message content");
        var value = new byte[size];
        if (!CryptMsgGetParam(message, parameter, 0, value, ref size))
            throw NativeCryptographicFailure("Windows could not read the Authenticode message content");
        if (size != value.Length) Array.Resize(ref value, checked((int)size));
        return value;
    }

    static string GetMessageString(IntPtr message, uint parameter)
    {
        var bytes = GetMessageBytes(message, parameter);
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, length);
    }

    static CryptographicException NativeCryptographicFailure(string message)
    {
        int error = Marshal.GetLastWin32Error();
        return new CryptographicException($"{message} (0x{error:X8}).", new Win32Exception(error));
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        [In] WinTrustData data);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptQueryObject(uint objectType, string objectPath, uint expectedContentTypeFlags,
        uint expectedFormatTypeFlags, uint flags, out uint encoding, out uint contentType, out uint formatType,
        out IntPtr certificateStore, out IntPtr message, out IntPtr context);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptMsgGetAndVerifySigner(IntPtr message, uint signerStoreCount,
        [In] IntPtr[] signerStores, uint flags, out IntPtr signer, ref uint signerIndex);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptMsgGetParam(IntPtr message, uint parameterType, uint index,
        [Out] byte[]? data, ref uint dataSize);

    [DllImport("crypt32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptDecodeObjectEx(uint encodingType, string structureType,
        [In] byte[] encoded, uint encodedSize, uint flags, IntPtr decodeParameters,
        out IntPtr decoded, ref uint decodedSize);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptSIPRetrieveSubjectGuid(string fileName, IntPtr fileHandle, out Guid subjectGuid);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptSIPVerifyIndirectData(ref SipSubjectInfo subjectInfo, IntPtr indirectData);

    [DllImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptMsgClose(IntPtr message);

    [DllImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CertCloseStore(IntPtr certificateStore, uint flags);

    [DllImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CertFreeCertificateContext(IntPtr certificateContext);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    struct CertificateContext
    {
        public uint EncodingType;
        public IntPtr EncodedCertificate;
        public uint EncodedCertificateSize;
        public IntPtr CertificateInfo;
        public IntPtr CertificateStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CryptDataBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CryptAlgorithmIdentifier
    {
        public IntPtr ObjectId;
        public CryptDataBlob Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SipSubjectInfo
    {
        public uint Size;
        public IntPtr SubjectType;
        public IntPtr FileHandle;
        public IntPtr FileName;
        public IntPtr DisplayName;
        public uint Reserved1;
        public uint InternalVersion;
        public IntPtr CryptographicProvider;
        public CryptAlgorithmIdentifier DigestAlgorithm;
        public uint Flags;
        public uint EncodingType;
        public uint Reserved2;
        public uint CapiSettings;
        public uint SecuritySettings;
        public uint Index;
        public uint UnionChoice;
        public IntPtr AdditionalInfo;
        public IntPtr ClientData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    sealed class WinTrustFileInfo : IDisposable
    {
        readonly uint size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        IntPtr filePath;
        readonly IntPtr fileHandle = IntPtr.Zero;
        readonly IntPtr knownSubject = IntPtr.Zero;
        public WinTrustFileInfo(string path) => filePath = Marshal.StringToCoTaskMemUni(path);
        public void Dispose() { if (filePath != IntPtr.Zero) { Marshal.FreeCoTaskMem(filePath); filePath = IntPtr.Zero; } }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    sealed class WinTrustData
    {
        readonly uint size = (uint)Marshal.SizeOf<WinTrustData>();
        readonly IntPtr policyCallbackData = IntPtr.Zero;
        readonly IntPtr sipClientData = IntPtr.Zero;
        readonly uint uiChoice = 2; // WTD_UI_NONE
        readonly uint revocationChecks = 1; // WTD_REVOKE_WHOLECHAIN
        readonly uint unionChoice = 1; // WTD_CHOICE_FILE
        IntPtr fileInfo;
        readonly uint stateAction = 0;
        readonly IntPtr stateData = IntPtr.Zero;
        readonly IntPtr urlReference = IntPtr.Zero;
        readonly uint providerFlags = 0x00000080; // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
        readonly uint uiContext = 0;
        readonly IntPtr signatureSettings = IntPtr.Zero;
        public WinTrustData(IntPtr fileInfo) => this.fileInfo = fileInfo;
    }
}
