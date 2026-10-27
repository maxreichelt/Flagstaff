namespace Flagstaff.Cli;

/// <summary>Per-invocation state: output streams, working directory and global options.</summary>
internal sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }
    public required string WorkingDirectory { get; init; }
    public bool Json { get; init; }
    public bool Quiet { get; init; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Resolves a user-supplied path against the working directory.</summary>
    public string Resolve(string path) => Path.GetFullPath(Path.Combine(WorkingDirectory, path));

    /// <summary>Shortens a path for display: relative to the working directory when it lies inside it.</summary>
    public string Display(string fullPath)
    {
        var rel = Path.GetRelativePath(WorkingDirectory, fullPath);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? fullPath : rel;
    }

    /// <summary>Human-readable progress on stderr (suppressed by --quiet).</summary>
    public void Progress(string message)
    {
        if (!Quiet)
            Err.WriteLine(message);
    }
}
