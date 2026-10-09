using System.Windows;
namespace MouseTrackerTabajara.Desktop;
public partial class MainWindow
{
    private void OpenExperiment(object sender, RoutedEventArgs e)
    {
        try { new ExperimentWindow(_model.Workspace.Store.DatabasePath, _model.Blind) { Owner = this }.Show(); }
        catch (Exception) { _model.Status = "Experiment view held. Check study integrity and reopen."; }
    }
}
