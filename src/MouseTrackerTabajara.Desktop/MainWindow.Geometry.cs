using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public partial class MainWindow
{
    public string GeometrySmokeReport { get; private set; } = "";

    private string? ReasonText()
    {
        var reason = AnnotationReasonInput.Text.Trim();
        return reason.Length == 0 ? null : reason;
    }

    private string RequiredReason() => ReasonText() ?? throw new ArgumentException("Enter a reason. Blank corrections and biological annotations are not saved.");

    private string RequiredReviewer()
    {
        var reviewer = ReviewerInput.Text.Trim();
        if (reviewer.Length == 0) throw new ArgumentException("Enter the reviewer. Biological annotations are not saved without one.");
        return reviewer;
    }

    private int CurrentExactFrame()
    {
        var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
        var value = FrameSlider.Value;
        if (Math.Abs(value - Math.Round(value)) > 1e-6)
            throw new InvalidOperationException("The viewer is between frames. Nearby frames are not substituted.");
        var frame = FrameIdentity.RequireSourceFrame((int)Math.Round(value), row.Asset.Metadata.FrameCount);
        if (!DisplayedFrameMatches(row, frame))
            throw new InvalidOperationException("The displayed source frame does not match this request. Edits stay blocked until this recording's frame finishes decoding.");
        return frame;
    }

    private void ShowExactFrame(int frame)
    {
        var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
        var exact = FrameIdentity.RequireSourceFrame(frame, row.Asset.Metadata.FrameCount);
        StopPlayback();
        FrameSlider.Value = exact;
        if (Math.Abs(FrameSlider.Value - exact) > 1e-6)
            throw new InvalidOperationException("The viewer substituted a nearby frame.");
    }

    private DisplayToVideo.VideoPoint? MapViewport(double xDip, double yDip)
    {
        if (_model.Selected is not { } row) return null;
        var width = ViewerViewport.ActualWidth;
        var height = ViewerViewport.ActualHeight;
        if (width <= 0 || height <= 0) return null;
        return DisplayToVideo.MapDips(xDip, yDip, width, height, row.Asset.Metadata.Width, row.Asset.Metadata.Height);
    }

    private static (int X, int Y) SourcePixel(DisplayToVideo.VideoPoint point, VideoMetadata metadata)
    {
        var x = (int)Math.Round(point.X);
        var y = (int)Math.Round(point.Y);
        if (x < 0 || y < 0 || x >= metadata.Width || y >= metadata.Height)
            throw new ArgumentException("That click is outside the source image.");
        return (x, y);
    }

    private void RefreshReviewQueue()
    {
        _model.ReviewMoments.Clear();
        if (_current is null) return;
        var evidence = ReviewRankingEvidence.Capture(_current);
        foreach (var moment in evidence.Moments) _model.ReviewMoments.Add(moment);
        _model.Workspace.Store.SaveRankingSnapshot(_current, evidence);
    }

    private void RefreshAnnotations()
    {
        if (_model.Selected is not { } row)
        {
            EventReadout.Text = "";
            ProtocolReadout.Text = "";
            ApplyProtocolDraft(null);
            CollectionCompatibilityReadout.Text = "";
            return;
        }
        var events = _model.Workspace.Store.EffectiveBiologicalEvents(row.Asset.Id);
        EventReadout.Text = events.Count == 0
            ? _model.Blind
                ? "No biological annotations. Platform-site crossing is not water entry or mounting."
                : "No biological annotations. A platform-site crossing is not water entry or mounting."
            : string.Join("\n", events.Select(item => _model.Blind
                ? string.Create(CultureInfo.InvariantCulture, $"{item.Kind} frame {item.Frame} ({item.TimeSeconds:0.000} s) revision {item.Revision}. {BlindIdentity.AnnotationWithheld}")
                : string.Create(CultureInfo.InvariantCulture, $"{item.Kind} frame {item.Frame} ({item.TimeSeconds:0.000} s) revision {item.Revision}: {item.Reason} — {item.Reviewer}")));
        var window = _model.Workspace.Store.LatestProtocol(row.Asset.Id);
        ApplyProtocolDraft(window);
        if (window is null)
            ProtocolReadout.Text = "No planned window. Requested exposure is not copied into the detector start or end.";
        else
        {
            var outcome = ProtocolEvaluator.Evaluate(window, row.Asset.Metadata, row.Asset.Phase, events);
            ProtocolReadout.Text = string.Create(CultureInfo.InvariantCulture, $"Protocol revision {window.Revision}. {outcome.Visible}");
        }
        RefreshCollectionCompatibility();
        ScrubBlindedReadouts();
    }

    private void ApplyProtocolDraft(ProtocolWindow? window)
    {
        var id = _model.Selected?.Asset.Id;
        var pending = id is not null && _releaseAnchors.TryGetValue(id, out var existing) && existing.Pending;
        if (id is not null && !pending)
            _releaseAnchors[id] = new ReleaseAnchorDraft(window?.ReleaseFrame, false);
        if (!pending)
        {
            if (window is null)
            {
                PlannedExposureInput.Text = "";
                ReleaseConfirmedInput.IsChecked = false;
                WindowConfirmedInput.IsChecked = false;
            }
            else
            {
                PlannedExposureInput.Text = window.RequestedExposureSeconds.ToString("0.000", CultureInfo.InvariantCulture);
                ReleaseConfirmedInput.IsChecked = window.ReleaseConfirmed;
                WindowConfirmedInput.IsChecked = window.WindowConfirmed;
            }
        }
        ShowReleaseAnchor();
    }

    private void ShowReleaseAnchor()
    {
        if (ReleaseAnchorReadout is null) return;
        var id = _model.Selected?.Asset.Id;
        int? frame = id is not null && _releaseAnchors.TryGetValue(id, out var draft) ? draft.Frame : null;
        ReleaseAnchorReadout.Text = frame is int value
            ? string.Create(CultureInfo.InvariantCulture, $"Release anchor frame {value}. Navigation does not move it.")
            : "No release anchor. The viewer frame is not the release.";
    }

    private void UseDisplayedFrameAsRelease(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
            var frame = CurrentExactFrame();
            _releaseAnchors[row.Asset.Id] = new ReleaseAnchorDraft(frame, true);
            ReleaseConfirmedInput.IsChecked = true;
            ShowReleaseAnchor();
            _model.Status = string.Create(CultureInfo.InvariantCulture, $"Release anchor set to completed frame {frame}. Save the protocol to store it. Seeking does not move this anchor.");
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }

    private void RefreshCollectionCompatibility()
    {
        if (CollectionCompatibilityReadout is null) return;
        var store = _model.Workspace.Store;
        var notes = new List<string>();
        var trials = new List<(string Label, ProtocolOutcome Outcome)>();
        foreach (var row in _model.Rows)
        {
            var label = _model.Blind ? BlindIdentity.RecordingAlias(row.Asset) : row.Asset.AnimalId;
            if (!_model.Blind && string.IsNullOrWhiteSpace(label)) label = row.Asset.Filename;
            if (string.IsNullOrWhiteSpace(label)) label = "Recording";
            var window = store.LatestProtocol(row.Asset.Id);
            if (window is null)
            {
                notes.Add(label + ": no planned window; excluded from exposure comparison.");
                continue;
            }
            var outcome = ProtocolEvaluator.Evaluate(window, row.Asset.Metadata, row.Asset.Phase, store.EffectiveBiologicalEvents(row.Asset.Id));
            trials.Add((label, outcome));
        }
        var compared = ProtocolEvaluator.Compare(trials);
        var text = notes.Count == 0 ? compared.Visible : compared.Visible + "\n" + string.Join("\n", notes);
        CollectionCompatibilityReadout.Text = _model.Blind ? BlindIdentity.Scrub(text, _model.StudySecrets()) : text;
    }

    private FrameContract CurrentContract()
    {
        if (_model.Selected is not { } row || _current is null) return FrameContract.For(false, false);
        var match = StudyStore.SettingsHash(_current.Settings) == StudyStore.SettingsHash(row.Asset.Settings);
        return FrameContract.For(true, match);
    }

    private TrackingSettings OverlaySettings()
    {
        var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
        if (CurrentContract().ShowAnalyzedTrack && _current is not null) return _current.Settings;
        return row.Asset.Settings;
    }

    private void ApplyViewportPoint(double xDip, double yDip)
    {
        var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
        var mapped = MapViewport(xDip, yDip);
        if (mapped is null)
        {
            _model.Status = "That click is outside the source image.";
            return;
        }
        var pixel = SourcePixel(mapped.Value, row.Asset.Metadata);
        if (_editMode == "pool")
        {
            if (_poolCorner is null)
            {
                _poolCorner = new System.Windows.Point(mapped.Value.X, mapped.Value.Y);
                ViewerHint.Text = "Now click the opposite corner. Either order defines the same pool.";
                return;
            }
            var corner = _poolCorner.Value;
            PersistSettings(ArenaGeometry.FromCorners(row.Asset.Settings, row.Asset.Metadata.Width, row.Asset.Metadata.Height, corner.X, corner.Y, mapped.Value.X, mapped.Value.Y), RequiredReason());
        }
        else if (_editMode == "platform")
            PersistSettings(ArenaGeometry.WithPlatform(row.Asset.Settings, row.Asset.Metadata.Width, row.Asset.Metadata.Height, mapped.Value.X, mapped.Value.Y, double.Parse(SiteDiameterInput.Text, CultureInfo.InvariantCulture)), RequiredReason());
        else CorrectFrame(pixel.X, pixel.Y);
        _editMode = null;
        _poolCorner = null;
        FrameOverlay.Cursor = Cursors.Arrow;
    }

    private void RecordWaterEntry(object sender, RoutedEventArgs e) => RecordAnnotation(BiologicalEventKind.WaterEntry, null);
    private void RecordMounting(object sender, RoutedEventArgs e) => RecordAnnotation(BiologicalEventKind.PlatformMounting, null);
    private void RecordIntervention(object sender, RoutedEventArgs e) => RecordAnnotation(BiologicalEventKind.ResearcherIntervention, null);
    private void RecordRecordingEnd(object sender, RoutedEventArgs e) => RecordAnnotation(BiologicalEventKind.RecordingEnd, null);

    private void ReviseLatestEvent(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
            var latest = _model.Workspace.Store.EffectiveBiologicalEvents(row.Asset.Id)
                .OrderBy(item => item.CreatedUtc).ThenBy(item => item.Id, StringComparer.Ordinal).LastOrDefault()
                ?? throw new InvalidOperationException("There is no biological annotation to revise.");
            RecordAnnotation(latest.Kind, latest.Id);
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }

    private void RecordAnnotation(BiologicalEventKind kind, string? reviseId)
    {
        try
        {
            var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
            var frame = CurrentExactFrame();
            var saved = _model.Workspace.Store.RecordBiologicalEvent(row.Asset, kind, frame, RequiredReason(), RequiredReviewer(), reviseId);
            if (saved.Frame != frame) throw new InvalidOperationException("The annotation was stored on a different frame.");
            _model.SyncReview(row);
            RefreshAnnotations();
            UpdateReadouts();
            row.Refresh();
            _model.Status = $"{saved.Kind} recorded at exact frame {saved.Frame} (revision {saved.Revision}). Platform-site entry was not converted into this annotation.";
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }

    private void SaveProtocol(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
            var before = row.Asset.Settings;
            _releaseAnchors.TryGetValue(row.Asset.Id, out var draft);
            var confirmed = ReleaseConfirmedInput.IsChecked == true;
            var frame = ReleaseAnchorChoice.ForSave(confirmed, draft?.Frame);
            var saved = _model.Workspace.Store.SaveProtocolWindow(row.Asset, new ProtocolWindow
            {
                RequestedExposureSeconds = double.Parse(PlannedExposureInput.Text, CultureInfo.InvariantCulture),
                ReleaseConfirmed = confirmed,
                ReleaseFrame = frame,
                WindowConfirmed = WindowConfirmedInput.IsChecked == true,
                Reason = RequiredReason(),
                Reviewer = RequiredReviewer()
            });
            _releaseAnchors[row.Asset.Id] = new ReleaseAnchorDraft(saved.ReleaseFrame, false);
            var after = _model.Workspace.Store.Videos().Single(video => video.Id == row.Asset.Id);
            if (after.Settings.StartSeconds != before.StartSeconds || after.Settings.EndSeconds != before.EndSeconds)
                throw new InvalidOperationException("Saving the protocol changed the detector window.");
            row.Replace(after);
            _model.SyncReview(row);
            RefreshAnnotations();
            UpdateReadouts();
            _model.Status = string.Create(CultureInfo.InvariantCulture, $"Protocol revision {saved.Revision} saved. Requested exposure stayed {saved.RequestedExposureSeconds:0.000} s and was not copied into the detector window.");
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }

    public async Task RunGeometrySmokeAsync(string directory)
    {
        var lines = new List<string>();
        try { await RunGeometrySmokeCore(directory, lines); }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex.GetType().Name + ": " + ex.Message);
            throw;
        }
        finally
        {
            GeometrySmokeReport = string.Join(Environment.NewLine, lines);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "geometry-smoke.txt"), GeometrySmokeReport);
        }
    }

    private async Task RunGeometrySmokeCore(string directory, List<string> lines)
    {
        void Pass(string text) => lines.Add("PASS " + text);
        async Task ShowDecodedFrameAsync()
        {
            await ShowSelectedAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await DisplayFrameAsync();
            if (FrameImage.Source is null) throw new InvalidOperationException("The synthetic frame did not decode. " + _model.Status);
        }
        var database = Path.GetFullPath(Path.Combine(directory, "synthetic.study.db"));
        if (!string.Equals(Path.GetFullPath(_model.Workspace.Store.DatabasePath), database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Geometry smoke opened a study other than the synthetic database.");
        var videoPath = Path.Combine(directory, "synthetic-recording.avi");
        WriteSyntheticRecording(videoPath);
        var imported = _model.Workspace.Import(videoPath);
        _model.Reload();
        var row = _model.Selected ?? throw new InvalidOperationException("The synthetic recording was not selected.");
        if (row.Asset.Id != imported.Id) throw new InvalidOperationException("The synthetic recording was not selected.");
        await ShowDecodedFrameAsync();
        var meta = row.Asset.Metadata;
        if (meta.Width != 160 || meta.Height != 120 || meta.FrameCount < 12)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Synthetic probe was {meta.Width}x{meta.Height}, {meta.FrameCount} frames at {meta.Fps} fps."));
        Pass(string.Create(CultureInfo.InvariantCulture, $"synthetic source {meta.Width}x{meta.Height} frames={meta.FrameCount} fps={meta.Fps:R}"));

        AnnotationReasonInput.Text = "Synthetic geometry confirmation";
        PersistSettings(ArenaGeometry.Confirm(row.Asset.Settings), ReasonText());
        if (ArenaConfirmedInput.IsChecked != true || !row.Asset.Settings.ArenaConfirmed)
            throw new InvalidOperationException("Deliberate confirmation did not persist. " + _model.Status);
        Pass("deliberate confirmation of unchanged imported geometry");
        var drafted = ArenaGeometry.FromCorners(row.Asset.Settings, meta.Width, meta.Height, 8, 8, meta.Width - 9, meta.Height - 9);
        PersistSettings(drafted, ReasonText());
        if (row.Asset.Settings.ArenaConfirmed || ArenaConfirmedInput.IsChecked == true)
            throw new InvalidOperationException("Geometry edit kept pool confirmation.");
        if (!_model.Status.Contains("confirmation was cleared", StringComparison.Ordinal))
            throw new InvalidOperationException("Missing confirmation-cleared status: " + _model.Status);
        Pass("geometry edit cleared pool confirmation");
        PersistSettings(ArenaGeometry.Confirm(row.Asset.Settings), ReasonText());
        if (!row.Asset.Settings.ArenaConfirmed || !ArenaGeometry.SameGeometry(drafted, row.Asset.Settings))
            throw new InvalidOperationException("Second save did not confirm the edited geometry. " + _model.Status);
        Pass("second save confirmed the same geometry numbers");
        var reloaded = new StudyStore(_model.Workspace.Store.DatabasePath).Videos().Single().Settings;
        if (reloaded.Cx != row.Asset.Settings.Cx || reloaded.Cy != row.Asset.Settings.Cy || reloaded.Rx != row.Asset.Settings.Rx || reloaded.Ry != row.Asset.Settings.Ry || !reloaded.ArenaConfirmed)
            throw new InvalidOperationException("Saved geometry did not round-trip.");
        Pass(string.Create(CultureInfo.InvariantCulture, $"geometry round-trip cx={reloaded.Cx:R} cy={reloaded.Cy:R} rx={reloaded.Rx:R} ry={reloaded.Ry:R}"));

        var settings = row.Asset.Settings;
        var centerX = (int)Math.Round(settings.Cx * meta.Width);
        var centerY = (int)Math.Round(settings.Cy * meta.Height);
        var points = Enumerable.Range(0, meta.FrameCount)
            .Select(frame => new TrackPoint(frame, frame / meta.Fps, centerX, centerY, PointStatus.Tracked, 1))
            .ToList();
        var run = new AnalysisRun
        {
            VideoId = row.Asset.Id,
            SourceSha256 = row.Asset.SourceSha256,
            Metadata = meta,
            Settings = settings,
            NominalTimingVerified = true,
            Points = points,
            Metrics = MetricCalculator.Calculate(points, meta, settings)
        };
        var store = _model.Workspace.Store;
        store.SaveRun(run);
        store.RecordReview(run, true);
        var immutableRun = JsonDefaults.Serialize(store.LatestRun(row.Asset.Id)!);
        _model.Reload();
        row = _model.Selected ?? throw new InvalidOperationException("Synthetic recording disappeared after analysis.");
        await ShowDecodedFrameAsync();
        if (_current is null) throw new InvalidOperationException("Analyzed synthetic frame did not display. " + _model.Status);
        if (!store.Reviewed(run.Id) || row.Status != "Reviewed") throw new InvalidOperationException("Coherent review did not display as reviewed: " + row.Status);
        if (ContractLabel.Text != "Analyzed geometry. Overlays, detection mask, and measurements use this run.")
            throw new InvalidOperationException(ContractLabel.Text);
        if (!FrameOverlay.Children.OfType<System.Windows.Shapes.Path>().Any())
            throw new InvalidOperationException("The analyzed trajectory was not drawn on the matching geometry.");
        var maskStatus = _model.Status;
        BinaryView.IsChecked = true;
        await DisplayFrameAsync();
        if (FrameImage.Source is null || _model.Status != maskStatus)
            throw new InvalidOperationException("Detection mask did not use the analyzed geometry. " + _model.Status);
        if (SettingsIdentity.Hash(OverlaySettings()) != SettingsIdentity.Hash(_current.Settings))
            throw new InvalidOperationException("Detection mask geometry diverged from the analyzed run.");
        BinaryView.IsChecked = false;
        await DisplayFrameAsync();
        if (_model.Status != maskStatus) throw new InvalidOperationException("Restoring the source frame failed. " + _model.Status);
        Pass("coherent overlay, detection mask, and measurements use the analyzed run");

        foreach (var size in new[] { new System.Windows.Size(1440, 960), new System.Windows.Size(1050, 700) })
        {
            ArrangeWorkspace(size.Width, size.Height);
            var viewportWidth = ViewerViewport.ActualWidth;
            var viewportHeight = ViewerViewport.ActualHeight;
            if (viewportWidth <= 1 || viewportHeight <= 1)
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Viewer has no layout at {size.Width:F0}x{size.Height:F0}."));
            var place = DisplayToVideo.Place(viewportWidth, viewportHeight, meta.Width, meta.Height);
            var interiorX = place.OffsetX + place.Width * 0.5;
            var interiorY = place.OffsetY + place.Height * 0.5;
            var mapped = MapViewport(interiorX, interiorY) ?? throw new InvalidOperationException("Interior click missed the source image.");
            var onPlane = ViewerViewport.TranslatePoint(new System.Windows.Point(interiorX, interiorY), FramePlane);
            if (Math.Abs(mapped.X - onPlane.X) > 1 || Math.Abs(mapped.Y - onPlane.Y) > 1)
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Transform {mapped.X:F3},{mapped.Y:F3} disagreed with layout {onPlane.X:F3},{onPlane.Y:F3}."));
            var borderX = place.OffsetX > 1 ? place.OffsetX * 0.5 : interiorX;
            var borderY = place.OffsetY > 1 ? place.OffsetY * 0.5 : interiorY;
            if (place.OffsetX <= 1 && place.OffsetY <= 1)
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"No letterbox at {size.Width:F0}x{size.Height:F0} viewer {viewportWidth:F1}x{viewportHeight:F1} offsets {place.OffsetX:F1},{place.OffsetY:F1}."));
            if (MapViewport(borderX, borderY) is not null) throw new InvalidOperationException("A letterbox click mapped onto the source image.");
            if (MapViewport(place.OffsetX + place.Width, interiorY) is not null)
                throw new InvalidOperationException("The far edge of the image was accepted as a source pixel.");
            foreach (var dpi in new[] { 96d, 144d, 192d })
            {
                var scale = dpi / 96d;
                var device = DisplayToVideo.MapDevicePixels(interiorX * scale, interiorY * scale, viewportWidth * scale, viewportHeight * scale, dpi, meta.Width, meta.Height)
                    ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"DPI {dpi:F0} lost the interior point."));
                if (Math.Abs(device.X - mapped.X) > 1e-6 || Math.Abs(device.Y - mapped.Y) > 1e-6)
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"DPI {dpi:F0} changed the source coordinate."));
            }
            var forward = ArenaGeometry.FromCorners(row.Asset.Settings, meta.Width, meta.Height, 12, 14, meta.Width - 15, meta.Height - 16);
            var reverse = ArenaGeometry.FromCorners(row.Asset.Settings, meta.Width, meta.Height, meta.Width - 15, meta.Height - 16, 12, 14);
            if (forward.Cx != reverse.Cx || forward.Cy != reverse.Cy || forward.Rx != reverse.Rx || forward.Ry != reverse.Ry)
                throw new InvalidOperationException("Reversed corners changed the pool.");
            if (viewportHeight < 200)
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Viewer is {viewportHeight:F1} DIP high at {size.Width:F0}x{size.Height:F0}. It needs at least 200 DIP."));
            EnsureInside(WorkspaceFooter, RootGrid, "Footer");
            EnsureInside(CommandBar, RootGrid, "Commands");
            EnsureInside(TrialGrid, RootGrid, "Trial grid");
            EnsureInside(ViewerTools, RootGrid, "Viewer tools");
            EnsureInside(PlayButton, RootGrid, "Play");
            EnsureInside(ContractLabel, RootGrid, "Contract");
            EnsureInside(ViewerHint, RootGrid, "Viewer hint");
            foreach (FrameworkElement child in CommandBar.Children) EnsureInside(child, RootGrid, "Command");
            foreach (FrameworkElement child in ViewerTools.Children) EnsureInside(child, RootGrid, "Viewer tool");
            Pass(string.Create(CultureInfo.InvariantCulture, $"layout {size.Width:F0}x{size.Height:F0} viewer={viewportWidth:F1}x{viewportHeight:F1} letterbox={place.OffsetX:F1},{place.OffsetY:F1} source={mapped.X:F3},{mapped.Y:F3}"));
        }

        FrameSlider.IsSnapToTickEnabled = false;
        FrameSlider.Value = 5.2;
        try
        {
            CurrentExactFrame();
            throw new InvalidOperationException("A fractional slider position was accepted.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("between frames", StringComparison.Ordinal))
        {
            Pass(ex.Message);
        }
        FrameSlider.IsSnapToTickEnabled = true;
        ShowExactFrame(5);
        await WaitForDisplayedFrameAsync(5);
        if (CurrentExactFrame() != 5) throw new InvalidOperationException("Exact frame 5 was replaced.");
        AnnotationReasonInput.Text = "Synthetic exclusion of this exact frame";
        var rawMetrics = JsonDefaults.Serialize(store.LatestRun(row.Asset.Id)!.Metrics);
        CorrectFrame(null, null);
        var raw = store.LatestRun(row.Asset.Id)!;
        var excluded = raw.Points.Single(point => point.Frame == 5);
        var effective = store.EffectiveRun(raw);
        var effectivePoint = effective.Points.Single(point => point.Frame == 5);
        if (excluded.Status != PointStatus.Tracked || effectivePoint.Status != PointStatus.Excluded || effectivePoint.X is not null)
            throw new InvalidOperationException("Exclusion rewrote the raw point or skipped the effective point.");
        if (JsonDefaults.Serialize(raw.Metrics) != rawMetrics) throw new InvalidOperationException("Raw metrics changed after correction.");
        if (effective.Metrics.TrackedFrames != raw.Metrics.TrackedFrames - 1) throw new InvalidOperationException("Effective metrics were not recomputed.");
        if (store.Corrections(run.Id).Count != 1) throw new InvalidOperationException("Correction count was not one.");
        if (!_model.ReviewMoments.Any(moment => moment.StartFrame <= 5 && moment.EndFrame >= 5))
            throw new InvalidOperationException("The review queue does not cover exact frame 5.");
        if (store.Reviewed(run.Id)) throw new InvalidOperationException("Correction left tracking review approved.");
        Pass("exact frame 5 excluded; raw point unchanged; effective metrics recomputed; review pending");
        await AssertDecodeRaceBlocksEditsAsync(store, row, 9);
        Pass("delayed decode kept the previous pixels and rejected correction, annotation, and release confirmation");
        try { ShowExactFrame(meta.FrameCount); throw new InvalidOperationException("A frame outside the source was shown."); }
        catch (ArgumentException ex) when (ex.Message.Contains("Nearby frames are not substituted", StringComparison.Ordinal)) { Pass(ex.Message); }

        store.RecordReview(raw, true);
        if (!store.Reviewed(run.Id)) throw new InvalidOperationException("Matching geometry could not be reviewed again.");
        ArrangeWorkspace(1440, 960);
        var confirmedHash = SettingsIdentity.Hash(raw.Settings);
        AnnotationReasonInput.Text = "Synthetic pool edit from the wide window";
        ClickSource(20, 18);
        ClickSource(140, 100);
        if (row.Asset.Settings.ArenaConfirmed || row.Status != "Editable preview" || !ContractLabel.Text.Contains("Editable preview", StringComparison.Ordinal))
            throw new InvalidOperationException("Wide-window pool edit did not become an editable preview. " + row.Status + " / " + ContractLabel.Text);
        if (store.Reviewed(run.Id) || SettingsIdentity.Hash(store.LatestRun(row.Asset.Id)!.Settings) != confirmedHash)
            throw new InvalidOperationException("The pool edit changed the raw run or left review approved.");
        if (FrameOverlay.Children.OfType<System.Windows.Shapes.Path>().Any())
            throw new InvalidOperationException("The analyzed trajectory stayed on the edited geometry.");
        if (SettingsIdentity.Hash(OverlaySettings()) != SettingsIdentity.Hash(row.Asset.Settings))
            throw new InvalidOperationException("Preview overlay is not the edited geometry.");
        var wideShot = Path.Combine(directory, "geometry-1440.png");
        SaveElementPng(RootGrid, wideShot, 1440, 960, 96);
        Pass("wide window saved an editable pool preview and hid the stale trajectory");
        var storedCorrections = store.Corrections(run.Id).Count;
        try
        {
            CorrectFrame(null, null);
            throw new InvalidOperationException("Correction was applied against the editable preview.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Corrections stay blocked", StringComparison.Ordinal))
        {
            Pass(ex.Message);
        }
        if (store.Corrections(run.Id).Count != storedCorrections) throw new InvalidOperationException("A blocked correction was stored.");

        ArrangeWorkspace(1050, 700);
        SiteDiameterInput.Text = "40";
        AnnotationReasonInput.Text = "Synthetic platform edit from the narrow window";
        var platformBefore = row.Asset.Settings;
        var platformDip = SourceDip(80, 59);
        var platformMapped = MapViewport(platformDip.X, platformDip.Y) ?? throw new InvalidOperationException("Platform click missed the source.");
        _editMode = "platform";
        ApplyViewportPoint(platformDip.X, platformDip.Y);
        var expectedPlatform = ArenaGeometry.WithPlatform(platformBefore, meta.Width, meta.Height, platformMapped.X, platformMapped.Y, 40);
        if (row.Asset.Settings.Platform is null || !ArenaGeometry.SameGeometry(expectedPlatform, row.Asset.Settings) || row.Asset.Settings.ArenaConfirmed)
            throw new InvalidOperationException("Narrow-window platform edit did not persist the clicked center.");
        var siteEllipse = ArenaGeometry.SourceEllipse(row.Asset.Settings.Platform, row.Asset.Settings, meta.Width, meta.Height);
        if (Math.Abs(siteEllipse.HorizontalRadius - siteEllipse.VerticalRadius) < 0.5)
            throw new InvalidOperationException("The synthetic pool axes did not separate the site ellipse.");
        var drawnSite = FrameOverlay.Children.OfType<System.Windows.Shapes.Ellipse>().SingleOrDefault(shape => Math.Abs(shape.Width - siteEllipse.Width) < 0.01 && Math.Abs(shape.Height - siteEllipse.Height) < 0.01)
            ?? throw new InvalidOperationException("The site overlay is not the metric ellipse.");
        if (Math.Abs(Canvas.GetLeft(drawnSite) - siteEllipse.Left) > 0.01 || Math.Abs(Canvas.GetTop(drawnSite) - siteEllipse.Top) > 0.01)
            throw new InvalidOperationException("The site ellipse offsets do not match the metric predicate.");
        var inside = new System.Windows.Point(siteEllipse.CenterX, siteEllipse.CenterY);
        var outside = new System.Windows.Point(siteEllipse.CenterX, siteEllipse.CenterY + siteEllipse.VerticalRadius + 1);
        if (!siteEllipse.Contains(inside.X, inside.Y) || !ArenaGeometry.InsidePlatformSite(inside.X, inside.Y, row.Asset.Settings, meta.Width, meta.Height))
            throw new InvalidOperationException("The site center is outside the drawn ellipse or the metric predicate.");
        if (siteEllipse.Contains(outside.X, outside.Y) || ArenaGeometry.InsidePlatformSite(outside.X, outside.Y, row.Asset.Settings, meta.Width, meta.Height))
            throw new InvalidOperationException("A point outside the vertical site radius was accepted.");
        Pass(string.Create(CultureInfo.InvariantCulture, $"site ellipse horizontal={siteEllipse.HorizontalRadius:F3} vertical={siteEllipse.VerticalRadius:F3}"));
        var narrowShot = Path.Combine(directory, "geometry-1050.png");
        SaveElementPng(RootGrid, narrowShot, 1050, 700, 96);
        Pass("narrow window saved the clicked platform without confirming the pool");

        var detectorStart = row.Asset.Settings.StartSeconds;
        var detectorEnd = row.Asset.Settings.EndSeconds;
        var detectorHash = SettingsIdentity.Hash(row.Asset.Settings);
        var probe = row.Asset with { Phase = TrialMode.Probe };
        store.SaveVideo(probe);
        row.Replace(probe);
        ReviewerInput.Text = "Synthetic reviewer";
        ShowExactFrame(2);
        await WaitForDisplayedFrameAsync(2);
        AnnotationReasonInput.Text = "Synthetic water entry at the release frame";
        RecordWaterEntry(this, new RoutedEventArgs());
        var water = store.EffectiveBiologicalEvents(row.Asset.Id).SingleOrDefault(item => item.Kind == BiologicalEventKind.WaterEntry)
            ?? throw new InvalidOperationException("Water entry was not stored. " + _model.Status);
        if (water.Frame != 2 || !_model.Status.Contains("Platform-site entry was not converted", StringComparison.Ordinal))
            throw new InvalidOperationException(_model.Status);
        ShowExactFrame(8);
        await WaitForDisplayedFrameAsync(8);
        AnnotationReasonInput.Text = "Synthetic confirmed mounting";
        RecordMounting(this, new RoutedEventArgs());
        var mounting = store.EffectiveBiologicalEvents(row.Asset.Id).SingleOrDefault(item => item.Kind == BiologicalEventKind.PlatformMounting)
            ?? throw new InvalidOperationException("Platform mounting was not stored. " + _model.Status);
        if (mounting.Frame != 8) throw new InvalidOperationException("Mounting was stored on frame " + mounting.Frame);
        ShowExactFrame(2);
        await WaitForDisplayedFrameAsync(2);
        UseDisplayedFrameAsRelease(this, new RoutedEventArgs());
        if (!ReleaseAnchorReadout.Text.Contains("frame 2", StringComparison.Ordinal))
            throw new InvalidOperationException("The completed frame was not stored as the release anchor. " + ReleaseAnchorReadout.Text);
        var releaseSeconds = 2 / meta.Fps;
        var available = meta.FrameCount / meta.Fps - releaseSeconds;
        var requested = Math.Floor(available * 0.5 * 1000d) / 1000d;
        if (requested <= 0 || releaseSeconds + requested > available + 1e-9)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Cannot place a fixed probe inside {available:R} s."));
        PlannedExposureInput.Text = requested.ToString("0.000", CultureInfo.InvariantCulture);
        ReleaseConfirmedInput.IsChecked = true;
        WindowConfirmedInput.IsChecked = true;
        AnnotationReasonInput.Text = "Synthetic planned probe window";
        SaveProtocol(this, new RoutedEventArgs());
        var window = store.LatestProtocol(row.Asset.Id) ?? throw new InvalidOperationException("Protocol window was not stored. " + _model.Status);
        var outcome = ProtocolEvaluator.Evaluate(window, row.Asset.Metadata, row.Asset.Phase, store.EffectiveBiologicalEvents(row.Asset.Id));
        if (window.Phase != TrialMode.Probe || Math.Abs(window.RequestedExposureSeconds - requested) > 1e-9)
            throw new InvalidOperationException("Requested exposure was rewritten.");
        if (outcome.TerminationReason != "Fixed probe exposure" || !outcome.Visible.Contains("Requested exposure was not shortened.", StringComparison.Ordinal)
            || !outcome.Visible.Contains("Platform mounting is annotated and does not shorten the probe.", StringComparison.Ordinal))
            throw new InvalidOperationException(outcome.Visible);
        if (!ProtocolReadout.Text.Contains("Fixed probe exposure", StringComparison.Ordinal) || !ProtocolReadout.Text.Contains("Requested exposure was not shortened.", StringComparison.Ordinal))
            throw new InvalidOperationException(ProtocolReadout.Text + " | " + _model.Status);
        if (window.ReleaseFrame != 2)
            throw new InvalidOperationException("The confirmed release was not the completed displayed frame.");
        if (row.Asset.Settings.StartSeconds != detectorStart || row.Asset.Settings.EndSeconds != detectorEnd || SettingsIdentity.Hash(row.Asset.Settings) != detectorHash)
            throw new InvalidOperationException("Saving the protocol changed detector settings.");
        ShowExactFrame(8);
        await WaitForDisplayedFrameAsync(8);
        UseDisplayedFrameAsRelease(this, new RoutedEventArgs());
        var releaseRevision = store.LatestProtocol(row.Asset.Id)!.Revision;
        AnnotationReasonInput.Text = "Synthetic release without a matching water entry";
        SaveProtocol(this, new RoutedEventArgs());
        var rejected = store.LatestProtocol(row.Asset.Id)!;
        if (rejected.Revision != releaseRevision || rejected.ReleaseFrame != 2 || !_model.Status.Contains("water entry", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A release without a matching water entry was stored. " + _model.Status);
        ShowExactFrame(2);
        await WaitForDisplayedFrameAsync(2);
        UseDisplayedFrameAsRelease(this, new RoutedEventArgs());
        ShowExactFrame(8);
        await WaitForDisplayedFrameAsync(8);
        AnnotationReasonInput.Text = "Synthetic duration-only revision after seeking";
        SaveProtocol(this, new RoutedEventArgs());
        var kept = store.LatestProtocol(row.Asset.Id)!;
        if (kept.ReleaseFrame != 2 || Math.Abs(kept.RequestedExposureSeconds - requested) > 1e-9 || Math.Abs(FrameSlider.Value - 8) > 1e-6)
            throw new InvalidOperationException("Seeking moved the confirmed release. " + ReleaseAnchorReadout.Text);
        Pass("duration-only save after seeking kept the release anchor");
        Pass("protocol outcome:");
        foreach (var line in outcome.Visible.Split('\n')) Pass(line);
        if (!MetricsReadout.Text.Contains("Trial endpoints are withheld", StringComparison.Ordinal) || MetricsReadout.Text.Contains("Path:", StringComparison.Ordinal))
            throw new InvalidOperationException("Full-run endpoints were displayed as the confirmed trial. " + MetricsReadout.Text);
        if (!MetricsReadout.Text.Contains(TrialMeasurementWording.ProtocolLabelDoesNotSupply, StringComparison.Ordinal)
            || MetricsReadout.Text.Contains("D3", StringComparison.Ordinal) || MetricsReadout.Text.Contains("measurement contract", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The trial-measurement limit is missing or still uses an internal label. " + MetricsReadout.Text);
        if (row.Asset.Settings.StartSeconds != detectorStart || row.Asset.Settings.EndSeconds != detectorEnd)
            throw new InvalidOperationException("The readout trimmed the detector window.");
        var exportDir = Path.Combine(directory, "export");
        ExportCurrentRun(exportDir);
        var exportedHtml = File.ReadAllText(Path.Combine(exportDir, "report.html"));
        var exportedScope = File.ReadAllText(Path.Combine(exportDir, "measurement-scope.txt"));
        if (!exportedHtml.Contains("Trial endpoints are withheld", StringComparison.Ordinal) || exportedHtml.Contains("Distance: ", StringComparison.Ordinal)
            || !exportedScope.Contains(TrialMeasurementWording.ProtocolLabelDoesNotSupply, StringComparison.Ordinal)
            || exportedScope.Contains("D3", StringComparison.Ordinal) || exportedHtml.Contains("D3", StringComparison.Ordinal)
            || exportedScope.Contains("measurement contract", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Export presented raw-run distance as a biological trial measurement.");
        Pass("mismatched biological window withheld trial endpoints in the readout and export");

        PlannedExposureInput.Text = "60.000";
        ReleaseConfirmedInput.IsChecked = true;
        WindowConfirmedInput.IsChecked = true;
        AnnotationReasonInput.Text = "Synthetic 60 s probe plan";
        SaveProtocol(this, new RoutedEventArgs());
        var sixty = store.LatestProtocol(row.Asset.Id)!;
        if (Math.Abs(sixty.RequestedExposureSeconds - 60) > 1e-9 || sixty.ReleaseFrame != 2)
            throw new InvalidOperationException("The 60 s plan moved the release anchor.");
        var videoB = Path.Combine(directory, "synthetic-recording-b.avi");
        var videoC = Path.Combine(directory, "synthetic-recording-c.avi");
        WriteSyntheticRecording(videoB, 90);
        WriteSyntheticRecording(videoC, 140);
        var importedB = _model.Workspace.Import(videoB);
        var importedC = _model.Workspace.Import(videoC);
        var idA = row.Asset.Id;
        _model.Reload();
        row = _model.Rows.Single(item => item.Asset.Id == idA);
        var boundB = await PrepareReviewedRecordingAsync(store, importedB.Id, TrialMode.Probe);
        row = _model.Rows.Single(item => item.Asset.Id == idA);
        await ShowExactFrameAndWaitAsync(2);
        AnnotationReasonInput.Text = "Synthetic water entry on recording B";
        ReviewerInput.Text = "Synthetic reviewer";
        RecordWaterEntry(this, new RoutedEventArgs());
        if (store.EffectiveBiologicalEvents(boundB.Asset.Id).All(item => item.Kind != BiologicalEventKind.WaterEntry || item.Frame != 2))
            throw new InvalidOperationException("Recording B did not keep its own water entry. " + _model.Status);
        UseDisplayedFrameAsRelease(this, new RoutedEventArgs());
        PlannedExposureInput.Text = "30.000";
        ReleaseConfirmedInput.IsChecked = true;
        WindowConfirmedInput.IsChecked = true;
        AnnotationReasonInput.Text = "Synthetic 30 s probe plan";
        SaveProtocol(this, new RoutedEventArgs());
        boundB = _model.Rows.Single(item => item.Asset.Id == importedB.Id);
        var savedProbeB = store.LatestProtocol(boundB.Asset.Id)!;
        if (Math.Abs(savedProbeB.RequestedExposureSeconds - 30) > 1e-9 || savedProbeB.ReleaseFrame != 2)
            throw new InvalidOperationException("Recording B did not store its own 30 s release.");
        await ShowExactFrameAndWaitAsync(11);
        AnnotationReasonInput.Text = "Synthetic duration-only revision on recording B";
        SaveProtocol(this, new RoutedEventArgs());
        var soughtB = store.LatestProtocol(boundB.Asset.Id)!;
        if (soughtB.ReleaseFrame != 2 || Math.Abs(soughtB.RequestedExposureSeconds - 30) > 1e-9 || Math.Abs(FrameSlider.Value - 11) > 1e-6)
            throw new InvalidOperationException("Recording B adopted the viewer frame as its release.");
        Pass("recording B kept its release anchor after seeking");

        await SelectRecordingAsync(row);
        if (Math.Abs(double.Parse(PlannedExposureInput.Text, CultureInfo.InvariantCulture) - 60) > 1e-9 || ReleaseConfirmedInput.IsChecked != true)
            throw new InvalidOperationException("Selecting A restored another recording's draft: " + PlannedExposureInput.Text);
        if (!ReleaseAnchorReadout.Text.Contains("frame 2", StringComparison.Ordinal) || Math.Abs(FrameSlider.Value - 2) < 1e-6)
            throw new InvalidOperationException("Selecting A adopted the viewer frame as the release. " + ReleaseAnchorReadout.Text + " slider " + FrameSlider.Value.ToString(CultureInfo.InvariantCulture));
        var reopenedPlans = new StudyStore(database);
        if (reopenedPlans.LatestProtocol(idA)?.ReleaseFrame != 2 || reopenedPlans.LatestProtocol(boundB.Asset.Id)?.ReleaseFrame != 2)
            throw new InvalidOperationException("Reopened plans did not keep each recording's release anchor.");
        Pass("reopened plans kept each release anchor");
        await SelectRecordingAsync(boundB);
        if (Math.Abs(double.Parse(PlannedExposureInput.Text, CultureInfo.InvariantCulture) - 30) > 1e-9)
            throw new InvalidOperationException("Selecting B kept A's 60 s draft: " + PlannedExposureInput.Text);
        var boundC = _model.Rows.Single(item => item.Asset.Id == importedC.Id);
        await SelectRecordingAsync(boundC);
        if (PlannedExposureInput.Text.Length != 0 || ReleaseConfirmedInput.IsChecked == true || WindowConfirmedInput.IsChecked == true)
            throw new InvalidOperationException("A recording with no plan kept another draft.");
        AnnotationReasonInput.Text = "Synthetic save without a plan";
        ReviewerInput.Text = "Synthetic reviewer";
        SaveProtocol(this, new RoutedEventArgs());
        if (store.LatestProtocol(boundC.Asset.Id) is not null)
            throw new InvalidOperationException("Saving the empty draft created a protocol for the unplanned recording.");
        await SelectRecordingAsync(boundB);
        await ShowExactFrameAndWaitAsync(2);
        var revisionBefore = store.LatestProtocol(boundB.Asset.Id)!.Revision;
        var draftBefore = PlannedExposureInput.Text;
        AnnotationReasonInput.Text = "Synthetic save without changing the draft";
        SaveProtocol(this, new RoutedEventArgs());
        var savedB = store.LatestProtocol(boundB.Asset.Id)!;
        var savedA = store.LatestProtocol(idA)!;
        if (Math.Abs(double.Parse(draftBefore, CultureInfo.InvariantCulture) - 30) > 1e-9 || Math.Abs(savedB.RequestedExposureSeconds - 30) > 1e-9 || savedB.Revision != revisionBefore + 1)
            throw new InvalidOperationException("Saving B without edits replaced its 30 s plan.");
        if (Math.Abs(savedA.RequestedExposureSeconds - 60) > 1e-9 || savedA.ReleaseFrame != 2 || savedB.ReleaseFrame != 2)
            throw new InvalidOperationException("Saving B replaced A's plan or moved a release anchor.");
        if (!CollectionCompatibilityReadout.Text.Contains("Unequal probe durations", StringComparison.Ordinal)
            || !CollectionCompatibilityReadout.Text.Contains("60.000", StringComparison.Ordinal)
            || !CollectionCompatibilityReadout.Text.Contains("30.000", StringComparison.Ordinal)
            || !CollectionCompatibilityReadout.Text.Contains("No statistical comparison is performed.", StringComparison.Ordinal)
            || CollectionCompatibilityReadout.Text.Contains("p-value", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Collection compatibility did not call the unequal-probe hold. " + CollectionCompatibilityReadout.Text);
        Pass("per-recording drafts and called unequal-probe compatibility");

        await SelectRecordingAsync(boundC);
        boundC.Phase = TrialMode.Training;
        CommitTrialMetadata(boundC);
        PlannedExposureInput.Text = "45.000";
        ReleaseConfirmedInput.IsChecked = false;
        WindowConfirmedInput.IsChecked = true;
        AnnotationReasonInput.Text = "Synthetic training plan kept separate from the probes";
        ReviewerInput.Text = "Synthetic reviewer";
        SaveProtocol(this, new RoutedEventArgs());
        if (store.LatestProtocol(boundC.Asset.Id)?.Phase != TrialMode.Training)
            throw new InvalidOperationException("The training plan was not stored on its own recording. " + _model.Status);
        if (!CollectionCompatibilityReadout.Text.Contains("Mixed phases: Probe, Training.", StringComparison.Ordinal)
            || !CollectionCompatibilityReadout.Text.Contains("No statistical comparison is performed.", StringComparison.Ordinal))
            throw new InvalidOperationException("Collection compatibility did not call the mixed-phase hold. " + CollectionCompatibilityReadout.Text);
        Pass("collection compatibility called the mixed-phase hold");

        await SelectRecordingAsync(boundB);
        var reviewedB = store.LatestRun(boundB.Asset.Id)!;
        store.RecordReview(reviewedB, true);
        boundB.Reviewed = store.Reviewed(reviewedB.Id);
        boundB.Refresh();
        if (!ReferenceEquals(TrialGrid.SelectedItem, boundB) || boundB.Status != "Reviewed")
            throw new InvalidOperationException("The bound row was not reviewed before the phase edit: " + boundB.Status);
        boundB.Phase = TrialMode.Training;
        CommitTrialMetadata(boundB);
        if (boundB.Reviewed || boundB.Status != "Review stale" || !ReferenceEquals(TrialGrid.SelectedItem, boundB))
            throw new InvalidOperationException("Phase edit left the bound row approved: " + boundB.Status + " / " + boundB.Reviewed);
        if (!ProtocolReadout.Text.Contains("Stored window phase differs from the recording's current phase.", StringComparison.Ordinal))
            throw new InvalidOperationException("Phase edit left the protocol outcome stale. " + ProtocolReadout.Text);
        if (!CollectionCompatibilityReadout.Text.Contains("Stored window phase differs from the recording's current phase.", StringComparison.Ordinal))
            throw new InvalidOperationException("Collection readout missed the phase mismatch. " + CollectionCompatibilityReadout.Text);
        Pass("phase edit refreshed the bound row to review stale and phase mismatch");

        await SelectRecordingAsync(row);
        await AssertRecordingSwitchBlocksStalePixelsAsync(store, boundB);
        Pass("recording-switch decode rejected edits against the previous recording's pixels");

        ArrangeWorkspace(1440, 960);
        var viewport = ViewerViewport;
        var pixelWidth = Math.Max(1, (int)Math.Round(viewport.ActualWidth * 144d / 96d));
        var pixelHeight = Math.Max(1, (int)Math.Round(viewport.ActualHeight * 144d / 96d));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(viewport);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        if (pixel.All(value => value == 0)) throw new InvalidOperationException("144 DPI viewer render is empty.");
        var elevated = Path.Combine(directory, "geometry-144dpi.png");
        SaveBitmap(bitmap, elevated);
        var placed = DisplayToVideo.Place(viewport.ActualWidth, viewport.ActualHeight, meta.Width, meta.Height);
        var dipX = placed.OffsetX + placed.Width * 0.5;
        var dipY = placed.OffsetY + placed.Height * 0.5;
        var physicalX = dipX * 144d / 96d;
        var physicalY = dipY * 144d / 96d;
        var at144 = DisplayToVideo.MapDevicePixels(physicalX, physicalY, viewport.ActualWidth * 144d / 96d, viewport.ActualHeight * 144d / 96d, 144, meta.Width, meta.Height)
            ?? throw new InvalidOperationException("144 DPI interior pixel missed the source.");
        var atDip = DisplayToVideo.MapDips(dipX, dipY, viewport.ActualWidth, viewport.ActualHeight, meta.Width, meta.Height)
            ?? throw new InvalidOperationException("DIP interior pixel missed the source.");
        if (Math.Abs(at144.X - atDip.X) > 1e-6 || Math.Abs(at144.Y - atDip.Y) > 1e-6)
            throw new InvalidOperationException("144 DPI device pixels disagreed with DIPs.");
        var at192 = DisplayToVideo.MapDevicePixels(physicalX, physicalY, viewport.ActualWidth * 192d / 96d, viewport.ActualHeight * 192d / 96d, 192, meta.Width, meta.Height);
        if (at192 is not null && Math.Abs(at192.Value.X - at144.X) < 1e-6 && Math.Abs(at192.Value.Y - at144.Y) < 1e-6)
            throw new InvalidOperationException("The same physical pixel mapped identically at 144 and 192 DPI.");
        Pass(string.Create(CultureInfo.InvariantCulture, $"dpi144 source={at144.X:F3},{at144.Y:F3} pixel={pixel[0]:X2}{pixel[1]:X2}{pixel[2]:X2}{pixel[3]:X2}"));
        Pass(at192 is null
            ? "dpi192 same physical pixel is outside the source image"
            : string.Create(CultureInfo.InvariantCulture, $"dpi192 source={at192.Value.X:F3},{at192.Value.Y:F3}"));
        if (JsonDefaults.Serialize(store.LatestRun(idA)!) != immutableRun)
            throw new InvalidOperationException("The stored raw run changed during geometry, correction, event, or protocol edits.");
        foreach (var shot in new[] { wideShot, narrowShot, elevated })
            if (!File.Exists(shot) || new FileInfo(shot).Length < 1000) throw new InvalidOperationException("Screenshot missing: " + shot);
        Pass("raw run bytes unchanged; screenshots written beside the assertion log");

        void ClickSource(double sourceX, double sourceY)
        {
            var dip = SourceDip(sourceX, sourceY);
            if (_editMode != "pool") { _editMode = "pool"; _poolCorner = null; }
            ApplyViewportPoint(dip.X, dip.Y);
            if (_model.Status == "That click is outside the source image.")
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Source {sourceX:F1},{sourceY:F1} landed in the letterbox."));
        }

        System.Windows.Point SourceDip(double sourceX, double sourceY)
        {
            var current = DisplayToVideo.Place(ViewerViewport.ActualWidth, ViewerViewport.ActualHeight, meta.Width, meta.Height);
            return new System.Windows.Point(current.OffsetX + sourceX * current.Scale, current.OffsetY + sourceY * current.Scale);
        }
    }

    private async Task SelectRecordingAsync(TrialRow target)
    {
        _loading = true;
        try
        {
            TrialGrid.SelectedItem = target;
            _model.Selected = target;
        }
        finally { _loading = false; }
        await ShowSelectedAsync();
    }

    private async Task ShowExactFrameAndWaitAsync(int frame)
    {
        ShowExactFrame(frame);
        await WaitForDisplayedFrameAsync(frame);
    }

    private async Task WaitForDisplayedFrameAsync(int frame)
    {
        var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
        var deadline = Environment.TickCount64 + 10000;
        while (!DisplayedFrameMatches(row, frame))
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"Displayed frame did not become {frame} for {row.Asset.Filename}. {_model.Status}");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(20);
        }
    }

    private async Task<TrialRow> PrepareReviewedRecordingAsync(StudyStore store, string id, TrialMode phase)
    {
        var target = _model.Rows.Single(item => item.Asset.Id == id);
        var asset = target.Asset with { Phase = phase, Settings = ArenaGeometry.Confirm(target.Asset.Settings) };
        store.SaveVideo(asset);
        target.Replace(asset);
        await SelectRecordingAsync(target);
        var points = Enumerable.Range(0, asset.Metadata.FrameCount)
            .Select(frame => new TrackPoint(frame, frame / asset.Metadata.Fps, 80, 60, PointStatus.Tracked, 1))
            .ToList();
        var analyzed = new AnalysisRun
        {
            VideoId = asset.Id,
            SourceSha256 = asset.SourceSha256,
            Metadata = asset.Metadata,
            Settings = asset.Settings,
            NominalTimingVerified = true,
            Points = points,
            Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings)
        };
        store.SaveRun(analyzed);
        store.RecordReview(analyzed, true);
        _model.Reload();
        target = _model.Rows.Single(item => item.Asset.Id == id);
        await SelectRecordingAsync(target);
        if (!store.Reviewed(analyzed.Id) || target.Status != "Reviewed")
            throw new InvalidOperationException("Prepared recording was not reviewed: " + target.Status);
        return target;
    }

    private async Task AssertDecodeRaceBlocksEditsAsync(StudyStore store, TrialRow row, int requestedFrame)
    {
        var eventsBefore = store.EffectiveBiologicalEvents(row.Asset.Id).Count;
        var correctionsBefore = store.Corrections(_current?.Id ?? row.Run?.Id ?? throw new InvalidOperationException("No run is available for the decode race.")).Count;
        var protocolsBefore = store.ProtocolHistory(row.Asset.Id).Count;
        var previousImage = FrameImage.Source ?? throw new InvalidOperationException("The race started without a decoded frame.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _decodeGate = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        ShowExactFrame(requestedFrame);
        if (await Task.WhenAny(entered.Task, Task.Delay(5000)) != entered.Task)
            throw new TimeoutException("The delayed decoder did not start.");
        if (!ReferenceEquals(FrameImage.Source, previousImage))
            throw new InvalidOperationException("The displayed pixels changed before the requested frame finished decoding.");
        if (MountingButton.IsEnabled || CorrectPointButton.IsEnabled)
            throw new InvalidOperationException("Annotation or correction stayed enabled during decode.");
        try
        {
            CurrentExactFrame();
            throw new InvalidOperationException("The requested slider frame was accepted while the previous pixels were displayed.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("finishes decoding", StringComparison.Ordinal))
        {
        }
        if (UseDisplayedReleaseButton.IsEnabled) throw new InvalidOperationException("Displayed-frame release stayed enabled during decode.");
        var anchorDuringDecode = ReleaseAnchorReadout.Text;
        UseDisplayedFrameAsRelease(this, new RoutedEventArgs());
        if (ReleaseAnchorReadout.Text != anchorDuringDecode || !_model.Status.Contains("finishes decoding", StringComparison.Ordinal))
            throw new InvalidOperationException("A decode race accepted the viewer frame as the release. " + _model.Status);
        ReleaseConfirmedInput.IsChecked = true;
        SaveProtocol(this, new RoutedEventArgs());
        RecordMounting(this, new RoutedEventArgs());
        try
        {
            CorrectFrame(1, 1);
            throw new InvalidOperationException("A correction was stored against the previous pixels.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("finishes decoding", StringComparison.Ordinal))
        {
        }
        if (!_model.Status.Contains("finishes decoding", StringComparison.Ordinal))
            throw new InvalidOperationException("The annotation was not rejected during decode. " + _model.Status);
        if (store.EffectiveBiologicalEvents(row.Asset.Id).Count != eventsBefore || store.Corrections(row.Run?.Id ?? _current!.Id).Count != correctionsBefore || store.ProtocolHistory(row.Asset.Id).Count != protocolsBefore)
            throw new InvalidOperationException("A blocked edit was stored during the decode race.");
        release.TrySetResult();
        await WaitForDisplayedFrameAsync(requestedFrame);
        ReleaseConfirmedInput.IsChecked = false;
    }

    private async Task AssertRecordingSwitchBlocksStalePixelsAsync(StudyStore store, TrialRow other)
    {
        var current = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
        await WaitForDisplayedFrameAsync((int)Math.Round(FrameSlider.Value));
        var previousImage = FrameImage.Source ?? throw new InvalidOperationException("Recording switch started without pixels.");
        var eventsBefore = store.EffectiveBiologicalEvents(current.Asset.Id).Count;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _decodeGate = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var requested = Math.Max(0, (int)FrameSlider.Value - 1);
        ShowExactFrame(requested);
        if (await Task.WhenAny(entered.Task, Task.Delay(5000)) != entered.Task)
            throw new TimeoutException("The recording-switch decoder did not start.");
        var pending = SelectRecordingAsync(other);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!ReferenceEquals(FrameImage.Source, previousImage))
            throw new InvalidOperationException("Selecting another recording replaced the pixels before its decode finished.");
        RecordMounting(this, new RoutedEventArgs());
        if (store.EffectiveBiologicalEvents(current.Asset.Id).Count != eventsBefore)
            throw new InvalidOperationException("An annotation was stored on the previous recording during the switch.");
        if (!_model.Status.Contains("finishes decoding", StringComparison.Ordinal) && !_model.Status.Contains("Select a recording", StringComparison.Ordinal))
            throw new InvalidOperationException("The switch did not reject the annotation. " + _model.Status);
        release.TrySetResult();
        await pending;
        await WaitForDisplayedFrameAsync((int)Math.Round(FrameSlider.Value));
        if (_shown is null || _shown.VideoId != other.Asset.Id || _shown.SourceSha256 != other.Asset.SourceSha256)
            throw new InvalidOperationException("The completed decode published the previous recording.");
    }

    private void ArrangeWorkspace(double width, double height)
    {
        RootGrid.Measure(new System.Windows.Size(width, height));
        RootGrid.Arrange(new Rect(0, 0, width, height));
        RootGrid.UpdateLayout();
    }

    private static void WriteSyntheticRecording(string path, byte tint = 40)
    {
        using var writer = new OpenCvSharp.VideoWriter(path, OpenCvSharp.FourCC.MJPG, 10, new OpenCvSharp.Size(160, 120));
        if (!writer.IsOpened()) throw new InvalidOperationException("The synthetic recording could not be created.");
        for (var frame = 0; frame < 20; frame++)
        {
            using var image = new OpenCvSharp.Mat(120, 160, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(tint, 90, 140));
            OpenCvSharp.Cv2.Circle(image, new OpenCvSharp.Point(24 + frame * 5, 60), 8, new OpenCvSharp.Scalar(230, 230, 230), -1);
            writer.Write(image);
        }
    }

    private static void SaveElementPng(FrameworkElement element, string path, int pixelWidth, int pixelHeight, double dpi)
    {
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(element);
        SaveBitmap(bitmap, path);
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
