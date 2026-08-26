using LiveStudio.Cloud.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveStudio.Cloud.IntegrationTests;

internal sealed class CloudApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IntegrationEnvironment _environment = IntegrationEnvironment.Load();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _environment.ConnectionString);
        builder.UseSetting("ObjectStorage:ServiceUrl", _environment.ObjectStorageServiceUrl.ToString());
        builder.UseSetting("ObjectStorage:Region", _environment.ObjectStorageRegion);
        builder.UseSetting("ObjectStorage:Bucket", _environment.ObjectStorageBucket);
        builder.UseSetting("ObjectStorage:AccessKey", _environment.ObjectStorageAccessKey);
        builder.UseSetting("ObjectStorage:SecretKey", _environment.ObjectStorageSecretKey);
        builder.UseSetting("ObjectStorage:UsePathStyle", bool.TrueString);
        builder.UseSetting("ServiceLimits:MaximumManagedDevices", "1");
        builder.UseSetting("ServiceLimits:MaximumLiveRooms", "2");
    }

    public async Task ResetDomainDataAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                "AdapterCatalog",
                "AuditEvents",
                "JobEvents",
                "RemoteJobs",
                "DeviceMappings",
                "SnapshotAssets",
                "Assets",
                "SnapshotComponents",
                "SnapshotUploads",
                "Snapshots",
                "DeviceHeartbeats",
                "DeviceCapabilities",
                "CurrentParameterStates",
                "DesktopAccessTokens",
                "DesktopAuthorizationSessions",
                "DeviceEnrollments",
                "Devices",
                "LiveRooms",
                "OrganizationMembers",
                "Organizations",
                "ObjectDeletions",
                "AspNetUserTokens",
                "AspNetUserPasskeys",
                "AspNetUserLogins",
                "AspNetUserClaims",
                "AspNetUserRoles",
                "AspNetUsers",
                "AspNetRoleClaims",
                "AspNetRoles"
            RESTART IDENTITY CASCADE;
            """);
    }
}
