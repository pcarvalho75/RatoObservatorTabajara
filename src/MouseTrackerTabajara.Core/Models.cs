using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseTrackerTabajara.Core;

public enum TrialMode { Unspecified, Training, Probe }
public enum PointStatus { Tracked, Manual, Missing, Ambiguous, Jump, Excluded }
public enum EventHistory { NotConfigured, ObservedExact, EarlierVisitPossibleInGap, VisitHistoryIndeterminate, RightCensoredComplete }
public sealed record PlatformSite(double X, double Y, double Radius);
public sealed record ExclusionRegion(double Left, double Top, double Right, double Bottom);

public sealed record TrackingSettings
{
    public double Cx { get; init; } = 803d / 1920;
    public double Cy { get; init; } = 434d / 1080;
    public double Rx { get; init; } = 440d / 1920;
    public double Ry { get; init; } = 430d / 1080;
    public int Threshold { get; init; } = 30;
    public bool Invert { get; init; } = true;
    public int Blur { get; init; } = 2;
    public double MinArea { get; init; } = 100;
    public double MaxArea { get; init; } = 6000;
    public int MaxExtension { get; init; } = 150;
    public double MaxJump { get; init; } = 50;
    public double? DiameterCm { get; init; }
    public double StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public PlatformSite? Platform { get; init; }
    public double WallBoundary { get; init; } = .8;
    public bool ArenaConfirmed { get; init; }
    public List<ExclusionRegion> Exclusions { get; init; } = [];

    public void Validate()
    {
        Check(Cx, 0, 1, nameof(Cx)); Check(Cy, 0, 1, nameof(Cy));
        Check(Rx, .01, 1, nameof(Rx)); Check(Ry, .01, 1, nameof(Ry));
        Check(Threshold, 0, 255, nameof(Threshold)); Check(Blur, 0, 31, nameof(Blur));
        Check(MinArea, 1, 1e7, nameof(MinArea)); Check(MaxArea, MinArea, 1e7, nameof(MaxArea));
        Check(MaxExtension, 1, 100000, nameof(MaxExtension)); Check(MaxJump, 1, 100000, nameof(MaxJump));
        Check(StartSeconds, 0, double.MaxValue, nameof(StartSeconds));
        if (EndSeconds is { } end) { Check(end, 0, double.MaxValue, nameof(EndSeconds)); if (end <= StartSeconds) throw new ArgumentException("Trial end must come after start."); }
        if (DiameterCm is { } diameter) Check(diameter, 1, 1000, nameof(DiameterCm));
        Check(WallBoundary, .1, .99, nameof(WallBoundary));
        if (Platform is { } p) { Check(p.X, 0, 1, "Platform X"); Check(p.Y, 0, 1, "Platform Y"); Check(p.Radius, .001, .2, "Platform radius"); }
        foreach (var mask in Exclusions)
        {
            Check(mask.Left, 0, 1, "Mask left"); Check(mask.Top, 0, 1, "Mask top");
            Check(mask.Right, mask.Left, 1, "Mask right"); Check(mask.Bottom, mask.Top, 1, "Mask bottom");
        }
    }
    private static void Check(double value, double min, double max, string name)
    { if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentException($"{name} is outside the permitted range."); }
}

public sealed record VideoMetadata(int Width, int Height, double Fps, int FrameCount)
{
    public double DurationSeconds => FrameCount / Fps;
}
public sealed record TrackPoint(int Frame, double TimeSeconds, int? X, int? Y, PointStatus Status, int CandidateCount,
    int? TentativeX = null, int? TentativeY = null, double? Area = null)
{
    [JsonIgnore] public bool Accepted => (Status is PointStatus.Tracked or PointStatus.Manual) && X.HasValue && Y.HasValue;
}
public sealed record PercentBounds(double Lower, double Upper);
public sealed record EndpointEligibility(bool Eligible, string Reason);
public sealed record Measurements
{
    public int TotalFrames { get; init; }
    public int TrackedFrames { get; init; }
    public double CoveragePercent { get; init; }
    public double DurationSeconds { get; init; }
    public double TrackedSeconds { get; init; }
    public double UnknownSeconds { get; init; }
    public double LongestGapSeconds { get; init; }
    public double Distance { get; init; }
    public string DistanceUnit { get; init; } = "pool diameters";
    public double? MeanSpeed { get; init; }
    public double AdjacentMotionSeconds { get; init; }
    public Dictionary<string, double> QuadrantSeconds { get; init; } = [];
    public Dictionary<string, double?> QuadrantObservedPercent { get; init; } = [];
    public Dictionary<string, PercentBounds> QuadrantFullTrialPercentBounds { get; init; } = [];
    public double? WallObservedPercent { get; init; }
    public string? TargetQuadrant { get; init; }
    public double? TargetObservedPercent { get; init; }
    public PercentBounds? TargetFullTrialPercentBounds { get; init; }
    public double? PlatformSiteSeconds { get; init; }
    public int? PlatformSiteEntries { get; init; }
    public double? FirstObservedSiteVisitSeconds { get; init; }
    public double? FirstVisitLowerSeconds { get; init; }
    public double? FirstVisitUpperSeconds { get; init; }
    public double? SiteVisitTime { get; init; }
    public bool? SiteVisitObserved { get; init; }
    public EventHistory SiteVisitStatus { get; init; }
    public double? MeanProximityDiameters { get; init; }
    public Dictionary<string, EndpointEligibility> EndpointEligibility { get; init; } = [];
    public Dictionary<string, int> StatusCounts { get; init; } = [];
}

public sealed record AnalysisRun
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string SoftwareVersion { get; init; } = "0.1.0-native";
    public string AlgorithmContract { get; init; } = "";
    public string VideoId { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public VideoMetadata Metadata { get; init; } = new(0, 0, 0, 0);
    public TrackingSettings Settings { get; init; } = new();
    public List<TrackPoint> Points { get; init; } = [];
    public Measurements Metrics { get; init; } = new();
    public bool NominalTimingVerified { get; init; }
    public double MaximumTimestampErrorSeconds { get; init; }
    public double ElapsedSeconds { get; init; }
    public List<ManualCorrection> AppliedCorrections { get; init; } = [];
}

public static class AlgorithmIdentity
{
    public const string Contract = "doris854fd5c-160point-v1;metrics-python011;nominal-pts-v1";
}

public sealed record VideoAsset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Path { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public string AnimalId { get; init; } = "";
    public string BlindedAlias { get; init; } = "";
    public string BlindedAnimalAlias { get; init; } = "";
    public bool AnimalIdConfirmed { get; init; }
    public string GroupName { get; init; } = "";
    public TrialMode Phase { get; init; }
    public int Day { get; init; } = 1;
    public int Trial { get; init; } = 1;
    public bool Included { get; init; } = true;
    public string ExclusionReason { get; init; } = "";
    public VideoMetadata Metadata { get; init; } = new(0, 0, 0, 0);
    public TrackingSettings Settings { get; init; } = new();
    [JsonIgnore] public string Filename => System.IO.Path.GetFileName(Path);
}

public sealed record ManualCorrection(string RunId, int Frame, int? X, int? Y, string Reason, DateTime CreatedUtc);
public sealed record ReviewMoment(int StartFrame, int EndFrame, string Reason, bool RandomAudit);
public sealed record BatchProgress(int Complete, int Total, string Filename, double CurrentPercent, string Status);

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Invalid saved record.");
}
