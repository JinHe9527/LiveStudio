namespace LiveStudio.Adapters.LiveCompanion;

internal static class LiveCompanionConfigurationLocation
{
    // Search only this user's two application data roots, with a bounded depth and no junctions.
    // Every selected location keeps WBStore and storage under the same transaction root.
    internal static string Resolve(string roamingRoot, string localRoot)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Path, int Depth)>();
        foreach (var root in new[] { roamingRoot, localRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(root)) { pending.Enqueue((Path.GetFullPath(root), 0)); }
        }
        var inspected = 0;
        while (pending.TryDequeue(out var current))
        {
            if (++inspected > 256)
            {
                throw new LiveCompanionConfigurationReadException("直播伴侣配置目录候选过多，无法唯一确认；未读取或写入其他账户配置。");
            }
            try
            {
                if ((File.GetAttributes(current.Path) & FileAttributes.ReparsePoint) != 0) { continue; }
                var store = Path.Combine(current.Path, "WBStore");
                if (Directory.Exists(store))
                {
                    if ((File.GetAttributes(store) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new LiveCompanionConfigurationReadException("直播伴侣 WBStore 是目录链接，无法确认真实事务边界。");
                    }
                    candidates.Add(current.Path);
                    // Do not traverse caches/accounts within a recognized application root.
                    continue;
                }
                if (current.Depth >= 3) { continue; }
                foreach (var child in Directory.EnumerateDirectories(current.Path))
                {
                    var name = Path.GetFileName(child);
                    if (name.Equals("cache", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("logs", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("storage", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (inspected + pending.Count >= 256)
                    {
                        throw new LiveCompanionConfigurationReadException("直播伴侣配置目录候选过多，无法完整检查；未选择配置目录。");
                    }
                    pending.Enqueue((child, current.Depth + 1));
                }
            }
            catch (UnauthorizedAccessException)
            {
                throw new LiveCompanionConfigurationReadException($"无权检查直播伴侣配置目录 {current.Path}；请使用运行直播伴侣的同一 Windows 用户。");
            }
        }
        return candidates.Count switch
        {
            0 => Path.GetFullPath(roamingRoot),
            1 => candidates.Single(),
            _ => throw new LiveCompanionConfigurationReadException(
                $"发现多个直播伴侣配置目录，无法确认当前使用哪份：{string.Join("；", candidates.Order(StringComparer.OrdinalIgnoreCase))}。未选择配置、未执行写入，请导出兼容报告核对。")
        };
    }
}
