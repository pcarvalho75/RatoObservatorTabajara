using System.Windows;
namespace MouseTrackerTabajara.Desktop;
public partial class MainWindow
{
    private void OpenStudyReport(object sender, RoutedEventArgs e)
    {
        try { new StudyReportWindow(_model.Workspace.Store.DatabasePath, _model.Blind) { Owner = this }.Show(); }
        catch (Exception) { _model.Status = "Whole-study report held; verify the expected plan and frozen evidence. Researcher sign-off remains unrun."; }
    }
}
