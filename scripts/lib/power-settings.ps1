# power-settings.ps1 - keep the Z-fan plans from changing settings this project does not own.
#
# Why: powercfg /duplicatescheme copies the whole source plan, including idle timers.
# A Z-fan plan cloned from a short-timeout plan made the machine sleep ~10 minutes after
# a mode switch, even though this project only intends to change CPU frequency
# (SUB_PROCESSOR) and fan curves. Every activation therefore re-mirrors the user's own
# sleep / hibernate / hybrid-sleep / display timers into the target Z-fan plan.
#
# The user's own plan GUID is remembered in data\power-baseline.txt so "restore" can go
# back to the plan the machine actually used before this tool took over.
#
# Dot-source this file. ASCII only.

$FanPlanGuids = @(
  "9a10e4a1-1111-4b7a-8c0d-000000000001",
  "9a10e4a1-1111-4b7a-8c0d-000000000002",
  "9a10e4a1-1111-4b7a-8c0d-000000000003",
  "9a10e4a1-1111-4b7a-8c0d-000000000004"
)

# settings that belong to the user, not to us: name / subgroup GUID / setting GUID
$UserTimerSettings = @(
  @{ name="sleep";          sub="238c9fa8-0aad-41ed-83f4-97be242c8f20"; set="29f6c1db-86da-48c5-9fdb-f2b67b1f44da" }, # STANDBYIDLE
  @{ name="hibernate";      sub="238c9fa8-0aad-41ed-83f4-97be242c8f20"; set="9d7815a6-7ee4-497e-8888-515a05f02364" }, # HIBERNATEIDLE
  @{ name="hybridsleep";    sub="238c9fa8-0aad-41ed-83f4-97be242c8f20"; set="94ac6d29-73ce-41a6-809f-6363ba21b47e" }, # HYBRIDSLEEP
  @{ name="unattendedsleep";sub="238c9fa8-0aad-41ed-83f4-97be242c8f20"; set="7bc4a2f9-d8fc-4469-b07b-33eb785aaca0" }, # UNATTENDSLEEP
  @{ name="displayoff";     sub="7516b95f-f776-4464-8c53-06167f40cc99"; set="3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e" }  # VIDEOIDLE
)

function Get-ActivePowerPlanGuid {
  $s = (powercfg /getactivescheme 2>&1) -join " "
  $m = [regex]::Match($s, '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})')
  if($m.Success){ return $m.Groups[1].Value.ToLower() }
  return ""
}

function Test-PowerPlanExists {
  param([string]$Plan)
  if(-not $Plan){ return $false }
  return (Test-Path ("HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + $Plan))
}

# The user's own plan: follow the currently active plan live when it is not one of ours
# (so switching/renaming their plan is picked up), otherwise keep the stored baseline.
# Nothing about the timer VALUES is cached: Get-PlanTimer reads them on every sync.
function Resolve-BaselinePlan {
  param([string]$DataDir)
  $file = Join-Path $DataDir "power-baseline.txt"
  $stored = ""
  if(Test-Path $file){
    $g = (Get-Content $file -ErrorAction SilentlyContinue | Select-Object -First 1)
    if($g){ $g = $g.Trim().ToLower() }
    if($g -and $g -match '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'){ $stored = $g }
  }
  $active = Get-ActivePowerPlanGuid
  if($active -and ($FanPlanGuids -notcontains $active) -and (Test-PowerPlanExists $active) -and ($active -ne $stored)){
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    Set-Content -Path $file -Value $active -Encoding ASCII
    return $active
  }
  if($stored -and (Test-PowerPlanExists $stored)){ return $stored }
  $candidates = @()
  if($active){ $candidates += $active }
  $candidates += "381b4222-f694-41f0-9685-ff5bb260df2e"   # Balanced
  $candidates += "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"   # High Performance
  $candidates += "a1841308-3541-4fab-bc81-f71556f20b4a"   # Power Saver
  foreach($g in $candidates){
    if(($FanPlanGuids -notcontains $g) -and (Test-PowerPlanExists $g)){
      New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
      Set-Content -Path $file -Value $g -Encoding ASCII
      return $g
    }
  }
  return ""
}

# effective AC/DC value of one setting. Explicit per-scheme registry values win (this also
# works for hidden settings such as UNATTENDSLEEP that powercfg refuses to print); otherwise
# fall back to effective values, parsed by hex tokens only so any display language works.
function Get-PlanTimer {
  param([string]$Plan,[string]$Sub,[string]$Setting)
  $k = Get-ItemProperty ("HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + $Plan + "\" + $Sub + "\" + $Setting) -ErrorAction SilentlyContinue
  if(($null -ne $k) -and ($null -ne $k.ACSettingIndex) -and ($null -ne $k.DCSettingIndex)){
    return @{ AC=[int64]$k.ACSettingIndex; DC=[int64]$k.DCSettingIndex }
  }
  $txt = (powercfg /q $Plan $Sub $Setting 2>&1) -join "`n"
  $ms = [regex]::Matches($txt, '0x([0-9A-Fa-f]{8})')
  if($ms.Count -lt 2){ return $null }
  $ac = [Convert]::ToUInt32($ms[$ms.Count-2].Groups[1].Value,16)
  $dc = [Convert]::ToUInt32($ms[$ms.Count-1].Groups[1].Value,16)
  return @{ AC=[int64]$ac; DC=[int64]$dc }
}

function Set-PlanTimer {
  param([string]$Plan,[string]$Sub,[string]$Setting,[int64]$AC,[int64]$DC)
  powercfg /setacvalueindex $Plan $Sub $Setting $AC | Out-Null
  powercfg /setdcvalueindex $Plan $Sub $Setting $DC | Out-Null
}

# Mirror the user's live sleep/display timers into one Z-fan plan. The SOURCE is the plan
# that is active right now (this is the plan the user just edited in Windows Settings);
# only when that is the target itself / unreadable do we fall back to the user's own plan.
# Values are read fresh from the registry / powercfg on every call - nothing is cached.
# Never writes to a plan outside the Z-fan set, so the user's own plan stays read-only.
# Returns one line per setting that actually changed (empty when nothing needed fixing).
function Sync-PlanUserTimers {
  param([string]$Target,[string]$DataDir,[switch]$PrintBaseline,[string]$Source="")
  $baseline = Resolve-BaselinePlan -DataDir $DataDir
  if($PrintBaseline){ "baseline=" + $baseline }
  if(-not $Target -or -not $baseline){ return }
  $t = $Target.ToLower()
  if($FanPlanGuids -notcontains $t){ return }
  $src = $Source
  if(-not $src){ $src = Get-ActivePowerPlanGuid }
  if($src){ $src = $src.ToLower() }
  if(-not $src -or -not (Test-PowerPlanExists $src)){ $src = $baseline }
  # source == target means the user is already on the plan we are about to keep: leave it alone
  if($src -eq $t){ return }
  foreach($it in $UserTimerSettings){
    $v = Get-PlanTimer -Plan $src -Sub $it.sub -Setting $it.set
    if($null -eq $v){ continue }
    $cur = Get-PlanTimer -Plan $t -Sub $it.sub -Setting $it.set
    if(($null -ne $cur) -and ($cur.AC -eq $v.AC) -and ($cur.DC -eq $v.DC)){ continue }
    Set-PlanTimer -Plan $t -Sub $it.sub -Setting $it.set -AC $v.AC -DC $v.DC
    $was = "inherit"
    if($null -ne $cur){ $was = ([string]$cur.AC + "/" + [string]$cur.DC) }
    "  " + $it.name + ": AC=" + $v.AC + " DC=" + $v.DC + " (was " + $was + ", from " + $src.Substring(0,8) + ")"
  }
}
