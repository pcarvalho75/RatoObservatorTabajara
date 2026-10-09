using System.Collections.Immutable;
using System.IO;

namespace MouseTrackerTabajara.Desktop;

public sealed record ExperimentSourceStamp(string Path, bool Exists, long Length, DateTime Written, DateTime Created)
{
    public static ExperimentSourceStamp Read(string path)
    { var file = new FileInfo(path); return file.Exists ? new(path, true, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) : new(path, false, 0, default, default); }
}
public interface IExperimentCalculator
{
    ExperimentResult Calculate(AtlasSnapshot snapshot, string context, CancellationToken cancellation);
}
public sealed class NativeExperimentCalculator : IExperimentCalculator
{
    public ExperimentResult Calculate(AtlasSnapshot snapshot, string context, CancellationToken cancellation) => ExperimentPlanAdapter.Build(snapshot, context, cancellation);
}
public sealed record ExperimentComputedResult(ExperimentResult Result, TemporalStudy Temporal, ImmutableArray<ExperimentSourceStamp> Sources);
public static class ExperimentComputation
{
    public static ExperimentComputedResult Run(AtlasSnapshot snapshot, string context, IExperimentCalculator calculator, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!snapshot.IsCurrent()) throw new InvalidDataException("Frozen study changed before calculation.");
        var before = snapshot.Recordings.Select(r => ExperimentSourceStamp.Read(r.Asset.Path)).ToImmutableArray();
        var result = calculator.Calculate(snapshot, context, cancellation);
        var temporal = TemporalProfiles.Build(result, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (!snapshot.IsCurrent() || before.Any(s => ExperimentSourceStamp.Read(s.Path) != s))
            throw new InvalidDataException("Frozen source/study changed during calculation; computed results withheld.");
        return new(result, temporal, before);
    }
}
