namespace Flagstaff.Compiler;

/// <summary>
/// Context checks for layout and universal props that the schema alone cannot express (the schema
/// says <em>whether</em> a keyword takes a prop; this says whether it means anything <em>where</em> it is).
/// Called by <see cref="XamlGenerator.GenerateView"/>.
/// <list type="bullet">
/// <item>FS1002 a universal prop the keyword's emitter does not apply yet (<c>tooltip:</c> on <c>table</c>)</item>
/// <item>FS1005 a malformed <c>width:</c> / <c>span:</c></item>
/// <item>FS1010 more than one <c>fill</c> child in the same vertical container</item>
/// <item>FS1011 a non-wrapping <c>row</c> whose children need more than <c>cols:</c> columns</item>
/// <item>FS1012 (warning) a layout prop with no effect in its position</item>
/// <item>FS1013 (warning) <c>fill</c> inside a scrolling region</item>
/// </list>
/// </summary>
public static class LayoutValidator
{
    /// <summary>How a container lays out its children.</summary>
    private enum Parent { Vertical, Row, Split, Cards, Other }

    /// <summary>Universal props that need the element's own emitter (see <see cref="XamlGenerator.AppliesCommonAttrs"/>).</summary>
    private static readonly string[] EmitterProps = ["align", "tooltip", "name", "uid", "load"];

    public static List<Diagnostic> Validate(ViewDecl view)
    {
        var diagnostics = new List<Diagnostic>();
        var pinned = XamlGenerator.PinnedToolbar(view);
        if (pinned is not null)
            CheckElement(pinned, Parent.Other, bounded: false, diagnostics);

        var body = view.Body.Where(e => !ReferenceEquals(e, pinned)).ToList();
        // The body only stops scrolling (and bounds its fill child) when it has a fill child.
        CheckChildren(body, Parent.Vertical, bounded: body.Any(IsFill), diagnostics);
        return diagnostics;
    }

    private static void CheckChildren(IReadOnlyList<Element> children, Parent parent, bool bounded, List<Diagnostic> diagnostics)
    {
        if (parent == Parent.Vertical)
        {
            var fills = children.Where(c => !XamlGenerator.IsHoisted(c) && IsFill(c)).ToList();
            if (fills.Count > 1)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1010",
                    "Only one element per container can be 'fill'",
                    FillLocation(fills[1]),
                    $"'fill' is already on the '{fills[0].Keyword}' at line {fills[0].Location.Line}; wrap the others in a split or row"));
            }
            else if (fills.Count == 1 && !bounded)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning, "FS1013",
                    "'fill' inside a scrolling region has no effect",
                    FillLocation(fills[0]),
                    "mark the enclosing containers 'fill' as well, up to the page body"));
            }
        }

        foreach (var child in children)
        {
            if (XamlGenerator.IsHoisted(child))
            {
                // Overlays render in the page's overlay layer, not in this container.
                CheckElement(child, Parent.Other, bounded: false, diagnostics);
                continue;
            }
            var childBounded = parent switch
            {
                Parent.Vertical => bounded && IsFill(child),
                Parent.Row or Parent.Split => bounded,
                _ => false,
            };
            CheckElement(child, parent, childBounded, diagnostics);
        }
    }

    private static void CheckElement(Element el, Parent parent, bool bounded, List<Diagnostic> diagnostics)
    {
        var inGrid = parent is Parent.Row or Parent.Split or Parent.Cards;
        // Generic elements get CommonAttrs from their emitter (XamlGenerator.Generic.cs).
        var ownEmitter = XamlGenerator.AppliesCommonAttrs(el);

        foreach (var prop in el.Props)
        {
            switch (prop.Key)
            {
                case "width":
                    if (el.Keyword == "col")
                        break; // table columns: width is applied by the table emitter (XamlGenerator.Tables.cs)
                    var width = XamlGenerator.ParseWidth(prop.Value);
                    if (width is null)
                        diagnostics.Add(new Diagnostic(
                            DiagnosticSeverity.Error, "FS1005",
                            $"Invalid width on '{el.Keyword}'; expected auto, a positive number, * or N*",
                            prop.Location, "write width: 240, width: auto, width: * or width: 2*"));
                    else if (!inGrid && !width.Value.IsPixel)
                        diagnostics.Add(NoEffect(el, prop, $"'width: {width.Value.Xaml.ToLowerInvariant()}' only sizes a column inside row, split or cards",
                            "use a number (width: 240) outside a grid"));
                    else if (!inGrid && !ownEmitter)
                        diagnostics.Add(NotSupported(el, prop));
                    break;

                case "span":
                    if (prop.Value is IntValue { Value: < 1 })
                        diagnostics.Add(new Diagnostic(
                            DiagnosticSeverity.Error, "FS1005",
                            $"'span' on '{el.Keyword}' must be at least 1", prop.Location, "write span: 2"));
                    else if (parent is not (Parent.Row or Parent.Cards))
                        diagnostics.Add(NoEffect(el, prop, "'span' only applies to children of row or cards", null));
                    break;

                case "fill":
                    if (parent is not (Parent.Vertical or Parent.Row or Parent.Split))
                        diagnostics.Add(NoEffect(el, prop,
                            "'fill' only applies to children of the page, stack, column, group, card, row or split", null));
                    break;

                default:
                    if (!ownEmitter && EmitterProps.Contains(prop.Key) && ElementSchema.Universal.ContainsKey(prop.Key)
                        && !(ElementSchema.All.TryGetValue(el.Keyword, out var schema) && schema.Props.ContainsKey(prop.Key)))
                        diagnostics.Add(NotSupported(el, prop));
                    break;
            }
        }

        if (el.Enabled is not null && ownEmitter && !el.IsGeneric && !XamlGenerator.IsControl(el))
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning, "FS1012",
                $"'enabled(...)' has no effect on '{el.Keyword}'",
                el.Enabled.Location, "put enabled(...) on the inputs and buttons themselves"));

        if (el.Keyword == "row")
            CheckRowOverflow(el, diagnostics);
        if (el.Keyword == "cards")
            CheckSpans(el, XamlGenerator.CardsColumns(el), diagnostics);

        var children = XamlGenerator.ChildElements(el);
        if (children.Count == 0)
            return;
        var kind = el.Keyword switch
        {
            "stack" or "column" or "group" or "card" => Parent.Vertical,
            "row" => Parent.Row,
            "split" => Parent.Split,
            "cards" => Parent.Cards,
            _ => Parent.Other,
        };
        CheckChildren(children, kind, bounded, diagnostics);
    }

    private static void CheckRowOverflow(Element row, List<Diagnostic> diagnostics)
    {
        var cols = XamlGenerator.RowColumns(row);
        if (XamlGenerator.RowWraps(row))
        {
            CheckSpans(row, cols, diagnostics);
            return;
        }
        var overflow = XamlGenerator.LayoutGrid(XamlGenerator.SlotChildren(row), cols, wrap: false)
            .FirstOrDefault(s => s.Column + s.Span > cols);
        if (overflow is not null)
        {
            var needed = XamlGenerator.LayoutGrid(XamlGenerator.SlotChildren(row), cols, wrap: false).Sum(s => s.Span);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1011",
                $"'row cols: {cols}' has children needing {needed} columns",
                overflow.Element.Location,
                $"raise cols:, or add 'wrap' to continue on a new line (row cols: {cols} wrap {{ ... }})"));
        }
    }

    private static void CheckSpans(Element grid, int cols, List<Diagnostic> diagnostics)
    {
        foreach (var child in XamlGenerator.SlotChildren(grid))
        {
            if (child.Props.FirstOrDefault(p => p.Key == "span") is { Value: IntValue { Value: var span } } prop && span > cols)
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1011",
                    $"'span: {span}' is wider than '{grid.Keyword} cols: {cols}'",
                    prop.Location, $"use span: {cols} or less"));
        }
    }

    private static bool IsFill(Element el) =>
        el.Props.Any(p => p.Key == "fill" && p.Value is BoolValue { Value: true });

    private static SourceLocation FillLocation(Element el) =>
        el.Props.First(p => p.Key == "fill").Location;

    private static Diagnostic NoEffect(Element el, PropPair prop, string message, string? hint) =>
        new(DiagnosticSeverity.Warning, "FS1012", $"{message}; it has no effect on this '{el.Keyword}'", prop.Location, hint);

    private static Diagnostic NotSupported(Element el, PropPair prop) =>
        new(DiagnosticSeverity.Error, "FS1002",
            $"Property '{prop.Key}' is not supported on '{el.Keyword}' yet",
            prop.Location,
            XamlGenerator.IsHoisted(el)
                ? $"'{prop.Key}:' is not supported on overlays: {el.Keyword} is hoisted into the page's overlay layer, so a wrapper would not carry it; put {prop.Key}: on the elements inside it"
                : $"wrap it: stack {prop.Key}: ... {{ {el.Keyword} ... }}");
}
