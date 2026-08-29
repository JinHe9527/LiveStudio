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

Preflight 在任何写入前检查应用版本、结构指纹、设备映射、完整视频模式、滤镜插件、素材、磁盘空间、写权限和目标场景。开播、推流和录制状态只展示，不参与恢复阻断；软件使用者负责只在未开播时执行恢复。

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
- 开播、推流和录制状态不参与恢复判定；其他全部 Preflight 失败仍发生在任何写入之前。

在此之前，正确表述是“已完成探测工具/证据采集/实验适配”，不能表述为完整可靠恢复。

## 12. 2026-08-25 本机显示映射与存档验收记录

本轮在 Windows 11 Pro、OBS Studio 32.2.2、抖音直播伴侣 12.8.1.454484231 上完成了展示层字段归组与真机存档检查。直播伴侣原生菜单、字段名称和枚举语义来自该版本随包代码，不是根据内部键猜测：

- `8481.6590504c.js`：基础设置、美颜设置、美体设置、美妆设置、滤镜设置、特效道具、绿幕抠图、镜头特效、导入导出的面板键与中文名称。
- `71180.038bad63.js`：色彩空间 `709/601`、色彩范围 `全部/局部` 的原生选项。
- `22226.a7c61b6e.js`：像素格式、色彩空间和色彩范围的数值枚举。
- 直播伴侣随包 `loki.zip/data.json`：57 个当前美颜/美体参数的官方中文名称。

最终保留 1 份重新生成的 `.lscfg`。读取器完成签名、清单哈希、字段覆盖哈希、文件哈希与敏感数据扫描后成功打开。直播伴侣 4 份原生文档共 1028 个叶字段：

- `effectConfigStore.json`：754 项。
- `effectStore.json`：119 项。
- `filterStore.json`：8 项。
- `sourceStore.json`：147 项。

八个原生菜单的展示归组结果为：

- 基础设置 14 项。
- 美颜设置 756 项，其中默认显示 57 个当前参数；另有 4 组、114 个预设参数通过独立开关查看。
- 美妆设置 0 项，明确显示“当前无内容”。
- 滤镜设置 138 项，包含 8 个滤镜及 Master、R、G、B 和 6 类色相关系曲线控制点。
- 特效道具 117 项，默认只显示 8 个有内容且可理解的设置；活动 ID 和内部缓存只保留在技术详情。
- 绿幕抠图 1 项，当前值为关闭，因此默认不显示。
- 镜头特效 2 项，当前值均为关闭，因此默认不显示。
- 导入导出 0 项，明确显示“当前无内容”。

本轮字段覆盖状态严格保持为：`Unknown=0`、`EvidenceOnly=1028`、`Mapped=0`、`Verified=0`。这里的“展示归组完成”只表示所有已读取字段都有可理解的原生菜单位置；它没有提升写入证据，也没有生成 12.8.1 的签名恢复适配器。

真机界面证据位于：

- `artifacts/windows-validation/2026-08-25/current-machine/final/final-live-companion-basic-settings-v2.png`
- `artifacts/windows-validation/2026-08-25/current-machine/final/final-live-companion-beauty-settings.png`
- `artifacts/windows-validation/2026-08-25/current-machine/final/final-live-companion-filter-settings.png`
- `artifacts/windows-validation/2026-08-25/current-machine/final/final-live-companion-props-settings.png`

验收过程中没有停止或修改 OBS 与抖音直播伴侣。结束时 OBS 仍为原 PID 30032，直播伴侣仍为原来的 11 个进程。Release 构建为 0 错误、0 警告；108 个测试全部通过；所有直接和传递 NuGet 包均无已知漏洞；`git diff --check` 通过。

下一项最小单变量实验仍是：先从可确认“未开播”的直播伴侣界面记录并导出基线，只把“磨皮”从当前界面值 33 改为 34，等待正常落盘，再导出修改后数据；只保留路径、类型和哈希差异，用于继续提升该字段的真机验证证据。

## 13. 2026-08-25 当前真机签名恢复适配与执行链记录

在第 12 节的只读展示验收之后，本轮继续针对当前机器的精确版本和精确结构建立签名恢复适配。此记录覆盖的环境为 Windows 11 专业版 10.0.26200、OBS Studio 32.2.2、抖音直播伴侣 12.8.1.454484231。适配器只匹配下列单一组合，不会用于其他版本或其他结构：

- 适配器 ID：`webcast-mate-12.8.1.454484231-68ba3cc2`。
- 结构指纹：`68ba3cc2b53cc19deaff9633f7d2e1ab1dbd36345ae44ef6e234b830c25816b1`。
- 定义 SHA-256：`3a468c8ffe7d3ccf695909518d095d9861fb4a33bb9d14792849204efc45731c`。
- 原生存储边界：`effectConfigStore.json`、`effectStore.json`、`filterStore.json`、`sourceStore.json` 四份 JSON 文档。
- 字段状态：`Unknown=0`、`EvidenceOnly=0`、`Mapped=1028`、`Verified=0`；1028 项全部具有精确原生位置、类型、写入和回读规则，且全部为必需字段。

Agent 已在 Release 输出中加载并验签该定义；重新保存的本机联合存档包含 OBS 33 项和直播伴侣 1028 项，共 1061 项。桌面端逐项计算结果为“已保存 1061 项 · 可恢复 1061 项”，两款应用均显示“已保存 · 可一键还原”。这表示当前版本、当前结构和当前存档已经进入正式事务执行路径，不表示达到 `Verified`。

直播伴侣执行路径当前具备：

1. 写入前重新检查精确版本、结构指纹、存储文档、字段路径、字段类型、素材、磁盘空间和权限。
2. 以四份真实 JSON 文档为事务边界保存原始字节备份和持久化事务日志。
3. 确认全部直播伴侣进程退出后才原子写入；仍有辅助进程存活时整次恢复在写入前失败。
4. 写入 1028 个必需字段后重启应用，并按相同精确路径和类型逐字段回读。
5. 任一字段缺失、类型不符或值不一致时恢复四份原始文档并重启原应用；不能静默跳过字段。

OBS 当前两项来源均为 `monitor_capture`。恢复不再伪造摄像头设备字段，而是按原来源名在原位恢复完整 `inputSettings`、5 个滤镜的 kind、名称、启用状态、顺序、嵌套参数与素材。只有真实存在 `video_device_id` 的来源才要求设备映射。

直播伴侣的 Chromium 页面当前不能通过 Win32 文本或 UI Automation 可靠读出“是否开播”。按照软件使用者 2026-08-25 的明确要求，恢复不再读取或判断 OBS 推流、OBS 录制和直播伴侣开播状态，本机与无人值守恢复均不会被这些状态阻断。界面仍可显示能够读取到的状态，但恢复按钮直接执行事务恢复。软件使用者负责只在未开播时操作。

本轮未执行真实恢复，因为用户本次指令只要求彻底移除限制，并未要求立即恢复当前存档。上述字段仍为 `Mapped`，不是 `Verified`；尚缺少一次真机保存 → 修改 → 恢复 → 逐字段回读闭环，以及第二台不同采集卡机器和连续 20 次故障回滚验收。

本轮真机界面证据位于：

- `artifacts/windows-validation/2026-08-25/current-machine/restorable-ui-printwindow.png`
- `artifacts/windows-validation/2026-08-25/current-machine/final-snapshot-restorable.png`
- `artifacts/windows-validation/2026-08-25/current-machine/final-live-companion-restorable.png`
- `artifacts/windows-validation/2026-08-25/current-machine/final-live-companion-compact-filter.png`

移除直播状态限制前的 Release 构建结果为 0 错误、0 警告，114 个测试全部通过；限制移除后的结果以本节后续记录为准。下一项最小验收动作是直接执行当前 1061 项联合存档的一次身份恢复，并保存事务日志和逐字段回读结果。

### 13.1 直播状态限制已按使用者要求移除

2026-08-25，软件使用者明确说明只会在未开播时操作，并要求完全取消软件内的开播、推流和录制限制。当前实现已完成以下调整：

- `RestoreCoordinator` 不再读取 `IsStreaming`、`IsRecording` 或 `CanDetermineLiveState`，本机和无人值守任务行为一致。
- 直播伴侣签名适配器不再因开播状态未知而拒绝无人值守写入。
- 本机 Agent 的 `CanRestore` 只由身份、适配器、存档和忙碌状态决定。
- 开播、推流和录制状态仍显示在控制室，但不再进入恢复判断。
- 按钮统一改为“恢复画面存档”或“恢复所选存档”，不再要求二次确认未开播。
- 回归测试明确构造“正在推流且正在录制”的运行状态，并验证恢复仍会创建事务、物化素材、逐字段验证和提交。
- 云端任务状态机不再允许新任务进入 `BlockedByLiveSession`；该枚举值只为读取历史任务保留。

限制移除后重新完成 Release 构建：0 错误、0 警告；115 个测试全部通过。

## 14. 2026-08-25 画面存档高密度界面验收

画面存档页面已改为系统主题自适应的高密度检查界面；本轮只修改展示投影、主题资源和页面信息架构，没有改变 `.lscfg` 格式、捕获逻辑或事务恢复协议。当前真机 1 份联合存档仍完整包含 OBS 33 项与直播伴侣 1028 项，共 1061 项。

默认展示投影已经执行以下规则：

- 隐藏 `enable/use/active/switch/status` 等启用字段和所有“已开启”值；关闭项目及其子参数不进入默认页面。
- 水平翻转、镜像等独立布尔功能为真时只显示功能名称，为假时隐藏。
- 未改变的默认值、GUID、内部 ID、路径、哈希、结构字段和默认曲线不进入默认页面，但仍完整保留在存档与技术信息抽屉。
- 曲线控制点、颜色和其他有业务含义的数值零继续保留。
- OBS 默认只列已启用的视频滤镜；完整 `inputSettings`、禁用滤镜和技术键仍可在技术信息中检查。

真机 UI 验收结果：

- 1280×800 下直播伴侣 57 个当前美颜/美体参数使用三列、单屏展示，无分页。
- 1920×1080 下滤镜按原始顺序分为 HSL、白平衡、高斯模糊、镜头虚化、色彩增强、曲线和 LUT 七个生效分组；关闭的第八个滤镜不显示。
- 直播伴侣菜单保持原生顺序且不折叠，但只列当前存档中有已修改或生效内容的菜单；当前真机只显示基础、美颜、滤镜和特效，美妆、绿幕、镜头与导入导出不占用左栏空间。
- 默认页面 UI Automation 文本不存在“已开启、已保存、已读取、可恢复”字段列；搜索默认隐藏，可由工具栏或 `Ctrl+F` 调出。
- 技术信息抽屉通过菜单或 `Ctrl+I` 打开，逐字段显示完整原生路径、类型、Evidence 状态和恢复规则。
- 浅色与深色覆盖控制室、画面存档、设备映射、操作记录和设置全部页面；“设置 → 外观”可以选择跟随系统、浅色或深色，并持久保存到当前 Windows 用户配置。

界面证据位于：

- `artifacts/ui-validation/2026-08-25/snapshots-obs-1280x800.png`
- `artifacts/ui-validation/2026-08-25/snapshots-livecompanion-1280x800-3col.png`
- `artifacts/ui-validation/2026-08-25/snapshots-livecompanion-filter-1920x1080-final.png`
- `artifacts/ui-validation/2026-08-25/snapshots-technical-panel-1280x800.png`
- `artifacts/ui-validation/2026-08-25/snapshots-livecompanion-dark-1280x800.png`
- `artifacts/ui-validation/2026-08-25/snapshots-livecompanion-light-adaptive-1280x800.png`
- `artifacts/ui-validation/2026-08-25/snapshots-livecompanion-light-adaptive-1920x1080.png`
- `artifacts/ui-validation/2026-08-25/snapshots-livecompanion-dark-adaptive-1280x800.png`
- `artifacts/ui-validation/2026-08-25/theme-light-settings-selector-1280x800.png`
- `artifacts/ui-validation/2026-08-25/theme-dark-settings-selector-1280x800.png`
- `artifacts/ui-validation/2026-08-25/theme-light-mappings-1280x800.png`
- `artifacts/ui-validation/2026-08-25/theme-light-activity-1280x800.png`

最终质量检查：Release 构建 0 错误、0 警告；新增自适应列数和空菜单过滤回归后，125 个测试全部通过；直接和传递 NuGet 包均无已知漏洞；`git diff --check` 通过。UI 验收没有停止或修改 OBS 与抖音直播伴侣，也没有执行真实恢复，因此 `Mapped=1028`、`Verified=0` 的证据边界保持不变。

## 15. 2026-08-25 字段精度、恢复缓存边界与删除界面复验

本节覆盖第 13、14 节之后的最新实现和真机结果；与旧数字冲突时以本节为准。

OBS 滤镜捕获已补充 obs-websocket `GetSourceFilterDefaultSettings`，并以显式设置覆盖默认设置，解决滤镜列表省略默认键时页面无数据的问题。当前最终存档实测：

- “锐化”类型为 `sharpness_filter_v2`，参数 `sharpness=0.07`，默认页显示“锐化强度 0.07”。
- “亮度键”4 项、“色值”8 项、“色彩校正”8 项均显示完整有效参数；颜色和枚举在默认页使用中文语义。
- OBS 共 2 个视频来源、5 个滤镜；第二个来源当前没有滤镜，因此不显示无意义占位。

直播伴侣 HSL 原生数据保留 8 个色点、34 个字段；默认页只显示真实改变的红色点：`hueAdjust=0.04`、`saturationAdjust=0.03`，并完整显示为“色相 0.04 · 饱和度 0.03”。其余 7 个全零色点仍在存档和技术信息中，但不占默认页面。礼物系统能力表、活动来源缓存和运行时特效 ID 不再让“特效道具”菜单产生假阳性；当前菜单只显示基础设置、美颜设置、滤镜设置。

当前签名适配器为 `webcast-mate-12.8.1.454484231-68ba3cc2-v2`，定义 SHA-256 为 `d3444fc8fe51744b3a962f367504356a6fd9d48a6257a45edabdbdc06c9d6d44`：

- 1028 个声明字段中，1018 个画面配置字段为必需、可写、必须回读一致。
- 10 个直播伴侣自行重建的运行时缓存为可选、不可写：1 个 `deviceFacing` 缓存、1 个礼物缓存和 8 个滤镜运行时 ID。
- 适配器匹配仍要求签名、精确版本以及全部 1018 个必需字段的原生路径和类型一致；只允许上述 10 个已声明 `ApplicationManaged` 字段缺失或重建，不会把任意结构当作可写版本。
- 存档和目标使用签名定义的规范结构指纹；恢复写入和回读投影只包含 1018 个可写字段。

20:15 重新生成并最终保留的存档为 `d2279cbe0f0148bab235462752e64163.lscfg`，大小 408311 字节，SHA-256 为 `50CDD4E141822EAD0392A3ED20B9EACC56166CDAE2E1167FFB582823613ED2D3`。独立回读结果：目标/当前版本、适配器和规范结构指纹一致；1028 项完整投影 `MISSING=0`、`EXTRA=0`、`CHANGED=0`；1018 项可写投影同样全部为 0。此前 19:49 的一次身份恢复已完成 Commit，事务目录清空；这仍只是当前一台电脑、一个版本的一次闭环，不提升为 `Verified`。

删除界面已拆成两个独立入口：

- “删除当前存档…”只确认并删除当前选择，确认文字包含具体存档名称。
- “清空全部存档…”与单份删除用分隔线隔开，显示独立确认窗和总数量。
- 取消按钮默认获得焦点；危险按钮使用语义红色；确认窗继承当前主窗口的浅色或深色主题。
- 删除当前存档前关闭技术信息；成功后选择相邻存档并重新加载详情。删除最后一份时进入完整空状态，不保留旧标题、字段、警告或 400px 抽屉占位。

删除执行端同时改为文件与索引一致的暂存提交：受管 `.lscfg` 会先在同一目录原子改名为专用删除暂存文件，随后才提交 SQLite 索引删除；如果暂存或索引提交失败，文件会改回原名，页面不会丢失仍然存在的存档。单份删除、全部删除、文件被占用和越界路径拒绝四类 Windows 回归测试均已通过。

本轮先生成新的签名存档，再通过真实删除确认窗移除旧的探测存档。删除后 UI Automation 确认时间线为 1 份、自动选择 20:14 存档、页面无旧恢复警告；磁盘也只剩上述最终 `.lscfg`。

完成审计又在当前 Release 中执行了一次真实“保存当前画面 → 删除新存档”闭环。20:55 临时生成的 `c8cf81b3e2e7479390963cec34ad0662.lscfg` 包含 OBS 45 个覆盖字段和直播伴侣 1028 个覆盖字段；两份 `native/obs.json`、`native/livecompanion.json` 的 SHA-256 与 20:15 正式存档逐字节一致，适配器定义、规范结构指纹和字段覆盖哈希也一致。临时包没有直播伴侣预览图，因此压缩包体积较小，但参数主体没有缺失。随后通过浅色确认窗删除临时包，UI Automation 确认“取消”为默认焦点、提示包含准确存档名；删除完成后自动选择 20:14 正式存档，临时项消失，磁盘只剩 1 份正式包且其大小和 SHA-256 均未变化。

同一轮 UI Automation 完成以下反证检查：

- 1280×800 美颜页首项“大眼”、中间项“磨皮”和末项“直角肩”均在屏内，没有可见水平或纵向滚动条。
- 直播伴侣左栏只出现基础设置、美颜设置、滤镜设置；美妆、特效道具、绿幕抠图、镜头特效、导入导出均不占位置。
- 默认字段区不存在“已开启、已保存、已读取、可恢复”状态文本。
- 深色主题下逐页打开控制室、画面存档、设备映射、操作记录和设置，所有页面均使用深色语义背景及可读前景；审计结束后主题偏好恢复为 `system`。
- 存档原始值直接复核：OBS 锐化为 `sharpness_filter_v2/sharpness=0.07`；直播伴侣 HSL 红色点为 `hueAdjust=0.04`、`saturationAdjust=0.03`、`lightnessAdjust=0`；Master 曲线三个位置点为 `0/0`、`0.5257352941176471/0.5555555555555556`、`1/1`。

最新界面证据：

- `artifacts/ui-validation/2026-08-25/delete-current-dialog-light-fixed.png`
- `artifacts/ui-validation/2026-08-25/delete-current-dialog-dark-fixed.png`
- `artifacts/ui-validation/2026-08-25/snapshot-after-delete-light.png`
- `artifacts/ui-validation/2026-08-25/live-companion-hsl-light.png`
- `artifacts/ui-validation/2026-08-25/live-companion-hsl-light-1920x1080.png`
- `artifacts/ui-validation/2026-08-25/live-companion-hsl-dark-1920x1080.png`
- `artifacts/ui-validation/2026-08-25/final-system-theme-live-companion-hsl-no-clipping.png`
- `artifacts/ui-validation/2026-08-25/final-release-livecompanion-light-transactional-delete.png`
- `artifacts/ui-validation/2026-08-25/final-release-light-maximized-adaptive.png`
- `artifacts/ui-validation/2026-08-25/more-menu-current-release.png`
- `artifacts/ui-validation/2026-08-25/completion-audit-delete-new-snapshot-light.png`
- `artifacts/ui-validation/2026-08-25/completion-audit-dark-control-1280x800.png`
- `artifacts/ui-validation/2026-08-25/completion-audit-dark-snapshots-1280x800.png`
- `artifacts/ui-validation/2026-08-25/completion-audit-dark-mappings-1280x800.png`
- `artifacts/ui-validation/2026-08-25/completion-audit-dark-activity-1280x800.png`
- `artifacts/ui-validation/2026-08-25/completion-audit-dark-settings-1280x800.png`

最终质量检查：Release 构建 0 错误、0 警告；143 项测试全部通过（展示与恢复核心 139 项、Agent 删除一致性 4 项）；所有直接和传递 NuGet 包均无已知漏洞；`git diff --check` 通过。当前证据状态仍为 `Mapped=1028`、`Verified=0`。下一项最小单变量实验仍是把直播伴侣界面“磨皮”从 33 改为 34，等待正常落盘，保存新存档后执行恢复并只比较该字段及完整可写投影；之后才能继续累计 20 次循环和第二台采集卡机器证据。

## 16. 2026-08-26 关闭状态保存与 OBS/直播伴侣来源联合重建验收

本节覆盖第 15 节之后的当前 Release；与旧存档数量、适配状态或恢复能力冲突时以本节为准。环境为 Windows 11 专业版 10.0.26200、OBS Studio 32.2.2、抖音直播伴侣 12.8.1.454484231。当前机器只有 OBS Virtual Camera，没有物理采集卡，因此本节只能维持 `Mapped`，不能授予 `Verified`。

直播伴侣关闭时的安装发现和存档匹配已补齐：

- 进程未运行时，从已验证的安装路径规则解析 `D:\webcast_mate\12.8.1.454484231\直播伴侣.exe` 及文件版本，不再把版本写成 `unknown`。
- 签名定义中的场景、来源和效果 ID 视为规范 ID；当前原生树中由直播伴侣重建的运行时 ID 会先进行唯一摄像头绑定，再把读写路径翻译到当前 ID，捕获时重新规范化。绑定不唯一时仍然失败关闭，不会猜测来源。
- 事务边界增加 `storage/camera-payloads.json`，与四份 WBStore 文档一起备份、写入、回读和回滚。删除摄像头后仅恢复 WBStore 会被 MediaSDK 再次清除，因此当前精确签名版本使用直播伴侣自己的“添加摄像头”和“导入配置文件”界面重建来源与效果链。
- 原生点击前临时将目标直播伴侣窗口置顶并激活，点击后立即撤销置顶，修复后台 Agent 第一次点击可能落到 LiveStudio 窗口、表现为没有出现添加摄像头窗口的问题。

关闭直播伴侣后保存的新正式存档为 `6cfed811cf5c4188be269191b1343a40.lscfg`，大小 238775 字节，SHA-256 为 `9B18074797614D66D599CAAD5CA0EF773AB9775C7AAB6017B1B29B33064A6929`。存档匹配适配器 `webcast-mate-12.8.1.454484231-68ba3cc2-v2`，包含直播伴侣 4 份文档、1028 个字段，覆盖为 `Unknown=0`、`EvidenceOnly=0`、`Mapped=1028`、`Verified=0`；OBS 包含 2 个视频来源和 5 个滤镜。

本轮执行了产品级联合破坏恢复：

1. 通过 obs-websocket 从场景“场景”删除来源“显示器采集”，只保留“显示器采集 2”。
2. 通过直播伴侣原生界面删除唯一摄像头“OBS Virtual Camera”，正常关闭后独立确认摄像头数为 0。
3. 在直播伴侣关闭状态，从 LiveStudio 桌面端选择 02:01 存档并点击“恢复所选存档”。
4. 第一次原生窗口未激活的尝试安全失败并完整回滚；加固窗口激活后，第二次显示“已应用并通过逐字段回读”并提交事务。
5. 提交后独立回读：OBS 场景同时存在“显示器采集 2”和重建的“显示器采集”，5 个滤镜顺序与参数一致，“锐化”为 `sharpness_filter_v2/sharpness=0.07`。
6. 直播伴侣重建 1 个摄像头，视频模式为 1920×1080、30 FPS、格式 6、色彩空间 2、色彩范围 2；8 个滤镜完整存在，HSL 红色点为色相 0.04、饱和度 0.03、明度 0。
7. 恢复开始和结束时直播伴侣均为关闭状态，提交后进程数为 0；事务目录为空。

真机证据记录位于：

- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/joint-source-deletion-restore-001.json`
- `C:/Users/WYZB/.codex/visualizations/2026/08/24/01a03416-0fad-7470-ae84-46d35dceb6aa/joint-restore-success-maximized.png`
- `C:/Users/WYZB/.codex/visualizations/2026/08/24/01a03416-0fad-7470-ae84-46d35dceb6aa/livecompanion-hsl-light-1280x800.png`
- `C:/Users/WYZB/.codex/visualizations/2026/08/24/01a03416-0fad-7470-ae84-46d35dceb6aa/snapshot-delete-success-light-1280x800.png`

界面又在最大化和 1280×800 下复验浅色、深色与跟随系统主题。直播伴侣左栏只显示当前有内容的基础设置、美颜设置和滤镜设置；HSL、曲线及 LUT 均可直接查看，无水平遮挡。通过产品删除确认窗清理了三份旧/无效存档，确认窗默认聚焦取消，删除后自动选择相邻项，文件和索引同步清理；最终只保留上述 02:01 正式存档。

最新质量门槛：Release 还原和构建成功，0 错误、0 警告；LiveStudio.Core.Tests 146 项、LiveStudio.Agent.Tests 4 项，共 150 项全部通过；全部直接和传递 NuGet 包均无已知漏洞；`git diff --check` 通过。下一步仍需在第二台物理采集卡电脑上执行相同破坏恢复，并累计规定的 20 次循环与阶段故障注入，达到门槛后才能把当前组合从 `Mapped` 提升为 `Verified`。

## 17. 2026-08-26 本机 20 次联合删除来源恢复循环

本节覆盖第 16 节之后的最终恢复加固与连续循环。机器、应用版本、正式存档和签名适配器均与第 16 节相同。本机仍只有 OBS Virtual Camera，没有物理采集卡，因此即使完成 20 次循环，证据状态仍为 `Mapped=1028`、`Verified=0`。

第 1 次为第 16 节记录的产品级联合破坏恢复；第 2–20 次使用同一产品 LocalAgent 恢复入口自动重复以下步骤：

1. 通过已认证 obs-websocket 删除 OBS 场景“场景”中的“显示器采集”，并确认“显示器采集 2”仍存在。
2. 启动直播伴侣，通过原生来源菜单删除唯一摄像头并正常关闭；独立解析 `sourceStore.json` 确认摄像头数为 0。
3. 在两款应用目标来源均缺失、直播伴侣关闭的状态下恢复正式存档。
4. 独立回读 OBS 来源、5 个滤镜顺序及锐化 0.07；独立回读直播伴侣唯一摄像头、1920×1080@30、格式 6、色彩空间 2、色彩范围 2、8 个滤镜顺序及 HSL 红色点 0.04/0.03/0。
5. 确认直播伴侣恢复后仍为关闭状态，事务目录为空。

20 次有效循环全部通过。第 2–20 次脚本化循环合计 2010.537 秒，平均 105.818 秒，最短 87.48 秒，最长 107.959 秒。完整摘要位于：

- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/restore-cycles-01-20-summary.json`
- 各批次原始结果为同目录下 `restore-cycles-*.json`；第 18 次失败后从相同前置状态重跑的独立证据为 `restore-cycle-18-retry.json`。

循环过程中发现并修复了四个不能用单元测试替代的 Windows 真机问题：

- 直播伴侣同一进程存在真实顶层窗和同尺寸黑色占位窗。来源菜单点击现优先选择包含原生 `View_` 预览子窗的真实窗口。
- Electron 弹出菜单位于 `Chrome_RenderWidgetHostHWND` 子窗。所有原生点击改为目标窗口客户区坐标转换后定向发送，不再移动系统鼠标，也不受其他置顶窗口遮挡。
- 启动器可能替换主进程 PID。所有窗口等待会重新解析当前主进程，并枚举同一直播伴侣进程组，不再永久绑定启动瞬间 PID。
- 原生导入页偶尔在窗口句柄出现后继续重建。导入入口现在以文件窗口实际出现为唯一成功条件执行最多三次有界重试；文件选择使用 UI Automation 按完整目标文件名调用列表项，不依赖排序和屏幕坐标。

第 8 次首次尝试在破坏前置条件阶段遇到 PID 替换，写入事务尚未开始；加固后重跑通过。第 18 次首次尝试未出现文件窗口，恢复事务正确失败并回滚，独立确认双方仍为失败前置状态、直播伴侣关闭、事务残留 0；加入有界重试后从同一状态重跑通过。这些失败记录保留在证据中，没有从成功率统计中隐藏。

UI Automation 反射桥接同时修复 .NET 10 将 `AutomationProperty` 放在 `UIAutomationTypes.dll`、静态标识公开为字段而非属性的运行时差异，并增加 Windows 实际装载回归测试。当前实现不需要关闭或移动用户的其他置顶窗口。

本机 20 次结果满足当前机器的连续循环要求，但仍不满足跨机器 `Verified` 红线。下一步必须在至少第二台具有不同物理采集卡的 Windows 电脑上，使用相同精确版本和签名定义执行 20 次保存、修改、恢复、逐字段回读与阶段故障回滚；在该证据完成前，UI 和文档不得声称“已全面验证”。

## 18. 2026-08-26 本机逐阶段故障注入与提交后回滚

完成第 17 节的成功路径循环后，继续审计恢复协调器发现两个此前测试未覆盖的安全问题：

- OBS 与直播伴侣会话曾在 `CommitAsync` 内过早拒绝回滚；如果第一款应用已经提交、第二款应用提交失败，协调器可能无法把第一款应用恢复到原状态。
- 事务开始后的 `OperationCanceledException` 曾绕过回滚；桌面端关闭、管道取消或 Agent 停机信号发生在停止、写入、启动或回读中途时，可能留下半恢复状态。

当前实现已经调整为：单应用提交后的回滚数据保留到会话最终释放；后续应用提交或最终状态持久化失败时，已提交会话仍执行回滚。事务会话创建后的所有异常（包括取消）统一使用不可取消令牌按逆序回滚。写入前的 Preflight 失败仍不会创建事务。

为避免只用假会话证明恢复安全，Core 增加 7 个明确的事务验证边界；Agent 默认使用空注入器，只有同时设置破坏性验证确认值和一个合法故障点时才启用，缺少确认时 Agent 拒绝启动：

1. `SessionsCreated`
2. `ApplicationsStopped`
3. `AssetsPrepared`
4. `SettingsApplied`
5. `ApplicationsStarted`
6. `VerificationPassed`
7. `ApplicationsCommitted`

本机从 OBS 目标来源和直播伴侣摄像头均被删除、直播伴侣关闭的同一前置状态逐阶段执行故障。每次都要求恢复调用返回失败，并独立核对：

- OBS 目标来源仍缺失，保留来源仍存在。
- 直播伴侣摄像头数仍为 0，进程数为 0。
- `effectConfigStore.json`、`effectStore.json`、`filterStore.json`、`sourceStore.json`、`camera-payloads.json` 五份真实事务文件的 SHA-256 与故障前完全一致。
- 本机事务目录为空。

七个边界全部通过。最晚的 `ApplicationsCommitted` 在两款应用均已执行提交之后注入故障，仍完成跨应用回滚，证明本节修复不是只覆盖提交前失败。矩阵完成后使用无故障 Agent 从相同双方来源缺失状态执行正常恢复，独立回读 OBS 锐化、直播伴侣设备/视频模式、8 个滤镜与 HSL 均一致，直播伴侣最终保持关闭，事务目录为空。

真机证据：

- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/restore-stage-fault-matrix-001.json`
- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/restore-stage-fault-settings-applied-001.json`

单元测试同步增加：Preflight 写入前阻断、Begin/Stop/Apply/Start/Verify/Commit 异常、事务中途取消、最终状态持久化失败、7 个显式边界以及环境变量双重确认。上述结果仍只来自当前一台无物理采集卡机器，证据状态继续保持 `Mapped=1028`、`Verified=0`。

本节完成后的最终质量门槛：Release 还原与构建成功，0 错误、0 警告；LiveStudio.Core.Tests 163 项、LiveStudio.Agent.Tests 7 项，共 170 项全部通过；全部直接和传递 NuGet 包无已知漏洞；`git diff --check` 通过。故障矩阵后的正常恢复已把电脑恢复到正式存档状态：OBS 两个来源与 5 个滤镜存在，直播伴侣一个摄像头与 8 个滤镜存在，直播伴侣保持关闭，事务目录为空。

## 19. 2026-08-26 Agent 崩溃后的 OBS/直播伴侣联合事务恢复

继续审计第 18 节后发现：直播伴侣已有磁盘事务备份，但 OBS 回滚快照只存在 Agent 内存；如果 Agent 在双方写入中途被强制终止或电脑断电，重启时可能只回滚直播伴侣，OBS 保留目标状态，形成跨应用半恢复。第 19 节实现与证据覆盖该进程外故障边界；与第 18 节提交清理顺序冲突时以本节为准。

当前联合恢复使用三层持久日志：

- `Transactions/Obs/<jobId>` 保存 OBS 原始完整快照、原始滤镜素材、原始运行状态，以及写入前预登记的本次可能新建来源。快照、素材均校验 SHA-256；即使 `CreateInput` 已成功但响应丢失，也会删除预登记的新来源。
- `Transactions/LiveCompanion/<jobId>` 保存五份真实原生文件的原始字节、长度和 SHA-256，并使用 `WriteThrough + Flush(true)` 强制落盘；应用与回滚的原子临时文件也执行强制落盘。
- `Transactions/Restore/<jobId>` 保存双方共用的 `Prepared/Committed` 决策。两款应用都逐字段验证并完成运行状态提交前保持 `Prepared`；此时任何重启都回滚双方。只有全局 `Committed` 已原子落盘后，重启才保留目标状态并清理应用日志。

所有恢复入口现在由同一个 `RestoreCoordinator` 串行化；本机控制与云端任务不能并发写两款应用。Agent 在启动本机管道、托盘、局域网或云端 Worker 之前，先完成 OBS 和直播伴侣未决日志处理；任一应用恢复失败时 Agent 不接受新请求。损坏或无法证明有效的全局提交清单一律按“未提交”处理，不会因为模糊状态保留半恢复结果。

故障注入新增 `DurableCommitRecorded` 边界，并为真实进程终止增加独立的第二确认值。生产默认仍使用空注入器；没有破坏性确认、合法故障点、`Crash` 行为和立即终止确认四项时，不会执行强制终止。

最终 Release 在当前真机执行了两类完整破坏性进程崩溃：

1. `SettingsApplied`：OBS 和直播伴侣均已写入、全局仍为 `Prepared` 时用 `Environment.FailFast` 立即终止 Agent。终止后独立观察到 OBS、直播伴侣和全局三个日志各 1 份。正常 Agent 重启后，OBS 回到“显示器采集”已删除且“显示器采集 2”保留的原状态；直播伴侣回到摄像头数 0、进程数 0；五份原生文件 SHA-256 与故障前逐字节一致；三个事务根目录均为 0。随后从同一缺失来源状态正常恢复正式存档，双方独立回读全部匹配。
2. `DurableCommitRecorded`：双方已经完成逐字段验证、全局 `Committed` 已落盘、应用日志尚未清理时立即终止 Agent。终止后同样观察到三个日志各 1 份，并确认全局 phase 为 `Committed`。正常 Agent 重启后保留已验证的 OBS 两个来源、5 个滤镜与锐化 0.07，以及直播伴侣唯一摄像头、1920×1080@30、8 个滤镜和 HSL 0.04/0.03/0；只清理日志，不误回滚；直播伴侣最终保持关闭。

当前最终二进制的证据为：

- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/restore-process-crash-settings-applied-002.json`
- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/restore-process-crash-durable-commit-002.json`
- 可复跑脚本为同目录 `Run-CrashRecoveryValidation.ps1` 与 `Run-CommittedCrashRecoveryValidation.ps1`。

最终真机状态为：OBS PID 30032 正常运行，正式存档中的两个来源与 5 个滤镜存在；直播伴侣目标摄像头和 8 个滤镜已恢复、应用保持关闭；OBS、直播伴侣和全局事务目录均为空；LiveStudio Agent 与 Desktop 均运行。Release 构建为 0 错误、0 警告；LiveStudio.Core.Tests 167 项、LiveStudio.Agent.Tests 9 项，共 176 项全部通过。

本节仍只能授予 `Mapped=1028`、`Verified=0`。当前机器只有 OBS Virtual Camera，没有物理采集卡；尚未在第二台不同物理采集卡电脑和另一个目标直播伴侣版本完成规定的 20 次保存、修改、恢复、逐字段回读、阶段异常与进程崩溃矩阵，因此不得把当前结果描述为跨硬件“全面验证”。

## 20. 2026-08-26 产品恢复进度与跨入口操作串行化

第 19 节证明执行端能够恢复，但桌面端原先只在点击瞬间显示一条通用“正在恢复”文字；直播伴侣原生重建与逐字段回读约需 90–110 秒，普通用户无法判断按钮是否生效、当前处于哪个阶段。当前实现新增只读 `GetOperationProgress` 本机协议：主命名管道只执行一次恢复，桌面端使用独立轻量管道每 500 ms 读取 Agent 已有的阶段消息，不调用应用适配器、不重复触发恢复，也不干扰事务。

画面存档页保持高密度布局，仅增加以下紧凑反馈：

- 点击后主按钮立即变为“正在恢复…”并禁用重复操作。
- 原状态条左侧显示 64×3 px 不定进度条，不增加新卡片或页面滚动。
- 同一行显示 Agent 当前中文阶段，例如“正在创建目标电脑事务快照”“正在应用设备、画面格式和视频滤镜”“正在逐项回读恢复结果”。
- 成功后明确显示存档名称和“已应用并通过逐字段回读”；失败时继续显示执行端原始失败原因或已回滚结果。

同时增加进程内共享 `ApplicationOperationGate`。本机保存、云端保存、恢复、心跳参数读取、手动当前状态发布、设备映射目标捕获和 OBS 自动连接共用同一异步操作门，避免本机 UI 与云端 Worker 同时读取/写入 OBS 和直播伴侣。等待操作支持取消，恢复事务自身仍由联合持久日志处理崩溃。

最终 Release 从正式存档状态通过原生界面删除直播伴侣唯一摄像头、通过 obs-websocket 删除 OBS 来源“显示器采集”，然后只调用桌面端“恢复所选存档”按钮。UI Automation 验收结果：

- 点击后 2 秒内观察到“正在恢复…”即时反馈。
- 逐字段回读阶段页面持续显示进度条和“恢复进度：正在逐项回读恢复结果”，字段区域无遮挡。
- 页面显示完成后独立回读 OBS 两个来源、5 个滤镜和锐化 0.07；直播伴侣唯一摄像头、1920×1080@30、8 个滤镜和 HSL 0.04/0.03/0 均匹配；直播伴侣保持关闭，事务目录为空。

最终有效证据：

- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/product-ui-restore-progress-004.json`
- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/product-ui-restore-progress-004-verifying.png`
- 可复跑脚本为同目录 `Run-ProductUiRestoreProgressValidation.ps1`。

Release 构建为 0 错误、0 警告；LiveStudio.Core.Tests 170 项、LiveStudio.Agent.Tests 9 项，共 179 项全部通过。当前机器结果继续为 `Mapped=1028`、`Verified=0`；跨硬件完成门槛与第 19 节相同。

## 21. 2026-08-26 最终完成度审计与外部验收阻塞

本节以当前工作树、运行中应用、正式存档、签名适配器和真机证据重新核对整个目标，不把“当前机器可以恢复”替代为“跨硬件已经全面验证”。后续用户指令已经明确取消开播、推流和录制状态的恢复阻断，并要求直播伴侣空菜单不显示；当前审计以这些较新的产品要求为准。

当前本机环境为 `DESKTOP-2C8JIC2`、Windows 11 专业版 build 26200 64 位、OBS 32.2.2、直播伴侣 12.8.1.454484231。本机 PnP 枚举没有物理采集卡或物理摄像头；20 次循环使用的是软件 `OBS Virtual Camera`。正式存档仍为 `6cfed811-cf5c-4188-be26-9191b1343a40`，文件大小 238775 字节，SHA-256 为 `9B18074797614D66D599CAAD5CA0EF773AB9775C7AAB6017B1B29B33064A6929`。

本机已经完成并有可复核证据的范围：

- OBS 来源删除后重建、来源设置、5 个滤镜顺序与锐化 0.07 的恢复和独立回读。
- 直播伴侣 12.8.1.454484231 的 4 个真实原生文档、1028 个目标字段映射，其中 1018 个字段由事务执行端写入，10 个应用管理字段由原生应用重建并回读验证；当前签名定义中 `Unknown=0`、`EvidenceOnly=0`、`Mapped=1028`、`Verified=0`。
- OBS 与直播伴侣来源同时删除后的联合恢复；设备、1920×1080@30、格式、色彩空间、色彩范围、8 个滤镜、HSL 和素材引用均在当前存档范围内逐项回读。
- 同一机器 20 次完整删除、恢复和独立回读；7 个事务阶段故障回滚；`SettingsApplied` 进程崩溃后的启动回滚；全局持久提交后的崩溃保留和日志清理。
- 桌面端真实“恢复所选存档”按钮触发、2 秒内反馈、恢复阶段进度、最终独立回读，以及保存、删除、导入、导出、主题和高密度检查界面的自动化与人工截图验收。
- 本机、云端、心跳、映射目标捕获和自动连接入口共用操作门；恢复写入使用跨应用持久事务，失败或取消按逆序回滚。

仍不能在当前机器完成、也不能以代码或单元测试替代的唯一验收边界：

- 至少第二台 Windows 真机和两种不同的物理采集卡；当前机器没有任何物理采集卡。
- 在第二台不同物理采集卡机器上，对相同精确 OBS/直播伴侣版本和签名定义再执行至少 20 次保存、修改、恢复、逐字段回读、阶段故障与进程崩溃矩阵。
- 为另一个目标直播伴侣版本建立真实单变量差异证据、独立结构指纹和签名定义，并重复相同验收，不能把 12.8.1 的定义泛化到其他版本。

因此当前产品对本机精确版本组合的证据等级保持 `Mapped`，不能升级为 `Verified`，也不能在 UI、发布说明或交付文案中声称“跨硬件 100% 全面验证”。接手者获得第二台物理采集卡电脑后，应先按本文第 2、4、5、6、7 节采集基线，再复用第 17–20 节目录中的循环、故障、崩溃和产品 UI 脚本；所有新证据必须使用新机器别名和真实设备硬件 ID，不能覆盖本机证据。

## 22. 2026-08-26 直播伴侣摄像头窗口重试修复

使用者在 13:39 从桌面端再次恢复正式存档时，真实本机操作记录为 `FailedRolledBack`，错误为“未出现预期的添加摄像头或摄像头设置窗口，恢复已取消”。事务已完整回滚，三个事务目录为空；该失败证明第 20 节的成功证据没有覆盖所有 Electron 首次渲染时序，不能用旧成功记录否认当前产品失败。

现场检查确认直播伴侣已经进入正确的空场景编辑页，真实主窗口同时存在同尺寸黑色占位窗；摄像头入口通过 `Chrome_RenderWidgetHostHWND` 接收消息。原实现只发送一次点击，并且摄像头弹窗只按尺寸识别。当前 12.8.1 真机弹出的原生窗口标题为“摄像头设置”，大小为 640×660；相同入口在重新发送一次原生点击后立即创建 `OBS Virtual Camera`，说明失败来自 Chromium 首次点击丢失或弹窗识别过窄，不是存档缺少设备配置。

当前修复：

- 打开摄像头入口改为三次有界尝试；每次重新解析当前真实主进程和主窗口，只在没有出现预期摄像头窗口时继续。
- 同时覆盖 1280×800 与本机 1080×720 紧凑竖屏布局中的摄像头卡片位置，不进行无限点击。
- 摄像头设置和添加摄像头窗口改为原生中文标题优先识别；只有窗口无标题时才使用已知尺寸兼容规则，带有“直播伴侣”等其他标题的同尺寸窗口不会被误认。
- 每次重试仍以预期摄像头窗口为保护条件；没有出现目标窗口时整次恢复失败并回滚，不会继续点击确认或写入其他来源类型。

修复后的 Release 替换后台 Agent 后，从直播伴侣关闭、摄像头存在的正式状态执行完整产品破坏恢复：脚本先删除 OBS 来源“显示器采集”和直播伴侣唯一摄像头，再只通过桌面端“恢复所选存档”按钮触发。结果在 2 秒内显示反馈，越过原失败阶段，先显示“正在恢复应用运行状态”，再显示“正在逐项回读恢复结果”，最终独立回读匹配并提交。

独立原生文件核对结果：直播伴侣唯一摄像头为 `OBS Virtual Camera`，1920×1080、30 FPS、格式 6、色彩空间 2、色彩范围 2；8 个滤镜顺序为 HSL、白平衡、高斯模糊、镜头虚化、色彩增强、曲线、LUT、蒙版；HSL 红色点为色相 0.04、饱和度 0.03、明度 0。恢复后直播伴侣进程数为 0，OBS、直播伴侣和联合恢复事务目录均为 0。

本轮有效证据：

- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/product-ui-restore-camera-retry-006.json`
- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/product-ui-restore-camera-retry-006-verifying.png`

`product-ui-restore-camera-retry-005.json` 在破坏前置检查时因直播伴侣仍在运行而停止，没有删除来源、没有触发恢复，结果为 `Invalid`，不得计入恢复成功或失败次数。新增窗口标题与误识别回归后，LiveStudio.Core.Tests 为 176 项、LiveStudio.Agent.Tests 为 9 项，共 185 项全部通过。该结果仍只证明当前机器、当前精确版本和软件虚拟摄像头组合，证据状态保持 `Mapped=1028`、`Verified=0`。

## 23. 2026-08-26 OBS 内置色卡与最终 5 次恢复

使用者提供的 `色卡..rar` 是 OBS“应用 LUT”滤镜素材，不属于直播伴侣。归档 SHA-256 为 `2F06CD00EE745207CA5B352BF4EDBE3F8C738D2D11E04A3F46A730EE1BD3CF33`。解包时执行了路径穿越、符号链接、空文件、PNG 结构和 CUBE 数据行校验；OBS 备份 JSON 因包含场景、音频、设备标识和本机路径而排除，三个零字节 PNG 也未打包。最终内置 17 个有效 CUBE 和 37 个有效 PNG，共 54 项、46688992 字节。

最终程序在启动时验证清单自身和全部文件的长度/SHA-256，然后将完整素材库原子部署到 `%LOCALAPPDATA%/LiveStudio/OBS素材/色卡/v1`。OBS 适配器只使用该解析器：现有原路径可读时保留真实文件；原路径丢失且文件名匹配内置素材时，重写为稳定部署路径；旧存档没有素材绑定时也可使用相同回退；不能解析的已知素材扩展名在 Preflight 以 `MissingAsset` 终止，写入前不创建事务。部署文件被删除或改变时，解析器从已验证的内置副本自愈并再次校验哈希。

内置清单为 `src/LiveStudio.Agent/BuiltInAssets/ColorCards/v1/manifest.json`，其 SHA-256 为 `59B8FFD612A44E5080BBD38476FC8C1F8E1636BA9F7EDA8A8FF95F218C56F63A`。完整审计证据为：

- `artifacts/windows-validation/2026-08-26/DESKTOP-2C8JIC2/livecompanion-12.8.1/built-in-color-card-bundle-011.json`，SHA-256 `0D4CC22FEA5387272A3C5C2789B8D29DC56DEB9C81C0D6E46A714446581F01A9`。
- `restore-built-in-color-card-cycles-012-01-20.json` 中第 1–4 次有效破坏恢复全部通过；第 5 次在破坏前置检查因直播伴侣摄像头仍存在而标记 `Invalid`，没有进入恢复事务，不计入成功或失败。
- 修复前置状态后，`restore-built-in-color-card-valid-cycle-013-05-05.json` 完成第 5 次有效破坏恢复并通过，SHA-256 `B56612B96D4B8045081A5E0002EC36270CC34DD1F289344B70A132224F28C638`。

因此该最终色卡版本按使用者要求完成 5 次有效联合删除来源、恢复和独立回读，另保留一次未开始事务的无效前置尝试。OBS 色卡恢复的自动化测试覆盖缺失原路径、新旧存档、无法解析时阻断、清单篡改、缺文件和部署自愈。仍不得把本机软件虚拟摄像头结果泛化为跨物理采集卡 `Verified`。

## 24. 2026-08-26 多直播间批量备份与统一管理

云端新增真实批量保存协议 `POST /api/v1/organizations/{organizationId}/capture-jobs/batch`。一次接受 1–200 个不重复直播间，并在同一组织、Operator 权限下逐房间判断：直播间不存在、未绑定 Windows 执行端、执行端离线和已有保存任务分别返回独立结果；合格房间各自创建不可混用的 Capture 任务、初始事件和审计事件。离线或错误房间不会阻止其他在线房间，也不会创建离线排队任务。

桌面端控制室改为高密度统一管理界面：左侧常驻组织与直播间列表，在线且已绑定的房间默认勾选，离线项可见但不可误选；支持全选在线、清除、批量保存和只重试未成功项。服务端结果按直播间显示真实中文原因；刷新组织数据保留用户选择，成功项清除，失败项保留用于重试。右侧使用自适应三行网格，显示批量结果、当前房间操作、OBS/直播伴侣预览和最近任务，不再在全屏时留下无意义大块空白。Windows 电脑即使同时运行本机 Agent，也可通过工具栏“多直播间 / 返回本机”直接切换，不会再因本机控制面板优先级而隐藏云端管理入口。网页控制台提供相同的批量选择和下发入口；组织存档库增加直播间与名称筛选，主表移除 SHA 技术列，详情默认只显示启用滤镜和用户中文名称。

桌面端在 1240×780 浅色主题下完成真实渲染检查，12 个在线房间默认选中、离线房间禁用、两路预览与最近任务无遮挡：`artifacts/desktop-batch-management-final.png`。服务端 PostgreSQL 集成用例已经编译，覆盖在线接受、离线、未绑定、不存在和已有任务五类结果；当前机器未配置 `LIVESTUDIO_INTEGRATION_CONNECTION`，所以该用例没有运行，不能记录为通过。

本节最终质量门：Release 整仓构建 0 错误、0 警告；LiveStudio.Core.Tests 180 项、LiveStudio.Agent.Tests 13 项，共 193 项通过；所有直接与传递 NuGet 包均无已知漏洞；`git diff --check` 无空白错误。多直播间批量管理是云端控制功能，不改变本机 OBS/直播伴侣存档格式、事务恢复链或第 21 节的跨硬件验收边界。

## 25. 2026-08-26 恢复内嵌设备对应与当前画面重设计

原“设备映射”顶级页面虽然已经连接真实本机与云端保存接口，但缺少恢复上下文，普通使用者无法判断何时需要它、要选择哪份存档或哪台目标电脑。本轮从左侧导航移除该入口，映射协议和存储没有删除，而是内嵌到“恢复所选存档”的恢复准备阶段：

- 每次恢复先读取当前存档和目标电脑来源；同应用下来源名称唯一一致时自动对应，否则只在设备名称唯一一致时自动对应。
- 只有换电脑、换采集卡或出现名称歧义时才打开右侧“恢复准备”面板；面板逐个显示存档来源、要求的视频模式和当前电脑候选来源，全部确认后才能继续。
- 无法读取 Agent 时显示单列错误、重新检测和“没有写入 OBS 或直播伴侣”说明，不再渲染两列空映射界面。
- 映射成功仍不绕过正式 Preflight；版本、视频模式、插件、素材、权限和逐字段回读继续由执行端再次校验。

真实产品冒烟首次发现，旧映射上下文把所有 OBS 来源都加入列表，但目标列表只包含带 `InterfaceHint` 的硬件来源，导致“显示器采集”等非设备来源显示“未识别设备、未映射”，且没有任何可选目标。本轮把本机与云端映射资格统一收紧为“存档来源必须具有非空硬件接口标识”；显示器采集、窗口采集等非设备来源不再阻塞恢复，真正的采集卡和摄像头仍保持故障关闭的显式选择。新增 3 项 Agent 回归测试分别覆盖无设备来源、缺少稳定接口标识的描述符和真实硬件接口标识。

产品信息架构同时调整：左侧“控制室”改名为“当前画面”，保留保存、检查、选择存档恢复的日常三步提示，并加入最近一次保存、事务恢复说明和最近操作；多直播间统一管理通过工具栏切换。当前画面内容在超宽窗口使用 1480 px 阅读宽度并居中，避免全屏时卡片被拉成无意义长条。存档页继续使用常驻时间线、OBS 来源栏和直播伴侣实际有内容的中文菜单；本机真实 1028 字段存档在 1936×1048 浅色窗口中，美颜有效参数单屏显示，滤镜页可直接看到 HSL、白平衡、模糊、色彩、曲线与 LUT 内容。主题在设置页提供“跟随系统 / 浅色 / 深色”，本轮分别以真实 Avalonia 窗口验收，结束时恢复为“跟随系统”。

本轮 UI 证据：

- `artifacts/current-screen-redesign-light-final.png`
- `artifacts/current-screen-light-maximized.png`
- `artifacts/settings-dark-final.png`
- `artifacts/snapshots-light-maximized-prep.png`
- `artifacts/snapshots-livecompanion-light-maximized.png`
- `artifacts/snapshots-livecompanion-filters-light.png`
- `artifacts/restore-preparation-agent-offline-system.png`
- `artifacts/restore-smoke-success-dark.png`

最终 Release 通过真实桌面按钮恢复正式存档 `6cfed811-cf5c-4188-be26-9191b1343a40`。修正前界面明确复现“2 个非设备来源需要映射且无候选”；修正后无需映射，按钮进入“正在逐项回读恢复结果”，最终显示“已应用并通过逐字段回读”。OBS PID 30032 保持响应，直播伴侣按存档前关闭状态退出；色卡部署仍为 54 项、46688992 字节、无 `.partial/.tmp`；三个事务分类目录存在但内部均为空。该冒烟是在已恢复状态上再次应用存档，不替代第 19–23 节的删除来源、故障回滚和 5 次破坏恢复证据。

最终质量门：Release 整仓构建 0 错误、0 警告；LiveStudio.Core.Tests 183 项、LiveStudio.Agent.Tests 16 项，共 199 项全部通过；所有直接与传递 NuGet 包均无已知漏洞；`git diff --check` 无空白错误。当前机器仍只有软件虚拟摄像头，证据状态保持 `Mapped=1028`、`Verified=0`，不得声称已经完成第二台物理采集卡电脑的跨硬件验证。

## 26. 2026-08-26 云存档管理与相机档案入口

桌面端左侧导航新增常驻“直播间管理”和“相机参数”，不再要求使用者先进入当前画面再寻找隐藏入口。直播间管理在未注册执行端时显示独立首次连接页，可填写服务地址、通过浏览器授权并查看隐私边界；注册后继续使用组织、直播间、多机批量保存和任务管理。画面存档时间线区分“仅本机、待同步、已同步”，已注册执行端可点击“立即同步”，该操作会触发一次受串行锁保护的真实上传循环并返回成功数与剩余数，不再只是刷新文字。

云端存档管理补齐重命名、导出和删除：重命名只更新服务端元数据，不改变已经签名的存档包；导出使用临时文件下载并校验服务端长度与 SHA-256 后原子落盘；删除按组织权限执行并重新加载列表。本机“清空”只统计和删除本机存档，不会把云端条目混入批量删除。当前机器未配置可访问的云服务地址、组织账号和 PostgreSQL 集成连接，因此本轮只完成客户端、服务端和协议单元测试以及未注册状态的真实桌面渲染；云端端到端上传、跨电脑下载和远程恢复不能记录为真机通过。

相机参数页提供两个明确层级：手动档案已可保存、修改和删除光圈、快门、ISO、创意外观，数据使用原子本机 JSON 存储；USB 自动区提供 Sony 设备检测、设备选择和官方 SDK 入口。固定四列输入改为可换行的紧凑布局，窄窗口与全屏均不会挤压字段。Windows 检测只枚举 `USB\VID_054C` Sony 设备，并设置 8 秒硬超时与子进程树清理；真实桌面点击在 679 ms 返回“未检测到 FX3、α7S III 或 FX30”，窗口持续响应且没有残留 PowerShell 进程。

截至本节，Sony 官方 Camera Remote SDK 当前版本为 2.02.00，官方兼容表覆盖 FX3、FX30 和 α7S III，且支持 Windows USB 远程设置；但当前机器没有连接相机，也没有安装由使用者接受许可协议后下载的 SDK 包。因此 USB 区只能诚实显示检测状态，不能声称已经读取或一键恢复光圈、快门、ISO、创意外观。接手者获得官方 Windows SDK 包和真实相机后，应实现原生桥接，并对每款机型逐字段验证读取、写入、回读、断线与回滚，不能用 HDMI 采集卡代替 USB 控制链路。

本节质量门：Release 还原与构建成功，0 错误、0 警告；LiveStudio.Core.Tests 195 项、LiveStudio.Agent.Tests 17 项，共 212 项全部通过；全部直接和传递 NuGet 包无已知漏洞；`git diff --check` 通过。OBS/直播伴侣的跨物理采集卡证据边界仍按第 21 节保持 `Mapped=1028`、`Verified=0`。

## 27. 2026-08-26 Sony SDK 下载与三机位手动参数表

使用者明确接受 Sony Camera Remote SDK 协议后，从 Sony 官方 Windows 端点下载 `CrSDK_v2.02.00_20260610a_Win64.zip` 到 `C:\Users\WYZB\Downloads`。最终文件长度为官网标注的 213268163 字节，SHA-256 为 `4D4CC0BEE7FDD60E1E947BFA7A524CBFB5550465CECCC8647EB7B7CDC383EF77`；逐项读取 11 个外层 ZIP 条目成功，未发现绝对路径或 `..` 路径。SDK 已解压到同名目录，`SimpleCli` 和 API Reference 也已展开；真实文件包含 `Cr_Core.dll`、`Cr_PTP_USB.dll`、`Cr_Core.lib`、全部 C++ 头文件和属性参考。

SDK 2.02 头文件明确提供 `CrDeviceProperty_Iris`、`CrDeviceProperty_ShutterSpeed`、`CrDeviceProperty_IsoSensitivity`、`CrDeviceProperty_CreativeLook`，以及创意外观的 Contrast、Highlights、Shadows、Fade、Saturation、Sharpness、SharpnessRange 和 Clarity 八个独立属性。这与 Sony 三款目标机型帮助指南一致。当前电脑没有 Visual Studio C++ Build Tools、CMake、clang-cl 或真实 Sony USB 相机，因此本轮不能编译 C++ ABI 桥接器，也不能执行真实相机写入和回读；下载成功不得描述成“一键恢复已经完成”。

按照使用者最新界面要求，手动相机页取消左侧档案列表和数字步进器，改为一个页面固定三张高密度机位卡，默认名称分别为“主机、游机、侧机”。每张卡默认 `F4 / 1/125 / ISO 640 / ST`，ST 八项默认值为 `0 / 0 / 0 / 0 / 0 / 4 / 3 / 1`，所有值均使用普通文本框直接填写。三卡分别保存；名称字段直接修改后保存即完成重命名；“删除”移除该机位持久档案并恢复该槽位默认值，未保存卡片显示“清空”。旧版无槽位档案按时间顺序迁移到空闲机位，不删除多余原始数据；新存档写入稳定的 0–2 机位槽位。

真实 Avalonia Release 窗口在 1240×780 下显示 3 个机位标题、3 个保存按钮和 36 个直接输入框，所有内容同屏、无遮挡：`artifacts/camera-three-stations-final.png`。Release 构建为 0 错误、0 警告；LiveStudio.Core.Tests 204 项、LiveStudio.Agent.Tests 17 项，共 221 项全部通过。

## 28. 2026-08-26 三机位参数内嵌画面存档

本节按使用者最新要求取代第 26、27 节中“相机参数作为独立页面和独立 JSON 档案”的产品形态。左侧独立“相机参数”入口已移除；画面存档页继续使用原有直播间筛选和存档时间线，并在 OBS、直播伴侣旁增加紧凑的“相机参数”分段。每份存档显示主机、游机、侧机三张自适应高密度编辑卡，一次“保存三个机位”完成整份绑定，不再逐卡保存或删除。宽窗口固定三列，窄窗口由 `AdaptiveUniformPanel` 在不产生水平遮挡的前提下自动减少列数。

三机位数据现在是 `.lscfg` 的正式签名内容，而不是旁路 sidecar：

- `CombinedSnapshot` 与 `SnapshotPackageManifest` 同时包含三个 `CameraStationSnapshot`；相机数据进入 `parameters.json`、文件哈希和 ECDSA 清单签名。
- “保存当前画面”一次读取 OBS、直播伴侣并携带当前三个机位；导出、导入、局域网发布、云上传和换机读取都使用同一存档包。
- 修改未上传的本机存档时由 Agent 在受管目录生成完整替换包、重新签名、原子替换文件并同步 SQLite 长度与 SHA-256；更新失败恢复原包。已经上传的云存档保持不可变，界面要求另存当前画面，避免同一个云端存档 ID 出现两份内容。
- 云端详情从已保存的签名清单读取相机参数；旧存档缺少该可选字段时继续通过签名验证，并在界面投影为三个默认机位。
- 相机参数已纳入相邻存档差异比较，光圈、快门、ISO、创意外观和八项细调发生变化时会计入“相机参数”变化数。

默认值为主机、游机、侧机，均使用 `F4 / 1/125 / ISO 640 / ST`。对比度、高光、阴影、褪色、饱和度、锐度、锐度范围和清晰度八项按最新要求全部为 `0`；锐度范围为兼容手动“未调整”状态允许 `0–5`。旧独立 `camera-profiles.json` 只作为一次性迁移输入保留，不再有可见独立入口，后续保存以画面存档为唯一持久化结果。

真实 Release 界面已在浅色、深色 1240×780 和深色 1800×1000 下检查：三个机位同屏、字段没有遮挡，时间线常驻，独立相机导航消失，宽窗口不会把输入区铺成单列长表。证据：

- `artifacts/camera-bound-snapshot-light-1240x780.png`
- `artifacts/camera-bound-snapshot-dark-1240x780.png`
- `artifacts/camera-bound-snapshot-dark-1800x1000.png`

本节相机数据仍是 HDMI 工作流下的手动机身参数。恢复所选存档会让使用者直接看到对应三机位值，但不会伪装成已经通过 HDMI 写回 Sony 机身；USB 自动写入仍需编译 SDK 原生桥接器并连接 FX3、α7S III、FX30 逐字段写入和回读。该边界不改变 OBS、直播伴侣当前 `Mapped=1028`、`Verified=0` 的跨物理采集卡验收状态。

最终质量门：Release 还原与整仓构建成功，0 错误、0 警告；LiveStudio.Core.Tests 207 项、LiveStudio.Agent.Tests 20 项，共 227 项全部通过；全部直接与传递 NuGet 包无已知漏洞；`git diff --check` 通过。

## 29. 2026-08-26 云端单空间部署与恢复覆盖口径校正

腾讯云轻量服务器 `111.229.162.72` 已部署独立的 LiveStudio Cloud、PostgreSQL 和对象存储，既有 `wuyoupaiban.cn` 的 80/443 站点保持不变。LiveStudio 使用独立 `8443` 端口和私有 CA；桌面端固定连接该地址，并在使用者点击明确的“安装证书并连接”按钮后才把内置根证书安装到当前 Windows 用户。服务端限制为一个直播管理空间、最多 15 个固定直播间和 15 台设备；首个账号自动成为管理员并创建“默认直播管理空间 / 直播间 1”，后续公开注册被拒绝，设备支持撤销。当前云容器与本机回环健康检查已通过，但腾讯云防火墙的公网 `8443` 规则尚未获得使用者操作时确认，因此外网连接、首账号、首设备绑定和真实云存档上传/下载仍不能记录为通过。

本轮同时核对了直播伴侣覆盖数字。签名定义仍声明 1028 个已映射叶字段，其中 1018 个字段在定义层标记为必需且原生可写，10 个为应用管理缓存。执行层的真实事务写入/回读投影是 966 项：另有 52 项位于 `effectStore/carnivalInfo/sourceLink/<摄像头>/<索引>`，实机值为活动系统为当前摄像头生成的 52 个数值能力 ID 列表；它不表示用户启用了任何特效，真正的画面状态由同级 `isOn` 和 `using` 控制。跨摄像头重放该列表会写入陈旧的应用能力缓存，因此这 52 项只保留在存档和技术信息中，不参与恢复；`isOn`、`using` 以及其余 964 项画面字段继续进入事务写入和逐字段回读。当前存档显示 `Mapped=1028`、`Writable/Required=966` 是这一安全边界的准确表达，不是漏捕获。旧章节中“执行端写入 1018 项”的表述由本节校正。

该校正没有提升证据等级。当前机器仍没有物理采集卡；跨机器与不同实体采集卡的恢复状态继续保持 `Mapped=1028`、`Verified=0`。

部署后的匿名浏览器冒烟发现根页和客户端管理路由返回带 `Location` 的 404：默认授权策略同时挑战 Identity Cookie 与 Desktop Bearer，Cookie 已生成登录跳转后又被 Desktop 方案的默认 401 覆盖，随后状态码页改写成 404。`DesktopAuthenticationHandler` 现在只在没有既有 3xx/Location 时写入 401；API 仍保持无重定向的 401。生产镜像已更新为 `sha256:fed545f99a856ab00f81d9e760d7c241cb36cd8fc51525ceb76a9466c3864386`，回滚镜像标记为 `pre-auth-challenge-20260827`，发布包 SHA-256 为 `B17BEB2225E21CDD0715CCA86605067E0FA921F782D50D9BC7FBB8066D9ABC5B`。经服务器本机 TLS/Host 解析验证，`/`、`/rooms`、`/snapshots` 均为 302 并保留准确 ReturnUrl，`/health/ready` 与 `/Account/Register` 为 200；既有 `wuyoupaiban.cn` 为 200。生产库仍为 0 账号、0 空间、0 设备、0 存档。新增双向挑战回归后，Release 构建 0 警告、0 错误，Agent 20 项与 Core 219 项共 239 项全部通过，`git diff --check` 通过。

## 30. 2026-08-27 云端真实上传下载、本机自检与存档页面修复

使用者已在腾讯云轻量服务器防火墙放行公网 `8443`，并完成首个管理员账户、默认直播管理空间、直播间 1 和当前 Windows 执行端注册。生产库保持单管理员注册关闭策略，服务限制继续为最多 15 个直播间和 15 台固定电脑；直播间工作人员不需要管理员账号，设备由管理员在桌面授权后使用设备凭据运行。

第一次真实云保存暴露了对象存储地址边界错误：服务端把 Docker 内部地址 `minio:9000` 写进预签名上传 URL，Windows Agent 无法解析该主机。对象存储现区分服务端内部地址与客户端公网地址；Caddy 和生产 Nginx 都把 `/livestudio/` 转发到 MinIO，API、健康检查和管理界面继续走 Cloud。生产镜像已更新并保留上一镜像回滚标签。修复后同一失败任务自动重试成功，生产库出现 1 份完成存档和 3 项素材；存档包长度为 2983993 字节，SHA-256 为 `40F6BF750874485D7BC897BC473BDE711AB95AEA5B762A396F9A827825C26358`，服务器对象长度和哈希一致，3 项素材逐项哈希均与内容寻址键一致。

桌面端新增“本机检查与修复”，在直播间电脑当前 Windows 用户会话内依次执行：启动 Agent、启用登录后自启动、检测并修复 OBS/直播伴侣连接、同步等待上传的本机存档，并从云端下载最新一份存档核对长度与 SHA-256。该功能不会执行画面恢复，也不会写入相机、OBS 或直播伴侣画面参数。真实 Release 窗口执行结果为“Agent 正常、自启动正常、两款应用可读取、云端下载回读通过”，证明公网预签名 GET 与 PUT 均已由真实客户端走通。

本轮还修复了两个正式使用前的本机问题：

- 老存档在云端注册导致设备签名密钥轮换后无法打开。现在只有本机 SQLite 索引中的长度和 SHA-256 与文件一致，且包内签名、逐文件哈希全部有效时，才迁移旧签名者信任；已有冲突信任记录仍然拒绝，不会把任意外部包自动设为可信。
- 存档详情读取失败时 Avalonia 的嵌套空绑定会让 OBS、相机参数和技术信息三个静态壳同时显示。现在三个区域均由详情存在状态统一控制；相机页打开技术信息时保留最近选中的 OBS/直播伴侣技术投影，不再出现空白面板或内容重叠。真实存档已核对 OBS 锐化 `0.07`、直播伴侣 HSL、三机位参数和技术字段均可见且无遮挡。

本节没有改变恢复证据等级。当前机器仍只有 `OBS Virtual Camera`，没有天创恒达或美乐威 4KPro 物理采集卡；直播伴侣签名定义仍为 `Mapped=1028`、`Verified=0`。代码可提交和下发到受控直播间试运行，但在第二台不同物理采集卡电脑完成规定循环前，不得把它标记为跨硬件百分之百验证版，也不得自动对不匹配的直播伴侣版本写入。

最终质量门：Release 还原与整仓构建成功，0 错误、0 警告；LiveStudio.Core.Tests 223 项、LiveStudio.Agent.Tests 22 项，共 245 项全部通过；云端 PostgreSQL/MinIO 集成项目 Release 编译通过，但本机没有隔离的 Docker 数据库，未对会重置数据的集成套件执行生产连接；全部直接与传递 NuGet 包无已知漏洞；敏感信息扫描与 `git diff --check` 通过。

## 31. 2026-08-27 单机基础版、存档导入应用与公开更新

本节按使用者最新产品方向暂时收起第 26、29、30 节的多直播间桌面入口。左侧不再显示“直播间管理”，画面存档时间线不再显示直播间选择、新建、设为当前、云同步或云设置；已有云端实现与凭据保持不变，供后续开发重新开放，但桌面启动不再主动连接云端。“本机检查与修复”也已收窄为 Agent、自启动、OBS 和直播伴侣本机连接，不再隐式上传存档或下载云端文件。设置页删除高级设置层级、OBS 手动凭据、原生差异实验、局域网目录、云端注册和 GitHub Token，只保留外观、一键连接、本机检查修复、自启动与软件更新。

画面存档页新增常驻“导入并应用”。选择 `.lscfg` 后仍先验证结构、逐文件 SHA-256、包签名和敏感字段；未知签名者必须由使用者核对公钥指纹并明确选择“信任、导入并应用”。导入成功后按包内 ID 选中本机受管存档，只在当前执行端允许恢复时继续调用原有设备映射、Preflight、事务写入、逐字段回读和失败回滚；当前版本、适配器或设备条件不满足时只完成导入并给出原因，不会绕过恢复命令的安全条件。更多菜单继续保留“仅导入存档”和“导出所选存档”，用于只归档不立即恢复的场景。

GitHub 仓库在提交历史敏感信息扫描未发现私钥、真实 `.env`、Token、密码或云端 Secret 后改为公开。软件更新不再读取 GitHub CLI、Windows Credential Manager 或备用 Token，而是匿名访问公开 Releases；下载后仍强制核对配套 SHA-256、固定 Publisher 和证书指纹。发布工作流在标签触发时改为执行整套 Core 与 Agent 测试以及传递依赖漏洞检查。当前公开仓库的最新既有 Release 尚未包含 `LiveStudio-Windows-x64.msix` 与 `.sha256`，真实设置页检查能够匿名连接并准确提示“最新发布中还没有可安装的 Windows 更新包”；本节遵守发布纪律，没有创建标签、Release 或安装包。

真机界面已在浅色 1256×819、浅色 900×700 和深色 900×700 检查。900px 宽度首次暴露存档工具栏和 OBS 滤镜参数裁切，现改为工具栏自动换行、滤镜名称自适应宽度且参数在剩余空间内换行；最终窄窗口可看到 LUT 强度、亮度键、色值、色彩校正和锐化全部真实值，不产生水平滚动。截图：

- `C:\Users\WYZB\AppData\Local\Temp\livestudio-basic-settings.png`
- `C:\Users\WYZB\AppData\Local\Temp\livestudio-basic-settings-900.png`
- `C:\Users\WYZB\AppData\Local\Temp\livestudio-basic-settings-dark-900.png`
- `C:\Users\WYZB\AppData\Local\Temp\livestudio-local-snapshots.png`
- `C:\Users\WYZB\AppData\Local\Temp\livestudio-local-snapshots-900-fixed.png`

本节没有新增物理采集卡证据，也没有重新执行破坏性来源删除恢复；OBS、直播伴侣恢复能力继续引用第 19–25 节当前精确版本的既有真机证据，跨硬件状态仍为 `Mapped=1028`、`Verified=0`。公开后的首次 GitHub CI 复现了旧工作流在 Linux 编译 Windows UI Automation Agent 的平台错误；工作流现拆分为 Windows 整仓 Release 构建、Core/Agent 测试和漏洞检查，以及 Ubuntu PostgreSQL/MinIO 云端集成测试，避免用不具备 `UIAutomationClient` 的 Linux 环境伪装 Windows 构建。拆分后的首次 Windows Runner 又发现 Git Checkout 把 `.cube` 的 LF 改成 CRLF，色卡完整性测试正确失败；仓库现用 `.gitattributes` 把全部内置 LUT 和 PNG 标为逐字节资产，禁止跨平台换行转换。最终质量门：Release 整仓构建 0 错误、0 警告；LiveStudio.Core.Tests 226 项、LiveStudio.Agent.Tests 23 项，共 249 项全部通过；所有直接和传递 NuGet 包无已知漏洞；`git diff --check` 通过。

## 32. 2026-08-27 单机备份与恢复工作台

原“当前画面”首页同时展示应用版本、字段数量、推流/录制状态、事务恢复说明、最近存档和最近操作，普通直播间使用者无法从页面主次判断下一步动作。本轮按单机基础版重新定义首页职责：左侧入口改名为“备份与恢复”，存档页改名为“存档管理”；首页只回答“画面调好了”和“需要恢复画面”两种日常情况，分别提供“保存一份新存档”和“选择存档恢复”两个明确入口。

OBS 与直播伴侣版本、来源/滤镜/字段统计、推流录制状态、恢复协议和操作明细不再占用首页卡片；首页只保留 OBS 连接、直播伴侣读取和本机存档数量三个紧凑状态，以及最近一份存档和重新检测入口。多直播间按钮从页面和可绑定属性中移除，云端实现仍保留在代码内等待后续版本重新开放。恢复前版本、设备、素材与权限检查、事务写入、逐字段回读和失败回滚没有改变，只在页面底部以一句普通用户能够理解的说明表达。

真实 Release 窗口已检查深色 1240×780、深色 900×700 和浅色 1240×780；两个主操作、三项状态、最近存档在窄窗口中均完整可见，没有水平滚动、嵌套滚动或文字遮挡：

- `C:\Users\WYZB\AppData\Local\Temp\livestudio-backup-restore-redesign-wide.png`
- `C:\Users\WYZB\AppData\Local\Temp\livestudio-backup-restore-redesign-narrow.png`
- `C:\Users\WYZB\AppData\Local\Temp\livestudio-backup-restore-redesign-light.png`

本节只调整桌面信息架构、中文文案和入口显隐，不改变 `.lscfg`、OBS/直播伴侣捕获、相机参数绑定或事务恢复执行链，也没有执行破坏性真机恢复。跨物理采集卡证据继续保持 `Mapped=1028`、`Verified=0`。

默认 1240×780 窗口复验随后发现三处纯布局问题并已修正：存档页原自由换行工具栏改为明确的上下两层，名称与 OBS/直播伴侣/相机分段位于第一层，刷新、导入、保存、恢复和更多操作位于第二层；时间线顶部只显示“本机存档”和数量，不再显示“未分配直播间”。设置页有限宽说明文字固定左对齐，自动运行状态和按钮统一垂直居中。Windows 标题栏已经显示应用图标与名称，因此侧栏重复的 LiveStudio 品牌块被移除。最终截图为 `C:\Users\WYZB\AppData\Local\Temp\livestudio-snapshots-default-fixed.png`、`livestudio-settings-default-final.png` 和 `livestudio-home-default-fixed.png`。

## 33. 2026-08-27 单页存档管理与标题栏操作

桌面产品入口进一步收敛为单一“存档管理”：启动后直接打开本机存档时间线和当前存档详情，不再展示独立“备份与恢复”首页，也不再显示全局工作区导航。设置保留为标题栏右侧唯一的页面入口；原独立“操作记录”已移入设置页，与基础设置使用两个紧凑分段切换。

Windows 原生标题栏只承载应用名称和系统最小化、最大化/还原、关闭按钮；当前存档名称、保存当前画面、恢复所选存档和更多菜单位于其下方独立的固定操作栏。OBS、直播伴侣、相机参数的内容切换留在存档详情头部，不再与操作按钮挤在同一排。更多菜单继续完整保留刷新、导入并应用、仅导入、导出、重命名、技术信息、删除和清空本机存档，不改变原命令、安全检查或确认流程。

Windows UI Automation 已确认旧“备份与恢复”和独立“操作记录”入口不再出现在主界面；设置内“操作记录”可切换，标题栏更多菜单中的刷新、导入并应用、仅导入、导出和技术信息均可见。真实 Release 窗口在浅色 900×650 和 1240×780 下无按钮遮挡或标题栏重叠：

- `C:\Users\WYZB\AppData\Local\Temp\livestudio-titlebar-actions-only.png`
- `C:\Users\WYZB\AppData\Local\Temp\livestudio-snapshot-only-narrow.png`
- `C:\Users\WYZB\AppData\Local\Temp\livestudio-activity-inside-settings-final.png`
- `C:\Users\WYZB\AppData\Local\Temp\livestudio-settings-moved-to-timeline.png`

设置入口最终移出 Windows 系统标题栏，固定放入左侧时间线最底部，不随存档列表滚动；“本机存档”栏头部只显示名称和数量。

扩展标题栏曾导致最大化后操作区被系统命中测试吞掉，自绘边框又造成最大化尺寸和圆角异常。窗口现恢复完整 Windows 原生装饰，不再把应用按钮放进系统标题栏；普通窗口缩放、最小化、最大化/还原、双击标题栏和关闭均由 Windows 处理。真机 UI Automation 已实际点击左下角“设置”和普通操作栏中的“返回存档管理”，并确认可以往返；原生最大化后 `IsZoomed=True`，保存、恢复、更多和左下角设置仍存在、可见且可用。

为避免原生标题栏、存档操作栏和详情标题形成三排，独立存档操作栏随后被删除：存档名称、OBS/直播伴侣/相机参数、保存、恢复和更多操作合并到详情顶部同一排，原生 Windows 标题栏之下不再有重复标题。800×600 真机窄窗口中逐个检查六个控件，均位于窗口边界内且无重叠；“更多”使用垂直居中的省略图标。左侧时间线支持整区右键，空白处显示导入并应用、仅导入、刷新和清空；具体存档项额外显示导出、重命名和删除。截图为 `C:\Users\WYZB\AppData\Local\Temp\livestudio-timeline-context-open.png` 与 `C:\Users\WYZB\AppData\Local\Temp\livestudio-snapshot-item-context-open.png`。

单机基础版隐藏直播间选择后，时间线仍按旧直播间筛选，造成保存成功、总数增加但新存档不出现在左侧。时间线现忽略隐藏的直播间分组，只展示全部本机受管存档，并用实际时间线条目数生成“X 份”。真实数据复验显示此前被分散在不同旧分组的 9 份本机存档现全部出现，最新 `2026-08-27 19:39` 位于首项，标题同步显示 9 份。截图为 `C:\Users\WYZB\AppData\Local\Temp\livestudio-local-count-fixed.png`。

相机页删除独立“保存三个机位”，只保留顶部“保存当前画面”。联合保存请求一次携带 OBS、直播伴侣、三机位参数和三张参考图变更；已随旧存档读取的参考图使用逐字节缓存重新嵌入新存档，新增参考图继续校验长度、格式和 SHA-256。参考图写入失败时自动删除本次刚生成的不完整存档，避免左侧出现半份结果。协议回归已确认相机参考图变更通过本机控制消息往返不丢失。

设置页原右上角纯文字“返回存档管理”识别度不足。入口现移到设置页左上角，使用带左箭头、强调色边框和浅强调背景的 32px 按钮，基础设置与操作记录紧随其后；真机 UI Automation 已确认按钮可用并成功返回存档页。截图为 `C:\Users\WYZB\AppData\Local\Temp\livestudio-settings-back-highlighted.png`。

本节只改变桌面信息架构、入口显隐和操作投影，不改变 `.lscfg` 格式、捕获逻辑、设备映射、Preflight、事务恢复、逐字段回读或失败回滚。跨物理采集卡证据仍为 `Mapped=1028`、`Verified=0`。

最终质量门：Release 整仓构建 0 错误、0 警告；LiveStudio.Core.Tests 226 项、LiveStudio.Agent.Tests 23 项，共 249 项全部通过；全部直接和传递 NuGet 包无已知漏洞；`git diff --check` 通过。

## 34. 2026-08-27 正式下发前审查修复

本轮针对完整发布审查发现的问题完成修复，但没有执行新的破坏性 OBS/直播伴侣恢复，也没有新增实体采集卡证据。公开更新不再调用受每公网出口每小时 60 次额度影响的 GitHub REST API，而是从 `github.com/<仓库>/releases/latest` 的公开重定向解析标签，并使用固定 Release 下载地址检查和下载 MSIX 与 SHA-256。所有下载重定向只接受 GitHub 与 `githubusercontent.com` HTTPS 主机；安装前继续强制验证 SHA-256、固定 Publisher 和证书指纹。真实设置页在 GitHub API 额度已经耗尽的环境中检查更新，不再返回 403，而是准确显示当前既有 `v0.1.1` 尚无 `LiveStudio-Windows-x64.msix`。下一次标签发布工作流仍负责生成并签名该固定名称的 MSIX。

联合保存现在先生成签名包，再在一个 SQLite 事务中同时写入存档索引和全部本机设备映射；映射或索引任一步失败会回滚整个数据库事务并删除刚生成的 `.lscfg`。新增 Windows 回归主动删除 `device_mappings` 表以强制最后一步失败，确认索引为 0、存档目录无 `.lscfg` 残留，覆盖了此前“界面提示保存失败但数量增加”的边界。

单机时间线不再显示历史云端 `已同步/等待上传/仅本机` 状态，只显示真实文件大小。设置页不再用 Agent 存活状态冒充两款应用均已连接，而是分别显示 OBS 与直播伴侣的实际读取/运行状态；真实客户端显示 OBS“已连接”、直播伴侣“已读取”。800×600 窗口中 OBS、直播伴侣、相机参数、保存、恢复和左下设置均位于窗口边界内且可用，时间线文本中旧同步状态为 0 项。色卡与 LUT 已由项目所有者明确确认为原创并允许自由使用，仓库授权说明同步纳入 MIT。

工程格式已经统一，`.gitattributes` 固定源码、配置和色卡清单为 LF，内置 LUT/PNG 保持逐字节 `-text -eol`；普通 CI 与标签 Release 均增加 `dotnet format --verify-no-changes`。最终 Release 还原和整仓构建为 0 错误、0 警告；LiveStudio.Core.Tests 232 项、LiveStudio.Agent.Tests 24 项，共 256 项全部通过；云端集成项目 Release 编译通过；全部直接和传递 NuGet 包均无已知漏洞；`dotnet format --verify-no-changes`、`git diff --check` 与 `git fsck --full` 通过。

本节没有改变跨硬件证据等级。当前机器仍缺少天创恒达或美乐威 4KPro 实体采集卡，直播伴侣精确版本的覆盖继续为 `Mapped=1028`、`Writable/Required=966`、`Verified=0`；正式标记跨硬件验证仍需第二台不同实体采集卡电脑完成规定循环。

发布准备新增固定内部 MSIX 签名身份 `CN=LiveStudio Internal`，公钥 SHA-1 指纹为 `4D42933F643E1E0B649513BCD10A15B485746E1D`，有效期至 2031-08-27。PFX 与密码只保存在 GitHub 加密 Secret 和仓库外的本机 DPAPI 备份中；仓库与 Release 只公开 `.cer`。`v0.1.2` 已完整通过还原、编译、测试和证书导入，但 SignTool `/pa` 因自签名根未进入 Runner 的受信任根而按预期阻止创建 Release；`v0.1.3` 和 `v0.1.4` 又确认向无界面 Runner 的 Root 写入会触发系统安全确认，因此均在产物生成前取消。`v0.1.5` 改为不修改 Runner Root，并已严格通过摘要、Signer 指纹和唯一链错误判断，但 PowerShell 保留了已接受的 SignTool 原生退出码，导致步骤在无异常时仍以 1 结束。上述标签保持不可变且没有发布下载。`v0.1.6` 在相同严格判断通过后显式清零原生退出码；坏摘要、未签名、错误证书或其他错误仍立即失败。工作流同时发布 MSIX、SHA-256、签名报告和首次安装所需的公钥证书。固定直播间电脑首次安装公钥后，本机签名状态为 `Valid`，软件内更新继续锁定同一 Publisher 与证书指纹。

## 35. 2026-08-27 一键首次安装

固定直播间电脑不再需要人工下载和导入发布证书。Windows Release 新增单文件 `LiveStudio-Setup.exe`，内部封装同一 Release 的签名 MSIX、SHA-256 和公开证书；双击后只请求一次 Windows 管理员授权，把指纹固定为 `4D42933F643E1E0B649513BCD10A15B485746E1D`、Subject 固定为 `CN=LiveStudio Internal` 的证书加入 `Local Machine\Trusted People`，不写入 `Root`。安装器在导入前核对自身与 MSIX 的 Signer，在导入后要求两份 Authenticode 状态都为 `Valid`，再按包版本执行安装或升级并启动 LiveStudio。同版本重复运行不会重复降级或制造第二份安装。

发布工作流继续保留独立 MSIX、证书和全部校验报告供审计，但对普通直播间使用者只推荐下载 `LiveStudio-Setup.exe`。软件安装后仍沿用既有匿名 GitHub Release 更新链，并在退出当前程序前校验 MSIX SHA-256、固定 Publisher 和固定证书指纹。本轮只改变 Windows 分发与首次安装，不改变 `.lscfg`、OBS/直播伴侣捕获、设备映射、相机记录或事务恢复协议；跨硬件证据仍为 `Mapped=1028`、`Writable/Required=966`、`Verified=0`。

安装器最初在管理员环境中通过 PowerShell `Get-AuthenticodeSignature` 执行导入后的信任校验；`v0.1.7` 公开包的哈希、Signer 与内嵌载荷均通过，但当前真机首次安装时系统无法自动加载 `Microsoft.PowerShell.Security`，证书加入 Trusted People 后安全中止，MSIX 没有安装。该 Release 已立即标记为预发布并注明撤回，不再作为软件最新正式版本。安装器随后改为直接调用 Windows `WinVerifyTrust`，不再依赖 PowerShell 安全模块；需要执行 Appx 安装时固定调用系统 Windows PowerShell 并补齐系统模块路径。`--verify-only` 同时执行 WinVerifyTrust 和 Appx 模块加载，防止发布流水线再次只验证外层而漏过运行时实现。

安装器现有 7 项专门测试，覆盖 Release SHA-256 格式、非法校验拒绝、MSIX manifest 版本读取、Publisher 拒绝和未签名文件拒绝。最终单文件封装、时间戳签名、公开下载后哈希和真机首次安装状态必须以修复标签的 GitHub Windows Release 工作流与本机实际安装为准。

`v0.1.8` 的 Release 在一键安装器自检阶段停留超过本机正常耗时，确认是非交互 Runner 上的失败提示框阻塞；流程在创建 Release 前被人工取消，因此没有公开产物。自检模式现禁止显示任何窗口，最多等待 60 秒，失败时把 `%ProgramData%\LiveStudio\Installer\install.log` 原因直接写入 Actions 日志；真实首次安装仍保留用户可见的成功或失败提示。

`v0.1.9` 随后在创建 Release 前明确返回 `0x800B0109`：一次性 Runner 的内部自签名根不在本机信任链。发布自检现只在 Signer 指纹和 Publisher 已固定匹配时接受该精确的内部根错误，与外层 SignTool 规则一致；坏摘要、未签名、错误证书及任何其他 WinVerifyTrust 错误仍失败。真实首次安装不启用此例外：证书写入 `Local Machine\Trusted People` 后，EXE 和 MSIX 必须由 WinVerifyTrust 返回完全成功才会执行 Appx 安装。

`v0.1.10` 的云端构建、签名和内置资源自检全部通过，但从公开 Release 下载后执行首次安装时，Windows PowerShell 没有把 `-Command` 后的独立参数填入脚本 `$args`，导致 `Add-AppxPackage -Path` 收到空值；包未安装，版本已立即标记为预发布并撤回。安装器现通过进程环境变量传递 MSIX 路径和目标版本，避免命令行重新解析路径；专门测试覆盖含中文和空格的参数，修复版本必须再次从公开 Release 下载并完成真实首次安装后才可下发。

`v0.1.11` 已完成公开 Release 回下载与本机升级验证：`LiveStudio-Setup.exe` SHA-256 为 `fa67d2b086bd5f253f302f5124fdafe8920b7fd7d37039ad8918f9c53e3f28ff`，与 GitHub Asset Digest 和随附校验文件完全一致；Authenticode 状态为 `Valid`，Signer 指纹为 `4D42933F643E1E0B649513BCD10A15B485746E1D`，`--verify-only` 返回 `0`。同一公开安装器已把本机包从 `0.1.6.0` 实际升级到 `0.1.11.0`，安装后 `LiveStudio.Desktop` 已运行，`Local Machine\Trusted People` 中固定证书存在，安装失败日志不存在。该证据只覆盖本机安装与升级链，不改变直播伴侣跨两台采集卡 20 次逐字段恢复的 `Verified=0` 状态。

## 36. 2026-08-28 恢复故障、永久前置备份与跨版本结构匹配

公开 `v0.1.11` 在另一台直播间电脑暴露 `GetInputDefaultSettings 失败 (605)`。OBS 的 `GetInputDefaultSettings` 与 `GetSourceFilterDefaultSettings` 只是补充默认值的可选请求，部分 OBS/插件组合会拒绝该请求，但当前值仍可完整读取。本轮只对带结构化请求名和状态码的这两类拒绝降级为空默认值；连接、认证、传输或其他 OBS 请求失败继续使保存失败，不能被吞掉。OBS 锐化等当前滤镜值仍来自 `GetSourceFilter`，不依赖默认值接口。

恢复事务现在在全部 Preflight 通过后、任何原生写入开始前，先生成一份永久 `.lscfg`，名称为“恢复前自动备份 yyyy-MM-dd HH:mm:ss”，包含当时的 OBS、直播伴侣和三机位手动参数。前置备份失败时恢复直接停止且不会创建任何写入会话；备份成功后才允许进入原有事务写入、逐字段回读和失败回滚。该永久存档与事务目录中的短期原生边界备份职责不同：前者供使用者日后手动恢复，后者只供本次失败自动回滚。恢复代码没有删除直播伴侣安装目录；事务完成时只删除 `%LocalAppData%\LiveStudio\Transactions\LiveCompanion` 下本软件创建的事务子目录，存档删除仍只响应使用者明确的删除操作。

直播伴侣版本兼容不再把版本号范围当作唯一依据。新版本捕获时先读取四个原生存储；签名定义把稳定必需字段与版本/状态相关的可选字段分开。966 个必需可恢复字段的运行时路径和 JSON 类型必须逐项一致；已发现的每个额外字段也必须存在于签名定义并匹配类型，签名定义中声明为可选的字段允许在旧版本中不存在、在新版本或使用后出现。真正未知的新增字段、必需字段缺失、类型变化、存储缺失、动态绑定失败或存档签名定义不一致都会返回不兼容，只允许检查而不写入。多个签名定义同时匹配时按数字修订号选择最新定义，避免字符串顺序把 `v10` 排在 `v2` 之前。

进程控制也已收紧：安装探测优先选择磁盘中实际存在的最高版本目录；启动、验证、回滚和崩溃事务恢复都必须等待刚才指定的可执行文件路径，旧版本进程不能再冒充新版本已经启动。当前机器曾实际读取到 12.9.2：四个存储共 1028 项，966/966 必需可恢复字段路径存在且类型一致；但更新目录随后不再存在，磁盘当前只剩 12.8.1.454484231。因此 12.9.2 只能记录为结构审计通过，不能记录为 5 次恢复通过。

12.9.2 使用后留下的原生存储在 12.8.1 启动时实际出现 14 个额外叶字段：上庭、中庭、下庭、眉距和 M 唇在当前值及两个预设临时树中的新增 `absVal`，以及 `zongyiyouxi/items`、`zongyiyouxi/use`。新增 v3 签名定义完整声明 1042 项，其中这 14 项为“存在时捕获、写入并回读”的可选字段；它们不是可忽略的未知数据。最终定义 SHA-256 为 `ded709c3af49d440335726237c76b6c3fe9aa3400a24a539303c4154a746926d`。真实联合保存 `d912cf10-7e01-4719-ac54-bf09be9c98ee` 已确认 `adapterId=webcast-mate-12.8.1.454484231-8216f9ee-v3`、`Compatibility=Verified`、字段与原生值均为 1042 项。随后在当前 12.8.1 进程上连续执行 5 次同状态恢复，5 次均返回成功、逐字段回读零差异，并把本机存档数从 28 增加到 33；每轮各新增一份永久“恢复前自动备份”。该证据证明最终 v3 的 1042 字段当前结构在本机可重复恢复，但不替代 12.9.2 可执行文件本身和第二台实体采集卡电脑验收。

两次早期故障分别由素材长度校验实现错误和同设备多实例歧义触发，均在提交前失败或完成事务回滚。素材校验现按真实 `AssetBlob.Length` 比较，不再误用 64 字符 SHA-256 字符串长度；同设备多实例先逐实例完整回读，任一实例完全一致即可成功，多个实例均不一致时拒绝猜测目标。

本节质量门：Release 整仓构建 0 错误、0 警告；LiveStudio.Core.Tests 238 项、LiveStudio.Agent.Tests 25 项、LiveStudio.Setup.Tests 8 项，共 271 项全部通过；全部直接与传递 NuGet 包无已知漏洞；`git diff --check` 除既有行尾转换提示外无空白错误。12.9.2 重新安装后的 5 次恢复，以及第二台不同实体采集卡电脑的 20 次验证仍未完成，证据等级继续保持 `Mapped=1028`、`Writable/Required=966`、`Verified=0`。

## 37. 2026-08-28 更新失败与存档索引自修复

公开 `v0.1.12` 的安装器本体、SHA-256、Authenticode 签名和内嵌载荷自检均通过，本机使用该安装器已从 `0.1.11.0` 实际升级为 `0.1.12.0`，升级后 Desktop 与 Agent 均来自同一个 `0.1.12.0` WindowsApps 包，原有 33 份本机存档未被删除且 Agent 全部返回。现场反馈同时说明旧应用内更新链和升级后旧 Agent 残留仍会造成不可用体验，因此后续补丁不再让桌面端自行执行隐藏的 MSIX PowerShell 脚本，而是下载 Release 中同一份 `LiveStudio-Setup.exe` 与 SHA-256，完成固定证书指纹校验后直接请求一次管理员授权并交给已验收的一键安装器升级。

桌面端启动时现在比较正在运行的 `LiveStudio.Agent.exe` 与当前包内 Agent 的完整路径。路径属于旧 WindowsApps 版本时先结束旧 Agent，再启动当前包内 Agent；路径一致时保持原进程，不重复启动。这样更新后不会继续连接旧协议执行端。

Agent 启动时还会扫描 `%LocalAppData%\LiveStudio\Snapshots`。由当前本机签名身份或已明确受信任签名者生成、包内签名和逐文件哈希均有效、但因异常退出而没有数据库记录的 `.lscfg` 会自动补回索引并重新显示。来自未知电脑的文件不会被静默信任，仍须通过界面的“仅导入存档”确认签名指纹。该修复不删除文件、不覆盖存档内容，也不放宽恢复前的签名、版本、结构、设备、素材和逐字段回读检查。

第一次 `v0.1.13` 真机启动时复现到界面显示 0 份而 Agent 实际返回 33 份：新 Agent 在启动阶段扫描存档，桌面端只等待固定 800 毫秒便执行一次状态读取，连接超时后清空界面且不再重试。最终补丁把首次状态加载改为有界重试，并让索引扫描先按已登记完整路径和文件长度跳过正常存档，只对缺失索引或长度异常的文件执行完整签名检查。该修复后必须以 `v0.1.14` 再次完成真实安装启动，确认 Desktop 与 Agent 同版本、33 份原存档立即显示，再下发给直播电脑。

`v0.1.14` 公开安装器 SHA-256、固定证书指纹和内置自检均通过，本机从 `0.1.13.0` 升级后的第一次启动立即显示原有 33 份；随后通过真实界面执行一次“保存当前画面”，Agent 与界面同步增加到 34 份，最新存档 `2026-08-28 18:24` 自动选中并可查看。继续使用软件自身更新服务下载同一公开安装器时，真实复现到 Windows PowerShell 无法自动加载 `Microsoft.PowerShell.Security`，因此 0.1.14 的存档显示修复成立，但应用内更新仍会在签名验证阶段被阻断。最终更新校验改为直接调用 Windows `WinVerifyTrust` 并读取 Authenticode 签名证书核对固定 Publisher 与指纹，不再依赖 PowerShell 模块；必须由 `v0.1.15` 再次完成软件自身“检查更新、下载校验、启动安装器”的真机升级链后下发。

`v0.1.15` 公开安装器已完成回下载、逐字节校验和本机升级：文件长度为 217831824 字节，SHA-256 为 `bf901087b8a8e697df2c382baa6a0f26a3fffdfd9b4425f31773d516cb712db6`，与 Release 校验文件一致；Authenticode 状态为 `Valid`，Publisher 为 `CN=LiveStudio Internal`，证书指纹为 `4D42933F643E1E0B649513BCD10A15B485746E1D`，安装器自检返回 0。本机安装后 Desktop 与 Agent 均来自同一个 `0.1.15.0` WindowsApps 包，首次启动立即读取并显示全部 34 份原有存档，最新 `2026-08-28 18:24` 仍可选中查看。更新服务的真实下载与准备流程也已使用正式编译程序集执行：从公开 Release 下载 `v0.1.14` 安装器并通过新的原生签名校验，生成可启动的待安装文件；旧版本首次升级到 0.1.15 仍应手动运行一次公开安装器，因为旧版本自身携带的是已经失效的 PowerShell 校验实现。

本节最终质量门：Release 整仓构建 0 错误、0 警告；LiveStudio.Setup.Tests 8 项、LiveStudio.Core.Tests 244 项、LiveStudio.Agent.Tests 28 项，共 280 项全部通过；全部直接与传递 NuGet 包无已知漏洞；格式验证和 `git diff --check` 通过。上述证据覆盖本机更新、启动、保存和存档显示，不改变第二台不同实体采集卡电脑 20 次恢复尚未完成的 `Verified=0` 状态。

## 38. 2026-08-28 跨电脑可移植存档与真实异机恢复

现场失败存档 `39e37fd9b322485fa859c00e5febc39d.lscfg` 来自另一台 Windows 电脑，SHA-256 为 `AAF88118D71560FB7FB7D79629B74106B63CCB3D3F72D30D096916AB8EF8A593`。其直播伴侣原生树包含 4 个文档、2109 个非敏感叶字段和 5 个等价摄像头实例；这些实例共享同一设备、效果配置和画面参数，但场景、来源及滤镜实例标识均由来源电脑生成。旧执行链因存档是探测扫描而拒绝写入；即使放开拒绝，直接重放这些标识也不能在另一台电脑正确恢复。

直播伴侣存档现增加 `webcast-mate-portable-v1` 投影：

- 保存时从完整、已完成敏感字段审计的原生树提取设备、视频模式、摄像头画面参数、完整效果配置和签名定义允许的全局画面字段，不再把来源电脑的场景 GUID、来源 ID、效果配置 ID 或滤镜实例 ID 当作目标标识。
- 多个场景中的等价摄像头实例折叠为一个可移植画面配置；全局字段中指向任一等价实例的引用先归一到代表来源，恢复时再绑定目标电脑的真实场景、来源和效果标识。
- 目标机现有摄像头的运行字段、未知目标字段和滤镜实例 ID 保留；存档画面值合并后通过直播伴侣原生效果包导入，再在停机边界把完整效果配置和全局字段绑定到目标 ID。重启后对设备、模式、滤镜、美颜、HSL、曲线、素材和所有进入投影的原生字段逐项回读。
- 旧 `webcast-mate-json-discovery` 和旧精确签名存档在读取时按相同规则升级为可移植恢复投影；新保存的存档默认直接写入可移植投影。存档原始字段仍保留在签名包与技术信息中。

第一次真实恢复在写入后发现 1576 项效果字段未落到目标效果配置，事务按预期失败并回滚；第二次补齐目标效果配置写入后只剩两个 `faceSources` 来源引用不一致，同样失败并回滚。两次失败均新增永久“恢复前自动备份”，操作状态为 `FailedRolledBack`，没有把半份配置留在电脑上。归一全部等价来源引用后，同一外部存档在当前 12.8.1.454484231 上完成真实异机恢复，1702 项可移植画面字段逐项回读零差异。随后连续执行第 2–5 轮，同样全部成功、零差异，每轮均先新增独立永久备份。

新执行端随后真实保存 `46a43344-bdfb-4b04-a8f6-34fa1e53f1ca`：OBS 为 77 个字段，直播伴侣自动生成 `webcast-mate-portable-v1`、4 个原生文档、1 个可映射摄像头来源和 2405 个可移植字段。导出文件重新通过包签名与逐文件哈希检查，长度为 3235416 字节；使用该新存档执行 OBS 与直播伴侣联合恢复成功、逐字段回读零差异，证明新保存链不依赖旧存档兼容转换。

OBS 设备映射还增加了物理设备枚举：即使目标 OBS 已删除全部视频采集来源，执行端也会创建一个禁用且立即清理的临时 `dshow_input`，读取 OBS 自身的 `video_device_id` 列表，用目标电脑的真实设备标识创建来源并检查分辨率、FPS、像素格式、色彩空间和色彩范围。当前电脑没有天创恒达或美乐威采集卡，真机枚举正确返回 0 个物理设备；因此这里证明了空来源探测路径和无残留清理逻辑，但没有新增第二台物理采集卡恢复证据。

本节最终质量门：Release 整仓构建 0 错误、0 警告；LiveStudio.Setup.Tests 8 项、LiveStudio.Core.Tests 250 项、LiveStudio.Agent.Tests 29 项，共 287 项全部通过。上述证据证明来源电脑存档到当前电脑的直播伴侣可移植恢复和新存档联合恢复闭环；按项目证据规则，第二台不同实体采集卡电脑的规定验收尚未完成，正式 `Verified` 计数仍保持 0，不能把本节描述为所有硬件组合已经完成百分之百验证。

## 39. 2026-08-28 能力式跨版本捕获与不可恢复存档阻断

另一台电脑新保存后立即显示“该直播伴侣存档来自探测扫描”的现场截图证明，旧捕获链仍可能在可移植投影生成失败或版本签名不匹配时静默保存 `webcast-mate-json-discovery`。这不是恢复按钮本身的问题，而是保存阶段生成了一份明知禁止写入的探测存档，直到用户点击恢复才暴露。

本轮把保存不变量收紧为：联合保存只有在直播伴侣已经提取出可移植摄像头画面配置、并且目标签名版本或全部必需恢复字段路径与类型匹配时才成功。任何条件不满足都会直接取消本次联合保存，不再落盘、索引或展示一份不可恢复的新存档。错误现在精确区分缺少 `sourceStore.json` / `effectConfigStore.json`、没有摄像头来源、设备或视频模式字段缺失、多个摄像头来源包含不同画面配置、效果配置缺失、敏感字段混入和结构匹配失败。

直播伴侣摄像头载荷不再把 `name`、`effect1`、`videoRange`、`colorSpace` 和 `filterData` 误作所有版本都必须存在的字段。它们在当前版本实际存在时仍逐项完整保存和恢复；某个版本或来源确实不生成这些可选能力时，不再因此把整份存档降级。真正必需的跨电脑绑定字段保持为设备标识、像素格式、宽度、高度和帧率。超出已记录版本范围的版本不再只因版本号被拒绝；已签名存储边界、全部必需可恢复路径、JSON 类型和可移植摄像头结构一致时可选择同一结构适配器。真正新增未知字段、必需路径缺失或类型变化仍在写入前阻断，不能靠放宽版本号盲写。

本机真实直播伴侣配置已通过一次只读捕获探针，生成 `webcast-mate-portable-v1`、单一可映射来源和非空字段覆盖；该探针没有执行恢复或修改直播伴侣配置。自动化新增未来版本号结构匹配和缺少上述可选摄像头字段两类回归。最终 Release 整仓构建为 0 错误、0 警告；LiveStudio.Setup.Tests 8 项、LiveStudio.Core.Tests 252 项、LiveStudio.Agent.Tests 29 项，共 289 项全部通过；全部直接与传递 NuGet 包无已知漏洞；格式验证和 `git diff --check` 通过。第二台失败电脑的新 `.lscfg` 尚未提供到当前工作区，因此该电脑的具体摄像头分组或存储差异仍需用真实失败包复核，正式 `Verified` 计数保持 0。

## 40. 2026-08-29 0.1.17 真机升级、跨版本恢复与五轮稳定性复验

公开 `v0.1.17` 已由 GitHub Windows Runner 完成构建、测试、MSIX 签名和一键安装器自检。本机回下载的 `LiveStudio-Setup.exe` 长度为 217853328 字节，SHA-256 为 `9B590A1BC398CF97AA7CECC0ABB9ECE2DC8C95E7E08AD2296EEC971913C4416A`；EXE 与内嵌 MSIX 的 Authenticode 状态均为 `Valid`，Signer 指纹为 `4D42933F643E1E0B649513BCD10A15B485746E1D`，MSIX manifest 版本为 `0.1.17.0`，安装器 `--verify-only` 返回 0。U 盘 `E:\参数恢复软件` 已只保留该安装器和配套 SHA-256；旧 0.1.16 安装器另存于本机安装器备份目录。

本机升级前停止 Desktop 与 Agent，单独备份存档、事务、相机参数、OBS 素材和本地索引，并对 62 个关键文件逐个计算 SHA-256。MSIX 从 `0.1.16.0` 升级到 `0.1.17.0` 后，同一 62 个文件的相对路径、长度和哈希全部一致，数据差异为 0；这证明本次真实升级没有删除或覆盖现有用户数据。

在 OBS 32.2.2 与直播伴侣 12.8.1.454484231 同时运行时，0.1.17 真实联合保存 `c9c8f9e0-ad32-4bab-867c-9757374a8042` 成功：OBS 为 3 个来源、9 个滤镜和 77 个覆盖字段；直播伴侣生成 `webcast-mate-portable-v1`，包含 4 份原生文档、2537 个原生叶值和 2405 个可移植字段，双读哈希一致，不再生成 `webcast-mate-json-discovery`。该存档第一次完整事务恢复耗时 40.1 秒，OBS 与直播伴侣均重新启动，逐字段回读返回“恢复完成”。

同一存档随后导出到独立 `.lscfg`，长度为 3104940 字节，SHA-256 为 `76038651369B963B1F949FAEE0767FC3D88AF72ECF1A7B8063A3AACCADBC2BC7`。执行端确认签名者已受信任后，从本机受管库删除该 ID，再只凭导出文件重新导入；导入后的 ID 保持不变、索引只出现一份且详情可读，证明导出文件不依赖原 SQLite 记录。

直播伴侣更新器已经下载但尚未启用的 12.9.2.470033184 随后由真实可执行文件启动。0.1.17 对该进程再次完成双读一致保存，仍得到 4 份原生文档、2537 个叶值和非探测可移植适配器。使用 12.8.1 导出的上述存档直接恢复到运行中的 12.9.2 成功，耗时 39.7 秒，逐字段回读为“恢复完成”。直播伴侣 Launcher 随后自行完成正式更新，`cur_path` 变为 `12.9.2.470033184`、`need_update=false`，旧 12.8.1 程序目录由其更新器清理；本轮没有手工修改 Launcher 配置。

在正式 12.9.2 上继续连续执行 5 轮完整事务恢复，五轮分别耗时 40.0、40.0、39.9、39.9、40.0 秒，全部成功；每轮 OBS 32.2.2 与直播伴侣 12.9.2 都重新启动并回读，未出现不可恢复提示、映射失败、半份写入或回滚失败。每轮恢复前均新增一份永久“恢复前自动备份”，因此桌面重启后时间线准确显示 2 份主动保存和 7 份恢复前备份，共 9 份；这不是重复计数。真实 0.1.17 截图为 `artifacts/windows-validation/2026-08-29/desktop/0.1.17/livestudio-after-restart.png`，页面没有再显示“存在无法恢复的内容”。最后通过 Windows UI Automation 直接点击真实界面的“保存当前画面”，Agent 和左侧时间线同步从 9 份增加到 10 份，新存档 `93308284-5ca5-4b4e-9924-325dacdf20bf` 自动选中并显示“当前画面已保存，并已自动打开新存档”；截图为 `artifacts/windows-validation/2026-08-29/desktop/0.1.17/livestudio-ui-save-10-items.png`。

新增 UI 防回归测试确认 `webcast-mate-portable-v1` 即使证据等级仍为 `Experimental`，也必须显示“已保存 · 可跨电脑还原”，不得生成恢复警告。最终 Release 构建为 0 错误、0 警告；LiveStudio.Setup.Tests 8 项、LiveStudio.Core.Tests 253 项、LiveStudio.Agent.Tests 29 项，共 290 项全部通过；全部直接与传递 NuGet 包无已知漏洞，格式验证通过。

本节证明了当前一台 Windows 机器上的真实升级、独立导出删除再导入、12.8.1 到 12.9.2 跨版本恢复，以及 12.9.2 五轮连续事务恢复。当前 PnP 枚举仍没有天创恒达或美乐威实体采集卡，且第二台电脑没有接入本轮自动化控制，所以不能把本节外推为所有 Windows、所有 OBS 插件或所有采集卡均已完成百分之百验证；正式 `Verified` 计数继续保持 0。下一项最小证据是把本节导出的 `.lscfg` 带到第二台 4KPro 电脑，记录精确硬件 ID 和驱动版本后执行同样五轮恢复与逐字段回读。

### 40.1 U 盘跨电脑验收交接

公开 `v0.1.17` 安装器已再次从 GitHub Release 下载到本机临时隔离目录，核对版本 `0.1.17.0`、SHA-256、Authenticode 签名和 `--verify-only` 后覆盖到 `E:\参数恢复软件\LiveStudio-Setup.exe`。U 盘副本长度为 217853328 字节，SHA-256 为 `9B590A1BC398CF97AA7CECC0ABB9ECE2DC8C95E7E08AD2296EEC971913C4416A`，Signer 为 `CN=LiveStudio Internal`，自检退出码为 0；被替换文件先备份到本机 `%LOCALAPPDATA%\LiveStudio\InstallerBackups\usb-before-v0.1.17-20260829-145239`，没有删除 U 盘或目标电脑的其他数据。

跨电脑验收包已复制为 `E:\参数恢复软件\跨电脑恢复测试\LiveStudio-跨电脑恢复测试.lscfg`，长度为 3104940 字节，SHA-256 为 `76038651369B963B1F949FAEE0767FC3D88AF72ECF1A7B8063A3AACCADBC2BC7`。运行中的正式 `0.1.17` Agent 已直接从 U 盘路径完成包检查，返回存档 ID `c9c8f9e0-ad32-4bab-867c-9757374a8042`、签名者受信任；同目录包含独立 SHA-256 文件和中文五轮操作说明。该动作只证明交接介质逐字节一致且正式读取器可打开，不能替代第二台 4KPro 真机上的设备映射、保存、恢复、逐字段回读和故障回滚证据。

### 40.2 第二台电脑可重复验收工具

仓库新增 `tools/LiveStudio.SecondMachineValidation/Run-SecondMachineValidation.ps1`，U 盘副本使用 ASCII 名称 `LiveStudio-CrossMachine-Test.lscfg`，避免 Windows PowerShell 5.1 对无 BOM 脚本中中文默认文件名的错误解码。工具默认只读采集 Windows、LiveStudio Desktop/Agent、OBS、直播伴侣、采集卡硬件 ID 与驱动版本，并同时核对安装器固定 SHA-256、原生 `--verify-only`、测试包 SHA-256 和固定包签名者指纹。五轮模式必须人工输入 `RESTORE-5`，随后只通过正式 Agent 的检查、导入、唯一候选设备映射和 `RestoreSnapshot` 接口执行；候选不唯一时停止，不直接写 OBS 或直播伴侣原生文件。

Windows PowerShell 5.1 实测发现，重定向启动的子进程可能无法自动加载 `Get-FileHash` 与 `Get-AuthenticodeSignature` 所属模块。工具已改用 .NET SHA-256，Authenticode 命令只作为辅助显示；强制门仍由固定哈希、安装器原生自检和正式 Agent 包读取器完成。U 盘相对路径只读冒烟返回 `EvidenceOnly`、退出码 0，准确读取安装器 `0.1.17.0`、OBS `32.2.2`、直播伴侣 `12.9.2.470033184`、受信测试包和 Agent 连接。五轮入口输入错误确认词时退出码为 1、循环数为 0，取消前后的 10 份本机 `.lscfg` 路径、长度和 SHA-256 逐项无差异。工具尚未在第二台实体 4KPro 电脑执行，因此这些结果只是交接工具的本机安全冒烟，不提升 `Verified=0`。

## 41. 2026-08-29 跨版本保存签名门槛修复与 0.1.18 发布前复验

现场旧版直播伴侣电脑在保存阶段返回“没有签名适配定义”，而同一安装包在开发电脑 12.9.2 上能够保存。代码审查确认 `MatchPortableTarget` 错误地要求目标版本必须落入 12.8.1–12.9.2，或者与 966 个已记录必需字段逐项完全相同；这个写入适配门被错误地提前放进了只读保存入口，因此旧版本即使已经成功读取四份原生存储并生成可移植摄像头配置，仍会被版本号拒绝。

0.1.18 将可移植存档的适配选择改为能力匹配：只要真实配置包含签名定义声明的 `effectConfigStore.json`、`effectStore.json`、`filterStore.json`、`sourceStore.json` 四份存储，并具备可移植摄像头结构，版本号不再阻断保存或事务恢复。缺少任一存储或摄像头结构仍会拒绝；恢复前仍执行目标存储根节点检查、设备映射、完整原生边界备份，写入后仍逐项回读，不一致时整次回滚。全套 291 项测试通过，新增回归覆盖“版本低于记录范围且不具备 966 项旧版完整字段仍可按存储能力匹配”，同时覆盖缺存储和缺摄像头时继续拒绝；`dotnet format --verify-no-changes`、依赖漏洞检查和 `git diff --check` 均通过。

本机真实复验前将 `%LOCALAPPDATA%\LiveStudio` 的 536 个文件完整复制到 `%LOCALAPPDATA%\LiveStudio-PreReleaseBackups\before-v0.1.18-capability-test-20260829-160834\LiveStudio`，长度与 SHA-256 对比差异为 0。修复后的 Agent 在 OBS 32.2.2、直播伴侣 12.9.2.470033184 上通过正式控制协议保存存档 `9b0fe748-b214-45e2-a8a3-b3d42d5750bd`，受管存档从 12 增至 13；随后恢复该存档耗时 39.8 秒，恢复前永久备份使存档从 13 增至 14，操作状态为成功、文案为“恢复完成”，最终状态为“恢复完成并通过逐项验证”。该结果证明本机正式保存、事务备份、写入、回读链没有因兼容修改退化；旧版电脑仍需安装 0.1.18 后执行同一保存诊断与恢复循环，才能形成对应版本的真机证据，不能据此把未知存储结构宣称为已验证。

GitHub Release `v0.1.18` 的 Windows 流水线随后完成全部测试、格式检查、漏洞检查、MSIX 签名、安装器签名和内嵌载荷复验，步骤均为成功。公开 `LiveStudio-Setup.exe` 长度为 217853328 字节，SHA-256 为 `69338CD01ADC93A430B8953A8C801EE238F202C2452770DD5D5C98976D5C65AA`；回下载副本的 Authenticode 状态为 `Valid`，Signer 为 `CN=LiveStudio Internal`，指纹为 `4D42933F643E1E0B649513BCD10A15B485746E1D`，`--verify-only` 退出码为 0。U 盘原 0.1.17 安装器和测试工具已先完整备份到 `%LOCALAPPDATA%\LiveStudio\InstallerBackups\usb-before-v0.1.18-20260829-161507`，再覆盖为该 0.1.18 安装器；U 盘副本的长度、SHA-256、签名和内嵌复验再次全部一致。第二台电脑工具同步要求 Desktop/Agent 0.1.18，并增加正式保存诊断入口。Windows 报告 U 盘卷健康状态为 `Warning`，但本轮所有复制与逐字节校验成功；交接前应安全弹出，若其他电脑仍报告卷错误则更换介质，不能把介质故障误判为 LiveStudio 恢复失败。

## 42. 2026-08-29 恢复写入“拒绝访问”权限链修复

0.1.18 在另一台电脑已能成功保存，但恢复返回 Windows“拒绝访问”。保存成功证明存档包、可移植投影和字段提取已通过；故障位于恢复事务的进程停止或原生文件写入阶段。旧实现在读取高完整性级别的直播伴侣 `MainModule`、强制终止进程或替换配置文件时会直接向上抛出本地化错误 5，协调器只显示原始四个字，无法判断对象和阶段。

当前修复包含：

- 正常关闭仍是默认路径；只在 Windows 明确返回错误 5 时，才使用系统 `%SystemRoot%\System32\taskkill.exe` 针对已校验进程 ID 和进程名请求 `runas` UAC。取消 UAC 时恢复立即停止，不写配置。LiveStudio 本身不会改为永久管理员运行。
- 通过 `PROCESS_QUERY_LIMITED_INFORMATION` 和 `QueryFullProcessImageName` 读取高权限直播伴侣的精确可执行文件，事务日志保留真实版本路径；多版本共存时不猜测最高版本，无法确认路径则在写入前失败关闭。
- Preflight 在事务前检查四份 WBStore 配置的只读属性和目录创建/删除权限；唯一权限探针用随机临时名并立即删除，不改原始配置。
- 恢复协调器为永久前置备份、事务快照、停止 OBS/直播伴侣、素材准备、写入、启动、回读和提交分别记录中文失败上下文，后续不再只显示“拒绝访问”。

本机使用修复后的 Release Agent 通过正式本机协议对存档 `9b0fe748-b214-45e2-a8a3-b3d42d5750bd` 执行一次身份恢复。执行时间为 16:43:50–16:44:28；永久恢复前备份从 14 份增加到 15 份，操作结果为“恢复完成”；OBS 保持运行，直播伴侣回到恢复前的关闭状态，三类事务文件数和权限探针残留均为 0。脱敏记录为 `artifacts/windows-validation/2026-08-29/DESKTOP-2C8JIC2/restore-permission-fix/restore-permission-identity-001.json`。

本机的直播伴侣本轮不是高权限进程，因此真实 UAC 交互分支还需在报错的那台电脑安装修复版后选择“是”并完成恢复，才能记录为该电脑的真机通过。本轮不新增第二种实体采集卡或 20 轮证据，全局 `Verified` 仍为 0。Release 整仓构建为 0 警告、0 错误；Setup 8 项、Core 259 项、Agent 29 项，共 296 项测试通过；格式验证和 `git diff --check` 通过。NuGet 漏洞服务在本轮首次检查时 TLS 握手被远端提前终止，本次修改没有变更任何包引用；发布前仍必须在网络恢复后重跑并通过该检查。

GitHub Release `v0.1.19` 的 Windows 流水线 `33245353452` 随后完成整仓还原、296 项测试、格式检查、依赖漏洞检查、MSIX 签名、安装器签名及 Release 创建，全部为成功。公开 `LiveStudio-Setup.exe` 长度为 217858960 字节，SHA-256 为 `E84FAD5E486FE11A540274634FF09CA8144120BED51A33F4B799F64C471CD451`；回下载副本的 Authenticode 状态为 `Valid`，Signer 为 `CN=LiveStudio Internal`，证书指纹为 `4D42933F643E1E0B649513BCD10A15B485746E1D`，`--verify-only` 退出码为 0。对应 MSIX SHA-256 为 `5DD96B230AE1DDD7615B62EA30174C0C61A23D71CA6531C3511323456FB3D55A`。第二台电脑仍必须安装 0.1.19、在恢复时接受 UAC，并形成实际恢复结果；发布流水线成功不能替代该电脑的高权限分支真机证据。

## 43. 2026-08-29 直播伴侣逐字段回读等待优化

直播伴侣恢复原先在两条回读路径中无条件等待 28 秒或 24 秒，即使四份签名原生存储已经完整可读且不再变化，仍要等满固定时间。当前实现改为每 500 毫秒读取并解析签名定义声明的全部 JSON 存储，计算联合哈希；经过 6 秒最短观察期并连续稳定后立即进入正式逐字段回读。文件暂时不可读、JSON 处于半写入状态或继续变化时会重新计数，原有 28 秒与 24 秒最长安全门保持不变，超时仍判定恢复失败并触发回滚。

本机 OBS 32.2.2 与直播伴侣 12.9.2.470033184 联合恢复连续执行两次，耗时分别为 17.69 秒和 16.26 秒；第二次从直播伴侣原本运行的状态开始，恢复前永久备份成功创建，操作结果为“恢复完成”，直播伴侣恢复后继续运行，事务残留为 0。直播伴侣在操作完成后又改写了一次 `effectStore` 运行时缓存，因此额外等待 30 秒后使用正式恢复校验器重读存档中的全部签名可恢复字段，差异仍为 0；说明本次提前结束没有把运行时缓存写动误判成业务字段丢失。脱敏证据位于 `artifacts/windows-validation/2026-08-29/DESKTOP-2C8JIC2/livecompanion-12.9.2/readback-stability-optimization-001.json`。

新增自动化覆盖稳定后提前完成、半写 JSON 重试和持续无效 JSON 到期失败。Release 整仓构建为 0 警告、0 错误；Setup 8 项、Core 262 项、Agent 29 项，共 299 项全部通过；格式验证、`git diff --check` 和全部直接与传递 NuGet 包漏洞检查通过。本节只验证当前电脑和 12.9.2 组合，不把其他直播伴侣版本或第二台采集卡电脑提升为 `Verified`。

## 44. 2026-08-29 联合恢复运行时复用与五轮速度复验

继续检查发现，直播伴侣稳定存储轮询本身已经缩短，但 OBS 关闭状态下的一次联合恢复仍曾耗时 92.930 秒。阶段记录确认主要时间不在逐字段比较，而是 OBS 在 Preflight、永久恢复前备份和事务快照三个阶段被重复启动、等待插件加载并关闭。现场同时暴露三个独立问题：OBS 尚未建立 WebSocket 时抛出的 `HttpRequestException` 没有进入连接重试；永久前置备份直接调用非稳定捕获边界；多个 Agent 后台服务重复初始化本地索引时会把仍在执行的操作误标记为“Agent 在操作完成前退出”。OBS 32 的非正常退出提示框还会阻断无人值守连接，正常关闭若只发送一次窗口消息也可能在插件退出期间超时。

恢复协调器现在为每个应用在 Preflight 前取得一次运行时租约，并把应用恢复前的真实运行状态传给事务会话。OBS 原本关闭时只启动一次，在同一进程中完成 Preflight、永久备份、事务快照、写入和回读，事务最终提交或回滚后才恢复为关闭状态；原本运行时保持运行。OBS 连接等待同时覆盖 WebSocket 与 `HttpRequestException`，使用绝对期限和短间隔重试；仅关闭属于目标 OBS 进程且标题精确匹配的崩溃提示框，正常退出期间持续向该进程窗口发送关闭消息。永久前置备份改用稳定捕获边界。本地索引初始化增加进程内一次性门，后台服务再次调用不会改写正在执行的操作。自动化覆盖运行时状态传递与释放、连接异常归类和期限、启动参数与崩溃提示标题、稳定捕获边界，以及索引重复初始化不破坏运行操作。

本机使用 OBS 32.2.2、直播伴侣 12.9.2.470033184 和正式 Agent 协议，对同一存档连续执行 5 次完整联合事务恢复。五轮分别耗时 22.910、21.949、21.577、23.073、21.868 秒，平均 22.275 秒；相对修复前 92.930 秒的同状态恢复缩短约 76.0%。5 次均返回“恢复完成”，每次都先创建一份永久恢复前备份，存档数从 21 增至 26；严格逐字段回读一致，事务残留为 0，OBS 与直播伴侣在每轮结束后都恢复为原先的关闭状态。随后核对该诊断轮和五轮复验对应的 6 份 OBS 日志：6 份均包含正常关闭标记，非正常关闭标记为 0；操作结束后两个应用进程数均为 0。脱敏证据位于 `artifacts/windows-validation/2026-08-29/DESKTOP-2C8JIC2/livecompanion-12.9.2/optimized-readback-five-cycles-001.json`。

该结果证明当前电脑上关闭状态启动、永久备份、事务写入、严格回读和恢复原进程状态的五轮闭环，并解决了本轮可复现的重复启停性能问题。当前电脑仍未检测到天创恒达或美乐威实体采集卡，第二台物理采集卡电脑也没有接入本轮自动化，因此证据等级记录为 `Mapped`，正式 `Verified` 继续保持 0；不能把这五轮外推为所有 Windows、OBS 插件、直播伴侣版本和采集卡组合均已完成验证。

本节最终质量门：Release 整仓构建 0 警告、0 错误；LiveStudio.Setup.Tests 8 项、LiveStudio.Core.Tests 274 项、LiveStudio.Agent.Tests 31 项，共 313 项全部通过；格式验证和 `git diff --check` 通过，全部直接与传递 NuGet 包无已知漏洞。
