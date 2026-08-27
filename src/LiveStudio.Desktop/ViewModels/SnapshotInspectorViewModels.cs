using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveStudio.Contracts;

namespace LiveStudio.Desktop.ViewModels;

public sealed partial class SnapshotInspectorViewModel : ObservableObject
{
    private SnapshotApplicationViewModel? lastSelectedApplication;

    public SnapshotInspectorViewModel(
        string name,
        DateTimeOffset createdAt,
        IReadOnlyList<ApplicationSnapshot> applications,
        string verification = "",
        string verificationDetail = "",
        IReadOnlyList<CameraCreativeLookOptionViewModel>? creativeLooks = null,
        IReadOnlyList<CameraStationSnapshot>? cameraStations = null)
    {
        Name = name;
        CreatedAt = createdAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        Applications = applications.Select(application => new SnapshotApplicationViewModel(application)).ToArray();
        SelectedApplication = Applications.Count > 0 ? Applications[0] : null;
        Summary = string.Join(" · ", Applications.Select(application => application.HeadlineSummary));
        Verification = verification;
        VerificationDetail = verificationDetail;
        DeclaredFieldCount = Applications.Sum(application => application.DeclaredFieldCount);
        ReadFieldCount = Applications.Sum(application => application.ReadFieldCount);
        RestorableFieldCount = Applications.Sum(application => application.RestorableFieldCount);
        VerifiedFieldCount = Applications.Sum(application => application.VerifiedFieldCount);
        GapCount = Applications.Sum(application => application.GapCount);
        CoverageStatus = Applications.Count > 0 && Applications.All(application => application.IsRestorable)
            ? "存档完整 · 可执行事务恢复"
            : GapCount == 0 && DeclaredFieldCount > 0 && VerifiedFieldCount == DeclaredFieldCount
            ? "全部字段已通过真机验收"
            : GapCount == 0
                ? "字段已映射，仍需完成真机验收"
                : "存档内容已读取 · 当前不能完整还原";
        CoverageDetail = $"已保存 {ReadFieldCount} 项 · 可恢复 {RestorableFieldCount} 项";
        HasRecoveryWarning = Applications.Any(application => !application.IsRestorable);
        RecoveryWarning = HasRecoveryWarning
            ? "此存档存在无法逐字段恢复的内容；打开技术信息查看具体原因。"
            : string.Empty;
        var lookOptions = creativeLooks ??
        [
            new("ST", "标准"), new("PT", "人像"), new("NT", "中性"),
            new("VV", "鲜艳"), new("VV2", "鲜艳 2"), new("FL", "胶片"),
            new("IN", "即时"), new("SH", "柔和高调"), new("BW", "黑白"), new("SE", "棕褐色")
        ];
        CameraStations =
        [
            new CameraStationEditorViewModel(0, "主机", lookOptions),
            new CameraStationEditorViewModel(1, "游机", lookOptions),
            new CameraStationEditorViewModel(2, "侧机", lookOptions)
        ];
        foreach (var station in cameraStations ?? [])
        {
            if (station.Slot is >= 0 and <= 2)
            {
                CameraStations[station.Slot].Apply(station);
            }
        }
    }

    public string Name { get; }

    public string CreatedAt { get; }

    public string Summary { get; }

    public IReadOnlyList<SnapshotApplicationViewModel> Applications { get; }

    public IReadOnlyList<CameraStationEditorViewModel> CameraStations { get; }

    [ObservableProperty]
    public partial SnapshotApplicationViewModel? SelectedApplication { get; set; }

    partial void OnSelectedApplicationChanged(SnapshotApplicationViewModel? value)
    {
        if (value is not null)
        {
            lastSelectedApplication = value;
            IsCameraSelected = false;
        }

        OnPropertyChanged(nameof(TechnicalApplication));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplicationSelected))]
    public partial bool IsCameraSelected { get; set; }

    partial void OnIsCameraSelectedChanged(bool value)
    {
        if (value)
        {
            SelectedApplication = null;
        }
    }

    public bool IsApplicationSelected => !IsCameraSelected;

    public SnapshotApplicationViewModel? TechnicalApplication =>
        SelectedApplication ?? lastSelectedApplication ?? (Applications.Count > 0 ? Applications[0] : null);

    [ObservableProperty]
    public partial string CameraSaveMessage { get; set; } = "修改后点击顶部“保存当前画面”，OBS、直播伴侣和三机位会一起生成新存档";

    [ObservableProperty]
    public partial bool IsSavingCameraStations { get; set; }

    [RelayCommand]
    private void SelectCamera() => IsCameraSelected = true;

    public string Verification { get; }

    public string VerificationDetail { get; }

    public bool HasVerification => !string.IsNullOrWhiteSpace(Verification);

    public int DeclaredFieldCount { get; }

    public int ReadFieldCount { get; }

    public int VerifiedFieldCount { get; }

    public int RestorableFieldCount { get; }

    public int GapCount { get; }

    public bool HasCoverageGaps => GapCount > 0;

    public string CoverageStatus { get; }

    public string CoverageDetail { get; }

    public bool HasRecoveryWarning { get; }

    public string RecoveryWarning { get; }

    [ObservableProperty]
    public partial bool IsTechnicalPanelOpen { get; set; }

    [RelayCommand]
    private void ToggleTechnicalPanel() => IsTechnicalPanelOpen = !IsTechnicalPanelOpen;

    [RelayCommand]
    private void CloseTechnicalPanel() => IsTechnicalPanelOpen = false;
}

public sealed partial class SnapshotApplicationViewModel : ObservableObject
{
    private const int SourcePageSize = 8;
    private const int CoveragePageSize = 100;
    private int sourcePageIndex;
    private int coveragePageIndex;
    private SnapshotCoverageFieldViewModel[] filteredCoverageFields = [];

    public SnapshotApplicationViewModel(ApplicationSnapshot application)
    {
        IsObs = application.Kind == ApplicationKind.Obs;
        IsLiveCompanion = application.Kind == ApplicationKind.LiveCompanion;
        Name = IsObs ? "OBS Studio" : "抖音直播伴侣";
        ShortName = IsObs ? "OBS" : "直播伴侣";
        Version = $"版本 {application.Version}";
        AdapterStatus = IsLiveCompanion && application.Compatibility != CompatibilityLevel.Verified
            ? "已保存 · 当前不能还原"
            : application.Compatibility switch
        {
            CompatibilityLevel.Verified => "已保存 · 可一键还原",
            CompatibilityLevel.Experimental => "已保存 · 还原功能待验证",
            _ => "已保存 · 当前不能还原"
        };
        AdapterId = application.AdapterId;
        AdapterDefinition = ShortHash(application.AdapterDefinitionSha256);
        StructureFingerprint = ShortHash(application.StructureFingerprint);
        NativeDocumentCount = application.NativeDocuments.Count;
        Sources = IsObs
            ? application.Sources.Select(source => new SnapshotSourceViewModel(source)).ToArray()
            : [];
        SelectedSource = Sources.Count > 0 ? Sources[0] : null;
        NativeDocuments = IsLiveCompanion
            ? application.NativeDocuments.Select(document => new SnapshotNativeDocumentViewModel(document)).ToArray()
            : [];
        SelectedNativeDocument = NativeDocuments.Count > 0 ? NativeDocuments[0] : null;
        CoverageFields = CreateCoverageFields(application);
        DeclaredFieldCount = CoverageFields.Count;
        ReadFieldCount = CoverageFields.Count(field => field.HasCapturedValue);
        MappedFieldCount = CoverageFields.Count(field => field.Status == "已映射");
        VerifiedFieldCount = CoverageFields.Count(field => field.Status == "已验证");
        EvidenceOnlyFieldCount = CoverageFields.Count(field => field.Status == "仅有证据");
        UnknownFieldCount = CoverageFields.Count(field => field.Status is "未知" or "缺失");
        GapCount = CoverageFields.Count(field => field.IsGap);
        IsRestorable = application.Compatibility == CompatibilityLevel.Verified
                       && GapCount == 0
                       && application.ConfigurationTree is
                       {
                           HasCompleteUiInventory: true,
                           HasCompleteNativeInventory: true,
                           UnknownCount: 0,
                           EvidenceOnlyCount: 0
                       };
        RestorableFieldCount = IsRestorable ? ReadFieldCount : 0;
        CoverageStatus = DeclaredFieldCount == 0
            ? IsObs ? "当前 OBS 没有视频采集来源" : "当前没有读取到目标字段"
            : GapCount == 0
              && application.ConfigurationTree is
              {
                  HasCompleteUiInventory: true,
                  HasCompleteNativeInventory: true,
                  UnknownCount: 0,
                  EvidenceOnlyCount: 0
              }
              && VerifiedFieldCount == DeclaredFieldCount
            ? "全部字段已通过真机验收"
            : IsRestorable
            ? $"{MappedFieldCount + VerifiedFieldCount} 项已映射 · 可逐字段回读恢复"
            : GapCount == 0
            ? $"已映射 {MappedFieldCount + VerifiedFieldCount} 项，尚未完成真机验收"
            : ReadFieldCount == 0
                ? $"{GapCount} 项缺失或未声明"
                : $"已读取 {ReadFieldCount} 项，{GapCount} 项尚未证明可恢复";
        CoverageCount = DeclaredFieldCount == 0
            ? "没有目标字段"
            : $"Unknown {UnknownFieldCount} · EvidenceOnly {EvidenceOnlyFieldCount} · Mapped {MappedFieldCount} · Verified {VerifiedFieldCount}";
        var filterCount = Sources.Sum(source => source.Filters.Count);
        HeadlineSummary = IsObs
            ? $"OBS {Sources.Count} 个视频来源、{filterCount} 个滤镜"
            : $"直播伴侣 {NativeDocumentCount} 份原生文档、{ReadFieldCount} 个字段";
        Summary = IsObs
            ? $"{Sources.Count} 个视频来源 · {filterCount} 个滤镜 · 已读取 {ReadFieldCount} 项字段"
            : $"{NativeDocumentCount} 类配置 · {NativeDocuments.Sum(document => document.EffectiveItems.Count)} 个有内容的配置项 · 完整保存 {ReadFieldCount} 个字段";
        ReadingNotice = IsObs
            ? "下面按 OBS 的真实来源、inputSettings 原字段和视频滤镜链展示。技术名称不做猜测翻译。"
            : application.Compatibility == CompatibilityLevel.Unsupported
                ? $"当前版本 {application.Version} 的配置可以保存和检查；尚未完成界面控件与原生字段的一一验证，所以暂不能安全写回直播伴侣。"
                : "下面严格按直播伴侣原生菜单、分组、字段名称和顺序展示。";
        ParameterGroups = CreateParameterGroups(application, CoverageFields);
        FeatureGroups = ParameterGroups.Skip(1).ToArray();
        NativeMenuGroups = FeatureGroups
            .Where(group => !group.IsUnmapped && HasMenuContent(group))
            .ToArray();
        SelectedParameterGroup = IsLiveCompanion
            ? NativeMenuGroups.FirstOrDefault(group => group.Name == "美颜设置")
              ?? (NativeMenuGroups.Count > 0 ? NativeMenuGroups[0] : null)
              ?? ParameterGroups.FirstOrDefault(group => group.IsUnmapped)
              ?? ParameterGroups[0]
            : ParameterGroups[0];
        RefreshSourcePage();
        RefreshCoveragePage();
    }

    public string Name { get; }

    public string ShortName { get; }

    public bool IsObs { get; }

    public bool IsLiveCompanion { get; }

    public string Version { get; }

    public string AdapterStatus { get; }

    public string AdapterId { get; }

    public string AdapterDefinition { get; }

    public string StructureFingerprint { get; }

    public string Summary { get; }

    public string HeadlineSummary { get; }

    public string ReadingNotice { get; }

    public int NativeDocumentCount { get; }

    public IReadOnlyList<SnapshotSourceViewModel> Sources { get; }

    [ObservableProperty]
    public partial SnapshotSourceViewModel? SelectedSource { get; set; }

    public IReadOnlyList<SnapshotNativeDocumentViewModel> NativeDocuments { get; }

    public bool HasNativeDocuments => NativeDocuments.Count > 0;

    [ObservableProperty]
    public partial SnapshotNativeDocumentViewModel? SelectedNativeDocument { get; set; }

    public bool HasSelectedNativeDocument => SelectedNativeDocument is not null;

    public bool ShowMappedMenuFields => IsLiveCompanion
                                        && SelectedParameterGroup is { IsUnmapped: false }
                                        && SelectedNativeDocument is null;

    public bool HasVisibleCoverageFields => VisibleCoverageFields.Count > 0;

    public string SelectedMenuTitle => SelectedParameterGroup?.Name ?? "直播伴侣参数";

    public string SelectedMenuFieldSummary => VisibleCoverageFields.Count == 0
        ? "当前没有已修改或生效的设置"
        : $"{VisibleCoverageFields.Count} 项已修改或生效设置 · 未修改原始值不在此列表";

    [ObservableProperty]
    public partial IReadOnlyList<SnapshotSourceViewModel> VisibleSources { get; private set; } = [];

    [ObservableProperty]
    public partial string SourcePageSummary { get; private set; } = string.Empty;

    public IReadOnlyList<SnapshotCoverageFieldViewModel> CoverageFields { get; }

    public IReadOnlyList<SnapshotParameterGroupViewModel> ParameterGroups { get; }

    public IReadOnlyList<SnapshotParameterGroupViewModel> FeatureGroups { get; }

    public IReadOnlyList<SnapshotParameterGroupViewModel> NativeMenuGroups { get; }

    [ObservableProperty]
    public partial SnapshotParameterGroupViewModel? SelectedParameterGroup { get; set; }

    [ObservableProperty]
    public partial string FieldSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearchVisible { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SnapshotCoverageFieldViewModel> VisibleCoverageFields { get; private set; } = [];

    [ObservableProperty]
    public partial string CoveragePageSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CoverageSearchSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<SnapshotFieldGroupViewModel> VisibleCoverageGroups { get; private set; } = [];

    public int DeclaredFieldCount { get; }

    public int ReadFieldCount { get; }

    public int VerifiedFieldCount { get; }

    public int RestorableFieldCount { get; }

    public bool IsRestorable { get; }

    public int MappedFieldCount { get; }

    public int EvidenceOnlyFieldCount { get; }

    public int UnknownFieldCount { get; }

    public int GapCount { get; }

    public string UnmappedNativeSummary => $"已读取但尚未确认归属 {EvidenceOnlyFieldCount + UnknownFieldCount} 项";

    public bool HasCoverageFields => CoverageFields.Count > 0;

    public bool HasCoverageGaps => GapCount > 0;

    public bool HasVerifiedCoverage => DeclaredFieldCount > 0
                                       && VerifiedFieldCount == DeclaredFieldCount
                                       && GapCount == 0;

    public string CoverageStatus { get; }

    public string CoverageCount { get; }

    public bool HasMultipleSourcePages => Sources.Count > SourcePageSize;

    public bool HasMultipleCoveragePages => !IsLiveCompanion && filteredCoverageFields.Length > CoveragePageSize;

    private bool HasMenuContent(SnapshotParameterGroupViewModel group)
    {
        if (group.Name == "美颜设置"
            && NativeDocuments.FirstOrDefault(document => document.FileName == "effectConfigStore.json") is
                { HasEffectiveItems: true })
        {
            return true;
        }

        var groupFields = CoverageFields.Where(field => field.GroupKey == group.Key);
        var rows = SnapshotCoverageFieldViewModel.CreateCompactMenuRows(groupFields);
        return group.Name == "特效道具"
            ? rows.Any(row => IsVisibleEffectContent(row.TechnicalPath))
            : rows.Count > 0;
    }

    private static bool IsVisibleEffectContent(string path) =>
        path.Contains("effectConfigStore", StringComparison.Ordinal)
        && (path.Contains("/default/", StringComparison.Ordinal)
            || path.Contains("/interact/", StringComparison.Ordinal));

    partial void OnSelectedParameterGroupChanged(SnapshotParameterGroupViewModel? value)
    {
        coveragePageIndex = 0;
        OnPropertyChanged(nameof(IsSelectedParameterGroupUnmapped));
        OnPropertyChanged(nameof(IsSelectedParameterGroupMappedMenu));
        OnPropertyChanged(nameof(ShowMappedMenuFields));
        OnPropertyChanged(nameof(SelectedMenuTitle));
        RefreshCoveragePage();
        if (!IsLiveCompanion || value is null || value.IsUnmapped)
        {
            return;
        }

        if (value.Name == "美颜设置")
        {
            var beautyDocument = NativeDocuments.FirstOrDefault(document => document.FileName == "effectConfigStore.json");
            if (beautyDocument is not null)
            {
                SelectedNativeDocument = beautyDocument;
            }
        }
        else
        {
            SelectedNativeDocument = null;
        }
    }

    partial void OnSelectedNativeDocumentChanged(SnapshotNativeDocumentViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedNativeDocument));
        OnPropertyChanged(nameof(ShowMappedMenuFields));
    }

    public bool IsSelectedParameterGroupUnmapped => SelectedParameterGroup?.IsUnmapped == true;

    public bool IsSelectedParameterGroupMappedMenu => SelectedParameterGroup is { IsUnmapped: false };

    partial void OnFieldSearchTextChanged(string value)
    {
        coveragePageIndex = 0;
        RefreshCoveragePage();
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchVisible = !IsSearchVisible;
        if (!IsSearchVisible)
        {
            FieldSearchText = string.Empty;
            if (SelectedNativeDocument is not null)
            {
                SelectedNativeDocument.ItemSearchText = string.Empty;
            }
        }
    }

    public void ShowSearch() => IsSearchVisible = true;

    [RelayCommand(CanExecute = nameof(CanGoToPreviousSourcePage))]
    private void PreviousSourcePage()
    {
        sourcePageIndex--;
        RefreshSourcePage();
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextSourcePage))]
    private void NextSourcePage()
    {
        sourcePageIndex++;
        RefreshSourcePage();
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousCoveragePage))]
    private void PreviousCoveragePage()
    {
        coveragePageIndex--;
        RefreshCoveragePage();
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextCoveragePage))]
    private void NextCoveragePage()
    {
        coveragePageIndex++;
        RefreshCoveragePage();
    }

    private bool CanGoToPreviousSourcePage() => sourcePageIndex > 0;

    private bool CanGoToNextSourcePage() => (sourcePageIndex + 1) * SourcePageSize < Sources.Count;

    private bool CanGoToPreviousCoveragePage() => coveragePageIndex > 0;

    private bool CanGoToNextCoveragePage() =>
        (coveragePageIndex + 1) * CoveragePageSize < filteredCoverageFields.Length;

    private void RefreshSourcePage()
    {
        var offset = sourcePageIndex * SourcePageSize;
        VisibleSources = Sources.Skip(offset).Take(SourcePageSize).ToArray();
        SourcePageSummary = FormatPageSummary(offset, VisibleSources.Count, Sources.Count);
        PreviousSourcePageCommand.NotifyCanExecuteChanged();
        NextSourcePageCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCoveragePage()
    {
        var groupKey = SelectedParameterGroup?.Key ?? SnapshotParameterGroupViewModel.AllKey;
        var search = FieldSearchText.Trim();
        var groupFields = CoverageFields
            .Where(field => groupKey == SnapshotParameterGroupViewModel.AllKey || field.GroupKey == groupKey)
            .ToArray();
        var displayFields = IsLiveCompanion && SelectedParameterGroup?.IsUnmapped != true
            ? SnapshotCoverageFieldViewModel.CreateCompactMenuRows(groupFields)
            : groupFields.Where(field => !IsLiveCompanion || field.ShowByDefault).ToArray();
        filteredCoverageFields = displayFields
            .Where(field => string.IsNullOrWhiteSpace(search) || field.Matches(search))
            .ToArray();
        var offset = coveragePageIndex * CoveragePageSize;
        VisibleCoverageFields = IsLiveCompanion
            ? filteredCoverageFields
            : filteredCoverageFields.Skip(offset).Take(CoveragePageSize).ToArray();
        VisibleCoverageGroups = VisibleCoverageFields
            .GroupBy(field => field.PresentationGroup, StringComparer.CurrentCulture)
            .Select(group => new SnapshotFieldGroupViewModel(group.Key, group.ToArray()))
            .ToArray();
        CoveragePageSummary = IsLiveCompanion
            ? $"共 {VisibleCoverageFields.Count} 项 · 连续滚动查看"
            : FormatPageSummary(offset, VisibleCoverageFields.Count, filteredCoverageFields.Length);
        CoverageSearchSummary = IsLiveCompanion
            ? $"当前只列 {filteredCoverageFields.Length} 项已修改或生效设置"
            : $"当前显示 {filteredCoverageFields.Length} 项 · 存档完整保留 {CoverageFields.Count} 项";
        OnPropertyChanged(nameof(HasMultipleCoveragePages));
        OnPropertyChanged(nameof(HasVisibleCoverageFields));
        OnPropertyChanged(nameof(SelectedMenuFieldSummary));
        PreviousCoveragePageCommand.NotifyCanExecuteChanged();
        NextCoveragePageCommand.NotifyCanExecuteChanged();
    }

    private static List<SnapshotParameterGroupViewModel> CreateParameterGroups(
        ApplicationSnapshot application,
        IReadOnlyList<SnapshotCoverageFieldViewModel> fields)
    {
        var groups = new List<SnapshotParameterGroupViewModel>
        {
            SnapshotParameterGroupViewModel.CreateAll(fields.Count)
        };
        if (application.Kind == ApplicationKind.LiveCompanion
            && application.ConfigurationTree is { } tree)
        {
            groups.AddRange(tree.Sections.OrderBy(section => section.Order).Select(section =>
                new SnapshotParameterGroupViewModel(
                    section.NativeName,
                    section.NativeName == "未映射原生字段" ? "待映射原生数据" : section.NativeName,
                    section.NativeName == "未映射原生字段"
                        ? section.UiPath
                        : "按直播伴侣原生面板结构归组；当前值来自存档，恢复能力另行验证",
                    fields.Count(field => string.Equals(field.GroupKey, section.NativeName, StringComparison.Ordinal)))));
        }

        return groups;
    }

    private static string FormatPageSummary(int offset, int visibleCount, int totalCount) => totalCount == 0
        ? "0 项"
        : $"第 {offset + 1}–{offset + visibleCount} 项，共 {totalCount} 项";

    private static SnapshotCoverageFieldViewModel[] CreateCoverageFields(
        ApplicationSnapshot application)
    {
        var actualValues = FlattenValues(application);
        var cannotVerifyRestore = application.Compatibility == CompatibilityLevel.Unsupported;
        var declaredPaths = application.FieldCoverage.Select(field => field.NativePath).ToArray();
        var fields = application.FieldCoverage.SelectMany(field =>
        {
            var isGap = cannotVerifyRestore || field.EvidenceStatus < FieldEvidenceStatus.Mapped;
            if (actualValues.TryGetValue(field.NativePath, out var value))
            {
                return SnapshotCoverageFieldViewModel.CreateCaptured(field, value, isGap);
            }

            var descendants = actualValues
                .Where(pair => pair.Key.StartsWith($"{field.NativePath}/", StringComparison.Ordinal))
                .SelectMany(pair => SnapshotCoverageFieldViewModel.CreateCaptured(
                    field with
                    {
                        NativePath = pair.Key,
                        NativeName = pair.Key.Split('/').LastOrDefault() ?? field.NativeName,
                        UiPath = $"{field.UiPath}/{pair.Key[(field.NativePath.Length + 1)..]}"
                    },
                    pair.Value,
                    isGap))
                .ToArray();
            return descendants.Length > 0
                ? descendants
                : [new SnapshotCoverageFieldViewModel(field, null, true)];
        }).ToList();
        fields.AddRange(actualValues
            .Where(pair => !declaredPaths.Any(path =>
                string.Equals(path, pair.Key, StringComparison.Ordinal)
                || pair.Key.StartsWith($"{path}/", StringComparison.Ordinal)))
            .SelectMany(pair => SnapshotCoverageFieldViewModel.CreateUndeclared(pair.Key, pair.Value)));
        return fields
            .OrderByDescending(field => field.IsGap)
            .ThenBy(field => field.TechnicalPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, JsonElement> FlattenValues(ApplicationSnapshot application)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (application.Kind == ApplicationKind.Obs)
        {
            foreach (var source in application.Sources)
            {
                values[$"/sources/{source.LogicalId:N}/kind"] = JsonSerializer.SerializeToElement(source.Kind);
                values[$"/sources/{source.LogicalId:N}/unversionedKind"] = JsonSerializer.SerializeToElement(
                    source.UnversionedKind);
                foreach (var setting in source.Settings)
                {
                    AddFlattenedValue(
                        values,
                        $"/sources/{source.LogicalId:N}/settings/{EscapePointerSegment(setting.Key)}",
                        setting.Value);
                }

                foreach (var filter in source.Filters)
                {
                    var prefix = $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}";
                    values[$"{prefix}/kind"] = JsonSerializer.SerializeToElement(filter.Kind);
                    values[$"{prefix}/enabled"] = JsonSerializer.SerializeToElement(filter.Enabled);
                    values[$"{prefix}/order"] = JsonSerializer.SerializeToElement(filter.Order);
                    foreach (var setting in filter.Settings)
                    {
                        AddFlattenedValue(
                            values,
                            $"{prefix}/settings/{EscapePointerSegment(setting.Key)}",
                            setting.Value);
                    }
                }
            }
        }

        foreach (var document in application.NativeDocuments)
        {
            foreach (var value in document.Values)
            {
                AddFlattenedValue(values, $"{document.RelativePath}:{value.JsonPointer}", value.Value);
            }
        }

        return values;
    }

    private static void AddFlattenedValue(
        IDictionary<string, JsonElement> destination,
        string path,
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length > 0)
            {
                foreach (var property in properties)
                {
                    AddFlattenedValue(
                        destination,
                        $"{path}/{EscapePointerSegment(property.Name)}",
                        property.Value);
                }

                return;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            if (items.Length > 0)
            {
                for (var index = 0; index < items.Length; index++)
                {
                    AddFlattenedValue(destination, $"{path}/{index}", items[index]);
                }

                return;
            }
        }

        destination[path] = value.Clone();
    }

    private static string EscapePointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static string ShortHash(string value) => string.IsNullOrWhiteSpace(value)
        ? "未记录"
        : value.Length <= 20 ? value : $"{value[..12]}…{value[^8..]}";
}

public sealed record SnapshotParameterGroupViewModel(
    string Key,
    string Name,
    string Description,
    int Count)
{
    public const string AllKey = "all";

    public bool IsUnmapped => Key == "未映射原生字段";

    public string Summary => IsUnmapped
        ? $"已读取 {Count} 项"
        : Count == 0
            ? "当前无内容"
            : $"{Count} 项";

    public string MappingNotice => IsUnmapped
        ? "按原生文档分页查看；内部字段名不等同于直播伴侣界面名称"
        : Count == 0
            ? "当前存档在此菜单下没有可显示的配置内容"
            : "按直播伴侣原生面板结构显示；恢复状态以字段标记为准";

    public static SnapshotParameterGroupViewModel CreateAll(int count) =>
        new(AllKey, "全部字段", "不遗漏地显示存档中的全部原生字段", count);
}

public sealed partial class SnapshotNativeDocumentViewModel : ObservableObject
{
    private const int HighlightLimit = 12;
    private const int PageSize = 40;
    private int pageIndex;
    private SnapshotNativeItemViewModel[] filteredItems = [];

    public SnapshotNativeDocumentViewModel(NativeConfigurationDocument document)
    {
        FileName = document.RelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                   ?? document.RelativePath;
        Name = DocumentName(FileName);
        Detail = $"完整保存 {document.Values.Count} 个原始字段";
        Fields = document.Values.Select(value => new SnapshotNativeFieldViewModel(value)).ToArray();
        var allEffectiveItems = SnapshotNativeItemViewModel.Create(
            document.Values,
            onlyEffectSliders: FileName == "effectConfigStore.json");
        EffectiveItems = allEffectiveItems.Where(item => !item.IsPreset).ToArray();
        PresetItems = allEffectiveItems.Where(item => item.IsPreset).ToArray();
        PresetGroupCount = PresetItems
            .Select(item => item.ScopeName)
            .Distinct(StringComparer.Ordinal)
            .Count();
        HighlightedFields = Fields
            .Where(field => field.HasMeaningfulValue && !field.IsTechnicalIdentifier)
            .Take(HighlightLimit)
            .ToArray();
        MeaningfulFieldCount = Fields.Count(field => field.HasMeaningfulValue && !field.IsTechnicalIdentifier);
        HighlightSummary = MeaningfulFieldCount == 0
            ? "没有检测到非空或启用值"
            : $"{MeaningfulFieldCount} 项有内容 · 默认显示前 {HighlightedFields.Count} 项";
        UsageSummary = EffectiveItems.Count == 0 && PresetItems.Count == 0
            ? "当前没有已修改或生效参数"
            : PresetItems.Count == 0
                ? $"{EffectiveItems.Count} 个已修改或生效参数"
                : $"{EffectiveItems.Count} 个已修改或生效参数 · {PresetGroupCount} 组预设备份";
        PresetSummary = PresetGroupCount == 0
            ? string.Empty
            : $"另有 {PresetItems.Count} 个预设参数，切换后查看";
        var officialNameCount = allEffectiveItems.Count(item => item.HasOfficialDisplayName);
        NameSourceSummary = officialNameCount == 0
            ? "当前文档尚未找到直播伴侣官方中文名称"
            : $"{officialNameCount}/{allEffectiveItems.Count} 项使用直播伴侣官方中文名称 · 仅改善显示，不代表已经允许还原";
        RefreshPage();
        RefreshItems();
    }

    public string Name { get; }

    public string FileName { get; }

    public string Detail { get; }

    public IReadOnlyList<SnapshotNativeFieldViewModel> Fields { get; }

    public IReadOnlyList<SnapshotNativeFieldViewModel> HighlightedFields { get; }

    public int MeaningfulFieldCount { get; }

    public string HighlightSummary { get; }

    public IReadOnlyList<SnapshotNativeItemViewModel> EffectiveItems { get; }

    public IReadOnlyList<SnapshotNativeItemViewModel> PresetItems { get; }

    public int PresetGroupCount { get; }

    public bool HasPresetItems => PresetItems.Count > 0;

    public string PresetSummary { get; }

    public string UsageSummary { get; }

    public string NameSourceSummary { get; }

    public bool HasEffectiveItems => EffectiveItems.Count > 0 || PresetItems.Count > 0;

    public bool HasVisibleItems => filteredItems.Length > 0;

    [ObservableProperty]
    public partial string ItemSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowPresetItems { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SnapshotNativeItemViewModel> VisibleItems { get; private set; } = [];

    [ObservableProperty]
    public partial string ItemPageSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<SnapshotNativeFieldViewModel> VisibleFields { get; private set; } = [];

    [ObservableProperty]
    public partial string PageSummary { get; private set; } = string.Empty;

    public bool HasMultiplePages => Fields.Count > PageSize;

    partial void OnItemSearchTextChanged(string value)
    {
        RefreshItems();
    }

    partial void OnShowPresetItemsChanged(bool value)
    {
        RefreshItems();
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousPage))]
    private void PreviousPage()
    {
        pageIndex--;
        RefreshPage();
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextPage))]
    private void NextPage()
    {
        pageIndex++;
        RefreshPage();
    }

    private bool CanGoToPreviousPage() => pageIndex > 0;

    private bool CanGoToNextPage() => (pageIndex + 1) * PageSize < Fields.Count;

    private void RefreshItems()
    {
        var search = ItemSearchText.Trim();
        var source = ShowPresetItems ? PresetItems : EffectiveItems;
        filteredItems = source
            .Where(item => string.IsNullOrWhiteSpace(search) || item.Matches(search))
            .ToArray();
        VisibleItems = filteredItems;
        ItemPageSummary = filteredItems.Length == 0
            ? "没有匹配的有效配置"
            : ShowPresetItems
                ? $"共 {filteredItems.Length} 项预设参数 · 连续滚动查看"
                : $"共 {filteredItems.Length} 项当前参数 · 连续滚动查看";
        OnPropertyChanged(nameof(HasVisibleItems));
    }

    private void RefreshPage()
    {
        var offset = pageIndex * PageSize;
        VisibleFields = Fields.Skip(offset).Take(PageSize).ToArray();
        PageSummary = Fields.Count == 0
            ? "没有读取到字段"
            : $"第 {offset + 1}–{offset + VisibleFields.Count} 项，共 {Fields.Count} 项";
        OnPropertyChanged(nameof(HasMultiplePages));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private static string DocumentName(string fileName) => fileName switch
    {
        "effectConfigStore.json" => "检测到的效果配置",
        "effectStore.json" => "检测到的效果状态",
        "filterStore.json" => "检测到的滤镜配置",
        "sourceStore.json" => "检测到的摄像头与视频配置",
        _ => "检测到的原生配置"
    };
}

public sealed class SnapshotNativeItemViewModel
{
    private static readonly string[] StateNames = ["use", "enable", "isOn", "panelUse"];
    private static readonly string[] PrimaryValueNames = ["absVal", "value", "rate", "width", "height", "name"];

    private SnapshotNativeItemViewModel(
        string name,
        string currentValue,
        string state,
        string detail,
        string technicalPath,
        string searchText)
    {
        Name = name;
        DisplayName = name;
        CurrentValue = currentValue;
        State = state;
        Detail = detail;
        TechnicalPath = technicalPath;
        searchData = searchText;
    }

    private readonly string searchData;

    public string Name { get; }

    public string DisplayName { get; private set; }

    public bool HasOfficialDisplayName { get; private set; }

    public bool IsPreset { get; private set; }

    public string ScopeName { get; private set; } = "当前参数";

    public string CurrentValue { get; }

    public string CompactValue => CurrentValue
        .Replace("界面值 ", string.Empty, StringComparison.Ordinal)
        .Replace("原生值 ", string.Empty, StringComparison.Ordinal);

    public string State { get; }

    public bool HasState => !string.IsNullOrWhiteSpace(State);

    public string Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string TechnicalPath { get; }

    public bool Matches(string search) => DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                                          || searchData.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    public static IReadOnlyList<SnapshotNativeItemViewModel> Create(
        IReadOnlyList<NativeConfigurationValue> values,
        bool onlyEffectSliders = false)
    {
        var leaves = new List<NativeLeaf>();
        var order = 0;
        foreach (var value in values)
        {
            Flatten(value.JsonPointer, value.Value, leaves, ref order);
        }

        var items = leaves
            .Where(leaf => !SnapshotNativeFieldViewModel.IsTechnicalIdentifierValue(leaf.LeafName, leaf.Value))
            .GroupBy(leaf => GroupKey(leaf.Segments), StringComparer.Ordinal)
            .Where(group => !onlyEffectSliders || group.Key.Contains("/sliders/", StringComparison.Ordinal))
            .Select(group => CreateItem(group.Key, group.OrderBy(leaf => leaf.Order).ToArray()))
            .Where(item => item is not null)
            .Cast<SnapshotNativeItemViewModel>()
            .OrderBy(item => item.SortOrder)
            .ToArray();
        var presetNames = CreatePresetNames(values);
        foreach (var item in items)
        {
            var officialName = SnapshotChineseDisplay.LiveCompanionOfficialParameterName(item.TechnicalPath);
            var displayName = officialName ?? SnapshotChineseDisplay.NativeItemName(item.Name);
            if (TryGetPresetScope(item.TechnicalPath, presetNames, out var presetScope))
            {
                item.IsPreset = true;
                item.ScopeName = presetScope;
                displayName = $"{presetScope} · {displayName}";
            }

            item.DisplayName = displayName;
            item.HasOfficialDisplayName = officialName is not null;
        }

        return items;
    }

    private static Dictionary<string, string> CreatePresetNames(
        IReadOnlyList<NativeConfigurationValue> values)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var segments = value.JsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Unescape)
                .ToArray();
            var tempsIndex = IndexOf(segments, "temps");
            if (tempsIndex <= 0
                || tempsIndex + 2 >= segments.Length
                || !segments[^1].Equals("name", StringComparison.Ordinal))
            {
                continue;
            }

            var name = value.Value.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var section = segments[tempsIndex - 1] switch
            {
                "effect" => "美颜预设",
                "slim" => "形体预设",
                _ => "效果预设"
            };
            names[$"{segments[tempsIndex - 1]}|{segments[tempsIndex + 1]}"] = $"{section} · {name}";
        }

        return names;
    }

    private static bool TryGetPresetScope(
        string technicalPath,
        Dictionary<string, string> presetNames,
        out string presetScope)
    {
        var segments = technicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Unescape)
            .ToArray();
        var tempsIndex = IndexOf(segments, "temps");
        if (tempsIndex > 0
            && tempsIndex + 1 < segments.Length
            && presetNames.TryGetValue(
                $"{segments[tempsIndex - 1]}|{segments[tempsIndex + 1]}",
                out var knownScope))
        {
            presetScope = knownScope;
            return true;
        }

        presetScope = string.Empty;
        return false;
    }

    private int SortOrder { get; init; }

    private static SnapshotNativeItemViewModel? CreateItem(string groupKey, IReadOnlyList<NativeLeaf> leaves)
    {
        var stateLeaf = leaves.FirstOrDefault(leaf => StateNames.Contains(leaf.LeafName, StringComparer.OrdinalIgnoreCase));
        if (stateLeaf is not null && IsDisabledState(stateLeaf.Value))
        {
            return null;
        }

        var meaningful = leaves
            .Where(leaf => SnapshotNativeFieldViewModel.HasMeaningfulContent(leaf.Value))
            .Where(leaf => !StateNames.Contains(leaf.LeafName, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (meaningful.Length == 0)
        {
            return null;
        }

        if (groupKey.EndsWith("/giftPlayConfig/groupEffectIds", StringComparison.Ordinal))
        {
            return CreateSummaryItem(
                "礼物特效分组",
                $"已保存 {meaningful.Length} 个特效编号",
                string.Empty,
                groupKey,
                leaves);
        }

        if (groupKey.EndsWith("/giftPlayConfig", StringComparison.Ordinal))
        {
            var details = meaningful
                .Where(leaf => leaf.LeafName is "queueLength" or "sourceTransparency" or "preDuration")
                .Take(3)
                .Select(FormatNamedValue);
            return CreateSummaryItem(
                "礼物特效播放配置",
                $"当前 {meaningful.Length} 项有内容",
                string.Join(" · ", details),
                groupKey,
                leaves);
        }

        if (groupKey.EndsWith("/shownTips", StringComparison.Ordinal))
        {
            return CreateSummaryItem(
                "功能提示记录",
                $"当前 {meaningful.Length} 项提示已显示",
                string.Empty,
                groupKey,
                leaves);
        }

        if (groupKey.EndsWith("/deviceFacing", StringComparison.Ordinal))
        {
            return CreateSummaryItem(
                "摄像头方向记录",
                "已记录",
                string.Empty,
                groupKey,
                leaves);
        }

        if (groupKey.EndsWith("/entityStamp", StringComparison.Ordinal))
        {
            return null;
        }

        if (groupKey.EndsWith("/jsbLynxEffectStr", StringComparison.Ordinal)
            || groupKey.EndsWith("/lynxEffectStr", StringComparison.Ordinal)
            || groupKey.EndsWith("/togetherStr", StringComparison.Ordinal))
        {
            return CreateSummaryItem(
                groupKey.EndsWith("/togetherStr", StringComparison.Ordinal) ? "连麦配置" : "特效渲染配置",
                "已保存配置",
                string.Empty,
                groupKey,
                leaves);
        }

        if (groupKey.Contains("/effect/items/", StringComparison.Ordinal)
            && !groupKey.Contains("/sliders/", StringComparison.Ordinal)
            && meaningful.All(leaf => leaf.LeafName is "use" or "edition"))
        {
            return null;
        }

        if (meaningful.All(leaf => int.TryParse(
                leaf.LeafName,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _)
            && leaf.Value.ValueKind == JsonValueKind.String))
        {
            return null;
        }

        var primaryLeaf = PrimaryValueNames
            .Select(name => meaningful.FirstOrDefault(leaf => leaf.LeafName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(leaf => leaf is not null)
            ?? meaningful[0];
        var detailValues = meaningful
            .Where(leaf => !ReferenceEquals(leaf, primaryLeaf)
                           && !ReferenceEquals(leaf, stateLeaf)
                           && !leaf.LeafName.Equals("name", StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .Select(FormatNamedValue)
            .ToArray();
        var name = ItemName(groupKey, leaves);
        var currentValue = FormatNamedValue(primaryLeaf);
        var width = meaningful.FirstOrDefault(leaf => leaf.LeafName == "width");
        var height = meaningful.FirstOrDefault(leaf => leaf.LeafName == "height");
        if (width is not null && height is not null)
        {
            var rate = meaningful.FirstOrDefault(leaf => leaf.LeafName == "rate");
            currentValue = $"{SnapshotNativeFieldViewModel.FormatDisplayValue(width.Value)} × "
                           + SnapshotNativeFieldViewModel.FormatDisplayValue(height.Value)
                           + (rate is null ? string.Empty : $" · {SnapshotNativeFieldViewModel.FormatDisplayValue(rate.Value)} FPS");
            detailValues = meaningful
                .Where(leaf => leaf.LeafName is "format" or "colorSpace" or "videoRange")
                .Select(FormatNamedValue)
                .ToArray();
        }
        var search = string.Join(
            '\n',
            new[] { name, currentValue, string.Join(" · ", detailValues), groupKey }
                .Concat(leaves.Select(leaf => leaf.Path)));
        return new SnapshotNativeItemViewModel(
            name,
            currentValue,
            string.Empty,
            string.Join(" · ", detailValues),
            groupKey,
            search)
        {
            SortOrder = leaves.Min(leaf => leaf.Order)
        };
    }

    private static SnapshotNativeItemViewModel CreateSummaryItem(
        string name,
        string currentValue,
        string detail,
        string groupKey,
        IReadOnlyList<NativeLeaf> leaves)
    {
        var search = string.Join('\n', new[] { name, currentValue, detail, groupKey }
            .Concat(leaves.Select(leaf => leaf.Path)));
        return new SnapshotNativeItemViewModel(
            name,
            currentValue,
            string.Empty,
            detail,
            groupKey,
            search)
        {
            SortOrder = leaves.Min(leaf => leaf.Order)
        };
    }

    private static void Flatten(
        string path,
        JsonElement value,
        ICollection<NativeLeaf> destination,
        ref int order)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length > 0)
            {
                foreach (var property in properties)
                {
                    Flatten($"{path}/{Escape(property.Name)}", property.Value, destination, ref order);
                }

                return;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            if (items.Length > 0)
            {
                for (var index = 0; index < items.Length; index++)
                {
                    Flatten($"{path}/{index}", items[index], destination, ref order);
                }

                return;
            }
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Unescape)
            .ToArray();
        destination.Add(new NativeLeaf(path, segments, segments.LastOrDefault() ?? path, value.Clone(), order++));
    }

    private static string GroupKey(string[] segments)
    {
        var sliderIndex = IndexOf(segments, "sliders");
        if (sliderIndex >= 0 && sliderIndex + 1 < segments.Length)
        {
            return "/" + string.Join('/', segments.Take(sliderIndex + 2).Select(Escape));
        }

        var pointsIndex = IndexOf(segments, "points");
        if (pointsIndex >= 0 && pointsIndex + 1 < segments.Length)
        {
            return "/" + string.Join('/', segments.Take(pointsIndex + 2).Select(Escape));
        }

        var anchorsIndex = IndexOf(segments, "anchors");
        if (anchorsIndex >= 0 && anchorsIndex + 1 < segments.Length)
        {
            return "/" + string.Join('/', segments.Take(anchorsIndex + 2).Select(Escape));
        }

        var filterIndex = IndexOf(segments, "filterDataList");
        if (filterIndex >= 0 && filterIndex + 1 < segments.Length)
        {
            return "/" + string.Join('/', segments.Take(filterIndex + 2).Select(Escape));
        }

        return segments.Length <= 2
            ? "/" + string.Join('/', segments.Select(Escape))
            : "/" + string.Join('/', segments.Take(segments.Length - 1).Select(Escape));
    }

    private static string ItemName(string groupKey, IReadOnlyList<NativeLeaf> leaves)
    {
        var segments = groupKey.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToArray();
        var sliderIndex = IndexOf(segments, "sliders");
        if (sliderIndex >= 0 && sliderIndex + 1 < segments.Length)
        {
            return $"内部参数 {segments[sliderIndex + 1]}";
        }

        var filterIndex = IndexOf(segments, "filterDataList");
        var pointsIndex = IndexOf(segments, "points");
        var curvesIndex = IndexOf(segments, "curves");
        var anchorsIndex = IndexOf(segments, "anchors");
        if (filterIndex >= 0
            && filterIndex + 1 < segments.Length
            && int.TryParse(segments[filterIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var filter))
        {
            if (pointsIndex >= 0
                && pointsIndex + 1 < segments.Length
                && int.TryParse(segments[pointsIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var point))
            {
                return $"滤镜第 {filter + 1} 项 · 控制点 {point + 1}";
            }

            if (curvesIndex >= 0
                && curvesIndex + 1 < segments.Length
                && int.TryParse(segments[curvesIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var curve)
                && anchorsIndex >= 0
                && anchorsIndex + 1 < segments.Length
                && int.TryParse(segments[anchorsIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var anchor))
            {
                return $"滤镜第 {filter + 1} 项 · 曲线通道 {curve + 1} · 控制点 {anchor + 1}";
            }

            var nativeName = leaves.FirstOrDefault(leaf => leaf.LeafName.Equals("name", StringComparison.OrdinalIgnoreCase));
            return nativeName is not null && nativeName.Value.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(nativeName.Value.GetString())
                ? $"滤镜第 {filter + 1} 项 · {nativeName.Value.GetString()}"
                : $"滤镜第 {filter + 1} 项";
        }


        var templateIndex = IndexOf(segments, "filterTempList");
        if (templateIndex >= 0 && templateIndex + 1 < segments.Length)
        {
            var nativeName = leaves.FirstOrDefault(leaf => leaf.LeafName.Equals("name", StringComparison.OrdinalIgnoreCase));
            return nativeName is not null && nativeName.Value.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(nativeName.Value.GetString())
                ? nativeName.Value.GetString()!
                : "滤镜预设";
        }

        var last = segments.LastOrDefault() ?? "原生配置";
        if (Guid.TryParse(last, out _) || long.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            var nativeName = leaves.FirstOrDefault(leaf => leaf.LeafName.Equals("name", StringComparison.OrdinalIgnoreCase));
            return nativeName is not null && nativeName.Value.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(nativeName.Value.GetString())
                ? $"视频来源 · {nativeName.Value.GetString()}"
                : "待确认配置项";
        }

        return FriendlyName(last);
    }

    private static string FormatNamedValue(NativeLeaf leaf) => leaf.LeafName switch
    {
        "absVal" => $"界面值 {SnapshotNativeFieldViewModel.FormatDisplayValue(leaf.Value)}",
        "value" => $"原生值 {SnapshotNativeFieldViewModel.FormatDisplayValue(leaf.Value)}",
        "width" => $"宽度 {SnapshotNativeFieldViewModel.FormatDisplayValue(leaf.Value)}",
        "height" => $"高度 {SnapshotNativeFieldViewModel.FormatDisplayValue(leaf.Value)}",
        "rate" => $"帧率 {SnapshotNativeFieldViewModel.FormatDisplayValue(leaf.Value)}",
        "resourceFilePath" => leaf.Value.ValueKind == JsonValueKind.String
                              && !string.IsNullOrWhiteSpace(leaf.Value.GetString())
            ? "已设置目录"
            : "未设置目录",
        _ => $"{FriendlyName(leaf.LeafName)} {SnapshotChineseDisplay.NativeValue(leaf.LeafName, leaf.Value)}"
    };

    private static string FormatState(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "已开启",
        JsonValueKind.False => "已关闭",
        JsonValueKind.Number when value.TryGetInt32(out var number) => number == 0 ? "已关闭" : "已开启",
        _ => SnapshotNativeFieldViewModel.FormatDisplayValue(value)
    };

    private static bool IsDisabledState(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.False or JsonValueKind.Null => true,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number == 0,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString())
                                || value.GetString() is "0" or "false" or "off",
        _ => false
    };

    private static string FriendlyName(string name) => name switch
    {
        "use" or "enable" or "isOn" or "panelUse" => "开关",
        "absVal" => "界面值",
        "value" => "原生值",
        "name" => "名称",
        "rate" => "帧率",
        "width" => "宽度",
        "height" => "高度",
        "colorSpace" => "色彩空间",
        "videoRange" => "色彩范围",
        "payload" => "视频参数",
        "effect1" => "画面效果",
        "filterData" => "滤镜设置",
        "type" => "类型",
        "format" => "视频格式",
        "hue" => "色相",
        "hueAdjust" => "色相调整",
        "saturation" => "饱和度",
        "saturationAdjust" => "饱和度调整",
        "lightnessAdjust" => "亮度调整",
        "opacity" => "不透明度",
        "gamma" => "伽马",
        "radius" => "半径",
        "sigma" => "柔化范围",
        "slim" => "强度",
        "selectedHueKey" => "选中色相",
        "useLoki" => "效果开关",
        "resourceFilePath" => "自定义滤镜素材目录",
        "useFilterSuppor" => "滤镜能力版本",
        "useNewFilter" => "新版滤镜引擎",
        "showRedDot" => "新功能提示",
        "filterInstanceList" => "当前滤镜实例",
        "filterTempList" => "滤镜预设",
        "carnivalInfo" => "特效状态",
        "using" => "正在使用",
        "showChat" => "聊天显示",
        "useCurveFilter" => "曲线滤镜开关",
        "sourceUsingLens" => "来源镜头效果",
        "deviceFacing" => "摄像头方向记录",
        "giftPlayConfig" => "礼物特效播放配置",
        "interactSourceId" => "互动特效来源",
        "shownTips" => "功能提示记录",
        "faceSources" => "人脸特效来源",
        "sourceLink" => "特效资源关联",
        "enableRenderFilter" => "礼物画面滤镜",
        "enableSpeech" => "语音识别",
        "queueLength" => "播放队列长度",
        "sourceTransparency" => "来源透明度",
        "preDuration" => "预加载时长",
        "togetherSlotCount" => "连麦席位数",
        "effectConfigId" => "美颜配置关联",
        "liveScene" => "直播画面来源",
        "secondSource" => "第二视频来源",
        "viewIndex" => "来源顺序",
        "cct" => "色温",
        "tint" => "色调",
        "amount" => "强度",
        "file" or "fileName" => "素材文件",
        "hueshift" => "色相偏移",
        "colorAdd" => "叠加颜色",
        "colorMultiply" => "混合颜色",
        "curvesType" => "曲线模式",
        "curves" => "曲线",
        "anchors" => "控制点",
        "position" => "位置",
        "leftControl" => "左控制柄",
        "rightControl" => "右控制柄",
        "x" => "横坐标",
        "y" => "纵坐标",
        _ => "其他参数"
    };

    private static int IndexOf(string[] values, string expected)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index].Equals(expected, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static string Unescape(string value) => value.Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);

    private sealed record NativeLeaf(
        string Path,
        string[] Segments,
        string LeafName,
        JsonElement Value,
        int Order);
}

public sealed class SnapshotNativeFieldViewModel
{
    private static readonly string[] SensitiveStringTerms =
        ["authorization", "apptoken", "cookie", "credential", "oauth", "password", "secret", "streamkey"];

    private static readonly Dictionary<string, string> CommonNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["absVal"] = "界面数值（absVal）",
            ["value"] = "原生值（value）",
            ["use"] = "启用状态（use）",
            ["enable"] = "总开关（enable）",
            ["isOn"] = "开启状态（isOn）",
            ["name"] = "名称（name）",
            ["id"] = "内部 ID（id）",
            ["deviceId"] = "设备 ID（deviceId）",
            ["width"] = "分辨率宽度（width）",
            ["height"] = "分辨率高度（height）",
            ["rate"] = "帧率（rate）",
            ["format"] = "视频格式（format）",
            ["colorSpace"] = "色彩空间（colorSpace）",
            ["videoRange"] = "色彩范围（videoRange）",
            ["type"] = "原生类型（type）"
        };

    public SnapshotNativeFieldViewModel(NativeConfigurationValue value)
    {
        var segments = value.JsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(UnescapePointerSegment)
            .ToArray();
        var leaf = segments.LastOrDefault() ?? value.JsonPointer;
        var subject = Subject(segments);
        var fieldName = CommonNames.TryGetValue(leaf, out var friendlyName) ? friendlyName : leaf;
        Name = string.IsNullOrWhiteSpace(subject) || string.Equals(subject, leaf, StringComparison.Ordinal)
            ? fieldName
            : $"{subject} · {fieldName}";
        Value = FormatDisplayValue(value.Value);
        NativeType = value.Value.ValueKind.ToString();
        TechnicalPath = value.JsonPointer;
        Status = "EvidenceOnly · 待映射";
        HasMeaningfulValue = HasMeaningfulContent(value.Value);
        IsTechnicalIdentifier = IsTechnicalIdentifierValue(leaf, value.Value);
    }

    public string Name { get; }

    public string Value { get; }

    public string NativeType { get; }

    public string TechnicalPath { get; }

    public string Status { get; }

    public bool HasMeaningfulValue { get; }

    public bool IsTechnicalIdentifier { get; }

    private static string Subject(string[] segments)
    {
        var sliderIndex = IndexOf(segments, "sliders");
        if (sliderIndex >= 0 && sliderIndex + 1 < segments.Length)
        {
            return $"内部字段 {segments[sliderIndex + 1]}";
        }

        var filterIndex = IndexOf(segments, "filterDataList");
        if (filterIndex >= 0
            && filterIndex + 1 < segments.Length
            && int.TryParse(segments[filterIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var itemIndex))
        {
            return $"滤镜第 {itemIndex + 1} 项";
        }

        if (segments.Length < 2)
        {
            return string.Empty;
        }

        var parent = segments[^2];
        if (Guid.TryParse(parent, out _) || long.TryParse(parent, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return "原生配置项";
        }

        return parent;
    }

    private static int IndexOf(string[] values, string expected)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (string.Equals(values[index], expected, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    internal static string FormatDisplayValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "开启",
        JsonValueKind.False => "关闭",
        JsonValueKind.String => FormatStringValue(value.GetString()),
        JsonValueKind.Null => "空值",
        JsonValueKind.Array when value.GetArrayLength() == 0 => "空数组",
        JsonValueKind.Object when !value.EnumerateObject().Any() => "空对象",
        _ => value.GetRawText()
    };

    private static string FormatStringValue(string? value)
    {
        if (value is null)
        {
            return "空字符串";
        }

        var normalized = new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return SensitiveStringTerms.Any(normalized.Contains)
            ? "已隐藏敏感内容"
            : value;
    }

    internal static bool HasMeaningfulContent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.False => false,
        JsonValueKind.True => true,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Number => !value.TryGetDecimal(out var number) || number != 0,
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        _ => true
    };

    internal static bool IsTechnicalIdentifierValue(string name, JsonElement value) =>
        name.Equals("id", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Id", StringComparison.Ordinal)
        || name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
        || name.Contains("guid", StringComparison.OrdinalIgnoreCase)
        || name.Contains("uuid", StringComparison.OrdinalIgnoreCase)
        || value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _);

    private static string UnescapePointerSegment(string value) => value.Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);
}

public sealed record SnapshotFieldGroupViewModel(
    string Name,
    IReadOnlyList<SnapshotCoverageFieldViewModel> Fields)
{
    public bool HasName => !string.IsNullOrWhiteSpace(Name);

    public bool IsCurveGroup => Name.Contains("曲线", StringComparison.Ordinal);

    public double FieldMinItemWidth => IsCurveGroup ? 520 : 205;

    public int FieldMaxColumns => IsCurveGroup ? 2 : 5;
}

public sealed class SnapshotCoverageFieldViewModel
{
    public SnapshotCoverageFieldViewModel(
        CapturedParameterField field,
        string? value,
        bool isGap)
    {
        Name = string.IsNullOrWhiteSpace(field.NativeName)
            ? FieldName(field.Category, field.NativePath)
            : field.NativeName;
        TechnicalPath = field.NativePath;
        UiPath = field.UiPath;
        Value = value ?? "没有捕获到值";
        DisplayValue = FormatDisplayValue(Value, TechnicalPath);
        HasCapturedValue = value is not null;
        GroupKey = string.IsNullOrWhiteSpace(field.UiPath)
            ? "未映射原生字段"
            : field.UiPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
              ?? "未映射原生字段";
        Type = field.ValueType;
        Requirement = field.Required ? "必需" : "可选";
        Access = field.Writable && field.EvidenceStatus >= FieldEvidenceStatus.Mapped ? "可恢复" : "只读";
        Verification = VerificationName(field.Verification);
        Status = !HasCapturedValue
            ? "缺失"
            : field.EvidenceStatus switch
            {
                FieldEvidenceStatus.Verified => "已验证",
                FieldEvidenceStatus.Mapped => "已映射",
                FieldEvidenceStatus.EvidenceOnly => "仅有证据",
                _ => "未知"
            };
        IsGap = isGap;
        MenuLabel = string.Join(" · ", field.UiPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1));
        if (string.IsNullOrWhiteSpace(MenuLabel))
        {
            MenuLabel = Name;
        }

        IsTechnicalIdentifier = IsTechnicalIdentifierField(Name, TechnicalPath, Value);
        IsActivationField = IsActivationPath(TechnicalPath);
        IsSemanticBooleanFeature = !IsActivationField && Value == "true";
        PresentationGroup = CreatePresentationGroup(UiPath);
        HasDisplayValue = !IsSemanticBooleanFeature;
        ShowByDefault = HasCapturedValue
                        && HasMeaningfulValue(Value, TechnicalPath)
                        && !IsActivationField
                        && !IsTechnicalIdentifier
                        && !IsUnchangedStructuralField(Name, TechnicalPath, Value)
                        && !IsEffectRuntimeInfrastructureField(TechnicalPath);
    }

    private SnapshotCoverageFieldViewModel(
        SnapshotCoverageFieldViewModel source,
        string menuLabel,
        string displayValue,
        string technicalPath)
    {
        Name = menuLabel;
        TechnicalPath = technicalPath;
        UiPath = source.UiPath;
        Value = displayValue;
        DisplayValue = displayValue;
        HasCapturedValue = true;
        GroupKey = source.GroupKey;
        Type = "汇总";
        Requirement = source.Requirement;
        Access = source.Access;
        Verification = source.Verification;
        Status = source.Status;
        IsGap = source.IsGap;
        MenuLabel = menuLabel;
        IsTechnicalIdentifier = false;
        IsActivationField = false;
        IsSemanticBooleanFeature = false;
        PresentationGroup = CreatePresentationGroup(UiPath);
        HasDisplayValue = true;
        ShowByDefault = true;
    }

    public string Name { get; }

    public string TechnicalPath { get; }

    public string UiPath { get; }

    public string Value { get; }

    public string DisplayValue { get; }

    public string MenuLabel { get; private set; }

    public string CompactMenuLabel => MenuLabel
        .Split(" · ", StringSplitOptions.RemoveEmptyEntries)
        .LastOrDefault() ?? MenuLabel;

    public bool HasCapturedValue { get; }

    public string GroupKey { get; }

    public string Type { get; }

    public string Requirement { get; }

    public string Access { get; }

    public string Verification { get; }

    public string Status { get; }

    public bool IsGap { get; }

    public bool IsTechnicalIdentifier { get; }

    public bool IsActivationField { get; }

    public bool IsSemanticBooleanFeature { get; }

    public bool HasDisplayValue { get; }

    public string PresentationGroup { get; }

    public bool ShowByDefault { get; }

    public string DisplayStatus => HasCapturedValue ? "已保存" : "缺失";

    public string FullDisplayText => HasDisplayValue
        ? $"{MenuLabel}：{DisplayValue}"
        : MenuLabel;

    public static IReadOnlyList<SnapshotCoverageFieldViewModel> CreateCompactMenuRows(
        IEnumerable<SnapshotCoverageFieldViewModel> fields)
    {
        var allFields = fields.ToArray();
        var disabledScopes = allFields
            .Where(field => field.IsActivationField && IsDisabledState(field.Value))
            .Select(field => ParentPath(field.TechnicalPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var defaultCurves = allFields
            .Where(field => TryCurveKey(field.TechnicalPath, out _))
            .GroupBy(field => CurveKey(field.TechnicalPath), StringComparer.Ordinal)
            .Where(IsDefaultCurve)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var visible = allFields
            .Where(field => field.ShowByDefault)
            .Where(field => !disabledScopes.Any(scope =>
                field.TechnicalPath.StartsWith($"{scope}/", StringComparison.Ordinal)))
            .Where(field => !TryCurveKey(field.TechnicalPath, out var curveKey)
                            || !defaultCurves.Contains(curveKey))
            .ToArray();
        var compactGroups = visible
            .Select(field => (Field: field, Key: CompactGroupKey(field.TechnicalPath)))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Field).ToArray(), StringComparer.Ordinal);
        var allCompactGroups = allFields
            .Select(field => (Field: field, Key: CompactGroupKey(field.TechnicalPath)))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Field).ToArray(), StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SnapshotCoverageFieldViewModel>(visible.Length);
        foreach (var field in visible)
        {
            var key = CompactGroupKey(field.TechnicalPath);
            if (key is null)
            {
                result.Add(field);
                continue;
            }

            if (!emitted.Add(key))
            {
                continue;
            }

            var compactFields = key.Contains("/anchors/", StringComparison.Ordinal)
                ? allCompactGroups[key]
                : compactGroups[key];
            result.Add(CreateCompactRow(key, compactFields));
        }

        return result;
    }

    public bool Matches(string search) =>
        Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || UiPath.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || TechnicalPath.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Value.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Type.Contains(search, StringComparison.OrdinalIgnoreCase);

    public static SnapshotCoverageFieldViewModel[] CreateCaptured(
        CapturedParameterField field,
        JsonElement value,
        bool cannotVerifyRestore) => Expand(field, field.NativePath, value, cannotVerifyRestore).ToArray();

    public static SnapshotCoverageFieldViewModel[] CreateUndeclared(
        string path,
        JsonElement value)
    {
        var field = new CapturedParameterField(
            path,
            "Undeclared",
            value.ValueKind.ToString(),
            false,
            false,
            "NoCoverageDeclaration",
            NativeName: path.Split('/').LastOrDefault() ?? path,
            UiPath: $"未映射原生字段/{path.Split('/').LastOrDefault() ?? path}",
            EvidenceStatus: FieldEvidenceStatus.Unknown);
        return Expand(field, path, value, true).ToArray();
    }

    private static IEnumerable<SnapshotCoverageFieldViewModel> Expand(
        CapturedParameterField field,
        string path,
        JsonElement value,
        bool isGap)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length > 0)
            {
                foreach (var property in properties)
                {
                    foreach (var child in Expand(
                                 field with { NativePath = $"{path}/{EscapePointerSegment(property.Name)}" },
                                 $"{path}/{EscapePointerSegment(property.Name)}",
                                 property.Value,
                                 isGap))
                    {
                        yield return child;
                    }
                }

                yield break;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            if (items.Length > 0)
            {
                for (var index = 0; index < items.Length; index++)
                {
                    foreach (var child in Expand(
                                 field with { NativePath = $"{path}/{index}" },
                                 $"{path}/{index}",
                                 items[index],
                                 isGap))
                    {
                        yield return child;
                    }
                }

                yield break;
            }
        }

        yield return new SnapshotCoverageFieldViewModel(
            field with { NativePath = path, ValueType = value.ValueKind.ToString() },
            FormatRawValue(value),
            isGap);
    }

    private static string EscapePointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static string FormatRawValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText()
    };

    private static string FormatDisplayValue(string value, string path)
    {
        if (path.EndsWith("/resourceFilePath", StringComparison.Ordinal))
        {
            return "已保存滤镜素材目录";
        }

        if (path.EndsWith("/preDuration", StringComparison.Ordinal)
            && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return $"{seconds:0.###} 秒";
        }

        if ((path.EndsWith("/sourceCanvasShowRate", StringComparison.Ordinal)
             || path.EndsWith("/sourceSelfShowRate", StringComparison.Ordinal)
             || path.EndsWith("/sourceTransparency", StringComparison.Ordinal))
            && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
        {
            return $"{ratio * 100:0.##}%";
        }

        if (path.EndsWith("/colorSpace", StringComparison.Ordinal))
        {
            return value switch
            {
                "0" => "默认",
                "1" => "JPEG",
                "2" => "709",
                "3" => "601",
                "4" => "FCC",
                "5" => "SMPTE 240M",
                "6" => "BT.470",
                "7" => "BT.2020",
                "8" => "BT.2100",
                _ => value
            };
        }

        if (path.EndsWith("/videoRange", StringComparison.Ordinal))
        {
            return value switch
            {
                "0" => "默认",
                "1" => "局部",
                "2" => "全部",
                _ => value
            };
        }

        if (path.EndsWith("/format", StringComparison.Ordinal))
        {
            return value switch
            {
                "0" => "未知",
                "1" => "I420",
                "2" => "YV12",
                "3" => "NV12",
                "4" => "NV21",
                "5" => "UYVY",
                "6" => "YUY2",
                "7" => "ARGB",
                "8" => "XRGB",
                "9" => "RGB24",
                "10" => "RGB32",
                "11" => "BGR24",
                "12" => "BGR32",
                "13" => "MJPEG",
                "14" => "I444",
                "15" => "DXVA2",
                "16" => "DX11",
                "17" => "I420A",
                "18" => "I422",
                "19" => "I422A",
                "20" => "YVYU",
                "21" => "YUVJ420P",
                "22" => "P010",
                "23" => "P016",
                "24" => "NV12 MS",
                "25" => "P010 MS",
                "26" => "P016 MS",
                "27" => "H.264",
                "28" => "H.265",
                "29" => "CUDA",
                _ => value
            };
        }

        if (path.Contains("/curves/", StringComparison.Ordinal)
            && path.EndsWith("/type", StringComparison.Ordinal))
        {
            return value switch
            {
                "0" => "Master / R / G / B",
                "1" => "色相对色相",
                "2" => "色相对饱和度",
                "3" => "色相对明度",
                "4" => "明度对饱和度",
                "5" => "饱和度对饱和度",
                "6" => "饱和度对明度",
                _ => value
            };
        }

        if (path.Contains("/filterDataList/", StringComparison.Ordinal)
            && path.EndsWith("/type", StringComparison.Ordinal))
        {
            return value switch
            {
                "3" => "色彩增强",
                "12" => "LUT",
                "20" => "蒙版",
                "21" => "白平衡",
                "24" => "高斯模糊",
                "26" => "镜头虚化",
                "27" => "曲线",
                "28" => "HSL",
                _ => $"原生滤镜类型 {value}"
            };
        }

        if (path.EndsWith("/selectedHueKey", StringComparison.Ordinal))
        {
            return value switch
            {
                "red" => "红色",
                "orange" => "橙色",
                "yellow" => "黄色",
                "green" => "绿色",
                "cyan" => "青色",
                "blue" => "蓝色",
                "purple" => "紫色",
                "magenta" => "品红",
                _ => value
            };
        }

        if ((path.EndsWith("/colorAdd", StringComparison.Ordinal)
             || path.EndsWith("/colorMultiply", StringComparison.Ordinal))
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var color))
        {
            return $"#{color & 0xFFFFFF:X6}";
        }

        if (path.EndsWith("/type", StringComparison.Ordinal) && value == "camera")
        {
            return "摄像头";
        }

        return FormatDisplayValue(value);
    }

    private static string FormatDisplayValue(string value)
    {
        if (value is "true" or "false" or "null")
        {
            return value switch
            {
                "true" => "已开启",
                "false" => "已关闭",
                _ => "未设置"
            };
        }

        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.###", CultureInfo.InvariantCulture)
            : value;
    }

    private static bool HasMeaningfulValue(string value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "false" or "null" or "[]" or "{}")
        {
            return false;
        }

        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return true;
        }

        return number != 0 || IsMeaningfulZeroPath(path);
    }

    private static bool IsMeaningfulZeroPath(string path) =>
        path.Contains("/anchors/", StringComparison.Ordinal)
        || path.Contains("/points/", StringComparison.Ordinal)
           && !path.EndsWith("/hueAdjust", StringComparison.Ordinal)
           && !path.EndsWith("/saturationAdjust", StringComparison.Ordinal)
           && !path.EndsWith("/lightnessAdjust", StringComparison.Ordinal)
        || path.EndsWith("/colorAdd", StringComparison.Ordinal)
        || path.EndsWith("/colorMultiply", StringComparison.Ordinal)
        || path.EndsWith("/hue", StringComparison.Ordinal)
        || path.EndsWith("/cct", StringComparison.Ordinal)
        || path.EndsWith("/tint", StringComparison.Ordinal);

    private static bool IsActivationPath(string path)
    {
        var leaf = UnescapePointerSegment(path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? path);
        return leaf.Equals("use", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("enable", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("enabled", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("isEnabled", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("active", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("isActive", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("switch", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("status", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("panelUse", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("isOn", StringComparison.OrdinalIgnoreCase)
               || leaf.Equals("using", StringComparison.OrdinalIgnoreCase)
               || leaf.StartsWith("enable", StringComparison.OrdinalIgnoreCase)
               || leaf.StartsWith("use", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisabledState(string value) => value is "false" or "0";

    private static string ParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? string.Empty : path[..index];
    }

    private static string CreatePresentationGroup(string uiPath)
    {
        var segments = uiPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        if (segments.Length == 0)
        {
            return "设置";
        }

        return segments[0] == "视频滤镜链" && segments.Length > 1
            ? segments[1]
            : segments[0];
    }

    private static bool IsTechnicalIdentifierField(string name, string path, string value)
    {
        var leaf = UnescapePointerSegment(path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? path);
        return name.Contains("内部标识", StringComparison.Ordinal)
               || name.Contains("内部参数", StringComparison.Ordinal)
               || name.Contains("配置标识", StringComparison.Ordinal)
               || name.Contains("合并标识", StringComparison.Ordinal)
               || leaf.Equals("id", StringComparison.OrdinalIgnoreCase)
               || leaf.EndsWith("Id", StringComparison.Ordinal)
               || leaf.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
               || leaf.Length >= 10 && leaf.All(char.IsAsciiDigit)
               || path.Contains("/carnivalInfo/", StringComparison.Ordinal)
               || path.Contains("/groupEffectIds/", StringComparison.Ordinal)
               || Guid.TryParse(value, out _)
               || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || value.Length >= 16 && value.All(char.IsAsciiDigit);
    }

    private static bool IsUnchangedStructuralField(string name, string path, string value)
    {
        var leaf = UnescapePointerSegment(path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? path);
        if (path.Contains("/filterDataList/", StringComparison.Ordinal))
        {
            if (leaf is "name" or "type" or "selectedHueKey" or "curvesType")
            {
                return true;
            }

            if (path.Contains("/points/", StringComparison.Ordinal) && leaf == "hue")
            {
                return true;
            }

            if (path.Contains("/curves/", StringComparison.Ordinal) && leaf is "enable" or "type")
            {
                return true;
            }

            if (leaf == "colorMultiply" && value == "16777215")
            {
                return true;
            }
        }

        return path.Contains("filterStore", StringComparison.Ordinal)
               && (leaf is "name" or "useFilterSuppor" or "useNewFilter" or "showRedDot" or "resourceFilePath");
    }

    private static bool IsEffectRuntimeInfrastructureField(string path) =>
        path.Contains("effectStore.json:/effectStore/giftPlayConfig/", StringComparison.Ordinal)
        || path.Contains("effectStore.json:/effectStore/carnivalInfo/", StringComparison.Ordinal)
        || path.Contains("effectStore.json:/effectStore/shownTips/", StringComparison.Ordinal)
        || path.Contains("effectStore.json:/effectStore/faceSources/", StringComparison.Ordinal)
        || path.EndsWith("effectStore.json:/effectStore/entityStamp", StringComparison.Ordinal)
        || path.EndsWith("effectStore.json:/effectStore/interactSourceId", StringComparison.Ordinal)
        || path.EndsWith("effectStore.json:/effectStore/jsbLynxEffectStr", StringComparison.Ordinal)
        || path.EndsWith("effectStore.json:/effectStore/lynxEffectStr", StringComparison.Ordinal)
        || path.EndsWith("effectStore.json:/effectStore/togetherStr", StringComparison.Ordinal)
        || path.EndsWith("effectStore.json:/effectStore/togetherSlotCount", StringComparison.Ordinal)
        || path.EndsWith("effectStore.json:/effectStore/showChat", StringComparison.Ordinal);

    private static string? CompactGroupKey(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            if (segments[index] is "anchors" or "points")
            {
                return string.Join('/', segments.Take(index + 2));
            }
        }

        return null;
    }

    private static SnapshotCoverageFieldViewModel CreateCompactRow(
        string key,
        SnapshotCoverageFieldViewModel[] fields)
    {
        var first = fields[0];
        if (key.Contains("/points/", StringComparison.Ordinal))
        {
            var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var pointIndex = Array.FindIndex(segments, segment => segment == "points");
            var colorName = pointIndex >= 0
                            && pointIndex + 1 < segments.Length
                            && int.TryParse(
                                segments[pointIndex + 1],
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var index)
                ? HuePointName(index)
                : "当前色相";
            var baseLabel = string.Join(" · ", first.UiPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .SkipLast(1));
            var value = string.Join(" · ", fields.Select(field =>
                $"{HslMetricName(field.UiPath.Split('/').Last())} {ShortNumber(field.DisplayValue)}"));
            return new SnapshotCoverageFieldViewModel(
                first,
                $"{baseLabel} · {colorName}",
                value,
                key);
        }

        var labelSegments = first.UiPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        var pointLabelIndex = Array.FindIndex(labelSegments, segment => segment.StartsWith("控制点 ", StringComparison.Ordinal));
        var label = pointLabelIndex >= 0
            ? string.Join(" · ", labelSegments.Take(pointLabelIndex + 1))
            : string.Join(" · ", labelSegments.SkipLast(2));
        label = label.Replace(" · 曲线 · 曲线 · ", " · 曲线 · ", StringComparison.Ordinal);
        var parts = new List<string>(3);
        AddCoordinatePart(parts, "位置", fields);
        AddCoordinatePart(parts, "左控制柄", fields);
        AddCoordinatePart(parts, "右控制柄", fields);
        return new SnapshotCoverageFieldViewModel(
            first,
            label,
            string.Join(" · ", parts),
            key);
    }

    private static string HslMetricName(string name) => name switch
    {
        "色相调整" => "色相",
        "饱和度调整" => "饱和度",
        "明度调整" => "明度",
        _ => name
    };

    private static void AddCoordinatePart(
        List<string> destination,
        string partName,
        SnapshotCoverageFieldViewModel[] fields)
    {
        var x = fields.FirstOrDefault(field => IsCoordinate(field.UiPath, partName, "横坐标"));
        var y = fields.FirstOrDefault(field => IsCoordinate(field.UiPath, partName, "纵坐标"));
        if (x is not null || y is not null)
        {
            destination.Add($"{partName} {ShortNumber(x?.DisplayValue ?? "—")} / {ShortNumber(y?.DisplayValue ?? "—")}");
        }
    }

    private static bool IsCoordinate(string uiPath, string partName, string axis) =>
        uiPath.EndsWith($"/{partName}/{axis}", StringComparison.Ordinal)
        || uiPath.EndsWith($"/{partName} · {axis}", StringComparison.Ordinal);

    private static string ShortNumber(string value) => decimal.TryParse(
        value,
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out var number)
        ? number.ToString("0.###", CultureInfo.InvariantCulture)
        : value;

    private static string HuePointName(int index) => index switch
    {
        0 => "红色",
        1 => "橙色",
        2 => "黄色",
        3 => "绿色",
        4 => "青色",
        5 => "蓝色",
        6 => "紫色",
        7 => "品红",
        _ => $"第 {index + 1} 个色相"
    };

    private static string CurveKey(string path)
    {
        TryCurveKey(path, out var key);
        return key;
    }

    private static bool TryCurveKey(string path, out string key)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            if (segments[index] == "curves")
            {
                key = string.Join('/', segments.Take(index + 2));
                return true;
            }
        }

        key = string.Empty;
        return false;
    }

    private static bool IsDefaultCurve(IGrouping<string, SnapshotCoverageFieldViewModel> curve)
    {
        var typeField = curve.FirstOrDefault(field => field.TechnicalPath.EndsWith("/type", StringComparison.Ordinal));
        if (typeField is null
            || !int.TryParse(typeField.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
        {
            return false;
        }

        var anchors = curve
            .Where(field => field.TechnicalPath.Contains("/anchors/", StringComparison.Ordinal))
            .GroupBy(field => CompactGroupKey(field.TechnicalPath), StringComparer.Ordinal)
            .Where(group => group.Key is not null)
            .Select(group => group.ToArray())
            .ToArray();
        if (anchors.Length == 0)
        {
            return true;
        }

        if (type is >= 4 and <= 6)
        {
            return anchors.Length == 2
                   && HasPosition(anchors[0], 0m, 0m)
                   && HasPosition(anchors[1], 1m, 0m)
                   && ControlCoordinatesAre(anchors, 0.5m, 0m);
        }

        return type == 0
               && anchors.Length == 2
               && HasPosition(anchors[0], 0m, 0m)
               && HasPosition(anchors[1], 1m, 1m)
               && ControlCoordinatesAre(anchors, 0.5m, 0.5m);
    }

    private static bool HasPosition(
        IReadOnlyList<SnapshotCoverageFieldViewModel> fields,
        decimal expectedX,
        decimal expectedY) => HasCoordinate(fields, "/position/x", expectedX)
                                      && HasCoordinate(fields, "/position/y", expectedY);

    private static bool ControlCoordinatesAre(
        IEnumerable<IReadOnlyList<SnapshotCoverageFieldViewModel>> anchors,
        decimal expectedX,
        decimal expectedY)
    {
        var controls = anchors.SelectMany(fields => fields)
            .Where(field => field.TechnicalPath.Contains("Control/", StringComparison.Ordinal))
            .ToArray();
        return controls.Length > 0 && controls.All(field => field.TechnicalPath.EndsWith("/x", StringComparison.Ordinal)
            ? ValueEquals(field.Value, expectedX)
            : ValueEquals(field.Value, expectedY));
    }

    private static bool HasCoordinate(
        IEnumerable<SnapshotCoverageFieldViewModel> fields,
        string suffix,
        decimal expected) => fields.Any(field => field.TechnicalPath.EndsWith(suffix, StringComparison.Ordinal)
                                                 && ValueEquals(field.Value, expected));

    private static bool ValueEquals(string value, decimal expected) => decimal.TryParse(
        value,
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out var actual) && actual == expected;

    private static string FieldName(string category, string path)
    {
        var segments = path.Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries);
        var leaf = UnescapePointerSegment(segments.LastOrDefault() ?? path);
        var name = category switch
        {
            "DeviceSelection" or "VideoSource" => leaf,
            "Width" => "分辨率宽度",
            "Height" => "分辨率高度",
            "FramesPerSecond" => "帧率",
            "PixelFormat" => "像素格式",
            "ColorSpace" => "色彩空间",
            "ColorRange" => "色彩范围",
            "FilterType" => "滤镜类型",
            "FilterEnabled" => "滤镜启用状态",
            "FilterOrder" => "滤镜顺序",
            "FilterSetting" or "VideoFilter" or "Filter" => leaf,
            "FilterAsset" => "滤镜素材",
            "Undeclared" => $"未声明字段 · {leaf}",
            _ => leaf
        };
        if (segments.Length >= 2
            && int.TryParse(segments[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var pointIndex))
        {
            return $"{name} · 第 {pointIndex + 1} 个控制点";
        }

        return int.TryParse(leaf, NumberStyles.None, CultureInfo.InvariantCulture, out var itemIndex)
            ? $"第 {itemIndex + 1} 项"
            : name;
    }

    private static string UnescapePointerSegment(string value) => value.Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);

    private static string VerificationName(string verification) => verification switch
    {
        "ObsWebSocketReadback" => "OBS WebSocket 回读",
        "SignedAdapterReadback" => "签名适配器回读",
        "NativeFieldReadback" => "原生字段回读",
        "DiscoveryReadOnly" => "探测读取（不可恢复）",
        "LegacySchemaReadOnly" => "历史存档（覆盖范围未证明）",
        _ => "没有覆盖声明"
    };
}

public sealed partial class SnapshotSourceViewModel : ObservableObject
{
    private const int FilterPageSize = 10;
    private int filterPageIndex;

    public SnapshotSourceViewModel(VideoSource source)
    {
        Name = source.Name;
        SourceKind = SnapshotChineseDisplay.SourceKind(source.Kind);
        TechnicalSourceKind = source.Kind;
        DeviceName = source.Device?.FriendlyName ?? "未记录采集设备";
        DeviceIdentity = FormatDeviceIdentity(source.Device);
        Resolution = source.Mode is null ? "未记录" : $"{source.Mode.Width} × {source.Mode.Height}";
        FrameRate = source.Mode is null
            ? "未记录"
            : $"{source.Mode.FramesPerSecondNumerator / (double)source.Mode.FramesPerSecondDenominator:0.##} FPS";
        PixelFormat = EmptyAsUnknown(source.Mode?.PixelFormat);
        ColorSpace = EmptyAsUnknown(source.Mode?.ColorSpace);
        ColorRange = EmptyAsUnknown(source.Mode?.ColorRange);
        FormatSummary = source.Mode is null
            ? "未记录"
            : string.Join(" · ", new[] { PixelFormat, ColorSpace, ColorRange });
        VideoSummary = string.Join(" · ", new[]
            {
                SourceKind,
                DeviceName == "未记录采集设备" ? null : DeviceName,
                Resolution == "未记录" ? null : Resolution,
                FrameRate == "未记录" ? null : FrameRate,
                FormatSummary == "未记录" ? null : FormatSummary
            }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        AllSettings = CreateSettings(source.Settings);
        Settings = CreateChangedSettings(source.Settings, source.DefaultSettings);
        SettingsTitle = Settings.Count == 0
            ? "当前设置与 OBS 默认值一致"
            : $"当前有效或非默认设置（{Settings.Count} 项）";
        AllSettingsTitle = $"完整来源设置（{AllSettings.Count} 项）";
        Filters = source.Filters
            .Where(filter => filter.Enabled)
            .OrderBy(filter => filter.Order)
            .Select(filter => new SnapshotFilterViewModel(filter))
            .ToArray();
        FilterSummary = Filters.Count == 0 ? "没有视频滤镜" : $"{Filters.Count} 个视频滤镜，按恢复顺序排列";
        RefreshFilterPage();
    }

    public string Name { get; }

    public string SourceKind { get; }

    public string TechnicalSourceKind { get; }

    public string DeviceName { get; }

    public string DeviceIdentity { get; }

    public string Resolution { get; }

    public string FrameRate { get; }

    public string PixelFormat { get; }

    public string ColorSpace { get; }

    public string ColorRange { get; }

    public string FormatSummary { get; }

    public string VideoSummary { get; }

    public string FilterSummary { get; }

    public IReadOnlyList<SnapshotSettingViewModel> Settings { get; }

    public IReadOnlyList<SnapshotSettingViewModel> AllSettings { get; }

    public string SettingsTitle { get; }

    public string AllSettingsTitle { get; }

    public IReadOnlyList<SnapshotFilterViewModel> Filters { get; }

    [ObservableProperty]
    public partial IReadOnlyList<SnapshotFilterViewModel> VisibleFilters { get; private set; } = [];

    [ObservableProperty]
    public partial string FilterPageSummary { get; private set; } = string.Empty;

    public bool HasSettings => Settings.Count > 0;

    public bool HasMultipleFilterPages => Filters.Count > FilterPageSize;

    [RelayCommand(CanExecute = nameof(CanGoToPreviousFilterPage))]
    private void PreviousFilterPage()
    {
        filterPageIndex--;
        RefreshFilterPage();
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextFilterPage))]
    private void NextFilterPage()
    {
        filterPageIndex++;
        RefreshFilterPage();
    }

    private bool CanGoToPreviousFilterPage() => filterPageIndex > 0;

    private bool CanGoToNextFilterPage() => (filterPageIndex + 1) * FilterPageSize < Filters.Count;

    private void RefreshFilterPage()
    {
        var offset = filterPageIndex * FilterPageSize;
        VisibleFilters = Filters.Skip(offset).Take(FilterPageSize).ToArray();
        FilterPageSummary = Filters.Count == 0
            ? "0 项"
            : $"第 {offset + 1}–{offset + VisibleFilters.Count} 项，共 {Filters.Count} 项";
        PreviousFilterPageCommand.NotifyCanExecuteChanged();
        NextFilterPageCommand.NotifyCanExecuteChanged();
    }

    internal static IReadOnlyList<SnapshotSettingViewModel> CreateSettings(
        IReadOnlyDictionary<string, JsonElement> settings) => settings
        .OrderBy(setting => setting.Key, StringComparer.Ordinal)
        .SelectMany(setting => ExpandSetting(setting.Key, setting.Value, setting.Key))
        .ToArray();

    private static IReadOnlyList<SnapshotSettingViewModel> CreateChangedSettings(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyDictionary<string, JsonElement>? defaults)
    {
        if (defaults is null || defaults.Count == 0)
        {
            return CreatePresentationSettings(settings
                .Where(setting => !IsTechnicalIdentifier(setting.Key))
                .ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal));
        }

        var changed = settings
            .Where(setting => !IsTechnicalIdentifier(setting.Key))
            .Where(setting => !defaults.TryGetValue(setting.Key, out var defaultValue)
                              || !JsonElement.DeepEquals(setting.Value, defaultValue))
            .ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal);
        return CreatePresentationSettings(changed);
    }

    internal static IReadOnlyList<SnapshotSettingViewModel> CreatePresentationSettings(
        IReadOnlyDictionary<string, JsonElement> settings) => settings
        .OrderBy(setting => setting.Key, StringComparer.Ordinal)
        .SelectMany(setting => ExpandPresentationSetting(setting.Key, setting.Value, setting.Key))
        .ToArray();

    private static bool IsTechnicalIdentifier(string name) =>
        name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Id", StringComparison.Ordinal)
        || name.Contains("guid", StringComparison.OrdinalIgnoreCase)
        || name.Contains("uuid", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<SnapshotSettingViewModel> ExpandSetting(
        string nativeName,
        JsonElement value,
        string technicalPath)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length > 0)
            {
                foreach (var property in properties)
                {
                    foreach (var field in ExpandSetting(
                                 property.Name,
                                 property.Value,
                                 $"{technicalPath}/{property.Name}"))
                    {
                        yield return field;
                    }
                }

                yield break;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                foreach (var field in ExpandSetting(
                             index.ToString(CultureInfo.InvariantCulture),
                             item,
                             $"{technicalPath}/{index}"))
                {
                    yield return field;
                }

                index++;
            }

            if (index > 0)
            {
                yield break;
            }
        }

        yield return new SnapshotSettingViewModel(
            SnapshotChineseDisplay.SettingName(nativeName),
            FormatNativeValue(value),
            technicalPath);
    }

    private static IEnumerable<SnapshotSettingViewModel> ExpandPresentationSetting(
        string nativeName,
        JsonElement value,
        string technicalPath)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            var state = properties.FirstOrDefault(property => IsActivationName(property.Name));
            if (!string.IsNullOrEmpty(state.Name) && IsDisabledValue(state.Value))
            {
                yield break;
            }

            foreach (var property in properties.Where(property => !IsActivationName(property.Name)))
            {
                foreach (var field in ExpandPresentationSetting(
                             property.Name,
                             property.Value,
                             $"{technicalPath}/{property.Name}"))
                {
                    yield return field;
                }
            }

            yield break;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                foreach (var field in ExpandPresentationSetting(
                             index.ToString(CultureInfo.InvariantCulture),
                             item,
                             $"{technicalPath}/{index}"))
                {
                    yield return field;
                }

                index++;
            }

            yield break;
        }

        if (IsActivationName(nativeName) || IsDisabledValue(value) || value.ValueKind == JsonValueKind.Null)
        {
            yield break;
        }

        yield return new SnapshotSettingViewModel(
            SnapshotChineseDisplay.SettingName(nativeName),
            value.ValueKind == JsonValueKind.True
                ? string.Empty
                : SnapshotChineseDisplay.SettingValue(nativeName, value),
            technicalPath);
    }

    private static bool IsActivationName(string name) =>
        name.Equals("use", StringComparison.OrdinalIgnoreCase)
        || name.Equals("enable", StringComparison.OrdinalIgnoreCase)
        || name.Equals("enabled", StringComparison.OrdinalIgnoreCase)
        || name.Equals("active", StringComparison.OrdinalIgnoreCase)
        || name.Equals("isOn", StringComparison.OrdinalIgnoreCase)
        || name.Equals("status", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("enable", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("use", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisabledValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.False => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString())
                                || value.GetString() is "false" or "off",
        _ => false
    };

    private static string FormatNativeValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "开启",
        JsonValueKind.False => "关闭",
        JsonValueKind.Null => "空值",
        _ => value.GetRawText()
    };

    private static string FormatDeviceIdentity(CaptureDeviceDescriptor? device)
    {
        if (device is null)
        {
            return "没有设备标识";
        }

        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(device.VendorId))
        {
            values.Add($"VID {device.VendorId}");
        }

        if (!string.IsNullOrWhiteSpace(device.ProductId))
        {
            values.Add($"PID {device.ProductId}");
        }

        if (!string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            values.Add($"序列号 {device.SerialNumber}");
        }

        return values.Count == 0 ? "没有可移植设备标识" : string.Join(" · ", values);
    }

    private static string EmptyAsUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? "未记录" : value;
}

public sealed class SnapshotFilterViewModel
{
    public SnapshotFilterViewModel(VideoFilter filter)
    {
        Name = filter.Name;
        Kind = SnapshotChineseDisplay.FilterKind(filter.Kind);
        KindDetail = string.Equals(Name, Kind, StringComparison.OrdinalIgnoreCase) ? string.Empty : Kind;
        TechnicalKind = filter.Kind;
        Order = $"第 {filter.Order + 1} 个";
        State = filter.Enabled ? "已启用" : "已停用";
        Settings = SnapshotSourceViewModel.CreatePresentationSettings(filter.Settings);
        SettingsTitle = $"滤镜参数（{Settings.Count} 项）";
        Assets = filter.Assets.Select(asset => new SnapshotAssetViewModel(asset)).ToArray();
        SettingsSummary = Settings.Count == 0
            ? string.Empty
            : string.Join(" · ", Settings.Select(setting => setting.Summary));
        AssetsSummary = Assets.Count == 0
            ? string.Empty
            : $"素材 {string.Join("、", Assets.Select(asset => asset.Name))}";
        Summary = Settings.Count == 0 ? Order : $"{Order} · {Settings.Count} 项参数";
    }

    public string Name { get; }

    public string Kind { get; }

    public string KindDetail { get; }

    public bool HasKindDetail => !string.IsNullOrWhiteSpace(KindDetail);

    public string TechnicalKind { get; }

    public string Order { get; }

    public string State { get; }

    public string Summary { get; }

    public IReadOnlyList<SnapshotSettingViewModel> Settings { get; }

    public string SettingsTitle { get; }

    public string SettingsSummary { get; }

    public string AssetsSummary { get; }

    public IReadOnlyList<SnapshotAssetViewModel> Assets { get; }

    public bool HasSettings => Settings.Count > 0;

    public bool HasAssets => Assets.Count > 0;

}

public sealed class SnapshotAssetViewModel
{
    public SnapshotAssetViewModel(AssetBinding asset)
    {
        Name = asset.OriginalFileName;
        Detail = $"SHA-256 {asset.BlobSha256[..Math.Min(12, asset.BlobSha256.Length)]}…";
    }

    public string Name { get; }

    public string Detail { get; }

}

public sealed record SnapshotSettingViewModel(string Name, string Value, string TechnicalName)
{
    public string Status { get; } = "已读取";

    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    public string Summary => HasValue ? $"{Name} {Value}" : Name;
}

internal static class SnapshotChineseDisplay
{
    // 这些名称来自直播伴侣 12.8.1.454484231 自带 loki.zip/data.json。
    // 必须同时匹配资源 ID 与滑块标签；它们只用于显示，不能提升证据等级或开放写入。
    private static readonly Dictionary<string, string> LiveCompanionOfficialParameterNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["6938367737173381646|BigEye"] = "大眼",
            ["6938367827954897439|Clear_ALL"] = "清晰",
            ["6938368504076702239|Internal_Deform_CutFace"] = "窄脸",
            ["6938369649906029070|Internal_Deform_Face"] = "小脸",
            ["6938369757032747527|Internal_Deform_Zoom_Cheekbone"] = "瘦颧骨",
            ["6938369845566116383|Internal_Deform_Zoom_Jawbone"] = "瘦下颌骨",
            ["6938369911022424612|Internal_Deform_Nose"] = "瘦鼻",
            ["6938369999706788360|Internal_Deform_MovNose"] = "长鼻",
            ["6938370064194212389|Mouth_ALL"] = "嘴形",
            ["6938370126815171108|Internal_Deform_Chin"] = "下巴",
            ["6938370225481978405|Internal_Deform_Forehead"] = "额头",
            ["6938370384030863908|removePouch"] = "黑眼圈",
            ["6938370510002590222|removeNasolabialFolds"] = "法令纹",
            ["7108615070787047973|Foundation_ALL"] = "美白",
            ["7182122554402804281|Face_all"] = "自然脸",
            ["7182123383063056933|Smooth_ALL"] = "磨皮",
            ["7321632612093530651|Teeth_ALL"] = "白牙",
            ["7322286931461542409|SmallHead"] = "小头",
            ["7322287526155129382|SlimWaist"] = "瘦腰",
            ["7322287672976740874|SlimHip"] = "美胯",
            ["7322287756384670245|SlimArm"] = "瘦手臂",
            ["7322287820700127754|SwanNeck"] = "天鹅颈",
            ["7322287893517439497|SlimBody"] = "瘦身",
            ["7322287963704922651|WidenShoulder"] = "肩宽",
            ["7322288040540377651|OrthoShoulder"] = "直角肩",
            ["7369406873918771749|Internal_Deform_MovMouth"] = "人中",
            ["7369407055687324211|Internal_Deform_MoveEye"] = "眼睛上下",
            ["7369407133349057075|Internal_Eye_Spacing"] = "眼睛距离",
            ["7369407217197388297|fd_lower_eyelid"] = "眼睑下至",
            ["7369407491056079387|fd_pupil"] = "瞳孔大小",
            ["7369407562321498651|fd_nose_root"] = "山根",
            ["7369407704319660571|fd_nose_size"] = "鼻翼",
            ["7369407776868536841|fd_nose_bridge_v6"] = "鼻梁",
            ["7369407978341929498|fd_brow_size_v6"] = "眉毛粗细",
            ["7369408148454511155|fd_brow_position_v6"] = "眉毛高低",
            ["7372079818403222043|fd_inner_corner_v6"] = "内眼角",
            ["7372079895343534630|fd_outer_corner_inout_v6"] = "外眼角",
            ["7372459465762673161|fd_mouse_corner_under_lip_line_v6"] = "微笑唇",
            ["7532038335389241883|Face_all_live_left"] = "瘦左脸",
            ["7532038401877348902|Face_all_live_right"] = "瘦右脸",
            ["7532038463160324654|BigEye_live_left"] = "左眼",
            ["7532038535994413595|BigEye_live_right"] = "右眼",
            ["7548795774864200219|smaller_head_v8"] = "头包脸",
            ["7633298414997869106|fd_proportion_upper_atrium_v6"] = "上庭",
            ["7633298500486173211|banlv_zhongting_v8"] = "中庭",
            ["7633298570791096859|fd_proportion_lower_atrium_v6"] = "下庭",
            ["7633298774382613019|banlv_etougaodu_v8"] = "额高",
            ["7633298858268693001|banlv_etoukuandu_v8"] = "额宽",
            ["7633298925012652582|banlv_taiyangxue_v8"] = "太阳穴",
            ["7633304109646352906|fd_brow_distance_v6"] = "眉距",
            ["7633628054253736498|Foundation_ALL"] = "基础",
            ["7633628816044200498|Smooth_ALL"] = "磨皮",
            ["7633629244530102810|Clear_ALL"] = "清晰",
            ["7633629577125827110|removePouch"] = "黑眼圈",
            ["7633629696499913267|removeNasolabialFolds"] = "法令纹",
            ["7636682380756914714|banlv_mchun_v8"] = "M唇",
            ["7639369841605874202|banlv_jianxiaba_v8"] = "整体"
        };

    public static string? LiveCompanionOfficialParameterName(string technicalPath)
    {
        var segments = technicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal))
            .ToArray();
        var itemIndex = Array.IndexOf(segments, "items");
        var sliderIndex = Array.IndexOf(segments, "sliders");
        if (itemIndex < 0 || itemIndex + 1 >= segments.Length
            || sliderIndex < 0 || sliderIndex + 1 >= segments.Length)
        {
            return null;
        }

        return LiveCompanionOfficialParameterNames.TryGetValue(
            $"{segments[itemIndex + 1]}|{segments[sliderIndex + 1]}",
            out var name)
            ? name
            : null;
    }

    public static string SourceKind(string kind) => kind switch
    {
        "monitor_capture" => "显示器采集",
        "window_capture" => "窗口采集",
        "game_capture" => "游戏采集",
        "dshow_input" => "视频采集设备",
        "image_source" => "图片来源",
        "browser_source" => "浏览器来源",
        "ffmpeg_source" => "媒体来源",
        _ when kind.Contains("ndi", StringComparison.OrdinalIgnoreCase) => "NDI 视频来源",
        _ => "其他视频来源"
    };

    public static string FilterKind(string kind) => kind switch
    {
        "clut_filter" => "LUT",
        "luma_key_filter_v2" or "luma_key_filter" => "亮度键",
        "color_key_filter_v2" or "color_key_filter" => "色值键",
        "chroma_key_filter_v2" or "chroma_key_filter" => "色度键",
        "color_filter_v2" or "color_filter" => "色彩校正",
        "sharpness_filter_v2" or "sharpness_filter" => "锐化",
        "crop_filter" => "裁剪与填充",
        "scale_filter" => "缩放与宽高比",
        "mask_filter" => "图像遮罩与混合",
        "scroll_filter" => "滚动",
        "render_delay_filter" or "async_delay_filter" => "视频延迟",
        _ => "其他视频滤镜"
    };

    public static string SettingName(string name) => name switch
    {
        "clut_amount" => "LUT 强度",
        "brightness" => "亮度",
        "contrast" => "对比度",
        "gamma" => "伽马",
        "hue_shift" => "色相偏移",
        "opacity" => "不透明度",
        "saturation" => "饱和度",
        "similarity" => "相似度",
        "smoothness" => "平滑度",
        "spill_reduction" => "溢色抑制",
        "luma_max" => "亮度上限",
        "luma_min" => "亮度下限",
        "luma_smooth" => "亮度平滑",
        "luma_max_smooth" => "亮度上限平滑",
        "luma_min_smooth" => "亮度下限平滑",
        "key_color" => "键颜色",
        "key_color_type" => "键颜色类型",
        "color_add" => "叠加颜色",
        "color_multiply" => "乘算颜色",
        "passthrough_alpha" => "保留 Alpha 通道",
        "sharpness" => "锐化强度",
        "strength" => "强度",
        "horizontal_flip" or "horizontalFlip" or "flip_horizontal" => "水平翻转",
        "vertical_flip" or "verticalFlip" or "flip_vertical" => "垂直翻转",
        "mirror" or "mirrored" => "镜像",
        "enabled" or "enable" => "启用状态",
        "width" => "宽度",
        "height" => "高度",
        "fps_num" => "帧率分子",
        "fps_den" => "帧率分母",
        "color_space" => "色彩空间",
        "color_range" => "色彩范围",
        "pixel_format" => "像素格式",
        "image_path" or "clut_path" => "LUT 文件",
        _ when int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) => $"第 {index + 1} 项",
        _ => "其他参数"
    };

    public static string SettingValue(string name, JsonElement value)
    {
        if (name is "key_color" or "color_add" or "color_multiply"
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var color))
        {
            return $"#{color & 0xFFFFFF:X6}";
        }

        if (name == "key_color_type" && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() switch
            {
                "green" => "绿色",
                "blue" => "蓝色",
                "magenta" => "品红",
                "custom" => "自定义",
                var current => current ?? string.Empty
            };
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "开启",
            JsonValueKind.False => "关闭",
            JsonValueKind.Null => "空值",
            _ => value.GetRawText()
        };
    }

    public static string NativeItemName(string name) => name switch
    {
        _ when name.StartsWith("内部参数 ", StringComparison.Ordinal) => "待确认名称",
        "payload" => "视频参数",
        "effect1" => "画面效果",
        "filterData" => "滤镜设置",
        "carnivalInfo" => "效果状态",
        "deviceFacing" => "摄像头方向",
        _ => name.All(character => character <= 127) ? "其他配置项" : name
    };

    public static string NativeValue(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return SnapshotNativeFieldViewModel.FormatDisplayValue(value);
        }

        var text = value.GetString() ?? string.Empty;
        return (name, text) switch
        {
            ("type", "camera") => "摄像头",
            ("selectedHueKey", "red") => "红色",
            ("selectedHueKey", "orange") => "橙色",
            ("selectedHueKey", "yellow") => "黄色",
            ("selectedHueKey", "green") => "绿色",
            ("selectedHueKey", "cyan") => "青色",
            ("selectedHueKey", "blue") => "蓝色",
            ("selectedHueKey", "purple") => "紫色",
            _ => text
        };
    }
}
