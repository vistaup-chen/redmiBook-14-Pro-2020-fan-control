# setup-plans2.ps1 - re-apply processor settings with the CORRECT subgroup GUID
$plans = @(
  @{ guid="9a10e4a1-1111-4b7a-8c0d-000000000001"; name="Silent";       maxstate=60; maxstate1=75; epp=60; boost=0 },
  @{ guid="9a10e4a1-1111-4b7a-8c0d-000000000002"; name="Balanced";     maxstate=100; maxstate1=100; epp=40; boost=1 },
  @{ guid="9a10e4a1-1111-4b7a-8c0d-000000000003"; name="Performance";  maxstate=100; maxstate1=100; epp=0;  boost=2 },
  @{ guid="9a10e4a1-1111-4b7a-8c0d-000000000004"; name="DoNotDisturb"; maxstate=50; maxstate1=60; epp=80; boost=0 }
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

foreach($p in $plans){
  $g = $p.guid
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
}
# re-activate current plan so values take effect
powercfg /setactive 9a10e4a1-1111-4b7a-8c0d-000000000001
"active: $(powercfg /getactivescheme)"
