$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class WinRect {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L; public int T; public int R; public int B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, ref RECT r, int size);
}
'@

$exe = Join-Path $PSScriptRoot 'UpDownSplit.exe'
$tmp = [System.IO.Path]::GetTempPath()

function Get-Rect([IntPtr]$h) {
  $r = New-Object WinRect+RECT
  [WinRect]::GetWindowRect($h, [ref]$r) | Out-Null
  return $r
}
# 可见边框（DWM 扩展边框），即用户实际看到的窗口边界
function Get-VisRect([IntPtr]$h) {
  $r = New-Object WinRect+RECT
  [void][WinRect]::DwmGetWindowAttribute($h, 9, [ref]$r, [Runtime.InteropServices.Marshal]::SizeOf($r))
  return $r
}
function Near([int]$a, [int]$b, [int]$tol) { return ([Math]::Abs($a - $b) -le $tol) }

$allPass = $true

# --- test windows on the same monitor ---
$np = Join-Path $env:WINDIR 'System32\notepad.exe'
$n1 = Start-Process -FilePath $np -ArgumentList 'a.txt' -WorkingDirectory $env:TEMP -PassThru
$n2 = Start-Process -FilePath $np -ArgumentList 'b.txt' -WorkingDirectory $env:TEMP -PassThru
Start-Sleep -Milliseconds 1800
$n1.Refresh(); $n2.Refresh()
$h1 = $n1.MainWindowHandle
$h2 = $n2.MainWindowHandle
if ($h1 -eq [IntPtr]::Zero -or $h2 -eq [IntPtr]::Zero) { Write-Output 'FAIL: no notepad handles'; exit 1 }

# --- 1) window enumeration ---
$listFile = Join-Path $tmp 'uds_list.txt'
& $exe listwindows $listFile
Start-Sleep -Milliseconds 300
$lines = @(Get-Content $listFile -ErrorAction SilentlyContinue)
$ok1 = ($lines.Count -ge 2) -and ($lines | Where-Object { $_.StartsWith("$($h1.ToInt64())|") }) -and ($lines | Where-Object { $_.StartsWith("$($h2.ToInt64())|") })
Write-Output ("enumerate windows: " + $(if ($ok1) {'PASS'} else {'FAIL'}) + "  ($($lines.Count) windows)")
$allPass = $allPass -and $ok1

# --- 2) split two windows top/bottom of same monitor (check VISIBLE bounds) ---
& $exe split $h1.ToInt64() $h2.ToInt64()
Start-Sleep -Milliseconds 800
$a1 = Get-VisRect $h1
$a2 = Get-VisRect $h2
Write-Output ("after split1 (visible): {0},{1},{2},{3}" -f $a1.L,$a1.T,$a1.R,$a1.B)
Write-Output ("after split2 (visible): {0},{1},{2},{3}" -f $a2.L,$a2.T,$a2.R,$a2.B)
$ok2 = (Near $a1.L $a2.L 3) -and (Near $a1.R $a2.R 3) -and ($a1.T -lt $a2.T) -and `
       (Near $a1.B $a2.T 3) -and (Near ($a1.B - $a1.T) ($a2.B - $a2.T) 3) -and (($a1.B - $a1.T) -gt 50)
Write-Output ("split top/bottom: " + $(if ($ok2) {'PASS'} else {'FAIL'}))
$allPass = $allPass -and $ok2

# --- 3) place into arbitrary rect (same code path as settings window apply) ---
& $exe place $h2.ToInt64() 120 80 900 640
Start-Sleep -Milliseconds 500
$p = Get-VisRect $h2
Write-Output ("after place (visible): {0},{1},{2},{3}" -f $p.L,$p.T,$p.R,$p.B)
$ok3 = (Near $p.L 120 3) -and (Near $p.T 80 3) -and (Near $p.R 1020 3) -and (Near $p.B 720 3)
Write-Output ("place rect: " + $(if ($ok3) {'PASS'} else {'FAIL'}))
$allPass = $allPass -and $ok3

# --- 4) undo restores previous position ---
$pre = Get-VisRect $h1
& $exe testundo $h1.ToInt64() 10 20 300 200
Start-Sleep -Milliseconds 500
$u = Get-VisRect $h1
Write-Output ("after undo:  {0},{1},{2},{3}  (expected {4},{5},{6},{7})" -f $u.L,$u.T,$u.R,$u.B,$pre.L,$pre.T,$pre.R,$pre.B)
$ok4 = (Near $u.L $pre.L 2) -and (Near $u.T $pre.T 2) -and (Near $u.R $pre.R 2) -and (Near $u.B $pre.B 2)
Write-Output ("undo restore: " + $(if ($ok4) {'PASS'} else {'FAIL'}))
$allPass = $allPass -and $ok4

Stop-Process -Id $n1.Id -ErrorAction SilentlyContinue
Stop-Process -Id $n2.Id -ErrorAction SilentlyContinue
Remove-Item $listFile -ErrorAction SilentlyContinue

Write-Output ("RESULT: " + $(if ($allPass) {'ALL PASS'} else {'FAILED'}))
if (-not $allPass) { exit 1 }
