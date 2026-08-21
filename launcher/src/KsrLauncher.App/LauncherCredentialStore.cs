using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace KsrLauncher.App;

internal sealed record RememberedSession(string Username, string ServerUrl, string RefreshToken);

internal static class LauncherCredentialStore
{
    private const string TargetName = "KSRLauncher/V1/Session";
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static void Save(RememberedSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.ServerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.RefreshToken);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new StoredCredential(session.ServerUrl, session.RefreshToken));
        var blob = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, blob, payload.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = payload.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = session.Username
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(payload);
            Marshal.Copy(payload, 0, blob, payload.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static RememberedSession? Load()
    {
        if (!CredRead(TargetName, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0) return null;
            var payload = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, payload, 0, payload.Length);
                var stored = JsonSerializer.Deserialize<StoredCredential>(payload);
                return stored is null || string.IsNullOrWhiteSpace(credential.UserName)
                    ? null
                    : new RememberedSession(credential.UserName, stored.ServerUrl, stored.RefreshToken);
            }
            finally
            {
                Array.Clear(payload);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Clear()
    {
        if (CredDelete(TargetName, CredentialTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound) throw new Win32Exception(error);
    }

    private sealed record StoredCredential(string ServerUrl, string RefreshToken);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
