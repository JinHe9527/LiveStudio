using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly bool supportsLocalAgent = OperatingSystem.IsWindows();
    private IReadOnlyList<LocalMappingTargetItemViewModel> allMappingTargets = [];
    private IReadOnlyList<LocalOperationSummary> localOperations = [];
    private IReadOnlyList<JobSummary> cloudJobs = [];
    private IReadOnlyList<LocalSnapshotItemViewModel> desktopSnapshotItems = [];
    private IReadOnlyList<LocalSnapshotItemViewModel> cloudSnapshotItems = [];
    private DesktopCloudCredentials? cloudCredentials;
    private NativeExportReport? nativeExportBaseline;
    private CancellationTokenSource? snapshotDetailCancellation;
    private bool isLoadingCloudWorkspace;
    private ApplicationUpdateRelease? availableUpdate;

    public MainViewModel()
        : this(
            new LocalAgentClient(),
            new DesktopCloudClient(new DesktopCredentialStore()),
            new SystemBrowser(),
            CreateSnapshotFileStore(),
            new ApplicationUpdateService())
    {
    }

    public MainViewModel(
        LocalAgentClient localAgentClient,
        DesktopCloudClient cloudClient,
        ISystemBrowser systemBrowser,
        SnapshotFileStore snapshotFileStore,
        ApplicationUpdateService applicationUpdateService)
    {
        this.localAgentClient = localAgentClient;
        this.cloudClient = cloudClient;
        this.systemBrowser = systemBrowser;
        this.snapshotFileStore = snapshotFileStore;
        this.applicationUpdateService = applicationUpdateService;
        CurrentVersionText = applicationUpdateService.CurrentVersionText;
        SelectedSection = supportsLocalAgent ? 0 : 1;
        ApplyDisconnectedState();
    }

    public event EventHandler? UpdateRestartRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsControlVisible))]
    [NotifyPropertyChangedFor(nameof(IsSnapshotsVisible))]
    [NotifyPropertyChangedFor(nameof(IsMappingsVisible))]
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
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsAgentAutoStartEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsAgentCloudEnrolled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsCloudControlVisible))]
    public partial bool IsAgentConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsCloudControlVisible))]
    [NotifyPropertyChangedFor(nameof(CanShowRestoreAction))]
    public partial bool IsCloudConnected { get; set; }

    [ObservableProperty]
    public partial bool IsCloudAuthorizing { get; set; }

    [ObservableProperty]
    public partial string CloudServiceUrl { get; set; } = "https://";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloudAuthorizationCode))]
    public partial string CloudAuthorizationCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudConnectionMessage { get; set; } = "尚未连接云端服务";

    [ObservableProperty]
    public partial string OrganizationName { get; set; } = "未选择 Organization";

    [ObservableProperty]
    public partial IReadOnlyList<OrganizationSummary> Organizations { get; set; } = [];

    [ObservableProperty]
    public partial OrganizationSummary? SelectedOrganization { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<CloudRoomItemViewModel> CloudRooms { get; set; } = [];

    [ObservableProperty]
    public partial CloudRoomItemViewModel? SelectedCloudRoom { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityItems))]
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
    public partial IReadOnlyList<LocalSnapshotItemViewModel> SnapshotItems { get; set; } = [];

    [ObservableProperty]
    public partial LocalSnapshotItemViewModel? SelectedSnapshot { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshotInspector))]
    public partial SnapshotInspectorViewModel? SnapshotInspector { get; set; }

    [ObservableProperty]
    public partial string SnapshotInspectorMessage { get; set; } = "选择一份存档查看设备、画面模式和滤镜参数";

    [ObservableProperty]
    public partial string ObsEndpoint { get; set; } = "ws://127.0.0.1:4455";

    [ObservableProperty]
    public partial string ObsPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SettingsMessage { get; set; } = string.Empty;

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
    public partial string GitHubUpdateToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasGitHubUpdateToken { get; set; }

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
    public partial string? PendingImportPath { get; set; }

    [ObservableProperty]
    public partial string PendingImportMessage { get; set; } = string.Empty;

    public string DeviceName { get; } = Environment.MachineName;

    public string PlatformMode { get; } = OperatingSystem.IsWindows()
        ? "Windows · 本机控制"
        : "macOS · 存档检查";

    public bool HasNativeExportAudit => !string.IsNullOrWhiteSpace(NativeExportAuditDetail);

    public bool HasNativeExportChangedFields => NativeExportChangedFields.Count > 0;

    public string CurrentVersionText { get; }

    public string SnapshotsSubtitle => SupportsLocalAgent
        ? "保存、检查并恢复 OBS 与直播伴侣联合配置"
        : "打开真实 .lscfg 存档，检查设备、画面模式、色彩与滤镜";

    public string SnapshotEmptyDescription => SupportsLocalAgent
        ? "保存第一份 OBS 与直播伴侣联合配置，之后可逐项查看或恢复。"
        : "打开 Windows 执行端生成的 .lscfg 文件，即可离线查看完整参数。";

    public bool HasSnapshots => SnapshotItems.Count > 0;

    public bool SupportsLocalAgent => supportsLocalAgent;

    public bool HasPendingImport => !string.IsNullOrWhiteSpace(PendingImportPath);

    public bool HasTransferMessage => !string.IsNullOrWhiteSpace(PendingImportMessage);

    public bool HasSelectedSnapshot => SelectedSnapshot is not null;

    public bool HasSnapshotInspector => SnapshotInspector is not null;

    public bool HasMappingSources => MappingSources.Count > 0;

    public bool HasCloudAuthorizationCode => !string.IsNullOrWhiteSpace(CloudAuthorizationCode);

    public bool HasObsPreview => CurrentObsPreview is not null;

    public bool HasLiveCompanionPreview => CurrentLiveCompanionPreview is not null;

    public bool CanShowRestoreAction => SupportsLocalAgent
        || SelectedSnapshot?.IsCloud == true && IsCloudConnected;

    public bool HasActivityItems => ActivityItems.Count > 0;

    public bool IsDisconnected => !IsAgentConnected && !IsCloudConnected;

    public bool IsCloudControlVisible => IsCloudConnected && !IsAgentConnected;

    public bool IsControlVisible => SelectedSection == 0;

    public bool IsSnapshotsVisible => SelectedSection == 1;

    public bool IsMappingsVisible => SelectedSection == 2;

    public bool IsActivityVisible => SelectedSection == 3;

    public bool IsSettingsVisible => SelectedSection == 4;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        HasGitHubUpdateToken = DesktopCredentialStore.TryLoadUpdateToken(out _);
        await LoadStateAsync(cancellationToken);
        if (!supportsLocalAgent)
        {
            await LoadDesktopSnapshotLibraryAsync(cancellationToken);
        }

        if (cloudClient.TryLoadCredentials(out var credentials))
        {
            cloudCredentials = credentials;
            CloudServiceUrl = credentials.ServiceUri.ToString();
            await LoadCloudWorkspaceAsync(cancellationToken);
        }
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
        DisableLanDirectoryCommand.NotifyCanExecuteChanged();
        SaveMappingCommand.NotifyCanExecuteChanged();
        TrustAndImportCommand.NotifyCanExecuteChanged();
        CancelPendingImportCommand.NotifyCanExecuteChanged();
        CaptureCloudSnapshotCommand.NotifyCanExecuteChanged();
        EnrollAgentCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCloudRoomChanged(CloudRoomItemViewModel? value)
    {
        CaptureCloudSnapshotCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
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
        cloudClient.SaveCredentials(cloudCredentials);
        EnrollAgentCommand.NotifyCanExecuteChanged();
        _ = ChangeOrganizationAsync(CancellationToken.None);
    }

    partial void OnIsCloudConnectedChanged(bool value) => EnrollAgentCommand.NotifyCanExecuteChanged();

    partial void OnIsAgentConnectedChanged(bool value) => EnrollAgentCommand.NotifyCanExecuteChanged();

    partial void OnIsAgentCloudEnrolledChanged(bool value) => EnrollAgentCommand.NotifyCanExecuteChanged();

    partial void OnHasGitHubUpdateTokenChanged(bool value) =>
        CheckForUpdatesCommand.NotifyCanExecuteChanged();

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
        CloudConnectionMessage = "正在切换 Organization";
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
        snapshotDetailCancellation?.Cancel();
        snapshotDetailCancellation?.Dispose();
        snapshotDetailCancellation = null;
        SnapshotInspector = null;
        SnapshotInspectorMessage = value is null
            ? "选择一份存档查看设备、画面模式和滤镜参数"
            : "正在读取存档参数…";
        if (value is not null)
        {
            snapshotDetailCancellation = new CancellationTokenSource();
            _ = LoadSnapshotInspectorAsync(value, snapshotDetailCancellation.Token);
        }
    }

    private async Task LoadSnapshotInspectorAsync(
        LocalSnapshotItemViewModel snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            SnapshotInspectorViewModel inspector;
            if (snapshot.IsCloud)
            {
                if (cloudCredentials is null || SelectedOrganization is null)
                {
                    SnapshotInspectorMessage = "云端连接或 Organization 已失效";
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
                    detail.Applications);
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
                    FormatSignerFingerprint(file.Inspection.Signer.FingerprintSha256));
            }
            else
            {
                var detail = await localAgentClient.GetSnapshotDetailAsync(snapshot.Id, cancellationToken);
                inspector = new SnapshotInspectorViewModel(
                    detail.Name,
                    detail.CreatedAt,
                    detail.Applications);
            }

            if (SelectedSnapshot?.Id == snapshot.Id)
            {
                SnapshotInspector = inspector;
                SnapshotInspectorMessage = inspector.Applications.Count == 0
                    ? "这份存档没有应用参数"
                    : string.Empty;
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
                SnapshotInspectorMessage = exception.Message;
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
        if (!Uri.TryCreate(CloudServiceUrl.Trim(), UriKind.Absolute, out var serviceUri))
        {
            CloudConnectionMessage = "请输入有效的云端服务地址";
            return;
        }

        IsBusy = true;
        IsCloudAuthorizing = true;
        CloudAuthorizationCode = string.Empty;
        try
        {
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
                    return;
                }

                break;
            }

            CloudConnectionMessage = "授权请求已过期，请重新连接";
        }
        catch (Exception exception) when (exception is HttpRequestException or ArgumentException or InvalidOperationException)
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
        CloudAuthorizationCode = string.Empty;
        if (!IsAgentConnected)
        {
            ApplyDisconnectedState();
        }

        CloudConnectionMessage = disconnectMessage;
    }

    [RelayCommand]
    private void SaveGitHubUpdateToken()
    {
        if (string.IsNullOrWhiteSpace(GitHubUpdateToken))
        {
            UpdateStatus = "请输入具有该私有仓库 Contents 只读权限的 GitHub Token";
            return;
        }

        DesktopCredentialStore.SaveUpdateToken(GitHubUpdateToken);
        GitHubUpdateToken = string.Empty;
        HasGitHubUpdateToken = true;
        UpdateStatus = "GitHub 更新凭据已安全保存";
    }

    [RelayCommand]
    private void DeleteGitHubUpdateToken()
    {
        DesktopCredentialStore.DeleteUpdateToken();
        GitHubUpdateToken = string.Empty;
        HasGitHubUpdateToken = false;
        availableUpdate = null;
        LatestVersionText = "—";
        UpdateStatus = "GitHub 更新凭据已移除";
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!DesktopCredentialStore.TryLoadUpdateToken(out var token))
        {
            UpdateStatus = "请先保存 GitHub Token";
            return;
        }

        IsBusy = true;
        UpdateStatus = "正在检查 GitHub 私有 Release…";
        try
        {
            availableUpdate = await applicationUpdateService.CheckAsync(token, cancellationToken);
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
            UpdateStatus = $"检查更新失败：{exception.Message}";
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
        if (availableUpdate is null
            || !DesktopCredentialStore.TryLoadUpdateToken(out var token))
        {
            return;
        }

        IsBusy = true;
        UpdateStatus = $"正在下载并校验 {availableUpdate.TagName}…";
        try
        {
            var prepared = await applicationUpdateService.PrepareAsync(
                availableUpdate,
                token,
                cancellationToken);
            UpdateStatus = "更新包校验完成，正在重启安装";
            ApplicationUpdateService.LaunchInstaller(prepared);
            UpdateRestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            UpdateStatus = $"安装更新失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
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
                    $"{SelectedCloudRoom.Name} {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"),
                cancellationToken);
            ControlStatusTitle = "联合保存任务已下发";
            ControlStatusDescription = "Windows 执行端会先检查直播状态，再读取并上传联合存档。";
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
        IsBusy = true;
        ControlStatusTitle = "正在保存当前画面";
        ControlStatusDescription = "正在读取 OBS 与直播伴侣参数、滤镜和预览图。";
        try
        {
            var name = $"{DateTime.Now:yyyy-MM-dd HH:mm} 画面存档";
            await localAgentClient.CaptureAsync(name, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            ControlStatusTitle = "保存失败";
            ControlStatusDescription = exception.Message;
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

        if (SelectedSnapshot.IsCloud)
        {
            await RestoreCloudSnapshotAsync(cancellationToken);
            return;
        }

        IsBusy = true;
        ControlStatusTitle = $"正在恢复“{SelectedSnapshot.Name}”";
        ControlStatusDescription = "恢复完成前不要关闭 OBS、直播伴侣或 LiveStudio。";
        try
        {
            await localAgentClient.RestoreAsync(SelectedSnapshot.Id, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            ControlStatusTitle = "恢复失败";
            ControlStatusDescription = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
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
        ControlStatusDescription = "目标电脑会先完成直播状态、设备映射和版本兼容性检查。";
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

    public async Task ImportSnapshotFileAsync(string path, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        PendingImportPath = null;
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
                PendingImportMessage = $"存档“{preview.Name}”的签名者尚未受信任。公钥指纹：{preview.SignerFingerprintSha256}";
                return;
            }

            var result = await localAgentClient.ImportSnapshotFileAsync(path, false, cancellationToken);
            PendingImportMessage = $"已导入“{result.Name}”";
            await LoadStateAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is LocalControlException
            or IOException
            or UnauthorizedAccessException
            or SnapshotPackageException)
        {
            PendingImportMessage = exception.Message;
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

        IsBusy = true;
        PendingImportMessage = "正在校验并导出存档…";
        try
        {
            var result = await localAgentClient.ExportSnapshotFileAsync(
                SelectedSnapshot.Id,
                path,
                cancellationToken);
            PendingImportMessage = $"已导出到 {result.Path}";
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            PendingImportMessage = exception.Message;
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
            PendingImportMessage = $"已信任签名者并导入“{result.Name}”";
            await LoadStateAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is LocalControlException or IOException)
        {
            PendingImportMessage = exception.Message;
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
        PendingImportMessage = "已取消导入";
    }

    [RelayCommand(CanExecute = nameof(CanConfigureObs))]
    private async Task DisableLanDirectoryAsync(CancellationToken cancellationToken)
    {
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
    private void OpenConnectionSettings() => SelectedSection = 4;

    [RelayCommand]
    private async Task SelectSectionAsync(string section, CancellationToken cancellationToken)
    {
        if (int.TryParse(section, out var index))
        {
            SelectedSection = index;
            if (index == 2)
            {
                await LoadMappingContextAsync(cancellationToken);
            }
        }
    }

    [RelayCommand]
    private async Task LoadMappingContextAsync(CancellationToken cancellationToken)
    {
        if (SelectedSnapshot?.IsCloud == true || !OperatingSystem.IsWindows())
        {
            await LoadCloudMappingContextAsync(cancellationToken);
            return;
        }

        if (SelectedSnapshot is null)
        {
            MappingMessage = "先在画面存档中选择一份存档";
            return;
        }

        IsBusy = true;
        MappingMessage = "正在回读存档来源和目标采集设备…";
        try
        {
            ApplyMappingContext(await localAgentClient.GetMappingContextAsync(
                SelectedSnapshot.Id,
                cancellationToken));
            MappingMessage = MappingSources.Count == 0
                ? "这份存档没有可映射的视频来源"
                : "选择存档来源和当前电脑上的目标来源";
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

    [RelayCommand(CanExecute = nameof(CanSaveMapping))]
    private async Task SaveMappingAsync(CancellationToken cancellationToken)
    {
        if (SelectedSnapshot is null
            || SelectedMappingSource is null
            || SelectedMappingTarget is null)
        {
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
            MappingMessage = "先在画面存档中选择一份云端存档";
            return;
        }

        if (cloudCredentials is null
            || SelectedOrganization is null
            || SelectedCloudRoom?.DeviceId is not { } deviceId)
        {
            MappingMessage = "先在控制室选择一台已注册的 Windows 直播电脑";
            return;
        }

        IsBusy = true;
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
                    .Where(source => source.Device is not null)
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
            MappingMessage = sources.Length == 0
                ? "这份存档没有可映射的视频来源"
                : "选择存档来源和目标 Agent 已回读的采集来源";
        }
        catch (HttpRequestException exception)
        {
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
    }

    private bool CanCaptureSnapshot() =>
        OperatingSystem.IsWindows() && CanControlLocalApplications && !IsBusy;

    private bool CanCaptureCloudSnapshot() =>
        IsCloudConnected && SelectedCloudRoom is { Online: true, DeviceId: not null } && !IsBusy;

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
        && (SelectedSnapshot.IsCloud
            ? IsCloudConnected && SelectedCloudRoom is { Online: true, DeviceId: not null }
            : !SelectedSnapshot.IsDesktopFile
                && OperatingSystem.IsWindows()
                && CanRestoreLocalApplications);

    private bool CanConfigureObs() => OperatingSystem.IsWindows() && !IsBusy;

    private bool CanCheckForUpdates() => HasGitHubUpdateToken && !IsBusy;

    private bool CanInstallUpdate() =>
        OperatingSystem.IsWindows() && availableUpdate is not null && !IsBusy;

    private bool CanConfirmPendingImport() =>
        OperatingSystem.IsWindows() && !IsBusy && !string.IsNullOrWhiteSpace(PendingImportPath);

    private bool CanSaveMapping() => !IsBusy
        && (SelectedSnapshot?.IsCloud == true ? IsCloudConnected : OperatingSystem.IsWindows())
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
            OrganizationName = SelectedOrganization?.Name ?? "尚无 Organization";
            if (SelectedOrganization is not null
                && cloudCredentials.SelectedOrganizationId != SelectedOrganization.Id)
            {
                cloudCredentials = cloudCredentials with { SelectedOrganizationId = SelectedOrganization.Id };
                cloudClient.SaveCredentials(cloudCredentials);
            }
            CloudRooms = workspace.Rooms.Select(room => new CloudRoomItemViewModel(room)).ToArray();
            cloudJobs = workspace.Jobs;
            RefreshActivityItems();
            SelectedCloudRoom = CloudRooms.FirstOrDefault(room => room.Id == selectedRoomId)
                ?? (CloudRooms.Count > 0 ? CloudRooms[0] : null);
            if (!IsAgentConnected)
            {
                cloudSnapshotItems = workspace.Snapshots
                    .Select(snapshot => new LocalSnapshotItemViewModel(snapshot))
                    .ToArray();
                RefreshAvailableSnapshots();
            }

            IsCloudConnected = true;
            CloudStatus = "已连接";
            CloudConnectionMessage = SelectedOrganization is null
                ? "账号下尚无 Organization，请先在云端控制台创建"
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
            RestoreSnapshotCommand.NotifyCanExecuteChanged();
        }
        catch (HttpRequestException exception)
        {
            IsCloudConnected = false;
            cloudSnapshotItems = [];
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

    private void ApplyAgentState(LocalAgentState state)
    {
        IsAgentConnected = true;
        ConnectionSubtitle = state.StatusMessage;
        ControlStatusTitle = state.CanCapture ? "本机画面已准备好" : state.StatusMessage;
        ControlStatusDescription = state.CanCapture
            ? "可以保存当前联合配置，或从画面存档中选择一个版本恢复。"
            : "LiveStudio 只会在直播状态、适配器和设备能力全部确认后允许写入。";
        AgentStatus = state.IsBusy ? "正在执行任务" : "已连接";
        CloudStatus = state.IsCloudEnrolled ? "已注册" : "未注册";
        IsAgentCloudEnrolled = state.IsCloudEnrolled;
        ConfigurationStatus = state.CanCapture ? "可保存" : "未就绪";
        LanSharedDirectory = state.LanSharedDirectory ?? "未配置";
        LanSyncStatus = state.LanSyncStatus;
        CanControlLocalApplications = state.CanCapture;
        CanRestoreLocalApplications = state.CanRestore;
        IsBusy = state.IsBusy;
        IsAgentAutoStartEnabled = state.AutoStartEnabled;
        SnapshotItems = state.Snapshots
            .Select(snapshot => new LocalSnapshotItemViewModel(snapshot))
            .ToArray();
        localOperations = state.Operations;
        RefreshActivityItems();
        SnapshotCountText = $"{SnapshotItems.Count} 份";
        ObsStatus = GetApplicationStatus(state, ApplicationKind.Obs);
        LiveCompanionStatus = GetApplicationStatus(state, ApplicationKind.LiveCompanion);
        if (SelectedSnapshot is not null)
        {
            SelectedSnapshot = SnapshotItems.FirstOrDefault(item => item.Id == SelectedSnapshot.Id);
        }
        else if (SnapshotItems.Count > 0 && SelectedSection == 1)
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
            SnapshotItems = [];
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
        if (IsAgentConnected)
        {
            return;
        }

        var selected = SelectedSnapshot;
        SnapshotItems = desktopSnapshotItems
            .Concat(cloudSnapshotItems)
            .ToArray();
        SnapshotCountText = $"{SnapshotItems.Count} 份";
        if (selected is not null)
        {
            SelectedSnapshot = SnapshotItems.FirstOrDefault(item =>
                item.Id == selected.Id
                && item.IsCloud == selected.IsCloud
                && item.IsDesktopFile == selected.IsDesktopFile);
        }
        else if (SnapshotItems.Count > 0 && SelectedSection == 1)
        {
            SelectedSnapshot = SnapshotItems[0];
        }
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

    private static string GetCloudApplicationStatus(DeviceManagementState state, ApplicationKind application)
    {
        var snapshot = state.CurrentParameters?.Applications.FirstOrDefault(value => value.Kind == application);
        return snapshot is null
            ? "尚未上报"
            : $"{snapshot.Version} · {snapshot.Sources.Count} 个来源";
    }
}

public sealed record NativeExportChangedFieldViewModel(string Path);
