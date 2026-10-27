namespace TaskTrack.ViewModels;

public partial class SettingsViewModel
{
    // In-memory only: settings survive navigation (static) but not an app restart.
    private static Saved s_saved = new("Alex Example", true, false, true, DefaultEstimate: 4, ShowCompleted: true);

    private sealed record Saved(
        string Name, bool EmailOnAssign, bool DailyDigest, bool HighlightOverdue, double DefaultEstimate, bool ShowCompleted);

    // Read by the other pages: the task form's default estimate and the task list's done filter.
    internal static double DefaultEstimateHours => s_saved.DefaultEstimate;

    internal static bool ShowCompletedTasks => s_saved.ShowCompleted;

    private partial Task OnLoadAsync()
    {
        DisplayName = s_saved.Name;
        EmailOnAssign = s_saved.EmailOnAssign;
        DailyDigest = s_saved.DailyDigest;
        HighlightOverdue = s_saved.HighlightOverdue;
        DefaultEstimate = s_saved.DefaultEstimate;
        ShowCompleted = s_saved.ShowCompleted;
        return Task.CompletedTask;
    }

    private partial Task OnSaveAsync()
    {
        s_saved = new(DisplayName, EmailOnAssign, DailyDigest, HighlightOverdue, DefaultEstimate, ShowCompleted);
        ShowSavedToast = true; // toast ... duration: 3000 hides it again
        return Task.CompletedTask;
    }
}
