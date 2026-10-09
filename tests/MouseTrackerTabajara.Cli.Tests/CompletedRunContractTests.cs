using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Tracking;
using Xunit;

namespace MouseTrackerTabajara.Cli.Tests;

public sealed class CompletedRunContractTests
{
    [Fact]
    public void CanonicalIntervalRejectsNonemptyPartialAndGappedRuns()
    {
        var metadata = new VideoMetadata(200, 200, 10, 12);
        var settings = Settings();
        var complete = Run(metadata, settings, Enumerable.Range(0, 12).Select(frame => Point(frame, metadata.Fps)).ToList());
        CompletedRunContract.Ensure(complete, metadata, settings, complete.SourceSha256, complete.AlgorithmContract, complete.SoftwareVersion);
        Assert.Equal((0, 12), CompletedRunContract.ExpectedInterval(metadata, settings));
        Assert.Equal((1, 5), CompletedRunContract.ExpectedInterval(metadata with { FrameCount = 8 }, settings with { StartSeconds = .1, EndSeconds = .5 }));

        var one = complete with { Points = [complete.Points[0]], NominalTimingVerified = true, Metrics = complete.Metrics with { TotalFrames = 1, TrackedFrames = 1 } };
        Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(one, metadata, settings, one.SourceSha256, one.AlgorithmContract, one.SoftwareVersion));
        var frame99 = complete with { Points = [Point(99, metadata.Fps)] };
        Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(frame99, metadata, settings, frame99.SourceSha256, frame99.AlgorithmContract, frame99.SoftwareVersion));
        var gapped = complete with { Points = [Point(0, metadata.Fps), Point(2, metadata.Fps)] };
        Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(gapped, metadata, settings, gapped.SourceSha256, gapped.AlgorithmContract, gapped.SoftwareVersion));
        var tracked = complete with { Metrics = complete.Metrics with { TrackedFrames = complete.Metrics.TrackedFrames - 1 } };
        Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(tracked, metadata, settings, tracked.SourceSha256, tracked.AlgorithmContract, tracked.SoftwareVersion));
        var counts = complete.Metrics.StatusCounts.ToDictionary(pair => pair.Key, pair => pair.Value);
        counts["tracked"]--;
        var status = complete with { Metrics = complete.Metrics with { StatusCounts = counts } };
        Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(status, metadata, settings, status.SourceSha256, status.AlgorithmContract, status.SoftwareVersion));
        var late = complete with { Points = complete.Points.Select(point => point with { TimeSeconds = point.Frame == 3 ? point.TimeSeconds + .02 : point.TimeSeconds }).ToList() };
        Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(late, metadata, settings, late.SourceSha256, late.AlgorithmContract, late.SoftwareVersion));
        Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(complete with { Settings = settings with { Cx = .2 } }, metadata, settings, complete.SourceSha256, complete.AlgorithmContract, complete.SoftwareVersion));
    }

    [Fact]
    public void ApplicationContractRejectsStatusDistributionCoordinatesAndOverflow()
    {
        var metadata = new VideoMetadata(200, 200, 10, 12);
        var settings = Settings();
        var complete = Run(metadata, settings, Enumerable.Range(0, 12).Select(frame => Point(frame, metadata.Fps)).ToList());
        var swapped = complete.Metrics.StatusCounts.ToDictionary(pair => pair.Key, pair => pair.Value);
        var tracked = swapped["tracked"];
        swapped["tracked"] = 0;
        swapped["missing"] = tracked;
        var wrongDistribution = complete with { Metrics = complete.Metrics with { StatusCounts = swapped } };
        var distribution = Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(wrongDistribution, metadata, settings, wrongDistribution.SourceSha256, wrongDistribution.AlgorithmContract, wrongDistribution.SoftwareVersion));
        Assert.Contains("status counts", distribution.Message, StringComparison.OrdinalIgnoreCase);

        var outside = complete.Points.ToList();
        outside[0] = outside[0] with { X = metadata.Width, Y = 1 };
        var outsideRun = complete with { Points = outside };
        var coordinates = Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(outsideRun, metadata, settings, outsideRun.SourceSha256, outsideRun.AlgorithmContract, outsideRun.SoftwareVersion));
        Assert.Contains("Accepted coordinates are outside the video.", coordinates.Message, StringComparison.Ordinal);

        var malformed = complete.Points.ToList();
        malformed[1] = malformed[1] with { X = null, Y = null };
        var malformedRun = complete with { Points = malformed };
        var evidence = Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(malformedRun, metadata, settings, malformedRun.SourceSha256, malformedRun.AlgorithmContract, malformedRun.SoftwareVersion));
        Assert.Contains("Point status/coordinate evidence is inconsistent.", evidence.Message, StringComparison.Ordinal);

        var overflow = Assert.Throws<InvalidDataException>(() => CompletedRunContract.ExpectedInterval(metadata, settings with { StartSeconds = 1e20 }));
        Assert.Contains("Selected interval is outside this recording.", overflow.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => CompletedRunContract.ExpectedInterval(metadata, settings with { StartSeconds = double.PositiveInfinity }));
        var infiniteTime = complete.Points.ToList();
        infiniteTime[2] = infiniteTime[2] with { TimeSeconds = double.NaN };
        var infiniteRun = complete with { Points = infiniteTime };
        var timing = Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(infiniteRun, metadata, settings, infiniteRun.SourceSha256, infiniteRun.AlgorithmContract, infiniteRun.SoftwareVersion));
        Assert.Contains("Frame timestamps violate the nominal CFR contract.", timing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DerivedEndpointsMustMatchPathUnitsAndFirstVisitHistory()
    {
        var metadata = new VideoMetadata(200, 200, 10, 4);
        var settings = Settings() with { Platform = new PlatformSite(.2, .2, .05), DiameterCm = 100 };
        var points = Enumerable.Range(0, 4).Select(frame => Point(frame, metadata.Fps)).ToList();
        var complete = Run(metadata, settings, points);
        CompletedRunContract.Ensure(complete, metadata, settings, complete.SourceSha256, complete.AlgorithmContract, complete.SoftwareVersion);

        var wrongPath = complete with { Metrics = complete.Metrics with { Distance = complete.Metrics.Distance + 1 } };
        var path = Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(wrongPath, metadata, settings, wrongPath.SourceSha256, wrongPath.AlgorithmContract, wrongPath.SoftwareVersion));
        Assert.Contains("distance", path.Message, StringComparison.OrdinalIgnoreCase);

        var wrongUnit = complete with { Metrics = complete.Metrics with { DistanceUnit = "furlongs" } };
        var unit = Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(wrongUnit, metadata, settings, wrongUnit.SourceSha256, wrongUnit.AlgorithmContract, wrongUnit.SoftwareVersion));
        Assert.Contains("Distance unit", unit.Message, StringComparison.Ordinal);

        var wrongVisit = complete with { Metrics = complete.Metrics with { SiteVisitStatus = EventHistory.RightCensoredComplete } };
        var visit = Assert.Throws<InvalidDataException>(() => CompletedRunContract.Ensure(wrongVisit, metadata, settings, wrongVisit.SourceSha256, wrongVisit.AlgorithmContract, wrongVisit.SoftwareVersion));
        Assert.Contains("First-visit history", visit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(complete.SoftwareVersion, complete.Metrics.DistanceUnit, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanFileHashMatchesTheNativeFileHash()
    {
        var path = Path.Combine(Path.GetTempPath(), "tabajara-replay-hash-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var bytes = "frozen-plan"u8.ToArray();
            File.WriteAllBytes(path, bytes);
            Assert.Equal(OpenCvTracker.HashFile(path), ReplayHashes.Sha256Hex(bytes));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static TrackingSettings Settings() => new() { Cx = .5, Cy = .5, Rx = .4, Ry = .4, Threshold = 40, Invert = true, Blur = 1, MinArea = 20, MaxArea = 5000, MaxExtension = 80, MaxJump = 40, ArenaConfirmed = true };
    private static TrackPoint Point(int frame, double fps) => new(frame, frame / fps, 40, 40, PointStatus.Tracked, 1);
    private static AnalysisRun Run(VideoMetadata metadata, TrackingSettings settings, List<TrackPoint> points) => new()
    {
        VideoId = "recording-a",
        SourceSha256 = new string('a', 64),
        AlgorithmContract = AlgorithmIdentity.Contract,
        SoftwareVersion = CompletedRunContract.SoftwareVersion,
        Metadata = metadata,
        Settings = settings,
        Points = points,
        NominalTimingVerified = true,
        Metrics = MetricCalculator.Calculate(points, metadata, settings)
    };
}
