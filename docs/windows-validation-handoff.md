# LiveStudio Windows 真机交接与操作手册

这份文档是 Windows 电脑和新 Codex 对话的持久交接入口。聊天记录不是项目事实来源；仓库代码、`AGENTS.md`、本手册和真机证据才是。

## 1. 新电脑第一次准备

1. 在 Windows 上安装 Git、GitHub CLI 和 .NET 10 SDK。
2. 登录有权访问私有仓库 `JinHe9527/LiveStudio` 的 GitHub 账号。
3. Clone 仓库并在 Codex 中打开仓库根目录，不要只复制安装包或单个源码目录。
4. 使用隔离的 Windows 测试账号。不要登录真实直播账号，不要使用正在生产开播的配置做探测。
5. 安装需要测试的 OBS、Windows 抖音直播伴侣和采集卡驱动，记录精确版本。

PowerShell 基线检查：

```powershell
git status --short
git branch --show-current
git log -1 --oneline
dotnet --info
dotnet restore LiveStudio.slnx
dotnet build LiveStudio.slnx --configuration Release --no-restore
dotnet test LiveStudio.slnx --configuration Release --no-build
```

2026-08-23 的功能基线是 `73dec26`：桌面设置页已能选择直播伴侣修改前/修改后原生导出 ZIP，并显示逐字段差异和敏感路径警告；当时 58 个单元测试通过。这个数字只是交接检查点，后续 commit 增加测试时应以当前仓库结果为准。

## 2. 新对话怎么开始

打开仓库后，把下面这句话发给新对话：

> 使用仓库内 `$livestudio-windows-validation` skill。先完整读取 `AGENTS.md` 和 `docs/windows-validation-handoff.md`，核对当前代码与真机证据后，继续 Windows 抖音直播伴侣和 OBS 的完整参数探测与恢复验收。不要把原生导出审计当成已完成恢复，也不要在缺少签名适配器时写入直播伴侣。

新对话应该先报告：

- 当前分支、最新 commit、工作区是否干净。
- OBS、直播伴侣、Windows、采集卡和驱动版本。
- 已有证据覆盖了哪些字段，哪些字段仍然未知。
- 本轮只做哪一个单变量实验。

如果它直接声称“直播伴侣已经完全支持”，或者没有检查版本和证据就准备写配置，应立即停止。

## 3. 当前真实能力边界

已经可以使用：

- `.lscfg` 的哈希、签名、素材归档、敏感字段和路径安全基础能力。
- OBS 的 obs-websocket 5.x 读取、写入、滤镜顺序、启用状态、素材和回读框架。
- 直播伴侣配置位置探测工具，以及 JSON、SQLite、LevelDB、普通文件和 Registry 的结构/哈希观察。
- 直播伴侣原生导出 ZIP 的安全解包、结构识别、JSON 路径/类型/值哈希比较和敏感路径提示。
- 桌面设置页中的原生导出修改前/修改后对比。

尚未完成：

- 没有真实 Windows 直播伴侣版本的完整字段矩阵和签名适配定义。
- 没有证明直播伴侣原生导出包含设备、视频模式、全部滤镜、美颜、曲线和素材。
- 正式执行端只实现签名定义下的 `JsonFile` 存储，Registry、SQLite、LevelDB 尚未依据真机结构实现写入和事务回滚。
- 没有两台不同采集卡电脑和连续 20 次逐字段一致的验收结果。
- 因此当前直播伴侣探测结果只允许查看，不能标记为可靠恢复。

## 4. 为什么必须先验证直播伴侣原生导出

如果直播伴侣自带“整套导出/导入”，它可能是最接近应用自身语义的入口，值得优先验证。但必须回答三个问题：

1. 它是否真的包含全部目标字段，而不仅是部分美颜预设或素材引用？
2. 它是否包含账号、登录态、Cookie、Token、设备唯一信息等禁止进入存档的数据？
3. 在不同采集卡、不同电脑和不同直播伴侣版本上导入后，设备映射、素材路径和字段回读是否完全一致？

只有这三个问题都有真机证据，原生导入才能进入事务恢复。否则它只是一个探测来源。

## 5. 原生导出单变量实验

每轮只修改一个参数。例如只把“磨皮 45”改成“磨皮 46”，不能同时切换美颜预设或改变其他滑块。

1. 确认未开播，关闭 OBS 推流/录制，并记录直播伴侣、OBS 原本的运行状态。
2. 在直播伴侣中导出修改前 ZIP。
3. 截图记录参数所在 UI 层级、修改前值和所有相邻控件。
4. 只修改一个参数，按正常流程让应用落盘，再导出修改后 ZIP。
5. 打开 LiveStudio 的“设置 → 直播伴侣原生导出验证”。
6. 选择修改前 ZIP，再选择修改后 ZIP。
7. 保存界面显示的变化字段、变化条目和敏感路径结果。
8. 如果没有字段变化，将该参数标记为“原生导出未覆盖”，转入 File/Registry/SQLite/LevelDB 探测。
9. 如果出现多个不相关业务字段，检查时间戳、缓存、随机 ID 和伴随状态；本轮不能直接用于生成字段映射。
10. 原始 ZIP 留在隔离测试机，未经敏感字段审计不得上传 GitHub、云端或聊天。

也可以使用 CLI 保留 JSON 报告：

```powershell
dotnet run --project tools/LiveStudio.Discovery.Windows -- inspect-export `
  --name before-smooth `
  --input "artifacts/native-export/before-smooth.zip" `
  --output "artifacts/native-export/before-smooth.report.json"

dotnet run --project tools/LiveStudio.Discovery.Windows -- inspect-export `
  --name after-smooth `
  --input "artifacts/native-export/after-smooth.zip" `
  --output "artifacts/native-export/after-smooth.report.json"

dotnet run --project tools/LiveStudio.Discovery.Windows -- diff-export `
  --before "artifacts/native-export/before-smooth.report.json" `
  --after "artifacts/native-export/after-smooth.report.json" `
  --output "artifacts/native-export/smooth.diff.json"
```

报告只保存路径、类型和哈希，不保存 JSON 原始值。

## 6. 必须逐项完成的参数矩阵

### 设备和视频模式

- 设备选择、设备内部 ID、设备槽位和设备缺失状态。
- 分辨率、FPS 的整数/分数表示、像素格式。
- 色彩空间、色彩范围以及 UI 中能选择的其他视频模式字段。
- 同一采集卡的多个模式、不同采集卡映射和目标模式不支持。

### OBS

- 每个视频采集来源的全部 `inputSettings`，包括未知但真实存在的嵌套字段。
- 每个视频滤镜的 kind、名称、启用状态、顺序和全部嵌套设置。
- LUT、遮罩、图像及插件滤镜引用的每一个素材路径。
- 来源存在/不存在、OBS 运行/关闭、插件存在/缺失时的行为。

音频来源、音频滤镜和场景布局不属于本项目范围。

### 直播伴侣滤镜与美颜

- 美颜总开关、当前预设、预设 ID、强度和默认值。
- 磨皮、美白、红润、肤色、清晰度及 UI 中出现的其他美肤项。
- 瘦脸、脸宽、颧骨、下颌、下巴、额头、眼睛、眼距、鼻子、嘴型等所有塑形项。
- 美妆类型、素材 ID、颜色、强度、透明度、资源版本和资源文件。
- Master、R、G、B 曲线；每个控制点的 X/Y、数值类型、数组顺序、插值方式、通道启用状态。
- 所有视频滤镜的类型、顺序、启用状态、嵌套参数、素材和内部引用。
- 数组、对象、空值、默认值、当前值，以及目标配置树中新发现的未知字段。

不能只按这份列表建立白名单。真机 UI 或原生结构出现新的目标字段时，必须加入矩阵；它未被保存和回读前，覆盖率不能是 100%。

## 7. 原生导出未覆盖时的存储探测

关闭直播伴侣，对候选 AppData 和 Registry 位置采集基线；启动应用只改一个参数，正常退出后再次采集并对比：

```powershell
dotnet run --project tools/LiveStudio.Discovery.Windows -- capture `
  --name before-smooth `
  --output artifacts/discovery/before-smooth.json `
  --root "$env:APPDATA\webcast_mate" `
  --process StreamingTool

dotnet run --project tools/LiveStudio.Discovery.Windows -- capture `
  --name after-smooth `
  --output artifacts/discovery/after-smooth.json `
  --root "$env:APPDATA\webcast_mate" `
  --process StreamingTool

dotnet run --project tools/LiveStudio.Discovery.Windows -- diff `
  --before artifacts/discovery/before-smooth.json `
  --after artifacts/discovery/after-smooth.json `
  --output artifacts/discovery/smooth.diff.json
```

实际路径和进程名必须先在测试机确认，示例不是可靠事实。还要观察：

- 正常退出和强制结束的差异。
- 进程运行时和退出后的差异。
- SQLite 主文件、`-wal`、`-shm` 事务边界。
- LevelDB 目录整体事务边界。
- Registry 值的类型、视图和写入时机。
- 只有进程存活时出现的 IPC 行为。

## 8. 从证据生成正式适配器

只有字段矩阵的每一项都达到 `Mapped`，才进入适配器实现：

1. 固定支持的直播伴侣版本范围和只包含结构、不包含参数值的结构指纹。
2. 为每个字段记录存储位置、原生路径、类型、统一字段、读取、写入和回读方式。
3. 明确所有敏感字段和禁止归档的原生文档。
4. 按真实存储边界实现事务备份与回滚。SQLite 必须考虑附属文件，LevelDB 必须按目录一致性处理，Registry 必须保留原始类型和值。
5. 生成适配定义和签名文件，放入 Agent 可发现的 `Adapters` 目录。
6. 使用脱敏真实 fixture 完成读写往返、结构变化、参数值变化、素材别名、敏感字段和失败注入测试。
7. 未匹配签名定义的版本保持只读，不能启用猜测写入。

## 9. 恢复验收顺序

每次恢复必须遵循：

```text
Preflight
→ 事务快照
→ 停止应用
→ 物化素材与路径绑定
→ 应用目标字段
→ 启动应用
→ 逐字段回读
→ Commit 或完整 Rollback
```

Preflight 在任何写入前检查开播/推流/录制、应用版本、结构指纹、设备映射、完整视频模式、滤镜插件、素材、磁盘空间、写权限和目标场景。任何一项失败都不能写入。

验收必须覆盖两台电脑、两种采集卡、目标 OBS 版本和至少两个直播伴侣版本。每个准备标记为可靠的组合连续执行至少 20 次：保存 → 修改 → 恢复 → 逐字段回读。还要在每个事务阶段注入失败，证明原生字节/值、OBS 目标配置和应用原始运行状态均能恢复。

## 10. 怎么把结果交给下一次对话

把不含原始敏感值的报告放在测试机 `artifacts/windows-validation/` 下，并按照 skill 的 `references/evidence-schema.md` 填写实验记录。向新对话提供：

- 实验记录 JSON。
- 修改前/修改后 UI 截图，遮挡账号和个人信息。
- `inspect-export`、`diff-export` 或 `capture`、`diff` 报告。
- 明确说明原始 ZIP 是否包含敏感路径；默认不要上传原始 ZIP。
- 对应的应用、系统、硬件和源码 commit 版本。

新对话收到证据后，应先判断 `Invalid`、`EvidenceOnly`、`Mapped` 或 `Verified`，再决定继续探测、实现存储后端、生成签名适配器还是执行恢复验收。

## 11. 完成标准

只有同时满足以下条件，才能向用户说明“这个直播伴侣版本能够可靠保存和还原目标范围内全部参数”：

- 真机参数矩阵没有未覆盖字段。
- 每个字段都有真实原生位置、类型、读写和回读证据。
- 存档敏感字段扫描为零命中。
- 设备、视频模式、滤镜、美颜、曲线、素材和路径绑定逐字段一致率 100%。
- 两台不同采集卡电脑的连续 20 次循环全部通过。
- 每个恢复阶段失败后的原状态一致率 100%。
- 开播、推流或录制阻断发生在任何写入之前。

在此之前，正确表述是“已完成探测工具/证据采集/实验适配”，不能表述为完整可靠恢复。
