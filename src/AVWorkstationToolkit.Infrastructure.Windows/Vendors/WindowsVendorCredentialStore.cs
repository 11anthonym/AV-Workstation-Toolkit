using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AVWorkstationToolkit.Application.Vendors;

namespace AVWorkstationToolkit.Infrastructure.Windows.Vendors;

public sealed class WindowsVendorCredentialStore : IVendorCredentialStore
{
    private const string CurrentPrefix = "AVWorkstationToolkit:VendorSftp:";
    private const string LegacyPrefix = "AVinite:VendorSftp:";
    private const uint Generic = 1;
    private const uint LocalMachine = 2;
    private const int NotFound = 1168;
    private const int MaximumBlobBytes = 2_560;

    public VendorCredential? Read(VendorSftpIdentity identity)
    {
        Validate(identity);
        return ReadTarget(Target(CurrentPrefix, identity), identity.Username) ?? ReadTarget(Target(LegacyPrefix, identity), identity.Username);
    }

    public void Write(VendorSftpIdentity identity, ReadOnlySpan<char> secret)
    {
        Validate(identity);
        if (secret.IsEmpty) throw new ArgumentException("The credential password cannot be empty.", nameof(secret));
        var bytes = Encoding.Unicode.GetBytes(secret.ToArray());
        if (bytes.Length > MaximumBlobBytes) { CryptographicOperations.ZeroMemory(bytes); throw new ArgumentException("The credential password length is unsupported.", nameof(secret)); }
        var targetPointer = IntPtr.Zero;
        var usernamePointer = IntPtr.Zero;
        var secretPointer = IntPtr.Zero;
        try
        {
            targetPointer = Marshal.StringToCoTaskMemUni(Target(CurrentPrefix, identity));
            usernamePointer = Marshal.StringToCoTaskMemUni(identity.Username);
            secretPointer = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, secretPointer, bytes.Length);
            var credential = new NativeCredential { Type = Generic, TargetName = targetPointer, CredentialBlobSize = checked((uint)bytes.Length), CredentialBlob = secretPointer, Persist = LocalMachine, UserName = usernamePointer };
            if (!CredWriteW(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager rejected the scoped vendor credential.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (secretPointer != IntPtr.Zero) { for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(secretPointer, index, 0); Marshal.FreeCoTaskMem(secretPointer); }
            if (usernamePointer != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUnicode(usernamePointer);
            if (targetPointer != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUnicode(targetPointer);
        }
    }

    public bool Delete(VendorSftpIdentity identity)
    {
        Validate(identity);
        var deleted = DeleteTarget(Target(CurrentPrefix, identity));
        return DeleteTarget(Target(LegacyPrefix, identity)) || deleted;
    }

    internal static string CurrentTarget(VendorSftpIdentity identity) => Target(CurrentPrefix, identity);
    internal static string LegacyTarget(VendorSftpIdentity identity) => Target(LegacyPrefix, identity);

    private static VendorCredential? ReadTarget(string target, string expectedUsername)
    {
        if (!CredReadW(target, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read the scoped vendor credential.");
        }
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.CredentialBlobSize is 0 or > MaximumBlobBytes || native.CredentialBlobSize % 2 != 0)
                throw new InvalidDataException("The stored vendor credential has an invalid secret length.");
            var username = Marshal.PtrToStringUni(native.UserName) ?? string.Empty;
            if (!string.Equals(username, expectedUsername, StringComparison.Ordinal)) throw new InvalidDataException("The stored credential username does not match the catalogued scope.");
            var bytes = new byte[native.CredentialBlobSize];
            Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
            try { return new VendorCredential(username, Encoding.Unicode.GetChars(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static bool DeleteTarget(string target)
    {
        if (CredDeleteW(target, Generic, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == NotFound) return false;
        throw new Win32Exception(error, "Windows Credential Manager could not delete the scoped vendor credential.");
    }

    private static string Target(string prefix, VendorSftpIdentity identity) => $"{prefix}{identity.Endpoint.Host}:{identity.Endpoint.Port}:{identity.Username}";
    private static void Validate(VendorSftpIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (Uri.CheckHostName(identity.Endpoint.Host) != UriHostNameType.Dns || identity.Endpoint.Host.Length > 253 || identity.Endpoint.Host.Any(char.IsControl))
            throw new ArgumentException("The credential host is invalid.");
        if (identity.Endpoint.Port is < 1 or > 65_535) throw new ArgumentException("The credential port is invalid.");
        if (string.IsNullOrWhiteSpace(identity.Username) || identity.Username.Length > 256 || identity.Username.Any(char.IsControl)) throw new ArgumentException("The credential username is invalid.");
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags; public uint Type; public IntPtr TargetName; public IntPtr Comment; public long LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount;
        public IntPtr Attributes; public IntPtr TargetAlias; public IntPtr UserName;
    }
}
