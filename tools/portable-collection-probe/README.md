# Extracted-package collection gate

Controller-owned explicit execution, after the final ZIP has been extracted into a
fresh versioned directory. PowerShell 7 and an SDK are required only to compile the
thin gate; the packaged desktop, managed/native dependencies and CoreCLR are used
for the actual workload. No production projects are referenced or rebuilt.

```powershell
pwsh -NoProfile -File scripts/delivery/Test-PortableCollection.ps1 `
  -PackageDirectory 'D:\Gate\extracted\portable' `
  -EvidenceDirectory 'D:\Gate\new-collection-evidence' -Execute
```

Omit `-Execute` to prepare/compile without running the collection. Both directories
must be explicit; evidence must not already exist. Never use an old package to
claim final candidate coverage. Include this tools directory in corresponding
source; the existing publisher's source allowlist does not include tools yet.

The script verifies every original file against `files.sha256.json`, copies the
unchanged desktop payload, builds only this probe against extracted assemblies,
and adds its own apphost/DLL plus derived NEW deps/runtimeconfig filenames. The
original production apphost/config/deps and DLLs remain byte-identical. Derived
runtimeconfig rejects framework-dependent packages. Each process host trace must
resolve its copied/package CoreCLR. The probe verifies every loaded managed file
and required OpenCV/SQLite/CoreCLR native module origin and SHA against the original
manifest. A failed stage is retained, not declared accepted or cleaned up.

Separate processes create30 actual OpenCV FFV1 sources, analyze10 with cap2, save a
processed project, reopen/resume with10 reused/20 new, and publish/reconstruct the
actual frozen report through package public APIs. Four synthetic animals, one per
genotype×treatment group, have six contexts/21slots each: A21 complete, B4 day1,
C3 day1, D2 day1. All84 expected slots/30mapped/54missing remain in reports. Day1
A/B training scalar contributors must be available, C/D partial contributors held.
Training fixed-probe maps remain held. Day6 has one supported probe animal and
three explicit missing/held animals. Probe map differences are not invented.
Report-only reconstruction needs CoreCLR/SQLite; OpenCV/FFmpeg origins are required
while actual prepare/resume decoding runs and checked if observed during reporting.
This is a synthetic completeness/functional gate, not real accuracy or group N.
ROI/normalized reference/explicit synthetic release and review are recorded;
detector preset is preserved; measured physical calibration stays held.

The final report must contain actual COORD/DIST/AREA/GROUP CSV and PATH/PCOORD/DENSI
SVG outputs answering the supplied Morris PDF questions, plus frozen input/HTML.
It does not invent a PDF generator. The exact original extracted Desktop.exe then
reopens the saved project and captures a native screenshot with package runtime
trace. Production payload hashes are checked again after all operations.

Receipts: `collection-gate-result.json`, per-process launch/stdout/stderr/host trace,
`production-copy-hashes.json`, probe hashes, per-phase loaded origin receipts,
source/plan/raw-run before material, partial/final project receipts and frozen report.
Actual clean-Windows/real-study/researcher sign-off remain UNRUN. Full execution
belongs to the controller; no worker >600second launch is authorized here.
