using System.Windows;
namespace MouseTrackerTabajara.Desktop;
public partial class MainWindow
{
    private void OpenIndependentValidation(object sender, RoutedEventArgs e)
    {
        try { new IndependentValidationWindow(_model.Workspace.Store.DatabasePath, _model.Blind) { Owner = this }.Show(); }
        catch (Exception) { _model.Status = "Independent validation tooling is held; check study integrity. Real held-out accuracy remains unrun."; }
    }
}
