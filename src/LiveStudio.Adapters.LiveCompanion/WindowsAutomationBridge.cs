using System.Reflection;
using System.Runtime.InteropServices;

namespace LiveStudio.Adapters.LiveCompanion;

/// <summary>
/// 通过反射加载 Windows 自带的 UI Automation 运行时，避免把仅用于原生文件窗口的
/// WindowsDesktop 引用扩散到适配器的跨平台数据模型。所有操作都直接发给目标窗口，
/// 不依赖鼠标位置、前台窗口或用户桌面上是否有其他窗口遮挡。
/// </summary>
internal static class WindowsAutomationBridge
{
    private static readonly Lazy<AutomationRuntime> Runtime = new(LoadRuntime);

    internal static void ValidateRuntime()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Runtime.Value;
        }
    }

    public static bool SelectFile(nint dialogHandle, string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var runtime = Runtime.Value;
        var root = runtime.FromHandle(dialogHandle);
        if (root is null
            || !runtime.TrySetValue(root, "1148", fullPath)
            || !runtime.TryInvoke(runtime.FromHandle(GetDlgItem(dialogHandle, 1))))
        {
            return false;
        }

        // 文件类型被直播伴侣固定为 config；提交绝对路径后，公共文件窗口会先导航到
        // 所在目录。随后按文件名精确查找并调用该列表项自身的 InvokePattern，不点击
        // “第一个文件”，也不会受排序、缩放、遮挡或鼠标位置影响。
        var fileName = Path.GetFileName(fullPath);
        var displayName = Path.GetFileNameWithoutExtension(fullPath);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsWindow(dialogHandle) || !IsWindowVisible(dialogHandle))
            {
                return true;
            }

            root = runtime.FromHandle(dialogHandle);
            var item = runtime.FindByName(root, fileName)
                       ?? runtime.FindByName(root, displayName);
            if (item is not null && runtime.TryInvoke(item))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static AutomationRuntime LoadRuntime()
    {
        var assembly = TryLoadByName();
        Assembly? typesAssembly = null;
        if (assembly is null)
        {
            var runtimeDirectory = Path.TrimEndingDirectorySeparator(
                RuntimeEnvironment.GetRuntimeDirectory());
            var sharedDirectory = Directory.GetParent(runtimeDirectory)?.Parent?.FullName
                ?? throw new InvalidOperationException("无法定位 .NET 共享运行时目录");
            var desktopRoot = Path.Combine(sharedDirectory, "Microsoft.WindowsDesktop.App");
            var currentVersion = Path.GetFileName(runtimeDirectory);
            var candidates = new List<string>();
            var matchingDirectory = Path.Combine(desktopRoot, currentVersion);
            if (Directory.Exists(matchingDirectory))
            {
                candidates.Add(matchingDirectory);
            }

            if (Directory.Exists(desktopRoot))
            {
                candidates.AddRange(Directory.EnumerateDirectories(desktopRoot)
                    .Where(path => Version.TryParse(Path.GetFileName(path), out var version)
                                   && version.Major == Environment.Version.Major)
                    .OrderByDescending(path => Version.Parse(Path.GetFileName(path))));
            }

            var directory = candidates.FirstOrDefault(path =>
                File.Exists(Path.Combine(path, "UIAutomationClient.dll"))
                && File.Exists(Path.Combine(path, "UIAutomationTypes.dll")))
                ?? throw new InvalidOperationException("当前 Windows 缺少 UI Automation 运行时");
            typesAssembly = Assembly.LoadFrom(Path.Combine(directory, "UIAutomationTypes.dll"));
            assembly = Assembly.LoadFrom(Path.Combine(directory, "UIAutomationClient.dll"));
        }
        else
        {
            try
            {
                typesAssembly = Assembly.Load("UIAutomationTypes");
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException)
            {
                var siblingPath = Path.Combine(
                    Path.GetDirectoryName(assembly.Location) ?? string.Empty,
                    "UIAutomationTypes.dll");
                if (File.Exists(siblingPath))
                {
                    typesAssembly = Assembly.LoadFrom(siblingPath);
                }
            }
        }

        return new AutomationRuntime(
            assembly,
            typesAssembly ?? throw new InvalidOperationException("当前 Windows 缺少 UI Automation 类型运行时"));
    }

    private static Assembly? TryLoadByName()
    {
        try
        {
            return Assembly.Load("UIAutomationClient");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
    }

    private sealed class AutomationRuntime
    {
        private readonly Type automationElementType;
        private readonly Type automationPropertyType;
        private readonly Type propertyConditionType;
        private readonly Type treeScopeType;
        private readonly object descendants;
        private readonly object automationIdProperty;
        private readonly object nameProperty;
        private readonly object invokePatternId;
        private readonly object valuePatternId;
        private readonly MethodInfo findAll;
        private readonly MethodInfo fromHandle;
        private readonly MethodInfo getCurrentPattern;

        public AutomationRuntime(Assembly assembly, Assembly typesAssembly)
        {
            Assembly[] assemblies = [assembly, typesAssembly];
            automationElementType = RequireType(assemblies, "System.Windows.Automation.AutomationElement");
            automationPropertyType = RequireType(assemblies, "System.Windows.Automation.AutomationProperty");
            propertyConditionType = RequireType(assemblies, "System.Windows.Automation.PropertyCondition");
            treeScopeType = RequireType(assemblies, "System.Windows.Automation.TreeScope");
            descendants = Enum.Parse(treeScopeType, "Descendants");
            automationIdProperty = RequireStaticProperty(automationElementType, "AutomationIdProperty");
            nameProperty = RequireStaticProperty(automationElementType, "NameProperty");

            var invokePatternType = RequireType(assemblies, "System.Windows.Automation.InvokePattern");
            var valuePatternType = RequireType(assemblies, "System.Windows.Automation.ValuePattern");
            invokePatternId = RequireStaticProperty(invokePatternType, "Pattern");
            valuePatternId = RequireStaticProperty(valuePatternType, "Pattern");
            fromHandle = automationElementType.GetMethod(
                "FromHandle",
                BindingFlags.Public | BindingFlags.Static,
                [typeof(nint)])
                ?? throw new InvalidOperationException("UI Automation 缺少 FromHandle");
            findAll = automationElementType.GetMethod("FindAll")
                ?? throw new InvalidOperationException("UI Automation 缺少 FindAll");
            getCurrentPattern = automationElementType.GetMethod("GetCurrentPattern")
                ?? throw new InvalidOperationException("UI Automation 缺少 GetCurrentPattern");
        }

        public object? FromHandle(nint handle) =>
            handle == nint.Zero ? null : fromHandle.Invoke(null, [handle]);

        public object? FindByName(object? root, string name)
        {
            if (root is null)
            {
                return null;
            }

            var matches = FindAll(root, nameProperty, name);
            return matches.Count > 0 ? matches[0] : null;
        }

        public bool TrySetValue(object root, string automationId, string value)
        {
            foreach (var element in FindAll(root, automationIdProperty, automationId))
            {
                var pattern = TryGetPattern(element, valuePatternId);
                var setValue = pattern?.GetType().GetMethod("SetValue", [typeof(string)]);
                if (setValue is null)
                {
                    continue;
                }

                setValue.Invoke(pattern, [value]);
                return true;
            }

            return false;
        }

        public bool TryInvoke(object? element)
        {
            if (element is null)
            {
                return false;
            }

            var pattern = TryGetPattern(element, invokePatternId);
            var invoke = pattern?.GetType().GetMethod("Invoke", Type.EmptyTypes);
            if (invoke is null)
            {
                return false;
            }

            invoke.Invoke(pattern, null);
            return true;
        }

        private List<object> FindAll(object root, object property, object value)
        {
            var constructor = propertyConditionType.GetConstructor([automationPropertyType, typeof(object)])
                ?? throw new InvalidOperationException("UI Automation 缺少 PropertyCondition 构造函数");
            var condition = constructor.Invoke([property, value]);
            var collection = findAll.Invoke(root, [descendants, condition])
                ?? throw new InvalidOperationException("UI Automation 返回了空集合");
            var collectionType = collection.GetType();
            var count = (int)(collectionType.GetProperty("Count")?.GetValue(collection) ?? 0);
            var item = collectionType.GetProperty("Item")
                ?? throw new InvalidOperationException("UI Automation 集合缺少索引器");
            var result = new List<object>(count);
            for (var index = 0; index < count; index++)
            {
                if (item.GetValue(collection, [index]) is { } element)
                {
                    result.Add(element);
                }
            }

            return result;
        }

        private object? TryGetPattern(object element, object patternId)
        {
            try
            {
                return getCurrentPattern.Invoke(element, [patternId]);
            }
            catch (TargetInvocationException exception) when (
                exception.InnerException is InvalidOperationException)
            {
                return null;
            }
        }

        private static Type RequireType(IEnumerable<Assembly> assemblies, string name) =>
            assemblies.Select(candidate => candidate.GetType(name, throwOnError: false))
                .FirstOrDefault(candidate => candidate is not null)
            ?? throw new InvalidOperationException($"UI Automation 缺少类型 {name}");

        private static object RequireStaticProperty(Type type, string name) =>
            type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException($"UI Automation 缺少静态属性 {type.Name}.{name}");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetDlgItem(nint dialogHandle, int itemId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);
}
