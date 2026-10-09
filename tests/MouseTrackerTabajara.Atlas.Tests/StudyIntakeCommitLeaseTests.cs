using System.IO;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Atlas.Tests;

public sealed class StudyIntakeCommitLeaseTests
{
    [Fact] public async Task LateTimestampPreservedRewriteRefusesPlanAndRetryLocksSourcesThroughCommit()
    {
        using var study = new IntakeScenario();
        var path = study.Paths[("M-COMPLETE", 1, 1)];
        var bytes = File.ReadAllBytes(path); var info = new FileInfo(path);
        var written = info.LastWriteTimeUtc; var created = info.CreationTimeUtc;
        var plans = new StudyPlanStore(study.Store); var before = plans.Latest()!;
        var preview = await StudyIntakeReconciler.PreviewAsync(study.Store, study.Manifest, StudyIntakeWindow.LucianaProtocol);
        Assert.False(preview.Blocked);
        try
        {
            var refused = await StudyIntakeTests.Apply(study, preview, checkpoint: (stage, _, _) =>
            {
                if (stage == StudyIntakeCheckpoint.BeforePlanSave)
                {
                    var changed = bytes.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(path, changed);
                    File.SetLastWriteTimeUtc(path, written); File.SetCreationTimeUtc(path, created);
                }
                return Task.CompletedTask;
            });
            Assert.Equal(StudyIntakeOutcome.Stale, refused.Outcome);
            Assert.Null(refused.PlanRevisionAfter);
            Assert.Contains("Source bytes", refused.Message);
            Assert.Equal(before.RawJson, plans.Latest()!.RawJson);
            Assert.NotEmpty(refused.RegisteredRecordingIds);
        }
        finally
        {
            File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, written); File.SetCreationTimeUtc(path, created);
        }
        var recovered = await StudyIntakeReconciler.PreviewAsync(study.Store, study.Manifest, StudyIntakeWindow.LucianaProtocol);
        Assert.False(recovered.Blocked); Assert.Equal(0, recovered.PendingImports);
        var checkedInsideCommit = false;
        var accepted = await StudyIntakeTests.Apply(study, recovered, checkpoint: (stage, _, _) =>
        {
            if (stage == StudyIntakeCheckpoint.InPlanTransaction)
            {
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, bytes));
                Assert.Throws<IOException>(() => File.Delete(path));
                Assert.Equal(bytes, File.ReadAllBytes(path)); checkedInsideCommit = true;
            }
            return Task.CompletedTask;
        });
        Assert.Equal(StudyIntakeOutcome.Applied, accepted.Outcome); Assert.True(checkedInsideCommit);
        Assert.Equal(before.Revision + 1, plans.Latest()!.Revision);
        using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(bytes.Length, writable.Length);
    }
}
