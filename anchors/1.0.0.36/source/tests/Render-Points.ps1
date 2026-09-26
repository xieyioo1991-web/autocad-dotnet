param([string]$Root = $PSScriptRoot, [string]$InputFile = 'support-opening-entities.txt', [string]$OutputFile = 'points-preview36.png', [string]$PointFile = 'precision-interface-points36.txt')
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
    $graphics.DrawString('试验36 · 固定角点 / 收尾400 / 间距不超过800', $font, $brush, 55, 36)
    $graphics.DrawString('由后台生成的DWG实体坐标绘制 · 非CAD截图', $font, $brush, 55, 77)
    $pointBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255,127,0))
    try {
        foreach ($line in Get-Content -LiteralPath (Join-Path $Root $PointFile)) {
            if ($line -notmatch 'LWPOLYLINE') { continue }
            $vertices = [regex]::Matches($line, '\(10 ([\d.Ee+-]+) ([\d.Ee+-]+)\)')
            if ($vertices.Count -ne 2) { throw 'Expected two dot vertices' }
            $x=([double]::Parse($vertices[0].Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture)+[double]::Parse($vertices[1].Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture))/2
            $y=[double]::Parse($vertices[0].Groups[2].Value,[Globalization.CultureInfo]::InvariantCulture)
            $graphics.FillEllipse($pointBrush,[single](100+($x-50-$xBounds.Minimum)*$scale),[single](150+($yBounds.Maximum-$y-50)*$scale),[single](100*$scale),[single](100*$scale))
        }
    } finally { $pointBrush.Dispose() }
    $bitmap.Save((Join-Path $Root $OutputFile), [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $brush.Dispose(); $font.Dispose(); $bar.Dispose(); $outline.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}



