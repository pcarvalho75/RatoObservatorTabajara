param([Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$WindowsArchive,[Parameter(Mandatory)][string]$EvidenceDirectory,[switch]$Online)
$ErrorActionPreference='Stop'
$PackageDirectory=(Resolve-Path -LiteralPath $PackageDirectory).Path
$WindowsArchive=(Resolve-Path -LiteralPath $WindowsArchive).Path
if(Test-Path -LiteralPath $EvidenceDirectory){throw 'Evidence directory must be fresh.'}
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($EvidenceDirectory)) | Out-Null
$EvidenceDirectory=(Resolve-Path -LiteralPath $EvidenceDirectory).Path
$receipt=@{state='RUNNING';production_rebuild=$false;online=[bool]$Online;scope='Exact extracted update engine and actual prepared WPF apphost; synthetic study only.';utc=[DateTime]::UtcNow.ToString('o')}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
try{
    $inventory=@(Get-Content -LiteralPath (Join-Path $PackageDirectory 'files.sha256.json') -Raw | ConvertFrom-Json)
    foreach($entry in $inventory){if((Hash (Join-Path $PackageDirectory $entry.path)) -ne $entry.sha256){throw 'Extracted package hash mismatch.'}}
    $desktop=Join-Path $PackageDirectory 'desktop'
    $stage=Join-Path $EvidenceDirectory 'verified-desktop-copy'
    Copy-Item -LiteralPath $desktop -Destination $stage -Recurse
    foreach($entry in $inventory | Where-Object {$_.path.Replace('\','/').StartsWith('desktop/')}){if((Hash (Join-Path $stage $entry.path.Substring(8))) -ne $entry.sha256){throw 'Copied desktop bytes differ.'}}
    $repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $project=Join-Path $repo 'tools/portable-update-probe/PortableUpdateProbe.csproj'
    if(-not(Test-Path -LiteralPath $project)){throw 'Run this script from matching corresponding-source/scripts/delivery.'}
    $probeSource=Join-Path (Split-Path $project) 'Program.cs'
    $sourceHashes=@(@{path=$PSCommandPath;sha256=Hash $PSCommandPath},@{path=$project;sha256=Hash $project},@{path=$probeSource;sha256=Hash $probeSource})
    $build=Join-Path $EvidenceDirectory 'probe-build'
    $obj=Join-Path $EvidenceDirectory 'probe-obj/'
    & dotnet build $project -c Release -p:PackageDirectory=$PackageDirectory -p:BaseIntermediateOutputPath=$obj -p:RestorePackagesWithLockFile=false -p:UseSharedCompilation=false -m:1 -o $build *> (Join-Path $EvidenceDirectory 'probe-build.log')
    if($LASTEXITCODE){throw 'Package-bound thin update probe compilation failed.'}
    foreach($name in @('PortableUpdateProbe.exe','PortableUpdateProbe.dll','PortableUpdateProbe.pdb')){Copy-Item -LiteralPath (Join-Path $build $name) -Destination $stage}
    $runtime=Get-Content -LiteralPath (Join-Path $desktop 'MouseTrackerTabajara.runtimeconfig.json') -Raw | ConvertFrom-Json
    if($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks){throw 'Desktop payload is not self-contained.'}
    $runtime | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $stage 'PortableUpdateProbe.runtimeconfig.json') -Encoding utf8
    $deps=Get-Content -LiteralPath (Join-Path $desktop 'MouseTrackerTabajara.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $deps.targets[$deps.runtimeTarget.name]['PortableUpdateProbe/1.0.0']=@{runtime=@{'PortableUpdateProbe.dll'=@{}}}
    $deps.libraries['PortableUpdateProbe/1.0.0']=@{type='project';serviceable=$false;sha512=''}
    $deps | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $stage 'PortableUpdateProbe.deps.json') -Encoding utf8
    $trace=Join-Path $EvidenceDirectory 'probe-host-trace.txt'
    $oldTrace=$env:DOTNET_HOST_TRACE;$oldTraceFile=$env:DOTNET_HOST_TRACEFILE
    try{
        $env:DOTNET_HOST_TRACE='1';$env:DOTNET_HOST_TRACEFILE=$trace
        $probeArguments=@($PackageDirectory,$WindowsArchive,(Join-Path $EvidenceDirectory 'exercise'))
        if($Online){$probeArguments+='--online'}
        $arguments=$probeArguments | ForEach-Object {'"'+$_+'"'}
        $process=Start-Process -FilePath (Join-Path $stage 'PortableUpdateProbe.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $EvidenceDirectory 'stdout.txt') -RedirectStandardError (Join-Path $EvidenceDirectory 'stderr.txt')
    }finally{$env:DOTNET_HOST_TRACE=$oldTrace;$env:DOTNET_HOST_TRACEFILE=$oldTraceFile}
    if(-not $process.WaitForExit(300000)){$process.Kill();throw 'Synthetic update probe timed out; evidence retained.'}
    if($process.ExitCode){throw 'Synthetic update probe failed; inspect retained stderr/result.'}
    if((Get-Content -LiteralPath $trace -Raw) -notmatch [regex]::Escape((Join-Path $stage 'coreclr.dll'))){throw 'Package-local probe runtime not established.'}
    foreach($source in $sourceHashes){if((Hash $source.path) -ne $source.sha256){throw 'Update proof source changed during execution.'}}
    foreach($entry in $inventory){if((Hash (Join-Path $PackageDirectory $entry.path)) -ne $entry.sha256){throw 'Original package changed during update proof.'}}
    $exercise=Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'exercise/updater-result.json') -Raw | ConvertFrom-Json
    if($exercise.state -ne 'PASS'){throw 'Update exercise did not record PASS.'}
    $receipt.state='PASS';$receipt.exercise=$exercise;$receipt.source_hashes=$sourceHashes;$receipt.archive_sha256=Hash $WindowsArchive;$receipt.package_manifest_sha256=Hash (Join-Path $PackageDirectory 'files.sha256.json')
}catch{$receipt.state='FAIL';$receipt.error=$_.Exception.ToString();throw}
finally{$receipt | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'update-gate-result.json') -Encoding utf8}
