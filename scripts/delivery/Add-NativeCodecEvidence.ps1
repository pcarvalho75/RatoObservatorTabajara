param([Parameter(Mandatory)][string]$PackageDirectory,[string]$VerifiedCache)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$config=Get-Content (Join-Path $PSScriptRoot 'native-codec-evidence.json') -Raw | ConvertFrom-Json
$PackageDirectory=(Resolve-Path $PackageDirectory).Path
foreach($kind in @('desktop','cli')){
    $dll=Join-Path $PackageDirectory "$kind/opencv_videoio_ffmpeg500_64.dll"
    if((Get-FileHash $dll -Algorithm SHA256).Hash -ne $config.expected_ffmpeg_sha256){throw "Unreviewed FFmpeg binary in $kind; audit must be refreshed."}
    $native=Join-Path $PackageDirectory "$kind/OpenCvSharpExtern.dll"
    if((Get-FileHash $native -Algorithm SHA256).Hash -ne $config.expected_opencvextern_sha256){throw "Unreviewed OpenCV native binary in $kind; audit must be refreshed."}
}
$dest=Join-Path $PackageDirectory 'native-codec-source'
if(Test-Path $dest){throw 'Native codec source evidence already exists; refusing overwrite.'}
New-Item -ItemType Directory $dest | Out-Null
$receipts=@()
foreach($item in @($config.archives)+@($config.header_supplements)){
    $dir=if($config.header_supplements.name -contains $item.name){Join-Path $dest 'openh264-header-supplement'}else{Join-Path $dest 'vendor-source-archives'}
    New-Item -ItemType Directory -Force $dir | Out-Null;$out=Join-Path $dir $item.name
    if($VerifiedCache -and (Test-Path (Join-Path $VerifiedCache $item.name))){Copy-Item (Join-Path $VerifiedCache $item.name) $out}else{Invoke-WebRequest $item.url -OutFile $out}
    $hash=(Get-FileHash $out -Algorithm SHA256).Hash
    if($hash -ne $item.sha256 -or (Get-Item $out).Length -ne $item.bytes){throw "Upstream source evidence digest/length mismatch: $($item.name)"}
    $receipts+=@{name=$item.name;source_url=$item.url;sha256=$hash;bytes=(Get-Item $out).Length}
}
Copy-Item (Join-Path $repo 'docs/delivery/native-codec-notices') (Join-Path $PackageDirectory 'licenses/native-codecs') -Recurse
Copy-Item (Join-Path $repo 'docs/delivery/NATIVE-CODEC-AUDIT.md') (Join-Path $dest 'AUDIT.md')
Copy-Item (Join-Path $PSScriptRoot 'native-codec-evidence.json') $dest
@{status=$config.status;factual_gate=$config.factual_gate;native_binary_hash_gate='PASS';source_digest_gate='PASS';files=$receipts;clean_windows_gate='UNRUN';missing_evidence=$config.missing_evidence} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $dest 'receipt.json') -Encoding utf8
