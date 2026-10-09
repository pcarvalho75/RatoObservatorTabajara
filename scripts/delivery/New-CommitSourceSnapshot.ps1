param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string[]]$Paths,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
$resolved = (& git -C $Repository rev-parse --verify "$Commit^{commit}").Trim()
if ($LASTEXITCODE -or $resolved -notmatch '^[0-9a-f]{40}([0-9a-f]{24})?$') { throw 'Commit cannot be resolved.' }
if (Test-Path -LiteralPath $ArchivePath) { throw 'Source archive already exists; preserve earlier evidence.' }
if ((Test-Path -LiteralPath $Destination) -and (Get-ChildItem -LiteralPath $Destination -Force | Select-Object -First 1)) { throw 'Source snapshot destination must be empty.' }
# Archive committed blobs, never files read from the mutable checkout.
$tree = @(& git -C $Repository -c core.quotepath=false ls-tree -r $resolved -- @Paths)
if ($LASTEXITCODE -or $tree.Count -eq 0) { throw 'No committed source selected.' }
$expected = @{}
foreach ($line in $tree) {
    $parts = $line -split "`t", 2
    if ($parts.Count -ne 2) { throw 'Unsupported source pathname.' }
    $metadata = $parts[0] -split ' '
    $name = $parts[1]
    if ($metadata[0] -notin @('100644', '100755') -or $metadata[1] -ne 'blob') { throw "Non-file source entry is unsupported: $name" }
    if ($name -match '[\x00-\x1f\\]' -or $name.StartsWith('/') -or ($name -split '/') -contains '..') { throw "Unsafe source pathname: $name" }
    if ($name -match '\.(avi|mp4|mkv|mov|wmv|db|morrisdb|sqlite|sqlite3|log|trx)(-(wal|shm))?$') { throw "Forbidden source file: $name" }
    $expected[$name] = $metadata[2]
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$selectedPaths = @($Paths | Where-Object { $scope = $_.TrimEnd('/'); @($expected.Keys | Where-Object { $_ -eq $scope -or $_.StartsWith($scope + '/', [StringComparison]::Ordinal) }).Count -gt 0 })
& git -C $Repository -c core.autocrlf=false -c core.eol=lf archive --format=zip "--output=$ArchivePath" $resolved -- @selectedPaths
if ($LASTEXITCODE) { throw 'Committed source archive failed.' }
$zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
$seen = @{}
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        if (-not $expected.ContainsKey($entry.FullName) -or $seen.ContainsKey($entry.FullName)) { throw 'Archive source inventory differs from the committed tree.' }
        $target = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
        if (-not $target.StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source archive entry escaped its snapshot.' }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        $inputStream = $entry.Open()
        $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        # Verify Git object identity from raw archive bytes. export-subst, omissions
        # and local archive attributes must not silently alter claimed source.
        $algorithm = if ($expected[$entry.FullName].Length -eq 40) { [Security.Cryptography.HashAlgorithmName]::SHA1 } else { [Security.Cryptography.HashAlgorithmName]::SHA256 }
        $hash = [Security.Cryptography.IncrementalHash]::CreateHash($algorithm)
        $file = [IO.File]::OpenRead($target)
        try {
            $hash.AppendData([Text.Encoding]::ASCII.GetBytes("blob $($file.Length)" + [char]0))
            $buffer = [byte[]]::new(65536)
            while (($read = $file.Read($buffer, 0, $buffer.Length)) -gt 0) { $hash.AppendData($buffer, 0, $read) }
            $actual = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
            if ($actual -ne $expected[$entry.FullName]) { throw "Archived bytes differ from the committed blob: $($entry.FullName)" }
        } finally { $file.Dispose(); $hash.Dispose() }
        $seen[$entry.FullName] = $actual
    }
    if ($seen.Count -ne $expected.Count) { throw 'Archive omitted selected committed source files.' }
} finally { $zip.Dispose() }
[pscustomobject][ordered]@{ source_commit = $resolved; method = 'git archive with raw committed blob verification'; archive_sha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant(); files = $seen; verified_file_count = $seen.Count }
