using TaskTrack.Services;

namespace TaskTrack.ViewModels;

public partial class TaskFormViewModel
{
    private readonly InMemoryTaskStore _store;

    public TaskFormViewModel(InMemoryTaskStore store)
    {
        _store = store;
    }

    private partial Task OnLoadAsync()
    {
        ProjectOptions.Clear();
        foreach (var project in _store.Projects)
            ProjectOptions.Add(project.Name);

        People.Clear();
        foreach (var person in InMemoryTaskStore.People)
            People.Add(person);

        Reset();
        return Task.CompletedTask;
    }

    // The toolbar's Save is enabled(vm.Title != ""); this guard also rejects whitespace-only titles.
    private partial async Task OnSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Title))
            return;

        await _store.AddAsync(new TaskItem
        {
            Title = Title.Trim(),
            Project = Project ?? _store.Projects[0].Name,
            Assignee = Assignee,
            Status = InMemoryTaskStore.ParseStatus(Status),
            Priority = InMemoryTaskStore.ParsePriority(Priority),
            DueDate = DueDate,
            EstimateHours = EstimateHours,
            Notes = Notes,
            IsBlocked = IsBlocked,
        });

        Reset();
        ShowSavedToast = true; // toast ... duration: 3000 hides it again
    }

    // dialog "Pick assignee" ... when(vm.IsPickerOpen); its Cancel is -> cancel (generated CancelIsPickerOpenCommand).
    private partial Task OnOpenPickerAsync()
    {
        PickedPerson = Assignee.Length > 0 ? Assignee : null;
        IsPickerOpen = true;
        return Task.CompletedTask;
    }

    private partial Task OnAssignAsync()
    {
        Assignee = PickedPerson ?? "";
        IsPickerOpen = false;
        return Task.CompletedTask;
    }

    private void Reset()
    {
        Title = "";
        Project = ProjectOptions.FirstOrDefault();
        Assignee = "";
        Status = "Open";
        Priority = "Medium";
        DueDate = new DateTimeOffset(DateTime.Today.AddDays(7));
        EstimateHours = SettingsViewModel.DefaultEstimateHours; // Settings > Advanced slider
        Notes = "";
        IsBlocked = false;
        NotifyAssignee = true;
    }
}
