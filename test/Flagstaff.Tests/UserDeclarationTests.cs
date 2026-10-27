using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Hand-written declarations (<see cref="UserDeclarations"/>, filled by the CLI's C# scan): generated members the
/// hand-written partial already declares are skipped with FS2016; implemented partial delegates nothing declares get a
/// declaration (FS2017); page constructors of the user replace the generated ones; <c>GlobalUsings.g.cs</c>.
/// </summary>
public class UserDeclarationTests
{
    private const string Ns = "App";
    private const string VmFile = "ViewModels/DashboardViewModel.cs";
    private const string PageFile = "Views/DashboardView.cs";

    private const string Dashboard = """
        view DashboardView {
            bind vm: DashboardViewModel;
            onload -> vm.Load;
            stat "Open" vm.OpenCount;
            table items: vm.Orders of Order -> vm.Edit(item) {
                col "Customer" bind: item.Customer;
                col "Total" bind: item.Total format: currency;
            }
            button "Save" -> vm.Save;
            confirm "Delete?" when(vm.IsDeleteOpen) -> vm.ConfirmDelete;
        }
        """;

    private static UserMember Member(UserMemberKind kind, string name, int line, string? type = null, string accessibility = "private",
        bool partial = false, bool body = false, string[]? attributes = null, UserParameter[]? parameters = null, bool initializes = false,
        string file = VmFile) =>
        new(kind, name, new SourceLocation(file, line, 5), file, type, accessibility, partial, body, attributes, parameters, initializes);

    private static UserDeclarations Vm(params UserMember[] members)
    {
        var user = new UserDeclarations();
        foreach (var m in members)
            user.Add($"{Ns}.ViewModels.DashboardViewModel", m);
        return user;
    }

    private static CompilationResult Compile(UserDeclarations user, string source = Dashboard)
    {
        var compiler = new FlagstaffCompiler { AppNamespace = Ns, NamespaceIsOverridden = true, UserDeclarations = user };
        return compiler.CompileAll([new SourceFile("Flags/app.flag", "app App { nav side { link \"Home\" -> DashboardView; } }"),
                                    new SourceFile("Flags/dashboard.flag", source)]);
    }

    private static string File(CompilationResult result, string name) => result.Files.Single(f => f.Name == name).Content;

    private static Diagnostic Single(CompilationResult result, string code) =>
        Assert.Single(result.Diagnostics, d => d.Code == code);

    [Fact]
    public void Without_user_declarations_nothing_is_skipped()
    {
        var result = Compile(UserDeclarations.Empty);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "FS2016" or "FS2017");
        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("[ObservableProperty] private string _openCount = \"\";", vm);
        Assert.Contains("private partial Task OnSaveAsync();", vm);
    }

    [Fact]
    public void Redeclared_observable_property_is_skipped_with_a_warning_at_the_declaration()
    {
        var result = Compile(Vm(Member(UserMemberKind.Field, "_openCount", 12, "string", attributes: ["ObservableProperty"])));

        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.DoesNotContain("private string _openCount", vm);
        Assert.Contains("[ObservableProperty] private bool _isDeleteOpen;", vm);
        Assert.Contains("string OpenCount  <- stat \"Open\" vm.OpenCount  (declared by you in ViewModels/DashboardViewModel.cs; not generated)", vm);

        var d = Single(result, "FS2016");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(new SourceLocation(VmFile, 12, 5), d.Location);
        Assert.Equal("'OpenCount' is also declared in ViewModels/DashboardViewModel.cs; the generator skipped it", d.Message);
        Assert.Equal("delete it from your partial; the .flag already generates it from stat \"Open\" vm.OpenCount (dashboard.flag line 4)", d.Hint);
        Assert.False(result.HasErrors);
        Assert.Contains(result.Contracts.Single().Properties, p => p.Name == "OpenCount" && p.DeclaredBy is not null);
    }

    [Theory]
    [InlineData("openCount")]
    [InlineData("m_openCount")]
    public void Toolkit_field_names_map_to_the_property(string field)
    {
        var result = Compile(Vm(Member(UserMemberKind.Field, field, 3, "string", attributes: ["ObservableProperty"])));
        Assert.DoesNotContain("private string _openCount", File(result, "DashboardViewModel.g.cs"));
    }

    [Fact]
    public void Type_mismatch_still_skips_and_names_both_types()
    {
        var result = Compile(Vm(Member(UserMemberKind.Property, "OpenCount", 7, "int", accessibility: "public")));

        var d = Single(result, "FS2016");
        Assert.Equal("'OpenCount' is also declared in ViewModels/DashboardViewModel.cs as int, but the .flag infers string; the generator skipped it and uses your int", d.Message);
        Assert.Equal("fine if int is what you want (generated conditions and bindings use it); delete yours to get the inferred string "
                     + "(from stat \"Open\" vm.OpenCount (dashboard.flag line 4))", d.Hint);
        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.DoesNotContain("private string _openCount", vm);
        Assert.Contains("//     int OpenCount  <- stat \"Open\" vm.OpenCount  (declared by you in ViewModels/DashboardViewModel.cs; not generated)", vm);
        var property = result.Contracts.Single().Properties.Single(p => p.Name == "OpenCount");
        Assert.Equal(("int", "string"), (property.CSharpType, property.InferredType));
    }

    // ── The hand-written type is authoritative (DeclaredTypes, FS2018) ──

    private static CompilationResult CompileView(string body, params UserMember[] members) =>
        Compile(Vm(members), $$"""
            view DashboardView {
                bind vm: DashboardViewModel;
            {{body}}
            }
            """);

    private static UserMember Field(string name, string type, int line = 10) =>
        Member(UserMemberKind.Field, name, line, type, attributes: ["ObservableProperty"]);

    [Fact]
    public void Declared_type_types_the_condition_parameters()
    {
        var result = CompileView("""
                text "Many" when(vm.Count > 5 && vm.Total >= 2.5);
            """, Field("_count", "long"), Field("_total", "decimal", 11));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var page = File(result, "DashboardView.xaml.cs");
        Assert.Contains("public static Microsoft.UI.Xaml.Visibility When_1(long count, decimal total) => (count > 5 && total >= 2.5m) ? Visibility.Visible : Visibility.Collapsed;", page);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "FS2016" && d.Message.Contains("uses your", StringComparison.Ordinal)));
    }

    [Fact]
    public void Declared_nullable_bool_is_compared_with_true()
    {
        var result = CompileView("""
                text "A" when(vm.IsOn && !vm.IsOff);
                button "Go" -> vm.Go enabled(vm.IsOn);
                text "B" when(vm.IsOn);
            """, Field("_isOn", "bool?"), Field("_isOff", "bool?", 11));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var page = File(result, "DashboardView.xaml.cs");
        Assert.Contains("When_1(bool? isOn, bool? isOff) => (isOn == true && !(isOff == true))", page);
        Assert.Contains("public static bool Enabled_1(bool? isOn) => isOn == true;", page);
        var xaml = File(result, "DashboardView.xaml");
        Assert.Contains("IsEnabled=\"{x:Bind local:DashboardView.Enabled_1(ViewModel.IsOn), Mode=OneWay}\"", xaml);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.IsOn, Mode=OneWay, Converter={StaticResource BoolToVisibility}}\"", xaml);
    }

    [Fact]
    public void Declared_enum_compares_with_a_string_literal_by_name()
    {
        var result = CompileView("""
                text "Open" when(vm.Status == "Open");
            """, Field("_status", "OrderStatus"));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Contains("When_1(OrderStatus status) => ($\"{status}\" == \"Open\")", File(result, "DashboardView.xaml.cs"));
    }

    [Fact]
    public void Declared_int_badge_is_an_info_badge()
    {
        var result = CompileView("""
                badge vm.Unread;
            """, Field("_unread", "int"));
        Assert.Contains("<InfoBadge Value=\"{x:Bind ViewModel.Unread, Mode=OneWay}\"", File(result, "DashboardView.xaml"));
    }

    [Theory]
    [InlineData("number \"Qty\" vm.Value;", "int")]
    [InlineData("number \"Qty\" vm.Value;", "float")]
    [InlineData("Slider Value: vm.Value;", "long")]
    [InlineData("checkbox \"On\" vm.Value;", "bool?")]
    [InlineData("datepicker \"Due\" vm.Value;", "DateTimeOffset")]
    [InlineData("stat \"Total\" vm.Value format: currency;", "double")]
    [InlineData("stat \"Share\" vm.Value format: percent;", "int?")]
    [InlineData("stat \"Due\" vm.Value format: date;", "DateTime")]
    [InlineData("table items: vm.Value of Order { col \"Id\" bind: item.Id; }", "List<Order>")]
    [InlineData("select \"Kind\" vm.Value options: 1..5;", "double")]
    [InlineData("text \"x\" when(vm.Value == null);", "Order")]
    [InlineData("text \"x\" when(vm.Value > 1);", "byte")]
    [InlineData("textbox \"Name\" vm.Value;", "int")]
    public void Declared_type_that_works_with_the_usage_is_used(string usage, string declared)
    {
        var result = CompileView("    " + usage, Field("_value", declared));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var d = Single(result, "FS2016");
        Assert.EndsWith($"uses your {declared}", d.Message);
        Assert.Equal(declared, result.Contracts.Single().Properties.Single(p => p.Name == "Value").CSharpType);
    }

    [Theory]
    [InlineData("checkbox \"Billable\" vm.Value;", "string", "a bool or bool?", "bool")]
    [InlineData("toggle \"On\" vm.Value;", "bool?", "a bool (not bool?)", "bool")]
    [InlineData("number \"Qty\" vm.Value;", "decimal", "a number that converts to double (sbyte, byte, short, ushort, int, uint, long, ulong, float, double)", "double")]
    [InlineData("datepicker \"Due\" vm.Value;", "DateTime?", "a DateTimeOffset? (DateTime does not convert)", "DateTimeOffset?")]
    [InlineData("stat \"Total\" vm.Value format: currency;", "bool", "a number (format: currency)", "decimal")]
    [InlineData("stat \"Due\" vm.Value format: date;", "string", "a DateTimeOffset or DateTime (format: date)", "DateTimeOffset?")]
    [InlineData("table items: vm.Value of Order { col \"Id\" bind: item.Id; }", "ObservableCollection<OrderRow>", "a collection of Order (of Order)", "ObservableCollection<Order>")]
    [InlineData("table items: vm.Value of Order { col \"Id\" bind: item.Id; }", "Order", "a collection of Order", "ObservableCollection<Order>")]
    [InlineData("text \"x\" when(vm.Value);", "int", "a bool (it is a condition)", "bool")]
    [InlineData("text \"x\" when(vm.Value > 1);", "string", "a number (it is compared with a number)", "int")]
    [InlineData("text \"x\" when(vm.Value == \"a\");", "int", "a string or an enum (it is compared with a string)", "string")]
    [InlineData("text \"x\" when(vm.Value != null);", "int", "a type that can be null (it is compared with null; int never is)", "int?")]
    public void Declared_type_that_cannot_work_with_the_usage_is_FS2018(string usage, string declared, string need, string suggest)
    {
        var result = CompileView("    " + usage, Field("_value", declared));

        var d = Single(result, "FS2018");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(new SourceLocation(VmFile, 10, 5), d.Location);
        Assert.Contains($"'Value' is declared in ViewModels/DashboardViewModel.cs as {declared}, but ", d.Message);
        Assert.Contains($" needs {need}; the .flag alone infers ", d.Message);
        Assert.StartsWith($"declare it as {suggest}, or delete yours (the .flag then generates ", d.Hint);
        Assert.DoesNotContain(result.Diagnostics, x => x.Code == "FS2016");
    }

    [Fact]
    public void FS2018_names_the_usage_and_the_alternative_fix()
    {
        var result = CompileView("""
                stat "Billable" vm.Billable format: currency;
            """, Field("_billable", "bool"));

        var d = Single(result, "FS2018");
        Assert.Equal("'Billable' is declared in ViewModels/DashboardViewModel.cs as bool, but stat \"Billable\" vm.Billable format: currency (dashboard.flag line 3) "
                     + "needs a number (format: currency); the .flag alone infers decimal", d.Message);
        Assert.Equal("declare it as decimal, or delete yours (the .flag then generates decimal), or remove format: currency from that line", d.Hint);
    }

    [Fact]
    public void Declared_item_property_types_row_conditions_and_the_contract()
    {
        var user = new UserDeclarations()
            .Add($"{Ns}.ViewModels.Order", Member(UserMemberKind.Property, "State", 5, "OrderState", accessibility: "public", file: "Models/Order.cs"))
            .Add($"{Ns}.ViewModels.Order", Member(UserMemberKind.Property, "Quantity", 6, "int", accessibility: "public", file: "Models/Order.cs"));
        var result = Compile(user, """
            view DashboardView {
                bind vm: DashboardViewModel;
                table items: vm.Orders of Order {
                    col "Qty" bind: item.Quantity format: number;
                    button "Ship" -> vm.Ship(item) enabled(item.State != "Shipped" && item.Quantity > 0);
                }
            }
            """);

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Contains("public static bool Enabled_1(OrderState state, int quantity) => $\"{state}\" != \"Shipped\" && quantity > 0;",
            File(result, "DashboardView.xaml.cs"));
        var order = result.ItemTypes.Single();
        Assert.Equal(("int", "double"), (order.Properties.Single(p => p.Name == "Quantity").CSharpType, order.Properties.Single(p => p.Name == "Quantity").InferredType));
        Assert.Contains(result.Diagnostics, d => d.Code == "FS2016" && d.Message == "'Order.Quantity' is also declared in Models/Order.cs as int, but the .flag infers double; the generator skipped it and uses your int");
    }

    [Fact]
    public void Declared_item_property_that_cannot_work_is_FS2018()
    {
        var user = new UserDeclarations()
            .Add($"{Ns}.ViewModels.Entry", Member(UserMemberKind.Property, "Billable", 7, "string", accessibility: "public", file: "Models/Entry.cs"));
        var result = Compile(user, """
            view DashboardView {
                bind vm: DashboardViewModel;
                list items: vm.Entries of Entry {
                    checkbox "Billable" item.Billable;
                }
            }
            """);

        var d = Single(result, "FS2018");
        Assert.Equal("'Entry.Billable' is declared in Models/Entry.cs as string, but item.Billable (dashboard.flag line 4) needs a bool or bool?; the .flag alone infers bool", d.Message);
        Assert.Equal(new SourceLocation("Models/Entry.cs", 7, 5), d.Location);
    }

    [Fact]
    public void Collection_types_compare_without_namespaces()
    {
        var result = Compile(Vm(Member(UserMemberKind.Field, "_orders", 7, "ObservableCollection<Order>", attributes: ["ObservableProperty"])));
        Assert.Equal("'Orders' is also declared in ViewModels/DashboardViewModel.cs; the generator skipped it", Single(result, "FS2016").Message);
    }

    [Fact]
    public void Plain_field_with_the_generated_field_name_renames_the_generated_field()
    {
        var result = Compile(Vm(Member(UserMemberKind.Field, "_openCount", 4, "string")));
        Assert.Contains("[ObservableProperty] private string openCount = \"\";", File(result, "DashboardViewModel.g.cs"));
        Assert.Contains("the generator named its field openCount", Single(result, "FS2016").Message);
    }

    [Fact]
    public void Relay_command_declared_by_hand_skips_the_command_and_its_delegate()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "SaveAsync", 20, "Task", attributes: ["RelayCommand"], body: true)));

        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.DoesNotContain("SaveAsync()", vm);
        Assert.DoesNotContain("OnSaveAsync", vm);
        Assert.Contains("SaveCommand (declared by you in ViewModels/DashboardViewModel.cs; not generated)", vm);
        Assert.Contains("private partial Task OnEditAsync(Order item);", vm);
        var d = Single(result, "FS2016");
        Assert.Equal("'SaveCommand' is also declared in ViewModels/DashboardViewModel.cs ([RelayCommand] SaveAsync()); the generator skipped it and OnSaveAsync", d.Message);
        Assert.Equal("delete it from your partial and implement private partial async Task OnSaveAsync() { ... }; the .flag already generates SaveCommand from button \"Save\" -> vm.Save (dashboard.flag line 9)", d.Hint);
        Assert.DoesNotContain(result.Contracts.Single().Delegates, c => c.Name == "SaveCommand");
    }

    [Theory]
    [InlineData("Save")]
    [InlineData("OnSave")]
    public void Relay_command_names_follow_the_toolkit(string method)
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, method, 20, "void", attributes: ["RelayCommand"], body: true)));
        Assert.DoesNotContain("OnSaveAsync", File(result, "DashboardViewModel.g.cs"));
    }

    [Fact]
    public void Command_property_declared_by_hand_is_skipped()
    {
        var result = Compile(Vm(Member(UserMemberKind.Property, "SaveCommand", 20, "IAsyncRelayCommand", accessibility: "public")));
        Assert.DoesNotContain("OnSaveAsync", File(result, "DashboardViewModel.g.cs"));
        Single(result, "FS2016");
    }

    [Fact]
    public void Public_partial_delegate_gets_a_public_declaration()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "OnSaveAsync", 30, "Task", accessibility: "public", partial: true, body: true)));

        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("public partial Task OnSaveAsync();", vm);
        Assert.DoesNotContain("private partial Task OnSaveAsync();", vm);
        Assert.Equal("'OnSaveAsync' is 'public partial' in ViewModels/DashboardViewModel.cs; the generator declared it public to match", Single(result, "FS2016").Message);
    }

    [Fact]
    public void Private_partial_delegate_is_the_convention_and_silent()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "OnSaveAsync", 30, "Task", partial: true, body: true)));
        Assert.Contains("private partial Task OnSaveAsync();", File(result, "DashboardViewModel.g.cs"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "FS2016" or "FS2017");
    }

    [Fact]
    public void Non_partial_delegate_is_called_directly()
    {
        var result = Compile(Vm(
            Member(UserMemberKind.Method, "OnSaveAsync", 30, "Task", body: true),
            Member(UserMemberKind.Method, "OnEditAsync", 31, "void", body: true, parameters: [new("Order", "order")])));

        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("[RelayCommand] private async Task SaveAsync() { await OnSaveAsync(); }", vm);
        Assert.DoesNotContain("partial Task OnSaveAsync", vm);
        Assert.Contains("[RelayCommand] private Task EditAsync(Order item) { OnEditAsync(item); return Task.CompletedTask; }", vm);
        Assert.DoesNotContain("partial Task OnEditAsync", vm);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "FS2016" && d.Message.Contains("is not partial", StringComparison.Ordinal)));
    }

    [Fact]
    public void Repeated_defining_declaration_skips_the_generated_one()
    {
        var result = Compile(Vm(
            Member(UserMemberKind.Method, "OnSaveAsync", 30, "Task", partial: true),
            Member(UserMemberKind.Method, "OnSaveAsync", 32, "Task", partial: true, body: true)));

        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.DoesNotContain("private partial Task OnSaveAsync();", vm.Split("#nullable enable")[1]);
        Assert.Contains("await OnSaveAsync();", vm);
        Assert.Equal(new SourceLocation(VmFile, 30, 5), Single(result, "FS2016").Location);
    }

    [Fact]
    public void Plain_method_with_the_command_method_name_is_called_by_the_command()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "SaveAsync", 40, "Task", body: true)));

        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("[RelayCommand] private async Task Save() { await SaveAsync(); }", vm);
        Assert.DoesNotContain("OnSaveAsync", vm.Split("#nullable enable")[1]);
        Assert.Contains("the generator named its method Save and SaveCommand calls yours", Single(result, "FS2016").Message);
    }

    [Fact]
    public void Plain_method_with_the_command_method_name_next_to_a_delegate_only_renames()
    {
        var result = Compile(Vm(
            Member(UserMemberKind.Method, "SaveAsync", 40, "Task", body: true),
            Member(UserMemberKind.Method, "OnSaveAsync", 41, "Task", partial: true, body: true)));

        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("[RelayCommand] private async Task Save() { await OnSaveAsync(); }", vm);
        Assert.Contains("private partial Task OnSaveAsync();", vm);
    }

    [Fact]
    public void Implemented_partial_without_a_command_is_declared_with_FS2017()
    {
        var result = Compile(Vm(
            Member(UserMemberKind.Method, "OnRefreshAsync", 50, "Task", partial: true, body: true),
            Member(UserMemberKind.Method, "OnSearchAsync", 55, "Task", partial: true, body: true, parameters: [new("string", "query")])));

        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("    private partial Task OnRefreshAsync();", vm);
        Assert.Contains("    private partial Task OnSearchAsync(string query);", vm);
        var warnings = result.Diagnostics.Where(d => d.Code == "FS2017").ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Equal(DiagnosticSeverity.Warning, warnings[0].Severity);
        Assert.Equal("'OnRefreshAsync()' is implemented as a partial method in ViewModels/DashboardViewModel.cs, but no .flag command declares it; the generator declared it so the build succeeds", warnings[0].Message);
        Assert.Equal("nothing calls it: bind it in the .flag (button \"...\" -> vm.Refresh;) or drop 'partial'", warnings[0].Hint);
    }

    [Fact]
    public void Implemented_partial_with_a_parameter_the_command_cannot_pass_is_FS2019()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "OnSaveAsync", 50, "Task", partial: true, body: true, parameters: [new("Order", "order")])));

        var d = Single(result, "FS2019");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(new SourceLocation(VmFile, 50, 5), d.Location);
        Assert.Equal("'OnSaveAsync(Order)' in ViewModels/DashboardViewModel.cs does not match its command: button \"Save\" -> vm.Save (dashboard.flag line 9) "
                     + "declares 'private partial Task OnSaveAsync()' (no row is passed)", d.Message);
        Assert.Equal("to receive the row, call it from inside a table or list of Order with -> vm.Save(item) (a row action button, a ContextFlyout: item, "
                     + "or the table's own ->) and implement OnSaveAsync(Order item); otherwise implement private partial async Task OnSaveAsync() { ... }", d.Hint);
        Assert.DoesNotContain(result.Diagnostics, x => x.Code == "FS2017");
        Assert.DoesNotContain("OnSaveAsync(Order order);", File(result, "DashboardViewModel.g.cs"));
    }

    [Fact]
    public void Implemented_partial_with_two_parameters_is_FS2019_naming_the_row_form()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "OnEditAsync", 50, "Task", partial: true, body: true,
            parameters: [new("Order", "order"), new("bool", "force")])));

        var d = Single(result, "FS2019");
        Assert.Equal("'OnEditAsync(Order, bool)' in ViewModels/DashboardViewModel.cs does not match its command: table -> vm.Edit(item) (dashboard.flag line 5) "
                     + "declares 'private partial Task OnEditAsync(Order item)'", d.Message);
        Assert.Equal("implement private partial async Task OnEditAsync(Order item) { ... }; the command passes the Order row", d.Hint);
    }

    [Fact]
    public void Delegate_without_the_row_parameter_is_declared_without_it()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "OnEditAsync", 50, "Task", partial: true, body: true)));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("[RelayCommand] private async Task EditAsync(Order item) { await OnEditAsync(); }", vm);
        Assert.Contains("    private partial Task OnEditAsync();", vm);
        Assert.DoesNotContain("OnEditAsync(Order item);", vm);
        Assert.DoesNotContain(result.Diagnostics, x => x.Code == "FS2017");
        var d = Single(result, "FS2016");
        Assert.Equal("'OnEditAsync()' in ViewModels/DashboardViewModel.cs takes no parameter, but table -> vm.Edit(item) (dashboard.flag line 5) passes the Order row; "
                     + "the generator declared it without one and EditCommand drops the row", d.Message);
        Assert.Equal("to use the row, implement private partial async Task OnEditAsync(Order item) { ... }", d.Hint);
    }

    [Theory]
    [InlineData("Order?")]
    [InlineData("object")]
    public void Delegate_taking_the_row_as_a_compatible_type_is_declared_with_it(string type)
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "OnEditAsync", 50, "Task", partial: true, body: true, parameters: [new(type, "order")])));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("[RelayCommand] private async Task EditAsync(Order item) { await OnEditAsync(item); }", vm);
        Assert.Contains($"    private partial Task OnEditAsync({type} item);", vm);
        Assert.DoesNotContain(result.Diagnostics, x => x.Code == "FS2017");
    }

    [Fact]
    public void Delegate_taking_another_row_type_is_FS2019()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "OnEditAsync", 50, "Task", partial: true, body: true, parameters: [new("Customer", "c")])));
        Assert.Equal("implement private partial async Task OnEditAsync(Order item) { ... }; the command passes the Order row", Single(result, "FS2019").Hint);
    }

    [Theory]
    [InlineData("", "    partial void OnSaveAsync();")]
    [InlineData("private", "    private partial void OnSaveAsync();")]
    public void Partial_void_delegate_is_declared_void_and_not_awaited(string accessibility, string declaration)
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "OnSaveAsync", 50, "void", accessibility: accessibility, partial: true, body: true)));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var vm = File(result, "DashboardViewModel.g.cs");
        Assert.Contains("[RelayCommand] private Task SaveAsync() { OnSaveAsync(); return Task.CompletedTask; }", vm);
        Assert.Contains(declaration, vm);
        Assert.DoesNotContain("partial Task OnSaveAsync", vm);
        Assert.Equal("'OnSaveAsync' is 'partial void' in ViewModels/DashboardViewModel.cs; the generator declared it void to match and SaveCommand does not await it",
            Single(result, "FS2016").Message);
    }

    [Fact]
    public void Toolkit_property_callbacks_are_not_command_delegates()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "OnOpenCountChanged", 60, "void", partial: true, body: true, parameters: [new("string", "value")])));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "FS2016" or "FS2017");
        Assert.DoesNotContain("OnOpenCountChanged", File(result, "DashboardViewModel.g.cs"));
    }

    [Fact]
    public void Dialog_cancel_command_declared_by_hand_is_skipped()
    {
        var result = Compile(Vm(Member(UserMemberKind.Method, "CancelIsDeleteOpen", 60, "void", attributes: ["RelayCommand"], body: true)));
        Assert.DoesNotContain("private void CancelIsDeleteOpen", File(result, "DashboardViewModel.g.cs"));
        Assert.Contains("'CancelIsDeleteOpenCommand' is also declared", Single(result, "FS2016").Message);
    }

    [Fact]
    public void Other_namespaces_do_not_count()
    {
        var user = new UserDeclarations().Add("Other.ViewModels.DashboardViewModel",
            Member(UserMemberKind.Field, "_openCount", 12, "string", attributes: ["ObservableProperty"]));
        var result = Compile(user);
        Assert.Contains("private string _openCount", File(result, "DashboardViewModel.g.cs"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "FS2016");
    }

    // ── Pages ──────────────────────────────────────────────────────

    private static UserDeclarations Page(params UserMember[] members)
    {
        var user = new UserDeclarations();
        foreach (var m in members)
            user.Add($"{Ns}.DashboardView", m);
        return user;
    }

    [Fact]
    public void Page_runs_OnViewCreated_at_the_end_of_the_generated_constructor()
    {
        var page = File(Compile(UserDeclarations.Empty), "DashboardView.xaml.cs");
        Assert.Contains("        InitializeComponent();\n        OnViewCreated();\n    }", page.ReplaceLineEndings("\n"));
        Assert.Contains("    partial void OnViewCreated();", page);
        Assert.Contains("'partial void OnViewCreated()'", page.Split('\n')[1]);
    }

    [Fact]
    public void Page_hook_takes_the_accessibility_of_the_implementation()
    {
        var page = File(Compile(Page(Member(UserMemberKind.Method, "OnViewCreated", 9, "void", partial: true, body: true, file: PageFile))), "DashboardView.xaml.cs");
        Assert.Contains("    private partial void OnViewCreated();", page);
    }

    [Fact]
    public void User_parameterless_constructor_replaces_the_generated_one()
    {
        var result = Compile(Page(Member(UserMemberKind.Constructor, "DashboardView", 8, accessibility: "public", body: true, initializes: true, file: PageFile)));

        var page = File(result, "DashboardView.xaml.cs");
        Assert.DoesNotContain("public DashboardView() :", page);
        Assert.Contains("public DashboardView(ViewModels.DashboardViewModel viewModel)", page);
        Assert.Contains("get => _viewModel ??= App.GetService<ViewModels.DashboardViewModel>();", page);
        Assert.Contains("private set => _viewModel = value;", page);
        Assert.DoesNotContain("OnNavigatedTo", page);
        Assert.Contains("// Your constructor in Views/DashboardView.cs replaces the generated one;", page);

        var d = Single(result, "FS2016");
        Assert.Equal("'DashboardView()' is also declared in Views/DashboardView.cs; the generator skipped its own constructor", d.Message);
        Assert.StartsWith("move the body into 'partial void OnViewCreated() { ... }'", d.Hint);
        Assert.Equal(["DashboardView(ViewModels.DashboardViewModel viewModel)"], result.Contracts.Single().Constructors);
    }

    [Fact]
    public void User_constructor_without_InitializeComponent_gets_it_from_OnNavigatedTo()
    {
        var result = Compile(Page(Member(UserMemberKind.Constructor, "DashboardView", 8, accessibility: "public", body: true, file: PageFile)));

        var page = File(result, "DashboardView.xaml.cs");
        Assert.Contains("protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)", page);
        Assert.EndsWith("so the generated OnNavigatedTo does", Single(result, "FS2016").Message);
    }

    [Fact]
    public void User_constructor_and_OnNavigatedTo_without_InitializeComponent_warns_the_page_stays_empty()
    {
        var result = Compile(Page(
            Member(UserMemberKind.Constructor, "DashboardView", 8, accessibility: "public", body: true, file: PageFile),
            Member(UserMemberKind.Method, "OnNavigatedTo", 12, "void", accessibility: "protected override", body: true, file: PageFile)));

        Assert.DoesNotContain("OnNavigatedTo", File(result, "DashboardView.xaml.cs"));
        Assert.EndsWith("so the page stays empty: call it first", Single(result, "FS2016").Message);
    }

    [Fact]
    public void User_view_model_constructor_replaces_the_generated_one()
    {
        var result = Compile(Page(Member(UserMemberKind.Constructor, "DashboardView", 8, accessibility: "public", body: true, initializes: true,
            parameters: [new("DashboardViewModel", "vm")], file: PageFile)));

        var page = File(result, "DashboardView.xaml.cs");
        Assert.Contains("public DashboardView() : this(App.GetService<ViewModels.DashboardViewModel>()) { }", page);
        Assert.DoesNotContain("public DashboardView(ViewModels.DashboardViewModel viewModel)", page);
        Assert.Equal("'DashboardView(DashboardViewModel)' is also declared in Views/DashboardView.cs; the generator skipped its own constructor", Single(result, "FS2016").Message);
    }

    [Fact]
    public void User_constructor_with_toasts_wires_the_lazy_view_model()
    {
        const string source = """
            view DashboardView {
                bind vm: DashboardViewModel;
                toast "Saved" when(vm.ShowSaved) duration: 2000;
            }
            """;
        var page = File(Compile(Page(Member(UserMemberKind.Constructor, "DashboardView", 8, accessibility: "public", body: true, initializes: true, file: PageFile)), source),
            "DashboardView.xaml.cs");
        Assert.Contains("get => _viewModel ??= WireViewModel(App.GetService<ViewModels.DashboardViewModel>());", page);
        Assert.Contains("viewModel.PropertyChanged += OnToastFlagChanged;", page);
    }

    [Fact]
    public void User_page_view_model_property_is_skipped()
    {
        var result = Compile(Page(Member(UserMemberKind.Property, "ViewModel", 6, "DashboardViewModel", accessibility: "public", file: PageFile)));
        Assert.DoesNotContain("ViewModel { get; }", File(result, "DashboardView.xaml.cs"));
        Assert.Equal("'ViewModel' is also declared in Views/DashboardView.cs; the generator skipped it", Single(result, "FS2016").Message);
    }

    // ── Item types ─────────────────────────────────────────────────

    [Fact]
    public void Item_type_property_declared_by_hand_is_skipped()
    {
        var user = new UserDeclarations()
            .Add($"{Ns}.ViewModels.Order", Member(UserMemberKind.Property, "Customer", 5, "string", accessibility: "public", file: "Models/Order.cs"))
            .Add($"{Ns}.ViewModels.Order", Member(UserMemberKind.Property, "Total", 6, "double", accessibility: "public", file: "Models/Order.cs"));
        var result = Compile(user);

        var order = File(result, "Order.g.cs");
        Assert.DoesNotContain("Customer", order.Split("#nullable enable")[1]);
        Assert.DoesNotContain("Total", order.Split("#nullable enable")[1]);
        Assert.Contains("// Declared by you, so not generated here: Customer (Models/Order.cs), Total (Models/Order.cs).", order);
        var warnings = result.Diagnostics.Where(d => d.Code == "FS2016").ToList();
        Assert.Equal("'Order.Customer' is also declared in Models/Order.cs; the generator skipped it", warnings[0].Message);
        Assert.Equal("'Order.Total' is also declared in Models/Order.cs as double, but the .flag infers decimal; the generator skipped it and uses your double", warnings[1].Message);
        Assert.Equal(["Customer", "Total"], result.ItemTypes.Single().DeclaredByUser);
    }

    // ── Global usings ──────────────────────────────────────────────

    [Fact]
    public void Global_usings_import_winui_and_the_app_view_models()
    {
        var usings = File(Compile(UserDeclarations.Empty), "GlobalUsings.g.cs");
        Assert.Contains("global using Microsoft.UI;", usings);
        Assert.Contains("global using Microsoft.UI.Xaml.Navigation;", usings);
        Assert.Contains("global using Windows.Storage.Pickers;", usings);
        Assert.Contains("global using Windows.Graphics.Imaging;", usings);
        Assert.Contains("global using Color = Windows.UI.Color;", usings);
        Assert.Contains("global using App.ViewModels;", usings);
        Assert.DoesNotContain("global using Windows.UI;", usings);
        Assert.DoesNotContain("global using Windows.System;", usings);
        Assert.DoesNotContain("Shapes", usings);
    }

    [Fact]
    public void Global_usings_skip_an_empty_view_models_namespace_and_need_an_app()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = Ns, NamespaceIsOverridden = true };
        var result = compiler.CompileAll([new SourceFile("app.flag", "app App { nav side { link \"Home\" -> HomeView; } }"),
                                          new SourceFile("home.flag", "view HomeView { text \"Hi\"; }")]);
        Assert.DoesNotContain("ViewModels", File(result, "GlobalUsings.g.cs"));

        var viewsOnly = new FlagstaffCompiler { AppNamespace = Ns }.CompileAll([new SourceFile("home.flag", "view HomeView { text \"Hi\"; }")]);
        Assert.DoesNotContain(viewsOnly.Files, f => f.Name == "GlobalUsings.g.cs");
    }
}
