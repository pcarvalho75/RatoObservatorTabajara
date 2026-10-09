param([string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path ([IO.Path]::GetTempPath()) ('tabajara-commit-snapshot-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a new evidence directory; preserve earlier tests.' }
New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
$repo = Join-Path $EvidenceDirectory 'repo'
New-Item -ItemType Directory -Path (Join-Path $repo 'src') -Force | Out-Null
& git -C $repo init --quiet
& git -C $repo config user.email 'synthetic@example.invalid'
& git -C $repo config user.name 'Synthetic snapshot test'
& git -C $repo config core.autocrlf false
$text = Join-Path $repo 'src/with space.cs'
$binary = Join-Path $repo 'src/binary.dat'
$original = [Text.Encoding]::UTF8.GetBytes("// synthetic committed version`n")
[IO.File]::WriteAllBytes($text, $original)
[IO.File]::WriteAllBytes($binary, [byte[]]@(0, 1, 10, 13, 255))
& git -C $repo add src
& git -C $repo commit --quiet -m 'Freeze synthetic source'
if ($LASTEXITCODE) { throw 'Synthetic commit failed.' }
$commit = (& git -C $repo rev-parse HEAD).Trim()
& git -C $repo config core.autocrlf true
[IO.File]::WriteAllText($text, 'LIVE CHANGED CHECKOUT MUST NOT ENTER RELEASE')
[IO.File]::WriteAllBytes($binary, [byte[]]@(42, 43))
$helper = Join-Path $PSScriptRoot 'New-CommitSourceSnapshot.ps1'
$receipt = & $helper -Repository $repo -Commit $commit -Paths @('src', 'not-present.txt') -Destination (Join-Path $EvidenceDirectory 'snapshot') -ArchivePath (Join-Path $EvidenceDirectory 'source.zip')
$actual = [IO.File]::ReadAllBytes((Join-Path $EvidenceDirectory 'snapshot/src/with space.cs'))
if (-not [Linq.Enumerable]::SequenceEqual[byte]($actual, $original)) { throw 'Mutable working file entered snapshot.' }
$binaryActual = [IO.File]::ReadAllBytes((Join-Path $EvidenceDirectory 'snapshot/src/binary.dat'))
if (-not [Linq.Enumerable]::SequenceEqual[byte]($binaryActual, [byte[]]@(0, 1, 10, 13, 255)) -or $receipt.verified_file_count -ne 2) { throw 'Raw binary/source inventory differs.' }
$archiveBefore = (Get-FileHash -LiteralPath (Join-Path $EvidenceDirectory 'source.zip')).Hash
$refused = $false
try { & $helper -Repository $repo -Commit $commit -Paths @('src') -Destination (Join-Path $EvidenceDirectory 'second') -ArchivePath (Join-Path $EvidenceDirectory 'source.zip') | Out-Null } catch { $refused = $_.Exception.Message -like '*already exists*' }
if (-not $refused -or (Get-FileHash -LiteralPath (Join-Path $EvidenceDirectory 'source.zip')).Hash -ne $archiveBefore) { throw 'Existing source evidence was overwritten.' }
[IO.File]::WriteAllText((Join-Path $repo '.gitattributes'), 'src/binary.dat export-ignore')
& git -C $repo add .gitattributes
& git -C $repo commit --quiet -m 'Synthetic archive omission'
$omittedCommit = (& git -C $repo rev-parse HEAD).Trim()
$refused = $false
try { & $helper -Repository $repo -Commit $omittedCommit -Paths @('src') -Destination (Join-Path $EvidenceDirectory 'omission') -ArchivePath (Join-Path $EvidenceDirectory 'omission.zip') | Out-Null } catch { $refused = $_.Exception.Message -like '*omitted*' }
if (-not $refused) { throw 'Attribute-omitted source was silently accepted.' }
[IO.File]::WriteAllText((Join-Path $repo 'src/private.morrisdb'), 'SYNTHETIC FORBIDDEN INPUT')
& git -C $repo add src/private.morrisdb
& git -C $repo commit --quiet -m 'Synthetic forbidden study file'
$privateCommit = (& git -C $repo rev-parse HEAD).Trim()
$refused = $false
try { & $helper -Repository $repo -Commit $privateCommit -Paths @('src') -Destination (Join-Path $EvidenceDirectory 'forbidden') -ArchivePath (Join-Path $EvidenceDirectory 'forbidden.zip') | Out-Null } catch { $refused = $_.Exception.Message -like '*Forbidden source file*' }
if (-not $refused -or (Test-Path -LiteralPath (Join-Path $EvidenceDirectory 'forbidden.zip'))) { throw 'Forbidden study file was archived.' }
[ordered]@{ status = 'PASS'; cases = @('committed text/binary survives live checkout changes', 'existing archive preserved', 'archive omission rejected', 'private study file rejected before archive'); receipt = $receipt; evidence_directory = $EvidenceDirectory } | ConvertTo-Json -Depth 8 | Tee-Object -FilePath (Join-Path $EvidenceDirectory 'result.json')
