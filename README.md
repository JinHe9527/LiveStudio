# LiveStudio 直播画面配置中心

LiveStudio 用于保存、查看并事务化恢复同一台 Windows 电脑上的 OBS 与抖音直播伴侣画面参数。范围严格限定为设备选择、分辨率、FPS、像素格式、色彩空间、色彩范围、视频滤镜、滤镜顺序和滤镜素材；不处理相机机身参数、音频、场景布局、账号、Cookie、Token 或推流密钥。

## 软件内更新

Windows 客户端从本项目的 GitHub Releases 检查更新。因为源代码仓库和 Release 都是私有资源，第一次使用时需要在左下角“设置”中保存一个只授予本仓库 `Contents: Read-only` 权限的 Fine-grained Token。Token 仅进入 Windows Credential Manager 或 macOS Keychain，不写入配置、日志、存档或云端。

之后在设置页点击“检查更新”和“下载、安装并重启”即可完成更新。客户端会先校验 Release 中配套的 SHA-256 文件，再退出 Desktop 和 Agent、覆盖程序文件并重新启动。推送 `v主版本.次版本.修订号` 标签会由 GitHub Actions 自动测试、构建并创建 Windows x64 Release。

## 当前实现

- `.lscfg`：不可变 ZIP 容器、逐文件 SHA-256、ECDSA P-256 签名、素材归档、敏感字段拒绝与路径安全检查。
- 恢复引擎：统一 Preflight、事务快照、停止、应用、启动、逐项验证、Commit/Rollback；任一验证失败即回滚。
- OBS：基于 obs-websocket 5.x 的来源、视频模式、滤镜、顺序、启用状态、素材和预览图读写适配器。
- 直播伴侣：Windows Agent 可以探测 `StreamingTool` 和 `%APPDATA%\webcast_mate` 的候选结构，并审计原生导出 ZIP；正式恢复只接受匹配版本和结构指纹的签名适配定义。当前执行端已实现 `JsonFile` 的事务读写框架，真实生产版本适配仍需 Windows 真机证据。
- Windows Agent：同用户会话运行、Windows Credential Manager 凭据、SQLite 本地索引、断网待上传、SignalR 通知、REST 任务租约和心跳。
- 本机控制：Windows 桌面端通过仅限当前用户的 Named Pipe 读取 Agent、OBS、直播伴侣和存档状态，发起真实联合保存与事务恢复；OBS 密码只写入 Credential Manager。
- 远程任务：Capture Job 实际执行读取、打包、签名与上传；Restore Job 下载后核对长度、SHA-256 和源设备签名，再使用目标设备映射执行验证与回滚。
- 桌面产品：基于 Avalonia 的 Windows/macOS 共用客户端。Windows 是本机保存与恢复的主平台；macOS 提供正式的存档、设备和远程任务管理能力。
- 云端：ASP.NET Core 10、Blazor WebAssembly 交互、Identity、PostgreSQL、S3/MinIO、SignalR、Organization 隔离、RBAC、审计、设备注册、存档与远程任务 API。
- 对象生命周期：存档包使用 8 MiB S3 Multipart Upload；删除存档时保留共享素材，独占素材、预览和包文件通过持久化删除队列重试清理。
- 桌面 UI：采用 Apple Pro App 的 Split View、Sidebar、Toolbar 和 Inspector 结构，以细分隔线组织信息，不使用卡片式 Dashboard 或 Emoji 图标。
- 私有部署：PostgreSQL、MinIO、云服务与 Caddy 的 Docker Compose 编排。

## 不能伪造的发布门槛

直播伴侣没有公开稳定配置 API。当前实验扫描可以把候选设备、分辨率、FPS、像素格式、色彩、滤镜、美颜和曲线子树展示为探测结果，但这种结果没有签名适配定义，只允许读取，不能用于写入。仓库目前没有经过真实生产版本完整验证的直播伴侣签名适配定义；`JsonFile` 执行框架会在缺少文件、JSON Pointer 或回读不一致时失败并恢复原文件字节，Registry、SQLite 和 LevelDB 仍需根据真机存储边界实现。正式标记“已验证版本”必须在两台不同采集卡电脑上完成至少 20 次循环。

桌面端发布前必须分别在 Windows 和 macOS 构建、签名并验证真实顶层窗口。Windows 版本还必须验证 Agent、OBS 与直播伴侣的同用户会话通信；macOS 版本必须验证存档浏览、设备管理和远程任务链路。

## 桌面端开发

```shell
dotnet run --project src/LiveStudio.Desktop/LiveStudio.Desktop.csproj
```

同一套 Avalonia UI 在 Windows 与 macOS 构建。Windows 客户端承担本机控制能力；macOS 客户端显示为管理模式，不尝试直接操作只存在于 Windows 的直播伴侣。

## 本地云端开发

需要 .NET SDK 10、PostgreSQL 和 S3 兼容对象存储。先设置以下配置：

```shell
export ConnectionStrings__DefaultConnection='Host=localhost;Port=5432;Database=livestudio;Username=livestudio;Password=...'
export ObjectStorage__ServiceUrl='http://localhost:9000'
export ObjectStorage__Region='us-east-1'
export ObjectStorage__Bucket='livestudio'
export ObjectStorage__AccessKey='...'
export ObjectStorage__SecretKey='...'
export ObjectStorage__UsePathStyle='true'
dotnet run --project src/LiveStudio.Cloud/LiveStudio.Cloud
```

应用启动时自动执行 EF Core migration。浏览器访问启动日志中的 HTTPS 地址，注册用户并创建第一个 Organization。

## 私有部署

```shell
cp .env.example .env
docker compose up --build
```

部署前必须把 `.env` 中的 PostgreSQL、MinIO 和域名值替换为真实值；`.env`、设备凭据、OBS WebSocket 密码和包签名私钥不得提交到仓库。

## Windows Agent

安装 MSIX 后，Agent 会在当前 Windows 登录用户会话内运行。首次使用时：

1. 打开 LiveStudio 桌面端，在“连接与设置”中连接云端并通过浏览器授权。
2. 选择 Organization 和尚未绑定设备的直播间。
3. 点击“注册到所选直播间”。桌面端向云端申请一次性注册凭据，再通过当前用户专属 Named Pipe 交给本机 Agent。
4. 在同一页面启用“登录后自动启动 Agent”。

设备 secret 与存档签名私钥只保存在 Windows Credential Manager，不上传云端。开发阶段保留 `Agent enroll` CLI 仅用于探测机自动化，不是产品主流程。

## 正式发布

Windows 使用签名 MSIX，包内 Desktop 与 Agent 分目录存放，避免两套 self-contained runtime 发生文件覆盖。macOS 使用 Developer ID 签名、Hardened Runtime、公证与 stapling。完整命令、证书前提和发布验收见 [发布说明](docs/release.md)。

## 验证

```shell
dotnet build LiveStudio.slnx
dotnet test LiveStudio.slnx
dotnet list LiveStudio.slnx package --vulnerable --include-transitive
```

默认 solution 只包含不依赖外部服务的测试。真实 PostgreSQL 与 MinIO 集成测试不会在缺少服务时跳过，运行前必须提供：

```shell
export LIVESTUDIO_INTEGRATION_CONNECTION='Host=localhost;Port=5432;Database=livestudio_integration;Username=livestudio;Password=...'
export LIVESTUDIO_INTEGRATION_S3_URL='http://localhost:9000'
export LIVESTUDIO_INTEGRATION_S3_REGION='us-east-1'
export LIVESTUDIO_INTEGRATION_S3_BUCKET='livestudio'
export LIVESTUDIO_INTEGRATION_S3_ACCESS_KEY='...'
export LIVESTUDIO_INTEGRATION_S3_SECRET_KEY='...'
dotnet test LiveStudio.Integration.slnx --configuration Release
```

GitHub Actions 会启动 PostgreSQL 18 和固定版本 MinIO，验证 Identity、Organization 隔离、设备注册、心跳、Multipart 内容一致性，以及共享素材引用与对象删除队列。

换到 Windows 电脑或新开 Codex 对话时，先阅读 [Windows 真机交接与操作手册](docs/windows-validation-handoff.md)，并使用仓库内 `$livestudio-windows-validation` skill。直播伴侣的详细探测流程见 [docs/live-companion-discovery.md](docs/live-companion-discovery.md)。
