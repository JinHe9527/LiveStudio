using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LiveStudio.Desktop.Services;

internal static class ApplicationUpdateSignatureVerifier
{
    private const uint WinTrustDataUiNone = 2;
    private const uint WinTrustDataRevokeNone = 0;
    private const uint WinTrustDataChoiceFile = 1;
    private const uint WinTrustDataStateActionIgnore = 0;
    private const uint WinTrustDataRevocationCheckNone = 0x00000010;
    private const uint WinTrustDataUiContextExecute = 0;
    private static readonly Guid WinTrustActionGenericVerifyV2 = new(
        "00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static void Verify(
        string filePath,
        string expectedPublisher,
        string expectedThumbprint)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("应用更新签名验证只支持 Windows");
        }

        var resolvedPath = Path.GetFullPath(filePath);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException("找不到需要验证签名的更新安装器", resolvedPath);
        }

        VerifyWindowsTrust(resolvedPath);
        using var signer = ReadSignerCertificate(resolvedPath);
        var actualThumbprint = NormalizeThumbprint(signer.Thumbprint);
        if (!string.Equals(
                actualThumbprint,
                NormalizeThumbprint(expectedThumbprint),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"更新安装器证书指纹不匹配：{actualThumbprint}");
        }

        if (!string.Equals(signer.Subject, expectedPublisher, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"更新安装器 Publisher 不匹配：{signer.Subject}");
        }
    }

    private static void VerifyWindowsTrust(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath
        };
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustDataUiNone,
                RevocationChecks = WinTrustDataRevokeNone,
                UnionChoice = WinTrustDataChoiceFile,
                FileInfoPointer = fileInfoPointer,
                StateAction = WinTrustDataStateActionIgnore,
                ProviderFlags = WinTrustDataRevocationCheckNone,
                UiContext = WinTrustDataUiContextExecute
            };
            var result = WinVerifyTrust(
                new IntPtr(-1),
                WinTrustActionGenericVerifyV2,
                ref trustData);
            if (result != 0)
            {
                var resultCode = unchecked((uint)result);
                throw new InvalidDataException(
                    $"Windows 更新签名信任校验失败：0x{resultCode:X8} {new Win32Exception(result).Message}");
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeCoTaskMem(fileInfoPointer);
        }
    }

    private static X509Certificate2 ReadSignerCertificate(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057
            return new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("更新安装器没有有效的 Authenticode 签名证书", exception);
        }
    }

    private static string NormalizeThumbprint(string value) => new(
        value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint StructureSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string FilePath;

        internal IntPtr FileHandle;
        internal IntPtr KnownSubjectPointer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal uint StructureSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr FileInfoPointer;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
    }
}
