using LiveStudio.Core;

namespace LiveStudio.Agent;

internal static class RestoreFinalization
{
    internal static async Task<RestoreExecutionResult> RunAsync(RestoreExecutionResult result,
        Func<CancellationToken, Task> persistResult,
        Func<CancellationToken, Task> publishPreview,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        try
        {
            // 事务已经得出确定结果；用户取消不能让终态记录继续停留在“进行中”。
            await persistResult(CancellationToken.None);
        }
        catch (Exception)
        {
            warnings.Add("恢复结果记录未能保存，请保留本次结果提示");
        }
        if (result.IsSuccess)
        {
            try
            {
                await publishPreview(cancellationToken);
            }
            catch (Exception)
            {
                warnings.Add("画面预览同步未完成，不影响本机参数恢复");
            }
        }
        return warnings.Count == 0 ? result : result with
        {
            Message = $"{result.Message}；{string.Join("；", warnings)}",
            Differences = result.Differences.Concat(warnings).ToArray()
        };
    }
}
