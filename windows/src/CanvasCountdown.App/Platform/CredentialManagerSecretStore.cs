using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// Secrets in Windows Credential Manager, as generic credentials named
/// <c>CanvasCountdown:&lt;account&gt;</c> — the Windows counterpart of the macOS
/// Keychain items with the same account names.
/// </summary>
/// <remarks>
/// They persist on this PC only (not roaming with the profile), which matches
/// the macOS "this device only" choice. Someone removing the app's data can
/// find them in Control Panel → Credential Manager → Windows Credentials.
/// </remarks>
internal sealed class CredentialManagerSecretStore : ISecretStore
{
    private const string TargetPrefix = "CanvasCountdown:";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumBlobBytes = 5 * 512;

    public Task<string?> ReadAsync(string account) => Task.Run(() => Read(account));

    public Task WriteAsync(string account, string secret) => Task.Run(() => Write(account, secret));

    public Task DeleteAsync(string account) => Task.Run(() => Delete(account));

    private static string? Read(string account)
    {
        if (!NativeMethods.CredRead(TargetPrefix + account, CredTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }
            throw Failure("read", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(credentialPointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            NativeMethods.CredFree(credentialPointer);
        }
    }

    private static void Write(string account, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaximumBlobBytes)
        {
            throw new SecretStoreException(
                "That value is too long to keep in Credential Manager.",
                "credman.too-long");
        }

        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = TargetPrefix + account,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = account,
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw Failure("write", Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            // The secret was in this buffer; do not leave it in freed memory.
            for (var offset = 0; offset < bytes.Length; offset++)
            {
                Marshal.WriteByte(blob, offset, 0);
            }
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    private static void Delete(string account)
    {
        if (NativeMethods.CredDelete(TargetPrefix + account, CredTypeGeneric, 0))
        {
            return;
        }
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw Failure("delete", error);
        }
    }

    /// <summary>The message names neither the account nor the value; the code is safe for diagnostics.</summary>
    private static SecretStoreException Failure(string operation, int error)
        => new(
            $"Credential Manager could not {operation} a saved value ({new Win32Exception(error).Message}).",
            $"credman.{operation}.{error}");

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}
