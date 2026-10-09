# Copyright (c) 2026 Rausch IT
# SPDX-License-Identifier: MIT

# make-icon.ps1
# Generates src\DisplayProfiles.Tray\displayprofiles.ico (rounded square with "DS", sizes 16-256, PNG-compressed ICO).
# Run once; the .ico is part of the source tree. Same design as the runtime tray icons (AppIcons.cs).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Target = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\DisplayProfiles.Tray\displayprofiles.ico'
$Sizes  = 16, 24, 32, 48, 64, 128, 256

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $r = [Math]::Max(2, [int]($size / 4))
    $m = $size - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90)
    $path.AddArc($m - $r, 0, $r, $r, 270, 90)
    $path.AddArc($m - $r, $m - $r, $r, $r, 0, 90)
    $path.AddArc(0, $m - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 120, 215))
    $g.FillPath($fill, $path)
    $font = New-Object System.Drawing.Font 'Segoe UI', ([single]($size * 0.42)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $text = $g.MeasureString('DS', $font)
    $g.DrawString('DS', $font, [System.Drawing.Brushes]::White, ($size - $text.Width) / 2, ($size - $text.Height) / 2)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$images = foreach ($s in $Sizes) { ,(New-IconPng $s) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$Sizes.Count)
$offset = 6 + 16 * $Sizes.Count
for ($i = 0; $i -lt $Sizes.Count; $i++) {
    $s = $Sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Target, $out.ToArray())
Write-Host "Written: $Target"
