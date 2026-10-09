# Development distribution0.1.0-dev.6

Sourcefe99c3517a27a01ecb26afedb01a4f2ba601d037. Fixed roadmap835/1000,
35/42accepted steps. This unsigned self-contained Windows x64 development build
contains three corrections arising from the scheduled Astra consultation:

- Authenticated region stability replay requires the complete frozen planned
  trial×scenario and raw/unassigned case inventory. Omissions, extras, duplicates,
  changed identities or relocated held/missing cases are rejected. Scalar/bounds
  reconstruction remains independently checked. Unsupported mixed detector+region
  authentication is rejected rather than silently merged.
- Whole-study scalar denominators match accepted whole-window endpoint support,
  separately from per-bin coverage and unrelated endpoint diagnostics. Every
  expected repeat is still required for an animal mean; held values remain null.
- Blinded trial aliases remain consistent across scenarios and published outputs;
  case aliases remain distinct, private identities omitted and values unchanged.

Archive:
artifacts/delivery/0.1.0-dev.6-fe99c3517a27-win-x64/
0.1.0-dev.6-fe99c3517a27-win-x64.zip,223544336bytes.
SHA256:C6A5A6FECBB9C060A6BE913F5AB6AC44F29D92A7C9B0FD82963DAB2893C777F3.
Previous dev5 archive/evidence remain intact and contain the earlier code.
Committed source blobs, corresponding source and hash-bound native codec notices
are included by the existing source allowlist. Full native source/toolchain/
compliance audit remains open; no private experimental media are packaged.

Root combined Release build:0warnings/errors,237/237tests across9projects,
0failed/skipped. Exact receipts:
artifacts/tests/20261009-110223-527-d59383a5/receipt.json and per-projectTRX.
New meaningful fixtures reproduce prior omission/denominator defects and test
the corrections. These are development numerical/control/recovery checks, not
real scientific accuracy or researcher acceptance. Astra read earlier receipts
and reviewed source but did not execute these later tests.

Actual ZIP extracted into a new folder with accents and spaces; its own Windows
PowerShell5.1 harness passes24expected synthetic coordinates/status/frame/time,
WPF startup and reopen, and bundled CLI/WPF CoreCLR resolution. Receipt:
artifacts/controller-dev6-extracted-ps51/evidence/result.json.
Dev5→dev6 copied-study update/rollback passes; originals/backups preserved:
artifacts/controller-dev5-dev6-transition/transition-result.json.
Original/backup SHA256:E2660BBAF729C56BEC72D49CCC3AF5711E4D724F18F6025E2851F48A604EFF70.
These receipts cover this installed developer host and these versions; a clean
Windows machine and arbitrary future migrations remain unverified.

The real-data/input/independent-reference/clean-Windows/researcher gates remain
G1/G2/G4/G5/H2/H3/H4,165points. Only development Mouse.mp4 is available. The
researcher pilot kit remains provisional and unsigned. Split-specific validation
summaries, failure-episode sampling and measured large-result GUI/performance
improvements are queued from Astra's review; they create no real validation data
or new point weights. Full objective stays blocked pending external evidence.
