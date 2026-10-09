# Luciana's six-day protocol: local setup preset

The supplied protocol has four groups: **treated wildtype**, **vehicle wildtype**, **treated mutant**, **vehicle mutant**. These encode the2×2 genotype/treatment labels; this preset adds no factorial interaction model, p-value or causal claim.

| Day | Distinct context | Phase |
|---|---|---|
|1|Visible platform **with flag**|Training|
|2|Visible platform **without flag**|Training|
|3|Hidden/submerged platform|Training|
|4|Hidden/submerged platform|Training|
|5|Hidden/submerged platform|Training|
|6|Platform removed; former-site probe|Probe|

Open **Experiment plan and maps → Expected plan editor → Start Luciana six-day protocol (empty plan only)**. This populates six distinct stage IDs/days/phases in a **fresh empty draft**. Existing saved plans or nonempty drafts are preserved. Nothing is saved until the normal reviewer/reason plan-save action.

Enter the actual animals manually and select them in **Animals and allocation**. Choose one of the four group labels, then **Assign protocol group to selected animals (unconfirmed)**. This changes only selected existing animals and clears allocation confirmation. It does not create mice, assign files, confirm identity or infer30files=30animals. Confirm local identity/allocation after checking acquisition records. Identifying editor/group controls stay hidden in blind mode; normal audited metadata reveal applies.

The researcher has now confirmed **the SAME animal across six consecutive days**, **four trials per day on days1–5**, and **one trial on day6**:21expected trial slots per actual animal. Select exactly one existing actual animal row, then **Add 21 expected trials for selected animal**. The six preset stage IDs/day/phases must match. The action adds only missing order1–4 slots on days1–5 and order1 on day6, using the same animal ID throughout. Existing mapped/unmapped slots, other animals and extra manually entered ordinals are preserved; repeated use creates no duplicates. Ambiguous duplicate slots/animal IDs or mismatched contexts are refused. New slots have no recording mapping or source-reference confirmation and remain held until locally assigned/reviewed. No identity/design/roster/release/window/geometry confirmation is changed and nothing is auto-saved.

Durations, release-relative comparison start/duration, temporal bins, geometry, source reference and expected trial slots remain editable/unconfirmed using the existing draft defaults. Enter actual phase/day/order and missing slots, inspect tracking and save release/window/source evidence through existing controls. Flag and no-flag stages must remain separate; inspect detector thresholds/masks/settings locally rather than assuming the flag is automatically excluded.

**Day6:** the platform site ROI represents the **former platform location**. Use the documented prior location and source/reference geometry for site occupancy, proximity and first-site visit. A site-region entry is not physical platform mounting; no mounting is inferred with the platform removed. Actual biological events remain explicitly annotated, and gaps/censoring stay visible.

The researcher also confirmed **each video is one trial**:21videos per complete animal (four/day1–5, one/day6). The existing one-recording-per-planned-trial model fits this protocol; no concatenated-file segmentation is needed or invented. Map each actual video explicitly to its animal/day/order slot. The number of actual animals and durations remain unknown; approximately30current files do not establish animal count or completeness. Incomplete21slot rosters remain visible/held by endpoint-specific support. Those local details do **not block shipping or collaborator testing**. Enter/review the experimental unit/design locally despite the confirmed repeated-animal protocol; its confirmation is not automatic. No independence, randomization, release, platform ROI, alignment or roster confirmation is auto-selected. Group reports already average supported complete repeats within a mouse/day/context before equally weighting mice; this action does not change those calculations.

Synthetic verification uses an actual hidden native editor plus a30-source fixture reassigned to four declared groups. It is software setup/report coverage, not biological accuracy/sign-off. No private footage is embedded or sent by this preset.

## Optional bulk CSV route

Use **Bulk intake** in the main top row for a collection; the manual plan/editor route above remains available. Save your working project first. Prepare UTF-8 CSV with these five required columns, one actual video per trial:

```csv
animal_id,group,day,trial,video_path
M01,treated wildtype,1,1,"videos/M01 day1 trial1.avi"
M01,treated wildtype,6,1,"videos/M01 probe.avi"
M02,vehicle wildtype,1,1,"D:\TabajaraVideos\M02 day1 trial1.avi"
```

Paths can be absolute or relative to the CSV folder; quote commas and spaces. Group must be one of the four labels above. Day/trial must match the 21-slot protocol; the example intentionally leaves other slots missing. Choose **Preview manifest**, inspect conflicts, warnings, missing slots, per-animal completeness and planned changes. Enter reviewer/reason, check **I reviewed every conflict, missing slot and planned change**, then **Apply reviewed preview**. Resolve blocking conflicts first; changed files/plan require a fresh preview. Identifying preview/apply requires the audited revealed state.

Intake registers/matches sources and assigns plan slots; it does not confirm identity/allocation, roster/design, settings, geometry, release/window or tracking review, and does not run analysis. Previously imported metadata is preserved; reconcile warnings through the manual recording controls. Cancelled/failed registration may leave unassigned imports that the next preview can match. Missing slots remain visible. Four declared animals have 84 expected slots: 30 mapped recordings still leave 54 missing, not a complete four-group cohort.

Keep projects outside the release folder: for example `D:\TabajaraStudies\Luciana\working.tabajara`. Cancel and wait for processing to settle, then **Save processed project** to a new checkpoint such as `checkpoints\after-10.tabajara`. It retains scientific tables and operational attempt history using online SQLite backup; videos are not embedded. Close, **Open project** on that checkpoint, then **Analyze all** to reuse complete matching runs and process remaining videos. Save a new checkpoint after the next session. Preserve original sources and failed/cancelled evidence.

Current intake QA records a native video-path limitation: UTF-8 accented paths parse and hash, but the default OpenCV decoder cannot open the accented source path and preview marks it `undecodable-video`. Use plain-ASCII source folder/file names for this pilot; keep spaces/commas properly quoted. This concerns source video decoding, not UTF-8 CSV parsing or extracting the application into a Unicode folder. Final candidate QA receipt remains pending.
