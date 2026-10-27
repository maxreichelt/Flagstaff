namespace TaskTrack.Services;

public enum WorkStatus { Open, InProgress, Done }

public enum WorkPriority { Low, Medium, High }

public sealed class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Project { get; set; } = "";
    public string Assignee { get; set; } = "";
    public WorkStatus Status { get; set; }
    public WorkPriority Priority { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public double EstimateHours { get; set; }
    public string Notes { get; set; } = "";
    public bool IsBlocked { get; set; }

    public bool IsOverdue(DateTimeOffset now) =>
        Status != WorkStatus.Done && DueDate is { } due && due.Date < now.Date;
}

public sealed record Project(string Name, string Owner);

/// <summary>Fake in-memory data. Registered as a singleton so every page sees the same tasks.</summary>
public sealed class InMemoryTaskStore
{
    public const string WebsiteRedesign = "Website Redesign";
    public const string MobileApp = "Mobile App";
    public const string DataPlatform = "Data Platform";

    private readonly Lock _gate = new();
    private readonly List<TaskItem> _tasks = [];
    private readonly List<TaskItem> _archived = [];
    private int _nextId = 1;

    public InMemoryTaskStore()
    {
        Projects =
        [
            new Project(WebsiteRedesign, "Ava"),
            new Project(MobileApp, "Ben"),
            new Project(DataPlatform, "Chloe"),
        ];
        Seed();
    }

    public IReadOnlyList<Project> Projects { get; }

    public static IReadOnlyList<string> People { get; } = ["Ava", "Ben", "Chloe", "Dan", "Eli", "Fay", "Gus"];

    // Status and priority choices are select ... options: [...] in the .flag files; these map them back.
    public static string StatusText(WorkStatus status) =>
        status == WorkStatus.InProgress ? "In Progress" : status.ToString();

    public static WorkStatus ParseStatus(string? text) => text switch
    {
        "In Progress" => WorkStatus.InProgress,
        "Done" => WorkStatus.Done,
        _ => WorkStatus.Open,
    };

    public static WorkPriority ParsePriority(string? text) =>
        Enum.TryParse<WorkPriority>(text, out var p) ? p : WorkPriority.Medium;

    public Task<IReadOnlyList<TaskItem>> GetTasksAsync()
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TaskItem>>(_tasks.ToList());
        }
    }

    public Task AddAsync(TaskItem task)
    {
        lock (_gate)
        {
            task.Id = _nextId++;
            _tasks.Add(task);
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(int id, Action<TaskItem> change)
    {
        lock (_gate)
        {
            if (_tasks.Find(t => t.Id == id) is { } task)
                change(task);
        }
        return Task.CompletedTask;
    }

    /// <summary>Moves every done task out of the active list (Tasks: More > Archive done). Returns how many moved.</summary>
    public Task<int> ArchiveDoneAsync()
    {
        lock (_gate)
        {
            var done = _tasks.Where(t => t.Status == WorkStatus.Done).ToList();
            _archived.AddRange(done);
            _tasks.RemoveAll(t => t.Status == WorkStatus.Done);
            return Task.FromResult(done.Count);
        }
    }

    public Task DeleteAsync(int id)
    {
        lock (_gate)
        {
            _tasks.RemoveAll(t => t.Id == id);
        }
        return Task.CompletedTask;
    }

    private void Seed()
    {
        var today = new DateTimeOffset(DateTime.Today);

        void Add(string title, string project, string assignee, WorkStatus status, WorkPriority priority,
                 int dueInDays, double hours, string notes = "") =>
            _tasks.Add(new TaskItem
            {
                Id = _nextId++,
                Title = title,
                Project = project,
                Assignee = assignee,
                Status = status,
                Priority = priority,
                DueDate = today.AddDays(dueInDays),
                EstimateHours = hours,
                Notes = notes,
            });

        Add("Audit current site navigation", WebsiteRedesign, "Ava", WorkStatus.Done, WorkPriority.Medium, -10, 6);
        Add("New homepage wireframes", WebsiteRedesign, "Dan", WorkStatus.Done, WorkPriority.High, -4, 12);
        Add("Design system color tokens", WebsiteRedesign, "Dan", WorkStatus.InProgress, WorkPriority.Medium, 3, 8);
        Add("Migrate blog to new CMS", WebsiteRedesign, "Ava", WorkStatus.Open, WorkPriority.Low, 21, 16, "Waiting on CMS licence.");
        Add("Accessibility review", WebsiteRedesign, "Eli", WorkStatus.Open, WorkPriority.High, -2, 5, "Blocks launch.");

        Add("Offline sync prototype", MobileApp, "Ben", WorkStatus.InProgress, WorkPriority.High, 5, 20);
        Add("Push notification opt-in screen", MobileApp, "Fay", WorkStatus.Open, WorkPriority.Medium, 9, 4);
        Add("Crash on login with SSO", MobileApp, "Ben", WorkStatus.Open, WorkPriority.High, -1, 3, "Repro on Android 14 only.");
        Add("App store screenshots", MobileApp, "Fay", WorkStatus.Done, WorkPriority.Low, -7, 2);
        Add("Dark mode polish", MobileApp, "Fay", WorkStatus.InProgress, WorkPriority.Low, 12, 6);

        Add("Nightly ingest job", DataPlatform, "Chloe", WorkStatus.Done, WorkPriority.High, -14, 10);
        Add("Schema registry", DataPlatform, "Gus", WorkStatus.InProgress, WorkPriority.Medium, 7, 14);
        Add("Cost dashboard", DataPlatform, "Chloe", WorkStatus.Open, WorkPriority.Medium, 15, 8);
        Add("Retire legacy warehouse", DataPlatform, "Gus", WorkStatus.Open, WorkPriority.Low, 40, 24);
        Add("PII column tagging", DataPlatform, "Chloe", WorkStatus.Open, WorkPriority.High, -3, 6, "Compliance deadline.");
    }
}
