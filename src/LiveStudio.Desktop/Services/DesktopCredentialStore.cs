using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveStudio.Desktop.Services;

public sealed record DesktopCloudCredentials(
    Uri ServiceUri,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid? SelectedOrganizationId = null);

public interface IDesktopCredentialStore
{
    void Save(DesktopCloudCredentials credentials);

    bool TryLoad([NotNullWhen(true)] out DesktopCloudCredentials? credentials);

    void Delete();
}

public sealed class DesktopCredentialStore : IDesktopCredentialStore
{
    private const string CredentialTarget = "LiveStudio/Desktop/Cloud";
    private const string CredentialAccount = "default";
    private const string UpdateCredentialTarget = "LiveStudio/Desktop/GitHubUpdates";
    private const string UpdateCredentialAccount = "github";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Save(DesktopCloudCredentials credentials)
    {
        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials, JsonOptions));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsCredentialApi.Write(CredentialTarget, CredentialAccount, content);
            }
            else if (OperatingSystem.IsMacOS())
            {
                MacKeychainApi.Write(CredentialTarget, CredentialAccount, content);
            }
            else
            {
                throw new PlatformNotSupportedException("桌面云端凭据只支持 Windows 和 macOS");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    public bool TryLoad([NotNullWhen(true)] out DesktopCloudCredentials? credentials)
    {
        byte[]? content;
        var found = OperatingSystem.IsWindows()
            ? WindowsCredentialApi.TryRead(CredentialTarget, out content)
            : OperatingSystem.IsMacOS()
                ? MacKeychainApi.TryRead(CredentialTarget, CredentialAccount, out content)
                : throw new PlatformNotSupportedException("桌面云端凭据只支持 Windows 和 macOS");
        if (!found || content is null)
        {
            credentials = null;
            return false;
        }

        try
        {
            credentials = JsonSerializer.Deserialize<DesktopCloudCredentials>(content, JsonOptions)
                ?? throw new InvalidOperationException("无法解析桌面云端凭据");
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    public void Delete()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsCredentialApi.Delete(CredentialTarget);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacKeychainApi.Delete(CredentialTarget, CredentialAccount);
        }
        else
        {
            throw new PlatformNotSupportedException("桌面云端凭据只支持 Windows 和 macOS");
        }
    }

    public static void SaveUpdateToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var content = Encoding.UTF8.GetBytes(token.Trim());
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsCredentialApi.Write(UpdateCredentialTarget, UpdateCredentialAccount, content);
            }
            else if (OperatingSystem.IsMacOS())
            {
                MacKeychainApi.Write(UpdateCredentialTarget, UpdateCredentialAccount, content);
            }
            else
            {
                throw new PlatformNotSupportedException("更新凭据只支持 Windows 和 macOS");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    public static bool TryLoadUpdateToken([NotNullWhen(true)] out string? token)
    {
        byte[]? content;
        var found = OperatingSystem.IsWindows()
            ? WindowsCredentialApi.TryRead(UpdateCredentialTarget, out content)
            : OperatingSystem.IsMacOS()
                ? MacKeychainApi.TryRead(UpdateCredentialTarget, UpdateCredentialAccount, out content)
                : throw new PlatformNotSupportedException("更新凭据只支持 Windows 和 macOS");
        if (!found || content is null)
        {
            token = null;
            return false;
        }

        try
        {
            token = Encoding.UTF8.GetString(content);
            return !string.IsNullOrWhiteSpace(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    public static void DeleteUpdateToken()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsCredentialApi.Delete(UpdateCredentialTarget);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacKeychainApi.Delete(UpdateCredentialTarget, UpdateCredentialAccount);
        }
        else
        {
            throw new PlatformNotSupportedException("更新凭据只支持 Windows 和 macOS");
        }
    }

    private static class WindowsCredentialApi
    {
        private const int CredentialTypeGeneric = 1;
        private const int CredentialPersistLocalMachine = 2;
        private const int ElementNotFound = 1168;

        public static void Write(string target, string userName, ReadOnlySpan<byte> content)
        {
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

        public static void Delete(string target)
        {
            if (!CredDelete(target, CredentialTypeGeneric, 0)
                && Marshal.GetLastWin32Error() != ElementNotFound)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPointer);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, int type, uint flags);

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

    private static class MacKeychainApi
    {
        private const int Success = 0;
        private const int ItemNotFound = -25300;
        private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        public static void Write(string service, string account, ReadOnlySpan<byte> content)
        {
            var serviceBytes = Encoding.UTF8.GetBytes(service);
            var accountBytes = Encoding.UTF8.GetBytes(account);
            var managedContent = content.ToArray();
            try
            {
                var status = SecKeychainFindGenericPassword(
                    IntPtr.Zero,
                    (uint)serviceBytes.Length,
                    serviceBytes,
                    (uint)accountBytes.Length,
                    accountBytes,
                    out _,
                    out var existingContent,
                    out var item);
                if (status == Success)
                {
                    try
                    {
                        ThrowOnError(SecKeychainItemFreeContent(IntPtr.Zero, existingContent));
                        ThrowOnError(SecKeychainItemModifyAttributesAndData(
                            item,
                            IntPtr.Zero,
                            (uint)managedContent.Length,
                            managedContent));
                    }
                    finally
                    {
                        CFRelease(item);
                    }

                    return;
                }

                if (status != ItemNotFound)
                {
                    ThrowOnError(status);
                }

                ThrowOnError(SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    (uint)serviceBytes.Length,
                    serviceBytes,
                    (uint)accountBytes.Length,
                    accountBytes,
                    (uint)managedContent.Length,
                    managedContent,
                    out var addedItem));
                if (addedItem != IntPtr.Zero)
                {
                    CFRelease(addedItem);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(managedContent);
            }
        }

        public static bool TryRead(
            string service,
            string account,
            [NotNullWhen(true)] out byte[]? content)
        {
            var serviceBytes = Encoding.UTF8.GetBytes(service);
            var accountBytes = Encoding.UTF8.GetBytes(account);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)serviceBytes.Length,
                serviceBytes,
                (uint)accountBytes.Length,
                accountBytes,
                out var contentLength,
                out var contentPointer,
                out var item);
            if (status == ItemNotFound)
            {
                content = null;
                return false;
            }

            ThrowOnError(status);
            try
            {
                content = new byte[contentLength];
                Marshal.Copy(contentPointer, content, 0, content.Length);
                return true;
            }
            finally
            {
                try
                {
                    ThrowOnError(SecKeychainItemFreeContent(IntPtr.Zero, contentPointer));
                }
                finally
                {
                    if (item != IntPtr.Zero)
                    {
                        CFRelease(item);
                    }
                }
            }
        }

        public static void Delete(string service, string account)
        {
            var serviceBytes = Encoding.UTF8.GetBytes(service);
            var accountBytes = Encoding.UTF8.GetBytes(account);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)serviceBytes.Length,
                serviceBytes,
                (uint)accountBytes.Length,
                accountBytes,
                out _,
                out var content,
                out var item);
            if (status == ItemNotFound)
            {
                return;
            }

            ThrowOnError(status);
            try
            {
                ThrowOnError(SecKeychainItemFreeContent(IntPtr.Zero, content));
                ThrowOnError(SecKeychainItemDelete(item));
            }
            finally
            {
                CFRelease(item);
            }
        }

        private static void ThrowOnError(int status)
        {
            if (status != Success)
            {
                throw new InvalidOperationException($"macOS Keychain 操作失败：{status}");
            }
        }

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            uint passwordLength,
            byte[] passwordData,
            out IntPtr itemRef);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainItemModifyAttributesAndData(
            IntPtr itemRef,
            IntPtr attrList,
            uint length,
            byte[] data);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(CoreFoundationFramework)]
        private static extern void CFRelease(IntPtr value);
    }
}
