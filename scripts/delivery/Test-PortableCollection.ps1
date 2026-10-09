param([Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$EvidenceDirectory,[switch]$Execute,[int]$PhaseTimeoutSeconds=600,[string]$ProbeProject)
$ErrorActionPreference='Stop'
$PackageDirectory=(Resolve-Path -LiteralPath $PackageDirectory).Path
if(Test-Path -LiteralPath $EvidenceDirectory){throw 'Use a fresh evidence directory; previous failure/success evidence is never overwritten.'}
New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
$EvidenceDirectory=(Resolve-Path -LiteralPath $EvidenceDirectory).Path
$receipt=@{state='PREPARING';clean_windows_gate='UNRUN';scientific_accuracy='UNRUN';package=$PackageDirectory;utc=[DateTime]::UtcNow.ToString('o');execution_requested=[bool]$Execute}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function Verify-Payload([string]$root){
    foreach($entry in $inventory){
        $path=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
        if(-not $path.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path escapes package.'}
        if((Hash $path) -ne $entry.sha256){throw "Package payload mismatch: $($entry.path)"}
    }
}
function Run-Child([string]$exe,[string[]]$arguments,[string]$name,[string]$runtime){
    $trace=Join-Path $EvidenceDirectory "$name-host-trace.txt"
    $oldTrace=$env:DOTNET_HOST_TRACE;$oldFile=$env:DOTNET_HOST_TRACEFILE;$oldCore=$env:COREHOST_TRACE;$oldCoreFile=$env:COREHOST_TRACEFILE
    try{
        $env:DOTNET_HOST_TRACE='1';$env:DOTNET_HOST_TRACEFILE=$trace;$env:COREHOST_TRACE='1';$env:COREHOST_TRACEFILE=$trace
        $quoted=@($arguments | ForEach-Object {if($_ -match '"'){throw 'Quotes in gate paths are unsupported.'};'"'+$_+'"'})
        $process=Start-Process -FilePath $exe -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $EvidenceDirectory "$name-stdout.txt") -RedirectStandardError (Join-Path $EvidenceDirectory "$name-stderr.txt")
    }finally{$env:DOTNET_HOST_TRACE=$oldTrace;$env:DOTNET_HOST_TRACEFILE=$oldFile;$env:COREHOST_TRACE=$oldCore;$env:COREHOST_TRACEFILE=$oldCoreFile}
    @{pid=$process.Id;exe=$exe;args=$arguments;utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceDirectory "$name-launch.json") -Encoding utf8
    if(-not $process.WaitForExit($PhaseTimeoutSeconds*1000)){$process.Kill();throw "$name timed out; partial evidence retained."}
    if($process.ExitCode -ne 0){throw "$name failed with exit $($process.ExitCode)."}
    if(-not(Test-Path -LiteralPath $trace) -or (Get-Content -LiteralPath $trace -Raw) -notmatch [regex]::Escape((Join-Path $runtime 'coreclr.dll'))){throw "$name did not prove the package-local CoreCLR path."}
}
try{
    $inventory=@(Get-Content -LiteralPath (Join-Path $PackageDirectory 'files.sha256.json') -Raw | ConvertFrom-Json)
    Verify-Payload $PackageDirectory
    $desktop=Join-Path $PackageDirectory 'desktop'
    foreach($required in @('MouseTrackerTabajara.exe','MouseTrackerTabajara.dll','MouseTrackerTabajara.Application.dll','MouseTrackerTabajara.Core.dll','MouseTrackerTabajara.Storage.dll','MouseTrackerTabajara.Tracking.dll','coreclr.dll','hostfxr.dll','OpenCvSharpExtern.dll','e_sqlite3.dll')){if(-not(Test-Path -LiteralPath (Join-Path $desktop $required))){throw "Exact desktop payload missing $required"}}
    $stage=Join-Path $EvidenceDirectory 'verified-desktop-copy'
    Copy-Item -LiteralPath $desktop -Destination $stage -Recurse
    # Copy-Item semantics can nest an existing destination. Fail rather than guessing payload placement.
    if(-not(Test-Path -LiteralPath (Join-Path $stage 'coreclr.dll'))){throw 'Staged desktop copy is not flat as expected.'}
    $productionHashes=@{}
    foreach($entry in $inventory | Where-Object {$_.path.Replace('\','/').StartsWith('desktop/')}){
        $relative=$entry.path.Substring(8);$file=Join-Path $stage $relative
        if((Hash $file) -ne $entry.sha256){throw "Copied production file differs: $relative"};$productionHashes[$relative]=$entry.sha256
    }
    $repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    # Run from matching corresponding-source/scripts/delivery, or explicitly supply
    # -ProbeProject when invoking the copied package/validation script. The SDK
    # compiles only this thin gate; production references stay package-bound.
    $project=if($ProbeProject){$ProbeProject}else{Join-Path $repo 'tools/portable-collection-probe/PortableCollectionProbe.csproj'}
    if(-not(Test-Path -LiteralPath $project)){throw 'Thin probe source missing. Invoke from matching corresponding-source/scripts/delivery or pass -ProbeProject <source>/tools/portable-collection-probe/PortableCollectionProbe.csproj.'}
    $project=(Resolve-Path -LiteralPath $project).Path
    if([IO.Path]::GetFileName($project) -ne 'PortableCollectionProbe.csproj'){throw 'ProbeProject must name the thin PortableCollectionProbe.csproj; production rebuild is not authorized by this gate.'}
    $probeSource=Join-Path (Split-Path $project -Parent) 'Program.cs'
    $sourceHashes=@(@{path=$PSCommandPath;sha256=Hash $PSCommandPath},@{path=$project;sha256=Hash $project},@{path=$probeSource;sha256=Hash $probeSource})
    $sourceRoot=Split-Path (Split-Path (Split-Path $project -Parent) -Parent) -Parent
    foreach($name in @('Directory.Build.props','global.json')){$file=Join-Path $sourceRoot $name;if(Test-Path -LiteralPath $file){$sourceHashes+=@{path=$file;sha256=Hash $file}}}
    $receipt.source_hashes=$sourceHashes;$receipt.package_manifest_sha256=Hash (Join-Path $PackageDirectory 'files.sha256.json')
    $receipt.source_invocation=@{script=$PSCommandPath;probe_project=$project;explicit_project_override=[bool]$ProbeProject;production_rebuild=$false}
    $build=Join-Path $EvidenceDirectory 'probe-build';$obj=Join-Path $EvidenceDirectory 'probe-obj/'
    & dotnet build $project -c Release -p:PackageDirectory=$PackageDirectory -p:BaseIntermediateOutputPath=$obj -p:RestorePackagesWithLockFile=false -p:TreatWarningsAsErrors=true -o $build *> (Join-Path $EvidenceDirectory 'probe-build.log')
    if($LASTEXITCODE){throw 'Thin probe compilation failed; no production rebuild was requested.'}
    foreach($source in $sourceHashes){if((Hash $source.path) -ne $source.sha256){throw 'Gate/probe source changed during compilation.'}}
    foreach($name in @('PortableCollectionProbe.exe','PortableCollectionProbe.dll','PortableCollectionProbe.pdb')){Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $stage $name)}
    $runtime=Get-Content -LiteralPath (Join-Path $desktop 'MouseTrackerTabajara.runtimeconfig.json') -Raw | ConvertFrom-Json
    if($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks){throw 'Desktop package runtimeconfig is not self-contained; do not fall back to developer runtimes.'}
    $runtime | ConvertTo-Json -Depth 50 | Set-Content (Join-Path $stage 'PortableCollectionProbe.runtimeconfig.json') -Encoding utf8
    # Derive only NEW probe deps; preserve every original production deps/runtimeconfig byte.
    $deps=Get-Content -LiteralPath (Join-Path $desktop 'MouseTrackerTabajara.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $target=$deps.targets[$deps.runtimeTarget.name]
    $target['PortableCollectionProbe/1.0.0']=@{runtime=@{'PortableCollectionProbe.dll'=@{}}}
    $deps.libraries['PortableCollectionProbe/1.0.0']=@{type='project';serviceable=$false;sha512=''}
    $deps | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $stage 'PortableCollectionProbe.deps.json') -Encoding utf8
    $productionHashes | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceDirectory 'production-copy-hashes.json') -Encoding utf8
    $receipt.probe_files=@('PortableCollectionProbe.exe','PortableCollectionProbe.dll','PortableCollectionProbe.deps.json','PortableCollectionProbe.runtimeconfig.json') | ForEach-Object {@{file=$_;sha256=Hash (Join-Path $stage $_)}}
    if(-not $Execute){$receipt.state='PREPARED_EXECUTION_UNRUN';return}
    $probe=Join-Path $stage 'PortableCollectionProbe.exe';$collection=Join-Path $EvidenceDirectory 'collection'
    foreach($phase in @('prepare','resume','report')){Run-Child $probe @($phase,$collection,$PackageDirectory) $phase $stage}
    $study=Join-Path $collection 'completed.tabajara'
    Run-Child (Join-Path $desktop 'MouseTrackerTabajara.exe') @('--study',$study,'--diagnostics',(Join-Path $EvidenceDirectory 'saved-project-wpf.log'),'--smoke-screenshot',(Join-Path $EvidenceDirectory 'saved-project-wpf.png')) 'saved-project-desktop' $desktop
    if(-not(Test-Path -LiteralPath (Join-Path $EvidenceDirectory 'saved-project-wpf.png'))){throw 'Actual packaged Desktop saved-project render absent.'}
    foreach($relative in $productionHashes.Keys){if((Hash (Join-Path $stage $relative)) -ne $productionHashes[$relative]){throw "Production stage bytes changed: $relative"}}
    Verify-Payload $PackageDirectory
    foreach($source in $sourceHashes){if((Hash $source.path) -ne $source.sha256){throw 'Gate/probe source changed during execution.'}}
    $receipt.state='PASS';$receipt.collection=@{recordings=30;groups=4;animals=4;expected_slots=84;mapped_slots=30;missing_slots=54;mapping=@(21,4,3,2);resume='prior10 reused,20 new';source_video_bytes='UNCHANGED';scope='Synthetic tooling only; not cohort completion or accuracy'}
}catch{$receipt.state='FAIL';$receipt.error=$_.Exception.ToString();throw}
finally{$receipt | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $EvidenceDirectory 'collection-gate-result.json') -Encoding utf8}
