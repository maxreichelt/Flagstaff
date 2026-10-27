using System.Text;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Syntax foundations for the generic tier (any WinUI type in the element form): generic elements, dotted keys,
/// property elements, value forms, <c>on</c> clauses and <c>use</c>. These tests pin the AST; GenericTests and LocalityTests cover validation and emission.
/// </summary>
public class GenericSyntaxTests
{
    private static FlagFile Parse(string source)
    {
        var parser = new Parser(new Lexer(source).Tokenize());
        var file = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        return file;
    }

    private static ViewDecl ParseView(string source) => Assert.IsType<ViewDecl>(Parse(source).Declarations[0]);

    private static CompilationResult Compile(string source) =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile(source, "test.flag");

    private static string Xaml(CompilationResult result, string name = "V.xaml") =>
        Assert.Single(result.Files, f => f.Name == name).Content;

    private static List<Element> Children(Element el) =>
        Assert.IsType<BlockBody>(el.Body).Children.OfType<ChildElement>().Select(c => c.Element).ToList();

    private static PropValue PropOf(Element el, string key) =>
        Assert.Single(el.Props, p => p.Key == key).Value;

    // ── generic elements ───────────────────────────────────────────

    private const string ExpanderAndTabView = """
        view SettingsView {
            bind vm: SettingsViewModel;

            Expander Header: "Advanced" IsExpanded: vm.ShowAdvanced {
                toggle "Telemetry" vm.Telemetry;
                Slider Header: "Cache size (MB)" Minimum: 64 Maximum: 2048 Value: vm.CacheMb;
            }

            TabView {
                TabViewItem Header: "General" IsClosable: false {
                    textbox "Display name" vm.DisplayName;
                }
                TabViewItem Header: "Account" IsClosable: false {
                    button "Sign out" -> vm.SignOutCommand variant: danger;
                }
            }
        }
        """;

    [Fact]
    public void Expander_parses_as_generic_element()
    {
        var view = ParseView(ExpanderAndTabView);
        var expander = view.Body[0];

        Assert.True(expander.IsGeneric);
        Assert.Equal("Expander", expander.Keyword);
        Assert.Equal("Expander", expander.TypeName);
        Assert.Null(expander.NamespacePrefix);
        Assert.Equal(2, expander.Props.Count);
        Assert.Equal(new StringValue("Advanced"), PropOf(expander, "Header"));
        Assert.Equal(new BindingValue("ShowAdvanced"), PropOf(expander, "IsExpanded"));

        var children = Children(expander);
        Assert.Equal(2, children.Count);
        Assert.Equal("toggle", children[0].Keyword);
        Assert.False(children[0].IsGeneric);
        Assert.Equal("toggle", children[0].TypeName);

        var slider = children[1];
        Assert.True(slider.IsGeneric);
        Assert.Equal(["Header", "Minimum", "Maximum", "Value"], slider.Props.Select(p => p.Key));
        Assert.Equal(new IntValue(64), PropOf(slider, "Minimum"));
        Assert.Equal(new BindingValue("CacheMb"), PropOf(slider, "Value"));
        Assert.IsType<EmptyBody>(slider.Body);
    }

    [Fact]
    public void TabView_has_two_items()
    {
        var tabView = ParseView(ExpanderAndTabView).Body[1];
        Assert.True(tabView.IsGeneric);
        Assert.Empty(tabView.Props);

        var items = Children(tabView);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("TabViewItem", i.Keyword));
        Assert.Equal(new StringValue("Account"), PropOf(items[1], "Header"));
        Assert.Equal(new BoolValue(false), PropOf(items[1], "IsClosable"));
        var button = Assert.Single(Children(items[1]));
        Assert.Equal("vm.SignOutCommand", button.NavTarget);
    }

    [Fact]
    public void Expander_and_TabView_compile_clean_and_emit_generic_elements()
    {
        var result = Compile(ExpanderAndTabView.Replace("SettingsView", "V"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var xaml = Xaml(result);
        Assert.Contains("<Expander Header=\"Advanced\"", xaml);
        Assert.Contains("<TabView>", xaml);
    }

    [Fact]
    public void String_children_of_generic_elements_are_text_content()
    {
        var el = ParseView("""view V { Button { "Save"; } }""").Body[0];
        Assert.Equal(new TextContent("Save"), Assert.Single(Assert.IsType<BlockBody>(el.Body).Children));
    }

    [Fact]
    public void Generic_element_takes_label_binding_and_clauses()
    {
        var el = ParseView("""
            view V {
                InfoBar "Heads up" vm.Message IsOpen: true when(vm.HasMessage) enabled(vm.CanAct) -> vm.DismissCommand;
            }
            """).Body[0];
        Assert.True(el.IsGeneric);
        Assert.Equal(new StringValue("Heads up"), PropOf(el, "_label"));
        Assert.Equal(new BindingValue("Message"), PropOf(el, "_binding"));
        Assert.NotNull(el.When);
        Assert.NotNull(el.Enabled);
        Assert.Equal("vm.DismissCommand", el.NavTarget);
    }

    // ── property elements and pass-through on keyword elements ─────

    private const string ButtonBodyForm = """
        view V {
            bind vm: VModel;
            button "More" Margin: "0,8,0,0" AutomationProperties.Name: "More actions" {
                Flyout: MenuFlyout {
                    MenuFlyoutItem Text: "Duplicate" -> vm.DuplicateCommand;
                    MenuFlyoutItem Text: "Archive"   -> vm.ArchiveCommand enabled(vm.CanArchive);
                }
            }
        }
        """;

    private const string ButtonHeaderForm = """
        view V {
            bind vm: VModel;
            button "More" Margin: "0,8,0,0" AutomationProperties.Name: "More actions" Flyout: MenuFlyout {
                MenuFlyoutItem Text: "Duplicate" -> vm.DuplicateCommand;
                MenuFlyoutItem Text: "Archive"   -> vm.ArchiveCommand enabled(vm.CanArchive);
            };
        }
        """;

    [Fact]
    public void Button_flyout_is_an_element_value()
    {
        var button = ParseView(ButtonBodyForm).Body[0];

        Assert.False(button.IsGeneric);
        Assert.Equal(["_label", "Margin", "AutomationProperties.Name", "Flyout"], button.Props.Select(p => p.Key));
        Assert.Equal(new StringValue("More actions"), PropOf(button, "AutomationProperties.Name"));
        Assert.IsType<EmptyBody>(button.Body); // a block of only property lines is no children

        var flyout = Assert.IsType<ElementValue>(PropOf(button, "Flyout")).Element;
        Assert.True(flyout.IsGeneric);
        Assert.Equal("MenuFlyout", flyout.TypeName);
        var items = Children(flyout);
        Assert.Equal(2, items.Count);
        Assert.Equal("vm.DuplicateCommand", items[0].NavTarget);
        Assert.Equal(new StringValue("Duplicate"), PropOf(items[0], "Text"));
        Assert.NotNull(items[1].Enabled);
    }

    [Fact]
    public void Property_line_in_body_and_header_produce_the_same_ast()
    {
        var body = ParseView(ButtonBodyForm).Body[0];
        var header = ParseView(ButtonHeaderForm).Body[0];
        Assert.Equal(Dump(header), Dump(body));
    }

    [Fact]
    public void Button_with_only_a_flyout_line_validates_clean()
    {
        var result = Compile(ButtonBodyForm);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Table_context_flyout_with_item_argument()
    {
        var table = ParseView("""
            view V {
                bind vm: VModel;
                table items: vm.Orders of Order {
                    col "Client" bind: item.Client;
                    ContextFlyout: MenuFlyout {
                        MenuFlyoutItem Text: "Open" -> vm.OpenCommand(item);
                    }
                }
            }
            """).Body[0];

        Assert.Equal("Order", table.OfType);
        var col = Assert.Single(Children(table));
        Assert.Equal("col", col.Keyword);
        var flyout = Assert.IsType<ElementValue>(PropOf(table, "ContextFlyout")).Element;
        var item = Assert.Single(Children(flyout));
        Assert.Equal("vm.OpenCommand", item.NavTarget);
        Assert.Equal("item", item.NavTargetArg);
    }

    [Fact]
    public void Header_element_value_ends_with_its_own_semicolon()
    {
        var el = ParseView("""
            view V {
                AppBarButton Icon: SymbolIcon Symbol: "Save"; Label: "Save";
            }
            """).Body[0];
        // `Icon: SymbolIcon Symbol: "Save";` ends the value element at its ';'; AppBarButton continues.
        Assert.Equal(["Icon", "Label"], el.Props.Select(p => p.Key));
        var icon = Assert.IsType<ElementValue>(PropOf(el, "Icon")).Element;
        Assert.Equal(new StringValue("Save"), PropOf(icon, "Symbol"));
    }

    [Fact]
    public void View_level_resources_are_property_elements()
    {
        var view = ParseView("""
            view V {
                Page.Resources: {
                    Style TargetType: Button {
                        Setter Property: "Padding" Value: "12,8";
                    }
                }
                text "Hello";
            }
            """);

        var resources = Assert.Single(view.PropertyElements);
        Assert.Equal("Page.Resources", resources.Key);
        var style = Assert.Single(Assert.IsType<ElementsValue>(resources.Value).Elements);
        Assert.Equal("Style", style.Keyword);
        Assert.Equal(new IdentValue("Button"), PropOf(style, "TargetType")); // *Type keys take a type name
        var setter = Assert.Single(Children(style));
        Assert.Equal(new StringValue("Padding"), PropOf(setter, "Property"));
        Assert.Equal(new StringValue("12,8"), PropOf(setter, "Value"));
        Assert.Single(view.Body);
    }

    [Fact]
    public void Collection_property_element_on_a_generic_element()
    {
        var grid = ParseView("""
            view V {
                Grid {
                    VisualStateManager.VisualStateGroups: {
                        VisualStateGroup {
                            VisualState name: Narrow {
                                Setter Target: ref(Nav).DisplayMode Value: "Minimal";
                            }
                        }
                    }
                    NavigationView name: Nav;
                }
            }
            """).Body[0];

        var groups = Assert.IsType<ElementsValue>(PropOf(grid, "VisualStateManager.VisualStateGroups"));
        Assert.Equal("VisualStateGroup", Assert.Single(groups.Elements).Keyword);
        Assert.Equal("NavigationView", Assert.Single(Children(grid)).Keyword);
    }

    // ── use / namespaces ───────────────────────────────────────────

    private const string ChartSource = """
        use "LiveChartsCore.SkiaSharpView.WinUI" as lvc;

        view V {
            bind vm: VModel;
            lvc.CartesianChart Series: vm.Series Height: 400;
        }
        """;

    [Fact]
    public void Use_declares_a_prefix_for_namespaced_elements()
    {
        var file = Parse(ChartSource);
        var use = Assert.Single(file.Uses);
        Assert.Equal("LiveChartsCore.SkiaSharpView.WinUI", use.Assembly);
        Assert.Equal("lvc", use.Prefix);

        var chart = Assert.IsType<ViewDecl>(file.Declarations[0]).Body[0];
        Assert.True(chart.IsGeneric);
        Assert.Equal("lvc", chart.NamespacePrefix);
        Assert.Equal("CartesianChart", chart.TypeName);
        Assert.Equal("lvc.CartesianChart", chart.Keyword);
        Assert.Equal(new BindingValue("Series"), PropOf(chart, "Series"));
        Assert.Equal(new IntValue(400), PropOf(chart, "Height"));
    }

    [Fact]
    public void Use_emits_an_xmlns_and_the_prefixed_element()
    {
        var result = Compile(ChartSource);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var xaml = Xaml(result);
        Assert.Contains("xmlns:lvc=\"using:LiveChartsCore.SkiaSharpView.WinUI\"", xaml);
        Assert.Contains("<lvc:CartesianChart ", xaml);
    }

    [Fact]
    public void Undeclared_prefix_is_FS1036()
    {
        var result = Compile("""
            view V {
                lvc.CartesianChart Height: 400;
            }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1036");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("'lvc'", d.Message);
        Assert.Equal("add use \"<assembly>\" as lvc; at the top of the file", d.Hint);
        Assert.Equal(new SourceLocation("test.flag", 2, 5), d.Location);
    }

    [Fact]
    public void Use_after_a_view_is_a_parse_error()
    {
        var result = Compile("""
            view V { text "a"; }
            use "Lib" as lib;
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1051");
        Assert.Contains("'use' declarations must come before", d.Message);
        Assert.NotNull(d.Hint);
    }

    [Fact]
    public void Use_inside_a_view_is_a_parse_error()
    {
        var result = Compile("""
            view V {
                use "Lib" as lib;
            }
            """);
        Assert.Single(result.Diagnostics, d => d.Code == "FS1051" && d.Message.Contains("'use'"));
    }

    // ── on clauses ─────────────────────────────────────────────────

    [Fact]
    public void On_clauses_parse_on_generic_and_keyword_elements()
    {
        var view = ParseView("""
            view V {
                ListView ItemsSource: vm.Items on SelectionChanged -> vm.SelectCommand on DoubleTapped -> navigate(DetailView);
                textbox "Search" vm.Query on TextChanged -> vm.SearchCommand;
                table items: vm.Rows of Row {
                    col "A" bind: item.A;
                    Button Content: "Go" on Click -> vm.GoCommand(item);
                }
            }
            """);

        var list = view.Body[0];
        Assert.Equal(2, list.Events.Count);
        Assert.Equal(new EventBinding("SelectionChanged", "vm.SelectCommand", null, list.Events[0].Location), list.Events[0]);
        Assert.Equal("DetailView", list.Events[1].Target);
        Assert.Equal(new BindingValue("Items"), PropOf(list, "ItemsSource"));

        var textbox = view.Body[1];
        Assert.False(textbox.IsGeneric);
        Assert.Equal("TextChanged", Assert.Single(textbox.Events).EventName);

        var go = Children(view.Body[2])[1];
        var click = Assert.Single(go.Events);
        Assert.Equal("item", click.Arg);
        Assert.Null(go.NavTarget);
    }

    // ── value forms ────────────────────────────────────────────────

    [Fact]
    public void Value_forms_ref_theme_resource_load()
    {
        var view = ParseView("""
            view V {
                Setter Target: ref(Nav).DisplayMode Value: "Expanded";
                Border Background: theme(CardBackgroundFillColorDefaultBrush) Style: resource(CardStyle) Tag: ref(Panel) Width: ref(Nav).Pane.Width;
                StackPanel Load: load(vm.ShowDetails);
            }
            """);

        Assert.Equal(new RefValue("Nav", "DisplayMode"), PropOf(view.Body[0], "Target"));
        Assert.Equal(new StringValue("Expanded"), PropOf(view.Body[0], "Value"));
        var border = view.Body[1];
        Assert.Equal(new ThemeResourceValue("CardBackgroundFillColorDefaultBrush"), PropOf(border, "Background"));
        Assert.Equal(new StaticResourceValue("CardStyle"), PropOf(border, "Style"));
        Assert.Equal(new RefValue("Panel", null), PropOf(border, "Tag"));
        Assert.Equal(new RefValue("Nav", "Pane.Width"), PropOf(border, "Width"));
        Assert.Equal(new LoadValue("ShowDetails"), PropOf(view.Body[2], "Load"));
    }

    // ── names ──────────────────────────────────────────────────────

    [Fact]
    public void Named_elements_are_collected_across_the_view()
    {
        var view = ParseView("""
            view V {
                Page.Resources: { Storyboard name: FadeIn; }
                NavigationView name: Nav {
                    stack name: Panel { text "x"; }
                }
                button "More" Flyout: MenuFlyout name: MoreMenu { MenuFlyoutItem Text: "A"; };
            }
            """);
        Assert.Equal(["FadeIn", "Nav", "Panel", "MoreMenu"], view.NamedElements.Select(n => n.Name));
        Assert.Equal("MenuFlyout", view.NamedElements[3].Owner.Keyword);
    }

    [Fact]
    public void Duplicate_name_is_FS1042()
    {
        var result = Compile("""
            view V {
                stack name: Panel { text "a"; }
                button "More" -> vm.GoCommand { Flyout: MenuFlyout name: Panel; }
            }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1042");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("'Panel'", d.Message);
        Assert.Contains("line 2", d.Message);
        Assert.Equal(3, d.Location.Line);
    }

    // ── prop validation ────────────────────────────────────────────

    [Fact]
    public void Generic_elements_validate_lowercase_props_only()
    {
        var ok = Compile("""
            view V {
                Expander Header: "x" Whatever.Attached: 3 uid: "Adv" when(vm.Show) { text "a"; }
            }
            """);
        Assert.DoesNotContain(ok.Diagnostics, d => d.Code is "FS1000" or "FS1001" or "FS1002" or "FS1004");

        var bad = Compile("""
            view V {
                Expander widht: 200;
            }
            """);
        var d = Assert.Single(bad.Diagnostics, d => d.Code == "FS1001");
        Assert.Equal("did you mean 'width'?", d.Hint);
    }

    [Fact]
    public void Keyword_children_of_generic_elements_are_validated()
    {
        var result = Compile("""
            view V {
                Expander { textbox "Name" vm.Name colz: 2; }
                button "x" -> vm.GoCommand Flyout: MenuFlyout { stat "S" vm.S variant: wrong; };
            }
            """);
        Assert.Single(result.Diagnostics, d => d.Code == "FS1001");
        Assert.Single(result.Diagnostics, d => d.Code == "FS1003");
    }

    [Fact]
    public void Keyword_elements_accept_pascal_case_keys_unchecked()
    {
        var result = Compile("""
            view V {
                stat "Total" vm.Total Margin: "0,8" AutomationProperties.Name: "Total";
            }
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Element_value_under_a_lowercase_key_is_FS1005()
    {
        var loc = new SourceLocation("test.flag", 1, 1);
        var flyout = new Element("MenuFlyout", [], new EmptyBody(), null, null, null, null, loc) { IsGeneric = true };
        var el = new Element("text", [new PropPair("tooltip", new ElementValue(flyout), loc)], new EmptyBody(), null, null, null, null, loc);
        var d = Assert.Single(SemanticValidator.Validate(new FlagFile([new ViewDecl("V", null, null, [el], loc)])));
        Assert.Equal("FS1005", d.Code);
    }

    // ── generation ─────────────────────────────────────────────────

    [Fact]
    public void Pass_through_props_on_keyword_elements_only_add_attributes()
    {
        var plain = Compile("""
            view V {
                bind vm: VModel;
                button "Save" -> vm.SaveCommand;
                textbox "Name" vm.Name;
            }
            """);
        var withMargin = Compile("""
            view V {
                bind vm: VModel;
                button "Save" -> vm.SaveCommand Margin: "0,8,0,0" AutomationProperties.Name: "Save" on PointerEntered -> vm.HoverCommand;
                textbox "Name" vm.Name Margin: "0,8";
            }
            """);
        Assert.DoesNotContain(withMargin.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        // Pass-through props and the `on` handler land on the root tag (PassThroughTests, EventTests).
        var stripped = Xaml(withMargin)
            .Replace(" Margin=\"0,8,0,0\" AutomationProperties.Name=\"Save\" PointerEntered=\"OnEvent_1\"", "")
            .Replace(" Margin=\"0,8\"", "");
        Assert.Equal(Xaml(plain), stripped);
    }

    [Fact]
    public void Uid_emits_x_uid()
    {
        var result = Compile("""
            view V {
                bind vm: VModel;
                button "Save" -> vm.SaveCommand uid: "SaveButton";
            }
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("x:Uid=\"SaveButton\"", Xaml(result));
    }

    // ── recovery ───────────────────────────────────────────────────

    [Fact]
    public void Pascal_case_identifier_followed_by_colon_is_a_key_not_an_element()
    {
        var view = ParseView("""
            view V {
                button "Save" -> vm.SaveCommand
                    AutomationProperties.Name: "Save"
                    Margin: "0,8";
                text "next";
            }
            """);
        Assert.Equal(2, view.Body.Count);
        Assert.Equal(["_label", "AutomationProperties.Name", "Margin"], view.Body[0].Props.Select(p => p.Key));
    }

    [Fact]
    public void Missing_semicolon_before_a_generic_element_is_reported()
    {
        var result = Compile("""
            view V {
                text "a"
                Expander Header: "x";
            }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1051");
        Assert.Contains("after 'text'", d.Message);
        Assert.Equal(3, d.Location.Line);
    }

    // ── bare names, trailing element values, on lines, layout ──

    [Fact]
    public void Bare_pascal_case_value_is_an_identifier()
    {
        var view = ParseView("""
            use "Lib" as lib;
            view V {
                StackPanel Orientation: Horizontal;
                Rectangle Fill: Red when(vm.Show) Stroke: Blue -> vm.GoCommand;
                Style TargetType: Button;
                Border Tag: lib.Thing { text "a"; };
                Grid { Background: Transparent; }
            }
            """);
        Assert.Equal(new IdentValue("Horizontal"), PropOf(view.Body[0], "Orientation"));
        Assert.Equal(new IdentValue("Red"), PropOf(view.Body[1], "Fill"));
        Assert.Equal(new IdentValue("Blue"), PropOf(view.Body[1], "Stroke"));
        Assert.NotNull(view.Body[1].When);
        Assert.Equal("vm.GoCommand", view.Body[1].NavTarget);
        Assert.Equal(new IdentValue("Button"), PropOf(view.Body[2], "TargetType"));
        // lib.Thing followed by '{' is an element value that owns the block, so Border has no children.
        Assert.IsType<ElementValue>(PropOf(view.Body[3], "Tag"));
        Assert.Equal(new IdentValue("Transparent"), PropOf(view.Body[4], "Background"));
        Assert.Equal(5, view.Body.Count);
    }

    [Fact]
    public void Bare_prefixed_name_is_an_identifier()
    {
        var view = ParseView("""
            use "Lib" as lib;
            view V { Border Tag: lib.Thing; }
            """);
        Assert.Equal(new IdentValue("lib.Thing"), PropOf(view.Body[0], "Tag"));
    }

    [Fact]
    public void Header_trailing_element_value_ends_the_owner()
    {
        var view = ParseView("""
            view V {
                button "Save" -> vm.SaveCommand Icon: SymbolIcon Symbol: "Save";
                text "next";
                Border Child: Grid Child: SymbolIcon Symbol: "Add";
                text "last";
            }
            """);
        Assert.Equal(["button", "text", "Border", "text"], view.Body.Select(e => e.Keyword));
        var button = view.Body[0];
        Assert.Equal("vm.SaveCommand", button.NavTarget);
        var icon = Assert.IsType<ElementValue>(PropOf(button, "Icon")).Element;
        Assert.Equal(new StringValue("Save"), PropOf(icon, "Symbol"));
        Assert.IsType<EmptyBody>(button.Body);
        var grid = Assert.IsType<ElementValue>(PropOf(view.Body[2], "Child")).Element;
        Assert.IsType<ElementValue>(PropOf(grid, "Child"));
    }

    [Fact]
    public void Header_continues_after_an_element_value_when_a_key_or_clause_follows()
    {
        var view = ParseView("""
            view V {
                AppBarButton Icon: SymbolIcon Symbol: "Save"; Label: "Save" on Click -> vm.SaveCommand;
                button "x" Icon: SymbolIcon Symbol: "Add";;
                text "next";
            }
            """);
        Assert.Equal(["AppBarButton", "button", "text"], view.Body.Select(e => e.Keyword));
        Assert.Equal(["Icon", "Label"], view.Body[0].Props.Select(p => p.Key));
        Assert.Equal("Click", Assert.Single(view.Body[0].Events).EventName);
    }

    [Fact]
    public void Arrow_after_an_element_value_attaches_to_the_value_and_FS1023_says_so()
    {
        var result = Compile("""
            view V {
                bind vm: VModel;
                button "Save" Icon: SymbolIcon Symbol: "Save" -> vm.SaveCommand;
            }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1023");
        Assert.Contains("belongs to the SymbolIcon value", d.Hint);
    }

    [Fact]
    public void Missing_semicolon_after_a_block_element_value_gets_a_hint()
    {
        var result = Compile("""
            view V {
                button "x" -> vm.GoCommand Flyout: MenuFlyout { MenuFlyoutItem Text: "A"; }
                text "next";
            }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1051");
        Assert.Contains("end 'button' with ';'", d.Hint);
    }

    [Fact]
    public void On_lines_in_a_block_belong_to_the_enclosing_element()
    {
        var body = ParseView("""
            view V {
                button "X" { on Click -> vm.XCommand; }
                ListView ItemsSource: vm.Items {
                    on SelectionChanged -> vm.SelectCommand;
                    TextBlock Text: "header";
                }
            }
            """);
        var header = ParseView("""
            view V {
                button "X" on Click -> vm.XCommand;
            }
            """);
        Assert.Equal(Dump(header.Body[0]), Dump(body.Body[0]));
        Assert.IsType<EmptyBody>(body.Body[0].Body);
        var list = body.Body[1];
        Assert.Equal("SelectionChanged", Assert.Single(list.Events).EventName);
        Assert.Single(Children(list));
    }

    [Fact]
    public void Uid_on_a_keyword_without_common_attrs_is_FS1002()
    {
        var result = Compile("""
            view V {
                bind vm: VModel;
                table items: vm.Rows of Row uid: "Rows" { col "A" bind: item.A; }
            }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1002");
        Assert.Contains("'uid'", d.Message);
    }

    [Fact]
    public void Universal_emitter_props_on_generic_elements_are_accepted()
    {
        var result = Compile("""
            view V {
                bind vm: VModel;
                Expander name: Adv align: center tooltip: "More" uid: "Adv" enabled(vm.CanExpand) { text "a"; }
            }
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "FS1002" or "FS1012");
    }

    // ── AST dump (location-free, for structural equality) ──────────

    private static string Dump(Element el)
    {
        var sb = new StringBuilder();
        DumpElement(el, sb);
        return sb.ToString();
    }

    private static void DumpElement(Element el, StringBuilder sb)
    {
        sb.Append(el.Keyword).Append(el.IsGeneric ? "[g]" : "").Append('(');
        foreach (var p in el.Props.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(p.Key).Append('=');
            switch (p.Value)
            {
                case ElementValue ev: DumpElement(ev.Element, sb); break;
                case ElementsValue evs: evs.Elements.ForEach(e => DumpElement(e, sb)); break;
                default: sb.Append(p.Value); break;
            }
            sb.Append(';');
        }
        sb.Append(") nav=").Append(el.NavTarget).Append(" enabled=").Append(el.Enabled is null ? "-" : "+");
        foreach (var e in el.Events)
            sb.Append(" on ").Append(e.EventName).Append("->").Append(e.Target);
        switch (el.Body)
        {
            case EmptyBody: sb.Append(';'); break;
            case BlockBody b:
                sb.Append('{');
                foreach (var c in b.Children)
                {
                    if (c is ChildElement ce) DumpElement(ce.Element, sb);
                    else sb.Append(c);
                }
                sb.Append('}');
                break;
        }
    }
}
