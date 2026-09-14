using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveStudio.Desktop.Services;

namespace LiveStudio.Desktop.ViewModels;

public sealed record TutorialStep(string Title, string Description, string First, string Second, string Third, string Note);

public partial class OnboardingViewModel : ObservableObject
{
    private readonly TutorialPreferenceStore preferences;
    public OnboardingViewModel() : this(new TutorialPreferenceStore()) { }
    internal OnboardingViewModel(TutorialPreferenceStore preferences) => this.preferences = preferences;

    public IReadOnlyList<TutorialStep> Steps { get; } =
    [
        new("先认识你的画面存档", "把调好的画面留下来，需要时按存档找回。先花一分钟熟悉操作，也可以随时跳过。",
            "首页左侧选择存档，右侧查看该存档的画面参数。",
            "连接状态和一键连接在首页；更多连接选项放在设置。",
            "按“连接 → 保存 → 导出 → 恢复”的顺序完成第一次使用。",
            "引导只介绍操作，不会自动连接、保存、删除或恢复任何配置。"),
        new("01 · 连接直播软件", "先确认这台电脑上的 OBS 和直播伴侣可以被读取。",
            "打开已安装的直播软件，在首页点击“一键连接”。",
            "查看连接状态；OBS 连接失败时，到设置检查 WebSocket 地址、端口和密码。",
            "直播伴侣可在设置查看“本机版本兼容”，确认是否匹配当前存储结构。",
            "连接成功不代表所有版本和设备都能恢复，恢复前仍会检查兼容性。"),
        new("02 · 保存当前画面", "画面调好后保存一份，给每次调整留下明确的参考。",
            "在开播前确认设备、画面模式、滤镜和美颜参数已经调好。",
            "点击首页保存按钮，等待完成后选择新存档检查参数。",
            "可给存档改一个容易识别的名称，例如“主机 · 暖光 · 近景”。",
            "保存和导出可能短时增加内存与磁盘负载，建议在开播前完成。"),
        new("03 · 导出与手动核对", "导出后得到可迁移的存档，以及方便查找的参数 Excel。",
            "选中需要的存档，点击导出并选择保存位置。",
            "保留 .lscfg 和同名 .xlsx；Excel 可筛选参数并标记手动核对状态。",
            "在其他电脑导入 .lscfg，再确认该电脑的版本、设备映射和依赖。",
            "Excel 修改不会自动写入软件；当前存档没有封装第三方插件程序本体。"),
        new("04 · 恢复与结果检查", "恢复会修改直播软件的配置，请把这一步安排在开播之前。",
            "先选对存档；跨电脑使用时，确认每个来源对应的设备。",
            "点击恢复，按提示处理缺少的设备、权限或不支持的项目。",
            "等待逐字段回读结果；失败或回滚警告必须处理，不能当作恢复成功。",
            "恢复可能关闭并重启 OBS 或直播伴侣。当前不会因正在开播而阻止恢复，请勿直播中操作。")
    ];

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(Progress), nameof(NextLabel))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    public partial int StepIndex { get; set; }
    public TutorialStep Current => Steps[StepIndex];
    public string Progress => $"{StepIndex + 1} / {Steps.Count}";
    public string NextLabel => StepIndex == Steps.Count - 1 ? "开始使用" : "下一步  →";
    public void ShowIfFirstUse() { if (!preferences.HasSeen()) Open(); }
    [RelayCommand] private void Open() { StepIndex = 0; IsOpen = true; }
    [RelayCommand]
    private void SelectStep(string index)
    {
        if (int.TryParse(index, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var selected)
            && selected >= 0 && selected < Steps.Count) StepIndex = selected;
    }
    private bool CanGoBack() => StepIndex > 0;
    [RelayCommand(CanExecute = nameof(CanGoBack))] private void Previous() => StepIndex--;
    [RelayCommand]
    private void Next()
    {
        if (StepIndex < Steps.Count - 1) StepIndex++;
        else Dismiss();
    }
    [RelayCommand]
    private void Dismiss()
    {
        IsOpen = false;
        Status = preferences.Remember() ? "随时可以重新查看使用引导。" : "引导已关闭，但偏好未能保存，下次启动可能再次显示。";
    }
}
