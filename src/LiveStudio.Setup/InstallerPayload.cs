using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace LiveStudio.Setup;

internal sealed record ExtractedInstallerPayload(
    string DirectoryPath,
    string PackagePath,
    string CertificatePath,
    Version PackageVersion);

internal static class InstallerPayload
{
    internal const string ExpectedPublisher = "CN=LiveStudio Internal";
    internal const string ExpectedCertificateThumbprint = "4D42933F643E1E0B649513BCD10A15B485746E1D";
    private const string PackageResourceName = "LiveStudio.Setup.Payload.LiveStudio-Windows-x64.msix";
    private const string ChecksumResourceName = "LiveStudio.Setup.Payload.LiveStudio-Windows-x64.msix.sha256";
    private const string CertificateResourceName = "LiveStudio.Setup.Payload.LiveStudio-Signing.cer";

    internal static ExtractedInstallerPayload ExtractAndValidate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"LiveStudio-Setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        try
        {
            var packagePath = Path.Combine(directoryPath, "LiveStudio-Windows-x64.msix");
            var certificatePath = Path.Combine(directoryPath, "LiveStudio-Signing.cer");
            ExtractResource(assembly, PackageResourceName, packagePath);
            ExtractResource(assembly, CertificateResourceName, certificatePath);
            var expectedHash = ReadExpectedHash(assembly);
            ValidatePackageHash(packagePath, expectedHash);
            ValidateCertificate(certificatePath);
            var packageVersion = ReadPackageIdentity(packagePath);
            return new ExtractedInstallerPayload(
                directoryPath,
                packagePath,
                certificatePath,
                packageVersion);
        }
        catch
        {
            Directory.Delete(directoryPath, true);
            throw;
        }
    }

    internal static string ParseChecksum(string checksumText)
    {
        var expectedHash = checksumText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (expectedHash is null
            || expectedHash.Length != 64
            || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("安装器内置的 SHA-256 校验文件无效");
        }

        return expectedHash.ToUpperInvariant();
    }

    internal static Version ReadPackageIdentity(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.GetEntry("AppxManifest.xml")
            ?? throw new InvalidDataException("安装包缺少 AppxManifest.xml");
        using var manifestStream = manifestEntry.Open();
        var manifest = XDocument.Load(manifestStream, LoadOptions.None);
        XNamespace packageNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = manifest.Root?.Element(packageNamespace + "Identity")
            ?? throw new InvalidDataException("安装包缺少 Package Identity");
        var publisher = (string?)identity.Attribute("Publisher");
        if (!string.Equals(publisher, ExpectedPublisher, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"安装包 Publisher 不匹配：{publisher}");
        }

        var versionText = (string?)identity.Attribute("Version");
        return Version.TryParse(versionText, out var version)
            ? version
            : throw new InvalidDataException($"安装包版本无效：{versionText}");
    }

    internal static X509Certificate2 ReadSignerCertificate(string signedFilePath)
    {
        try
        {
#pragma warning disable SYSLIB0057
            return new X509Certificate2(X509Certificate.CreateFromSignedFile(signedFilePath));
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("文件没有有效的 Authenticode 签名证书", exception);
        }
    }

    internal static void ValidateSignerCertificate(string signedFilePath)
    {
        using var signer = ReadSignerCertificate(signedFilePath);
        ValidateCertificateIdentity(signer, "签名文件");
    }

    internal static void ValidateCertificateIdentity(X509Certificate2 certificate, string source)
    {
        var thumbprint = NormalizeThumbprint(certificate.Thumbprint);
        if (!string.Equals(
                thumbprint,
                ExpectedCertificateThumbprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{source}证书指纹不匹配：{thumbprint}");
        }

        if (!string.Equals(certificate.Subject, ExpectedPublisher, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{source}发布者不匹配：{certificate.Subject}");
        }
    }

    private static void ExtractResource(Assembly assembly, string resourceName, string outputPath)
    {
        using var source = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("这个安装器不包含完整安装资源，请重新下载");
        using var target = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.WriteThrough);
        source.CopyTo(target);
        target.Flush(true);
    }

    private static string ReadExpectedHash(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ChecksumResourceName)
            ?? throw new InvalidDataException("这个安装器缺少 SHA-256 校验资源，请重新下载");
        using var reader = new StreamReader(stream);
        return ParseChecksum(reader.ReadToEnd());
    }

    private static void ValidatePackageHash(string packagePath, string expectedHash)
    {
        using var package = File.OpenRead(packagePath);
        var actualHash = Convert.ToHexString(SHA256.HashData(package));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("安装器内置的 LiveStudio 安装包 SHA-256 校验失败");
        }
    }

    private static void ValidateCertificate(string certificatePath)
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        ValidateCertificateIdentity(certificate, "内置");
        var now = DateTimeOffset.Now;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
        {
            throw new InvalidDataException("内置发布证书不在有效期内");
        }
    }

    private static string NormalizeThumbprint(string value) => new(
        value.Where(character => !char.IsWhiteSpace(character)).ToArray());
}
