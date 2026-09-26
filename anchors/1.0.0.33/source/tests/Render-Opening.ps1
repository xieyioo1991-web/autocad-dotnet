param([string]$Root = $PSScriptRoot, [string]$InputFile = 'support-opening-entities.txt', [string]$OutputFile = 'support-opening-preview.png')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$rows = foreach ($row in Get-Content -LiteralPath (Join-Path $Root $InputFile)) {
    if ([string]::IsNullOrWhiteSpace($row)) { continue }
    $pair = $row.Split('|')
    $values = @($pair[1].Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
    [pscustomobject]@{Kind=$pair[0]; Values=$values}
}
$allX = @($rows | ForEach-Object { for ($i = 0; $i -lt $_.Values.Count; $i += 2) { $_.Values[$i] } })
$allY = @($rows | ForEach-Object { for ($i = 1; $i -lt $_.Values.Count; $i += 2) { $_.Values[$i] } })
$xBounds = $allX | Measure-Object -Minimum -Maximum
$yBounds = $allY | Measure-Object -Minimum -Maximum
$scale = [Math]::Min(1200 / ($xBounds.Maximum - $xBounds.Minimum), 760 / ($yBounds.Maximum - $yBounds.Minimum))
$bitmap = [Drawing.Bitmap]::new(1400, 980)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$outline = [Drawing.Pen]::new([Drawing.Color]::White, 1.2)
$bar = [Drawing.Pen]::new([Drawing.Color]::Magenta, 35 * $scale)
$font = [Drawing.Font]::new('Microsoft YaHei', 18)
$brush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
try {
    $graphics.Clear([Drawing.Color]::FromArgb(33, 40, 48))
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $bar.LineJoin = [Drawing.Drawing2D.LineJoin]::Miter
    foreach ($row in $rows) {
        $points = for ($i = 0; $i -lt $row.Values.Count; $i += 2) {
            [Drawing.PointF]::new([single](100 + ($row.Values[$i] - $xBounds.Minimum) * $scale), [single](150 + ($yBounds.Maximum - $row.Values[$i + 1]) * $scale))
        }
        $pen = if ($row.Kind -eq 'B') { $bar } else { $outline }
        $graphics.DrawLines($pen, [Drawing.PointF[]]$points)
    }
    $graphics.DrawString('试验33 · 小区域上筋直锚600 / 取消下筋 / 自由竖段再减50', $font, $brush, 55, 35)
    $graphics.DrawString('由后台生成的DWG实体坐标绘制 · 非CAD截图', $font, $brush, 55, 77)
    $bitmap.Save((Join-Path $Root $OutputFile), [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $brush.Dispose(); $font.Dispose(); $bar.Dispose(); $outline.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}

