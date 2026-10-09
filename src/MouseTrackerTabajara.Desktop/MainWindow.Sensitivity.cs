using System.Windows;

namespace MouseTrackerTabajara.Desktop;

public partial class MainWindow
{
    private void OpenSensitivity(object sender, RoutedEventArgs e)
    {
        try { new SensitivityWindow(_model.Workspace.Store.DatabasePath, _model.Blind) { Owner = this }.Show(); }
        catch (Exception) { _model.Status = "Detector sensitivity is held. Check study integrity and reopen."; }
    }
}
