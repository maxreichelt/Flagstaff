using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>ViewModel conventions, inference confidence, element schema and source locations.</summary>
public class ConventionsTests
{
    private static CompilationResult Compile(string source) =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile(source, "test.flag");

    private static string File(CompilationResult result, string name) =>
        result.Files.First(f => f.Name == name).Content;

    private static List<InferredMember> Infer(string source, List<Diagnostic>? diagnostics = null)
    {
        var view = (ViewDecl)new Parser(new Lexer(source, "test.flag").Tokenize()).Parse().Declarations[0];
        return TypeInference.InferMembers(view, diagnostics);
    }

    // ── onload ─────────────────────────────────────────────────────

    [Fact]
    public void Onload_load_generates_load_command()
    {
        var result = Compile("""
            view DashboardView {
                bind vm: DashboardViewModel;
                onload -> vm.Load;
                text "Hello";
            }
            """);

        Assert.Empty(result.Diagnostics);
        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("    [RelayCommand(FlowExceptionsToTaskScheduler = true)] private async Task LoadAsync() { await OnLoadAsync(); }\r\n    private partial Task OnLoadAsync();".Replace("\r\n", Environment.NewLine), vm);
        var cs = File(result, "DashboardView.xaml.cs");
        Assert.Contains("await ViewModel.LoadCommand.ExecuteAsync(null);", cs);
        Assert.Contains("catch (Exception ex)", cs);
        Assert.Contains("#if DEBUG", cs);
        Assert.Contains("throw;", cs);
    }

    [Fact]
    public void Legacy_onload_async_warns_fs2006_and_generates_same_command()
    {
        var result = Compile("""
            view DashboardView {
                bind vm: DashboardViewModel;
                onload -> vm.LoadAsync;
                text "Hello";
            }
            """);

        var d = Assert.Single(result.Diagnostics);
        Assert.Equal("FS2006", d.Code);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("write onload -> vm.Load; and implement OnLoadAsync()", d.Hint);
        Assert.Equal(new SourceLocation("test.flag", 3, 5), d.Location);
        Assert.False(result.HasErrors);

        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("private async Task LoadAsync() { await OnLoadAsync(); }", vm);
        Assert.DoesNotContain("LoadAsyncAsync", vm);
        Assert.Contains("await ViewModel.LoadCommand.ExecuteAsync(null);", File(result, "DashboardView.xaml.cs"));
    }

    // ── Commands ───────────────────────────────────────────────────

    [Fact]
    public void Commands_declare_partial_delegates()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                button "Save" -> vm.SaveCommand;
                button "Save again" -> vm.SaveCommand;
            }
            """);

        var vm = File(result, "VM.g.cs");
        Assert.Contains("[RelayCommand] private async Task SaveAsync() { await OnSaveAsync(); }", vm);
        Assert.Contains("private partial Task OnSaveAsync();", vm);
        Assert.Single(vm.Split('\n'), l => l.Contains("Task SaveAsync()"));
    }

    [Fact]
    public void Item_command_in_typed_table_takes_row_parameter()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Orders of Order {
                    col "Id" bind: item.Id;
                    button "Edit" -> vm.EditCommand(item);
                }
            }
            """);

        Assert.False(result.HasErrors);
        var vm = File(result, "VM.g.cs");
        Assert.Contains("[RelayCommand] private async Task EditAsync(Order item) { await OnEditAsync(item); }", vm);
        Assert.Contains("private partial Task OnEditAsync(Order item);", vm);
    }

    [Fact]
    public void Item_command_outside_table_is_fs2007()
    {
        var diagnostics = new List<Diagnostic>();
        Infer("""
            view V {
                card "c" { button "Edit" -> vm.EditCommand(item); }
                table items: vm.Rows { button "Del" -> vm.DeleteCommand(item); }
            }
            """, diagnostics);

        Assert.Equal(2, diagnostics.Count(d => d.Code == "FS2007"));
    }

    [Fact]
    public void Item_command_records_parameter_type()
    {
        var members = Infer("""
            view V {
                table items: vm.Orders of Order { button "Edit" -> vm.EditCommand(item); }
            }
            """);

        var cmd = members.Single(m => m.Name == "EditCommand");
        Assert.Equal(InferredKind.Command, cmd.Kind);
        Assert.Equal("Order", cmd.ParameterType);
    }

    // ── Inference confidence ───────────────────────────────────────

    [Fact]
    public void Comparison_beats_default_and_explicit_beats_comparison()
    {
        var members = Infer("""
            view V {
                text vm.Count;
                text "a" when(vm.Count > 3);
                text "b" when(vm.Price > 3);
                stat "P" vm.Price format: currency;
                text "c" when(vm.Maybe == null);
            }
            """);

        var count = members.Single(m => m.Name == "Count");
        Assert.Equal(("int", InferenceConfidence.Comparison), (count.CSharpType, count.Confidence));
        var price = members.Single(m => m.Name == "Price");
        Assert.Equal(("decimal", InferenceConfidence.Explicit), (price.CSharpType, price.Confidence));
        Assert.Equal("string?", members.Single(m => m.Name == "Maybe").CSharpType);
    }

    [Fact]
    public void First_wins_on_ties()
    {
        var members = Infer("""
            view V {
                checkbox "A" vm.X;
                number "B" vm.X;
            }
            """);

        Assert.Equal("bool", members.Single(m => m.Name == "X").CSharpType);
    }

    [Fact]
    public void Select_items_infers_collections_and_nullable_selection()
    {
        var members = Infer("""
            view V {
                select "Cat" vm.Category items: vm.Categories;
                select "Vendor" vm.Vendor items: vm.Vendors of Vendor;
            }
            """);

        string TypeOf(string name) => members.Single(m => m.Name == name).CSharpType;
        Assert.Equal("System.Collections.ObjectModel.ObservableCollection<string>", TypeOf("Categories"));
        Assert.Equal("string?", TypeOf("Category"));
        Assert.Equal("System.Collections.ObjectModel.ObservableCollection<Vendor>", TypeOf("Vendors"));
        Assert.Equal("Vendor?", TypeOf("Vendor"));
    }

    [Fact]
    public void Stat_with_format_is_explicit()
    {
        var members = Infer("""
            view V { stat "Rate" vm.Rate format: percent; }
            """);

        var rate = members.Single();
        Assert.Equal(("double", InferenceConfidence.Explicit), (rate.CSharpType, rate.Confidence));
    }

    // ── Schema ─────────────────────────────────────────────────────

    [Fact]
    public void Parser_keywords_come_from_schema()
    {
        Assert.Equal(ElementSchema.All.Keys.OrderBy(k => k), Parser.ElementKeywords.OrderBy(k => k));
        Assert.Contains("toast", ElementSchema.All.Keys);
        Assert.Contains("xaml", ElementSchema.All.Keys);
    }

    [Fact]
    public void Schema_shapes_match_the_language()
    {
        var stat = ElementSchema.All["stat"];
        Assert.True(stat.AllowsLabel && stat.AllowsBinding && !stat.AllowsChildren);
        Assert.DoesNotContain("value", stat.Props.Keys);
        Assert.Equal(["warning", "danger", "info", "success"], stat.Props["variant"].Enum!);

        Assert.True(ElementSchema.All["line"].Props["bold"].Bare);
        Assert.Equal(PropKind.ItemBinding, ElementSchema.All["col"].Props["bind"].Kind);
        Assert.True(ElementSchema.All["button"].AllowsArrow);
        Assert.Equal(["align", "fill", "key", "load", "name", "span", "tooltip", "uid", "width"], ElementSchema.Universal.Keys.Order(StringComparer.Ordinal));
        Assert.All(ElementSchema.All.Values, s => Assert.DoesNotContain(s.Props.Keys, k => k is "_label" or "_binding" or "format" or "when" or "of"));
    }

    // ── Source locations ───────────────────────────────────────────

    [Fact]
    public void Ast_nodes_carry_source_locations()
    {
        var file = new Parser(new Lexer("""
            app A {
                nav top { link "Home" icon: home -> HomeView; }
            }
            view V {
                text "a" when(vm.X > 1);
            }
            """, "loc.flag").Tokenize()).Parse();

        var app = (AppDecl)file.Declarations[0];
        Assert.Equal(new SourceLocation("loc.flag", 1, 1), app.Location);
        var link = ((NavBlock)app.Members[0]).Members.OfType<NavLinkDecl>().Single();
        Assert.Equal(new SourceLocation("loc.flag", 2, 15), link.Location);
        Assert.Equal(new SourceLocation("loc.flag", 2, 27), link.Props[0].Location);

        var view = (ViewDecl)file.Declarations[1];
        Assert.Equal(new SourceLocation("loc.flag", 4, 1), view.Location);
        var text = view.Body[0];
        Assert.Equal(new SourceLocation("loc.flag", 5, 5), text.Location);
        Assert.Equal(new SourceLocation("loc.flag", 5, 10), text.Props[0].Location);
        var compare = Assert.IsType<CompareCond>(text.When);
        Assert.Equal(new SourceLocation("loc.flag", 5, 19), compare.Location);
        Assert.Equal(new SourceLocation("loc.flag", 5, 26), compare.Right.Location);
    }
}
