namespace MouseTrackerTabajara.Cli;

public static class ReplayClaims
{
    public const string Contract = "tabajara-cli-detector-scenario-replay-v1";
    public const string PlanSchema = "tabajara-detector-scenario-replay-plan-v1";
    public const string ManifestSchema = "tabajara-detector-scenario-replay-manifest-v1";
    public const string CaseSchema = "tabajara-detector-scenario-replay-case-v1";
    public const string SettingsHashMethod = "sha256-utf8-jsondefaults";

    public const string SelectionPolicy = "Every planned recording and scenario is retained in plan order. Endpoints are not ranked, and unfavorable or failed cases are not dropped.";
    public const string NoBiologicalN = "Biological N is not counted. Animal fields are provenance only and are never inferred from file names.";
    public const string RawDifferences = "Reported differences are raw decoder-run counts for one prespecified detector setting. Path, speed, occupancy, and first-visit differences are withheld because no confirmed biological window contract is supplied. They are not significance tests, confidence intervals, or effect sizes.";
    public const string NoMemory = "This replay does not produce a memory score, a causal claim, or a learning conclusion.";
    public const string NoGroup = "Cases are not aggregated into group means or a study conclusion.";
    public const string Coverage = "Coverage counts accepted detections. Coverage is not accuracy.";
    public const string Preserved = "Pool geometry, platform geometry, exclusions, wall boundary, the decoder interval, and distance units are copied from each recording's frozen baseline and are not scenario parameters. start_seconds and end_seconds are that decoder interval. They are not a biological release and not a confirmed trial window.";
    public const string Held = "Tested on short synthetic videos: the called native CLI, frozen plan and source identity, code and native binary identity, immutable baseline and published bytes, malformed evidence, per-case failure retention, cancellation and resume, and a held cross-process output lease. Not established here: desktop or study integration, real recordings, and biological release or trial inference.";
    public const string FailedNotRetried = "A failed case stays in this output directory as terminal evidence and is not rerun.";
    public const string ResumeIdentity = "Resume requires the same plan bytes, source hashes, baseline hashes, and the frozen code and native binary identity. A version string alone is not that identity. Published attempt files are not overwritten. A second process cannot take an output lease that is already held.";
    public const string DecoderWindow = "start_seconds and end_seconds are the raw decoder interval copied from the baseline settings. They are not a biological release and not a confirmed trial window.";

    public static IReadOnlyList<string> All { get; } =
    [
        SelectionPolicy, NoBiologicalN, RawDifferences, NoMemory, NoGroup, Coverage, Preserved, DecoderWindow, Held, FailedNotRetried, ResumeIdentity
    ];
}
