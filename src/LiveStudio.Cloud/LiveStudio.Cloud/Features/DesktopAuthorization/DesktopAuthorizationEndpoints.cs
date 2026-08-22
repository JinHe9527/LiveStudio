using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Security;
using LiveStudio.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Features.DesktopAuthorization;

public static class DesktopAuthorizationEndpoints
{
    private const string UserCodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromDays(90);

    public static IEndpointRouteBuilder MapDesktopAuthorizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/desktop-auth");
        group.MapPost("/sessions", StartAsync).AllowAnonymous();
        group.MapPost("/sessions/poll", PollAsync).AllowAnonymous();
        group.MapGet("/approvals/{userCode}", GetApprovalAsync).RequireAuthorization();
        group.MapPost("/approvals", ApproveAsync).RequireAuthorization();
        group.MapDelete("/tokens/current", RevokeCurrentAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = DesktopAuthenticationHandler.AuthenticationScheme
            });
        return endpoints;
    }

    private static async Task<IResult> StartAsync(
        StartDesktopAuthorizationRequest request,
        HttpRequest httpRequest,
        IConfiguration configuration,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var deviceName = request.DeviceName.Trim();
        if (deviceName.Length is < 1 or > 128)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.DeviceName)] = ["设备名称长度必须为 1 到 128 个字符"]
            });
        }

        await dbContext.DesktopAuthorizationSessions
            .Where(session => session.ExpiresAt < DateTimeOffset.UtcNow.AddDays(-1))
            .ExecuteDeleteAsync(cancellationToken);

        var deviceCode = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var userCode = await CreateUniqueUserCodeAsync(dbContext, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        dbContext.DesktopAuthorizationSessions.Add(new DesktopAuthorizationSessionEntity
        {
            Id = Guid.NewGuid(),
            DeviceName = deviceName,
            UserCode = userCode,
            DeviceCodeHash = Hash(deviceCode),
            CreatedAt = now,
            ExpiresAt = now.Add(SessionLifetime)
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        var publicBaseUrl = configuration["PublicBaseUrl"]?.TrimEnd('/');
        var baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl)
            ? $"{httpRequest.Scheme}://{httpRequest.Host}"
            : publicBaseUrl;
        var verificationUri = new Uri($"{baseUrl}/desktop-authorize?userCode={Uri.EscapeDataString(userCode)}");
        return TypedResults.Ok(new StartDesktopAuthorizationResponse(
            deviceCode,
            FormatUserCode(userCode),
            verificationUri,
            now.Add(SessionLifetime),
            3));
    }

    private static async Task<IResult> PollAsync(
        PollDesktopAuthorizationRequest request,
        ApplicationDbContext dbContext,
        IDataProtectionProvider dataProtectionProvider,
        CancellationToken cancellationToken)
    {
        if (request.DeviceCode.Length is < 32 or > 256)
        {
            return TypedResults.BadRequest();
        }

        var deviceCodeHash = Hash(request.DeviceCode);
        var session = await dbContext.DesktopAuthorizationSessions.SingleOrDefaultAsync(
            value => value.DeviceCodeHash == deviceCodeHash,
            cancellationToken);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return TypedResults.Ok(new PollDesktopAuthorizationResponse(
                DesktopAuthorizationStatus.Expired,
                null,
                null));
        }

        if (session.ApprovedByUserId is null)
        {
            return TypedResults.Ok(new PollDesktopAuthorizationResponse(
                DesktopAuthorizationStatus.Pending,
                null,
                null));
        }

        var protector = dataProtectionProvider.CreateProtector("LiveStudio.DesktopAccessToken.v1");
        if (session.IssuedTokenProtected is not null && session.IssuedTokenExpiresAt is { } issuedTokenExpiresAt)
        {
            return TypedResults.Ok(new PollDesktopAuthorizationResponse(
                DesktopAuthorizationStatus.Approved,
                protector.Unprotect(session.IssuedTokenProtected),
                issuedTokenExpiresAt));
        }

        var accessToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var tokenExpiresAt = now.Add(AccessTokenLifetime);
        dbContext.DesktopAccessTokens.Add(new DesktopAccessTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = session.ApprovedByUserId,
            DeviceName = session.DeviceName,
            TokenHash = Hash(accessToken),
            CreatedAt = now,
            ExpiresAt = tokenExpiresAt
        });
        session.ConsumedAt = now;
        session.IssuedTokenProtected = protector.Protect(accessToken);
        session.IssuedTokenExpiresAt = tokenExpiresAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new PollDesktopAuthorizationResponse(
            DesktopAuthorizationStatus.Approved,
            accessToken,
            tokenExpiresAt));
    }

    private static async Task<IResult> GetApprovalAsync(
        string userCode,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeUserCode(userCode);
        var session = await dbContext.DesktopAuthorizationSessions.AsNoTracking().SingleOrDefaultAsync(
            value => value.UserCode == normalizedCode,
            cancellationToken);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow || session.ConsumedAt is not null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new DesktopAuthorizationApproval(
            session.DeviceName,
            FormatUserCode(session.UserCode),
            session.ExpiresAt));
    }

    private static async Task<IResult> ApproveAsync(
        ApproveDesktopAuthorizationRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.Forbid();
        }

        var normalizedCode = NormalizeUserCode(request.UserCode);
        var session = await dbContext.DesktopAuthorizationSessions.SingleOrDefaultAsync(
            value => value.UserCode == normalizedCode,
            cancellationToken);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow || session.ConsumedAt is not null)
        {
            return TypedResults.NotFound();
        }

        if (session.ApprovedByUserId is not null
            && !string.Equals(session.ApprovedByUserId, userId, StringComparison.Ordinal))
        {
            return TypedResults.Conflict();
        }

        session.ApprovedByUserId = userId;
        session.ApprovedAt ??= DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RevokeCurrentAsync(
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(user.FindFirstValue(DesktopAuthenticationHandler.TokenIdClaim), out var tokenId))
        {
            return TypedResults.Forbid();
        }

        var token = await dbContext.DesktopAccessTokens.SingleOrDefaultAsync(
            value => value.Id == tokenId,
            cancellationToken);
        if (token is null)
        {
            return TypedResults.NotFound();
        }

        token.RevokedAt ??= DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<string> CreateUniqueUserCodeAsync(
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var random = RandomNumberGenerator.GetBytes(8);
            var characters = new char[random.Length];
            for (var index = 0; index < random.Length; index++)
            {
                characters[index] = UserCodeAlphabet[random[index] % UserCodeAlphabet.Length];
            }

            var code = new string(characters);
            if (!await dbContext.DesktopAuthorizationSessions.AnyAsync(
                    session => session.UserCode == code,
                    cancellationToken))
            {
                return code;
            }
        }

        throw new InvalidOperationException("无法生成唯一桌面授权码");
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string NormalizeUserCode(string value) => value
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .ToUpperInvariant();

    private static string FormatUserCode(string value) => $"{value[..4]}-{value[4..]}";
}
