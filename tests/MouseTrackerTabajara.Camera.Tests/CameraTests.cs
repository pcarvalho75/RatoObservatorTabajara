using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Window = System.Windows.Window;
using Rect = System.Windows.Rect;
using Xunit;
using Xunit.Abstractions;
namespace MouseTrackerTabajara.Camera.Tests;

public sealed class CameraFixture:IDisposable
{
    public string Folder{get;}public StudyStore Store{get;}public VideoAsset Asset{get;}public AnalysisRun Raw{get;}
    public CameraFixture(string? folder=null,int width=200,StudyStore? existingStore=null,Func<Point2,Point2>? cameraTransform=null)
    {
        Folder=folder??Path.Combine(Path.GetTempPath(),"tabajara-camera-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Folder);Store=existingStore??new(Path.Combine(Folder,"study.db"));var path=Path.Combine(Folder,"synthetic.avi");
        var sourceVariant=existingStore?.Videos().Count??0;if(sourceVariant>20)throw new InvalidOperationException("Synthetic camera fixture supports at most20 shared-store background variants without collisions.");
        using(var writer=new VideoWriter(path,VideoWriter.FourCC('F','F','V','1'),10,new OpenCvSharp.Size(width,width)))
        {
            Assert.True(writer.IsOpened());for(var frame=0;frame<12;frame++){using var image=new Mat(width,width,MatType.CV_8UC3,new Scalar(180+frame+sourceVariant,190,200));foreach(var point in Markers(width))Cv2.Circle(image,Pixel(cameraTransform?.Invoke(point.ImagePixels)??point.ImagePixels),2,new Scalar(0,220,220),-1);Cv2.Circle(image,Pixel(cameraTransform?.Invoke(new(width/2,20))??new(width/2,20)),2,new Scalar(0,220,220),-1);if(frame!=2)Cv2.Circle(image,Pixel(cameraTransform?.Invoke(new(width/2-20+frame*2,width/2))??new(width/2-20+frame*2,width/2)),8,new Scalar(5,5,5),-1);writer.Write(image);}
        }
        Asset=new(){Path=path,SourceSha256=OpenCvTracker.HashFile(path),Metadata=OpenCvTracker.Probe(path),Settings=new(){Cx=.5,Cy=.5,Rx=.4,Ry=.4,ArenaConfirmed=true},AnimalId="synthetic",AnimalIdConfirmed=true,Phase=TrialMode.Probe};Store.SaveVideo(Asset);Raw=OpenCvTracker.Analyze(Asset);Store.SaveRun(Raw);Store.SaveProtocolWindow(Asset,new(){RequestedExposureSeconds=.9,ReleaseFrame=1,ReleaseConfirmed=false,WindowConfirmed=true,Reviewer="synthetic protocol reviewer",Reason="prespecified synthetic exposure"});Store.RecordReview(Raw,true);
    }
    private static OpenCvSharp.Point Pixel(Point2 point)=>new((int)Math.Round(point.X),(int)Math.Round(point.Y));
    public static ImmutableArray<FiducialObservation> Markers(int width=200){var low=width==200?20:15;var high=width==200?180:110;return[new("C1",new(low,low)),new("C2",new(high,low)),new("C3",new(high,high)),new("C4",new(low,high))];}
    public CameraFrameIdentity Frame(int frame=0){using var decoder=new FrameDecoderPool(1);var bytes=decoder.Read(Asset.Path,frame,expectedSha256:Asset.SourceSha256);var bitmap=CameraFrameMaterial.Bitmap(bytes);return new(Asset.Id,Asset.SourceSha256,frame,Asset.Metadata.Width,Asset.Metadata.Height,CameraFrameMaterial.BytesHash(bytes),CameraFrameMaterial.PixelHash(bitmap));}
    public CameraObservation Observation(CameraFrameIdentity frame)=>new("camera-A",frame.Width,frame.Height,frame.SourceSha256,frame.Frame,true,Markers(frame.Width));
    public CalibrationProfile Profile(int frame=0,bool badCheck=false,int revision=1)
    {var controls=Markers().Select(p=>new MeasuredLandmark(p.Id,p.ImagePixels,new(p.ImagePixels.X*.5+10,p.ImagePixels.Y*.5-5))).ToImmutableArray();return WaterPlaneCalibration.Fit(new("camera-A",200,200,Asset.SourceSha256,frame,revision,controls,[new("Q1",new(100,20),new(badCheck?80:60,5))],.01,.01,true,"synthetic reviewer"));}
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();} // Retain synthetic success/failure evidence for independent review.
}

public sealed class CameraServiceTests
{
    [Theory][InlineData("translation")][InlineData("rotation")][InlineData("scale")][InlineData("missing")][InlineData("camera")][InlineData("resolution")][InlineData("unconfirmed")]
    public void CameraCompatibilityRejectsKnownShiftsAndMismatch(string failure)
    {
        using var fixture=new CameraFixture();var service=new CameraCalibrationService(fixture.Store);var frame=fixture.Frame();var observation=fixture.Observation(frame);var row=service.SaveProfile(fixture.Profile(),frame,"synthetic reviewer","known controls/check",true);
        var preset=service.SavePreset("stationary geometry","camera-A",frame,observation,fixture.Asset.Settings,"synthetic reviewer","known preset");
        if(failure=="resolution")
        {
            using var smaller=new CameraFixture(width:128);var sf=smaller.Frame();var so=smaller.Observation(sf);var holds=CameraCalibrationService.StationaryPresetCheck("camera-A",200,200,observation.Fiducials,so,new(1,.5));Assert.Contains(holds,h=>h.Contains("Resolution"));Assert.False(CameraReuse.Evaluate(fixture.Profile(),so,new(1,.5)).CanReuse);return;
        }
        observation=failure switch
        {
            "translation"=>observation with{Fiducials=observation.Fiducials.Select(p=>p with{ImagePixels=new(p.ImagePixels.X+5,p.ImagePixels.Y)}).ToImmutableArray()},
            "rotation"=>observation with{Fiducials=observation.Fiducials.Select(p=>p with{ImagePixels=new(100+(p.ImagePixels.X-100)*Math.Cos(.05)-(p.ImagePixels.Y-100)*Math.Sin(.05),100+(p.ImagePixels.X-100)*Math.Sin(.05)+(p.ImagePixels.Y-100)*Math.Cos(.05))}).ToImmutableArray()},
            "scale"=>observation with{Fiducials=observation.Fiducials.Select(p=>p with{ImagePixels=new(100+(p.ImagePixels.X-100)*1.05,100+(p.ImagePixels.Y-100)*1.05)}).ToImmutableArray()},
            "missing"=>observation with{Fiducials=observation.Fiducials.RemoveAt(0)},"camera"=>observation with{CameraId="other-camera"},"unconfirmed"=>observation with{StationaryMarkersConfirmed=false},_=>observation
        };
        Assert.False(service.Preview(preset,fixture.Asset.Id,frame,observation,new(1,.5)).Compatible);Assert.Throws<InvalidOperationException>(()=>service.Assign(row,frame,observation,new(1,.5),"synthetic reviewer","reuse check",true));Assert.Empty(service.History.Read("assignment"));
    }
    [Fact]
    public void BadChecksDegenerateControlsFrameFingerprintsAndHistoryMutationFailClosed()
    {
        using var fixture=new CameraFixture();var service=new CameraCalibrationService(fixture.Store);var frame=fixture.Frame();var bad=fixture.Profile(badCheck:true);Assert.False(bad.EligibleForMeasuredCoordinates);var badRow=service.SaveProfile(bad,frame,"synthetic reviewer","bad independent check retained",true);
        Assert.Throws<InvalidOperationException>(()=>service.Assign(badRow,frame,fixture.Observation(frame),new(1,.5),"synthetic reviewer","held check",true));
        var collinear=fixture.Profile().Request with{ControlLandmarks=Enumerable.Range(0,4).Select(i=>new MeasuredLandmark("L"+i,new(20+40*i,100),new(10+20*i,50))).ToImmutableArray()};Assert.Throws<ArgumentException>(()=>WaterPlaneCalibration.Fit(collinear));
        Assert.Throws<InvalidDataException>(()=>service.SaveProfile(fixture.Profile(),frame with{EncodedImageSha256=new string('f',64)},"synthetic reviewer","wrong image",true));
        Assert.Throws<InvalidDataException>(()=>service.Assign(badRow,frame,fixture.Observation(frame) with{SourceFrame=1},new(1,.5),"synthetic reviewer","wrong frame",true));
        using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+fixture.Store.DatabasePath);connection.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE camera_history SET json='{}'";Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>command.ExecuteNonQuery());command.CommandText="DELETE FROM camera_history";Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>command.ExecuteNonQuery());
    }
    [Fact]
    public void PresetPreviewCannotBeEditedAndPartialApplyRetainsPreparedEvidence()
    {
        using var fixture=new CameraFixture();var service=new CameraCalibrationService(fixture.Store);var frame=fixture.Frame();var observation=fixture.Observation(frame);var row=service.SavePreset("changed geometry","camera-A",frame,observation,fixture.Asset.Settings with{Cx=.52},"synthetic reviewer","template");var preview=service.Preview(row,fixture.Asset.Id,frame,observation,new(1,.5));
        Assert.Throws<InvalidOperationException>(()=>service.Apply(preview with{Proposed=preview.Proposed with{Cx=.55}},frame,observation,new(1,.5),"synthetic reviewer","edited preview",true));
        Assert.Throws<InvalidOperationException>(()=>service.Apply(preview,frame,observation,new(1,.5),"synthetic reviewer","no confirmation",false));
        Assert.Throws<InvalidDataException>(()=>service.Preview(row with{Json="{}"},fixture.Asset.Id,frame,observation,new(1,.5)));
        using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+fixture.Store.DatabasePath);connection.Open();using var command=connection.CreateCommand();command.CommandText="CREATE TRIGGER camera_applied_fail BEFORE INSERT ON camera_history WHEN NEW.kind='preset_application' AND json_extract(NEW.json,'$.phase')='applied' BEGIN SELECT RAISE(ABORT,'applied receipt failure'); END";command.ExecuteNonQuery();
        var partial=Assert.Throws<InvalidOperationException>(()=>service.Apply(preview,frame,observation,new(1,.5),"synthetic reviewer","recorded prepared intent",true));Assert.Contains("Settings were saved",partial.Message);Assert.Equal(.52,fixture.Store.Videos().Single().Settings.Cx);Assert.False(fixture.Store.Reviewed(fixture.Raw.Id));Assert.Single(service.History.Read("preset_application"));Assert.Contains("prepared",service.History.Read("preset_application").Single().Json);
    }
    [Fact]
    public void MeasuredCorrectedPathPreservesGapsRawAndBecomesStaleOnCorrectionsOrProfileChecks()
    {
        using var fixture=new CameraFixture();var service=new CameraCalibrationService(fixture.Store);var raw=JsonDefaults.Serialize(fixture.Raw);var source=OpenCvTracker.HashFile(fixture.Asset.Path);var frame=fixture.Frame();var profileRow=service.SaveProfile(fixture.Profile(),frame,"synthetic reviewer","measured",true);
        fixture.Store.SaveCorrection(fixture.Raw,new(fixture.Raw.Id,4,89,100,"synthetic one-pixel correction",DateTime.UtcNow));fixture.Store.RecordReview(fixture.Raw,true);
        service.Assign(profileRow,frame,fixture.Observation(frame),new(1,.5),"synthetic reviewer","same camera",true);var output=service.Derive(fixture.Asset.Id);Assert.Empty(output.Holds);Assert.Equal(12,output.Frames.Length);Assert.False(output.Frames[2].Accepted);Assert.Null(output.Frames[2].RoomXcm);Assert.InRange(Math.Abs(output.Frames[4].RoomXcm!.Value-54.5),0,1e-8);Assert.InRange(Math.Abs(output.Frames[4].RoomYcm!.Value-45),0,1e-8);
        // Eleven known frames, one explicit gap: no path segment1->3.
        Assert.InRange(Math.Abs(output.AdjacentPathCm-9),0,1e-7);Assert.InRange(Math.Abs(output.UnknownSeconds-.1),0,1e-12);service.SaveDerived(fixture.Asset.Id);
        Assert.Equal(raw,JsonDefaults.Serialize(fixture.Store.LatestRun(fixture.Asset.Id)!));Assert.Equal(source,OpenCvTracker.HashFile(fixture.Asset.Path));
        fixture.Store.SaveCorrection(fixture.Raw,new(fixture.Raw.Id,5,null,null,"new explicit unknown",DateTime.UtcNow));Assert.Contains(service.Derive(fixture.Asset.Id).Holds,h=>h.Contains("Corrections"));
        fixture.Store.RecordReview(fixture.Raw,true);service.Assign(profileRow,frame,fixture.Observation(frame),new(1,.5),"synthetic reviewer","new correction revision",true);Assert.Empty(service.Derive(fixture.Asset.Id).Holds);
        var newer=service.SaveProfile(fixture.Profile(revision:2),frame,"synthetic reviewer","new measured/check revision",true);Assert.Contains(service.Derive(fixture.Asset.Id).Holds,h=>h.Contains("revision changed"));
        var reopened=new CameraCalibrationService(new StudyStore(fixture.Store.DatabasePath));Assert.Equal(fixture.Profile(revision:2).Id,reopened.Replay(newer).Id);Assert.Single(reopened.History.Read("derived"));
    }
}

public sealed class CameraWindowTests(ITestOutputHelper output)
{
    [Fact]
    public void ActualSidebarWindowControlsFitAssignDerivePreviewApplyReopenAndDispose()
    {
        Exception? error=null;var lines=new List<string>();var thread=new Thread(()=>{try{Proof(lines);}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(150)));foreach(var line in lines)output.WriteLine(line);if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Proof(List<string> lines)
    {
        var root=Repository();var folder=Path.Combine(root,"artifacts","camera-presets","wpf-"+Guid.NewGuid().ToString("N"));using var fixture=new CameraFixture(folder);var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        MainWindow? main=null;CameraCalibrationWindow? window=null;
        try
        {
            var model=new MainViewModel(new MouseTrackerTabajara.Application.WorkspaceService(fixture.Store));main=new(model);
            var gateField=typeof(MainWindow).GetField("_decodeGate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            var entered=new TaskCompletionSource();var release=new TaskCompletionSource();var latestEntered=new TaskCompletionSource();var latestRelease=new TaskCompletionSource();
            gateField.SetValue(main,(Func<Task>)(()=>{entered.TrySetResult();return release.Task;}));main.Show();var initialization=main.InitializeAsync();Pump(entered.Task);
            gateField.SetValue(main,(Func<Task>)(()=>{latestEntered.TrySetResult();return latestRelease.Task;}));((Slider)main.FindName("FrameSlider")).Value=10;release.SetResult();Pump(latestEntered.Task);
            var premature=initialization.IsCompleted;
            if(premature){Click((Button)main.FindName("CameraSetupButton"));lines.Add("Premature initialization sidebar refusal="+model.Status);Assert.Null(main.CameraSetupWindow);latestRelease.TrySetResult();}
            Assert.False(premature,"Initialization must await the superseding source request, not just its obsolete decode.");latestRelease.SetResult();Pump(initialization);
            Assert.Contains($"Frame {(int)((Slider)main.FindName("FrameSlider")).Value} ",((TextBlock)main.FindName("FrameClock")).Text);Assert.NotNull(((Image)main.FindName("FrameImage")).Source);
            ((Slider)main.FindName("FrameSlider")).Value=11;Pump(main.WaitForSourceReadinessAsync());
            Click((Button)main.FindName("CameraSetupButton"));window=main.CameraSetupWindow;lines.Add("Main sidebar status="+model.Status);Assert.NotNull(window);Pump(window.PendingOperation);Layout(window,1050,700);
            lines.Add("Sidebar readiness status="+window.LastStatus+" requested="+window.FrameInput.Text+" pending="+window.PendingOperation.Status);Assert.NotNull(window.DisplayedBitmap);Assert.Equal(11,window.DisplayedFrame!.Frame);Assert.True(window.SourceViewport.ActualHeight>=230);var viewportRight=window.SourceViewport.TranslatePoint(new System.Windows.Point(window.SourceViewport.ActualWidth,0),(UIElement)window.Content).X;var controlsLeft=window.DeriveButton.TranslatePoint(new System.Windows.Point(0,0),(UIElement)window.Content).X;Assert.True(controlsLeft>=viewportRight,"Camera controls do not overlay source pixels.");
            using(var capture=new VideoCapture(fixture.Asset.Path,VideoCaptureAPIs.FFMPEG))
            {
                // Independent sequential original decode: neither a copied expected hash nor the pooled preview.
                using var frame=new Mat();for(var index=0;index<=11;index++)Assert.True(capture.Read(frame));
                Assert.Equal(12,(int)capture.Get(VideoCaptureProperties.PosFrames));Assert.Equal(fixture.Asset.Metadata.Width,frame.Width);Assert.Equal(fixture.Asset.Metadata.Height,frame.Height);
                Assert.Equal(CameraFrameMaterial.BytesHash(frame.ToBytes(".png")),window.DisplayedFrame.EncodedImageSha256);
                var bitmap=new FormatConvertedBitmap(window.DisplayedBitmap,PixelFormats.Bgra32,null,0);Assert.Equal(frame.Width,bitmap.PixelWidth);Assert.Equal(frame.Height,bitmap.PixelHeight);
                var width=frame.Width;var height=frame.Height;var stride=width*4;var shown=new byte[stride*height];bitmap.CopyPixels(shown,stride,0);var maximumDelta=0;var changedPixels=0;
                for(var y=0;y<height;y++)for(var x=0;x<width;x++)
                {
                    var original=frame.At<Vec3b>(y,x);var offset=y*stride+x*4;
                    var delta=Math.Max(Math.Abs(original.Item0-shown[offset]),Math.Max(Math.Abs(original.Item1-shown[offset+1]),Math.Abs(original.Item2-shown[offset+2])));
                    maximumDelta=Math.Max(maximumDelta,delta);if(delta!=0)changedPixels++;Assert.Equal(255,shown[offset+3]);
                }
                Assert.Equal(0,maximumDelta);Assert.Equal(0,changedPixels);
                lines.Add($"PASS independent original FFmpeg frame11 PNG receipt and exact all-channel/all-pixel equality: {width}x{height}, maxDelta={maximumDelta}, changedPixels={changedPixels}.");
            }
            using(var initial=new CameraCalibrationWindow(fixture.Store,fixture.Asset.Id,11))
            {
                var readiness=initial.PendingOperation;Pump(readiness);
                Assert.False(initial.IsVisible);Assert.NotNull(initial.DisplayedBitmap);Assert.Equal(11,initial.DisplayedFrame!.Frame);
                var bitmap=initial.DisplayedBitmap;var identity=initial.DisplayedFrame;
                initial.Show();Layout(initial,1050,700);
                initial.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                initial.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Layout(initial,1100,800);Pump(initial.PendingOperation);
                Assert.Same(readiness,initial.PendingOperation);Assert.Same(bitmap,initial.DisplayedBitmap);Assert.Equal(identity,initial.DisplayedFrame);
                initial.Close();
                lines.Add("Initial camera readiness loads frame11 before Show; repeated Loaded/layout retains the same pending task, pixels and source identity.");
            }
            window.CameraIdInput.Text="camera-A";window.ReviewerInput.Text="synthetic reviewer";window.ReasonInput.Text="known synthetic water-plane measurements";window.WaterPlaneInput.IsChecked=true;window.StationaryInput.IsChecked=true;window.PrespecifiedInput.IsChecked=true;window.LimitsInput.IsChecked=true;
            foreach(var point in CameraFixture.Markers())
            {
                var add=Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,"Add control"));Click(add);var row=window.Landmarks.Last();row.Id=point.Id;row.ImageX=point.ImagePixels.X;row.ImageY=point.ImagePixels.Y;row.RoomXcm=point.ImagePixels.X*.5+10;row.RoomYcm=point.ImagePixels.Y*.5-5;
                window.Editors.SelectedIndex=1;Pump(Task.Delay(30));Click(Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,"Add stationary observation")));var observed=window.ObservedMarkers.Last();observed.Id=point.Id;observed.ImageX=point.ImagePixels.X;observed.ImageY=point.ImagePixels.Y;window.Editors.SelectedIndex=0;Pump(Task.Delay(30));
            }
            Click(Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,"Add independent check")));var check=window.Landmarks.Last();check.Id="Q1";check.ImageX=100;check.ImageY=20;check.RoomXcm=60;check.RoomYcm=5;
            Click(window.FitButton);Assert.NotNull(window.FittedProfile);Assert.True(window.FittedProfile.EligibleForMeasuredCoordinates);Click(window.SaveProfileButton);Assert.NotNull(window.SelectedProfileRow);Click(window.AssignButton);Click(window.DeriveButton);Assert.NotNull(window.PhysicalOutput);Assert.InRange(Math.Abs(window.PhysicalOutput.AdjacentPathCm-9),0,1e-7);Assert.Equal(12,window.PhysicalGrid.Items.Count);Click(window.SaveDerivedButton);
            var pickedRow=window.Landmarks[0];window.LandmarkGrid.SelectedItem=pickedRow;var placement=DisplayToVideo.Place(window.SourceViewport.ActualWidth,window.SourceViewport.ActualHeight,200,200);window.PickAtViewport(placement.OffsetX+20.25*placement.Scale,placement.OffsetY+20.25*placement.Scale);Assert.Equal(20,pickedRow.ImageX);Assert.Equal(20,pickedRow.ImageY);Assert.Throws<ArgumentException>(()=>window.PickAtViewport(placement.OffsetX+placement.Width,placement.OffsetY));
            Click(window.FitButton);Click(window.AssignButton);Click(window.DeriveButton);Assert.NotNull(window.PhysicalOutput);
            var profileId=window.SelectedProfileRow!.Id;check.RoomXcm=80;Click(window.FitButton);Assert.False(window.FittedProfile!.EligibleForMeasuredCoordinates);window.RevisionInput.Text="2";Click(window.FitButton);Click(window.SaveProfileButton);Click(window.AssignButton);Assert.Contains("Independent check",window.LastStatus);Assert.Null(window.PhysicalOutput);
            check.RoomXcm=60;window.RevisionInput.Text="3";Click(window.FitButton);Click(window.SaveProfileButton);Click(window.AssignButton);Click(window.DeriveButton);Assert.NotNull(window.PhysicalOutput);
            var controls=window.Landmarks.Where(p=>!p.IndependentCheck).ToArray();var originals=controls.Select(p=>(p.ImageX,p.ImageY,p.RoomXcm,p.RoomYcm)).ToArray();
            for(var i=0;i<controls.Length;i++){controls[i].ImageX=new[]{20d,60,100,180}[i];controls[i].ImageY=100;controls[i].RoomXcm=controls[i].ImageX*.5+10;controls[i].RoomYcm=45;}
            Click(window.FitButton);Assert.Null(window.FittedProfile);Assert.Contains("singular",window.LastStatus,StringComparison.OrdinalIgnoreCase);
            for(var i=0;i<controls.Length;i++){controls[i].ImageX=originals[i].ImageX;controls[i].ImageY=originals[i].ImageY;controls[i].RoomXcm=originals[i].RoomXcm;controls[i].RoomYcm=originals[i].RoomYcm;}
            Click(window.AssignButton);Click(window.DeriveButton);Assert.NotNull(window.PhysicalOutput);
            fixture.Store.SaveCorrection(fixture.Raw,new(fixture.Raw.Id,4,89,100,"synthetic correction revision while window open",DateTime.UtcNow));Pump(Task.Delay(1200));Assert.Null(window.PhysicalOutput);
            fixture.Store.RecordReview(fixture.Raw,true);Click(window.AssignButton);Click(window.DeriveButton);Assert.NotNull(window.PhysicalOutput);Assert.InRange(Math.Abs(window.PhysicalOutput.Frames.Single(p=>p.Frame==4).RoomXcm!.Value-54.5),0,1e-8);
            var raw=JsonDefaults.Serialize(fixture.Store.LatestRun(fixture.Asset.Id)!);var source=OpenCvTracker.HashFile(fixture.Asset.Path);var protocols=JsonDefaults.Serialize(fixture.Store.ProtocolHistory(fixture.Asset.Id));fixture.Store.SaveEditedSettings(fixture.Store.Videos().Single() with{Settings=fixture.Store.Videos().Single().Settings with{StartSeconds=.2,EndSeconds=.9}},"Explicit synthetic detector interval, separate from camera preset");Pump(main.InitializeAsync());var before=fixture.Store.Videos().Single().Settings;
            window.PresetNameInput.Text="Known stationary camera geometry";window.DetectorInput("cx").Text="0.52";window.DetectorInput("threshold").Text="45";Click(window.SavePresetButton);Assert.NotNull(window.SelectedPresetRow);Click(window.PreviewButton);Assert.Contains("Detector interval retained",window.LastStatus);Click(window.ApplyButton);Assert.Contains("confirm Apply",window.LastStatus);Click(window.PreviewButton);window.ApplyConfirmedInput.IsChecked=true;Click(window.ApplyButton);
            var after=fixture.Store.Videos().Single().Settings;Assert.Equal(.52,after.Cx);Assert.Equal(45,after.Threshold);Assert.False(after.ArenaConfirmed);Assert.Equal(before.StartSeconds,after.StartSeconds);Assert.Equal(before.EndSeconds,after.EndSeconds);Assert.False(fixture.Store.Reviewed(fixture.Raw.Id));Assert.Equal(raw,JsonDefaults.Serialize(fixture.Store.LatestRun(fixture.Asset.Id)!));Assert.Equal(source,OpenCvTracker.HashFile(fixture.Asset.Path));Assert.Equal(protocols,JsonDefaults.Serialize(fixture.Store.ProtocolHistory(fixture.Asset.Id)));Assert.Null(window.PhysicalOutput);
            var statistics=window.DecoderStatistics;Layout(window,1050,700);Assert.True(window.StatusViewport.ActualHeight<=72);var footerBottom=window.StatusViewport.TranslatePoint(new System.Windows.Point(0,window.StatusViewport.ActualHeight),(UIElement)window.Content).Y;Assert.True(footerBottom<=((FrameworkElement)window.Content).ActualHeight+1,"Long diff remains in a scrollable footer, not clipped beyond the window.");SaveImage(window,Path.Combine(folder,"camera-minimum.png"));window.Close();Assert.Equal(0,window.DecoderStatistics.ActiveSessions);Assert.Equal(window.DecoderStatistics.Opens,window.DecoderStatistics.Disposals);
            window=new CameraCalibrationWindow(new StudyStore(fixture.Store.DatabasePath),fixture.Asset.Id,0){Owner=main};window.Show();Pump(window.PendingOperation);Assert.True(window.ProfileChoice.Items.Count>=3);Assert.Equal(2,new CameraHistoryStore(fixture.Store.DatabasePath).Read("preset_application").Count);Assert.True(new CameraCalibrationService(fixture.Store).Derive(fixture.Asset.Id).Holds.Length>0);Layout(window,1100,800);SaveImage(window,Path.Combine(folder,"camera-wide.png"));window.Close();window=null;
            foreach(var scenario in new[]{"translation","rotation","scale","camera","missing","resolution"})
            {
                Func<Point2,Point2> transform=scenario switch{"translation"=>p=>new(p.X+5,p.Y),"rotation"=>p=>new(100+(p.X-100)*Math.Cos(.05)-(p.Y-100)*Math.Sin(.05),100+(p.X-100)*Math.Sin(.05)+(p.Y-100)*Math.Cos(.05)),"scale"=>p=>new(100+(p.X-100)*1.05,100+(p.Y-100)*1.05),_=>p=>p};
                using var shifted=new CameraFixture(Path.Combine(folder,scenario),scenario=="resolution"?128:200,fixture.Store,transform);
                window=new CameraCalibrationWindow(fixture.Store,shifted.Asset.Id,0){Owner=main};window.Show();Pump(window.PendingOperation);window.CameraIdInput.Text=scenario=="camera"?"camera-B":"camera-A";window.ReviewerInput.Text="synthetic reviewer";window.ReasonInput.Text="known camera change check";window.WaterPlaneInput.IsChecked=true;window.LimitsInput.IsChecked=true;window.StationaryInput.IsChecked=true;
                window.ProfileChoice.SelectedItem=window.ProfileChoice.Items.Cast<ComboBoxItem>().Last();window.Editors.SelectedIndex=1;Pump(Task.Delay(30));
                var reference=CameraFixture.Markers(scenario=="resolution"?128:200);foreach(var marker in reference.Take(scenario=="missing"?3:4)){Click(Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,"Add stationary observation")));var observed=window.ObservedMarkers.Last();var actual=transform(marker.ImagePixels);observed.Id=marker.Id;observed.ImageX=Math.Round(actual.X);observed.ImageY=Math.Round(actual.Y);}
                Click(window.AssignButton);Assert.Null(window.PhysicalOutput);Assert.Contains(scenario switch{"translation"=>"translation","rotation"=>"rotation","scale"=>"scale","camera"=>"Camera identity","missing"=>"absent",_=>"resolution"},window.LastStatus,StringComparison.OrdinalIgnoreCase);Assert.Empty(new CameraHistoryStore(fixture.Store.DatabasePath).Read("assignment",shifted.Asset.Id));window.Close();window=null;
            }
            // Informative camera/preset aliases and WT/KO/A/B metadata stay absent in the actual blind visual tree.
            var current=fixture.Store.Videos().Single(v=>v.Id==fixture.Asset.Id);fixture.Store.SaveVideo(current with{AnimalId="WT",GroupName="A",BlindedAlias="KO-B"});var service=new CameraCalibrationService(fixture.Store);var referenceFrame=fixture.Frame();service.SavePreset("PRIVATE-WT-KO-A-B-preset","informative-WT-camera",referenceFrame,fixture.Observation(referenceFrame) with{CameraId="informative-WT-camera"},current.Settings,"PRIVATE-KO-reviewer","PRIVATE-A-B-notes");
            window=new CameraCalibrationWindow(fixture.Store,fixture.Asset.Id,0,true){Owner=main};window.Show();Pump(window.PendingOperation);Layout(window,1050,700);AssertBlind(window);
            Assert.False(window.TryRevealMetadata("", "cancelled"));Assert.True(window.IsBlinded);AssertBlind(window);
            using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+fixture.Store.DatabasePath)){connection.Open();using var command=connection.CreateCommand();command.CommandText="CREATE TRIGGER camera_reveal_fail BEFORE INSERT ON audit WHEN NEW.action='camera_setup_metadata_revealed' BEGIN SELECT RAISE(ABORT,'audit rejection'); END";command.ExecuteNonQuery();Assert.False(window.TryRevealMetadata("synthetic disclosure reviewer","explicit camera identity inspection"));Assert.True(window.IsBlinded);AssertBlind(window);command.CommandText="DROP TRIGGER camera_reveal_fail";command.ExecuteNonQuery();}
            SaveImage(window,Path.Combine(folder,"camera-blind.png"));Assert.True(window.TryRevealMetadata("synthetic disclosure reviewer","explicit camera identity inspection"));Assert.False(window.IsBlinded);Assert.Contains(window.PresetChoice.Items.Cast<ComboBoxItem>(),i=>i.Content is string text && text.Contains("PRIVATE-WT-KO"));
            using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+fixture.Store.DatabasePath)){connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM audit WHERE action='camera_setup_metadata_revealed'";Assert.Equal(1L,(long)command.ExecuteScalar()!);}
            window.PrespecifiedInput.IsChecked=true;window.FrameInput.Text="1";Assert.Null(window.DisplayedFrame);Click(window.FitButton);Assert.Contains("Exact source pixels",window.LastStatus);Click(window.LoadFrameButton);Pump(window.PendingOperation);Assert.Equal(1,window.DisplayedFrame!.Frame);
            lines.Add("Actual shifted/rotated/scaled/camera/missing/resolution WPF checks held; audited blinded metadata success/failure/cancel checks and pending-frame edit rejection passed.");
            lines.Add($"WPF camera proof passed; source={source}; profile histories={window.ProfileChoice.Items.Count}; decoder opens/disposals before close={statistics.Opens}/{statistics.Disposals}; evidence={folder}");
        }
        finally{window?.Close();main?.Close();app.Shutdown();}
    }
    private static void AssertBlind(CameraCalibrationWindow window)
    {
        Assert.True(window.IsBlinded);Assert.NotNull(window.DisplayedBitmap);var pixels=CameraFrameMaterial.PixelHash(window.DisplayedBitmap);Assert.Equal(window.DisplayedFrame!.DisplayedPixelSha256,pixels);
        foreach(var item in window.ProfileChoice.Items.Cast<ComboBoxItem>().Concat(window.PresetChoice.Items.Cast<ComboBoxItem>())){var label=(string)item.Content;Assert.DoesNotContain("PRIVATE",label);Assert.DoesNotContain("WT",label);Assert.DoesNotContain("KO",label);}
        foreach(var element in Descendants(window))
        {
            var text=element switch{TextBlock block=>block.Text,TextBox box when box.IsVisible=>box.Text,ComboBoxItem item=>item.Content as string,_=>null};
            if(text is null)continue;Assert.DoesNotContain("PRIVATE",text);Assert.DoesNotContain("WT",text);Assert.DoesNotContain("KO",text);Assert.DoesNotContain("informative",text);Assert.NotEqual("A",text);Assert.NotEqual("B",text);Assert.DoesNotContain(window.DisplayedFrame.SourceSha256[..12],text);
        }
    }
    private static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(Task.Delay(15));}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static void Layout(Window window,double width,double height){window.Width=width;window.Height=height;var content=(FrameworkElement)window.Content;content.Measure(new System.Windows.Size(width-32,height-55));content.Arrange(new Rect(0,0,width-32,height-55));content.UpdateLayout();Pump(Task.Delay(30));}
    private static void SaveImage(Window window,string path){var root=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
    private static void Pump(Task task){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(20)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};var dispatcher=Dispatcher.CurrentDispatcher;task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false));timer.Start();Dispatcher.PushFrame(frame);timer.Stop();Assert.True(task.IsCompleted,"Bounded dispatcher operation completed.");task.GetAwaiter().GetResult();}
    private static string Repository(){for(var dir=new DirectoryInfo(AppContext.BaseDirectory);dir is not null;dir=dir.Parent)if(File.Exists(Path.Combine(dir.FullName,"MouseTrackerTabajara.sln")))return dir.FullName;throw new DirectoryNotFoundException();}
}
