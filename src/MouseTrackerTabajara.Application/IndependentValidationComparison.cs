using System.Collections.Immutable;
using System.Security.Cryptography;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Application;

public sealed record ValidationFrameResult(string VideoId, int Frame, bool RandomSample, double? InclusionProbability, bool SecondReviewRequired,
    ReferenceVisibility? Visibility, double? PositionErrorPixels, bool? PositionPass, bool? FalseAccepted, bool? MissedVisible,
    bool Disagreement, ImmutableArray<long> ReferenceIds, double? EventTimingErrorSeconds, bool? EventPass, ImmutableArray<string> Holds, bool Excluded, string ExclusionReason);
public sealed record ValidationEndpointResult(string VideoId, int StartFrame, int EndFrame, double? PathDifferenceDiameters, double? SiteOccupancyDifference,
    bool? WithinTolerances, ImmutableArray<string> Holds);
public sealed record ValidationComparisonReport(string PlanFingerprint, string ProducerFingerprint, string ReferenceFingerprint, string ComparatorBinarySha256,
    ImmutableArray<ValidationFrameResult> Frames, ImmutableArray<ValidationEndpointResult> Endpoints, int FullSourcePopulation, int SampledUniqueFrames,
    int ReferenceRecordedFrames, int PositionJoinedFrames, double? MeanRandomPositionErrorPixels, double? MaximumPositionErrorPixels, ImmutableArray<string> Holds, ValidationMeasurementTarget MeasurementTarget,
    string Interpretation = "Frame comparisons are not biological replicates. Coverage is not accuracy. Synthetic checks validate tooling only. Real held-out accuracy/acceptance remains unrun or requires separate independent researcher evidence; no automatic G4/H4 closure. Recorded producer algorithm/software and current comparison dependency binary receipts are preserved; legacy runs do not prove an original producer binary hash.",
    string PooledScopeLabel = ValidationSplitSummaries.PooledScopeLabel,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<ValidationSplitSummary> SplitSummaries = default);

public static class IndependentValidationComparison
{
    public static string ReferenceKey(string samplingHash, IndependentReference reference) => samplingHash + ":" + reference.VideoId + ":" + reference.Frame + ":" + reference.Lane;
    public static ValidationComparisonReport Compare(IndependentValidationSnapshot snapshot, IndependentValidationPlan plan, ValidationSampling sampling, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        snapshot.ValidatePlan(plan);
        var holds = ImmutableArray.CreateBuilder<string>();
        if (sampling.PlanFingerprint != IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(plan)) || sampling.ProducerFingerprint != snapshot.ProducerFingerprint)
            throw new InvalidDataException("Sampling/source/plan revision changed; comparison must be regenerated.");
        if (plan.SyntheticToolingOnly) holds.Add("Synthetic tooling dataset; real held-out accuracy gate is unrun.");
        if (!plan.Recordings.Any(r => r.Split == ValidationSplit.IndependentValidation)) holds.Add("No independently declared held-out recording/session; development results do not validate held-out accuracy.");
        if (plan.Recordings.Any(r => r.Split == ValidationSplit.Unspecified)) holds.Add("Recording/session split is unspecified.");
        var sampleHash = CancelableJsonHash.Hash(sampling, cancellation);
        var invalidSources = HashSources(snapshot, plan, cancellation);
        if (sampling.Runs.Any(frozen => snapshot.Runs.SingleOrDefault(r => r.VideoId == frozen.VideoId)?.ComparatorBinarySha256 != frozen.ComparatorBinarySha256)) throw new InvalidDataException("Comparator code identity changed; frozen sampling must be regenerated.");
        var index = ValidationComparisonIndex.Create(snapshot, sampling, sampleHash, cancellation);
        var frames = ImmutableArray.CreateBuilder<ValidationFrameResult>();
        var chosen = new Dictionary<(string, int), IndependentReference>();
        foreach (var key in sampling.Cases.SelectMany(c => Enumerable.Range(c.StartFrame, c.EndFrame - c.StartFrame + 1).Select(f => (c.VideoId, Frame: f))).Distinct().OrderBy(k => k.VideoId, StringComparer.Ordinal).ThenBy(k => k.Frame))
        {
            cancellation.ThrowIfCancellationRequested();
            var frameHolds = ImmutableArray.CreateBuilder<string>();
            var coverage = index.Covering(key.VideoId, key.Frame);
            var frozen = index.SamplingRun(key.VideoId);
            var asset = index.Asset(key.VideoId);
            var material = frozen is null ? null : index.Material(frozen);
            var rows = index.ReferencesAt(key.VideoId, key.Frame);
            foreach (var row in rows) row.Reference.Validate(asset.Metadata);
            var first = rows.FirstOrDefault(r => r.Reference.Lane == ReferenceReviewerLane.First);
            var second = rows.FirstOrDefault(r => r.Reference.Lane == ReferenceReviewerLane.Second);
            var consensus = rows.FirstOrDefault(r => r.Reference.Lane == ReferenceReviewerLane.Consensus);
            var disagreement = first is not null && second is not null && Disagree(first.Reference, second.Reference, plan.Tolerances.DisagreementPixels);
            if (invalidSources.Contains(key.VideoId)) frameHolds.Add("Original source bytes are missing/changed; source reference join held.");
            IndependentReference? reference = first?.Reference;
            if (first is null) frameHolds.Add("Independent first reference is missing; no zero substituted.");
            if (coverage.SecondReviewRequired && second is null) frameHolds.Add("Declared second-review reference is missing.");
            if (first is not null && second is not null && first.Reference.Reviewer.Equals(second.Reference.Reviewer, StringComparison.OrdinalIgnoreCase)) frameHolds.Add("Second review must name a distinct independent reviewer.");
            if (disagreement)
            {
                if (consensus is not null && consensus.Reference.ConsensusReferenceIds.Contains(first!.Entry.Id) && consensus.Reference.ConsensusReferenceIds.Contains(second!.Entry.Id)) reference = consensus.Reference;
                else frameHolds.Add("Independent reviewers disagree; unresolved reference is held.");
            }
            var rawValid = material is not null && material.RawValid;
            if (frozen is null) frameHolds.Add("Producer analysis is missing.");
            else if (!rawValid) frameHolds.Add("Producer completed-frame evidence is invalid.");
            var automatic = rawValid ? material!.PointAt(plan.MeasurementTarget, key.Frame) : null;
            if (automatic is null) frameHolds.Add("No producer frame join in analyzed interval; source frame remains sampled.");
            double? error = null, eventError = null; bool? pass = null, falseAccepted = null, missed = null, eventPass = null;
            if (frameHolds.Count == 0 && reference is not null)
            {
                chosen[key] = reference;
                if (reference.Visibility == ReferenceVisibility.Visible)
                {
                    missed = automatic is not { Accepted: true };
                    if (automatic is { Accepted: true }) { error = Math.Sqrt(Math.Pow(automatic.X!.Value - reference.X!.Value, 2) + Math.Pow(automatic.Y!.Value - reference.Y!.Value, 2)); pass = error <= plan.Tolerances.PositionPixels; }
                }
                else if (reference.Visibility == ReferenceVisibility.NotVisible) falseAccepted = automatic is { Accepted: true };
                else frameHolds.Add("Reference visibility/position is ambiguous; position accuracy held.");
                if (reference.Event != ReferenceEvent.None)
                {
                    var kind = Enum.Parse<BiologicalEventKind>(reference.Event.ToString());
                    var events = material!.EventsOfKind(kind);
                    if (events.Length == 0) frameHolds.Add("Published biological annotation is unavailable; site entry is not inferred mounting/release.");
                    else { eventError = events.Min(e => Math.Abs(e.Frame - reference.Frame) / asset.Metadata.Fps); eventPass = eventError <= plan.Tolerances.EventSeconds; }
                }
            }
            frames.Add(new(key.VideoId, key.Frame, coverage.AnyRandom, coverage.AnyRandom ? coverage.InclusionProbability : null, coverage.SecondReviewRequired,
                reference?.Visibility, error, pass, falseAccepted, missed, disagreement, rows.Select(r => r.Entry.Id).ToImmutableArray(), eventError, eventPass, frameHolds.Distinct().ToImmutableArray(), !asset.Included, asset.ExclusionReason));
        }
        var endpoints = ImmutableArray.CreateBuilder<ValidationEndpointResult>();
        foreach (var sample in sampling.Cases.Where(c => !c.RandomSample))
        {
            cancellation.ThrowIfCancellationRequested();
            var failures = ImmutableArray.CreateBuilder<string>();
            var run = index.SamplingRun(sample.VideoId);
            var material = run is null ? null : index.Material(run);
            var rows = Enumerable.Range(sample.StartFrame, sample.EndFrame - sample.StartFrame + 1).Select(frame => chosen.GetValueOrDefault((sample.VideoId, frame))).ToArray();
            var auto = material is null ? null : material.PointsInRange(plan.MeasurementTarget, sample.StartFrame, sample.EndFrame);
            double? pathDifference = null, occupancyDifference = null; bool? within = null;
            if (run is null || auto is null || rows.Any(r => r is null || r.Visibility != ReferenceVisibility.Visible) || auto.Length != rows.Length || auto.Any(p => !p.Accepted)) failures.Add("Continuous interval needs complete visible resolved references and accepted producer rows; missing/gaps are retained, not imputed.");
            else
            {
                var s = material!.Raw.Settings; var m = material.Raw.Metadata; var rx = s.Rx * m.Width; var ry = s.Ry * m.Height;
                double distanceReference = 0, distanceAuto = 0; int siteReference = 0, siteAuto = 0;
                for (var i = 0; i < rows.Length; i++)
                {
                    if (i > 0) { distanceReference += Distance(rows[i]!.X!.Value, rows[i]!.Y!.Value, rows[i - 1]!.X!.Value, rows[i - 1]!.Y!.Value, rx, ry); distanceAuto += Distance(auto[i].X!.Value, auto[i].Y!.Value, auto[i - 1].X!.Value, auto[i - 1].Y!.Value, rx, ry); }
                    if (s.Platform is { } site) { if (Inside(rows[i]!.X!.Value, rows[i]!.Y!.Value, site, m, s)) siteReference++; if (Inside(auto[i].X!.Value, auto[i].Y!.Value, site, m, s)) siteAuto++; }
                }
                pathDifference = Math.Abs(distanceAuto - distanceReference);
                if (s.Platform is not null) occupancyDifference = Math.Abs(siteAuto - siteReference) / (double)rows.Length; else failures.Add("Platform site is unset; site-occupancy consequence held.");
                within = pathDifference <= plan.Tolerances.PathDiameters && occupancyDifference.HasValue && occupancyDifference <= plan.Tolerances.SiteOccupancyFraction;
            }
            endpoints.Add(new(sample.VideoId, sample.StartFrame, sample.EndFrame, pathDifference, occupancyDifference, within, failures.ToImmutable()));
        }
        var frameRows = frames.ToImmutable();
        var recorded = frameRows.Count(f => !f.ReferenceIds.IsEmpty);
        if (recorded / (double)Math.Max(1, frameRows.Length) < plan.Tolerances.MinimumReferenceFraction) holds.Add("Prespecified reference-completion denominator is not met; unannotated cases remain visible.");
        var randomErrors = frameRows.Where(f => f.RandomSample && f.PositionErrorPixels.HasValue).Select(f => f.PositionErrorPixels!.Value).ToArray();
        var errors = frameRows.Where(f => f.PositionErrorPixels.HasValue).Select(f => f.PositionErrorPixels!.Value).ToArray();
        // Pooled figures keep every declared recording, including marked exclusions. Role summaries are separate and are not animal N or held-out accuracy.
        var splitSummaries = ValidationSplitSummaries.Build(plan, snapshot, sampling, frameRows);
        return new(sampling.PlanFingerprint, snapshot.ProducerFingerprint, snapshot.ReferenceFingerprint, sampling.Runs.FirstOrDefault()?.ComparatorBinarySha256 ?? "unavailable", frameRows, endpoints.ToImmutable(),
            plan.Recordings.Sum(r => index.Asset(r.VideoId).Metadata.FrameCount), frameRows.Length, recorded, errors.Length, randomErrors.Length > 0 ? randomErrors.Average() : null, errors.Length > 0 ? errors.Max() : null, holds.ToImmutable(), plan.MeasurementTarget, SplitSummaries: splitSummaries, PooledScopeLabel: ValidationSplitSummaries.PooledScopeLabel);
    }

    static HashSet<string> HashSources(IndependentValidationSnapshot snapshot, IndependentValidationPlan plan, CancellationToken cancellation)
    {
        var invalidSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in plan.Recordings)
        {
            cancellation.ThrowIfCancellationRequested();
            var asset = snapshot.Imported.Single(a => a.Id == source.VideoId);
            try
            {
                using var stream = new FileStream(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                int count;
                while ((count = stream.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
                if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != asset.SourceSha256) invalidSources.Add(source.VideoId);
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { invalidSources.Add(source.VideoId); }
        }
        return invalidSources;
    }
    private static bool Disagree(IndependentReference a, IndependentReference b, double tolerance) => a.Visibility != b.Visibility || a.Event != b.Event || a.Visibility == ReferenceVisibility.Visible && Math.Sqrt(Math.Pow(a.X!.Value - b.X!.Value, 2) + Math.Pow(a.Y!.Value - b.Y!.Value, 2)) > tolerance;
    private static double Distance(double x, double y, double priorX, double priorY, double rx, double ry) => Math.Sqrt(Math.Pow((x - priorX) / (2 * rx), 2) + Math.Pow((y - priorY) / (2 * ry), 2));
    private static bool Inside(double x, double y, PlatformSite site, VideoMetadata m, TrackingSettings s) => Math.Sqrt(Math.Pow((x - site.X * m.Width) / (s.Rx * m.Width), 2) + Math.Pow((y - site.Y * m.Height) / (s.Ry * m.Height), 2)) <= site.Radius / s.Rx;
}
