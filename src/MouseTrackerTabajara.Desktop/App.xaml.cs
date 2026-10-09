using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Embedded test/probe hosts own their windows and resource-only App instance.
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly) return;
        StreamWriter? writer = null; TextWriterTraceListener? listener = null;
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var diagnostics = Argument(e.Args, "--diagnostics") ?? System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(StudyLocation.DiagnosticsLog(local))!,
                $"startup-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(diagnostics))!);
            writer = new StreamWriter(diagnostics, false) { AutoFlush = true };
            listener = new TextWriterTraceListener(writer); PresentationTraceSources.DataBindingSource.Listeners.Add(listener); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            string database; string? studyNote;
            var geometrySmoke = Argument(e.Args, "--geometry-smoke");
            var reviewSmoke = Argument(e.Args, "--review-smoke");
            if (geometrySmoke is not null)
            {
                Directory.CreateDirectory(geometrySmoke);
                database = System.IO.Path.Combine(geometrySmoke, "synthetic.study.db");
                studyNote = "Synthetic geometry smoke. Original recordings were not opened.";
            }
            else if (reviewSmoke is not null)
            {
                Directory.CreateDirectory(reviewSmoke);
                database = System.IO.Path.Combine(reviewSmoke, "synthetic.study.db");
                studyNote = "Synthetic blinded-review smoke. Original recordings were not opened.";
            }
            else if (Argument(e.Args, "--study") is { } requested) { database = File.Exists(requested) ? ProcessedProject.ValidateForOpen(requested) : requested; studyNote = null; }
            else
            {
                var choice = StudyLocation.Resolve(StudyLocation.PreferredDatabase(local), StudyLocation.LegacyDatabase(local));
                database = choice.DatabasePath; studyNote = choice.Note;
            }
            var store = File.Exists(database) ? ProcessedProject.OpenProject(database) : new StudyStore(database);
            var model = new MainViewModel(new WorkspaceService(store), studyNote) { Blind = e.Args.Contains("--blind-review",StringComparer.Ordinal) };
            var window = new MainWindow(model);window.BlindReview.IsChecked=model.Blind; MainWindow = window;
            await window.InitializeAsync(Argument(e.Args, "--video"), Argument(e.Args, "--analysis"));
            if (geometrySmoke is not null)
            {
                await window.RunGeometrySmokeAsync(geometrySmoke);
                writer.WriteLine("Geometry smoke completed.");
                writer.WriteLine(window.GeometrySmokeReport);
                Shutdown(0);
            }
            else if (reviewSmoke is not null)
            {
                await window.RunReviewSmokeAsync(reviewSmoke);
                writer.WriteLine("Review smoke completed.");
                writer.WriteLine(window.ReviewSmokeReport);
                Shutdown(0);
            }
            else if (Argument(e.Args, "--smoke-screenshot") is { } screenshot)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                window.SaveSmokeScreenshot(screenshot);
                writer.WriteLine("Native WPF startup, layout and screenshot completed.");
                writer.WriteLine(window.BrandLayoutReport);
                Shutdown(0);
            }
            else { window.Show();await window.WaitForSourceReadinessAsync();window.StartAutomaticUpdates();SignalUpdateReady(Argument(e.Args,"--update-ready"),Argument(e.Args,"--study"),model.Blind); writer.WriteLine("Native WPF startup completed."); }
        }
        catch (Exception ex)
        {
            try { writer?.WriteLine(ex.ToString()); } catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or ObjectDisposedException) { Debug.WriteLine(logError); }
            Debug.WriteLine(ex);
            if (Argument(e.Args, "--smoke-screenshot") is null && Argument(e.Args, "--geometry-smoke") is null && Argument(e.Args, "--review-smoke") is null)
                MessageBox.Show(e.Args.Contains("--blind-review",StringComparer.Ordinal)?"Startup could not complete. The original workspace and update checkpoints are retained; review the local startup diagnostics.":ex.Message, "Mouse Tracker Tabajara startup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
        finally
        {
            if (listener is not null) PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            try { listener?.Flush(); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or ObjectDisposedException) { Debug.WriteLine(logError); }
            finally
            {
                try { writer?.Dispose(); }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or ObjectDisposedException) { Debug.WriteLine(logError); }
            }
        }
    }
    private static string? Argument(string[] args, string name)
    { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    public static string? FindRepositoryFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = System.IO.Path.Combine(directory.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
