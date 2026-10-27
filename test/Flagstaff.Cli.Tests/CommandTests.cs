using System.Text.Json.Nodes;
using Flagstaff.Cli.Commands;

namespace Flagstaff.Cli.Tests;

public class CheckTests
{
    [Fact]
    public void Clean_project_is_ok_with_full_envelope()
    {
        using var p = new TempProject();
        p.Write("Flags/app.flag", Samples.App);
        p.Write("Flags/home.flag", Samples.Home);

        var run = p.Run("check", "--format", "json");

        Assert.Equal(0, run.ExitCode);
        var json = run.Json();
        Assert.Equal("flagstaff", (string?)json["tool"]);
        Assert.Equal(Cli.Version, (string?)json["version"]);
        Assert.Equal("check", (string?)json["command"]);
        Assert.True((bool)json["ok"]!);
        Assert.Empty(json["diagnostics"]!.AsArray());
        Assert.Empty(json["outputs"]!.AsArray());
        Assert.Contains("HomeView.xaml", json["generated"]!.AsArray().Select(n => (string?)n));
        Assert.False(Directory.Exists(p.Path_("Generated")), "check must not write");
    }

    [Fact]
    public void Unknown_property_reports_FS1001_with_position()
    {
        using var p = new TempProject();
        p.Write("Flags/home.flag", """
            view HomeView {
                bind vm: HomeViewModel;
                stat "X" value: vm.Y;
            }
            """);

        var run = p.Run("check", "--format", "json");

        Assert.Equal(1, run.ExitCode);
        var json = run.Json();
        Assert.False((bool)json["ok"]!);
        var d = json["diagnostics"]!.AsArray().Single()!.AsObject();
        Assert.Equal("error", (string?)d["severity"]);
        Assert.Equal("FS1001", (string?)d["code"]);
        Assert.Equal(3, (int)d["line"]!);
        Assert.Equal(14, (int)d["column"]!);
        Assert.EndsWith("home.flag", (string?)d["file"]);
        Assert.NotNull((string?)d["hint"]);
        Assert.NotNull((string?)d["message"]);
    }

    [Fact]
    public void Text_mode_prints_canonical_msbuild_lines_on_stdout()
    {
        using var p = new TempProject();
        p.Write("Flags/home.flag", "view HomeView {\n    bind vm: HomeViewModel;\n    stat \"X\" value: vm.Y;\n}\n");

        var run = p.Run("check");

        Assert.Equal(1, run.ExitCode);
        Assert.Matches(@"home\.flag\(3,14\): error FS1001: ", run.Stdout);
        Assert.Contains("error(s)", run.Stderr);
    }

    [Fact]
    public void Missing_path_is_a_usage_error()
    {
        using var p = new TempProject();
        var run = p.Run("check", "nope", "--format", "json");
        Assert.Equal(2, run.ExitCode);
        Assert.False((bool)run.Json()["ok"]!);
    }

    [Fact]
    public void Parse_errors_exit_2_and_still_honor_json()
    {
        using var p = new TempProject();
        var run = p.Run("spec", "--bogus", "--format", "json");
        Assert.Equal(2, run.ExitCode);
        Assert.Equal(2, (int)run.Json()["exitCode"]!);
    }

    [Fact]
    public void Version_option_prints_version()
    {
        using var p = new TempProject();
        var run = p.Run("--version");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Cli.Version, run.Stdout.Trim());
        Assert.DoesNotContain('+', Cli.Version);
    }
}

public class CompileTests
{
    [Fact]
    public void Writes_manifest_and_clean_removes_only_stale_listed_files()
    {
        using var p = new TempProject();
        p.Write("Flags/app.flag", Samples.App);
        p.Write("Flags/home.flag", Samples.Home);
        var orders = p.Write("Flags/orders.flag", Samples.Orders);

        var first = p.Run("compile", "--format", "json");
        Assert.Equal(0, first.ExitCode);
        Assert.True(p.Exists("Generated/OrdersView.xaml"));
        Assert.True(p.Exists("Generated/Order.g.cs"));
        var manifest = JsonNode.Parse(p.Read("Generated/.flagstaff-manifest.json"))!["files"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("OrdersView.xaml", manifest);
        Assert.Contains("HomeViewModel.g.cs", manifest);
        Assert.Equal(first.Json()["outputs"]!.AsArray().Count, manifest.Count);

        p.Write("Generated/Handwritten.cs", "// not ours");
        File.Delete(orders);

        var second = p.Run("compile", "--clean", "--format", "json");
        Assert.Equal(0, second.ExitCode);
        Assert.False(p.Exists("Generated/OrdersView.xaml"));
        Assert.False(p.Exists("Generated/OrdersView.xaml.cs"));
        Assert.False(p.Exists("Generated/OrdersViewModel.g.cs"));
        Assert.False(p.Exists("Generated/Order.g.cs"));
        Assert.True(p.Exists("Generated/HomeView.xaml"));
        Assert.True(p.Exists("Generated/Handwritten.cs"), "unlisted files are never deleted");
        Assert.Equal(4, second.Json()["removed"]!.AsArray().Count);
    }

    [Fact]
    public void Stale_files_are_kept_when_compile_has_errors_unless_clean()
    {
        using var p = new TempProject();
        p.Write("Flags/home.flag", Samples.Home);
        p.Write("Flags/orders.flag", Samples.Orders);
        Assert.Equal(0, p.Run("compile").ExitCode);

        p.Write("Flags/orders.flag", "view OrdersView { bind vm: OrdersViewModel; stat \"X\" value: vm.Y; }");
        Assert.Equal(1, p.Run("compile").ExitCode);
        Assert.True(p.Exists("Generated/OrdersView.xaml"), "last good output survives an error");

        Assert.Equal(1, p.Run("compile", "--clean").ExitCode);
        Assert.False(p.Exists("Generated/OrdersView.xaml"));
    }

    [Fact]
    public void Watch_once_runs_one_check_and_compile_cycle()
    {
        using var p = new TempProject();
        p.Write("Flags/home.flag", Samples.Home);
        var run = p.Run("watch", "--once", "--format", "json");
        Assert.Equal(0, run.ExitCode);
        var lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length); // one JSON line per cycle + the final envelope
        Assert.Equal(1, (int)JsonNode.Parse(lines[0])!["cycle"]!);
        Assert.True(p.Exists("Generated/HomeView.xaml"));
    }

    [Fact]
    public void Namespace_option_is_applied()
    {
        using var p = new TempProject();
        p.Write("Flags/home.flag", Samples.Home);
        Assert.Equal(0, p.Run("compile", "-n", "Contoso.App", "-o", "out").ExitCode);
        Assert.Contains("namespace Contoso.App.ViewModels;", p.Read("out/HomeViewModel.g.cs"));
    }
}

public class StubsTests
{
    [Fact]
    public void Refuses_to_scan_the_working_directory_without_a_flags_folder()
    {
        using var p = new TempProject();
        p.Write("fixtures/stray.flag", Samples.Home);

        var run = p.Run("stubs", "-n", "Demo", "--format", "json");

        Assert.Equal(2, run.ExitCode);
        Assert.False(p.Exists("ViewModels/HomeViewModel.cs"));
        Assert.False(Directory.Exists(p.Path_("ViewModels")));

        var explicitRun = p.Run("stubs", "fixtures", "-n", "Demo", "--format", "json");
        Assert.Equal(0, explicitRun.ExitCode);
        Assert.True(p.Exists("ViewModels/HomeViewModel.cs"));
    }

    [Fact]
    public void Creates_then_is_idempotent_then_update_appends_missing()
    {
        using var p = new TempProject();
        p.Write("Flags/home.flag", Samples.Home);
        p.Write("Flags/orders.flag", Samples.Orders);

        var first = p.Run("stubs", "-n", "Demo", "--format", "json");
        Assert.Equal(0, first.ExitCode);
        var home = p.Read("ViewModels/HomeViewModel.cs");
        Assert.Contains("namespace Demo.ViewModels;", home);
        Assert.Contains("public partial class HomeViewModel", home);
        Assert.Contains("private partial Task OnLoadAsync() => Task.CompletedTask; // TODO", home);
        Assert.Contains("private partial Task OnSaveAsync() => Task.CompletedTask; // TODO", home);
        Assert.True(p.Exists("ViewModels/OrdersViewModel.cs"));
        Assert.Equal(2, first.Json()["created"]!.AsArray().Count);

        // Hand edits survive; a second run changes nothing.
        p.Write("ViewModels/HomeViewModel.cs", home.Replace("private partial Task OnSaveAsync() => Task.CompletedTask; // TODO", "private partial Task OnSaveAsync() { return Task.CompletedTask; } // mine"));
        var edited = p.Read("ViewModels/HomeViewModel.cs");
        var second = p.Run("stubs", "-n", "Demo", "--format", "json");
        Assert.Equal(0, second.ExitCode);
        Assert.Empty(second.Json()["created"]!.AsArray());
        Assert.Equal(edited, p.Read("ViewModels/HomeViewModel.cs"));

        // A new command with an item parameter: reported, then appended with --update.
        p.Write("Flags/orders.flag", """
            view OrdersView {
                bind vm: OrdersViewModel;
                table items: vm.Orders of Order {
                    col "Customer" bind: item.Customer;
                    button "Open" -> vm.OpenCommand(item);
                }
                button "Refresh" -> vm.RefreshCommand;
            }
            """);
        var report = p.Run("stubs", "-n", "Demo", "--format", "json");
        Assert.Single(report.Json()["missing"]!.AsObject());
        var update = p.Run("stubs", "-n", "Demo", "--update");
        Assert.Equal(0, update.ExitCode);
        var orders = p.Read("ViewModels/OrdersViewModel.cs");
        Assert.Contains("private partial Task OnOpenAsync(Order item) => Task.CompletedTask; // TODO", orders);
        Assert.Equal(1, CountOf(orders, "OnRefreshAsync("));
        Assert.EndsWith("// TODO\n}", orders.Replace("\r\n", "\n").TrimEnd());

        var again = p.Run("stubs", "-n", "Demo", "--update");
        Assert.Equal(0, again.ExitCode);
        Assert.Equal(orders, p.Read("ViewModels/OrdersViewModel.cs"));
    }

    [Fact]
    public void Append_before_final_brace_keeps_existing_members()
    {
        var text = "namespace X;\n\npublic partial class A\n{\n    private int _x;\n}\n";
        var updated = StubsCommand.AppendBeforeFinalBrace(text, ["private partial Task OnGoAsync() => Task.CompletedTask; // TODO"]);
        Assert.Equal("namespace X;\n\npublic partial class A\n{\n    private int _x;\n\n    private partial Task OnGoAsync() => Task.CompletedTask; // TODO\n}\n", updated);
    }

    private static int CountOf(string text, string value) =>
        (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
}

public class InitAndNewViewTests
{
    [Theory]
    [InlineData("side", "none")]
    [InlineData("top", "claude")]
    [InlineData("side", "cursor")]
    public void Init_creates_a_project_that_checks_clean(string nav, string agent)
    {
        using var p = new TempProject();
        var run = p.Run("init", "TaskTrack", "--nav", nav, "--agent", agent, "--format", "json");
        Assert.Equal(0, run.ExitCode);

        var root = Path.Combine(p.Dir, "TaskTrack");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        var expected = new List<string>
        {
            ".config/dotnet-tools.json", ".gitignore", "App.Services.cs", "Flags/app.flag", "Flags/home.flag",
            "README.md", "TaskTrack.csproj", "ViewModels/HomeViewModel.cs", "app.manifest", "global.json",
        };
        if (agent == "claude")
            expected.AddRange(["AGENTS.md", "CLAUDE.md", ".claude/skills/flagstaff/SKILL.md"]);
        if (agent == "cursor")
            expected.AddRange(["AGENTS.md", ".cursor/rules/flagstaff.mdc"]);
        Assert.Equal(expected.Order(StringComparer.Ordinal), files);

        var csproj = File.ReadAllText(Path.Combine(root, "TaskTrack.csproj"));
        Assert.Contains($"<PackageReference Include=\"Flagstaff\" Version=\"{Cli.Version}\" />", csproj);
        Assert.Contains($"<PackageReference Include=\"Microsoft.WindowsAppSDK\" Version=\"{Cli.WindowsAppSdkVersion}\" />", csproj);
        Assert.Contains("<WindowsPackageType>None</WindowsPackageType>", csproj);
        Assert.DoesNotContain("{{", csproj);
        Assert.Contains($"nav {nav} {{", File.ReadAllText(Path.Combine(root, "Flags", "app.flag")));
        Assert.Contains($"\"version\": \"{Cli.Version}\"", File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json")));
        Assert.Contains("cd TaskTrack", run.Json()["nextSteps"]!.ToJsonString());

        var check = CliRun.Invoke(root, "check", "--format", "json");
        Assert.Equal(0, check.ExitCode);
        Assert.Equal("TaskTrack", (string?)check.Json()["namespace"]);

        // The hand-written partial already implements every delegate: stubs has nothing to add.
        var stubs = CliRun.Invoke(root, "stubs", "--format", "json");
        Assert.Empty(stubs.Json()["missing"]!.AsObject());
        Assert.Empty(stubs.Json()["created"]!.AsArray());
    }

    [Fact]
    public void Init_refuses_non_empty_directory_and_bad_names()
    {
        using var p = new TempProject();
        p.Write("Taken/file.txt", "x");
        Assert.Equal(1, p.Run("init", "Taken").ExitCode);
        Assert.Equal(2, p.Run("init", "9bad").ExitCode);
    }

    [Fact]
    public void New_view_inserts_nav_link_and_refuses_to_overwrite()
    {
        using var p = new TempProject();
        Assert.Equal(0, p.Run("init", "Shop", "-o", ".", "-q").ExitCode);

        var run = p.Run("new", "view", "OrderHistory", "--icon", "list", "--format", "json");
        Assert.Equal(0, run.ExitCode);
        Assert.True((bool)run.Json()["navUpdated"]!);
        Assert.True(p.Exists("Flags/order-history.flag"));
        Assert.Contains("view OrderHistoryView {", p.Read("Flags/order-history.flag"));
        var vm = p.Read("ViewModels/OrderHistoryViewModel.cs");
        Assert.Contains("namespace Shop.ViewModels;", vm);
        Assert.Contains("private partial Task OnLoadAsync()", vm);

        var app = p.Read("Flags/app.flag").Replace("\r\n", "\n");
        Assert.Contains("        link \"Home\" icon: home -> HomeView;\n        link \"Order History\" icon: list -> OrderHistoryView;\n    }", app);

        Assert.Equal(0, p.Run("check").ExitCode);

        var again = p.Run("new", "view", "OrderHistory");
        Assert.Equal(1, again.ExitCode);
        Assert.Equal(app, p.Read("Flags/app.flag").Replace("\r\n", "\n"));
    }

    [Fact]
    public void Nav_link_insertion_handles_brace_on_same_line()
    {
        var text = "app A { nav top { link \"Home\" icon: home -> HomeView; } }";
        var updated = NewCommand.InsertNavLink(text, "app.flag", "SettingsView", "link \"Settings\" icon: setting -> SettingsView;", out _);
        Assert.Equal("app A { nav top { link \"Home\" icon: home -> HomeView; link \"Settings\" icon: setting -> SettingsView; } }", updated);
        Assert.Null(NewCommand.InsertNavLink(updated!, "app.flag", "SettingsView", "x", out var reason));
        Assert.Contains("already links", reason);
    }

    [Theory]
    [InlineData("OrderHistory", "order-history", "Order History")]
    [InlineData("Settings", "settings", "Settings")]
    [InlineData("HTMLReport", "html-report", "HTML Report")]
    public void Name_helpers(string pascal, string kebab, string words)
    {
        Assert.Equal(kebab, NewCommand.Kebab(pascal));
        Assert.Equal(words, NewCommand.Words(pascal));
    }
}

public class SpecAndDoctorTests
{
    [Fact]
    public void Spec_section_returns_one_section()
    {
        using var p = new TempProject();
        var run = p.Run("spec", "--section", "tables", "--format", "json");
        Assert.Equal(0, run.ExitCode);
        var json = run.Json();
        Assert.Equal("Tables", (string?)json["section"]);
        var content = (string)json["content"]!;
        Assert.StartsWith("## Tables", content);
        Assert.DoesNotContain("## Alerts", content);
        Assert.Contains("Common Mistakes", json["sections"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void Spec_text_prints_full_reference_and_unknown_section_fails()
    {
        using var p = new TempProject();
        var full = p.Run("spec");
        Assert.Equal(0, full.ExitCode);
        Assert.StartsWith("# Flagstaff Language Reference", full.Stdout);
        Assert.Equal(1, p.Run("spec", "--section", "no-such-section").ExitCode);
    }

    [Fact]
    public void Doctor_reports_checks()
    {
        using var p = new TempProject();
        var run = p.Run("doctor", "--format", "json");
        Assert.True(run.ExitCode is 0 or 3, run.Stderr);
        var json = run.Json();
        var names = json["checks"]!.AsArray().Select(c => (string?)c!["name"]).ToList();
        Assert.Contains("dotnetSdk", names);
        Assert.Contains("msbuild", names);
        Assert.Contains("xamlCompiler", names);
        Assert.Contains("flagsDir", names);
        Assert.Equal(Cli.Version, (string?)json["version"]);
    }
}

public class BuildOutputParserTests
{
    [Fact]
    public void Parses_canonical_msbuild_lines()
    {
        string[] lines =
        [
            @"C:\app\Views\HomeView.xaml(12,5): XamlCompiler error WMC1121: Invalid binding path 'ViewModel.Nope' : Property 'Nope' not found on type 'HomeViewModel'. [C:\app\App.csproj]",
            @"  C:\app\ViewModels\Home.cs(14,24): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\app\App.csproj]",
            @"C:\app\Flags\home.flag(3,14): error FS1001: Unknown property 'value' on 'stat' [hint: use positional binding: stat ""X"" vm.Y;] [C:\app\App.csproj]",
            @"3>C:\app\Generated\HomeView.xaml(4): warning FS3003: Unknown Symbol 'foo'",
            @"MSBUILD : error MSB1009: Project file does not exist.",
            @"C:\Program Files (x86)\x\Some.targets(5131,5): warning MSB3026: Could not copy. [C:\app\App.csproj]",
            @"Build succeeded.",
            @"    0 Warning(s)",
            // MSBuild repeats errors in its summary: de-duplicated.
            @"C:\app\ViewModels\Home.cs(14,24): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\app\App.csproj]",
        ];

        var d = BuildOutputParser.ParseLines(lines);

        Assert.Equal(6, d.Count);
        Assert.Equal(new CliDiagnostic("error", "WMC1121", "Invalid binding path 'ViewModel.Nope' : Property 'Nope' not found on type 'HomeViewModel'.", @"C:\app\Views\HomeView.xaml", 12, 5, null), d[0]);
        Assert.Equal(new CliDiagnostic("error", "CS0029", "Cannot implicitly convert type 'string' to 'int'", @"C:\app\ViewModels\Home.cs", 14, 24, null), d[1]);
        Assert.Equal("FS1001", d[2].Code);
        Assert.Equal("Unknown property 'value' on 'stat'", d[2].Message);
        Assert.Equal("use positional binding: stat \"X\" vm.Y;", d[2].Hint);
        Assert.Equal(new CliDiagnostic("warning", "FS3003", "Unknown Symbol 'foo'", @"C:\app\Generated\HomeView.xaml", 4, null, null), d[3]);
        Assert.Equal(new CliDiagnostic("error", "MSB1009", "Project file does not exist.", "MSBUILD", null, null, null), d[4]);
        Assert.Equal(@"C:\Program Files (x86)\x\Some.targets", d[5].File);
        Assert.Equal(5131, d[5].Line);
    }

    [Fact]
    public void Round_trips_through_ToMsBuildString()
    {
        var d = new CliDiagnostic("error", "FS1001", "Unknown property", @"C:\a.flag", 3, 14, "use x");
        Assert.Equal(@"C:\a.flag(3,14): error FS1001: Unknown property [hint: use x]", d.ToMsBuildString());
        Assert.Equal(d, BuildOutputParser.ParseLine(d.ToMsBuildString()));
    }

    [Fact]
    public void Parses_xaml_compiler_output_json()
    {
        const string json = """
            {
              "GeneratedCodeFiles": ["C:\\app\\obj\\x64\\Debug\\App.g.cs"],
              "MSBuildLogEntries": [
                {"SubCategory":null,"ErrorCode":null,"HelpKeyword":null,"File":null,"LineNumber":0,"ColumnNumber":0,"EndLineNumber":0,"EndColumnNumber":0,"Message":"Xaml Compiler Marker: 3:08:00 PM: 991 perfXC_StartPass1, App"},
                {"SubCategory":"XamlCompiler","ErrorCode":"WMC1121","HelpKeyword":null,"File":"C:\\app\\Generated\\HomeView.xaml","LineNumber":21,"ColumnNumber":13,"EndLineNumber":0,"EndColumnNumber":0,"Message":"Invalid binding path 'ViewModel.Nope'"},
                {"SubCategory":null,"ErrorCode":"WMC1506","HelpKeyword":null,"File":"C:\\app\\Generated\\HomeView.xaml","LineNumber":3,"ColumnNumber":1,"Message":"OneWay bindings require at least one property","EntryType":"Warning"}
              ]
            }
            """;

        var d = BuildOutputParser.ParseXamlCompilerOutput(json);

        Assert.Equal(2, d.Count);
        Assert.Equal(new CliDiagnostic("error", "WMC1121", "Invalid binding path 'ViewModel.Nope'", @"C:\app\Generated\HomeView.xaml", 21, 13, null), d[0]);
        Assert.Equal("warning", d[1].Severity);
        Assert.Empty(BuildOutputParser.ParseXamlCompilerOutput("""{"GeneratedCodeFiles":[]}"""));
    }

    [Fact]
    public void Finds_newest_output_json_under_obj()
    {
        using var p = new TempProject();
        var old = p.Write("obj/Debug/net10.0/output.json", "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        var fresh = p.Write("obj/x64/Debug/net10.0-windows10.0.22621.0/output.json", "{}");

        Assert.Equal(fresh, BuildOutputParser.FindXamlCompilerOutput(p.Dir, DateTime.Now.AddMinutes(-1)));
        Assert.Null(BuildOutputParser.FindXamlCompilerOutput(p.Dir, DateTime.Now.AddMinutes(5)));
    }

    [Fact]
    public void Build_without_project_is_a_usage_error()
    {
        using var p = new TempProject();
        var run = p.Run("build", "--format", "json");
        Assert.Equal(2, run.ExitCode);
        Assert.Contains("No .csproj", (string?)run.Json()["error"]);
    }
}
