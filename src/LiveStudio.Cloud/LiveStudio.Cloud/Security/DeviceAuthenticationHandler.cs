using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LiveStudio.Cloud.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LiveStudio.Cloud.Security;

public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApplicationDbContext dbContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationScheme = "Device";
    public const string DeviceIdClaim = "device_id";
    public const string OrganizationIdClaim = "organization_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Device ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var parts = authorization[7..].Split('.', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var deviceId))
        {
            return AuthenticateResult.Fail("设备凭据格式无效");
        }

        var device = await dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == deviceId, Context.RequestAborted);
        if (device is null || device.RevokedAt is not null)
        {
            return AuthenticateResult.Fail("设备不存在或已被解除");
        }

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(parts[1]));
        if (!CryptographicOperations.FixedTimeEquals(providedHash, device.DeviceKeyHash))
        {
            return AuthenticateResult.Fail("设备凭据无效");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, device.Id.ToString()),
            new Claim(DeviceIdClaim, device.Id.ToString()),
            new Claim(OrganizationIdClaim, device.OrganizationId.ToString())
        };
        var identity = new ClaimsIdentity(claims, AuthenticationScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme));
    }
}
