using Flagstaff.Compiler;

namespace Flagstaff.Tests;

public class TypeInferenceTests
{
    private static List<InferredMember> InferFrom(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        var file = new Parser(tokens).Parse();
        var view = (ViewDecl)file.Declarations[0];
        return TypeInference.InferMembers(view);
    }

    private static ViewDecl Parse(string source) =>
        new Parser(new Lexer(source, "test.flag").Tokenize()).Parse().Declarations.OfType<ViewDecl>().Single();

    [Fact]
    public void Infers_int_from_select_with_range()
    {
        var members = InferFrom("""
            view V {
                bind vm: VM;
                select vm.Year options: 2019..2026;
            }
            """);

        var year = members.First(m => m.Name == "Year");
        Assert.Equal("int", year.CSharpType);
    }

    [Fact]
    public void Infers_bool_from_when_clause()
    {
        var members = InferFrom("""
            view V {
                bind vm: VM;
                alert when(vm.HasWarning) variant: warning {
                    "msg";
                }
            }
            """);

        var prop = members.First(m => m.Name == "HasWarning");
        Assert.Equal("bool", prop.CSharpType);
    }

    [Fact]
    public void Infers_decimal_from_currency_format()
    {
        var members = InferFrom("""
            view V {
                bind vm: VM;
                line "Total" vm.Amount format: currency;
            }
            """);

        var prop = members.First(m => m.Name == "Amount");
        Assert.Equal("decimal", prop.CSharpType);
    }

    [Fact]
    public void Infers_string_from_textbox()
    {
        var members = InferFrom("""
            view V {
                bind vm: VM;
                textbox vm.Name;
            }
            """);

        var prop = members.First(m => m.Name == "Name");
        Assert.Equal("string", prop.CSharpType);
    }

    [Fact]
    public void Infers_bool_from_checkbox()
    {
        var members = InferFrom("""
            view V {
                bind vm: VM;
                checkbox "Active" vm.IsActive;
            }
            """);

        var prop = members.First(m => m.Name == "IsActive");
        Assert.Equal("bool", prop.CSharpType);
    }

    [Fact]
    public void Infers_command_from_nav_target()
    {
        var members = InferFrom("""
            view V {
                bind vm: VM;
                button "Save" -> vm.SaveCommand;
            }
            """);

        var cmd = members.First(m => m.Name == "SaveCommand");
        Assert.Equal(InferredKind.Command, cmd.Kind);
        Assert.Equal("IRelayCommand", cmd.CSharpType);
    }

    [Fact]
    public void Infers_collection_with_of_type()
    {
        var members = InferFrom("""
            view V {
                bind vm: VM;
                table items: vm.Items of Expense { }
            }
            """);

        var prop = members.First(m => m.Name == "Items");
        Assert.Contains("ObservableCollection<Expense>", prop.CSharpType);
    }

    [Fact]
    public void Infers_string_from_stat_positional_binding()
    {
        var members = InferFrom("""
            view V {
                bind vm: VM;
                stat "Count" vm.Total;
            }
            """);

        var prop = members.First(m => m.Name == "Total");
        Assert.Equal("string", prop.CSharpType);
        Assert.Equal(InferenceConfidence.Default, prop.Confidence);
    }

    // ── Keyword element types ──────────────────────────────────────

    [Fact]
    public void Badge_infers_string_type()
    {
        var tokens = new Lexer("""
            view V {
                bind vm: VM;
                badge vm.RemainingCount;
            }
            """).Tokenize();
        var file = new Parser(tokens).Parse();
        var view = (ViewDecl)file.Declarations[0];
        var members = TypeInference.InferMembers(view);

        var badge = members.First(m => m.Name == "RemainingCount");
        Assert.Equal("string", badge.CSharpType);
    }

    [Fact]
    public void Table_selected_infers_item_type()
    {
        var tokens = new Lexer("""
            view V {
                bind vm: VM;
                table items: vm.Expenses selected: vm.SelectedExpense of Expense {
                    col "Vendor" bind: item.Vendor;
                }
            }
            """).Tokenize();
        var file = new Parser(tokens).Parse();
        var view = (ViewDecl)file.Declarations[0];
        var members = TypeInference.InferMembers(view);

        var selected = members.First(m => m.Name == "SelectedExpense");
        Assert.Equal("Expense?", selected.CSharpType);
    }

    [Fact]
    public void Progress_binding_is_double()
    {
        var members = TypeInference.InferMembers(Parse("view V { progress vm.Percent; }"));
        var m = Assert.Single(members);
        Assert.Equal("double", m.CSharpType);
        Assert.Equal(InferenceConfidence.Explicit, m.Confidence);
    }
}
