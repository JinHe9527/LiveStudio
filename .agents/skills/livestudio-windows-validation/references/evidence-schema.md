# Windows 真机证据格式

每一项单变量实验创建一条记录。文件名和 `experimentId` 使用同一个 ASCII 标识，例如 `beauty-smooth-strength-001`。

```json
{
  "experimentId": "beauty-smooth-strength-001",
  "capturedAt": "2026-08-23T12:00:00+08:00",
  "machine": {
    "machineAlias": "win-test-a",
    "windowsVersion": "",
    "captureDevice": "",
    "captureDeviceHardwareId": "",
    "driverVersion": ""
  },
  "applications": {
    "liveStudioCommit": "",
    "obsVersion": "",
    "liveCompanionVersion": ""
  },
  "change": {
    "application": "LiveCompanion",
    "uiPath": "美颜/美肤/磨皮",
    "beforeDisplayValue": "45",
    "afterDisplayValue": "46",
    "onlyOneBusinessFieldChanged": true
  },
  "observations": {
    "nativeExportChanged": true,
    "nativePaths": [],
    "storageKinds": [],
    "sensitivePaths": [],
    "writeTiming": "",
    "readBackMatched": false,
    "rollbackMatched": false
  },
  "artifacts": {
    "beforeReport": "",
    "afterReport": "",
    "differenceReport": "",
    "screenshots": []
  },
  "result": "EvidenceOnly",
  "notes": ""
}
```

`result` 只允许：

- `Invalid`：不止一个业务字段变化、版本信息缺失或实验过程不完整。
- `EvidenceOnly`：已发现差异，但尚未证明可安全读写和回读。
- `Mapped`：原生位置、类型、读写、回读和敏感字段边界均已确认。
- `Verified`：所在版本组合已完成规定的 20 次循环和故障回滚验收。

证据目录建议放在测试机本地的 `artifacts/windows-validation/<日期>/<机器>/<直播伴侣版本>/`。仓库会忽略 `artifacts/`；只把完成脱敏、经人工核对的 fixture 放进测试目录。
