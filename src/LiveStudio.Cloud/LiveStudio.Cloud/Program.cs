using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LiveStudio.Cloud.Client.Pages;
using LiveStudio.Cloud.Components;
using LiveStudio.Cloud.Components.Account;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Features.Devices;
using LiveStudio.Cloud.Features.DesktopAuthorization;
using LiveStudio.Cloud.Features.Adapters;
using LiveStudio.Cloud.Features.Jobs;
using LiveStudio.Cloud.Features.Organizations;
using LiveStudio.Cloud.Features.Snapshots;
using LiveStudio.Cloud.Infrastructure;
using LiveStudio.Cloud.Realtime;
using LiveStudio.Cloud.Security;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("LiveStudio.Cloud");
if (builder.Configuration["DataProtection:KeyPath"] is { Length: > 0 } keyPath)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}

var authentication = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    });
authentication.AddIdentityCookies();
authentication.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DeviceAuthenticationHandler>(
    DeviceAuthenticationHandler.AuthenticationScheme,
    _ => { });
authentication.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DesktopAuthenticationHandler>(
    DesktopAuthenticationHandler.AuthenticationScheme,
    _ => { });
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
            IdentityConstants.ApplicationScheme,
            DesktopAuthenticationHandler.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"])
    .AddCheck<ObjectStorageHealthCheck>("object-storage", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var partitionKey = context.User.Identity?.Name
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});
builder.Services.AddSignalR();
builder.Services.AddScoped<OrganizationAccessService>();
builder.Services.AddSingleton<DeviceConnectionRegistry>();
builder.Services.AddOptions<ObjectStorageOptions>()
    .Bind(builder.Configuration.GetSection(ObjectStorageOptions.SectionName))
    .Validate(options => options.ServiceUrl is not null, "ObjectStorage:ServiceUrl 不能为空")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Region), "ObjectStorage:Region 不能为空")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Bucket), "ObjectStorage:Bucket 不能为空")
    .Validate(options => !string.IsNullOrWhiteSpace(options.AccessKey), "ObjectStorage:AccessKey 不能为空")
    .Validate(options => !string.IsNullOrWhiteSpace(options.SecretKey), "ObjectStorage:SecretKey 不能为空")
    .ValidateOnStart();
builder.Services.AddHttpClient<IObjectStorage, S3ObjectStorage>();
builder.Services.AddHostedService<SnapshotUploadCleanupWorker>();
builder.Services.AddSingleton<ObjectDeletionWorker>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ObjectDeletionWorker>());

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
}
await AdapterCatalogImporter.ImportAsync(app.Services, builder.Configuration, CancellationToken.None);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/hubs"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(LiveStudio.Cloud.Client._Imports).Assembly);

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();
app.MapOrganizationEndpoints();
app.MapDesktopAuthorizationEndpoints();
app.MapAdapterEndpoints();
app.MapDeviceEndpoints();
app.MapJobEndpoints();
app.MapSnapshotEndpoints();
app.MapHub<AgentHub>("/hubs/agents");
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.Run();

public partial class Program;
