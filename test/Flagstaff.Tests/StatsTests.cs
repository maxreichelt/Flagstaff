using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// <see cref="CompilationStats"/>: escape ratio, generic ratio, the <c>xaml { }</c> block list with reasons
/// (<see cref="EscapeAnalyzer"/>), and FS3011 for expressible blocks.
/// </summary>
public class StatsTests
{
    private const string View = """
        // A view with three escape blocks.
        view ChartsView {
            bind vm: ChartsViewModel;

            heading "Charts";
            Expander Header: "A";

            xaml {
                <TreeView />
            }

            xaml {
                <lvc:CartesianChart Series="{x:Bind ViewModel.Series}" />
            }

            xaml {
                <TextBlock Text="{Binding Name}" />
            }
        }
        """;

    private static CompilationResult Compile(string source, bool report = true) =>
        new FlagstaffCompiler { AppNamespace = "TestApp", ReportExpressibleEscapes = report }
            .CompileAll([new SourceFile("charts.flag", source)]);

    [Fact]
    public void Counts_lines_blocks_and_elements()
    {
        var stats = Compile(View).Stats;

        var file = Assert.Single(stats.Files);
        Assert.Equal("charts.flag", file.File);
        Assert.Equal(14, stats.FlagLines); // blank and // lines excluded
        Assert.Equal(3, stats.EscapeBlocks);
        Assert.Equal(3, stats.EscapeLines);
        Assert.Equal(3.0 / 14, stats.EscapeRatio, 6);
        Assert.Equal(1, stats.KeywordElements);
        Assert.Equal(1, stats.GenericElements);
        Assert.Equal(0.5, stats.GenericRatio, 6);
    }

    [Fact]
    public void Classifies_each_block_at_its_xaml_keyword()
    {
        var blocks = Compile(View).Stats.EscapeBlocksDetail;

        Assert.Equal(3, blocks.Count);
        Assert.Equal([8, 12, 16], blocks.Select(b => b.Line));
        Assert.Equal([EscapeReason.Expressible], blocks[0].Reasons);
        Assert.Equal(["TreeView"], blocks[0].RootTypes);
        Assert.Equal([EscapeReason.UnknownType], blocks[1].Reasons);
        Assert.Equal(["lvc:CartesianChart"], blocks[1].RootTypes);
        Assert.Equal([EscapeReason.MarkupExtension], blocks[2].Reasons);
        Assert.Contains("markup-extension: {Binding}", blocks[2].Findings);
    }

    [Fact]
    public void Fs3011_on_expressible_blocks_only_when_enabled()
    {
        var result = Compile(View);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS3011");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(new SourceLocation("charts.flag", 8, 5), d.Location);
        Assert.Contains("expressible as a generic element: TreeView", d.Message);

        Assert.DoesNotContain(Compile(View, report: false).Diagnostics, d => d.Code == "FS3011");
    }

    [Fact]
    public void Single_file_compile_has_stats_too()
    {
        var result = new FlagstaffCompiler { ReportExpressibleEscapes = true }.Compile(View, "charts.flag");
        Assert.Equal(3, result.Stats.EscapeBlocks);
        Assert.Contains(result.Diagnostics, d => d.Code == "FS3011");
    }

    [Theory]
    [InlineData("<TextBlock x:Uid=\"Title\" x:Phase=\"1\" />", "x-directive", "x-directive: x:Phase")]
    [InlineData("<Border x:Load=\"False\" />", "x-directive", "x-directive: x:Load")]
    [InlineData("<Border x:DeferLoadStrategy=\"Lazy\" />", "x-directive", "x-directive: x:DeferLoadStrategy")]
    [InlineData("<TextBox x:FieldModifier=\"public\" />", "x-directive", "x-directive: x:FieldModifier")]
    [InlineData("<Grid x:DefaultBindMode=\"OneWay\" />", "x-directive", "x-directive: x:DefaultBindMode")]
    [InlineData("<SolidColorBrush x:Key=\"A\" x:Shared=\"False\" />", "x-directive", "x-directive: x:Shared")]
    [InlineData("<Border Background=\"{TemplateBinding Background}\" />", "markup-extension", "markup-extension: {TemplateBinding}")]
    [InlineData("<Button Content=\"{x:Bind Name, Converter={CustomResource C}}\" />", "markup-extension", "markup-extension: {CustomResource}")]
    [InlineData("<local:MyControl />", "unknown-type", "unknown-type: local:MyControl")]
    [InlineData("<Grid><NotAControl /></Grid>", "unknown-type", "unknown-type: NotAControl")]
    [InlineData("<Grid><TextBlock>", "malformed", null)]
    public void Analyzer_reasons(string xaml, string reason, string? finding)
    {
        var (reasons, _, findings) = EscapeAnalyzer.Analyze(xaml);
        Assert.Contains(reason, reasons);
        Assert.DoesNotContain(EscapeReason.Expressible, reasons);
        if (finding is not null)
            Assert.Contains(finding, findings);
    }

    [Theory]
    [InlineData("<TreeView x:Name=\"FolderTree\" SelectionMode=\"Single\" />")]
    [InlineData("<Expander Header=\"Advanced\" Margin=\"0,8,0,0\">\n  <TreeView x:Name=\"FolderTree\" />\n</Expander>")]
    [InlineData("<Grid Background=\"{ThemeResource CardBrush}\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\" /></Grid.ColumnDefinitions><TextBlock Style=\"{StaticResource TitleTextBlockStyle}\" Text=\"{x:Bind ViewModel.Title, Mode=OneWay}\" /></Grid>")]
    public void Analyzer_expressible(string xaml)
    {
        var (reasons, roots, findings) = EscapeAnalyzer.Analyze(xaml);
        Assert.Equal([EscapeReason.Expressible], reasons);
        Assert.NotEmpty(roots);
        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("<Button Content=\"Go\" Click=\"OnGo\" />", "event: Click")]
    [InlineData("<TextBlock x:Uid=\"Title\" />", "prop: x:Uid -> uid:")]
    [InlineData("<Grid><Grid.Resources><SolidColorBrush x:Key=\"Accent\" Color=\"Red\" /></Grid.Resources></Grid>", "prop: x:Key -> key:")]
    [InlineData("<Border x:Load=\"{x:Bind ViewModel.ShowCard, Mode=OneWay}\" />", "prop: x:Load -> load:")]
    public void Analyzer_features_with_a_flagstaff_form_are_expressible(string xaml, string finding)
    {
        var (reasons, _, findings) = EscapeAnalyzer.Analyze(xaml);
        Assert.Equal([EscapeReason.Expressible], reasons);
        Assert.Equal([finding], findings);
    }

    [Fact]
    public void Fs3011_hint_names_on_event_and_props()
    {
        const string source = """
            view V {
                bind vm: VVm;
                xaml {
                    <Button Content="Go" x:Uid="GoButton" Click="OnGo" />
                }
            }
            """;
        var d = Assert.Single(Compile(source).Diagnostics, d => d.Code == "FS3011");
        Assert.Equal("write it as a generic element: Button Content: \"Go\" uid: \"GoButton\" on Click -> vm.GoCommand; (every type and attribute in it is "
                     + "known; ... marks what this hint leaves out); Button has a keyword: prefer button; write x:Uid -> uid:", d.Hint);
    }

    [Fact]
    public void Fs3011_hint_without_a_converted_event_keeps_the_event_note()
    {
        const string source = """
            view V {
                bind vm: VVm;
                xaml {
                    <Expander Header="A" Margin="0,8" Padding="4" Width="200" Height="40" Expanding="OnExpand" />
                }
            }
            """;
        var d = Assert.Single(Compile(source).Diagnostics, d => d.Code == "FS3011");
        Assert.StartsWith("write it as a generic element: Expander Header: \"A\" Margin: \"0,8\" Padding: \"4\" Width: 200 ...;", d.Hint);
        Assert.EndsWith("; replace Expanding=\"...\" with on Expanding -> vm.XCommand", d.Hint);
    }

    [Theory]
    [InlineData("""
        <MenuBar>
            <MenuBarItem Title="File">
                <MenuFlyoutItem Text="New note" Click="OnMenuNewNote"/>
                <MenuFlyoutItem Text="Close note" Click="OnMenuCloseNote"/>
                <MenuFlyoutSeparator/>
                <MenuFlyoutItem Text="Exit" Click="OnMenuExit"/>
            </MenuBarItem>
            <MenuBarItem Title="Help">
                <MenuFlyoutItem Text="About" Click="OnMenuAbout"/>
            </MenuBarItem>
        </MenuBar>
        """,
        """MenuBar { MenuBarItem Title: "File" { MenuFlyoutItem Text: "New note" on Click -> vm.MenuNewNoteCommand; MenuFlyoutItem Text: "Close note" on Click -> vm.MenuCloseNoteCommand; MenuFlyoutSeparator; ... } MenuBarItem Title: "Help" { MenuFlyoutItem Text: "About" on Click -> vm.MenuAboutCommand; } }""")]
    [InlineData("""
        <GridView x:Name="PhotoGrid" ItemsSource="{x:Bind ViewModel.Photos, Mode=OneWay}" SelectionMode="None">
            <GridView.ItemTemplate>
                <DataTemplate x:DataType="vm:Photo">
                    <StackPanel Spacing="4">
                        <TextBlock Text="{x:Bind Title}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    </StackPanel>
                </DataTemplate>
            </GridView.ItemTemplate>
        </GridView>
        """,
        """GridView name: PhotoGrid ItemsSource: vm.Photos SelectionMode: None of Photo { StackPanel Spacing: 4 { TextBlock Text: item.Title Foreground: theme(TextFillColorSecondaryBrush); } }""")]
    [InlineData("""
        <Grid Background="{StaticResource CardBrush}">
            <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions>
            <TextBlock Grid.Column="1">Hello</TextBlock>
        </Grid>
        """,
        """Grid Background: resource(CardBrush) { ColumnDefinitions: { ColumnDefinition Width: "*"; ColumnDefinition Width: "Auto"; } TextBlock Text: "Hello" Grid.Column: 1; }""")]
    [InlineData("""<MediaPlayerElement x:Name="Player" AreTransportControlsEnabled="False" x:Load="{x:Bind ViewModel.HasMedia, Mode=OneWay}" />""",
        """MediaPlayerElement name: Player AreTransportControlsEnabled: false load: load(vm.HasMedia);""")]
    public void Suggest_writes_the_first_root_as_a_generic_element(string xaml, string expected)
    {
        Assert.Equal([EscapeReason.Expressible], EscapeAnalyzer.Analyze(xaml).Reasons);
        Assert.Equal(expected, EscapeAnalyzer.Suggest(xaml));
    }

    [Fact]
    public void Suggestions_compile_as_flag_source()
    {
        // Every suggestion above, unabridged, is valid Flagstaff (only FS4001 keyword nudges).
        const string source = """
            view V {
                bind vm: VVm;
                MenuBar { MenuBarItem Title: "File" { MenuFlyoutItem Text: "New note" on Click -> vm.MenuNewNoteCommand; MenuFlyoutSeparator; } }
                GridView name: PhotoGrid ItemsSource: vm.Photos SelectionMode: None of Photo { StackPanel Spacing: 4 { TextBlock Text: item.Title Foreground: theme(TextFillColorSecondaryBrush); } }
                Grid Background: resource(CardBrush) { ColumnDefinitions: { ColumnDefinition Width: "*"; ColumnDefinition Width: "Auto"; } TextBlock Text: "Hello" Grid.Column: 1; }
                MediaPlayerElement name: Player AreTransportControlsEnabled: false load: load(vm.HasMedia);
            }
            """;
        var result = Compile(source);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.All(result.Diagnostics, d => Assert.True(d.Code is "FS4001" or "FS3010", d.ToString()));
    }

    [Fact]
    public void Fs3011_is_on_by_default()
    {
        Assert.True(new FlagstaffCompiler().ReportExpressibleEscapes);
        var result = new FlagstaffCompiler().Compile(View, "charts.flag");
        Assert.Contains(result.Diagnostics, d => d.Code == "FS3011");
    }

    [Fact]
    public void Multiple_reasons_are_all_reported()
    {
        var (reasons, roots, _) = EscapeAnalyzer.Analyze(
            "<Button x:Load=\"False\" Click=\"OnClick\" Content=\"{Binding X}\" />\n<lvc:Chart />");
        Assert.Equal([EscapeReason.UnknownType, EscapeReason.MarkupExtension, EscapeReason.XDirective], reasons);
        Assert.Equal(["Button", "lvc:Chart"], roots);
    }

    [Fact]
    public void Unparsed_file_still_counts_lines()
    {
        var stats = Compile("view V {\n  button \"x\" -> \n").Stats;
        Assert.Equal(2, stats.FlagLines);
        Assert.Equal(0, stats.KeywordElements);
    }

    [Fact]
    public void Project_totals_sum_files()
    {
        var stats = new FlagstaffCompiler().CompileAll(
        [
            new SourceFile("a.flag", "view A {\n    text \"a\";\n}\n"),
            new SourceFile("b.flag", View),
        ]).Stats;
        Assert.Equal(2, stats.Files.Count);
        Assert.Equal(17, stats.FlagLines);
        Assert.Equal(2, stats.KeywordElements);
        Assert.Equal(3, stats.EscapeBlocks);
    }
}
