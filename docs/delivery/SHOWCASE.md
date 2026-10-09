# Synthetic native report showcase

These are **synthetic demonstrations**, not collaborator results or accuracy evidence. They reuse the retained30-source/2-group S20 fixture, unequal planned repeats, missing/held/excluded states and its already completed publication. Metadata remains blind with opaque aliases. No original real-video pixels, new product features or outgoing messages/uploads are involved.

Open the included [group heatmaps and bounds](showcase/synthetic-group-heatmaps-and-bounds-1440x960.png),
[proximity distributions](showcase/synthetic-proximity-distribution-1440x960.png) or
[observed-distance view](showcase/synthetic-observed-distance-1050x700.png).
The [trajectory](showcase/context-001-trial-001_PATH.svg) and
[coordinate plot](showcase/context-001-trial-001_PCOORD.svg) are also included as vector examples.

## Views and captions

- **Group heatmaps and difference bounds:** actual1440×960 WPF report scrolled to the map section, with the existing conservative cellwise difference-bound expander open. Common-scale known-time maps show included/held counts and unknown mass. Lower/upper bounds are conditional missing-position bounds, not confidence intervals.
- **Proximity distribution:** actual1440×960 report after selecting **proximity**. Equal-mouse means, observed min–max and source-linked mouse dots share one endpoint scale; maps remain immediately below. The observed range is descriptive, not an inferential outlier classification.
- **Observed distance:** actual1050×700 report after selecting **observed adjacent distance**. The unit is pool diameters and gaps are never bridged. This is observed motion, not a fabricated full unknown path.
- **PATH / PCOORD / DENSI / group difference SVGs:** byte-for-byte copies of the original blinded report's producer-generated vector figures. Open SVG in a browser or the exported HTML. No substitute raster renderer/mockup was created.

Current retained capture receipt and files (local developer/controller evidence; package placement decided by controller):

Retained local developer evidence is not part of the public source snapshot.

Files: `synthetic-group-heatmaps-and-bounds-1440x960.png`, `synthetic-proximity-distribution-1440x960.png`, `synthetic-observed-distance-1050x700.png`, `context-001-trial-001_PATH.svg`, `context-001-trial-001_PCOORD.svg`, `context-001-trial-001_DENSI.svg`, `context-001_GROUP_DIFFERENCE.svg`, and `receipt.json` with source publication/path/hash identities. PNGs were visually inspected as synthetic controls only. Ignored developer artifacts may not accompany a source checkout.

## Reach the same views in the app

Open **Whole-study report**, select contexts and preview (or inspect the saved package as historical aggregate evidence). Use the group endpoint selector for proximity or observed distance. Scroll the report body down to the common-scale group maps; expand **conservative cellwise missing-position bounds**. Click map cells or mouse dots to inspect contributors through the source viewer. Keep phase/day/window/unit and held/excluded denominators visible. Full authenticated historical replay separately requires compatible code and original bytes; inspecting a historical package alone does not attest current eligibility.

Use **Publish frozen HTML CSV and input** for the original standalone vector plots and Excel-friendly tables; see [question coverage](QUESTION-COVERAGE.md). These captures show existing source-rendered plots and controls. They do not change the mathematical/report contract, score or deferred biological validation.

## Reproduction receipt

`dotnet test tests/MouseTrackerTabajara.Report.Tests/MouseTrackerTabajara.Report.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ShowcaseCapturesRetainedNativeHeatmapsBoundsAndMetricSelector --logger "console;verbosity=normal"`

PASS1/1,0failed/skipped,7.4105s (native test6s); compiled with global warnings-as-errors. Capture test asserts30sources, blind labels, actual scroll/map visibility, opened bound control, selected proximity/distance and existing SVG presence. It creates a unique evidence folder, never overwrites prior proof or modifies the retained original publication. Original eight report-test expectations and production source remain unchanged; full report suite was not rerun for this bounded test-only capture. No heavy job, private media, extra agents/Opus or external send.
