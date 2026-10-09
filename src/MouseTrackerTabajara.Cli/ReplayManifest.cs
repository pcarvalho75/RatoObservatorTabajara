using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Cli;

public static class ReplayStatuses
{
    public const string Unrun = "unrun";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed class ReplayHashedFile
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class EndpointDifference
{
    public string Name { get; set; } = "";
    public double? Baseline { get; set; }
    public double? Scenario { get; set; }
    public double? Difference { get; set; }
    public string Unit { get; set; } = "";
}

public sealed class PreservedBaseline
{
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double Rx { get; set; }
    public double Ry { get; set; }
    public double StartSeconds { get; set; }
    public double? EndSeconds { get; set; }
    public string TimeWindow { get; set; } = "decoder_interval";
    public string TimeWindowStatement { get; set; } = ReplayClaims.DecoderWindow;
    public double? DiameterCm { get; set; }
    public string DistanceUnit { get; set; } = "";
    public double WallBoundary { get; set; }
    public bool ArenaConfirmed { get; set; }
    public PlatformSite? Platform { get; set; }
    public List<ExclusionRegion> Exclusions { get; set; } = [];
}

public sealed class DetectorChoice
{
    public bool OriginalDetectorSettings { get; set; }
    public int? Threshold { get; set; }
    public bool? Invert { get; set; }
    public int? Blur { get; set; }
    public double? MinArea { get; set; }
    public double? MaxArea { get; set; }
    public int? MaxExtension { get; set; }
    public double? MaxJump { get; set; }
    public int AppliedThreshold { get; set; }
    public bool AppliedInvert { get; set; }
    public int AppliedBlur { get; set; }
    public double AppliedMinArea { get; set; }
    public double AppliedMaxArea { get; set; }
    public int AppliedMaxExtension { get; set; }
    public double AppliedMaxJump { get; set; }
    public string AppliedSettingsSha256 { get; set; } = "";
}

public sealed class ReplayAttemptRecord
{
    public string Schema { get; set; } = ReplayClaims.CaseSchema;
    public int Index { get; set; }
    public string Status { get; set; } = "";
    public bool Published { get; set; }
    public string RecordingId { get; set; } = "";
    public string ScenarioName { get; set; } = "";
    public string PlanSha256 { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public string AlgorithmContract { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string? RunId { get; set; }
    public string? AppliedSettingsSha256 { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public double? MaximumTimestampErrorSeconds { get; set; }
    public string CodeIdentitySha256 { get; set; } = "";
    public List<ReplayHashedFile> Files { get; set; } = [];
}

public sealed class ManifestAttempt
{
    public int Index { get; set; }
    public string Status { get; set; } = "";
    public bool Published { get; set; }
    public string RelativeDirectory { get; set; } = "";
    public string? RunId { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ReplayHashedFile> Files { get; set; } = [];
}

public sealed class ManifestRecording
{
    public string RecordingId { get; set; } = "";
    public string VideoPath { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public VideoMetadata Metadata { get; set; } = new(0, 0, 0, 0);
    public string BaselinePath { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public string BaselineRunId { get; set; } = "";
    public string BaselineSettingsSha256 { get; set; } = "";
    public string? AnimalId { get; set; }
    public bool AnimalIdProvided { get; set; }
    public bool? AnimalIdConfirmed { get; set; }
    public string AnimalIdSource { get; set; } = "not_provided";
    public string? GroupName { get; set; }
    public string? Phase { get; set; }
    public int? Day { get; set; }
    public int? Trial { get; set; }
    public string? ProvenanceNote { get; set; }
    public Measurements? BaselineMetrics { get; set; }
}

public sealed class ManifestScenario
{
    public string Name { get; set; } = "";
    public bool OriginalDetectorSettings { get; set; }
    public int? Threshold { get; set; }
    public bool? Invert { get; set; }
    public int? Blur { get; set; }
    public double? MinArea { get; set; }
    public double? MaxArea { get; set; }
    public int? MaxExtension { get; set; }
    public double? MaxJump { get; set; }
}

public sealed class ManifestCase
{
    public int PlanIndex { get; set; }
    public string RecordingId { get; set; } = "";
    public string ScenarioName { get; set; } = "";
    public string Status { get; set; } = ReplayStatuses.Unrun;
    public string Reason { get; set; } = "not_started";
    public bool Published { get; set; }
    public bool Reused { get; set; }
    public string? AnimalId { get; set; }
    public bool AnimalIdProvided { get; set; }
    public string AnimalIdSource { get; set; } = "not_provided";
    public PreservedBaseline? Preserved { get; set; }
    public DetectorChoice? Detector { get; set; }
    public List<EndpointDifference>? EndpointDifferences { get; set; }
    public Measurements? ScenarioMetrics { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ManifestAttempt> Attempts { get; set; } = [];
}

public sealed class ManifestInterruption
{
    public string Utc { get; set; } = "";
    public string Reason { get; set; } = "";
    public int PlanIndex { get; set; }
    public int CompletedCases { get; set; }
}

public sealed class AssemblyFingerprint
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ModuleVersionId { get; set; } = "";
}

public sealed class NativeBinaryFingerprint
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class ReplayCodeIdentity
{
    public string ReplayContract { get; set; } = ReplayClaims.Contract;
    public string AlgorithmContract { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string NativeVersion { get; set; } = "";
    public string SettingsHashMethod { get; set; } = ReplayClaims.SettingsHashMethod;
    public List<AssemblyFingerprint> Assemblies { get; set; } = [];
    public List<NativeBinaryFingerprint> NativeBinaries { get; set; } = [];
}

public sealed class ReplayManifestDocument
{
    public string Schema { get; set; } = ReplayClaims.ManifestSchema;
    public ReplayCodeIdentity CodeIdentity { get; set; } = new();
    public string PlanSha256 { get; set; } = "";
    public string PlanPath { get; set; } = "";
    public int? BiologicalN { get; set; }
    public string BiologicalNStatement { get; set; } = ReplayClaims.NoBiologicalN;
    public int RecordingCount { get; set; }
    public int ScenarioCount { get; set; }
    public int PlannedCaseCount { get; set; }
    public string SelectionPolicy { get; set; } = ReplayClaims.SelectionPolicy;
    public string EndpointDifferenceScope { get; set; } = ReplayClaims.RawDifferences;
    public List<string> ClaimLimits { get; set; } = [];
    public List<ManifestRecording> Recordings { get; set; } = [];
    public List<ManifestScenario> Scenarios { get; set; } = [];
    public List<ManifestCase> Cases { get; set; } = [];
    public List<ManifestInterruption> Interruptions { get; set; } = [];
}

public sealed class ReplaySummary
{
    public string Result { get; set; } = "";
    public string Manifest { get; set; } = "";
    public string PlanSha256 { get; set; } = "";
    public int PlannedCases { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Unrun { get; set; }
    public int Cancelled { get; set; }
    public int Reused { get; set; }
    public int? BiologicalN { get; set; }
    public bool CaseCountIsNotAnimalN { get; set; } = true;
    public string SelectionPolicy { get; set; } = ReplayClaims.SelectionPolicy;
}
