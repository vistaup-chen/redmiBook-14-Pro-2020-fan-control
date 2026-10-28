# scripts —— 脚本实现

全部功能以命令行/双击 .bat 提供；GUI（见 `../ui`）只是同一套逻辑的图形外壳。

## 日常使用（双击即用）

| 脚本 | 作用 |
|---|---|
| `静音模式.bat` | 日常默认：≤58℃ 风扇 0 转 + 60% 性能上限 |
| `均衡模式.bat` | 全性能 + 睿频，风扇 ≤50℃ 停转 |
| `极限性能.bat` | PL1 25W→28W + EPP 0 + 激进睿频 + 风扇领跑（退出自动还原功耗墙） |
| `勿扰全静.bat` | 最安静：≤60℃ 0 转，50% 性能上限 |
| `恢复默认.bat` | 停止 NBFC 控制（EC 回自动）、还原功耗墙、回到系统原电源方案 |
| `状态查看.bat` | 当前模式/温度/风扇/EC 寄存器/智能自动状态 |
| `紧急恢复风扇.bat` | 风扇异常时：强制写 EC 0x93=0x04 交还固件自动控制 |
| `智能自动-开/关.bat` | 计划任务 ZFanAutoMode：温度驱动自动在静音↔均衡间切换（默认关） |

## 目录

- `mode-switch.ps1` —— 模式切换核心（电源方案 + NBFC 配置 + 功耗墙 MSR 联动）
- `status.ps1` / `emergency-fan-restore.ps1` —— 状态查询 / 紧急恢复
- `auto-mode.ps1` —— 智能自动观察器（由计划任务每分钟调用）
- `lib/msr-lib.ps1` —— MSR 读写（复用 NBFC 自带 OpenHardwareMonitorLib 的 Ring0 类，免杀软拦截）
- `lib/run-elevated.ps1` —— 静默提权封装（UAC 静默机器上无弹窗）
- `lib/setup-plans.ps1` —— 创建 4 个 Z-fan 电源方案（AC/DC 同值）
- `lib/gen-configs.ps1` —— 重新生成 4 份 NBFC 配置（改曲线模板用）
- `lib/install-auto.ps1` —— 创建/启用/禁用智能自动计划任务
- `lib/load.ps1` / `lib/sensors.ps1` —— 受控负载发生器 / 传感器采样（标定与验证用）
- `../nbfc-configs/` —— 4 份 NBFC 配置（Silent/Balanced/Performance/DoNotDisturb）

## 首次部署

1. 安装 NBFC 并确认 `NbfcService` 服务存在；
2. 复制 `../nbfc-configs/*.xml` 到 `C:\Program Files (x86)\NoteBook FanControl\Configs\`；
3. 管理员运行 `lib/setup-plans.ps1`；
4. **先只读验证**：`nbfc config -s "RedmiBook 14 2020 Silent"` → `nbfc start -r` →
   `nbfc status` 确认温度读数合理 → `nbfc start` 转正式控制；
5. 出问题时：`紧急恢复风扇.bat`（写 EC 0x93=0x04 交还固件）。

> EC 寄存器映射与验证方法见 [../docs/技术原理.md](../docs/技术原理.md)；
> 若你的机器不是 TM1901，务必先按文档完成寄存器验证再启用。
