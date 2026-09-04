# LiveStudio 直播画面配置中心

源代码采用 [MIT License](LICENSE)；内置色卡与 LUT 素材不在 MIT 授权范围内，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

LiveStudio 用于保存、查看并事务化恢复同一台 Windows 电脑上的 OBS 与抖音直播伴侣画面参数。范围严格限定为设备选择、分辨率、FPS、像素格式、色彩空间、色彩范围、视频滤镜、滤镜顺序和滤镜素材；相机机身参数当前只随存档手动记录，不会通过 HDMI 自动写入相机。软件不处理音频、场景布局、账号、Cookie、Token 或推流密钥。

## 下载与软件更新

[从 GitHub 下载最新版 Windows 安装器](https://github.com/JinHe9527/LiveStudio/releases/latest/download/LiveStudio-Setup.exe)。这是 GitHub 官方固定地址，新版本发布后会自动指向新的安装包。

新电脑首次安装只需下载并双击 `LiveStudio-Setup.exe`。安装器请求一次 Windows 管理员授权，校验自身签名、内置证书、MSIX Publisher 和 SHA-256 后，把固定发布证书加入 `Local Machine\Trusted People`，安装或升级 LiveStudio 并自动启动；不需要手工下载或导入证书。

0.1.23 及后续 Windows 客户端直接从公开 GitHub Releases 检查和下载安装更新。在设置页点击“检查更新”和“下载并安装”即可升级，不需要登录、Token 或重新寻找下载地址。客户端在退出 Desktop 和 Agent 前会先校验配套 SHA-256，再核对固定 Publisher 和证书指纹。0.1.22 及更早版本可用上面的 GitHub 固定链接手动升级一次，此后无需重复下载完整安装入口。

推送 `v主版本.次版本.修订号` 标签会由 GitHub Actions 自动执行完整 Release 构建、全部单元测试、依赖漏洞检查、MSIX 和一键安装器签名复验，再创建 Windows x64 Release。

## 单机直播间使用

当前桌面产品只展示单机直播间工作流，直播间统一管理入口暂时隐藏：

1. 每台直播电脑单独安装 LiveStudio，在“设置”中点击“一键连接”。
2. 在“画面存档”保存当前 OBS、直播伴侣和手动相机参数。
3. 通过右上角更多菜单把所选存档导出为 `.lscfg`。
4. 在另一台直播电脑点击“导入并应用”，选择该文件。LiveStudio 会先验证结构、逐文件哈希、签名、版本、素材和设备对应，再进入现有事务恢复与逐字段回读；任一检查不通过都不会静默写入。

## 当前实现

- `.lscfg`：不可变 ZIP 容器、逐文件 SHA-256、ECDSA P-256 签名、素材归档、敏感字段拒绝与路径安全检查。
- 恢复引擎：统一 Preflight、事务快照、停止、应用、启动、逐项验证、Commit/Rollback；任一验证失败即回滚。
- OBS：基于 obs-websocket 5.x 的来源、视频模式、滤镜、顺序、启用状态、素材和预览图读写适配器。
- 直播伴侣：正式恢复只接受匹配版本和结构指纹的签名适配定义。当前 12.8.1.454484231 精确签名版本已在本机完成保存、来源重建、事务恢复、逐字段回读和故障回滚；跨物理采集卡证据仍未完成，因此状态保持 `Mapped`，不能标记为跨设备 `Verified`。
- Windows Agent：同用户会话运行、Windows Credential Manager 凭据、SQLite 本地索引和本机恢复事务。
- 本机控制：Windows 桌面端通过仅限当前用户的 Named Pipe 读取 Agent、OBS、直播伴侣和存档状态，发起真实联合保存与事务恢复；OBS 密码只写入 Credential Manager。
- 远程任务与云端：实现保留在代码中供后续开发，当前基础版不在桌面界面展示，也不会在启动时主动连接云端。
- 桌面产品：基于 Avalonia 的 Windows 客户端，当前以本机保存、导出、导入和事务恢复为主。
- 对象生命周期：存档包使用 8 MiB S3 Multipart Upload；删除存档时保留共享素材，独占素材、预览和包文件通过持久化删除队列重试清理。
- 桌面 UI：采用 Apple Pro App 的 Split View、Sidebar、Toolbar 和 Inspector 结构，以细分隔线组织信息，不使用卡片式 Dashboard 或 Emoji 图标。
- 私有部署：PostgreSQL、MinIO、云服务与 Caddy 的 Docker Compose 编排。

## 不能伪造的发布门槛

直播伴侣没有公开稳定配置 API。未匹配签名适配定义的版本只允许读取和生成探测证据，不能写入。当前精确签名版本的 `JsonFile` 执行框架会在缺少文件、JSON Pointer 或回读不一致时失败并恢复原文件字节；Registry、SQLite 和 LevelDB 仍需根据真机存储边界实现。正式标记“已验证版本”仍必须在两台不同物理采集卡电脑上完成至少 20 次循环。

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

1. 打开 LiveStudio，在“设置”中点击“一键连接”。
2. 点击“本机检查与修复”，核对执行端、自启动、OBS 和直播伴侣连接。
3. 在“画面存档”保存、导出或导入并应用 `.lscfg`。

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
