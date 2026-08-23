using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Cloud.Data;
using LiveStudio.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Features.Adapters;

public static class AdapterCatalogImporter
{
    public static async Task ImportAsync(
        IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var directory = configuration["Adapters:CatalogPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "Adapters");
        var catalog = new LiveCompanionAdapterCatalog(directory);
        var definitions = catalog.GetAll();
        if (definitions.Count == 0)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var adapter in definitions)
        {
            var definition = adapter.Definition;
            var entity = await dbContext.AdapterCatalog.SingleOrDefaultAsync(value =>
                value.Application == ApplicationKind.LiveCompanion
                && value.StructureFingerprint == definition.StructureFingerprint,
                cancellationToken);
            if (entity is null)
            {
                entity = new AdapterCatalogEntity
                {
                    Id = CreateId(adapter.DefinitionSha256),
                    Application = ApplicationKind.LiveCompanion,
                    StructureFingerprint = definition.StructureFingerprint,
                    MinimumVersion = definition.MinimumVersion,
                    MaximumVersion = definition.MaximumVersion,
                    DefinitionObjectKey = $"bundled:{definition.Id}",
                    DefinitionSha256 = adapter.DefinitionSha256,
                    Signature = adapter.KeyId,
                    Verified = true,
                    PublishedAt = DateTimeOffset.UtcNow
                };
                dbContext.AdapterCatalog.Add(entity);
            }
            else
            {
                entity.MinimumVersion = definition.MinimumVersion;
                entity.MaximumVersion = definition.MaximumVersion;
                entity.DefinitionObjectKey = $"bundled:{definition.Id}";
                entity.DefinitionSha256 = adapter.DefinitionSha256;
                entity.Signature = adapter.KeyId;
                entity.Verified = true;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Guid CreateId(string hash)
    {
        Span<byte> bytes = stackalloc byte[16];
        Convert.FromHexString(hash).AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
