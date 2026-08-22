namespace LiveStudio.Cloud.IntegrationTests;

internal sealed record IntegrationEnvironment(
    string ConnectionString,
    Uri ObjectStorageServiceUrl,
    string ObjectStorageRegion,
    string ObjectStorageBucket,
    string ObjectStorageAccessKey,
    string ObjectStorageSecretKey)
{
    public static IntegrationEnvironment Load()
    {
        return new IntegrationEnvironment(
            Require("LIVESTUDIO_INTEGRATION_CONNECTION"),
            new Uri(Require("LIVESTUDIO_INTEGRATION_S3_URL"), UriKind.Absolute),
            GetOrDefault("LIVESTUDIO_INTEGRATION_S3_REGION", "us-east-1"),
            Require("LIVESTUDIO_INTEGRATION_S3_BUCKET"),
            Require("LIVESTUDIO_INTEGRATION_S3_ACCESS_KEY"),
            Require("LIVESTUDIO_INTEGRATION_S3_SECRET_KEY"));
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"集成测试缺少环境变量 {name}")
            : value;
    }

    private static string GetOrDefault(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
