using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

public sealed class SplitBoundaryAuditTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ValidationMeasurementTarget.RawAutomatic)]
    [InlineData(ValidationMeasurementTarget.InspectedCorrected)]
    public void FrozenRolesHaveIndependentKnownCountsAndPooledDescriptorCannotBeHeldOutAccuracy(ValidationMeasurementTarget target)
    {
        using var fixture=new S14SplitBoundaryFixture();var result=fixture.Compare(target);var report=result.Report;
        Assert.Equal(260,report.FullSourcePopulation);Assert.Equal(260,report.SampledUniqueFrames);Assert.Equal(259,report.ReferenceRecordedFrames);
        Assert.Equal(target==ValidationMeasurementTarget.RawAutomatic?255:256,report.PositionJoinedFrames);
        Assert.Equal(target==ValidationMeasurementTarget.RawAutomatic?42d/17:5d/16,report.MeanRandomPositionErrorPixels!.Value,10);
        Assert.Equal(target==ValidationMeasurementTarget.RawAutomatic?20:4,report.MaximumPositionErrorPixels);
        foreach(var (role,truth) in S14SplitBoundaryFixture.Truth(target))
        {
            AssertSummary(report,role,truth);
            var ids=result.Plan.Recordings.Where(r=>r.Split==role).Select(r=>r.VideoId).ToHashSet();var rows=report.Frames.Where(f=>ids.Contains(f.VideoId)).ToArray();
            Assert.Equal(truth.Recordings,ids.Count);Assert.Equal(truth.Population,fixture.Assets.Where(a=>ids.Contains(a.Id)).Sum(a=>a.Metadata.FrameCount));Assert.Equal(truth.Sampled,rows.Length);
            Assert.Equal(truth.References,rows.Count(f=>!f.ReferenceIds.IsEmpty));Assert.Equal(truth.PositionJoins,rows.Count(f=>f.PositionErrorPixels.HasValue));Assert.Equal(truth.RandomPositionJoins,rows.Count(f=>f.RandomSample&&f.PositionErrorPixels.HasValue));
            Assert.Equal(truth.MissingReference,rows.Count(f=>f.ReferenceIds.IsEmpty));Assert.Equal(truth.NotJoined,rows.Count(f=>!f.PositionErrorPixels.HasValue));Assert.Equal(truth.ToleranceFailures,rows.Count(f=>f.PositionPass==false));Assert.Equal(truth.FalseAccepted,rows.Count(f=>f.FalseAccepted==true));Assert.Equal(truth.ExcludedFrames,rows.Count(f=>f.Excluded));
            Assert.Equal(truth.Mean,rows.Where(f=>f.RandomSample&&f.PositionErrorPixels.HasValue).Average(f=>f.PositionErrorPixels!.Value),10);Assert.Equal(truth.Maximum,rows.Where(f=>f.PositionErrorPixels.HasValue).Max(f=>f.PositionErrorPixels!.Value),10);
            output.WriteLine($"{target} {role}: recordings={truth.Recordings},population/sample={truth.Population}/{truth.Sampled},references={truth.References},position/randomjoins={truth.PositionJoins}/{truth.RandomPositionJoins},missing/notjoined={truth.MissingReference}/{truth.NotJoined},failures={truth.ToleranceFailures},falseaccepted={truth.FalseAccepted},excludedframes={truth.ExcludedFrames},mean/max={truth.Mean:R}/{truth.Maximum:R}");
        }
        Assert.Equal(400,result.Plan.Recordings.Length*result.Plan.DifficultIntervalsPerRecording);Assert.Equal(4,result.Sampling.Cases.Count(c=>!c.RandomSample));Assert.All(result.Sampling.Cases.Where(c=>c.RandomSample),c=>Assert.Equal(1,c.InclusionProbability));
        var independentRows=report.Frames.Where(f=>f.VideoId==fixture.Assets[1].Id).ToArray();Assert.True(independentRows.Single(f=>f.Frame==16).Disagreement);Assert.Null(independentRows.Single(f=>f.Frame==16).PositionErrorPixels);Assert.True(independentRows.Single(f=>f.Frame==17).FalseAccepted);Assert.Null(independentRows.Single(f=>f.Frame==18).PositionErrorPixels);
        Assert.Equal(target==ValidationMeasurementTarget.RawAutomatic,independentRows.Single(f=>f.Frame==19).MissedVisible);Assert.All(report.Endpoints.Where(e=>e.VideoId==fixture.Assets[1].Id),e=>Assert.Null(e.WithinTolerances));
        using var json=JsonDocument.Parse(JsonDefaults.Serialize(report));output.WriteLine("Public serialized split_summaries present="+json.RootElement.TryGetProperty("split_summaries",out _));
        output.WriteLine($"Pooled conditional mean={report.MeanRandomPositionErrorPixels:R}; independent raw expected110/7≈15.7142857; source/reference/sampling hashes retained; requested/realizedpurposive400/4. Synthetic tooling only.");
    }
    [Fact] public void PublicReportMustExposeThreeDeclaredSplitSummaries()
    {
        using var fixture=new S14SplitBoundaryFixture();var result=fixture.Compare(ValidationMeasurementTarget.RawAutomatic);
        using var json=JsonDocument.Parse(JsonDefaults.Serialize(result.Report));
        Assert.True(json.RootElement.TryGetProperty("split_summaries",out var summaries),"BASELINE DEFECT: current report exposes only pooled error; split_summaries is absent. This independent conformance probe intentionally fails until the accepted owned implementation is hydrated.");
        Assert.Equal(JsonValueKind.Array,summaries.ValueKind);Assert.Equal(3,summaries.GetArrayLength());
        output.WriteLine(summaries.GetRawText()); // The final accepted summary's actual contract will be checked after hydration, not guessed here.
    }
    [Fact] public void UndeclaredRolesStayEmptyAndCannotBorrowAnIndependentScore()
    {
        using var fixture=new S14SplitBoundaryFixture();var result=fixture.Compare(ValidationMeasurementTarget.RawAutomatic,ValidationSplit.IndependentValidation);
        Assert.Equal(40,result.Report.FullSourcePopulation);Assert.Equal(40,result.Report.SampledUniqueFrames);Assert.Equal(35,result.Report.PositionJoinedFrames);Assert.Equal(110d/7,result.Report.MeanRandomPositionErrorPixels!.Value,10);
        Assert.All(result.Plan.Recordings,r=>Assert.Equal(ValidationSplit.IndependentValidation,r.Split));
        foreach(var role in new[]{ValidationSplit.Development,ValidationSplit.Unspecified})Assert.Empty(result.Plan.Recordings.Where(r=>r.Split==role));
        AssertSummary(result.Report,ValidationSplit.IndependentValidation,S14SplitBoundaryFixture.Truth(ValidationMeasurementTarget.RawAutomatic)[ValidationSplit.IndependentValidation]);
        foreach(var role in new[]{ValidationSplit.Development,ValidationSplit.Unspecified})
        { var summary=result.Report.SplitSummaries.Single(s=>s.Role==role); Assert.Equal(0,summary.RecordingCount); Assert.Equal(0,summary.FullSourceFrames);Assert.Equal(0,summary.SampledFrames);Assert.Equal(0,summary.ReferenceRecordedFrames);Assert.Equal(0,summary.PositionJoinedFrames);Assert.Equal(0,summary.RandomPositionJoinedFrames);Assert.Equal(0,summary.MissingReferenceFrames);Assert.Equal(0,summary.NotJoinedFrames);Assert.Equal(0,summary.ToleranceFailureFrames);Assert.Equal(0,summary.FalseAcceptedFrames);Assert.Equal(0,summary.ExcludedRecordings);Assert.Equal(0,summary.ExcludedSampledFrames);Assert.Equal(0,summary.RequestedPurposiveIntervals);Assert.Equal(0,summary.RealizedPurposiveIntervals);Assert.Null(summary.MeanRandomPositionErrorPixels);Assert.Null(summary.MaximumPositionErrorPixels); }
        output.WriteLine("Independent-only fixture: independent2recordings/40population/40sample/35joins/mean110/7; development and unspecified0recordings/0population/0sample/0joins and null means/maxima required in final public summaries.");
    }
    internal static void AssertSummary(ValidationComparisonReport report,ValidationSplit role,S14SplitTruth truth)
    {
        var s=report.SplitSummaries.Single(s=>s.Role==role);Assert.Equal(role,s.Role);Assert.Equal(truth.Recordings,s.RecordingCount);Assert.Equal(truth.Population,s.FullSourceFrames);Assert.Equal(truth.Sampled,s.SampledFrames);Assert.Equal(truth.References,s.ReferenceRecordedFrames);Assert.Equal(truth.PositionJoins,s.PositionJoinedFrames);Assert.Equal(truth.RandomPositionJoins,s.RandomPositionJoinedFrames);Assert.Equal(truth.MissingReference,s.MissingReferenceFrames);Assert.Equal(truth.NotJoined,s.NotJoinedFrames);Assert.Equal(truth.ToleranceFailures,s.ToleranceFailureFrames);Assert.Equal(truth.FalseAccepted,s.FalseAcceptedFrames);Assert.Equal(role==ValidationSplit.IndependentValidation?1:0,s.ExcludedRecordings);Assert.Equal(truth.ExcludedFrames,s.ExcludedSampledFrames);Assert.Equal(truth.Mean,s.MeanRandomPositionErrorPixels!.Value,10);Assert.Equal(truth.Maximum,s.MaximumPositionErrorPixels!.Value,10);Assert.Equal(truth.Recordings*100,s.RequestedPurposiveIntervals);Assert.Equal(truth.Recordings,s.RealizedPurposiveIntervals);Assert.True(s.SourceTooShortForDistinctIntervals);Assert.Equal(role==ValidationSplit.IndependentValidation?1:0,s.AmbiguousFrames);Assert.Equal(role==ValidationSplit.IndependentValidation?1:0,s.UnresolvedDisagreementFrames);Assert.Equal(role==ValidationSplit.IndependentValidation?1:0,s.NotVisibleReferenceFrames);
    }
    [Fact] public void LegacyDefaultSummariesStayHeldAndSafelySerializeWithoutInventingValues()
    {
        using var fixture=new S14SplitBoundaryFixture();var result=fixture.Compare(ValidationMeasurementTarget.RawAutomatic);
        var node=JsonNode.Parse(JsonDefaults.Serialize(result.Report))!.AsObject();node.Remove("split_summaries");node["mean_random_position_error_pixels"]=999;
        var legacy=JsonDefaults.Deserialize<ValidationComparisonReport>(node.ToJsonString());Assert.True(legacy.SplitSummaries.IsDefault);Assert.False(ValidationSplitSummaries.TryRead(legacy,out _,out var hold));Assert.Contains("absent",hold);
        Assert.DoesNotContain(ValidationSplitSummaryView.Lines(legacy),line=>line.StartsWith("Declared role",StringComparison.Ordinal));
        string? serialized=null;var error=Record.Exception(()=>serialized=JsonDefaults.Serialize(legacy));Assert.Null(error);
        var again=JsonDefaults.Deserialize<ValidationComparisonReport>(serialized!);Assert.False(ValidationSplitSummaries.TryRead(again,out _,out _));Assert.Equal(999,again.MeanRandomPositionErrorPixels);
    }
    [Fact] public void IndependentNativeSplitControlsSaveBlindReplayAndHoldLegacyWithoutFabrication()
    {
        Exception? error=null;var lines=new List<string>();var thread=new Thread(()=>{try{S14SplitBoundaryNativeProbe.Run(lines);}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromMinutes(3)));foreach(var line in lines)output.WriteLine(line);if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    [Fact] public void ResolvedConsensusWithMissingAutomaticPositionIsNotAnUnresolvedReference()
    {
        using var fixture=new S14SplitBoundaryFixture();var saved=fixture.Compare(ValidationMeasurementTarget.RawAutomatic);var history=new MouseTrackerTabajara.Storage.ValidationHistoryStore(fixture.Store.DatabasePath);
        var hash=IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(saved.Sampling));var first=new IndependentReference(fixture.Assets[1].Id,19,ReferenceReviewerLane.First,"Independent first reviewer",ReferenceVisibility.Visible,40,50,ReferenceEvent.None,"Synthetic first truth",[]);
        var firstId=history.History().Last(h=>h.EntityId==IndependentValidationComparison.ReferenceKey(hash,first)).Id;
        var second=first with {Lane=ReferenceReviewerLane.Second,Reviewer="Independent second reviewer",X=70,Reason="Synthetic disagreement at missing automatic row"};var key=IndependentValidationComparison.ReferenceKey(hash,second);var secondEntry=history.Save("reference",key,JsonDefaults.Serialize(second),second.Reviewer,second.Reason,1);
        var consensus=first with {Lane=ReferenceReviewerLane.Consensus,Reviewer="Independent consensus reviewer",Reason="Adjudicated synthetic visible center; automatic row remains missing",ConsensusReferenceIds=[firstId,secondEntry.Id]};
        history.Save("reference",IndependentValidationComparison.ReferenceKey(hash,consensus),JsonDefaults.Serialize(consensus),consensus.Reviewer,consensus.Reason,0);
        var report=IndependentValidationComparison.Compare(IndependentValidationSnapshot.Read(fixture.Store.DatabasePath),saved.Plan,saved.Sampling);var frame=report.Frames.Single(f=>f.VideoId==fixture.Assets[1].Id&&f.Frame==19);
        Assert.True(frame.Disagreement);Assert.Equal(3,frame.ReferenceIds.Length);Assert.Contains(secondEntry.Id,frame.ReferenceIds);Assert.True(frame.MissedVisible);Assert.Null(frame.PositionErrorPixels);Assert.DoesNotContain(frame.Holds,h=>h.Contains("unresolved",StringComparison.OrdinalIgnoreCase));
        var role=report.SplitSummaries.Single(s=>s.Role==ValidationSplit.IndependentValidation);Assert.Equal(1,role.UnresolvedDisagreementFrames);Assert.Equal(5,role.NotJoinedFrames);Assert.Equal(35,role.PositionJoinedFrames);
        foreach(var (split,truth) in S14SplitBoundaryFixture.Truth(ValidationMeasurementTarget.RawAutomatic))AssertSummary(report,split,truth);
        output.WriteLine("PASS valid current first/second/consensus at raw-missing frame19 remains an unjoined position, not another unresolved reference. Only original unresolved frame16 counts; predeclared role counts/errors unchanged.");
    }
    [Fact] public void FrozenDevelopmentSessionAndByteIdentityCannotBecomeIndependentThroughRelabeling()
    {
        using var fixture=new S14SplitBoundaryFixture();var result=fixture.Compare(ValidationMeasurementTarget.RawAutomatic);
        Assert.Throws<ArgumentException>(()=>(result.Plan with {Recordings=result.Plan.Recordings.Select(r=>r.VideoId==fixture.Assets[1].Id?r with {Session=result.Plan.Recordings[0].Session}:r).ToImmutableArray()}).Validate());
        var snapshot=IndependentValidationSnapshot.Read(fixture.Store.DatabasePath);var copied=fixture.Assets[0] with {Id="S14-reconstructed-copy"};
        var aliased=snapshot with {Imported=snapshot.Imported.Add(copied)};
        var candidate=result.Plan with {Id="S14-byte-relabel",Recordings=[new(copied.Id,"fresh-session",ValidationSplit.IndependentValidation,false,true,"synthetic-probe","")]};
        Assert.Throws<ArgumentException>(()=>aliased.ValidatePlan(candidate));
    }
}
