param([Parameter(Mandatory=$true)][string]$Destination,[switch]$ReceiptExisting)
$ErrorActionPreference='Stop'
$repoRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$destinationPath=[IO.Path]::GetFullPath($Destination)
if(-not $ReceiptExisting){if(Test-Path -LiteralPath $destinationPath){throw 'Pin destination already exists; use a new path.'};Push-Location $repoRoot;try{& dotnet restore tools/validation-benchmark/ValidationBenchmark.csproj --locked-mode;if($LASTEXITCODE -ne 0){throw 'Locked restore failed'};& dotnet publish tools/validation-benchmark/ValidationBenchmark.csproj -c Release --no-restore -warnaserror -o $destinationPath;if($LASTEXITCODE -ne 0){throw 'Pinned publish failed'}}finally{Pop-Location}}
if(-not(Test-Path -LiteralPath (Join-Path $destinationPath 'ValidationBenchmark.exe'))){throw 'Runner executable missing.'}
$receiptPath=Join-Path $destinationPath 'pin.json';if(Test-Path -LiteralPath $receiptPath){throw 'Pin receipt exists; do not rewrite.'}
$files=@(Get-ChildItem -LiteralPath $destinationPath -Recurse -File|Sort-Object FullName|ForEach-Object{[ordered]@{File=$_.FullName.Substring($destinationPath.Length+1);Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
$pin=[ordered]@{CreatedUtc=[DateTime]::UtcNow.ToString('O');SourceHead=(& git -C $repoRoot rev-parse HEAD);SourceStatus=@(& git -C $repoRoot status --short);FixtureSha256=(Get-FileHash -LiteralPath (Join-Path $repoRoot 'tests/MouseTrackerTabajara.Validation.Tests/ThirtyRecordingPerformanceFixture.cs') -Algorithm SHA256).Hash;RunnerSourceSha256=(Get-FileHash -LiteralPath (Join-Path $repoRoot 'tools/validation-benchmark/Program.cs') -Algorithm SHA256).Hash;Files=$files;Scope='Frozen framework-dependent runner; existing host runtime, not clean-Windows proof. Never republish this folder.'}
[IO.File]::WriteAllText($receiptPath,($pin|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256
