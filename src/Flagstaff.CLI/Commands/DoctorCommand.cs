using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Flagstaff.Cli.Commands;

/// <summary>
/// <c>doctor [dir]</c>: reports the .NET SDK, MSBuild.exe, the WinUI XAML compiler in the NuGet cache, the tool
/// version and the project layout. Exit 3 only for hard blockers (no .NET SDK).
/// </summary>
internal static class DoctorCommand
{
    public static Command Create(CliHost host)
    {
        var dir = new Argument<string>("dir") { Description = "Project directory (default: .)", Arity = ArgumentArity.ZeroOrOne };
        var cmd = new Command("doctor", "Check the build environment (.NET SDK, MSBuild.exe, WinUI XAML compiler) and the project layout");
        cmd.Arguments.Add(dir);
        host.Bind(cmd, (pr, ctx) => Run(ctx, pr.GetValue(dir)));
        return cmd;
    }

    private sealed record Check(string Name, string Status, string Detail, string? Hint = null)
    {
        public JsonObject ToJson() => new() { ["name"] = Name, ["status"] = Status, ["detail"] = Detail, ["hint"] = Hint };
    }

    public static CommandResult Run(CliContext ctx, string? dir)
    {
        var result = new CommandResult("doctor");
        var checks = new List<Check>
        {
            new("flagstaff", "ok", Cli.Version),
            OperatingSystem.IsWindows()
                ? new("os", "ok", RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture)
                : new("os", "warn", RuntimeInformation.OSDescription, "WinUI 3 apps build and run on Windows only; check/compile/stubs work anywhere."),
        };

        var sdk = ProcessRunner.Run("dotnet", ["--version"], ctx.WorkingDirectory, timeout: TimeSpan.FromSeconds(30));
        var sdkVersion = sdk.Lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        var hasSdk = sdk.Started && sdk.ExitCode == 0 && sdkVersion is not null;
        checks.Add(hasSdk
            ? new("dotnetSdk", "ok", sdkVersion!)
            : new("dotnetSdk", "error", sdk.StartError ?? string.Join(" ", sdk.Lines).Trim(), "Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10"));

        var vswhere = MsBuildLocator.FindVsWhere();
        var msbuild = MsBuildLocator.Find();
        checks.Add(msbuild is not null
            ? new("msbuild", "ok", msbuild)
            : new("msbuild", "warn", vswhere is null ? "vswhere.exe not found; no Visual Studio install detected" : "no MSBuild.exe found by vswhere",
                "'flagstaff build' falls back to 'dotnet build', which hides XAML compiler errors. Install: " + MsBuildLocator.BuildToolsInstallHint));

        var xamlCompiler = FindXamlCompiler();
        checks.Add(xamlCompiler is not null
            ? new("xamlCompiler", "ok", xamlCompiler)
            : new("xamlCompiler", "warn", "Microsoft.WindowsAppSDK XamlCompiler not found in the NuGet cache",
                "It is downloaded by the first restore of a WinUI project ('flagstaff build')."));

        var projectDir = ctx.Resolve(dir ?? ".");
        var project = ProjectInfo.ForDirectory(projectDir);
        checks.Add(project.ProjectFile is not null
            ? new("project", "ok", project.ProjectFile)
            : new("project", "warn", $"no single .csproj in {projectDir}", "Run doctor from the app folder, or create one with 'flagstaff init <AppName>'."));
        checks.Add(project.HasFlags
            ? new("flagsDir", "ok", project.FlagsDirectory)
            : new("flagsDir", "warn", $"{project.FlagsDirectory} not found", "Put .flag files in Flags/."));
        checks.Add(Directory.Exists(project.GeneratedDirectory)
            ? new("generatedDir", "ok", project.GeneratedDirectory)
            : new("generatedDir", "warn", $"{project.GeneratedDirectory} not found", "Created by 'flagstaff compile' or the first build."));

        result.Extra["checks"] = new JsonArray(checks.Select(c => (JsonNode)c.ToJson()).ToArray());
        result.Extra["dotnetSdk"] = hasSdk ? sdkVersion : null;
        result.Extra["msbuild"] = msbuild;
        result.Extra["xamlCompiler"] = xamlCompiler;
        result.Extra["windowsAppSdkVersion"] = Cli.WindowsAppSdkVersion;

        foreach (var c in checks)
        {
            var mark = c.Status switch { "ok" => "[ok]  ", "warn" => "[warn]", _ => "[FAIL]" };
            result.TextLines.Add($"{mark} {c.Name,-13} {c.Detail}");
            if (c.Status != "ok" && c.Hint is not null)
                result.TextLines.Add($"       {"",-13} hint: {c.Hint}");
        }

        if (!hasSdk)
            result.Fail(".NET SDK not found.", ExitCodes.Environment, "Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10");
        return result;
    }

    /// <summary>XamlCompiler.exe from the newest Microsoft.WindowsAppSDK(.WinUI) package in the NuGet cache.</summary>
    public static string? FindXamlCompiler()
    {
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(packages))
            packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (!Directory.Exists(packages))
            return null;

        var candidates = new List<(Version Version, string Path)>();
        foreach (var id in new[] { "microsoft.windowsappsdk.winui", "microsoft.windowsappsdk" })
        {
            var pkg = Path.Combine(packages, id);
            if (!Directory.Exists(pkg))
                continue;
            foreach (var versionDir in Directory.EnumerateDirectories(pkg))
            {
                var tools = Path.Combine(versionDir, "tools");
                if (!Directory.Exists(tools))
                    continue;
                var exe = Directory.EnumerateFiles(tools, "XamlCompiler.exe", SearchOption.AllDirectories).FirstOrDefault()
                    ?? Directory.EnumerateFiles(tools, "Microsoft.UI.Xaml.Markup.Compiler*.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (exe is null)
                    continue;
                var numeric = Path.GetFileName(versionDir).Split('-')[0];
                candidates.Add((Version.TryParse(numeric, out var v) ? v : new Version(0, 0), exe));
            }
        }
        return candidates.OrderByDescending(c => c.Version).Select(c => c.Path).FirstOrDefault();
    }
}
