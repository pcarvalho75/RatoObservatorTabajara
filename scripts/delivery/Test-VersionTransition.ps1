param([Parameter(Mandatory)][string]$PreviousPackage,[Parameter(Mandatory)][string]$NewPackage,[Parameter(Mandatory)][string]$SyntheticEvidence,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path $OutputDirectory){throw 'Transition evidence must be new.'}
New-Item -ItemType Directory $OutputDirectory | Out-Null;$OutputDirectory=(Resolve-Path $OutputDirectory).Path
$PreviousPackage=(Resolve-Path $PreviousPackage).Path;$NewPackage=(Resolve-Path $NewPackage).Path;$SyntheticEvidence=(Resolve-Path $SyntheticEvidence).Path
$report=@{status='RUNNING';scope='Synthetic version switch; does not prove compatibility with future schema migrations.'}
function OpenStudy([string]$Package,[string]$Study,[string]$Tag,[bool]$Import){
    $args=@('--study',$Study,'--diagnostics',(Join-Path $OutputDirectory "$Tag.log"),'--smoke-screenshot',(Join-Path $OutputDirectory "$Tag.png"))
    if($Import){$args+=@('--video',(Join-Path $SyntheticEvidence 'fixture/synthetic.avi'),'--analysis',(Join-Path $SyntheticEvidence 'run/analysis.json'))}
    $p=Start-Process (Join-Path $Package 'desktop/MouseTrackerTabajara.exe') -ArgumentList ($args | ForEach-Object {'"'+$_+'"'}) -WindowStyle Hidden -PassThru
    if(-not $p.WaitForExit(120000)){$p.Kill();throw "Timeout: $Tag"};if($p.ExitCode -ne 0){throw "WPF failed: $Tag"}
    if((Get-Content (Join-Path $OutputDirectory "$Tag.log") -Raw) -notmatch 'Native WPF startup, layout and screenshot completed'){throw "Missing completion receipt: $Tag"}
}
try {
    $previous=Get-Content (Join-Path $PreviousPackage 'build-identity.json') -Raw | ConvertFrom-Json
    $next=Get-Content (Join-Path $NewPackage 'build-identity.json') -Raw | ConvertFrom-Json
    if($previous.version -eq $next.version){throw 'Use two distinct development versions.'}
    $report.previous=$previous;$report.next=$next
    $original=Join-Path $OutputDirectory 'original.study.db';OpenStudy $PreviousPackage $original 'previous-create' $true
    $backup=Join-Path $OutputDirectory 'backup.study.db';Copy-Item $original $backup
    $before=(Get-FileHash $original -Algorithm SHA256).Hash;$backupHash=(Get-FileHash $backup -Algorithm SHA256).Hash
    $update=Join-Path $OutputDirectory 'update-copy.study.db';Copy-Item $backup $update;OpenStudy $NewPackage $update 'new-open-copy' $false
    $rollback=Join-Path $OutputDirectory 'rollback-copy.study.db';Copy-Item $backup $rollback;OpenStudy $PreviousPackage $rollback 'previous-open-backup-copy' $false
    if((Get-FileHash $original -Algorithm SHA256).Hash -ne $before -or (Get-FileHash $backup -Algorithm SHA256).Hash -ne $backupHash){throw 'Original or backup changed.'}
    foreach($suffix in @('-wal','-shm')){if((Test-Path "$original$suffix") -or (Test-Path "$backup$suffix")){throw 'Unexpected live database sidecar after closing app.'}}
    $report.original_sha256=$before;$report.backup_sha256=$backupHash;$report.status='PASS'
} catch {$report.status='FAIL';$report.error=$_.Exception.ToString();throw}
finally {$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'transition-result.json') -Encoding utf8}
