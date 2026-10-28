# 风扇管家（FanManager）说明

## 启动
- 桌面快捷方式「风扇管家」，或双击本目录 `FanManager.ps1`（自动静默提权，无弹窗）
- 命令行模式：`powershell -File FanManager.ps1 selftest`（自测→ `..\logs\app-selftest.txt`）

## 文件
| 文件 | 作用 |
|---|---|
| FanManager.ps1 | 入口：自提权 + 运行时编译 C# |
| app-cs.txt | 全部 C# 源码（WinForms 界面 + EC/MSR/模式/曲线逻辑） |
| Program.cs | app-cs.txt 的生成源（改这里 → 重新生成，见下） |
| make-shortcut.ps1 | 重建桌面快捷方式 |
| test-mutations.ps1 | 变更类功能的自动化测试（曲线保存/恢复、28W 解锁/还原） |

## 修改代码后重新生成 app-cs.txt
`app-cs.txt` 由 `Program.cs` 去掉 Main 入口生成：

```powershell
# 在 ui 目录
$c = [IO.File]::ReadAllText("Program.cs")
$c = $c.Substring(0, $c.IndexOf("static class Program")) + $c.Substring($c.IndexOf("public static class Config"))
[IO.File]::WriteAllText("app-cs.txt", $c)
```

## 安全设计
- 手动风扇模式下每 3 秒检查温度，≥80℃ 自动交还 NBFC 自动曲线
- 所有曲线档自带过热兜底（CriticalTemperature 76-80℃ 强制 100%）
- 功耗墙改写前自动备份原始值到 `..\ec-backup\msr-610-original.txt`，切出极限模式自动还原
- 单实例互斥；所有操作写日志到 `..\logs\app.log`

## 已知渲染说明
在**锁屏/远程会话**下用 `--screenshot` 抓图时，编辑框（NumericUpDown/ComboBox）的文字可能
显示为空——这是 DrawToBitmap 在未解锁会话的渲染伪影，控件实际值正确（截图同时导出
`app-window-values.txt` 可验证）。真实桌面显示正常。
