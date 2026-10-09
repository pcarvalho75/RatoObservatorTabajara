$ErrorActionPreference = 'Stop'
$mouseRepository = Split-Path $PSScriptRoot -Parent
Push-Location $mouseRepository
try {
    dotnet restore MouseTrackerTabajara.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked package restore failed.' }
    dotnet build MouseTrackerTabajara.sln --no-restore -c Release -warnaserror -p:UseSharedCompilation=false -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    $tabajaraTestProjects = @(rg --files tests -g '*Tests.csproj' | Sort-Object)
    if ($LASTEXITCODE -ne 0 -or $tabajaraTestProjects.Count -eq 0) { throw 'No native test projects found.' }
    $tabajaraRunToken = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $tabajaraTestResultsDirectory = Join-Path $mouseRepository ('artifacts\tests\' + $tabajaraRunToken)
    [System.IO.Directory]::CreateDirectory($tabajaraTestResultsDirectory) | Out-Null
    Write-Output ('Test evidence: ' + $tabajaraTestResultsDirectory)
    foreach ($tabajaraTestProject in $tabajaraTestProjects) {
        $tabajaraTestName = [System.IO.Path]::GetFileNameWithoutExtension($tabajaraTestProject)
        dotnet test $tabajaraTestProject --no-build --no-restore -c Release --logger ('trx;LogFileName=' + $tabajaraTestName + '.trx') --results-directory $tabajaraTestResultsDirectory
        if ($LASTEXITCODE -ne 0) { throw ('Native checks failed: ' + $tabajaraTestProject) }
    }
} finally { Pop-Location }
