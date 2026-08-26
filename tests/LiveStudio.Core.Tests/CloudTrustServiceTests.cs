using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LiveStudio.Desktop.Services;

namespace LiveStudio.Core.Tests;

public sealed class CloudTrustServiceTests
{
    [Fact]
    public void BundledRootCertificateMatchesPinnedIdentityAndCaConstraints()
    {
        using var certificate = CloudTrustService.LoadBundledRootCertificate();

        Assert.Equal(
            CloudTrustService.ExpectedRootSha256,
            certificate.GetCertHashString(HashAlgorithmName.SHA256));
        Assert.False(certificate.HasPrivateKey);
        var constraints = Assert.Single(certificate.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.HasPathLengthConstraint);
        Assert.Equal(0, constraints.PathLengthConstraint);
    }

    [Theory]
    [InlineData("https://111.229.162.72:8443")]
    [InlineData("https://111.229.162.72:8443/")]
    public void SupportedServiceUrlAcceptsOnlyPinnedEndpoint(string value)
    {
        Assert.True(CloudTrustService.IsSupportedServiceUrl(value));
    }

    [Theory]
    [InlineData("http://111.229.162.72:8443/")]
    [InlineData("https://111.229.162.72/")]
    [InlineData("https://111.229.162.72:8443/api")]
    [InlineData("https://example.com:8443/")]
    [InlineData("https://user@111.229.162.72:8443/")]
    [InlineData("https://111.229.162.72:8443/?redirect=example")]
    [InlineData("")]
    public void SupportedServiceUrlRejectsOtherEndpoints(string value)
    {
        Assert.False(CloudTrustService.IsSupportedServiceUrl(value));
    }
}
