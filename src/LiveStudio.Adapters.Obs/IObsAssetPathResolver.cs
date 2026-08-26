namespace LiveStudio.Adapters.Obs;

public interface IObsAssetPathResolver
{
    string? ResolveMissingPath(string configuredPath);
}
