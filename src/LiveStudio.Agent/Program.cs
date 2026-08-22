using LiveStudio.Agent;
using LiveStudio.Adapters.Obs;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("LiveStudio Agent 只能在 Windows 登录用户会话内运行。");
    return 2;
}

var credentialStore = new WindowsCredentialStore();
if (args.Length > 0 && string.Equals(args[0], "enroll", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length is < 3 or > 4 || !Uri.TryCreate(args[1], UriKind.Absolute, out var serviceUri))
    {
        Console.Error.WriteLine("用法: LiveStudio.Agent enroll <service-url> <enrollment-token> [machine-name]");
        return 2;
    }

    using var httpClient = new HttpClient { BaseAddress = serviceUri };
    var enrollment = new DeviceEnrollmentClient(httpClient, credentialStore);
    await enrollment.EnrollAsync(args[2], args.Length == 4 ? args[3] : Environment.MachineName, CancellationToken.None);
    Console.WriteLine("设备注册完成，凭据已保存到 Windows Credential Manager。");
    return 0;
}

credentialStore.EnsureLocalIdentity();

var builder = Host.CreateApplicationBuilder(args);
using var instanceMutex = new Mutex(true, @"Local\LiveStudio.Agent", out var isFirstInstance);
if (!isFirstInstance)
{
    Console.Error.WriteLine("LiveStudio Agent 已在当前 Windows 用户会话中运行。");
    return 0;
}

builder.Services.AddSingleton<IDeviceCredentialStore>(credentialStore);
builder.Services.AddHostedService<TrayIconService>();
builder.Services.AddSingleton<LocalSnapshotIndex>();
builder.Services.AddSingleton<AgentObsConfigurationStore>();
builder.Services.AddSingleton<LanSnapshotConfigurationStore>();
builder.Services.AddSingleton<IObsConnectionOptionsProvider>(services =>
    services.GetRequiredService<AgentObsConfigurationStore>());
builder.Services.AddSingleton<IObsCredentialProvider>(services =>
    services.GetRequiredService<AgentObsConfigurationStore>());
builder.Services.AddSingleton<IObsDeviceCatalog, AgentObsDeviceCatalog>();
builder.Services.AddSingleton<IApplicationAdapter, ObsAdapter>();
builder.Services.AddSingleton<IApplicationAdapter, LiveCompanionAdapter>();
builder.Services.AddSingleton<SnapshotCaptureService>();
builder.Services.AddSingleton<RestoreCoordinator>();
builder.Services.AddSingleton<LocalRestoreService>();
builder.Services.AddSingleton<SnapshotTransferService>();
builder.Services.AddSingleton<LanSnapshotWorker>();
builder.Services.AddSingleton<DeviceApiClient>();
builder.Services.AddSingleton<CurrentStatePublisher>();
builder.Services.AddSingleton<AgentWorker>();
builder.Services.AddSingleton<SnapshotUploadWorker>();
builder.Services.AddSingleton<CloudAgentRuntime>();
builder.Services.AddHostedService<LocalControlServer>();
builder.Services.AddHostedService(services => services.GetRequiredService<LanSnapshotWorker>());
builder.Services.AddHostedService(services => services.GetRequiredService<CloudAgentRuntime>());
await builder.Build().RunAsync();
return 0;
