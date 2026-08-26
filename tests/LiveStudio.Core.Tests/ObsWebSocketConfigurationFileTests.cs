using System.Text.Json.Nodes;
using LiveStudio.Adapters.Obs;

namespace LiveStudio.Core.Tests;

public sealed class ObsWebSocketConfigurationFileTests
{
    [Fact]
    public void EnableAuthenticatedPreservesUnknownFieldsAndCommits()
    {
        var directory = CreateDirectory();
        try
        {
            var path = WriteConfiguration(directory);
            var file = new ObsWebSocketConfigurationFile(path);

            using (var transaction = file.EnableAuthenticated())
            {
                Assert.True(transaction.Configuration.ServerEnabled);
                Assert.True(transaction.Configuration.AuthenticationRequired);
                Assert.Equal(4455, transaction.Configuration.Port);
                Assert.Equal("existing-secret", transaction.Configuration.Password);
                transaction.Commit();
            }

            var root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
            Assert.Equal("preserved", root["plugin_extension"]!.GetValue<string>());
            Assert.True(root["server_enabled"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void EnableAuthenticatedRollsBackWhenNotCommitted()
    {
        var directory = CreateDirectory();
        try
        {
            var path = WriteConfiguration(directory);
            var original = File.ReadAllBytes(path);
            var file = new ObsWebSocketConfigurationFile(path);

            using (file.EnableAuthenticated())
            {
            }

            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateDirectory()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"livestudio-obs-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteConfiguration(string directory)
    {
        var path = System.IO.Path.Combine(directory, "config.json");
        File.WriteAllText(path, """
            {
              "server_enabled": false,
              "auth_required": true,
              "server_password": "existing-secret",
              "server_port": 4455,
              "plugin_extension": "preserved"
            }
            """);
        return path;
    }
}
