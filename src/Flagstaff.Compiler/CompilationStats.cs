using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Flagstaff.Compiler;

/// <summary>
/// Why a <c>xaml { }</c> block could not be written as a generic element (unknown type or member, markup the element form
/// cannot express, malformed XML).
/// A block can have several reasons; <see cref="Expressible"/> means none applied.
/// </summary>
public static class EscapeReason
{
    /// <summary>The body is not well-formed XML (even wrapped in a root with the standard namespaces).</summary>
    public const string Malformed = "malformed";
    /// <summary>An element type is not in <see cref="ControlManifest.Default"/> (includes custom <c>prefix:</c> types).</summary>
    public const string UnknownType = "unknown-type";
    /// <summary>A markup extension other than <c>{x:Bind}</c>, <c>{StaticResource}</c>, <c>{ThemeResource}</c>.</summary>
    public const string MarkupExtension = "markup-extension";
    /// <summary>
    /// An <c>x:</c> directive without a Flagstaff prop: <c>x:Phase</c>, <c>x:DeferLoadStrategy</c>, <c>x:FieldModifier</c>,
    /// <c>x:DefaultBindMode</c>, <c>x:Shared</c>, and <c>x:Load</c> with a literal value. <c>x:Name</c>, <c>x:DataType</c>,
    /// <c>x:Key</c> (<c>key:</c>), <c>x:Uid</c> (<c>uid:</c>) and <c>x:Load="{x:Bind ...}"</c> (<c>load:</c>) are expressible.
    /// </summary>
    public const string XDirective = "x-directive";
    /// <summary>
    /// Kept for the JSON contract; never produced. An attribute naming a manifest event of its element
    /// (<c>Click="..."</c>) is expressible as <c>on Click -> target</c> and recorded as the finding <c>event: Click</c>.
    /// </summary>
    public const string EventHandler = "event-handler";
    /// <summary>None of the above: every type is known and every attribute is plain or x:Bind/StaticResource/ThemeResource.</summary>
    public const string Expressible = "expressible";
}

/// <summary>One <c>xaml { }</c> block: where it is, how long it is, and why it is (or is not) still raw XAML.</summary>
/// <param name="Line">Line of the <c>xaml</c> keyword in the .flag file.</param>
/// <param name="Lines">Non-blank lines inside the braces.</param>
/// <param name="Reasons"><see cref="EscapeReason"/> values, in a fixed order.</param>
/// <param name="RootTypes">Top-level element names as written (<c>TreeView</c>, <c>lvc:CartesianChart</c>).</param>
/// <param name="Findings">What triggered each reason (<c>unknown-type: lvc:CartesianChart</c>, <c>x-directive: x:Phase</c>), plus
/// expressible features that need a Flagstaff form (<c>event: Click</c> → <c>on Click -></c>, <c>prop: x:Uid -> uid:</c>).</param>
/// <param name="Suggestion">For an expressible block: its first root written as a generic element, abridged
/// (<see cref="EscapeAnalyzer.Suggest"/>); FS3011's hint shows it.</param>
public sealed record EscapeBlockStats(
    string File,
    int Line,
    int Column,
    int Lines,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> RootTypes,
    IReadOnlyList<string> Findings,
    string? Suggestion = null)
{
    public bool IsExpressible => Reasons.Count == 1 && Reasons[0] == EscapeReason.Expressible;
}

/// <summary>Per-file counts (see <see cref="CompilationStats"/>).</summary>
public sealed record FileStats(
    string File,
    int FlagLines,
    int EscapeBlocks,
    int EscapeLines,
    int KeywordElements,
    int GenericElements);

/// <summary>
/// Escape-hatch and generic-element usage for a compilation (<see cref="CompilationResult.Stats"/>).
/// <c>FlagLines</c> counts non-blank, non-<c>//</c> lines; <c>EscapeLines</c> counts non-blank lines inside
/// <c>xaml { }</c> bodies; element counts exclude the <c>xaml</c> blocks themselves.
/// </summary>
public sealed class CompilationStats
{
    public List<FileStats> Files { get; } = [];
    public List<EscapeBlockStats> EscapeBlocksDetail { get; } = [];

    public int FlagLines => Files.Sum(f => f.FlagLines);
    public int EscapeBlocks => Files.Sum(f => f.EscapeBlocks);
    public int EscapeLines => Files.Sum(f => f.EscapeLines);
    public int KeywordElements => Files.Sum(f => f.KeywordElements);
    public int GenericElements => Files.Sum(f => f.GenericElements);

    /// <summary><see cref="EscapeLines"/> / <see cref="FlagLines"/> (0 when there are no lines).</summary>
    public double EscapeRatio => FlagLines == 0 ? 0 : (double)EscapeLines / FlagLines;

    /// <summary><see cref="GenericElements"/> / all elements (0 when there are none).</summary>
    public double GenericRatio => KeywordElements + GenericElements == 0 ? 0 : (double)GenericElements / (KeywordElements + GenericElements);

    public void Merge(CompilationStats other)
    {
        Files.AddRange(other.Files);
        EscapeBlocksDetail.AddRange(other.EscapeBlocksDetail);
    }

    /// <summary>Counts one .flag file. <paramref name="ast"/> is null when it did not parse (lines are still counted).</summary>
    public static CompilationStats ForFile(string path, string content, FlagFile? ast)
    {
        var stats = new CompilationStats();
        int keyword = 0, generic = 0, escapeLines = 0;
        var blocks = new List<EscapeBlockStats>();

        void Visit(Element el)
        {
            if (el.Body is RawXamlBody raw)
            {
                var lines = CountNonBlank(raw.Xaml);
                escapeLines += lines;
                var (reasons, roots, findings) = EscapeAnalyzer.Analyze(raw.Xaml);
                var suggestion = reasons is [EscapeReason.Expressible] ? EscapeAnalyzer.Suggest(raw.Xaml) : null;
                blocks.Add(new EscapeBlockStats(path, el.Location.Line, el.Location.Column, lines, reasons, roots, findings, suggestion));
                return;
            }
            if (el.IsGeneric) generic++; else keyword++;
            foreach (var p in el.Props) VisitValue(p.Value);
            if (el.Body is BlockBody body)
                foreach (var child in body.Children.OfType<ChildElement>())
                    Visit(child.Element);
        }

        void VisitValue(PropValue value)
        {
            switch (value)
            {
                case ElementValue ev: Visit(ev.Element); break;
                case ElementsValue evs: foreach (var e in evs.Elements) Visit(e); break;
                case ListValue list: foreach (var item in list.Items) VisitValue(item); break;
            }
        }

        foreach (var view in ast?.Declarations.OfType<ViewDecl>() ?? [])
        {
            foreach (var p in view.PropertyElements) VisitValue(p.Value);
            foreach (var el in view.Body) Visit(el);
        }

        stats.Files.Add(new FileStats(path, CountFlagLines(content), blocks.Count, escapeLines, keyword, generic));
        stats.EscapeBlocksDetail.AddRange(blocks);
        return stats;
    }

    internal static int CountFlagLines(string content) =>
        content.Split('\n').Count(l => l.Trim() is { Length: > 0 } t && !t.StartsWith("//", StringComparison.Ordinal));

    private static int CountNonBlank(string text) => text.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));
}

/// <summary>Classifies the raw XAML of one <c>xaml { }</c> block into <see cref="EscapeReason"/> values.</summary>
public static class EscapeAnalyzer
{
    private const string PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.Ordinal) { "x:Bind", "StaticResource", "ThemeResource" };
    private static readonly HashSet<string> AllowedDirectives = new(StringComparer.Ordinal) { "Name", "DataType" };

    /// <summary><c>x:</c> directives a Flagstaff prop writes: recorded as a finding, not a reason.</summary>
    private static readonly Dictionary<string, string> DirectiveProps = new(StringComparer.Ordinal)
    {
        ["Key"] = "key:",
        ["Uid"] = "uid:",
    };

    /// <summary>Finding prefix for an <c>x:</c> directive that has a Flagstaff prop (<c>prop: x:Uid -> uid:</c>).</summary>
    public const string PropFinding = "prop";

    /// <summary>Finding prefix for an event handler attribute that <c>on Event -></c> can wire (<c>event: Click</c>).</summary>
    public const string EventFinding = "event";

    // prefix:Name in element or attribute position (not inside {...} values, which follow '{' or '"').
    private static readonly Regex PrefixUse = new(@"(?<=[<\s/])([A-Za-z_][\w-]*):[A-Za-z_]", RegexOptions.Compiled);
    private static readonly Regex ExtensionName = new(@"\{\s*([A-Za-z_][\w:.]*)", RegexOptions.Compiled);

    public static (IReadOnlyList<string> Reasons, IReadOnlyList<string> RootTypes, IReadOnlyList<string> Findings) Analyze(string xaml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(Wrap(xaml));
        }
        catch (XmlException ex)
        {
            return ([EscapeReason.Malformed], [], [$"{EscapeReason.Malformed}: {ex.Message}"]);
        }

        var roots = root.Elements().Select(Written).ToList();
        if (roots.Count == 0)
            return ([EscapeReason.Malformed], [], [$"{EscapeReason.Malformed}: no element"]);

        var manifest = ControlManifest.Default;
        var findings = new List<(string Reason, string What)>();
        void Add(string reason, string what)
        {
            if (!findings.Contains((reason, what))) findings.Add((reason, what));
        }

        foreach (var el in root.Descendants())
        {
            ManifestType? type = null;
            var isPropertyElement = el.Name.LocalName.Contains('.');
            if (el.Name.NamespaceName != PresentationNs)
                Add(EscapeReason.UnknownType, Written(el));
            else if (!isPropertyElement && !manifest.TryGetType(el.Name.LocalName, out type))
                Add(EscapeReason.UnknownType, Written(el));

            foreach (var attr in el.Attributes())
            {
                if (attr.IsNamespaceDeclaration)
                    continue;
                if (attr.Name.NamespaceName == XamlNs)
                {
                    var directive = attr.Name.LocalName;
                    if (AllowedDirectives.Contains(directive))
                        continue;
                    if (DirectiveProps.TryGetValue(directive, out var prop))
                        Add(PropFinding, $"x:{directive} -> {prop}");
                    else if (directive == "Load" && IsXBind(attr.Value))
                        Add(PropFinding, "x:Load -> load:"); // load: load(vm.X); a literal x:Load has no prop
                    else
                        Add(EscapeReason.XDirective, "x:" + directive);
                    continue;
                }
                // Click="OnClick": `on Click -> vm.XCommand` wires any manifest event, so it is not a reason.
                if (attr.Name.NamespaceName.Length == 0 && type is not null && !attr.Name.LocalName.Contains('.')
                    && manifest.HasEvent(type, attr.Name.LocalName))
                {
                    Add(EventFinding, attr.Name.LocalName);
                    continue;
                }

                var value = attr.Value.TrimStart();
                if (value.StartsWith('{') && !value.StartsWith("{}", StringComparison.Ordinal))
                {
                    foreach (Match m in ExtensionName.Matches(value))
                    {
                        if (!AllowedExtensions.Contains(m.Groups[1].Value))
                            Add(EscapeReason.MarkupExtension, "{" + m.Groups[1].Value + "}");
                    }
                }
            }
        }

        string[] order = [EscapeReason.UnknownType, EscapeReason.MarkupExtension, EscapeReason.XDirective];
        var reasons = order.Where(r => findings.Any(f => f.Reason == r)).ToList();
        if (reasons.Count == 0)
            reasons.Add(EscapeReason.Expressible);
        return (reasons, roots, findings.Select(f => $"{f.Reason}: {f.What}").ToList());
    }

    private static bool IsXBind(string value) =>
        value.TrimStart().StartsWith("{x:Bind", StringComparison.Ordinal);

    // ── Generic-element suggestion (FS3011) ─────────────────────────

    /// <summary>Props written per element before <c>...</c>.</summary>
    private const int SuggestProps = 4;

    /// <summary>Child elements written per element before <c>...</c>.</summary>
    private const int SuggestChildren = 3;

    /// <summary>Nesting levels written before <c>{ ... }</c>.</summary>
    private const int SuggestDepth = 3;

    private static readonly Regex XBindPath = new(@"^\{x:Bind\s+(?:Path=)?(?<path>[A-Za-z_][\w.]*)\s*(?:,\s*Mode=\w+\s*)?\}$", RegexOptions.Compiled);
    private static readonly Regex ResourceRef = new(@"^\{(?<kind>StaticResource|ThemeResource)\s+(?:ResourceKey=)?(?<key>[\w.]+)\s*\}$", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>
    /// The first root of an expressible <c>xaml { }</c> block written as a Flagstaff generic element, on one line and
    /// abridged (<c>...</c> after <see cref="SuggestProps"/> props, <see cref="SuggestChildren"/> children or
    /// <see cref="SuggestDepth"/> levels): <c>MenuBar { MenuBarItem Title: "File" { MenuFlyoutItem Text: "New" on Click
    /// -> vm.NewCommand; ... } ... }</c>. Attributes become <c>Key: value</c> (bools, numbers and enum members bare, the rest
    /// quoted), <c>{x:Bind ViewModel.X}</c> <c>vm.X</c> (<c>item.X</c> in a template), <c>{StaticResource K}</c>
    /// <c>resource(K)</c>, <c>{ThemeResource K}</c> <c>theme(K)</c>, <c>x:Name</c>/<c>x:Uid</c>/<c>x:Key</c>/<c>x:Load</c>
    /// their props, a handler attribute <c>on Event -> vm.XCommand</c>; an <c>ItemTemplate</c> over <c>x:DataType</c>
    /// <c>T</c> becomes <c>of T</c> with the template's content as the body. Null when the block does not parse.
    /// </summary>
    public static string? Suggest(string xaml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(Wrap(xaml));
        }
        catch (XmlException)
        {
            return null;
        }
        return root.Elements().FirstOrDefault() is { } first ? Terminated(Render(first, 0, inTemplate: false)) : null;
    }

    private static string Render(XElement el, int depth, bool inTemplate)
    {
        var manifest = ControlManifest.Default;
        manifest.TryGetType(el.Name.LocalName, out var type);
        var parts = new List<string> { el.Name.LocalName };
        var props = new List<string>();
        var more = false;

        // An ItemTemplate property element over x:DataType="vm:T": of T, and the template's content is the body.
        XElement? template = null;
        string? ofType = null;
        var propertyElements = new List<XElement>();
        foreach (var child in el.Elements().Where(e => e.Name.LocalName.Contains('.')))
        {
            var member = child.Name.LocalName[(child.Name.LocalName.IndexOf('.') + 1)..];
            if (member == "ItemTemplate" && child.Elements().FirstOrDefault() is { Name.LocalName: "DataTemplate" } dataTemplate
                && dataTemplate.Attribute(XName.Get("DataType", XamlNs))?.Value is { } dataType)
            {
                ofType = dataType[(dataType.IndexOf(':') + 1)..];
                template = dataTemplate;
            }
            else
            {
                propertyElements.Add(child);
            }
        }

        foreach (var attr in el.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            if (props.Count >= SuggestProps)
            {
                more = true;
                break;
            }
            if (RenderAttribute(attr, type, inTemplate) is { } prop)
                props.Add(prop);
            else if (attr.Name != XName.Get("DataType", XamlNs))
                more = true;
        }
        parts.AddRange(props);
        if (more)
            parts.Add("...");
        if (ofType is not null)
            parts.Add($"of {ofType}");

        var body = new List<string>();
        foreach (var prop in propertyElements)
        {
            var member = prop.Name.LocalName[(prop.Name.LocalName.IndexOf('.') + 1)..];
            var values = prop.Elements().ToList();
            body.Add(depth + 1 >= SuggestDepth || values.Count == 0 ? $"{member}: {{ ... }}"
                : values.Count == 1 ? $"{member}: {Terminated(Render(values[0], depth + 1, inTemplate))}"
                : $"{member}: {{ {string.Join(" ", values.Take(SuggestChildren).Select(v => Terminated(Render(v, depth + 2, inTemplate))))}{(values.Count > SuggestChildren ? " ..." : "")} }}");
        }

        var children = (template ?? el).Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
        var childTemplate = inTemplate || template is not null;
        if (depth + 1 >= SuggestDepth && children.Count > 0)
        {
            body.Add("...");
        }
        else
        {
            foreach (var child in children.Take(SuggestChildren))
                body.Add(Terminated(Render(child, depth + 1, childTemplate)));
            if (children.Count > SuggestChildren)
                body.Add("...");
        }
        // Text content: Text: "..." where the type has a Text property (TextBlock's content property is Inlines), else
        // the label (the content property of a Button or ContentControl).
        if (children.Count == 0 && template is null && el.Nodes().OfType<XText>().Select(t => t.Value.Trim()).FirstOrDefault(t => t.Length > 0) is { } text
            && !text.Contains('"'))
        {
            parts.Insert(1, type is not null && GenericValidator.ResolveProperty(type, "Text") is not null ? $"Text: \"{text}\"" : $"\"{text}\"");
        }

        var head = string.Join(" ", parts);
        return body.Count == 0 ? head : $"{head} {{ {string.Join(" ", body)} }}";
    }

    /// <summary>A leaf ends with <c>;</c>; a block (<c>... }</c>) needs none.</summary>
    private static string Terminated(string element) => element.EndsWith('}') ? element : element + ";";

    private static string? RenderAttribute(XAttribute attr, ManifestType? type, bool inTemplate)
    {
        var manifest = ControlManifest.Default;
        var name = attr.Name.LocalName;
        var value = attr.Value.Trim();
        if (attr.Name.NamespaceName == XamlNs)
        {
            return name switch
            {
                "Name" => $"name: {value}",
                "Key" => $"key: {value}",
                "Uid" => value.Contains('"') ? null : $"uid: \"{value}\"",
                "Load" when XBindPath.Match(value) is { Success: true } m => $"load: load({BindingPath(m.Groups["path"].Value, inTemplate)})",
                _ => null,
            };
        }
        if (attr.Name.NamespaceName.Length > 0)
            return null;
        if (type is not null && !name.Contains('.') && manifest.HasEvent(type, name))
        {
            var handler = value.Length > 2 && value.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(value[2]) ? value[2..] : value;
            return $"on {name} -> vm.{handler}Command";
        }
        if (value.StartsWith('{'))
        {
            if (XBindPath.Match(value) is { Success: true } bind)
                return $"{name}: {BindingPath(bind.Groups["path"].Value, inTemplate)}";
            if (ResourceRef.Match(value) is { Success: true } resource)
                return $"{name}: {(resource.Groups["kind"].Value == "ThemeResource" ? "theme" : "resource")}({resource.Groups["key"].Value})";
            return null;
        }
        if (value.Contains('"'))
            return null;
        var prop = !name.Contains('.') && type is not null ? GenericValidator.ResolveProperty(type, name) : null;
        var clr = prop?.TypeName.TrimEnd('?');
        if (clr == "bool" && value is "True" or "False" or "true" or "false")
            return $"{name}: {value.ToLowerInvariant()}";
        if (Number.IsMatch(value) && (clr is "int" or "double" or "float" or "long" or "short" or "byte" or "uint" or "Thickness" or "CornerRadius"
                                      || name.Contains('.') && !value.Contains('.')))
            return $"{name}: {value}";
        if (prop?.EnumMembers is { } members && members.Contains(value, StringComparer.Ordinal))
            return $"{name}: {value}";
        return $"{name}: \"{value}\"";
    }

    /// <summary><c>ViewModel.Title</c> → <c>vm.Title</c>; a path in a template binds the row (<c>item.Title</c>).</summary>
    private static string BindingPath(string path, bool inTemplate) =>
        path.StartsWith("ViewModel.", StringComparison.Ordinal) ? "vm." + path["ViewModel.".Length..]
        : inTemplate ? "item." + path
        : "vm." + path;

    /// <summary>Wraps the fragment in a root declaring the default, <c>x:</c>, and every other prefix it uses.</summary>
    private static string Wrap(string xaml)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal) { "x", "xmlns", "xml" };
        var extra = new System.Text.StringBuilder();
        foreach (Match m in PrefixUse.Matches(xaml))
        {
            var prefix = m.Groups[1].Value;
            if (declared.Add(prefix))
                extra.Append($" xmlns:{prefix}=\"using:Flagstaff.Unknown.{prefix}\"");
        }
        return $"<FlagstaffEscapeRoot xmlns=\"{PresentationNs}\" xmlns:x=\"{XamlNs}\"{extra}>{xaml}</FlagstaffEscapeRoot>";
    }

    private static string Written(XElement el)
    {
        var prefix = el.GetPrefixOfNamespace(el.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? el.Name.LocalName : $"{prefix}:{el.Name.LocalName}";
    }
}
