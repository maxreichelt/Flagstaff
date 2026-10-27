using System.Globalization;
using System.Text;

namespace Flagstaff.Compiler;

// Generic elements: any WinUI type written in the element form (`Expander Header: "x" { ... }`,
// `lvc.CartesianChart Series: vm.Series;`). See docs/universal-element.md, section 2a. Validation (manifest, locality
// rule) happens before generation in GenericValidator; this file assumes a valid tree.
public sealed partial class XamlGenerator
{
    private static ControlManifest Manifest => ControlManifest.Default;

    private int _genericNavigateCount;

    /// <summary>
    /// Emits a generic element (<see cref="Element.IsGeneric"/>): open tag (<c>&lt;Expander</c>, <c>&lt;lvc:CartesianChart</c>),
    /// the label / single text child as the content property, the positional binding on the overlay's input property,
    /// scalar pass-through props (<see cref="RenderAttr"/>), the arrow, <see cref="CommonAttrs"/>; then property elements
    /// (<c>&lt;Type.Key&gt;</c>), then the children through <see cref="EmitElement"/> (several children of a single-object
    /// content property are wrapped in a <c>StackPanel Spacing="8"</c>). Self-closes when nothing is inside.
    /// </summary>
    private void EmitGeneric(Element el)
    {
        var tag = el.NamespacePrefix is { } prefix ? $"{prefix}:{el.TypeName}" : el.TypeName;
        var type = GenericValidator.ManifestTypeOf(el);
        var content = type is not null ? GenericValidator.ContentProperty(type) : null;
        IReadOnlyList<ElementOrText> children = el.Body is BlockBody block ? block.Children : [];

        var attrs = new StringBuilder();
        // key: CompactStat → x:Key first, as XAML writes it (GenericValidator.ValidateResources checks placement: FS1044/FS1045).
        if (el.Props.FirstOrDefault(p => p.Key == "key")?.Value is { } key && TextOf(key) is { Length: > 0 } xKey)
            attrs.Append($" x:Key=\"{Esc(xKey)}\"");
        if (el.Props.FirstOrDefault(p => p.Key == "_label")?.Value is StringValue label && content is not null)
            attrs.Append($" {content.Name}=\"{Esc(label.Value)}\"");

        var textAsAttr = children is [TextContent] && content is not null && GenericValidator.IsTextual(content);
        if (textAsAttr)
            attrs.Append($" {content!.Name}=\"{Esc(((TextContent)children[0]).Text)}\"");

        if (el.Props.FirstOrDefault(p => p.Key == "_binding")?.Value is BindingValue binding && type is not null
            && GenericValidator.FindInputProperty(type) is { } input)
            attrs.Append(RenderAttr(input.Name, binding, type, input));

        var propertyElements = new List<PropPair>();
        foreach (var prop in el.Props)
        {
            if (!AstExtensions.IsPassThroughKey(prop.Key))
                continue;
            var mprop = GenericValidator.ResolveProperty(type, prop.Key);
            if (prop.Value is ElementValue or ElementsValue || prop.Value is IdentValue iv && !IsTypeNameProperty(mprop) && IsTypeValue(iv.Name, mprop))
            {
                propertyElements.Add(prop);
                continue;
            }
            attrs.Append(RenderAttr(prop.Key, prop.Value, type, mprop));
        }

        attrs.Append(GenericActionAttrs(el, type));
        attrs.Append(CommonAttrs(el));

        var itemTemplate = IsGenericItemTemplate(el);
        var contentChildren = textAsAttr || itemTemplate ? [] : children;
        if (propertyElements.Count == 0 && contentChildren.Count == 0 && !itemTemplate)
        {
            Line($"<{tag}{attrs} />");
            return;
        }

        Line($"<{tag}{attrs}>");
        Indent();
        foreach (var prop in propertyElements)
            EmitGenericPropertyElement(tag, prop);
        if (itemTemplate)
            EmitGenericItemTemplate(tag, el.OfType!, children);
        else
            EmitGenericContent(contentChildren, content);
        Dedent();
        Line($"</{tag}>");
    }

    /// <summary>
    /// A WinUI items control written with <c>of T</c> and a body (<c>ListView ItemsSource: vm.Rows of Row { text
    /// item.Name; }</c>): the body is the row template, not the control's items. True when the element is generic,
    /// has <c>of T</c>, child elements, and its manifest type has an <c>ItemTemplate</c> property. Shared with
    /// <see cref="TypeInference"/>, <see cref="ItemTypeRegistry"/> and the overlay walk, which treat the body as a row.
    /// </summary>
    internal static bool IsGenericItemTemplate(Element el) =>
        el.IsGeneric && el.OfType is not null
        && el.Body is BlockBody block && block.Children.Any(c => c is ChildElement)
        && GenericValidator.ManifestTypeOf(el) is { } type
        && GenericValidator.ResolveProperty(type, "ItemTemplate") is not null;

    /// <summary>
    /// <c>&lt;Tag.ItemTemplate&gt;&lt;DataTemplate x:DataType="vm:T"&gt;</c> holding the body, emitted with
    /// <see cref="ConditionCompiler.InItemTemplate"/> set exactly like <c>list</c> (several children are stacked in a
    /// <c>StackPanel Spacing="4"</c>, a DataTemplate has one root).
    /// </summary>
    private void EmitGenericItemTemplate(string tag, string itemType, IReadOnlyList<ElementOrText> children)
    {
        Line($"<{tag}.ItemTemplate>");
        Indent();
        Line($"<DataTemplate x:DataType=\"vm:{itemType}\">");
        Indent();
        var visible = children.Count(c => c is TextContent || c is ChildElement { Element: var e } && !IsHoisted(e));
        var wrap = visible > 1;
        if (wrap)
        {
            Line("<StackPanel Spacing=\"4\">");
            Indent();
        }
        var wasInTemplate = Conditions.InItemTemplate;
        Conditions.InItemTemplate = true;
        try
        {
            foreach (var child in children)
            {
                if (child is ChildElement ce)
                    EmitElement(ce.Element);
                else if (child is TextContent tc)
                    Line($"<TextBlock Text=\"{Esc(tc.Text)}\" TextWrapping=\"Wrap\" />");
            }
        }
        finally
        {
            Conditions.InItemTemplate = wasInTemplate;
        }
        if (wrap)
        {
            Dedent();
            Line("</StackPanel>");
        }
        Dedent();
        Line("</DataTemplate>");
        Dedent();
        Line($"</{tag}.ItemTemplate>");
    }

    /// <summary><c>Key: Type { }</c> / <c>Key: { ... }</c> / <c>Icon: SymbolIcon;</c> → <c>&lt;Owner.Key&gt;...&lt;/Owner.Key&gt;</c> (dotted keys verbatim).</summary>
    private void EmitGenericPropertyElement(string tag, PropPair prop)
    {
        var propertyTag = prop.Key.Contains('.') ? prop.Key : $"{tag}.{prop.Key}";
        Line($"<{propertyTag}>");
        Indent();
        switch (prop.Value)
        {
            case ElementValue ev:
                EmitElement(ev.Element);
                break;
            case ElementsValue evs:
                EmitElements(evs.Elements);
                break;
            case IdentValue iv:
                Line($"<{iv.Name.Replace('.', ':')} />");
                break;
        }
        Dedent();
        Line($"</{propertyTag}>");
    }

    private void EmitGenericContent(IReadOnlyList<ElementOrText> children, ManifestProperty? content)
    {
        var visible = children.Count(c => c is TextContent || c is ChildElement { Element: var e } && !IsHoisted(e));
        var wrap = content is { IsCollection: false } && visible > 1;
        if (wrap)
        {
            Line("<StackPanel Spacing=\"8\">");
            Indent();
        }
        foreach (var child in children)
        {
            if (child is ChildElement ce)
                EmitElement(ce.Element);
            else if (child is TextContent tc)
                Line($"<TextBlock Text=\"{Esc(tc.Text)}\" TextWrapping=\"Wrap\" />");
        }
        if (wrap)
        {
            Dedent();
            Line("</StackPanel>");
        }
    }

    /// <summary>
    /// A bare name that is a type (<c>Icon: SymbolIcon;</c>, emitted as an empty element) rather than an enum member,
    /// a named color or a Symbol name. A <c>prefix.Type</c> name is always a type.
    /// </summary>
    private static bool IsTypeValue(string name, ManifestProperty? prop)
    {
        if (prop is not null && ReferenceEquals(prop, SetterValue.Value))
            return false; // Setter Value: Inline → Value="Inline" (an enum member of the target; objects take Type { } form)
        if (name.Contains('.'))
            return true;
        if (prop?.EnumMembers?.Contains(name) == true || !Manifest.TryGetType(name, out var type))
            return false;
        if (prop is null)
            return true;
        return Manifest.ShapeFor(prop) switch
        {
            ValueShape.Brush or ValueShape.Color => !GenericValidator.NamedColors.Contains(name),
            ValueShape.Icon => Manifest.DerivesFrom(type, "IconElement"),
            ValueShape.GridLength => false,
            _ => true,
        };
    }

    /// <summary>
    /// <c>Setter.Value</c> (object): a bare name there is the value of the property the setter targets (an enum member or
    /// a named color, typed by <c>GenericValidator.ValidateResources</c>), never an empty element, even when the name is
    /// also a WinUI type (<c>Inline</c>, <c>Image</c>).
    /// </summary>
    private static readonly Lazy<ManifestProperty?> SetterValue = new(() =>
        Manifest.TryGetType("Setter", out var setter) && Manifest.TryGetProperty(setter, "Value", out var value, out _) ? value : null);

    /// <summary>
    /// The XAML attribute text (<c> Key="..."</c>, leading space) for a scalar pass-through value, or <c>""</c> for values
    /// emitted as property elements (<see cref="ElementValue"/>, <see cref="ElementsValue"/>, a bare type name) or not
    /// renderable. <paramref name="ownerType"/>/<paramref name="prop"/> are the manifest type of the element whose tag
    /// receives the attribute and the property behind <paramref name="key"/> (null when unknown: third-party elements).
    /// <list type="bullet">
    /// <item><c>vm.X</c> → <c>{x:Bind ViewModel.X, Mode=TwoWay|OneWay}</c> (manifest input properties are TwoWay);
    /// <c>item.X</c> → <c>{x:Bind X}</c> (row types are plain classes: OneTime).</item>
    /// <item><c>theme(K)</c> → <c>{ThemeResource K}</c>; <c>resource(K)</c> → <c>{StaticResource K}</c>;
    /// <c>load(vm.X)</c> → <c>x:Load="{x:Bind ViewModel.X, Mode=OneWay}"</c> (whatever the key).</item>
    /// <item><c>ref(N).M</c> on a reference property: <c>N.M</c> (TargetPath) or <c>N</c> / <c>{x:Bind N}</c> (Name);
    /// on any other property an element binding <c>{x:Bind N.M, Mode=OneWay}</c>.</item>
    /// <item>Type-name properties (<see cref="IsTypeNameProperty"/>): a bare name or <c>prefix.Type</c> is the attribute
    /// string (<c>TargetType="Button"</c>, <c>"local:Card"</c>).</item>
    /// <item>Bare names (enum members in the manifest's spelling, named colors, <c>*</c>/<c>2*</c>), literals (<c>True</c>, numbers, escaped strings,
    /// <c>#hex</c>, <c>{x:Null}</c>); GridLength values go through <see cref="ParseWidth"/> (<c>auto</c> → <c>Auto</c>).</item>
    /// </list>
    /// </summary>
    internal string RenderAttr(string key, PropValue value, ManifestType? ownerType, ManifestProperty? prop)
    {
        string Attr(string text) => $" {key}=\"{text}\"";
        var isGridLength = prop is not null && Manifest.ShapeFor(prop) == ValueShape.GridLength;

        switch (value)
        {
            case BindingValue b:
                if (b.Property.StartsWith("item.", StringComparison.Ordinal))
                    return Attr($"{{x:Bind {Bind(b.Property)}}}");
                var mode = GenericValidator.ModeFor(ownerType, prop) == BindingMode.TwoWay ? "TwoWay" : "OneWay";
                return Attr($"{{x:Bind {Bind(b.Property)}, Mode={mode}}}");
            case ThemeResourceValue tr:
                return Attr($"{{ThemeResource {tr.Key}}}");
            case StaticResourceValue sr:
                return Attr($"{{StaticResource {sr.Key}}}");
            case LoadValue lv:
                return $" x:Load=\"{BindExpr(lv.Path)}\"";
            case RefValue rv:
                return Attr(RefText(key, rv, ownerType, prop));
            case IdentValue iv:
                if (IsTypeNameProperty(prop))
                {
                    // TargetType="Button" / "local:OrderCard"; Setter Property="Padding" / "Grid.Row" (owner-qualified).
                    var isDependencyProperty = prop!.TypeName == "Microsoft.UI.Xaml.DependencyProperty";
                    return Attr(Esc(isDependencyProperty ? iv.Name : iv.Name.Replace('.', ':')));
                }
                if (isGridLength && ParseWidth(iv) is { } star)
                    return Attr(star.Xaml);
                return IsTypeValue(iv.Name, prop) ? "" : Attr(Esc(EnumSpelling(iv.Name, prop)));
            case BoolValue bv:
                return Attr(bv.Value ? "True" : "False");
            case IntValue or FloatValue:
                return Attr(Number(value)!);
            case StringValue sv:
                if (isGridLength && ParseWidth(sv) is { } width)
                    return Attr(width.Xaml);
                return Attr(Esc(EnumSpelling(sv.Value, prop)));
            case ColorValue cv:
                return Attr(cv.Hex);
            case NullValue:
                return Attr("{x:Null}");
            default:
                return "";
        }
    }

    /// <summary>An enum member as the manifest spells it (<c>wrap</c> → <c>Wrap</c>); other text unchanged.</summary>
    private static string EnumSpelling(string text, ManifestProperty? prop) =>
        prop?.EnumMembers?.FirstOrDefault(m => string.Equals(m, text, StringComparison.OrdinalIgnoreCase)) ?? text;

    /// <summary>
    /// A property whose value names a type or a dependency property (<c>Style.TargetType</c>,
    /// <c>ControlTemplate.TargetType</c>, <c>Frame.SourcePageType</c>: <c>System.Type</c>, projected from
    /// <c>Interop.TypeName</c>; <c>Setter.Property</c>: <c>DependencyProperty</c>). XAML writes these as plain attribute
    /// strings (<c>TargetType="Button"</c>, <c>Property="Padding"</c>), never as a property element.
    /// </summary>
    internal static bool IsTypeNameProperty(ManifestProperty? prop) =>
        prop?.TypeName is "Type" or "System.Type" or "Windows.UI.Xaml.Interop.TypeName" or "Microsoft.UI.Xaml.Interop.TypeName"
            or "Microsoft.UI.Xaml.DependencyProperty";

    /// <summary>The attribute value for <c>ref(Name)[.Member]</c> (see <see cref="RenderAttr"/>).</summary>
    private string RefText(string key, RefValue rv, ManifestType? ownerType, ManifestProperty? prop)
    {
        var qualified = key.Contains('.') || ownerType is null ? key : $"{ownerType.Name}.{key}";
        var inTemplate = _conditions?.InItemTemplate == true;
        switch (Manifest.ReferenceEmitFor(qualified))
        {
            case ReferenceEmit.TargetPath:
                return rv.MemberPath is { } member ? $"{rv.Name}.{member}" : rv.Name;
            case ReferenceEmit.Name:
                if (prop is null || prop.TypeName is "string" or "object")
                    return rv.Name;
                return inTemplate ? $"{{Binding ElementName={rv.Name}}}" : $"{{x:Bind {rv.Name}}}";
            default:
                if (inTemplate)
                    return rv.MemberPath is { } path ? $"{{Binding ElementName={rv.Name}, Path={path}}}" : $"{{Binding ElementName={rv.Name}}}";
                return $"{{x:Bind {rv.Name}{(rv.MemberPath is { } m ? "." + m : "")}, Mode=OneWay}}";
        }
    }

    /// <summary>
    /// The arrow on a generic element: <c>-> vm.XCommand</c> binds <c>Command</c> exactly like <c>button</c>
    /// (<see cref="ActionAttrs"/>, including the item-template form); <c>-> navigate(View)</c> wires <c>Click</c> to a
    /// generated handler typed by the manifest's event args.
    /// </summary>
    private string GenericActionAttrs(Element el, ManifestType? type)
    {
        if (el.NavTarget is not { } target)
            return "";
        if (target.StartsWith("vm.", StringComparison.Ordinal) || target == CancelTarget)
            return ActionAttrs(el);

        var args = type is not null && Manifest.TryGetEvent(type, "Click", out var evt) ? evt.ArgsTypeName : "Microsoft.UI.Xaml.RoutedEventArgs";
        var name = "OnNavigate_" + (++_genericNavigateCount).ToString(CultureInfo.InvariantCulture);
        var nl = Environment.NewLine;
        ExtraCodeBehindMembers.Add(
            $"    private void {name}(object sender, {args} e){nl}    {{{nl}        Frame.Navigate(typeof({target}));{nl}    }}");
        return $" Click=\"{name}\"";
    }
}
