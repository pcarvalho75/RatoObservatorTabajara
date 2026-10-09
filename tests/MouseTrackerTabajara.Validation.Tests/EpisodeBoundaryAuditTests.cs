using System.Collections.Immutable;
using System.IO;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

public sealed class EpisodeBoundaryAuditTests(ITestOutputHelper output)
{
    private sealed class EpisodeAuditSourceSet
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "tabajara-episode-boundary-audit-" + Guid.NewGuid().ToString("N"));
        public StudyStore Store { get; }
        public EpisodeAuditSourceSet(int population = 100, bool secondSource = false)
        {
            Directory.CreateDirectory(Folder); Store = new(Path.Combine(Folder, "study.db"));
            foreach (var ordinal in Enumerable.Range(0, secondSource ? 2 : 1))
            {
                var id = ordinal == 0 ? "a-source" : "z-source"; var path = Path.Combine(Folder, id + ".avi");
                using (var writer = new VideoWriter(path, VideoWriter.FourCC('F','F','V','1'), 10, new Size(64,64)))
                {
                    Assert.True(writer.IsOpened()); for (var frame = 0; frame < population; frame++) { using var image = new Mat(64,64,MatType.CV_8UC3,new Scalar(80 + ordinal * 20, 120 + frame % 80, 200)); writer.Write(image); }
                }
                var asset = new VideoAsset { Id = id, Path = path, SourceSha256 = MouseTrackerTabajara.Tracking.OpenCvTracker.HashFile(path), Metadata = MouseTrackerTabajara.Tracking.OpenCvTracker.Probe(path), Settings = new() { Cx=.5,Cy=.5,Rx=.4,Ry=.4,ArenaConfirmed=true } };
                Assert.Equal(population, asset.Metadata.FrameCount); Store.SaveVideo(asset);
            }
            Assert.Equal(secondSource ? 2 : 1, Store.Videos().Select(a => a.SourceSha256).Distinct().Count());
        }
        public IndependentValidationSnapshot Snapshot(IEnumerable<int> failures, bool includeRun = true)
        {
            var snapshot = IndependentValidationSnapshot.Read(Store.DatabasePath); if (!includeRun) return snapshot;
            var rejected = failures.ToHashSet(); var runs = snapshot.Imported.Select(asset =>
            {
                var points = Enumerable.Range(0,asset.Metadata.FrameCount).Select(frame => rejected.Contains(frame) && asset.Id == "a-source" ? new TrackPoint(frame,frame/10d,null,null,PointStatus.Missing,0) : new TrackPoint(frame,frame/10d,32,32,PointStatus.Tracked,1)).ToList();
                var raw = new AnalysisRun { Id = "synthetic-"+asset.Id, VideoId=asset.Id, SourceSha256=asset.SourceSha256, Metadata=asset.Metadata, Settings=asset.Settings, Points=points, Metrics=MetricCalculator.Calculate(points,asset.Metadata,asset.Settings), NominalTimingVerified=true, AlgorithmContract=AlgorithmIdentity.Contract };
                var json = JsonDefaults.Serialize(raw);
                return new ValidationFrozenRun(asset.Id,asset.SourceSha256,JsonDefaults.Serialize(asset),json,[],IndependentValidationSnapshot.Hash(json),MaterialFingerprint.Hash([]),raw.AlgorithmContract,raw.SoftwareVersion,"synthetic sampling-only comparator receipt",[]);
            }).ToImmutableArray();
            return snapshot with { Runs=runs };
        }
        public IndependentValidationPlan Plan(int budget = 3, int interval = 10, int seed = 123, double second = .5) => new() { Id="independent-episode-audit",SyntheticToolingOnly=true,RandomFramesPerRecording=10,DifficultIntervalsPerRecording=budget,IntervalFrames=interval,Seed=seed,SecondReviewFraction=second,
            Recordings=Store.Videos().Select(asset=>new ValidationRecording(asset.Id,"synthetic-session-"+asset.Id,ValidationSplit.Development,true,false,"synthetic protocol","opaque allocation")).ToImmutableArray() };
    }
    private static ValidationSamplingCase[] Difficult(ValidationSampling sample,string id="a-source") => sample.Cases.Where(c=>!c.RandomSample&&c.VideoId==id).ToArray();
    private static bool Hits(ValidationSamplingCase sample,int first,int last)=>sample.StartFrame<=last&&sample.EndFrame>=first;
    private static string RandomMaterial(ValidationSampling sample,string id)=>string.Join(";",sample.Cases.Where(c=>c.RandomSample&&c.VideoId==id).Select(c=>$"{c.StartFrame}:{c.InclusionProbability:R}:{c.PopulationFrames}:{c.SecondReviewRequired}"));
    [Fact] public void BaselineObservationRecordsBoundaryBudgetAndLaterRandomCoupling()
    {
        var sources=new EpisodeAuditSourceSet(secondSource:true);var plan=sources.Plan();
        var dense=IndependentValidationSampling.Generate(sources.Snapshot(Enumerable.Range(0,30).Concat(Enumerable.Range(80,10))),plan);
        var isolated=IndependentValidationSampling.Generate(sources.Snapshot([0,40,80]),plan);
        output.WriteLine($"evidence={sources.Folder}; population=100; interval=10; feasible-distinct=91; requested=3; observed={Difficult(dense).Length}; late80..89-hit={Difficult(dense).Any(c=>Hits(c,80,89))}; intervals="+string.Join(";",Difficult(dense).Select(c=>$"{c.StartFrame}..{c.EndFrame}")));
        output.WriteLine("dense later random="+RandomMaterial(dense,"z-source"));output.WriteLine("isolated later random="+RandomMaterial(isolated,"z-source"));
        output.WriteLine("later-random-identical="+(RandomMaterial(dense,"z-source")==RandomMaterial(isolated,"z-source")));
        var legacy=LegacySampling();var legacyJson=JsonDefaults.Serialize(legacy);var legacyHash=IndependentValidationSnapshot.Hash(legacyJson);var reference=new IndependentReference("legacy-source",2,ReferenceReviewerLane.First,"legacy reviewer",ReferenceVisibility.Visible,32,32,ReferenceEvent.None,"legacy reason",[]);
        output.WriteLine("legacy-sampling-hash="+legacyHash+"; legacy-reference-key="+IndependentValidationComparison.ReferenceKey(legacyHash,reference));File.WriteAllText(Path.Combine(sources.Folder,"legacy-sampling.json"),legacyJson);
        File.WriteAllText(Path.Combine(sources.Folder,"baseline-dense-sampling.json"),JsonDefaults.Serialize(dense));File.WriteAllText(Path.Combine(sources.Folder,"baseline-isolated-sampling.json"),JsonDefaults.Serialize(isolated));
    }
    [Theory] [InlineData(ValidationMeasurementTarget.RawAutomatic)] [InlineData(ValidationMeasurementTarget.InspectedCorrected)]
    public void DistinctBudgetRepresentsEarlyAndLateEpisodesAtBoundary(ValidationMeasurementTarget target)
    {
        var sources=new EpisodeAuditSourceSet();var plan=sources.Plan() with{MeasurementTarget=target};var snapshot=sources.Snapshot(Enumerable.Range(0,30).Concat(Enumerable.Range(80,10)));
        var sample=IndependentValidationSampling.Generate(snapshot,plan);var intervals=Difficult(sample);
        Assert.Equal(3,intervals.Length);Assert.Equal(3,intervals.Select(c=>(c.StartFrame,c.EndFrame)).Distinct().Count());Assert.Contains(intervals,c=>Hits(c,0,29));Assert.Contains(intervals,c=>Hits(c,80,89));
        Assert.All(intervals,c=>{Assert.Null(c.InclusionProbability);Assert.Equal(100,c.PopulationFrames);Assert.InRange(c.StartFrame,0,90);Assert.Equal(9,c.EndFrame-c.StartFrame);Assert.False(string.IsNullOrWhiteSpace(c.SelectionReason));});
        Assert.Equal(JsonDefaults.Serialize(sample),JsonDefaults.Serialize(IndependentValidationSampling.Generate(snapshot,plan)));
    }
    [Fact] public void LaterRecordingRandomFramesAndReviewFlagsDoNotDependOnEarlierFailureLayout()
    {
        var sources=new EpisodeAuditSourceSet(secondSource:true);var plan=sources.Plan();var dense=sources.Snapshot(Enumerable.Range(0,30).Concat(Enumerable.Range(80,10)));var isolated=sources.Snapshot([0,40,80]);
        var first=IndependentValidationSampling.Generate(dense,plan);var second=IndependentValidationSampling.Generate(isolated,plan);
        Assert.Equal(RandomMaterial(first,"z-source"),RandomMaterial(second,"z-source"));
        Assert.Equal(JsonDefaults.Serialize(first.Cases),JsonDefaults.Serialize(IndependentValidationSampling.Generate(dense,plan with{Recordings=plan.Recordings.Reverse().ToImmutableArray()}).Cases));
        Assert.All(first.Cases.Where(c=>c.RandomSample),c=>{Assert.Equal(.1,c.InclusionProbability);Assert.Equal(c.StartFrame,c.EndFrame);Assert.Equal(100,c.PopulationFrames);});
    }
    [Fact] public void MoreEpisodesThanSlotsCoverSeparatedSourceRegions()
    {
        var sources=new EpisodeAuditSourceSet();var failures=new[]{2,12,22,32,42,52,62,72,92};var sample=IndependentValidationSampling.Generate(sources.Snapshot(failures),sources.Plan(interval:4));var intervals=Difficult(sample);
        Assert.Equal(3,intervals.Length);Assert.Contains(intervals,c=>Hits(c,2,2));Assert.Contains(intervals,c=>Hits(c,92,92));Assert.Equal(3,failures.Count(f=>intervals.Any(c=>Hits(c,f,f))));
    }
    [Theory] [InlineData("none")] [InlineData("all")] [InlineData("unanalysed")]
    public void FeasibleFallbackFillsBudgetWithoutOnlyGoodSampling(string mode)
    {
        var sources=new EpisodeAuditSourceSet();var snapshot=sources.Snapshot(mode=="all"?Enumerable.Range(0,100):[],mode!="unanalysed");var sample=IndependentValidationSampling.Generate(snapshot,sources.Plan());
        Assert.Equal(3,Difficult(sample).Length);Assert.Equal(3,Difficult(sample).Select(c=>(c.StartFrame,c.EndFrame)).Distinct().Count());Assert.All(Difficult(sample),c=>Assert.Null(c.InclusionProbability));
        Assert.Equal(10,sample.Cases.Count(c=>c.RandomSample));Assert.All(sample.Cases.Where(c=>c.RandomSample),c=>Assert.Equal(.1,c.InclusionProbability));
    }
    [Fact] public void TrulySmallSourceHasOneFeasibleIntervalAndFullSourceRandomProbability()
    {
        var sources=new EpisodeAuditSourceSet(5);var sample=IndependentValidationSampling.Generate(sources.Snapshot(Enumerable.Range(0,5)),sources.Plan(interval:10));var interval=Assert.Single(Difficult(sample));
        Assert.Equal((0,4),(interval.StartFrame,interval.EndFrame));Assert.Null(interval.InclusionProbability);Assert.Equal(5,sample.Cases.Count(c=>c.RandomSample));Assert.All(sample.Cases.Where(c=>c.RandomSample),c=>Assert.Equal(1,c.InclusionProbability));
        output.WriteLine("population5 interval10 requested3 feasible-distinct1 realized1; evidence="+sources.Folder);
    }
    [Theory] [InlineData(12,10,5,3)] [InlineData(10,1,20,10)] [InlineData(100,10,100,91)]
    public void DistinctIntervalsReachIndependentlyCountedFeasibleCapacity(int population,int width,int requested,int expected)
    {
        var sources=new EpisodeAuditSourceSet(population);var sample=IndependentValidationSampling.Generate(sources.Snapshot([]),sources.Plan(requested,width));
        Assert.Equal(expected,Difficult(sample).Length);Assert.Equal(expected,Difficult(sample).Select(c=>(c.StartFrame,c.EndFrame)).Distinct().Count());
        Assert.Equal(Math.Min(requested,Math.Max(1,population-width+1)),expected);
    }
    [Theory] [InlineData(0,false)] [InlineData(1,true)]
    public void SecondReviewExtremesAndProbabilityRemainExplicit(double fraction,bool expected)
    {
        var sources=new EpisodeAuditSourceSet();var sample=IndependentValidationSampling.Generate(sources.Snapshot(Enumerable.Range(0,30).Concat([85])),sources.Plan(second:fraction));
        Assert.All(sample.Cases,c=>Assert.Equal(expected,c.SecondReviewRequired));Assert.All(sample.Cases.Where(c=>!c.RandomSample),c=>Assert.Null(c.InclusionProbability));
    }
    private static ValidationSampling LegacySampling()=>new("legacy-plan","legacy-producer",42,[],[new("legacy-source",2,2,true,.1,10,false,"legacy random reason"),new("legacy-source",4,7,false,null,10,true,"legacy purposive reason")]);
    [Fact] public void PreviouslySerializedSamplingAndReferenceKeysRemainByteStable()
    {
        var sample=LegacySampling();var json=JsonDefaults.Serialize(sample);var hash=IndependentValidationSnapshot.Hash(json);
        Assert.Equal("ed41d1461112e132f8dc561c2a37d3a98f020a0664e54e22c1fe19c2fe07309e",hash);
        Assert.Equal(json,JsonDefaults.Serialize(JsonDefaults.Deserialize<ValidationSampling>(json)));
        var reference=new IndependentReference("legacy-source",2,ReferenceReviewerLane.First,"legacy reviewer",ReferenceVisibility.Visible,32,32,ReferenceEvent.None,"legacy reason",[]);
        Assert.Equal(hash+":legacy-source:2:First",IndependentValidationComparison.ReferenceKey(hash,reference));
    }
}
