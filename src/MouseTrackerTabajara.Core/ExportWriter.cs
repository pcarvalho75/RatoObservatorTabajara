using System.Globalization;
using System.Net;
using System.Text;

namespace MouseTrackerTabajara.Core;

public static class ExportWriter
{
    public static void SaveRun(AnalysisRun run, string folder) =>
        SaveRun(run, folder, RawRunMeasurementScope.Describe(run, null, null));

    public static void SaveRun(AnalysisRun run, string folder, RawRunMeasurementScope scope)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "analysis.json"), JsonDefaults.Serialize(run));
        File.WriteAllText(Path.Combine(folder, "measurement-scope.txt"), scope.Visible);
        var csv = new StringBuilder("frame_zero_based,time_seconds,x_px,y_px,status,candidate_count\n");
        foreach (var p in run.Points)
            csv.AppendLine(string.Join(',', p.Frame, p.TimeSeconds.ToString("R", CultureInfo.InvariantCulture), p.X?.ToString(CultureInfo.InvariantCulture) ?? "",
                p.Y?.ToString(CultureInfo.InvariantCulture) ?? "", p.Status.ToString().ToLowerInvariant(), p.CandidateCount));
        File.WriteAllText(Path.Combine(folder, "coordinates.csv"), csv.ToString(), new UTF8Encoding(true));
        var m = run.Metrics;
        var narrative = scope.TrialEndpointsWithheld
            ? "Trial endpoints are withheld. The archived raw decoder metrics are not confirmed biological-trial measurements."
            : $"Unknown time: {m.UnknownSeconds:F3} s. Distance: {m.Distance:F3} {m.DistanceUnit}. Gaps omit motion and centroid jitter can add motion. Position accuracy requires independent review. These are raw decoder-run figures, not a separate biological-window measurement.";
        const string style = "body{font:16px Segoe UI,Arial;color:#143e59;margin:40px;max-width:1000px}pre{white-space:pre-wrap;background:#f3f7fa;padding:20px}h1{font-size:34px}";
        var html = $"""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Mouse Tracker Tabajara raw decoder run</title>
            <style>{style}</style>
            <h1>Mouse Tracker Tabajara native analysis</h1><p>Run {WebUtility.HtmlEncode(run.Id)}; software {WebUtility.HtmlEncode(run.SoftwareVersion)}</p>
            <p>Accepted detections: {m.TrackedFrames}/{m.TotalFrames} ({m.CoveragePercent:F2}% raw decoder-run coverage). Coverage is not accuracy.</p>
            <pre>{WebUtility.HtmlEncode(scope.Visible)}</pre>
            <p>{WebUtility.HtmlEncode(narrative)}</p>
            <p>First observed platform entry is not confirmed platform mounting. Earlier gaps can conceal visits; exact event times are withheld where history is uncertain.</p>
            <p>Raw decoder metrics JSON, not biological-trial endpoints.</p>
            <pre>{WebUtility.HtmlEncode(JsonDefaults.Serialize(m))}</pre>
            <p>Source SHA256: {WebUtility.HtmlEncode(run.SourceSha256)}. Automatic coordinates are unchanged by this export.
            This single-run report makes no biological group conclusion.</p></html>
            """;
        File.WriteAllText(Path.Combine(folder, "report.html"), html);
    }
}
