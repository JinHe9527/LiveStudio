using System.Diagnostics;
using System.Windows.Automation;

namespace LiveStudio.Agent;

internal static class ObsUiAutomationConnector
{
    public static async Task<bool> TryEnableServerAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        using var process = Process.GetProcessById(processId);
        if (process.MainWindowHandle == nint.Zero)
        {
            return false;
        }

        var mainWindow = AutomationElement.FromHandle(process.MainWindowHandle);
        var tools = Find(mainWindow, element =>
            element.Current.ControlType == ControlType.MenuItem
            && (element.Current.Name.Contains("工具", StringComparison.OrdinalIgnoreCase)
                || element.Current.Name.Contains("Tools", StringComparison.OrdinalIgnoreCase)));
        if (tools is null || !TryExpand(tools))
        {
            return false;
        }

        await Task.Delay(200, cancellationToken);
        var settingsItem = Find(AutomationElement.RootElement, element =>
            element.Current.ProcessId == processId
            && element.Current.ControlType == ControlType.MenuItem
            && element.Current.Name.Contains("WebSocket", StringComparison.OrdinalIgnoreCase));
        if (settingsItem is null || !TryInvoke(settingsItem))
        {
            return false;
        }

        var dialog = await WaitForAsync(
            processId,
            element => element.Current.ControlType == ControlType.Window
                       && element.Current.Name.Contains("WebSocket", StringComparison.OrdinalIgnoreCase),
            cancellationToken);
        if (dialog is null)
        {
            return false;
        }

        var enabled = Find(dialog, element =>
            element.Current.ControlType == ControlType.CheckBox
            && element.Current.Name.Contains("WebSocket", StringComparison.OrdinalIgnoreCase));
        if (enabled is null || !TryEnable(enabled))
        {
            return false;
        }

        var apply = Find(dialog, element =>
            element.Current.ControlType == ControlType.Button
            && (string.Equals(element.Current.Name, "应用", StringComparison.OrdinalIgnoreCase)
                || string.Equals(element.Current.Name, "Apply", StringComparison.OrdinalIgnoreCase)));
        if (apply is null || !TryInvoke(apply))
        {
            return false;
        }

        await Task.Delay(300, cancellationToken);
        var confirm = Find(dialog, element =>
            element.Current.ControlType == ControlType.Button
            && (string.Equals(element.Current.Name, "确定", StringComparison.OrdinalIgnoreCase)
                || string.Equals(element.Current.Name, "OK", StringComparison.OrdinalIgnoreCase)));
        return confirm is not null && TryInvoke(confirm);
    }

    private static async Task<AutomationElement?> WaitForAsync(
        int processId,
        Func<AutomationElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = Find(AutomationElement.RootElement, element =>
                element.Current.ProcessId == processId && predicate(element));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(100, cancellationToken);
        }

        return null;
    }

    private static AutomationElement? Find(
        AutomationElement root,
        Func<AutomationElement, bool> predicate)
    {
        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            try
            {
                if (predicate(element))
                {
                    return element;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return null;
    }

    private static bool TryExpand(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern))
        {
            return false;
        }

        ((ExpandCollapsePattern)pattern).Expand();
        return true;
    }

    private static bool TryEnable(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern))
        {
            return false;
        }

        var toggle = (TogglePattern)pattern;
        if (toggle.Current.ToggleState != ToggleState.On)
        {
            toggle.Toggle();
        }

        return true;
    }

    private static bool TryInvoke(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            return false;
        }

        ((InvokePattern)pattern).Invoke();
        return true;
    }
}
