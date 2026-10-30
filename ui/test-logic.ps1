# test-logic.ps1 - non-mutating logic checks (no EC/MSR writes, no mode switches).
# Usage: powershell -ExecutionPolicy Bypass -File test-logic.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$script:pass = 0
$script:fail = 0
function Check($name, $cond) {
    if ($cond) { $script:pass++; "PASS  $name" } else { $script:fail++; "FAIL  $name" }
}

"== 1. app-cs.txt in sync with Program.cs =="
$src = [IO.File]::ReadAllText("$PSScriptRoot\Program.cs")
$gen = $src.Substring(0, $src.IndexOf("static class Program")) + $src.Substring($src.IndexOf("public static class Config"))
Check "app-cs.txt == generated from Program.cs" ($gen -eq [IO.File]::ReadAllText("$PSScriptRoot\app-cs.txt"))

"== 2. compile + entry points =="
Add-Type -TypeDefinition ([IO.File]::ReadAllText("$PSScriptRoot\app-cs.txt")) `
    -ReferencedAssemblies @('System.Windows.Forms','System.Drawing','System.ServiceProcess','System.Core','System.Xml','System')
[FanManager.App]::Headless = $true      # no tray icon / hotkeys / timer while testing
[FanManager.Config]::Init($root)
# keep test noise out of the real logs\app.log (UiLog mirrors every entry to file)
$script:testLogDir = Join-Path $env:TEMP ('fmlogic-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:testLogDir | Out-Null
[FanManager.Config]::LogDir = $script:testLogDir
Check "App.Run exists" ($null -ne [FanManager.App].GetMethod("Run"))
Check "App.RunScreenshot exists" ($null -ne [FanManager.App].GetMethod("RunScreenshot"))
Check "Extensions.InvalidateTaskState exists" ($null -ne [FanManager.Extensions].GetMethod("InvalidateTaskState"))
Check "Modes.Verify exists" ($null -ne [FanManager.Modes].GetMethod("Verify"))
Check "Diagnostics.Export exists" ($null -ne [FanManager.Diagnostics].GetMethod("Export"))

"== 3. machine profile =="
Check "profile model detected" ([FanManager.Profile]::Model -ne "?" -and [FanManager.Profile]::Model.Length -gt 3)
Check "supported flag matches model string" ([FanManager.Profile]::Supported -eq ([bool]([FanManager.Profile]::Model -match 'TM1901|RedmiBook')))

"== 4. curve tab index maps to the same mode as Modes.Meta =="
$form = New-Object FanManager.MainForm
try {
    $combo = $form.GetType().GetField('cmbCurveMode', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($form)
    Check "combo item count == mode count" ($combo.Items.Count -eq [FanManager.Modes]::Meta.Length)
    for ($i = 0; $i -lt $combo.Items.Count; $i++) {
        Check ("combo[$i] matches Modes.Names[$i]") ($combo.Items[$i] -eq [FanManager.Modes]::Names[$i])
    }
} finally { $form.Dispose() }

"== 5. Curves.Load(i) returns the shipped default curve for mode i =="
# Hard-coded expectations on purpose: Load() silently falls back to Defaults on any error and the
# committed XMLs equal those Defaults, so comparing Load() with the same XML could never fail.
# Curves are the v2 defaults from scripts\lib\gen-configs.ps1; update this table if defaults change.
$expect = @(
    @{ rows = 5; base = 0;  firstUp = 58 },
    @{ rows = 5; base = 0;  firstUp = 50 },
    @{ rows = 4; base = 40; firstUp = 44 },
    @{ rows = 4; base = 0;  firstUp = 60 }
)
for ($i = 0; $i -lt $expect.Count; $i++) {
    $steps = [FanManager.Curves]::Load($i)
    Check ("Load($i).rows == $($expect[$i].rows)") ($steps.Length -eq $expect[$i].rows)
    Check ("Load($i) base $($steps[0][2]) == $($expect[$i].base)") ($steps[0][2] -eq $expect[$i].base)
    Check ("Load($i) first up $($steps[1][0]) == $($expect[$i].firstUp)") ($steps[1][0] -eq $expect[$i].firstUp)
}

"== 6. Config.Run: missing exe / timeout / normal =="
$r = [FanManager.Config]::Run("C:\nope\nope.exe", "", 2000)
Check "missing exe returns ERR" ($r -like "ERR:*")
$sw = [Diagnostics.Stopwatch]::StartNew()
$null = [FanManager.Config]::Run("powershell.exe", "-NoProfile -Command `"Start-Sleep -Seconds 30`"", 1500)
$sw.Stop()
Check ("timeout honoured ({0:N1}s < 10s)" -f $sw.Elapsed.TotalSeconds) ($sw.Elapsed.TotalSeconds -lt 10)
Check "powercfg still readable" ([FanManager.Config]::ActivePlanGuid() -match '^[0-9a-f-]{36}$')

"== 7. scheduled task state cache =="
$a = [FanManager.Extensions]::TaskSchedulerFallback()
$sw2 = [Diagnostics.Stopwatch]::StartNew()
$b = [FanManager.Extensions]::TaskSchedulerFallback()
$sw2.Stop()
Check "cached call is fast (<300ms)" ($sw2.Elapsed.TotalMilliseconds -lt 300)
Check "cached value stable" ($a -eq $b)

"== 8. launcher files: plain ASCII, no control characters =="
# A previous generator turned "\r", "\a", "\e" inside paths into real control bytes, which cmd then
# collapsed, breaking every .bat. Nothing else in the repo can catch that class of damage.
$bad = @()
Get-ChildItem (Join-Path $root 'scripts') -Filter *.bat | ForEach-Object {
    $bytes = [IO.File]::ReadAllBytes($_.FullName)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $b = $bytes[$i]
        if (($b -lt 0x20 -and $b -ne 0x09 -and $b -ne 0x0A -and $b -ne 0x0D) -or $b -gt 0x7F) {
            $bad += ("{0}@{1}=0x{2}" -f $_.Name, $i, $b.ToString('X2'))
            break
        }
    }
}
Check ("{0} .bat launchers clean{1}" -f (Get-ChildItem (Join-Path $root 'scripts') -Filter *.bat).Count, $(if ($bad) { " (bad: " + ($bad -join ', ') + ")" } else { "" })) ($bad.Count -eq 0)
foreach ($f in @('FanManager.ps1', 'make-shortcut.ps1', 'test-logic.ps1', 'gen-appcs.ps1')) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $f))
    Check "$f is pure ASCII" (-not ($bytes | Where-Object { $_ -gt 0x7F }))
}
$vbsName = -join ([char]0x542F, [char]0x52A8, [char]0x98CE, [char]0x6247, [char]0x7BA1, [char]0x5BB6)
Check "VBS launcher name still resolves" (Test-Path (Join-Path $PSScriptRoot ($vbsName + '.vbs')))

"== 9. page structure =="
# The Tools tab was once built into a FlowLayoutPanel and then never attached to its TabPage,
# so it rendered empty. Match pages by content instead of index so tab order can change.
$form2 = New-Object FanManager.MainForm
try {
    $tabs = $form2.GetType().GetField('tabs', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($form2)
    Check ("tab count {0} == 5" -f $tabs.TabPages.Count) ($tabs.TabPages.Count -eq 5)
    foreach ($tp in $tabs.TabPages) {
        Check ("tab '" + $tp.Text + "' has controls") ($tp.Controls.Count -gt 0)
    }
    $mon = @($tabs.TabPages | Where-Object { @($_.Controls | Where-Object { $_ -is [FanManager.TrendChart] }).Count -gt 0 })
    Check "monitor page has trend chart" ($mon.Count -eq 1)
    $curve = @($tabs.TabPages | Where-Object { @($_.Controls | Where-Object { $_ -is [FanManager.CurvePreview] }).Count -gt 0 })
    Check "curve page has preview" ($curve.Count -eq 1)
    $tool = @($tabs.TabPages | Where-Object {
        $_.Controls.Count -eq 1 -and $_.Controls[0].Controls.Count -eq 5 -and
        $_.Controls[0].Controls[0] -is [System.Windows.Forms.GroupBox]
    })
    Check "tools page keeps 5 sections" ($tool.Count -eq 1)
} finally { $form2.Dispose() }

"== 10. read-only gating follows the profile =="
$form3 = New-Object FanManager.MainForm
try {
    $mb = $form3.GetType().GetField('modeButtons', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($form3)
    $enabled = @($mb | Where-Object { $_.Enabled }).Count
    if ([FanManager.Profile]::Supported) { Check "mode buttons enabled on supported model" ($enabled -eq 4) }
    else { Check "mode buttons disabled on unsupported model" ($enabled -eq 0) }
    $flags = [Reflection.BindingFlags]'NonPublic,Instance'
    Check "activity log panel exists" ($null -ne $form3.GetType().GetField('txtActivity', $flags).GetValue($form3))
    Check "current-action label exists" ($null -ne $form3.GetType().GetField('lblNow', $flags).GetValue($form3))
    Check "activity log initially empty" ($form3.GetType().GetField('txtActivity', $flags).GetValue($form3).TextLength -eq 0)
} finally { $form3.Dispose() }

"== 11. telemetry =="
[FanManager.Telemetry]::Clear()
[FanManager.Telemetry]::Add(50, 35, 12.5)
[FanManager.Telemetry]::Add(52, 40, 13.0)
Check "history records samples" ([FanManager.Telemetry]::Copy().Count -eq 2)
$w = [FanManager.Telemetry]::PackageWatts()
Check "PackageWatts returns a number without throwing" (-not [double]::IsNaN($w))
[FanManager.Telemetry]::Clear()

"== 12. status engine =="
Check "StatusEngine.Latest starts unpainted" ([FanManager.StatusEngine]::Latest.Stamp -eq [datetime]::MinValue)
[FanManager.StatusEngine]::Start(); [FanManager.StatusEngine]::Start()   # must be idempotent (Stop -> would abort)
Check "StatusEngine.Start called twice without throwing" $true
Check "UiLog.Action exists" ($null -ne [FanManager.UiLog].GetMethod("Action"))
Check "UiLog.Recent returns array" ([FanManager.UiLog]::Recent() -is [array])
[FanManager.UiLog]::Log("test-logic smoke entry")
Check "UiLog records entries" ([FanManager.UiLog]::Recent().Count -ge 1)

"== 13. fan RPM estimate & EC dump parser =="
# Chinese expected-values are built from code points: this file is parsed as ANSI/GBK by
# powershell.exe (no BOM), and a UTF-8 multibyte run right before a quote would swallow it.
$stStop = [string][char]0x505C + [char]0x8F6C   # stopped
$stLow  = [string][char]0x4F4E + [char]0x901F   # low
$stMid  = [string][char]0x4E2D + [char]0x901F   # medium
$stHigh = [string][char]0x9AD8 + [char]0x901F   # high
$stUnk  = [string][char]0x672A + [char]0x77E5   # unknown
Check "0% duty -> stopped" ([FanManager.StatusEngine]::FanStateOf(0.0, 0xC8) -eq $stStop)
Check "35% -> low" ([FanManager.StatusEngine]::FanStateOf(35, -1) -eq $stLow)
Check "55% -> medium" ([FanManager.StatusEngine]::FanStateOf(55, -1) -eq $stMid)
Check "95% -> high" ([FanManager.StatusEngine]::FanStateOf(95, -1) -eq $stHigh)
Check "no data -> unknown" ([FanManager.StatusEngine]::FanStateOf(-1, -1) -eq $stUnk)
Check "duty band fallback (0xA1 -> medium)" ([FanManager.StatusEngine]::FanStateOf(-1, 0xA1) -eq $stMid)
Check "duty band fallback (0xB0 -> low)" ([FanManager.StatusEngine]::FanStateOf(-1, 0xB0) -eq $stLow)
$mini = "   | 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F`n" +
        "---|------------------------------------------------`n" +
        "90 | 00 01 00 04 86 88 00 00 00 81 00 00 00 00 02 08`n" +
        "A0 | 86 80 80 80 80 80 80 80 3C 2F 2C 3C 1F 00 00 09`n"
$ec = [FanManager.StatusEngine]::ParseEcDump($mini)
Check "dump parser: returns 256 bytes" ($ec -ne $null -and $ec.Length -eq 256)
Check "dump parser: mode 0x93=0x04" ($ec[0x93] -eq 4)
Check "dump parser: duty 0x94=0x86" ($ec[0x94] -eq 0x86)
Check "dump parser: feedback 0x95=0x88" ($ec[0x95] -eq 0x88)
Check "dump parser: temp 0xA8=0x3C" ($ec[0xA8] -eq 0x3C)
Check "dump parser: garbage returns null" ($null -eq [FanManager.StatusEngine]::ParseEcDump("hello world"))

$origRpm = [FanManager.Prefs]::MaxFanRpm
[FanManager.Prefs]::MaxFanRpm = 8800
[FanManager.Prefs]::Save()
[FanManager.Prefs]::MaxFanRpm = 7000
[FanManager.Prefs]::Load()
Check "prefs roundtrip via file" ([FanManager.Prefs]::MaxFanRpm -eq 8800)
[FanManager.Prefs]::MaxFanRpm = $origRpm
[FanManager.Prefs]::Save()

"== 14. busy guard (mode-switch mutual exclusion) =="
$formT = New-Object FanManager.MainForm
try {
    $flags2 = [Reflection.BindingFlags]'NonPublic,Instance'
    $begin = $formT.GetType().GetMethod('TryBeginBusy', $flags2)
    $end = $formT.GetType().GetMethod('EndBusy', $flags2)
    Check "busy guard: first TryBeginBusy true" ($begin.Invoke($formT, $null) -eq $true)
    Check "busy guard: second TryBeginBusy false" ($begin.Invoke($formT, $null) -eq $false)
    $end.Invoke($formT, $null)
    Check "busy guard: usable again after EndBusy" ($begin.Invoke($formT, $null) -eq $true)
    $end.Invoke($formT, $null)
    $mb2 = $formT.GetType().GetField('modeButtons', $flags2).GetValue($formT)
    if ([FanManager.Profile]::Supported) { Check "busy guard: EndBusy restores buttons" (@($mb2 | Where-Object { $_.Enabled }).Count -eq 4) }
} finally { $formT.Dispose() }

"== 15. UiLog capacity trim =="
for ($i = 0; $i -lt 700; $i++) { [FanManager.UiLog]::Log("trim-test $i") }
Check "UiLog trims to <= 600 entries" ([FanManager.UiLog]::Recent().Count -le 600)
Check "UiLog still records after trim" ([FanManager.UiLog]::Recent()[-1] -match 'trim-test 699')

"== 16. warning banner grows with wrapped text =="
# Regression: the banner used to be a fixed-height flow panel, so a long NBFC message pushed the
# buttons to a second row that the TabControl covered. Height must follow the wrapped label.
$formB = New-Object FanManager.MainForm
try {
    $flagsB = [Reflection.BindingFlags]'NonPublic,Instance'
    $showB = $formB.GetType().GetMethod('ShowBanner', $flagsB)
    $showB.Invoke($formB, @(('x' * 200), $false)) | Out-Null
    $formB.Show()
    [System.Windows.Forms.Application]::DoEvents()
    $bannerB = $formB.GetType().GetField('banner', $flagsB).GetValue($formB)
    $lblB = $formB.GetType().GetField('lblBanner', $flagsB).GetValue($formB)
    $tabsB = $formB.GetType().GetField('tabs', $flagsB).GetValue($formB)
    Check "long banner wraps to more than one line" ($lblB.Height -gt 30)
    Check "banner grows past the old fixed 38px" ($bannerB.Height -gt 38)
    Check "banner does not overlap the tab strip" ($tabsB.Top -ge $bannerB.Bottom)
} finally { $formB.Dispose() }

"== 17. a dismissed banner stays dismissed =="
# The status engine repaints every second and used to call ShowBanner again, so the close button
# did nothing. After the user closes the banner it must stay hidden until the state changes.
$formD = New-Object FanManager.MainForm
try {
    $flagsD = [Reflection.BindingFlags]'NonPublic,Instance'
    $showD = $formD.GetType().GetMethod('ShowBanner', $flagsD)
    $bannerD = $formD.GetType().GetField('banner', $flagsD).GetValue($formD)
    $showD.Invoke($formD, @('service down', $false)) | Out-Null
    $formD.Show()
    [System.Windows.Forms.Application]::DoEvents()
    Check "banner shown before dismiss" ($bannerD.Visible)
    $buttons = $bannerD.GetControlFromPosition(1, 0)
    $buttons.Controls[1].PerformClick()
    [System.Windows.Forms.Application]::DoEvents()
    Check "banner hidden after close click" (-not $bannerD.Visible)
    $showD.Invoke($formD, @('service down', $false)) | Out-Null
    Check "dismissed banner stays hidden on refresh" (-not $bannerD.Visible)
} finally { $formD.Dispose() }

"== 18. application icon =="
# The window/taskbar and the tray both used to fall back to the stock Windows icon.
Check "ui/app.ico ships with the repo" (Test-Path (Join-Path $PSScriptRoot 'app.ico'))
Check "App.Icon is not the stock system icon" (-not [object]::ReferenceEquals([FanManager.App]::Icon, [System.Drawing.SystemIcons]::Application))
$formI = New-Object FanManager.MainForm
try {
    Check "form uses the app icon" ([object]::ReferenceEquals($formI.Icon, [FanManager.App]::Icon))
} finally { $formI.Dispose() }

""
try { Remove-Item $script:testLogDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
"RESULT: $($script:pass) passed, $($script:fail) failed"
if ($script:fail -gt 0) { exit 1 }
