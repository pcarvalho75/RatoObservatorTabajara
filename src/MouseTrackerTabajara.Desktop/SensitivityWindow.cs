using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public sealed class SensitivityWindow : Window
{
    private readonly string _database;
    private readonly string _workspace;
    private readonly bool _showViewers;
    private readonly CheckBox _blind = new() { Content = "Blind review" };
    private readonly TextBox _unblindReviewer = new() { Width = 140, ToolTip = "Unblinding reviewer" };
    private readonly TextBox _unblindReason = new() { Width = 220, ToolTip = "Reason for unblinding this view" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _identity = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _progressText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 8, Maximum = 1 };
    private readonly StackPanel _recordings = new();
    private readonly StackPanel _scenarios = new();
    private readonly StackPanel _cases = new();
    private readonly StackPanel _detail = new();
    private readonly StackPanel _editor = new();
    private readonly DispatcherTimer _integrity = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, (long Length, DateTime Written, DateTime Created)> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SensitivityScenarioDraft> _drafts = [];
    private List<SensitivityRecordingRow> _recordingsRows = [];
    private bool _blindReady, _auditReveal, _checking;
    private volatile bool _closed;
    private volatile bool _freezeActive;
    private int _freezeGeneration, _viewGeneration;
    private CancellationTokenSource? _runCancel;
    private CancellationTokenSource? _freezeCancel;
    public TextBox ScenarioNameBox { get; } = new() { MinWidth = 180 };
    public TextBox RationaleBox { get; } = new() { MinWidth = 220 };
    public TextBox ThresholdBox { get; } = new() { Width = 80 };
    public TextBox BlurBox { get; } = new() { Width = 80 };
    public TextBox MinAreaBox { get; } = new() { Width = 80 };
    public TextBox MaxAreaBox { get; } = new() { Width = 80 };
    public TextBox ExtentBox { get; } = new() { Width = 80 };
    public TextBox JumpBox { get; } = new() { Width = 80 };
    public ComboBox InvertBox { get; } = new() { Width = 180 };
    public IReplayTracker Tracker { get; set; } = new NativeReplayTracker();
    public int? CancelAfterCompleted { get; set; }
    public Task PendingRun { get; private set; } = Task.CompletedTask;
    public Task PendingView { get; private set; } = Task.CompletedTask;
    public SensitivitySourceViewer? LastViewer { get; private set; }
    public SensitivityFreezeDocument? Freeze { get; private set; }
    public ReplayManifestDocument? Manifest { get; private set; }
    public bool Held { get; private set; }
    public int LastExitCode { get; private set; }
    public string StatusText => _status.Text;
    public bool Blinded { get => _blind.IsChecked == true; set => _blind.IsChecked = value; }

    public SensitivityWindow(string database, bool blinded, bool showViewers = true)
    {
        _database = database;
        _workspace = SensitivityCatalog.WorkspaceFor(database);
        _showViewers = showViewers;
        Title = "Mouse Tracker Tabajara — Detector sensitivity";
        Width = 1440;
        Height = 960;
        MinWidth = 1050;
        MinHeight = 700;
        Background = System.Windows.Media.Brushes.WhiteSmoke;
        InvertBox.Items.Add("Inherit baseline polarity");
        InvertBox.Items.Add("Invert on");
        InvertBox.Items.Add("Invert off");
        InvertBox.SelectedIndex = 0;
        BuildEditor();
        Content = BuildShell();
        _blind.Checked += (_, _) =>
        {
            _unblindReviewer.Clear();
            _unblindReason.Clear();
            InvalidatePendingViewers();
            ClearListedIdentity();
            Rebuild();
        };
        _blind.Unchecked += (_, _) =>
        {
            if (!_blindReady || _auditReveal) { Rebuild(); return; }
            var reviewer = _unblindReviewer.Text;
            var reason = _unblindReason.Text;
            _blind.IsChecked = true;
            TryUnblind(reviewer, reason);
        };
        _integrity.Tick += async (_, _) =>
        {
            if (_closed || _checking || Held || _freezeActive || Freeze is null) return;
            _checking = true;
            var revision = Freeze.SnapshotRevision;
            var stamps = _stamps.ToArray();
            try
            {
                var ok = await Task.Run(() => SnapshotMatches(revision) && StampsMatch(stamps)).ConfigureAwait(true);
                if (!ok && !_closed && !Held && !_freezeActive) Hold("Study snapshot or source identity changed. Replay and playback are held.");
            }
            catch (Exception)
            { if (!_closed) Hold("Sensitivity integrity is unavailable. Replay and playback are held."); }
            finally { _checking = false; }
        };
        _integrity.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            Interlocked.Increment(ref _freezeGeneration);
            _freezeCancel?.Cancel();
            _integrity.Stop();
            _runCancel?.Cancel();
            InvalidatePendingViewers();
            ClearListedIdentity();
            _status.Text = "";
            _progressText.Text = "";
        };
        var loaded = SensitivityCatalog.TryLoad(_workspace);
        if (loaded is { Schema: SensitivityCatalog.FreezeSchema })
        {
            Freeze = loaded;
            _drafts.AddRange(loaded.Scenarios.Select(Copy));
            RememberStamps(loaded);
        }
        Manifest = SensitivityCatalog.ReadManifest(_workspace);
        Blinded = blinded;
        _blindReady = true;
        Rebuild();
        if (!Held)
            _status.Text = "Name detector scenarios and record a rationale before execution. Geometry and the decoder interval stay on each baseline.";
    }

    public bool TryUnblind(string reviewer, string reason)
    {
        if (!Blinded) return true;
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason))
        {
            _status.Text = "Reviewer and reason required; sensitivity remains blind.";
            return false;
        }
        try
        {
            new StudyStore(_database).Audit("sensitivity_unblinded", new { Reviewer = reviewer.Trim(), Reason = reason.Trim(), SnapshotRevision = Freeze?.SnapshotRevision });
            _auditReveal = true;
            _blind.IsChecked = false;
            return true;
        }
        catch (Exception)
        {
            _status.Text = "Unblinding audit failed; sensitivity remains blind.";
            return false;
        }
        finally { _auditReveal = false; }
    }

    public void SaveScenario()
    {
        if (Blinded) { _status.Text = "Unblind explicitly before naming scenarios."; return; }
        if (!PendingRun.IsCompleted) { _status.Text = "Wait for the current replay before changing the plan."; return; }
        try
        {
            var draft = new SensitivityScenarioDraft
            {
                Name = ScenarioNameBox.Text.Trim(),
                Rationale = RationaleBox.Text,
                Threshold = ParseInt(ThresholdBox.Text, "Threshold"),
                Blur = ParseInt(BlurBox.Text, "Blur"),
                MaxExtension = ParseInt(ExtentBox.Text, "Extent"),
                MinArea = ParseDouble(MinAreaBox.Text, "Min area"),
                MaxArea = ParseDouble(MaxAreaBox.Text, "Max area"),
                MaxJump = ParseDouble(JumpBox.Text, "Jump"),
                Invert = InvertBox.SelectedIndex switch { 1 => true, 2 => false, _ => null }
            };
            SensitivityCatalog.ValidateDrafts([draft]);
            var existing = _drafts.FindIndex(item => item.Name == draft.Name);
            if (existing >= 0) _drafts[existing] = draft;
            else _drafts.Add(draft);
            Rebuild();
            _status.Text = "Scenario rationale saved. It is not executed until the plan is frozen and run.";
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    public void AddOriginalFamilies()
    {
        if (Blinded) { _status.Text = "Unblind explicitly before naming scenarios."; return; }
        foreach (var family in SensitivityCatalog.OriginalFamilies())
            if (_drafts.All(draft => !string.Equals(draft.Name, family.Name, StringComparison.Ordinal))) _drafts.Add(family);
        Rebuild();
        _status.Text = "The seven prespecified families are listed. Each keeps its own rationale. None is a preferred setting.";
    }

    public Task FreezePlanAsync()
    {
        if (!PendingRun.IsCompleted) return PendingRun;
        _freezeActive = true;
        var task = Task.Run(ExecuteFreeze);
        PendingRun = task;
        return task;
    }

    public Task RunAsync()
    {
        if (!PendingRun.IsCompleted) return PendingRun;
        var task = Task.Run(ExecuteReplay);
        PendingRun = task;
        return task;
    }

    public void Cancel()
    {
        _runCancel?.Cancel();
        var freeze = _freezeCancel;
        if (freeze is null) return;
        Interlocked.Increment(ref _freezeGeneration);
        try { freeze.Cancel(); } catch (ObjectDisposedException) { }
        Post(() =>
        {
            if (_closed) return;
            _status.Text = "Freeze cancelled. No new plan was published.";
        });
    }

    public async Task CheckIntegrityAsync()
    {
        if (Freeze is null) return;
        var revision = Freeze.SnapshotRevision;
        var stamps = _stamps.ToArray();
        var ok = await Task.Run(() => SnapshotMatches(revision) && StampsMatch(stamps)).ConfigureAwait(true);
        if (!ok) Hold("Study snapshot or source identity changed. Replay and playback are held.");
    }

    private void ExecuteFreeze()
    {
        var generation = Volatile.Read(ref _freezeGeneration);
        var cancel = new CancellationTokenSource();
        _freezeCancel = cancel;
        SensitivityFreezePublication? publication = null;
        var accepted = false;
        try
        {
            var drafts = _drafts.Select(Copy).ToList();
            var snapshot = AtlasSnapshot.Read(_database);
            bool Allowed() => !_closed && Volatile.Read(ref _freezeGeneration) == generation && !cancel.IsCancellationRequested;
            publication = SensitivityCatalog.Freeze(snapshot, drafts, _workspace, Tracker, cancel.Token, Allowed);
            var result = publication.Result;
            SensitivityCatalog.EnsureStillPublishable(snapshot, result, Tracker, cancel.Token, Allowed);
            Post(() =>
            {
                if (!Allowed()) return;
                if (!SnapshotMatches(result.Document.SnapshotRevision) || !StampsMatch(StampPairs(result.SourceStamps))) return;
                Freeze = result.Document;
                Held = false;
                ApplyStamps(result.SourceStamps);
                Manifest = null;
                Rebuild();
                _status.Text = Blinded
                    ? "Plan, baselines, snapshot, and code identity are frozen."
                    : "Plan frozen. Baseline bytes, source hashes, snapshot, and code identity were recorded before execution.";
                accepted = true;
            });
            if (accepted) publication.Acknowledge();
            else
            {
                publication.Revert();
                if (Allowed()) Post(() => Hold("The study snapshot or a source changed while the plan was being frozen. No new freeze was published."));
            }
        }
        catch (OperationCanceledException)
        {
            publication?.Revert();
            Post(() =>
            {
                if (_closed || Volatile.Read(ref _freezeGeneration) != generation) return;
                _status.Text = "Freeze cancelled. No new plan was published.";
            });
        }
        catch (Exception exception)
        {
            publication?.Revert();
            Post(() =>
            {
                if (_closed || Volatile.Read(ref _freezeGeneration) != generation) return;
                Hold(exception.Message);
            });
        }
        finally
        {
            publication?.Dispose();
            _freezeActive = false;
            if (ReferenceEquals(_freezeCancel, cancel)) _freezeCancel = null;
            cancel.Dispose();
        }
    }

    private void ExecuteReplay()
    {
        var cancel = new CancellationTokenSource();
        _runCancel = cancel;
        var error = new StringWriter();
        var output = new StringWriter();
        try
        {
            var freeze = Freeze;
            if (freeze is null) { Post(() => Hold("Freeze the scenario plan before running.")); LastExitCode = 2; return; }
            if (SensitivityCatalog.ReadInventory(_workspace, freeze) is null)
            {
                Post(() => Hold("Frozen recording inventory failed authentication. Current study rows were not substituted."));
                LastExitCode = 2;
                return;
            }
            if (!SnapshotMatches(freeze.SnapshotRevision)) { Post(() => Hold("Study snapshot changed. Replay was not started.")); LastExitCode = 2; return; }
            if (!SensitivityCatalog.SamePlanBytes(_workspace, freeze)) { Post(() => Hold("Frozen plan bytes changed. Replay was not started.")); LastExitCode = 2; return; }
            var plan = Path.Combine(_workspace, "freeze", "plan.json");
            var destination = Path.Combine(_workspace, "replay");
            var code = ReplayCommand.Execute(["replay", plan, destination], new ReplayCommandContext
            {
                Tracker = Tracker,
                Out = output,
                Error = error,
                Cancellation = cancel.Token,
                Progress = OnProgress
            });
            LastExitCode = code;
            var manifest = SensitivityCatalog.ReadManifest(_workspace);
            Post(() =>
            {
                Manifest = manifest;
                Held = code == 2;
                Rebuild();
                if (code == 2) Hold(string.IsNullOrWhiteSpace(error.ToString()) ? "Replay rejected the frozen identity. Output publications were left unchanged." : error.ToString().Trim());
                else _status.Text = Blinded
                    ? "Replay finished. Every planned case remains listed."
                    : $"Replay exit {code}. {ReplayClaims.SelectionPolicy}";
            });
        }
        catch (Exception exception)
        {
            LastExitCode = 2;
            Post(() => Hold(exception.Message));
        }
    }

    private void OnProgress(ReplayProgress progress)
    {
        if (CancelAfterCompleted is int limit && progress.Phase == "saved" && progress.Completed >= limit) Cancel();
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _progress.Maximum = Math.Max(progress.Planned, 1);
            _progress.Value = Math.Min(progress.Completed + progress.Failed + progress.Cancelled, _progress.Maximum);
            _progressText.Text = Blinded
                ? $"Completed {progress.Completed}, failed {progress.Failed}, unrun {progress.Unrun}, cancelled {progress.Cancelled}."
                : $"Replay {progress.Phase}. Completed {progress.Completed}, failed {progress.Failed}, unrun {progress.Unrun}, cancelled {progress.Cancelled}.";
        });
    }

    private void Open(SensitivityCaseRow row)
    {
        ShowDetail(row);
        var generation = Interlocked.Increment(ref _viewGeneration);
        var blinded = Blinded;
        PendingView = OpenAsync(row, generation, blinded);
    }

    private async Task OpenAsync(SensitivityCaseRow row, int generation, bool blinded)
    {
        var freeze = Freeze;
        var manifest = Manifest;
        var held = Held;
        SensitivityViewRequest request;
        try { request = await Task.Run(() => BuildRequest(row, blinded, freeze, manifest)).ConfigureAwait(true); }
        catch (Exception)
        {
            if (!ViewCurrent(generation, blinded)) return;
            throw;
        }
        if (!ViewCurrent(generation, blinded)) return;
        if (held || Held) request = RefuseHeld(request, blinded);
        if (!ViewCurrent(generation, blinded)) return;
        CloseViewer(LastViewer);
        if (!ViewCurrent(generation, blinded)) return;
        var viewer = new SensitivitySourceViewer(blinded) { Owner = _showViewers ? this : null };
        if (!ViewCurrent(generation, blinded))
        {
            CloseViewer(viewer);
            return;
        }
        LastViewer = viewer;
        if (_showViewers) viewer.Show();
        if (!ViewCurrent(generation, blinded))
        {
            if (ReferenceEquals(LastViewer, viewer)) LastViewer = null;
            CloseViewer(viewer);
            return;
        }
        viewer.Show(request);
        try { await viewer.PendingFrame.ConfigureAwait(true); }
        catch (Exception) when (!ViewCurrent(generation, blinded)) { }
    }

    private bool ViewCurrent(int generation, bool blinded) =>
        !_closed && generation == Volatile.Read(ref _viewGeneration) && Blinded == blinded;

    private SensitivityViewRequest BuildRequest(SensitivityCaseRow row, bool blinded, SensitivityFreezeDocument? freeze, ReplayManifestDocument? manifest)
    {
        string RecordingLabel() => blinded ? SensitivityCatalog.OpaqueRecording(row.RecordingOrdinal) : row.RecordingId;
        string ScenarioLabel() => blinded ? SensitivityCatalog.OpaqueScenario(row.ScenarioOrdinal) : row.ScenarioName;
        SensitivityViewRequest Refuse(string note) => new()
        {
            RecordingLabel = RecordingLabel(),
            ScenarioLabel = ScenarioLabel(),
            Status = row.Status,
            VideoPath = "",
            SourceSha256 = "",
            Metadata = new VideoMetadata(1, 1, 1, 1),
            PreservedSettings = new TrackingSettings(),
            BaselinePoints = [],
            AllowPixels = false,
            Note = blinded ? "Source review held. Markers were not invented." : note
        };
        if (freeze is null || !SnapshotMatches(freeze.SnapshotRevision)) return Refuse("The study snapshot does not match the frozen plan.");
        if (!SensitivityCatalog.SamePlanBytes(_workspace, freeze)) return Refuse("Frozen plan bytes changed.");
        if (manifest is not null && !RecordedCodeMatches(freeze, manifest)) return Refuse("Frozen code identity does not match the manifest.");
        var recording = freeze.Recordings.SingleOrDefault(item => item.RecordingId == row.RecordingId);
        if (recording is null) return Refuse("This case is not in the frozen plan.");
        var baselinePath = Path.GetFullPath(Path.Combine(_workspace, "freeze", recording.BaselineFile));
        var baselineRoot = Path.GetFullPath(Path.Combine(_workspace, "freeze", "baselines")) + Path.DirectorySeparatorChar;
        if (!baselinePath.StartsWith(baselineRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(baselinePath))
            return Refuse("Frozen baseline file is missing.");
        if (!string.Equals(ReplayHashes.Sha256File(baselinePath), recording.BaselineSha256, StringComparison.Ordinal))
            return Refuse("Frozen baseline bytes changed.");
        AnalysisRun baseline;
        try { baseline = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(baselinePath)); }
        catch (Exception) { return Refuse("Frozen baseline could not be read."); }
        try { CompletedRunContract.Ensure(baseline, baseline.Metadata, baseline.Settings, recording.SourceSha256, AlgorithmIdentity.Contract, CompletedRunContract.SoftwareVersion); }
        catch (Exception) { return Refuse("Frozen baseline no longer validates as an automatic run."); }
        if (!File.Exists(recording.VideoPath) || !string.Equals(Tracker.HashFile(recording.VideoPath), recording.SourceSha256, StringComparison.Ordinal))
            return Refuse("Source identity changed. Playback was held.");
        IReadOnlyList<TrackPoint> scenarioPoints = [];
        var corrections = Array.Empty<ManualCorrection>();
        var allowScenario = false;
        var note = row.Status switch
        {
            ReplayStatuses.Unrun => "Unrun case. Scenario markers were not invented.",
            ReplayStatuses.Cancelled => "Cancelled case. Scenario markers were not invented.",
            ReplayStatuses.Failed => "Failed case. Scenario markers were not invented.",
            _ => ""
        };
        var evidence = ResolveEvidence(row.EvidenceRelative);
        if (evidence is not null && row.Status is ReplayStatuses.Completed or ReplayStatuses.Failed)
        {
            try
            {
                var run = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(evidence));
                var manifestCase = manifest?.Cases.SingleOrDefault(item => item.PlanIndex == row.PlanIndex);
                var expectedHash = manifestCase?.Detector?.AppliedSettingsSha256;
                var hash = StudyStore.SettingsHash(run.Settings);
                var sameGeometry = CompletedRunContract.SamePreserved(baseline.Settings, run.Settings);
                var sameSource = string.Equals(run.SourceSha256, recording.SourceSha256, StringComparison.Ordinal)
                    && CompletedRunContract.SameMetadata(run.Metadata, baseline.Metadata);
                if (!sameSource || !sameGeometry || expectedHash is null || !string.Equals(hash, expectedHash, StringComparison.Ordinal))
                    note = "Scenario evidence does not match the frozen source or settings. Markers were withheld.";
                else if (row.Status == ReplayStatuses.Completed)
                {
                    CompletedRunContract.Ensure(run, baseline.Metadata, run.Settings, recording.SourceSha256, AlgorithmIdentity.Contract, CompletedRunContract.SoftwareVersion);
                    scenarioPoints = run.Points;
                    corrections = run.AppliedCorrections.ToArray();
                    allowScenario = true;
                    note = "Completed automatic scenario. Accepted positions and gap status come from this frozen run.";
                }
                else
                {
                    note = "Failed evidence did not pass the automatic-run contract. Markers were withheld.";
                }
            }
            catch (Exception)
            {
                note = "Scenario evidence failed validation. Markers were withheld.";
            }
        }
        return new SensitivityViewRequest
        {
            RecordingLabel = RecordingLabel(),
            ScenarioLabel = ScenarioLabel(),
            Status = row.Status,
            VideoPath = recording.VideoPath,
            SourceSha256 = recording.SourceSha256,
            Metadata = baseline.Metadata,
            PreservedSettings = baseline.Settings,
            BaselinePoints = baseline.Points,
            ScenarioPoints = scenarioPoints,
            Corrections = corrections,
            AllowPixels = true,
            AllowScenarioMarkers = allowScenario,
            AllowBaselineMarkers = true,
            Note = blinded ? OpaqueNote(row.Status, allowScenario) : note
        };
    }

    private static string OpaqueNote(string status, bool allowScenario) => allowScenario
        ? "Frozen scenario source. Accepted positions and gaps stay visible. Identifying notes stay hidden."
        : status + " case. Scenario markers were not invented.";

    private static SensitivityViewRequest RefuseHeld(SensitivityViewRequest request, bool blinded) => new()
    {
        RecordingLabel = request.RecordingLabel,
        ScenarioLabel = request.ScenarioLabel,
        Status = request.Status,
        VideoPath = "",
        SourceSha256 = "",
        Metadata = request.Metadata,
        PreservedSettings = request.PreservedSettings,
        BaselinePoints = [],
        AllowPixels = false,
        Note = blinded ? "Source review held. Markers were not invented." : "Source review is held until snapshot, source, and code identity match."
    };

    private string? ResolveEvidence(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var replay = Path.GetFullPath(Path.Combine(_workspace, "replay"));
        var directory = Path.GetFullPath(Path.Combine(replay, relative));
        var root = replay.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var published = Path.Combine(directory, "analysis.json");
        if (File.Exists(published)) return published;
        var rejected = Path.Combine(directory, "rejected-analysis.json");
        return File.Exists(rejected) ? rejected : null;
    }

    private bool SnapshotMatches(string revision)
    {
        var snapshot = AtlasSnapshot.Read(_database);
        return snapshot.IsCurrent() && string.Equals(snapshot.Revision, revision, StringComparison.Ordinal);
    }

    private static bool RecordedCodeMatches(SensitivityFreezeDocument freeze, ReplayManifestDocument manifest)
    {
        try
        {
            ReplayCodeIdentityProbe.RequireSame(manifest.CodeIdentity, freeze.CodeIdentity);
            return string.Equals(ReplayCodeIdentityProbe.CanonicalSha256(manifest.CodeIdentity), freeze.CodeIdentitySha256, StringComparison.Ordinal);
        }
        catch (PlanRejectedException) { return false; }
    }

    private static bool StampsMatch(KeyValuePair<string, (long Length, DateTime Written, DateTime Created)>[] stamps)
    {
        foreach (var (path, stamp) in stamps)
        {
            var info = new FileInfo(path);
            if (!info.Exists || stamp != (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc)) return false;
        }
        return true;
    }

    private void RememberStamps(SensitivityFreezeDocument document)
    {
        _stamps.Clear();
        foreach (var recording in document.Recordings)
        {
            var info = new FileInfo(recording.VideoPath);
            if (info.Exists) _stamps[info.FullName] = (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
    }

    private void ApplyStamps(IReadOnlyDictionary<string, SensitivitySourceStamp> stamps)
    {
        _stamps.Clear();
        foreach (var (path, stamp) in stamps) _stamps[path] = (stamp.Length, stamp.WrittenUtc, stamp.CreatedUtc);
    }

    private static KeyValuePair<string, (long Length, DateTime Written, DateTime Created)>[] StampPairs(IReadOnlyDictionary<string, SensitivitySourceStamp> stamps) =>
        stamps.Select(pair => KeyValuePair.Create(pair.Key, (pair.Value.Length, pair.Value.WrittenUtc, pair.Value.CreatedUtc))).ToArray();

    private void InvalidatePendingViewers()
    {
        Interlocked.Increment(ref _viewGeneration);
        CloseViewer(LastViewer);
        LastViewer = null;
    }

    private static void CloseViewer(SensitivitySourceViewer? viewer)
    {
        if (viewer is null) return;
        viewer.ClearSensitive();
        if (!viewer.ResourcesReleased) viewer.Close();
    }

    private void ClearListedIdentity()
    {
        _detail.Children.Clear();
        _recordings.Children.Clear();
        _scenarios.Children.Clear();
        _cases.Children.Clear();
        _identity.Text = "";
    }

    private void Rebuild()
    {
        if (Freeze is not null)
        {
            var inventory = SensitivityCatalog.ReadInventory(_workspace, Freeze);
            if (inventory is null)
            {
                _recordingsRows = [];
                ClearListedIdentity();
                Hold("Frozen recording inventory failed authentication. Current study rows were not substituted.");
                return;
            }
            _recordingsRows = inventory.Select(row => new SensitivityRecordingRow
            {
                RecordingId = row.RecordingId,
                Ordinal = row.Ordinal,
                Availability = row.Availability,
                Reason = row.Reason,
                Filename = row.Filename,
                AnimalId = row.AnimalId,
                GroupName = row.GroupName
            }).ToList();
        }
        else
        {
            try { _recordingsRows = SensitivityCatalog.ReadRecordings(AtlasSnapshot.Read(_database)).ToList(); }
            catch (Exception)
            {
                Hold("Study snapshot could not be read.");
                return;
            }
        }
        _recordings.Children.Clear();
        _recordings.Children.Add(Text("Imported recordings", true));
        foreach (var row in _recordingsRows)
            _recordings.Children.Add(Text(Blinded
                ? $"{SensitivityCatalog.OpaqueRecording(row.Ordinal)} · {row.Availability}"
                : $"{row.RecordingId} · {row.Asset?.Filename ?? row.Filename} · {row.Asset?.AnimalId ?? row.AnimalId} · {row.Asset?.GroupName ?? row.GroupName} · {row.Availability} · {row.Reason}"));
        if (_editor.Parent is Panel parent) parent.Children.Remove(_editor);
        _scenarios.Children.Clear();
        _scenarios.Children.Add(Text("Named scenarios", true));
        if (Blinded) _scenarios.Children.Add(Text("Unblind explicitly before naming scenarios or reading rationale."));
        else
        {
            _scenarios.Children.Add(_editor);
            foreach (var draft in _drafts)
                _scenarios.Children.Add(Text($"{draft.Name}: {draft.Rationale}"));
        }
        _cases.Children.Clear();
        _cases.Children.Add(Text("Every planned case", true));
        foreach (var row in SensitivityCatalog.Cases(Freeze, Manifest, _recordingsRows))
        {
            var captured = row;
            var button = new Button { Content = SensitivityCatalog.CaseLabel(row, Blinded), HorizontalContentAlignment = HorizontalAlignment.Left, Tag = "sensitivity-case" };
            button.Click += (_, _) => Open(captured);
            _cases.Children.Add(button);
            var measurement = row.CoveragePercent is double coverage
                ? $"Coverage {coverage.ToString("0.###", CultureInfo.InvariantCulture)} percent. Unknown {row.UnknownSeconds.GetValueOrDefault().ToString("0.###", CultureInfo.InvariantCulture)} seconds."
                : "No scenario measurement yet.";
            if (!Blinded && !string.IsNullOrWhiteSpace(row.ErrorMessage)) measurement += " " + row.ErrorMessage;
            _cases.Children.Add(new TextBlock { Text = measurement, TextWrapping = TextWrapping.Wrap, Tag = "case-summary" });
        }
        _identity.Text = Blinded
            ? "Code, snapshot, and plan identity are frozen. Hashes, filenames, groups, animal ids, and error text stay hidden while blind. The software version is not a code fingerprint."
            : Freeze is null
                ? "No frozen plan yet."
                : $"Snapshot {Freeze.SnapshotRevision}\nPlan {Freeze.PlanSha256}\nInventory {Freeze.InventorySha256}\nCode identity {Freeze.CodeIdentitySha256}\nSoftware version {CompletedRunContract.SoftwareVersion} is not that fingerprint.";
        _detail.Children.Clear();
        ShowClaims();
    }

    private void ShowDetail(SensitivityCaseRow row)
    {
        _detail.Children.Clear();
        ShowClaims();
        _detail.Children.Add(Text(SensitivityCatalog.CaseLabel(row, Blinded), true));
        if (row.CoveragePercent is double coverage)
            _detail.Children.Add(Text($"Coverage {coverage.ToString("0.###", CultureInfo.InvariantCulture)} percent. Unknown time {row.UnknownSeconds.GetValueOrDefault().ToString("0.###", CultureInfo.InvariantCulture)} seconds. Raw decoder run."));
        if (!Blinded)
        {
            _detail.Children.Add(Text(string.IsNullOrWhiteSpace(row.EvidenceRelative) ? "No attempt evidence yet." : "Evidence " + row.EvidenceRelative));
            _detail.Children.Add(Text("Manifest replay/replay-manifest.json"));
            if (!string.IsNullOrWhiteSpace(row.ErrorMessage)) _detail.Children.Add(Text(row.ErrorMessage));
        }
        else _detail.Children.Add(Text("Frozen manifest and case evidence stay linked. Paths stay hidden while blind."));
        foreach (var difference in row.Differences)
            _detail.Children.Add(new TextBlock
            {
                Tag = "endpoint-row",
                TextWrapping = TextWrapping.Wrap,
                Text = $"{difference.Name}: baseline {Format(difference.Baseline)}, scenario {Format(difference.Scenario)}, difference {Format(difference.Difference)} {difference.Unit}"
            });
    }

    private void ShowClaims()
    {
        _detail.Children.Add(new TextBlock
        {
            Tag = "claim-limits",
            TextWrapping = TextWrapping.Wrap,
            Text = "Every planned case stays listed, including failed, unrun, and unfavorable detector results. Scenario order is the prespecified plan order. Treatment labels do not rank settings. Coverage is not accuracy. Raw decoder-run counts are not sampling confidence intervals, memory scores, causal claims, or a best-setting selector. Biological N is not counted. Distance, speed, occupancy, first visit, and quadrant time stay withheld. The decoder interval is not a biological release."
        });
    }

    private void Hold(string message)
    {
        Held = true;
        if (LastViewer is { Held: false })
        {
            CloseViewer(LastViewer);
            LastViewer = null;
        }
        _status.Text = Blinded || string.IsNullOrWhiteSpace(message)
            ? "Frozen replay held. Verify snapshot, source, and code identity."
            : message.Trim();
    }

    private void Post(Action action)
    {
        if (_closed) return;
        try
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        }
        catch (Exception) when (_closed) { }
    }

    private UIElement BuildShell()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Detector sensitivity", FontSize = 23, FontWeight = FontWeights.SemiBold });
        var commands = new WrapPanel();
        commands.Children.Add(_blind);
        commands.Children.Add(new TextBlock { Text = "Reviewer / reason to unblind", VerticalAlignment = VerticalAlignment.Center });
        commands.Children.Add(_unblindReviewer);
        commands.Children.Add(_unblindReason);
        commands.Children.Add(Command("Freeze plan and identities", (_, _) => PendingRun = FreezePlanAsync()));
        commands.Children.Add(Command("Run frozen replay", (_, _) => PendingRun = RunAsync()));
        commands.Children.Add(Command("Cancel replay", (_, _) => Cancel()));
        commands.Children.Add(Command("Resume frozen replay", (_, _) => PendingRun = RunAsync()));
        header.Children.Add(commands);
        header.Children.Add(_progress);
        header.Children.Add(_progressText);
        header.Children.Add(_identity);
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.Children.Add(Scroll(_recordings));
        var middle = Scroll(_scenarios);
        Grid.SetColumn(middle, 1);
        columns.Children.Add(middle);
        var right = new DockPanel();
        DockPanel.SetDock(_detail, Dock.Bottom);
        right.Children.Add(_detail);
        right.Children.Add(Scroll(_cases));
        Grid.SetColumn(right, 2);
        columns.Children.Add(right);
        root.Children.Add(columns);
        return root;
    }

    private void BuildEditor()
    {
        _editor.Children.Add(Text("Detector fields only. Pool geometry, exclusions, the decoder interval, and units stay on the baseline."));
        _editor.Children.Add(Text("Scenario name"));
        _editor.Children.Add(ScenarioNameBox);
        _editor.Children.Add(Text("Rationale, recorded before execution"));
        _editor.Children.Add(RationaleBox);
        var fields = new WrapPanel();
        fields.Children.Add(Field("Threshold", ThresholdBox));
        fields.Children.Add(Field("Blur", BlurBox));
        fields.Children.Add(Field("Min area", MinAreaBox));
        fields.Children.Add(Field("Max area", MaxAreaBox));
        fields.Children.Add(Field("Extent", ExtentBox));
        fields.Children.Add(Field("Jump", JumpBox));
        _editor.Children.Add(fields);
        _editor.Children.Add(Text("Polarity"));
        _editor.Children.Add(InvertBox);
        _editor.Children.Add(Command("Save named scenario", (_, _) => SaveScenario()));
        _editor.Children.Add(Command("Add the seven prespecified families", (_, _) => AddOriginalFamilies()));
    }

    private static StackPanel Field(string label, TextBox box)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(Text(label));
        panel.Children.Add(box);
        return panel;
    }

    private static Button Command(string content, RoutedEventHandler click)
    {
        var button = new Button { Content = content };
        button.Click += click;
        return button;
    }

    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 8, 0) };

    private static TextBlock Text(string text, bool heading = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = new Thickness(0, heading ? 0 : 2, 0, 4)
    };

    private static int? ParseInt(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException(label + " must be a blank or an integer.");
        return value;
    }

    private static double? ParseDouble(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidDataException(label + " must be a blank or a finite number.");
        return value;
    }

    private static string Format(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "missing";

    private static SensitivityScenarioDraft Copy(SensitivityScenarioDraft draft) => new()
    {
        Name = draft.Name,
        Rationale = draft.Rationale,
        Threshold = draft.Threshold,
        Invert = draft.Invert,
        Blur = draft.Blur,
        MinArea = draft.MinArea,
        MaxArea = draft.MaxArea,
        MaxExtension = draft.MaxExtension,
        MaxJump = draft.MaxJump
    };
}
