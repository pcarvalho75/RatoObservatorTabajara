using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Application;

public sealed record BatchOutcome(int Completed, int Reused, bool Cancelled, Dictionary<string, string> Errors)
{
    public IReadOnlyList<RecordingBatchEvidence> Recordings { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int MaxDegreeOfParallelism { get; init; } = NativeBatchRequest.DefaultParallelism;
    public int EffectiveDecoderCap { get; init; } = NativeBatchRequest.DefaultParallelism;
    public int ObservedMaximumActiveJobs { get; init; }
    public int ObservedMaximumActiveDecoders { get; init; }
    public int ActiveDecodersAtCompletion { get; init; }
    public string CountInterpretation { get; init; } = RecordingAttempts.CountInterpretation;
}
public sealed class WorkspaceService(StudyStore store)
{
    public StudyStore Store { get; } = store;
    public VideoAsset Import(string path)
    {
        var hash = OpenCvTracker.HashFile(path);
        return Store.AddOrGet(new VideoAsset
        {
            Path = Path.GetFullPath(path), SourceSha256 = hash, Metadata = OpenCvTracker.Probe(path),
            AnimalId = Path.GetFileNameWithoutExtension(path), AnimalIdConfirmed = false,
            BlindedAlias = "Mouse " + hash[..6].ToUpperInvariant()
        });
    }
    public Task<BatchOutcome> AnalyzeAsync(IEnumerable<VideoAsset> assets, IProgress<BatchProgress>? progress = null, CancellationToken cancellation = default)
        => AnalyzeAsync(assets, progress, NativeBatchRequest.Default, cancellation);

    public Task<BatchOutcome> AnalyzeAsync(IEnumerable<VideoAsset> assets, IProgress<BatchProgress>? progress, NativeBatchRequest? request, CancellationToken cancellation = default)
        => new NativeBatchAdapter(Store).RunAsync(assets, progress, request ?? NativeBatchRequest.Default, cancellation);

    public IReadOnlyList<PersistedRecordingEvidence> ReadPersistedRecordingEvidence()
        => NativeBatchAdapter.ReadEvidence(Store);

    public AnalysisRun? EffectiveRun(string videoId)
    { var raw = Store.LatestRun(videoId); return raw is null ? null : Store.EffectiveRun(raw); }
}
public sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
