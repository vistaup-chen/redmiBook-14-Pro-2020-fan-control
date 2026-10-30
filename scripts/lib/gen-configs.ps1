# gen-configs-v2.ps1 - regenerate 4 NBFC configs with optimized curves (ASCII only)
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dir = Join-Path $root "nbfc-configs"
function Th($pairs){
  $out = ""
  foreach($p in $pairs){
    $out += "        <TemperatureThreshold><UpThreshold>$($p[0])</UpThreshold><DownThreshold>$($p[1])</DownThreshold><FanSpeed>$($p[2])</FanSpeed></TemperatureThreshold>`r`n"
  }
  return $out.TrimEnd("`r","`n")
}
function MakeCfg($name, $critical, $thresholds){
  $head = '<?xml version="1.0" encoding="utf-8"?>'
  $body = @"
<FanControlConfigV2 xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <NotebookModel>$name</NotebookModel>
  <Author>ZCode fan project v2 (optimized curves)</Author>
  <EcPollInterval>1000</EcPollInterval>
  <ReadWriteWords>false</ReadWriteWords>
  <CriticalTemperature>$critical</CriticalTemperature>
  <FanConfigurations>
    <FanConfiguration>
      <ReadRegister>148</ReadRegister>
      <WriteRegister>148</WriteRegister>
      <MinSpeedValue>200</MinSpeedValue>
      <MaxSpeedValue>88</MaxSpeedValue>
      <IndependentReadMinMaxValues>false</IndependentReadMinMaxValues>
      <MinSpeedValueRead>0</MinSpeedValueRead>
      <MaxSpeedValueRead>0</MaxSpeedValueRead>
      <ResetRequired>true</ResetRequired>
      <FanSpeedResetValue>255</FanSpeedResetValue>
      <FanDisplayName>CPU Fan</FanDisplayName>
      <TemperatureThresholds>
$(Th $thresholds)
      </TemperatureThresholds>
      <FanSpeedPercentageOverrides>
      </FanSpeedPercentageOverrides>
    </FanConfiguration>
  </FanConfigurations>
  <RegisterWriteConfigurations>
    <RegisterWriteConfiguration>
      <WriteMode>Set</WriteMode>
      <WriteOccasion>OnInitialization</WriteOccasion>
      <Register>147</Register>
      <Value>28</Value>
      <ResetRequired>true</ResetRequired>
      <ResetValue>4</ResetValue>
      <ResetWriteMode>Set</ResetWriteMode>
      <Description>EC fan manual mode on init, auto on reset</Description>
    </RegisterWriteConfiguration>
  </RegisterWriteConfigurations>
</FanControlConfigV2>
"@
  [System.IO.File]::WriteAllText("$dir\$name.xml", "$head`r`n$body")
  "written: $name.xml"
}
$silent   = @(@(0,0,0),@(58,51,35),@(64,58,55),@(70,64,75),@(74,68,100))
$balanced = @(@(0,0,0),@(50,44,40),@(60,54,60),@(70,64,85),@(76,70,100))
$perf     = @(@(0,0,40),@(44,38,65),@(54,48,85),@(63,57,100))
$dnd      = @(@(0,0,0),@(60,53,40),@(68,61,70),@(74,67,100))
MakeCfg "RedmiBook 14 2020 Silent" 76 $silent
MakeCfg "RedmiBook 14 2020 Balanced" 80 $balanced
MakeCfg "RedmiBook 14 2020 Performance" 78 $perf
MakeCfg "RedmiBook 14 2020 DoNotDisturb" 76 $dnd
"validating..."
Get-ChildItem $dir -Filter "*.xml" | ForEach-Object {
  try { [xml](Get-Content $_.FullName -Raw) | Out-Null; "OK   $($_.Name)" } catch { "FAIL $($_.Name): $_" }
}
