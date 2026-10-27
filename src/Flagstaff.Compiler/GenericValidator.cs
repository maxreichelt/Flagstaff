using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Flagstaff.Compiler;

/// <summary>
/// Manifest-driven checks for generic elements (<c>Expander</c>, <c>lvc.CartesianChart</c>) and for the pass-through
/// keys (<c>Margin:</c>, <c>Grid.Row:</c>, <c>Flyout:</c>) of any element, under the locality rule: an attribute
/// passes through only when its correctness is local to the element; anything else is compiler-checked here or rejected.
/// Run by <see cref="SemanticValidator"/> before its schema walk; it also repairs bare enum values the parser could not
/// tell from elements (<see cref="TryRepairBareEnum"/>).
/// <list type="bullet">
/// <item>FS1030 unknown (or abstract) WinUI type</item>
/// <item>FS1031 unknown property on the type (also an unknown member in <c>ref(Name).Member</c>)</item>
/// <item>FS1032 value shape mismatch (expected CLR type / enum members)</item>
/// <item>FS1035 children not allowed (no content property, one-child content, wrong item type)</item>
/// <item>FS1037 unknown attached-property owner (<c>Gird.Row</c>)</item>
/// <item>FS1038 <c>Grid.Row/Column/RowSpan/ColumnSpan</c> outside a generic <c>Grid</c> with definitions, or past them</item>
/// <item>FS1039 <c>ref(Name)</c> names no <c>name:</c> declaration in the view</item>
/// <item>FS1040 a reference property written as a string (or without the member it needs)</item>
/// <item>FS1041 attached property not in the locality allowlist (<c>Manifest/overlay.json</c>)</item>
/// <item>FS1004 label / positional binding the type cannot take</item>
/// <item>FS1002 a lowercase layout prop on a type that is not a FrameworkElement</item>
/// <item>FS1012 (warning) <c>enabled(...)</c> on a type that is not a Control</item>
/// <item>FS2014 <c>-&gt;</c> on a type without a Command property / Click event</item>
/// <item>FS1034 / FS1043 / FS1024 for <c>on Event -&gt; target</c> clauses (<see cref="ValidateEvents"/>)</item>
/// <item>FS4001 (warning) the type has a keyword (<c>TextBox</c> → <c>textbox</c>)</item>
/// <item>FS4002 (warning) a <c>use</c>-namespace element the manifest does not know: own properties pass unvalidated</item>
/// <item>FS1044 <c>key:</c> outside a resources block (or on a keyword), or a resource entry without one</item>
/// <item>FS1045 the same key twice in one resource dictionary</item>
/// </list>
/// <see cref="ValidateResources"/> adds the resource, style, visual-state and storyboard checks (value forms included).
/// </summary>
public static class GenericValidator
{
    private static ControlManifest M => ControlManifest.Default;

    /// <summary>Checks every element of the view (children, property-element values, view-level property elements).</summary>
    public static void Validate(ViewDecl view, List<Diagnostic> diagnostics)
    {
        foreach (var prop in view.PropertyElements)
            WalkValue(prop, view, diagnostics);
        foreach (var el in view.Body)
        {
            CheckUiContent(el, diagnostics);
            Walk(el, view, diagnostics);
        }
        ValidateEvents(view, diagnostics);
        ValidateResources(view, diagnostics);
    }

    private static void Walk(Element el, ViewDecl view, List<Diagnostic> diagnostics, bool inTemplate = false)
    {
        RepairBareEnums(el, diagnostics);

        if (el.IsGeneric)
            CheckGeneric(el, view, diagnostics);
        else
            CheckKeywordPassThrough(el, view, diagnostics);

        if (inTemplate)
            CheckTemplateScope(el, diagnostics);
        // A DataTemplate / ControlTemplate written as a generic element changes the binding root (scope change): nothing
        // inside may bind, because the compiler does not know the template's data type. `list ... of T` does.
        var childInTemplate = inTemplate || ManifestTypeOf(el) is { } t && el.IsGeneric && M.DerivesFrom(t, "FrameworkTemplate");

        foreach (var prop in el.Props)
            WalkValue(prop, view, diagnostics, childInTemplate);
        if (el.Body is BlockBody block)
        {
            foreach (var child in block.Children.OfType<ChildElement>())
            {
                if (!el.IsGeneric && el.Keyword != "xaml")
                    CheckUiContent(child.Element, diagnostics); // generic parents: CheckChildren knows their item type
                Walk(child.Element, view, diagnostics, childInTemplate);
            }
        }
    }

    /// <summary>
    /// FS1035 for a WinUI type that is not UI content where UI content is expected: a direct child of the view body or
    /// of a keyword element (all keyword containers and templates hold UIElements). <c>RowDefinition</c>, <c>Style</c>,
    /// <c>SolidColorBrush</c>, <c>Setter</c> ... belong in property elements (<c>RowDefinitions: { }</c>,
    /// <c>Page.Resources: { }</c>, <c>Setters: { }</c>). Flyouts and menu items are let through (keywords that take them
    /// check them). Third-party and unknown types are not judged here.
    /// </summary>
    private static void CheckUiContent(Element el, List<Diagnostic> diagnostics)
    {
        if (!el.IsGeneric || el.NamespacePrefix is not null || !M.TryGetType(el.TypeName, out var type) || type.IsUIElement
            || M.DerivesFrom(type, "FlyoutBase") || M.DerivesFrom(type, "MenuFlyoutItemBase"))
            return;
        var hint = type.Name switch
        {
            "RowDefinition" => "RowDefinition belongs in Grid RowDefinitions: { RowDefinition Height: Auto; ... }",
            "ColumnDefinition" => "ColumnDefinition belongs in Grid ColumnDefinitions: { ColumnDefinition Width: \"*\"; ... }",
            "Setter" => "Setter belongs in a Style { ... } or a VisualState's Setters: { ... }",
            "VisualStateGroup" or "VisualState" => "visual states belong in VisualStateManager.VisualStateGroups: { VisualStateGroup { ... } }",
            _ when M.DerivesFrom(type, "Style") || M.DerivesFrom(type, "Brush") || M.DerivesFrom(type, "Storyboard")
                   || M.DerivesFrom(type, "FrameworkTemplate") => $"{type.Name} belongs in Page.Resources: {{ {type.Name} key: Name ...; }}",
            _ => $"set it as a property of an element instead (Key: {type.Name} {{ ... }})",
        };
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error, "FS1035",
            $"'{type.Name}' is not a UI element and cannot be content here",
            el.Location, hint));
    }

    private static void WalkValue(PropPair prop, ViewDecl view, List<Diagnostic> diagnostics, bool inTemplate = false)
    {
        switch (prop.Value)
        {
            case ElementValue ev:
                Walk(ev.Element, view, diagnostics, inTemplate);
                break;
            case ElementsValue evs:
                foreach (var element in evs.Elements)
                    Walk(element, view, diagnostics, inTemplate);
                break;
        }
    }

    private static void CheckTemplateScope(Element el, List<Diagnostic> diagnostics)
    {
        SourceLocation? location = el.Props.FirstOrDefault(p => p.Value is BindingValue or LoadValue)?.Location
                                   ?? el.When?.Location ?? el.Enabled?.Location
                                   ?? (el.NavTarget is not null ? el.Location : null);
        if (location is not { } at)
            return;
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error, "FS2005",
            "Bindings, conditions and commands cannot be used inside a template written as a generic element; its binding root is unknown",
            at,
            "use list items: vm.X of T { ... } for row templates, or xaml { } for a hand-written template"));
    }

    // ── Parser repair ──────────────────────────────────────────────────

    /// <summary>Elements already reported by the repair pass (no second FS1030 for them).</summary>
    private static readonly ConditionalWeakTable<Element, object> s_reported = new();

    private static void RepairBareEnums(Element owner, List<Diagnostic> diagnostics)
    {
        var ownerType = ManifestTypeOf(owner);
        for (var i = 0; i < owner.Props.Count; i++)
        {
            var prop = owner.Props[i];
            if (!AstExtensions.IsPassThroughKey(prop.Key) || prop.Value is not ElementValue { Element: var value })
                continue;
            if (TryRepairBareEnum(owner, prop, M, out var repaired, out var moved, ownerType))
            {
                owner.Props[i] = repaired;
                owner.Props.InsertRange(i + 1, moved);
                continue;
            }

            // Neither a type nor a member: if the property is an enum, say so instead of "unknown type".
            if (value.NamespacePrefix is null && !M.TryGetType(value.TypeName, out _)
                && ResolveProperty(ownerType, prop.Key) is { EnumMembers: { } members })
            {
                s_reported.AddOrUpdate(value, value);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1032",
                    $"'{value.TypeName}' is not a value of '{prop.Key}' (one of {List(members, 12)})",
                    value.Location,
                    SemanticValidator.Closest(value.TypeName, members) is { } close
                        ? $"did you mean '{close}'?"
                        : $"write {prop.Key}: {members[0]}; strings are quoted (\"...\")"));
            }
        }
    }

    /// <summary>
    /// Repairs <c>Orientation: Horizontal Spacing: 8</c>, which the parser reads as an element <c>Horizontal</c> owning
    /// <c>Spacing: 8</c>: when <paramref name="prop"/> holds an <see cref="ElementValue"/> with no body, label, binding or
    /// clauses whose name is not a manifest type but is a member of the property's enum (or a named color for a
    /// Brush/Color property), <paramref name="repaired"/> is the prop with an <see cref="IdentValue"/> and
    /// <paramref name="movedProps"/> are the element's props, which belong back on <paramref name="owner"/> right after it.
    /// <paramref name="ownerType"/> is the owner's manifest type; null resolves it from a generic owner (keyword owners
    /// pass their root type).
    /// </summary>
    public static bool TryRepairBareEnum(Element owner, PropPair prop, ControlManifest m, out PropPair repaired,
        out List<PropPair> movedProps, ManifestType? ownerType = null)
    {
        repaired = prop;
        movedProps = [];
        if (prop.Value is not ElementValue { Element: var v })
            return false;
        if (!v.IsGeneric || v.NamespacePrefix is not null || v.Body is not EmptyBody
            || v.When is not null || v.Enabled is not null || v.Format is not null || v.OfType is not null
            || v.NavTarget is not null || v.Events.Count > 0
            || v.Props.Any(p => p.Key is "_label" or "_binding"))
            return false;

        ownerType ??= owner.IsGeneric && owner.NamespacePrefix is null && m.TryGetType(owner.TypeName, out var t) ? t : null;
        var target = ResolveProperty(ownerType, prop.Key, m);
        if (target is null)
            return false;
        // An enum member wins over a type of the same name: an enum property never holds an object
        // (`Modifiers: Control Key: S` is VirtualKeyModifiers.Control, not the Control type).
        var isMember = target.EnumMembers?.Contains(v.TypeName) == true
                       || (!m.TryGetType(v.TypeName, out _)
                           && m.ShapeFor(target) is ValueShape.Brush or ValueShape.Color && NamedColors.Contains(v.TypeName));
        if (!isMember)
            return false;

        repaired = prop with { Value = new IdentValue(v.TypeName) };
        movedProps = [.. v.Props];
        return true;
    }

    private static readonly Lazy<HashSet<string>> s_valueNames = new(() =>
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in M.Types)
            foreach (var p in type.Properties.Values)
                if (p.EnumMembers is { } members) names.UnionWith(members);
        foreach (var attached in M.AttachedProperties)
            if (M.TryGetValueType(attached.TypeName, out var vt) && vt.EnumMembers is { } attachedMembers) names.UnionWith(attachedMembers);
        names.UnionWith(NamedColors!); // declared below: nullable analysis follows field order
        names.RemoveWhere(n => M.TryGetType(n, out _));
        return names;
    });

    /// <summary>
    /// True for a PascalCase name that the manifest knows as a value (a WinUI enum member or a named color) and not as a
    /// type. The parser reads such a name under a pass-through key as a bare name even when keys or a block follow, so
    /// <c>StackPanel Orientation: Horizontal Spacing: 8 { ... }</c> keeps <c>Spacing</c> and the block on the StackPanel.
    /// </summary>
    internal static bool IsBareValueName(string name) => s_valueNames.Value.Contains(name);

    /// <summary>
    /// UWP controls that WinUI 3 / Windows App SDK does not have (checked against the manifest), with what to use instead.
    /// FS1030 says so instead of suggesting a look-alike name or a third-party <c>use</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> UwpOnlyControls = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["InkCanvas"] = "draw strokes yourself: a Canvas name: Sketch Background: White fill, with PointerPressed/PointerMoved/PointerReleased handlers "
                        + "subscribed in your page partial (partial void OnViewCreated) that add Polyline children (Microsoft.UI.Xaml.Shapes); or a third-party ink control via use",
        ["InkToolbar"] = "build the toolbar from AppBarToggleButton/ToggleButton, a ColorPicker or ComboBox for the color, and a Slider for the size",
        ["MediaElement"] = "use MediaPlayerElement (Source: \"...\" AreTransportControlsEnabled: true)",
        ["WebView"] = "use WebView2 (Source: \"https://...\")",
        ["CaptureElement"] = "use MediaPlayerElement with a MediaCapture frame source (MediaSource.CreateFromMediaFrameSource) set from code",
        ["SearchBox"] = "use AutoSuggestBox",
        ["SettingsFlyout"] = "use a settings page or a ContentDialog",
    };

    /// <summary>The replacement hint for a UWP-only control (also every <c>InkToolbar*</c> button), or null.</summary>
    public static string? UwpOnlyControl(string typeName) =>
        UwpOnlyControls.TryGetValue(typeName, out var hint) ? hint
        : typeName.StartsWith("InkToolbar", StringComparison.Ordinal) ? UwpOnlyControls["InkToolbar"]
        : null;

    // ── Generic elements ───────────────────────────────────────────────

    private static void CheckGeneric(Element el, ViewDecl view, List<Diagnostic> diagnostics)
    {
        if (el.NamespacePrefix is not null)
        {
            // An undeclared prefix is FS1036 (parser). The manifest holds WinUI only: own properties, label, children
            // and arrow pass unvalidated; attached properties and ref() are still checked (they do not depend on the type).
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning, "FS4002",
                $"'{el.Keyword}' is not in the control manifest; its properties are passed through unvalidated",
                el.Location,
                $"check property names against the '{el.NamespacePrefix}' library's documentation"));
            CheckKeysWithoutType(el, view, diagnostics);
            CheckUntypedBindings(el, diagnostics);
            CheckOfTypeUsed(el, null, diagnostics);
            return;
        }

        if (!M.TryGetType(el.TypeName, out var type))
        {
            if (s_reported.TryGetValue(el, out _))
                return;
            if (UwpOnlyControl(el.TypeName) is { } uwp)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1030",
                    $"'{el.TypeName}' is a UWP control: WinUI 3 (Windows App SDK) has no {el.TypeName}",
                    el.Location, uwp));
                return;
            }
            var suggestions = M.SuggestTypes(el.TypeName);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1030",
                $"Unknown WinUI type '{el.TypeName}'" + (suggestions.Count > 0 ? $"; did you mean '{suggestions[0]}'?" : ""),
                el.Location,
                (suggestions.Count > 1 ? $"close: {string.Join(", ", suggestions)}; " : "")
                + "a third-party control needs use \"<assembly>\" as prefix; and prefix.Type; a value is quoted (\"...\")"));
            return;
        }
        if (type.IsAbstract)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1030",
                $"'{el.TypeName}' is an abstract WinUI type and cannot be written as an element",
                el.Location, "use a concrete type that derives from it"));
            return;
        }

        if (M.KeywordFor(type) is { } keyword)
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning, "FS4001",
                $"'{el.TypeName}' has a keyword: use '{keyword}'",
                el.Location, "keywords carry Flagstaff's defaults (bindings, styles, layout); see LANG.md"));

        CheckPassThroughProps(el, type, view, diagnostics);
        CheckOfTypeUsed(el, type, diagnostics);
        CheckPositional(el, type, diagnostics);
        CheckChildren(el, type, diagnostics);
        CheckArrow(el, type, diagnostics);
        CheckLayoutProps(el, type, diagnostics);
    }

    /// <summary>
    /// FS1047: <c>of T</c> on a generic element that types nothing (no binding right before it, no collection or
    /// <c>SelectedItem</c> binding, no item-template body), so it would be ignored silently. On a manifest type the
    /// element's <c>of T</c> types its collection property and <c>SelectedItem</c>; on a third-party element only the
    /// binding it follows.
    /// </summary>
    private static void CheckOfTypeUsed(Element el, ManifestType? type, List<Diagnostic> diagnostics)
    {
        if (el.OfType is not { } ofType || el.Props.Any(p => p.TypeArg is not null) || XamlGenerator.IsGenericItemTemplate(el))
            return;
        if (type is not null && el.Props.Any(p => p.Value is BindingValue && AstExtensions.IsPassThroughKey(p.Key)
                && ResolveProperty(type, p.Key) is { } mp && (TypeInference.IsCollectionProperty(mp) || mp.Name == "SelectedItem")))
            return;
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error, "FS1047",
            $"'of {ofType}' types nothing on '{el.Keyword}'",
            el.Location,
            $"write it right after the binding it types (ItemsSource: vm.Items of {ofType}), or remove it"));
    }

    /// <summary>
    /// FS4003 (warning): a <c>vm.X</c>/<c>item.X</c> binding under an own property of an element the manifest does not
    /// know infers <c>object</c> (<see cref="TypeInference.ManifestTypeFor"/>); <c>of T</c> after the binding types it
    /// (<c>Series: vm.Series of ISeries</c> → <c>ObservableCollection&lt;ISeries&gt;</c>, <c>Value: vm.Volume of
    /// double</c> → <c>double</c>). Attached (dotted) properties are typed by the manifest and not reported.
    /// </summary>
    private static void CheckUntypedBindings(Element el, List<Diagnostic> diagnostics)
    {
        foreach (var prop in el.Props)
        {
            if (prop.Value is not BindingValue binding || !AstExtensions.IsPassThroughKey(prop.Key) || prop.Key.Contains('.')
                || prop.TypeArg is not null)
                continue;
            var path = binding.Property.StartsWith("item.", StringComparison.Ordinal) ? binding.Property : "vm." + binding.Property;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning, "FS4003",
                $"binding '{prop.Key}: {path}' on an unvalidated element infers object; add `of <Type>`",
                prop.Location,
                $"write {prop.Key}: {path} of <Type> (a built-in type such as double is used as is; any other type T becomes ObservableCollection<T>)"));
        }
    }

    /// <summary>
    /// Validates the PascalCase and dotted props of <paramref name="el"/> against <paramref name="rootType"/> (the generic
    /// element's type, or a keyword's emitted root type): existence (FS1031), value shape (FS1032), locality of attached
    /// properties (FS1038/FS1040/FS1041) and <c>ref()</c> resolution (FS1039/FS1040).
    /// </summary>
    public static void CheckPassThroughProps(Element el, ManifestType rootType, ViewDecl view, List<Diagnostic> diagnostics)
    {
        foreach (var prop in el.Props)
        {
            if (!AstExtensions.IsPassThroughKey(prop.Key))
                continue;
            if (CheckLoad(prop, diagnostics) || prop.Value is ElementValue { Element: var reported } && s_reported.TryGetValue(reported, out _))
                continue;
            if (prop.Key.Contains('.'))
            {
                CheckDotted(el, prop, rootType, view, diagnostics);
                continue;
            }
            if (rootType.Name == "Setter" && prop.Key == "Value")
                continue; // typed against the setter's Property / Target member by ValidateResources
            if (!M.TryGetProperty(rootType, prop.Key, out var mprop, out var declaring))
            {
                var suggestions = M.SuggestProperties(rootType, prop.Key);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1031",
                    $"'{rootType.Name}' has no property '{prop.Key}'" + (suggestions.Count > 0 ? $"; did you mean '{suggestions[0]}'?" : ""),
                    prop.Location,
                    suggestions.Count > 1 ? $"close: {string.Join(", ", suggestions)}"
                        : $"'{rootType.Name}' declares: {List(rootType.Properties.Keys.Order(StringComparer.Ordinal).ToList(), 8)}"));
                continue;
            }
            CheckValue(prop, $"{declaring.Name}.{mprop.Name}", mprop, rootType.Name, view, diagnostics);
        }
    }

    /// <summary>
    /// Keyword elements: props are checked against the keyword's root type (<see cref="KeywordRootType"/>) through
    /// <see cref="CheckPassThroughProps"/>; keywords without one check attached properties and <c>ref()</c> values only.
    /// </summary>
    private static void CheckKeywordPassThrough(Element el, ViewDecl view, List<Diagnostic> diagnostics)
    {
        if (KeywordRootType(el) is { } rootType)
            CheckPassThroughProps(el, rootType, view, diagnostics);
        else
            CheckKeysWithoutType(el, view, diagnostics);
    }

    /// <summary>
    /// The manifest type of a keyword's root tag (<see cref="KeywordSchema.RootType"/>); null for keywords without one
    /// (<c>xaml</c>, <c>col</c>, <c>empty</c>). Variant tags (<c>heading</c> with <c>icon:</c> → StackPanel, a string
    /// <c>badge</c> → Border, <c>stack</c> with a <c>fill</c> child → Grid) are validated against this declared type;
    /// <c>XamlGenerator.EmitRoot</c> reports FS1031 when a prop is not also on the tag actually emitted.
    /// </summary>
    internal static ManifestType? KeywordRootType(Element el) =>
        !el.IsGeneric && ElementSchema.All.TryGetValue(el.Keyword, out var schema) && schema.RootType is { } name
        && M.TryGetType(name, out var type)
            ? type
            : null;

    /// <summary>The manifest type of an element: a generic element's type, a keyword's root type, or null.</summary>
    internal static ManifestType? ManifestTypeOf(Element el)
    {
        if (!el.IsGeneric)
            return KeywordRootType(el);
        return el.NamespacePrefix is null && M.TryGetType(el.TypeName, out var type) ? type : null;
    }

    /// <summary>Third-party elements and keywords without a root type: only dotted keys and <c>ref()</c> values.</summary>
    private static void CheckKeysWithoutType(Element el, ViewDecl view, List<Diagnostic> diagnostics)
    {
        foreach (var prop in el.Props)
        {
            if (!AstExtensions.IsPassThroughKey(prop.Key))
                continue;
            if (CheckLoad(prop, diagnostics))
                continue;
            if (prop.Key.Contains('.'))
                CheckDotted(el, prop, ownerType: null, view, diagnostics);
            else if (prop.Value is RefValue rv)
                ResolveRef(rv, prop, view, diagnostics);
        }
    }

    /// <summary>
    /// <c>x:Load</c> is the universal <c>load: load(vm.X)</c> (checked by <see cref="ValidateResources"/>). The
    /// PascalCase spelling <c>Load: load(vm.X)</c> is FS1046 (one spelling only); <c>Load:</c> with another value is
    /// FS1032, and <c>load()</c> under any other key is FS1032. True when handled.
    /// </summary>
    private static bool CheckLoad(PropPair prop, List<Diagnostic> diagnostics)
    {
        if (prop.Key == "Load")
        {
            diagnostics.Add(prop.Value is LoadValue lv
                ? new Diagnostic(
                    DiagnosticSeverity.Error, "FS1046",
                    "'Load:' is not a property; x:Load is the universal prop load:",
                    prop.Location, $"use the universal prop load: (write load: load({(lv.Path.StartsWith("item.", StringComparison.Ordinal) ? lv.Path : "vm." + lv.Path)});)")
                : new Diagnostic(
                    DiagnosticSeverity.Error, "FS1032",
                    $"'Load' (x:Load) expects load(vm.X), got {Describe(prop.Value)}",
                    prop.Location, "write load: load(vm.ShowDetails);"));
            return true;
        }
        if (prop.Value is LoadValue)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1032",
                $"load(...) is only valid as load: load(vm.X) (x:Load), not under '{prop.Key}'",
                prop.Location, "write load: load(vm.ShowDetails);"));
            return true;
        }
        return false;
    }

    // ── Attached properties and the locality rule ─────────────────────

    private static readonly Lazy<HashSet<string>> s_attachedOwners = new(() =>
        M.AttachedProperties.Select(a => a.Owner).ToHashSet(StringComparer.Ordinal));

    private static void CheckDotted(Element el, PropPair prop, ManifestType? ownerType, ViewDecl view, List<Diagnostic> diagnostics)
    {
        var dot = prop.Key.LastIndexOf('.');
        var owner = prop.Key[..dot];
        var member = prop.Key[(dot + 1)..];

        // `Grid.RowDefinitions:` on a Grid: the element's own property, qualified.
        if (ownerType is not null && M.BaseChain(ownerType).Any(t => t.Name == owner)
            && M.TryGetProperty(ownerType, member, out var own, out var declaring))
        {
            CheckValue(prop, $"{declaring.Name}.{own.Name}", own, ownerType.Name, view, diagnostics);
            return;
        }

        if (!M.TryGetAttachedProperty(prop.Key, out var attached))
        {
            var ownerKnown = M.TryGetType(owner, out _) || s_attachedOwners.Value.Contains(owner);
            if (!ownerKnown && SemanticValidator.Closest(owner, s_attachedOwners.Value) is { } close)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1037",
                    $"Unknown attached-property owner '{owner}' in '{prop.Key}'; did you mean '{close}.{member}'?",
                    prop.Location,
                    $"attached properties of {close}: {List(AttachedNames(close), 8)}"));
            }
            else if (!ownerKnown)
            {
                diagnostics.Add(NotAllowlisted(prop, $"'{owner}' is not a WinUI type with attached properties"));
            }
            else
            {
                var names = AttachedNames(owner);
                var suggestion = SemanticValidator.Closest(member, names);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1031",
                    $"'{owner}' has no attached property '{member}'" + (suggestion is not null ? $"; did you mean '{owner}.{suggestion}'?" : ""),
                    prop.Location,
                    names.Count > 0 ? $"attached properties of {owner}: {List(names, 8)}" : $"'{owner}' declares no attached properties"));
            }
            return;
        }

        switch (M.Locality(prop.Key))
        {
            case AttachedLocality.Unknown:
                diagnostics.Add(NotAllowlisted(prop, null));
                return;
            case AttachedLocality.CheckedByGrid:
                CheckGridPlacement(el, prop, owner, member, view, diagnostics);
                return;
            case AttachedLocality.CheckedByRef:
                CheckReference(prop, prop.Key, M.ReferenceEmitFor(prop.Key), view, diagnostics);
                return;
            default:
                CheckShape(prop, AttachedAsProperty(attached), M.ShapeForTypeName(attached.TypeName, IsEnumType(attached.TypeName)),
                    prop.Key, view, diagnostics);
                return;
        }
    }

    private static Diagnostic NotAllowlisted(PropPair prop, string? why) =>
        new(DiagnosticSeverity.Error, "FS1041",
            $"Attached property '{prop.Key}' is not in the locality allowlist" + (why is not null ? $" ({why})" : ""),
            prop.Location,
            "its correctness may depend on other elements, so it cannot pass through unchecked: use a keyword layout, "
            + "ref(Name) for references, or xaml { }; the allowlist is src/Flagstaff.Compiler/Manifest/overlay.json");

    private static List<string> AttachedNames(string owner) =>
        M.AttachedProperties.Where(a => a.Owner == owner).Select(a => a.Name).Order(StringComparer.Ordinal).ToList();

    /// <summary><c>Grid.Row/Column/RowSpan/ColumnSpan</c>: the direct parent must be a generic Grid whose definitions cover the cell.</summary>
    private static void CheckGridPlacement(Element el, PropPair prop, string owner, string member, ViewDecl view, List<Diagnostic> diagnostics)
    {
        var isRow = member is "Row" or "RowSpan";
        var defsKey = isRow ? "RowDefinitions" : "ColumnDefinitions";
        var defType = isRow ? "RowDefinition" : "ColumnDefinition";
        var axis = isRow ? "row" : "column";
        var example = $"Grid {defsKey}: {{ {defType} {(isRow ? "Height" : "Width")}: \"Auto\"; {defType} {(isRow ? "Height" : "Width")}: \"*\"; }} {{ ... }}";

        var parent = ParentOf(view, el);
        if (parent is not { IsGeneric: true, NamespacePrefix: null } || !M.TryGetType(parent.TypeName, out var parentType)
            || !M.DerivesFrom(parentType, "Grid"))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1038",
                $"{prop.Key} only applies to a direct child of a generic Grid with {defsKey}:"
                + (parent is null ? "" : $" (this '{el.Keyword}' is inside '{parent.Keyword}')"),
                prop.Location,
                $"keyword layouts (row, split, cards, stack) place children by position; for explicit cells write {example}"));
            return;
        }

        var count = CountDefinitions(parent, defsKey, defType);
        if (count is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1038",
                $"{prop.Key} only applies inside a generic Grid with {defsKey}: (the Grid at line {parent.Location.Line} declares none)",
                prop.Location, $"declare the {axis}s: {example}"));
            return;
        }

        var isSpan = member.EndsWith("Span", StringComparison.Ordinal);
        if (prop.Value is not IntValue { Value: var n } || n < (isSpan ? 1 : 0))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1038",
                $"{prop.Key} must be a literal integer {(isSpan ? "of at least 1" : "from 0")} so it can be checked against {defsKey}:, got {Describe(prop.Value)}",
                prop.Location, $"write {prop.Key}: {(isSpan ? 2 : 1)};"));
            return;
        }

        var startKey = $"{owner}.{(isRow ? "Row" : "Column")}";
        var spanKey = $"{owner}.{(isRow ? "RowSpan" : "ColumnSpan")}";
        var start = el.Props.FirstOrDefault(p => p.Key == startKey)?.Value is IntValue s ? s.Value : 0;
        var span = el.Props.FirstOrDefault(p => p.Key == spanKey)?.Value is IntValue sp ? sp.Value : 1;
        var declared = $"the Grid at line {parent.Location.Line} declares {count} {axis}{(count == 1 ? "" : "s")}";
        if (!isSpan && n >= count)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1038",
                $"{prop.Key}: {n} is past the last {axis}; {declared} (0–{count - 1})",
                prop.Location, $"add a {defType} to {defsKey}:, or use {prop.Key}: {count - 1} or less"));
        }
        else if (isSpan && start < count && start + span > count)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1038",
                $"{prop.Key}: {n} from {axis} {start} overflows; {declared}",
                prop.Location, $"use {prop.Key}: {count - start} or less"));
        }
    }

    /// <summary>Number of <c>RowDefinition</c>s (or columns) in the Grid's <c>RowDefinitions:</c> property element; null when absent.</summary>
    internal static int? CountDefinitions(Element grid, string defsKey, string defType)
    {
        var prop = grid.Props.FirstOrDefault(p => p.Key == defsKey || p.Key == "Grid." + defsKey);
        return prop?.Value switch
        {
            ElementsValue evs => evs.Elements.Count(e => e.TypeName == defType),
            ElementValue { Element.TypeName: var t } => t == defType ? 1 : 0,
            _ => null,
        };
    }

    // ── Values ─────────────────────────────────────────────────────────

    private static void CheckValue(PropPair prop, string qualifiedName, ManifestProperty mprop, string ownerName, ViewDecl view,
        List<Diagnostic> diagnostics)
    {
        var shape = M.ShapeFor(mprop);
        if (shape == ValueShape.Reference || M.ReferenceEmitFor(qualifiedName) != ReferenceEmit.None)
        {
            CheckReference(prop, $"{ownerName}.{mprop.Name}", M.ReferenceEmitFor(qualifiedName), view, diagnostics);
            return;
        }
        CheckShape(prop, mprop, shape, $"{ownerName}.{mprop.Name}", view, diagnostics);
    }

    /// <summary>A reference property (<c>Setter.Target</c>, <c>TeachingTip.Target</c>, <c>RelativePanel.Above</c>) takes <c>ref(Name)</c> only.</summary>
    private static void CheckReference(PropPair prop, string name, ReferenceEmit emit, ViewDecl view, List<Diagnostic> diagnostics)
    {
        var example = emit == ReferenceEmit.TargetPath ? "ref(Nav).DisplayMode" : "ref(SaveButton)";
        if (prop.Value is not RefValue rv)
        {
            if (prop.Value is BindingValue or ThemeResourceValue or StaticResourceValue)
                return; // not a name reference; resolved by the binding/resource
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1040",
                $"'{name}' references another element; write ref(Name), not {Describe(prop.Value)}",
                prop.Location,
                $"write {prop.Key}: {example}; Name is a name: declaration in this view, so a typo is reported instead of silently failing"));
            return;
        }
        if (emit == ReferenceEmit.TargetPath && rv.MemberPath is null)
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1040",
                $"'{name}' targets a property: write ref({rv.Name}).Property",
                prop.Location, $"write {prop.Key}: ref({rv.Name}).Visibility"));
        else if (emit == ReferenceEmit.Name && rv.MemberPath is not null)
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1040",
                $"'{name}' takes an element, not a member: write ref({rv.Name})",
                prop.Location, $"write {prop.Key}: ref({rv.Name})"));
        ResolveRef(rv, prop, view, diagnostics);
    }

    /// <summary>FS1039 when <c>ref(Name)</c> names no <c>name:</c> in the view; FS1031 when <c>.Member</c> is not on the named element's type.</summary>
    private static void ResolveRef(RefValue rv, PropPair prop, ViewDecl view, List<Diagnostic> diagnostics)
    {
        var target = view.NamedElements.FirstOrDefault(n => n.Name == rv.Name);
        if (target is null)
        {
            var suggestion = SemanticValidator.Closest(rv.Name, view.NamedElements.Select(n => n.Name).Distinct());
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1039",
                $"ref({rv.Name}) names no element in this view" + (suggestion is not null ? $"; did you mean '{suggestion}'?" : ""),
                prop.Location,
                $"declare it on the element with name: {rv.Name}"));
            return;
        }
        if (rv.MemberPath is null || ManifestTypeOf(target.Owner) is not { } type)
            return;

        var first = rv.MemberPath.Split('.')[0];
        if (!M.TryGetProperty(type, first, out _, out _))
        {
            var suggestions = M.SuggestProperties(type, first);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1031",
                $"'{type.Name}' ({rv.Name}) has no property '{first}'" + (suggestions.Count > 0 ? $"; did you mean '{suggestions[0]}'?" : ""),
                prop.Location,
                suggestions.Count > 1 ? $"close: {string.Join(", ", suggestions)}" : null));
        }
    }

    private static void CheckShape(PropPair prop, ManifestProperty mprop, ValueShape shape, string name, ViewDecl view,
        List<Diagnostic> diagnostics)
    {
        var value = prop.Value;
        switch (value)
        {
            case BindingValue or ThemeResourceValue or StaticResourceValue:
                return;
            case RefValue rv:
                ResolveRef(rv, prop, view, diagnostics); // an element binding ({x:Bind Name.Member})
                return;
        }

        var ok = value switch
        {
            NullValue => shape is not (ValueShape.Bool or ValueShape.Int or ValueShape.Double or ValueShape.Enum
                or ValueShape.Thickness or ValueShape.CornerRadius or ValueShape.GridLength or ValueShape.Color),
            ElementValue ev => ElementFits(ev.Element, mprop, shape),
            ElementsValue => shape is ValueShape.Collection or ValueShape.Unknown,
            IdentValue iv => IdentFits(iv.Name, mprop, shape),
            BoolValue => shape is ValueShape.Bool or ValueShape.Object or ValueShape.Unknown,
            IntValue i => shape switch
            {
                ValueShape.Int or ValueShape.Double or ValueShape.Thickness or ValueShape.CornerRadius
                    or ValueShape.Object or ValueShape.Unknown => true,
                ValueShape.GridLength => i.Value > 0,
                _ => false,
            },
            FloatValue f => shape switch
            {
                ValueShape.Double or ValueShape.Thickness or ValueShape.CornerRadius or ValueShape.Object or ValueShape.Unknown => true,
                ValueShape.GridLength => f.Value > 0,
                _ => false,
            },
            StringValue sv => StringFits(sv.Value, mprop, shape),
            ColorValue => shape is ValueShape.Brush or ValueShape.Color or ValueShape.Object or ValueShape.Unknown,
            _ => false,
        };
        if (ok)
            return;

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error, "FS1032",
            $"'{name}' expects {Expected(mprop, shape)}, got {Describe(value)}",
            prop.Location,
            ShapeHint(prop, mprop, shape)));
    }

    private static bool ElementFits(Element el, ManifestProperty mprop, ValueShape shape)
    {
        if (shape is ValueShape.Bool or ValueShape.Int or ValueShape.Double or ValueShape.String or ValueShape.Enum
            or ValueShape.Thickness or ValueShape.CornerRadius or ValueShape.GridLength or ValueShape.Color)
            return false;
        // `Flyout: Button { }`: the element must derive from the property's type when both are known.
        if (shape != ValueShape.Collection && el.IsGeneric && el.NamespacePrefix is null
            && M.TryGetType(el.TypeName, out var valueType) && M.TryGetType(mprop.TypeName.TrimEnd('?'), out var propType))
            return M.DerivesFrom(valueType, propType.FullName);
        return true;
    }

    private static bool IdentFits(string name, ManifestProperty mprop, ValueShape shape) => shape switch
    {
        _ when mprop.TypeName == "Microsoft.UI.Xaml.DependencyProperty" => true, // Setter Property: Padding
        _ when XamlGenerator.IsTypeNameProperty(mprop) => name.Contains('.') || M.TryGetType(name, out _), // TargetType: Button
        ValueShape.Enum => mprop.EnumMembers?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? true,
        ValueShape.Brush or ValueShape.Color => NamedColors.Contains(name),
        ValueShape.GridLength => name == "Auto" || XamlGenerator.ParseWidth(new IdentValue(name)) is not null,
        ValueShape.Icon => M.TryGetType(name, out _) || SymbolMembers.Contains(name),
        ValueShape.Object or ValueShape.Unknown or ValueShape.Collection or ValueShape.Uri => M.TryGetType(name, out _),
        _ => false,
    };

    private static readonly Regex Numbers = new(@"^\s*-?\d+(\.\d+)?(\s*,\s*-?\d+(\.\d+)?)*\s*$", RegexOptions.CultureInvariant);

    private static bool StringFits(string text, ManifestProperty mprop, ValueShape shape) => shape switch
    {
        ValueShape.String or ValueShape.Uri or ValueShape.Object or ValueShape.Unknown => true,
        ValueShape.Icon => SymbolMembers.Contains(text),
        ValueShape.Enum => mprop.EnumMembers?.Contains(text, StringComparer.OrdinalIgnoreCase) ?? true,
        ValueShape.Thickness or ValueShape.CornerRadius => Numbers.IsMatch(text) && text.Split(',').Length is 1 or 2 or 4,
        ValueShape.GridLength => text.Trim() is "Auto" or "auto" || XamlGenerator.ParseWidth(new StringValue(text)) is not null,
        ValueShape.Brush or ValueShape.Color => NamedColors.Contains(text) || HexColor.IsMatch(text),
        _ => false,
    };

    private static readonly Regex HexColor = new("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant);

    private static string Expected(ManifestProperty mprop, ValueShape shape) => shape switch
    {
        ValueShape.Bool => "bool (true or false)",
        ValueShape.Int => "an integer",
        ValueShape.Double => "a number",
        ValueShape.String => "a string",
        ValueShape.Enum => $"{ShortType(mprop.TypeName)} (one of {List(mprop.EnumMembers ?? [], 12)})",
        ValueShape.Thickness => "Thickness (a number, \"h,v\" or \"l,t,r,b\")",
        ValueShape.CornerRadius => "CornerRadius (a number or \"tl,tr,br,bl\")",
        ValueShape.GridLength => "GridLength (Auto, a positive number, * or N*)",
        ValueShape.Brush => "a Brush (#hex, a named color, theme(Key) or a brush element)",
        ValueShape.Color => "a Color (#hex or a named color)",
        ValueShape.Uri => "a URI string",
        ValueShape.Icon => "an icon (a Symbol name or an icon element such as SymbolIcon Symbol: Save)",
        ValueShape.Collection => $"a collection ({ShortType(mprop.TypeName)}): {{ elements }} or vm.X",
        _ => ShortType(mprop.TypeName),
    };

    private static string ShapeHint(PropPair prop, ManifestProperty mprop, ValueShape shape) => shape switch
    {
        ValueShape.Bool => $"write {prop.Key}: true;",
        ValueShape.Int => $"write {prop.Key}: 1;",
        ValueShape.Double => $"write {prop.Key}: 1.5;",
        ValueShape.Enum when prop.Value is IdentValue or StringValue
            && SemanticValidator.Closest(prop.Value is IdentValue iv ? iv.Name : ((StringValue)prop.Value).Value, mprop.EnumMembers ?? []) is { } close
            => $"did you mean '{close}'?",
        ValueShape.Enum => $"write {prop.Key}: {mprop.EnumMembers?.FirstOrDefault() ?? "Value"};",
        ValueShape.Thickness => $"write {prop.Key}: 8; or {prop.Key}: \"0,8,0,0\";",
        ValueShape.GridLength => $"write {prop.Key}: \"Auto\"; or {prop.Key}: \"2*\";",
        ValueShape.Brush or ValueShape.Color => $"write {prop.Key}: #0078D4; or {prop.Key}: theme(AccentFillColorDefaultBrush);",
        ValueShape.Icon when prop.Value is IdentValue or StringValue
            && SemanticValidator.SuggestSymbol(prop.Value is IdentValue iv3 ? iv3.Name : ((StringValue)prop.Value).Value) is { } symbol
            => $"Symbol has no '{(prop.Value is IdentValue iv4 ? iv4.Name : ((StringValue)prop.Value).Value)}'; did you mean {prop.Key}: {symbol}?",
        ValueShape.Icon => $"write a Symbol member ({prop.Key}: Save) or an icon element ({prop.Key}: SymbolIcon Symbol: Save)",
        _ => prop.Value is IdentValue iv2 ? $"strings are quoted: {prop.Key}: \"{iv2.Name}\";" : $"bind it instead: {prop.Key}: vm.X;",
    };

    // ── Label, binding, children, arrow, layout props ─────────────────

    private static void CheckPositional(Element el, ManifestType type, List<Diagnostic> diagnostics)
    {
        foreach (var prop in el.Props)
        {
            if (prop.Key == "_label")
            {
                var content = ContentProperty(type);
                if (content is null || !IsTextual(content))
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error, "FS1004",
                        $"'{el.TypeName}' does not take a \"label\"" + (content is null
                            ? " (it has no content property)"
                            : $" (its content property '{content.Name}' is {ShortType(content.TypeName)}, not text)"),
                        prop.Location, StringPropertyHint(type)));
                else if (HasContentChildren(el))
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error, "FS1004",
                        $"'{el.TypeName}' has both a \"label\" and children; both set '{content.Name}'",
                        prop.Location, "keep one: the label, or the { } children"));
            }
            else if (prop.Key == "_binding" && FindInputProperty(type) is null)
            {
                var binding = prop.Value is BindingValue b ? (b.Property.StartsWith("item.", StringComparison.Ordinal) ? b.Property : "vm." + b.Property) : "vm.X";
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1004",
                    $"'{el.TypeName}' does not take a positional binding (it has no input property)",
                    prop.Location, $"use Key: {binding}, e.g. {StringPropertyName(type) ?? "Tag"}: {binding}"));
            }
        }
    }

    private static string StringPropertyHint(ManifestType type) =>
        StringPropertyName(type) is { } name ? $"set a property instead: {name}: \"...\"" : "set a property instead (Key: \"...\")";

    private static string? StringPropertyName(ManifestType type)
    {
        foreach (var candidate in (string[])["Text", "Header", "Title", "Label", "Content"])
            if (M.TryGetProperty(type, candidate, out var p, out _) && IsTextual(p))
                return candidate;
        return null;
    }

    private static bool HasContentChildren(Element el) =>
        el.Body is BlockBody block && block.Children.Any(c => c is TextContent || c is ChildElement { Element: var e } && !XamlGenerator.IsHoisted(e));

    private static void CheckChildren(Element el, ManifestType type, List<Diagnostic> diagnostics)
    {
        if (el.Body is not BlockBody block || XamlGenerator.IsGenericItemTemplate(el))
            return; // `ListView ItemsSource: vm.Rows of Row { ... }`: the body is the ItemTemplate, not the items
        var children = block.Children
            .Where(c => c is TextContent || c is ChildElement { Element: var e } && !XamlGenerator.IsHoisted(e))
            .ToList();
        if (children.Count == 0)
            return;

        var content = ContentProperty(type);
        if (content is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1035",
                $"'{el.TypeName}' takes no children (it has no content property)",
                el.Location, "set its properties instead: Key: Element { ... } or Key: \"...\""));
            return;
        }

        // A single string child of a text content property becomes an attribute.
        if (children is [TextContent] && IsTextual(content))
            return;

        if (content.TypeName == "string")
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1035",
                $"'{el.TypeName}' takes text only (its content property '{content.Name}' is a string)",
                el.Location, $"write {content.Name}: \"...\" or one string child"));
            return;
        }

        if (!content.IsCollection && children.Count > 1 && !HoldsAnyElement(content))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1035",
                $"'{el.TypeName}' takes one child (its content property '{content.Name}' is {ShortType(content.TypeName)}), got {children.Count}",
                el.Location, "keep one child, or wrap them in a container (stack { ... })"));
            return;
        }

        var itemType = content.IsCollection ? CollectionItemType(content.TypeName) : TypeOfName(content.TypeName);
        if (itemType is null || itemType.Name == "UIElement" || M.TryGetType("UIElement", out var ui) && M.DerivesFrom(ui, itemType.FullName))
        {
            CheckGenericChildren(el, children, itemType, content, diagnostics);
            return;
        }
        foreach (var child in children)
        {
            var fits = child is ChildElement { Element: { IsGeneric: true, NamespacePrefix: not null } }
                       || child is ChildElement { Element: { IsGeneric: true } ce } && (!M.TryGetType(ce.TypeName, out var ct) || M.DerivesFrom(ct, itemType.FullName));
            if (!fits)
            {
                var (what, loc) = child is ChildElement c ? ($"'{c.Element.Keyword}'", c.Element.Location) : ("a text child", el.Location);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1035",
                    $"'{el.TypeName}.{content.Name}' holds {itemType.Name} items; {what} is not one",
                    loc, $"put {itemType.Name} elements here (e.g. {ExampleOf(itemType)})"));
            }
        }
    }

    /// <summary>UI content: a generic child whose type is known must be a UIElement (keywords and text always are).</summary>
    private static void CheckGenericChildren(Element el, List<ElementOrText> children, ManifestType? itemType, ManifestProperty content,
        List<Diagnostic> diagnostics)
    {
        if (itemType is null)
            return; // object / unknown item type: anything goes
        foreach (var child in children.OfType<ChildElement>())
        {
            if (child.Element is { IsGeneric: true, NamespacePrefix: null } ce && M.TryGetType(ce.TypeName, out var ct)
                && !M.DerivesFrom(ct, itemType.FullName))
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1035",
                    $"'{el.TypeName}.{content.Name}' holds {itemType.Name} items; '{ce.TypeName}' is not one",
                    ce.Location, $"set it as a property instead (Key: {ce.TypeName} {{ ... }})"));
        }
    }

    private static string ExampleOf(ManifestType itemType) => itemType.Name switch
    {
        "MenuFlyoutItemBase" => "MenuFlyoutItem Text: \"Open\" -> vm.OpenCommand;",
        "VisualState" => "VisualState name: Wide { ... }",
        "SetterBase" => "Setter Target: ref(Nav).DisplayMode Value: \"Inline\";",
        _ => itemType.Name + " ...;",
    };

    private static bool HoldsAnyElement(ManifestProperty content) =>
        content.TypeName == "object" || TypeOfName(content.TypeName) is { } t && (t.Name == "UIElement" || t.IsUIElement);

    private static void CheckArrow(Element el, ManifestType type, List<Diagnostic> diagnostics)
    {
        if (el.NavTarget is not { } target)
            return;
        string? problem = null;
        if (target == XamlGenerator.CancelTarget)
            problem = "'-> cancel' is only valid on a button inside a dialog or confirm";
        else if (target.StartsWith("vm.", StringComparison.Ordinal) && !M.HasCommand(type))
            problem = $"'{el.TypeName}' has no Command property, so '-> {target}' cannot bind it";
        else if (!target.StartsWith("vm.", StringComparison.Ordinal) && !M.HasEvent(type, "Click"))
            problem = $"'{el.TypeName}' has no Click event, so '-> {target}' cannot navigate";
        if (problem is not null)
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS2014", problem, el.Location,
                "-> works on types with a Command property (Button, MenuFlyoutItem, AppBarButton, ...); for other events use on Event -> vm.XCommand"));
    }

    private static void CheckLayoutProps(Element el, ManifestType type, List<Diagnostic> diagnostics)
    {
        if (el.Enabled is not null && !type.IsControl)
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning, "FS1012",
                $"'enabled(...)' has no effect on '{el.TypeName}' (not a Control)",
                el.Enabled.Location, "put enabled(...) on the controls inside it"));

        var isFrameworkElement = M.DerivesFrom(type, "FrameworkElement");
        if (el.When is not null && !type.IsUIElement && !M.DerivesFrom(type, "UIElement"))
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1002",
                $"'when(...)' needs a UIElement (it sets Visibility); '{el.TypeName}' is not one",
                el.When.Location, "put when(...) on the element that shows it"));
        if (isFrameworkElement)
            return;
        foreach (var prop in el.Props.Where(p => p.Key is "width" or "align" or "tooltip" or "fill" or "span"))
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1002",
                $"Property '{prop.Key}' needs a FrameworkElement; '{el.TypeName}' is not one",
                prop.Location, "set it on a visual element"));
    }

    // ── Events: on Event -> target ─────────────────────────────────────

    /// <summary>
    /// Checks every <c>on Event -> target</c> clause in the view (children, property-element values, view-level
    /// property elements). The event must exist on the element's manifest type, walking the base chain: a generic
    /// element's type, or a keyword's declared <see cref="KeywordSchema.RootType"/> (FS1034, with the closest names;
    /// keywords without a root tag cannot take events). One handler per event and element (FS1043, also when the
    /// element's own arrow already wires it: <c>Click</c> for a navigating button/link/generic element,
    /// <c>ItemClick</c> for a table/list with <c>-&gt;</c>). <c>-&gt; cancel</c> only inside a dialog/confirm (FS1024)
    /// (the parser admits only command, view and <c>cancel</c> targets). Third-party
    /// (<c>prefix.Type</c>) elements are not checked against the manifest. <c>(item)</c> and property targets are
    /// checked by <see cref="TypeInference"/> (FS2007, FS2015).
    /// </summary>
    internal static void ValidateEvents(ViewDecl view, List<Diagnostic> diagnostics)
    {
        foreach (var prop in view.PropertyElements)
            VisitValue(prop.Value, false);
        foreach (var el in view.Body)
            Visit(el, false);

        void VisitValue(PropValue value, bool inModal)
        {
            if (value is ElementValue ev)
            {
                Visit(ev.Element, inModal);
            }
            else if (value is ElementsValue evs)
            {
                foreach (var e in evs.Elements)
                    Visit(e, inModal);
            }
        }

        void Visit(Element el, bool inModal)
        {
            if (el.Events.Count > 0)
                CheckEvents(el, inModal, diagnostics);
            // -> cancel closes the nearest dialog/confirm; a list/table template is row-level, not the modal.
            var childInModal = !el.IsGeneric && el.Keyword is "dialog" or "confirm"
                               || inModal && (el.IsGeneric || el.Keyword is not ("list" or "table"));
            foreach (var prop in el.Props)
                VisitValue(prop.Value, childInModal);
            if (el.Body is BlockBody block)
            {
                foreach (var child in block.Children.OfType<ChildElement>())
                    Visit(child.Element, childInModal);
            }
        }
    }

    private static void CheckEvents(Element el, bool inModal, List<Diagnostic> diagnostics)
    {
        ManifestType? type = null;
        if (el.IsGeneric)
        {
            if (el.NamespacePrefix is null)
                M.TryGetType(el.TypeName, out type); // null: unknown type, already FS1030
        }
        else if (KeywordRootType(el) is { } root)
        {
            type = root;
        }
        else
        {
            foreach (var evt in el.Events)
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1034",
                    $"'{el.Keyword}' has no XAML element of its own, so it cannot handle '{evt.EventName}'",
                    evt.Location,
                    "put the on clause on the element that raises the event"));
            return;
        }

        var name = el.IsGeneric ? el.TypeName : el.Keyword;
        var arrowEvent = XamlGenerator.ArrowEvent(el);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in el.Events)
        {
            if (type is not null && !M.HasEvent(type, evt.EventName))
            {
                var events = EventNames(type);
                var lower = evt.EventName.ToLowerInvariant();
                var suggestions = events
                    .Select(e => (Name: e, Distance: SemanticValidator.Levenshtein(lower, e.ToLowerInvariant())))
                    .Where(x => x.Distance <= 2)
                    .OrderBy(x => x.Distance).ThenBy(x => x.Name, StringComparer.Ordinal)
                    .Select(x => x.Name)
                    .Take(8)
                    .ToList();
                var owner = el.IsGeneric ? $"'{type.Name}'" : $"'{el.Keyword}' ({type.Name})";
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1034",
                    $"{owner} has no event '{evt.EventName}'" + (suggestions.Count > 0 ? $"; did you mean '{suggestions[0]}'?" : ""),
                    evt.Location,
                    suggestions.Count > 1 ? $"close: {string.Join(", ", suggestions)}" : $"'{type.Name}' events: {List(events, 8)}"));
            }

            if (!seen.Add(evt.EventName))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1043",
                    $"'on {evt.EventName}' is written twice on '{name}'",
                    evt.Location,
                    "keep one on clause per event; one command can do several things"));
            }
            else if (evt.EventName == arrowEvent)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1043",
                    $"'on {evt.EventName}' repeats the {evt.EventName} handler that '-> {el.NavTarget}' already wires on '{name}'",
                    evt.Location,
                    $"keep either '-> {el.NavTarget}' or 'on {evt.EventName} -> ...'"));
            }

            if (evt.Target == XamlGenerator.CancelTarget && !inModal)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1024",
                    $"'on {evt.EventName} -> cancel' is only valid inside a dialog or confirm",
                    evt.Location,
                    "use -> vm.XCommand, or move the element into dialog { } / confirm { }"));
            }
        }
    }

    /// <summary>The type's events, nearest type first (own events, then each base's), alphabetical within a type.</summary>
    private static List<string> EventNames(ManifestType type) =>
        M.BaseChain(type)
            .SelectMany(t => t.Events.Keys.Order(StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    // ── Resources, styles, visual states, storyboards ─────────────────

    /// <summary>
    /// Checks the forms that reference something declared elsewhere in a resource or state block, so each is judged at its
    /// own .flag line (the locality rule: nothing is passed through whose correctness depends on another place):
    /// <list type="bullet">
    /// <item><c>key:</c> (→ <c>x:Key</c>) only on a generic element directly inside a resources block
    /// (<c>Page.Resources: { }</c>, <c>Resources: { }</c>, <c>Grid.Resources: { }</c>, a <c>ResourceDictionary</c>'s
    /// children); outside one, or on a keyword element, FS1044. An entry without <c>key:</c> is FS1044 too, except a
    /// <c>Style</c> with <c>TargetType:</c> (an implicit style, keyed by its type). A key repeated in one dictionary
    /// (implicit styles for the same type included) is FS1045.</item>
    /// <item><c>load:</c> takes <c>load(vm.X)</c> only (FS1032).</item>
    /// <item><c>Style TargetType: T { Setter Property: P Value: V; }</c>: <c>P</c> must be a property of <c>T</c>
    /// (FS1031), <c>V</c> must fit its type (FS1032).</item>
    /// <item><c>Setter Target: ref(N).M Value: V</c> (visual states): <c>M</c> is resolved by <see cref="ResolveRef"/>;
    /// <c>V</c> must fit <c>M</c>'s type (FS1032). A string <c>Target</c> is FS1040 (<see cref="CheckReference"/>).</item>
    /// <item><c>Storyboard.TargetProperty: "Opacity"</c> on an animation: a simple property name must exist on the type of
    /// the element named by <c>Storyboard.TargetName: ref(N)</c> (on the animation or its Storyboard; FS1031) and match
    /// the animation's value type (<c>DoubleAnimation</c> → a number property; FS1032). Parenthesised paths
    /// (<c>"(UIElement.Opacity)"</c>) pass as written.</item>
    /// </list>
    /// Undeclared <c>resource(Key)</c> keys are FS3010, reported by <see cref="XamlLinter"/> on the generated page, which
    /// knows the page's own <c>x:Key</c>s and the App/Styles.xaml keys.
    /// </summary>
    public static void ValidateResources(ViewDecl view, List<Diagnostic> diagnostics)
    {
        foreach (var prop in view.PropertyElements)
            VisitProp(prop, storyboardTarget: null);
        foreach (var el in view.Body)
            Visit(el, dictionary: null, storyboardTarget: null);

        void VisitProp(PropPair prop, RefValue? storyboardTarget)
        {
            if (prop.Value is not (ElementValue or ElementsValue))
                return;
            List<Element> elements = prop.Value is ElementValue ev ? [ev.Element] : ((ElementsValue)prop.Value).Elements;
            Dictionary<string, PropPair?>? dictionary = null;
            if (IsResourcesKey(prop.Key))
            {
                // `Page.Resources: ResourceDictionary { ... }` is the dictionary itself: its children are the entries.
                if (elements is [{ IsGeneric: true, NamespacePrefix: null, TypeName: "ResourceDictionary" } rd])
                {
                    Visit(rd, dictionary: null, storyboardTarget, isDictionaryItself: true);
                    return;
                }
                dictionary = new Dictionary<string, PropPair?>(StringComparer.Ordinal);
            }
            foreach (var element in elements)
                Visit(element, dictionary, storyboardTarget);
        }

        void Visit(Element el, Dictionary<string, PropPair?>? dictionary, RefValue? storyboardTarget, bool isDictionaryItself = false)
        {
            if (!isDictionaryItself)
                CheckKey(el, dictionary, diagnostics);
            CheckLoadProp(el, diagnostics);

            var type = el.IsGeneric ? ManifestTypeOf(el) : null;
            if (type is not null)
            {
                if (M.DerivesFrom(type, "Style"))
                    CheckStyle(el, view, diagnostics);
                if (type.Name == "Setter")
                    CheckStateSetter(el, view, diagnostics);
                if (el.Props.FirstOrDefault(p => p.Key == "Storyboard.TargetName")?.Value is RefValue own)
                    storyboardTarget = own;
                if (M.DerivesFrom(type, "Timeline") && !M.DerivesFrom(type, "Storyboard"))
                    CheckAnimation(el, type, storyboardTarget, view, diagnostics);
            }

            foreach (var prop in el.Props)
                VisitProp(prop, storyboardTarget);
            if (el.Body is not BlockBody block)
                return;
            var childDictionary = type is not null && M.DerivesFrom(type, "ResourceDictionary")
                ? new Dictionary<string, PropPair?>(StringComparer.Ordinal)
                : null;
            foreach (var child in block.Children.OfType<ChildElement>())
                Visit(child.Element, childDictionary, storyboardTarget);
        }
    }

    /// <summary>
    /// <c>Page.Resources</c>, <c>Resources</c>, <c>Grid.Resources</c>, <c>ThemeDictionaries</c>: a property element whose
    /// entries are keyed (<c>MergedDictionaries</c> entries are not).
    /// </summary>
    internal static bool IsResourcesKey(string key) =>
        key[(key.LastIndexOf('.') + 1)..] is "Resources" or "ThemeDictionaries";

    private static string? KeyOf(Element el) => el.Props.FirstOrDefault(p => p.Key == "key")?.Value switch
    {
        IdentValue iv => iv.Name,
        StringValue sv => sv.Value,
        _ => null,
    };

    /// <summary>FS1044 (misplaced / missing <c>key:</c>) and FS1045 (duplicate key in one dictionary).</summary>
    private static void CheckKey(Element el, Dictionary<string, PropPair?>? dictionary, List<Diagnostic> diagnostics)
    {
        var keyProp = el.Props.FirstOrDefault(p => p.Key == "key");
        if (dictionary is null)
        {
            if (keyProp is not null)
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1044",
                    $"key: only applies to an entry of a resources block; this '{el.Keyword}' is not in one",
                    keyProp.Location,
                    "declare resources at view level: Page.Resources: { Style key: CompactStat TargetType: Border { ... } }, "
                    + "then use them with Style: resource(CompactStat)"));
            return;
        }
        if (!el.IsGeneric)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1044",
                $"'{el.Keyword}' is a keyword element; a resource entry is written as a WinUI type (Style, SolidColorBrush, Storyboard, ...)",
                keyProp?.Location ?? el.Location,
                "write the resource as a generic element: SolidColorBrush key: Accent Color: \"#2563eb\";"));
            return;
        }

        var key = KeyOf(el);
        var isImplicitStyle = false;
        if (key is null && el.TypeName == "Style" && el.Props.FirstOrDefault(p => p.Key == "TargetType")?.Value is { } target
            && target is IdentValue or StringValue)
        {
            key = "{" + (target is IdentValue tiv ? tiv.Name : ((StringValue)target).Value) + "}";
            isImplicitStyle = true;
        }
        if (key is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1044",
                $"'{el.Keyword}' in a resources block needs key: (resources are looked up by key)",
                el.Location,
                $"write {el.Keyword} key: MyKey ...; and use it with resource(MyKey)"));
            return;
        }
        if (dictionary.TryGetValue(key, out var first))
        {
            var shown = isImplicitStyle ? $"implicit Style for {key[1..^1]}" : $"key '{key}'";
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1045",
                $"Duplicate {shown} in this resources block" + (first is not null ? $" (first declared at line {first.Location.Line})" : ""),
                keyProp?.Location ?? el.Location,
                "keys are unique within one dictionary: rename one, or remove the duplicate"));
            return;
        }
        dictionary[key] = keyProp;
    }

    /// <summary><c>load: load(vm.X)</c> (→ <c>x:Load</c>); anything else under <c>load:</c> is FS1032.</summary>
    private static void CheckLoadProp(Element el, List<Diagnostic> diagnostics)
    {
        if (el.Props.FirstOrDefault(p => p.Key == "load") is not { } prop || prop.Value is LoadValue)
            return;
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error, "FS1032",
            $"'load' (x:Load) expects load(vm.X), got {Describe(prop.Value)}",
            prop.Location, "write load: load(vm.ShowDetails); the view model member is a bool"));
    }

    private static string? NameText(PropValue? value) => value switch
    {
        IdentValue iv => iv.Name,
        StringValue sv => sv.Value,
        _ => null,
    };

    /// <summary>The setters of a <c>Style</c>: <c>Property:</c> against <c>TargetType</c>, <c>Value:</c> against that property.</summary>
    private static void CheckStyle(Element style, ViewDecl view, List<Diagnostic> diagnostics)
    {
        var targetName = NameText(style.Props.FirstOrDefault(p => p.Key == "TargetType")?.Value);
        // Unknown or third-party target types (local:Card, lvc.Chart) cannot be checked: the setters keep the object check.
        var targetType = targetName is not null && !targetName.Contains('.') && !targetName.Contains(':')
                         && M.TryGetType(targetName, out var t) ? t : null;

        var setters = new List<Element>();
        if (style.Body is BlockBody block)
            setters.AddRange(block.Children.OfType<ChildElement>().Select(c => c.Element));
        if (style.Props.FirstOrDefault(p => p.Key is "Setters" or "Style.Setters")?.Value is ElementsValue evs)
            setters.AddRange(evs.Elements);

        foreach (var setter in setters.Where(s => s is { IsGeneric: true, NamespacePrefix: null, TypeName: "Setter" }))
        {
            var propertyProp = setter.Props.FirstOrDefault(p => p.Key == "Property");
            var valueProp = setter.Props.FirstOrDefault(p => p.Key == "Value");
            var name = NameText(propertyProp?.Value);
            if (targetType is null || propertyProp is null || name is null)
            {
                CheckUntypedSetterValue(valueProp, view, diagnostics);
                continue;
            }

            var mprop = ResolveProperty(targetType, name);
            if (mprop is null)
            {
                var suggestions = name.Contains('.') ? [] : M.SuggestProperties(targetType, name);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1031",
                    $"Style for '{targetType.Name}' sets '{name}', which '{targetType.Name}' does not have"
                    + (suggestions.Count > 0 ? $"; did you mean '{suggestions[0]}'?" : ""),
                    propertyProp.Location,
                    suggestions.Count > 1 ? $"close: {string.Join(", ", suggestions)}"
                        : $"Setter Property: names a property of the Style's TargetType ({targetType.Name})"));
                continue;
            }
            if (valueProp is not null)
                CheckTypedSetterValue(valueProp, mprop, $"{targetType.Name}.{mprop.Name}", view, diagnostics);
        }
    }

    /// <summary>
    /// A visual-state <c>Setter Target: ref(N).M Value: V</c>: <c>V</c> must fit <c>M</c> on N's type. A Setter with
    /// <c>Property:</c> belongs to a Style (<see cref="CheckStyle"/>); one whose target cannot be resolved keeps the
    /// object check.
    /// </summary>
    private static void CheckStateSetter(Element setter, ViewDecl view, List<Diagnostic> diagnostics)
    {
        if (setter.Props.Any(p => p.Key == "Property"))
            return;
        var valueProp = setter.Props.FirstOrDefault(p => p.Key == "Value");
        if (valueProp is null)
            return;
        if (setter.Props.FirstOrDefault(p => p.Key == "Target")?.Value is RefValue { MemberPath: { } path } rv
            && !path.Contains('.')
            && view.NamedElements.FirstOrDefault(n => n.Name == rv.Name) is { } named
            && ManifestTypeOf(named.Owner) is { } type
            && M.TryGetProperty(type, path, out var mprop, out _))
        {
            CheckTypedSetterValue(valueProp, mprop, $"{type.Name}.{mprop.Name} ({rv.Name})", view, diagnostics);
            return;
        }
        CheckUntypedSetterValue(valueProp, view, diagnostics);
    }

    /// <summary>The plain <c>Setter.Value</c> (object) check, for setters whose target member is unknown.</summary>
    private static void CheckUntypedSetterValue(PropPair? valueProp, ViewDecl view, List<Diagnostic> diagnostics)
    {
        if (valueProp is not null && M.TryGetType("Setter", out var setterType) && M.TryGetProperty(setterType, "Value", out var value, out _))
            CheckShape(valueProp, value, M.ShapeFor(value), "Setter.Value", view, diagnostics);
    }

    /// <summary>
    /// <c>Setter Value:</c> against the property it sets. XAML writes setter values as strings, so a quoted literal that
    /// parses as the property's type (<c>"True"</c>, <c>"240"</c>, an enum member, <c>"12,8"</c>, <c>"#2563eb"</c>) fits.
    /// </summary>
    private static void CheckTypedSetterValue(PropPair valueProp, ManifestProperty mprop, string name, ViewDecl view,
        List<Diagnostic> diagnostics)
    {
        var shape = M.ShapeFor(mprop);
        if (shape is ValueShape.Reference)
            return;
        if (valueProp.Value is StringValue sv && shape switch
            {
                ValueShape.Bool => bool.TryParse(sv.Value, out _),
                ValueShape.Int => int.TryParse(sv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                ValueShape.Double => double.TryParse(sv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                _ => false,
            })
            return;
        CheckShape(valueProp, mprop, shape, name, view, diagnostics);
    }

    /// <summary>
    /// <c>Storyboard.TargetProperty: "Opacity"</c> on an animation: checked against the type of the element named by
    /// <paramref name="target"/> (FS1031), and against the animation's value type (FS1032).
    /// </summary>
    private static void CheckAnimation(Element animation, ManifestType animationType, RefValue? target, ViewDecl view,
        List<Diagnostic> diagnostics)
    {
        if (animation.Props.FirstOrDefault(p => p.Key == "Storyboard.TargetProperty") is not { Value: StringValue { Value: var path } } prop
            || !SimplePropertyPath.IsMatch(path) || target is null
            || view.NamedElements.FirstOrDefault(n => n.Name == target.Name) is not { } named
            || ManifestTypeOf(named.Owner) is not { } type)
            return;

        if (!M.TryGetProperty(type, path, out var mprop, out _))
        {
            var suggestions = M.SuggestProperties(type, path);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1031",
                $"'{type.Name}' ({target.Name}) has no property '{path}' to animate" + (suggestions.Count > 0 ? $"; did you mean '{suggestions[0]}'?" : ""),
                prop.Location,
                "Storyboard.TargetProperty names a property of the Storyboard.TargetName element; "
                + "an attached or nested property is written as a path: \"(UIElement.Opacity)\""));
            return;
        }

        var name = animationType.Name;
        var animates = name.StartsWith("DoubleAnimation", StringComparison.Ordinal) ? "a number"
            : name.StartsWith("ColorAnimation", StringComparison.Ordinal) ? "a Color"
            : name.StartsWith("PointAnimation", StringComparison.Ordinal) ? "a Point"
            : null;
        var shape = M.ShapeFor(mprop);
        var fits = animates switch
        {
            "a number" => shape is ValueShape.Double or ValueShape.Int,
            "a Color" => shape is ValueShape.Color,
            "a Point" => mprop.TypeName.TrimEnd('?').EndsWith("Point", StringComparison.Ordinal),
            _ => true,
        };
        if (!fits)
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1032",
                $"'{name}' animates {animates}; '{type.Name}.{mprop.Name}' is {ShortType(mprop.TypeName)}",
                prop.Location,
                shape == ValueShape.Brush
                    ? "animate the brush's color: ColorAnimation Storyboard.TargetProperty: \"(Border.Background).(SolidColorBrush.Color)\""
                    : "use the animation type that matches the property (DoubleAnimation, ColorAnimation, PointAnimation, ObjectAnimationUsingKeyFrames)"));
    }

    private static readonly Regex SimplePropertyPath = new(@"^[A-Za-z_]\w*$", RegexOptions.CultureInvariant);

    // ── Shared manifest helpers (also used by XamlGenerator.Generic.cs and TypeInference) ──

    /// <summary>
    /// The manifest property behind a pass-through key: an own property (<c>Header</c>), an own property qualified with
    /// the type (<c>Grid.RowDefinitions</c>), or an attached property (<c>Grid.Row</c>) as a synthesized property.
    /// </summary>
    internal static ManifestProperty? ResolveProperty(ManifestType? ownerType, string key) => ResolveProperty(ownerType, key, M);

    private static ManifestProperty? ResolveProperty(ManifestType? ownerType, string key, ControlManifest m)
    {
        var dot = key.LastIndexOf('.');
        if (dot < 0)
            return ownerType is not null && m.TryGetProperty(ownerType, key, out var p, out _) ? p : null;
        var owner = key[..dot];
        var member = key[(dot + 1)..];
        if (ownerType is not null && m.BaseChain(ownerType).Any(t => t.Name == owner) && m.TryGetProperty(ownerType, member, out var own, out _))
            return own;
        return m.TryGetAttachedProperty(key, out var attached) ? AttachedAsProperty(attached, m) : null;
    }

    private static ManifestProperty AttachedAsProperty(ManifestAttachedProperty attached) => AttachedAsProperty(attached, M);

    private static ManifestProperty AttachedAsProperty(ManifestAttachedProperty attached, ControlManifest m)
    {
        var members = m.TryGetValueType(attached.TypeName, out var vt) && vt.IsEnum ? vt.EnumMembers : null;
        var isCollection = attached.TypeName.Contains("IList<", StringComparison.Ordinal)
                           || attached.TypeName.EndsWith("Collection", StringComparison.Ordinal);
        return new ManifestProperty(attached.Name, attached.TypeName, true, isCollection, false, members);
    }

    private static bool IsEnumType(string typeName) => M.TryGetValueType(typeName, out var vt) && vt.IsEnum;

    /// <summary>The type's content property (walking bases), or null.</summary>
    internal static ManifestProperty? ContentProperty(ManifestType type) =>
        M.ContentPropertyOf(type) is { } name && M.TryGetProperty(type, name, out var p, out _) ? p : null;

    /// <summary>True for a <c>string</c> or <c>object</c> property (a label / single string child can set it).</summary>
    internal static bool IsTextual(ManifestProperty prop) => prop.TypeName is "string" or "object";

    private static readonly string[] InputPriority =
    [
        "Value", "Text", "IsChecked", "IsOn", "IsExpanded", "IsOpen", "IsPaneOpen", "Date", "SelectedDate", "Time",
        "SelectedTime", "Color", "Password", "SelectedItem", "SelectedIndex", "SelectedValue",
    ];

    /// <summary>The property a positional binding binds (<c>Slider vm.Volume</c> → <c>Value</c>): an overlay input (two-way) property.</summary>
    internal static ManifestProperty? FindInputProperty(ManifestType type)
    {
        foreach (var name in InputPriority)
        {
            if (M.TryGetProperty(type, name, out var p, out _) && !p.IsReadOnly && M.DefaultMode(type, p) == BindingMode.TwoWay)
                return p;
        }
        return null;
    }

    /// <summary>The binding mode for <c>Key: vm.X</c>: the manifest's default, OneWay for read-only and unknown properties.</summary>
    internal static BindingMode ModeFor(ManifestType? ownerType, ManifestProperty? prop) =>
        ownerType is not null && prop is not null && !prop.IsReadOnly ? M.DefaultMode(ownerType, prop) : BindingMode.OneWay;

    /// <summary>The element type of a collection property (<c>IList&lt;MenuFlyoutItemBase&gt;</c>, <c>UIElementCollection</c>).</summary>
    private static ManifestType? CollectionItemType(string typeName)
    {
        var lt = typeName.IndexOf('<');
        if (lt >= 0)
            return TypeOfName(typeName[(lt + 1)..^1]);
        var shortName = typeName[(typeName.LastIndexOf('.') + 1)..];
        return shortName.EndsWith("Collection", StringComparison.Ordinal)
            ? TypeOfName(shortName[..^"Collection".Length])
            : null;
    }

    private static ManifestType? TypeOfName(string typeName) =>
        M.TryGetType(typeName.TrimEnd('?'), out var t) ? t : null;

    private static string ShortType(string typeName)
    {
        var lt = typeName.IndexOf('<');
        var head = lt < 0 ? typeName : typeName[..lt];
        var shortName = head[(head.LastIndexOf('.') + 1)..];
        return lt < 0 ? shortName : $"{shortName}<{ShortType(typeName[(lt + 1)..^1])}>";
    }

    // ── Layout parents (for Grid.Row) ─────────────────────────────────

    private static readonly ConditionalWeakTable<ViewDecl, Dictionary<Element, Element?>> s_parents = new();

    /// <summary>The element whose <c>{ }</c> children include <paramref name="el"/>; null at view level and for property-element values.</summary>
    internal static Element? ParentOf(ViewDecl view, Element el) =>
        s_parents.GetValue(view, BuildParents).GetValueOrDefault(el);

    private static Dictionary<Element, Element?> BuildParents(ViewDecl view)
    {
        var parents = new Dictionary<Element, Element?>(ReferenceEqualityComparer.Instance);
        void Visit(Element el, Element? parent)
        {
            parents[el] = parent;
            foreach (var prop in el.Props)
            {
                switch (prop.Value)
                {
                    case ElementValue ev: Visit(ev.Element, null); break;
                    case ElementsValue evs: evs.Elements.ForEach(e => Visit(e, null)); break;
                }
            }
            if (el.Body is BlockBody block)
                foreach (var child in block.Children.OfType<ChildElement>())
                    Visit(child.Element, el);
        }
        foreach (var prop in view.PropertyElements)
        {
            if (prop.Value is ElementValue ev) Visit(ev.Element, null);
            else if (prop.Value is ElementsValue evs) evs.Elements.ForEach(e => Visit(e, null));
        }
        view.Body.ForEach(e => Visit(e, null));
        return parents;
    }

    // ── Text ───────────────────────────────────────────────────────────

    private static string List(IReadOnlyList<string> items, int max) =>
        items.Count <= max ? string.Join(", ", items) : string.Join(", ", items.Take(max)) + $", ... ({items.Count} total)";

    private static string Describe(PropValue value) => value switch
    {
        StringValue sv => $"\"{sv.Value}\"",
        IntValue iv => iv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FloatValue fv => fv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ColorValue cv => cv.Hex,
        BoolValue bv => bv.Value ? "true" : "false",
        BindingValue b => b.Property.StartsWith("item.", StringComparison.Ordinal) ? b.Property : "vm." + b.Property,
        IdentValue iv => iv.Name,
        NullValue => "null",
        ListValue => "a list",
        RangeValue rv => $"{rv.Start}..{rv.End}",
        ElementValue ev => $"an element ({ev.Element.Keyword})",
        ElementsValue => "an element block",
        ThemeResourceValue tr => $"theme({tr.Key})",
        StaticResourceValue sr => $"resource({sr.Key})",
        LoadValue lv => $"load(vm.{lv.Path})",
        RefValue rv => $"ref({rv.Name})" + (rv.MemberPath is { } mp ? "." + mp : ""),
        _ => value.ToString() ?? "",
    };

    private static readonly Lazy<HashSet<string>> s_symbols = new(() =>
        M.TryGetValueType("Symbol", out var vt) && vt.EnumMembers is { } members
            ? members.ToHashSet(StringComparer.Ordinal)
            : []);

    private static HashSet<string> SymbolMembers => s_symbols.Value;

    /// <summary>The named colors of <c>Microsoft.UI.Colors</c> (XAML accepts them for Brush and Color properties).</summary>
    internal static readonly HashSet<string> NamedColors = new(StringComparer.Ordinal)
    {
        "AliceBlue", "AntiqueWhite", "Aqua", "Aquamarine", "Azure", "Beige", "Bisque", "Black", "BlanchedAlmond", "Blue",
        "BlueViolet", "Brown", "BurlyWood", "CadetBlue", "Chartreuse", "Chocolate", "Coral", "CornflowerBlue", "Cornsilk",
        "Crimson", "Cyan", "DarkBlue", "DarkCyan", "DarkGoldenrod", "DarkGray", "DarkGreen", "DarkKhaki", "DarkMagenta",
        "DarkOliveGreen", "DarkOrange", "DarkOrchid", "DarkRed", "DarkSalmon", "DarkSeaGreen", "DarkSlateBlue",
        "DarkSlateGray", "DarkTurquoise", "DarkViolet", "DeepPink", "DeepSkyBlue", "DimGray", "DodgerBlue", "Firebrick",
        "FloralWhite", "ForestGreen", "Fuchsia", "Gainsboro", "GhostWhite", "Gold", "Goldenrod", "Gray", "Green",
        "GreenYellow", "Honeydew", "HotPink", "IndianRed", "Indigo", "Ivory", "Khaki", "Lavender", "LavenderBlush",
        "LawnGreen", "LemonChiffon", "LightBlue", "LightCoral", "LightCyan", "LightGoldenrodYellow", "LightGray",
        "LightGreen", "LightPink", "LightSalmon", "LightSeaGreen", "LightSkyBlue", "LightSlateGray", "LightSteelBlue",
        "LightYellow", "Lime", "LimeGreen", "Linen", "Magenta", "Maroon", "MediumAquamarine", "MediumBlue",
        "MediumOrchid", "MediumPurple", "MediumSeaGreen", "MediumSlateBlue", "MediumSpringGreen", "MediumTurquoise",
        "MediumVioletRed", "MidnightBlue", "MintCream", "MistyRose", "Moccasin", "NavajoWhite", "Navy", "OldLace", "Olive",
        "OliveDrab", "Orange", "OrangeRed", "Orchid", "PaleGoldenrod", "PaleGreen", "PaleTurquoise", "PaleVioletRed",
        "PapayaWhip", "PeachPuff", "Peru", "Pink", "Plum", "PowderBlue", "Purple", "Red", "RosyBrown", "RoyalBlue",
        "SaddleBrown", "Salmon", "SandyBrown", "SeaGreen", "SeaShell", "Sienna", "Silver", "SkyBlue", "SlateBlue",
        "SlateGray", "Snow", "SpringGreen", "SteelBlue", "Tan", "Teal", "Thistle", "Tomato", "Transparent", "Turquoise",
        "Violet", "Wheat", "White", "WhiteSmoke", "Yellow", "YellowGreen",
    };
}
