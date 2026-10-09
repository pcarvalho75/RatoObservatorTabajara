# Native individual atlas and matched-exposure spatial views

Core provides immutable inputs, canonical frame exposure accounting, gap-safe
trajectory segments, prespecified temporal bins, trial-within-animal averages,
animal-equal group maps and conservative per-cell missing-position difference
bounds. Wpf provides a real AtlasControl with recording cards, group maps on one
shared scale, visible held/missing animals and exact source-navigation events.
These components are not yet connected to the main Experiment workspace.
Roadmap E1/E2/E3/F1 remain incomplete until integration and their full gates pass.

## Input and accounting contract

- Supply confirmed release-relative frame exposure intervals in canonical order.
  Use verified next-frame boundaries so adjacent exposure intervals agree exactly;
  do not replace actual/verified timing with an unverified frame-rate guess.
- Supply one frozen run/correction/review/metadata/protocol/calibration snapshot
  revision. Source/run and transform identities remain attached to every trial.
- Coordinates must use a declared physical or normalized reference, with common
  orientation and bounds. Camera pixels are refused. Geometry confirmation is
  explicit; coordinates outside the comparison grid are errors, never discarded.
- A comparison scope specifies phase/day/context, planned release-relative window,
  exposure and coordinate definition before processing. The caller must derive
  this scope from the confirmed protocol, including outcome-dependent training
  termination. Trial windows cannot be trimmed to pass tracking criteria.
- Import every planned animal and expected trial slot from the study roster.
  Never-imported recordings/unknown roster, provisional identity, missing review,
  exclusions, incompatible exposure/reference and duplicate bytes/identities
  remain visible holds. Trial map values are recomputed against frozen frames
  before aggregation; altered caches are refused.
- Average planned trials within an animal, then weight contributing animals
  equally. Group maps list contributing and held animal IDs. Frames, processing
  jobs and raw video counts do not become biological N.
- A map cell contains recorded-position time divided by planned exposure. Unknown
  time remains separate. This known mass is a lower bound on actual cell occupancy,
  conditional on accepted coordinates being correct. Per-cell upper bounds add
  unknown mass, capped at1. Different cells cannot all realize their upper bound
  simultaneously. These are not confidence intervals or biological conclusions.

## Host integration still required

1. Map the accepted study-analysis snapshot/roster into SpatialTrialInput and
   AnimalSpatialInput. Confirm geometry/calibration and timing eligibility through
   native Core/protocol APIs. Do not invent confirmations for imported filenames.
2. Keep training/probe and day contexts explicit. Convert positions to the common
   room or separately target-aligned coordinates; preserve all transform revisions.
   Existing ellipse-normalized coordinates are not automatically measured cm.
3. Add AtlasControl to the native Experiment destination. Call Load only with
   a validated frozen SpatialStudy. Blinded mode hides identifying labels/groups
   and detailed allocation-dependent hold text; full review/export blinding in
   the main application remains roadmap C5/R02.
4. Handle FrameRequested using its exact run/source/snapshot/frame. Verify source
   integrity and revision before opening the actual video. The component does not
   decode video or implement synchronized multi-video playback by itself.
5. Link held source trials and missing planned slots to their review/protocol
   screens. Edits mark the experiment view stale; explicit recomputation replaces
   the frozen view. Preserve individual recordings/outliers in the atlas.
6. Add common axes/reference labels and endpoint-specific denominators alongside
   native reports. Integrate temporal views and bounds displays without treating
   outcome-dependent training exposure as a fixed probe protocol.

## Local evidence

12 numerical tests pass:30 synthetic animals, unequal trial counts, duplicate
bytes/slots/identities, absent planned trials, unknown roster, review/calibration
holds, malformed time/coordinates, immutable-cache checks, window/unit/orientation
compatibility, unknown-mass bounds and complete prespecified bins.

The native smoke validates30 cards plus1 missing planned animal, source navigation,
metadata blinding at1050x700, frozen-frame protection and sanitized source failures.
It renders the actual WPF component at1280x950 with an explicit SYNTHETIC DATA
caption. The image has been visually inspected. This is not30 real-video results.

```
dotnet test features/spatial-views/tests/MouseTrackerTabajara.SpatialViews.Tests.csproj -c Release
dotnet run --project features/spatial-views/smoke/MouseTrackerTabajara.SpatialViews.Smoke.csproj -c Release -- features/spatial-views/artifacts/atlas-component.png
```

No automatic memory score, p-value or causal group conclusion is produced.
