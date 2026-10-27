using System.Text;
using System.Text.RegularExpressions;

namespace Flagstaff.Compiler;

// Pass-through props (PascalCase / dotted keys) and property elements (`Flyout: MenuFlyout { }`).
// Only attributes whose correctness is local to the element pass through (the locality rule, checked in GenericValidator).
//
// Keyword elements: each emitter writes its root tag (KeywordSchema.RootType) through EmitRoot, which adds the
// property elements; the scalar pass-through attributes arrive through CommonAttrs -> PassThroughAttrs. Keywords
// whose emitter cannot carry them get FS1033 from ReportUnsupportedPassThrough, so nothing is dropped silently.
public sealed partial class XamlGenerator
{
    /// <summary>Keyword elements whose scalar pass-through props were written by <see cref="PassThroughAttrs"/>.</summary>
    private readonly HashSet<Element> _passThroughEmitted = new(ReferenceEqualityComparer.Instance);

    /// <summary>Keyword elements whose Grid placement props went on a wrapper (<see cref="PlacementAttrs"/>).</summary>
    private readonly HashSet<Element> _placedByWrapper = new(ReferenceEqualityComparer.Instance);

    /// <summary>Property-element props written by <see cref="EmitPropertyElementList"/> (checked by <see cref="ReportUnsupportedPassThrough"/>).</summary>
    private readonly HashSet<PropPair> _propertyElementsEmitted = new(ReferenceEqualityComparer.Instance);

    /// <summary>A pass-through key holding an object: <c>Flyout: MenuFlyout { }</c> / <c>KeyboardAccelerators: { ... }</c>.</summary>
    private static bool IsPropertyElement(PropPair p) =>
        AstExtensions.IsPassThroughKey(p.Key) && p.Value is ElementValue or ElementsValue;

    /// <summary>A pass-through key with a scalar value (an attribute on the root tag).</summary>
    private static bool IsScalarPassThrough(PropPair p) =>
        AstExtensions.IsPassThroughKey(p.Key) && p.Value is not (ElementValue or ElementsValue or ListValue or RangeValue);

    private static bool HasPropertyElements(Element el) => el.Props.Any(IsPropertyElement);

    /// <summary>
    /// The pass-through attributes (<c>Margin: "0,8"</c> → <c> Margin="0,8"</c>) for a keyword element's root tag,
    /// rendered by <see cref="RenderAttr"/> against the keyword's <see cref="KeywordSchema.RootType"/> (binding mode,
    /// enum spelling, value shape). Appended by <see cref="CommonAttrs"/> (and directly by the table/list/alert/overlay
    /// emitters, which do not call it). Every prop here was validated by <see cref="GenericValidator"/> first
    /// (FS1031/FS1032, and the locality rule for attached properties: FS1038/FS1040/FS1041), so nothing is emitted
    /// unchecked. Generic elements return <c>""</c>: their emitter renders their props itself.
    /// </summary>
    /// A table/list with an <c>empty</c> child sits in a wrapper Grid (<see cref="EmitCollection"/>); the wrapper carries
    /// its Grid placement props (<see cref="PlacementAttrs"/>), so they are skipped on the ListView.
    private string PassThroughAttrs(Element el)
    {
        if (el.IsGeneric)
            return "";
        if (!el.Props.Any(IsScalarPassThrough))
            return "";
        _passThroughEmitted.Add(el);

        var rootType = GenericValidator.KeywordRootType(el);
        var sb = new StringBuilder();
        foreach (var p in el.Props.Where(IsScalarPassThrough))
        {
            if (_placedByWrapper.Contains(el) && IsGridPlacement(p))
                continue;
            sb.Append(RenderAttr(p.Key, p.Value, rootType, GenericValidator.ResolveProperty(rootType, p.Key)));
        }
        return sb.ToString();
    }

    private static bool IsGridPlacement(PropPair p) => p.Key is "Grid.Row" or "Grid.Column" or "Grid.RowSpan" or "Grid.ColumnSpan";

    /// <summary>The element's Grid placement props (<c>Grid.Row: 1</c>), for a wrapper that sits in the parent Grid's cell.</summary>
    private string PlacementAttrs(Element el) =>
        string.Concat(el.Props.Where(p => IsGridPlacement(p) && IsScalarPassThrough(p)).Select(p => RenderAttr(p.Key, p.Value, null, null)));

    /// <summary>
    /// Emits the element's property elements inside the root tag named <paramref name="tag"/>: each
    /// <c>Key: Type { }</c> / <c>Key: { ... }</c> prop becomes <c>&lt;tag.Key&gt;...&lt;/tag.Key&gt;</c> (a dotted key
    /// such as <c>VisualStateManager.VisualStateGroups</c> is used as written), its elements emitted through
    /// <see cref="EmitElement"/>, so keywords and generic elements nest freely.
    /// </summary>
    private void EmitPropertyElements(Element el, string tag)
    {
        if (el.IsGeneric || !HasPropertyElements(el))
            return;
        EmitPropertyElementList(el.Props.Where(p => IsPropertyElement(p) && !IsRowPropertyElement(el, p)), tag);
    }

    /// <summary>
    /// <c>ContextFlyout:</c> on a <c>table</c>/<c>list</c>: it belongs to the row, not the ListView, so it is written on
    /// the row template's root (<see cref="EmitRowPropertyElements"/>) where <c>item.X</c> and <c>(item)</c> resolve to
    /// the row. Every other property element of a table/list stays on the ListView.
    /// </summary>
    internal static bool IsRowPropertyElement(Element el, PropPair p) =>
        !el.IsGeneric && el.Keyword is "table" or "list" && p.Key == "ContextFlyout" && IsPropertyElement(p);

    private static bool HasRowPropertyElements(Element el) => el.Props.Any(p => IsRowPropertyElement(el, p));

    /// <summary>
    /// A table/list's row-level property elements (<see cref="IsRowPropertyElement"/>) inside the row template root
    /// <paramref name="tag"/> (<c>&lt;Grid.ContextFlyout&gt;</c> / <c>&lt;StackPanel.ContextFlyout&gt;</c>), emitted with
    /// <see cref="ConditionCompiler.InItemTemplate"/> set: <c>-> vm.XCommand(item)</c> takes the template command form
    /// (<c>CommandParameter="{x:Bind}"</c>) exactly like row-action buttons.
    /// </summary>
    private void EmitRowPropertyElements(Element el, string tag)
    {
        var props = el.Props.Where(p => IsRowPropertyElement(el, p)).ToList();
        if (props.Count == 0)
            return;
        var wasInTemplate = Conditions.InItemTemplate;
        Conditions.InItemTemplate = true;
        try
        {
            EmitPropertyElementList(props, tag);
        }
        finally
        {
            Conditions.InItemTemplate = wasInTemplate;
        }
    }

    /// <summary>
    /// <c> Background="Transparent"</c> for a row template root that carries a ContextFlyout, so a right-click anywhere in
    /// the row (not only on its text) opens it; <c>""</c> otherwise.
    /// </summary>
    private static string RowHitTestAttrs(Element el) => HasRowPropertyElements(el) ? " Background=\"Transparent\"" : "";

    /// <summary>Writes one <c>&lt;owner.Key&gt;</c> block per element/elements value (also used for view-level props).</summary>
    private void EmitPropertyElementList(IEnumerable<PropPair> props, string ownerTag)
    {
        foreach (var p in props)
        {
            _propertyElementsEmitted.Add(p);
            var name = p.Key.Contains('.') ? p.Key : $"{ownerTag}.{p.Key}";
            Line($"<{name}>");
            Indent();
            switch (p.Value)
            {
                case ElementValue ev:
                    EmitElement(ev.Element);
                    break;
                case ElementsValue evs:
                    EmitElements(evs.Elements);
                    break;
            }
            Dedent();
            Line($"</{name}>");
        }
    }

    /// <summary>
    /// Writes a keyword element's root tag: <c>&lt;tag attrs&gt;</c>, its property elements
    /// (<see cref="EmitPropertyElements"/>), then <paramref name="body"/>, then <c>&lt;/tag&gt;</c>; self-closing
    /// (<c>&lt;tag attrs /&gt;</c>) when there is neither. A pass-through attribute that repeats one the emitter
    /// already wrote (<c>Style: resource(X)</c> on <c>stat</c>) replaces it (<see cref="DedupeAttrs"/>).
    /// </summary>
    private void EmitRoot(Element el, string tag, string attrs, Action? body)
    {
        CheckVariantTag(el, tag);
        attrs = DedupeAttrs(attrs);
        if (body is null && (el.IsGeneric || !HasPropertyElements(el)))
        {
            Line($"<{tag}{attrs} />");
            return;
        }

        Line($"<{tag}{attrs}>");
        Indent();
        EmitPropertyElements(el, tag);
        body?.Invoke();
        Dedent();
        Line($"</{tag}>");
    }

    /// <summary>
    /// Pass-through props are validated against the keyword's declared <see cref="KeywordSchema.RootType"/>, and they land
    /// on the tag the emitter writes. A few keywords write a different tag in some forms (<c>heading</c> with
    /// <c>icon:</c> → StackPanel, a string <c>badge</c> → Border, <c>stack</c>/<c>group</c>/<c>column</c> with a
    /// <c>fill</c> child → Grid); a prop the emitted tag does not have would be invalid XAML, so it is FS1031 here, and an
    /// <c>on Event</c> the emitted tag does not have is FS1034. Both hints name the tag actually written.
    /// </summary>
    private void CheckVariantTag(Element el, string tag)
    {
        if (el.IsGeneric || GenericValidator.KeywordRootType(el) is not { } declared || declared.Name == tag
            || !Manifest.TryGetType(tag, out var emitted))
            return;
        foreach (var p in el.Props)
        {
            if (!AstExtensions.IsPassThroughKey(p.Key) || p.Key.Contains('.') || IsRowPropertyElement(el, p)
                || GenericValidator.ResolveProperty(emitted, p.Key) is not null)
                continue;
            Report(new Diagnostic(
                DiagnosticSeverity.Error, "FS1031",
                $"'{el.Keyword}' is written as a {tag} here (not a {declared.Name}), which has no property '{p.Key}'",
                p.Location,
                $"'{el.Keyword}' is a {tag} here: wrap it in a generic {declared.Name}, or drop what changes the tag ({VariantReason(el.Keyword)})"));
        }
        foreach (var evt in el.Events)
        {
            if (Manifest.TryGetEvent(emitted, evt.EventName, out _) || !Manifest.TryGetEvent(declared, evt.EventName, out _))
                continue; // an event the declared type lacks too is FS1034 from validation already
            Report(new Diagnostic(
                DiagnosticSeverity.Error, "FS1034",
                $"'{el.Keyword}' is written as a {tag} here (not a {declared.Name}), which has no event '{evt.EventName}'",
                evt.Location,
                $"'{el.Keyword}' is a {tag} here: wrap it in a generic {declared.Name}, or drop what changes the tag ({VariantReason(el.Keyword)})"));
        }
    }

    private static string VariantReason(string keyword) => keyword switch
    {
        "heading" => "icon:",
        "badge" => "a badge is an InfoBadge only when bound to an int",
        _ => "a fill child",
    };

    /// <summary>A plain tag (no property elements): self-closing without <paramref name="body"/>.</summary>
    private void EmitTag(string tag, string attrs, Action? body)
    {
        if (body is null)
        {
            Line($"<{tag}{attrs} />");
            return;
        }
        Line($"<{tag}{attrs}>");
        Indent();
        body();
        Dedent();
        Line($"</{tag}>");
    }

    private static readonly Regex AttrPattern = new(@"\s+([\w.:]+)=""[^""]*""", RegexOptions.CultureInvariant);

    /// <summary>Drops earlier occurrences of a repeated attribute (the last one wins); unchanged when nothing repeats.</summary>
    private static string DedupeAttrs(string attrs)
    {
        var matches = AttrPattern.Matches(attrs);
        if (matches.Count < 2)
            return attrs;
        var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < matches.Count; i++)
            lastIndex[matches[i].Groups[1].Value] = i;
        if (lastIndex.Count == matches.Count)
            return attrs;

        var sb = new StringBuilder(attrs.Length);
        int pos = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            sb.Append(attrs, pos, m.Index - pos);
            if (lastIndex[m.Groups[1].Value] == i)
                sb.Append(m.Value);
            pos = m.Index + m.Length;
        }
        sb.Append(attrs, pos, attrs.Length - pos);
        return sb.ToString();
    }

    /// <summary>
    /// View-level property elements (<see cref="ViewDecl.PropertyElements"/>) that go on the Page:
    /// <c>Page.Resources: { ... }</c> (or <c>Resources:</c>) → <c>&lt;Page.Resources&gt;</c>, any other key the same
    /// way. Called by <see cref="GenerateView"/> before the root Grid. <c>VisualStateManager.VisualStateGroups</c> is
    /// written inside the root Grid instead (<see cref="EmitViewVisualStateGroups"/>).
    /// </summary>
    private void EmitViewPropertyElements(ViewDecl view) =>
        EmitPropertyElementList(view.PropertyElements.Where(p => p.Value is ElementValue or ElementsValue && !IsVisualStateGroups(p)), "Page");

    /// <summary>
    /// <c>VisualStateManager.VisualStateGroups: { ... }</c> at view level, written as the first child of the page's
    /// root Grid: WinUI looks for the groups on the root element of the page content, not on the Page.
    /// </summary>
    private void EmitViewVisualStateGroups(ViewDecl view) =>
        EmitPropertyElementList(view.PropertyElements.Where(p => p.Value is ElementValue or ElementsValue && IsVisualStateGroups(p)), "Grid");

    private static bool IsVisualStateGroups(PropPair p) => p.Key == "VisualStateManager.VisualStateGroups";

    /// <summary>
    /// FS1033 for keyword elements whose pass-through attributes or property elements no emitter wrote: a keyword
    /// whose emitter does not route its root tag through <see cref="EmitRoot"/> (<c>dialog</c>, <c>confirm</c>,
    /// <c>toast</c> for property elements), or one not emitted on its own (<c>col</c>, <c>empty</c>, a <c>spacer</c>
    /// in a toolbar/row/split, <c>xaml</c>). Called at the end of <see cref="GenerateView"/>.
    /// </summary>
    private void ReportUnsupportedPassThrough(ViewDecl view)
    {
        foreach (var p in view.PropertyElements)
            Walk(p.Value);
        foreach (var el in view.Body)
            Visit(el);

        void Walk(PropValue value)
        {
            switch (value)
            {
                case ElementValue ev:
                    Visit(ev.Element);
                    break;
                case ElementsValue evs:
                    foreach (var e in evs.Elements)
                        Visit(e);
                    break;
            }
        }

        void Visit(Element el)
        {
            if (!el.IsGeneric)
            {
                {
                    foreach (var p in el.Props.Where(p => IsPropertyElement(p) && !_propertyElementsEmitted.Contains(p)))
                    {
                        Report(new Diagnostic(
                            DiagnosticSeverity.Error, "FS1033",
                            $"property elements are not supported on '{el.Keyword}' yet ('{p.Key}' would be dropped)",
                            p.Location,
                            $"move '{p.Key}' to an element that supports it, or write this part in xaml {{ }}"));
                    }
                }
                if (!_passThroughEmitted.Contains(el))
                {
                    foreach (var p in el.Props.Where(IsScalarPassThrough))
                    {
                        Report(new Diagnostic(
                            DiagnosticSeverity.Error, "FS1033",
                            $"XAML attributes are not supported on '{el.Keyword}' yet ('{p.Key}' would be dropped)",
                            p.Location,
                            $"move '{p.Key}' to an element that supports it, or wrap the element in a stack"));
                    }
                }
            }

            foreach (var p in el.Props)
                Walk(p.Value);
            if (el.Body is BlockBody block)
            {
                foreach (var child in block.Children.OfType<ChildElement>())
                    Visit(child.Element);
            }
        }
    }
}
