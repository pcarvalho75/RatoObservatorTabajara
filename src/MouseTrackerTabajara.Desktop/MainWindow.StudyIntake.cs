using System.ComponentModel;
using System.Windows;
namespace MouseTrackerTabajara.Desktop;
public partial class MainWindow
{
    public StudyIntakeWindow? IntakeWindow { get; private set; }
    /// <summary>True while an intake apply holds the main window's Busy gate (processing, metadata edits and project changes wait).</summary>
    public bool IntakeHoldsBusy => _intakeHoldsBusy;
    private bool _intakeHoldsBusy, _closeAfterIntake, _intakeCloseFence;

    private void OpenStudyIntake(object sender, RoutedEventArgs e)
    {
        if (_model.Busy) { _model.Status = "Finish or cancel processing before bulk study intake."; return; }
        try
        {
            if (IntakeWindow is { IsVisible: true } open) { open.Activate(); return; }
            var window = new StudyIntakeWindow(_model.Workspace.Store.DatabasePath, _model.Blind) { Owner = this };
            // Apply takes the same Busy gate as batch processing: it is refused while the main window is busy, and while it runs
            // the main window cannot start processing, edit metadata, import or change/snapshot the project.
            window.AcquireOwner = () =>
            {
                if (_model.Busy) return "The main window is processing or saving. Finish or cancel it before applying the intake; nothing was written.";
                _model.Busy = true; _intakeHoldsBusy = true;
                _model.Status = "Bulk intake is applying; processing, metadata edits and project changes wait until it records its outcome.";
                return null;
            };
            window.ReleaseOwner = ReleaseIntakeGate;
            window.IntakeApplied += async (_, receipt) =>
            {
                try
                {
                    _model.Reload();
                    _model.Status = $"Bulk intake saved plan revision {receipt.PlanRevisionAfter}; {receipt.RegisteredRecordingIds.Length} sources registered with unconfirmed identity. Confirm identities, allocation and roster locally.";
                    await ShowSelectedAsync();
                }
                catch (Exception) { _model.Status = "Bulk intake saved; reload the workspace to refresh the recording list."; }
            };
            window.Closed += (_, _) =>
            {
                ReleaseIntakeGate();
                if (ReferenceEquals(IntakeWindow, window)) IntakeWindow = null;
                if (_closeAfterIntake) { _closeAfterIntake = false; Dispatcher.BeginInvoke(Close); }
            };
            if (!_intakeCloseFence) { _intakeCloseFence = true; Closing += HoldCloseForIntake; }
            IntakeWindow = window; window.Show();
        }
        catch (Exception) { _model.Status = "Bulk study intake held. Check study integrity and reopen."; }
    }

    private void ReleaseIntakeGate()
    {
        if (!_intakeHoldsBusy) return;
        _intakeHoldsBusy = false; _model.Busy = false;
    }

    /// <summary>Closing the main window during an intake preview/apply waits for its audited outcome, then closes both windows.</summary>
    private void HoldCloseForIntake(object? sender, CancelEventArgs args)
    {
        if (IntakeWindow is not { Busy: true } intake) return;
        args.Cancel = true; _closeAfterIntake = true;
        _model.Status = "Closing after bulk intake records its outcome…";
        intake.RequestClose();
    }
}
