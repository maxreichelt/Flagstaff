using Flagstaff.Compiler;

namespace Flagstaff.Tests;

public class ParserTests
{
    private static FlagFile Parse(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        return new Parser(tokens).Parse();
    }

    [Fact]
    public void Parses_minimal_app()
    {
        var file = Parse("""
            app TestApp {
                theme { accent: #c0392b; }
                nav top {
                    brand "TestApp";
                    link "Home" icon: speedometer -> HomeView;
                }
            }
            """);

        Assert.Single(file.Declarations);
        var app = Assert.IsType<AppDecl>(file.Declarations[0]);
        Assert.Equal("TestApp", app.Name);
        Assert.Equal(2, app.Members.Count);
    }

    [Fact]
    public void Parses_view_with_bind()
    {
        var file = Parse("""
            view HomeView {
                bind vm: HomeViewModel;
                text "Hello";
            }
            """);

        var view = Assert.IsType<ViewDecl>(file.Declarations[0]);
        Assert.Equal("HomeView", view.Name);
        Assert.NotNull(view.Binding);
        Assert.Equal("HomeViewModel", view.Binding!.ViewModelType);
        Assert.Single(view.Body);
    }

    [Fact]
    public void Parses_stat_with_positional_binding_and_when()
    {
        var file = Parse("""
            view V {
                stat "Count" vm.X variant: danger when(vm.X > 0);
            }
            """);

        var view = Assert.IsType<ViewDecl>(file.Declarations[0]);
        var stat = view.Body[0];
        Assert.Equal("stat", stat.Keyword);
        var compare = Assert.IsType<CompareCond>(stat.When);
        Assert.Equal("vm.X", Assert.IsType<PathOperand>(compare.Left).Path);
        Assert.Equal(ComparisonOp.Gt, compare.Op);
        Assert.Equal(0, Assert.IsType<IntValue>(Assert.IsType<LiteralOperand>(compare.Right).Value).Value);
        var binding = stat.Props.First(p => p.Key == "_binding");
        Assert.IsType<BindingValue>(binding.Value);
    }

    [Fact]
    public void Parses_format_clause()
    {
        var file = Parse("""
            view V {
                line "Total" vm.Amount format: currency bold;
            }
            """);

        var view = Assert.IsType<ViewDecl>(file.Declarations[0]);
        var line = view.Body[0];
        Assert.NotNull(line.Format);
        Assert.Equal(FormatKind.Currency, line.Format!.Kind);
        Assert.Contains(line.Props, p => p.Key == "bold");
    }

    [Fact]
    public void Parses_nested_block()
    {
        var file = Parse("""
            view V {
                card "Test" {
                    text "Inner";
                    button "Click";
                }
            }
            """);

        var view = Assert.IsType<ViewDecl>(file.Declarations[0]);
        var card = view.Body[0];
        var block = Assert.IsType<BlockBody>(card.Body);
        Assert.Equal(2, block.Children.Count);
    }

    [Fact]
    public void Parses_select_with_range()
    {
        var file = Parse("""
            view V {
                select vm.Year options: 2019..2026;
            }
            """);

        var view = Assert.IsType<ViewDecl>(file.Declarations[0]);
        var sel = view.Body[0];
        Assert.Equal("select", sel.Keyword);
        var rangeProp = sel.Props.First(p => p.Key == "options");
        var range = Assert.IsType<RangeValue>(rangeProp.Value);
        Assert.Equal(2019, range.Start);
        Assert.Equal(2026, range.End);
    }

    [Fact]
    public void Parses_nav_target()
    {
        var file = Parse("""
            view V {
                button "Go" -> navigate(OtherView);
            }
            """);

        var view = Assert.IsType<ViewDecl>(file.Declarations[0]);
        var btn = view.Body[0];
        Assert.Equal("OtherView", btn.NavTarget);
    }

    [Fact]
    public void Parses_alert_with_text_content()
    {
        var file = Parse("""
            view V {
                alert when(vm.Show) variant: warning {
                    "Warning message here.";
                }
            }
            """);

        var view = Assert.IsType<ViewDecl>(file.Declarations[0]);
        var alert = view.Body[0];
        Assert.NotNull(alert.When);
        var block = Assert.IsType<BlockBody>(alert.Body);
        var text = Assert.IsType<TextContent>(block.Children[0]);
        Assert.Equal("Warning message here.", text.Text);
    }

    // ── Universal element syntax (see also GenericSyntaxTests) ─────

    [Fact]
    public void Dotted_keys_join_into_one_prop()
    {
        var file = Parse("""
            view V {
                text "a" Grid.Row: 1 AutomationProperties.Name: "Label" ToolTipService.Placement: "Top";
            }
            """);
        var text = Assert.IsType<ViewDecl>(file.Declarations[0]).Body[0];
        Assert.Equal(["_label", "Grid.Row", "AutomationProperties.Name", "ToolTipService.Placement"], text.Props.Select(p => p.Key));
        Assert.Equal(new IntValue(1), text.Props[1].Value);
    }

    [Fact]
    public void Keyword_elements_are_not_generic_and_keep_their_type_name()
    {
        var file = Parse("""view V { textbox "Name" vm.Name; }""");
        var textbox = Assert.IsType<ViewDecl>(file.Declarations[0]).Body[0];
        Assert.False(textbox.IsGeneric);
        Assert.Null(textbox.NamespacePrefix);
        Assert.Equal("textbox", textbox.TypeName);
        Assert.Empty(textbox.Events);
        Assert.Empty(file.Uses);
    }

    [Fact]
    public void Pascal_case_key_line_in_a_block_belongs_to_the_enclosing_element()
    {
        var file = Parse("""
            view V {
                stack {
                    Margin: "0,8";
                    text "a";
                }
            }
            """);
        var stack = Assert.IsType<ViewDecl>(file.Declarations[0]).Body[0];
        Assert.Equal(new StringValue("0,8"), Assert.Single(stack.Props).Value);
        Assert.Single(Assert.IsType<BlockBody>(stack.Body).Children);
    }

    [Fact]
    public void Pascal_case_value_under_a_lowercase_key_stays_an_identifier()
    {
        var file = Parse("""view V { stack name: SearchPanel { text "a"; } }""");
        var stack = Assert.IsType<ViewDecl>(file.Declarations[0]).Body[0];
        Assert.Equal(new IdentValue("SearchPanel"), Assert.Single(stack.Props).Value);
        Assert.IsType<BlockBody>(stack.Body);
    }

    [Fact]
    public void Lowercase_key_line_in_a_block_is_still_FS1000()
    {
        var parser = new Parser(new Lexer("""view V { stack { width: 3; text "a"; } }""").Tokenize());
        parser.Parse();
        Assert.Equal("FS1000", Assert.Single(parser.Diagnostics).Code);
    }

    [Fact]
    public void Key_line_directly_in_a_property_block_is_a_parse_error()
    {
        var ex = Assert.Throws<FlagstaffParseException>(() => Parse("""
            view V { Grid { Grid.RowDefinitions: { Height: "Auto"; } } }
            """));
        Assert.Equal("FS1051", ex.Code);
        Assert.Contains("'Height:'", ex.Reason);
    }

    [Fact]
    public void Use_requires_as_and_a_lowercase_prefix()
    {
        Assert.Contains("Expected 'as'", Assert.Throws<FlagstaffParseException>(() => Parse("""use "Lib" lib;""")).Reason);
        Assert.Contains("Invalid namespace prefix", Assert.Throws<FlagstaffParseException>(() => Parse("""use "Lib" as Lib;""")).Reason);
        Assert.Contains("declared twice", Assert.Throws<FlagstaffParseException>(() => Parse("""use "A" as a; use "B" as a;""")).Reason);
    }

    [Fact]
    public void On_without_an_arrow_is_a_parse_error()
    {
        var ex = Assert.Throws<FlagstaffParseException>(() => Parse("""view V { Button on Click vm.GoCommand; }"""));
        Assert.Equal("write on Click -> vm.SaveCommand", ex.Hint);
    }
}
