using System.Text.Json;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;

try
{
    if (args.Length == 0 || args[0] == "--help")
    {
        Console.WriteLine(ReplayCommand.TopLevelHelp); return 0;
    }
    if (args[0] == "replay")
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
        }
        catch (InvalidOperationException)
        {
            // A redirected CLI has no console. --cancel-when and the process token still stop the run.
        }
        return ReplayCommand.Execute(args, new ReplayCommandContext { Cancellation = cancellation.Token });
    }
    if (args[0] == "doctor") { Console.WriteLine($".NET {Environment.Version}; OpenCV {OpenCvTracker.NativeVersion}; Windows x64 native processing ready."); return 0; }
    if (args[0] == "analyze")
    {
        if (args.Length < 3) throw new ArgumentException("Specify video and output folder.");
        var settings = args.Length > 3 ? JsonDefaults.Deserialize<TrackingSettings>(File.ReadAllText(args[3])) : new TrackingSettings();
        var asset = new VideoAsset { Path = Path.GetFullPath(args[1]), SourceSha256 = OpenCvTracker.HashFile(args[1]), Metadata = OpenCvTracker.Probe(args[1]), Settings = settings, AnimalId = Path.GetFileNameWithoutExtension(args[1]) };
        var progress = new Progress<double>(p => { if (p >= 99.9) Console.WriteLine("Processing complete."); });
        var result = OpenCvTracker.Analyze(asset, progress);
        ExportWriter.SaveRun(result, args[2]);
        Console.WriteLine(JsonDefaults.Serialize(new { result.Id, result.Metrics.TrackedFrames, result.Metrics.TotalFrames, result.Metrics.CoveragePercent, result.ElapsedSeconds, result.MaximumTimestampErrorSeconds })); return 0;
    }
    if (args[0] == "verify")
    {
        if (args.Length < 3) throw new ArgumentException("Specify both saved analyses.");
        var native = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(args[1]));
        using var source = JsonDocument.Parse(File.ReadAllText(args[2]));
        if (source.RootElement.GetProperty("video_sha256").GetString() != native.SourceSha256) throw new InvalidDataException("Source video hashes differ.");
        var original = source.RootElement.GetProperty("points").EnumerateArray().ToArray();
        var statusMismatch = 0; var frameMismatch = 0; var coordinateMismatch = 0; double maximumCoordinateError = 0;
        if (original.Length != native.Points.Count) throw new InvalidDataException("Frame-row counts differ.");
        for (var i = 0; i < original.Length; i++)
        {
            var p = native.Points[i]; var row = original[i];
            if (p.Frame != row[0].GetInt32()) frameMismatch++;
            if (p.Status.ToString().ToLowerInvariant() != row[3].GetString()) statusMismatch++;
            int? x = row[1].ValueKind == JsonValueKind.Null ? null : row[1].GetInt32();
            int? y = row[2].ValueKind == JsonValueKind.Null ? null : row[2].GetInt32();
            if (p.X != x || p.Y != y) coordinateMismatch++;
            if (p.X.HasValue && x.HasValue && p.Y.HasValue && y.HasValue) maximumCoordinateError = Math.Max(maximumCoordinateError, Math.Sqrt(Math.Pow(p.X.Value - x.Value, 2) + Math.Pow(p.Y.Value - y.Value, 2)));
        }
        var expectedMetrics = source.RootElement.GetProperty("metrics");
        var differences = new Dictionary<string, double>();
        foreach (var pair in new[] { ("distance", native.Metrics.Distance), ("unknown_seconds", native.Metrics.UnknownSeconds), ("tracked_seconds", native.Metrics.TrackedSeconds), ("coverage_percent", native.Metrics.CoveragePercent) })
            differences[pair.Item1] = Math.Abs(pair.Item2 - expectedMetrics.GetProperty(pair.Item1).GetDouble());
        foreach (var q in native.Metrics.QuadrantSeconds) differences["quadrant_" + q.Key] = Math.Abs(q.Value - expectedMetrics.GetProperty("quadrant_seconds").GetProperty(q.Key).GetDouble());
        var accepted = frameMismatch == 0 && statusMismatch == 0 && coordinateMismatch == 0 && differences.Values.All(v => v <= 1e-9);
        var report = new { accepted, rows = original.Length, frameMismatch, statusMismatch, coordinateMismatch, maximumCoordinateError, differences, limit = "Regression agreement is not independent scientific validation." };
        Console.WriteLine(JsonDefaults.Serialize(report)); File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "reference-comparison.json"), JsonDefaults.Serialize(report)); return accepted ? 0 : 2;
    }
    throw new ArgumentException("Unknown command.");
}
catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
