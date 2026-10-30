# 风扇管家（FanManager）—— UI 实现

PowerShell + WinForms 的图形界面：运行时编译 `app-cs.txt`（无新增二进制，杀软友好），
复用 `../scripts` 的同一套逻辑（模式切换 / EC 读写 / MSR 功耗墙）。

## 启动

- **首选**：双击桌面上的「风扇管家」VBS 启动器（由 `make-shortcut.ps1` 创建；
  或直接双击本目录 `启动风扇管家.vbs`）。自动静默提权，无弹窗。
- 双击后约 1 秒会出现蓝色启动闪现窗（首次需运行时编译界面，约 1 秒），编译完成后主窗口出现。
- 命令行自测：`powershell -ExecutionPolicy Bypass -File FanManager.ps1 selftest`
  （结果写入 `../logs/app-selftest.txt`）

> 注意：不要用 .lnk 快捷方式指向 powershell 脚本——360 等杀软会清理此类快捷方式；
> VBS 启动器实测可长期保留。

## 文件

| 文件 | 作用 |
|---|---|
| FanManager.ps1 | 入口：自提权 → 启动闪现窗 → 运行时编译 → 显示窗口（编译耗时写入 app.log） |
| app-cs.txt | 全部 C# 源码（WinForms 界面 + EC/MSR/模式/曲线逻辑），由 Program.cs 生成 |
| Program.cs | 生成源（含 Main 入口，想编成 exe 也可，配合 app.manifest；但 exe 会被杀软隔离，不推荐） |
| gen-appcs.ps1 | 修改 Program.cs 后一键重新生成 app-cs.txt |
| app.ico | 应用图标（16~256 共 9 档），窗口/任务栏、托盘、桌面快捷方式共用 |
| 启动风扇管家.vbs | 桌面级启动器（自定位路径，静默提权） |
| make-shortcut.ps1 | 在桌面创建指向 VBS 的快捷方式（可能被杀软清理，非必需） |
| test-logic.ps1 | 只读逻辑回归（70 项）：入口存在性、曲线映射、超时、EC 转储解析、转速分档、Prefs 往返等，不碰 EC/MSR |
| test-e2e.ps1 | 端到端冒烟：真实提权启动 GUI → 等状态引擎日志上报 → 关闭（抓「启动即崩」类回归） |
| test-mutations.ps1 | 变更类自动化测试（曲线保存/恢复、28W 解锁/还原、切回静音验证） |
| screenshot-one.ps1 | 开发用：以无头模式截取指定标签页渲染图 |

## 界面功能

- **主面板**：四模式 + 恢复默认一键切换（当前模式绿色高亮，悬浮说明写明风扇行为/电源参数）；
  机型与适配状态、EC/NBFC 双温度、风扇 %、估算转速（≈RPM + 停转/低速/中速/高速）、EC 占空比
  （含 0x95 回读）、PL1 功耗墙、服务状态、智能自动预测显示实时更新（后台引擎 5 秒轮询、
  UI 每秒绘制快照，UI 线程零阻塞）；手动风扇滑条（温度 ≥80℃ 自动交还）
- **活动日志**（所有标签页底部常驻）：一行式「状态： xxx」+ 每步进度（模式切换 1/4→4/4、
  曲线保存 1/3→3/3），时间戳滚动日志，忙碌时点击有「请稍候」反馈；镜像写 `../logs/app.log`
- **监控**：实时功耗（RAPL）/ 功耗墙 / CPU 实时频率（MSR 0x198），最近 10 分钟温度/风扇/频率
  走势（每 5 秒采样），10 分钟统计摘要，历史导出 CSV
- **风扇曲线**：按模式可视化编辑，档位可增删（最多 8 档）、折线预览并标出当前温度；
  回差自动 = 触发温度 - 7℃；保存写入 `../nbfc-configs/` 并同步到 NBFC 后立即生效；
  切换模式后曲线页自动跟随
- **工具**：智能自动开关（带预测进度）、全局频率上限、Intel DPTF 停止/启动、
  风扇转速估算校准（MaxFanRpm）、EC 全寄存器转储、一键诊断包、打开日志目录
- **自测**：机型适配/管理员/服务/驱动 MSR/EC/占空比回读/温度/配置文件/电源方案/功耗墙备份/计划任务
- **托盘与热键**：关闭窗口最小化到托盘（托盘文字实时显示温度/风扇%/模式），托盘菜单直接切模式；
  `Ctrl+Alt+1~4` 全局热键，窗口内 `1-4` 切模式、`5` 恢复默认
- **横幅与记忆**：未提权 / NBFC 服务未运行时顶部黄色横幅（可一键重新提权）；
  窗口位置/大小/最大化/日志面板高度自动记忆（`../data/ui-prefs.txt`）

## 架构要点（改代码前读）

- **UI 线程零阻塞**：所有轮询（nbfc status、ec-probe dump、powercfg、schtasks、MSR）在
  `StatusEngine` 后台线程执行并产出 `StatusSnapshot`；主窗体 1 秒定时器只把快照画上控件。
  给 UI 加新状态一律走「引擎采集 → 快照字段 → PaintStatus 绘制」，不要在定时器里跑子进程。
- **操作反馈**：任何后台操作用 `UiLog.Action/Step/Done` 汇报进度（自动进日志面板 + app.log）；
  会写状态的操作用 `TryBeginBusy()/EndBusy()` 互斥。
- **EC 访问串行化**：进程内所有 ec-probe 调用经 `Config.EcLock`，与状态引擎互斥。
- **手动风扇守护**：≥80℃ 自动交还在引擎线程执行（`StatusEngine.ManualEngaged/GuardTriggered`）。

## 修改代码后重新生成 app-cs.txt

```powershell
# 在 ui 目录
powershell -ExecutionPolicy Bypass -File gen-appcs.ps1
```

## 测试

```powershell
powershell -ExecutionPolicy Bypass -File test-logic.ps1   # 70 项只读回归，不需要提权
powershell -ExecutionPolicy Bypass -File test-e2e.ps1     # 端到端启动冒烟（自动提权）
powershell -ExecutionPolicy Bypass -File test-mutations.ps1  # 变更类（会真实切换模式，测完自动还原）
```

## 安全设计

- 手动风扇模式下状态引擎每 5 秒检查温度，≥80℃ 自动交还 NBFC 自动曲线并记录日志
- 所有曲线档自带过热兜底（CriticalTemperature 76-80℃ 强制 100%）
- 模式切换后回读电源方案、NBFC 配置名、EC 模式，任一项不符自动回滚到切换前的模式
- 机型适配按主板/整机型号判断：非 TM1901 / RedmiBook 进入只读模式，模式按钮、手动风扇、
  曲线写入全部禁用，只保留状态显示与诊断
- 功耗墙改写前自动备份原始值到 `../data/msr-610-original.txt`——脚本（mode-switch）与 UI 共用
  同一份备份，任一侧切出极限模式都会自动还原
- 所有操作写日志到 `../logs/app.log`；启动失败/致命异常写 `../logs/app-error.txt`；
  运行时数据位于仓库根的 `data/`、`logs/`（已 gitignore）

## 已知渲染说明

在**锁屏/远程会话**下用 `screenshot-one.ps1` 抓图时，编辑框（NumericUpDown/ComboBox）文字
可能显示为空——这是 DrawToBitmap 在未解锁会话的渲染伪影，控件实际值正确。真实桌面显示正常。

> 监控页「实时功耗」在**刚启动的几十秒内**显示「读取失败」是正常现象:RAPL 功耗是
> 能量计数器差分,需要两个采样点(约 5 秒)预热;之后持续显示。
