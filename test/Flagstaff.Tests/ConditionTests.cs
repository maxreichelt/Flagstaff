using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>Condition grammar (when/enabled), ConditionCompiler output and its diagnostics.</summary>
public class ConditionTests
{
    private static ViewDecl ParseView(string source)
    {
        var parser = new Parser(new Lexer(source, "test.flag").Tokenize());
        var file = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        return (ViewDecl)file.Declarations[0];
    }

    private static CompilationResult Compile(string source) =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile(source, "invoices.flag");

    private static string File(CompilationResult result, string name) =>
        result.Files.First(f => f.Name == name).Content;

    // ── Grammar ────────────────────────────────────────────────────

    [Fact]
    public void And_binds_tighter_than_or_and_not_binds_tightest()
    {
        var view = ParseView("""
            view V {
                text "a" when(vm.A || vm.B && !vm.C);
            }
            """);

        var or = Assert.IsType<BinaryCond>(view.Body[0].When);
        Assert.Equal(LogicalOp.Or, or.Op);
        Assert.Equal("vm.A", Assert.IsType<PathCond>(or.Left).Path);
        var and = Assert.IsType<BinaryCond>(or.Right);
        Assert.Equal(LogicalOp.And, and.Op);
        var not = Assert.IsType<NotCond>(and.Right);
        Assert.Equal("vm.C", Assert.IsType<PathCond>(not.Inner).Path);
        Assert.Equal("vm.A || vm.B && !vm.C", ConditionCompiler.Print(or));
    }

    [Fact]
    public void Parentheses_group_and_operands_cover_all_literals()
    {
        var view = ParseView("""
            view V {
                text "a" when((vm.A || vm.B) && vm.N >= 1.5 && vm.S == "x" && vm.F != false && vm.O == null);
            }
            """);

        var cond = view.Body[0].When!;
        Assert.Equal("(vm.A || vm.B) && vm.N >= 1.5 && vm.S == \"x\" && vm.F != false && vm.O == null", ConditionCompiler.Print(cond));
        Assert.Equal(new SourceLocation("test.flag", 2, 20), cond.Location); // first operand inside the parentheses
    }

    [Fact]
    public void Enabled_clause_is_parsed_with_the_same_grammar()
    {
        var view = ParseView("""
            view V {
                button "Save" -> vm.SaveCommand enabled(vm.IsDirty && !vm.IsBusy);
            }
            """);

        Assert.Null(view.Body[0].When);
        Assert.IsType<BinaryCond>(view.Body[0].Enabled);
    }

    [Fact]
    public void List_values_parse()
    {
        var view = ParseView("""
            view V {
                select "S" vm.S options: ["a", "b",];
                select "N" vm.N options: [1, 2];
            }
            """);

        var strings = Assert.IsType<ListValue>(view.Body[0].Props.First(p => p.Key == "options").Value);
        Assert.Equal([new StringValue("a"), new StringValue("b")], strings.Items);
        var ints = Assert.IsType<ListValue>(view.Body[1].Props.First(p => p.Key == "options").Value);
        Assert.Equal([new IntValue(1), new IntValue(2)], ints.Items);
    }

    [Fact]
    public void Command_with_item_argument_sets_nav_target_arg()
    {
        var view = ParseView("""
            view V {
                table items: vm.Orders of Order {
                    col "Id" bind: item.Id;
                    button "Edit" -> vm.EditCommand(item);
                }
            }
            """);

        var button = ((ChildElement)((BlockBody)view.Body[0].Body).Children[1]).Element;
        Assert.Equal("vm.EditCommand", button.NavTarget);
        Assert.Equal("item", button.NavTargetArg);
    }

    [Fact]
    public void Lexer_tokenizes_logical_operators_and_keywords()
    {
        var tokens = new Lexer("! && || != enabled null").Tokenize();
        Assert.Equal(
            [TokenKind.Bang, TokenKind.AndAnd, TokenKind.OrOr, TokenKind.BangEqual, TokenKind.Keyword, TokenKind.Keyword, TokenKind.EndOfFile],
            tokens.Select(t => t.Kind));
    }

    // ── Compilation ────────────────────────────────────────────────

    [Fact]
    public void Compound_when_compiles_to_static_function()
    {
        var result = Compile("""
            view InvoiceListView {
                bind vm: InvoiceListViewModel;
                stat "Open" vm.Count when(vm.Count > 5 && vm.Status != "Closed");
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Contains(
            "Visibility=\"{x:Bind local:InvoiceListView.When_1(ViewModel.Count, ViewModel.Status), Mode=OneWay}\"",
            File(result, "InvoiceListView.xaml"));

        var cs = File(result, "InvoiceListView.xaml.cs");
        Assert.Contains("using Microsoft.UI.Xaml;", cs);
        Assert.Contains("    // when(vm.Count > 5 && vm.Status != \"Closed\") at invoices.flag:3", cs);
        Assert.Contains(
            "    public static Microsoft.UI.Xaml.Visibility When_1(int count, string status) => (count > 5 && status != \"Closed\") ? Visibility.Visible : Visibility.Collapsed;",
            cs);

        var vm = File(result, "InvoiceListViewModel.g.cs");
        Assert.Contains("[ObservableProperty] private int _count;", vm);
        Assert.Contains("[ObservableProperty] private string _status = \"\";", vm);
    }

    [Fact]
    public void Trivial_when_keeps_bool_converter_binding()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                text "x" when(vm.IsAdmin);
            }
            """);

        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.IsAdmin, Mode=OneWay, Converter={StaticResource BoolToVisibility}}\"",
            File(result, "V.xaml"));
        Assert.DoesNotContain("When_", File(result, "V.xaml.cs"));
        Assert.DoesNotContain("using Microsoft.UI.Xaml;", File(result, "V.xaml.cs"));
    }

    [Fact]
    public void Enabled_trivial_and_compound()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                button "Save" -> vm.SaveCommand enabled(vm.CanSave);
                button "Send" -> vm.SendCommand enabled(vm.CanSave && !vm.IsBusy);
            }
            """);

        var xaml = File(result, "V.xaml");
        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.CanSave, Mode=OneWay}\"", xaml);
        Assert.Contains("IsEnabled=\"{x:Bind local:V.Enabled_1(ViewModel.CanSave, ViewModel.IsBusy), Mode=OneWay}\"", xaml);
        Assert.Contains("public static bool Enabled_1(bool canSave, bool isBusy) => canSave && !isBusy;", File(result, "V.xaml.cs"));
    }

    [Fact]
    public void Functions_are_numbered_in_document_order_per_kind()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                text "a" when(vm.A > 1);
                card "c" when(!vm.B) {
                    button "b" -> vm.GoCommand enabled(vm.C || vm.D) when(vm.E == 2);
                }
            }
            """);

        var cs = File(result, "V.xaml.cs");
        Assert.True(cs.IndexOf("When_1(int a)") < cs.IndexOf("When_2(bool b)"));
        Assert.True(cs.IndexOf("When_2(bool b)") < cs.IndexOf("When_3(int e)"));
        // Within one element, when() is numbered before enabled().
        Assert.True(cs.IndexOf("When_3(int e)") < cs.IndexOf("Enabled_1(bool c, bool d)"));
    }

    [Fact]
    public void Decimal_property_compared_with_number_uses_m_suffix()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                stat "Total" vm.Total format: currency when(vm.Total > 5 || vm.Total < 0.5);
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Contains("When_1(decimal total) => (total > 5m || total < 0.5m)", File(result, "V.xaml.cs"));
    }

    [Fact]
    public void Null_comparison_on_selected_item_is_valid()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Orders selected: vm.Selected of Order { col "Id" bind: item.Id; }
                button "Edit" -> vm.EditCommand enabled(vm.Selected != null);
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Contains("public static bool Enabled_1(ViewModels.Order? selected) => selected != null;", File(result, "V.xaml.cs"));
    }

    [Fact]
    public void Parameter_names_escape_csharp_keywords_and_strings_are_escaped()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                text "x" when(vm.Event == "a" && vm.Class > 1);
            }
            """);

        Assert.Contains("When_1(string @event, int @class) => (@event == \"a\" && @class > 1)", File(result, "V.xaml.cs"));
    }

    // ── Diagnostics ────────────────────────────────────────────────

    [Fact]
    public void Toast_requires_bare_bool_fs2003()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                toast "Saved" when(vm.A && vm.B);
                confirm "Delete?" when(vm.Count > 0) { button "OK" -> vm.OkCommand; }
                toast "Fine" when(vm.ShowFine);
            }
            """);

        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "FS2003"));
        Assert.Contains("IsOpen=\"{x:Bind ViewModel.ShowFine, Mode=TwoWay}\"", File(result, "V.xaml"));
    }

    [Fact]
    public void Explicit_type_vs_incompatible_literal_is_fs2004()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                number "Qty" vm.Qty;
                text "x" when(vm.Qty == "many");
            }
            """);

        var d = Assert.Single(result.Diagnostics, x => x.Code == "FS2004");
        Assert.Equal(4, d.Location.Line);           // the comparison
        Assert.Contains("invoices.flag(3,", d.Message); // the explicit usage
        Assert.Contains("double", d.Message);
    }

    [Fact]
    public void Explicit_type_registered_after_comparison_still_reports_fs2004()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                text "x" when(vm.Done == 1);
                checkbox "Done" vm.Done;
            }
            """);

        Assert.Single(result.Diagnostics, x => x.Code == "FS2004");
        Assert.Contains("bool _done", File(result, "VM.g.cs"));
    }

    [Fact]
    public void Null_against_value_type_and_ordering_strings_are_fs2004()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                number "N" vm.N;
                text "a" when(vm.N == null);
                text "b" when(vm.Name > "m");
            }
            """);

        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "FS2004"));
    }

    [Fact]
    public void Vm_path_inside_item_template_is_fs2005()
    {
        var view = ParseView("""
            view V {
                text "a" when(vm.A > 1);
                text "b" when(item.Status == "Open" && item.Count > 2);
            }
            """);
        var conditions = new ConditionCompiler(view, TypeInference.InferMembers(view)) { InItemTemplate = true };

        Assert.Contains("local:V.When_1(", conditions.VisibilityAttr(view.Body[0]));
        Assert.Single(conditions.Diagnostics, d => d.Code == "FS2005");

        Assert.Equal(
            " Visibility=\"{x:Bind local:V.When_2(Status, Count)}\"",
            conditions.VisibilityAttr(view.Body[1]));
        var fn = conditions.Functions.Single(f => f.Name == "When_2");
        Assert.Equal([("item.Status", "string"), ("item.Count", "int")], fn.Parameters);
        Assert.Equal(["status", "count"], fn.ParameterNames);
        Assert.Single(conditions.Diagnostics);
    }

    [Fact]
    public void Item_path_outside_item_template_is_fs2005()
    {
        var result = Compile("""
            view V {
                text "a" when(item.Flag);
            }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "FS2005");
    }

    [Fact]
    public void Old_converter_functions_are_still_generated_but_not_referenced()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var result = compiler.Compile("""
            view V { bind vm: VM; stat "S" vm.S when(vm.S > 0); }
            """, "v.flag");
        Assert.DoesNotContain("Converters.", File(result, "V.xaml"));
        Assert.Contains("IsZero", compiler.GenerateAppScaffold().Files.First(f => f.Name == "Converters.g.cs").Content);
    }
}
