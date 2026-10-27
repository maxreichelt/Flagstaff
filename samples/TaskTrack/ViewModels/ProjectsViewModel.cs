using System.Globalization;
using TaskTrack.Services;

namespace TaskTrack.ViewModels;

public partial class ProjectsViewModel
{
    // The "Tip" card is load: load(vm.ShowHint); once dismissed it stays unloaded for the session.
    private static bool s_hintDismissed;

    private readonly InMemoryTaskStore _store;
    private readonly Dictionary<string, Details> _details = [];

    private sealed record Details(string Owner, string Done, double Progress, int Overdue);

    public ProjectsViewModel(InMemoryTaskStore store)
    {
        _store = store;
    }

    private partial async Task OnLoadAsync()
    {
        var tasks = await _store.GetTasksAsync();
        var now = DateTimeOffset.Now;

        Projects.Clear();
        _details.Clear();
        foreach (var project in _store.Projects)
        {
            var items = tasks.Where(t => t.Project == project.Name).ToList();
            var done = items.Count(t => t.Status == WorkStatus.Done);
            var open = items.Count - done;

            _details[project.Name] = new Details(
                project.Owner,
                done.ToString(CultureInfo.CurrentCulture),
                items.Count == 0 ? 0 : (double)done / items.Count,
                items.Count(t => t.IsOverdue(now)));
            Projects.Add(new ProjectCard
            {
                Name = project.Name,
                Owner = $"Owned by {project.Owner}",
                OpenCount = $"{open} open",
            });
        }

        Summary = $"{Projects.Count} projects, {tasks.Count} tasks";
        ShowHint = !s_hintDismissed;
        Selected ??= Projects.FirstOrDefault();
    }

    private partial Task OnDismissHintAsync()
    {
        s_hintDismissed = true;
        ShowHint = false;
        return Task.CompletedTask;
    }

    // list ... selected: vm.Selected drives the detail pane (group "Details" when(vm.Selected != null)).
    partial void OnSelectedChanged(ProjectCard? value)
    {
        if (value is null || !_details.TryGetValue(value.Name, out var d))
            return;

        SelectedName = value.Name;
        SelectedOwner = d.Owner;
        SelectedOpen = value.OpenCount;
        SelectedDone = d.Done;
        SelectedProgress = d.Progress;
        SelectedPercent = d.Progress * 100; // ProgressBar runs 0..100
        SelectedOverdue = d.Overdue;
    }
}
