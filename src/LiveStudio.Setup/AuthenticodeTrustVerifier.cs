using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LiveStudio.Setup;

internal static class AuthenticodeTrustVerifier
{
    private const uint WinTrustDataUiNone = 2;
    private const uint WinTrustDataRevokeNone = 0;
    private const uint WinTrustDataChoiceFile = 1;
    private const uint WinTrustDataStateActionIgnore = 0;
    private const uint WinTrustDataRevocationCheckNone = 0x00000010;
    private const uint WinTrustDataUiContextExecute = 0;
    private static readonly Guid WinTrustActionGenericVerifyV2 = new(
        "00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static void Verify(string filePath)
    {
        var resolvedPath = Path.GetFullPath(filePath);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException("找不到需要验证签名的文件", resolvedPath);
        }

        var fileInfo = new WinTrustFileInfo
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = resolvedPath
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
                var reason = new Win32Exception(result).Message;
                throw new InvalidDataException(
                    $"Windows 签名信任校验失败：0x{resultCode:X8} {reason}");
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeCoTaskMem(fileInfoPointer);
        }
    }

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
