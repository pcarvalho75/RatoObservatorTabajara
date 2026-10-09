using System.Windows;

namespace MouseTrackerTabajara.Desktop;

public partial class MainWindow
{
    private void OpenResultStability(object sender, RoutedEventArgs e)
    {
        try { new StabilityWindow(_model.Workspace.Store, _model.Blind) { Owner = this }.Show(); }
        catch (Exception) { _model.Status = "Result stability is held; check frozen study integrity."; }
    }
}
