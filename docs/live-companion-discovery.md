# 抖音直播伴侣配置探测流程

探测必须在隔离的 Windows 测试账号上执行，不能使用真实直播账号。每轮实验只修改一个参数。

如果直播伴侣提供原生整套导入导出，必须同时完成“原生导出包差异实验”。原生 ZIP 只有在设备、视频模式、美颜、滤镜、曲线和素材全部逐项验证后，才能成为正式恢复事务的一部分；不能因为能成功导入 ZIP 就假定其内容完整。

## 实验步骤

1. 关闭直播伴侣，记录进程已退出。
2. 对候选 AppData 和 Registry 位置执行 `capture`，命名为 `before-参数名`。
3. 启动直播伴侣，只修改一个参数并正常退出。
4. 对相同位置再次执行 `capture`，命名为 `after-参数名`。
5. 使用 `diff` 生成差异报告。
6. 对差异文件确认存储类型、字段位置、写入时机和是否包含登录凭据。
7. 使用脱敏测试账号连续执行 20 次读取、修改、恢复和回读。

## 原生导出包差异实验

1. 使用隔离测试账号，在直播伴侣中导出基线 ZIP。
2. 只修改一个参数，例如“磨皮 45 → 46”，再导出第二份 ZIP。
3. 分别使用 `inspect-export` 生成结构报告。
4. 使用 `diff-export` 生成逐字段差异报告。
5. 差异报告必须只出现该参数对应字段；如果没有差异，说明原生导出未包含该参数。
6. 如果结构报告出现敏感路径，原生 ZIP 不得进入 `.lscfg` 或上传云端。

美颜必须逐项覆盖：

- 总开关、预设、强度、磨皮、美白、红润、肤色和清晰度。
- 瘦脸、颧骨、下颌、下巴、额头、眼睛、鼻子、嘴型等塑形参数。
- 美妆类型、素材 ID、颜色、强度、透明度和资源文件。
- Master/R/G/B 曲线、每个控制点的 X/Y、数组顺序和插值方式。
- 每个美颜项的启用状态、默认值和当前值。

报告会根据文件头与目录结构标记 `JSON`、`SQLite`、`LevelDB` 或普通 `File`。它只用于缩小真实存储位置，不能直接生成正式适配器。正式适配定义必须人工确认每个字段的路径、类型、写入时机与回读行为，并用签名密钥签名后放入 `%LocalAppData%\LiveStudio\Adapters`；Agent 没有匹配的签名定义时只允许读取，不允许写入。

必须逐项建立实验矩阵：

- 设备选择、分辨率、FPS、像素格式、色彩空间、色彩范围。
- 每一种视频滤镜的类型、启用状态、顺序、全部参数和素材路径。
- 应用运行/关闭、开播/空闲、正常退出/强制结束时的落盘差异。
- File、Registry、SQLite、LevelDB，以及进程存活期间才出现的 IPC 行为。

每项差异需要保存 before、after 和 diff 三份报告。不同参数同时改变的报告不得用于确认字段映射。

示例：

```powershell
dotnet run --project tools/LiveStudio.Discovery.Windows -- capture `
  --name before-filter-strength `
  --output artifacts/discovery/before.json `
  --root "$env:APPDATA\直播伴侣" `
  --process livecompanion

dotnet run --project tools/LiveStudio.Discovery.Windows -- diff `
  --before artifacts/discovery/before.json `
  --after artifacts/discovery/after.json `
  --output artifacts/discovery/filter-strength.diff.json

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

探测报告只保存路径、类型和哈希，不复制原始配置值。原生 ZIP 可能包含账号或登录数据，未完成敏感路径检查前不得上传。原始样本必须在测试机本地脱敏后才能加入测试 fixture。
