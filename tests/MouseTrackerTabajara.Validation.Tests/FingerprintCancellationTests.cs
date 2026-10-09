using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

/// <summary>Legacy sampling-fingerprint bytes and incremental cancellation. Fresh synthetic 3×24 controls. The retained S16 folder is optional.</summary>
public sealed class FingerprintCancellationTests(ITestOutputHelper output)
{
    const string RetainedS16FolderName = "s16-small-83242eaf5b8747eebc2500e1a5b1b03c";
    const int BulkChars = 4_000_000;

    static readonly Lazy<ThirtyRecordingPerformanceFixture> Synthetic3624 = new(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-g11-3x24-" + Guid.NewGuid().ToString("N"));
        return new ThirtyRecordingPerformanceFixture(folder, 3, 24);
    });

    [Fact]
    public void Utf8BytesAndHashMatchLegacyForUnicodeEscaping()
    {
        var document = new EscapeDocument
        {
            SelectionNote = "café \"quoted\" \\path\r\n<tag>&plus+`tick`\u0001\u2028日本語\U0001F42D end ",
            LaneEvent = ReferenceEvent.WaterEntry,
            QuotedLines = ["line\ttwo", "trail "],
            Head = "head"
        };
        var (json, utf8, hash, elapsed) = LegacyAndStream(document);
        Assert.Contains("selection_note", json, StringComparison.Ordinal);
        Assert.Contains("water_entry", json, StringComparison.Ordinal);
        Assert.Contains("\\u00E9", json, StringComparison.Ordinal);
        Assert.Contains("\\u0022", json, StringComparison.Ordinal);
        Assert.Contains("\\u0001", json, StringComparison.Ordinal);
        Assert.Contains("\\u003C", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionNote", json, StringComparison.Ordinal);
        Assert.DoesNotContain("WaterEntry", json, StringComparison.Ordinal);
        output.WriteLine($"unicode utf8={utf8.Length}; hash={hash}; legacySerializeMs={elapsed.TotalMilliseconds:R}");
    }

    [Fact]
    public void FreshBoundedFixtureReferenceKeysMatchBothTargets()
    {
        var fixture = Synthetic3624.Value;
        output.WriteLine("fresh synthetic evidence=" + fixture.Folder);
        foreach (var workload in fixture.Workloads)
        {
            var legacy = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(workload.Sampling));
            var (_, utf8, hash, elapsed) = LegacyAndStream(workload.Sampling);
            Assert.Equal(legacy, hash);
            var references = workload.Snapshot.History.Where(h => h.Kind == "reference").ToArray();
            Assert.Equal(workload.ExpectedFrames.Count(f => !f.MissingReference) * 2, references.Length);
            Assert.All(references, entry =>
            {
                Assert.StartsWith(legacy + ":", entry.EntityId, StringComparison.Ordinal);
                var reference = JsonDefaults.Deserialize<IndependentReference>(entry.Json);
                Assert.Equal(entry.EntityId, IndependentValidationComparison.ReferenceKey(legacy, reference));
            });
            var report = IndependentValidationComparison.Compare(workload.Snapshot, workload.Plan, workload.Sampling);
            AssertJoins(workload, report);
            output.WriteLine($"target={workload.Plan.MeasurementTarget}; utf8={utf8.Length}; sample={hash}; legacySerializeMs={elapsed.TotalMilliseconds:R}; joined={report.PositionJoinedFrames}; recorded={report.ReferenceRecordedFrames}; references={references.Length}");
        }
        AssertRetainedFolderWhenPresent();
    }

    [Fact]
    public void PreCancelSkipsFingerprintAndMidCancelStopsOnControlledWrite()
    {
        Assert.Null(CancelableJsonHash.TestBeforeWrite.Value);
        var workload = Synthetic3624.Value.Workloads.Single(w => w.Plan.MeasurementTarget == ValidationMeasurementTarget.RawAutomatic);
        var inflated = Inflate(workload.Sampling);
        var (json, utf8, hash, legacyElapsed) = LegacyAndStream(inflated);
        Assert.True(utf8.Length > BulkChars);
        Assert.Equal(IndependentValidationSnapshot.Hash(json), hash);
        output.WriteLine($"large utf8={utf8.Length}; legacySerializeMs={legacyElapsed.TotalMilliseconds:R}; hash={hash}");

        var snapshot = workload.Snapshot;
        var plan = workload.Plan;
        using (var warmup = new CancellationTokenSource())
        {
            warmup.Cancel();
            Assert.Throws<OperationCanceledException>(() => IndependentValidationComparison.Compare(snapshot, plan, workload.Sampling, warmup.Token));
        }

        var probeCalls = 0;
        var pre = Stopwatch.StartNew();
        try
        {
            CancelableJsonHash.TestBeforeWrite.Value = _ => probeCalls++;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            ValidationComparisonReport? report = null;
            var error = Assert.Throws<OperationCanceledException>(() => report = IndependentValidationComparison.Compare(snapshot, plan, inflated, cancelled.Token));
            pre.Stop();
            Assert.Null(report);
            Assert.Equal(0, probeCalls);
            Assert.True(pre.Elapsed < TimeSpan.FromMilliseconds(500), $"pre-cancel took {pre.Elapsed.TotalMilliseconds:R} ms");
            Assert.True(pre.Elapsed * 4 < legacyElapsed, $"pre-cancel {pre.Elapsed.TotalMilliseconds:R} ms was not prompt against fingerprint {legacyElapsed.TotalMilliseconds:R} ms");
            output.WriteLine($"pre-cancel ms={pre.Elapsed.TotalMilliseconds:R}; probeWrites={probeCalls}; exception={error.GetType().Name}");
        }
        finally { CancelableJsonHash.TestBeforeWrite.Value = null; }

        var observedWrites = 0;
        var observedBytes = 0;
        var mid = Stopwatch.StartNew();
        try
        {
            using var during = new CancellationTokenSource();
            CancelableJsonHash.TestBeforeWrite.Value = count =>
            {
                observedWrites++;
                observedBytes += count;
                if (observedBytes >= 64 * 1024) during.Cancel();
            };
            ValidationComparisonReport? report = null;
            Assert.Throws<OperationCanceledException>(() => report = IndependentValidationComparison.Compare(snapshot, plan, inflated, during.Token));
            mid.Stop();
            Assert.Null(report);
            Assert.True(observedWrites >= 2, $"writes={observedWrites}");
            Assert.True(observedBytes >= 32 * 1024, $"bytes={observedBytes}");
            Assert.True(observedBytes * 4 < utf8.Length, $"stopped at {observedBytes} of {utf8.Length}");
            Assert.True(mid.Elapsed * 2 < legacyElapsed, $"mid-cancel {mid.Elapsed.TotalMilliseconds:R} ms did not stop before fingerprint {legacyElapsed.TotalMilliseconds:R} ms");
            output.WriteLine($"mid-cancel ms={mid.Elapsed.TotalMilliseconds:R}; writes={observedWrites}; bytes={observedBytes}");
        }
        finally { CancelableJsonHash.TestBeforeWrite.Value = null; }

        Assert.Equal(hash, CancelableJsonHash.Hash(inflated, CancellationToken.None));
        var after = IndependentValidationComparison.Compare(snapshot, plan, workload.Sampling);
        var again = IndependentValidationComparison.Compare(snapshot, plan, workload.Sampling);
        Assert.Equal(JsonDefaults.Serialize(after), JsonDefaults.Serialize(again));
        AssertJoins(workload, after);
    }

    [Fact]
    public void CancelledComparisonDoesNotPoisonEitherTarget()
    {
        Assert.Null(CancelableJsonHash.TestBeforeWrite.Value);
        foreach (var workload in Synthetic3624.Value.Workloads)
        {
            var before = IndependentValidationComparison.Compare(workload.Snapshot, workload.Plan, workload.Sampling);
            var inflated = Inflate(workload.Sampling);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                ValidationComparisonReport? partial = null;
                Assert.Throws<OperationCanceledException>(() => partial = IndependentValidationComparison.Compare(workload.Snapshot, workload.Plan, inflated, cancelled.Token));
                Assert.Null(partial);
            }
            var observedBytes = 0;
            try
            {
                using var during = new CancellationTokenSource();
                CancelableJsonHash.TestBeforeWrite.Value = count =>
                {
                    observedBytes += count;
                    if (observedBytes >= 64 * 1024) during.Cancel();
                };
                ValidationComparisonReport? partial = null;
                Assert.Throws<OperationCanceledException>(() => partial = IndependentValidationComparison.Compare(workload.Snapshot, workload.Plan, inflated, during.Token));
                Assert.Null(partial);
                Assert.True(observedBytes >= 64 * 1024 && observedBytes * 4 < BulkChars, $"bytes={observedBytes}");
            }
            finally { CancelableJsonHash.TestBeforeWrite.Value = null; }
            var after = IndependentValidationComparison.Compare(workload.Snapshot, workload.Plan, workload.Sampling);
            Assert.Equal(JsonDefaults.Serialize(before), JsonDefaults.Serialize(after));
            AssertJoins(workload, after);
            output.WriteLine($"target={workload.Plan.MeasurementTarget}; joined={after.PositionJoinedFrames}; midCancelBytes={observedBytes}");
        }
    }

    [Fact]
    public void OutsideRunCorrectionFailureSurvivesCancellation()
    {
        Assert.Null(CancelableJsonHash.TestBeforeWrite.Value);
        var (snapshot, plan, sampling) = BadCorrection();
        var first = Assert.Throws<ArgumentException>(() => IndependentValidationComparison.Compare(snapshot, plan, sampling));
        Assert.Contains("Correction is outside this run", first.Message, StringComparison.Ordinal);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => IndependentValidationComparison.Compare(snapshot, plan, sampling, cancelled.Token));
        }
        var again = Assert.Throws<ArgumentException>(() => IndependentValidationComparison.Compare(snapshot, plan, sampling));
        Assert.Contains("Correction is outside this run", again.Message, StringComparison.Ordinal);
        output.WriteLine("outside-run correction still throws after a cancelled call");
    }

    [Fact]
    public void GrowthChecksCancellationBeforeLargerBuffer()
    {
        Assert.Null(CancelableJsonHash.TestBeforeWrite.Value);
        CancelableJsonHash.TestLargestAllocatedBuffer = 0;
        using var gate = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => CancelableJsonHash.Hash(new LargeGrowthDocument { Gate = gate }, gate.Token));
        var cancelledAllocation = CancelableJsonHash.TestLargestAllocatedBuffer;
        Assert.True(cancelledAllocation < 1_000_000, $"cancelled allocation {cancelledAllocation}");

        var (json, utf8, hash, elapsed) = LegacyAndStream(new LargeGrowthDocument());
        Assert.Equal(IndependentValidationSnapshot.Hash(json), hash);
        Assert.True(utf8.Length > LargeGrowthDocument.PayloadLength);
        var successfulAllocation = CancelableJsonHash.TestLargestAllocatedBuffer;
        Assert.True(successfulAllocation >= LargeGrowthDocument.PayloadLength, $"successful allocation {successfulAllocation}");
        output.WriteLine($"growth cancelledAllocation={cancelledAllocation}; successfulAllocation={successfulAllocation}; utf8={utf8.Length}; legacySerializeMs={elapsed.TotalMilliseconds:R}; hash={hash}");
    }

    void AssertRetainedFolderWhenPresent()
    {
        var folder = RetainedS16Folder();
        if (folder is null)
        {
            output.WriteLine("retained " + RetainedS16FolderName + " absent");
            return;
        }
        foreach (var target in new[] { ValidationMeasurementTarget.RawAutomatic, ValidationMeasurementTarget.InspectedCorrected })
        {
            var samplingPath = Path.Combine(folder, target + "-sampling.json");
            var fileBytes = File.ReadAllBytes(samplingPath);
            var fileHash = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
            Assert.Equal(RetainedSamplingSha256[target], fileHash);
            Assert.Equal(181647, fileBytes.Length);
            var workload = ThirtyRecordingPerformanceFixture.Read(folder, target);
            var legacy = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(workload.Sampling));
            var streamed = CancelableJsonHash.Hash(workload.Sampling, CancellationToken.None);
            Assert.Equal(fileHash, legacy);
            Assert.Equal(legacy, streamed);
            var report = IndependentValidationComparison.Compare(workload.Snapshot, workload.Plan, workload.Sampling);
            AssertJoins(workload, report);
            output.WriteLine($"retained target={target}; fileSha256={fileHash}; joined={report.PositionJoinedFrames}; recorded={report.ReferenceRecordedFrames}");
        }
    }

    static readonly Dictionary<ValidationMeasurementTarget, string> RetainedSamplingSha256 = new()
    {
        [ValidationMeasurementTarget.RawAutomatic] = "a84d17a812f1c72dde922ee010066eecee582c4aefbe8ba10936c2db7327d8b8",
        [ValidationMeasurementTarget.InspectedCorrected] = "8eb34cab9a5c2f82c8a80691b56da10265d6d23aed1249470846e661762d4e7b"
    };

    static void AssertJoins(ThirtyRecordingPerformanceFixture.Workload workload, ValidationComparisonReport report)
    {
        Assert.Equal(workload.Plan.MeasurementTarget, report.MeasurementTarget);
        Assert.Equal(workload.Snapshot.Imported.Sum(asset => asset.Metadata.FrameCount), report.FullSourcePopulation);
        Assert.Equal(workload.ExpectedFrames.Length, report.SampledUniqueFrames);
        Assert.Equal(workload.ExpectedFrames.Length, report.Frames.Length);
        Assert.Equal(workload.ExpectedFrames.Count(frame => !frame.MissingReference), report.ReferenceRecordedFrames);
        var joined = workload.ExpectedFrames.Where(frame => frame.PositionErrorPixels.HasValue).Select(frame => frame.PositionErrorPixels!.Value).ToArray();
        Assert.NotEmpty(joined);
        Assert.Equal(joined.Length, report.PositionJoinedFrames);
        Assert.Equal(joined.Max(), report.MaximumPositionErrorPixels);
        foreach (var truth in workload.ExpectedFrames)
        {
            var actual = report.Frames.Single(frame => frame.VideoId == truth.VideoId && frame.Frame == truth.Frame);
            Assert.Equal(truth.PositionErrorPixels, actual.PositionErrorPixels);
            Assert.Equal(truth.FalseAccepted, actual.FalseAccepted);
            Assert.Equal(truth.MissedVisible, actual.MissedVisible);
            Assert.Equal(truth.Disputed, actual.Disagreement);
        }
        Assert.Equal(workload.ExpectedEndpoints.Length, report.Endpoints.Length);
        foreach (var truth in workload.ExpectedEndpoints)
        {
            var actual = report.Endpoints.Single(endpoint => endpoint.VideoId == truth.VideoId && endpoint.StartFrame == truth.StartFrame && endpoint.EndFrame == truth.EndFrame);
            Assert.Equal(truth.PathDifferenceDiameters, actual.PathDifferenceDiameters);
            Assert.Equal(truth.SiteOccupancyDifference, actual.SiteOccupancyDifference);
            Assert.Equal(truth.WithinTolerances, actual.WithinTolerances);
        }
    }

    static ValidationSampling Inflate(ValidationSampling sampling)
    {
        var seed = sampling.Cases[0];
        var head = "café \"q\" \\\r\n<&>\u0001" + new string('A', BulkChars);
        var extra = new ValidationSamplingCase(seed.VideoId, seed.StartFrame, seed.EndFrame, true, seed.InclusionProbability, seed.PopulationFrames, false, head);
        return sampling with { Cases = sampling.Cases.Add(extra) };
    }

    static (string Json, byte[] Utf8, string Hash, TimeSpan Elapsed) LegacyAndStream<T>(T value)
    {
        var watch = Stopwatch.StartNew();
        var json = JsonDefaults.Serialize(value);
        var legacyHash = IndependentValidationSnapshot.Hash(json);
        watch.Stop();
        var buffer = new ArrayBufferWriter<byte>();
        CancelableJsonHash.WriteUtf8(value, buffer, CancellationToken.None);
        var utf8 = buffer.WrittenSpan.ToArray();
        AssertSameBytes(Encoding.UTF8.GetBytes(json), utf8);
        var streamed = CancelableJsonHash.Hash(value, CancellationToken.None);
        Assert.Equal(legacyHash, streamed);
        return (json, utf8, streamed, watch.Elapsed);
    }

    static void AssertSameBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (expected.SequenceEqual(actual)) return;
        var length = Math.Min(expected.Length, actual.Length);
        var index = 0;
        while (index < length && expected[index] == actual[index]) index++;
        var start = Math.Max(0, index - 40);
        var expectedWindow = Window(expected, start);
        var actualWindow = Window(actual, start);
        Assert.Fail($"UTF-8 differs at {index}. legacyLen={expected.Length} streamLen={actual.Length} legacy={expectedWindow} stream={actualWindow}");
    }

    static string Window(ReadOnlySpan<byte> bytes, int start)
    {
        if (start >= bytes.Length) return "";
        var count = Math.Min(80, bytes.Length - start);
        return Encoding.UTF8.GetString(bytes.Slice(start, count));
    }

    static (IndependentValidationSnapshot Snapshot, IndependentValidationPlan Plan, ValidationSampling Sampling) BadCorrection()
    {
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var asset = new VideoAsset { Id = "video-1", Path = "unused-source.bin", SourceSha256 = "abc", Metadata = new(64, 64, 10, 4) };
        var run = new AnalysisRun
        {
            Id = "run-1", VideoId = asset.Id, SourceSha256 = asset.SourceSha256, CreatedUtc = epoch,
            AlgorithmContract = AlgorithmIdentity.Contract, SoftwareVersion = "synthetic-fingerprint-control",
            Metadata = asset.Metadata, Points = [new TrackPoint(0, 0, 40, 32, PointStatus.Tracked, 1)]
        };
        var runJson = JsonDefaults.Serialize(run);
        var correction = JsonDefaults.Serialize(new ManualCorrection("wrong-run", 0, 40, 32, "Outside this run", epoch));
        var frozen = new ValidationFrozenRun(asset.Id, asset.SourceSha256, JsonDefaults.Serialize(asset), runJson, [correction], IndependentValidationSnapshot.Hash(runJson), "corrections", run.AlgorithmContract, run.SoftwareVersion, "fingerprint-control", []);
        var plan = new IndependentValidationPlan
        {
            Id = "cancel-failure", SyntheticToolingOnly = true, MeasurementTarget = ValidationMeasurementTarget.RawAutomatic,
            RandomFramesPerRecording = 1, DifficultIntervalsPerRecording = 0, IntervalFrames = 1, SecondReviewFraction = 0,
            Tolerances = new(5, 3, .25, .1, .05, .1),
            Recordings = [new ValidationRecording(asset.Id, "session-1", ValidationSplit.Development, true, false, "protocol", "allocation")]
        };
        var sampling = new ValidationSampling(IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(plan)), "fixed-producer", plan.Seed, [frozen], [new ValidationSamplingCase(asset.Id, 0, 0, true, 1, 4, false, "random")]);
        var snapshot = new IndependentValidationSnapshot("no-live-database", "fixed-producer", "ref", [frozen], [asset], []);
        return (snapshot, plan, sampling);
    }

    static string? RetainedS16Folder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var folder = Path.Combine(dir.FullName, "artifacts", "comparison-cache", RetainedS16FolderName);
            if (File.Exists(Path.Combine(folder, "manifest.json"))) return folder;
        }
        return null;
    }

    sealed class EscapeDocument
    {
        public string SelectionNote { get; init; } = "";
        public ReferenceEvent LaneEvent { get; init; }
        public ImmutableArray<string> QuotedLines { get; init; } = [];
        public string Head { get; init; } = "";
    }

    sealed class LargeGrowthDocument
    {
        public const int PayloadLength = 1_500_000;
        static readonly string Payload = new string('B', PayloadLength);

        [JsonIgnore]
        public CancellationTokenSource? Gate { get; init; }

        public string Head { get; init; } = "head";

        public string Tail => Gate is null ? Payload : CancelAndPayload();

        string CancelAndPayload()
        {
            Gate!.Cancel();
            return Payload;
        }
    }
}
