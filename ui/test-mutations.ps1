$ErrorActionPreference = 'Stop'
$cs = [System.IO.File]::ReadAllText("$PSScriptRoot\app-cs.txt")
Add-Type -TypeDefinition $cs -ReferencedAssemblies @('System.Windows.Forms','System.Drawing','System.ServiceProcess','System.Core','System.Xml','System')
[FanManager.Config]::Init((Split-Path $PSScriptRoot -Parent))

"== 1. curve save test: set base speed 0 -> 25 =="
$rows = @([int[]]@(0,25), [int[]]@(58,35), [int[]]@(64,55), [int[]]@(70,75), [int[]]@(74,100))
[FanManager.Curves]::Save(0, $rows)
$check = Select-String -Path "C:\Program Files (x86)\NoteBook FanControl\Configs\RedmiBook 14 2020 Silent.xml" -Pattern "FanSpeed>25<"
"saved 25 found: " + ($check -ne $null)

"== 2. curve reset test =="
[FanManager.Curves]::ResetDefaults(0)
$check2 = Select-String -Path "C:\Program Files (x86)\NoteBook FanControl\Configs\RedmiBook 14 2020 Silent.xml" -Pattern "UpThreshold>58<"
"reset 58 found: " + ($check2 -ne $null)

"== 3. performance mode via app (native MSR) =="
[FanManager.Modes]::Apply("performance") | Out-Host
Start-Sleep -Seconds 2
"PL1 after perf: " + [FanManager.Msr]::Pl1W() + "W"

"== 4. back to quiet =="
[FanManager.Modes]::Apply("quiet") | Out-Host
Start-Sleep -Seconds 2
"PL1 after quiet: " + [FanManager.Msr]::Pl1W() + "W"
"active scheme: " + [FanManager.Config]::ActivePlanGuid()
"EC mode: 0x{0:X2}  duty: 0x{1:X2}" -f [FanManager.Config]::EcRead(147), [FanManager.Config]::EcRead(148)
"ALL MUTATION TESTS DONE"
