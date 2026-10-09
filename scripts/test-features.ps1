$ErrorActionPreference = 'Stop'
$tabajaraRepository = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $tabajaraRepository
try {
    $tabajaraProjects = @(rg --files features -g '*Tests.csproj' | Sort-Object)
    if ($LASTEXITCODE -ne 0 -or $tabajaraProjects.Count -eq 0) { throw 'No feature test projects found.' }
    foreach ($tabajaraProject in $tabajaraProjects) {
        Write-Output ('Checking feature tests: ' + $tabajaraProject)
        dotnet restore $tabajaraProject --locked-mode
        if ($LASTEXITCODE -ne 0) { throw ('Locked restore failed: ' + $tabajaraProject) }
        $tabajaraResultFolder = Join-Path (Split-Path -Parent $tabajaraProject) 'artifacts'
        dotnet test $tabajaraProject -c Release --no-restore --logger 'trx;LogFileName=feature-tests.trx' --results-directory $tabajaraResultFolder
        if ($LASTEXITCODE -ne 0) { throw ('Feature checks failed: ' + $tabajaraProject) }
    }
} finally { Pop-Location }
