using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using OpenCvSharp;

namespace MouseTrackerTabajara.Atlas.Tests;

/// <summary>Synthetic study: one complete 21-video animal, three partial animals across the four groups,
/// pre-imported recordings with raw-run history, an unrelated confirmed animal and a saved plan revision.</summary>
internal sealed class IntakeScenario : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "tabajara-intake-" + Guid.NewGuid().ToString("N"));
    public StudyStore Store { get; }
    public WorkspaceService Workspace { get; }
    public string Manifest { get; }
    public string OtherPath { get; } = "";
    /// <summary>UTF-8 path the manifest parses and hashes but the native decoder cannot open.</summary>
    public string AccentedPath { get; }
    public Dictionary<(string Animal, int Day, int Trial), string> Paths { get; } = [];
    public SavedStudyPlan InitialPlan { get; } = null!;
    public static readonly string[] PrivateText = ["M-COMPLETE", "M-PARTIAL", "M-TM", "M-VM", "OTHER-1", "Comp, A", "videos, set A", "cam2 clip", "partial's videos", "sonda final", "treated wildtype", "vehicle wildtype", "treated mutant", "vehicle mutant"];
    private int _seed;

    public IntakeScenario(bool seedPlan = true)
    {
        Directory.CreateDirectory(Folder);
        Store = new StudyStore(Path.Combine(Folder, "study.morrisdb")); Workspace = new WorkspaceService(Store);
        var setA = Path.Combine(Folder, "videos, set A"); var clip = 0;
        foreach (var stage in StudyIntakeWindow.LucianaProtocol.Stages)
            for (var trial = 1; trial <= stage.Trials; trial++)
                Paths[("M-COMPLETE", stage.Day, trial)] = Video(Path.Combine(setA, stage.Day == 3 && trial == 2 ? "M-OTHER (do not infer).avi" : $"cam2 clip {clip++:00}.avi"));
        foreach (var (day, trial) in new[] { (1, 1), (1, 2), (1, 3), (1, 4), (2, 1), (2, 2), (2, 3) })
            Paths[("M-PARTIAL", day, trial)] = Video(Path.Combine(Folder, "partial's videos", $"day {day} trial {trial}.avi"));
        foreach (var (day, trial) in new[] { (3, 1), (4, 2), (5, 4) }) Paths[("M-TM", day, trial)] = Video(Path.Combine(Folder, "tm", $"tm d{day} t{trial}.avi"));
        Paths[("M-VM", 2, 4)] = Video(Path.Combine(setA, "vm d2 t4.avi"));
        Paths[("M-VM", 6, 1)] = Video(Path.Combine(Folder, "acao, probe", "sonda final.avi"));
        AccentedPath = Video(Path.Combine(Folder, "ação, sonda", "vídeo final.avi"));
        Manifest = Path.Combine(Folder, "intake manifest, v1.csv");
        if (!seedPlan) return;

        var pre1 = Workspace.Import(Paths[("M-COMPLETE", 1, 1)]);
        pre1 = pre1 with { Settings = pre1.Settings with { Cx = .5, Cy = .5, Rx = .4, Ry = .4, ArenaConfirmed = true } }; Store.SaveVideo(pre1);
        var points = Enumerable.Range(0, 10).Select(f => new TrackPoint(f, f / 5d, 20 + f, 20, PointStatus.Tracked, 1)).ToList();
        var run = new AnalysisRun { VideoId = pre1.Id, SourceSha256 = pre1.SourceSha256, Metadata = pre1.Metadata, Settings = pre1.Settings, Points = points,
            Metrics = MetricCalculator.Calculate(points, pre1.Metadata, pre1.Settings), AlgorithmContract = AlgorithmIdentity.Contract, NominalTimingVerified = true };
        Store.SaveRun(run); Store.SaveCorrection(run, new(run.Id, 3, 25, 21, "Synthetic manual correction", DateTime.UtcNow));
        Store.RecordBiologicalEvent(pre1, BiologicalEventKind.WaterEntry, 1, "Synthetic release", "Reviewer");
        Store.SaveProtocolWindow(pre1, new() { RequestedExposureSeconds = 1.6, ReleaseConfirmed = true, ReleaseFrame = 1, WindowConfirmed = true, Reviewer = "Reviewer", Reason = "Synthetic window" });
        Store.RecordReview(run, true); Store.SaveInclusion(pre1, true, "Synthetic inclusion", "Reviewer");
        var original = Path.Combine(Folder, "original copies", "first import.avi"); Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.Copy(Paths[("M-COMPLETE", 1, 2)], original); Workspace.Import(original); // manifest lists a byte-identical copy at another path
        var partialMapped = Workspace.Import(Paths[("M-PARTIAL", 1, 1)]);
        OtherPath = Video(Path.Combine(Folder, "unrelated", "other.avi"));
        var other = Workspace.Import(OtherPath) with { AnimalId = "OTHER-1", AnimalIdConfirmed = true, GroupName = "vehicle mutant", Phase = TrialMode.Probe, Day = 6, Trial = 1 };
        Store.SaveVideo(other);
        var stages = LucianaProtocolPreset.StageIds;
        var plan = new StudyPlan
        {
            Title = "Existing local plan", UnitDesign = StudyUnitDesign.RepeatedTrialsWithinAnimals, RosterConfirmed = true,
            Contexts = Enumerable.Range(1, 6).Select(day => new PlannedContext { Id = stages[day - 1], Day = day, Phase = day == 6 ? TrialMode.Probe : TrialMode.Training, RequestedExposureSeconds = day == 1 ? 60 : 0 }).ToImmutableArray(),
            Animals = [new() { Id = "OTHER-1", Group = "vehicle mutant", Alias = "Other alias", IdentityConfirmed = true, AllocationConfirmed = true }, new() { Id = "M-PARTIAL", Cage = "C-9" }],
            Trials = [new() { Id = "other-d6", AnimalId = "OTHER-1", ContextId = stages[5], Order = 1, RecordingId = other.Id }, new() { Id = "other-d1", AnimalId = "OTHER-1", ContextId = stages[0], Order = 1, MissingReason = "Not filmed" },
                new() { Id = "partial-d1t1", AnimalId = "M-PARTIAL", ContextId = stages[0], Order = 1, RecordingId = partialMapped.Id }, new() { Id = "partial-d1t2", AnimalId = "M-PARTIAL", ContextId = stages[0], Order = 2, MissingReason = "Pending" }]
        };
        InitialPlan = new StudyPlanStore(Store).Save(plan, "Reviewer", "Existing local plan before intake", 0);
        var lines = new List<IntakeLine>();
        foreach (var ((animal, day, trial), path) in Paths)
            lines.Add(animal switch
            {
                "M-COMPLETE" => new(animal, "treated wildtype", day, trial, path, Alias: "Comp, A"),
                "M-PARTIAL" => new(animal, "Vehicle Wildtype", day, trial, path, Cage: "C-9", Litter: day == 2 ? "L1" : ""),
                "M-TM" => new(animal, "treated mutant", day, trial, path, Relative: true),
                _ => new(animal, "vehicle mutant", day, trial, path)
            });
        WriteManifest(System.IO.Path.GetFileName(Manifest), lines);
    }

    public string WriteManifest(string name, (string Animal, string Group, int Day, int Trial, string Path)[] rows) =>
        WriteManifest(name, rows.Select(r => new IntakeLine(r.Animal, r.Group, r.Day, r.Trial, r.Path)));
    public string WriteManifest(string name, IEnumerable<IntakeLine> rows)
    {
        var path = System.IO.Path.Combine(Folder, name); var text = new StringBuilder("animal_id,group,day,trial,video_path,alias,cage,litter\r\n");
        foreach (var r in rows)
            text.Append(string.Join(",", new[] { r.Animal, r.Group, r.Day.ToString(CultureInfo.InvariantCulture), r.Trial.ToString(CultureInfo.InvariantCulture),
                r.Relative ? System.IO.Path.GetRelativePath(Folder, r.Path) : r.Path, r.Alias, r.Cage, r.Litter }.Select(Quote))).Append("\r\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        return path;
    }
    private static string Quote(string value) => value.IndexOfAny([',', '"', ' ', '\'']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    private string Video(string path) { WriteVideo(path, ++_seed); return path; }

    /// <summary>Writes a tiny lossless synthetic clip (via an ASCII temporary path, then moved so Unicode folders work).</summary>
    public static void WriteVideo(string path, int seed)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tabajara-intake-clip-" + Guid.NewGuid().ToString("N") + ".avi");
        using (var writer = new VideoWriter(temp, VideoWriter.FourCC('F', 'F', 'V', '1'), 5, new Size(64, 48)))
        {
            if (!writer.IsOpened()) throw new InvalidOperationException("Synthetic writer unavailable.");
            for (var frame = 0; frame < 10; frame++)
            {
                using var image = new Mat(48, 64, MatType.CV_8UC3, new Scalar(seed % 250, seed / 250 * 40 + 5, 10 + frame * 20));
                Cv2.Circle(image, new Point(20 + frame, 20), 3, Scalar.Black, -1); writer.Write(image);
            }
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.Move(temp, path, true);
    }
    public static string Sha(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public IEnumerable<string> AllVideoFiles() => Directory.EnumerateFiles(Folder, "*.avi", SearchOption.AllDirectories).ToArray();
    public string Dump(string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
        using var reader = command.ExecuteReader(); var text = new StringBuilder();
        while (reader.Read()) { for (var i = 0; i < reader.FieldCount; i++) text.Append(reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString()).Append('|'); text.Append('\n'); }
        return text.ToString();
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed record IntakeLine(string Animal, string Group, int Day, int Trial, string Path, string Alias = "", string Cage = "", string Litter = "", bool Relative = false);
