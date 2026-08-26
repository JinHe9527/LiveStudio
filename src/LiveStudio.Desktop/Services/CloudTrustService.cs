using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LiveStudio.Desktop.Services;

public static class CloudTrustService
{
    public const string DefaultServiceUrl = "https://111.229.162.72:8443/";
    public const string ExpectedRootSha256 = "94C6B25C53C7700ACAB1AD9D7886766A6013086B523EE6916A5314692B593ED4";

    private const string CertificateResourceName =
        "LiveStudio.Desktop.Assets.Cloud.LiveStudio-Cloud-Root-CA.crt";

    public static bool IsSupportedServiceUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate)
            || !Uri.TryCreate(DefaultServiceUrl, UriKind.Absolute, out var supported))
        {
            return false;
        }

        return candidate.Scheme == Uri.UriSchemeHttps
            && string.Equals(candidate.Host, supported.Host, StringComparison.OrdinalIgnoreCase)
            && candidate.Port == supported.Port
            && string.IsNullOrEmpty(candidate.UserInfo)
            && string.IsNullOrEmpty(candidate.Query)
            && string.IsNullOrEmpty(candidate.Fragment)
            && candidate.AbsolutePath is "" or "/";
    }

    public static X509Certificate2 LoadBundledRootCertificate()
    {
        using var stream = typeof(CloudTrustService).Assembly.GetManifestResourceStream(CertificateResourceName)
            ?? throw new InvalidOperationException("软件包缺少 LiveStudio 云端根证书");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var certificate = X509CertificateLoader.LoadCertificate(memory.ToArray());
        ValidateBundledRootCertificate(certificate);
        return certificate;
    }

    public static bool IsBundledRootInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var certificate = LoadBundledRootCertificate();
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Any(installed =>
            CryptographicOperations.FixedTimeEquals(
                installed.GetCertHash(HashAlgorithmName.SHA256),
                certificate.GetCertHash(HashAlgorithmName.SHA256)));
    }

    public static void InstallBundledRoot(string? serviceUrl)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("仅 Windows 支持一键安装云端证书");
        }

        if (!IsSupportedServiceUrl(serviceUrl))
        {
            throw new InvalidOperationException("内置证书只适用于 LiveStudio 固定 IP 云端地址");
        }

        using var certificate = LoadBundledRootCertificate();
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (!store.Certificates.Any(installed =>
                CryptographicOperations.FixedTimeEquals(
                    installed.GetCertHash(HashAlgorithmName.SHA256),
                    certificate.GetCertHash(HashAlgorithmName.SHA256))))
        {
            store.Add(certificate);
        }
    }

    public static void ValidateBundledRootCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var actualSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualSha256),
                Convert.FromHexString(ExpectedRootSha256)))
        {
            throw new CryptographicException("LiveStudio 云端根证书指纹不匹配");
        }

        if (certificate.HasPrivateKey)
        {
            throw new CryptographicException("软件包中的云端根证书不应包含私钥");
        }

        var basicConstraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .SingleOrDefault();
        if (basicConstraints is not { CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0 })
        {
            throw new CryptographicException("LiveStudio 云端根证书的 CA 约束无效");
        }

        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        if (keyUsage is null || !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
        {
            throw new CryptographicException("LiveStudio 云端根证书缺少证书签名用途");
        }

        if (!string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal))
        {
            throw new CryptographicException("LiveStudio 云端根证书不是自签名根证书");
        }

        var now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
        {
            throw new CryptographicException("LiveStudio 云端根证书不在有效期内");
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(certificate))
        {
            throw new CryptographicException("LiveStudio 云端根证书自签名校验失败");
        }
    }
}
