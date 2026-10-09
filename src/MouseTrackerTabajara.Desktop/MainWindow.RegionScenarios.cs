using System.Windows;

namespace MouseTrackerTabajara.Desktop;

public partial class MainWindow
{
    private void OpenRegionScenarios(object sender, RoutedEventArgs e)
    {
        try { new RegionScenarioWindow(_model.Workspace.Store.DatabasePath, _model.Blind) { Owner = this }.Show(); }
        catch (Exception) { _model.Status = "Region scenario view held. Check study integrity and reopen."; }
    }
}
