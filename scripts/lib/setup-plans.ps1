# setup-plans.ps1 - create/refresh the 4 Z-fan power plans.
# Only the SUB_PROCESSOR subgroup belongs to this project. Sleep / hibernate / display
# timers are mirrored from the user's own plan (see lib\power-settings.ps1) so switching
# modes never changes when the machine sleeps.
$plans = @(
  @{ guid="9a10e4a1-1111-4b7a-8c0d-000000000001"; name="Z-fan Silent";       maxstate=60; maxstate1=75; epp=60; boost=0 },
  @{ guid="9a10e4a1-1111-4b7a-8c0d-000000000002"; name="Z-fan Balanced";     maxstate=100; maxstate1=100; epp=40; boost=1 },
  @{ guid="9a10e4a1-1111-4b7a-8c0d-000000000003"; name="Z-fan Performance";  maxstate=100; maxstate1=100; epp=0;  boost=2 },
  @{ guid="9a10e4a1-1111-4b7a-8c0d-000000000004"; name="Z-fan DoNotDisturb"; maxstate=50; maxstate1=60; epp=80; boost=0 }
)
$SUB   = "54533251-82be-4824-96c1-47b60b740d00"  # SUB_PROCESSOR (correct)
$G_MAX  = "bc5038f7-23e0-4960-96da-33abaf5935ec"
$G_MAX1 = "bc5038f7-23e0-4960-96da-33abaf5935ed"
$G_MIN  = "893dee8e-2bef-41e0-89c6-b55d0929964c"
$G_EPP  = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863"
$G_EPP1 = "36687f9e-e3a5-4dbf-b1dc-15eb381c6864"
$G_BOOST= "be337238-0d82-4146-a960-4f3749d470c7"
# hidden "Processor max frequency" (MHz). A leftover value here hard-caps the CPU
# (e.g. 2500 = 2.5GHz, kills turbo); 0 = no cap.
$G_FREQMAX = "75b0ae3f-bce0-45a7-8c89-c9611c25e100"

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dataDir  = Join-Path $repoRoot "data"
. "$PSScriptRoot\power-settings.ps1"

# the plan the machine used before this tool took over; also the template for missing plans
$baseline = Resolve-BaselinePlan -DataDir $dataDir
if(-not $baseline){ throw "no usable baseline power plan found" }
$prevActive = Get-ActivePowerPlanGuid
"baseline plan: $baseline"

foreach($p in $plans){
  $g = $p.guid
  if(-not (Test-PowerPlanExists $g)){
    powercfg /duplicatescheme $baseline $g | Out-Null
    powercfg /changename $g $p.name "created by the Z-fan fan-control tool" | Out-Null
    "created $($p.name) from baseline"
  }
  foreach($side in @("/setacvalueindex","/setdcvalueindex")){
    powercfg $side $g $SUB $G_MAX  $p.maxstate | Out-Null
    powercfg $side $g $SUB $G_MAX1 $p.maxstate1 | Out-Null
    powercfg $side $g $SUB $G_MIN  5 | Out-Null
    powercfg $side $g $SUB $G_EPP  $p.epp | Out-Null
    powercfg $side $g $SUB $G_EPP1 $p.epp | Out-Null
    powercfg $side $g $SUB $G_BOOST $p.boost | Out-Null
    powercfg $side $g $SUB $G_FREQMAX 0 | Out-Null
  }
  "set $($p.name): state=$($p.maxstate)/$($p.maxstate1) epp=$($p.epp) boost=$($p.boost) freqmax=0"
  # keep the user's CURRENT sleep/hibernate/display timeouts (read live from the plan that
  # is active right now); only fix what differs
  Sync-PlanUserTimers -Target $g -DataDir $dataDir -Source $prevActive
}
# do NOT leave the user on a fan plan: re-activate whatever was active before
if($prevActive){ powercfg /setactive $prevActive | Out-Null }
"active: $(powercfg /getactivescheme)"
