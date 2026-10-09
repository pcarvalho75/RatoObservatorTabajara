param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(Test-Path $OutputDirectory){throw 'Build output must be new.'}
$identity=Get-Content (Join-Path $repo 'DELIVERY-SOURCE.json') -Raw | ConvertFrom-Json
foreach($project in @('Desktop','Cli')){
    $path=Join-Path $repo "src/MouseTrackerTabajara.$project/MouseTrackerTabajara.$project.csproj"
    & dotnet publish $path -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.12 -p:RestoreLockedMode=true -p:ContinuousIntegrationBuild=true -p:PublishTrimmed=false -p:PublishSingleFile=false "-p:Version=$($identity.version)" "-p:SourceRevisionId=$($identity.source_commit)" -o (Join-Path $OutputDirectory $project.ToLowerInvariant())
    if($LASTEXITCODE){throw 'Locked source replay failed.'}
}

