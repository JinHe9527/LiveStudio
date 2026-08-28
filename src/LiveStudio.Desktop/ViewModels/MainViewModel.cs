using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Desktop.Services;
using LiveStudio.Packaging;
using Avalonia.Media.Imaging;

namespace LiveStudio.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly LocalAgentClient localAgentClient;
    private readonly DesktopCloudClient cloudClient;
    private readonly ISystemBrowser systemBrowser;
    private readonly SnapshotFileStore snapshotFileStore;
    private readonly ApplicationUpdateService applicationUpdateService;
    private readonly CameraProfileStore cameraProfileStore;
    private readonly ISonyCameraDetectionService sonyCameraDetectionService;
    private readonly bool isDemoMode;
    private readonly bool supportsLocalAgent;
    private IReadOnlyList<LocalMappingTargetItemViewModel> allMappingTargets = [];
    private IReadOnlyList<LocalOperationSummary> localOperations = [];
    private IReadOnlyList<JobSummary> cloudJobs = [];
    private IReadOnlyList<LocalSnapshotItemViewModel> localSnapshotItems = [];
    private IReadOnlyList<LocalSnapshotItemViewModel> desktopSnapshotItems = [];
    private IReadOnlyList<LocalSnapshotItemViewModel> cloudSnapshotItems = [];
    private DesktopCloudCredentials? cloudCredentials;
    private NativeExportReport? nativeExportBaseline;
    private CancellationTokenSource? snapshotDetailCancellation;
    private bool isLoadingCloudWorkspace;
    private bool isRefreshingSnapshotNavigation;
    private ApplicationUpdateRelease? availableUpdate;
    private Guid[] failedBatchCaptureRoomIds = [];
    private bool batchSelectionInitialized;
    private bool skipNextMappingPreparation;

    public MainViewModel()
        : this(
            new LocalAgentClient(),
            new DesktopCloudClient(new DesktopCredentialStore()),
            new SystemBrowser(),
            CreateSnapshotFileStore(),
            new ApplicationUpdateService(),
            false)
    {
    }

    public MainViewModel(bool isDemoMode)
        : this(
            new LocalAgentClient(),
            new DesktopCloudClient(new DesktopCredentialStore()),
            new SystemBrowser(),
            CreateSnapshotFileStore(),
            new ApplicationUpdateService(),
            isDemoMode)
    {
    }

    public MainViewModel(
        LocalAgentClient localAgentClient,
        DesktopCloudClient cloudClient,
        ISystemBrowser systemBrowser,
        SnapshotFileStore snapshotFileStore,
        ApplicationUpdateService applicationUpdateService,
        bool isDemoMode = false,
        CameraProfileStore? cameraProfileStore = null,
        ISonyCameraDetectionService? sonyCameraDetectionService = null)
    {
        this.localAgentClient = localAgentClient;
        this.cloudClient = cloudClient;
        this.systemBrowser = systemBrowser;
        this.snapshotFileStore = snapshotFileStore;
        this.applicationUpdateService = applicationUpdateService;
        this.cameraProfileStore = cameraProfileStore ?? new CameraProfileStore();
        this.sonyCameraDetectionService = sonyCameraDetectionService ?? new SonyCameraDetectionService();
        this.isDemoMode = isDemoMode;
        supportsLocalAgent = OperatingSystem.IsWindows() || isDemoMode;
        ThemeModes =
        [
            new ThemeModeOptionViewModel(ThemePreferenceService.SystemMode, "跟随系统"),
            new ThemeModeOptionViewModel(ThemePreferenceService.LightMode, "浅色"),
            new ThemeModeOptionViewModel(ThemePreferenceService.DarkMode, "深色")
        ];
        CameraCreativeLooks =
        [
            new("ST", "标准"),
            new("PT", "人像"),
            new("NT", "中性"),
            new("VV", "鲜艳"),
            new("VV2", "鲜艳 2"),
            new("FL", "胶片"),
            new("IN", "即时"),
            new("SH", "柔和高调"),
            new("BW", "黑白"),
            new("SE", "棕褐色")
        ];
        CameraStations =
        [
            new CameraStationEditorViewModel(0, "主机", CameraCreativeLooks),
            new CameraStationEditorViewModel(1, "游机", CameraCreativeLooks),
            new CameraStationEditorViewModel(2, "侧机", CameraCreativeLooks)
        ];
        SelectedCameraCreativeLook = CameraCreativeLooks[0];
        initializingThemePreference = true;
        SelectedThemeMode = ThemeModes.First(mode => mode.Key == ThemePreferenceService.LoadMode());
        initializingThemePreference = false;
        CurrentVersionText = applicationUpdateService.CurrentVersionText;
        SelectedSection = 1;
        ApplyDisconnectedState();
        RefreshCloudCertificateStatus();
    }

    public event EventHandler? UpdateRestartRequested;

    private bool initializingThemePreference;

    public IReadOnlyList<ThemeModeOptionViewModel> ThemeModes { get; }

    public IReadOnlyList<CameraCreativeLookOptionViewModel> CameraCreativeLooks { get; }

    public IReadOnlyList<CameraStationEditorViewModel> CameraStations { get; }

    [ObservableProperty]
    public partial ThemeModeOptionViewModel? SelectedThemeMode { get; set; }

    partial void OnSelectedThemeModeChanged(ThemeModeOptionViewModel? value)
    {
        if (!initializingThemePreference && value is not null)
        {
            ThemePreferenceService.Apply(value.Key);
        }
    }

    partial void OnSelectedCameraProfileChanged(CameraProfileItemViewModel? value)
    {
        IsCameraDeleteConfirmationVisible = false;
        if (value is null)
        {
            return;
        }

        var profile = value.Profile;
        IsManualCameraMode = profile.Mode == CameraProfileMode.Manual;
        CameraProfileName = profile.Name;
        CameraAperture = profile.Aperture;
        CameraShutterSpeed = profile.ShutterSpeed;
        CameraIso = profile.Iso;
        SelectedCameraCreativeLook = CameraCreativeLooks.FirstOrDefault(
            option => string.Equals(option.Code, profile.CreativeLook, StringComparison.OrdinalIgnoreCase))
            ?? CameraCreativeLooks[0];
        ApplyCameraCreativeLookSettings(profile.EffectiveCreativeLookSettings);
        CameraProfileMessage = $"已选择“{profile.Name}”";
    }

    partial void OnIsManualCameraModeChanged(bool value)
    {
        IsCameraDeleteConfirmationVisible = false;
        CameraProfileMessage = value
            ? "手动档案只记录光圈、快门、ISO 和创意外观。"
            : "USB 模式将使用相机原生设置文件；检测到相机后才允许读取或恢复。";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsControlVisible))]
    [NotifyPropertyChangedFor(nameof(IsSnapshotsVisible))]
    [NotifyPropertyChangedFor(nameof(IsCloudWorkspaceVisible))]
    [NotifyPropertyChangedFor(nameof(IsCloudSetupVisible))]
    [NotifyPropertyChangedFor(nameof(IsActivityVisible))]
    [NotifyPropertyChangedFor(nameof(IsSettingsVisible))]
    public partial int SelectedSection { get; set; }

    [ObservableProperty]
    public partial string ConnectionSubtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ControlStatusTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ControlStatusDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AgentStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ObsStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LiveCompanionStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ObsVersionText { get; set; } = "版本未知";

    [ObservableProperty]
    public partial string ObsConnectionState { get; set; } = "未连接";

    [ObservableProperty]
    public partial string ObsStreamingState { get; set; } = "推流状态未知";

    [ObservableProperty]
    public partial string ObsRecordingState { get; set; } = "录制状态未知";

    [ObservableProperty]
    public partial string ObsReadSummary { get; set; } = "尚未读取画面存档";

    [ObservableProperty]
    public partial string LiveCompanionVersionText { get; set; } = "版本未知";

    [ObservableProperty]
    public partial string LiveCompanionConnectionState { get; set; } = "未连接";

    [ObservableProperty]
    public partial string LiveCompanionLiveState { get; set; } = "开播状态未知";

    [ObservableProperty]
    public partial string LiveCompanionAccessState { get; set; } = "当前不能还原";

    [ObservableProperty]
    public partial string LiveCompanionReadSummary { get; set; } = "尚未读取画面存档";

    [ObservableProperty]
    public partial string CloudStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfigurationStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SnapshotCountText { get; set; } = "0 份";

    [ObservableProperty]
    public partial bool CanControlLocalApplications { get; set; }

    [ObservableProperty]
    public partial bool CanRestoreLocalApplications { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSyncCloudSnapshots))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestoreActionText))]
    public partial bool IsRestoringSnapshot { get; set; }

    [ObservableProperty]
    public partial bool IsAgentAutoStartEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnapshotCloudStatus))]
    [NotifyPropertyChangedFor(nameof(CanAssignSelectedSnapshotRoom))]
    [NotifyPropertyChangedFor(nameof(CanSyncCloudSnapshots))]
    public partial bool IsAgentCloudEnrolled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsControlDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsCloudControlVisible))]
    [NotifyPropertyChangedFor(nameof(IsLocalControlPanelVisible))]
    [NotifyPropertyChangedFor(nameof(HasLocalAndCloudControl))]
    [NotifyPropertyChangedFor(nameof(CanReturnToLocalControl))]
    [NotifyPropertyChangedFor(nameof(ControlPageTitle))]
    [NotifyPropertyChangedFor(nameof(ControlPageSubtitle))]
    [NotifyPropertyChangedFor(nameof(CanSyncCloudSnapshots))]
    [NotifyPropertyChangedFor(nameof(IsCloudSetupVisible))]
    public partial bool IsAgentConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsControlDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsCloudControlVisible))]
    [NotifyPropertyChangedFor(nameof(IsLocalControlPanelVisible))]
    [NotifyPropertyChangedFor(nameof(HasLocalAndCloudControl))]
    [NotifyPropertyChangedFor(nameof(CanReturnToLocalControl))]
    [NotifyPropertyChangedFor(nameof(CanShowRestoreAction))]
    [NotifyPropertyChangedFor(nameof(ControlPageTitle))]
    [NotifyPropertyChangedFor(nameof(ControlPageSubtitle))]
    [NotifyPropertyChangedFor(nameof(SnapshotCloudStatus))]
    [NotifyPropertyChangedFor(nameof(CanAssignSelectedSnapshotRoom))]
    [NotifyPropertyChangedFor(nameof(IsCloudWorkspaceVisible))]
    [NotifyPropertyChangedFor(nameof(IsCloudSetupVisible))]
    public partial bool IsCloudConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloudControlVisible))]
    [NotifyPropertyChangedFor(nameof(IsLocalControlPanelVisible))]
    [NotifyPropertyChangedFor(nameof(CanReturnToLocalControl))]
    [NotifyPropertyChangedFor(nameof(ControlPageTitle))]
    [NotifyPropertyChangedFor(nameof(ControlPageSubtitle))]
    [NotifyPropertyChangedFor(nameof(IsCloudWorkspaceVisible))]
    [NotifyPropertyChangedFor(nameof(IsCloudSetupVisible))]
    [NotifyPropertyChangedFor(nameof(IsControlDisconnected))]
    public partial bool IsShowingCloudManagement { get; set; }

    [ObservableProperty]
    public partial bool IsCloudAuthorizing { get; set; }

    [ObservableProperty]
    public partial string CloudServiceUrl { get; set; } = CloudTrustService.DefaultServiceUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloudConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(CanInstallCloudCertificate))]
    public partial bool IsCloudCertificateInstalled { get; set; }

    [ObservableProperty]
    public partial string CloudCertificateStatus { get; set; } = "正在检查固定 IP 私有证书";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloudAuthorizationCode))]
    public partial string CloudAuthorizationCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudConnectionMessage { get; set; } = "尚未连接云端服务";

    [ObservableProperty]
    public partial string OrganizationName { get; set; } = "未选择直播管理空间";

    [ObservableProperty]
    public partial IReadOnlyList<OrganizationSummary> Organizations { get; set; } = [];

    [ObservableProperty]
    public partial OrganizationSummary? SelectedOrganization { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<CloudRoomItemViewModel> CloudRooms { get; set; } = [];

    [ObservableProperty]
    public partial CloudRoomItemViewModel? SelectedCloudRoom { get; set; }

    [ObservableProperty]
    public partial string BatchCaptureResultText { get; set; } = "选择在线直播间后统一保存；每个直播间独立执行，失败不会影响其他直播间。";

    [ObservableProperty]
    public partial bool HasBatchCaptureFailures { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityItems))]
    [NotifyPropertyChangedFor(nameof(RecentActivityItems))]
    public partial IReadOnlyList<ActivityItemViewModel> ActivityItems { get; set; } = [];

    [ObservableProperty]
    public partial string CurrentObsPreviewUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentLiveCompanionPreviewUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasObsPreview))]
    public partial Bitmap? CurrentObsPreview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveCompanionPreview))]
    public partial Bitmap? CurrentLiveCompanionPreview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshots))]
    [NotifyPropertyChangedFor(nameof(HasLocalSnapshots))]
    [NotifyPropertyChangedFor(nameof(LocalSnapshotCount))]
    [NotifyPropertyChangedFor(nameof(PendingCloudUploadCount))]
    public partial IReadOnlyList<LocalSnapshotItemViewModel> SnapshotItems { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshotRoomFilters))]
    public partial IReadOnlyList<SnapshotRoomFilterItemViewModel> SnapshotRoomFilters { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAssignSelectedSnapshotRoom))]
    public partial SnapshotRoomFilterItemViewModel? SelectedSnapshotRoomFilter { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SnapshotTimelineItemViewModel> SnapshotTimelineItems { get; set; } = [];

    [ObservableProperty]
    public partial SnapshotTimelineItemViewModel? SelectedSnapshotTimelineItem { get; set; }

    [ObservableProperty]
    public partial string SnapshotTimelineSummary { get; set; } = "尚无存档";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveCameraStationsToSelectedSnapshot))]
    public partial LocalSnapshotItemViewModel? SelectedSnapshot { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshotInspector))]
    [NotifyPropertyChangedFor(nameof(CanSaveCameraStationsToSelectedSnapshot))]
    public partial SnapshotInspectorViewModel? SnapshotInspector { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCameraProfiles))]
    public partial IReadOnlyList<CameraProfileItemViewModel> CameraProfiles { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCameraProfile))]
    public partial CameraProfileItemViewModel? SelectedCameraProfile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWiredCameraMode))]
    [NotifyPropertyChangedFor(nameof(CanSaveManualCameraProfile))]
    public partial bool IsManualCameraMode { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveManualCameraProfile))]
    public partial string CameraProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveManualCameraProfile))]
    public partial string CameraAperture { get; set; } = "F4";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveManualCameraProfile))]
    public partial string CameraShutterSpeed { get; set; } = "1/125";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveManualCameraProfile))]
    public partial string CameraIso { get; set; } = "640";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveManualCameraProfile))]
    public partial CameraCreativeLookOptionViewModel? SelectedCameraCreativeLook { get; set; }

    [ObservableProperty]
    public partial int CameraLookContrast { get; set; }

    [ObservableProperty]
    public partial int CameraLookHighlights { get; set; }

    [ObservableProperty]
    public partial int CameraLookShadows { get; set; }

    [ObservableProperty]
    public partial int CameraLookFade { get; set; }

    [ObservableProperty]
    public partial int CameraLookSaturation { get; set; }

    [ObservableProperty]
    public partial int CameraLookSharpness { get; set; } = 4;

    [ObservableProperty]
    public partial int CameraLookSharpnessRange { get; set; } = 3;

    [ObservableProperty]
    public partial int CameraLookClarity { get; set; } = 1;

    [ObservableProperty]
    public partial string CameraProfileMessage { get; set; } = "手动档案只记录光圈、快门、ISO 和创意外观。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveManualCameraProfile))]
    public partial bool IsCameraProfileBusy { get; set; }

    [ObservableProperty]
    public partial bool IsCameraDeleteConfirmationVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectedSonyCameras))]
    public partial IReadOnlyList<SonyCameraDevice> DetectedSonyCameras { get; set; } = [];

    [ObservableProperty]
    public partial SonyCameraDevice? SelectedSonyCamera { get; set; }

    [ObservableProperty]
    public partial string SonyCameraConnectionState { get; set; } = "尚未检测 USB 相机";

    [ObservableProperty]
    public partial string SnapshotInspectorMessage { get; set; } = "选择一份存档查看设备、画面模式和滤镜参数";

    [ObservableProperty]
    public partial string SnapshotComparisonSummary { get; set; } = "选择存档后比较上一份保存";

    [ObservableProperty]
    public partial string ObsEndpoint { get; set; } = "ws://127.0.0.1:4455";

    [ObservableProperty]
    public partial string ObsPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SettingsMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalCheckStatus { get; set; } = "尚未执行本机检查";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNativeExportAudit))]
    public partial string NativeExportAuditDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NativeExportAuditStatus { get; set; } = "尚未检查原生导出包";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNativeExportChangedFields))]
    public partial IReadOnlyList<NativeExportChangedFieldViewModel> NativeExportChangedFields { get; set; } = [];

    [ObservableProperty]
    public partial bool NativeExportHasSensitivePaths { get; set; }

    [ObservableProperty]
    public partial bool HasNativeExportBaseline { get; set; }

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = "尚未检查更新";

    [ObservableProperty]
    public partial string LatestVersionText { get; set; } = "—";

    [ObservableProperty]
    public partial string LanSharedDirectory { get; set; } = "未配置";

    [ObservableProperty]
    public partial string LanSyncStatus { get; set; } = "未配置";

    [ObservableProperty]
    public partial string LanSettingsMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMappingSources))]
    [NotifyPropertyChangedFor(nameof(PendingMappingCount))]
    [NotifyPropertyChangedFor(nameof(HasPendingMappings))]
    [NotifyPropertyChangedFor(nameof(RestorePreparationSummary))]
    public partial IReadOnlyList<LocalMappingSourceItemViewModel> MappingSources { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<LocalMappingTargetItemViewModel> MappingTargets { get; set; } = [];

    [ObservableProperty]
    public partial LocalMappingSourceItemViewModel? SelectedMappingSource { get; set; }

    [ObservableProperty]
    public partial LocalMappingTargetItemViewModel? SelectedMappingTarget { get; set; }

    [ObservableProperty]
    public partial string MappingMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinuePreparedRestore))]
    public partial bool IsRestorePreparationOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinuePreparedRestore))]
    [NotifyPropertyChangedFor(nameof(RestorePreparationSummary))]
    public partial bool IsMappingContextReady { get; set; }

    [ObservableProperty]
    public partial string? PendingImportPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingImportActionText))]
    public partial bool PendingImportApplyAfterImport { get; set; }

    [ObservableProperty]
    public partial string PendingImportMessage { get; set; } = string.Empty;

    public string DeviceName => isDemoMode ? "Windows-直播机-01" : Environment.MachineName;

    public string PlatformMode => isDemoMode
        ? "演示模式 · Windows 全功能"
        : OperatingSystem.IsWindows()
            ? "Windows · 本机控制"
            : "macOS · 存档检查";

    public string BrandCaption => isDemoMode ? "完整产品演示" : "画面配置中心";

    public bool IsDemoMode => isDemoMode;

    public bool HasNativeExportAudit => !string.IsNullOrWhiteSpace(NativeExportAuditDetail);

    public bool HasNativeExportChangedFields => NativeExportChangedFields.Count > 0;

    public string CurrentVersionText { get; }

    public string SnapshotsSubtitle => isDemoMode
        ? "14 个直播间·按日期查看 OBS 与直播伴侣的全部画面参数"
        : SupportsLocalAgent
            ? "保存、检查并恢复 OBS 与直播伴侣联合配置"
        : "打开真实 .lscfg 存档，检查设备、画面模式、色彩与滤镜";

    public string SnapshotEmptyDescription => SupportsLocalAgent
        ? "保存第一份 OBS 与直播伴侣联合配置，之后可逐项查看或恢复。"
        : "打开 Windows 执行端生成的 .lscfg 文件，即可离线查看完整参数。";

    public bool HasSnapshots => SnapshotItems.Count > 0;

    public int LocalSnapshotCount => SnapshotItems.Count(snapshot => !snapshot.IsCloud && !snapshot.IsDesktopFile);

    public bool HasLocalSnapshots => LocalSnapshotCount > 0;

    public int PendingCloudUploadCount => SnapshotItems.Count(snapshot => snapshot.IsUploadPending);

    public bool CanSyncCloudSnapshots => IsAgentConnected && IsAgentCloudEnrolled && !IsBusy;

    public bool HasSnapshotRoomFilters => SnapshotRoomFilters.Count > 0;

    public bool SupportsLocalAgent => supportsLocalAgent;

    public bool CanInstallCloudCertificate =>
        OperatingSystem.IsWindows()
        && CloudTrustService.IsSupportedServiceUrl(CloudServiceUrl)
        && !IsCloudCertificateInstalled;

    public string CloudConnectButtonText => IsCloudCertificateInstalled
        ? "连接…"
        : "安装证书并连接";

    public bool HasPendingImport => !string.IsNullOrWhiteSpace(PendingImportPath);

    public string PendingImportActionText => PendingImportApplyAfterImport ? "信任、导入并应用" : "信任并导入";

    public bool HasTransferMessage => !string.IsNullOrWhiteSpace(PendingImportMessage);

    public bool HasSelectedSnapshot => SelectedSnapshot is not null;

    public string SnapshotCloudStatus => IsCloudConnected
        ? IsAgentCloudEnrolled ? "云存档自动同步" : "选择直播间并注册本机"
        : "连接云存档";

    public bool CanAssignSelectedSnapshotRoom =>
        IsCloudConnected
        && !IsAgentCloudEnrolled
        && SelectedSnapshotRoomFilter?.RoomId is not null;

    public bool HasSnapshotInspector => SnapshotInspector is not null;

    public bool CanSaveCameraStationsToSelectedSnapshot =>
        supportsLocalAgent
        && SelectedSnapshot is { IsCloud: false, IsDesktopFile: false }
        && SnapshotInspector is not null;

    public bool HasCameraProfiles => CameraProfiles.Count > 0;

    public bool HasSelectedCameraProfile => SelectedCameraProfile is not null;

    public bool HasDetectedSonyCameras => DetectedSonyCameras.Count > 0;

    public bool IsWiredCameraMode => !IsManualCameraMode;

    public bool CanSaveManualCameraProfile =>
        IsManualCameraMode
        && !IsCameraProfileBusy
        && !string.IsNullOrWhiteSpace(CameraProfileName)
        && !string.IsNullOrWhiteSpace(CameraAperture)
        && !string.IsNullOrWhiteSpace(CameraShutterSpeed)
        && !string.IsNullOrWhiteSpace(CameraIso)
        && SelectedCameraCreativeLook is not null;

    public bool HasMappingSources => MappingSources.Count > 0;

    public int PendingMappingCount => MappingSources.Count(source => source.Mapping is null);

    public bool HasPendingMappings => PendingMappingCount > 0;

    public bool CanContinuePreparedRestore =>
        IsRestorePreparationOpen && IsMappingContextReady && !HasPendingMappings && !IsBusy && SelectedSnapshot is not null;

    public string RestorePreparationSummary => !IsMappingContextReady
        ? "暂时无法检查目标设备"
        : MappingSources.Count == 0
        ? "当前存档没有需要对应的视频来源"
        : PendingMappingCount == 0
            ? $"{MappingSources.Count} 个来源已经匹配，可以安全恢复"
            : $"已自动匹配 {MappingSources.Count - PendingMappingCount} 个，还需选择 {PendingMappingCount} 个";

    public bool HasCloudAuthorizationCode => !string.IsNullOrWhiteSpace(CloudAuthorizationCode);

    public bool HasObsPreview => CurrentObsPreview is not null;

    public bool HasLiveCompanionPreview => CurrentLiveCompanionPreview is not null;

    public bool CanShowRestoreAction => SupportsLocalAgent
        || SelectedSnapshot?.IsCloud == true && IsCloudConnected;

    public string RestoreActionText => IsRestoringSnapshot ? "正在恢复…" : "恢复所选存档";

    public bool HasActivityItems => ActivityItems.Count > 0;

    public IReadOnlyList<ActivityItemViewModel> RecentActivityItems => ActivityItems.Take(4).ToArray();

    public int BatchSelectedRoomCount => CloudRooms.Count(room => room.IsBatchSelected && room.CanBatchSelect);

    public string BatchSelectedCountText => $"已选 {BatchSelectedRoomCount} 个";

    public bool IsDisconnected => !IsAgentConnected && !IsCloudConnected;

    public bool IsControlDisconnected => IsDisconnected && !IsShowingCloudManagement;

    public bool IsCloudControlVisible => IsCloudConnected && (IsShowingCloudManagement || !IsAgentConnected);

    public bool IsLocalControlPanelVisible => IsAgentConnected && !IsShowingCloudManagement;

    public bool IsCloudSetupVisible => IsControlVisible && IsShowingCloudManagement && !IsCloudConnected;

    public bool HasLocalAndCloudControl => IsAgentConnected && IsCloudConnected;

    public bool CanReturnToLocalControl => IsAgentConnected && IsShowingCloudManagement;

    public string ControlPageTitle => IsShowingCloudManagement ? "直播间管理" : "备份与恢复";

    public string ControlPageSubtitle => IsShowingCloudManagement
        ? "统一保存、查看和管理多台 Windows 直播电脑"
        : "画面调好后保存一份；需要时从存档恢复到这台电脑";

    public bool IsControlVisible => SelectedSection == 0;

    public bool IsSnapshotsVisible => SelectedSection != 3;

    public bool IsCloudWorkspaceVisible => IsControlVisible && IsShowingCloudManagement;

    public bool IsActivityVisible => SelectedSection == 2;

    public bool IsSettingsVisible => SelectedSection == 3;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadCameraProfilesAsync(cancellationToken);
        if (isDemoMode)
        {
            await LoadDesktopSnapshotLibraryAsync(cancellationToken);
            ApplyDemoWorkspace();
            return;
        }

        await LoadStateAsync(cancellationToken);
        if (!supportsLocalAgent)
        {
            await LoadDesktopSnapshotLibraryAsync(cancellationToken);
        }

        // 单机基础版不主动连接云端。保留已有云端实现和本机凭据，待直播间管理重新开放时继续使用。
    }

    partial void OnCanControlLocalApplicationsChanged(bool value) =>
        CaptureSnapshotCommand.NotifyCanExecuteChanged();

    partial void OnCanRestoreLocalApplicationsChanged(bool value) =>
        RestoreSnapshotCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        CaptureSnapshotCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
        ConfigureObsCommand.NotifyCanExecuteChanged();
        AutoConfigureObsCommand.NotifyCanExecuteChanged();
        DisableLanDirectoryCommand.NotifyCanExecuteChanged();
        SaveMappingCommand.NotifyCanExecuteChanged();
        TrustAndImportCommand.NotifyCanExecuteChanged();
        CancelPendingImportCommand.NotifyCanExecuteChanged();
        CaptureCloudSnapshotCommand.NotifyCanExecuteChanged();
        BatchCaptureCloudSnapshotsCommand.NotifyCanExecuteChanged();
        RetryFailedBatchCaptureCommand.NotifyCanExecuteChanged();
        ContinuePreparedRestoreCommand.NotifyCanExecuteChanged();
        EnrollAgentCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        InstallUpdateCommand.NotifyCanExecuteChanged();
        RunLocalCheckAndRepairCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCloudRoomChanged(CloudRoomItemViewModel? value)
    {
        CaptureCloudSnapshotCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
        if (isDemoMode)
        {
            ApplyDemoRoomState(value);
            return;
        }

        EnrollAgentCommand.NotifyCanExecuteChanged();
        if (value is not null && IsCloudConnected && !isLoadingCloudWorkspace)
        {
            _ = LoadSelectedCloudRoomStateAsync(CancellationToken.None);
        }
    }

    partial void OnSelectedOrganizationChanged(OrganizationSummary? value)
    {
        if (value is null
            || cloudCredentials is null
            || isLoadingCloudWorkspace
            || cloudCredentials.SelectedOrganizationId == value.Id)
        {
            return;
        }

        cloudCredentials = cloudCredentials with { SelectedOrganizationId = value.Id };
        batchSelectionInitialized = false;
        failedBatchCaptureRoomIds = [];
        HasBatchCaptureFailures = false;
        cloudClient.SaveCredentials(cloudCredentials);
        EnrollAgentCommand.NotifyCanExecuteChanged();
        _ = ChangeOrganizationAsync(CancellationToken.None);
    }

    partial void OnIsCloudConnectedChanged(bool value) => EnrollAgentCommand.NotifyCanExecuteChanged();

    partial void OnCloudServiceUrlChanged(string value) => RefreshCloudCertificateStatus();

    partial void OnIsCloudCertificateInstalledChanged(bool value) =>
        InstallCloudCertificateCommand.NotifyCanExecuteChanged();

    partial void OnIsAgentConnectedChanged(bool value) => EnrollAgentCommand.NotifyCanExecuteChanged();

    partial void OnIsAgentCloudEnrolledChanged(bool value) => EnrollAgentCommand.NotifyCanExecuteChanged();

    private async Task ChangeOrganizationAsync(CancellationToken cancellationToken)
    {
        CurrentObsPreview = null;
        CurrentLiveCompanionPreview = null;
        SelectedCloudRoom = null;
        SelectedSnapshot = null;
        cloudSnapshotItems = [];
        RefreshAvailableSnapshots();
        CloudRooms = [];
        cloudJobs = [];
        RefreshActivityItems();
        CloudConnectionMessage = "正在切换直播管理空间";
        await LoadCloudWorkspaceAsync(cancellationToken);
    }

    partial void OnSelectedSnapshotChanged(LocalSnapshotItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedSnapshot));
        OnPropertyChanged(nameof(CanShowRestoreAction));
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
        MappingSources = [];
        MappingTargets = [];
        SelectedMappingSource = null;
        SelectedMappingTarget = null;
        IsRestorePreparationOpen = false;
        snapshotDetailCancellation?.Cancel();
        snapshotDetailCancellation?.Dispose();
        snapshotDetailCancellation = null;
        SnapshotInspector = null;
        if (value?.IsCloud != true)
        {
            CurrentObsPreview = null;
            CurrentLiveCompanionPreview = null;
        }
        SnapshotInspectorMessage = value is null
            ? "选择一份存档查看设备、画面模式和滤镜参数"
            : "正在读取存档参数…";
        SnapshotComparisonSummary = value is null
            ? "选择存档后比较上一份保存"
            : "正在比较上一份存档…";
        if (value is not null)
        {
            if (!isRefreshingSnapshotNavigation)
            {
                SelectedSnapshotTimelineItem = SnapshotTimelineItems.FirstOrDefault(item => item.Snapshot.Id == value.Id);
            }

            snapshotDetailCancellation = new CancellationTokenSource();
            _ = LoadSnapshotInspectorAsync(value, snapshotDetailCancellation.Token);
        }
    }

    partial void OnSelectedSnapshotRoomFilterChanged(SnapshotRoomFilterItemViewModel? value)
    {
        AssignSelectedSnapshotRoomCommand.NotifyCanExecuteChanged();
        if (!isRefreshingSnapshotNavigation)
        {
            RefreshSnapshotTimeline(true);
            if (isDemoMode && value is not null)
            {
                var matchingRoom = CloudRooms.FirstOrDefault(room =>
                    string.Equals(room.Name, value.Name, StringComparison.Ordinal));
                if (matchingRoom is not null && SelectedCloudRoom?.Id != matchingRoom.Id)
                {
                    SelectedCloudRoom = matchingRoom;
                }
            }
        }
    }

    partial void OnSelectedSnapshotTimelineItemChanged(SnapshotTimelineItemViewModel? value)
    {
        if (!isRefreshingSnapshotNavigation && value is not null)
        {
            SelectedSnapshot = value.Snapshot;
        }
    }

    private async Task LoadSnapshotInspectorAsync(
        LocalSnapshotItemViewModel snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            SnapshotInspectorViewModel inspector;
            var comparisonSummary = "当前来源没有可用的上一份存档";
            if (snapshot.IsCloud)
            {
                if (cloudCredentials is null || SelectedOrganization is null)
                {
                    SnapshotInspectorMessage = "云端连接或直播管理空间已失效";
                    return;
                }

                var detail = await cloudClient.GetSnapshotDetailAsync(
                    cloudCredentials,
                    SelectedOrganization.Id,
                    snapshot.Id,
                    cancellationToken);
                inspector = new SnapshotInspectorViewModel(
                    detail.Summary.Name,
                    detail.Summary.CreatedAt,
                    detail.Applications,
                    creativeLooks: CameraCreativeLooks,
                    cameraStations: detail.CameraStations);
                await LoadCloudCameraReferenceImagesAsync(snapshot, inspector, cancellationToken);
            }
            else if (snapshot.IsDesktopFile)
            {
                var file = await snapshotFileStore.GetAsync(snapshot.Id, cancellationToken);
                var detail = file.Inspection.Package.Snapshot;
                inspector = new SnapshotInspectorViewModel(
                    detail.Name,
                    detail.CreatedAt,
                    detail.Applications,
                    "签名有效",
                    FormatSignerFingerprint(file.Inspection.Signer.FingerprintSha256),
                    CameraCreativeLooks,
                    detail.CameraStations);
                ApplyCameraReferenceImages(inspector, file.Inspection.Package);
            }
            else
            {
                var detail = await localAgentClient.GetSnapshotDetailAsync(snapshot.Id, cancellationToken);
                inspector = new SnapshotInspectorViewModel(
                    detail.Name,
                    detail.CreatedAt,
                    detail.Applications,
                    creativeLooks: CameraCreativeLooks,
                    cameraStations: detail.CameraStations);
                await LoadLocalCameraReferenceImagesAsync(snapshot.Id, inspector, cancellationToken);
                comparisonSummary = await CompareWithPreviousLocalSnapshotAsync(
                    snapshot,
                    detail,
                    cancellationToken);
            }

            if (SelectedSnapshot?.Id == snapshot.Id)
            {
                SnapshotInspector = inspector;
                ApplySnapshotReadSummary(inspector);
                SnapshotComparisonSummary = comparisonSummary;
                SnapshotInspectorMessage = inspector.Applications.Count == 0
                    ? "这份存档没有应用参数"
                    : string.Empty;
            }

            if (!snapshot.IsCloud && !snapshot.IsDesktopFile)
            {
                await LoadLocalSnapshotPreviewsAsync(snapshot.Id, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is LocalControlException
            or IOException
            or HttpRequestException
            or SnapshotPackageException)
        {
            if (SelectedSnapshot?.Id == snapshot.Id)
            {
                SnapshotInspectorMessage = SnapshotOperationError(exception);
            }
        }
    }

    partial void OnSelectedMappingSourceChanged(LocalMappingSourceItemViewModel? value)
    {
        MappingTargets = value is null
            ? []
            : allMappingTargets.Where(target => target.Application == value.Application).ToArray();
        SelectedMappingTarget = value?.Mapping is { } mapping
            ? MappingTargets.FirstOrDefault(target =>
                string.Equals(target.SourceName, mapping.TargetSourceName, StringComparison.Ordinal)
                && string.Equals(target.TargetDeviceId, mapping.TargetDeviceId, StringComparison.Ordinal))
            : MappingTargets.FirstOrDefault();
        SaveMappingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedMappingTargetChanged(LocalMappingTargetItemViewModel? value) =>
        SaveMappingCommand.NotifyCanExecuteChanged();

    partial void OnMappingSourcesChanged(IReadOnlyList<LocalMappingSourceItemViewModel> value)
    {
        ContinuePreparedRestoreCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRestorePreparationOpenChanged(bool value) =>
        ContinuePreparedRestoreCommand.NotifyCanExecuteChanged();

    partial void OnIsMappingContextReadyChanged(bool value) =>
        ContinuePreparedRestoreCommand.NotifyCanExecuteChanged();

    partial void OnPendingImportPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPendingImport));
        TrustAndImportCommand.NotifyCanExecuteChanged();
        CancelPendingImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingImportMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasTransferMessage));

    partial void OnCurrentObsPreviewChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    partial void OnCurrentLiveCompanionPreviewChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    [RelayCommand]
    private async Task RefreshStateAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await LoadDesktopSnapshotLibraryAsync(cancellationToken);
            ApplyDemoRoomState(SelectedCloudRoom);
            ControlStatusDescription = "演示数据已刷新；未连接真实 Windows 执行端。";
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            await LoadDesktopSnapshotLibraryAsync(cancellationToken);
            if (IsCloudConnected)
            {
                await LoadCloudWorkspaceAsync(cancellationToken);
            }

            return;
        }

        if (IsCloudControlVisible)
        {
            await LoadCloudWorkspaceAsync(cancellationToken);
            return;
        }

        try
        {
            ApplyAgentState(await localAgentClient.RefreshCurrentStateAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            ApplyDisconnectedState(exception.Message);
        }
    }

    [RelayCommand]
    private async Task ConnectCloudAsync(CancellationToken cancellationToken)
    {
        if (!CloudTrustService.IsSupportedServiceUrl(CloudServiceUrl)
            || !Uri.TryCreate(CloudTrustService.DefaultServiceUrl, UriKind.Absolute, out var serviceUri))
        {
            CloudServiceUrl = CloudTrustService.DefaultServiceUrl;
            CloudConnectionMessage = "当前版本只连接内置的 LiveStudio 云端服务器";
            return;
        }

        IsBusy = true;
        IsCloudAuthorizing = true;
        CloudAuthorizationCode = string.Empty;
        try
        {
            if (OperatingSystem.IsWindows() && !CloudTrustService.IsBundledRootInstalled())
            {
                CloudTrustService.InstallBundledRoot(serviceUri.ToString());
                RefreshCloudCertificateStatus();
                CloudConnectionMessage = "服务器证书已安装，正在打开浏览器授权";
            }

            var authorization = await cloudClient.StartAuthorizationAsync(
                serviceUri,
                Environment.MachineName,
                cancellationToken);
            CloudAuthorizationCode = authorization.UserCode;
            CloudConnectionMessage = "请在浏览器中登录并确认相同代码";
            systemBrowser.Open(authorization.VerificationUri);
            while (DateTimeOffset.UtcNow < authorization.ExpiresAt)
            {
                await Task.Delay(TimeSpan.FromSeconds(authorization.PollIntervalSeconds), cancellationToken);
                var result = await cloudClient.PollAuthorizationAsync(
                    serviceUri,
                    authorization.DeviceCode,
                    cancellationToken);
                if (result.Status == DesktopAuthorizationStatus.Pending)
                {
                    continue;
                }

                if (result.Status == DesktopAuthorizationStatus.Approved
                    && result.AccessToken is { } accessToken
                    && result.AccessTokenExpiresAt is { } expiresAt)
                {
                    cloudClient.SaveCredentials(serviceUri, accessToken, expiresAt);
                    cloudCredentials = new DesktopCloudCredentials(serviceUri, accessToken, expiresAt);
                    CloudServiceUrl = serviceUri.ToString();
                    CloudConnectionMessage = "授权成功，正在读取直播间";
                    await LoadCloudWorkspaceAsync(cancellationToken);
                    await TryAutoEnrollSingleRoomAsync(cancellationToken);
                    return;
                }

                break;
            }

            CloudConnectionMessage = "授权请求已过期，请重新连接";
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or CryptographicException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException)
        {
            CloudConnectionMessage = exception.Message;
        }
        finally
        {
            IsCloudAuthorizing = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectCloudAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            CloudConnectionMessage = "演示模式保持本地连接，不会修改真实云端授权";
            return;
        }

        var credentials = cloudCredentials;
        var disconnectMessage = "已断开云端服务";
        try
        {
            if (credentials is not null)
            {
                await cloudClient.RevokeAsync(credentials, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            disconnectMessage = "云端撤销失败，本机授权已清除";
        }

        cloudCredentials = null;
        Organizations = [];
        SelectedOrganization = null;
        IsCloudConnected = false;
        CloudRooms = [];
        cloudSnapshotItems = [];
        cloudJobs = [];
        RefreshActivityItems();
        RefreshAvailableSnapshots();
        CloudAuthorizationCode = string.Empty;
        if (!IsAgentConnected)
        {
            ApplyDisconnectedState();
        }

        CloudConnectionMessage = disconnectMessage;
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await Task.Yield();
            LatestVersionText = CurrentVersionText;
            UpdateStatus = "演示检查完成 · 当前已是最新版本 · SHA-256 与发布签名均通过";
            return;
        }

        IsBusy = true;
        UpdateStatus = "正在检查更新…";
        try
        {
            availableUpdate = await applicationUpdateService.CheckAsync(cancellationToken);
            if (availableUpdate is null)
            {
                LatestVersionText = CurrentVersionText;
                UpdateStatus = "当前已经是最新版本";
            }
            else
            {
                LatestVersionText = availableUpdate.Version.ToString(3);
                UpdateStatus = $"发现 {availableUpdate.Name}，可以下载并安装";
            }

            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception) when (exception is HttpRequestException
            or InvalidDataException
            or InvalidOperationException)
        {
            availableUpdate = null;
            LatestVersionText = "—";
            UpdateStatus = exception.Message.Contains("Release 缺少", StringComparison.Ordinal)
                    ? "已连接更新服务，但最新发布中还没有可安装的 Windows 更新包"
                    : $"检查更新失败：{exception.Message}";
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync(CancellationToken cancellationToken)
    {
        if (availableUpdate is null)
        {
            return;
        }

        IsBusy = true;
        UpdateStatus = $"正在下载并校验 {availableUpdate.TagName}…";
        try
        {
            var prepared = await applicationUpdateService.PrepareAsync(
                availableUpdate,
                cancellationToken);
            UpdateStatus = "更新包校验完成，正在重启安装";
            ApplicationUpdateService.LaunchInstaller(prepared);
            UpdateRestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException)
        {
            UpdateStatus = $"安装更新失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunLocalCheckAndRepair))]
    private async Task RunLocalCheckAndRepairAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await Task.Yield();
            LocalCheckStatus = "演示模式：执行端、自启动、OBS 和直播伴侣本机连接均已模拟通过";
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            LocalCheckStatus = "本机检查与修复仅在 Windows 直播电脑上执行";
            return;
        }

        IsBusy = true;
        LocalCheckStatus = "正在启动执行端并检查本机环境…";
        try
        {
            var agentStarted = WindowsAgentBootstrapper.EnsureRunning();
            var state = await WaitForLocalAgentAsync(cancellationToken);

            if (!state.AutoStartEnabled)
            {
                LocalCheckStatus = "正在修复 Agent 登录后自动启动…";
                state = await localAgentClient.ConfigureAutoStartAsync(true, cancellationToken);
            }

            LocalCheckStatus = "正在检测并修复 OBS 与直播伴侣连接…";
            state = await localAgentClient.AutoConfigureObsAsync(cancellationToken);

            ApplyAgentState(state);
            IsBusy = true;

            var applications = state.Applications.ToDictionary(application => application.Application);
            var warnings = new List<string>();
            AddApplicationCheckWarning(warnings, "OBS", applications.GetValueOrDefault(ApplicationKind.Obs));
            AddApplicationCheckWarning(
                warnings,
                "直播伴侣",
                applications.GetValueOrDefault(ApplicationKind.LiveCompanion));
            if (!state.CanCapture)
            {
                warnings.Add("当前仍不能完整读取两款应用");
            }

            var startMessage = agentStarted ? "Agent 已重新启动" : "Agent 正常";
            LocalCheckStatus = warnings.Count == 0
                ? $"检查并修复完成：{startMessage}、自启动正常、两款应用可读取"
                : $"检查完成，但仍需处理：{string.Join("；", warnings)}";
        }
        catch (Exception exception) when (exception is LocalControlException
            or IOException
            or HttpRequestException
            or InvalidDataException
            or UnauthorizedAccessException)
        {
            LocalCheckStatus = $"检查或修复失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<LocalAgentState> WaitForLocalAgentAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                return await localAgentClient.GetStateAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is LocalControlException or IOException)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
            }
        }

        throw new IOException("无法连接 LiveStudio Agent，请确认当前 Windows 用户可以启动执行端", lastError);
    }

    private async Task VerifyCloudSnapshotReadbackAsync(
        SnapshotSummary snapshot,
        CancellationToken cancellationToken)
    {
        if (cloudCredentials is null || SelectedOrganization is null)
        {
            return;
        }

        var diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "LiveStudio", "Diagnostics");
        var destinationPath = Path.Combine(diagnosticsDirectory, $"{snapshot.Id:N}-{Guid.NewGuid():N}.lscfg");
        try
        {
            await cloudClient.DownloadSnapshotAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                snapshot,
                destinationPath,
                cancellationToken);
        }
        finally
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
        }
    }

    private static void AddApplicationCheckWarning(
        List<string> warnings,
        string applicationName,
        LocalApplicationState? application)
    {
        if (application is null || !application.AdapterAvailable)
        {
            warnings.Add($"{applicationName} 适配器不可用");
        }
        else if (!application.IsRunning)
        {
            warnings.Add($"{applicationName} 未运行");
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallCloudCertificate))]
    private void InstallCloudCertificate()
    {
        try
        {
            CloudTrustService.InstallBundledRoot(CloudServiceUrl);
            RefreshCloudCertificateStatus();
            CloudConnectionMessage = "固定 IP 私有证书已安装，可以连接云存档";
        }
        catch (Exception exception) when (exception is CryptographicException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException)
        {
            CloudCertificateStatus = $"证书安装失败：{exception.Message}";
        }
    }

    private void RefreshCloudCertificateStatus()
    {
        try
        {
            if (!CloudTrustService.IsSupportedServiceUrl(CloudServiceUrl))
            {
                IsCloudCertificateInstalled = false;
                CloudCertificateStatus = "内置证书仅用于 LiveStudio 固定 IP 云端";
                InstallCloudCertificateCommand.NotifyCanExecuteChanged();
                return;
            }

            IsCloudCertificateInstalled = CloudTrustService.IsBundledRootInstalled();
            CloudCertificateStatus = IsCloudCertificateInstalled
                ? "固定 IP 私有证书已安装"
                : "首次连接前，请先安装此服务器证书";
            InstallCloudCertificateCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception) when (exception is CryptographicException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            IsCloudCertificateInstalled = false;
            CloudCertificateStatus = $"证书检查失败：{exception.Message}";
            InstallCloudCertificateCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task LoadLocalSnapshotPreviewsAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        var obsTask = localAgentClient.GetSnapshotPreviewAsync(
            snapshotId,
            ApplicationKind.Obs,
            cancellationToken);
        var liveCompanionTask = localAgentClient.GetSnapshotPreviewAsync(
            snapshotId,
            ApplicationKind.LiveCompanion,
            cancellationToken);
        await Task.WhenAll(obsTask, liveCompanionTask);
        if (SelectedSnapshot?.Id != snapshotId)
        {
            return;
        }

        CurrentObsPreview = CreatePreviewBitmap(await obsTask);
        CurrentLiveCompanionPreview = CreatePreviewBitmap(await liveCompanionTask);
    }

    private static Bitmap? CreatePreviewBitmap(LocalSnapshotPreview preview)
    {
        if (!preview.Found || preview.Content.Length == 0)
        {
            return null;
        }

        try
        {
            return new Bitmap(new MemoryStream(preview.Content, writable: false));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task EnableAgentAutoStartAsync(CancellationToken cancellationToken) =>
        await ConfigureAgentAutoStartAsync(true, cancellationToken);

    [RelayCommand]
    private async Task DisableAgentAutoStartAsync(CancellationToken cancellationToken) =>
        await ConfigureAgentAutoStartAsync(false, cancellationToken);

    private async Task ConfigureAgentAutoStartAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await Task.Yield();
            IsAgentAutoStartEnabled = enabled;
            SettingsMessage = enabled
                ? "演示模式·Agent 已模拟开启登录后自动启动"
                : "演示模式·Agent 已模拟关闭自动启动";
            return;
        }

        if (!OperatingSystem.IsWindows() || !IsAgentConnected)
        {
            return;
        }

        try
        {
            ApplyAgentState(await localAgentClient.ConfigureAutoStartAsync(enabled, cancellationToken));
            SettingsMessage = enabled ? "Agent 已开启登录后自动启动" : "Agent 已关闭自动启动";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            SettingsMessage = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnrollAgent))]
    private async Task EnrollAgentAsync(CancellationToken cancellationToken)
    {
        if (cloudCredentials is null
            || SelectedOrganization is null
            || SelectedCloudRoom is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var enrollment = await cloudClient.CreateDeviceEnrollmentAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                new CreateDeviceEnrollmentRequest(SelectedCloudRoom.Id, DeviceName),
                cancellationToken);
            var state = await localAgentClient.EnrollDeviceAsync(
                cloudCredentials.ServiceUri,
                enrollment.EnrollmentToken,
                DeviceName,
                cancellationToken);
            ApplyAgentState(state);
            SettingsMessage = $"设备已注册到“{SelectedCloudRoom.Name}”，正在建立云端连接";
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await LoadCloudWorkspaceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or InvalidOperationException
            or LocalControlException
            or IOException)
        {
            SettingsMessage = $"设备注册失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCaptureCloudSnapshot))]
    private async Task CaptureCloudSnapshotAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await RunDemoOperationAsync(
                JobKind.Capture,
                "正在演示联合保存：Preflight → 读取参数 → 打包 → 签名 → 上传",
                "演示联合保存完成，OBS 与直播伴侣目标字段均已读取",
                cancellationToken);
            return;
        }

        if (cloudCredentials is null
            || SelectedOrganization is null
            || SelectedCloudRoom?.DeviceId is not { } deviceId)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await cloudClient.CreateCaptureJobAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                new CreateCaptureJobRequest(
                    SelectedCloudRoom.Id,
                    deviceId,
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture)),
                cancellationToken);
            ControlStatusTitle = "联合保存任务已下发";
            ControlStatusDescription = "Windows 执行端会读取、校验并上传联合存档。";
            await LoadCloudWorkspaceAsync(cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            ControlStatusTitle = "无法下发保存任务";
            ControlStatusDescription = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            ApplyDisconnectedState();
            return;
        }

        try
        {
            ApplyAgentState(await localAgentClient.GetStateAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            ApplyDisconnectedState(exception.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCaptureSnapshot))]
    private async Task CaptureSnapshotAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await RunDemoOperationAsync(
                JobKind.Capture,
                "正在演示本机联合保存：读取设备、视频模式、滤镜、美颜、曲线与素材",
                "演示存档已通过哈希、签名和敏感字段检查",
                cancellationToken);
            return;
        }

        IsBusy = true;
        ControlStatusTitle = "正在保存当前画面";
        ControlStatusDescription = "正在读取 OBS 与直播伴侣参数、滤镜和预览图。";
        try
        {
            var cameraEditors = SnapshotInspector?.CameraStations ?? CameraStations;
            if (!TryCollectCameraStations(
                    cameraEditors,
                    out var cameraStations,
                    out var cameraError))
            {
                ControlStatusTitle = "相机参数有误";
                ControlStatusDescription = cameraError;
                PendingImportMessage = cameraError;
                return;
            }

            var name = DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
            var imageChanges = cameraEditors
                .Select(editor => editor.CreateReferenceImageChange())
                .Where(change => change is not null)
                .Select(change => change!)
                .ToArray();
            var result = await localAgentClient.CaptureAsync(
                name,
                cameraStations,
                imageChanges,
                cancellationToken);
            await LoadStateAsync(cancellationToken);
            SelectedSnapshot = SnapshotItems.FirstOrDefault(snapshot => snapshot.Id == result.SnapshotId);
            SelectedSection = 1;
            ControlStatusTitle = "当前画面已保存并读取";
            ControlStatusDescription = "新存档已经生成并自动打开，可以检查内容或导出。";
            PendingImportMessage = "当前画面已保存，并已自动打开新存档";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            ControlStatusTitle = "保存失败";
            ControlStatusDescription = exception.Message;
            PendingImportMessage = SnapshotOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSnapshot))]
    private async Task RestoreSnapshotAsync(CancellationToken cancellationToken)
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        if (isDemoMode)
        {
            await RunDemoOperationAsync(
                JobKind.Restore,
                "正在演示恢复事务：Preflight → 备份 → 应用 → 启动 → 逐字段回读",
                "演示恢复完成，全部目标字段回读一致",
                cancellationToken);
            return;
        }

        if (!skipNextMappingPreparation
            && !await PrepareMappingsForRestoreAsync(cancellationToken))
        {
            return;
        }

        skipNextMappingPreparation = false;
        if (SelectedSnapshot.IsCloud)
        {
            await RestoreCloudSnapshotAsync(cancellationToken);
            return;
        }

        var cameraEditors = SnapshotInspector?.CameraStations ?? CameraStations;
        if (!TryCollectCameraStations(cameraEditors, out var currentCameraStations, out var cameraError))
        {
            ControlStatusTitle = "相机参数有误";
            ControlStatusDescription = cameraError;
            PendingImportMessage = cameraError;
            return;
        }

        IsBusy = true;
        IsRestoringSnapshot = true;
        ControlStatusTitle = $"正在恢复“{SelectedSnapshot.Name}”";
        ControlStatusDescription = "恢复完成前不要关闭 OBS、直播伴侣或 LiveStudio。";
        PendingImportMessage = $"正在恢复“{SelectedSnapshot.Name}”…";
        try
        {
            var result = await RestoreLocalSnapshotWithProgressAsync(
                SelectedSnapshot.Id,
                SelectedSnapshot.Name,
                currentCameraStations,
                cancellationToken);
            await LoadStateAsync(cancellationToken);
            ControlStatusTitle = "恢复完成";
            ControlStatusDescription = $"“{result.Name}”已应用并通过逐字段回读；恢复前状态已另存为自动备份。";
            PendingImportMessage = $"恢复完成：“{result.Name}”已应用；恢复前状态已自动备份";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            ControlStatusTitle = "恢复失败";
            ControlStatusDescription = exception.Message;
            PendingImportMessage = $"恢复失败：{SnapshotOperationError(exception)}";
            if (exception is LocalControlException { ErrorCode: nameof(JobStatus.MappingRequired) or nameof(JobStatus.UnsupportedDeviceMode) })
            {
                MappingMessage = $"目标设备需要重新选择：{exception.Message}";
                await LoadMappingContextAsync(CancellationToken.None);
                IsRestorePreparationOpen = true;
                SelectedMappingSource = MappingSources.FirstOrDefault(source => source.Mapping is null)
                    ?? (MappingSources.Count > 0 ? MappingSources[0] : null);
            }
        }
        finally
        {
            IsRestoringSnapshot = false;
            IsBusy = false;
        }
    }

    private async Task TryAutoEnrollSingleRoomAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()
            || !IsAgentConnected
            || IsAgentCloudEnrolled
            || cloudCredentials is null
            || SelectedOrganization is null
            || CloudRooms is not [var onlyRoom]
            || onlyRoom.DeviceId is not null)
        {
            return;
        }

        SelectedCloudRoom = onlyRoom;
        CloudConnectionMessage = $"正在把本机绑定到“{onlyRoom.Name}”";
        await EnrollAgentAsync(cancellationToken);
        CloudConnectionMessage = IsAgentCloudEnrolled
            ? $"已连接 {SelectedOrganization.Name}，本机已绑定到“{onlyRoom.Name}”"
            : SettingsMessage;
    }

    [RelayCommand]
    private async Task SaveSelectedSnapshotCameraStationsAsync(CancellationToken cancellationToken)
    {
        if (SelectedSnapshot is null || SnapshotInspector is null)
        {
            return;
        }

        if (!CanSaveCameraStationsToSelectedSnapshot)
        {
            SnapshotInspector.CameraSaveMessage = SelectedSnapshot.IsCloud
                ? "云端存档不能直接改写；请点“保存当前画面”生成一份包含新参数的存档"
                : "导入预览不会改写原文件；请先导入本机后再保存相机参数";
            return;
        }

        if (!TryCollectCameraStations(SnapshotInspector.CameraStations, out var stations, out var error))
        {
            SnapshotInspector.CameraSaveMessage = error;
            return;
        }

        SnapshotInspector.IsSavingCameraStations = true;
        SnapshotInspector.CameraSaveMessage = "正在把三个机位写入当前画面存档…";
        try
        {
            await localAgentClient.UpdateSnapshotCameraStationsAsync(
                SelectedSnapshot.Id,
                stations,
                SnapshotInspector.CameraStations
                    .Select(editor => editor.CreateReferenceImageChange())
                    .Where(change => change is not null)
                    .Select(change => change!)
                    .ToArray(),
                cancellationToken);
            SnapshotInspector.CameraSaveMessage = "三个机位和参考图已写入并重新签名；导出和云同步都会完整携带";
            await LoadStateAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is LocalControlException or IOException or InvalidOperationException)
        {
            SnapshotInspector.CameraSaveMessage = SnapshotOperationError(exception);
        }
        finally
        {
            if (SnapshotInspector is not null)
            {
                SnapshotInspector.IsSavingCameraStations = false;
            }
        }
    }

    private static bool TryCollectCameraStations(
        IReadOnlyList<CameraStationEditorViewModel> editors,
        out IReadOnlyList<CameraStationSnapshot> stations,
        out string error)
    {
        var values = new List<CameraStationSnapshot>(3);
        foreach (var editor in editors.OrderBy(editor => editor.Slot))
        {
            if (!editor.TryCreateSnapshot(out var station, out error))
            {
                stations = [];
                error = $"{editor.PositionTitle}：{error}";
                return false;
            }

            values.Add(station);
        }

        stations = values;
        error = string.Empty;
        return values.Count == 3;
    }

    private async Task LoadLocalCameraReferenceImagesAsync(
        Guid snapshotId,
        SnapshotInspectorViewModel inspector,
        CancellationToken cancellationToken)
    {
        foreach (var editor in inspector.CameraStations.Where(editor => editor.ReferenceImageMetadata is not null))
        {
            var image = await localAgentClient.GetCameraReferenceImageAsync(
                snapshotId,
                editor.Slot,
                cancellationToken);
            if (image.Found)
            {
                editor.ApplyReferenceImageContent(image.Content);
            }
        }
    }

    private async Task LoadCloudCameraReferenceImagesAsync(
        LocalSnapshotItemViewModel snapshot,
        SnapshotInspectorViewModel inspector,
        CancellationToken cancellationToken)
    {
        if (!inspector.CameraStations.Any(editor => editor.ReferenceImageMetadata is not null)
            || cloudCredentials is null
            || SelectedOrganization is null
            || snapshot.CloudSummary is not { } summary)
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "LiveStudio", "CameraReferences");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{snapshot.Id:N}-{Guid.NewGuid():N}.lscfg");
        try
        {
            await cloudClient.DownloadSnapshotAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                summary,
                path,
                cancellationToken);
            var inspection = await SnapshotPackageReader.InspectAsync(path, cancellationToken);
            ApplyCameraReferenceImages(inspector, inspection.Package);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void ApplyCameraReferenceImages(
        SnapshotInspectorViewModel inspector,
        SnapshotPackage package)
    {
        foreach (var editor in inspector.CameraStations)
        {
            if (editor.ReferenceImageMetadata is { } reference
                && package.Files.TryGetValue(reference.PackagePath, out var file))
            {
                editor.ApplyReferenceImageContent(file.Content);
            }
        }
    }

    public async Task<bool> CreateCloudRoomAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = name.Trim();
        if (cloudCredentials is null || SelectedOrganization is null || !IsCloudConnected)
        {
            SelectedSection = 3;
            SettingsMessage = "请先连接云端服务并选择直播管理空间，再新建直播间";
            return false;
        }

        if (normalizedName.Length is < 1 or > 100)
        {
            PendingImportMessage = "直播间名称必须为 1 至 100 个字符";
            return false;
        }

        IsBusy = true;
        try
        {
            var room = await cloudClient.CreateRoomAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                normalizedName,
                cancellationToken);
            await LoadCloudWorkspaceAsync(cancellationToken);
            SelectedCloudRoom = CloudRooms.FirstOrDefault(item => item.Id == room.Id);
            RefreshSnapshotNavigation();
            SelectedSnapshotRoomFilter = SnapshotRoomFilters.FirstOrDefault(item => item.RoomId == room.Id);
            PendingImportMessage = $"已新建“{room.Name}”；选择“设为当前直播间”后，新存档会自动归入并同步到云端";
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            PendingImportMessage = $"新建直播间失败：{exception.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAssignSelectedSnapshotRoom))]
    private async Task AssignSelectedSnapshotRoomAsync(CancellationToken cancellationToken)
    {
        var roomId = SelectedSnapshotRoomFilter?.RoomId;
        if (roomId is null)
        {
            return;
        }

        SelectedCloudRoom = CloudRooms.FirstOrDefault(room => room.Id == roomId);
        if (SelectedCloudRoom is null)
        {
            PendingImportMessage = "当前直播间不属于已连接的直播管理空间";
            return;
        }

        await EnrollAgentAsync(cancellationToken);
        if (IsAgentCloudEnrolled)
        {
            PendingImportMessage = $"本机已分配到“{SelectedCloudRoom.Name}”；以后保存会自动归档并上传云端";
        }
    }

    [RelayCommand]
    private async Task SyncCloudSnapshotsAsync(CancellationToken cancellationToken)
    {
        if (!CanSyncCloudSnapshots)
        {
            PendingImportMessage = "先把本机注册到一个直播间，再执行云端同步";
            return;
        }

        IsBusy = true;
        PendingImportMessage = "正在校验并上传等待同步的画面存档…";
        try
        {
            var result = await localAgentClient.SyncPendingSnapshotsAsync(cancellationToken);
            await LoadStateAsync(cancellationToken);
            if (IsCloudConnected)
            {
                await LoadCloudWorkspaceAsync(cancellationToken);
            }

            PendingImportMessage = result.Message;
        }
        catch (Exception exception) when (exception is LocalControlException or IOException or HttpRequestException)
        {
            PendingImportMessage = $"云存档同步失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenCloudSettings()
    {
        SelectedSection = 3;
        SettingsMessage = IsCloudConnected
            ? "在这里选择直播管理空间，并将当前 Windows 执行端注册到直播间"
            : "先连接云端服务，再创建直播间和启用自动存档同步";
    }

    private async Task<bool> PrepareMappingsForRestoreAsync(CancellationToken cancellationToken)
    {
        SnapshotInspector?.IsTechnicalPanelOpen = false;
        IsRestorePreparationOpen = false;
        await LoadMappingContextAsync(cancellationToken);
        if (!IsMappingContextReady)
        {
            IsRestorePreparationOpen = true;
            return false;
        }

        if (MappingSources.Count == 0)
        {
            return true;
        }

        var automaticMatches = MappingSources
            .Where(source => source.Mapping is null)
            .Select(source => (Source: source, Target: FindAutomaticMappingTarget(source)))
            .Where(match => match.Target is not null)
            .Select(match => (match.Source, Target: match.Target!))
            .ToArray();
        if (automaticMatches.Length > 0)
        {
            IsBusy = true;
            try
            {
                foreach (var match in automaticMatches)
                {
                    await SaveMappingPairAsync(match.Source, match.Target, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is LocalControlException or HttpRequestException or IOException or InvalidOperationException)
            {
                MappingMessage = $"自动匹配没有全部保存：{exception.Message}";
            }
            finally
            {
                IsBusy = false;
            }

            await LoadMappingContextAsync(cancellationToken);
        }

        if (!HasPendingMappings)
        {
            MappingMessage = $"系统已确认 {MappingSources.Count} 个来源与当前电脑对应";
            return true;
        }

        IsRestorePreparationOpen = true;
        SelectedMappingSource = MappingSources.First(source => source.Mapping is null);
        MappingMessage = $"有 {PendingMappingCount} 个来源无法安全自动判断，请选择它在目标电脑上的对应来源";
        return false;
    }

    private LocalMappingTargetItemViewModel? FindAutomaticMappingTarget(LocalMappingSourceItemViewModel source)
        => RestoreMappingMatcher.FindAutomaticTarget(source, allMappingTargets);

    private async Task SaveMappingPairAsync(
        LocalMappingSourceItemViewModel source,
        LocalMappingTargetItemViewModel target,
        CancellationToken cancellationToken)
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        if (SelectedSnapshot.IsCloud)
        {
            if (cloudCredentials is null
                || SelectedOrganization is null
                || SelectedCloudRoom?.DeviceId is not { } deviceId)
            {
                throw new InvalidOperationException("尚未选择目标直播间");
            }

            await cloudClient.SaveMappingAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                deviceId,
                new SaveDeviceMappingRequest(
                    source.SourceLogicalId,
                    source.Application,
                    target.TargetDeviceId,
                    target.SourceName,
                    string.Empty,
                    false),
                cancellationToken);
            return;
        }

        await localAgentClient.SaveDeviceMappingAsync(
            new SaveLocalDeviceMappingRequest(
                SelectedSnapshot.Id,
                source.SourceLogicalId,
                source.Application,
                target.TargetDeviceId,
                target.SourceName),
            cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanContinuePreparedRestore))]
    private async Task ContinuePreparedRestoreAsync(CancellationToken cancellationToken)
    {
        if (HasPendingMappings)
        {
            return;
        }

        IsRestorePreparationOpen = false;
        skipNextMappingPreparation = true;
        await RestoreSnapshotAsync(cancellationToken);
    }

    [RelayCommand]
    private void CancelRestorePreparation()
    {
        IsRestorePreparationOpen = false;
        MappingMessage = "已取消本次恢复准备，没有写入 OBS 或直播伴侣";
    }

    [RelayCommand]
    private void SelectAllOnlineRooms()
    {
        foreach (var room in CloudRooms)
        {
            room.IsBatchSelected = room.CanBatchSelect;
        }
    }

    [RelayCommand]
    private void ShowLocalControl() => IsShowingCloudManagement = false;

    [RelayCommand]
    private void ShowCloudManagement() => IsShowingCloudManagement = true;

    [RelayCommand]
    private void OpenCloudWorkspace()
    {
        IsShowingCloudManagement = true;
        SelectedSection = 0;
        if (!IsCloudConnected)
        {
            CloudConnectionMessage = "连接后即可统一管理直播间、电脑和云存档";
        }
    }

    [RelayCommand]
    private void OpenSnapshotRestore() => SelectedSection = 1;

    [RelayCommand]
    private void ClearBatchRoomSelection()
    {
        foreach (var room in CloudRooms)
        {
            room.IsBatchSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBatchCaptureCloudSnapshots))]
    private async Task BatchCaptureCloudSnapshotsAsync(CancellationToken cancellationToken)
    {
        var roomIds = CloudRooms
            .Where(room => room.IsBatchSelected && room.CanBatchSelect)
            .Select(room => room.Id)
            .ToArray();
        await RunBatchCaptureAsync(roomIds, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanRetryFailedBatchCapture))]
    private async Task RetryFailedBatchCaptureAsync(CancellationToken cancellationToken) =>
        await RunBatchCaptureAsync(failedBatchCaptureRoomIds, cancellationToken);

    private async Task RunBatchCaptureAsync(
        Guid[] roomIds,
        CancellationToken cancellationToken)
    {
        if (roomIds.Length == 0)
        {
            return;
        }

        if (isDemoMode)
        {
            BatchCaptureResultText = $"演示：已为 {roomIds.Length} 个直播间创建独立保存任务。";
            return;
        }

        if (cloudCredentials is null || SelectedOrganization is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var response = await cloudClient.CreateBatchCaptureJobsAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                new CreateBatchCaptureJobsRequest(
                    roomIds,
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture)),
                cancellationToken);
            var failures = response.Results.Where(result => !result.Accepted).ToArray();
            failedBatchCaptureRoomIds = failures.Select(result => result.RoomId).ToArray();
            HasBatchCaptureFailures = failures.Length > 0;
            var failureSummary = failures.Length == 0
                ? string.Empty
                : "；" + string.Join("；", failures.Take(3).Select(result => $"{result.RoomName}：{result.Message}"))
                    + (failures.Length > 3 ? $"；另有 {failures.Length - 3} 个未创建" : string.Empty);
            BatchCaptureResultText = $"已下发 {response.Accepted} 个保存任务，{response.Rejected} 个未创建{failureSummary}";
            ControlStatusTitle = response.Accepted > 0 ? "批量保存任务已下发" : "没有创建保存任务";
            ControlStatusDescription = BatchCaptureResultText;
            await LoadCloudWorkspaceAsync(cancellationToken);
            foreach (var room in CloudRooms)
            {
                room.IsBatchSelected = failedBatchCaptureRoomIds.Contains(room.Id) && room.CanBatchSelect;
            }
        }
        catch (HttpRequestException exception)
        {
            HasBatchCaptureFailures = true;
            failedBatchCaptureRoomIds = roomIds.ToArray();
            BatchCaptureResultText = $"批量保存未下发：{exception.Message}";
            ControlStatusTitle = "无法下发批量保存任务";
            ControlStatusDescription = BatchCaptureResultText;
        }
        finally
        {
            IsBusy = false;
            RetryFailedBatchCaptureCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<LocalSnapshotOperationResult> RestoreLocalSnapshotWithProgressAsync(
        Guid snapshotId,
        string snapshotName,
        IReadOnlyList<CameraStationSnapshot> currentCameraStations,
        CancellationToken cancellationToken)
    {
        var restoreTask = localAgentClient.RestoreAsync(
            snapshotId,
            currentCameraStations,
            cancellationToken);
        var lastMessage = string.Empty;
        while (!restoreTask.IsCompleted)
        {
            await Task.WhenAny(restoreTask, Task.Delay(500, CancellationToken.None));
            if (restoreTask.IsCompleted)
            {
                break;
            }

            try
            {
                var progress = await localAgentClient.GetOperationProgressAsync(CancellationToken.None);
                if (progress.IsBusy
                    && !string.IsNullOrWhiteSpace(progress.Message)
                    && !string.Equals(progress.Message, lastMessage, StringComparison.Ordinal))
                {
                    lastMessage = progress.Message;
                    ControlStatusTitle = $"正在恢复“{snapshotName}”";
                    ControlStatusDescription = progress.Message;
                    PendingImportMessage = $"恢复进度：{progress.Message}";
                }
            }
            catch (Exception exception) when (exception is LocalControlException or IOException)
            {
                // The main restore pipe is authoritative. A transient progress-poll failure must
                // not cancel or duplicate the transaction.
                _ = exception;
            }
        }

        return await restoreTask;
    }

    private async Task RestoreCloudSnapshotAsync(CancellationToken cancellationToken)
    {
        if (SelectedSnapshot is null
            || cloudCredentials is null
            || SelectedOrganization is null
            || SelectedCloudRoom?.DeviceId is not { } deviceId)
        {
            return;
        }

        IsBusy = true;
        ControlStatusTitle = $"正在向“{SelectedCloudRoom.Name}”下发恢复任务";
        ControlStatusDescription = "目标电脑会先完成设备映射和版本兼容性检查。";
        try
        {
            await cloudClient.CreateRestoreJobAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                new CreateRestoreJobRequest(
                    SelectedCloudRoom.Id,
                    deviceId,
                    SelectedSnapshot.Id),
                cancellationToken);
            ControlStatusTitle = "恢复任务已下发";
            ControlStatusDescription = "执行端完成验证或回滚后会把最终结果写入操作记录。";
            await LoadCloudWorkspaceAsync(cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            ControlStatusTitle = "无法下发恢复任务";
            ControlStatusDescription = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshCloudPreviewAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await RunDemoOperationAsync(
                JobKind.RefreshPreview,
                "正在演示回读当前参数并生成 OBS 与直播伴侣预览",
                "演示画面与当前参数已刷新",
                cancellationToken);
            ApplyDemoRoomState(SelectedCloudRoom);
            return;
        }

        if (cloudCredentials is null
            || SelectedOrganization is null
            || SelectedCloudRoom?.DeviceId is not { } deviceId)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await cloudClient.CreateRefreshPreviewJobAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                new CreateRefreshPreviewJobRequest(SelectedCloudRoom.Id, deviceId),
                cancellationToken);
            ControlStatusTitle = "刷新任务已下发";
            ControlStatusDescription = "Windows 执行端正在回读当前参数并生成两套应用预览。";
            await LoadCloudWorkspaceAsync(cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            ControlStatusTitle = "无法刷新当前画面";
            ControlStatusDescription = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigureObs))]
    private async Task ConfigureObsAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await Task.Yield();
            ObsPassword = string.Empty;
            SettingsMessage = "演示模式：OBS WebSocket 地址与密码格式校验通过，未写入系统凭据库";
            return;
        }

        if (!Uri.TryCreate(ObsEndpoint.Trim(), UriKind.Absolute, out var endpoint))
        {
            SettingsMessage = "请输入有效的 OBS WebSocket 地址";
            return;
        }

        IsBusy = true;
        SettingsMessage = "正在验证并保存 OBS 连接设置…";
        try
        {
            var state = await localAgentClient.ConfigureObsAsync(endpoint, ObsPassword, cancellationToken);
            ObsPassword = string.Empty;
            SettingsMessage = "OBS 连接设置已保存到当前 Windows 用户";
            ApplyAgentState(state);
        }
        catch (Exception exception) when (exception is LocalControlException or IOException or ArgumentException)
        {
            SettingsMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigureObs))]
    private async Task AutoConfigureObsAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await Task.Yield();
            SettingsMessage = "演示模式：已模拟自动连接 OBS，未修改本机配置";
            return;
        }

        IsBusy = true;
        SettingsMessage = "正在自动检测并连接 OBS 与直播伴侣…";
        ControlStatusDescription = "正在启动并核对两款应用；无需填写地址或凭据。";
        try
        {
            var state = await localAgentClient.AutoConfigureObsAsync(cancellationToken);
            ApplyAgentState(state);
            SettingsMessage = $"检测完成：OBS {ObsConnectionState}；直播伴侣 {LiveCompanionConnectionState}";
            ControlStatusDescription = state.CanCapture
                ? "OBS 与直播伴侣配置均可读取，可以保存当前画面。"
                : "至少一款应用尚未就绪，请按上方实际状态处理后重新检测。";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            SettingsMessage = exception.Message;
            ControlStatusDescription = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportSnapshotFileAsync(
        string path,
        bool applyAfterImport = false,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        PendingImportPath = null;
        PendingImportApplyAfterImport = applyAfterImport;
        PendingImportMessage = "正在检查存档结构、哈希和签名…";
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var imported = await snapshotFileStore.ImportAsync(path, cancellationToken);
                await LoadDesktopSnapshotLibraryAsync(cancellationToken);
                SelectedSnapshot = SnapshotItems.FirstOrDefault(item =>
                    item.IsDesktopFile
                    && item.Id == imported.Inspection.Package.Snapshot.Id);
                PendingImportMessage = $"已打开“{imported.Inspection.Package.Snapshot.Name}”，结构、哈希和签名均有效";
                return;
            }

            var preview = await localAgentClient.InspectSnapshotFileAsync(path, cancellationToken);
            if (!preview.SignerTrusted)
            {
                PendingImportPath = path;
                PendingImportMessage = applyAfterImport
                    ? $"存档“{preview.Name}”来自另一台电脑。确认指纹后，将信任签名者、导入并应用：{preview.SignerFingerprintSha256}"
                    : $"存档“{preview.Name}”的签名者尚未受信任。公钥指纹：{preview.SignerFingerprintSha256}";
                return;
            }

            var result = await localAgentClient.ImportSnapshotFileAsync(path, false, cancellationToken);
            await CompleteImportedSnapshotAsync(result, applyAfterImport, cancellationToken);
        }
        catch (Exception exception) when (exception is LocalControlException
            or IOException
            or UnauthorizedAccessException
            or SnapshotPackageException)
        {
            PendingImportMessage = SnapshotOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetNativeExportBaselineAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        NativeExportAuditStatus = "正在检查修改前的原生导出包…";
        NativeExportAuditDetail = string.Empty;
        NativeExportChangedFields = [];
        try
        {
            nativeExportBaseline = await NativeExportInspector.InspectAsync(
                "修改前",
                path,
                cancellationToken);
            HasNativeExportBaseline = true;
            var fieldCount = nativeExportBaseline.Entries.Sum(entry => entry.Fields.Count);
            NativeExportHasSensitivePaths = nativeExportBaseline.SensitivePaths.Count > 0;
            NativeExportAuditStatus = "已记录修改前的原生导出包";
            NativeExportAuditDetail = $"{nativeExportBaseline.Entries.Count} 个条目 · {fieldCount} 个可核对字段 · "
                + $"{nativeExportBaseline.SensitivePaths.Count} 个敏感路径。现在只修改一个美颜参数，再选择修改后 ZIP。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            nativeExportBaseline = null;
            HasNativeExportBaseline = false;
            NativeExportHasSensitivePaths = false;
            NativeExportAuditStatus = "无法读取修改前的原生导出包";
            NativeExportAuditDetail = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CompareNativeExportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (nativeExportBaseline is null)
        {
            NativeExportAuditStatus = "请先选择修改前 ZIP";
            return;
        }

        IsBusy = true;
        NativeExportAuditStatus = "正在逐字段对比原生导出包…";
        try
        {
            var after = await NativeExportInspector.InspectAsync("修改后", path, cancellationToken);
            var difference = NativeExportReportComparer.Compare(nativeExportBaseline, after);
            NativeExportChangedFields = difference.ChangedFields
                .Select(field => new NativeExportChangedFieldViewModel(field))
                .ToArray();
            NativeExportHasSensitivePaths = difference.SensitivePaths.Count > 0;
            NativeExportAuditStatus = difference.ChangedFields.Count == 0
                ? "原生导出包中没有发现该参数变化"
                : $"发现 {difference.ChangedFields.Count} 个变化字段";
            NativeExportAuditDetail = $"变化条目 {difference.ChangedEntries.Count} 个 · "
                + $"新增字段 {difference.AddedFields.Count} 个 · 删除字段 {difference.RemovedFields.Count} 个 · "
                + $"敏感路径 {difference.SensitivePaths.Count} 个";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            NativeExportAuditStatus = "无法对比原生导出包";
            NativeExportAuditDetail = exception.Message;
            NativeExportChangedFields = [];
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ConfigureLanDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (isDemoMode)
        {
            await Task.Yield();
            LanSharedDirectory = path;
            LanSyncStatus = "已同步";
            LanSettingsMessage = "演示模式·共享目录已模拟保存，未访问真实网络路径";
            return;
        }

        IsBusy = true;
        LanSettingsMessage = "正在验证共享目录…";
        try
        {
            ApplyAgentState(await localAgentClient.ConfigureLanDirectoryAsync(path, cancellationToken));
            LanSettingsMessage = "共享目录已保存，Agent 将自动同步 .lscfg 存档";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            LanSettingsMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExportSelectedSnapshotAsync(string path, CancellationToken cancellationToken = default)
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        if (isDemoMode)
        {
            await Task.Yield();
            PendingImportMessage = "演示模式·已完成存档签名与 SHA-256 检查，不写出真实文件";
            return;
        }

        IsBusy = true;
        PendingImportMessage = "正在校验并导出存档…";
        try
        {
            if (SelectedSnapshot.IsCloud)
            {
                if (cloudCredentials is null
                    || SelectedOrganization is null
                    || SelectedSnapshot.CloudSummary is not { } summary)
                {
                    PendingImportMessage = "云端连接已失效，请重新连接后再导出";
                    return;
                }

                await cloudClient.DownloadSnapshotAsync(
                    cloudCredentials,
                    SelectedOrganization.Id,
                    summary,
                    path,
                    cancellationToken);
                PendingImportMessage = $"云存档已校验并导出到 {path}";
            }
            else
            {
                var result = await localAgentClient.ExportSnapshotFileAsync(
                    SelectedSnapshot.Id,
                    path,
                    cancellationToken);
                PendingImportMessage = $"已导出到 {result.Path}";
            }
        }
        catch (Exception exception) when (exception is LocalControlException or IOException or HttpRequestException)
        {
            PendingImportMessage = SnapshotOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmPendingImport))]
    private async Task TrustAndImportAsync(CancellationToken cancellationToken)
    {
        var path = PendingImportPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await localAgentClient.ImportSnapshotFileAsync(path, true, cancellationToken);
            PendingImportPath = null;
            await CompleteImportedSnapshotAsync(result, PendingImportApplyAfterImport, cancellationToken);
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            PendingImportMessage = SnapshotOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmPendingImport))]
    private void CancelPendingImport()
    {
        PendingImportPath = null;
        PendingImportApplyAfterImport = false;
        PendingImportMessage = "已取消导入";
    }

    private static string SnapshotOperationError(Exception exception)
    {
        if (exception is SnapshotSensitiveDataException
            || exception is LocalControlException { ErrorCode: nameof(SnapshotSensitiveDataException) }
            || exception.Message.Contains("禁止归档敏感字段", StringComparison.Ordinal))
        {
            return "这份历史存档含敏感配置，已停止打开、导出和同步。请重新保存当前画面生成安全存档。";
        }

        return exception.Message;
    }

    [RelayCommand(CanExecute = nameof(CanConfigureObs))]
    private async Task DisableLanDirectoryAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            await Task.Yield();
            LanSharedDirectory = "未配置";
            LanSyncStatus = "已停用";
            LanSettingsMessage = "演示模式·局域网存档同步已模拟停用";
            return;
        }

        IsBusy = true;
        try
        {
            ApplyAgentState(await localAgentClient.ConfigureLanDirectoryAsync(null, cancellationToken));
            LanSettingsMessage = "已停用局域网存档同步";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            LanSettingsMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenConnectionSettings() => SelectedSection = 3;

    [RelayCommand]
    private void SelectSection(string section)
    {
        if (int.TryParse(section, out var index))
        {
            SelectedSection = index;
        }
    }

    private async Task CompleteImportedSnapshotAsync(
        SnapshotTransferResult result,
        bool applyAfterImport,
        CancellationToken cancellationToken)
    {
        await LoadStateAsync(cancellationToken);
        SelectedSnapshot = SnapshotItems.FirstOrDefault(snapshot => snapshot.Id == result.SnapshotId);
        SelectedSection = 1;
        if (SelectedSnapshot is null)
        {
            PendingImportMessage = $"已导入“{result.Name}”，但暂时无法在本机存档列表中打开";
            return;
        }

        if (!applyAfterImport)
        {
            PendingImportMessage = $"已导入并打开“{result.Name}”";
            return;
        }

        IsBusy = false;
        if (!CanRestoreSnapshot())
        {
            PendingImportMessage = $"已导入“{result.Name}”，但当前电脑尚未通过恢复条件检查；请先执行“本机检查与修复”";
            return;
        }

        PendingImportMessage = $"已导入“{result.Name}”，正在检查设备对应并应用…";
        await RestoreSnapshotAsync(cancellationToken);
        if (IsRestorePreparationOpen)
        {
            PendingImportMessage = $"已导入“{result.Name}”；确认来源与设备对应后即可继续应用";
        }
    }

    public async Task RenameSnapshotAsync(
        LocalSnapshotItemViewModel snapshot,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalizedName = name.Trim();
        if (snapshot.IsDesktopFile || normalizedName.Length is < 1 or > 120)
        {
            PendingImportMessage = normalizedName.Length is < 1 or > 120
                ? "存档名称必须为 1 至 120 个字符"
                : "当前文件不是受管存档，不能在此修改名称";
            return;
        }

        IsBusy = true;
        try
        {
            if (snapshot.IsCloud)
            {
                if (cloudCredentials is null || SelectedOrganization is null)
                {
                    PendingImportMessage = "云端连接已失效，请重新连接后再重命名";
                    return;
                }

                await cloudClient.RenameSnapshotAsync(
                    cloudCredentials,
                    SelectedOrganization.Id,
                    snapshot.Id,
                    normalizedName,
                    cancellationToken);
                await LoadCloudWorkspaceAsync(cancellationToken);
            }
            else
            {
                await localAgentClient.RenameSnapshotAsync(snapshot.Id, normalizedName, cancellationToken);
                await LoadStateAsync(cancellationToken);
            }

            SelectedSnapshot = SnapshotItems.FirstOrDefault(item => item.Id == snapshot.Id);
            PendingImportMessage = $"已将存档改名为“{normalizedName}”";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException or HttpRequestException)
        {
            PendingImportMessage = SnapshotOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowManualCameraMode() => IsManualCameraMode = true;

    [RelayCommand]
    private void ShowWiredCameraMode() => IsManualCameraMode = false;

    [RelayCommand]
    private void OpenSonyCameraSdkPage() => systemBrowser.Open(
        new Uri("https://support.d-imaging.sony.co.jp/app/sdk/en/"));

    [RelayCommand]
    private async Task SaveCameraStationAsync(
        CameraStationEditorViewModel? station,
        CancellationToken cancellationToken)
    {
        if (station?.SelectedCreativeLook is null)
        {
            return;
        }

        if (!station.TryGetCreativeLookSettings(out var lookSettings, out var lookError))
        {
            station.StatusText = lookError;
            return;
        }

        if (!CameraProfileInput.TryNormalize(
                station.Name,
                station.Aperture,
                station.ShutterSpeed,
                station.Iso,
                station.SelectedCreativeLook.Code,
                lookSettings,
                out var input,
                out var error))
        {
            station.StatusText = error;
            return;
        }

        IsCameraProfileBusy = true;
        try
        {
            var id = station.ProfileId ?? Guid.NewGuid();
            var saved = new CameraProfile(
                id,
                input.Name,
                DateTimeOffset.Now,
                CameraProfileMode.Manual,
                input.Aperture,
                input.ShutterSpeed,
                input.Iso,
                input.CreativeLook,
                CreativeLookSettings: input.CreativeLookSettings,
                StationSlot: station.Slot);
            var profiles = CameraProfiles
                .Select(item => item.Profile)
                .Where(profile => profile.Id != id && profile.StationSlot != station.Slot)
                .Append(saved)
                .OrderByDescending(profile => profile.UpdatedAt)
                .ToArray();
            await cameraProfileStore.SaveAsync(profiles, cancellationToken);
            ApplyCameraProfiles(profiles, selectedId: null);
            ApplyCameraStations(profiles);
            CameraProfileMessage = $"已保存“{saved.Name}”的完整相机参数";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            station.StatusText = $"保存失败：{exception.Message}";
        }
        finally
        {
            IsCameraProfileBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteCameraStationAsync(
        CameraStationEditorViewModel? station,
        CancellationToken cancellationToken)
    {
        if (station is null)
        {
            return;
        }

        if (station.ProfileId is not { } profileId)
        {
            station.Reset();
            return;
        }

        IsCameraProfileBusy = true;
        var deletedName = station.Name;
        try
        {
            var profiles = CameraProfiles
                .Where(item => item.Id != profileId)
                .Select(item => item.Profile)
                .ToArray();
            await cameraProfileStore.SaveAsync(profiles, cancellationToken);
            ApplyCameraProfiles(profiles, selectedId: null);
            ApplyCameraStations(profiles);
            CameraProfileMessage = $"已删除“{deletedName}”";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            station.StatusText = $"删除失败：{exception.Message}";
        }
        finally
        {
            IsCameraProfileBusy = false;
        }
    }

    [RelayCommand]
    private void NewCameraProfile()
    {
        SelectedCameraProfile = null;
        CameraProfileName = string.Empty;
        CameraAperture = "F4";
        CameraShutterSpeed = "1/125";
        CameraIso = "640";
        SelectedCameraCreativeLook = CameraCreativeLooks[0];
        ApplyCameraCreativeLookSettings(CameraCreativeLookSettings.StandardDefault);
        IsManualCameraMode = true;
        CameraProfileMessage = "填写曝光参数和创意外观完整参数后保存；档案名称可使用直播间名称。";
    }

    [RelayCommand]
    private async Task SaveManualCameraProfileAsync(CancellationToken cancellationToken)
    {
        if (!CanSaveManualCameraProfile || SelectedCameraCreativeLook is null)
        {
            CameraProfileMessage = "请填写档案名称、光圈、快门、ISO 和创意外观";
            return;
        }

        if (!CameraProfileInput.TryNormalize(
                CameraProfileName,
                CameraAperture,
                CameraShutterSpeed,
                CameraIso,
                SelectedCameraCreativeLook.Code,
                new CameraCreativeLookSettings(
                    CameraLookContrast,
                    CameraLookHighlights,
                    CameraLookShadows,
                    CameraLookFade,
                    CameraLookSaturation,
                    CameraLookSharpness,
                    CameraLookSharpnessRange,
                    CameraLookClarity),
                out var input,
                out var error))
        {
            CameraProfileMessage = error;
            return;
        }

        IsCameraProfileBusy = true;
        try
        {
            var id = SelectedCameraProfile?.Id ?? Guid.NewGuid();
            var saved = new CameraProfile(
                id,
                input.Name,
                DateTimeOffset.Now,
                CameraProfileMode.Manual,
                input.Aperture,
                input.ShutterSpeed,
                input.Iso,
                input.CreativeLook,
                CreativeLookSettings: input.CreativeLookSettings);
            var profiles = CameraProfiles
                .Select(item => item.Profile)
                .Where(profile => profile.Id != id)
                .Append(saved)
                .OrderByDescending(profile => profile.UpdatedAt)
                .ToArray();
            await cameraProfileStore.SaveAsync(profiles, cancellationToken);
            ApplyCameraProfiles(profiles, id);
            CameraProfileMessage = $"已保存“{saved.Name}”的曝光与创意外观完整参数";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            CameraProfileMessage = $"相机档案保存失败：{exception.Message}";
        }
        finally
        {
            IsCameraProfileBusy = false;
        }
    }

    private void ApplyCameraCreativeLookSettings(CameraCreativeLookSettings settings)
    {
        CameraLookContrast = settings.Contrast;
        CameraLookHighlights = settings.Highlights;
        CameraLookShadows = settings.Shadows;
        CameraLookFade = settings.Fade;
        CameraLookSaturation = settings.Saturation;
        CameraLookSharpness = settings.Sharpness;
        CameraLookSharpnessRange = settings.SharpnessRange;
        CameraLookClarity = settings.Clarity;
    }

    [RelayCommand]
    private void RequestDeleteCameraProfile()
    {
        if (SelectedCameraProfile is not null)
        {
            IsCameraDeleteConfirmationVisible = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteCameraProfile() => IsCameraDeleteConfirmationVisible = false;

    [RelayCommand]
    private async Task ConfirmDeleteCameraProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedCameraProfile is null)
        {
            IsCameraDeleteConfirmationVisible = false;
            return;
        }

        IsCameraProfileBusy = true;
        var deletedName = SelectedCameraProfile.Name;
        try
        {
            var profiles = CameraProfiles
                .Where(item => item.Id != SelectedCameraProfile.Id)
                .Select(item => item.Profile)
                .ToArray();
            await cameraProfileStore.SaveAsync(profiles, cancellationToken);
            ApplyCameraProfiles(profiles, profiles.FirstOrDefault()?.Id);
            if (profiles.Length == 0)
            {
                NewCameraProfile();
            }

            CameraProfileMessage = $"已删除“{deletedName}”";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            CameraProfileMessage = $"相机档案删除失败：{exception.Message}";
        }
        finally
        {
            IsCameraDeleteConfirmationVisible = false;
            IsCameraProfileBusy = false;
        }
    }

    [RelayCommand]
    private async Task DetectSonyCameraAsync(CancellationToken cancellationToken)
    {
        IsCameraProfileBusy = true;
        SonyCameraConnectionState = "正在检测 USB 相机…";
        try
        {
            DetectedSonyCameras = await sonyCameraDetectionService.DetectAsync(cancellationToken);
            SelectedSonyCamera = DetectedSonyCameras.Count > 0 ? DetectedSonyCameras[0] : null;
            SonyCameraConnectionState = SelectedSonyCamera is null
                ? "未检测到 FX3、α7S III 或 FX30；请连接 USB 数据线并选择 PC 遥控模式"
                : $"已检测到 {SelectedSonyCamera.Name}";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException)
        {
            DetectedSonyCameras = [];
            SelectedSonyCamera = null;
            SonyCameraConnectionState = $"相机检测失败：{exception.Message}";
        }
        finally
        {
            IsCameraProfileBusy = false;
        }
    }

    private async Task LoadCameraProfilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var profiles = await cameraProfileStore.LoadAsync(cancellationToken);
            ApplyCameraProfiles(profiles, profiles.Count > 0 ? profiles[0].Id : null);
            ApplyCameraStations(profiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            CameraProfiles = [];
            SelectedCameraProfile = null;
            ApplyCameraStations([]);
            CameraProfileMessage = $"相机档案读取失败：{exception.Message}";
        }
    }

    private void ApplyCameraProfiles(IReadOnlyList<CameraProfile> profiles, Guid? selectedId)
    {
        CameraProfiles = profiles
            .OrderByDescending(profile => profile.UpdatedAt)
            .Select(profile => new CameraProfileItemViewModel(profile))
            .ToArray();
        SelectedCameraProfile = selectedId is { } id
            ? CameraProfiles.FirstOrDefault(profile => profile.Id == id)
            : null;
    }

    private void ApplyCameraStations(IReadOnlyList<CameraProfile> profiles)
    {
        foreach (var station in CameraStations)
        {
            station.Reset();
        }

        var assignedIds = new HashSet<Guid>();
        foreach (var profile in profiles.Where(profile => profile.StationSlot is >= 0 and <= 2))
        {
            var station = CameraStations[profile.StationSlot!.Value];
            if (station.ProfileId is null)
            {
                station.Apply(profile);
                assignedIds.Add(profile.Id);
            }
        }

        var availableStations = new Queue<CameraStationEditorViewModel>(
            CameraStations.Where(station => station.ProfileId is null));
        foreach (var legacyProfile in profiles.Where(profile => !assignedIds.Contains(profile.Id)))
        {
            if (!availableStations.TryDequeue(out var station))
            {
                break;
            }

            station.Apply(legacyProfile);
        }
    }

    [RelayCommand]
    private async Task LoadMappingContextAsync(CancellationToken cancellationToken)
    {
        if (isDemoMode)
        {
            if (SelectedSnapshot is null)
            {
                IsMappingContextReady = false;
                MappingMessage = "先在画面存档中选择一份演示存档";
                return;
            }

            var roomNumber = DemoWorkspaceData.GetRoomNumber(SelectedSnapshot.RoomName);
            ApplyMappingContext(DemoWorkspaceData.CreateMappingContext(SelectedSnapshot.Id, roomNumber));
            IsMappingContextReady = true;
            MappingMessage = "演示模式 · 已回读存档来源、目标采集卡和完整视频模式";
            return;
        }

        if (SelectedSnapshot?.IsCloud == true || !OperatingSystem.IsWindows())
        {
            await LoadCloudMappingContextAsync(cancellationToken);
            return;
        }

        if (SelectedSnapshot is null)
        {
            IsMappingContextReady = false;
            MappingMessage = "先在画面存档中选择一份存档";
            return;
        }

        IsBusy = true;
        IsMappingContextReady = false;
        MappingMessage = "正在回读存档来源和目标采集设备…";
        try
        {
            ApplyMappingContext(await localAgentClient.GetMappingContextAsync(
                SelectedSnapshot.Id,
                cancellationToken));
            IsMappingContextReady = true;
            MappingMessage = MappingSources.Count == 0
                ? "这份存档没有可映射的视频来源"
                : "选择存档来源和当前电脑上的目标来源";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            IsMappingContextReady = false;
            MappingMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveMapping))]
    private async Task SaveMappingAsync(CancellationToken cancellationToken)
    {
        if (SelectedSnapshot is null
            || SelectedMappingSource is null
            || SelectedMappingTarget is null)
        {
            return;
        }

        if (isDemoMode)
        {
            await Task.Yield();
            MappingMessage = "演示映射已通过分辨率、FPS、像素格式和色彩模式校验；未写入真实设备";
            return;
        }

        IsBusy = true;
        MappingMessage = "正在验证目标设备的视频模式…";
        try
        {
            if (SelectedSnapshot.IsCloud)
            {
                await SaveCloudMappingAsync(cancellationToken);
                return;
            }

            var context = await localAgentClient.SaveDeviceMappingAsync(
                new SaveLocalDeviceMappingRequest(
                    SelectedSnapshot.Id,
                    SelectedMappingSource.SourceLogicalId,
                    SelectedMappingSource.Application,
                    SelectedMappingTarget.TargetDeviceId,
                    SelectedMappingTarget.SourceName),
                cancellationToken);
            var selectedSourceId = SelectedMappingSource.SourceLogicalId;
            ApplyMappingContext(context, selectedSourceId);
            MappingMessage = "设备映射已保存并通过当前能力校验";
            AdvanceRestorePreparation();
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            MappingMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadCloudMappingContextAsync(CancellationToken cancellationToken)
    {
        if (SelectedSnapshot is null)
        {
            IsMappingContextReady = false;
            MappingMessage = "先在画面存档中选择一份云端存档";
            return;
        }

        if (cloudCredentials is null
            || SelectedOrganization is null
            || SelectedCloudRoom?.DeviceId is not { } deviceId)
        {
            IsMappingContextReady = false;
            MappingMessage = "先在控制室选择一台已注册的 Windows 直播电脑";
            return;
        }

        IsBusy = true;
        IsMappingContextReady = false;
        MappingMessage = "正在读取存档来源和目标 Agent 设备能力…";
        try
        {
            var detailTask = cloudClient.GetSnapshotDetailAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                SelectedSnapshot.Id,
                cancellationToken);
            var stateTask = cloudClient.GetManagementStateAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                deviceId,
                cancellationToken);
            var mappingsTask = cloudClient.GetMappingsAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                cancellationToken);
            await Task.WhenAll(detailTask, stateTask, mappingsTask);
            var detail = await detailTask;
            var state = await stateTask;
            var mappings = (await mappingsTask).Where(mapping => mapping.DeviceId == deviceId).ToArray();
            var sources = detail.Applications.SelectMany(application => application.Sources
                    .Where(source => !string.IsNullOrWhiteSpace(source.Device?.InterfaceHint))
                    .Select(source => new LocalMappingSource(
                        source.LogicalId,
                        application.Kind,
                        source.Name,
                        source.Device!.FriendlyName,
                        source.Mode,
                        mappings.FirstOrDefault(mapping =>
                            mapping.SourceLogicalId == source.LogicalId
                            && mapping.Application == application.Kind))))
                .ToArray();
            var targets = state.Capabilities?.VideoSources
                .Where(source => !string.IsNullOrWhiteSpace(source.Device.InterfaceHint))
                .Select(source => new LocalMappingTarget(
                    source.Application,
                    source.SourceName,
                    source.Device.InterfaceHint!,
                    source.Device.FriendlyName,
                    source.CurrentMode))
                .ToArray() ?? [];
            ApplyMappingContext(new LocalMappingContext(SelectedSnapshot.Id, sources, targets));
            IsMappingContextReady = true;
            MappingMessage = sources.Length == 0
                ? "这份存档没有可映射的视频来源"
                : "选择存档来源和目标 Agent 已回读的采集来源";
        }
        catch (HttpRequestException exception)
        {
            IsMappingContextReady = false;
            MappingMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveCloudMappingAsync(CancellationToken cancellationToken)
    {
        if (cloudCredentials is null
            || SelectedOrganization is null
            || SelectedCloudRoom?.DeviceId is not { } deviceId
            || SelectedMappingSource is null
            || SelectedMappingTarget is null)
        {
            return;
        }

        await cloudClient.SaveMappingAsync(
            cloudCredentials,
            SelectedOrganization.Id,
            deviceId,
            new SaveDeviceMappingRequest(
                SelectedMappingSource.SourceLogicalId,
                SelectedMappingSource.Application,
                SelectedMappingTarget.TargetDeviceId,
                SelectedMappingTarget.SourceName,
                string.Empty,
                false),
            cancellationToken);
        var selectedSourceId = SelectedMappingSource.SourceLogicalId;
        await LoadCloudMappingContextAsync(cancellationToken);
        SelectedMappingSource = MappingSources.FirstOrDefault(source => source.SourceLogicalId == selectedSourceId);
        MappingMessage = "设备映射已保存，恢复时仍会再次校验目标格式和滤镜能力";
        AdvanceRestorePreparation();
    }

    private void AdvanceRestorePreparation()
    {
        if (!IsRestorePreparationOpen)
        {
            return;
        }

        var next = MappingSources.FirstOrDefault(source => source.Mapping is null);
        if (next is not null)
        {
            SelectedMappingSource = next;
            MappingMessage = $"已保存一个来源，还需选择 {PendingMappingCount} 个";
            return;
        }

        MappingMessage = $"全部 {MappingSources.Count} 个来源已经对应，可以继续恢复";
        ContinuePreparedRestoreCommand.NotifyCanExecuteChanged();
    }

    private async Task RunDemoOperationAsync(
        JobKind kind,
        string runningMessage,
        string completedMessage,
        CancellationToken cancellationToken)
    {
        var roomNumber = DemoWorkspaceData.GetRoomNumber(SelectedCloudRoom?.Name
            ?? SelectedSnapshot?.RoomName);
        IsBusy = true;
        ControlStatusTitle = kind switch
        {
            JobKind.Capture => "正在演示联合保存",
            JobKind.Restore => "正在演示事务恢复",
            _ => "正在演示画面刷新"
        };
        ControlStatusDescription = runningMessage;
        cloudJobs =
        [
            DemoWorkspaceData.CreateInteractiveJob(roomNumber, kind, JobStatus.Verifying, runningMessage),
            .. cloudJobs
        ];
        RefreshActivityItems();
        try
        {
            await Task.Delay(650, cancellationToken);
            cloudJobs =
            [
                DemoWorkspaceData.CreateInteractiveJob(roomNumber, kind, JobStatus.Succeeded, completedMessage),
                .. cloudJobs.Skip(1)
            ];
            ControlStatusTitle = completedMessage;
            ControlStatusDescription = "这是安全的本地演示，没有连接真实 Agent，也没有改写 OBS 或直播伴侣。";
            RefreshActivityItems();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCaptureSnapshot() =>
        (isDemoMode || OperatingSystem.IsWindows() && CanControlLocalApplications) && !IsBusy;

    private bool CanCaptureCloudSnapshot() =>
        IsCloudConnected && SelectedCloudRoom is { Online: true, DeviceId: not null } && !IsBusy;

    private bool CanBatchCaptureCloudSnapshots() =>
        IsCloudConnected && BatchSelectedRoomCount > 0 && !IsBusy;

    private bool CanRetryFailedBatchCapture() =>
        IsCloudConnected && failedBatchCaptureRoomIds.Length > 0 && !IsBusy;

    private bool CanEnrollAgent() =>
        OperatingSystem.IsWindows()
        && IsAgentConnected
        && IsCloudConnected
        && !IsAgentCloudEnrolled
        && SelectedOrganization is not null
        && SelectedCloudRoom is { DeviceId: null }
        && !IsBusy;

    private bool CanRestoreSnapshot() =>
        SelectedSnapshot is not null
        && !IsBusy
        && (isDemoMode
            || (SelectedSnapshot.IsCloud
                ? IsCloudConnected && SelectedCloudRoom is { Online: true, DeviceId: not null }
                : !SelectedSnapshot.IsDesktopFile
                    && OperatingSystem.IsWindows()
                    && CanRestoreLocalApplications));

    private bool CanConfigureObs() => (isDemoMode || OperatingSystem.IsWindows()) && !IsBusy;

    private bool CanRunLocalCheckAndRepair() => supportsLocalAgent && !IsBusy;

    private bool CanCheckForUpdates() => !IsBusy;

    private bool CanInstallUpdate() =>
        OperatingSystem.IsWindows() && availableUpdate is not null && !IsBusy;

    private bool CanConfirmPendingImport() =>
        OperatingSystem.IsWindows() && !IsBusy && !string.IsNullOrWhiteSpace(PendingImportPath);

    private bool CanSaveMapping() => !IsBusy
        && (isDemoMode || (SelectedSnapshot?.IsCloud == true ? IsCloudConnected : OperatingSystem.IsWindows()))
        && SelectedMappingSource is not null
        && SelectedMappingTarget is not null
        && SelectedMappingSource.Application == SelectedMappingTarget.Application;

    private void ApplyMappingContext(LocalMappingContext context, Guid? selectedSourceId = null)
    {
        allMappingTargets = context.Targets
            .Select(target => new LocalMappingTargetItemViewModel(target))
            .ToArray();
        MappingSources = context.Sources
            .Select(source => new LocalMappingSourceItemViewModel(source))
            .ToArray();
        SelectedMappingSource = selectedSourceId is { } id
            ? MappingSources.FirstOrDefault(source => source.SourceLogicalId == id)
            : MappingSources.Count > 0 ? MappingSources[0] : null;
    }

    private async Task LoadCloudWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (cloudCredentials is null)
        {
            return;
        }

        isLoadingCloudWorkspace = true;
        try
        {
            var selectedRoomId = SelectedCloudRoom?.Id;
            var workspace = await cloudClient.LoadWorkspaceAsync(
                cloudCredentials,
                cloudCredentials.SelectedOrganizationId ?? SelectedOrganization?.Id,
                cancellationToken);
            Organizations = workspace.Organizations;
            SelectedOrganization = workspace.SelectedOrganization;
            OrganizationName = SelectedOrganization?.Name ?? "尚无直播管理空间";
            if (SelectedOrganization is not null
                && cloudCredentials.SelectedOrganizationId != SelectedOrganization.Id)
            {
                cloudCredentials = cloudCredentials with { SelectedOrganizationId = SelectedOrganization.Id };
                cloudClient.SaveCredentials(cloudCredentials);
            }
            var selectedBatchRoomIds = CloudRooms
                .Where(room => room.IsBatchSelected)
                .Select(room => room.Id)
                .ToHashSet();
            var cloudRooms = workspace.Rooms.Select(room => new CloudRoomItemViewModel(room)).ToArray();
            foreach (var room in cloudRooms)
            {
                room.PropertyChanged += OnCloudRoomPropertyChanged;
                room.IsBatchSelected = room.CanBatchSelect
                    && (!batchSelectionInitialized || selectedBatchRoomIds.Contains(room.Id));
            }
            CloudRooms = cloudRooms;
            ApplyKnownRoomNames();
            batchSelectionInitialized = true;
            NotifyBatchSelectionChanged();
            cloudJobs = workspace.Jobs;
            RefreshActivityItems();
            SelectedCloudRoom = CloudRooms.FirstOrDefault(room => room.Id == selectedRoomId)
                ?? (CloudRooms.Count > 0 ? CloudRooms[0] : null);
            cloudSnapshotItems = workspace.Snapshots
                .Select(snapshot => new LocalSnapshotItemViewModel(snapshot))
                .ToArray();
            RefreshAvailableSnapshots();

            IsCloudConnected = true;
            CloudStatus = "已连接";
            CloudConnectionMessage = SelectedOrganization is null
                ? "账号下尚无直播管理空间，请联系服务器管理员"
                : $"已连接 {SelectedOrganization.Name}";
            if (!IsAgentConnected)
            {
                ConnectionSubtitle = $"{OrganizationName} · {CloudRooms.Count} 个直播间";
                ControlStatusTitle = SelectedCloudRoom is null
                    ? "尚未创建直播间"
                    : SelectedCloudRoom.Name;
                ControlStatusDescription = SelectedCloudRoom is null
                    ? "请先在云端控制台创建直播间并注册 Windows 执行端。"
                    : $"{SelectedCloudRoom.Status} · {SelectedCloudRoom.ConfigurationStatus} · {SelectedCloudRoom.LastSnapshot}";
                AgentStatus = SelectedCloudRoom?.Status ?? "无设备";
                ConfigurationStatus = SelectedCloudRoom?.ConfigurationStatus ?? "无数据";
            }

            if (SelectedCloudRoom is not null)
            {
                await LoadSelectedCloudRoomStateAsync(cancellationToken);
            }

            CaptureCloudSnapshotCommand.NotifyCanExecuteChanged();
            BatchCaptureCloudSnapshotsCommand.NotifyCanExecuteChanged();
            RestoreSnapshotCommand.NotifyCanExecuteChanged();
        }
        catch (HttpRequestException exception)
        {
            IsCloudConnected = false;
            cloudSnapshotItems = [];
            RefreshAvailableSnapshots();
            CloudConnectionMessage = $"无法读取云端：{exception.Message}";
            if (!IsAgentConnected)
            {
                ApplyDisconnectedState(exception.Message);
            }
        }
        finally
        {
            isLoadingCloudWorkspace = false;
        }
    }

    private async Task LoadSelectedCloudRoomStateAsync(CancellationToken cancellationToken)
    {
        if (cloudCredentials is null
            || SelectedOrganization is null
            || SelectedCloudRoom?.DeviceId is not { } deviceId)
        {
            CurrentObsPreviewUrl = string.Empty;
            CurrentLiveCompanionPreviewUrl = string.Empty;
            CurrentObsPreview = null;
            CurrentLiveCompanionPreview = null;
            return;
        }

        try
        {
            var state = await cloudClient.GetManagementStateAsync(
                cloudCredentials,
                SelectedOrganization.Id,
                deviceId,
                cancellationToken);
            CurrentObsPreviewUrl = state.CurrentPreviewUrls.TryGetValue(ApplicationKind.Obs, out var obsPreview)
                ? obsPreview.ToString()
                : string.Empty;
            CurrentLiveCompanionPreviewUrl = state.CurrentPreviewUrls.TryGetValue(
                ApplicationKind.LiveCompanion,
                out var liveCompanionPreview)
                ? liveCompanionPreview.ToString()
                : string.Empty;
            var obsPreviewTask = LoadPreviewBitmapAsync(obsPreview, cancellationToken);
            var liveCompanionPreviewTask = LoadPreviewBitmapAsync(liveCompanionPreview, cancellationToken);
            await Task.WhenAll(obsPreviewTask, liveCompanionPreviewTask);
            CurrentObsPreview = await obsPreviewTask;
            CurrentLiveCompanionPreview = await liveCompanionPreviewTask;
            ObsStatus = GetCloudApplicationStatus(state, ApplicationKind.Obs);
            LiveCompanionStatus = GetCloudApplicationStatus(state, ApplicationKind.LiveCompanion);
            AgentStatus = state.Device.Online ? "在线" : "离线";
        }
        catch (HttpRequestException exception)
        {
            ControlStatusDescription = $"无法读取当前参数：{exception.Message}";
        }
    }

    private async Task<Bitmap?> LoadPreviewBitmapAsync(Uri? previewUri, CancellationToken cancellationToken)
    {
        if (previewUri is null)
        {
            return null;
        }

        try
        {
            var content = await cloudClient.DownloadPreviewAsync(previewUri, cancellationToken);
            return new Bitmap(new MemoryStream(content, writable: false));
        }
        catch (Exception exception) when (exception is HttpRequestException or ArgumentException)
        {
            return null;
        }
    }

    private void ApplyDemoWorkspace()
    {
        isLoadingCloudWorkspace = true;
        try
        {
            Organizations = [DemoWorkspaceData.Organization];
            SelectedOrganization = DemoWorkspaceData.Organization;
            OrganizationName = DemoWorkspaceData.Organization.Name;
            var demoRooms = DemoWorkspaceData.CreateRooms()
                .Select(room => new CloudRoomItemViewModel(room))
                .ToArray();
            foreach (var room in demoRooms)
            {
                room.PropertyChanged += OnCloudRoomPropertyChanged;
                room.IsBatchSelected = room.CanBatchSelect;
            }
            CloudRooms = demoRooms;
            batchSelectionInitialized = true;
            NotifyBatchSelectionChanged();
            cloudJobs = DemoWorkspaceData.CreateJobs();
            RefreshActivityItems();
            SelectedCloudRoom = CloudRooms[0];
        }
        finally
        {
            isLoadingCloudWorkspace = false;
        }

        IsAgentConnected = false;
        IsCloudConnected = true;
        IsAgentCloudEnrolled = true;
        CanControlLocalApplications = true;
        CanRestoreLocalApplications = true;
        IsAgentAutoStartEnabled = true;
        CloudServiceUrl = "https://demo.livestudio.local";
        CloudStatus = "演示已连接";
        CloudConnectionMessage = "演示模式 · 14 个直播间、设备、存档和任务均来自本机，不连接真实云端";
        ConnectionSubtitle = "LiveStudio 演示空间 · 14 个直播间 · 本地安全演示";
        LatestVersionText = CurrentVersionText;
        UpdateStatus = "演示状态 · 公开 Release、SHA-256 和签名校验链路已配置";
        LanSharedDirectory = @"\\演示文件服务器\LiveStudio\Snapshots";
        LanSyncStatus = "已同步";
        LanSettingsMessage = "演示状态 · 局域网目录与云端使用同一种不可变 .lscfg 格式";
        ObsEndpoint = "ws://127.0.0.1:4455";
        SettingsMessage = "演示状态 · OBS 密码只进入 Windows Credential Manager";
        HasNativeExportBaseline = true;
        NativeExportHasSensitivePaths = false;
        NativeExportAuditStatus = "演示对比完成 · 4 个美颜字段发生变化";
        NativeExportAuditDetail = "变化条目 2 个 · 新增字段 0 个 · 删除字段 0 个 · 敏感路径 0 个";
        NativeExportChangedFields =
        [
            new NativeExportChangedFieldViewModel("beauty.json:/skin/smoothness"),
            new NativeExportChangedFieldViewModel("beauty.json:/reshape/faceSlim"),
            new NativeExportChangedFieldViewModel("beauty.json:/curves/master/points/2/y"),
            new NativeExportChangedFieldViewModel("beauty.json:/makeup/opacity")
        ];
        MappingMessage = "演示模式 · 选择存档后可查看来源、采集卡和格式映射";
        ApplyDemoRoomState(SelectedCloudRoom);
        RefreshSnapshotNavigation(SelectedSnapshot);
        if (SelectedSnapshot is not null)
        {
            ApplyMappingContext(DemoWorkspaceData.CreateMappingContext(
                SelectedSnapshot.Id,
                DemoWorkspaceData.GetRoomNumber(SelectedSnapshot.RoomName)));
        }

        CaptureCloudSnapshotCommand.NotifyCanExecuteChanged();
        BatchCaptureCloudSnapshotsCommand.NotifyCanExecuteChanged();
        CaptureSnapshotCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
        ConfigureObsCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        SaveMappingCommand.NotifyCanExecuteChanged();
    }

    private void ApplyDemoRoomState(CloudRoomItemViewModel? room)
    {
        if (!isDemoMode || room is null)
        {
            return;
        }

        var roomNumber = DemoWorkspaceData.GetRoomNumber(room.Name);
        var matchingFilter = SnapshotRoomFilters.FirstOrDefault(filter =>
            string.Equals(filter.Name, room.Name, StringComparison.Ordinal));
        if (matchingFilter is not null && SelectedSnapshotRoomFilter?.RoomId != matchingFilter.RoomId)
        {
            SelectedSnapshotRoomFilter = matchingFilter;
        }

        CurrentObsPreview = DemoWorkspaceData.CreatePreview(roomNumber, ApplicationKind.Obs);
        CurrentLiveCompanionPreview = DemoWorkspaceData.CreatePreview(roomNumber, ApplicationKind.LiveCompanion);
        CurrentObsPreviewUrl = $"demo://rooms/{roomNumber}/obs";
        CurrentLiveCompanionPreviewUrl = $"demo://rooms/{roomNumber}/live-companion";
        AgentStatus = room.Online ? "在线 · Windows 11" : "离线";
        ObsStatus = room.Online ? "31.1.2 · 正常运行 · 未推流" : "设备离线";
        LiveCompanionStatus = room.Online ? "8.1.0 · 正常运行 · 未开播" : "设备离线";
        ConfigurationStatus = room.HasConfigurationDrift ? "检测到 3 项配置漂移" : "逐字段一致";
        ControlStatusTitle = room.Name;
        ControlStatusDescription = $"{room.Status} · {room.ConfigurationStatus} · 最近备份 {room.LastSnapshot}";
        CaptureCloudSnapshotCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
    }

    private void ApplyAgentState(LocalAgentState state)
    {
        IsAgentConnected = true;
        ConnectionSubtitle = state.StatusMessage;
        ControlStatusTitle = state.CanCapture ? "可以开始备份" : state.StatusMessage;
        ControlStatusDescription = state.CanCapture
            ? "OBS 与直播伴侣已经连接。画面调好后，保存一份新存档即可。"
            : "点击重新检测；连接正常后即可保存或恢复。";
        AgentStatus = state.IsBusy ? "正在执行任务" : "已连接";
        CloudStatus = state.IsCloudEnrolled ? "已注册" : "未注册";
        IsAgentCloudEnrolled = state.IsCloudEnrolled;
        ConfigurationStatus = state.CanCapture ? "连接正常" : "需要处理";
        LanSharedDirectory = state.LanSharedDirectory ?? "未配置";
        LanSyncStatus = state.LanSyncStatus;
        CanControlLocalApplications = state.CanCapture;
        CanRestoreLocalApplications = state.CanRestore;
        IsBusy = state.IsBusy;
        IsAgentAutoStartEnabled = state.AutoStartEnabled;
        localSnapshotItems = state.Snapshots
            .Select(snapshot => new LocalSnapshotItemViewModel(
                snapshot,
                ResolveRoomName(snapshot.RoomId)))
            .ToArray();
        RefreshAvailableSnapshots();
        localOperations = state.Operations;
        RefreshActivityItems();
        ObsStatus = GetApplicationStatus(state, ApplicationKind.Obs);
        LiveCompanionStatus = GetApplicationStatus(state, ApplicationKind.LiveCompanion);
        ApplyApplicationRuntimeState(state);
        if (SelectedSnapshot is not null)
        {
            SelectedSnapshot = SnapshotItems.FirstOrDefault(item => item.Id == SelectedSnapshot.Id);
        }
        else if (SnapshotItems.Count > 0)
        {
            SelectedSnapshot = SnapshotItems[0];
        }
    }

    private void ApplyDisconnectedState(string? error = null)
    {
        IsAgentConnected = false;
        var isWindows = OperatingSystem.IsWindows();
        ConnectionSubtitle = isWindows ? "当前用户会话中没有可用的执行端" : "连接 Windows 直播电脑后可远程管理";
        ControlStatusTitle = isWindows ? "启动 LiveStudio Agent" : "连接你的 Windows 直播电脑";
        ControlStatusDescription = error ?? (isWindows
            ? "Agent 必须与 OBS、直播伴侣运行在同一个 Windows 登录用户会话。"
            : "macOS 客户端用于查看存档、管理设备和向在线 Windows 电脑发送任务。");
        AgentStatus = "不可用";
        ObsStatus = "未连接";
        LiveCompanionStatus = "未连接";
        ObsVersionText = "版本未知";
        ObsConnectionState = "未连接";
        ObsStreamingState = "推流状态未知";
        ObsRecordingState = "录制状态未知";
        ObsReadSummary = "尚未读取画面存档";
        LiveCompanionVersionText = "版本未知";
        LiveCompanionConnectionState = "未连接";
        LiveCompanionLiveState = "开播状态未知";
        LiveCompanionAccessState = "当前不能还原";
        LiveCompanionReadSummary = "尚未读取画面存档";
        CloudStatus = "未连接";
        IsAgentCloudEnrolled = false;
        ConfigurationStatus = "尚未同步";
        CanControlLocalApplications = false;
        CanRestoreLocalApplications = false;
        IsBusy = false;
        IsAgentAutoStartEnabled = false;
        if (isWindows)
        {
            SelectedSnapshot = null;
            localSnapshotItems = [];
            SnapshotItems = [];
            SnapshotRoomFilters = [];
            SnapshotTimelineItems = [];
            SelectedSnapshotRoomFilter = null;
            SelectedSnapshotTimelineItem = null;
            SnapshotTimelineSummary = "尚无存档";
            SnapshotCountText = "0 份";
        }
        else
        {
            RefreshAvailableSnapshots();
        }

        localOperations = [];
        RefreshActivityItems();
    }

    private async Task LoadDesktopSnapshotLibraryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await snapshotFileStore.GetAllAsync(cancellationToken);
            desktopSnapshotItems = result.Snapshots
                .Select(snapshot => new LocalSnapshotItemViewModel(
                    snapshot.Inspection.Package.Snapshot,
                    snapshot.PackageLength))
                .ToArray();
            RefreshAvailableSnapshots();
            if (result.Errors.Count > 0)
            {
                PendingImportMessage = $"有 {result.Errors.Count} 份本地存档无法读取：{result.Errors[0]}";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PendingImportMessage = $"无法读取本地存档库：{exception.Message}";
        }
    }

    private void RefreshAvailableSnapshots()
    {
        var selected = SelectedSnapshot;
        var primary = IsAgentConnected ? localSnapshotItems : desktopSnapshotItems;
        var primaryIds = primary.Select(snapshot => snapshot.Id).ToHashSet();
        SnapshotItems = primary
            .Concat(cloudSnapshotItems.Where(snapshot => !primaryIds.Contains(snapshot.Id)))
            .ToArray();
        RefreshSnapshotNavigation(selected);
    }

    private void RefreshSnapshotNavigation(LocalSnapshotItemViewModel? preferredSnapshot = null)
    {
        var selectedRoomId = SelectedSnapshotRoomFilter?.RoomId;
        var snapshotGroups = SnapshotItems
            .GroupBy(snapshot => snapshot.RoomId)
            .Select(group => (group.Key, Snapshots: group.ToArray()))
            .ToArray();
        var filters = new List<SnapshotRoomFilterItemViewModel>();
        foreach (var room in CloudRooms)
        {
            var roomSnapshots = snapshotGroups.FirstOrDefault(group => group.Key == room.Id).Snapshots;
            filters.Add(new SnapshotRoomFilterItemViewModel(
                room.Id,
                room.Name,
                roomSnapshots?.Length ?? 0,
                GetRoomSortOrder(room.Name)));
        }

        foreach (var group in snapshotGroups.Where(group =>
                     group.Key is null || CloudRooms.All(room => room.Id != group.Key)))
        {
            var newest = group.Snapshots.OrderByDescending(snapshot => snapshot.CreatedAtValue).First();
            filters.Add(new SnapshotRoomFilterItemViewModel(
                group.Key,
                newest.RoomName,
                group.Snapshots.Length,
                GetRoomSortOrder(newest.RoomName)));
        }

        var orderedFilters = filters
            .GroupBy(filter => filter.RoomId)
            .Select(group => group.First())
            .OrderBy(filter => filter.SortOrder)
            .ThenBy(filter => filter.Name, StringComparer.CurrentCulture)
            .ToArray();

        isRefreshingSnapshotNavigation = true;
        try
        {
            SnapshotRoomFilters = orderedFilters;
            SelectedSnapshotRoomFilter = orderedFilters.FirstOrDefault(filter => filter.RoomId == selectedRoomId)
                ?? orderedFilters.FirstOrDefault();
        }
        finally
        {
            isRefreshingSnapshotNavigation = false;
        }

        RefreshSnapshotTimeline(false, preferredSnapshot);
    }

    private void RefreshSnapshotTimeline(
        bool selectFirst,
        LocalSnapshotItemViewModel? preferredSnapshot = null)
    {
        var filtered = (SupportsLocalAgent
                ? SnapshotItems.Where(snapshot => !snapshot.IsCloud && !snapshot.IsDesktopFile)
                : SnapshotItems)
            .OrderByDescending(snapshot => snapshot.CreatedAtValue)
            .ToArray();
        var timeline = new List<SnapshotTimelineItemViewModel>(filtered.Length);
        DateOnly? previousDate = null;
        foreach (var snapshot in filtered)
        {
            var startsDate = previousDate != snapshot.LocalDate;
            timeline.Add(new SnapshotTimelineItemViewModel(snapshot, startsDate));
            previousDate = snapshot.LocalDate;
        }

        var selectedId = preferredSnapshot?.Id
            ?? (!selectFirst ? SelectedSnapshot?.Id : null);
        var selectedTimeline = timeline.FirstOrDefault(item => item.Snapshot.Id == selectedId)
            ?? timeline.FirstOrDefault();
        isRefreshingSnapshotNavigation = true;
        try
        {
            SnapshotTimelineItems = timeline;
            SnapshotCountText = $"{timeline.Count} 份";
            SelectedSnapshotTimelineItem = selectedTimeline;
            SelectedSnapshot = selectedTimeline?.Snapshot;
            var dayCount = filtered.Select(snapshot => snapshot.LocalDate).Distinct().Count();
            SnapshotTimelineSummary = filtered.Length == 0
                ? "尚无存档"
                : $"本机存档 · {dayCount} 天 · {filtered.Length} 份";
        }
        finally
        {
            isRefreshingSnapshotNavigation = false;
        }

    }

    private static int GetRoomSortOrder(string roomName)
    {
        var digits = new string(roomName.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var roomNumber) ? roomNumber : int.MaxValue;
    }

    private static SnapshotFileStore CreateSnapshotFileStore() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveStudio",
        "SnapshotLibrary"));

    private static string FormatSignerFingerprint(string fingerprint) => fingerprint.Length <= 24
        ? $"签名指纹 · {fingerprint}"
        : $"签名指纹 · {fingerprint[..12]}…{fingerprint[^8..]}";

    private void RefreshActivityItems()
    {
        ActivityItems = localOperations
            .Select(operation => new ActivityItemViewModel(operation))
            .Concat(cloudJobs.Select(job => new ActivityItemViewModel(job)))
            .OrderByDescending(item => item.SortTime)
            .Take(200)
            .ToArray();
    }

    private static string GetApplicationStatus(LocalAgentState state, ApplicationKind application) =>
        state.Applications.FirstOrDefault(item => item.Application == application)?.StatusMessage ?? "适配器不可用";

    private void ApplyApplicationRuntimeState(LocalAgentState state)
    {
        var obs = state.Applications.FirstOrDefault(application => application.Application == ApplicationKind.Obs);
        ObsVersionText = obs is null || string.Equals(obs.Version, "unknown", StringComparison.OrdinalIgnoreCase)
            ? "版本未知"
            : $"版本 {obs.Version}";
        ObsConnectionState = obs switch
        {
            null => "读取失败",
            { AdapterAvailable: false } => "读取失败",
            { IsRunning: true } => "已连接",
            _ => "未运行"
        };
        ObsStreamingState = obs?.CanDetermineLiveState == true
            ? obs.IsStreaming ? "正在推流" : "未推流"
            : "推流状态未知";
        ObsRecordingState = obs?.CanDetermineLiveState == true
            ? obs.IsRecording ? "正在录制" : "未录制"
            : "录制状态未知";

        var companion = state.Applications.FirstOrDefault(application =>
            application.Application == ApplicationKind.LiveCompanion);
        LiveCompanionVersionText = companion is null
                                   || string.Equals(companion.Version, "unknown", StringComparison.OrdinalIgnoreCase)
            ? "版本未知"
            : $"版本 {companion.Version}";
        LiveCompanionConnectionState = companion switch
        {
            null => "读取失败",
            { AdapterAvailable: false } => "读取失败",
            { IsRunning: true } => "已读取",
            _ => "未运行"
        };
        LiveCompanionLiveState = companion?.CanDetermineLiveState == true
            ? companion.IsStreaming ? "正在开播" : "未开播"
            : "开播状态未知";
    }

    private string? ResolveRoomName(Guid? roomId) => roomId is { } id
        ? CloudRooms.FirstOrDefault(room => room.Id == id)?.Name
        : null;

    private void ApplyKnownRoomNames()
    {
        foreach (var snapshot in SnapshotItems)
        {
            snapshot.ApplyRoomName(ResolveRoomName(snapshot.RoomId));
        }

        RefreshSnapshotNavigation(SelectedSnapshot);
    }

    private void OnCloudRoomPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(CloudRoomItemViewModel.IsBatchSelected))
        {
            NotifyBatchSelectionChanged();
        }
    }

    private void NotifyBatchSelectionChanged()
    {
        OnPropertyChanged(nameof(BatchSelectedRoomCount));
        OnPropertyChanged(nameof(BatchSelectedCountText));
        BatchCaptureCloudSnapshotsCommand.NotifyCanExecuteChanged();
    }

    private void ApplySnapshotReadSummary(SnapshotInspectorViewModel inspector)
    {
        var obs = inspector.Applications.FirstOrDefault(application => application.IsObs);
        if (obs is not null)
        {
            var filterCount = obs.Sources.Sum(source => source.Filters.Count);
            ObsReadSummary = $"已读取 {obs.Sources.Count} 个来源 · {filterCount} 个滤镜";
        }

        var companion = inspector.Applications.FirstOrDefault(application => application.IsLiveCompanion);
        if (companion is not null)
        {
            LiveCompanionReadSummary = $"已读取 {companion.NativeDocumentCount} 份原生文档 · {companion.ReadFieldCount} 个字段";
            LiveCompanionAccessState = companion.AdapterStatus;
        }
    }

    public async Task DeleteSelectedSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSnapshot is null || SelectedSnapshot.IsDesktopFile)
        {
            return;
        }

        IsBusy = true;
        try
        {
            SnapshotInspector?.IsTechnicalPanelOpen = false;
            var selectedId = SelectedSnapshot.Id;
            var selectedIndex = SnapshotTimelineItems
                .Select((item, index) => (item.Snapshot.Id, index))
                .FirstOrDefault(item => item.Id == selectedId)
                .index;
            var adjacentId = SnapshotTimelineItems.Count > 1
                ? SnapshotTimelineItems[selectedIndex < SnapshotTimelineItems.Count - 1
                    ? selectedIndex + 1
                    : selectedIndex - 1].Snapshot.Id
                : (Guid?)null;
            var deletedCloudSnapshot = SelectedSnapshot.IsCloud;
            if (deletedCloudSnapshot)
            {
                if (cloudCredentials is null || SelectedOrganization is null)
                {
                    PendingImportMessage = "云端连接已失效，请重新连接后再删除";
                    return;
                }

                await cloudClient.DeleteSnapshotAsync(
                    cloudCredentials,
                    SelectedOrganization.Id,
                    selectedId,
                    cancellationToken);
                await LoadCloudWorkspaceAsync(cancellationToken);
            }
            else
            {
                if (!SupportsLocalAgent)
                {
                    return;
                }

                await localAgentClient.DeleteSnapshotAsync(selectedId, cancellationToken);
                await LoadStateAsync(cancellationToken);
            }
            if (adjacentId is { } id)
            {
                SelectedSnapshot = SnapshotItems.FirstOrDefault(snapshot => snapshot.Id == id)
                    ?? SelectedSnapshot;
            }

            PendingImportMessage = SnapshotItems.Count == 0
                ? "当前存档已删除；存档列表现在为空"
                : deletedCloudSnapshot
                    ? "云存档及其云端预览和无引用素材已进入安全清理队列"
                    : $"当前存档已删除；已选择相邻存档（剩余 {SnapshotItems.Count} 份）";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException or HttpRequestException)
        {
            PendingImportMessage = SnapshotOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteAllSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsLocalAgent || LocalSnapshotCount == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            SnapshotInspector?.IsTechnicalPanelOpen = false;
            var result = await localAgentClient.DeleteAllSnapshotsAsync(cancellationToken);
            await LoadStateAsync(cancellationToken);
            SelectedSnapshot = null;
            SnapshotInspector = null;
            PendingImportMessage = $"已永久清空全部 {result.DeletedCount} 份画面存档；现在可以重新保存当前画面";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            PendingImportMessage = SnapshotOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string> CompareWithPreviousLocalSnapshotAsync(
        LocalSnapshotItemViewModel current,
        CombinedSnapshot currentDetail,
        CancellationToken cancellationToken)
    {
        var ordered = SnapshotItems
            .Where(snapshot => !snapshot.IsCloud
                               && !snapshot.IsDesktopFile
                               && snapshot.RoomId == current.RoomId)
            .OrderByDescending(snapshot => snapshot.CreatedAtValue)
            .ToArray();
        var currentIndex = Array.FindIndex(ordered, snapshot => snapshot.Id == current.Id);
        if (currentIndex < 0 || currentIndex + 1 >= ordered.Length)
        {
            return "这是当前直播间的第一份可比较存档";
        }

        CombinedSnapshot previousDetail;
        try
        {
            previousDetail = await localAgentClient.GetSnapshotDetailAsync(
                ordered[currentIndex + 1].Id,
                cancellationToken);
        }
        catch (LocalControlException exception)
        {
            return exception.ErrorCode == nameof(SnapshotSensitiveDataException)
                ? "上一份历史存档含敏感配置，已跳过比较"
                : "上一份存档暂时无法读取，已跳过比较";
        }
        catch (IOException)
        {
            return "上一份存档暂时无法读取，已跳过比较";
        }
        var differences = SnapshotParameterComparer.Compare(
            ToSnapshotDetail(previousDetail),
            ToSnapshotDetail(currentDetail));
        if (differences.Count == 0)
        {
            return "与上一份存档相比没有参数变化";
        }

        var obsChanges = differences.Count(difference =>
            difference.Label.StartsWith("OBS /", StringComparison.Ordinal));
        var companionChanges = differences.Count(difference =>
            difference.Label.StartsWith("直播伴侣 /", StringComparison.Ordinal));
        var cameraChanges = differences.Count(difference =>
            difference.Label.StartsWith("相机参数 /", StringComparison.Ordinal));
        var changedApplications = new List<string>(3);
        if (obsChanges > 0)
        {
            changedApplications.Add($"OBS {obsChanges} 项变化");
        }

        if (companionChanges > 0)
        {
            changedApplications.Add($"直播伴侣 {companionChanges} 项变化");
        }

        if (cameraChanges > 0)
        {
            changedApplications.Add($"相机参数 {cameraChanges} 项变化");
        }

        return $"与上一份相比：{string.Join(" · ", changedApplications)}";
    }

    private static SnapshotDetail ToSnapshotDetail(CombinedSnapshot snapshot) => new(
        new SnapshotSummary(
            snapshot.Id,
            snapshot.RoomId,
            snapshot.Name,
            snapshot.CreatedAt,
            0,
            string.Empty),
        snapshot.Applications,
        new Dictionary<ApplicationKind, Uri>(),
        snapshot.CameraStations);

    private static string GetCloudApplicationStatus(DeviceManagementState state, ApplicationKind application)
    {
        var snapshot = state.CurrentParameters?.Applications.FirstOrDefault(value => value.Kind == application);
        return snapshot is null
            ? "尚未上报"
            : $"{snapshot.Version} · {snapshot.Sources.Count} 个来源";
    }
}

public sealed record NativeExportChangedFieldViewModel(string Path);
