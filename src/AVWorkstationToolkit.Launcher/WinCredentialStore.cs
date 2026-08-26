using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AVWorkstationToolkit.Launcher;

internal static class WinCredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 2560;

    public static void Write(string target, string username, string password)
    {
        ValidateTarget(target);
        ValidateUsername(username);
        ArgumentNullException.ThrowIfNull(password);
        var secret = Encoding.Unicode.GetBytes(password);
        if (secret.Length == 0 || secret.Length > MaximumCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new ArgumentException("The credential password length is unsupported.");
        }

        var targetPointer = IntPtr.Zero;
        var usernamePointer = IntPtr.Zero;
        var secretPointer = IntPtr.Zero;
        try
        {
            targetPointer = Marshal.StringToCoTaskMemUni(target);
            usernamePointer = Marshal.StringToCoTaskMemUni(username);
            secretPointer = Marshal.AllocCoTaskMem(secret.Length);
            Marshal.Copy(secret, 0, secretPointer, secret.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)secret.Length),
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = usernamePointer
            };
            if (!CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager rejected the credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (secretPointer != IntPtr.Zero)
            {
                for (var offset = 0; offset < secret.Length; offset++)
                {
                    Marshal.WriteByte(secretPointer, offset, 0);
                }
                Marshal.FreeCoTaskMem(secretPointer);
            }
            if (usernamePointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(usernamePointer);
            }
            if (targetPointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(targetPointer);
            }
        }
    }

    public static StoredCredential? Read(string target)
    {
        ValidateTarget(target);
        if (!CredReadW(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }
            throw new Win32Exception(error, "Windows Credential Manager could not read the credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize > MaximumCredentialBlobBytes || credential.CredentialBlobSize % 2 != 0)
            {
                throw new InvalidDataException("The stored credential has an invalid secret length.");
            }
            var bytes = new byte[credential.CredentialBlobSize];
            if (bytes.Length > 0)
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            }
            try
            {
                var username = Marshal.PtrToStringUni(credential.UserName) ?? string.Empty;
                ValidateUsername(username);
                return new StoredCredential(username, Encoding.Unicode.GetString(bytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public static bool Exists(string target)
    {
        ValidateTarget(target);
        if (!CredReadW(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return false;
            }
            throw new Win32Exception(error, "Windows Credential Manager could not inspect the credential.");
        }
        CredFree(credentialPointer);
        return true;
    }

    public static bool Delete(string target)
    {
        ValidateTarget(target);
        if (CredDeleteW(target, CredentialTypeGeneric, 0))
        {
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return false;
        }
        throw new Win32Exception(error, "Windows Credential Manager could not delete the credential.");
    }

    private static void ValidateTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 256 || target.Any(char.IsControl))
        {
            throw new ArgumentException("The credential target is invalid.");
        }
    }

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username.Any(char.IsControl))
        {
            throw new ArgumentException("The credential username is invalid.");
        }
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

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}

internal sealed record StoredCredential(string Username, string Password);
