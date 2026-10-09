using System.Text.Json;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;

namespace MouseTrackerTabajara.Tests;

public static class TestPaths
{
    public static string Root
    {
        get
        {
            for (var p = new DirectoryInfo(AppContext.BaseDirectory); p is not null; p = p.Parent)
                if (File.Exists(Path.Combine(p.FullName, "MouseTrackerTabajara.sln"))) return p.FullName;
            throw new InvalidOperationException("Repository root not found.");
        }
    }
}

public class ScientificRegressionTests
{
    public static IEnumerable<object[]> MetricCases()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "fixtures/metric-reference-cases.json")));
        foreach (var item in file.RootElement.GetProperty("cases").EnumerateArray()) yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }
    [Theory] [MemberData(nameof(MetricCases))]
    public void NumericalReferenceCasesAgree(string name, string json)
    {
        using var file = JsonDocument.Parse(json); var root = file.RootElement;
        var settings = JsonDefaults.Deserialize<TrackingSettings>(root.GetProperty("settings").GetRawText());
        var metadata = JsonDefaults.Deserialize<VideoMetadata>(root.GetProperty("metadata").GetRawText());
        var points = root.GetProperty("points").EnumerateArray().Select(p => new TrackPoint(p[0].GetInt32(), p[0].GetInt32() / metadata.Fps,
            p[1].ValueKind == JsonValueKind.Null ? null : p[1].GetInt32(), p[2].ValueKind == JsonValueKind.Null ? null : p[2].GetInt32(),
            Enum.Parse<PointStatus>(p[3].GetString()!, true), p[4].GetInt32())).ToArray();
        var result = MetricCalculator.Calculate(points, metadata, settings); var expected = root.GetProperty("expected");
        AssertClose(result.Distance, expected.GetProperty("distance").GetDouble(), name);
        AssertClose(result.UnknownSeconds, expected.GetProperty("unknown_seconds").GetDouble(), name);
        AssertClose(result.CoveragePercent, expected.GetProperty("coverage_percent").GetDouble(), name);
        AssertClose(result.MeanSpeed, Number(expected, "mean_speed"), name);
        AssertClose(result.SiteVisitTime, Number(expected, "site_visit_time"), name);
        Assert.Equal(expected.GetProperty("site_visit_observed").ValueKind == JsonValueKind.Null ? null : (bool?)expected.GetProperty("site_visit_observed").GetBoolean(), result.SiteVisitObserved);
        foreach (var q in result.QuadrantSeconds) AssertClose(q.Value, expected.GetProperty("quadrant_seconds").GetProperty(q.Key).GetDouble(), name);
        foreach (var endpoint in result.EndpointEligibility) Assert.Equal(expected.GetProperty("endpoint_eligibility").GetProperty(endpoint.Key).GetBoolean(), endpoint.Value.Eligible);
        foreach (var q in result.QuadrantFullTrialPercentBounds)
        {
            var values = expected.GetProperty("quadrant_full_trial_percent_bounds").GetProperty(q.Key);
            AssertClose(q.Value.Lower, values[0].GetDouble(), name); AssertClose(q.Value.Upper, values[1].GetDouble(), name);
        }
        AssertClose(result.QuadrantSeconds.Values.Sum() + result.UnknownSeconds, result.DurationSeconds, name);
    }
    private static double? Number(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetDouble();
    private static void AssertClose(double? actual, double? expected, string name)
    {
        Assert.True(actual.HasValue == expected.HasValue, name + " null handling differs.");
        if (actual.HasValue && expected.HasValue) Assert.True(Math.Abs(actual.Value - expected.Value) <= 1e-10, name + $": {actual} != {expected}");
    }

    public static IEnumerable<object[]> MaskCases()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "fixtures/mask-reference-cases.json")));
        foreach (var item in file.RootElement.EnumerateArray()) yield return [item.GetProperty("frame").GetInt32(), item.GetProperty("input").GetString()!, item.GetProperty("expected").GetString()!];
    }
    [Theory] [MemberData(nameof(MaskCases))]
    public void OriginalImageMasksAgreePixelForPixel(int frame, string input, string expected)
    {
        var settings = JsonDefaults.Deserialize<TrackingSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "fixtures/sample-settings.json")));
        using var image = Cv2.ImRead(Path.Combine(TestPaths.Root, "fixtures", input));
        using var reference = Cv2.ImRead(Path.Combine(TestPaths.Root, "fixtures", expected), ImreadModes.Grayscale);
        using var arena = OpenCvTracker.ArenaMask(image.Width, image.Height, settings);
        using var binary = OpenCvTracker.ProcessFrame(image, settings, arena); using var difference = new Mat();
        Cv2.Absdiff(reference, binary, difference);
        Assert.True(Cv2.CountNonZero(difference) == 0, $"Frame {frame} mask differs.");
        Assert.Single(OpenCvTracker.Detect(binary, settings));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ExclusionsStayBackgroundForEitherPolarity(bool invert)
    {
        var settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, Blur = 0, Invert = invert,
            Exclusions = [new(.3, .3, .6, .6)] };
        using var image = new Mat(100, 100, MatType.CV_8UC3, new Scalar(180, 90, 20));
        using var arena = OpenCvTracker.ArenaMask(100, 100, settings); using var binary = OpenCvTracker.ProcessFrame(image, settings, arena);
        Assert.Equal(0, binary.At<byte>(40, 40));
    }
    [Fact] public void InvalidSettingsFailBeforeProcessing()
    {
        Assert.Throws<ArgumentException>(() => new TrackingSettings { Cx = double.NaN }.Validate());
        Assert.Throws<ArgumentException>(() => new TrackingSettings { StartSeconds = 5, EndSeconds = 4 }.Validate());
        Assert.Throws<ArgumentException>(() => new TrackingSettings { MinArea = 500, MaxArea = 100 }.Validate());
    }
    [Fact] public void ExplicitGapsAndRandomAuditsAreRetained()
    {
        var metadata = new VideoMetadata(200, 200, 10, 3);
        var points = new List<TrackPoint> { new(0, 0, 80, 80, PointStatus.Tracked, 1), new(1, .1, null, null, PointStatus.Missing, 0), new(2, .2, 90, 80, PointStatus.Tracked, 1) };
        var run = new AnalysisRun { Metadata = metadata, Points = points, SourceSha256 = "stable" };
        var queue = MetricCalculator.ReviewQueue(run);
        Assert.Contains(queue, p => !p.RandomAudit); Assert.Contains(queue, p => p.RandomAudit);
        Assert.Equal(queue, MetricCalculator.ReviewQueue(run));
    }
    [Fact] public void FilterBoundariesAndMomentCentroidsAreExplicit()
    {
        using var binary = new Mat(100, 100, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(binary, new Point(30, 30), new Point(50, 40), Scalar.White, -1);
        var settings = new TrackingSettings { MinArea = 200, MaxArea = 200, MaxExtension = 20 };
        var detection = Assert.Single(OpenCvTracker.Detect(binary, settings));
        Assert.Equal(40, detection.X); Assert.Equal(35, detection.Y); Assert.Equal(200, detection.Area);
        Assert.Empty(OpenCvTracker.Detect(binary, settings with { MaxExtension = 19 }));
        Assert.Empty(OpenCvTracker.Detect(binary, settings with { MinArea = 201, MaxArea = 300 }));
        Assert.Empty(OpenCvTracker.Detect(binary, settings with { MinArea = 100, MaxArea = 199 }));
    }
    [Fact] public void IrregularOrMissingPresentationTimesAreWithheld()
    {
        var proper = new[] { new TrackPoint(10, 1, 80, 80, PointStatus.Tracked, 1), new TrackPoint(11, 1.1, 81, 80, PointStatus.Tracked, 1) };
        OpenCvTracker.VerifyNominalTiming(proper, 10);
        Assert.Throws<InvalidDataException>(() => OpenCvTracker.VerifyNominalTiming([proper[0], proper[1] with { TimeSeconds = 1.2 }], 10));
        Assert.Throws<InvalidDataException>(() => OpenCvTracker.VerifyNominalTiming([proper[0] with { TimeSeconds = double.NaN }], 10));
    }
}
