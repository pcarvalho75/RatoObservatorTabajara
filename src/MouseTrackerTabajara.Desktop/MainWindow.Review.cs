using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public partial class MainWindow
{
    public string ReviewSmokeReport { get; private set; } = "";

    private void ApplyBlindChrome()
    {
        foreach (var column in TrialGrid.Columns.Where(column => column.Header is "Animal" or "ID confirmed" or "Group"))
            column.Visibility = _model.Blind ? Visibility.Collapsed : Visibility.Visible;
        ImportButton.IsEnabled = !_model.Blind && _model.NotBusy;
        if (BlindPixelNote is not null) BlindPixelNote.Visibility = _model.Blind ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(TrialGrid, _model.Blind ? "Blinded recording inventory" : "Recording inventory");
        NameInventoryRows();
    }

    private void NameInventoryRows()
    {
        for (var i = 0; i < TrialGrid.Items.Count; i++)
            if (TrialGrid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow gridRow && gridRow.Item is TrialRow row)
                AutomationProperties.SetName(gridRow, row.DisplayName);
    }

    private void ScrubBlindedReadouts()
    {
        if (!_model.Blind) return;
        var secrets = _model.StudySecrets();
        foreach (var block in new[] { EventReadout, ProtocolReadout, MetricsReadout, ContractLabel, ViewerHint, InclusionReadout, ReleaseAnchorReadout, CollectionCompatibilityReadout, SelectedTitle })
        {
            if (block is null || string.IsNullOrEmpty(block.Text)) continue;
            block.Text = BlindIdentity.Scrub(block.Text, secrets);
        }
        ScrubBlindedSurfaces(RootGrid, secrets);
    }

    private static void ScrubBlindedSurfaces(DependencyObject root, BlindSecrets secrets)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element)
            {
                if (element.ToolTip is string tip)
                {
                    var projected = BlindIdentity.PresentSurface(tip, secrets);
                    if (!string.Equals(projected, tip, StringComparison.Ordinal)) element.ToolTip = projected;
                }
                var name = AutomationProperties.GetName(element);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var projected = BlindIdentity.PresentSurface(name, secrets);
                    if (!string.Equals(projected, name, StringComparison.Ordinal)) AutomationProperties.SetName(element, projected);
                }
            }
            ScrubBlindedSurfaces(child, secrets);
        }
    }

    private void ShowInclusion(InclusionDecision decision)
    {
        var reason = _model.Blind ? BlindIdentity.ProjectAuthored(decision.Reason, _model.StudySecrets()) : decision.Reason;
        InclusionReadout.Text = decision.Included
            ? "Included. " + reason + " Inclusion does not approve accuracy or change the planned window."
            : "Excluded. " + reason + " The recording stays in the inventory. The planned window is unchanged.";
        ScrubBlindedReadouts();
    }

    private void RefreshInclusion()
    {
        if (InclusionReadout is null) return;
        if (_model.Selected is not { } row)
        {
            InclusionReadout.Text = "Inclusion is separate from tracking review and from measurement eligibility.";
            return;
        }
        var decision = _model.Workspace.Store.LatestInclusion(row.Asset.Id);
        if (decision is null)
        {
            InclusionReadout.Text = row.Asset.Included
                ? "Inclusion is separate from tracking review and from measurement eligibility."
                : "Excluded. " + (_model.Blind ? BlindIdentity.ReasonWithheld : row.Asset.ExclusionReason) + " The recording stays in the inventory. The planned window is unchanged.";
            ScrubBlindedReadouts();
            return;
        }
        ShowInclusion(decision);
    }

    private void AttestInspection(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
            var frame = CurrentExactFrame();
            var displayed = _displayedInspection ?? throw new InvalidOperationException("The source frame is not displayed.");
            if (displayed.VideoId != row.Asset.Id || displayed.SourceSha256 != row.Asset.SourceSha256 || displayed.Frame != frame || displayed.Generation != _frameGeneration)
                throw new InvalidOperationException("The displayed source frame does not match this inspection.");
            var moment = _navigationMoment ?? throw new InvalidOperationException("Navigate to a review moment before attesting an inspection.");
            if (moment.StartFrame != frame)
                throw new InvalidOperationException("The displayed frame is not the review moment's source frame.");
            var saved = _model.Workspace.Store.RecordInspection(row.Asset, new InspectionAttestation
            {
                RunId = displayed.Material.RunId,
                SourceSha256 = displayed.Material.SourceSha256,
                CorrectionFingerprint = displayed.Material.CorrectionFingerprint,
                ReviewFingerprint = displayed.Material.ReviewFingerprint,
                DisplayedFrame = frame,
                QueueStartFrame = moment.StartFrame,
                QueueEndFrame = moment.EndFrame,
                Reason = RequiredReason(),
                Reviewer = RequiredReviewer()
            });
            if (saved.StartFrame != frame || saved.EndFrame != frame)
                throw new InvalidOperationException("The inspection claimed a frame that was not displayed.");
            _model.Status = string.Create(CultureInfo.InvariantCulture, $"Inspected source frame {frame}. The rest of the review moment was not marked inspected.");
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }

    private void ExcludeRecording(object sender, RoutedEventArgs e) => SaveInclusion(false);
    private void IncludeRecording(object sender, RoutedEventArgs e) => SaveInclusion(true);

    private void SaveInclusion(bool included)
    {
        try
        {
            var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
            var store = _model.Workspace.Store;
            var before = row.Asset.Settings;
            var protocol = store.LatestProtocol(row.Asset.Id);
            var metrics = _current is null ? null : JsonDefaults.Serialize(_current.Metrics);
            var saved = store.SaveInclusion(row.Asset, included, RequiredReason(), RequiredReviewer());
            var after = store.Videos().Single(video => video.Id == row.Asset.Id);
            if (after.Settings.StartSeconds != before.StartSeconds || after.Settings.EndSeconds != before.EndSeconds)
                throw new InvalidOperationException("The inclusion decision changed the detector window.");
            if (JsonDefaults.Serialize(store.LatestProtocol(row.Asset.Id)) != JsonDefaults.Serialize(protocol))
                throw new InvalidOperationException("The inclusion decision changed the planned window.");
            if (metrics is not null && JsonDefaults.Serialize(_current!.Metrics) != metrics)
                throw new InvalidOperationException("The inclusion decision changed measurement eligibility.");
            if (!store.Videos().Any(video => video.Id == row.Asset.Id))
                throw new InvalidOperationException("The recording left the inventory.");
            row.Replace(after);
            _model.SyncReview(row);
            ShowInclusion(saved);
            _model.Status = included
                ? "Inclusion recorded. Coverage was not treated as accuracy, and the planned window stayed in place."
                : "Exclusion recorded. The recording stays in the inventory. The planned window and measurement eligibility are unchanged.";
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }

    private void ExportBlindReview(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_model.Selected is null) throw new InvalidOperationException("Select a recording.");
            var dialog = new OpenFolderDialog { Title = "Choose blinded review export folder" };
            if (dialog.ShowDialog(this) != true) return;
            WriteBlindReview(dialog.FolderName);
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }

    private void WriteBlindReview(string folder)
    {
        var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
        var export = BuildBlindExport(row);
        ReviewExportWriter.Save(folder, export);
        var secrets = _model.StudySecrets();
        foreach (var name in new[] { "blind-review.json", "blind-review.txt" })
        {
            var path = Path.Combine(folder, name);
            var original = File.ReadAllText(path);
            var scrubbed = BlindIdentity.Scrub(original, secrets);
            if (!string.Equals(scrubbed, original, StringComparison.Ordinal)) File.WriteAllText(path, scrubbed);
        }
        _model.Status = "Blinded review exported. Software labels only; the video picture is not redacted.";
    }

    private BlindedReviewExport BuildBlindExport(TrialRow row)
    {
        var store = _model.Workspace.Store;
        var asset = row.Asset;
        var ranking = _current is null ? null : store.LatestRanking(_current.Id);
        var inclusion = store.LatestInclusion(asset.Id);
        var reviewed = _current is not null && store.Reviewed(_current.Id);
        var secrets = _model.StudySecrets();
        return new()
        {
            RecordingAlias = BlindIdentity.RecordingAlias(asset),
            AnimalAlias = BlindIdentity.AnimalAlias(asset),
            Phase = asset.Phase.ToString(),
            Day = asset.Day,
            Trial = asset.Trial,
            Included = inclusion?.Included ?? asset.Included,
            InclusionReason = BlindIdentity.ProjectAuthored(inclusion?.Reason ?? "", secrets),
            ReviewConfirmed = reviewed,
            ReviewStale = _current is not null && store.ReviewStale(_current.Id),
            RankingVersion = ranking?.Version ?? "",
            Moments = ranking?.Moments.Select(moment => new BlindedMoment(moment.StartFrame, moment.EndFrame, BlindIdentity.Scrub(moment.Reason, secrets), moment.RandomAudit)).ToList() ?? [],
            Inspections = (_current is null ? [] : store.CurrentInspections(asset.Id, _current.Id)).Select(item => new BlindedInspection(item.StartFrame, item.EndFrame, BlindIdentity.ProjectAuthored(item.Reason, secrets), BlindIdentity.ProjectAuthored(item.Reviewer, secrets))).ToList(),
            PixelNote = BlindIdentity.PixelNote,
            MeasurementNote = TrialMeasurementWording.ProtocolLabelDoesNotSupply
        };
    }

    public async Task RunReviewSmokeAsync(string directory)
    {
        var lines = new List<string>();
        try { await RunReviewSmokeCore(directory, lines); }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex.GetType().Name + ": " + ex.Message);
            throw;
        }
        finally
        {
            ReviewSmokeReport = string.Join(Environment.NewLine, lines);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "review-smoke.txt"), ReviewSmokeReport);
        }
    }

    private async Task RunReviewSmokeCore(string directory, List<string> lines)
    {
        void Pass(string text) => lines.Add("PASS " + text);
        ArrangeWorkspace(1440, 960);
        var alphaPath = Path.Combine(directory, "SENTINEL_FILE_ALPHA.avi");
        var betaPath = Path.Combine(directory, "SENTINEL_FILE_BETA.avi");
        WriteSyntheticRecording(alphaPath, 30);
        WriteSyntheticRecording(betaPath, 70);
        var importedAlpha = _model.Workspace.Import(alphaPath);
        var importedBeta = _model.Workspace.Import(betaPath);
        var store = _model.Workspace.Store;
        var alpha = store.Videos().Single(video => video.Id == importedAlpha.Id);
        var beta = store.Videos().Single(video => video.Id == importedBeta.Id);
        const string animal = "SENTINEL_ANIMAL_ALPHA";
        const string group = "SENTINEL_GROUP_ALPHA";
        const string animalBeta = "SENTINEL_ANIMAL_BETA";
        const string groupBeta = "SENTINEL_GROUP_BETA";
        alpha = alpha with { AnimalId = animal, GroupName = group, AnimalIdConfirmed = true, Phase = TrialMode.Probe, Settings = ArenaGeometry.Confirm(alpha.Settings) };
        beta = beta with { AnimalId = animalBeta, GroupName = groupBeta, AnimalIdConfirmed = true, Phase = TrialMode.Probe, Settings = ArenaGeometry.Confirm(beta.Settings) };
        store.SaveVideo(alpha);
        store.SaveVideo(beta);
        _model.Reload();
        var row = _model.Rows.Single(item => item.Asset.Id == alpha.Id);
        await SelectRecordingAsync(row);
        var analyzed = SaveSyntheticRun(store, row.Asset, unknownFrame: 3);
        _model.Reload();
        row = _model.Rows.Single(item => item.Asset.Id == alpha.Id);
        await SelectRecordingAsync(row);
        if (_current is null) throw new InvalidOperationException("The synthetic review run did not load.");
        var queue = MetricCalculator.ReviewQueue(_current);
        if (!queue.SequenceEqual(_model.ReviewMoments)) throw new InvalidOperationException("The review list is not the accepted consequence queue.");
        var ranking = store.LatestRanking(_current.Id) ?? throw new InvalidOperationException("Ranking evidence was not saved.");
        if (ranking.Version != ReviewRankingEvidence.Version || ranking.SeedMaterial != ReviewRankingEvidence.SeedMaterialOf(_current.Points) || !ranking.Moments.SequenceEqual(queue))
            throw new InvalidOperationException("Saved ranking evidence does not match the accepted queue.");
        var audits = queue.Where(moment => moment.RandomAudit).ToArray();
        if (audits.Length == 0 || queue.All(moment => moment.RandomAudit)) throw new InvalidOperationException("Critical moments removed the accepted-segment audits.");
        foreach (var audit in audits)
        {
            var segment = _current.Points.Where(point => point.Frame >= audit.StartFrame && point.Frame <= audit.EndFrame).ToArray();
            if (segment.Length != audit.EndFrame - audit.StartFrame + 1 || segment.Any(point => !point.Accepted))
                throw new InvalidOperationException("A random audit is not a fully accepted continuous segment.");
        }
        for (var i = 0; i < audits.Length; i++)
            for (var j = i + 1; j < audits.Length; j++)
                if (!(audits[i].EndFrame < audits[j].StartFrame || audits[j].EndFrame < audits[i].StartFrame))
                    throw new InvalidOperationException("Random audits overlap.");
        Pass("accepted queue, seed evidence, and fully accepted audits are connected");

        ReviewerInput.Text = "Synthetic reviewer";
        ArrangeWorkspace(1440, 960);
        ReviewMomentList.UpdateLayout();
        var critical = queue.First(moment => !moment.RandomAudit);
        var auditMoment = audits[0];
        var inspectionsBeforeNavigation = store.Inspections(row.Asset.Id).Count;
        await ClickReviewMomentAsync(critical);
        await ClickReviewMomentAsync(auditMoment);
        if (store.Inspections(row.Asset.Id).Count != inspectionsBeforeNavigation)
            throw new InvalidOperationException("Choosing a review moment recorded an inspection before the frame was attested.");
        var navigation = store.NavigationReceipts(row.Asset.Id);
        if (navigation.Count < 2 || navigation.Any(item => item.DisplayCompleted) || navigation[^1].RequestedFrame != auditMoment.StartFrame)
            throw new InvalidOperationException("Navigation did not keep a request receipt separate from inspection.");
        if (Math.Abs(FrameSlider.Value - auditMoment.StartFrame) > 1e-6)
            throw new InvalidOperationException("The audit click did not leave the slider on its source frame.");
        AnnotationReasonInput.Text = "Inspected the displayed source frame";
        AttestInspection(this, new RoutedEventArgs());
        var attested = store.CurrentInspections(row.Asset.Id, _current.Id);
        var attestedAudit = attested.LastOrDefault() ?? throw new InvalidOperationException("The displayed audit frame was not attested. " + _model.Status);
        if (attestedAudit.DisplayedFrame != auditMoment.StartFrame || attestedAudit.StartFrame != auditMoment.StartFrame || attestedAudit.EndFrame != auditMoment.StartFrame)
            throw new InvalidOperationException("The attestation did not bind the completed displayed frame.");
        if (auditMoment.EndFrame != auditMoment.StartFrame && attestedAudit.EndFrame == auditMoment.EndFrame)
            throw new InvalidOperationException("The attestation claimed the whole review moment.");
        await ClickReviewMomentAsync(critical);
        AttestInspection(this, new RoutedEventArgs());
        var both = store.CurrentInspections(row.Asset.Id, _current.Id);
        if (both.Count < 2 || both[^2].DisplayedFrame != auditMoment.StartFrame || both[^1].DisplayedFrame != critical.StartFrame)
            throw new InvalidOperationException("Both displayed moments were not attested. " + _model.Status);
        Pass("queue selection seeks the canonical source frame");

        ReviewerInput.Text = "Synthetic reviewer";
        await ShowExactFrameAndWaitAsync(2);
        AnnotationReasonInput.Text = "Synthetic water entry for the blinded review";
        RecordWaterEntry(this, new RoutedEventArgs());
        if (store.EffectiveBiologicalEvents(row.Asset.Id).All(item => item.Kind != BiologicalEventKind.WaterEntry || item.Frame != 2))
            throw new InvalidOperationException("Water entry was not stored on the review recording. " + _model.Status);
        UseDisplayedFrameAsRelease(this, new RoutedEventArgs());
        PlannedExposureInput.Text = "1.000";
        WindowConfirmedInput.IsChecked = true;
        AnnotationReasonInput.Text = "Synthetic planned exposure";
        var detectorStart = row.Asset.Settings.StartSeconds;
        var detectorEnd = row.Asset.Settings.EndSeconds;
        SaveProtocol(this, new RoutedEventArgs());
        if (store.LatestProtocol(row.Asset.Id)?.ReleaseFrame != 2)
            throw new InvalidOperationException("The explicit release was not stored. " + _model.Status);
        await ShowExactFrameAndWaitAsync(7);
        PlannedExposureInput.Text = "1.200";
        AnnotationReasonInput.Text = "Synthetic duration-only edit";
        SaveProtocol(this, new RoutedEventArgs());
        var anchored = store.LatestProtocol(row.Asset.Id) ?? throw new InvalidOperationException("The duration edit dropped the plan.");
        if (anchored.ReleaseFrame != 2 || Math.Abs(anchored.RequestedExposureSeconds - 1.2) > 1e-9 || Math.Abs(FrameSlider.Value - 7) > 1e-6)
            throw new InvalidOperationException("The duration edit moved the release to the viewer frame.");
        if (row.Asset.Settings.StartSeconds != detectorStart || row.Asset.Settings.EndSeconds != detectorEnd)
            throw new InvalidOperationException("The protocol edit changed the detector interval.");
        var betaRow = _model.Rows.Single(item => item.Asset.Id == beta.Id);
        await SelectRecordingAsync(betaRow);
        await SelectRecordingAsync(row);
        if (!ReleaseAnchorReadout.Text.Contains("frame 2", StringComparison.Ordinal))
            throw new InvalidOperationException("Returning to the recording lost its release anchor. " + ReleaseAnchorReadout.Text);
        var reopened = new StudyStore(store.DatabasePath);
        if (reopened.LatestProtocol(row.Asset.Id)?.ReleaseFrame != 2)
            throw new InvalidOperationException("The reopened plan lost the release anchor.");
        Pass("release anchor survived seeking, recording navigation, and reopening");

        var metricsBefore = JsonDefaults.Serialize(_current.Metrics);
        var protocolBefore = JsonDefaults.Serialize(store.LatestProtocol(row.Asset.Id));
        AnnotationReasonInput.Text = "Kept out of the endpoint set";
        ExcludeRecording(this, new RoutedEventArgs());
        row = _model.Rows.Single(item => item.Asset.Id == alpha.Id);
        if (row.Asset.Included || row.Inclusion != "Excluded" || !_model.Rows.Any(item => item.Asset.Id == alpha.Id))
            throw new InvalidOperationException("The excluded recording left the inventory. " + _model.Status);
        if (JsonDefaults.Serialize(_current.Metrics) != metricsBefore || JsonDefaults.Serialize(store.LatestProtocol(row.Asset.Id)) != protocolBefore)
            throw new InvalidOperationException("Exclusion changed measurements or the planned window.");
        if (!InclusionReadout.Text.Contains("Excluded", StringComparison.Ordinal) || !InclusionReadout.Text.Contains("Kept out of the endpoint set", StringComparison.Ordinal))
            throw new InvalidOperationException(InclusionReadout.Text);
        Pass("exclusion stays visible and does not change the window or measurements");

        BlindReview.IsChecked = true;
        if (!_model.Blind) throw new InvalidOperationException("Blind review did not turn on.");
        ArrangeWorkspace(1440, 960);
        TrialGrid.UpdateLayout();
        NameInventoryRows();
        var visible = CollectVisibleText(RootGrid);
        visible.Add(Title);
        var forbidden = new[] { animal, group, animalBeta, groupBeta, "SENTINEL_FILE_ALPHA", "SENTINEL_FILE_BETA", alpha.SourceSha256, beta.SourceSha256, alphaPath, betaPath };
        var leaked = forbidden.Where(secret => visible.Any(text => text.Contains(secret, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (leaked.Length > 0) throw new InvalidOperationException("Blinded review showed identifying text: " + string.Join(", ", leaked));
        var alias = BlindIdentity.RecordingAlias(row.Asset);
        if (!visible.Any(text => text.Contains(alias, StringComparison.Ordinal)) || !visible.Any(text => text.Contains("not redacted", StringComparison.Ordinal)))
            throw new InvalidOperationException("The blinded alias or the pixel limit is not visible.");
        if (TrialGrid.Columns.Any(column => column.Header is "Animal" or "Group" or "ID confirmed" && column.Visibility == Visibility.Visible))
            throw new InvalidOperationException("Identity columns stayed visible during blind review.");
        Pass("blinded tree shows the alias and hides source names, animals, groups, and hashes");

        var exportFolder = Path.Combine(directory, "blind-export");
        WriteBlindReview(exportFolder);
        var exported = File.ReadAllText(Path.Combine(exportFolder, "blind-review.json")) + File.ReadAllText(Path.Combine(exportFolder, "blind-review.txt"));
        if (forbidden.Any(secret => exported.Contains(secret, StringComparison.OrdinalIgnoreCase)) || exported.Contains("analysis.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The blinded review export contains identifying material.");
        if (exported.Contains(ranking.SeedMaterial, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The blinded review export contains the raw point fingerprint.");
        if (!exported.Contains(alias, StringComparison.Ordinal) || !exported.Contains("not redacted", StringComparison.Ordinal) || !exported.Contains(ranking.Version, StringComparison.Ordinal))
            throw new InvalidOperationException("The blinded review export dropped the alias, pixel limit, or ranking version.");
        if (!exported.Contains(ranking.Moments[0].StartFrame.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new InvalidOperationException("The blinded review export dropped the ranking frame range.");
        if (File.Exists(Path.Combine(exportFolder, "analysis.json"))) throw new InvalidOperationException("Blinded review wrote a raw decoder archive.");
        Pass("blinded review export is an allowlisted projection");

        alpha = store.Videos().Single(video => video.Id == alpha.Id);
        beta = store.Videos().Single(video => video.Id == beta.Id);
        alpha = alpha with { AnimalId = "A", GroupName = "KO", AnimalIdConfirmed = true, BlindedAlias = "KO1", BlindedAnimalAlias = "Animal KO1" };
        beta = beta with { AnimalId = "A", GroupName = "WT", AnimalIdConfirmed = true, BlindedAlias = "WT2", BlindedAnimalAlias = "Animal WT" };
        store.SaveVideo(alpha);
        store.SaveVideo(beta);
        _model.Reload();
        row = _model.Rows.Single(item => item.Asset.Id == alpha.Id);
        await SelectRecordingAsync(row);
        if (!_model.Blind) throw new InvalidOperationException("Blind review dropped while the short labels were saved.");
        var reopenedAliases = new StudyStore(store.DatabasePath);
        var reopenedAlpha = reopenedAliases.Videos().Single(video => video.Id == alpha.Id);
        var reopenedBeta = reopenedAliases.Videos().Single(video => video.Id == beta.Id);
        if (!string.Equals(reopenedAlpha.BlindedAlias, row.Asset.BlindedAlias, StringComparison.Ordinal) || !reopenedAlpha.BlindedAlias.StartsWith("Recording ", StringComparison.Ordinal))
            throw new InvalidOperationException("The recording alias was not a stored opaque alias after reopen.");
        if (!string.Equals(reopenedAlpha.BlindedAnimalAlias, reopenedBeta.BlindedAnimalAlias, StringComparison.Ordinal) || !reopenedAlpha.BlindedAnimalAlias.StartsWith("Animal ", StringComparison.Ordinal))
            throw new InvalidOperationException("The confirmed animal did not keep one stored alias across recordings.");
        if (reopenedAlpha.BlindedAlias.Contains("KO", StringComparison.OrdinalIgnoreCase) || reopenedAlpha.BlindedAnimalAlias.Contains("KO", StringComparison.OrdinalIgnoreCase) || reopenedBeta.BlindedAlias.Contains("WT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A stored alias still carries a short treatment or animal label.");
        AnnotationReasonInput.Text = "Held out KO animal A PBS alias KO1";
        ExcludeRecording(this, new RoutedEventArgs());
        await ShowExactFrameAndWaitAsync(2);
        AnnotationReasonInput.Text = "WT release for animal B and PBS";
        RecordWaterEntry(this, new RoutedEventArgs());
        AnnotationReasonInput.Text = "";
        InclusionReadout.ToolTip = "tooltip KO animal A PBS KO1";
        AutomationProperties.SetName(InclusionReadout, "accessible KO animal A PBS KO1");
        _model.Status = "Error for group KO animal A alias KO1 and PBS";
        ScrubBlindedReadouts();
        ArrangeWorkspace(1440, 960);
        TrialGrid.UpdateLayout();
        NameInventoryRows();
        var shortVisible = CollectVisibleText(RootGrid);
        shortVisible.Add(Title);
        foreach (var label in new[] { "KO", "WT", "PBS", "KO1", "WT2" })
        {
            if (shortVisible.Any(text => ContainsToken(text, label)))
                throw new InvalidOperationException("Blinded review showed short label " + label + ".");
        }
        if (!shortVisible.Any(text => text.Contains("not redacted", StringComparison.Ordinal)))
            throw new InvalidOperationException("The identifying-video caveat is not visible.");
        if (!shortVisible.Any(text => text.Contains(row.Asset.BlindedAlias, StringComparison.Ordinal)))
            throw new InvalidOperationException("The stored recording alias is not visible.");
        Pass("short treatment labels stay out of the blinded tree, errors, and aliases");

        var shortExport = Path.Combine(directory, "blind-export-short");
        WriteBlindReview(shortExport);
        var shortExported = File.ReadAllText(Path.Combine(shortExport, "blind-review.json")) + File.ReadAllText(Path.Combine(shortExport, "blind-review.txt"));
        foreach (var label in new[] { "KO", "WT", "PBS", "KO1", ranking.SeedMaterial })
            if (ContainsToken(shortExported, label) || shortExported.Contains(ranking.SeedMaterial, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The short-label export leaked " + label + ".");
        Pass("short-label export keeps frame ranges and omits raw fingerprints");

        var historyBefore = store.RankingHistory(_current.Id);
        if (historyBefore.Count < 1) throw new InvalidOperationException("Ranking history was not stored.");
        var priorMoments = historyBefore[0].Moments.ToList();
        var priorSeed = historyBefore[0].SeedMaterial;
        AnnotationReasonInput.Text = "Synthetic unknown position";
        MarkUnknown(this, new RoutedEventArgs());
        var historyAfter = store.RankingHistory(_current.Id);
        if (historyAfter.Count != historyBefore.Count + 1)
            throw new InvalidOperationException("The corrected ranking replaced the earlier evidence.");
        if (!historyAfter[0].Moments.SequenceEqual(priorMoments) || historyAfter[0].SeedMaterial != priorSeed)
            throw new InvalidOperationException("The earlier ranking moments were not preserved.");
        var currentRanking = store.LatestRanking(_current.Id) ?? throw new InvalidOperationException("The corrected ranking is not the current lookup.");
        if (currentRanking.Revision == historyAfter[0].Revision || !currentRanking.Moments.SequenceEqual(MetricCalculator.ReviewQueue(_current)))
            throw new InvalidOperationException("The current ranking lookup is not the corrected queue.");
        if (store.CurrentInspections(row.Asset.Id, _current.Id).Count != 0)
            throw new InvalidOperationException("An inspection of the previous correction revision still counts.");
        if (store.Inspections(row.Asset.Id).Count < 2)
            throw new InvalidOperationException("The earlier inspection history was deleted.");
        Pass("ranking history keeps the earlier moments and old inspections do not count");

        var liveMoment = _model.ReviewMoments.First();
        ArrangeWorkspace(1440, 960);
        ReviewMomentList.UpdateLayout();
        var inspectionsAtDelay = store.Inspections(row.Asset.Id).Count;
        var navigationAtDelay = store.NavigationReceipts(row.Asset.Id).Count;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _decodeGate = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var delayedButton = ReviewButton(liveMoment) ?? throw new InvalidOperationException("The corrected queue has no moment button.");
        delayedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (await Task.WhenAny(entered.Task, Task.Delay(5000)) != entered.Task)
            throw new TimeoutException("The delayed review decoder did not start.");
        AttestInspection(this, new RoutedEventArgs());
        if (store.Inspections(row.Asset.Id).Count != inspectionsAtDelay)
            throw new InvalidOperationException("A delayed decode recorded an inspection.");
        if (store.NavigationReceipts(row.Asset.Id).Count <= navigationAtDelay)
            throw new InvalidOperationException("Delayed navigation did not record a request.");
        var duringDelay = string.Join("\n", CollectVisibleText(RootGrid));
        if (ContainsToken(duringDelay, "KO") || ContainsToken(duringDelay, "PBS") || ContainsToken(duringDelay, "KO1"))
            throw new InvalidOperationException("The delayed-decode tree showed a short label.");
        release.TrySetResult();
        await WaitForDisplayedFrameAsync(liveMoment.StartFrame);
        var delayedFrame = liveMoment.StartFrame == 0 ? 1 : 0;
        _decodeGate = () => throw new InvalidOperationException("decoder failed for group KO animal A PBS KO1");
        ShowExactFrame(delayedFrame);
        var failedDeadline = Environment.TickCount64 + 5000;
        while (!_model.Status.Contains("rejected", StringComparison.OrdinalIgnoreCase) && !_model.Status.Contains("hidden", StringComparison.OrdinalIgnoreCase) && Environment.TickCount64 < failedDeadline)
            await Task.Delay(20);
        if (!_model.Status.Contains("rejected", StringComparison.OrdinalIgnoreCase) && !_model.Status.Contains("hidden", StringComparison.OrdinalIgnoreCase))
            throw new TimeoutException("The failed decode did not surface a blinded rejection. " + _model.Status);
        if (store.Inspections(row.Asset.Id).Count != inspectionsAtDelay)
            throw new InvalidOperationException("A failed decode recorded an inspection.");
        AttestInspection(this, new RoutedEventArgs());
        if (store.Inspections(row.Asset.Id).Count != inspectionsAtDelay)
            throw new InvalidOperationException("Attestation after a failed decode recorded an inspection.");
        var failedText = string.Join("\n", CollectVisibleText(RootGrid));
        if (ContainsToken(failedText, "KO") || ContainsToken(failedText, "PBS") || ContainsToken(failedText, "KO1"))
            throw new InvalidOperationException("The failed-decode error showed a short label. " + _model.Status);
        Pass("delayed and failed decode do not attest a frame or leak short labels");

        var betaRowForSwitch = _model.Rows.Single(item => item.Asset.Id == beta.Id);
        var switchInspections = store.Inspections(row.Asset.Id).Count;
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _decodeGate = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        ShowExactFrame(liveMoment.StartFrame);
        if (await Task.WhenAny(entered.Task, Task.Delay(5000)) != entered.Task)
            throw new TimeoutException("The recording-switch decoder did not start.");
        var pendingSwitch = SelectRecordingAsync(betaRowForSwitch);
        AttestInspection(this, new RoutedEventArgs());
        if (store.Inspections(row.Asset.Id).Count != switchInspections)
            throw new InvalidOperationException("Switching recordings attested the previous source.");
        release.TrySetResult();
        await pendingSwitch;
        await WaitForDisplayedFrameAsync((int)Math.Round(FrameSlider.Value));
        var foreign = store.LatestRun(row.Asset.Id) ?? throw new InvalidOperationException("The first recording has no stored run.");
        var foreignMaterial = store.InspectionMaterialFor(foreign.Id);
        var mismatched = new InspectionAttestation
        {
            RunId = foreign.Id,
            SourceSha256 = foreignMaterial.SourceSha256,
            CorrectionFingerprint = foreignMaterial.CorrectionFingerprint,
            ReviewFingerprint = foreignMaterial.ReviewFingerprint,
            DisplayedFrame = 0,
            QueueStartFrame = 0,
            QueueEndFrame = 0,
            Reason = "Wrong run",
            Reviewer = "Synthetic reviewer"
        };
        var wrongRun = AssertRejected(() => store.RecordInspection(betaRowForSwitch.Asset, mismatched));
        if (!wrongRun.Contains("does not belong", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(wrongRun);
        var stale = store.Inspections(row.Asset.Id).First();
        var staleAttempt = AssertRejected(() => store.RecordInspection(row.Asset, new InspectionAttestation
        {
            RunId = stale.RunId ?? "",
            SourceSha256 = stale.SourceSha256,
            CorrectionFingerprint = stale.CorrectionFingerprint,
            ReviewFingerprint = stale.ReviewFingerprint,
            DisplayedFrame = stale.DisplayedFrame,
            QueueStartFrame = stale.QueueStartFrame,
            QueueEndFrame = stale.QueueEndFrame,
            Reason = "Stale revision",
            Reviewer = "Synthetic reviewer"
        }));
        if (!staleAttempt.Contains("stale", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(staleAttempt);
        if (store.Inspections(beta.Id).Any(item => item.RunId == foreign.Id))
            throw new InvalidOperationException("A foreign run counted as this recording's inspection.");
        Pass("a recording switch does not attest the previous source");
        Pass("wrong-run and stale revision inspections are rejected");

        AnnotationReasonInput.Text = "";
        BlindReview.IsChecked = false;
        if (BlindReview.IsChecked != true || !_model.Blind || store.UnblindingHistory().Count != 0)
            throw new InvalidOperationException("Blank unblinding was accepted.");
        AnnotationReasonInput.Text = "Labels needed for a stated comparison";
        ReviewerInput.Text = "Synthetic reviewer";
        BlindReview.IsChecked = false;
        if (_model.Blind || store.UnblindingHistory().Single().Reason != "Labels needed for a stated comparison")
            throw new InvalidOperationException("Explicit unblinding was not stored. " + _model.Status);
        Pass("unblinding requires a stored reason");
    }

    private static string AssertRejected(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException ex) { return ex.Message; }
        catch (InvalidOperationException ex) { return ex.Message; }
        throw new InvalidOperationException("The inspection was stored.");
    }

    private static bool ContainsToken(string text, string token) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![A-Za-z0-9]){System.Text.RegularExpressions.Regex.Escape(token)}(?![A-Za-z0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static AnalysisRun SaveSyntheticRun(StudyStore store, VideoAsset asset, int unknownFrame)
    {
        var points = Enumerable.Range(0, asset.Metadata.FrameCount).Select(frame => frame == unknownFrame
            ? new TrackPoint(frame, frame / asset.Metadata.Fps, null, null, PointStatus.Missing, 0)
            : new TrackPoint(frame, frame / asset.Metadata.Fps, 40 + frame % 5, 60, PointStatus.Tracked, 1)).ToList();
        var run = new AnalysisRun
        {
            VideoId = asset.Id,
            SourceSha256 = asset.SourceSha256,
            Metadata = asset.Metadata,
            Settings = asset.Settings,
            NominalTimingVerified = true,
            Points = points,
            Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings)
        };
        store.SaveRun(run);
        return run;
    }

    private async Task ClickReviewMomentAsync(ReviewMoment moment)
    {
        ArrangeWorkspace(1440, 960);
        ReviewMomentList.UpdateLayout();
        var button = ReviewButton(moment) ?? throw new InvalidOperationException("The review moment has no button. " + moment.Reason);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Math.Abs(FrameSlider.Value - moment.StartFrame) > 1e-6)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Review click sought {FrameSlider.Value} instead of source frame {moment.StartFrame}."));
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private Button? ReviewButton(ReviewMoment moment)
    {
        for (var i = 0; i < ReviewMomentList.Items.Count; i++)
        {
            if (ReviewMomentList.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container) continue;
            var button = FindVisualChild<Button>(container);
            if (button?.DataContext is ReviewMoment candidate && candidate == moment) return button;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static List<string> CollectVisibleText(DependencyObject root)
    {
        var texts = new List<string>();
        void Walk(DependencyObject node)
        {
            switch (node)
            {
                case TextBlock block:
                    texts.Add(block.Text);
                    break;
                case ContentControl control when control.Content is string content:
                    texts.Add(content);
                    break;
                case HeaderedContentControl headered when headered.Header is string header:
                    texts.Add(header);
                    break;
            }
            if (node is FrameworkElement element)
            {
                if (element.ToolTip is string tip) texts.Add(tip);
                var name = AutomationProperties.GetName(element);
                if (!string.IsNullOrWhiteSpace(name)) texts.Add(name);
            }
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }
}
