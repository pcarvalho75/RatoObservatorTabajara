namespace MouseTrackerTabajara.Core;

public sealed record FrameContract
{
    public bool EditablePreview { get; init; }
    public bool ShowAnalyzedTrack { get; init; }
    public bool AllowCorrection { get; init; }
    public bool AllowReviewConfirmation { get; init; }
    public string Label { get; init; } = "";

    public static FrameContract For(bool hasAnalysis, bool settingsMatch)
    {
        if (!hasAnalysis)
            return new()
            {
                EditablePreview = true,
                Label = "Editable preview. No analyzed run yet."
            };
        if (!settingsMatch)
            return new()
            {
                EditablePreview = true,
                Label = "Editable preview. This geometry is not the analyzed run. Measurements below belong to the previous run."
            };
        return new()
        {
            ShowAnalyzedTrack = true,
            AllowCorrection = true,
            AllowReviewConfirmation = true,
            Label = "Analyzed geometry. Overlays, detection mask, and measurements use this run."
        };
    }
}

public static class FrameIdentity
{
    public static int RequireSourceFrame(int frame, int frameCount)
    {
        if (frameCount <= 0 || frame < 0 || frame >= frameCount)
            throw new ArgumentException($"Frame {frame} is outside the source recording (0..{Math.Max(0, frameCount - 1)}). Nearby frames are not substituted.");
        return frame;
    }

    public static int IndexOfExactFrame(IReadOnlyList<TrackPoint> points, int frame)
    {
        var index = -1;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Frame != frame) continue;
            if (index >= 0) throw new ArgumentException($"Frame {frame} is duplicated in this run. Nearby frames are not substituted.");
            index = i;
        }
        if (index < 0) throw new ArgumentException($"Frame {frame} is not in this run. Nearby frames are not substituted.");
        return index;
    }
}
