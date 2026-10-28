# 风扇管家（FanManager）—— UI 实现

PowerShell + WinForms 的图形界面：运行时编译 `app-cs.txt`（无新增二进制，杀软友好），
复用 `../scripts` 的同一套逻辑（模式切换 / EC 读写 / MSR 功耗墙）。

## 启动

- **首选**：双击桌面上的「风扇管家」VBS 启动器（由 `make-shortcut.ps1` 创建；
  或直接双击本目录 `启动风扇管家.vbs`）。自动静默提权，无弹窗。
- 命令行自测：`powershell -ExecutionPolicy Bypass -File FanManager.ps1 selftest`
  （结果写入 `../logs/app-selftest.txt`）

> 注意：不要用 .lnk 快捷方式指向 powershell 脚本——360 等杀软会清理此类快捷方式；
> VBS 启动器实测可长期保留。

## 文件

| 文件 | 作用 |
|---|---|
| FanManager.ps1 | 入口：自提权 → `Config.Init(仓库根)` → 运行时编译 → 显示窗口 |
| app-cs.txt | 全部 C# 源码（WinForms 界面 + EC/MSR/模式/曲线逻辑），由 Program.cs 生成 |
| Program.cs | 生成源（含 Main 入口，想编成 exe 也可，配合 app.manifest；但 exe 会被杀软隔离，不推荐） |
| 启动风扇管家.vbs | 桌面级启动器（自定位路径，静默提权） |
| make-shortcut.ps1 | 在桌面创建指向 VBS 的快捷方式（可能被杀软清理，非必需） |
| test-mutations.ps1 | 变更类自动化测试（曲线保存/恢复、28W 解锁/还原、切回静音验证） |
| screenshot-one.ps1 | 开发用：以无头模式截取指定标签页渲染图 + 控件值转储 |

## 界面功能

- **主面板**：四模式 + 恢复默认一键切换（当前模式绿色高亮）；EC/NBFC 双温度、风扇 %、
  EC 占空比、PL1 功耗墙、服务状态实时显示（3 秒刷新）；手动风扇滑条（温度 ≥80℃ 自动交还）
- **风扇曲线**：按模式可视化编辑每档触发温度/转速（回差自动 = 触发温度 - 7℃），
  保存写入 `../nbfc-configs/` 并同步到 NBFC 后立即生效；一键恢复默认曲线
- **工具**：智能自动开关（计划任务）、全局频率上限（1800-2800MHz，作用于全部 Z-fan 方案）、
  Intel DPTF 停止/启动（极限模式可选解锁项）、EC 全寄存器转储、打开日志目录
- **自测**：管理员/服务/驱动 MSR/EC/温度/配置文件/电源方案/功耗墙备份/计划任务 共 9 项

## 修改代码后重新生成 app-cs.txt

```powershell
# 在 ui 目录
$c = [IO.File]::ReadAllText("Program.cs")
$c = $c.Substring(0, $c.IndexOf("static class Program")) + $c.Substring($c.IndexOf("public static class Config"))
[IO.File]::WriteAllText("app-cs.txt", $c)
```

## 安全设计

- 手动风扇模式下每 3 秒检查温度，≥80℃ 自动交还 NBFC 自动曲线并记录日志
- 所有曲线档自带过热兜底（CriticalTemperature 76-80℃ 强制 100%）
- 功耗墙改写前自动备份原始值：应用写 `../data/msr-610-original.txt`，
  脚本（mode-switch）写 `../logs/msr-610-original.txt`——切出极限模式各自自动还原
- 所有操作写日志到 `../logs/app.log`；运行时数据位于仓库根的 `data/`、`logs/`（已 gitignore）

## 已知渲染说明

在**锁屏/远程会话**下用 `screenshot-one.ps1` 抓图时，编辑框（NumericUpDown/ComboBox）文字
可能显示为空——这是 DrawToBitmap 在未解锁会话的渲染伪影，控件实际值正确（截图同时导出
`../logs/app-window-values.txt` 可验证）。真实桌面显示正常。
