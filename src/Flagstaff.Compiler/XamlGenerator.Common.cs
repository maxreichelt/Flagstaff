using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Flagstaff.Compiler;

// Universal props: CommonAttrs() is appended to the root tag of every emitter in Layout.cs and Inputs.cs.
public sealed partial class XamlGenerator
{
    /// <summary>
    /// Keywords whose emitter appends <see cref="CommonAttrs"/> to its root tag, so <c>align:</c>,
    /// <c>tooltip:</c>, <c>name:</c> and a pixel <c>width:</c> take effect. On any other keyword
    /// <see cref="LayoutValidator"/> reports those props as FS1002 rather than silently dropping them.
    /// An emitter that starts calling <c>CommonAttrs(el)</c> adds its keyword here.
    /// </summary>
    internal static readonly HashSet<string> CommonAttrsKeywords = new(StringComparer.Ordinal)
    {
        // Layout.cs
        "toolbar", "heading", "spacer", "separator", "text", "row", "column", "stack", "split",
        "group", "card", "cards",
        // Inputs.cs
        "button", "textbox", "number", "checkbox", "radio", "datepicker", "filePicker", "textarea",
        "toggle", "image", "progress", "spinner", "badge", "summary", "line", "stat", "link",
        // Tables.cs
        "select",
    };

    /// <summary>Keywords emitted as a <c>Control</c> (they carry <c>IsEnabled</c> for <c>enabled(...)</c>).</summary>
    internal static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "button", "textbox", "number", "checkbox", "radio", "datepicker", "filePicker", "textarea",
        "toggle", "link", "select",
    };

    /// <summary>
    /// True when the element's emitter appends <see cref="CommonAttrs"/> to its root tag: a keyword in
    /// <see cref="CommonAttrsKeywords"/>, or any generic element (<see cref="EmitGeneric"/> always does).
    /// </summary>
    internal static bool AppliesCommonAttrs(Element el) => el.IsGeneric || CommonAttrsKeywords.Contains(el.Keyword);

    /// <summary>
    /// True when the element is emitted as a <c>Control</c> (so <c>enabled(...)</c> becomes <c>IsEnabled</c>): a keyword in
    /// <see cref="ControlKeywords"/>, a generic element whose manifest type derives from <c>Control</c>, or a
    /// third-party (<c>prefix.Type</c>) element, which the manifest cannot judge (FS4002 already warns).
    /// </summary>
    internal static bool IsControl(Element el)
    {
        if (!el.IsGeneric)
            return ControlKeywords.Contains(el.Keyword);
        if (el.NamespacePrefix is not null)
            return true;
        return ControlManifest.Default.TryGetType(el.TypeName, out var type) && type.IsControl;
    }

    /// <summary>Elements whose <c>width:</c> became a Column/RowDefinition, so no <c>Width</c> attribute is emitted.</summary>
    private readonly HashSet<Element> _gridSized = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The attributes every emitter appends to its root tag: <c>when(...)</c> visibility, <c>enabled(...)</c>
    /// (controls only), then the universal and keyword props (<c>name</c>, <c>width</c>, <c>align</c>,
    /// <c>tooltip</c>, <c>placeholder</c>, <c>hint</c>, <c>readonly</c>, <c>min/max/step</c>).
    /// Grid placement (<c>Grid.Row</c>/<c>Grid.Column</c>) is carried by the container's wrapper element.
    /// </summary>
    private string CommonAttrs(Element el)
    {
        var sb = new StringBuilder();
        sb.Append(Conditions.VisibilityAttr(el));
        if (IsControl(el))
            sb.Append(Conditions.EnabledAttr(el));

        if (Prop(el, "name") is { } name && TextOf(name) is { Length: > 0 } xName)
            sb.Append($" x:Name=\"{Esc(xName)}\"");
        else if (_loadNames.TryGetValue(el, out var loadName))
            sb.Append($" x:Name=\"{loadName}\""); // x:Load needs x:Name (WMC0907)
        if (GetPropValue(el, "uid") is { Length: > 0 } uid)
            sb.Append($" x:Uid=\"{Esc(uid)}\"");
        if (Prop(el, "load") is LoadValue load)
            sb.Append($" x:Load=\"{BindExpr(load.Path)}\"");
        if (!_gridSized.Contains(el) && Prop(el, "width") is { } w && ParseWidth(w) is { IsPixel: true } width)
            sb.Append($" Width=\"{width.Xaml}\"");
        if (GetPropValue(el, "align") is { } align)
            sb.Append($" HorizontalAlignment=\"{char.ToUpperInvariant(align[0])}{align[1..]}\"");
        if (GetPropValue(el, "tooltip") is { } tooltip)
            sb.Append($" ToolTipService.ToolTip=\"{Esc(tooltip)}\"");
        if (GetPropValue(el, "placeholder") is { } placeholder)
            sb.Append($" PlaceholderText=\"{Esc(placeholder)}\"");
        if (GetPropValue(el, "hint") is { } hint)
            sb.Append($" Description=\"{Esc(hint)}\"");
        if (Prop(el, "readonly") is BoolValue { Value: true })
            sb.Append(" IsReadOnly=\"True\"");
        if (Number(Prop(el, "min")) is { } min)
            sb.Append($" Minimum=\"{min}\"");
        if (Number(Prop(el, "max")) is { } max)
            sb.Append($" Maximum=\"{max}\"");
        if (Number(Prop(el, "step")) is { } step)
            sb.Append($" SmallChange=\"{step}\"");
        sb.Append(PassThroughAttrs(el));
        sb.Append(EventAttrs(el));
        return sb.ToString();
    }

    /// <summary>Generated <c>x:Name</c>s for elements with <c>load:</c> and no <c>name:</c> (<see cref="NumberLoadNames"/>).</summary>
    private readonly Dictionary<Element, string> _loadNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// WinUI requires an <c>x:Name</c> on every element with <c>x:Load</c> (WMC0907). An element with
    /// <c>load: load(vm.X)</c> and no <c>name:</c> gets <c>x:Name="FlagstaffLoad_n"</c>, n 1-based per view in
    /// document order (view-level property elements, then the body; property values before children). The linter's
    /// FS3012 is the safety net for any <c>x:Load</c> that still has no name.
    /// </summary>
    private void NumberLoadNames(ViewDecl view)
    {
        _loadNames.Clear();
        foreach (var p in view.PropertyElements)
            Value(p.Value);
        foreach (var el in view.Body)
            Visit(el);

        void Value(PropValue value)
        {
            if (value is ElementValue ev)
                Visit(ev.Element);
            else if (value is ElementsValue evs)
                evs.Elements.ForEach(Visit);
        }

        void Visit(Element el)
        {
            if (Prop(el, "load") is LoadValue && !(Prop(el, "name") is { } n && TextOf(n) is { Length: > 0 }))
                _loadNames[el] = "FlagstaffLoad_" + (_loadNames.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var p in el.Props)
                Value(p.Value);
            if (el.Body is BlockBody block)
                foreach (var child in block.Children.OfType<ChildElement>())
                    Visit(child.Element);
        }
    }

    private static PropValue? Prop(Element el, string key) =>
        el.Props.FirstOrDefault(p => p.Key == key)?.Value;

    private static bool IsFill(Element el) => Prop(el, "fill") is BoolValue { Value: true };

    private static string? TextOf(PropValue value) => value switch
    {
        IdentValue iv => iv.Name,
        StringValue sv => sv.Value,
        _ => null,
    };

    private static string? Number(PropValue? value) => value switch
    {
        IntValue iv => iv.Value.ToString(CultureInfo.InvariantCulture),
        FloatValue fv => fv.Value.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>A parsed <c>width:</c> value: its XAML text (<c>Auto</c>, <c>240</c>, <c>*</c>, <c>2*</c>).</summary>
    internal readonly record struct WidthSpec(string Xaml, bool IsPixel);

    private static readonly Regex StarWidth = new(@"^([0-9]+(\.[0-9]+)?)?\*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The one width parser (layout children, <c>col</c>, validators): <c>auto</c>, a positive number,
    /// <c>*</c> or <c>N*</c> (the lexer turns <c>*</c>/<c>2*</c>/<c>10*</c> into an identifier value; the
    /// quoted forms <c>"2*"</c> / <c>"120"</c> are accepted too). Returns null for anything else.
    /// </summary>
    internal static WidthSpec? ParseWidth(PropValue value)
    {
        switch (value)
        {
            case IntValue { Value: > 0 } iv:
                return new WidthSpec(iv.Value.ToString(CultureInfo.InvariantCulture), true);
            case FloatValue { Value: > 0 } fv:
                return new WidthSpec(fv.Value.ToString(CultureInfo.InvariantCulture), true);
            case IdentValue or StringValue:
                var text = TextOf(value)!.Trim();
                if (text == "auto") return new WidthSpec("Auto", false);
                if (StarWidth.IsMatch(text))
                {
                    var factor = text[..^1];
                    if (factor.Length == 0) return new WidthSpec("*", false);
                    var f = double.Parse(factor, NumberStyles.Float, CultureInfo.InvariantCulture);
                    return f > 0 ? new WidthSpec(f.ToString(CultureInfo.InvariantCulture) + "*", false) : null;
                }
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && px > 0)
                    return new WidthSpec(px.ToString(CultureInfo.InvariantCulture), true);
                return null;
            default:
                return null;
        }
    }

    /// <summary>A <c>width:</c> value as a XAML GridLength (<see cref="ParseWidth"/>); missing or invalid is <c>*</c>.</summary>
    internal static string WidthToXaml(PropValue? value) =>
        value is not null && ParseWidth(value) is { } width ? width.Xaml : "*";

    /// <summary>The ColumnDefinition width for a grid child: its <c>width:</c>, else <c>*</c>.</summary>
    private string SlotWidth(Element child)
    {
        if (Prop(child, "width") is { } w)
        {
            _gridSized.Add(child);
            if (ParseWidth(w) is { } width)
                return width.Xaml;
        }
        return "*";
    }

    private void Report(Diagnostic diagnostic) => Diagnostics.Add(diagnostic);

    /// <summary>
    /// <c>, Mode=OneWay</c> for a view-model binding; <c>""</c> (x:Bind's OneTime default) for <c>item.X</c>:
    /// row types are plain generated classes, so a OneWay binding to them is WMC1506.
    /// </summary>
    private static string OneWayFor(string property) =>
        property.StartsWith("item.", StringComparison.Ordinal) ? "" : ", Mode=OneWay";

    /// <summary><c>{x:Bind Path[, Mode=OneWay][extra]}</c> for a parser-stored binding (<c>X</c> = vm.X, or <c>item.X</c>).</summary>
    private static string BindExpr(string property, string extra = "") =>
        $"{{x:Bind {Bind(property)}{OneWayFor(property)}{extra}}}";

    /// <summary>The raw (parser-stored) binding of a prop: <c>X</c> for vm.X, <c>item.X</c> for item.X.</summary>
    private static string? RawBinding(Element el, string key) =>
        el.Props.FirstOrDefault(p => p.Key == key)?.Value is BindingValue bv ? bv.Property : null;

    /// <summary>The converter suffix for a formatted value (<c>, Converter={StaticResource ...}</c>), or <c>""</c>.</summary>
    private static string FormatConverter(FormatClause? format) => format switch
    {
        { Kind: FormatKind.Currency } => ", Converter={StaticResource CurrencyConverter}",
        { Kind: FormatKind.Percent } => ", Converter={StaticResource PercentConverter}",
        { Kind: FormatKind.Date } => ", Converter={StaticResource DateConverter}",
        { Kind: FormatKind.Number, Precision: { } p } => $", Converter={{StaticResource NumberConverter}}, ConverterParameter=F{p}",
        { Kind: FormatKind.Number } => ", Converter={StaticResource NumberConverter}",
        _ => "",
    };
}
