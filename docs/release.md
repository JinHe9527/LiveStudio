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

## Windows 签名 MSIX

构建机要求 Windows 10/11、.NET 10 SDK、Windows SDK，以及位于当前用户或本机证书库中的代码签名证书。`Publisher` 必须与证书 Subject 完全一致。

```powershell
.\deploy\windows\Build-Msix.ps1 `
  -Version 1.0.0.0 `
  -Architecture x64 `
  -Publisher 'CN=LiveStudio' `
  -CertificateThumbprint '<SHA1 thumbprint>' `
  -TimestampUrl 'https://timestamp.example.com'
```

脚本会分别 self-contained publish Desktop 与 Agent，放入 MSIX 的 `Desktop\`、`Agent\` 目录，生成 manifest，使用 SHA-256 签名并执行 `SignTool verify /pa /v`。不得将两套 publish 输出直接合并到同一目录。

安装后在真实 Windows 用户会话验收：

- 开始菜单图标、顶层窗口图标和系统托盘图标正确。
- 重复启动 Desktop 或 Agent 不产生第二实例。
- 桌面端能通过浏览器授权，并把 Agent 注册到所选直播间。
- 启用自动启动后，注销并重新登录，Agent 与托盘正常出现。
- 托盘双击能打开 Desktop；退出 Agent 后云端在心跳窗口结束时显示离线。
- 推流、录制或开播时恢复在任何写入之前被拒绝。

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
