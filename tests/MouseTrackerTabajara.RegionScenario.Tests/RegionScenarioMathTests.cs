using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;
using Xunit;

namespace MouseTrackerTabajara.RegionScenario.Tests;

public class RegionScenarioMathTests
{
    private static readonly VideoMetadata Meta = new(200, 160, 10, 5);
    private static TrackingSettings Baseline() => new()
    {
        Cx = .5, Cy = .5, Rx = .4, Ry = .25, ArenaConfirmed = true, Platform = new(.5, .5, .05)
    };
    private static ScenarioIdentityHeader Identity()
    {
        var implementation = ScenarioImplementationProbe.Capture();
        return new()
        {
            OriginalRunId = "synthetic-run", SourceSha256 = new string('a', 64), SettingsHash = "settings-hash",
            CorrectionFingerprint = "corrections", CodeContract = implementation.CodeContract, ImplementationSha256 = implementation.CanonicalSha256
        };
    }
    private static TrackPoint At(int frame, int? x, int? y) => new(frame, frame / 10d, x, y, x is null ? PointStatus.Missing : PointStatus.Tracked, x is null ? 0 : 1);
    private static List<TrackPoint> CleanPath() => [At(0, 111, 80), At(1, 100, 80), At(2, 100, 80), At(3, 111, 80), At(4, 100, 85)];
    private static List<TrackPoint> GapPath() => [At(0, null, null), At(1, 111, 80), At(2, 100, 80), At(3, 100, 80), At(4, 130, 80)];
    private static NamedRegionScenario Geometry(string name, string rationale, RegionScenarioKind kind, DisclosedUnit unit, double dx, double dy, double extent, double ry, double min, double max) => new()
    {
        Name = name, Rationale = rationale, Kind = kind, Unit = unit,
        CenterDeltaX = dx, CenterDeltaY = dy, ExtentDelta = extent, PoolRyDelta = ry,
        ComponentRange = new(unit, min, max), RoomRange = new(DisclosedUnit.Centimeters, 0, 0)
    };

    [Fact]
    public void PlatformCenterAndRadiusChangesHaveIndependentSiteConsequences()
    {
        var points = CleanPath(); var settings = Baseline();
        var direct = MetricCalculator.Calculate(points, Meta, settings);
        Assert.Equal(.3, direct.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(2, direct.PlatformSiteEntries);
        Assert.Equal(.1, direct.SiteVisitTime!.Value, 9);
        Assert.Equal(EventHistory.ObservedExact, direct.SiteVisitStatus);
        Assert.Equal(11d / 160 + 11d / 160 + Math.Sqrt(221d) / 160, direct.Distance, 9);
        Assert.Equal("pool diameters", direct.DistanceUnit);
        Assert.Equal("SE", direct.TargetQuadrant);
        Assert.Equal(100, direct.TargetObservedPercent!.Value, 9);

        var center = Geometry("center-plus-12", "Twelve source pixels of platform-center uncertainty, documented before calculation.", RegionScenarioKind.PlatformCenterExtent, DisclosedUnit.SourcePixels, 12, 0, 0, 0, -12, 12);
        var radius = Geometry("radius-minus-6", "Six source pixels less platform radius, documented before calculation.", RegionScenarioKind.PlatformCenterExtent, DisclosedUnit.SourcePixels, 0, 0, -6, 0, -12, 12);
        var normalized = Geometry("center-plus-normalized", "The same twelve source pixels expressed as a width fraction, documented before calculation.", RegionScenarioKind.PlatformCenterExtent, DisclosedUnit.Normalized, 12d / 200, 0, 0, 0, -.2, .2);
        var set = RegionScenarioMath.Evaluate(points, Meta, settings, [center, radius, normalized], Identity());
        var moved = set.Scenarios.Single(s => s.Name == "center-plus-12");
        var smaller = set.Scenarios.Single(s => s.Name == "radius-minus-6");
        var same = set.Scenarios.Single(s => s.Name == "center-plus-normalized");
        Assert.Equal(RegionScenarioWording.FrozenCoordinateMode, moved.Mode);
        Assert.DoesNotContain("retracking occurred", moved.Mode, StringComparison.Ordinal);
        Assert.Equal(.2, moved.Endpoints!.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(1, moved.Endpoints.PlatformSiteEntries);
        Assert.Equal(0, moved.Endpoints.SiteVisitTime!.Value, 9);
        Assert.Equal(-.1, moved.DeltaFromOriginal!.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(-1, moved.DeltaFromOriginal.PlatformSiteEntries);
        Assert.Equal(-.1, moved.DeltaFromOriginal.FirstVisitSeconds!.Value, 9);
        Assert.Equal(0, moved.OutlierFrame);
        Assert.Equal(.56, moved.MeasurementSettings!.Platform!.X, 9);
        Assert.Equal(.2, smaller.Endpoints!.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(1, smaller.Endpoints.PlatformSiteEntries);
        Assert.Equal(.1, smaller.Endpoints.SiteVisitTime!.Value, 9);
        Assert.Equal(4, smaller.OutlierFrame);
        Assert.Equal(.02, smaller.MeasurementSettings!.Platform!.Radius, 9);
        Assert.Equal(moved.Endpoints.PlatformSiteSeconds, same.Endpoints!.PlatformSiteSeconds);
        Assert.Equal(moved.Endpoints.PlatformSiteEntries, same.Endpoints.PlatformSiteEntries);
        Assert.Equal(.5, set.BaselineSettings.Platform!.X, 9);
        Assert.Equal(111, points[0].X);
        var site = set.Envelopes.Single(e => e.Endpoint == "platform_site_seconds");
        Assert.True(site.Numeric); Assert.Equal(.2, site.Minimum!.Value, 9); Assert.Equal(.3, site.Maximum!.Value, 9);
        Assert.Contains("original", site.Contributing);
        Assert.Equal(RegionScenarioWording.EnvelopeLabel, site.Label);
        Assert.Contains("not a confidence interval", site.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not sampling uncertainty", site.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not the missing-position bound", site.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnequalPoolAxesChangeOccupancyWithoutClaimingRetracking()
    {
        var points = new List<TrackPoint> { At(0, 100, 86), At(1, 100, 80) };
        var wider = Geometry("ry-plus-40", "Forty source pixels of vertical pool-axis uncertainty, documented before calculation.", RegionScenarioKind.PoolMeasurementEllipse, DisclosedUnit.SourcePixels, 0, 0, 0, 40, -40, 40);
        var set = RegionScenarioMath.Evaluate(points, Meta with { FrameCount = 2 }, Baseline(), [wider], Identity());
        Assert.Equal(.1, set.Original.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(1, set.Original.PlatformSiteEntries);
        Assert.Equal(.075, set.Original.Distance!.Value, 9);
        var outcome = Assert.Single(set.Scenarios);
        Assert.Equal(RegionScenarioWording.FrozenCoordinateMode, outcome.Mode);
        Assert.Equal(.2, outcome.Endpoints!.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(0, outcome.Endpoints.PlatformSiteEntries);
        Assert.Equal(0, outcome.Endpoints.SiteVisitTime!.Value, 9);
        Assert.Equal(.0375, outcome.Endpoints.Distance!.Value, 9);
        Assert.Equal("pool diameters", outcome.Endpoints.DistanceUnit);
        Assert.Equal(.5, outcome.MeasurementSettings!.Ry, 9);
        Assert.Equal(.25, set.BaselineSettings.Ry, 9);
        Assert.Equal(0, outcome.OutlierFrame);
    }

    [Fact]
    public void UnknownGapsKeepMissingPositionBoundsAndWithholdFirstVisit()
    {
        var points = GapPath();
        var shift = Geometry("gap-center-plus-12", "Twelve source pixels of platform-center uncertainty on a path with an unknown frame, documented before calculation.", RegionScenarioKind.PlatformCenterExtent, DisclosedUnit.SourcePixels, 12, 0, 0, 0, -12, 12);
        var set = RegionScenarioMath.Evaluate(points, Meta, Baseline(), [shift], Identity());
        Assert.Equal(.2, set.Original.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(1, set.Original.PlatformSiteEntries);
        Assert.Equal(EventHistory.EarlierVisitPossibleInGap, set.Original.SiteVisitStatus);
        Assert.Null(set.Original.SiteVisitTime);
        Assert.True(set.Original.FirstVisitWithheld);
        Assert.Equal(0, set.Original.FirstVisitLowerSeconds!.Value, 9);
        Assert.Equal(.2, set.Original.FirstVisitUpperSeconds!.Value, 9);
        Assert.Equal(80, set.Original.TargetMissingPositionBounds!.Lower, 9);
        Assert.Equal(100, set.Original.TargetMissingPositionBounds.Upper, 9);
        Assert.Equal(RegionScenarioWording.MissingPositionLabel, set.Original.MissingPositionLabel);
        var moved = Assert.Single(set.Scenarios);
        Assert.Equal(.1, moved.Endpoints!.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(0, moved.Endpoints.PlatformSiteEntries);
        Assert.Null(moved.Endpoints.SiteVisitTime);
        Assert.True(moved.Endpoints.FirstVisitWithheld);
        Assert.Null(moved.DeltaFromOriginal!.FirstVisitSeconds);
        Assert.Contains("held", moved.DeltaFromOriginal.FirstVisitNote, StringComparison.Ordinal);
        var envelope = set.Envelopes.Single(e => e.Endpoint == "platform_site_seconds");
        Assert.Equal(.1, envelope.Minimum!.Value, 9); Assert.Equal(.2, envelope.Maximum!.Value, 9);
        Assert.NotEqual(set.Original.TargetMissingPositionBounds.Lower, envelope.Minimum);
        Assert.NotEqual(set.Original.TargetMissingPositionBounds.Upper, envelope.Maximum);
        var visit = set.Envelopes.Single(e => e.Endpoint == "first_site_visit_seconds");
        Assert.False(visit.Numeric);
        Assert.Contains("original", visit.HeldOutside);
        Assert.Contains("gap-center-plus-12", visit.HeldOutside);
    }

    [Fact]
    public void DetectionMaskChangesStayListedAndAreNotApplied()
    {
        var points = CleanPath(); var copy = points.Select(p => p.X).ToArray();
        var unrun = Geometry("mask-unrun", "A pool change that would affect detection is retained and was not run.", RegionScenarioKind.PoolDetectionMaskUnrun, DisclosedUnit.SourcePixels, 0, 0, 0, 8, -12, 12);
        var collapsed = Geometry("collapsed-radius", "A documented radius reduction that leaves the permitted measurement domain.", RegionScenarioKind.PlatformCenterExtent, DisclosedUnit.SourcePixels, 0, 0, -10, 0, -12, 12);
        var set = RegionScenarioMath.Evaluate(points, Meta, Baseline(), [unrun, collapsed], Identity());
        Assert.Equal(2, set.Scenarios.Length);
        Assert.All(set.Scenarios, s => Assert.True(s.Held));
        Assert.Equal(RegionScenarioWording.DetectionUnrunMode, set.Scenarios[0].Mode);
        Assert.Contains("not changed", set.Scenarios[0].Holds.Single(), StringComparison.Ordinal);
        Assert.Contains("permitted measurement domain", set.Scenarios[1].Holds.Single(), StringComparison.Ordinal);
        Assert.All(set.Scenarios, s => Assert.Equal(0, s.OutlierFrame));
        Assert.Equal(.3, set.Original.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(.05, set.BaselineSettings.Platform!.Radius, 9);
        Assert.Equal(copy, points.Select(p => p.X).ToArray());
        var envelope = set.Envelopes.Single(e => e.Endpoint == "platform_site_seconds");
        Assert.Equal(.3, envelope.Minimum!.Value, 9); Assert.Equal(.3, envelope.Maximum!.Value, 9);
        Assert.Contains("mask-unrun", envelope.HeldOutside);
        Assert.Contains("collapsed-radius", envelope.HeldOutside);
        Assert.Contains(RegionScenarioWording.ParameterPolicy, set.ParameterPolicy);
    }

    [Fact]
    public void UndocumentedRangesAreRejectedBeforeCalculation()
    {
        var outside = Geometry("outside", "This perturbation was not inside its documented range.", RegionScenarioKind.PlatformCenterExtent, DisclosedUnit.SourcePixels, 12, 0, 0, 0, -5, 5);
        var centimeters = Geometry("centimeters", "Platform perturbations are not centimeters.", RegionScenarioKind.PlatformCenterExtent, DisclosedUnit.Centimeters, 0, 0, 0, 0, 0, 1);
        Assert.Throws<ArgumentException>(() => outside.Validate());
        Assert.Throws<ArgumentException>(() => centimeters.Validate());
        Assert.Throws<ArgumentException>(() => RegionScenarioMath.Evaluate(CleanPath(), Meta, Baseline(), [outside], Identity()));
        Assert.Throws<ArgumentException>(() => RegionScenarioMath.Evaluate(CleanPath(), Meta, Baseline(), [], Identity()));
    }

    [Fact]
    public void EqualAnimalWeightsKeepExcludedMissingAndUnconfirmedRows()
    {
        var trials = new[]
        {
            Eligible("t1", "A", "WT", 1), Eligible("t3", "A", "WT", 3), Eligible("t6", "B", "WT", 6),
            Eligible("excluded", "C", "KO", 100) with { AnimalExcluded = true, AnimalExclusionReason = "Synthetic exclusion" },
            Eligible("missing", "D", "KO", 9) with { RecordingPresent = false, MissingReason = "Not acquired", Endpoint = null },
            Eligible("unconfirmed", "E", "WT", 4) with { AnimalIdentityConfirmed = false }
        };
        var summary = RegionScenarioMath.Collect(trials);
        Assert.True(summary.Emitted);
        Assert.Equal(1, summary.Animals.Single(a => a.AnimalId == "A").Weight);
        Assert.Equal(2, summary.Animals.Single(a => a.AnimalId == "A").AnimalMean!.Value, 9);
        Assert.Equal(6, summary.Animals.Single(a => a.AnimalId == "B").AnimalMean!.Value, 9);
        Assert.Equal(4, summary.Groups.Single(g => g.Group == "WT").Mean!.Value, 9);
        Assert.Equal(2, summary.Groups.Single(g => g.Group == "WT").EligibleAnimals);
        Assert.Null(summary.Groups.Single(g => g.Group == "KO").Mean);
        Assert.NotEqual(10d / 3, summary.Groups.Single(g => g.Group == "WT").Mean!.Value);
        Assert.Equal(0, summary.Animals.Single(a => a.AnimalId == "C").Weight);
        Assert.Equal("excluded", summary.Animals.Single(a => a.AnimalId == "C").Status);
        Assert.Equal("missing", summary.Animals.Single(a => a.AnimalId == "D").Status);
        Assert.Equal("held", summary.Animals.Single(a => a.AnimalId == "E").Status);
        Assert.Contains("Equal animal weights", summary.Weighting, StringComparison.Ordinal);
        var mixed = RegionScenarioMath.Collect([Eligible("room", "A", "WT", 1), Eligible("target", "B", "KO", 2, StudyOrientation.TargetAligned)]);
        Assert.True(mixed.Emitted);
        Assert.Contains("not comparable", mixed.Hold, StringComparison.Ordinal);
        Assert.All(mixed.Animals, a => Assert.Equal(0, a.Weight));
        Assert.Equal(2, mixed.Animals.Length);
        var units = RegionScenarioMath.Collect([Eligible("seconds", "A", "WT", 1), Eligible("centimeters", "B", "KO", 2, unit: "cm")]);
        Assert.Contains("units", units.Hold, StringComparison.OrdinalIgnoreCase);
        Assert.All(units.Animals, a => Assert.Equal(0, a.Weight));
        Assert.False(RegionScenarioMath.Collect([]).Emitted);
    }

    [Fact]
    public void MeasuredHomographyAndAdverseCalibrationScenariosStayInOneSet()
    {
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var profile = SyntheticProfile(sha, 1);
        var points = new List<TrackPoint> { At(0, 40, 60), At(1, 100, 80) };
        var settings = Baseline() with { DiameterCm = 180 };
        var plane = new Plane(50, 40, 50, 20, 50, 40, 5);
        var scenarios = new[]
        {
            Calibration("measured-zero", "Zero control shift. The known scale homography is the expected transform.", [], -1, 1, 0, 0, plane),
            Calibration("preserved-shift", "Control N moves along the same homography, so transformed coordinates stay known.", [new("N", 10, 0, 5, 0)], -20, 20, -5, 5, plane),
            Calibration("bad-independent-check", "A twenty pixel control shift that fails the prespecified independent check.", [new("N", 20, 0, 0, 0)], -20, 20, 0, 0, plane),
            Calibration("duplicate-control", "Shifting NE onto NW makes the measured fit fail and keeps the scenario.", [new("NE", -160, 0, 0, 0)], -160, 20, 0, 0, plane),
            Calibration("unknown-landmark", "A shift names a landmark that was not measured.", [new("absent", 0, 0, 0, 0)], -1, 1, 0, 0, plane)
        };
        var set = RegionScenarioMath.Evaluate(points, Meta with { FrameCount = 2 }, settings, scenarios,
            Identity() with { SourceSha256 = sha, ProfileId = profile.Id, ProfileRevision = 1 },
            scenario => CalibrationScenarioMath.Evaluate(profile, scenario, points, Meta with { FrameCount = 2 }, sha));
        Assert.Equal(5, set.Scenarios.Length);
        var zero = set.Scenarios.Single(s => s.Name == "measured-zero");
        var preserved = set.Scenarios.Single(s => s.Name == "preserved-shift");
        Assert.False(zero.Held); Assert.False(preserved.Held);
        Assert.Equal(RegionScenarioWording.CalibrationMode, zero.Mode);
        Assert.Equal(20, zero.Coordinates[0].RoomXcm!.Value, 6);
        Assert.Equal(30, zero.Coordinates[0].RoomYcm!.Value, 6);
        Assert.Equal(50, zero.Coordinates[1].RoomXcm!.Value, 6);
        Assert.Equal(40, zero.Coordinates[1].RoomYcm!.Value, 6);
        Assert.Equal(10, zero.Coordinates[0].TargetXcm!.Value, 6);
        Assert.Equal(-30, zero.Coordinates[0].TargetYcm!.Value, 6);
        Assert.Equal(0, zero.Coordinates[1].TargetXcm!.Value, 6);
        Assert.Equal(Math.Sqrt(1000), zero.Endpoints!.Distance!.Value, 6);
        Assert.Equal("cm", zero.Endpoints.DistanceUnit);
        Assert.NotEqual(set.Original.Distance, zero.Endpoints.Distance);
        Assert.Equal("detector-diameter units", set.Original.DistanceUnit);
        Assert.Equal(20, preserved.Coordinates[0].RoomXcm!.Value, 9);
        Assert.Equal(30, preserved.Coordinates[0].RoomYcm!.Value, 9);
        Assert.Equal(10, preserved.Coordinates[0].TargetXcm!.Value, 9);
        Assert.Equal(-30, preserved.Coordinates[0].TargetYcm!.Value, 9);
        Assert.Equal(zero.Coordinates[0].RoomXcm!.Value, preserved.Coordinates[0].RoomXcm!.Value, 9);
        Assert.Equal(zero.Coordinates[0].TargetXcm!.Value, preserved.Coordinates[0].TargetXcm!.Value, 9);
        Assert.Contains("No transformed coordinate moved", preserved.OutlierReason, StringComparison.Ordinal);
        foreach (var name in new[] { "bad-independent-check", "duplicate-control", "unknown-landmark" })
        {
            var held = set.Scenarios.Single(s => s.Name == name);
            Assert.True(held.Held); Assert.Null(held.Endpoints); Assert.Equal(0, held.OutlierFrame);
        }
        Assert.Contains(set.Scenarios.Single(s => s.Name == "bad-independent-check").Holds, h => h.Contains("Independent check", StringComparison.Ordinal));
        Assert.Contains(set.Scenarios.Single(s => s.Name == "duplicate-control").Holds, h => h.Contains("fit failed", StringComparison.OrdinalIgnoreCase));
        var distance = set.Envelopes.Single(e => e.Endpoint == "distance");
        Assert.False(distance.Numeric);
        Assert.Contains("Units differ", distance.Label, StringComparison.Ordinal);
        Assert.Contains("bad-independent-check", distance.HeldOutside);

        var missing = RegionScenarioMath.Evaluate(points, Meta with { FrameCount = 2 }, settings, [scenarios[0]], Identity() with { SourceSha256 = sha }, _ => CalibrationScenarioMath.Evaluate(null, scenarios[0], points, Meta, sha));
        Assert.Contains("No centimeter transform was fabricated", Assert.Single(missing.Scenarios).Holds.Single(), StringComparison.Ordinal);
        var mismatched = CalibrationScenarioMath.Evaluate(profile, scenarios[0], points, Meta with { FrameCount = 2 }, new string('b', 64));
        Assert.True(mismatched.Held); Assert.Contains("does not match", mismatched.Holds.Single(), StringComparison.Ordinal);
        var wrongSize = CalibrationScenarioMath.Evaluate(profile, scenarios[0], points, Meta with { Width = 100, Height = 100, FrameCount = 2 }, sha);
        Assert.True(wrongSize.Held); Assert.Contains("resolution", wrongSize.Holds.Single(), StringComparison.OrdinalIgnoreCase);
        var targetOnly = scenarios[0] with { Name = "target-view", Orientation = StudyOrientation.TargetAligned };
        var target = CalibrationScenarioMath.Evaluate(profile, targetOnly, points, Meta with { FrameCount = 2 }, sha);
        Assert.False(target.Held);
        Assert.Equal(10, target.Coordinates[0].TargetXcm!.Value, 6);
        Assert.Equal(20, target.Coordinates[0].RoomXcm!.Value, 6);
        Assert.Equal(Math.Sqrt(1000), target.Endpoints!.Distance!.Value, 6);
        Assert.NotEqual(target.Coordinates[0].RoomXcm, target.Coordinates[0].TargetXcm);
        var incomplete = targetOnly with { PoolCenterXcm = null, PoolCenterYcm = null, TargetXcm = null, TargetYcm = null };
        Assert.Throws<ArgumentException>(() => incomplete.Validate());
    }

    [Fact]
    public void EvidenceAppendsWithoutReplacingSourceOrPriorRevisions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-region-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "source.bin");
            File.WriteAllBytes(source, [1, 2, 3, 4]);
            var before = File.ReadAllBytes(source);
            var first = Document("{\"scenario\":\"one\"}");
            var path = RegionScenarioEvidenceStore.Append(folder, first);
            var frozen = File.ReadAllBytes(path);
            var second = RegionScenarioEvidenceStore.Append(folder, Document("{\"scenario\":\"two\"}"));
            Assert.NotEqual(path, second);
            Assert.Equal(before, File.ReadAllBytes(source));
            Assert.Equal(frozen, File.ReadAllBytes(path));
            File.WriteAllBytes(second, Encoding.UTF8.GetBytes("replacement"));
            Assert.Equal(frozen, File.ReadAllBytes(path));
            Assert.Throws<InvalidDataException>(() => RegionScenarioEvidenceStore.Append(folder, first with { ComputationSha256 = new string('c', 64) }));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(folder, RegionScenarioEvidenceStore.FolderName), "region-scenario-*.json").Length);
        }
        finally { DeleteScratch(folder, "tabajara-region-evidence-"); }
    }

    [Fact]
    public void FrozenIdentityNamesSourceRunSettingsPlanAndProfileChanges()
    {
        var stamp = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var source = new FrozenSourceIdentity
        {
            VideoId = "video", Sha256 = new string('a', 64), Length = 4, WrittenUtc = stamp, CreatedUtc = stamp,
            RunId = "run", RunMaterialSha256 = "material", SettingsHash = "settings", CorrectionFingerprint = "corrections",
            ProtocolRevision = 1, ProtocolFingerprint = "protocol"
        };
        var identity = new FrozenRegionIdentity
        {
            SnapshotRevision = "1", PlanRevision = 2, PlanFingerprint = "plan", ProfileId = "profile", ProfileRevision = 1, Sources = [source]
        };
        Assert.Null(identity.Difference(identity));
        Assert.Contains("source bytes", identity.Difference(identity with { Sources = [source with { Sha256 = new string('b', 64) }] })!, StringComparison.Ordinal);
        Assert.Contains("settings", identity.Difference(identity with { Sources = [source with { SettingsHash = "edited" }] })!, StringComparison.Ordinal);
        Assert.Contains("original run", identity.Difference(identity with { Sources = [source with { RunId = "other", SettingsHash = "edited" }] })!, StringComparison.Ordinal);
        Assert.Contains("plan", identity.Difference(identity with { PlanRevision = 3 })!, StringComparison.Ordinal);
        Assert.Contains("calibration profile", identity.Difference(identity with { ProfileRevision = 2 })!, StringComparison.Ordinal);
        Assert.Contains("recording roster", identity.Difference(identity with { Sources = [source, source with { VideoId = "added" }] })!, StringComparison.Ordinal);
        Assert.Contains("protocol window", identity.Difference(identity with { Sources = [source with { ProtocolRevision = 2 }] })!, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWindowDiffersFromFullDecoderAndKeepsFractionalUnknownMass()
    {
        var points = CleanPath();
        var aligned = new ReleasedComparisonWindow { Supported = true, ReleaseFrame = 1, OriginSeconds = .1, PlannedSeconds = .3, RequestedExposureSeconds = .5 };
        var shifted = RegionScenarioComparison.Measure(points, Meta, Baseline(), aligned, "aligned-window");
        Assert.True(shifted.Supported); Assert.False(shifted.FractionalBoundary);
        Assert.Equal(.2, shifted.Comparison!.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(0, shifted.Comparison.PlatformSiteEntries);
        Assert.Equal(0, shifted.Comparison.SiteVisitTime!.Value, 9);
        Assert.Equal(EventHistory.ObservedExact, shifted.Comparison.SiteVisitStatus);
        Assert.Equal(11d / 160, shifted.Comparison.Distance!.Value, 9);
        Assert.Equal(.3, shifted.FullRun!.PlatformSiteSeconds!.Value, 9);
        Assert.NotEqual(shifted.FullRun.PlatformSiteSeconds, shifted.Comparison.PlatformSiteSeconds);
        Assert.Contains(RegionScenarioWording.ComparisonScope, shifted.Scope, StringComparison.Ordinal);
        Assert.Contains("not the full decoder interval", shifted.Scope, StringComparison.Ordinal);

        var fractionalPoints = new List<TrackPoint> { At(0, 111, 80), At(1, 100, 80), At(2, null, null), At(3, 111, 80), At(4, 100, 85), At(5, 111, 80), At(6, 100, 80) };
        var fractional = new ReleasedComparisonWindow { Supported = true, ReleaseFrame = 2, OriginSeconds = .25, PlannedSeconds = .4, RequestedExposureSeconds = .8 };
        var cut = RegionScenarioComparison.Measure(fractionalPoints, Meta with { FrameCount = 7 }, Baseline(), fractional, "fractional-window");
        Assert.True(cut.FractionalBoundary);
        Assert.Equal(.15, cut.Comparison!.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(2, cut.Comparison.PlatformSiteEntries);
        Assert.Equal(.05, cut.UnknownSeconds, 9);
        Assert.Equal(.35, cut.ObservedSeconds, 9);
        Assert.Equal(.4, cut.PlannedSeconds, 9);
        Assert.Null(cut.Comparison.Distance);
        Assert.Contains("No fractional path was invented", cut.DistanceNote, StringComparison.Ordinal);
        Assert.True(cut.Comparison.FirstVisitWithheld);
        Assert.Equal(EventHistory.EarlierVisitPossibleInGap, cut.Comparison.SiteVisitStatus);
        Assert.Null(cut.Comparison.SiteVisitTime);
        Assert.Equal(0, cut.Comparison.FirstVisitLowerSeconds!.Value, 9);
        Assert.Equal(.15, cut.Comparison.FirstVisitUpperSeconds!.Value, 9);
        Assert.Equal(87.5, cut.Comparison.TargetMissingPositionBounds!.Lower, 9);
        Assert.Equal(100, cut.Comparison.TargetMissingPositionBounds.Upper, 9);
        Assert.NotEqual(cut.FullRun!.PlatformSiteSeconds, cut.Comparison.PlatformSiteSeconds);

        var context = new PlannedContext { Id = "probe", Phase = TrialMode.Probe, RequestedExposureSeconds = .8, ComparisonStartSeconds = .05, ComparisonDurationSeconds = .4, WindowConfirmed = true, Orientation = StudyOrientation.Room, AlignmentConfirmed = true, CommonCoordinateReference = "Synthetic room" };
        var protocol = new ProtocolWindow { Phase = TrialMode.Probe, RequestedExposureSeconds = .8, ReleaseConfirmed = true, ReleaseFrame = 2, WindowConfirmed = true };
        var resolved = ReleasedComparisonWindow.Resolve(protocol, context, Meta with { FrameCount = 12 }, [Event(BiologicalEventKind.WaterEntry, 2)], TrialMode.Probe, true);
        Assert.True(resolved.Supported); Assert.Equal(.25, resolved.OriginSeconds, 9); Assert.Equal(.4, resolved.PlannedSeconds, 9);
        Assert.Equal(.8, resolved.EffectiveExposureSeconds, 9); Assert.Equal("Fixed probe exposure", resolved.TerminationReason);
        Assert.DoesNotContain(RegionScenarioWording.EffectiveExposureLimit, resolved.ExposureNote, StringComparison.Ordinal);
        var missing = RegionScenarioComparison.Measure(points, Meta, Baseline(), ReleasedComparisonWindow.Unsupported("Release is unconfirmed."), "held-window");
        Assert.False(missing.Supported); Assert.Null(missing.Comparison); Assert.NotNull(missing.FullRun);
        Assert.Contains(RegionScenarioWording.ComparisonSubstitutionHold, missing.Hold, StringComparison.Ordinal);
        Assert.NotEqual(missing.FullRun!.PlatformSiteSeconds, missing.Comparison?.PlatformSiteSeconds);
    }

    private static ScenarioTrialContract Eligible(string trial, string animal, string group, double endpoint, StudyOrientation orientation = StudyOrientation.Room, string unit = "s") => new()
    {
        TrialId = trial, AnimalId = animal, Group = group, AnimalIdentityConfirmed = true, AllocationConfirmed = true,
        RecordingPresent = true, RecordingIncluded = true, SlotMatchesRecording = true, SourceReferenceConfirmed = true,
        SourceHashMatches = true, SettingsHashMatches = true, WindowConfirmed = true, ReleaseConfirmed = true, ProtocolMatchesContext = true,
        CurrentInspection = true, EffectiveOutcomeClear = true,
        OrientationConfirmed = true, Orientation = orientation, Unit = unit, Endpoint = endpoint
    };

    private readonly record struct Plane(double PoolX, double PoolY, double TargetX, double TargetY, double PlatformX, double PlatformY, double Radius);
    private static NamedRegionScenario Calibration(string name, string rationale, ControlCoordinateShift[] shifts, double pixelMin, double pixelMax, double roomMin, double roomMax, Plane plane) => new()
    {
        Name = name, Rationale = rationale, Kind = RegionScenarioKind.CalibrationControlUncertainty, Unit = DisclosedUnit.SourcePixels,
        ComponentRange = new(DisclosedUnit.SourcePixels, pixelMin, pixelMax), RoomRange = new(DisclosedUnit.Centimeters, roomMin, roomMax),
        ControlShifts = shifts.ToImmutableArray(), Orientation = StudyOrientation.Room,
        PoolCenterXcm = plane.PoolX, PoolCenterYcm = plane.PoolY, TargetXcm = plane.TargetX, TargetYcm = plane.TargetY,
        PlatformXcm = plane.PlatformX, PlatformYcm = plane.PlatformY, PlatformRadiusCm = plane.Radius
    };
    private static readonly Matrix3 KnownScale = new(.5, 0, 0, 0, .5, 0, 0, 0, 1);
    private static MeasuredLandmark Mark(string id, double x, double y) => new(id, new(x, y), KnownScale.Map(new(x, y)));
    private static CalibrationProfile SyntheticProfile(string sha, int revision) => WaterPlaneCalibration.Fit(new("synthetic-camera", 200, 160, sha, 0, revision,
        [Mark("NW", 20, 20), Mark("NE", 180, 20), Mark("SW", 20, 140), Mark("SE", 180, 140), Mark("N", 100, 30), Mark("W", 30, 80)],
        [Mark("C", 100, 80), Mark("E", 150, 80)], .01, .01, true, "Reviewer"));
    [Fact]
    public void TrainingMountAtThreeSecondsExcludesTwentySecondDecoderTail()
    {
        const double fps = 10;
        var metadata = Meta with { FrameCount = 200 };
        var settings = Baseline();
        var points = Enumerable.Range(0, 200).Select(frame => frame < 30 ? At(frame, 20, 20) : At(frame, 100, 80)).ToList();
        Assert.False(ArenaGeometry.InsidePlatformSite(20, 20, settings, metadata.Width, metadata.Height));
        Assert.True(ArenaGeometry.InsidePlatformSite(100, 80, settings, metadata.Width, metadata.Height));
        var context = TrainingContext();
        var protocol = TrainingProtocol(10);
        var window = ReleasedComparisonWindow.Resolve(protocol, context, metadata, [Event(BiologicalEventKind.WaterEntry, 10), Event(BiologicalEventKind.PlatformMounting, 30)], TrialMode.Training, true);
        Assert.True(window.Supported);
        Assert.Equal(1, window.OriginSeconds, 9);
        Assert.Equal(2, window.PlannedSeconds, 9);
        Assert.Equal(10, window.RequestedExposureSeconds, 9);
        Assert.Equal(2, window.EffectiveExposureSeconds, 9);
        Assert.Equal("Confirmed platform mounting", window.TerminationReason);
        Assert.Contains(RegionScenarioWording.EffectiveExposureLimit, window.ExposureNote, StringComparison.Ordinal);
        var measured = RegionScenarioComparison.Measure(points, metadata, settings, window, "training-mount");
        var slice = points.Where(point => point.Frame >= 10 && point.Frame <= 29).ToList();
        var sliceMetrics = MetricCalculator.Calculate(slice, metadata, settings);
        var full = MetricCalculator.Calculate(points, metadata, settings);
        double tailInsideWindow = 0;
        foreach (var point in points.Where(point => point.Frame >= 30))
        {
            var overlap = Math.Min(3d, (point.Frame + 1d) / fps) - Math.Max(1d, point.Frame / fps);
            if (overlap > 0) tailInsideWindow += overlap;
        }
        Assert.Equal(0, tailInsideWindow, 9);
        Assert.Equal(170, points.Count(point => point.Frame >= 30));
        Assert.Equal(17, full.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(sliceMetrics.PlatformSiteSeconds, measured.Comparison!.PlatformSiteSeconds);
        Assert.Equal(0, measured.Comparison.PlatformSiteSeconds!.Value, 9);
        Assert.Equal(full.PlatformSiteSeconds, measured.FullRun!.PlatformSiteSeconds);
        Assert.NotEqual(measured.FullRun.PlatformSiteSeconds, measured.Comparison.PlatformSiteSeconds);
        Assert.Equal(2, measured.PlannedSeconds, 9);
        Assert.Equal(10, measured.RequestedExposureSeconds, 9);
        Assert.Contains(RegionScenarioWording.EffectiveExposureLimit, measured.Scope, StringComparison.Ordinal);
        Assert.False(measured.FractionalBoundary);

        var uninspected = ReleasedComparisonWindow.Resolve(protocol, context, metadata, [Event(BiologicalEventKind.WaterEntry, 10), Event(BiologicalEventKind.PlatformMounting, 30)], TrialMode.Training, false);
        Assert.False(uninspected.Supported);
        Assert.Contains("inspection", uninspected.Hold, StringComparison.OrdinalIgnoreCase);
        var heldInspection = RegionScenarioComparison.Measure(points, metadata, settings, uninspected, "inspection-held");
        Assert.Null(heldInspection.Comparison);
        Assert.Equal(full.PlatformSiteSeconds, heldInspection.FullRun!.PlatformSiteSeconds);
    }

    [Fact]
    public void MissingReleaseAgreementAndInWindowInterventionStayHeld()
    {
        var metadata = Meta with { FrameCount = 200 };
        var settings = Baseline();
        var points = Enumerable.Range(0, 200).Select(frame => frame < 30 ? At(frame, 20, 20) : At(frame, 100, 80)).ToList();
        var context = TrainingContext();
        var mismatched = ReleasedComparisonWindow.Resolve(TrainingProtocol(10), context, metadata, [Event(BiologicalEventKind.WaterEntry, 0)], TrialMode.Training, true);
        Assert.False(mismatched.Supported);
        Assert.Contains("does not match a water-entry", mismatched.Hold, StringComparison.Ordinal);
        var mismatchedMeasurement = RegionScenarioComparison.Measure(points, metadata, settings, mismatched, "release-disagrees");
        Assert.Null(mismatchedMeasurement.Comparison);
        Assert.Equal(17, mismatchedMeasurement.FullRun!.PlatformSiteSeconds!.Value, 9);
        Assert.NotEqual(mismatchedMeasurement.FullRun.PlatformSiteSeconds, mismatchedMeasurement.Comparison?.PlatformSiteSeconds);

        var intervention = ReleasedComparisonWindow.Resolve(TrainingProtocol(10), context, metadata,
            [Event(BiologicalEventKind.WaterEntry, 10), Event(BiologicalEventKind.ResearcherIntervention, 20), Event(BiologicalEventKind.PlatformMounting, 30)], TrialMode.Training, true);
        Assert.False(intervention.Supported);
        Assert.Contains("Researcher intervention", intervention.Hold, StringComparison.Ordinal);
        Assert.Contains(RegionScenarioWording.ComparisonSubstitutionHold, intervention.Hold, StringComparison.Ordinal);
        var held = RegionScenarioComparison.Measure(points, metadata, settings, intervention, "intervention-held");
        Assert.Null(held.Comparison);
        Assert.Equal(17, held.FullRun!.PlatformSiteSeconds!.Value, 9);
        Assert.True(held.EffectiveExposureSeconds > 0);
        Assert.NotEqual(held.FullRun.PlatformSiteSeconds, held.EffectiveExposureSeconds);

        var flagsOnly = RegionScenarioMath.Collect([Eligible("flags", "A", "WT", 17) with { CurrentInspection = false }]);
        Assert.Equal(0, flagsOnly.Animals.Single().Weight);
        Assert.Contains("inspection", flagsOnly.Animals.Single().Detail, StringComparison.OrdinalIgnoreCase);
        var outcomeHeld = RegionScenarioMath.Collect([Eligible("outcome", "A", "WT", 17) with { EffectiveOutcomeClear = false, EffectiveOutcomeHold = mismatched.Hold }]);
        Assert.Equal(0, outcomeHeld.Animals.Single().Weight);
        Assert.Contains("water-entry", outcomeHeld.Animals.Single().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SameContractChangedImplementationIdentityStaysHeld()
    {
        var actual = ScenarioImplementationProbe.Capture();
        Assert.Equal(RegionScenarioWording.CodeContract, actual.CodeContract);
        var coreSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RegionScenarioMath).Assembly.Location))).ToLowerInvariant();
        var applicationSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ScenarioImplementationProbe).Assembly.Location))).ToLowerInvariant();
        Assert.Equal(coreSha, actual.CoreAssemblySha256);
        Assert.Equal(applicationSha, actual.ApplicationAssemblySha256);
        Assert.Equal("MouseTrackerTabajara", actual.DesktopAssemblyName);
        var desktopSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "MouseTrackerTabajara.dll")))).ToLowerInvariant();
        Assert.Equal(desktopSha, actual.DesktopAssemblySha256);
        var names = typeof(ScenarioImplementationProbe).Assembly.GetManifestResourceNames();
        var preimage = ScenarioImplementationProbe.SourceMaterialBytes();
        var root = ScenarioImplementationProbe.RepositoryRoot();
        foreach (var relative in ScenarioImplementationProbe.SourceFiles)
        {
            Assert.Contains(ScenarioImplementationProbe.SourceResourcePrefix + relative, names);
            var file = File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(preimage.AsSpan().IndexOf(file) >= 0, relative);
        }
        Assert.Equal(Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant(), actual.SourceMaterialSha256);
        Assert.Equal(ScenarioImplementationProbe.Canonical(actual), actual.CanonicalSha256);
        var contractOnly = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual.CodeContract))).ToLowerInvariant();
        Assert.NotEqual(contractOnly, actual.CanonicalSha256);
        Assert.NotEqual(contractOnly, actual.CoreAssemblySha256);
        Assert.Null(ScenarioImplementationProbe.ReplayHold(actual, actual));

        var changed = actual with { CoreAssemblySha256 = new string('b', 64) };
        changed = changed with { CanonicalSha256 = ScenarioImplementationProbe.Canonical(changed) };
        Assert.Equal(actual.CodeContract, changed.CodeContract);
        var hold = ScenarioImplementationProbe.ReplayHold(changed, actual);
        Assert.Contains("declared code contract is unchanged", hold, StringComparison.Ordinal);
        Assert.Contains("original implementation", hold, StringComparison.Ordinal);
        var frozen = new FrozenRegionIdentity { CodeContract = actual.CodeContract, Implementation = actual };
        Assert.Null(frozen.Difference(frozen));
        var drift = frozen.Difference(frozen with { Implementation = changed });
        Assert.Contains("implementation identity", drift, StringComparison.Ordinal);
        Assert.DoesNotContain("code contract", drift, StringComparison.Ordinal);

        var folder = Path.Combine(Path.GetTempPath(), "tabajara-region-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var accepted = Document("{\"scenario\":\"current-code\"}");
            RegionScenarioEvidenceStore.Authenticate(accepted);
            var path = RegionScenarioEvidenceStore.Append(folder, accepted);
            var rejected = accepted with { Identity = accepted.Identity with { Implementation = changed } };
            Assert.Equal(accepted.CodeContract, rejected.CodeContract);
            var denied = Assert.Throws<InvalidDataException>(() => RegionScenarioEvidenceStore.Authenticate(rejected));
            Assert.Contains("unchanged", denied.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => RegionScenarioEvidenceStore.Append(folder, rejected));
            Assert.Single(Directory.GetFiles(Path.Combine(folder, RegionScenarioEvidenceStore.FolderName), "region-scenario-*.json"));
            Assert.Contains(actual.CoreAssemblySha256, File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Contains("\"schema_version\": 2", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Contains("synthetic frozen material", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally { DeleteScratch(folder, "tabajara-region-evidence-"); }
    }

    private static PlannedContext TrainingContext() => new()
    {
        Id = "train", Phase = TrialMode.Training, RequestedExposureSeconds = 10, ComparisonStartSeconds = 0, ComparisonDurationSeconds = 10,
        WindowConfirmed = true, Orientation = StudyOrientation.Room, AlignmentConfirmed = true, CommonCoordinateReference = "Synthetic room"
    };
    private static ProtocolWindow TrainingProtocol(int releaseFrame) => new()
    {
        Phase = TrialMode.Training, RequestedExposureSeconds = 10, ReleaseConfirmed = true, ReleaseFrame = releaseFrame, WindowConfirmed = true
    };
    private static BiologicalEvent Event(BiologicalEventKind kind, int frame) => new()
    {
        Id = kind.ToString(), VideoId = "video", Kind = kind, Frame = frame, TimeSeconds = frame / 10d, Reason = "Synthetic", Reviewer = "Reviewer", Revision = 1
    };
    private static RegionScenarioEvidenceDocument Document(string json)
    {
        var material = "{\"plan\":\"synthetic frozen material\"}";
        var implementation = ScenarioImplementationProbe.Capture();
        return new()
        {
            CodeContract = implementation.CodeContract,
            Identity = new FrozenRegionIdentity { CodeContract = implementation.CodeContract, Implementation = implementation },
            ComputationJson = json,
            ComputationSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
            FrozenMaterialJson = material,
            FrozenMaterialSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()
        };
    }
    internal static void DeleteScratch(string folder, string prefix)
    {
        var full = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
