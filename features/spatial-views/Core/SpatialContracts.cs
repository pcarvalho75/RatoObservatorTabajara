using System.Collections.Immutable;

namespace MouseTrackerTabajara.SpatialViews;

public sealed record GridDefinition(int Columns, int Rows, double Left, double Top, double Right, double Bottom,
    string CoordinateUnit, string CoordinateSpace, string Orientation)
{
    public int CellCount => checked(Columns * Rows);
    public void Validate()
    {
        if (Columns < 2 || Rows < 2 || Columns > 256 || Rows > 256 ||
            !double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Right) || !double.IsFinite(Bottom) || Right <= Left || Bottom <= Top ||
            string.IsNullOrWhiteSpace(CoordinateUnit) || string.IsNullOrWhiteSpace(CoordinateSpace) || string.IsNullOrWhiteSpace(Orientation))
            throw new ArgumentException("Grid must name a common coordinate reference, orientation, units and finite bounds.");
        if (CoordinateUnit is not ("cm" or "pool diameters" or "pool radii")) throw new ArgumentException("Use declared physical or normalized pool units, not camera pixels.");
    }
    public int Cell(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < Left || x > Right || y < Top || y > Bottom)
            throw new ArgumentException("Accepted coordinate lies outside the declared grid; do not silently discard its time.");
        var column = Math.Min(Columns - 1, (int)((x - Left) / (Right - Left) * Columns));
        var row = Math.Min(Rows - 1, (int)((y - Top) / (Bottom - Top) * Rows));
        return row * Columns + column;
    }
}

/// <summary>Times are relative to the confirmed trial release. A missing sample/gap remains unknown exposure.</summary>
public sealed record SpatialFrame(int Frame, double StartSeconds, double EndSeconds, double? X, double? Y, bool Accepted);
public sealed record SpatialTrialInput(string TrialId, string RunId, string SourceSha256, string SnapshotRevision,
    string TransformRevision, string ContextKey, string WindowBasis, double PlannedSeconds,
    bool WindowConfirmed, bool GeometryConfirmed, bool Reviewed, GridDefinition Grid, ImmutableArray<SpatialFrame> Frames);
public sealed record TrialSpatialMap(SpatialTrialInput Input, ImmutableArray<double> KnownFractions, double UnknownFraction,
    ImmutableArray<string> HoldReasons)
{
    public bool Eligible => HoldReasons.IsEmpty;
}
public sealed record AnimalSpatialInput(string AnimalId, string DisplayAlias, string Group, bool IdentityConfirmed,
    bool RosterConfirmed, ImmutableArray<string> ExpectedTrialIds, ImmutableArray<TrialSpatialMap> Trials,
    bool Excluded = false, string ExclusionReason = "");
public sealed record AnimalSpatialMap(AnimalSpatialInput Input, GridDefinition? Grid, string? ContextKey, string? WindowBasis,
    double? PlannedSeconds, ImmutableArray<double> KnownFractions, double? UnknownFraction, ImmutableArray<string> HoldReasons)
{
    public bool Eligible => HoldReasons.IsEmpty;
}
public sealed record SpatialGroupMap(string Group, GridDefinition Grid, string ContextKey, string WindowBasis, double PlannedSeconds,
    ImmutableArray<string> IncludedAnimalIds, ImmutableArray<string> HeldAnimalIds,
    ImmutableArray<double> KnownFractions, double UnknownFraction);
public sealed record SpatialStudy(ImmutableArray<AnimalSpatialMap> Animals, ImmutableArray<SpatialGroupMap> Groups,
    double SharedKnownFractionMaximum);
public sealed record CellBounds(double Lower, double Upper);
public sealed record GroupDifferenceBounds(string GroupA, string GroupB, ImmutableArray<CellBounds> Cells,
    string Interpretation = "Conservative missing-position bounds, conditional on correct accepted coordinates; not confidence intervals.");
public sealed record SpatialComparisonScope(GridDefinition Grid, string ContextKey, string WindowBasis, double PlannedSeconds);
public sealed record TrajectoryVertex(int Frame, double Seconds, double X, double Y);
public sealed record TrajectorySegment(ImmutableArray<TrajectoryVertex> Vertices);
public sealed record TemporalSpatialBin(double StartSeconds, double EndSeconds, TrialSpatialMap Map);
