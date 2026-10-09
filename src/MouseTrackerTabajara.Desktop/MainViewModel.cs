using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

public sealed class TrialRow(VideoAsset asset) : ObservableModel
{
    public VideoAsset Asset { get; private set; } = asset;
    public AnalysisRun? Run { get; set; }
    public bool Reviewed { get; set; }
    public bool ReviewStale { get; set; }
    public bool Blind { get; set; }
    public string DisplayName => Blind ? BlindIdentity.RecordingAlias(Asset) : Asset.Filename;
    public string AnimalId { get => Asset.AnimalId; set { Asset = Asset with { AnimalId = value }; Changed(); } }
    public string AnimalDisplay
    {
        get => Blind ? "" : Asset.AnimalId;
        set { if (Blind || value == Asset.AnimalId) return; Asset = Asset with { AnimalId = value }; Changed(nameof(AnimalDisplay)); Changed(nameof(AnimalId)); }
    }
    public bool AnimalIdConfirmed { get => Asset.AnimalIdConfirmed; set { Asset = Asset with { AnimalIdConfirmed = value }; Changed(); } }
    public string GroupName { get => Asset.GroupName; set { Asset = Asset with { GroupName = value }; Changed(); } }
    public string GroupDisplay
    {
        get => Blind ? "" : Asset.GroupName;
        set { if (Blind || value == Asset.GroupName) return; Asset = Asset with { GroupName = value }; Changed(nameof(GroupDisplay)); Changed(nameof(GroupName)); }
    }
    public string Inclusion => Asset.Included ? "Included" : "Excluded";
    public TrialMode Phase { get => Asset.Phase; set { Asset = Asset with { Phase = value }; Changed(); } }
    public int Day { get => Asset.Day; set { if (value < 1) return; Asset = Asset with { Day = value }; Changed(); } }
    public int Trial { get => Asset.Trial; set { if (value < 1) return; Asset = Asset with { Trial = value }; Changed(); } }
    public string Coverage => Run is null ? "—" : $"{Run.Metrics.CoveragePercent:F1}%";
    public string Status => Run is null ? "Not analyzed" : StudyStore.SettingsHash(Run.Settings) != StudyStore.SettingsHash(Asset.Settings) ? "Editable preview" : Reviewed ? "Reviewed" : ReviewStale ? "Review stale" : "Review pending";
    public void Replace(VideoAsset asset) { Asset = asset; Refresh(); }
    public void Refresh() { foreach (var property in new[] { nameof(DisplayName), nameof(AnimalId), nameof(AnimalDisplay), nameof(AnimalIdConfirmed), nameof(GroupName), nameof(GroupDisplay), nameof(Phase), nameof(Day), nameof(Trial), nameof(Coverage), nameof(Inclusion), nameof(Status) }) Changed(property); }
}

public sealed class MainViewModel : ObservableModel
{
    public WorkspaceService Workspace { get; }
    public ObservableCollection<TrialRow> Rows { get; } = [];
    public ObservableCollection<ReviewMoment> ReviewMoments { get; } = [];
    public static Array TrialModes => Enum.GetValues<TrialMode>();
    private TrialRow? _selected;
    public TrialRow? Selected { get => _selected; set { _selected = value; Changed(); } }
    private bool _busy;
    public bool Busy { get => _busy; set { _busy = value; Changed(); Changed(nameof(NotBusy)); } }
    public bool NotBusy => !Busy;
    private string _status = "Add local videos or open a saved processed project.";
    public string Status { get => _status; set { _status = Blind ? BlindIdentity.PresentStatus(value, StudySecrets()) : value; Changed(); } }
    private double _progress;
    public double Progress { get => _progress; set { _progress = value; Changed(); } }
    private bool _blind;
    public bool Blind
    {
        get => _blind;
        set
        {
            _blind = value;
            foreach (var row in Rows) { row.Blind = value; row.Refresh(); }
            if (_blind) _status = BlindIdentity.PresentStatus(_status, StudySecrets());
            Changed();
            Changed(nameof(Status));
            Changed(nameof(StudyFileLabel));
        }
    }
    public string Inventory => $"{Rows.Count} recordings  •  {Rows.Count(r => r.Run is not null)} analyzed  •  {Rows.Count(r => r.Reviewed)} reviewed";
    public string StudyPath => Workspace.Store.DatabasePath;
    public string StudyNote { get; }
    public string StudyFileLabel
    {
        get
        {
            var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(StudyPath) ?? "");
            var file = System.IO.Path.GetFileName(StudyPath);
            if (Blind) return "Study file hidden during blind review";
            return string.IsNullOrEmpty(folder) ? "Study file " + file : "Study file " + folder + "\\" + file;
        }
    }
    public BlindSecrets StudySecrets() => BlindSecrets.FromAssets(Rows.Select(row => row.Asset));
    public void SyncReview(TrialRow row)
    {
        var raw = Workspace.Store.LatestRun(row.Asset.Id);
        if (raw is null) { row.Reviewed = false; row.ReviewStale = false; }
        else
        {
            row.Reviewed = Workspace.Store.Reviewed(raw.Id);
            row.ReviewStale = Workspace.Store.ReviewStale(raw.Id);
        }
        row.Refresh();
        Changed(nameof(Inventory));
    }
    public MainViewModel(WorkspaceService workspace, string? studyNote = null)
    {
        Workspace = workspace; StudyNote = studyNote ?? "";
        if (StudyNote.Length > 0) Status = StudyNote;
        Reload();
    }
    public void Reload()
    {
        var selectedId = Selected?.Asset.Id; Rows.Clear();
        foreach (var asset in Workspace.Store.Videos())
        {
            var raw = Workspace.Store.LatestRun(asset.Id);
            var reviewed = raw is not null && Workspace.Store.Reviewed(raw.Id);
            Rows.Add(new(asset) { Blind = Blind, Run = raw is null ? null : Workspace.Store.EffectiveRun(raw), Reviewed = reviewed, ReviewStale = raw is not null && !reviewed && Workspace.Store.ReviewStale(raw.Id) });
        }
        Selected = Rows.FirstOrDefault(r => r.Asset.Id == selectedId) ?? Rows.FirstOrDefault(); Changed(nameof(Inventory));
    }
}
