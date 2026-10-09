param([Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$EvidenceDirectory,[switch]$CleanMachine)
$ErrorActionPreference='Stop'
$PackageDirectory=(Resolve-Path $PackageDirectory).Path
if(Test-Path $EvidenceDirectory){throw 'Evidence directory must be new; no existing study will be overwritten.'}
New-Item -ItemType Directory $EvidenceDirectory | Out-Null
$EvidenceDirectory=(Resolve-Path $EvidenceDirectory).Path
$report=@{clean_machine_gate='UNRUN';host_test='RUNNING';host=[Environment]::OSVersion.VersionString;utc=[DateTime]::UtcNow.ToString('o')}
try {
    foreach($entry in (Get-Content (Join-Path $PackageDirectory 'files.sha256.json') -Raw | ConvertFrom-Json)){
        $path=Join-Path $PackageDirectory $entry.path
        if((Get-FileHash $path -Algorithm SHA256).Hash -ne $entry.sha256){throw "Hash mismatch: $($entry.path)"}
    }
    $dotnetInventory=@(Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    $report.dotnet_commands=$dotnetInventory
    $report.machine_runtimes_present=@('C:\Program Files\dotnet','C:\Program Files (x86)\dotnet') | Where-Object {Test-Path $_}
    if($CleanMachine){
        if($dotnetInventory.Count -or $report.machine_runtimes_present.Count){throw 'Clean-machine assertion rejected: installed dotnet is visible. Use a fresh Windows x64 VM with no SDK/runtime.'}
        $report.clean_machine_gate='RUNNING'
    }
    & (Join-Path $PSScriptRoot 'New-SyntheticFixture.ps1') -OutputDirectory (Join-Path $EvidenceDirectory 'fixture')
    $cli=Join-Path $PackageDirectory 'cli/MouseTrackerTabajara.Cli.exe'
    $savedTrace=$env:DOTNET_HOST_TRACE;$savedTraceFile=$env:DOTNET_HOST_TRACEFILE
    try {
        $env:DOTNET_HOST_TRACE='1';$env:DOTNET_HOST_TRACEFILE=Join-Path $EvidenceDirectory 'packaged-host-trace.txt'
        & $cli doctor 2>&1 | Out-File (Join-Path $EvidenceDirectory 'doctor.txt');if($LASTEXITCODE){throw 'Packaged doctor failed.'}
    } finally {$env:DOTNET_HOST_TRACE=$savedTrace;$env:DOTNET_HOST_TRACEFILE=$savedTraceFile}
    $trace=Get-Content (Join-Path $EvidenceDirectory 'packaged-host-trace.txt') -Raw -Encoding utf8
    if($trace -notmatch [regex]::Escape((Join-Path $PackageDirectory 'cli\coreclr.dll'))){throw 'Bundled CoreCLR resolution not proven by host trace.'}
    $report.bundled_coreclr='Host trace resolved package cli/coreclr.dll'
    & $cli analyze (Join-Path $EvidenceDirectory 'fixture/synthetic.avi') (Join-Path $EvidenceDirectory 'run') (Join-Path $EvidenceDirectory 'fixture/settings.json') 2>&1 | Out-File (Join-Path $EvidenceDirectory 'analyze.txt');if($LASTEXITCODE){throw 'Packaged analysis failed.'}
    $actual=Get-Content (Join-Path $EvidenceDirectory 'run/analysis.json') -Raw | ConvertFrom-Json
    $expected=Get-Content (Join-Path $EvidenceDirectory 'fixture/expected.json') -Raw | ConvertFrom-Json
    if($actual.points.Count -ne 24 -or $actual.metrics.tracked_frames -ne 24){throw 'Synthetic frame count mismatch.'}
    for($i=0;$i -lt 24;$i++){$a=$actual.points[$i];$e=$expected.points[$i];if($a.frame -ne $e.frame -or $a.x -ne $e.x -or $a.y -ne $e.y -or $a.status -ne $e.status -or [Math]::Abs($a.time_seconds-$e.time_seconds) -gt .001){throw "Synthetic row mismatch $i"}}
    $report.synthetic_rows=24
    $desktop=Join-Path $PackageDirectory 'desktop/MouseTrackerTabajara.exe'
    $study=Join-Path $EvidenceDirectory 'synthetic.study.db'
    $args=@('--study',$study,'--video',(Join-Path $EvidenceDirectory 'fixture/synthetic.avi'),'--analysis',(Join-Path $EvidenceDirectory 'run/analysis.json'),'--diagnostics',(Join-Path $EvidenceDirectory 'wpf.log'),'--smoke-screenshot',(Join-Path $EvidenceDirectory 'wpf.png'))
    $savedTrace=$env:DOTNET_HOST_TRACE;$savedTraceFile=$env:DOTNET_HOST_TRACEFILE
    try {
        $env:DOTNET_HOST_TRACE='1';$env:DOTNET_HOST_TRACEFILE=Join-Path $EvidenceDirectory 'wpf-host-trace.txt'
    $p=Start-Process -FilePath $desktop -ArgumentList ($args | ForEach-Object {'"'+$_+'"'}) -WindowStyle Hidden -PassThru
    } finally {$env:DOTNET_HOST_TRACE=$savedTrace;$env:DOTNET_HOST_TRACEFILE=$savedTraceFile}
    if(-not $p.WaitForExit(120000)){$p.Kill();throw 'WPF timed out.'}
    if($p.ExitCode -ne 0 -or -not(Test-Path (Join-Path $EvidenceDirectory 'wpf.png'))){throw 'Packaged WPF smoke failed.'}
    if((Get-Content (Join-Path $EvidenceDirectory 'wpf.log') -Raw) -notmatch 'Native WPF startup, layout and screenshot completed'){throw 'WPF completion receipt absent.'}
    $wpfTrace=Get-Content (Join-Path $EvidenceDirectory 'wpf-host-trace.txt') -Raw -Encoding utf8
    if($wpfTrace -notmatch [regex]::Escape((Join-Path $PackageDirectory 'desktop\coreclr.dll'))){throw 'Bundled WPF CoreCLR resolution not proven.'}
    $report.bundled_wpf_coreclr='Host trace resolved package desktop/coreclr.dll'
    # Same saved synthetic study reopened by an unchanged package, emulating rollback.
    $backup=Join-Path $EvidenceDirectory 'before-rollback.study.db';Copy-Item $study $backup
    $args=@('--study',$study,'--diagnostics',(Join-Path $EvidenceDirectory 'rollback-wpf.log'),'--smoke-screenshot',(Join-Path $EvidenceDirectory 'rollback-wpf.png'))
    $p=Start-Process -FilePath $desktop -ArgumentList ($args | ForEach-Object {'"'+$_+'"'}) -WindowStyle Hidden -PassThru
    if(-not $p.WaitForExit(120000)){$p.Kill();throw 'Rollback reopen timed out.'}
    if($p.ExitCode -ne 0){throw 'Synthetic study reopen failed.'}
    $report.rollback='Same-version reopen passed; cross-version schema compatibility UNRUN.'
    $report.host_test='PASS';if($CleanMachine){$report.clean_machine_gate='PASS'}
} catch {$report.host_test='FAIL';$report.error=$_.Exception.ToString();if($CleanMachine){$report.clean_machine_gate='FAIL'};throw}
finally {$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDirectory 'result.json') -Encoding utf8}


