using Flagstaff.Compiler;

namespace Flagstaff.Cli.Tests;

/// <summary>
/// <see cref="ContractHints"/>: C# errors in the hand-written half against generated members get a hint. Fed with
/// parsed diagnostics and an in-memory compilation; no MSBuild.
/// </summary>
public class ContractHintTests
{
    private const string Dashboard = """
        view DashboardView {
            bind vm: DashboardViewModel;
            onload -> vm.Load;

            cards cols: 2 {
                stat "Open" vm.OpenCount;
                stat "Overdue" vm.OverdueCount when(vm.OverdueCount > 0);
            }
            textbox "Color" vm.Color;
            table items: vm.Orders of Order -> vm.Edit(item) {
                col "Customer" bind: item.Customer;
                col "Total" bind: item.Total format: currency;
            }
            button "Save" -> vm.Save;
        }
        """;

    private static readonly Dictionary<(string File, int Line), string> Lines = new()
    {
        [(@"C:\app\ViewModels\DashboardViewModel.cs", 20)] = "        OpenCount = orders.Count(o => o.IsOpen);",
        [(@"C:\app\ViewModels\DashboardViewModel.cs", 21)] = "        if (OpenCount > 0) Status = \"busy\";",
        [(@"C:\app\ViewModels\DashboardViewModel.cs", 22)] = "        OverdueCount = 2.5;",
        [(@"C:\app\ViewModels\DashboardViewModel.cs", 30)] = "    private partial Task OnSaveAsync();",
        [(@"C:\app\ViewModels\DashboardViewModel.cs", 31)] = "    public partial async Task OnSaveAsync()",
        [(@"C:\app\ViewModels\DashboardViewModel.cs", 40)] = "    private partial void OnOpenCountChanged(string value) => Refresh();",
        [(@"C:\app\ViewModels\DashboardViewModel.cs", 41)] = "    private partial void OnSaveAsync()",
        [(@"C:\app\ViewModels\DashboardViewModel.cs", 42)] = "    partial bool OnOpenCountChanging(string value) => true;",
        [(@"C:\app\DashboardView.cs", 5)] = "    private partial void OnViewCreated()",
        [(@"C:\app\Models\Order.cs", 12)] = "    public Order WithTotal(double total) => new() { Customer = Customer, Total = total };",
    };

    private static ContractHints Hints()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "DemoApp", NamespaceIsOverridden = true };
        var result = compiler.CompileAll([new SourceFile(@"C:\app\Flags\dashboard.flag", Dashboard)]);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        return new ContractHints(result.Contracts, result.ItemTypes, "DemoApp",
            l => $"Flags/{l.FileName} line {l.Line}",
            (file, line) => Lines.GetValueOrDefault((file, line)));
    }

    private static string? Hint(string code, string message, string file = @"C:\app\ViewModels\DashboardViewModel.cs", int line = 10) =>
        Hints().Apply(new CliDiagnostic("error", code, message, file, line, 5, null)).Hint;

    [Fact]
    public void CS0102_on_a_generated_property_or_its_field_names_the_flag_usage()
    {
        var hint = Hint("CS0102", "The type 'DashboardViewModel' already contains a definition for '_openCount'");
        Assert.Equal("'OpenCount' (string) is generated from Flags/dashboard.flag line 6 (stat \"Open\" vm.OpenCount) into "
                     + "Generated/DashboardViewModel.g.cs; delete your declaration of _openCount/OpenCount, the generated partial already has it", hint);

        hint = Hint("CS0102", "The type 'DemoApp.ViewModels.DashboardViewModel' already contains a definition for 'OpenCount'");
        Assert.StartsWith("'OpenCount' (string) is generated", hint);
    }

    [Fact]
    public void CS0102_on_a_generated_command_says_to_implement_the_delegate()
    {
        var hint = Hint("CS0102", "The type 'DashboardViewModel' already contains a definition for 'SaveCommand'");
        Assert.NotNull(hint);
        Assert.Contains("button \"Save\" -> vm.Save", hint);
        Assert.Contains("'private partial Task OnSaveAsync()'", hint);
    }

    [Fact]
    public void CS0102_on_a_generated_item_property()
    {
        var hint = Hint("CS0102", "The type 'Order' already contains a definition for 'Customer'", @"C:\app\Models\Order.cs");
        Assert.Contains("'Order.Customer' (string) is generated from item.Customer", hint);
        Assert.Contains("Generated/Order.g.cs", hint);
    }

    [Fact]
    public void CS0756_from_the_toolkit_generator_is_a_follow_on_error()
    {
        var hint = Hint("CS0756", "A partial method may not have multiple defining declarations",
            @"CommunityToolkit.Mvvm.SourceGenerators\CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator\DemoApp.ViewModels.DashboardViewModel.g.cs", 25);
        Assert.NotNull(hint);
        Assert.StartsWith("follow-on error of an [ObservableProperty] declared twice: Generated/DashboardViewModel.g.cs already declares OpenCount, OverdueCount, Color, Orders", hint);
    }

    [Fact]
    public void CS0756_on_a_repeated_delegate_declaration_asks_for_a_body()
    {
        var hint = Hint("CS0756", "A partial method may not have multiple defining declarations", line: 30);
        Assert.Equal("Generated/DashboardViewModel.g.cs already declares 'private partial Task OnSaveAsync();'; write only the implementation, "
                     + "with a body: private partial async Task OnSaveAsync() { ... }", hint);
    }

    [Fact]
    public void CS0111_constructor_in_a_page_partial()
    {
        var hint = Hint("CS0111", "Type 'DashboardView' already defines a member called 'DashboardView' with the same parameter types", @"C:\app\Views.cs");
        Assert.NotNull(hint);
        Assert.StartsWith("Generated/DashboardView.xaml.cs already declares the constructors DashboardView() and DashboardView(ViewModels.DashboardViewModel viewModel); "
                          + "delete your constructor and put its body in 'partial void OnViewCreated() { ... }'", hint);
        Assert.Contains("onload -> vm.Load", hint);
    }

    [Fact]
    public void CS0111_on_a_generated_page_method_names_it()
    {
        var hint = Hint("CS0111", "Type 'DashboardView' already defines a member called 'OnViewLoaded' with the same parameter types", @"C:\app\DashboardView.cs");
        Assert.Equal("Generated/DashboardView.xaml.cs already declares 'OnViewLoaded' (the Loaded handler that runs 'onload -> vm.X'); rename yours, "
                     + "or put page setup in 'partial void OnViewCreated() { ... }' (runs once after InitializeComponent)", hint);
    }

    [Fact]
    public void CS8799_names_the_generated_accessibility()
    {
        var hint = Hint("CS8799", "Both partial member declarations must have identical accessibility modifiers.", line: 31);
        Assert.Equal("Generated/DashboardViewModel.g.cs declares 'private partial Task OnSaveAsync();' (from Flags/dashboard.flag line 14 (button \"Save\" -> vm.Save)); "
                     + "write yours with the same accessibility: private partial async Task OnSaveAsync() { ... }", hint);
        Assert.StartsWith("the generated page declares 'partial void OnViewCreated();'",
            Hint("CS8799", "Both partial method declarations must have identical accessibility modifiers.", @"C:\app\DashboardView.cs", 5));
    }

    [Fact]
    public void CS1061_from_the_xaml_compiler_is_a_missing_handler()
    {
        var hint = Hint("CS1061", "'DashboardView' does not contain a definition for 'OnAddTab' and no accessible extension method 'OnAddTab' accepting a first argument of type 'DashboardView' could be found",
            @"C:\app\obj\x64\Debug\net10.0-windows10.0.22621.0\Generated\DashboardView.g.cs", 120);
        Assert.StartsWith("'OnAddTab' is an event handler named in DashboardView's XAML (a xaml { } block in dashboard.flag) that your page partial does not declare: "
                          + "add 'private void OnAddTab(object sender, RoutedEventArgs e) { ... }'", hint);
        Assert.Null(Hint("CS1061", "'DashboardView' does not contain a definition for 'Nope'", @"C:\app\DashboardView.cs"));
    }

    [Fact]
    public void CS1061_on_a_nullable_date_suggests_null_propagation()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "DemoApp", NamespaceIsOverridden = true };
        var result = compiler.CompileAll([new SourceFile("Flags/form.flag", """
            view FormView {
                bind vm: FormViewModel;
                datepicker "Due" vm.DueDate;
            }
            """)]);
        var hints = new ContractHints(result.Contracts, result.ItemTypes, "DemoApp", l => $"Flags/{l.FileName} line {l.Line}",
            (_, _) => "        var day = DueDate.Date;");
        var d = hints.Apply(new CliDiagnostic("error", "CS1061",
            "'DateTimeOffset?' does not contain a definition for 'Date' and no accessible extension method 'Date' accepting a first argument of type 'DateTimeOffset?' could be found",
            "x.cs", 9, 32, null));
        Assert.Equal("vm.DueDate is DateTimeOffset? (from Flags/form.flag line 3 (datepicker \"Due\" vm.DueDate)); it can be null: use DueDate?.DateTime (a DateTime?), "
                     + "or DueDate.Value.Date after a null check", d.Hint);
    }

    [Fact]
    public void CS0260_on_a_class_flagstaff_does_not_generate_is_not_called_generated()
    {
        var hint = Hint("CS0260", "Missing partial modifier on declaration of type 'InvoiceStore'; another partial declaration of this type exists", @"C:\app\Services\InvoiceStore.cs");
        Assert.Equal("'InvoiceStore' is not generated by Flagstaff; another declaration of it in your code is 'partial': add 'partial' to every declaration of InvoiceStore, "
                     + "or remove it from all of them", hint);
    }

    [Fact]
    public void Hint_flag_paths_are_relative_to_the_project_not_the_working_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs-hint-paths");
        var project = Path.Combine(root, "app");
        var compiler = new FlagstaffCompiler { AppNamespace = "DemoApp", NamespaceIsOverridden = true };
        var result = compiler.CompileAll([new SourceFile("app/Flags/dashboard.flag", Dashboard)]);
        var hints = Commands.BuildCommand.CreateHints(project, result, "DemoApp", root);
        var d = hints.Apply(new CliDiagnostic("error", "CS0102", "The type 'DashboardViewModel' already contains a definition for 'OpenCount'", "x.cs", 1, 1, null));
        Assert.Contains("generated from Flags/dashboard.flag line 6", d.Hint);
    }

    [Fact]
    public void CS8799_on_a_toolkit_hook_says_to_drop_the_access_modifier()
    {
        var hint = Hint("CS8799", "Both partial member declarations must have identical accessibility modifiers.", line: 40);
        Assert.Equal("CommunityToolkit.Mvvm declares 'partial void OnOpenCountChanged(string value);' with no access modifier (it is not a Flagstaff delegate); "
                     + "drop 'private'/'public': partial void OnOpenCountChanged(string value) { ... }", hint);
    }

    [Fact]
    public void CS8817_on_a_void_delegate_asks_for_a_Task()
    {
        Assert.Equal("Generated/DashboardViewModel.g.cs declares 'private partial Task OnSaveAsync();' (from Flags/dashboard.flag line 14 (button \"Save\" -> vm.Save)): "
                     + "return a Task, not void: private partial async Task OnSaveAsync() { ... }",
            Hint("CS8817", "Both partial method declarations must have the same return type.", line: 41));
        Assert.Equal("CommunityToolkit.Mvvm declares 'partial void OnOpenCountChanging(string value);', which returns void: write partial void OnOpenCountChanging(string value) { ... }",
            Hint("CS8817", "Both partial method declarations must have the same return type.", line: 42));
    }

    [Fact]
    public void CS0759_on_a_toolkit_hook_names_the_property_type()
    {
        var hint = Hint("CS0759", "No defining declaration found for implementing declaration of partial method 'DashboardViewModel.OnOpenCountChanged(int)'");
        Assert.Equal("CommunityToolkit.Mvvm declares 'partial void OnOpenCountChanged(string value)' for the property OpenCount (string, from "
                     + "Flags/dashboard.flag line 6 (stat \"Open\" vm.OpenCount)); write exactly that signature (also 'partial void OnOpenCountChanged(string oldValue, "
                     + "string newValue)'), without an access modifier", hint);
    }

    [Fact]
    public void CS0759_lists_the_declared_delegates()
    {
        var hint = Hint("CS0759", "No defining declaration found for implementing declaration of partial method 'DashboardViewModel.OnRefreshAsync()'");
        Assert.Equal("the generator declares delegates only for commands the .flag files use; declared for DashboardViewModel: "
                     + "OnLoadAsync(), OnEditAsync(Order item), OnSaveAsync(). 'OnRefreshAsync' is not one of them: bind a command to it in "
                     + "Flags/ (button \"...\" -> vm.Refresh;) or drop 'partial' and make it a normal method. 'flagstaff stubs' lists them", hint);
    }

    [Fact]
    public void CS0759_on_OnLoadAsync_suggests_onload()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "DemoApp", NamespaceIsOverridden = true };
        var result = compiler.CompileAll([new SourceFile("Flags/form.flag", """
            view FormView {
                bind vm: FormViewModel;
                button "Save" -> vm.Save;
            }
            """)]);
        var hints = new ContractHints(result.Contracts, result.ItemTypes, "DemoApp");
        var d = hints.Apply(new CliDiagnostic("error", "CS0759", "No defining declaration found for implementing declaration of partial method 'FormViewModel.OnLoadAsync()'", "x.cs", 9, 32, null));
        Assert.Contains("declared for FormViewModel: OnSaveAsync().", d.Hint);
        Assert.Contains("add 'onload -> vm.Load;' after 'bind vm:' in Flags/form.flag", d.Hint);
    }

    [Fact]
    public void CS8795_shows_the_implementation_signature()
    {
        var hint = Hint("CS8795", "Partial method 'DashboardViewModel.OnEditAsync(Order)' must have an implementation part because it has accessibility modifiers.",
            @"C:\app\Generated\DashboardViewModel.g.cs");
        Assert.NotNull(hint);
        Assert.StartsWith("implement it in your ViewModels/DashboardViewModel.cs: private partial async Task OnEditAsync(Order item) { ... }", hint);
        Assert.EndsWith("an OnEditAsync with other parameters or another return type does not implement it: change that one to exactly this signature", hint);
    }

    [Theory]
    [InlineData("CS0029", "Cannot implicitly convert type 'int' to 'string'", 20)]
    [InlineData("CS0019", "Operator '>' cannot be applied to operands of type 'string' and 'int'", 21)]
    public void Default_string_property_used_as_a_number_suggests_format_number(string code, string message, int line)
    {
        var hint = Hint(code, message, line: line);
        Assert.NotNull(hint);
        Assert.StartsWith("'OpenCount' is inferred string (no type information) from Flags/dashboard.flag line 6 (stat \"Open\" vm.OpenCount). "
                          + "To make it a number, add format: number", hint);
    }

    [Fact]
    public void Explicitly_typed_property_mismatch_names_the_type_source()
    {
        var hint = Hint("CS0266", "Cannot implicitly convert type 'double' to 'int'. An explicit conversion exists (are you missing a cast?)", line: 22);
        Assert.Equal("'OverdueCount' is generated as int from Flags/dashboard.flag line 7 (vm.OverdueCount compared with an int literal); "
                     + "convert your value to int, or change that .flag usage (format:, keyword) to type it differently", hint);
    }

    [Fact]
    public void Item_type_rules_partial_members_namespace_and_ambiguity()
    {
        Assert.Equal("'Order' is generated as a partial class in Generated/Order.g.cs (namespace DemoApp.ViewModels); add 'partial' to your class: public partial class Order",
            Hint("CS0260", "Missing partial modifier on declaration of type 'Order'; another partial declaration of this type exists"));

        var missing = Hint("CS1061", "'Order' does not contain a definition for 'Priority' and no accessible extension method 'Priority' accepting a first argument of type 'Order' could be found (are you missing a using directive or an assembly reference?)");
        Assert.StartsWith("'Order' only has the properties the .flag files bind (Customer, Total); bind item.Priority", missing);
        Assert.NotNull(Hint("CS0117", "'Order' does not contain a definition for 'Priority'"));

        Assert.Equal("'Order' is generated in namespace DemoApp.ViewModels (Generated/Order.g.cs); add 'using DemoApp.ViewModels;'",
            Hint("CS0246", "The type or namespace name 'Order' could not be found (are you missing a using directive or an assembly reference?)"));
        Assert.NotNull(Hint("CS0104", "'Order' is an ambiguous reference between 'DemoApp.Models.Order' and 'DemoApp.ViewModels.Order'"));
    }

    [Fact]
    public void CS0103_on_a_view_model_property_in_a_page_partial()
    {
        var hint = Hint("CS0103", "The name 'Color' does not exist in the current context", @"C:\app\SketchView.cs");
        Assert.NotNull(hint);
        Assert.Contains("in a page partial write ViewModel.Color", hint);
        Assert.Contains("Windows.UI.Color", hint);
        Assert.Null(Hint("CS0103", "The name 'Color' does not exist in the current context", @"C:\app\Generated\DashboardView.xaml.cs"));
    }

    [Fact]
    public void Item_property_type_mismatch_names_the_generated_type()
    {
        var hint = Hint("CS0266", "Cannot implicitly convert type 'double' to 'decimal'. An explicit conversion exists (are you missing a cast?)",
            @"C:\app\Models\Order.cs", 12);
        Assert.Equal("'Order.Total' is generated as decimal from item.Total in Flags/dashboard.flag line 12; "
                     + "match that type, or change the .flag usage (format:) to type it differently", hint);
    }

    [Fact]
    public void WMC0011_suggests_the_manifest_property()
    {
        var hint = Hint("WMC0011", "Unknown member 'Header' on element 'MenuBarItem'", @"C:\app\Generated\NotesView.xaml", 32);
        Assert.NotNull(hint);
        Assert.StartsWith("MenuBarItem has no 'Header'; did you mean 'Title'?", hint);
        Assert.Null(Hint("WMC0011", "Unknown member 'Foo' on element 'local:Custom'", @"C:\app\Generated\NotesView.xaml", 32));
    }

    [Theory]
    [InlineData("CS0246", "The type or namespace name 'InkCanvas' could not be found (are you missing a using directive or an assembly reference?)",
        "WinUI 3 (Windows App SDK) has no InkCanvas (it is a UWP control, as FS1030 says in a .flag file): draw strokes yourself")]
    [InlineData("CS0234", "The type or namespace name 'InkCanvas' does not exist in the namespace 'Microsoft.UI.Xaml.Controls' (are you missing an assembly reference?)",
        "WinUI 3 (Windows App SDK) has no InkCanvas")]
    [InlineData("CS0246", "The type or namespace name 'MediaElement' could not be found (are you missing a using directive or an assembly reference?)",
        "WinUI 3 (Windows App SDK) has no MediaElement (it is a UWP control, as FS1030 says in a .flag file): use MediaPlayerElement")]
    [InlineData("CS0246", "The type or namespace name 'WebView' could not be found (are you missing a using directive or an assembly reference?)",
        "WinUI 3 (Windows App SDK) has no WebView (it is a UWP control, as FS1030 says in a .flag file): use WebView2")]
    [InlineData("CS0246", "The type or namespace name 'InkPresenter' could not be found (are you missing a using directive or an assembly reference?)",
        "WinUI 3 (Windows App SDK) has no inking API: InkCanvas, InkPresenter and Windows.UI.Input.Inking are UWP-only; draw strokes yourself")]
    [InlineData("CS0246", "The type or namespace name 'InkStrokesCollectedEventArgs' could not be found (are you missing a using directive or an assembly reference?)",
        "WinUI 3 (Windows App SDK) has no inking API")]
    [InlineData("CS0234", "The type or namespace name 'Inking' does not exist in the namespace 'Windows.UI.Input' (are you missing an assembly reference?)",
        "WinUI 3 (Windows App SDK) has no inking API")]
    [InlineData("CS0234", "The type or namespace name 'Ink' does not exist in the namespace 'Microsoft.UI.Input' (are you missing an assembly reference?)",
        "WinUI 3 (Windows App SDK) has no inking API")]
    [InlineData("CS0246", "The type or namespace name 'Polyline' could not be found (are you missing a using directive or an assembly reference?)",
        "Polyline is Microsoft.UI.Xaml.Shapes.Polyline: add 'using Microsoft.UI.Xaml.Shapes;' to this file (it is not a global using because its Path clashes with System.IO.Path")]
    [InlineData("CS0234", "The type or namespace name 'Media' does not exist in the namespace 'Windows.UI.Xaml' (are you missing an assembly reference?)",
        "Windows.UI.Xaml is UWP; WinUI 3 uses Microsoft.UI.Xaml.Media")]
    [InlineData("CS0234", "The type or namespace name 'Canvas' does not exist in the namespace 'Microsoft.Graphics' (are you missing an assembly reference?)",
        "Microsoft.Graphics.Canvas is Win2D")]
    [InlineData("CS0234", "The type or namespace name 'Colors' does not exist in the namespace 'Windows.UI' (are you missing an assembly reference?)",
        "use Microsoft.UI.Colors")]
    [InlineData("CS0103", "The name 'FileAccessMode' does not exist in the current context",
        "FileAccessMode is in Windows.Storage: add 'using Windows.Storage;' to this file (not a global using: its FileAttributes clashes with System.IO)")]
    [InlineData("CS0115", "'SketchPad.OnPointerPressed(PointerRoutedEventArgs)': no suitable method found to override",
        "OnPointerPressed is a virtual of Control; a Canvas, Panel, Grid or Border subclass has none: subscribe to the event instead (PointerPressed += ")]
    public void WinUI_types_get_the_replacement_or_the_using(string code, string message, string expected)
    {
        var hint = Hint(code, message, @"C:\app\Views\SketchView.cs");
        Assert.NotNull(hint);
        Assert.StartsWith(expected, hint);
    }

    [Fact]
    public void Unrelated_diagnostics_and_existing_hints_are_untouched()
    {
        Assert.Null(Hint("CS0102", "The type 'Other' already contains a definition for 'OpenCount'"));
        Assert.Null(Hint("CS0103", "The name 'Nope' does not exist in the current context"));
        Assert.Null(Hint("CS0029", "Cannot implicitly convert type 'int' to 'string'", line: 99));
        var d = new CliDiagnostic("error", "CS0102", "The type 'DashboardViewModel' already contains a definition for 'OpenCount'", "x.cs", 1, 1, "kept");
        Assert.Equal("kept", Hints().Apply(d).Hint);
        var fs = new CliDiagnostic("error", "FS1001", "Unknown property", "x.flag", 1, 1, null);
        Assert.Same(fs, Hints().Apply(fs));
    }

    [Fact]
    public void Hints_show_in_json_and_text()
    {
        var d = Hints().Apply(new CliDiagnostic("error", "CS0102", "The type 'DashboardViewModel' already contains a definition for 'OpenCount'", "x.cs", 1, 1, null));
        Assert.NotNull(d.ToJson()["hint"]);
        Assert.Contains("[hint: 'OpenCount' (string) is generated", d.ToMsBuildString());
    }
}

/// <summary>The <c>contract</c> / <c>itemTypes</c> envelope fields of <c>check</c> and <c>stubs</c>.</summary>
public class ContractOutputTests
{
    [Fact]
    public void Check_json_lists_generated_members_constructors_and_delegates()
    {
        using var p = new TempProject();
        p.Write("Flags/home.flag", Samples.Home);
        p.Write("Flags/orders.flag", Samples.Orders);

        var json = p.Run("check", "--format", "json").Json();

        var home = json["contract"]!.AsArray().Select(n => n!.AsObject()).Single(c => (string?)c["view"] == "HomeView");
        Assert.Equal("HomeViewModel", (string?)home["viewModel"]);
        Assert.Equal("Generated/HomeViewModel.g.cs", (string?)home["viewModelFile"]);
        Assert.Equal(["HomeView()", "HomeView(ViewModels.HomeViewModel viewModel)"], home["constructors"]!.AsArray().Select(n => (string?)n));
        var count = home["properties"]!.AsArray().Single()!.AsObject();
        Assert.Equal("Count", (string?)count["name"]);
        Assert.Equal("_count", (string?)count["field"]);
        Assert.Equal("string", (string?)count["type"]);
        Assert.Equal("default", (string?)count["inferred"]);
        Assert.Equal("stat \"Count\" vm.Count", (string?)count["from"]);
        Assert.Equal(["private partial Task OnLoadAsync()", "private partial Task OnSaveAsync()"],
            home["commands"]!.AsArray().Select(n => (string?)n!["implement"]));

        var order = json["itemTypes"]!.AsArray().Single()!.AsObject();
        Assert.Equal("Order", (string?)order["name"]);
        Assert.Equal("Generated/Order.g.cs", (string?)order["file"]);
        Assert.Equal("Customer", (string?)order["properties"]![0]!["name"]);
    }

    [Fact]
    public void Stubs_reports_the_contract_in_json_and_text()
    {
        using var p = new TempProject();
        p.Write("Flags/home.flag", Samples.Home);

        var json = p.Run("stubs", "-n", "Demo", "--format", "json").Json();
        Assert.Single(json["contract"]!.AsArray());

        var text = p.Run("stubs", "-n", "Demo");
        Assert.Contains("HomeViewModel: generated (do not redeclare): string Count, LoadCommand, SaveCommand; implement: OnLoadAsync(), OnSaveAsync()", text.Stdout);
    }
}
