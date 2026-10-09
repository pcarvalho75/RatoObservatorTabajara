using System.Collections.Immutable;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;

if(args.Length!=3)throw new ArgumentException("prepare|resume|report, new evidence root, original verified package directory required.");
var mode=args[0];var evidence=Path.GetFullPath(args[1]);var package=Path.GetFullPath(args[2]);
Directory.CreateDirectory(evidence);
var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(package,"files.sha256.json"))).RootElement.EnumerateArray()
    .ToDictionary(e=>e.GetProperty("path").GetString()!.Replace('\\','/'),e=>e.GetProperty("sha256").GetString()!,StringComparer.OrdinalIgnoreCase);
string HashFile(string file){using var input=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();}
void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
void Save(string name,object value)=>File.WriteAllText(Path.Combine(evidence,name),JsonDefaults.Serialize(value));
string StageRelative(string path){var relative=Path.GetRelativePath(AppContext.BaseDirectory,path).Replace('\\','/');Require(!relative.StartsWith("..",StringComparison.Ordinal)&&!Path.IsPathRooted(relative),"A production assembly/native module escaped the verified stage.");return"desktop/"+relative;}
object? NativeOrigin(string name,bool required)
{
    using var process=Process.GetCurrentProcess();var module=process.Modules.Cast<ProcessModule>().SingleOrDefault(m=>string.Equals(m.ModuleName,name,StringComparison.OrdinalIgnoreCase));
    if(module is null){Require(!required,"Required package native module not observed: "+name);return null;}
    var relative=StageRelative(module.FileName);var observed=HashFile(module.FileName);
    Require(manifest.TryGetValue(relative,out var expected)&&string.Equals(expected,observed,StringComparison.OrdinalIgnoreCase),"Native module not package-identical: "+name);
    return new{name,path=module.FileName,sha256=observed};
}
void ActiveCodecOrigin(string resource,string backend)
{
    Require(backend.Equals("FFMPEG",StringComparison.OrdinalIgnoreCase),"Synthetic FFV1 gate requires the actual FFmpeg backend; no alternate codec fallback.");
    var codec=NativeOrigin("opencv_videoio_ffmpeg500_64.dll",true);var hostfxr=NativeOrigin("hostfxr.dll",false);
    Save(mode+"-active-codec-origin.json",new{resource,backend,codec,hostfxr,hostfxr_state=hostfxr is null?"Not observed":"Observed and manifest-bound",process=Environment.ProcessId,resource_still_open=true});
}
void Origins()
{
    var managed=new List<object>();
    foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic&&a.GetName().Name!="PortableCollectionProbe"))
    {
        Require(!string.IsNullOrEmpty(assembly.Location),"Production assembly lacks a file origin.");var relative=StageRelative(assembly.Location);
        Require(manifest.TryGetValue(relative,out var expected)&&string.Equals(expected,HashFile(assembly.Location),StringComparison.OrdinalIgnoreCase),"Loaded assembly not identical to extracted manifest: "+relative);
        managed.Add(new{name=assembly.GetName().Name,path=assembly.Location,sha256=HashFile(assembly.Location)});
    }
    var native=new List<object>();
    foreach(var name in new[]{"coreclr.dll","e_sqlite3.dll"})
    {
        native.Add(NativeOrigin(name,true)!);
    }
    var wrapper=NativeOrigin("OpenCvSharpExtern.dll",mode!="report");if(wrapper is not null)native.Add(wrapper);
    foreach(var name in new[]{"hostfxr.dll","opencv_videoio_ffmpeg500_64.dll"})if(NativeOrigin(name,false) is {} observed)native.Add(observed);
    Save(mode+"-origins.json",new{managed,native,process=Environment.ProcessId,probe=Assembly.GetExecutingAssembly().Location,probe_sha256=HashFile(Assembly.GetExecutingAssembly().Location),synthetic_only=true});
}
try
{
    var partial=Path.Combine(evidence,"partial.tabajara");
    if(mode=="prepare")
    {
        Require(!File.Exists(Path.Combine(evidence,"working.tabajara")),"Never overwrite previous collection evidence.");
        ProcessedProject.CreateNew(Path.Combine(evidence,"working.tabajara"));var store=ProcessedProject.OpenProject(Path.Combine(evidence,"working.tabajara"));var workspace=new WorkspaceService(store);
        var groups=new[]{"WT_vehicle","WT_treatment","KO_vehicle","KO_treatment"};
        var animals=groups.Select((g,i)=>new PlannedAnimal{Id="synthetic-animal-"+i,Group=g,IdentityConfirmed=true,AllocationConfirmed=true}).ToImmutableArray();
        var contexts=Enumerable.Range(1,6).Select(day=>new PlannedContext{Id="day-"+day,Day=day,Phase=day==6?TrialMode.Probe:TrialMode.Training,RequestedExposureSeconds=2.4,ComparisonDurationSeconds=2,TemporalBinEdges=[0,1,2],WindowConfirmed=true,CommonCoordinateReference="synthetic shared normalized camera",Orientation=StudyOrientation.Room,AlignmentConfirmed=true}).ToImmutableArray();
        var assets=new List<VideoAsset>();var slots=ImmutableArray.CreateBuilder<PlannedTrial>();var ordinal=0;var sourceOrdinal=0;
        foreach(var context in contexts)foreach(var animal in animals)for(var order=1;order<=(context.Day==6?1:4);order++)
        {
            VideoAsset? asset=null;
            var animalIndex=animals.IndexOf(animal);var mapped=animalIndex==0||context.Day==1&&order<=(animalIndex==1?4:animalIndex==2?3:2);
            if(mapped)
            {
                var path=Path.Combine(evidence,$"synthetic-{ordinal:D2}.avi");
                using(var writer=new VideoWriter(path,VideoWriter.FourCC('F','F','V','1'),10,new Size(160,160)))
                {
                    Require(writer.IsOpened(),"Packaged OpenCV writer failed.");
                    for(var frame=0;frame<24;frame++){using var image=new Mat(160,160,MatType.CV_8UC3,new Scalar(180+sourceOrdinal,190,210));Cv2.Circle(image,new Point(58+frame,80),5,new Scalar(5,5,5),-1);writer.Write(image);}
                    if(sourceOrdinal==0)ActiveCodecOrigin(path,writer.GetBackendName());
                }
                asset=workspace.Import(path) with{AnimalId=animal.Id,AnimalIdConfirmed=true,GroupName=animal.Group,Phase=context.Phase,Day=context.Day,Trial=order,
                    Settings=new(){Cx=.5,Cy=.5,Rx=.45,Ry=.45,Threshold=60,Blur=0,MinArea=20,MaxArea=200,MaxExtension=20,MaxJump=50,Invert=true,ArenaConfirmed=true,Platform=new(.6,.5,.04)}};
                Require(asset.Metadata.FrameCount==24,"Actual package probe population differs.");store.SaveVideo(asset);assets.Add(asset);
                store.RecordBiologicalEvent(asset,BiologicalEventKind.WaterEntry,0,"Synthetic release control","synthetic package gate");
                if(context.Phase==TrialMode.Training)store.RecordBiologicalEvent(asset,BiologicalEventKind.PlatformMounting,20,"Explicit constructed synthetic termination timing control; not real biological evidence","synthetic package gate");
                store.SaveProtocolWindow(asset,new(){RequestedExposureSeconds=2.4,ReleaseFrame=0,ReleaseConfirmed=true,WindowConfirmed=true,Reviewer="synthetic package gate",Reason="Explicit synthetic release/exposure control; comparison2s, requested2.4s retained"});
                sourceOrdinal++;
            }
            slots.Add(new(){Id=$"slot-{animal.Id}-{context.Day}-{order}",AnimalId=animal.Id,ContextId=context.Id,Order=order,RecordingId=asset?.Id,MissingReason=asset is null?"Synthetic deliberately unacquired later slot":"",SourceReferenceConfirmed=asset is not null,ReferenceSourceSha256=asset?.SourceSha256??"",ReferenceSettingsHash=asset is null?"":StudyStore.SettingsHash(asset.Settings)});ordinal++;
        }
        Require(assets.Count==30&&slots.Count==84&&slots.Count(t=>t.RecordingId is null)==54,"Full planned inventory lost.");Require(assets.Select(a=>a.SourceSha256).Distinct().Count()==30,"Synthetic bytes are not distinct.");
        var plan=new StudyPlan{Title="Synthetic4groups6days21trials package gate",UnitDesign=StudyUnitDesign.RepeatedTrialsWithinAnimals,DesignConfirmed=true,RosterConfirmed=true,Exploratory=true,Animals=animals,Contexts=contexts,Trials=slots.ToImmutable()};new StudyPlanStore(store).Save(plan,"synthetic package gate","Human-confirmed layout exercised synthetically; not real approval",0);
        var result=await workspace.AnalyzeAsync(assets.Take(10),null,new NativeBatchRequest{MaxDegreeOfParallelism=2});Require(result.Completed==10&&result.Errors.Count==0&&result.ActiveDecodersAtCompletion==0,"Partial package analysis failed.");
        foreach(var asset in assets.Take(10))store.RecordReview(store.LatestRun(asset.Id)!,true); // Synthetic tooling review only.
        var first=assets[0];var raw=store.LatestRun(first.Id)!;store.SaveCorrection(raw,new(raw.Id,4,62,80,"Synthetic intermediary edit control",DateTime.UtcNow));store.RecordReview(raw,true);
        using(var decoder=new FrameDecoderPool(1))
        {
            var bytes=decoder.Read(first.Path,0,expectedSha256:first.SourceSha256);var bitmap=CameraFrameMaterial.Bitmap(bytes);var frame=new CameraFrameIdentity(first.Id,first.SourceSha256,0,160,160,CameraFrameMaterial.BytesHash(bytes),CameraFrameMaterial.PixelHash(bitmap));
            var fiducials=new[]{new FiducialObservation("C1",new(20,20)),new FiducialObservation("C2",new(140,20)),new FiducialObservation("C3",new(140,140)),new FiducialObservation("C4",new(20,140))}.ToImmutableArray();
            new CameraCalibrationService(store).SavePreset("Synthetic detector/ROI template; no measured cm","synthetic-camera",frame,new("synthetic-camera",160,160,first.SourceSha256,0,true,fiducials),first.Settings,"synthetic package gate","Explicit constructed stationary controls; no physical calibration claim");
        }
        Save("before.json",assets.Take(10).ToDictionary(a=>a.Id,a=>JsonDefaults.Serialize(store.LatestRun(a.Id)!)));Save("registered-assets.json",store.Videos());Save("plan.json",plan);
        Save("correction-history-before.json",assets.Take(10).ToDictionary(a=>a.Id,a=>JsonDefaults.Serialize(store.Corrections(store.LatestRun(a.Id)!.Id))));
        Save("camera-history-before.json",new CameraHistoryStore(store.DatabasePath).Read());
        Save("partial-receipt.json",ProcessedProject.SaveSnapshot(store.DatabasePath,partial));Save("prepare-result.json",new{partial_completed=10,groups=4,animals=4,contexts=6,planned=84,mapped=30,missing=54,animal_mapping=new[]{21,4,3,2},result.EffectiveDecoderCap,result.ObservedMaximumActiveDecoders,physical_calibration="HELD; no measured profile",synthetic_only=true});
    }
    else if(mode=="resume")
    {
        var store=ProcessedProject.OpenProject(partial);var before=JsonDefaults.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(evidence,"before.json")));
        Require(JsonDefaults.Serialize(store.Videos())==File.ReadAllText(Path.Combine(evidence,"registered-assets.json")),"Metadata/settings changed during reopen.");Require(new StudyPlanStore(store).Latest()!.RawJson==File.ReadAllText(Path.Combine(evidence,"plan.json")),"Plan changed during reopen.");
        using(var capture=new VideoCapture(store.Videos().First().Path))
        {
            Require(capture.IsOpened(),"Packaged FFV1 source capture failed.");using var image=new Mat();Require(capture.Read(image)&&!image.Empty(),"Active package capture did not decode a synthetic frame.");
            ActiveCodecOrigin(store.Videos().First().Path,capture.GetBackendName());
        }
        var result=await new WorkspaceService(store).AnalyzeAsync(store.Videos(),null,new NativeBatchRequest{MaxDegreeOfParallelism=2});Require(result.Reused==10&&result.Completed==30&&result.Recordings.Count(r=>r.State.ToString()=="Completed")==20&&result.Errors.Count==0&&result.ActiveDecodersAtCompletion==0,"Video-level resume did not reuse10/new20.");
        foreach(var saved in before)Require(saved.Value==JsonDefaults.Serialize(store.LatestRun(saved.Key)),"Prior raw evidence was rewritten.");
        foreach(var asset in store.Videos()){CompletedRecordingContract.Validate(store.LatestRun(asset.Id)!);Require(HashFile(asset.Path)==asset.SourceSha256,"Original source bytes changed.");store.RecordReview(store.LatestRun(asset.Id)!,true);}
        Require(store.Corrections(store.LatestRun(before.Keys.First())!.Id).Count==1,"Intermediary correction lost.");Require(new CameraHistoryStore(store.DatabasePath).Read("preset").Count==1,"Preset history lost.");
        var correctionBefore=JsonDefaults.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(evidence,"correction-history-before.json")));
        foreach(var saved in correctionBefore)Require(saved.Value==JsonDefaults.Serialize(store.Corrections(store.LatestRun(saved.Key)!.Id)),"Canonical correction history changed during reopen/resume: "+saved.Key);
        Require(File.ReadAllText(Path.Combine(evidence,"camera-history-before.json"))==JsonDefaults.Serialize(new CameraHistoryStore(store.DatabasePath).Read()),"Canonical camera preset/history identity or content changed during reopen/resume.");
        Save("completed-receipt.json",ProcessedProject.SaveSnapshot(partial,Path.Combine(evidence,"completed.tabajara")));Save("resume-result.json",new{result.Completed,result.Reused,newly_completed=20,result.EffectiveDecoderCap,result.ObservedMaximumActiveDecoders,source_hashes_unchanged=true,prior_raw_ids_json_unchanged=true});
    }
    else if(mode=="report")
    {
        var store=ProcessedProject.OpenProject(Path.Combine(evidence,"completed.tabajara"));var plan=new StudyPlanStore(store).Latest()!.Plan;
        var computed=StudyReportComputation.Compute(AtlasSnapshot.Read(store.DatabasePath),plan.Contexts.Select(c=>c.Id));
        Require(computed.Document.Contexts.Sum(c=>c.Trials.Length)==84,"Report dropped planned trials.");Require(computed.Document.Contexts.SelectMany(c=>c.Trials).Count(t=>t.VideoId is null)==54,"Report lost missing later slots.");
        var day1=computed.Document.Contexts.Single(c=>c.Day==1);Require(day1.Trials.Length==16,"Day1 expected4trials across4groups lost.");
        var day1Speed=day1.Questions!.Groups.Where(g=>g.Endpoint=="adjacent speed").ToArray();
        Require(day1Speed.Single(g=>g.Group=="WT_vehicle").Contributors.Contains("synthetic-animal-0")&&day1Speed.Single(g=>g.Group=="WT_treatment").Contributors.Contains("synthetic-animal-1"),"Complete day1 A/B descriptive scalar contributors unavailable.");
        Require(day1Speed.Single(g=>g.Group=="KO_vehicle").HeldAnimals==1&&day1Speed.Single(g=>g.Group=="KO_treatment").HeldAnimals==1,"Partial day1 C/D scalar contributors were falsely completed or dropped.");
        Require(day1.Groups.IsEmpty&&day1.Animals.All(a=>!a.MapIncluded),"Training fixed-probe map safeguard was bypassed.");
        var probeContext=computed.Document.Contexts.Single(c=>c.Day==6);
        Require(probeContext.Groups.Single(g=>g.Group=="WT_vehicle").IncludedAnimals.Contains("synthetic-animal-0"),"Complete animal's supported probe map unavailable.");
        Require(probeContext.Animals.Where(a=>a.Id!="synthetic-animal-0").All(a=>!a.MapIncluded&&!a.Excluded),"Missing probe animals were falsely included or excluded.");
        Require(computed.Document.Contexts.SelectMany(c=>c.Trials).Count(t=>t.Animal=="synthetic-animal-0"&&t.VideoId is not null)==21,"Complete21trial synthetic animal lost.");
        foreach(var pair in new[]{(Animal:1,Count:4),(Animal:2,Count:3),(Animal:3,Count:2)})Require(computed.Document.Contexts.SelectMany(c=>c.Trials).Count(t=>t.Animal=="synthetic-animal-"+pair.Animal&&t.VideoId is not null)==pair.Count,"Partial animal inventory changed.");
        StudyReportPublication.Publish(Path.Combine(evidence,"frozen-report"),computed.Document,computed.Material);var saved=StudyReportPublication.Read(Path.Combine(evidence,"frozen-report"));
        using var reconstructed=saved.Material!.Reconstruct();var replay=StudyReportComputation.Compute(reconstructed.Snapshot,plan.Contexts.Select(c=>c.Id));
        Require(JsonDefaults.Serialize(saved.Document)==JsonDefaults.Serialize(replay.Document with{FrozenUtc=saved.Document.FrozenUtc}),"Authenticated frozen numerical replay differs.");
        foreach(var file in new[]{"report.json","frozen-input.json","study.html","temporal.csv","COORD.csv","DIST.csv","AREA_RESULT.csv","AREA_TIME.csv","GROUP_SCALARS.csv","GROUP_SCALARS_pt-BR.csv"})Require(File.Exists(Path.Combine(evidence,"frozen-report",file)),"Expected public report/PDF-question output absent: "+file);
        Require(Directory.GetFiles(Path.Combine(evidence,"frozen-report"),"*_PATH.svg").Length>0&&Directory.GetFiles(Path.Combine(evidence,"frozen-report"),"*_PCOORD.svg").Length>0&&Directory.GetFiles(Path.Combine(evidence,"frozen-report"),"*_DENSI.svg").Length>0,"Actual PDF-question SVG producers did not execute.");
        var expectedPayloads=ReportQuestionExport.Payloads(computed.Document);var payloadEvidence=new List<object>();
        foreach(var payload in expectedPayloads)
        {
            var path=Path.Combine(evidence,"frozen-report",payload.Key);Require(File.Exists(path),"Expected question payload missing: "+payload.Key);
            Require(File.ReadAllText(path)==payload.Value,"Question payload content differs from frozen producer: "+payload.Key);
            var expectedHash=Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(payload.Value))).ToLowerInvariant();
            Require(HashFile(path)==expectedHash&&saved.Manifest.FileHashes.TryGetValue(payload.Key,out var manifestHash)&&manifestHash.Equals(expectedHash,StringComparison.OrdinalIgnoreCase),"Question payload hash/manifest mismatch: "+payload.Key);
            if(payload.Key.EndsWith(".svg",StringComparison.Ordinal))Require(XDocument.Parse(payload.Value).Root?.Name.LocalName=="svg","Question figure is not XML SVG: "+payload.Key);
            payloadEvidence.Add(new{file=payload.Key,sha256=expectedHash});
        }
        for(var i=1;i<=computed.Document.Contexts.Length;i++)foreach(var suffix in new[]{"GROUP_DENSI.svg","GROUP_DIFFERENCE.svg"})Require(expectedPayloads.ContainsKey($"context-{i:000}_"+suffix),"Missing explicit group figure producer.");
        Require(expectedPayloads["COORD.csv"].StartsWith("context,trial,animal,recording,source_sha256,run_id,source_frame,source_seconds,",StringComparison.Ordinal),"Coordinate CSV source header changed.");
        Require(expectedPayloads["GROUP_SCALARS_pt-BR.csv"].StartsWith("context;phase;day;comparison_offset_after_release_seconds;",StringComparison.Ordinal),"Portuguese group CSV delimiter/header changed.");
        Save("question-payloads.json",payloadEvidence);
        Save("report-result.json",new{planned=84,mapped=30,missing=54,groups=4,animals=4,contexts=6,replay="PASS",synthetic_only=true,scope="Morris PDF question CSV/HTML/SVG producers; physical calibration/real accuracy held"});
    }
    else throw new ArgumentException("Unknown probe mode.");
    Origins();
}
catch(Exception error){Save(mode+"-failure.json",new{error=error.ToString(),state="FAIL",preserved_partial_evidence=true});throw;}
