using System.Windows;

namespace MouseTrackerTabajara.Desktop;

public partial class MainWindow
{
    private void OpenStudyAtlas(object sender, RoutedEventArgs e)
    {
        try { new StudyAtlasWindow(_model.Workspace.Store.DatabasePath, _model.Blind) { Owner = this }.Show(); }
        catch (Exception) { _model.Status = _model.Blind ? "Atlas is held. Check study integrity." : "Atlas could not freeze the study. Check study integrity and reopen."; }
    }
}
