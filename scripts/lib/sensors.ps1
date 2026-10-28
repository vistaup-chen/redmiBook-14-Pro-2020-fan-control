# sensors.ps1 —— 用 LibreHardwareMonitorLib 读取传感器（CPU/GPU 温度、功耗、风扇转速）
# 需要管理员权限（内核驱动）。建议经 run-elevated.ps1 调用。
# 用法:
#   sensors.ps1                                  # 输出完整快照到 stdout
#   sensors.ps1 -OutFile out.txt                 # 完整快照写入文件
#   sensors.ps1 -CsvFile log.csv -Seconds 120 -IntervalSec 2   # 连续采样写 CSV
param(
  [string]$OutFile,
  [string]$CsvFile,
  [int]$Seconds = 0,
  [int]$IntervalSec = 2
)
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot '..\..\tools\LHM\LibreHardwareMonitorLib.dll'
Add-Type -Path $lib

$comp = New-Object LibreHardwareMonitor.Hardware.Computer
$comp.IsCpuEnabled          = $true
$comp.IsMotherboardEnabled  = $true
$comp.IsGpuEnabled          = $true
$comp.IsMemoryEnabled       = $true
$comp.IsBatteryEnabled      = $true
$comp.IsStorageEnabled      = $false
$comp.IsNetworkEnabled      = $false
$comp.IsControllerEnabled   = $false
$comp.IsPsuEnabled          = $false
$comp.Open()

function Get-AllHardware {
  foreach($hw in $comp.Hardware){
    $hw.Update()
    Write-Output $hw
    foreach($sub in $hw.SubHardware){ $sub.Update(); Write-Output $sub }
  }
}

function Format-Snapshot {
  $lines = New-Object System.Collections.Generic.List[string]
  $lines.Add("# snapshot " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
  foreach($hw in (Get-AllHardware)){
    $lines.Add("# HW " + $hw.Identifier + " | " + $hw.Name)
    foreach($s in $hw.Sensors){
      $lines.Add(("{0}`t{1}`t{2}`t{3}" -f $s.Identifier, $s.Name, $s.SensorType, $s.Value))
    }
  }
  return ($lines -join [Environment]::NewLine)
}

function Get-CsvRow {
  $cpuPkgT=''; $coreMaxT=''; $cpuW=''; $cpuClkMax=''; $fanRpm=''; $gpuT=''; $gpuW=''
  $mbT=''; $batPct=''
  foreach($hw in (Get-AllHardware)){
    foreach($s in $hw.Sensors){
      $n = $s.Name
      if($s.SensorType -eq [LibreHardwareMonitor.Hardware.SensorType]::Temperature){
        if($n -eq 'CPU Package'){ $cpuPkgT = [math]::Round([double]$s.Value,1) }
        elseif($n -like 'Core *'){ $v=[double]$s.Value; if($coreMaxT -eq '' -or $v -gt [double]$coreMaxT){ $coreMaxT=$v } }
        elseif($n -like 'GPU Core'){ $gpuT = [math]::Round($v,1) }
        elseif($n -match 'Temperature|Motherboard'){ $mbT = $n + '=' + [math]::Round($v,1) + ';'+$mbT }
      }
      elseif($s.SensorType -eq [LibreHardwareMonitor.Hardware.SensorType]::Power){
        if($n -eq 'CPU Package'){ $cpuW = [math]::Round([double]$s.Value,2) }
        elseif($n -like 'GPU Package' -or $n -like 'GPU Power'){ $gpuW = [math]::Round([double]$s.Value,2) }
      }
      elseif($s.SensorType -eq [LibreHardwareMonitor.Hardware.SensorType]::Fan){
        if($fanRpm -eq '' -and $n -match 'Fan'){ $fanRpm = [double]$s.Value }
      }
      elseif($s.SensorType -eq [LibreHardwareMonitor.Hardware.SensorType]::Clock){
        if($n -like 'CPU Core #*'){ $v=[double]$s.Value; if($cpuClkMax -eq '' -or $v -gt $cpuClkMax){ $cpuClkMax=$v } }
      }
      elseif($s.SensorType -eq [LibreHardwareMonitor.Hardware.SensorType]::Level){
        if($n -match 'Battery'){ $batPct = [double]$s.Value }
      }
    }
  }
  return ("{0},{1},{2},{3},{4},{5},{6},{7},{8}" -f (Get-Date -Format 'HH:mm:ss'),$cpuPkgT,$coreMaxT,$cpuW,$cpuClkMax,$fanRpm,$gpuT,$gpuW,$batPct)
}

try {
  if($CsvFile){
    $header = 'time,cpuPkgT,coreMaxT,cpuW,cpuClkMax,fanRpm,gpuT,gpuW,batteryPct'
    if(-not (Test-Path $CsvFile)){ Set-Content -Path $CsvFile -Value $header -Encoding UTF8 }
    $deadline = (Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
      Add-Content -Path $CsvFile -Value (Get-CsvRow) -Encoding UTF8
      Start-Sleep -Seconds $IntervalSec
    }
  }
  elseif($OutFile){
    Set-Content -Path $OutFile -Value (Format-Snapshot) -Encoding UTF8
  }
  else {
    Write-Output (Format-Snapshot)
  }
}
finally { $comp.Close() }
