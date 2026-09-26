param([string]$Root=$PSScriptRoot,[string]$InputFile='contact38-render.txt',[string]$OutputFile='contact38-preview.png')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$rows=@(foreach($line in Get-Content -LiteralPath (Join-Path $Root $InputFile)) {
    $parts=$line.Split('|')
    [pscustomobject]@{Kind=$parts[0]; Values=@($parts[1].Split(',') | ForEach-Object {[double]::Parse($_,[Globalization.CultureInfo]::InvariantCulture)});Text=if($parts.Count -gt 2){$parts[2]}else{''}}
})
$xValues=@();$yValues=@()
foreach($row in $rows){
    if($row.Kind -eq 'T'){$xValues+=@($row.Values[0],($row.Values[0]+$row.Text.Length*$row.Values[2]));$yValues+=@($row.Values[1],($row.Values[1]+$row.Values[2]));continue}
    for($i=0;$i -lt $row.Values.Count;$i+=2){$xValues+=$row.Values[$i];$yValues+=$row.Values[$i+1]}
}
$xb=$xValues | Measure-Object -Minimum -Maximum;$yb=$yValues | Measure-Object -Minimum -Maximum
$scale=[Math]::Min(1400/($xb.Maximum-$xb.Minimum),800/($yb.Maximum-$yb.Minimum))
$bitmap=[Drawing.Bitmap]::new(1600,1020);$g=[Drawing.Graphics]::FromImage($bitmap)
$outline=[Drawing.Pen]::new([Drawing.Color]::White,1.2);$bar=[Drawing.Pen]::new([Drawing.Color]::Magenta,35*$scale)
$white=[Drawing.SolidBrush]::new([Drawing.Color]::White);$orange=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255,127,0))
$title=[Drawing.Font]::new('Microsoft YaHei',18)
function Px([double]$x){[single](100+($x-$xb.Minimum)*$scale)}
function Py([double]$y){[single](150+($yb.Maximum-$y)*$scale)}
try{
    $g.Clear([Drawing.Color]::FromArgb(33,40,48));$g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias;$bar.LineJoin=[Drawing.Drawing2D.LineJoin]::Miter
    $g.DrawString('38试验 · 相接端点收尾 / 横排优先 / 支撑指引', $title,$white,55,30)
    $g.DrawString('由CAD实体坐标绘制 · 非CAD截图；文字字体为预览字体', $title,$white,55,70)
    foreach($row in $rows){
        $v=$row.Values
        if($row.Kind -eq 'D'){$g.FillEllipse($orange,(Px ($v[0]-50)),(Py ($v[1]+50)),[single](100*$scale),[single](100*$scale));continue}
        if($row.Kind -eq 'T'){$font=[Drawing.Font]::new('仿宋',[single]($v[2]*$scale),[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel);try{$g.DrawString($row.Text,$font,$white,(Px $v[0]),(Py ($v[1]+$v[2])))}finally{$font.Dispose()};continue}
        $pts=@(for($i=0;$i -lt $v.Count;$i+=2){[Drawing.PointF]::new((Px $v[$i]),(Py $v[$i+1]))})
        $pen=if($row.Kind -eq 'B'){$bar}else{$outline};$g.DrawLines($pen,[Drawing.PointF[]]$pts)
    }
    $bitmap.Save((Join-Path $Root $OutputFile),[Drawing.Imaging.ImageFormat]::Png)
}finally{$title.Dispose();$orange.Dispose();$white.Dispose();$bar.Dispose();$outline.Dispose();$g.Dispose();$bitmap.Dispose()}
