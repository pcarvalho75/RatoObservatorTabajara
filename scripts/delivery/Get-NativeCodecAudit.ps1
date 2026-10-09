param([Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
if(Test-Path $EvidenceDirectory){throw 'Audit evidence folder must be new.'}
New-Item -ItemType Directory $EvidenceDirectory | Out-Null
$config=Get-Content (Join-Path $PSScriptRoot 'native-codec-evidence.json') -Raw | ConvertFrom-Json
$PackageDirectory=(Resolve-Path $PackageDirectory).Path
$ffmpeg=Join-Path $PackageDirectory 'desktop/opencv_videoio_ffmpeg500_64.dll'
$native=Join-Path $PackageDirectory 'desktop/OpenCvSharpExtern.dll'
if((Get-FileHash $ffmpeg -Algorithm SHA256).Hash -ne $config.expected_ffmpeg_sha256 -or (Get-FileHash $native -Algorithm SHA256).Hash -ne $config.expected_opencvextern_sha256){throw 'Binary changed: no prior codec audit can be inherited.'}
# Audit probe runs on the development host, not a clean-machine execution test.
[Reflection.Assembly]::LoadFrom((Join-Path $PackageDirectory 'desktop/OpenCvSharp.dll')) | Out-Null
$handle=[Runtime.InteropServices.NativeLibrary]::Load($native)
[OpenCvSharp.Cv2]::GetBuildInformation() | Set-Content (Join-Path $EvidenceDirectory 'opencv-build-information.txt')
$text=[Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($ffmpeg))
$configLine=[regex]::Match($text,'--enable-cross-compile[^\x00]+').Value
$libraryLines=@([regex]::Matches($text,'[\x20-\x7e]{8,}') | ForEach-Object Value | Where-Object {$_ -match '^WebM Project VP[89].*v1\.16\.0|^AOMedia Project AV1.*3\.14\.1|^--target=x86_64-win64-gcc|^cmake ../aom|^libswscale license:'})
$configLine | Set-Content (Join-Path $EvidenceDirectory 'ffmpeg-configure.txt')
$libraryLines | Set-Content (Join-Path $EvidenceDirectory 'component-build-strings.txt')
[regex]::Match($text,'General configuration for OpenCV 5\.0\.0-pre[^\x00]+').Value | Set-Content (Join-Path $EvidenceDirectory 'plugin-opencv-build-information.txt')
@{ffmpeg_sha256=(Get-FileHash $ffmpeg -Algorithm SHA256).Hash;opencvextern_sha256=(Get-FileHash $native -Algorithm SHA256).Hash;factual_gate=$config.factual_gate;full_native_source_gate=$config.status;clean_windows_gate='UNRUN';probe='Installed development-host audit only';ffmpeg_configure=$configLine;component_strings=$libraryLines} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDirectory 'binary-audit.json')
