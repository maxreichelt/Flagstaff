using System.Globalization;
using TaskTrack.Services;

namespace TaskTrack.ViewModels;

public partial class DashboardViewModel
{
    internal const string AllProjects = "All projects";

    private readonly InMemoryTaskStore _store;
    private IReadOnlyList<TaskItem> _all = [];

    public DashboardViewModel(InMemoryTaskStore store)
    {
        _store = store;
    }

    private partial async Task OnLoadAsync()
    {
        // The Overdue stat and the alert are when(vm.OverdueCount > 0 && !vm.IsLoading).
        IsLoading = true;
        try
        {
            _all = await _store.GetTasksAsync();
        }
        finally
        {
            IsLoading = false;
        }

        ProjectNames.Clear();
        ProjectNames.Add(AllProjects);
        foreach (var project in _store.Projects)
            ProjectNames.Add(project.Name);

        if (SelectedProject is null)
            SelectedProject = AllProjects; // triggers Recalculate via OnSelectedProjectChanged
        else
            Recalculate();
    }

    partial void OnSelectedProjectChanged(string? value) => Recalculate();

    private void Recalculate()
    {
        var now = DateTimeOffset.Now;
        var tasks = SelectedProject is null or AllProjects
            ? _all
            : _all.Where(t => t.Project == SelectedProject).ToList();

        var open = tasks.Count(t => t.Status == WorkStatus.Open);
        var inProgress = tasks.Count(t => t.Status == WorkStatus.InProgress);
        var done = tasks.Count(t => t.Status == WorkStatus.Done);

        OpenCount = open.ToString(CultureInfo.CurrentCulture);
        InProgressCount = inProgress.ToString(CultureInfo.CurrentCulture);
        DoneCount = done.ToString(CultureInfo.CurrentCulture);
        OverdueCount = tasks.Count(t => t.IsOverdue(now));

        TotalCount = tasks.Count.ToString(CultureInfo.CurrentCulture);
        TotalEstimate = tasks.Sum(t => t.EstimateHours);
        RemainingEstimate = tasks.Where(t => t.Status != WorkStatus.Done).Sum(t => t.EstimateHours);

        CompletionRate = tasks.Count == 0 ? 0 : (double)done / tasks.Count;
        HighPriorityOpen = tasks
            .Count(t => t.Priority == WorkPriority.High && t.Status != WorkStatus.Done)
            .ToString(CultureInfo.CurrentCulture);
        var next = tasks
            .Where(t => t.Status != WorkStatus.Done && t.DueDate >= now.Date)
            .OrderBy(t => t.DueDate)
            .FirstOrDefault();
        NextDue = next?.DueDate;
        NextDueTitle = next?.Title ?? "Nothing scheduled";
    }
}
