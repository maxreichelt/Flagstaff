using TaskTrack.Services;

namespace TaskTrack.ViewModels;

// The generated TaskRow (TaskRow.g.cs) has the columns; the store id is hand-written.
// Row bindings are OneTime: rows are replaced, not mutated.
public partial class TaskRow
{
    public int Id { get; set; }
}

public partial class TasksViewModel
{
    private const string AllProjects = "All projects";
    private const string AllStatuses = "All statuses"; // first entry of the Status filter's options: in tasks.flag

    private readonly InMemoryTaskStore _store;
    private TaskRow? _editing;
    private TaskRow? _pendingDelete;
    private bool _loaded;

    public TasksViewModel(InMemoryTaskStore store)
    {
        _store = store;
    }

    private partial async Task OnLoadAsync()
    {
        ProjectOptions.Clear();
        ProjectOptions.Add(AllProjects);
        foreach (var project in _store.Projects)
            ProjectOptions.Add(project.Name);

        ProjectFilter ??= AllProjects;
        if (StatusFilter.Length == 0)
            StatusFilter = AllStatuses;

        _loaded = true;
        await RefreshAsync();
    }

    // Both filter selects have on SelectionChanged -> vm.ApplyFiltersCommand: the filters apply as soon as
    // they change (the bound ProjectFilter/StatusFilter is already updated when the command runs).
    private partial Task OnApplyFiltersAsync() => RefreshIfLoadedAsync();

    // More > Export CSV: writes the rows currently listed to %TEMP%\TaskTrack-tasks.csv.
    private partial async Task OnExportAsync()
    {
        var csv = new System.Text.StringBuilder("Title,Project,Assignee,Status,Priority,Due" + Environment.NewLine);
        foreach (var row in Tasks)
        {
            string[] cells = [row.Title, row.Project, row.Assignee, row.Status, row.Priority,
                row.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? ""];
            csv.Append(string.Join(',', cells.Select(Quote))).Append(Environment.NewLine);
        }

        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "TaskTrack-tasks.csv"), csv.ToString());
        ShowExportedToast = true;

        static string Quote(string cell) => $"\"{cell.Replace("\"", "\"\"")}\"";
    }

    // More > Archive done, enabled(vm.HasDone): moves done tasks out of the active list.
    private partial async Task OnArchiveDoneAsync()
    {
        if (await _store.ArchiveDoneAsync() > 0)
            ShowArchivedToast = true;
        await RefreshAsync();
    }

    // Row click (table ... -> vm.EditCommand(item)) opens the "Edit task" dialog for that row.
    private partial Task OnEditAsync(TaskRow item)
    {
        _editing = item;
        EditTitle = item.Title;
        EditStatus = item.Status;
        EditPriority = item.Priority;
        EditDueDate = item.DueDate;
        IsEditOpen = true;
        return Task.CompletedTask;
    }

    private partial Task OnCancelEditAsync()
    {
        IsEditOpen = false;
        _editing = null;
        return Task.CompletedTask;
    }

    private partial async Task OnSaveEditAsync()
    {
        IsEditOpen = false;
        if (_editing is not { } row)
            return;

        _editing = null;
        var title = string.IsNullOrWhiteSpace(EditTitle) ? row.Title : EditTitle.Trim();
        await _store.UpdateAsync(row.Id, t =>
        {
            t.Title = title;
            t.Status = InMemoryTaskStore.ParseStatus(EditStatus);
            t.Priority = InMemoryTaskStore.ParsePriority(EditPriority);
            t.DueDate = EditDueDate;
        });
        await RefreshAsync();
        ShowUpdatedToast = true; // toast ... duration: 3000 hides it again
    }

    // Row action: button "Done" -> vm.CompleteCommand(item) enabled(item.Status != "Done")
    private partial async Task OnCompleteAsync(TaskRow item)
    {
        await _store.UpdateAsync(item.Id, t => t.Status = WorkStatus.Done);
        await RefreshAsync();
        ShowUpdatedToast = true;
    }

    // Row action: button "Delete" -> vm.RequestDeleteCommand(item) opens the confirm.
    private partial Task OnRequestDeleteAsync(TaskRow item)
    {
        _pendingDelete = item;
        IsDeleteVisible = true;
        return Task.CompletedTask;
    }

    // confirm ... -> vm.DeleteCommand: the confirming command clears the flag itself.
    // Its Cancel button is generated (CancelIsDeleteVisibleCommand).
    private partial async Task OnDeleteAsync()
    {
        IsDeleteVisible = false;
        if (_pendingDelete is not { } row)
            return;

        _pendingDelete = null;
        await _store.DeleteAsync(row.Id);
        await RefreshAsync();
        ShowDeletedToast = true;
    }

    private Task RefreshIfLoadedAsync() => _loaded ? RefreshAsync() : Task.CompletedTask;

    private async Task RefreshAsync()
    {
        var tasks = await _store.GetTasksAsync();
        HasDone = tasks.Any(t => t.Status == WorkStatus.Done);

        // Settings > Advanced "Show completed tasks" off hides done tasks unless the Status filter asks for them.
        var hideDone = !SettingsViewModel.ShowCompletedTasks && StatusFilter != "Done";
        var filtered = tasks
            .Where(t => ProjectFilter is null or AllProjects || t.Project == ProjectFilter)
            .Where(t => StatusFilter is "" or AllStatuses || InMemoryTaskStore.StatusText(t.Status) == StatusFilter)
            .Where(t => !(hideDone && t.Status == WorkStatus.Done))
            .OrderBy(t => t.DueDate);

        Tasks.Clear();
        foreach (var t in filtered)
        {
            Tasks.Add(new TaskRow
            {
                Id = t.Id,
                Title = t.Title,
                Project = t.Project,
                Assignee = t.Assignee,
                Status = InMemoryTaskStore.StatusText(t.Status),
                Priority = t.Priority.ToString(),
                DueDate = t.DueDate,
            });
        }
    }
}
