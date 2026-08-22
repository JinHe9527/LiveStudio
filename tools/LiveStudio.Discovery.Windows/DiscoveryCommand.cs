using System.Text.Json;

namespace LiveStudio.Discovery.Windows;

public static class DiscoveryCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "capture" => await CaptureAsync(args[1..], output, cancellationToken),
                "diff" => await DiffAsync(args[1..], output, cancellationToken),
                _ => throw new DiscoveryCommandException($"未知命令: {args[0]}")
            };
        }
        catch (DiscoveryCommandException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync(Usage);
            return 2;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task<int> CaptureAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args);
        var name = GetSingle(options, "--name");
        var destination = GetSingle(options, "--output");
        var roots = GetMany(options, "--root");
        var registryKeys = GetMany(options, "--registry");
        var processNames = GetMany(options, "--process");
        if (roots.Count == 0 && registryKeys.Count == 0)
        {
            throw new DiscoveryCommandException("capture 至少需要一个 --root 或 --registry");
        }

        var report = await DiscoveryCollector.CaptureAsync(
            name,
            roots,
            registryKeys,
            processNames.Count == 0 ? ["obs64", "直播伴侣", "livecompanion"] : processNames,
            cancellationToken);
        await WriteJsonAsync(destination, report, cancellationToken);
        await output.WriteLineAsync($"已生成探测报告: {Path.GetFullPath(destination)}");
        return 0;
    }

    private static async Task<int> DiffAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args);
        var beforePath = GetSingle(options, "--before");
        var afterPath = GetSingle(options, "--after");
        var destination = GetSingle(options, "--output");
        var before = await ReadJsonAsync<DiscoveryReport>(beforePath, cancellationToken);
        var after = await ReadJsonAsync<DiscoveryReport>(afterPath, cancellationToken);
        var difference = DiscoveryReportComparer.Compare(before, after);
        await WriteJsonAsync(destination, difference, cancellationToken);
        await output.WriteLineAsync($"已生成差异报告: {Path.GetFullPath(destination)}");
        return 0;
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new DiscoveryCommandException($"参数格式错误: {args[index]}");
            }

            if (!options.TryGetValue(args[index], out var values))
            {
                values = [];
                options.Add(args[index], values);
            }

            values.Add(args[index + 1]);
        }

        return options;
    }

    private static string GetSingle(Dictionary<string, List<string>> options, string name)
    {
        if (!options.TryGetValue(name, out var values) || values.Count != 1)
        {
            throw new DiscoveryCommandException($"必须且只能提供一次 {name}");
        }

        return values[0];
    }

    private static List<string> GetMany(
        Dictionary<string, List<string>> options,
        string name) => options.TryGetValue(name, out var values) ? values : [];

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new DiscoveryCommandException($"无法解析报告: {path}");
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new DiscoveryCommandException("无法确定输出目录"));
        await using var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            65_536,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private const string Usage = """
        直播伴侣配置探测工具

        capture --name <实验名> --output <report.json>
                --root <目录> [--root <目录> ...]
                [--registry <HKCU\\路径> ...]
                [--process <进程名> ...]

        diff --before <before.json> --after <after.json> --output <diff.json>

        报告只保存文件哈希、Registry 值哈希和进程元数据，不保存配置内容或凭据。
        """;

    private sealed class DiscoveryCommandException(string message) : Exception(message);
}
