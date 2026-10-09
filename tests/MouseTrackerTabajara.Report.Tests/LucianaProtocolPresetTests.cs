using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Report.Tests;

public sealed partial class WholeStudyReportTests
{
    [Fact] public void LucianaTemplateKeepsAllUnknownFactsAndFourGroupReportUsesExistingMath()
    {
        var draft=new ExperimentPlanDraft();Assert.True(LucianaProtocolPreset.PopulateFresh(draft));var plan=draft.Freeze();plan.Validate();Assert.Equal(6,plan.Contexts.Length);Assert.Empty(plan.Animals);Assert.Empty(plan.Trials);Assert.False(plan.RosterConfirmed);Assert.False(plan.DesignConfirmed);Assert.Equal(StudyUnitDesign.Unknown,plan.UnitDesign);
        Assert.Equal(Enumerable.Range(1,6),plan.Contexts.Select(c=>c.Day));Assert.All(plan.Contexts.Take(5),c=>Assert.Equal(TrialMode.Training,c.Phase));Assert.Equal(TrialMode.Probe,plan.Contexts[5].Phase);Assert.Contains("with-flag",plan.Contexts[0].Id);Assert.Contains("no-flag",plan.Contexts[1].Id);Assert.Contains("former-site",plan.Contexts[5].Id);
        Assert.All(plan.Contexts,c=>{Assert.Equal(0,c.RequestedExposureSeconds);Assert.Equal(0,c.ComparisonDurationSeconds);Assert.Empty(c.TemporalBinEdges);Assert.False(c.WindowConfirmed);Assert.False(c.AlignmentConfirmed);Assert.Equal(StudyOrientation.Unconfirmed,c.Orientation);});Assert.False(LucianaProtocolPreset.PopulateFresh(draft));
        var actual=new ExperimentAnimalDraft{Id="actual-entered-mouse",IdentityConfirmed=false,AllocationConfirmed=true};draft.Animals.Add(actual);Assert.True(LucianaProtocolPreset.AssignGroup(draft,[actual],LucianaProtocolPreset.Groups[2]));Assert.Equal("treated mutant",actual.Group);Assert.False(actual.AllocationConfirmed);Assert.False(actual.IdentityConfirmed);Assert.Empty(draft.Trials);
        using var fixture=new Fixture();var store=new StudyPlanStore(fixture.Store);var saved=store.Latest()!;var animalMap=saved.Plan.Animals.Select((a,i)=>a with{Group=LucianaProtocolPreset.Groups[i%4]}).ToImmutableArray();
        foreach(var asset in fixture.Store.Videos())fixture.Store.SaveVideo(asset with{GroupName=animalMap.Single(a=>a.Id==asset.AnimalId).Group});store.Save(saved.Plan with{Animals=animalMap},"Reviewer","Synthetic four-group existing report support",saved.Revision);
        var result=fixture.Compute().Document.Contexts[0];Assert.Equal(4,result.Groups.Length);var q=result.Questions!;Assert.Equal(4,q.Groups.Where(g=>g.Endpoint=="adjacent speed").Select(g=>g.Group).Distinct().Count());Assert.Equal(6,q.Differences.Count(d=>d.Endpoint=="adjacent speed"));Assert.All(q.Groups.Where(g=>g.Endpoint=="adjacent speed"),g=>Assert.True(g.IncludedAnimals>0));
    }
    [Fact] public void ActualNativeLucianaEmptyPlanPresetGroupAssignmentAndBlindProtection()
    {
        Exception? error=null;var thread=new Thread(()=>{try{NativeLuciana();}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromMinutes(2)));if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    [Fact] public void ConfirmedTwentyOneSlotsAppendOnlyMissingAndKeepOneActualAnimalAcrossDays()
    {
        var draft=new ExperimentPlanDraft();Assert.True(LucianaProtocolPreset.PopulateFresh(draft));var actual=new ExperimentAnimalDraft{Id="actual-mouse"};var other=new ExperimentAnimalDraft{Id="other-mouse"};draft.Animals.Add(actual);draft.Animals.Add(other);
        var mapped=new ExperimentTrialDraft{Id="existing-mapped",AnimalId=actual.Id,ContextId=LucianaProtocolPreset.StageIds[0],Order=1,RecordingId="actual-recording-entered",SourceReferenceConfirmed=true,ReferenceSourceSha256=new string('a',64),ReferenceSettingsHash=new string('b',64)};draft.Trials.Add(mapped);var untouched=new ExperimentTrialDraft{Id="other-existing",AnimalId=other.Id,ContextId=LucianaProtocolPreset.StageIds[0],Order=1};draft.Trials.Add(untouched);var before=JsonDefaults.Serialize(mapped.Freeze());
        Assert.True(LucianaProtocolPreset.AppendExpectedTrials(draft,actual,out var added));Assert.Equal(20,added);var slots=draft.Trials.Where(t=>t.AnimalId==actual.Id).ToArray();Assert.Equal(21,slots.Length);Assert.Equal(new[]{4,4,4,4,4,1},LucianaProtocolPreset.StageIds.Select(id=>slots.Count(t=>t.ContextId==id)));Assert.All(slots.Where(t=>t!=mapped),t=>{Assert.Null(t.RecordingId);Assert.False(t.SourceReferenceConfirmed);Assert.Equal("",t.ReferenceSourceSha256);});Assert.Equal(before,JsonDefaults.Serialize(mapped.Freeze()));Assert.Same(untouched,draft.Trials.Single(t=>t.AnimalId==other.Id));Assert.True(LucianaProtocolPreset.AppendExpectedTrials(draft,actual,out added));Assert.Equal(0,added);Assert.False(draft.DesignConfirmed);Assert.False(draft.RosterConfirmed);Assert.Equal(StudyUnitDesign.Unknown,draft.UnitDesign);
        var plan=draft.Freeze();plan.Validate();Assert.All(StudyPlanInventory.Build(plan,[]),slot=>Assert.NotEmpty(slot.Holds));var mismatched=ExperimentPlanDraft.From(plan);mismatched.Contexts[0].Phase=TrialMode.Probe;Assert.False(LucianaProtocolPreset.AppendExpectedTrials(mismatched,mismatched.Animals[0],out _));Assert.Equal(22,mismatched.Trials.Count);
    }
    private static void NativeLuciana()
    {
        var folder=Path.Combine(Path.GetTempPath(),"luciana-preset-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var store=new StudyStore(Path.Combine(folder,"pilot.morrisdb"));SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var window=new ExperimentWindow(store.DatabasePath,false,false);
        try
        {
            Find<TabControl>(window).Single().SelectedIndex=1;Layout(window,1050,700);Click(window,"Start Luciana six-day protocol (empty plan only)");Layout(window,1050,700);Assert.Equal(6,window.Draft.Contexts.Count);Assert.Empty(window.Draft.Animals);Assert.Empty(window.Draft.Trials);Assert.Null(new StudyPlanStore(store).Latest());
            var group=Find<ComboBox>(window).Single(c=>Equals(c.Tag,"luciana-protocol-group"));Assert.Equal(LucianaProtocolPreset.Groups,group.Items.Cast<string>());
            var animal=new ExperimentAnimalDraft{Id="entered-by-researcher",AllocationConfirmed=true};window.Draft.Animals.Add(animal);window.AnimalsEditor.SelectedItem=animal;group.SelectedItem="vehicle wildtype";Click(window,"Assign protocol group to selected animals (unconfirmed)");Assert.Equal("vehicle wildtype",animal.Group);Assert.False(animal.AllocationConfirmed);Assert.False(animal.IdentityConfirmed);
            Click(window,"Add 21 expected trials for selected animal");Assert.Equal(21,window.Draft.Trials.Count);Assert.All(window.Draft.Trials,t=>Assert.Equal(animal.Id,t.AnimalId));Click(window,"Add 21 expected trials for selected animal");Assert.Equal(21,window.Draft.Trials.Count);
            var before=JsonDefaults.Serialize(window.Draft.Freeze());Click(window,"Start Luciana six-day protocol (empty plan only)");Assert.Equal(before,JsonDefaults.Serialize(window.Draft.Freeze()));Assert.Contains("preserved",window.StatusText);
            window.ReviewerInput.Text="Reviewer";window.ReasonInput.Text="Save unconfirmed human protocol scaffold";Click(window,"Save expected plan revision");Assert.Equal(1,window.ExpectedPlanRevision);var saved=JsonDefaults.Serialize(new StudyPlanStore(store).Latest()!.Plan);window.StartLucianaProtocol();Assert.Equal(saved,JsonDefaults.Serialize(new StudyPlanStore(store).Latest()!.Plan));
            window.Blinded=true;Layout(window,1050,700);Assert.DoesNotContain(Find<ComboBox>(window),c=>Equals(c.Tag,"luciana-protocol-group"));Assert.DoesNotContain(Find<Button>(window),b=>Equals(b.Content,"Add 21 expected trials for selected animal"));Assert.DoesNotContain(Find<TextBlock>(window),t=>LucianaProtocolPreset.Groups.Any(g=>t.Text.Contains(g,StringComparison.Ordinal)));window.StartLucianaProtocol();window.AddLucianaExpectedTrials();Assert.Equal(saved,JsonDefaults.Serialize(new StudyPlanStore(store).Latest()!.Plan));
        }
        finally{window.Close();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
    }
}
