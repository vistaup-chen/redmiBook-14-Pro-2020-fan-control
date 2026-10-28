# msr-lib.ps1 - MSR read/write via NBFC's own OpenHardwareMonitorLib (Ring0 class).
# Dot-source this file, then call Get-Msr <indexHex> / Set-Msr <indexHex> <valueUlong>.
# NOTE: no hex literals >= 0x80000000 anywhere (PowerShell parses them as negative ints).
# ASCII only.
$script:MsrAsmPath = "C:\Program Files (x86)\NoteBook FanControl\Plugins\OpenHardwareMonitorLib.dll"
$script:Asm = [System.Reflection.Assembly]::LoadFrom($script:MsrAsmPath)
$script:R0 = $script:Asm.GetType("OpenHardwareMonitor.Hardware.Ring0")

function Ensure-MsrOpen {
  $isOpen = $script:R0.GetProperty("IsOpen", [System.Reflection.BindingFlags]"Public,Static")
  $v = $isOpen.GetValue($null, $null)
  if(-not $v){
    $open = $script:R0.GetMethod("Open", [System.Reflection.BindingFlags]"Public,Static")
    $open.Invoke($null, $null) | Out-Null
  }
}

function Get-Msr {
  param([string]$IndexHex)
  Ensure-MsrOpen
  $idx = Convert-ToUInt32Hex $IndexHex
  $args = @($idx, [UInt32]0, [UInt32]0)
  $ok = $script:R0.InvokeMember("Rdmsr", [System.Reflection.BindingFlags]"Public,Static,InvokeMethod", $null, $null, $args)
  if(-not $ok){ throw "Rdmsr failed for $IndexHex" }
  return ([UInt64]$args[2] * 4294967296) + [UInt64]$args[1]
}

function Set-Msr {
  param([string]$IndexHex, [UInt64]$Value)
  Ensure-MsrOpen
  $idx = Convert-ToUInt32Hex $IndexHex
  $b = [BitConverter]::GetBytes([UInt64]$Value)
  $eax = [BitConverter]::ToUInt32($b, 0)
  $edx = [BitConverter]::ToUInt32($b, 4)
  $ok = $script:R0.InvokeMember("Wrmsr", [System.Reflection.BindingFlags]"Public,Static,InvokeMethod", $null, $null, @($idx, $eax, $edx))
  if(-not $ok){ throw "Wrmsr failed for $IndexHex" }
}

function Convert-ToUInt32Hex([string]$s){
  $t = $s.Trim()
  if($t.ToLower().StartsWith("0x")){ $t = $t.Substring(2) }
  return [UInt32]::Parse($t, [System.Globalization.NumberStyles]::HexNumber)
}
