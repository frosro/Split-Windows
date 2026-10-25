Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(32,32)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Transparent)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,64,132,220))), 2, 3, 28, 12)
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,84,185,116))), 2, 17, 28, 12)
$g.Dispose()
$h = $bmp.GetHicon()
$ico = [System.Drawing.Icon]::FromHandle($h)
$fs = [System.IO.File]::Create((Join-Path $PSScriptRoot 'app.ico'))
$ico.Save($fs)
$fs.Close()
Write-Output "app.ico written"
