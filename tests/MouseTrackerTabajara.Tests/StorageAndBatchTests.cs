using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using OpenCvSharp;
using Xunit;

namespace MouseTrackerTabajara.Tests;

public sealed class Scratch : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "MouseTrackerTabajaraTest-" + Guid.NewGuid().ToString("N"));
    public Scratch() => Directory.CreateDirectory(Folder);
    public void Dispose()
    {
        var full = Path.GetFullPath(Folder); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("MouseTrackerTabajaraTest-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid test cleanup target.");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(full, true);
    }
}

public class StorageAndBatchTests
{
    [Fact] public void RawRunsRemainImmutableAcrossCorrectionsAndReopen()
    {
        using var scratch = new Scratch(); var path = Path.Combine(scratch.Folder, "study.morrisdb"); var store = new StudyStore(path);
        var settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, ArenaConfirmed = true };
        var asset = new VideoAsset { SourceSha256 = new string('a', 64), AnimalId = "A", Settings = settings, Metadata = new(200, 200, 10, 2) };
        store.AddOrGet(asset);
        var points = new List<TrackPoint> { new(0, 0, 80, 80, PointStatus.Tracked, 1), new(1, .1, null, null, PointStatus.Missing, 0) };
        var meta = new VideoMetadata(200, 200, 10, 2);
        var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Points = points, Metadata = meta, Settings = settings, NominalTimingVerified = true, Metrics = MetricCalculator.Calculate(points, meta, settings) };
        store.SaveRun(run); store.RecordReview(run, true);
        store.SaveCorrection(run, new(run.Id, 1, 96, 80, "Reference body position", DateTime.UtcNow));
        var reopened = new StudyStore(path); var raw = reopened.LatestRun(asset.Id)!; var effective = reopened.EffectiveRun(raw);
        Assert.Equal(PointStatus.Missing, raw.Points[1].Status); Assert.Equal(PointStatus.Manual, effective.Points[1].Status);
        Assert.False(reopened.Reviewed(run.Id)); Assert.Single(effective.AppliedCorrections);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => reopened.SaveRun(run));
        Assert.Throws<InvalidOperationException>(() => reopened.SaveVideo(asset with { SourceSha256 = new string('b', 64) }));
        Assert.Throws<InvalidOperationException>(() => reopened.SaveRun(run with { Id = "wrong-hash", SourceSha256 = new string('b', 64) }));
        Assert.Throws<ArgumentException>(() => reopened.SaveVideo(asset with { Metadata = new(0, 0, 0, 0) }));
    }
    [Fact] public async Task ThirtyVideoBatchCancelsSafelyResumesAndReusesOnlyMatchingRuns()
    {
        using var scratch = new Scratch(); var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb")); var workspace = new WorkspaceService(store);
        var assets = new List<VideoAsset>();
        for (var animal = 0; animal < 30; animal++)
        {
            var path = Path.Combine(scratch.Folder, $"mouse-{animal:00}.avi");
            using (var writer = new VideoWriter(path, FourCC.MJPG, 10, new Size(200, 200)))
            {
                Assert.True(writer.IsOpened());
                for (var frame = 0; frame < 12; frame++)
                {
                    using var image = new Mat(200, 200, MatType.CV_8UC3, new Scalar(160, 85, 10));
                    Cv2.Circle(image, new Point(60 + animal + frame * 3, 90), 8, new Scalar(5, 5, 5), -1); writer.Write(image);
                }
            }
            var asset = workspace.Import(path) with { Settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, ArenaConfirmed = true } };
            store.SaveVideo(asset); assets.Add(asset);
        }
        // The same recording cannot become a second biological sample.
        var duplicate = workspace.Import(assets[0].Path); Assert.Equal(assets[0].Id, duplicate.Id); Assert.Equal(30, store.Videos().Count);
        using var cancellation = new CancellationTokenSource();
        var interrupted = await workspace.AnalyzeAsync(assets, new InlineProgress<BatchProgress>(p => { if (p.Status == "Completed") cancellation.Cancel(); }), cancellation.Token);
        // Cancel is observed when the first completion is reported. The host may already have admitted the configured decoder cap.
        Assert.True(interrupted.Cancelled);
        var persistedBeforeResume = assets.Where(asset => store.LatestRun(asset.Id) is not null).ToArray();
        Assert.Equal(persistedBeforeResume.Length, interrupted.Completed);
        Assert.InRange(interrupted.Completed, 1, NativeBatchRequest.DefaultParallelism);
        Assert.Empty(interrupted.Errors);
        Assert.Equal(0, interrupted.ActiveDecodersAtCompletion);
        Assert.InRange(interrupted.ObservedMaximumActiveDecoders, 1, interrupted.EffectiveDecoderCap);
        Assert.Equal(NativeBatchRequest.DefaultParallelism, interrupted.MaxDegreeOfParallelism);
        Assert.Contains("not a biological sample size", interrupted.CountInterpretation, StringComparison.Ordinal);
        foreach (var asset in persistedBeforeResume) Assert.Equal(12, store.LatestRun(asset.Id)!.Metrics.TrackedFrames);
        var resumed = await workspace.AnalyzeAsync(assets);
        Assert.Equal(30, resumed.Completed);
        Assert.Equal(persistedBeforeResume.Length, resumed.Reused);
        Assert.Empty(resumed.Errors);
        Assert.Equal(0, resumed.ActiveDecodersAtCompletion);
        Assert.True(resumed.ObservedMaximumActiveDecoders <= resumed.EffectiveDecoderCap);
        var replay = await workspace.AnalyzeAsync(assets);
        Assert.Equal(30, replay.Reused);
        Assert.Equal(0, replay.ObservedMaximumActiveDecoders);
        Assert.Equal(0, replay.ActiveDecodersAtCompletion);
        foreach (var asset in assets) Assert.Equal(12, store.LatestRun(asset.Id)!.Metrics.TrackedFrames);
        var changed = assets[0] with { Settings = assets[0].Settings with { Threshold = 35 } }; store.SaveVideo(changed);
        var changedRun = await workspace.AnalyzeAsync([changed]); Assert.Equal(0, changedRun.Reused); Assert.Empty(changedRun.Errors);
        var latest = store.LatestRun(changed.Id)!;
        store.SaveRun(latest with { Id = Guid.NewGuid().ToString("N"), AlgorithmContract = "legacy-contract" });
        var incompatibleReplay = await workspace.AnalyzeAsync([changed]); Assert.Equal(0, incompatibleReplay.Reused);
    }
    [Fact] public void LegacyStudyOpensInPlaceWithoutCopyingItsWal()
    {
        using var scratch = new Scratch();
        var preferred = Path.Combine(scratch.Folder, StudyLocation.ProductDirectoryName, StudyLocation.StudyFileName);
        var legacy = Path.Combine(scratch.Folder, StudyLocation.LegacyDirectoryName, StudyLocation.LegacyStudyFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        var database = Encoding.UTF8.GetBytes("legacy-database"); var wal = Encoding.UTF8.GetBytes("legacy-wal"); var shm = Encoding.UTF8.GetBytes("legacy-shm");
        File.WriteAllBytes(legacy, database); File.WriteAllBytes(legacy + "-wal", wal); File.WriteAllBytes(legacy + "-shm", shm);
        Assert.True(StudyLocation.DatabasePresent(legacy)); Assert.False(StudyLocation.DatabasePresent(preferred));
        var choice = StudyLocation.Resolve(preferred, legacy);
        Assert.Equal(Path.GetFullPath(legacy), choice.DatabasePath);
        Assert.Contains("not copied", choice.Note, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(preferred)); Assert.False(File.Exists(preferred + "-wal")); Assert.False(File.Exists(preferred + "-shm"));
        Assert.Equal(database, File.ReadAllBytes(legacy)); Assert.Equal(wal, File.ReadAllBytes(legacy + "-wal")); Assert.Equal(shm, File.ReadAllBytes(legacy + "-shm"));
        Directory.CreateDirectory(Path.GetDirectoryName(preferred)!);
        var current = Encoding.UTF8.GetBytes("current-database");
        File.WriteAllBytes(preferred, current);
        var both = StudyLocation.Resolve(preferred, legacy);
        Assert.Equal(Path.GetFullPath(preferred), both.DatabasePath);
        Assert.Contains(Path.GetFullPath(legacy), both.Note);
        Assert.Equal(current, File.ReadAllBytes(preferred));
        Assert.Equal(database, File.ReadAllBytes(legacy)); Assert.Equal(wal, File.ReadAllBytes(legacy + "-wal")); Assert.Equal(shm, File.ReadAllBytes(legacy + "-shm"));
        File.Delete(legacy); File.Delete(legacy + "-wal"); File.Delete(legacy + "-shm");
        var preferredWal = Encoding.UTF8.GetBytes("current-wal"); var preferredShm = Encoding.UTF8.GetBytes("current-shm");
        File.WriteAllBytes(preferred + "-wal", preferredWal); File.WriteAllBytes(preferred + "-shm", preferredShm);
        var currentOnly = StudyLocation.Resolve(preferred, legacy);
        Assert.Equal(Path.GetFullPath(preferred), currentOnly.DatabasePath); Assert.Null(currentOnly.Note);
        Assert.Equal(current, File.ReadAllBytes(preferred)); Assert.Equal(preferredWal, File.ReadAllBytes(preferred + "-wal")); Assert.Equal(preferredShm, File.ReadAllBytes(preferred + "-shm"));
        Assert.False(File.Exists(legacy)); Assert.False(File.Exists(legacy + "-wal")); Assert.False(File.Exists(legacy + "-shm"));
        File.Delete(preferred); File.Delete(preferred + "-wal"); File.Delete(preferred + "-shm");
        var empty = StudyLocation.Resolve(preferred, legacy);
        Assert.Equal(Path.GetFullPath(preferred), empty.DatabasePath); Assert.Null(empty.Note);
        Assert.False(File.Exists(preferred)); Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(preferred + "-wal")); Assert.False(File.Exists(preferred + "-shm"));
        Assert.False(File.Exists(legacy + "-wal")); Assert.False(File.Exists(legacy + "-shm"));
    }
    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-wal", "-shm")]
    public void PreferredOrphanSidecarsFailClosedAndPreserveBytes(params string[] suffixes)
    {
        using var scratch = new Scratch();
        var preferred = Path.Combine(scratch.Folder, StudyLocation.ProductDirectoryName, StudyLocation.StudyFileName);
        var legacy = Path.Combine(scratch.Folder, StudyLocation.LegacyDirectoryName, StudyLocation.LegacyStudyFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(preferred)!);
        var payloads = WriteSidecars(preferred, suffixes);
        Assert.False(StudyLocation.DatabasePresent(preferred));
        StudyLocationChoice? chosen = null;
        var ex = Assert.Throws<StudyRecoveryNeededException>(() => chosen = StudyLocation.Resolve(preferred, legacy));
        Assert.Null(chosen);
        Assert.Equal(Path.GetFullPath(preferred), ex.PreferredPath);
        Assert.Equal(Path.GetFullPath(legacy), ex.LegacyPath);
        Assert.Equal(suffixes.Select(suffix => Path.GetFullPath(preferred + suffix)), ex.OrphanSidecars);
        Assert.Contains("Study recovery is required", ex.Message);
        Assert.Contains("No file was created, copied, renamed, deleted, or recovered.", ex.Message);
        AssertUnchanged(payloads);
        Assert.False(File.Exists(preferred)); Assert.False(File.Exists(legacy));
        Assert.False(Directory.Exists(Path.GetDirectoryName(legacy)!));
    }
    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-wal", "-shm")]
    public void LegacyOrphanSidecarsFailClosedAndPreserveBytes(params string[] suffixes)
    {
        using var scratch = new Scratch();
        var preferred = Path.Combine(scratch.Folder, StudyLocation.ProductDirectoryName, StudyLocation.StudyFileName);
        var legacy = Path.Combine(scratch.Folder, StudyLocation.LegacyDirectoryName, StudyLocation.LegacyStudyFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        var payloads = WriteSidecars(legacy, suffixes);
        Assert.False(StudyLocation.DatabasePresent(legacy));
        StudyLocationChoice? chosen = null;
        var ex = Assert.Throws<StudyRecoveryNeededException>(() => chosen = StudyLocation.Resolve(preferred, legacy));
        Assert.Null(chosen);
        Assert.Equal(suffixes.Select(suffix => Path.GetFullPath(legacy + suffix)), ex.OrphanSidecars);
        Assert.Contains(Path.GetFullPath(legacy), ex.Message);
        AssertUnchanged(payloads);
        Assert.False(File.Exists(preferred)); Assert.False(File.Exists(legacy));
        Assert.False(Directory.Exists(Path.GetDirectoryName(preferred)!));
    }
    [Fact]
    public void IntactStudyBesideTheOtherOrphanFailsClosedWithoutOpeningEither()
    {
        using var scratch = new Scratch();
        var preferred = Path.Combine(scratch.Folder, StudyLocation.ProductDirectoryName, StudyLocation.StudyFileName);
        var legacy = Path.Combine(scratch.Folder, StudyLocation.LegacyDirectoryName, StudyLocation.LegacyStudyFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(preferred)!);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        var intact = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [preferred] = Encoding.UTF8.GetBytes("preferred-intact"),
            [preferred + "-wal"] = Encoding.UTF8.GetBytes("preferred-wal"),
            [preferred + "-shm"] = Encoding.UTF8.GetBytes("preferred-shm")
        };
        var orphan = WriteSidecars(legacy, "-wal", "-shm");
        foreach (var item in intact) File.WriteAllBytes(item.Key, item.Value);
        Assert.True(StudyLocation.DatabasePresent(preferred)); Assert.False(StudyLocation.DatabasePresent(legacy));
        var ex = Assert.Throws<StudyRecoveryNeededException>(() => StudyLocation.Resolve(preferred, legacy));
        Assert.Equal(new[] { Path.GetFullPath(legacy + "-wal"), Path.GetFullPath(legacy + "-shm") }, ex.OrphanSidecars);
        Assert.Contains("Refusing", ex.Message); Assert.Contains(Path.GetFullPath(preferred), ex.Message);
        AssertUnchanged(intact); AssertUnchanged(orphan); Assert.False(File.Exists(legacy));

        foreach (var path in intact.Keys.Concat(orphan.Keys).ToArray()) File.Delete(path);
        intact = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [legacy] = Encoding.UTF8.GetBytes("legacy-intact"),
            [legacy + "-wal"] = Encoding.UTF8.GetBytes("legacy-wal"),
            [legacy + "-shm"] = Encoding.UTF8.GetBytes("legacy-shm")
        };
        orphan = WriteSidecars(preferred, "-shm");
        foreach (var item in intact) File.WriteAllBytes(item.Key, item.Value);
        ex = Assert.Throws<StudyRecoveryNeededException>(() => StudyLocation.Resolve(preferred, legacy));
        Assert.Equal(new[] { Path.GetFullPath(preferred + "-shm") }, ex.OrphanSidecars);
        AssertUnchanged(intact); AssertUnchanged(orphan); Assert.False(File.Exists(preferred));

        File.Delete(legacy);
        var preferredWal = WriteSidecars(preferred, "-wal");
        ex = Assert.Throws<StudyRecoveryNeededException>(() => StudyLocation.Resolve(preferred, legacy));
        Assert.Equal(
            new[] { Path.GetFullPath(preferred + "-wal"), Path.GetFullPath(preferred + "-shm"), Path.GetFullPath(legacy + "-wal"), Path.GetFullPath(legacy + "-shm") },
            ex.OrphanSidecars);
        AssertUnchanged(preferredWal); AssertUnchanged(orphan);
        Assert.Equal(intact[legacy + "-wal"], File.ReadAllBytes(legacy + "-wal"));
        Assert.Equal(intact[legacy + "-shm"], File.ReadAllBytes(legacy + "-shm"));
        Assert.False(File.Exists(preferred)); Assert.False(File.Exists(legacy));
    }
    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-wal", "-shm")]
    public void DirectStudyStoreOrphanSidecarsFailClosedAndPreserveBytes(params string[] suffixes)
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "operator", "chosen.study.db");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var payloads = WriteSidecars(path, suffixes);
        var before = DirectoryFiles(directory);
        StudyStore? store = null;
        var ex = Assert.Throws<StudyRecoveryNeededException>(() => store = new StudyStore(path));
        Assert.Null(store);
        AssertRecoveredSinglePath(ex, path, suffixes);
        AssertUnchanged(payloads);
        Assert.False(File.Exists(path));
        Assert.Equal(before, DirectoryFiles(directory));
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        Assert.False(Path.IsPathRooted(relative));
        var relativeEx = Assert.Throws<StudyRecoveryNeededException>(() => store = new StudyStore(relative));
        Assert.Null(store);
        AssertRecoveredSinglePath(relativeEx, path, suffixes);
        AssertUnchanged(payloads);
        Assert.False(File.Exists(path));
        Assert.Equal(before, DirectoryFiles(directory));
    }
    [Fact]
    public void NewSyntheticStudyReopensWhenPathIsEmptyOrHasNormalSidecars()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "fresh", "synthetic.study.db");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
        var settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, ArenaConfirmed = true };
        var asset = new VideoAsset { SourceSha256 = new string('c', 64), AnimalId = "S", Settings = settings, Metadata = new(200, 200, 10, 2) };
        var created = new StudyStore(path);
        Assert.Equal(Path.GetFullPath(path), created.DatabasePath);
        Assert.True(File.Exists(path));
        Assert.Empty(created.Videos());
        var saved = created.AddOrGet(asset);
        using (var hold = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString()))
        {
            hold.Open();
            using var command = hold.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            command.ExecuteScalar();
            Assert.True(File.Exists(path + "-wal"));
            Assert.True(File.Exists(path + "-shm"));
            var reopened = new StudyStore(path);
            var video = Assert.Single(reopened.Videos());
            Assert.Equal(saved.Id, video.Id);
            Assert.Equal(saved.SourceSha256, video.SourceSha256);
            Assert.Equal("S", video.AnimalId);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var again = new StudyStore(path);
        Assert.Equal(saved.Id, Assert.Single(again.Videos()).Id);
        Assert.Equal(saved.SourceSha256, again.Videos().Single().SourceSha256);
    }
    private static void AssertRecoveredSinglePath(StudyRecoveryNeededException ex, string databasePath, string[] suffixes)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Assert.Equal(fullPath, ex.ExactPath);
        Assert.Equal(fullPath, ex.PreferredPath);
        Assert.Equal(string.Empty, ex.LegacyPath);
        Assert.Equal(suffixes.Select(suffix => fullPath + suffix), ex.OrphanSidecars);
        Assert.Contains("Study recovery is required", ex.Message);
        Assert.Contains("Refusing " + fullPath + " because", ex.Message);
        Assert.DoesNotContain(" and ", ex.Message);
        Assert.Contains("No file was created, copied, renamed, deleted, or recovered.", ex.Message);
        foreach (var sidecar in ex.OrphanSidecars) Assert.Contains(sidecar, ex.Message);
    }
    private static string[] DirectoryFiles(string directory) =>
        Directory.GetFiles(directory).Select(Path.GetFullPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    private static Dictionary<string, byte[]> WriteSidecars(string databasePath, params string[] suffixes)
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var suffix in suffixes)
        {
            var path = databasePath + suffix;
            var bytes = Encoding.UTF8.GetBytes("orphan" + suffix + "-" + Path.GetFileName(databasePath));
            File.WriteAllBytes(path, bytes);
            payloads[path] = bytes;
        }
        return payloads;
    }
    private static void AssertUnchanged(Dictionary<string, byte[]> payloads)
    {
        foreach (var item in payloads) Assert.Equal(item.Value, File.ReadAllBytes(item.Key));
    }
    [Fact] public void SavedAnalysisRoundTripKeepsMeasurementsAndCurrentExportTitle()
    {
        using var scratch = new Scratch();
        var settings = JsonDefaults.Deserialize<TrackingSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "fixtures/sample-settings.json")));
        var hash = StudyStore.SettingsHash(settings);
        Assert.Equal(hash, StudyStore.SettingsHash(JsonDefaults.Deserialize<TrackingSettings>(JsonDefaults.Serialize(settings))));
        Assert.Equal("205d28d0e7906910673150c6df697f9b268a2b4f1222f2d20887818bac6a7a71", hash);
        var points = new List<TrackPoint> { new(0, 0, 10, 12, PointStatus.Tracked, 1), new(1, .1, null, null, PointStatus.Missing, 0) };
        var meta = new VideoMetadata(200, 200, 10, 2);
        var run = new AnalysisRun { SoftwareVersion = "0.1.0-native", AlgorithmContract = AlgorithmIdentity.Contract, Points = points, Metadata = meta, Settings = settings, Metrics = MetricCalculator.Calculate(points, meta, settings) };
        ExportWriter.SaveRun(run, scratch.Folder);
        var html = File.ReadAllText(Path.Combine(scratch.Folder, "report.html"));
        Assert.Contains("Mouse Tracker Tabajara", html); Assert.DoesNotContain("MorrisLab", html);
        var saved = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(Path.Combine(scratch.Folder, "analysis.json")));
        Assert.Equal("0.1.0-native", saved.SoftwareVersion); Assert.Equal(AlgorithmIdentity.Contract, saved.AlgorithmContract);
        Assert.Equal(PointStatus.Tracked, saved.Points[0].Status); Assert.Equal(10, saved.Points[0].X); Assert.Equal(12, saved.Points[0].Y);
        Assert.Equal(PointStatus.Missing, saved.Points[1].Status); Assert.Equal(hash, StudyStore.SettingsHash(saved.Settings));
        Assert.Equal(run.Metrics.Distance, saved.Metrics.Distance); Assert.Equal(run.Metrics.CoveragePercent, saved.Metrics.CoveragePercent);
    }
}
