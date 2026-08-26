using System.Text.Encodings.Web;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LiveStudio.Core.Tests;

public sealed class DesktopAuthenticationHandlerTests
{
    [Fact]
    public async Task ChallengePreservesIdentityCookieBrowserRedirect()
    {
        await using var dbContext = CreateDbContext();
        var handler = CreateHandler(dbContext);
        var context = new DefaultHttpContext();
        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = "/Account/Login?ReturnUrl=%2F";
        await handler.InitializeAsync(
            new AuthenticationScheme(
                DesktopAuthenticationHandler.AuthenticationScheme,
                null,
                typeof(DesktopAuthenticationHandler)),
            context);

        await handler.ChallengeAsync(null);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/Account/Login?ReturnUrl=%2F", context.Response.Headers.Location);
    }

    [Fact]
    public async Task ChallengeKeepsApiRequestUnauthorizedWithoutRedirect()
    {
        await using var dbContext = CreateDbContext();
        var handler = CreateHandler(dbContext);
        var context = new DefaultHttpContext();
        await handler.InitializeAsync(
            new AuthenticationScheme(
                DesktopAuthenticationHandler.AuthenticationScheme,
                null,
                typeof(DesktopAuthenticationHandler)),
            context);

        await handler.ChallengeAsync(null);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    private static DesktopAuthenticationHandler CreateHandler(ApplicationDbContext dbContext) => new(
        new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
        NullLoggerFactory.Instance,
        UrlEncoder.Default,
        dbContext);

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused")
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
