# 抖音直播伴侣配置探测流程

探测必须在隔离的 Windows 测试账号上执行，不能使用真实直播账号。每轮实验只修改一个参数。

## 实验步骤

1. 关闭直播伴侣，记录进程已退出。
2. 对候选 AppData 和 Registry 位置执行 `capture`，命名为 `before-参数名`。
3. 启动直播伴侣，只修改一个参数并正常退出。
4. 对相同位置再次执行 `capture`，命名为 `after-参数名`。
5. 使用 `diff` 生成差异报告。
6. 对差异文件确认存储类型、字段位置、写入时机和是否包含登录凭据。
7. 使用脱敏测试账号连续执行 20 次读取、修改、恢复和回读。

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
```

探测报告只保存路径、文件哈希、Registry 值哈希和进程元数据，不复制原始配置内容。原始样本必须在测试机本地脱敏后才能加入测试 fixture。
