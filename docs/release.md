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

只把 `.cer` 安装到测试电脑的 `Local Machine\Trusted People`；PFX 和密码不得离开受控的离线备份与 GitHub 加密 Secret。

```powershell
.\deploy\windows\Build-Msix.ps1 `
  -Version 1.0.0.0 `
  -Architecture x64 `
  -Publisher 'CN=LiveStudio' `
  -CertificateThumbprint '<SHA1 thumbprint>' `
    -TimestampUrl 'http://timestamp.digicert.com'
```

脚本会分别 self-contained publish Desktop 与 Agent，放入 MSIX 的 `Desktop\`、`Agent\` 目录，生成 manifest，使用 SHA-256 签名并执行 `SignTool verify /pa /v`。不得将两套 publish 输出直接合并到同一目录。

GitHub Actions Release 只发布 `LiveStudio-Windows-x64.msix`、SHA-256 和签名验证报告。仓库必须配置 `LIVESTUDIO_SIGNING_PFX_BASE64`、`LIVESTUDIO_SIGNING_PFX_PASSWORD`、`LIVESTUDIO_SIGNING_PUBLISHER` 三个 Secret；Publisher、证书和 Package Identity 一旦用于首个测试安装包就不得更换。首次安装前把公钥证书导入测试电脑 `Trusted People`，并人工核对指纹。

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
