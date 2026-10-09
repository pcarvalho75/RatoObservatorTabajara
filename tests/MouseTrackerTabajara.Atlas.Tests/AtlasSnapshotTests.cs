using System.IO;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Atlas.Tests;

public class AtlasSnapshotTests
{
    [Fact] public async Task FrozenReadNeverMixesVideoAndRunVersionsAcrossConcurrentAtomicWrites()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-atlas-snapshot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var database = Path.Combine(folder, "synthetic.morrisdb"); var store = new StudyStore(database);
            var asset = new VideoAsset { Id = "video", SourceSha256 = new string('a', 64), Metadata = new(300, 200, 10, 40), Settings = new() };
            store.SaveVideo(asset);
            var run = new AnalysisRun { Id = "run", VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings,
                Points = [new(5, .5, 100, 100, PointStatus.Tracked, 1)], NominalTimingVerified = true };
            store.SaveRun(run); var original = AtlasSnapshot.Read(database);
            using var start = new ManualResetEventSlim();
            var writer = Task.Run(() =>
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, DefaultTimeout = 5 }.ToString()); connection.Open(); start.Wait();
                for (var i = 0; i < 100; i++)
                {
                    var settings = asset.Settings with { Threshold = 30 + i % 2 };
                    using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "UPDATE videos SET json=$asset WHERE id='video'; UPDATE runs SET json=$run WHERE id='run'";
                    command.Parameters.AddWithValue("$asset", JsonDefaults.Serialize(asset with { Settings = settings }));
                    command.Parameters.AddWithValue("$run", JsonDefaults.Serialize(run with { Settings = settings })); command.ExecuteNonQuery(); transaction.Commit();
                }
            });
            start.Set();
            for (var i = 0; i < 100; i++)
            {
                var frozen = AtlasSnapshot.Read(database); var row = Assert.Single(frozen.Recordings);
                Assert.Equal(row.Asset.Settings.Threshold, row.RawRun!.Settings.Threshold);
                Assert.Single(frozen.Tables["videos"]); Assert.Single(frozen.Rows("runs"));
                Assert.Equal(AtlasInspectionState.NotRecorded, row.InspectionState);
            }
            await writer;
            Assert.Equal(30, original.Recordings[0].Asset.Settings.Threshold);
            Assert.Equal(30, original.Recordings[0].RawRun!.Settings.Threshold);
            Assert.False(original.IsCurrent());
            Assert.Equal(new[] { 5 }, original.Recordings[0].RawRun!.Points.Select(p => p.Frame));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO reviews(run_id,confirmed,created_utc,contract_json) VALUES('run',1,'2026-10-08T00:00:00Z',$contract)";
                command.Parameters.AddWithValue("$contract", "{\"run_id\":\"run\",\"source_sha256\":\"unvalidated\"}"); command.ExecuteNonQuery();
                var withContract = AtlasSnapshot.Read(database);
                Assert.Equal(AtlasInspectionState.Stale, withContract.Recordings[0].InspectionState);
                Assert.Single(withContract.Rows("reviews")); Assert.NotNull(withContract.Rows("reviews")[0].Value("contract_json"));
                command.CommandText = "UPDATE reviews SET contract_json=$contract"; command.Parameters["$contract"].Value = "{\"run_id\":\"stale-run\"}"; command.ExecuteNonQuery();
                Assert.False(withContract.IsCurrent());
                Assert.Contains(AtlasSnapshot.Read(database).Recordings[0].Holds, h => h.Contains("Scientific approval withheld"));
                var material = AtlasSnapshot.Read(database).Recordings[0].RawRun!;
                string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                command.Parameters["$contract"].Value = System.Text.Json.JsonSerializer.Serialize(new
                { run_id = material.Id, video_id = asset.Id, source_sha256 = asset.SourceSha256, settings_hash = Hash(JsonDefaults.Serialize(material.Settings)), correction_fingerprint = Hash("") });
                command.ExecuteNonQuery();
                var recorded = AtlasSnapshot.Read(database).Recordings[0]; Assert.Equal(AtlasInspectionState.ContractRecordedUnvalidated, recorded.InspectionState);
                Assert.Contains(recorded.Holds, h => h.Contains("Scientific approval withheld"));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
}
