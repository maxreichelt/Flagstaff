using Flagstaff.Compiler;

namespace Flagstaff.Cli.Tests;

/// <summary>
/// The CLI scans the hand-written C# partials (<see cref="UserDeclarationScanner"/>) before every compile: generated
/// members they already declare are skipped with FS2016, in <c>check</c>, <c>compile</c> and <c>stubs</c> alike.
/// </summary>
public class UserDeclarationScanTests
{
    private const string Redeclaring = """
        using CommunityToolkit.Mvvm.ComponentModel;
        using CommunityToolkit.Mvvm.Input;

        namespace TestApp.ViewModels;

        public partial class HomeViewModel
        {
            [ObservableProperty]
            private string _count = "";

            [RelayCommand]
            private async Task SaveAsync() => await Task.Delay(1);

            private partial async Task OnLoadAsync() => await Task.CompletedTask;
        }
        """;

    private static TempProject Project(string viewModel)
    {
        var p = new TempProject();
        p.Write("Flags/app.flag", Samples.App);
        p.Write("Flags/home.flag", Samples.Home);
        p.Write("ViewModels/HomeViewModel.cs", viewModel);
        return p;
    }

    [Fact]
    public void Check_reports_FS2016_warnings_and_stays_ok()
    {
        using var p = Project(Redeclaring);

        var run = p.Run("check", "--format", "json");

        Assert.Equal(0, run.ExitCode);
        var json = run.Json();
        Assert.True((bool)json["ok"]!);
        var warnings = json["diagnostics"]!.AsArray().Select(n => n!.AsObject()).Where(d => (string?)d["code"] == "FS2016").ToList();
        Assert.Equal(2, warnings.Count);
        var count = warnings.Single(w => ((string?)w["message"])!.StartsWith("'Count'", StringComparison.Ordinal));
        Assert.Equal("warning", (string?)count["severity"]);
        Assert.Equal("'Count' is also declared in ViewModels/HomeViewModel.cs; the generator skipped it", (string?)count["message"]);
        Assert.Equal("delete it from your partial; the .flag already generates it from stat \"Count\" vm.Count (home.flag line 11)", (string?)count["hint"]);
        Assert.EndsWith("HomeViewModel.cs", (string?)count["file"]);
        Assert.Equal(9, (int)count["line"]!);
        Assert.Equal(20, (int)count["column"]!);

        var home = json["contract"]!.AsArray().Select(n => n!.AsObject()).Single(c => (string?)c["view"] == "HomeView");
        Assert.Equal("ViewModels/HomeViewModel.cs", (string?)home["properties"]![0]!["declaredBy"]);
        var save = home["commands"]!.AsArray().Single(c => (string?)c!["name"] == "SaveCommand")!;
        Assert.Null((string?)save["implement"]);
        Assert.Equal("ViewModels/HomeViewModel.cs", (string?)save["declaredBy"]);
    }

    [Fact]
    public void Compile_writes_the_view_model_without_the_duplicates()
    {
        using var p = Project(Redeclaring);

        var run = p.Run("compile", "--format", "json");

        Assert.Equal(0, run.ExitCode);
        var vm = p.Read("Generated/HomeViewModel.g.cs");
        Assert.DoesNotContain("private string _count", vm);
        Assert.DoesNotContain("SaveAsync", vm.Split("#nullable enable")[1]);
        Assert.Contains("private partial Task OnLoadAsync();", vm);
        Assert.Contains("(declared by you in ViewModels/HomeViewModel.cs; not generated)", vm);
        Assert.Equal(2, (int)run.Json()["warnings"]!);
    }

    [Fact]
    public void Text_output_prints_the_warning_in_msbuild_format()
    {
        using var p = Project(Redeclaring);

        var run = p.Run("compile");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("HomeViewModel.cs(9,20): warning FS2016: 'Count' is also declared in ViewModels/HomeViewModel.cs; the generator skipped it [hint: ", run.Stdout);
    }

    [Fact]
    public void Stubs_do_not_add_a_delegate_for_a_hand_written_command()
    {
        using var p = Project(Redeclaring);

        var run = p.Run("stubs", "--update", "--format", "json");

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("OnSaveAsync", p.Read("ViewModels/HomeViewModel.cs"));
    }

    [Fact]
    public void Page_constructor_in_a_page_partial_replaces_the_generated_one()
    {
        using var p = Project("namespace TestApp.ViewModels;\npublic partial class HomeViewModel { }\n");
        p.Write("Views/HomeView.cs", """
            namespace TestApp
            {
                public sealed partial class HomeView
                {
                    private readonly int _clicks;

                    public HomeView()
                    {
                        this.InitializeComponent();
                        _clicks = 1;
                    }
                }
            }
            """);

        var run = p.Run("compile", "--format", "json");

        Assert.Equal(0, run.ExitCode);
        var page = p.Read("Generated/HomeView.xaml.cs");
        Assert.DoesNotContain("public HomeView() :", page);
        Assert.Contains("public HomeView(ViewModels.HomeViewModel viewModel)", page);
        Assert.DoesNotContain("OnNavigatedTo(", page);
        var warning = run.Json()["diagnostics"]!.AsArray().Single(d => (string?)d!["code"] == "FS2016")!;
        Assert.Equal("'HomeView()' is also declared in Views/HomeView.cs; the generator skipped its own constructor", (string?)warning["message"]);
    }

    private const string Typed = """
        view HomeView {
            bind vm: HomeViewModel;
            text "Many" when(vm.Count > 5);
            checkbox "Billable" vm.Billable;
        }
        """;

    [Fact]
    public void Declared_type_is_authoritative_for_generated_conditions_and_the_contract()
    {
        using var p = new TempProject();
        p.Write("Flags/app.flag", Samples.App);
        p.Write("Flags/home.flag", Typed);
        p.Write("ViewModels/HomeViewModel.cs", """
            namespace TestApp.ViewModels;

            public partial class HomeViewModel
            {
                [ObservableProperty] private long _count;
                [ObservableProperty] private bool? _billable;
            }
            """);

        var run = p.Run("compile", "--format", "json");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("When_1(long count) => (count > 5)", p.Read("Generated/HomeView.xaml.cs"));
        var json = p.Run("check", "--format", "json").Json();
        var count = json["contract"]!.AsArray().Single(c => (string?)c!["view"] == "HomeView")!["properties"]!.AsArray()
            .Single(x => (string?)x!["name"] == "Count")!;
        Assert.Equal(("long", "int"), ((string?)count["type"], (string?)count["inferredType"]));
        Assert.Contains(json["diagnostics"]!.AsArray(), d => (string?)d!["code"] == "FS2016"
                                                            && (string?)d["message"] == "'Count' is also declared in ViewModels/HomeViewModel.cs as long, but the .flag infers int; the generator skipped it and uses your long");
    }

    [Fact]
    public void Declared_type_that_cannot_work_with_the_usage_fails_check_with_FS2018()
    {
        using var p = new TempProject();
        p.Write("Flags/app.flag", Samples.App);
        p.Write("Flags/home.flag", Typed);
        p.Write("ViewModels/HomeViewModel.cs", """
            namespace TestApp.ViewModels;

            public partial class HomeViewModel
            {
                [ObservableProperty] private string _billable = "";
            }
            """);

        var run = p.Run("check", "--format", "json");

        Assert.NotEqual(0, run.ExitCode);
        var error = run.Json()["diagnostics"]!.AsArray().Single(d => (string?)d!["code"] == "FS2018")!;
        Assert.Equal("error", (string?)error["severity"]);
        Assert.Equal("'Billable' is declared in ViewModels/HomeViewModel.cs as string, but checkbox \"Billable\" vm.Billable (home.flag line 4) needs a bool or bool?; "
                     + "the .flag alone infers bool", (string?)error["message"]);
        Assert.Equal("declare it as bool, or delete yours (the .flag then generates bool)", (string?)error["hint"]);
        Assert.Equal(5, (int)error["line"]!);
    }

    [Fact]
    public void Classes_in_other_namespaces_bin_obj_and_generated_are_ignored()
    {
        using var p = Project("namespace Other.ViewModels;\npublic partial class HomeViewModel { [ObservableProperty] private string _count; }\n");
        p.Write("obj/Debug/HomeViewModel.cs", Redeclaring);
        p.Write("bin/HomeViewModel.cs", Redeclaring);
        p.Write("Generated/Extra.cs", Redeclaring);

        var json = p.Run("check", "--format", "json").Json();

        Assert.DoesNotContain(json["diagnostics"]!.AsArray(), d => (string?)d!["code"] == "FS2016");
    }

    [Fact]
    public void Scanner_reads_members_attributes_and_constructors()
    {
        var user = new UserDeclarations();
        UserDeclarationScanner.ScanText("""
            namespace A.ViewModels
            {
                public partial class Vm
                {
                    [CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute] private int m_total;
                    public partial string Name { get; set; }
                    [RelayCommand] void Go() { }
                    public partial Task OnSaveAsync(Order item);
                    public Vm(int x) => _ = x;
                    class Nested { public int Total { get; set; } }
                }
                public class NotPartial { public int Count { get; set; } }
            }
            """, "x.cs", "ViewModels/x.cs", user);

        var members = user.Members("A.ViewModels.Vm");
        Assert.Equal(["m_total", "Name", "Go", "OnSaveAsync", "Vm"], members.Select(m => m.Name));
        Assert.Equal("Total", members[0].ProvidedProperty);
        Assert.Equal("GoCommand", members[2].ProvidedCommand);
        Assert.True(members[3].IsPartial);
        Assert.False(members[3].HasBody);
        Assert.Equal("public", members[3].Accessibility);
        Assert.Equal("Order", members[3].ParameterList.Single().Type);
        Assert.Equal(UserMemberKind.Constructor, members[4].Kind);
        Assert.False(members[4].CallsInitializeComponent);
        Assert.Empty(user.Members("A.ViewModels.NotPartial"));
        Assert.Equal(new SourceLocation("x.cs", 5, 88), members[0].Location);
    }
}
