# Bulk study intake (CSV manifest)

Use this when a study has many trial videos. One CSV manifest says which video is which animal, day and trial. Mouse Tracker Tabajara previews the whole manifest against the saved expected plan and the recordings already imported. It writes nothing until you apply a reviewed preview.

Open it from the main window: **Bulk intake** (top row, next to *Save processed project*).

## Manifest format

UTF-8 CSV (with or without BOM), one header line, one row per trial video.

| Column | Required | Meaning |
|---|---|---|
| `animal_id` | yes | The actual mouse ID you assign locally. It is never taken from file names. |
| `group` | yes | One of `treated wildtype`, `vehicle wildtype`, `treated mutant`, `vehicle mutant` (letter case ignored). |
| `day` | yes | 1–6. |
| `trial` | yes | Days 1–5: 1–4. Day 6 (probe): 1 only. |
| `video_path` | yes | Absolute, or relative to the manifest's folder. |
| `alias`, `cage`, `litter` | no | Optional animal details. They fill blank plan fields only. |

Quote values that contain commas, for example `"D:\Study, 2026\day 1\clip 3.avi"`. Double any embedded quote (`""`). Quoted values cannot span lines. Unknown or duplicated columns are rejected rather than ignored.

```
animal_id,group,day,trial,video_path,cage
M01,treated wildtype,1,1,"D:\Study, 2026\M01\d1 t1.avi",C-3
M01,treated wildtype,6,1,"D:\Study, 2026\M01\probe.avi",C-3
```

Protocol (human-confirmed): the same animal is recorded on six consecutive days, with four trials per day on days 1–5 and one probe on day 6. One video is one trial, so each actual animal has **21 expected slots**. Day 1 is a visible platform with flag, day 2 is visible without flag, days 3–5 are submerged, and day 6 has the platform removed (the probe target is the former platform site). The approximately 30 current files are not an animal count and do not prove the study is complete.

## Preview

**Preview manifest** reads the CSV, then for every listed video:

1. hashes it (SHA-256);
2. matches it to an imported recording by content, so a moved or copied file is recognized as the same recording and is never treated as a new trial;
3. checks that videos not yet imported open in the native decoder.

It then reconciles every row with the saved plan, using the existing protocol stage contexts (`day1-visible-with-flag` … `day6-platform-removed-former-site-probe`).

The summary shows manifest rows, animals (new vs. existing), files (already imported vs. to register), **expected slots (animals × 21; 84 for four animals)**, slots mapped after apply, explicitly missing slots, blocking conflicts, warnings, and the plan revision change. The tabs list conflicts and warnings, missing slots, per-animal completeness (for example 7/21), every row's slot action, the planned plan changes, and the intake receipts.

**Blocking conflicts.** Each one names its line and the fix:

- malformed CSV; invalid group, day or trial;
- a duplicated trial row, the same file listed twice, or identical bytes at two paths;
- an animal whose rows disagree on group, alias, cage or litter, or IDs that differ only by letter case;
- a missing, unreadable or undecodable file;
- a file whose bytes changed since it was imported at that path;
- a slot that already holds another recording (mapped trials are never overwritten);
- a recording that already fills a different slot (one video, at most one trial);
- a recording whose identity is confirmed as a different animal (different animals are never combined);
- a plan animal with a different non-blank group, alias, cage or litter (never reallocated);
- protocol contexts that conflict with, or duplicate, existing plan contexts.

**Warnings** do not block. Examples: a previously imported recording whose own animal, group, day or trial metadata differs from its slot (it is preserved unchanged and stays held), an excluded recording or animal, and extra slots beyond the protocol.

## Apply

Enter a reviewer and reason, tick **I reviewed every conflict, missing slot and planned change**, then **Apply reviewed preview**. Apply is available only for a current, conflict-free preview with changes, and only when the window is not blinded.

Apply refuses a stale preview (receipt **Stale**, no plan revision) when any of these changed after preview:

- the manifest, or the plan revision;
- any imported recording's identity metadata (animal, confirmation, group, phase, day, trial, inclusion, path);
- a listed video's size, timestamps or **content**. Already-imported videos are re-hashed when apply starts, and new videos are re-hashed just before registration, so a rewrite that keeps size and timestamps is still caught;
- a new video's bytes were registered by another import after preview. That recording was never reviewed here, so intake leaves it unchanged and stops; the next preview shows it (for example as an identity conflict, if it was confirmed as a different animal).

Otherwise apply:

1. registers new videos with the manifest's animal, group, day, trial and phase. **Identity stays unconfirmed**, and nothing is taken from file names;
2. writes **one** new plan revision through the normal reviewed plan store, with an expected-revision guard. That revision adds new animals (identity and allocation unconfirmed), fills blank animal fields, adds the protocol contexts when missing (windows and alignment unconfirmed), adds every missing slot of the 21 (left explicitly missing), and assigns each video to its slot. It clears roster confirmation when the roster grows.

Inside that plan write transaction, before the revision is inserted, apply re-reads the registered recordings and checks them again. Other recordings must be exactly as previewed. The videos this attempt registered must still carry the reviewed, unconfirmed manifest identity. Every new mapping must resolve to the previewed bytes, and no mapped recording may be confirmed as a different animal. Other writers wait for that transaction, so no metadata edit can land between this final check and the revision.

The plan change is a single transaction, so a plan is never partially applied. If apply fails, is cancelled (**Cancel**) or is stale while videos are being registered, no plan revision is written. Videos already registered stay as unassigned imports, unchanged, and the next preview matches them by content. Existing raw runs, corrections, reviews, inclusion decisions, events, protocol windows, recording metadata and source bytes are not modified. Animals not in the manifest, and existing mappings, are unchanged. Repeating the same manifest after apply finds nothing to apply and writes no revision.

Every attempt leaves an immutable receipt in the study audit: Applied, NoChanges, Refused, Stale, Failed or Cancelled.

**Main window while intake applies.** Apply uses the main window's normal busy gate. It is refused while the main window is processing or saving. While it runs, the main window cannot start processing, import videos, edit recording metadata, open, create or snapshot a project, or open a second intake. The gate is released after every outcome.

**Closing.** Closing the intake window, or the main window, during a preview or apply does not abandon it. The running step is cancelled at its next safe point, and both windows stay open until the outcome is recorded as a receipt. Then they close. If the plan transaction has already started, it finishes and its Applied receipt is recorded first.

## Blinding

When opened from a blinded workspace, animal IDs, groups, file paths, detailed messages and planned changes are hidden. Animals appear as *Animal 001…*; counts stay visible. The manifest path and the **Choose manifest…** picker are hidden while blind, as are the reviewer and reason typed for apply. Hidden values are kept, not cleared, so a chosen manifest can still be previewed blind. Status lines that arrive late (progress, failures, receipts) are shown blind-safe while blind: the outcome and counts only, never the receipt text. A reviewer and reason reveal the metadata, and the reveal is audited. Apply requires the revealed state, because it writes identifying plan data. To choose a manifest from a blinded workspace, reveal first.

## What intake does not do

Intake never confirms identity, allocation, the roster, the design, geometry, windows, the release or inspection. After applying, the plan inventory still holds every trial until those are confirmed locally in the plan editor and recording review. Holds include: animal identity unconfirmed, roster unconfirmed, and recording identity not matching the slot until the recording's identity is confirmed.

Intake does not compute results, infer durations, mouse IDs or group membership from file names, or count files as animals. Group summaries elsewhere weight animals equally, never files or frames. Original videos are only read.

## Known limits

- The native decoder (OpenCV/FFmpeg) cannot open paths containing accents or other non-ASCII characters. The manifest parses and hashes them, but the preview blocks such rows as `undecodable-video`. Move or rename those files to plain-ASCII paths. The same limit affects ordinary import and analysis.
- Previously imported recordings keep their own metadata (by default the import sets the animal to the file name, unconfirmed). Intake shows the mismatch as a warning and does not edit them. Correct and confirm them in the main window.
- Videos registered before a failed, cancelled or stale apply remain registered (recoverable, matched by content). Registration and the plan revision use the existing store contracts and are not one combined database transaction.
- Every listed source is re-hashed after the final apply checkpoint. Read-share handles remain open through the plan transaction and its outcome, preventing source writes or replacement during that step. Hashing happens before the database write lock; recording metadata is checked inside the transaction. A changed source, including one with unchanged size and timestamps, refuses the plan and keeps registered imports recoverable.
- Apply re-hashes every listed video once more, so its time grows with total video bytes, as preview does.
- Closing is held by the window close events. Forced termination (Task Manager, power loss) between the plan commit and its receipt can leave a saved revision without an Applied receipt; the revision itself still carries the intake marker and its own audit row.
- Preview hashes every listed file, so time grows with total video bytes. Synthetic checks used tiny clips; no throughput claim is made for real videos.
