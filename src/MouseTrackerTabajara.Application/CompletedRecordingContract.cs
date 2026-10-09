using MouseTrackerTabajara.Core;
namespace MouseTrackerTabajara.Application;

/// <summary>Checks completed frame evidence, not detection accuracy or biological validity.</summary>
public static class CompletedRecordingContract
{
    public static (int First, int LastExclusive) Interval(VideoMetadata metadata, TrackingSettings settings)
    {
        settings.Validate();
        if (metadata.Width <= 0 || metadata.Height <= 0 || metadata.FrameCount <= 0 || !double.IsFinite(metadata.Fps) || metadata.Fps <= 0)
            throw new InvalidDataException("Invalid registered video geometry/timing.");
        var firstValue = Math.Ceiling(settings.StartSeconds * metadata.Fps);
        var lastValue = settings.EndSeconds is { } end ? Math.Min(metadata.FrameCount, Math.Ceiling(end * metadata.Fps)) : metadata.FrameCount;
        if (!double.IsFinite(firstValue) || firstValue < 0 || firstValue >= metadata.FrameCount || !double.IsFinite(lastValue) || lastValue <= firstValue)
            throw new InvalidDataException("Selected interval is outside this recording.");
        return (checked((int)firstValue), checked((int)lastValue));
    }

    public static void Validate(AnalysisRun run)
    {
        if (!TryValidate(run, out var reason)) throw new InvalidDataException("Incomplete analysis was not published. Invalid completed evidence: " + reason);
    }

    public static bool TryValidate(AnalysisRun? run, out string? reason)
    {
        try
        {
            if (run is null || string.IsNullOrWhiteSpace(run.Id) || string.IsNullOrWhiteSpace(run.VideoId) || string.IsNullOrWhiteSpace(run.SourceSha256)
                || string.IsNullOrWhiteSpace(run.AlgorithmContract) || !run.NominalTimingVerified || run.Settings is null || run.Metadata is null || run.Metrics is null || run.Points is null)
                throw new InvalidDataException("Missing run identity, settings, timing verification or evidence.");
            var (first, last) = Interval(run.Metadata, run.Settings);
            if (run.Points.Count != last - first) throw new InvalidDataException($"Expected {last-first} frame rows [{first},{last}); found {run.Points.Count}.");
            double maximumError = 0;
            for (var i=0; i<run.Points.Count; i++)
            {
                var point=run.Points[i];
                if (point is null || point.Frame != first+i) throw new InvalidDataException("Frame rows must exactly cover the consecutive requested interval, without duplicates or gaps.");
                var error=Math.Abs(point.TimeSeconds-point.Frame/run.Metadata.Fps);
                if (!double.IsFinite(point.TimeSeconds) || point.TimeSeconds < 0 || error > .001) throw new InvalidDataException("Frame timestamps violate the nominal CFR contract.");
                maximumError=Math.Max(maximumError,error);
                if (!Enum.IsDefined(point.Status) || point.X.HasValue != point.Y.HasValue || ((point.Status is PointStatus.Tracked or PointStatus.Manual) ? !point.Accepted : point.X.HasValue || point.Y.HasValue))
                    throw new InvalidDataException("Point status/coordinate evidence is inconsistent.");
                if (point.Accepted && (point.X < 0 || point.Y < 0 || point.X >= run.Metadata.Width || point.Y >= run.Metadata.Height)) throw new InvalidDataException("Accepted coordinates are outside the video.");
            }
            if (!double.IsFinite(run.MaximumTimestampErrorSeconds) || run.MaximumTimestampErrorSeconds < 0 || run.MaximumTimestampErrorSeconds > .001 || run.MaximumTimestampErrorSeconds+1e-9 < maximumError)
                throw new InvalidDataException("Declared timestamp error is inconsistent with frame evidence.");
            var expected=MetricCalculator.Calculate(run.Points,run.Metadata,run.Settings);
            var actual=run.Metrics;
            if (actual.TotalFrames != expected.TotalFrames || actual.TrackedFrames != expected.TrackedFrames || actual.StatusCounts is null
                || actual.StatusCounts.Count != expected.StatusCounts.Count || expected.StatusCounts.Any(p=>!actual.StatusCounts.TryGetValue(p.Key,out var n) || n!=p.Value))
                throw new InvalidDataException("Measurement frame/status counts differ from frame evidence.");
            foreach (var pair in new[]{(actual.CoveragePercent,expected.CoveragePercent),(actual.DurationSeconds,expected.DurationSeconds),(actual.TrackedSeconds,expected.TrackedSeconds),(actual.UnknownSeconds,expected.UnknownSeconds),(actual.LongestGapSeconds,expected.LongestGapSeconds),(actual.AdjacentMotionSeconds,expected.AdjacentMotionSeconds)})
                if (!double.IsFinite(pair.Item1) || Math.Abs(pair.Item1-pair.Item2)>1e-9) throw new InvalidDataException("Measurement coverage/time counts differ from frame evidence.");
            if (actual.QuadrantSeconds is null || actual.QuadrantSeconds.Count != expected.QuadrantSeconds.Count
                || expected.QuadrantSeconds.Any(p=>!actual.QuadrantSeconds.TryGetValue(p.Key,out var value) || !double.IsFinite(value) || Math.Abs(value-p.Value)>1e-9))
                throw new InvalidDataException("Quadrant time counts differ from frame evidence.");
            reason=null; return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or NullReferenceException or OverflowException)
        { reason=ex.Message; return false; }
    }
}
