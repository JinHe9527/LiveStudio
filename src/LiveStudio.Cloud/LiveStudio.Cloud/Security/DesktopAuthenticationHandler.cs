using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LiveStudio.Cloud.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LiveStudio.Cloud.Security;

public sealed class DesktopAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApplicationDbContext dbContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationScheme = "Desktop";
    public const string TokenIdClaim = "desktop_token_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorization[7..].Trim();
        if (token.Length is < 32 or > 256)
        {
            return AuthenticateResult.Fail("桌面访问令牌格式无效");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var accessToken = await dbContext.DesktopAccessTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.TokenHash == hash,
                Context.RequestAborted);
        if (accessToken is null
            || accessToken.RevokedAt is not null
            || accessToken.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return AuthenticateResult.Fail("桌面访问令牌无效或已过期");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, accessToken.UserId),
            new Claim(TokenIdClaim, accessToken.Id.ToString())
        };
        var identity = new ClaimsIdentity(claims, AuthenticationScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            AuthenticationScheme));
    }
}
