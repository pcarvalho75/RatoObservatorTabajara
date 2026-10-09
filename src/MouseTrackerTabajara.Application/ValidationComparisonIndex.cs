using System.Runtime.CompilerServices;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Application;

/// <summary>
/// Per-comparison indexes. Nothing is retained after Compare returns, so a cancelled call cannot affect the next one.
/// </summary>
internal sealed class ValidationComparisonIndex
{
    readonly IndependentValidationSnapshot _snapshot;
    readonly ValidationSampling _sampling;
    readonly Dictionary<string, VideoAsset>? _assets;
    readonly Dictionary<string, ValidationFrozenRun>? _runs;
    readonly Dictionary<(string VideoId, int Frame), ReferenceRow[]> _references;
    readonly ValidationSamplingCase[] _cases;
    readonly Dictionary<(string VideoId, int Frame), List<int>> _pointCases = new();
    readonly Dictionary<string, List<int>> _rangeCases = new(StringComparer.Ordinal);
    readonly Dictionary<ValidationFrozenRun, RunMaterial> _materials = new(RunReferenceComparer.Instance);

    ValidationComparisonIndex(IndependentValidationSnapshot snapshot, ValidationSampling sampling, Dictionary<string, VideoAsset>? assets, Dictionary<string, ValidationFrozenRun>? runs, Dictionary<(string VideoId, int Frame), ReferenceRow[]> references, ValidationSamplingCase[] cases)
    {
        _snapshot = snapshot;
        _sampling = sampling;
        _assets = assets;
        _runs = runs;
        _references = references;
        _cases = cases;
        for (var index = 0; index < cases.Length; index++)
        {
            var sample = cases[index];
            if (sample.StartFrame == sample.EndFrame)
            {
                var key = (sample.VideoId, sample.StartFrame);
                if (!_pointCases.TryGetValue(key, out var point)) { point = []; _pointCases[key] = point; }
                point.Add(index);
            }
            else
            {
                if (!_rangeCases.TryGetValue(sample.VideoId, out var range)) { range = []; _rangeCases[sample.VideoId] = range; }
                range.Add(index);
            }
        }
    }

    public static ValidationComparisonIndex Create(IndependentValidationSnapshot snapshot, ValidationSampling sampling, string sampleHash, CancellationToken cancellation)
    {
        var seen = 0;
        void Gate() { if ((++seen & 1023) == 0) cancellation.ThrowIfCancellationRequested(); }
        var assets = Unique(snapshot.Imported, asset => asset.Id, Gate);
        var runs = Unique(sampling.Runs, run => run.VideoId, Gate);
        var latest = new List<ValidationHistoryEntry>();
        var prefix = sampleHash + ":";
        foreach (var entry in snapshot.History)
        {
            Gate();
            if (entry.Kind == "reference" && entry.EntityId.StartsWith(prefix, StringComparison.Ordinal)) latest.Add(entry);
        }
        var references = new Dictionary<(string VideoId, int Frame), List<ReferenceRow>>();
        foreach (var entry in latest.GroupBy(entry => entry.EntityId).Select(group => group.OrderBy(entry => entry.Revision).Last()))
        {
            Gate();
            var reference = JsonDefaults.Deserialize<IndependentReference>(entry.Json);
            var key = (reference.VideoId, reference.Frame);
            if (!references.TryGetValue(key, out var rows)) { rows = []; references[key] = rows; }
            rows.Add(new ReferenceRow(entry, reference));
        }
        var frozenReferences = new Dictionary<(string VideoId, int Frame), ReferenceRow[]>(references.Count);
        foreach (var pair in references) frozenReferences[pair.Key] = pair.Value.ToArray();
        for (var index = 0; index < sampling.Cases.Length; index++) Gate();
        return new(snapshot, sampling, assets, runs, frozenReferences, sampling.Cases.ToArray());
    }

    public VideoAsset Asset(string videoId)
    {
        if (_assets is not null && _assets.TryGetValue(videoId, out var asset)) return asset;
        return _snapshot.Imported.Single(asset => asset.Id == videoId);
    }

    public ValidationFrozenRun? SamplingRun(string videoId)
    {
        if (_runs is not null) return _runs.TryGetValue(videoId, out var run) ? run : null;
        return _sampling.Runs.SingleOrDefault(run => run.VideoId == videoId);
    }

    public RunMaterial Material(ValidationFrozenRun run)
    {
        if (!_materials.TryGetValue(run, out var material))
        {
            material = new RunMaterial(run);
            _materials[run] = material;
        }
        return material;
    }

    public ReferenceRow[] ReferencesAt(string videoId, int frame) =>
        _references.TryGetValue((videoId, frame), out var rows) ? rows : [];

    public CaseCoverage Covering(string videoId, int frame)
    {
        var anyRandom = false;
        double? inclusion = null;
        var secondRequired = false;
        var best = int.MaxValue;
        void Consider(int index)
        {
            var sample = _cases[index];
            if (sample.SecondReviewRequired) secondRequired = true;
            if (sample.RandomSample && index < best)
            {
                best = index;
                anyRandom = true;
                inclusion = sample.InclusionProbability;
            }
        }
        if (_pointCases.TryGetValue((videoId, frame), out var points))
            foreach (var index in points) Consider(index);
        if (_rangeCases.TryGetValue(videoId, out var ranges))
            foreach (var index in ranges)
                if (_cases[index].StartFrame <= frame && _cases[index].EndFrame >= frame) Consider(index);
        return new(anyRandom, inclusion, secondRequired);
    }

    static Dictionary<string, T>? Unique<T>(IEnumerable<T> items, Func<T, string> id, Action gate)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        var duplicated = false;
        foreach (var item in items)
        {
            gate();
            if (!map.TryAdd(id(item), item)) duplicated = true;
        }
        return duplicated ? null : map;
    }

    sealed class RunReferenceComparer : IEqualityComparer<ValidationFrozenRun>
    {
        public static readonly RunReferenceComparer Instance = new();
        public bool Equals(ValidationFrozenRun? x, ValidationFrozenRun? y) => ReferenceEquals(x, y);
        public int GetHashCode(ValidationFrozenRun obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

internal sealed class ReferenceRow(ValidationHistoryEntry entry, IndependentReference reference)
{
    public ValidationHistoryEntry Entry { get; } = entry;
    public IndependentReference Reference { get; } = reference;
}

internal readonly record struct CaseCoverage(bool AnyRandom, double? InclusionProbability, bool SecondReviewRequired);

internal sealed class RunMaterial
{
    readonly ValidationFrozenRun _run;
    AnalysisRun? _raw;
    bool _rawChecked;
    bool _rawValid;
    FrameIndex? _rawFrames;
    List<TrackPoint>? _corrected;
    FrameIndex? _correctedFrames;
    Dictionary<BiologicalEventKind, BiologicalEvent[]>? _events;

    public RunMaterial(ValidationFrozenRun run) => _run = run;

    public AnalysisRun Raw => _raw ??= _run.RawRun;

    public bool RawValid
    {
        get
        {
            if (!_rawChecked)
            {
                _rawValid = CompletedRecordingContract.TryValidate(Raw, out _);
                _rawChecked = true;
            }
            return _rawValid;
        }
    }

    public TrackPoint? PointAt(ValidationMeasurementTarget target, int frame) =>
        Frames(target).SingleOrDefault(frame);

    public TrackPoint[] PointsInRange(ValidationMeasurementTarget target, int start, int end)
    {
        var points = target == ValidationMeasurementTarget.RawAutomatic ? Raw.Points : Corrected();
        var matches = new List<TrackPoint>();
        foreach (var point in points)
            if (point.Frame >= start && point.Frame <= end) matches.Add(point);
        return matches.ToArray();
    }

    public BiologicalEvent[] EventsOfKind(BiologicalEventKind kind)
    {
        if (_events is null)
        {
            var events = _run.BiologicalEvents.Select(JsonDefaults.Deserialize<BiologicalEvent>).ToArray();
            _events = events.GroupBy(item => item.Kind).ToDictionary(group => group.Key, group => group.ToArray());
        }
        return _events.TryGetValue(kind, out var rows) ? rows : [];
    }

    FrameIndex Frames(ValidationMeasurementTarget target)
    {
        if (target == ValidationMeasurementTarget.RawAutomatic) return _rawFrames ??= FrameIndex.Build(Raw.Points);
        return _correctedFrames ??= FrameIndex.Build(Corrected());
    }

    List<TrackPoint> Corrected() => _corrected ??= MetricCalculator.ApplyCorrections(Raw, _run.Corrections.Select(JsonDefaults.Deserialize<ManualCorrection>));

    sealed class FrameIndex
    {
        readonly IReadOnlyList<TrackPoint> _points;
        readonly Dictionary<int, int> _index;
        readonly HashSet<int>? _duplicates;

        FrameIndex(IReadOnlyList<TrackPoint> points, Dictionary<int, int> index, HashSet<int>? duplicates)
        {
            _points = points;
            _index = index;
            _duplicates = duplicates;
        }

        public static FrameIndex Build(IReadOnlyList<TrackPoint> points)
        {
            var index = new Dictionary<int, int>(points.Count);
            HashSet<int>? duplicates = null;
            for (var i = 0; i < points.Count; i++)
            {
                if (!index.TryAdd(points[i].Frame, i))
                {
                    duplicates ??= [];
                    duplicates.Add(points[i].Frame);
                }
            }
            return new(points, index, duplicates);
        }

        public TrackPoint? SingleOrDefault(int frame)
        {
            if (_duplicates is not null && _duplicates.Contains(frame)) return _points.SingleOrDefault(point => point.Frame == frame);
            return _index.TryGetValue(frame, out var index) ? _points[index] : null;
        }
    }
}
