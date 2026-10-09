using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
namespace MouseTrackerTabajara.Desktop;

public sealed record CameraFrameIdentity(string VideoId,string SourceSha256,int Frame,int Width,int Height,string EncodedImageSha256,string DisplayedPixelSha256);
public sealed record CameraProfileEvidence(string ProfileId,string SavedProfileJson,string SavedMaterialSha256,CameraFrameIdentity ReferenceFrame,string Reviewer,string Reason,bool TolerancesPrespecified);
public sealed record DetectorGeometryPreset(string Name,string CameraId,int Width,int Height,CameraFrameIdentity ReferenceFrame,CameraObservation ReferenceMarkers,TrackingSettings DetectorGeometry,string Reviewer,string Reason);
public sealed record PresetPreview(string PresetRowId,string VideoId,string SourceSha256,string BeforeSettingsHash,TrackingSettings Proposed,string Difference,bool Compatible,ImmutableArray<string> Holds);
public sealed record CameraAssignment(string ProfileRowId,string ProfileId,string ProfileMaterialHash,CameraFrameIdentity ObservedFrame,CameraObservation Observation,ShiftTolerances Tolerances,string SettingsHash,string RunId,string CorrectionFingerprint,string RunFingerprint,string Reviewer,string Reason,bool AssumptionsConfirmed);
public sealed record PhysicalFrame(int Frame,double TimeSeconds,double? RoomXcm,double? RoomYcm,bool Accepted);
public sealed record PhysicalPathEvidence(string AssignmentRowId,string ProfileId,string SourceSha256,string RunId,string SettingsHash,string CorrectionFingerprint,ImmutableArray<PhysicalFrame> Frames,double AdjacentPathCm,double UnknownSeconds,string Scope,ImmutableArray<string> Holds);

/// <summary>Connected camera contract. All output remains derived; raw runs and biological windows are unchanged.</summary>
public sealed class CameraCalibrationService
{
    public StudyStore Store{get;}public CameraHistoryStore History{get;}
    public CameraCalibrationService(StudyStore store){Store=store;History=new(store.DatabasePath);}
    public static void RequireFrame(CameraFrameIdentity frame,VideoAsset asset)
    {
        if(frame.VideoId!=asset.Id || frame.SourceSha256!=asset.SourceSha256 || frame.Width!=asset.Metadata.Width || frame.Height!=asset.Metadata.Height)throw new InvalidDataException("Source/frame/resolution identity changed; camera operation held.");
        FrameIdentity.RequireSourceFrame(frame.Frame,asset.Metadata.FrameCount);
        if(frame.EncodedImageSha256.Length!=64 || frame.DisplayedPixelSha256.Length!=64)throw new InvalidDataException("Exact displayed image identity is required.");
        CameraFrameMaterial.Verify(frame,asset);
    }
    public CameraHistoryRow SaveProfile(CalibrationProfile profile,CameraFrameIdentity frame,string reviewer,string reason,bool prespecified)
    {
        var asset=Current(frame.VideoId);RequireFrame(frame,asset);RequireReview(reviewer,reason);
        if(profile.Request.ConfirmedBy!=reviewer.Trim())throw new InvalidOperationException("Fit reviewer and saved reviewer differ; fit a new revision.");
        if(!prespecified)throw new InvalidOperationException("Confirm error tolerances were specified before inspecting checks.");
        if(profile.Request.ReferenceSourceSha256!=frame.SourceSha256 || profile.Request.ReferenceFrame!=frame.Frame || profile.Request.ImageWidth!=frame.Width || profile.Request.ImageHeight!=frame.Height)throw new InvalidDataException("Profile is not measured on this exact source frame.");
        var directory=Path.Combine(Path.GetDirectoryName(Store.DatabasePath)!,"camera-profile-replay");Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,Guid.NewGuid().ToString("N")+".json");CalibrationProfileFile.Save(profile,file);var material=File.ReadAllText(file);
        return History.Append("profile",asset.Id,new CameraProfileEvidence(profile.Id,material,CameraHistoryStore.Digest(material),frame,reviewer.Trim(),reason.Trim(),prespecified));
    }
    public CalibrationProfile Replay(CameraHistoryRow row)
    {
        row=Authoritative(row,"profile");
        if(row.Kind!="profile")throw new ArgumentException("Select an immutable calibration profile.");
        var evidence=JsonDefaults.Deserialize<CameraProfileEvidence>(row.Json);
        if(CameraHistoryStore.Digest(evidence.SavedProfileJson)!=evidence.SavedMaterialSha256)throw new InvalidDataException("Profile material changed.");
        var directory=Path.Combine(Path.GetDirectoryName(Store.DatabasePath)!,"camera-profile-replay");Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,evidence.SavedMaterialSha256+".json");
        if(!File.Exists(file)){using var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None);var bytes=Encoding.UTF8.GetBytes(evidence.SavedProfileJson);stream.Write(bytes);stream.Flush(true);}
        if(CameraHistoryStore.Digest(File.ReadAllText(file))!=evidence.SavedMaterialSha256)throw new InvalidDataException("Replay copy changed; use a verified database backup.");
        var profile=CalibrationProfileFile.Load(file);if(profile.Id!=evidence.ProfileId)throw new InvalidDataException("Replayed profile identity differs.");return profile;
    }
    public CameraHistoryRow SavePreset(string name,string cameraId,CameraFrameIdentity frame,CameraObservation markers,TrackingSettings geometry,string reviewer,string reason)
    {
        var asset=Current(frame.VideoId);RequireFrame(frame,asset);RequireReview(reviewer,reason);
        if(string.IsNullOrWhiteSpace(name))throw new ArgumentException("Name the preset.");
        RequireObservation(frame,markers);var holds=StationaryPresetCheck(cameraId,frame.Width,frame.Height,markers.Fiducials,markers,new(.01,.01));
        if(!holds.IsEmpty)throw new InvalidOperationException(string.Join(" ",holds));
        geometry.Validate();var template=geometry with{StartSeconds=0,EndSeconds=null,ArenaConfirmed=false,Exclusions=geometry.Exclusions.Select(e=>e with{}).ToList()};
        return History.Append("preset",asset.Id,new DetectorGeometryPreset(name.Trim(),cameraId,frame.Width,frame.Height,frame,markers,template,reviewer.Trim(),reason.Trim()));
    }
    public PresetPreview Preview(CameraHistoryRow row,string targetId,CameraFrameIdentity frame,CameraObservation observation,ShiftTolerances tolerances)
    {
        row=Authoritative(row,"preset");
        var preset=JsonDefaults.Deserialize<DetectorGeometryPreset>(row.Json);var target=Current(targetId);RequireFrame(frame,target);RequireObservation(frame,observation);
        var holds=StationaryPresetCheck(preset.CameraId,preset.Width,preset.Height,preset.ReferenceMarkers.Fiducials,observation,tolerances);
        var template=preset.DetectorGeometry;var proposed=target.Settings with{Cx=template.Cx,Cy=template.Cy,Rx=template.Rx,Ry=template.Ry,Threshold=template.Threshold,Blur=template.Blur,Invert=template.Invert,MinArea=template.MinArea,MaxArea=template.MaxArea,MaxExtension=template.MaxExtension,MaxJump=template.MaxJump,DiameterCm=template.DiameterCm,Platform=template.Platform,Exclusions=template.Exclusions.Select(e=>e with{}).ToList()};
        proposed=ArenaGeometry.MergeEdit(target.Settings,proposed);
        if(JsonDefaults.Serialize(target.Settings.Exclusions)!=JsonDefaults.Serialize(proposed.Exclusions))proposed=proposed with{ArenaConfirmed=false};var difference=$"Detector: blur {target.Settings.Blur} → {proposed.Blur}; polarity {target.Settings.Invert} → {proposed.Invert}; area {target.Settings.MinArea:G}–{target.Settings.MaxArea:G} → {proposed.MinArea:G}–{proposed.MaxArea:G} px²; extent {target.Settings.MaxExtension} → {proposed.MaxExtension} px; step {target.Settings.MaxJump:G} → {proposed.MaxJump:G} px. Legacy diameter estimate {target.Settings.DiameterCm?.ToString("G")??"unset"} → {proposed.DiameterCm?.ToString("G")??"unset"} cm; site {Site(target.Settings.Platform)} → {Site(proposed.Platform)}; exclusions {Zones(target.Settings.Exclusions)} → {Zones(proposed.Exclusions)}. Threshold {target.Settings.Threshold} → {proposed.Threshold}; pool ({target.Settings.Cx:G},{target.Settings.Cy:G};{target.Settings.Rx:G},{target.Settings.Ry:G}) → ({proposed.Cx:G},{proposed.Cy:G};{proposed.Rx:G},{proposed.Ry:G}). Detector interval retained: {target.Settings.StartSeconds:G} to {target.Settings.EndSeconds?.ToString("G")??"recording end"} s. Biological protocol is unchanged. Geometry changes clear confirmation and tracking review.";
        return new(row.Id,target.Id,target.SourceSha256,SettingsIdentity.Hash(target.Settings),proposed,difference,holds.IsEmpty,holds);
    }
    public TrackingSettings Apply(PresetPreview preview,CameraFrameIdentity frame,CameraObservation observation,ShiftTolerances tolerances,string reviewer,string reason,bool confirmed,Func<TrackingSettings,string,TrackingSettings>? save=null)
    {
        RequireReview(reviewer,reason);if(!confirmed)throw new InvalidOperationException("Preview and explicitly confirm Apply.");
        var row=History.Read("preset").Single(p=>p.Id==preview.PresetRowId);var fresh=Preview(row,preview.VideoId,frame,observation,tolerances);
        if(!fresh.Compatible)throw new InvalidOperationException(string.Join(" ",fresh.Holds));if(fresh.BeforeSettingsHash!=preview.BeforeSettingsHash || JsonDefaults.Serialize(fresh.Proposed)!=JsonDefaults.Serialize(preview.Proposed))throw new InvalidOperationException("Preset preview is stale; preview the current target again.");
        var target=Current(preview.VideoId);
        var operation=Guid.NewGuid().ToString("N");
        History.Append("preset_application",target.Id,new{OperationId=operation,Phase="prepared",PresetRowId=row.Id,Frame=frame,Observation=observation,Tolerances=tolerances,BeforeHash=preview.BeforeSettingsHash,ProposedHash=SettingsIdentity.Hash(fresh.Proposed),Reviewer=reviewer.Trim(),Reason=reason.Trim()});
        var saved=save is null?Store.SaveEditedSettings(target with{Settings=fresh.Proposed},reason):save(fresh.Proposed,reason);
        try { History.Append("preset_application",target.Id,new{OperationId=operation,Phase="applied",PresetRowId=row.Id,Frame=frame,Observation=observation,Tolerances=tolerances,BeforeHash=preview.BeforeSettingsHash,AfterHash=SettingsIdentity.Hash(saved),Reviewer=reviewer.Trim(),Reason=reason.Trim()}); }
        catch(Exception ex) { throw new InvalidOperationException("Settings were saved, but the camera completion receipt failed. Prepared camera intent and audited geometry remain; tracking review is pending. "+ex.Message,ex); }
        return saved;
    }
    public CameraHistoryRow Assign(CameraHistoryRow profileRow,CameraFrameIdentity frame,CameraObservation observation,ShiftTolerances tolerances,string reviewer,string reason,bool assumptions)
    {
        RequireReview(reviewer,reason);if(!assumptions)throw new InvalidOperationException("Confirm planar water surface, stationary landmarks and uncorrected lens/parallax limits.");
        var frozen=CameraRunSnapshot.Read(Store.DatabasePath,frame.VideoId);var asset=frozen.Asset;RequireFrame(frame,asset);RequireObservation(frame,observation);
        var profile=Replay(profileRow);var reference=JsonDefaults.Deserialize<CameraProfileEvidence>(profileRow.Json);RequireFrame(reference.ReferenceFrame,Current(reference.ReferenceFrame.VideoId));var decision=CameraReuse.Evaluate(profile,observation,tolerances);if(!decision.CanReuse)throw new InvalidOperationException(string.Join(" ",decision.HoldReasons));
        var run=frozen.RawRun??throw new InvalidOperationException("Analyze this recording before assigning a physical-output snapshot.");
        MouseTrackerTabajara.Application.CompletedRecordingContract.Validate(run);
        if(SettingsIdentity.Hash(run.Settings)!=SettingsIdentity.Hash(asset.Settings))throw new InvalidOperationException("Current settings differ from the analyzed run; reanalyze before assignment.");
        if(!frozen.IsCurrent())throw new InvalidOperationException("Study inputs changed during assignment; retry.");
        var fingerprint=frozen.CorrectionFingerprint;var evidence=JsonDefaults.Deserialize<CameraProfileEvidence>(profileRow.Json);
        return History.Append("assignment",asset.Id,new CameraAssignment(profileRow.Id,profile.Id,evidence.SavedMaterialSha256,frame,observation,tolerances,SettingsIdentity.Hash(asset.Settings),run.Id,fingerprint,frozen.RunFingerprint,reviewer.Trim(),reason.Trim(),assumptions));
    }
    public PhysicalPathEvidence Derive(string videoId)
    {
        var assignmentRow=History.Read("assignment",videoId).LastOrDefault()??throw new InvalidOperationException("No accepted profile assignment. Physical output held.");var assignment=JsonDefaults.Deserialize<CameraAssignment>(assignmentRow.Json);
        var frozen=CameraRunSnapshot.Read(Store.DatabasePath,videoId);var asset=frozen.Asset;var raw=frozen.RawRun;var holds=ImmutableArray.CreateBuilder<string>();
        var profileRow=History.Read("profile").SingleOrDefault(r=>r.Id==assignment.ProfileRowId);if(profileRow is null)holds.Add("Assigned profile material is missing.");
        CalibrationProfile? profile=null;if(profileRow is not null){profile=Replay(profileRow);if(profile.Id!=assignment.ProfileId || JsonDefaults.Deserialize<CameraProfileEvidence>(profileRow.Json).SavedMaterialSha256!=assignment.ProfileMaterialHash)holds.Add("Profile revision changed.");}
        try{RequireFrame(assignment.ObservedFrame,asset);}catch(Exception ex){holds.Add(ex.Message);}
        if(raw is null || raw.Id!=assignment.RunId || raw.SourceSha256!=asset.SourceSha256 || SettingsIdentity.Hash(asset.Settings)!=assignment.SettingsHash || (raw is not null && SettingsIdentity.Hash(raw.Settings)!=assignment.SettingsHash))holds.Add("Source/settings/run changed; physical output is stale.");
        if(profileRow is not null){var reference=JsonDefaults.Deserialize<CameraProfileEvidence>(profileRow.Json);try{RequireFrame(reference.ReferenceFrame,Current(reference.ReferenceFrame.VideoId));}catch(Exception ex){holds.Add("Profile reference source changed/unavailable: "+ex.Message);}}
        if(raw is not null && (frozen.CorrectionFingerprint!=assignment.CorrectionFingerprint || frozen.RunFingerprint!=assignment.RunFingerprint))holds.Add("Corrections changed; physical output is stale. Reassign after inspecting the new revision.");
        if(!asset.Settings.ArenaConfirmed)holds.Add("Current pool/water-plane domain confirmation is absent.");
        if(raw is not null && !Store.Reviewed(raw.Id))holds.Add("Tracking review is not current; physical position output remains held.");
        if(profile is not null){var check=CameraReuse.Evaluate(profile,assignment.Observation,assignment.Tolerances);if(!check.CanReuse)holds.AddRange(check.HoldReasons);}
        if (profile is not null && History.Read("profile").Where(r=>profileRow is not null && r.Ordinal>profileRow.Ordinal).Select(Replay).Any(p=>p.Request.CameraId==profile.Request.CameraId && p.Request.ReferenceSourceSha256==profile.Request.ReferenceSourceSha256 && p.Request.ReferenceFrame==profile.Request.ReferenceFrame && p.Id!=profile.Id)) holds.Add("Measured profile/check revision changed; reassign after inspecting the new revision.");
        var frames=ImmutableArray.CreateBuilder<PhysicalFrame>();double path=0;PhysicalFrame? previous=null;var effective=frozen.EffectiveRun();
        if(holds.Count==0 && effective is not null && profile is not null)
        {
            foreach(var point in effective.Points)
            {
                if(!point.Accepted){frames.Add(new(point.Frame,point.TimeSeconds,null,null,false));previous=null;continue;}
                var x=point.X!.Value;var y=point.Y!.Value;var u=(x-asset.Settings.Cx*asset.Metadata.Width)/(asset.Settings.Rx*asset.Metadata.Width);var v=(y-asset.Settings.Cy*asset.Metadata.Height)/(asset.Settings.Ry*asset.Metadata.Height);
                if(u*u+v*v>1.0201 || !InsideControlHull(new(x,y),profile.Request.ControlLandmarks.Select(p=>p.ImagePixels).ToArray())){holds.Add("Accepted point is outside the confirmed pool or measured control domain; mapping held.");break;}
                var room=profile.ToRoom(new(x,y));var item=new PhysicalFrame(point.Frame,point.TimeSeconds,room.X,room.Y,true);frames.Add(item);
                if(previous is{Accepted:true}prior && prior.Frame+1==item.Frame)path+=new Point2(prior.RoomXcm!.Value,prior.RoomYcm!.Value).DistanceTo(room);previous=item;
            }
        }
        if(!frozen.IsCurrent() || History.Read("assignment",videoId).Last().Id!=assignmentRow.Id)holds.Add("Study/profile assignment changed during derived calculation; output is stale.");
        if(holds.Count>0){frames.Clear();path=0;}
        return new(assignmentRow.Id,assignment.ProfileId,asset.SourceSha256,raw?.Id??"",SettingsIdentity.Hash(asset.Settings),assignment.CorrectionFingerprint,frames.ToImmutable(),path,effective?.Points.Count(p=>!p.Accepted)/(asset.Metadata.Fps)??0,"Room centimeters; adjacent accepted corrected frames only, without gap bridging. Decoder interval only; biological-window/group endpoints remain held. Legacy normalized/ellipse estimates are unchanged.",holds.ToImmutable());
    }
    public CameraHistoryRow SaveDerived(string videoId){var output=Derive(videoId);if(!output.Holds.IsEmpty)throw new InvalidOperationException(string.Join(" ",output.Holds));return History.Append("derived",videoId,output);}
    private CameraHistoryRow Authoritative(CameraHistoryRow supplied,string kind){var row=History.Read(kind).SingleOrDefault(r=>r.Id==supplied.Id);if(row is null || row.ContentSha256!=supplied.ContentSha256 || row.Json!=supplied.Json || supplied.Kind!=kind)throw new InvalidDataException("Camera evidence row does not match immutable study material.");return row;}
    private static string Site(PlatformSite? site)=>site is null?"unset":$"({site.X:G},{site.Y:G}), radius fraction {site.Radius:G}";
    private static string Zones(IEnumerable<ExclusionRegion> zones)=>string.Join(";",zones.Select(z=>$"({z.Left:G},{z.Top:G})–({z.Right:G},{z.Bottom:G})"));
    private VideoAsset Current(string id)=>Store.Videos().Single(v=>v.Id==id);
    private static void RequireReview(string reviewer,string reason){if(string.IsNullOrWhiteSpace(reviewer)||string.IsNullOrWhiteSpace(reason))throw new ArgumentException("Reviewer and reason are required.");}
    private static void RequireObservation(CameraFrameIdentity frame,CameraObservation observation){if(observation.SourceSha256!=frame.SourceSha256 || observation.SourceFrame!=frame.Frame || observation.ImageWidth!=frame.Width || observation.ImageHeight!=frame.Height || string.IsNullOrWhiteSpace(observation.CameraId))throw new InvalidDataException("Observation does not belong to this exact source frame/camera declaration.");}
    public static ImmutableArray<string> StationaryPresetCheck(string cameraId,int width,int height,ImmutableArray<FiducialObservation> reference,CameraObservation observation,ShiftTolerances tolerances)
    {
        var holds=ImmutableArray.CreateBuilder<string>();if(cameraId!=observation.CameraId)holds.Add("Camera identity differs.");if(width!=observation.ImageWidth || height!=observation.ImageHeight)holds.Add("Resolution differs; no silent rescaling.");
        if(!observation.StationaryMarkersConfirmed)holds.Add("Stationary marker confirmation is required.");
        if(!double.IsFinite(tolerances.MaximumTranslationPixels)||tolerances.MaximumTranslationPixels<0||!double.IsFinite(tolerances.MaximumResidualPixels)||tolerances.MaximumResidualPixels<=0)throw new ArgumentException("Prespecify finite camera shift tolerances.");
        if(reference.IsDefault || observation.Fiducials.IsDefault || reference.Length<3 || reference.Select(p=>p.Id).Distinct().Count()!=reference.Length || observation.Fiducials.Select(p=>p.Id).Distinct().Count()!=observation.Fiducials.Length || reference.Any(p=>!p.ImagePixels.IsFinite) || observation.Fiducials.Any(p=>!p.ImagePixels.IsFinite || p.ImagePixels.X<0 || p.ImagePixels.Y<0 || p.ImagePixels.X>observation.ImageWidth-1 || p.ImagePixels.Y>observation.ImageHeight-1))throw new ArgumentException("Supply at least three distinct finite stationary reference markers and valid observed image positions.");
        if(!InsideNoncollinear(reference.Select(p=>p.ImagePixels).ToArray()))holds.Add("Stationary reference markers are collinear or degenerate.");
        var dictionary=observation.Fiducials.ToDictionary(p=>p.Id);if(reference.Any(p=>!dictionary.ContainsKey(p.Id)) || dictionary.Keys.Any(id=>!reference.Any(p=>p.Id==id)))holds.Add("Stationary markers are missing or unknown.");
        if(holds.Count==0){var shifts=reference.Select(p=>new Point2(dictionary[p.Id].ImagePixels.X-p.ImagePixels.X,dictionary[p.Id].ImagePixels.Y-p.ImagePixels.Y)).ToArray();double Median(IEnumerable<double> values){var a=values.Order().ToArray();return a.Length%2==0?(a[a.Length/2-1]+a[a.Length/2])/2:a[a.Length/2];}var shift=new Point2(Median(shifts.Select(p=>p.X)),Median(shifts.Select(p=>p.Y)));if(shift.DistanceTo(new(0,0))>tolerances.MaximumTranslationPixels)holds.Add("Camera translation exceeds tolerance; preset reuse held.");if(shifts.Max(p=>p.DistanceTo(shift))>tolerances.MaximumResidualPixels)holds.Add("Rotation/scale/nontranslation residual exceeds tolerance; preset reuse held.");}
        return holds.ToImmutable();
    }
    private static bool InsideNoncollinear(Point2[] points)=>points.SelectMany((a,i)=>points.Skip(i+1).SelectMany((b,j)=>points.Skip(i+j+2).Select(c=>Math.Abs((b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X))>1e-9))).Any(v=>v);
    private static bool InsideControlHull(Point2 point,Point2[] points)
    {
        var sorted=points.Distinct().OrderBy(p=>p.X).ThenBy(p=>p.Y).ToArray();if(sorted.Length<3)return false;double Cross(Point2 a,Point2 b,Point2 c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        var hull=new List<Point2>();foreach(var p in sorted){while(hull.Count>=2&&Cross(hull[^2],hull[^1],p)<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}var lower=hull.Count;foreach(var p in sorted.Reverse().Skip(1)){while(hull.Count>lower&&Cross(hull[^2],hull[^1],p)<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}hull.RemoveAt(hull.Count-1);return Enumerable.Range(0,hull.Count).All(i=>Cross(hull[i],hull[(i+1)%hull.Count],point)>=-1e-8);
    }
}
