using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Flagstaff.Compiler;

/// <summary>
/// Post-generation XAML linter. Two passes:
/// 1. XML well-formedness via XmlReader (catches malformed XML from transpiled output and xaml {} escapes)
/// 2. Structural validation via XDocument (targeted at known WinUI 3 failure patterns)
/// </summary>
public static class XamlLinter
{
    private static readonly XNamespace Xmlns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> KnownResources = new(StringComparer.Ordinal)
    {
        "FlagstaffAccentColor", "FlagstaffAccentBrush", "FlagstaffFontFamily",
        "FlagstaffToolbar", "FlagstaffCard", "FlagstaffStatCard",
        "FlagstaffFilePickerButton", "FlagstaffTableHeader", "FlagstaffDialogOverlay",
        "BoolToVisibility", "CurrencyConverter", "PercentConverter", "DateConverter",
        "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
        "SubtitleTextBlockStyle", "BodyStrongTextBlockStyle", "CaptionTextBlockStyle",
        "TitleTextBlockStyle", "ApplicationPageBackgroundThemeBrush",
        // Overlays (Styles.xaml region: overlays) and the WinUI accent button style used by confirm.
        "FlagstaffDialogBackdrop", "FlagstaffDialogCard", "AccentButtonStyle",
        "NumberConverter", "FlagstaffTableRow", "FlagstaffTableItemContainer", "FlagstaffListItemContainer",
        "FlagstaffEmptyState",
        "FlagstaffDangerButton", "DefaultButtonStyle",
        "FlagstaffStatCardSuccess", "FlagstaffStatCardWarning", "FlagstaffStatCardDanger", "FlagstaffStatCardInfo",
    };

    /// <summary>
    /// True when <paramref name="key"/> is a StaticResource key any generated page may reference: a key of the stdlib
    /// <c>Styles.xaml</c>, of the generated <c>App.xaml</c> (converters, theme accent), or a WinUI built-in style.
    /// Keys a page declares itself (<c>Page.Resources: { Style key: X ... }</c> → <c>x:Key="X"</c>) are known to that page
    /// only (<see cref="DeclaredKeys"/>).
    /// </summary>
    public static bool IsKnownResource(string key) => KnownResources.Contains(key);

    private static readonly Regex StaticResourceRegex = new(
        @"\{StaticResource\s+(\w+)\}", RegexOptions.Compiled);

    public static List<Diagnostic> Lint(string fileName, string xamlContent, string expectedNamespace)
    {
        var diagnostics = new List<Diagnostic>();
        var loc = new SourceLocation(fileName, 0, 0);

        if (!CheckWellFormedness(fileName, xamlContent, diagnostics))
            return diagnostics;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xamlContent, LoadOptions.SetLineInfo);
        }
        catch
        {
            return diagnostics;
        }

        CheckFontIconUsage(doc, fileName, diagnostics);
        CheckInvalidSymbolValues(doc, fileName, diagnostics);
        CheckGridColumnOverflow(doc, fileName, diagnostics);
        CheckDataTemplateBindings(doc, fileName, diagnostics);
        CheckDataTemplateMissingDataType(doc, fileName, diagnostics);
        CheckWindowResources(doc, fileName, diagnostics);
        CheckDuplicateXName(doc, fileName, diagnostics);
        CheckLoadHasName(doc, fileName, diagnostics);
        CheckXClassNamespace(doc, fileName, expectedNamespace, diagnostics);
        CheckStaticResources(xamlContent, fileName, DeclaredKeys(doc), diagnostics);

        return diagnostics;
    }

    /// <summary>
    /// FS3011: a <c>xaml { }</c> block that <see cref="EscapeAnalyzer"/> classified as
    /// <see cref="EscapeReason.Expressible"/>. Reported at the <c>xaml</c> keyword in the .flag file (not the generated
    /// XAML), from the compiler's escape-block list rather than the generated page.
    /// </summary>
    public static List<Diagnostic> LintEscapeBlocks(IEnumerable<EscapeBlockStats> blocks)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var block in blocks.Where(b => b.IsExpressible))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "FS3011",
                $"this xaml {{ }} block is expressible as a generic element: {string.Join(", ", block.RootTypes)}",
                new SourceLocation(block.File, block.Line, block.Column),
                EscapeHint(block)));
        }
        return diagnostics;
    }

    /// <summary>
    /// The FS3011 hint: the block's first root written as a generic element (<see cref="EscapeBlockStats.Suggestion"/>),
    /// the keyword when the type has one, plus <c>on Event -></c> for handler attributes and the prop for
    /// <c>x:Uid</c>/<c>x:Key</c>/<c>x:Load</c> (from the block's <c>event:</c>/<c>prop:</c> findings).
    /// </summary>
    private static string EscapeHint(EscapeBlockStats block)
    {
        var hint = block.Suggestion is { } suggestion
            ? $"write it as a generic element: {suggestion} (every type and attribute in it is known; ... marks what this hint leaves out)"
            : $"write it as a generic element ({block.RootTypes[0]} Prop: value {{ ... }}); every type and attribute in it is known";
        if (ControlManifest.Default.TryGetType(block.RootTypes[0], out var rootType) && ControlManifest.Default.KeywordFor(rootType) is { } keyword)
            hint += $"; {block.RootTypes[0]} has a keyword: prefer {keyword}";
        var events = block.Findings.Where(f => f.StartsWith(EscapeAnalyzer.EventFinding + ": ", StringComparison.Ordinal))
            .Select(f => f[(EscapeAnalyzer.EventFinding.Length + 2)..])
            .Where(e => block.Suggestion?.Contains($"on {e} ->", StringComparison.Ordinal) != true).ToList();
        if (events.Count > 0)
            hint += "; replace " + string.Join(", ", events.Select(e => $"{e}=\"...\" with on {e} -> vm.XCommand"));
        var props = block.Findings.Where(f => f.StartsWith(EscapeAnalyzer.PropFinding + ": ", StringComparison.Ordinal))
            .Select(f => f[(EscapeAnalyzer.PropFinding.Length + 2)..]).ToList();
        if (props.Count > 0)
            hint += "; write " + string.Join(", ", props);
        return hint;
    }

    private static bool CheckWellFormedness(string fileName, string xamlContent, List<Diagnostic> diagnostics)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xamlContent));
            while (reader.Read()) { }
            return true;
        }
        catch (XmlException ex)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "FS3001",
                $"Malformed XML: {ex.Message}",
                new SourceLocation(fileName, ex.LineNumber, ex.LinePosition)));
            return false;
        }
    }

    private static void CheckFontIconUsage(XDocument doc, string fileName, List<Diagnostic> diagnostics)
    {
        // FontIcon exists in WinUI 3; what renders nothing is a FontIcon without a Glyph.
        foreach (var el in doc.Descendants(Xmlns + "FontIcon"))
        {
            if (el.Attribute("Glyph") is not null)
                continue;
            var info = (IXmlLineInfo)el;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "FS3002",
                "<FontIcon> has no Glyph; it renders nothing",
                new SourceLocation(fileName, info.LineNumber, info.LinePosition),
                "write FontIcon Glyph: \"\uE710\"; or use SymbolIcon Symbol: Add; for a named Symbol"));
        }
    }

    private static void CheckInvalidSymbolValues(XDocument doc, string fileName, List<Diagnostic> diagnostics)
    {
        foreach (var el in doc.Descendants())
        {
            var symbolAttr = el.Attribute("Symbol");
            if (symbolAttr is not null && !XamlGenerator.ValidSymbols.Contains(symbolAttr.Value))
            {
                var info = (IXmlLineInfo)el;
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "FS3003",
                    $"Symbol=\"{symbolAttr.Value}\" is not a known WinUI 3 Symbol enum value",
                    new SourceLocation(fileName, info.LineNumber, info.LinePosition)));
            }

            var iconAttr = el.Attribute("Icon");
            if (iconAttr is not null
                && !iconAttr.Value.Contains("{")
                && el.Name.LocalName == "NavigationViewItem"
                && !XamlGenerator.ValidSymbols.Contains(iconAttr.Value))
            {
                var info = (IXmlLineInfo)el;
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "FS3003",
                    $"Icon=\"{iconAttr.Value}\" is not a known WinUI 3 Symbol enum value",
                    new SourceLocation(fileName, info.LineNumber, info.LinePosition)));
            }
        }
    }

    private static void CheckGridColumnOverflow(XDocument doc, string fileName, List<Diagnostic> diagnostics)
    {
        foreach (var grid in doc.Descendants(Xmlns + "Grid"))
        {
            var colDefs = grid.Element(Xmlns + "Grid.ColumnDefinitions");
            if (colDefs is null) continue;
            int colCount = colDefs.Elements(Xmlns + "ColumnDefinition").Count();

            foreach (var child in grid.Elements())
            {
                var colAttr = child.Attribute(Xmlns + "Grid.Column")
                              ?? child.Attribute("Grid.Column");
                if (colAttr is null) continue;
                if (int.TryParse(colAttr.Value, out int colIndex) && colIndex >= colCount)
                {
                    var info = (IXmlLineInfo)child;
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "FS3004",
                        $"Grid.Column=\"{colIndex}\" exceeds column count ({colCount})",
                        new SourceLocation(fileName, info.LineNumber, info.LinePosition)));
                }
            }
        }
    }

    private static void CheckDataTemplateBindings(XDocument doc, string fileName, List<Diagnostic> diagnostics)
    {
        foreach (var dt in doc.Descendants(Xmlns + "DataTemplate"))
        {
            foreach (var el in dt.Descendants())
            {
                foreach (var attr in el.Attributes())
                {
                    if (attr.Value.Contains("x:Bind ViewModel."))
                    {
                        var info = (IXmlLineInfo)el;
                        diagnostics.Add(new Diagnostic(
                            DiagnosticSeverity.Warning,
                            "FS3005",
                            "x:Bind inside DataTemplate should not use ViewModel. prefix (DataTemplate has its own data context)",
                            new SourceLocation(fileName, info.LineNumber, info.LinePosition)));
                    }
                }
            }
        }
    }

    private static void CheckDataTemplateMissingDataType(XDocument doc, string fileName, List<Diagnostic> diagnostics)
    {
        foreach (var dt in doc.Descendants(Xmlns + "DataTemplate"))
        {
            var hasDataType = dt.Attribute(XNs + "DataType") is not null;
            if (hasDataType) continue;

            bool usesXBind = dt.Descendants().Any(el =>
                el.Attributes().Any(a => a.Value.Contains("x:Bind")));

            if (usesXBind)
            {
                var info = (IXmlLineInfo)dt;
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "FS3006",
                    "<DataTemplate> uses x:Bind but is missing x:DataType attribute (required in WinUI 3)",
                    new SourceLocation(fileName, info.LineNumber, info.LinePosition)));
            }
        }
    }

    private static void CheckWindowResources(XDocument doc, string fileName, List<Diagnostic> diagnostics)
    {
        foreach (var el in doc.Descendants(Xmlns + "Window.Resources"))
        {
            var info = (IXmlLineInfo)el;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "FS3007",
                "<Window.Resources> is not valid in WinUI 3 (Window has no Resources property); use NavigationView.Resources or Page.Resources instead",
                new SourceLocation(fileName, info.LineNumber, info.LinePosition)));
        }
    }

    /// <summary>FS3012: an element with <c>x:Load</c> must have <c>x:Name</c> (WinUI's WMC0907).</summary>
    private static void CheckLoadHasName(XDocument doc, string fileName, List<Diagnostic> diagnostics)
    {
        foreach (var el in doc.Descendants())
        {
            if (el.Attribute(XNs + "Load") is null || el.Attribute(XNs + "Name") is not null)
                continue;
            var info = (IXmlLineInfo)el;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "FS3012",
                $"<{el.Name.LocalName}> has x:Load but no x:Name (the WinUI XAML compiler rejects it: WMC0907)",
                new SourceLocation(fileName, info.LineNumber, info.LinePosition),
                "add name: X to the element with load:"));
        }
    }

    private static void CheckDuplicateXName(XDocument doc, string fileName, List<Diagnostic> diagnostics)
    {
        var names = new Dictionary<string, XElement>();
        foreach (var el in doc.Descendants())
        {
            var nameAttr = el.Attribute(XNs + "Name");
            if (nameAttr is null) continue;
            if (names.TryGetValue(nameAttr.Value, out var existing))
            {
                var info = (IXmlLineInfo)el;
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "FS3008",
                    $"Duplicate x:Name=\"{nameAttr.Value}\"",
                    new SourceLocation(fileName, info.LineNumber, info.LinePosition)));
            }
            else
            {
                names[nameAttr.Value] = el;
            }
        }
    }

    private static void CheckXClassNamespace(XDocument doc, string fileName, string expectedNamespace, List<Diagnostic> diagnostics)
    {
        var root = doc.Root;
        if (root is null) return;
        var xClass = root.Attribute(XNs + "Class");
        if (xClass is null) return;

        if (!xClass.Value.StartsWith(expectedNamespace + "."))
        {
            var info = (IXmlLineInfo)root;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "FS3009",
                $"x:Class=\"{xClass.Value}\" does not match expected namespace \"{expectedNamespace}\"",
                new SourceLocation(fileName, info.LineNumber, info.LinePosition)));
        }
    }

    /// <summary>Every <c>x:Key</c> declared in the document (a page's own resources: <c>Page.Resources</c>, <c>Grid.Resources</c>).</summary>
    private static HashSet<string> DeclaredKeys(XDocument doc) =>
        doc.Descendants().Select(e => e.Attribute(XNs + "Key")?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// FS3010: a <c>{StaticResource Key}</c> whose key is neither <see cref="IsKnownResource">known</see> nor declared in
    /// this document. From <c>.flag</c> source this is <c>resource(Key)</c> without a matching <c>key: Key</c> in the view's
    /// <c>Page.Resources</c>.
    /// </summary>
    private static void CheckStaticResources(string xamlContent, string fileName, HashSet<string> declared, List<Diagnostic> diagnostics)
    {
        int lineNum = 0;
        foreach (var line in xamlContent.Split('\n'))
        {
            lineNum++;
            foreach (Match match in StaticResourceRegex.Matches(line))
            {
                var resourceName = match.Groups[1].Value;
                if (!KnownResources.Contains(resourceName) && !declared.Contains(resourceName))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        "FS3010",
                        $"StaticResource '{resourceName}' is not in the known resource set",
                        new SourceLocation(fileName, lineNum, match.Index + 1),
                        $"declare it in the view: Page.Resources: {{ ... key: {resourceName} ... }} (a WinUI theme brush is theme({resourceName}))"));
                }
            }
        }
    }
}
