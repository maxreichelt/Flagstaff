namespace Flagstaff.Cli;

/// <summary>
/// Finds Visual Studio's <c>MSBuild.exe</c> (full-framework MSBuild surfaces WinUI XAML compiler errors that
/// <c>dotnet build</c> swallows). Order: <c>FLAGSTAFF_MSBUILD</c>, vswhere, well-known install folders.
/// </summary>
internal static class MsBuildLocator
{
    public const string BuildToolsInstallHint =
        "winget install Microsoft.VisualStudio.2022.BuildTools --override \"--add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs\"";

    public static IEnumerable<string> VsWhereCandidates()
    {
        foreach (var root in ProgramFilesRoots())
            yield return Path.Combine(root, "Microsoft Visual Studio", "Installer", "vswhere.exe");
    }

    public static string? FindVsWhere() => VsWhereCandidates().FirstOrDefault(File.Exists);

    public static string? Find()
    {
        var overridePath = Environment.GetEnvironmentVariable("FLAGSTAFF_MSBUILD");
        if (string.Equals(overridePath, "none", StringComparison.OrdinalIgnoreCase))
            return null; // force the 'dotnet build' fallback
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        if (!OperatingSystem.IsWindows())
            return null;

        if (FindVsWhere() is { } vswhere)
        {
            var result = ProcessRunner.Run(vswhere,
                ["-latest", "-prerelease", "-products", "*", "-requires", "Microsoft.Component.MSBuild",
                 "-find", @"MSBuild\**\Bin\MSBuild.exe"],
                Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(30));
            var found = result.Lines
                .Select(l => l.Trim())
                .Where(l => l.EndsWith("MSBuild.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(l))
                .OrderBy(l => l.Contains(@"\amd64\", StringComparison.OrdinalIgnoreCase) || l.Contains(@"\arm64\", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .FirstOrDefault();
            if (found is not null)
                return found;
        }

        // vswhere missing or found nothing: probe the standard layouts, newest version folder first.
        foreach (var root in ProgramFilesRoots())
        {
            var vsRoot = Path.Combine(root, "Microsoft Visual Studio");
            if (!Directory.Exists(vsRoot))
                continue;
            var candidates =
                from version in Directory.EnumerateDirectories(vsRoot)
                from edition in Directory.EnumerateDirectories(version)
                let exe = Path.Combine(edition, "MSBuild", "Current", "Bin", "MSBuild.exe")
                where File.Exists(exe)
                orderby Path.GetFileName(version) descending
                select exe;
            if (candidates.FirstOrDefault() is { } exePath)
                return exePath;
        }
        return null;
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramW6432"),
        };
        return roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase)!;
    }
}
