using MouseTrackerTabajara.Core;
using Xunit;

namespace MouseTrackerTabajara.Tests;

public class ConsequenceReviewQueueTests
{
    private static TrackPoint Good(int frame, int x = 20, double area = 100) => new(frame, frame / 10d, x, 50, PointStatus.Tracked, 1, Area: area);
    private static TrackPoint Unknown(int frame, PointStatus status = PointStatus.Missing) => new(frame, frame / 10d, null, null, status, 0);
    private static AnalysisRun Run(params TrackPoint[] points) => new()
    {
        Metadata = new(100, 100, 10, 1000), Points = points.ToList(),
        Settings = new() { Cx = .5, Cy = .5, Rx = .4, Ry = .4, MaxJump = 10, Platform = new(.5, .5, .05) }
    };

    [Fact] public void EarlierHistoryPrecedesCrossingJumpAndLaterGap()
    {
        var run = Run(Enumerable.Range(100, 101).Select(f => f == 110 ? Unknown(f, PointStatus.Ambiguous) :
            f == 190 ? Unknown(f) : Good(f, f == 150 ? 50 : 20 + f % 2)).ToArray());
        var critical = MetricCalculator.ReviewQueue(run).Where(m => !m.RandomAudit).ToList();
        Assert.Contains("earlier visit", critical[0].Reason);
        Assert.Contains("boundary crossing", critical[1].Reason);
        Assert.Contains(critical, m => m.Reason.Contains("Accepted position jump"));
        Assert.Contains("occupancy", critical[^1].Reason);
        Assert.All(critical, m => { Assert.InRange(m.StartFrame, 100, 200); Assert.InRange(m.EndFrame, m.StartFrame, 200); });
        Assert.Equal(critical.Count, critical.Select(m => (m.StartFrame, m.EndFrame)).Distinct().Count());
    }

    [Fact] public void UnknownPositionsNeverCreateCrossingsOrMotion()
    {
        var queue = MetricCalculator.ReviewQueue(Run(Good(0), Unknown(1), Good(2, 50)));
        Assert.DoesNotContain(queue, m => m.Reason.Contains("boundary crossing") || m.Reason.Contains("Accepted position jump"));
        Assert.Contains(queue, m => m.Reason.Contains("earlier visit"));
    }

    [Fact] public void NoObservedVisitKeepsAllGapsEventCritical()
    {
        Assert.Contains(MetricCalculator.ReviewQueue(Run(Good(0), Unknown(1), Good(2))), m => m.Reason.Contains("earlier visit"));
        Assert.DoesNotContain(MetricCalculator.ReviewQueue(Run(Good(0), Unknown(1)) with { Settings = new() }), m => m.Reason.Contains("earlier visit"));
    }

    [Fact] public void AreaAndStationarityRemainInspectionCues()
    {
        var points = Enumerable.Range(40, 30).Select(f => Good(f, area: f < 50 ? 100 : 300)).ToArray();
        var queue = MetricCalculator.ReviewQueue(Run(points));
        var area = queue.FindIndex(m => m.Reason.Contains("Contour area"));
        var stationary = queue.FindIndex(m => m.Reason.Contains("Persistent stationary"));
        Assert.True(area >= 0 && stationary > area);
        Assert.Contains("artifact is not established", queue[stationary].Reason);
    }

    [Fact] public void AuditsAreContinuousAcceptedDistinctAndRetainBudgetBesideCriticalItems()
    {
        var points = Enumerable.Range(100, 300).Select(f => f % 50 == 0 ? Unknown(f) : Good(f, 20 + f % 10)).ToArray();
        var run = Run(points);
        var audits = MetricCalculator.ReviewQueue(run).Where(m => m.RandomAudit).ToArray();
        Assert.Equal(5, audits.Length);
        Assert.Contains(MetricCalculator.ReviewQueue(run), m => !m.RandomAudit);
        foreach (var audit in audits)
        {
            var segment = points.Where(p => p.Frame >= audit.StartFrame && p.Frame <= audit.EndFrame).ToArray();
            Assert.Equal(audit.EndFrame - audit.StartFrame + 1, segment.Length);
            Assert.All(segment, p => Assert.True(p.Accepted));
        }
        for (var i = 0; i < audits.Length; i++) for (var j = i + 1; j < audits.Length; j++)
            Assert.True(audits[i].EndFrame < audits[j].StartFrame || audits[j].EndFrame < audits[i].StartFrame);
    }

    [Fact] public void IdentityAndLabelsCannotInfluenceReplay()
    {
        var run = Run(Enumerable.Range(0, 300).Select(f => Good(f, 20 + f % 10)).ToArray());
        var expected = MetricCalculator.ReviewQueue(run);
        var relabeled = run with { Id = "treated-animal", VideoId = "drug-file.mp4", SourceSha256 = "informative-label", CreatedUtc = DateTime.MinValue, SoftwareVersion = "label" };
        Assert.Equal(expected, MetricCalculator.ReviewQueue(relabeled));
        Assert.Equal(expected, MetricCalculator.ReviewQueue(run));
    }

    [Fact] public void EmptyShortAndAllUnknownAreHonest()
    {
        Assert.Empty(MetricCalculator.ReviewQueue(new()));
        Assert.Single(MetricCalculator.ReviewQueue(Run(Good(100))), m => m.RandomAudit && m.StartFrame == 100 && m.EndFrame == 100);
        var unknown = MetricCalculator.ReviewQueue(Run(Unknown(100), Unknown(101, PointStatus.Jump)));
        Assert.DoesNotContain(unknown, m => m.RandomAudit);
        Assert.Contains(unknown, m => m.Reason.Contains("Rejected jump"));
        Assert.Throws<ArgumentException>(() => MetricCalculator.ReviewQueue(Run(Good(1), Good(3))));
        Assert.Throws<ArgumentException>(() => MetricCalculator.ReviewQueue(Run(Good(-1))));
        Assert.Throws<ArgumentException>(() => MetricCalculator.ReviewQueue(Run(Good(1000))));
    }
}
