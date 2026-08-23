using System.Security.Cryptography;

namespace LiveStudio.Adapters.LiveCompanion;

public sealed class LiveCompanionAdapterCatalog
{
    private readonly string catalogDirectory;
    private readonly Lazy<IReadOnlyList<VerifiedAdapterDefinition>> adapters;

    public LiveCompanionAdapterCatalog()
        : this(ResolveDefaultDirectory())
    {
    }

    public LiveCompanionAdapterCatalog(string catalogDirectory)
    {
        this.catalogDirectory = Path.GetFullPath(catalogDirectory);
        adapters = new Lazy<IReadOnlyList<VerifiedAdapterDefinition>>(Load, true);
    }

    public AdapterMatchResult Match(string applicationVersion, string structureFingerprint) =>
        CompatibilityMatcher.Match(applicationVersion, structureFingerprint, adapters.Value);

    public IReadOnlyList<VerifiedAdapterDefinition> GetAll() => adapters.Value;

    private List<VerifiedAdapterDefinition> Load()
    {
        if (!Directory.Exists(catalogDirectory))
        {
            return [];
        }

        var trustedKeyDirectory = Path.Combine(catalogDirectory, "trusted-keys");
        var result = new List<VerifiedAdapterDefinition>();
        foreach (var definitionPath in Directory.EnumerateFiles(
                     catalogDirectory,
                     "*.adapter.json",
                     SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            var signaturePath = definitionPath[..^".adapter.json".Length] + ".signature.json";
            if (!File.Exists(signaturePath))
            {
                throw new AdapterDefinitionException($"适配定义缺少签名: {Path.GetFileName(definitionPath)}");
            }

            result.Add(AdapterDefinitionVerifier.Verify(
                File.ReadAllBytes(definitionPath),
                File.ReadAllBytes(signaturePath),
                keyId => LoadTrustedKey(trustedKeyDirectory, keyId)));
        }

        return result;
    }

    private static ECDsa? LoadTrustedKey(string directory, string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId)
            || keyId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || keyId.Contains(Path.DirectorySeparatorChar)
            || keyId.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        var path = Path.Combine(directory, $"{keyId}.pem");
        if (!File.Exists(path))
        {
            return null;
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static string ResolveDefaultDirectory()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Adapters");
        return Directory.Exists(bundled)
            ? bundled
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveStudio",
                "Adapters");
    }
}
