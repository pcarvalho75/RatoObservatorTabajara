using System.Windows;
using MouseTrackerTabajara.Core;
namespace MouseTrackerTabajara.Desktop;
public partial class MainWindow
{
    public CameraCalibrationWindow? CameraSetupWindow{get;private set;}
    private void OpenCameraSetup(object sender,RoutedEventArgs e)
    {
        try
        {
            var row=_model.Selected??throw new InvalidOperationException("Select a recording for camera setup.");
            if(_model.Busy)throw new InvalidOperationException("Wait for analysis before changing camera setup.");
            StopPlayback();var sourceId=row.Asset.Id;var frame=CurrentExactFrame();
            CameraSetupWindow=new(_model.Workspace.Store,sourceId,frame,_model.Blind,(settings,reason)=>
            {
                if(_model.Busy || _model.Selected?.Asset.Id!=sourceId)throw new InvalidOperationException("The selected recording changed; return to this target and preview again.");
                var current=_model.Workspace.Store.Videos().Single(v=>v.Id==sourceId);
                if(JsonDefaults.Serialize(current)!=JsonDefaults.Serialize(_model.Selected.Asset))throw new InvalidOperationException("Target metadata/settings changed; reload the recording before applying a preset.");
                PersistSettings(settings,reason);return _model.Selected.Asset.Settings;
            }){Owner=this};CameraSetupWindow.Show();
        }
        catch(Exception ex){_model.Status=_model.Blind?"Camera setup held; inspect the selected source.":ex.Message;}
    }
}
