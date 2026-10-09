param([string]$Version='0.1.0-dev',[string]$OutputRoot)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(-not $OutputRoot){$OutputRoot=Join-Path $repo 'artifacts/delivery'}
$commit=(& git -C $repo rev-parse HEAD).Trim();$short=$commit.Substring(0,12)
if($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+-dev[.A-Za-z0-9-]*$'){throw 'Use an explicit development version, for example 0.1.0-dev.1.'}
if((& git -C $repo status --porcelain --untracked-files=no)){throw 'Commit tracked source changes before packaging.'}
$root=Join-Path $OutputRoot "$Version-$short-win-x64"
if(Test-Path $root){throw 'Version folder already exists; choose a new dev version. Never overwrite a release.'}
New-Item -ItemType Directory -Force $root | Out-Null
$root=(Resolve-Path $root).Path;$stage=Join-Path $root 'build-source';New-Item -ItemType Directory $stage | Out-Null
# An explicit source allowlist excludes coordination, private reference rows, logs and media.
$sourcePaths=@('src','features','tests','scripts','tools/portable-collection-probe','tools/portable-update-probe','docs/delivery','Directory.Build.props','global.json','MouseTrackerTabajara.sln','LICENSE.txt','README.txt','docs/SOURCE-PROVENANCE.txt','docs/ROADMAP.html','docs/ROADMAP.json','fixtures/mask-reference-cases.json','fixtures/metric-reference-cases.json')
$snapshot=& (Join-Path $PSScriptRoot 'New-CommitSourceSnapshot.ps1') -Repository $repo -Commit $commit -Paths $sourcePaths -Destination $stage -ArchivePath (Join-Path $root 'committed-source.zip')
$snapshot | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'source-snapshot.json') -Encoding utf8
$files=@($snapshot.files.Keys | Sort-Object)
$originalLocks=@{}
Get-ChildItem (Join-Path $stage 'src') -Recurse -Filter packages.lock.json | ForEach-Object {$originalLocks[$_.FullName]=Get-Content $_.FullName -Raw | ConvertFrom-Json -AsHashtable}
$projects=@('Desktop','Cli')
$log=Join-Path $root 'build.log'
foreach($project in $projects){
    $path=Join-Path $stage "src/MouseTrackerTabajara.$project/MouseTrackerTabajara.$project.csproj"
    & dotnet restore $path -r win-x64 -p:SelfContained=true -p:RuntimeFrameworkVersion=10.0.12 --force-evaluate 2>&1 | Tee-Object -FilePath $log -Append
    if($LASTEXITCODE){throw 'RID lock derivation failed.'}
}
foreach($path in $originalLocks.Keys){
    $old=$originalLocks[$path];$new=Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
    foreach($target in $old.dependencies.Keys){foreach($id in $old.dependencies[$target].Keys){$a=$old.dependencies[$target][$id];$b=$new.dependencies[$target][$id];if($a.type -ne 'Project' -and ($a.resolved -ne $b.resolved -or $a.contentHash -ne $b.contentHash)){throw "Pinned package changed: $id"}}}
}
$package=Join-Path $root 'portable';New-Item -ItemType Directory $package | Out-Null
foreach($project in $projects){
    $path=Join-Path $stage "src/MouseTrackerTabajara.$project/MouseTrackerTabajara.$project.csproj"
    & dotnet publish $path -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.12 -p:RestoreLockedMode=true -p:ContinuousIntegrationBuild=true -p:PublishTrimmed=false -p:PublishSingleFile=false -p:Version=$Version -p:SourceRevisionId=$commit -o (Join-Path $package $project.ToLowerInvariant()) 2>&1 | Tee-Object -FilePath $log -Append
    if($LASTEXITCODE){throw "Locked publish failed: $project"}
}
$licenses=Join-Path $package 'licenses';New-Item -ItemType Directory $licenses | Out-Null
Copy-Item (Join-Path $stage 'LICENSE.txt') (Join-Path $licenses 'APPLICATION-GPL-3.0.txt')
Copy-Item (Join-Path $stage 'docs/SOURCE-PROVENANCE.txt') $licenses
Copy-Item (Join-Path $stage 'docs/delivery/licenses/*') $licenses
New-Item -ItemType Directory (Join-Path $package 'docs') | Out-Null
Copy-Item (Join-Path $stage 'docs/ROADMAP.*') (Join-Path $package 'docs')
foreach($guideName in @('COLLABORATOR-TEST.md','QUESTION-COVERAGE.md','SHOWCASE.md','LUCIANA-PROTOCOL.md','REPORT-BOUNDARIES.md','PERFORMANCE-CHECK.md','STUDY-INTAKE.md','AUTOMATIC-UPDATES.md')){
    $guidePath=Join-Path $stage ('docs/delivery/'+$guideName)
    if(Test-Path -LiteralPath $guidePath){Copy-Item -LiteralPath $guidePath -Destination (Join-Path $package ('docs/'+$guideName))}
}
$showcasePath=Join-Path $stage 'docs/delivery/showcase'
if(Test-Path -LiteralPath $showcasePath){Copy-Item -LiteralPath $showcasePath -Destination (Join-Path $package 'docs/showcase') -Recurse}
$inventory=@{};$cache=Join-Path $env:USERPROFILE '.nuget/packages'
foreach($project in $projects){
    $assets=Get-Content (Join-Path $stage "src/MouseTrackerTabajara.$project/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    foreach($key in $assets.libraries.Keys){$lib=$assets.libraries[$key];if($lib.type -ne 'package'){continue};$folder=Join-Path $cache $lib.path;$nuspec=Get-ChildItem $folder -Filter '*.nuspec' | Select-Object -First 1
        if(-not $nuspec){throw "Missing license metadata: $key"}
        [xml]$xml=Get-Content $nuspec.FullName -Raw;$metadata=$xml.package.metadata
        $safe=$key.Replace('/','-');$dest=Join-Path $licenses $safe;New-Item -ItemType Directory -Force $dest | Out-Null;Copy-Item $nuspec.FullName $dest
        $noticeFiles=@(Get-ChildItem $folder -Recurse -File | Where-Object {$_.Name -match '^(LICENSE|COPYING|NOTICE|Third[-_]?Party[-_]?Notices)' -and $_.Extension -ne '.dll'})
        foreach($notice in $noticeFiles){$relative=$notice.FullName.Substring($folder.Length+1);$out=Join-Path $dest $relative;New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null;Copy-Item $notice.FullName $out}
        $inventory[$key]=@{id=[string]$metadata.id;version=[string]$metadata.version;declared_license=[string]$metadata.license.InnerText;license_type=[string]$metadata.license.type;license_url=[string]$metadata.licenseUrl;repository_url=[string]$metadata.repository.url;repository_commit=[string]$metadata.repository.commit;content_sha512=$lib.sha512;nupkg_sha256=(Get-FileHash (Get-ChildItem $folder -Filter '*.nupkg' | Select-Object -First 1).FullName -Algorithm SHA256).Hash;notice_files=@($noticeFiles | ForEach-Object {$_.FullName.Substring($folder.Length+1)})}
    }
}
# Runtime packs are downloaded by the SDK and may not appear as libraries in assets.
foreach($name in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')){
    $folder=Join-Path $cache "$name/10.0.12";if(-not(Test-Path $folder)){throw "Pinned runtime pack missing: $name"}
    $dest=Join-Path $licenses "$name-10.0.12";New-Item -ItemType Directory -Force $dest | Out-Null
    Get-ChildItem $folder -Recurse -File | Where-Object {$_.Name -match '^(LICENSE|COPYING|NOTICE|Third[-_]?Party[-_]?Notices)' -or $_.Extension -eq '.nuspec'} | ForEach-Object {$relative=$_.FullName.Substring($folder.Length+1);$out=Join-Path $dest $relative;New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null;Copy-Item $_.FullName $out}
}
@($inventory.Values) | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $package 'dependency-provenance.json') -Encoding utf8
& (Join-Path $stage 'scripts/delivery/Add-NativeCodecEvidence.ps1') -PackageDirectory $package
Copy-Item (Join-Path $stage 'scripts/delivery') (Join-Path $package 'validation') -Recurse
Copy-Item (Join-Path $stage 'docs/delivery/PORTABLE.md') (Join-Path $package 'README.md')
@('@echo off','"%~dp0desktop\MouseTrackerTabajara.exe" %*') | Set-Content (Join-Path $package 'Start Mouse Tracker Tabajara.cmd') -Encoding ascii
@{version=$Version;source_commit=$commit;sdk_version=(& dotnet --version).Trim();configuration='Release';rid='win-x64';self_contained=$true;runtime_version='10.0.12';signed=$false;identity='UNSIGNED DEVELOPMENT DISTRIBUTION';clean_machine_gate='UNRUN';scientific_validation='NOT ESTABLISHED';dependency_license_gate='Hash-bound official FFmpeg codec binary/source and notices verified; full native source/toolchain/compliance audit remains OPEN';built_utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content (Join-Path $package 'build-identity.json') -Encoding utf8
# Corresponding app source contains exact RID locks and build scripts, without bin/obj.
$source=Join-Path $root 'corresponding-source';New-Item -ItemType Directory $source | Out-Null
foreach($file in $files){$dest=Join-Path $source $file;New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null;Copy-Item (Join-Path $stage $file) $dest}
@{source_commit=$commit;version=$Version;sdk_version=(& dotnet --version).Trim();rid_locks='Derived in isolated snapshot; original resolved versions and content hashes verified unchanged.';runtime_version='10.0.12';command='powershell -NoProfile -ExecutionPolicy Bypass -File scripts/delivery/Replay-Build.ps1 -OutputDirectory C:\TabajaraBuild'} | ConvertTo-Json | Set-Content (Join-Path $source 'DELIVERY-SOURCE.json')
Compress-Archive -Path (Join-Path $source '*') -DestinationPath (Join-Path $package 'corresponding-source.zip')
$manifest=@(Get-ChildItem $package -Recurse -File | Sort-Object FullName | ForEach-Object {@{path=$_.FullName.Substring($package.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $package 'files.sha256.json') -Encoding utf8
Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $root "$Version-$short-win-x64.zip")
Get-FileHash (Join-Path $root "$Version-$short-win-x64.zip") -Algorithm SHA256 | Format-List | Out-File (Join-Path $root 'archive-sha256.txt')
Write-Output $package


