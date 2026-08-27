using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Forms;

namespace LiveStudio.Setup;

internal static class Program
{
    private const string VerifyOnlyArgument = "--verify-only";
    private const string ElevatedArgument = "--elevated";
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LiveStudio",
        "Installer");
    private static readonly string LogPath = Path.Combine(LogDirectory, "install.log");

    [STAThread]
    private static int Main(string[] args)
    {
        var verifyOnly = args.Contains(VerifyOnlyArgument, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("LiveStudio 一键安装器只支持 Windows");
            }

            if (!verifyOnly && !IsAdministrator())
            {
                RelaunchAsAdministrator();
                return 0;
            }

            using var context = new InstallerContext(InstallerPayload.ExtractAndValidate());
            InstallerPayload.ValidateSignerCertificate(Environment.ProcessPath
                ?? throw new InvalidOperationException("无法读取安装器路径"));
            InstallerPayload.ValidateSignerCertificate(context.Payload.PackagePath);

            if (verifyOnly)
            {
                AuthenticodeTrustVerifier.Verify(Environment.ProcessPath!);
                AuthenticodeTrustVerifier.Verify(context.Payload.PackagePath);
                VerifyInstallationPrerequisites();
                return 0;
            }

            EnsurePublishingCertificateTrusted(context.Payload.CertificatePath);
            AuthenticodeTrustVerifier.Verify(Environment.ProcessPath!);
            AuthenticodeTrustVerifier.Verify(context.Payload.PackagePath);
            InstallOrUpgrade(context.Payload.PackagePath, context.Payload.PackageVersion);
            ClearFailureLog();
            MessageBox.Show(
                "LiveStudio 已安装并启动。以后可以直接在软件中检查和安装更新。",
                "LiveStudio 安装完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception exception)
        {
            WriteFailureLog(exception);
            if (!verifyOnly)
            {
                MessageBox.Show(
                    $"安装失败：{exception.Message}\n\n错误记录：{LogPath}",
                    "LiveStudio 安装失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 1;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchAsAdministrator()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法读取安装器路径");
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executablePath)
                ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add(ElevatedArgument);
        try
        {
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动管理员安装进程");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消管理员授权，LiveStudio 未安装", exception);
        }
    }

    private static void EnsurePublishingCertificateTrusted(string certificatePath)
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        InstallerPayload.ValidateCertificateIdentity(certificate, "准备安装的");
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            InstallerPayload.ExpectedCertificateThumbprint,
            validOnly: false);
        if (existing.Count == 0)
        {
            store.Add(certificate);
        }
    }

    private static void InstallOrUpgrade(string packagePath, Version packageVersion)
    {
        const string script = """
& {
    param($PackagePath, $TargetVersion)
    $ErrorActionPreference = 'Stop'
    Import-Module Appx -ErrorAction Stop
    $installed = Get-AppxPackage -Name 'LiveStudio.BroadcastConfiguration' |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if (-not $installed -or [version]$installed.Version -lt [version]$TargetVersion) {
        Add-AppxPackage -Path $PackagePath -ForceApplicationShutdown
        $installed = Get-AppxPackage -Name 'LiveStudio.BroadcastConfiguration' |
            Sort-Object Version -Descending |
            Select-Object -First 1
    }
    if (-not $installed) { throw '安装完成后无法读取 LiveStudio 包身份' }
    if ([version]$installed.Version -lt [version]$TargetVersion) {
        throw "安装版本不正确：$($installed.Version)"
    }
    Start-Process explorer.exe "shell:AppsFolder\$($installed.PackageFamilyName)!LiveStudio"
} $args[0] $args[1]
""";
        RunPowerShell(script, packagePath, packageVersion.ToString());
    }

    private static void VerifyInstallationPrerequisites()
    {
        const string script = """
$ErrorActionPreference = 'Stop'
Import-Module Appx -ErrorAction Stop
if (-not (Get-Command Add-AppxPackage -ErrorAction Stop)) {
    throw '当前 Windows 缺少 Add-AppxPackage'
}
""";
        RunPowerShell(script);
    }

    private static void RunPowerShell(string script, params string[] arguments)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windowsPowerShellRoot = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0");
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(windowsPowerShellRoot, "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var systemModules = Path.Combine(windowsPowerShellRoot, "Modules");
        var programFilesModules = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsPowerShell",
            "Modules");
        var existingModulePath = startInfo.Environment.TryGetValue("PSModulePath", out var value)
            ? value
            : string.Empty;
        startInfo.Environment["PSModulePath"] = string.Join(
            Path.PathSeparator,
            new[] { systemModules, programFilesModules, existingModulePath }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Windows 安装服务");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var details = string.Join(
                Environment.NewLine,
                new[] { standardOutput, standardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                .Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details)
                    ? "Windows 安装服务执行失败"
                    : details);
        }
    }

    private static void WriteFailureLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var content = new StringBuilder()
                .AppendLine(DateTimeOffset.Now.ToString("O"))
                .AppendLine(exception.ToString())
                .ToString();
            File.WriteAllText(LogPath, content, Encoding.UTF8);
        }
        catch
        {
            // 错误提示仍会展示原始异常，日志写入失败不能覆盖安装原因。
        }
    }

    private static void ClearFailureLog()
    {
        try
        {
            if (File.Exists(LogPath))
            {
                File.Delete(LogPath);
            }
        }
        catch
        {
            // 旧错误日志清理失败不能改变已经成功的安装结果。
        }
    }

    private sealed class InstallerContext(ExtractedInstallerPayload payload) : IDisposable
    {
        internal ExtractedInstallerPayload Payload { get; } = payload;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Payload.DirectoryPath))
                {
                    Directory.Delete(Payload.DirectoryPath, true);
                }
            }
            catch
            {
                // 临时文件会由 Windows 后续清理，不影响安装结果。
            }
        }
    }
}
