using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>list, table upgrades, select options/display, empty, and the project-wide item type registry.</summary>
public class DataTests
{
    private static CompilationResult Compile(string source, FlagstaffCompiler? compiler = null, string file = "test.flag") =>
        (compiler ?? new FlagstaffCompiler { AppNamespace = "TestApp" }).Compile(source, file);

    private static string Xaml(CompilationResult result) =>
        result.Files.First(f => f.Name.EndsWith(".xaml", StringComparison.Ordinal)).Content;

    private static Diagnostic Single(CompilationResult result, string code) =>
        Assert.Single(result.Diagnostics, d => d.Code == code);

    // ── FS2005 ─────────────────────────────────────────────────────

    [Fact]
    public void Vm_binding_inside_list_template_is_fs2005()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                list items: vm.Rows of Row {
                    text item.Name;
                    text vm.Title;
                }
            }
            """);

        var d = Single(result, "FS2005");
        Assert.Contains("vm.Title", d.Message);
        Assert.Equal(new SourceLocation("test.flag", 5, 14), d.Location);
    }

    [Fact]
    public void Vm_condition_inside_list_template_is_fs2005()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                list items: vm.Rows of Row {
                    text item.Name when(vm.ShowNames);
                }
            }
            """);

        var d = Single(result, "FS2005");
        Assert.Contains("vm.ShowNames", d.Message);
    }

    [Fact]
    public void Item_binding_outside_template_is_fs2005()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                text item.Name;
            }
            """);

        var d = Single(result, "FS2005");
        Assert.Contains("item.Name", d.Message);
        Assert.Equal(new SourceLocation("test.flag", 3, 10), d.Location);
    }

    [Fact]
    public void Vm_binding_in_table_row_action_is_fs2005()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row {
                    col "A" bind: item.A;
                    button "Edit" -> vm.EditCommand(item) when(vm.CanEdit);
                }
            }
            """);

        Single(result, "FS2005");
    }

    // ── FS2010 ─────────────────────────────────────────────────────

    [Fact]
    public void Conflicting_item_property_types_across_views_are_fs2010()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var first = Compile("""
            view A {
                bind vm: AVM;
                table items: vm.Orders of Order { col "Total" bind: item.Total format: currency; }
            }
            """, compiler, "a.flag");
        Assert.False(first.HasErrors);

        var second = Compile("""
            view B {
                bind vm: BVM;
                list items: vm.Orders of Order { text item.Total format: date; }
            }
            """, compiler, "b.flag");

        var d = Single(second, "FS2010");
        Assert.Contains("Order.Total", d.Message);
        Assert.Contains("a.flag(3,", d.Message);      // first usage
        Assert.Equal("b.flag", d.Location.File);       // second usage
        Assert.Equal(3, d.Location.Line);
    }

    [Fact]
    public void Default_typed_usage_does_not_conflict_with_explicit_type()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        Compile("""
            view A { bind vm: AVM; table items: vm.Orders of Order { col "Total" bind: item.Total; } }
            """, compiler, "a.flag");
        var second = Compile("""
            view B { bind vm: BVM; table items: vm.Orders of Order { col "Total" bind: item.Total format: currency; } }
            """, compiler, "b.flag");

        Assert.DoesNotContain(second.Diagnostics, d => d.Code == "FS2010");
        var order = compiler.GenerateAppScaffold().Files.Single(f => f.Name == "Order.g.cs");
        Assert.Contains("public decimal Total { get; set; }", order.Content);
    }

    [Fact]
    public void Item_types_merge_across_views_into_one_file()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var all = compiler.CompileAll([
            new SourceFile("a.flag", "view A { bind vm: AVM; table items: vm.Orders of Order { col \"C\" bind: item.Customer; } }"),
            new SourceFile("b.flag", "view B { bind vm: BVM; list items: vm.Orders of Order { checkbox \"Done\" item.IsDone; } }"),
        ]);

        Assert.False(all.HasErrors);
        var order = Assert.Single(all.Files, f => f.Name == "Order.g.cs");
        Assert.Contains("public string Customer { get; set; } = \"\";", order.Content);
        Assert.Contains("public bool IsDone { get; set; }", order.Content);
    }

    // ── FS2011 / FS2012 / FS2013 ───────────────────────────────────

    [Fact]
    public void Empty_outside_table_or_list_is_fs2011()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                card "c" { empty "Nothing."; }
            }
            """);

        var d = Single(result, "FS2011");
        Assert.Equal("'empty' must be a child of table or list", d.Message);
        Assert.Equal(new SourceLocation("test.flag", 3, 16), d.Location);
    }

    [Fact]
    public void Empty_without_items_is_fs2011()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                list of Row { text item.A; empty "Nothing."; }
            }
            """);

        Assert.Contains("items:", Single(result, "FS2011").Message);
    }

    [Fact]
    public void Non_column_child_of_table_is_fs2012()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row { col "A" bind: item.A; text "stray"; }
            }
            """);

        Assert.Contains("'text' is not allowed inside a table", Single(result, "FS2012").Message);
    }

    [Fact]
    public void Col_outside_table_is_fs2012()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                col "A" bind: item.A;
            }
            """);

        Assert.Equal("'col' must be a child of table", Single(result, "FS2012").Message);
    }

    [Fact]
    public void Display_without_items_of_type_is_fs2013()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                select "Vendor" vm.Vendor items: vm.Vendors display: item.Name;
            }
            """);

        Single(result, "FS2013");
    }

    // ── FS1005 shapes ──────────────────────────────────────────────

    [Theory]
    [InlineData("options: [1, 2]")]
    [InlineData("options: \"Open\"")]
    [InlineData("options: []")]
    public void Bad_select_options_are_fs1005(string options)
    {
        var result = Compile($$"""
            view V {
                bind vm: VM;
                select "S" vm.S {{options}};
            }
            """);

        Assert.Contains("options", Single(result, "FS1005").Message);
    }

    [Theory]
    [InlineData("width: wide")]
    [InlineData("width: \"2x\"")]
    [InlineData("width: 0")]
    public void Bad_col_width_is_fs1005(string width)
    {
        var result = Compile($$"""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row { col "A" bind: item.A {{width}}; }
            }
            """);

        Assert.Contains("width", Single(result, "FS1005").Message);
    }

    // ── Emission ───────────────────────────────────────────────────

    [Fact]
    public void Col_widths_apply_to_header_and_rows()
    {
        var xaml = Xaml(Compile("""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row {
                    col "A" bind: item.A width: auto;
                    col "B" bind: item.B width: 80;
                    col "C" bind: item.C width: "2*";
                    col "D" bind: item.D;
                }
            }
            """));

        var defs = "<ColumnDefinition Width=\"Auto\" />";
        Assert.Equal(2, xaml.Split(defs).Length - 1);
        Assert.Equal(2, xaml.Split("<ColumnDefinition Width=\"80\" />").Length - 1);
        Assert.Equal(2, xaml.Split("<ColumnDefinition Width=\"2*\" />").Length - 1);
        Assert.Equal(2, xaml.Split("<ColumnDefinition Width=\"*\" />").Length - 1);
    }

    [Fact]
    public void Number_format_with_precision_uses_number_converter()
    {
        var xaml = Xaml(Compile("""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row { col "Rate" bind: item.Rate format: number(2); }
            }
            """));

        Assert.Contains("Text=\"{x:Bind Rate, Converter={StaticResource NumberConverter}, ConverterParameter=F2}\"", xaml);
    }

    [Fact]
    public void Row_click_on_list_wires_handler_in_code_behind()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                list items: vm.Rows of Row -> vm.OpenCommand(item) { text item.Name; }
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Contains("IsItemClickEnabled=\"True\" ItemClick=\"OnItemClick_1\"", Xaml(result));
        var cs = result.Files.Single(f => f.Name == "V.xaml.cs").Content;
        Assert.Contains("private void OnItemClick_1(object sender, ItemClickEventArgs e)", cs);
        Assert.Contains("if (e.ClickedItem is ViewModels.Row item) ViewModel.OpenCommand.Execute(item);", cs);
        var vm = result.Files.Single(f => f.Name == "VM.g.cs").Content;
        Assert.Contains("private async Task OpenAsync(Row item)", vm);
    }

    [Fact]
    public void Button_nested_in_list_template_binds_through_page()
    {
        var xaml = Xaml(Compile("""
            view V {
                bind vm: VM;
                list items: vm.Rows of Row {
                    row cols: 2 { text item.Name; button "Edit" -> vm.EditCommand(item); }
                }
            }
            """));

        Assert.Contains("Command=\"{Binding ElementName=FlagstaffPage, Path=ViewModel.EditCommand}\" CommandParameter=\"{x:Bind}\"", xaml);
        Assert.DoesNotContain("x:Bind ViewModel.EditCommand", xaml);
    }

    [Fact]
    public void Select_string_options_emit_strings_and_infer_string()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                select "Status" vm.Status options: ["Open", "Closed"];
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Contains("<x:String>Open</x:String>", Xaml(result));
        Assert.Contains("private string _status", result.Files.Single(f => f.Name == "VM.g.cs").Content);
    }
}
