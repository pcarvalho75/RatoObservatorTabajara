param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
# Uncompressed RGB AVI, generated without a codec, Python, SDK, or experimental input.
function Chunk([string]$Id, [byte[]]$Bytes) {
    $m = [IO.MemoryStream]::new(); $b = [IO.BinaryWriter]::new($m)
    $b.Write([Text.Encoding]::ASCII.GetBytes($Id)); $b.Write([uint32]$Bytes.Length); $b.Write($Bytes)
    if ($Bytes.Length % 2) { $b.Write([byte]0) }; $b.Flush(); return ,$m.ToArray()
}
function JoinBytes([object[]]$Arrays) { $m=[IO.MemoryStream]::new(); foreach($a in $Arrays){$m.Write([byte[]]$a,0,$a.Length)}; return ,$m.ToArray() }
function ListChunk([string]$Type,[byte[]]$Bytes) { return ,(Chunk 'LIST' (JoinBytes @([Text.Encoding]::ASCII.GetBytes($Type),$Bytes))) }
$m=[IO.MemoryStream]::new(); $b=[IO.BinaryWriter]::new($m)
foreach($v in @(100000,122880,0,16,24,0,1,12288,64,64,0,0,0,0)){$b.Write([uint32]$v)}; $b.Flush(); $avih=Chunk 'avih' $m.ToArray()
$m=[IO.MemoryStream]::new();$b=[IO.BinaryWriter]::new($m)
$b.Write([Text.Encoding]::ASCII.GetBytes('vidsDIB '));$b.Write([uint32]0);$b.Write([uint16]0);$b.Write([uint16]0)
foreach($v in @(0,1,10,0,24,12288,4294967295,0)){$b.Write([uint32]$v)}
foreach($v in @(0,0,64,64)){$b.Write([int16]$v)};$b.Flush();$strh=Chunk 'strh' $m.ToArray()
$m=[IO.MemoryStream]::new();$b=[IO.BinaryWriter]::new($m)
$b.Write([uint32]40);$b.Write([int32]64);$b.Write([int32]64);$b.Write([uint16]1);$b.Write([uint16]24)
foreach($v in @(0,12288,0,0,0,0)){$b.Write([uint32]$v)};$b.Flush();$strf=Chunk 'strf' $m.ToArray()
$headers=ListChunk 'hdrl' (JoinBytes @($avih,(ListChunk 'strl' (JoinBytes @($strh,$strf)))))
$frames=[Collections.Generic.List[object]]::new();$expected=@()
for($i=0;$i -lt 24;$i++){
    $x=20+($i%12);$y=32;$pixels=[byte[]]::new(12288)
    for($dy=-3;$dy -le 3;$dy++){for($dx=-3;$dx -le 3;$dx++){$offset=((63-($y+$dy))*64+$x+$dx)*3;for($c=0;$c -lt 3;$c++){$pixels[$offset+$c]=255}}}
    $frames.Add((Chunk '00db' $pixels));$expected+=@{frame=$i;x=$x;y=$y;status='tracked';time_seconds=$i/10.0}
}
$movi=ListChunk 'movi' (JoinBytes $frames.ToArray())
[IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'synthetic.avi'),(Chunk 'RIFF' (JoinBytes @([Text.Encoding]::ASCII.GetBytes('AVI '),$headers,$movi))))
@{cx=.5;cy=.5;rx=.49;ry=.49;threshold=100;invert=$false;blur=0;min_area=20;max_area=100;max_extension=12;max_jump=50;arena_confirmed=$true} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'settings.json') -Encoding utf8
@{width=64;height=64;fps=10;frames=24;points=$expected;construction='White 7 by 7 square on black, integer symmetric centroid; uncompressed bottom-up BGR AVI.'} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'expected.json') -Encoding utf8
