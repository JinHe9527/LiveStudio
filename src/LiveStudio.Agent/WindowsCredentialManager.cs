using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LiveStudio.Agent;

internal static class WindowsCredentialManager
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ElementNotFound = 1168;

    public static void Write(string target, string userName, ReadOnlySpan<byte> content)
    {
        EnsureWindows();
        var blob = Marshal.AllocCoTaskMem(content.Length);
        var managedContent = content.ToArray();
        try
        {
            Marshal.Copy(managedContent, 0, blob, content.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)content.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = userName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(managedContent);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static bool TryRead(string target, [NotNullWhen(true)] out byte[]? content)
    {
        EnsureWindows();
        if (!CredRead(target, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ElementNotFound)
            {
                content = null;
                return false;
            }

            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            content = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, content, 0, content.Length);
            return true;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager 只在 Windows 上可用");
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
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
        public string UserName;
    }
}
