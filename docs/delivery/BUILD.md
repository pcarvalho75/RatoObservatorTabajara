# Build and validation pipeline

From a clean committed checkout in PowerShell 7:

```
pwsh -NoProfile -File scripts/delivery/Publish-Portable.ps1 -Version 0.1.0-dev.1
```

Each invocation refuses existing version folders and publishes Desktop/CLI from
an isolated committed source snapshot under ignored artifacts/delivery. The
allowlisted files come from `git archive` at the recorded commit, and every raw
file is checked against its Git blob identity before a build. Changes to the live
checkout cannot alter these inputs. Archive attributes that substitute or omit
selected files fail the snapshot gate. The archive and receipt are retained.
Existing production
projects, locks and launchers are not edited. RID lock derivation is recorded,
original package versions/content hashes are checked, and actual publish uses
RestoreLockedMode=true. RuntimeFrameworkVersion is pinned to 10.0.12. Warnings
are errors via repository props. Update that pin through explicit delivery review
when dependencies change. Corresponding-source includes derived locks for replay.

The published archive is intentionally development-only. Preserve failed build
and test folders. Run Test-Portable.ps1 on the extracted package. Controller-owned
jobs are required if an operation is expected to exceed 600 seconds.

Test-VersionTransition.ps1 opens a synthetic study using the previous version,
copies it for the newer version, and reopens a separate backup copy with the
previous version. Original/backup SHA256 must remain unchanged. This tests only
the two supplied versions; future schema migrations need their own validation.
