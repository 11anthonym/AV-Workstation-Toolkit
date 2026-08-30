using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace AVWorkstationToolkit.Infrastructure.Windows.Authenticode;

public sealed record AuthenticodeTrustResult(bool Valid, string SignerSubject, string Detail);
public sealed record AuthenticodeSignatureResult(bool Valid, string SignerSubject, string Detail);

public interface IAuthenticodeTrustVerifier
{
    AuthenticodeTrustResult Verify(string path);
}

public interface IAuthenticodeSignatureInspector
{
    AuthenticodeSignatureResult Inspect(string path);
}

public sealed partial class WinTrustAuthenticodeVerifier : IAuthenticodeTrustVerifier, IAuthenticodeSignatureInspector
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [GeneratedRegex(@"(^|,\s*)O=Microsoft Corporation(,|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MicrosoftPublisherPattern();

    public AuthenticodeTrustResult Verify(string path)
    {
        var result = Inspect(path);
        if (!result.Valid) return new(false, result.SignerSubject, result.Detail);
        return MicrosoftPublisherPattern().IsMatch(result.SignerSubject)
            ? new(true, result.SignerSubject, "Authenticode signature and Microsoft publisher identity are valid.")
            : new(false, result.SignerSubject, "Authenticode signer is not Microsoft Corporation.");
    }

    public AuthenticodeSignatureResult Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00001000,
                UiContext = 0
            };
            var action = GenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            if (status != 0) return new(false, string.Empty, $"WinVerifyTrust rejected the candidate (0x{status:X8}).");
            // The BCL has no X509CertificateLoader API for extracting the signer from a PE file.
            // WinVerifyTrust above is the validation authority; this legacy API only reads the
            // already-validated signer's public certificate for publisher-identity comparison.
#pragma warning disable SYSLIB0057
            using var legacy = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(legacy.GetRawCertData());
            var subject = certificate.Subject;
            return new(true, subject, "Authenticode signature is valid.");
        }
        catch (CryptographicException exception)
        {
            return new(false, string.Empty, $"Authenticode certificate could not be read: {exception.Message}");
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
