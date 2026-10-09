using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Desktop;

public sealed record SourceFrameRequest(string VideoId, string ScenarioName, int? Frame);

public sealed record RecordingScenarioComputation
{
    public string VideoId { get; init; } = "";
    public RegionScenarioSet? Set { get; init; }
    public ImmutableArray<WindowedTrialMeasurement> Windows { get; init; } = [];
    public ImmutableArray<string> RecordingHolds { get; init; } = [];
}

public sealed record ScenarioCollectionBlock
{
    public string ContextId { get; init; } = "";
    public string ScenarioName { get; init; } = "";
    public string Endpoint { get; init; } = "platform_site_seconds";
    public CollectionSummary Summary { get; init; } = new();
}

public sealed record RegionScenarioComputation
{
    public FrozenRegionIdentity Identity { get; init; } = new();
    public ImmutableArray<NamedRegionScenario> Scenarios { get; init; } = [];
    public ImmutableArray<RecordingScenarioComputation> Recordings { get; init; } = [];
    public ImmutableArray<ScenarioCollectionBlock> Collections { get; init; } = [];
    public FrozenReplayMaterial Replay { get; init; } = new();
    public string EvidencePath { get; init; } = "";
    public string StaleReason { get; init; } = "";
}

public static class RegionScenarioBaseline
{
    public static FrozenRegionIdentity Capture(AtlasSnapshot snapshot, CalibrationProfile? profile)
    {
        var plan = snapshot.Rows("study_plans").LastOrDefault();
        var sources = ImmutableArray.CreateBuilder<FrozenSourceIdentity>();
        foreach (var row in snapshot.Recordings)
        {
            var asset = row.Asset;
            var info = new FileInfo(asset.Path);
            if (!info.Exists) throw new FileNotFoundException("Original recording is missing.", asset.Path);
            var runRow = snapshot.Rows("runs").LastOrDefault(r => r.Value("video_id") == asset.Id);
            var runJson = runRow?.Value("json");
            var run = runJson is null ? null : JsonDefaults.Deserialize<AnalysisRun>(runJson);
            var corrections = run is null ? [] : snapshot.Rows("corrections").Where(r => r.Value("run_id") == run.Id).Select(r => r.Value("json")!);
            var protocolRow = snapshot.Rows("protocol_windows").LastOrDefault(r => r.Value("video_id") == asset.Id);
            sources.Add(new FrozenSourceIdentity
            {
                VideoId = asset.Id, Path = asset.Path, Sha256 = OpenCvTracker.HashFile(asset.Path), Length = info.Length,
                WrittenUtc = info.LastWriteTimeUtc, CreatedUtc = info.CreationTimeUtc,
                RunId = run?.Id ?? "", RunMaterialSha256 = runJson is null ? "" : Digest(runJson),
                SettingsHash = run is null ? "" : StudyStore.SettingsHash(run.Settings),
                CorrectionFingerprint = MaterialFingerprint.Hash(corrections),
                ProtocolRevision = protocolRow?.Value("revision") is { } revision ? int.Parse(revision, System.Globalization.CultureInfo.InvariantCulture) : 0,
                ProtocolFingerprint = MaterialFingerprint.Hash(protocolRow?.Value("json") is { } json ? [json] : [])
            });
        }
        var implementation = ScenarioImplementationProbe.Capture();
        return new FrozenRegionIdentity
        {
            SnapshotRevision = snapshot.Revision,
            PlanRevision = plan?.Value("revision") is { } planRevision ? long.Parse(planRevision, System.Globalization.CultureInfo.InvariantCulture) : 0,
            PlanFingerprint = plan?.Value("fingerprint") ?? "",
            ProfileId = profile?.Id ?? "", ProfileRevision = profile?.Request.Revision ?? 0,
            CodeContract = implementation.CodeContract, Implementation = implementation,
            Sources = sources.ToImmutable()
        };
    }

    public static ScenarioIdentityHeader Header(FrozenRegionIdentity identity, FrozenSourceIdentity source) => new()
    {
        OriginalRunId = source.RunId, SourceSha256 = source.Sha256, SettingsHash = source.SettingsHash,
        CorrectionFingerprint = source.CorrectionFingerprint, CodeContract = identity.CodeContract,
        ImplementationSha256 = identity.Implementation.CanonicalSha256,
        ProfileId = identity.ProfileId, ProfileRevision = identity.ProfileRevision,
        PlanRevision = identity.PlanRevision, PlanFingerprint = identity.PlanFingerprint,
        ProtocolRevision = source.ProtocolRevision, ProtocolFingerprint = source.ProtocolFingerprint
    };

    public static ImmutableArray<ScenarioCollectionBlock> Collections(AtlasSnapshot snapshot, IReadOnlyList<NamedRegionScenario> scenarios, IReadOnlyList<RecordingScenarioComputation> recordings)
    {
        var plan = ExperimentPlanAdapter.FrozenPlan(snapshot)?.Plan;
        if (plan is null) return [];
        var blocks = ImmutableArray.CreateBuilder<ScenarioCollectionBlock>();
        foreach (var context in plan.Contexts)
        {
            var names = new[] { RegionScenarioWording.OriginalName }.Concat(scenarios.Select(s => s.Name.Trim()));
            foreach (var name in names)
            {
                var trials = ImmutableArray.CreateBuilder<ScenarioTrialContract>();
                foreach (var slot in plan.Trials.Where(t => string.Equals(t.ContextId, context.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    var animal = plan.Animals.Single(a => string.Equals(a.Id, slot.AnimalId, StringComparison.OrdinalIgnoreCase));
                    var row = snapshot.Recordings.SingleOrDefault(r => r.Asset.Id == slot.RecordingId);
                    var recording = recordings.SingleOrDefault(r => r.VideoId == slot.RecordingId);
                    var windowed = recording?.Windows.FirstOrDefault(item => item.Name == name);
                    var comparisonReady = windowed is { Supported: true, Comparison.PlatformSiteSeconds: not null };
                    double? endpoint = comparisonReady ? windowed!.Comparison!.PlatformSiteSeconds : null;
                    var held = !comparisonReady;
                    var windowHold = comparisonReady ? "" : string.IsNullOrWhiteSpace(windowed?.Hold)
                        ? "Comparison-window platform time is unavailable. " + RegionScenarioWording.ComparisonSubstitutionHold
                        : windowed!.Hold;
                    var protocol = row is null ? null : ExperimentPlanAdapter.Protocol(snapshot, row.Asset.Id);
                    var run = row?.RawRun;
                    var inspection = row is not null && ExperimentPlanAdapter.CurrentInspection(row);
                    var outcomeClear = false;
                    var outcomeHold = "";
                    if (row is not null && protocol is not null)
                    {
                        try
                        {
                            var outcome = ProtocolEvaluator.Evaluate(protocol, run?.Metadata ?? row.Asset.Metadata, row.Asset.Phase, ExperimentPlanAdapter.EffectiveEvents(snapshot, row.Asset.Id));
                            outcomeClear = inspection && outcome.Holds.Count == 0 && outcome.ActualExposureSeconds is > 0;
                            outcomeHold = outcome.Holds.Count == 0
                                ? outcome.ActualExposureSeconds is > 0 ? "" : "Effective exposure is withheld."
                                : string.Join(" ", outcome.Holds);
                        }
                        catch (ArgumentException ex) { outcomeHold = ex.Message; }
                    }
                    else if (row is not null) outcomeHold = "Effective protocol outcome is unavailable.";
                    if (!inspection)
                        outcomeHold = string.IsNullOrWhiteSpace(outcomeHold)
                            ? "Current frozen inspection is missing, invalidated, or stale."
                            : "Current frozen inspection is missing, invalidated, or stale. " + outcomeHold;
                    trials.Add(new ScenarioTrialContract
                    {
                        TrialId = slot.Id, AnimalId = animal.Id, Group = animal.Group,
                        AnimalIdentityConfirmed = animal.IdentityConfirmed, AllocationConfirmed = animal.AllocationConfirmed,
                        AnimalExcluded = animal.Excluded, AnimalExclusionReason = animal.ExclusionReason,
                        RecordingPresent = row is not null, MissingReason = slot.MissingReason,
                        RecordingIncluded = row?.Asset.Included ?? false, RecordingExclusionReason = row?.Asset.ExclusionReason ?? "",
                        SlotMatchesRecording = row is not null && row.Asset.Phase == context.Phase && row.Asset.Day == context.Day && row.Asset.Trial == slot.Order,
                        SourceReferenceConfirmed = slot.SourceReferenceConfirmed,
                        SourceHashMatches = row is not null && slot.ReferenceSourceSha256 == row.Asset.SourceSha256,
                        SettingsHashMatches = run is not null && slot.ReferenceSettingsHash == StudyStore.SettingsHash(run.Settings),
                        WindowConfirmed = protocol?.WindowConfirmed == true, ReleaseConfirmed = protocol?.ReleaseConfirmed == true,
                        ProtocolMatchesContext = protocol is not null && protocol.Phase == context.Phase && Math.Abs(protocol.RequestedExposureSeconds - context.RequestedExposureSeconds) <= 1e-9,
                        CurrentInspection = inspection, EffectiveOutcomeClear = outcomeClear, EffectiveOutcomeHold = outcomeClear ? "" : outcomeHold,
                        OrientationConfirmed = context.AlignmentConfirmed, Orientation = context.Orientation,
                        Unit = endpoint is null ? "" : "s", Endpoint = endpoint, MeasurementHeld = held,
                        MeasurementHold = held ? windowHold : ""
                    });
                }
                blocks.Add(new ScenarioCollectionBlock { ContextId = context.Id, ScenarioName = name, Summary = RegionScenarioMath.Collect(trials.ToImmutable()) });
            }
        }
        return blocks.ToImmutable();
    }

    public static FrozenReplayMaterial Material(AtlasSnapshot snapshot, string? profileText)
    {
        var plans = snapshot.Rows("study_plans").ToArray();
        var plan = plans.LastOrDefault();
        var profile = profileText ?? "";
        var recordings = ImmutableArray.CreateBuilder<FrozenReplayRecording>();
        foreach (var row in snapshot.Recordings)
        {
            var id = row.Asset.Id;
            var runs = snapshot.Rows("runs").Where(item => item.Value("video_id") == id).Select(item => item.Value("json") ?? "").ToImmutableArray();
            var measured = runs.LastOrDefault() ?? "";
            var run = string.IsNullOrWhiteSpace(measured) ? null : JsonDefaults.Deserialize<AnalysisRun>(measured);
            recordings.Add(new FrozenReplayRecording
            {
                VideoId = id,
                VideoJson = snapshot.Rows("videos").LastOrDefault(item => item.Value("id") == id)?.Value("json") ?? "",
                MeasuredRunJson = measured,
                RunHistory = runs,
                Corrections = run is null ? [] : snapshot.Rows("corrections").Where(item => item.Value("run_id") == run.Id).Select(item => item.Value("json") ?? "").ToImmutableArray(),
                Protocols = snapshot.Rows("protocol_windows").Where(item => item.Value("video_id") == id).Select(item => item.Value("json") ?? "").ToImmutableArray(),
                Events = snapshot.Rows("biological_events").Where(item => item.Value("video_id") == id).Select(item => item.Value("json") ?? "").ToImmutableArray(),
                Reviews = run is null ? [] : snapshot.Rows("reviews").Where(item => item.Value("run_id") == run.Id).Select(ReviewText).ToImmutableArray()
            });
        }
        return new FrozenReplayMaterial
        {
            PlanJson = plan?.Value("json") ?? "",
            PlanRevision = plan?.Value("revision") is { } revision ? long.Parse(revision, System.Globalization.CultureInfo.InvariantCulture) : 0,
            PlanFingerprint = plan?.Value("fingerprint") ?? "",
            PlanHistory = plans.Select(item => item.Value("json") ?? "").ToImmutableArray(),
            ProfileJson = profile,
            ProfileSha256 = profile.Length == 0 ? "" : Digest(profile),
            Recordings = recordings.ToImmutable()
        };
    }

    private static string ReviewText(FrozenAtlasSqlRow row) => row.Value("contract_json") is { Length: > 0 } contract
        ? contract : JsonDefaults.Serialize(row.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    private static string Digest(string material) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
}

public sealed class RegionScenarioSourceViewer : Window
{
    private readonly AtlasSnapshot _snapshot;
    private readonly FrameDecoderPool _decoder = new(1);
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _identity = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Grid _plane = new();
    private int _generation;
    private bool _closed;
    private string _scenarioName = "";
    private string _filename = "";
    private int _frame;
    private bool _trajectoryHeld;
    public BitmapSource? DisplayedBitmap => _image.Source as BitmapSource;
    public Canvas Overlay => _overlay;
    public bool Held { get; private set; }
    public int? DisplayedFrame { get; private set; }
    public string? DisplayedSourceSha256 { get; private set; }
    public string StatusText => _status.Text;
    public Task Pending { get; private set; } = Task.CompletedTask;
    public bool Blinded { get; private set; }
    public RegionScenarioSourceViewer(AtlasSnapshot snapshot, AtlasRecording recording, TrackingSettings region, int frame, string scenarioName, bool blinded)
    {
        _snapshot = snapshot; Blinded = blinded;
        Title = "Mouse Tracker Tabajara — Region scenario source";
        Width = 1440; Height = 960; MinWidth = 1050; MinHeight = 700; Background = Brushes.WhiteSmoke;
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_identity, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_identity); root.Children.Add(_status);
        _plane.Children.Add(_image); _plane.Children.Add(_overlay);
        root.Children.Add(new Border { Background = Brushes.DarkSlateGray, Child = new Viewbox { Stretch = Stretch.Uniform, Child = _plane } });
        Content = root;
        Closed += (_, _) => { _closed = true; _generation++; _decoder.Dispose(); };
        Pending = SeekAsync(recording, region, frame, scenarioName);
    }
    public void ApplyBlind(bool blinded)
    {
        Blinded = blinded;
        if (Held || DisplayedFrame is null) _status.Text = "Source review held. Recompute and verify study/source integrity.";
        else RefreshChrome();
    }
    private async Task SeekAsync(AtlasRecording recording, TrackingSettings region, int frame, string scenarioName)
    {
        var generation = ++_generation;
        try
        {
            var asset = recording.Asset;
            if (frame < 0 || frame >= asset.Metadata.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
            var bytes = await Task.Run(() =>
            {
                if (!_snapshot.IsCurrent()) throw new InvalidDataException("Frozen study is stale.");
                var pixels = _decoder.Read(asset.Path, frame, expectedSha256: asset.SourceSha256);
                if (!_snapshot.IsCurrent()) throw new InvalidDataException("Study changed during source decode.");
                return pixels;
            });
            if (_closed || generation != _generation) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            _plane.Width = asset.Metadata.Width; _plane.Height = asset.Metadata.Height; _image.Source = bitmap;
            DisplayedFrame = frame; DisplayedSourceSha256 = asset.SourceSha256; Held = false;
            _scenarioName = scenarioName; _filename = asset.Filename; _frame = frame;
            Draw(recording, region, frame);
            RefreshChrome();
        }
        catch (Exception ex)
        {
            if (_closed || generation != _generation) return;
            Held = true; _image.Source = null; _overlay.Children.Clear(); DisplayedFrame = null; DisplayedSourceSha256 = null;
            _scenarioName = ""; _filename = "";
            _status.Text = Blinded ? "Source review held. Recompute and verify study/source integrity." : ex.Message;
            _identity.Text = Blinded ? "Blinded source frame. Software labels only. The picture is not redacted." : "";
        }
    }
    private void RefreshChrome()
    {
        _identity.Text = Blinded ? "Blinded source frame. Software labels only. The picture is not redacted."
            : _filename + " · frame " + _frame.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var label = Blinded ? "Named scenario" : _scenarioName;
        var trajectory = _trajectoryHeld ? (Blinded ? " Frozen trajectory is held." : " Frozen trajectory could not be validated. The region is still shown.") : "";
        _status.Text = label + " · frozen source frame " + _frame.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + RegionScenarioWording.FrozenCoordinateMode + trajectory;
    }
    private void Draw(AtlasRecording recording, TrackingSettings region, int frame)
    {
        _overlay.Children.Clear();
        var metadata = recording.Asset.Metadata;
        var rx = region.Rx * metadata.Width; var ry = region.Ry * metadata.Height;
        var pool = new Ellipse { Width = Math.Max(1, 2 * rx), Height = Math.Max(1, 2 * ry), Stroke = Brushes.LightSkyBlue, StrokeThickness = 2, Tag = "scenario-pool" };
        Canvas.SetLeft(pool, region.Cx * metadata.Width - rx); Canvas.SetTop(pool, region.Cy * metadata.Height - ry); _overlay.Children.Add(pool);
        if (region.Platform is { } site)
        {
            var horizontal = site.Radius * metadata.Width; var vertical = horizontal * ry / rx;
            var shape = new Ellipse { Width = Math.Max(1, 2 * horizontal), Height = Math.Max(1, 2 * vertical), Stroke = Brushes.PeachPuff, StrokeThickness = 2, Tag = "scenario-platform" };
            Canvas.SetLeft(shape, site.X * metadata.Width - horizontal); Canvas.SetTop(shape, site.Y * metadata.Height - vertical); _overlay.Children.Add(shape);
        }
        AnalysisRun? run = null; _trajectoryHeld = false;
        try { run = recording.EffectiveRun(); } catch (Exception) { _trajectoryHeld = true; }
        var active = run?.Points.SingleOrDefault(point => point.Frame == frame);
        if (active is { Accepted: true })
        {
            var marker = new Ellipse { Width = 18, Height = 18, Stroke = Brushes.LightGreen, StrokeThickness = 2, Tag = "active-source-marker" };
            Canvas.SetLeft(marker, active.X!.Value - 9); Canvas.SetTop(marker, active.Y!.Value - 9); _overlay.Children.Add(marker);
        }
    }
}
