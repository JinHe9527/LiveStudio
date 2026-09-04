# LiveStudio 发布说明

正式产物只从干净的源码提交构建。Windows 是本机执行产品；macOS 是远程管理产品，不直接操作 OBS 或抖音直播伴侣。

## 发布前统一检查

```shell
dotnet build LiveStudio.slnx --configuration Release
dotnet test LiveStudio.slnx --configuration Release --no-build
dotnet test LiveStudio.Integration.slnx --configuration Release
dotnet list LiveStudio.slnx package --vulnerable --include-transitive
```

还必须确认：

- 数据库 migration 已在与生产相同主版本的 PostgreSQL 上执行。
- 真实 PostgreSQL/MinIO 集成套件的 Identity、Organization 隔离、设备注册、心跳、Multipart 哈希与对象生命周期测试全部通过。
- 目标 OBS 与直播伴侣版本已经进入 Adapter Catalog 的已验证范围。
- 真实 Windows 电脑完成联合保存、恢复、逐字段回读和失败注入回滚。
- 发布版本没有账号、Cookie、Token、推流密钥或测试凭据。
- 云端只保留首个管理员账户，公开注册已关闭；直播间电脑只保存设备凭据，工作人员不持有管理员密码。
- 每台直播电脑在设置页执行一次“本机检查与修复”，确认 Agent、自启动、两款应用读取、待上传队列和云端下载哈希回读均正常。

## Windows 签名 MSIX

构建机要求 Windows 10/11、.NET 10 SDK、Windows SDK，以及位于当前用户或本机证书库中的代码签名证书。`Publisher` 必须与证书 Subject 完全一致。

首次生成内部签名身份必须在离线 Windows 电脑执行，输出目录不得位于 Git 仓库：

```powershell
$password = Read-Host 'PFX password' -AsSecureString
.\deploy\windows\New-InternalSigningCertificate.ps1 `
  -Publisher 'CN=LiveStudio Internal' `
  -Password $password `
  -OutputDirectory 'D:\LiveStudio-Signing'
```

PFX 和密码不得离开受控的离线备份与 GitHub 加密 Secret。正式下发由 `LiveStudio-Setup.exe` 在用户确认 UAC 后把固定 `.cer` 加入 `Local Machine\Trusted People`；不得加入 `Root`，也不得接受不同 Publisher 或指纹的证书。

```powershell
.\deploy\windows\Build-Msix.ps1 `
  -Version 1.0.0.0 `
  -Architecture x64 `
  -Publisher 'CN=LiveStudio Internal' `
  -CertificateThumbprint '<SHA1 thumbprint>' `
    -TimestampUrl 'http://timestamp.digicert.com'
```

脚本会分别 self-contained publish Desktop 与 Agent，放入 MSIX 的 `Desktop\`、`Agent\` 目录，生成 manifest，使用 SHA-256 签名并执行 `SignTool verify /pa /v`。内部自签名根不会写入一次性构建机的 Root；发布校验只接受签名摘要有效、Signer 指纹完全一致且唯一错误为“内部根尚未受系统信任”的结果。不得将两套 publish 输出直接合并到同一目录。

GitHub Actions Release 发布推荐下载项 `LiveStudio-Setup.exe` 及其 SHA-256、签名报告，同时保留 `LiveStudio-Windows-x64.msix`、MSIX 校验与签名报告和公开证书供审计。仓库必须配置 `LIVESTUDIO_SIGNING_PFX_BASE64`、`LIVESTUDIO_SIGNING_PFX_PASSWORD`、`LIVESTUDIO_SIGNING_PUBLISHER` 三个 Secret；Publisher、证书和 Package Identity 一旦用于首个安装包就不得更换。Release 创建后，流水线必须等待国内镜像的 `latest.json` 切换到同一标签，再从国内版本化地址回下载安装器，复核 SHA-256、固定证书签名和 `--verify-only`。

一键安装器把当前 Release 的 MSIX、SHA-256 和公钥证书作为资源封装在同一个签名 EXE 中。运行前检查 EXE 和 MSIX 的 Signer 指纹；提权后只安装完全匹配的证书，再要求两份 Authenticode 状态均为 `Valid`，随后执行 `Add-AppxPackage`。同版本重复运行只启动现有安装，更高版本执行升级。0.1.21 起，软件内更新优先读取 `https://wuyoupaiban.cn/livestudio/latest.json` 并下载同一份一键安装器，国内服务网络失败或返回 404 时回退到 GitHub；安装前始终校验 SHA-256、固定 Publisher 和证书指纹。

安装后在真实 Windows 用户会话验收：

- 开始菜单图标、顶层窗口图标和系统托盘图标正确。
- 重复启动 Desktop 或 Agent 不产生第二实例。
- 桌面端能通过浏览器授权，并把 Agent 注册到所选直播间。
- 启用自动启动后，注销并重新登录，Agent 与托盘正常出现。
- 托盘双击能打开 Desktop；退出 Agent 后云端在心跳窗口结束时显示离线。
- 开播、推流和录制状态仅展示，不参与恢复阻断；发布说明明确由使用者只在未开播时执行恢复。
- “本机检查与修复”只修复执行端、应用连接和同步链，不会自动执行画面恢复；恢复仍必须由使用者明确选择存档后触发。

## macOS 签名与公证

构建机要求 macOS、.NET 10 SDK、Developer ID Application 证书，以及已通过 `xcrun notarytool store-credentials` 保存的 keychain profile。

```shell
deploy/macos/build-app.sh \
  1.0.0 \
  1 \
  arm64 \
  'Developer ID Application: Example Company (TEAMID)' \
  'livestudio-notary'
```

脚本会生成 `.app`，执行 Hardened Runtime 签名、签名验证、公证、stapling 和 Gatekeeper 检查，最终输出 ZIP。Intel 构建把架构改为 `x64`；两个架构必须分别验收，除非后续明确改为 Universal Binary 构建。

macOS 验收范围仅包括浏览器授权、Organization/直播间切换、存档查看、参数对比、设备映射和远程任务。界面不得显示本机 OBS、直播伴侣或 Agent 控件。

## 尚未满足时不得发布

仓库无法代替真实 Windows 电脑生成抖音直播伴侣的版本证据。内置适配器可作为实验适配器进行完整事务测试；没有完成两台不同采集卡和至少 20 次保存/修改/恢复/回读循环时，不得把该版本标记为已验证。
