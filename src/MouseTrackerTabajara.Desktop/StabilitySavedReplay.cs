using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using MouseTrackerTabajara.Application;

namespace MouseTrackerTabajara.Desktop;

public static class StabilitySavedReplay
{
    public static StabilityEvidenceSnapshot Read(string reportPath, CancellationToken cancellation = default)
    {
        var identity = StabilityEvidenceSnapshot.Freeze(reportPath);
        var saved = JsonSerializer.Deserialize<StabilityReport>(File.ReadAllText(reportPath)) ?? throw new InvalidDataException("Saved stability report is unreadable.");
        // Check producer receipts before independently reconstructing; never authenticate merely by trusting saved.Trials.
        _ = StabilityReportWriter.ReplayAggregation(reportPath, cancellation);
        if (saved.Inputs.Any(i => i.Kind.StartsWith("region-", StringComparison.Ordinal)))
        {
            RegionProducerReconstruction.VerifySavedScalars(saved, cancellation);
            var files = ImmutableArray.CreateBuilder<StabilityFileIdentity>(); files.Add(identity);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var trace in saved.Trials.Select(t => t.Trace).OfType<StabilityTrace>().DistinctBy(t => (t.SourcePath, t.SourceSha256)))
            {
                if (!File.Exists(trace.SourcePath)) { changed.Add(trace.SourceSha256); continue; }
                var source = StabilityEvidenceSnapshot.Freeze(trace.SourcePath); files.Add(source); if (source.Sha256 != trace.SourceSha256) changed.Add(trace.SourceSha256);
            }
            var sourceRows = saved.Trials.Select(row => changed.Contains(row.BaselineIdentity.Source) ? row with { Trace = null, Supported = false, Holds = row.Holds.Add("Current source differs/missing; frozen numerical reconstruction retained, source trace held.") } : row);
            return Publication(new(ResultStability.Build(sourceRows, cancellation) with { Inputs = saved.Inputs }, files.ToImmutable()), reportPath, cancellation);
        }
        var rows = saved.Trials.Select(row => row with { Supported = false, Holds = row.Holds.Add("Saved report aggregation is retained; authenticated reconstruction for this producer input is not supplied by this route.") }).ToImmutableArray();
        return Publication(new(ResultStability.Build(rows, cancellation) with { Inputs = saved.Inputs }, [identity]), reportPath, cancellation);
    }
    private static StabilityEvidenceSnapshot Publication(StabilityEvidenceSnapshot snapshot, string reportPath, CancellationToken cancellation)
    {
        var receipt = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "publication.log");
        if (File.Exists(receipt) && File.ReadLines(receipt).LastOrDefault()?.StartsWith("completed ", StringComparison.Ordinal) == true)
            return snapshot with { Files = snapshot.Files.Add(StabilityEvidenceSnapshot.Freeze(receipt)) };
        var rows = snapshot.Report.Trials.Select(row => row with { Supported = false, Holds = row.Holds.Add("Publication is partial/failed/cancelled or has no completion receipt; numerical rows retained, not a completed report.") });
        return snapshot with { Report = ResultStability.Build(rows, cancellation) with { Inputs = snapshot.Report.Inputs } };
    }
}
