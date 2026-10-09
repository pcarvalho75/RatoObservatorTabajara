# Mouse Tracker Tabajara portable collaborator test release

This unsigned Windows x64 development package includes its .NET 10 runtime,
WPF runtime, OpenCV and SQLite binaries. Launch `Start Mouse Tracker Tabajara.cmd`,
or `desktop\MouseTrackerTabajara.exe`. The command line executable is
`cli\MouseTrackerTabajara.Cli.exe`; use `doctor` or `--help`.
No Python or developer SDK is needed to execute these packaged programs.
Start with [the collaborator guide](docs/COLLABORATOR-TEST.md) for importing your
collection, dark-mouse settings, saved processed projects and group comparisons.
The package also includes a question/output guide in `docs/QUESTION-COVERAGE.md`.
The build machine needs the pinned SDK in global.json and NuGet access/cache.
Clean Windows execution remains UNRUN until a fresh Windows machine passes the
included harness. Native prerequisite availability is not inferred from a host test.

## Verify and test

Extract into a new version folder; do not run inside the ZIP. Verify ZIP SHA256
against the separately supplied archive-sha256.txt. `files.sha256.json` covers all
package files except itself; these hashes provide integrity, not publisher trust.
From a standard interactive Windows x64 desktop run:

```
powershell -NoProfile -ExecutionPolicy Bypass -File validation\Test-Portable.ps1 -PackageDirectory . -EvidenceDirectory C:\TabajaraEvidence\unique-run
```

The evidence directory must not exist. The harness writes only synthetic videos,
analyses and study files under that directory, runs the actual EXEs, checks every
synthetic coordinate/time/status against generated expectations, and captures WPF
startup/layout and saved-study reopening. It requires Windows PowerShell 5.1 or
PowerShell 7, already supplied PowerShell policy permission, and an interactive
Windows desktop. The application itself has no PowerShell dependency.
On an already available fresh Windows VM with no .NET SDK/runtime add
`-CleanMachine`. This flag rejects common visible .NET installations; it cannot
prove the VM was clean. Save VM image identity, OS build, installed software and
prerequisite inventory alongside result.json. Do not install prerequisites during
that test. A failure exposes missing native/system dependencies and must remain a
failure. Windows Sandbox, features or new paid VMs must not be installed/enabled
solely for this validation without separate authorization.

## Versioned update and rollback

Before closing, prefer **Save processed project** to a new `.tabajara` checkpoint; its online SQLite backup includes committed WAL pages and embedded operational history. Videos remain external. Close the app and stop CLI jobs. For a manual closed-project backup, retain operational `.recording-operations.json` and the database together with its WAL/SHM
sidecars after all writers close; keep original recordings and exports unchanged.
Keep studies outside application version folders. Extract each update into a NEW
folder named by its version and commit. Verify hashes, run the synthetic harness,
and launch that version with an explicit study path, for example:

```
desktop\MouseTrackerTabajara.exe --study "D:\MyStudies\Luciana\working.tabajara"
```

For a new version, first open a COPY of the backed-up study. Confirm compatibility
and expected results before choosing the production copy. Keep the prior executable
folder and database backup. Rollback means closing the newer app and launching the
older folder against a separate copy of its pre-update database backup. Do not let
an older executable open a database migrated by a newer version unless that exact
schema transition was tested. No updater deletes, replaces or migrates studies.
Retained dev6→dev7 synthetic compatibility checks cover that exact transition; they do not certify arbitrary schema rollback. Final candidate version QA receipt is pending controller publication.

## Source, dependencies and identity

build-identity.json identifies source commit, runtime and unsigned development
status. corresponding-source.zip contains the app source, license/provenance,
Windows-specific dependency locks and replay script. Extract it to a new build
folder and run scripts\delivery\Replay-Build.ps1 using the pinned SDK. The archive
excludes real videos, private reference trajectories, credentials, coordination
and worker logs, ignored build outputs and study files. Replay builds the executable
payload; it does not assert byte-identical ZIP timestamps or publisher signing.
NuGet declarations, repository commits, package content hashes, nupkg hashes and
available license/notice files are in dependency-provenance.json and licenses/.
Application code includes DORIS GPL-3.0-or-later attribution. Third-party native
bundled-source/license audit remains OPEN; NuGet's top-level license declaration
alone does not establish all embedded codec/component obligations. This package
is for local development verification, not external redistribution approval.

Final candidate version/build and extracted-package QA receipt remain pending controller publication. Earlier development receipts remain identified by their own version; they are not a final-version receipt. Real study accuracy/design review and human sign-off are deferred to collaborator feedback. Clean-machine execution and code signing remain unverified. Synthetic regression is not biological or scientific validation.
