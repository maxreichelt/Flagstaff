namespace Flagstaff.Compiler;

/// <summary>
/// Checks a parsed file against <see cref="ElementSchema"/> before code generation.
/// Nothing unknown is silently ignored: unknown keywords, props and values are errors with a
/// location and, where possible, a "did you mean" hint.
/// <list type="bullet">
/// <item>FS1000 unknown element keyword</item>
/// <item>FS1001 unknown property</item>
/// <item>FS1002 property not applicable to this keyword</item>
/// <item>FS1003 invalid enum value</item>
/// <item>FS1004 label / binding / children / arrow not allowed on this keyword</item>
/// <item>FS1005 property value has the wrong shape</item>
/// <item>FS1023 (warning) <c>button</c> / <c>filePicker</c> / <c>link</c> without <c>-&gt;</c> (it does nothing), unless
/// a pass-through <c>Flyout:</c> / <c>NavigateUri:</c> / <c>Command:</c> or an <c>on</c> event gives it an action</item>
/// <item>FS1024 <c>-&gt; cancel</c> outside a <c>dialog</c> / <c>confirm</c>, or on something other than a button</item>
/// <item>FS1042 the same <c>name:</c> declared twice in a view</item>
/// <item>FS2006 (warning) legacy <c>onload -> vm.XAsync;</c></item>
/// </list>
/// Generic elements and pass-through keys are checked first by <see cref="GenericValidator"/> (FS1030–FS1041, FS2014,
/// FS4001, FS4002).
/// </summary>
public static class SemanticValidator
{
    /// <summary>FS1000 hint: generic elements first; <c>xaml { }</c> only for what the manifest cannot express.</summary>
    public const string XamlEscapeHint =
        "write the WinUI type in PascalCase (e.g. Expander Header: \"...\" { ... }), see Generic Elements; use xaml { } only for what the manifest cannot express";

    public static List<Diagnostic> Validate(FlagFile file)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var decl in file.Declarations)
        {
            switch (decl)
            {
                case AppDecl app:
                    ValidateApp(app, diagnostics);
                    break;
                case ViewDecl view:
                    ValidateView(view, diagnostics);
                    break;
            }
        }
        return diagnostics;
    }

    /// <summary>Common icon names that are not WinUI <c>Symbol</c> members, and the member to use.</summary>
    internal static readonly IReadOnlyDictionary<string, string> IconSynonyms = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PreviousTrack"] = "Previous",
        ["NextTrack"] = "Next",
        ["SkipBack"] = "Previous",
        ["SkipForward"] = "Next",
        ["Settings"] = "Setting",
        ["Gear"] = "Setting",
        ["Search"] = "Find",
        ["Trash"] = "Delete",
        ["Close"] = "Cancel",
        ["Plus"] = "Add",
        ["New"] = "Add",
        ["Info"] = "Help",
        ["User"] = "Contact",
        ["Person"] = "Contact",
        ["Folder"] = "OpenLocal",
        ["Note"] = "Document",
        ["File"] = "Document",
        ["Image"] = "Pictures",
        ["Photo"] = "Pictures",
        ["Music"] = "Audio",
        ["Star"] = "SolidStar",
        ["Heart"] = "Favorite",
        ["Brush"] = "Edit",
        ["Pen"] = "Edit",
        ["Pencil"] = "Edit",
        ["Eraser"] = "Clear",
        ["Check"] = "Accept",
        ["Checkmark"] = "Accept",
        ["Warning"] = "Important",
        ["Stats"] = "List",
    };

    /// <summary>
    /// FS1048 for an <c>icon:</c> value that names no WinUI <c>Symbol</c> member (after kebab-case → PascalCase), with
    /// the closest member in the same kebab-case spelling; null when the icon is valid.
    /// </summary>
    public static Diagnostic? UnknownIcon(string written)
    {
        var symbol = XamlGenerator.NormalizeIconName(written);
        if (XamlGenerator.ValidSymbols.Contains(symbol))
            return null;
        var suggestion = SuggestSymbol(symbol);
        var kebab = suggestion is null ? null : Kebab(suggestion);
        return new Diagnostic(DiagnosticSeverity.Error, "FS1048",
            $"Unknown icon '{written}': WinUI has no Symbol.{symbol}" + (kebab is not null ? $"; did you mean '{kebab}'?" : ""),
            default,
            "icon: takes a Symbol enum member, written in kebab-case (home, add, delete, previous, play, setting, ...)");
    }

    /// <summary>The <c>Symbol</c> member to suggest for an unknown PascalCase name: a known synonym, else the closest.</summary>
    internal static string? SuggestSymbol(string symbol) =>
        IconSynonyms.TryGetValue(symbol, out var synonym) && XamlGenerator.ValidSymbols.Contains(synonym)
            ? synonym
            : Closest(symbol, XamlGenerator.ValidSymbols);

    /// <summary><c>OutlineStar</c> → <c>outline-star</c> (the inverse of <see cref="XamlGenerator.NormalizeIconName"/>).</summary>
    public static string Kebab(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));

    /// <summary>FS1000 for an unknown element keyword (also used by the parser while recovering).</summary>
    public static Diagnostic UnknownKeyword(string keyword, SourceLocation location)
    {
        // when(...) { ... } / if (...) written as an element: conditions are clauses of an element, not blocks.
        if (keyword is "when" or "enabled" or "if")
        {
            var clause = keyword == "enabled" ? "enabled" : "when";
            return new Diagnostic(DiagnosticSeverity.Error, "FS1000",
                $"'{keyword}' is not an element: conditions are written after an element's props", location,
                $"write it on the element: button \"Save\" -> vm.Save {clause}(vm.CanSave); for several elements, on a container: stack {clause}(vm.X) {{ ... }}");
        }
        var suggestion = Closest(keyword, ElementSchema.All.Keys);
        var message = suggestion is not null
            ? $"Unknown element keyword '{keyword}'; did you mean '{suggestion}'?"
            : $"Unknown element keyword '{keyword}'";
        return new Diagnostic(DiagnosticSeverity.Error, "FS1000", message, location, XamlEscapeHint);
    }

    // ── App ────────────────────────────────────────────────────────

    private static void ValidateApp(AppDecl app, List<Diagnostic> diagnostics)
    {
        foreach (var member in app.Members)
        {
            switch (member)
            {
                case ThemeBlock theme:
                    foreach (var prop in theme.Properties)
                    {
                        if (ElementSchema.Theme.TryGetValue(prop.Key, out var spec))
                            CheckValue("theme", prop, spec, diagnostics);
                        else
                            diagnostics.Add(UnknownProp("theme", prop, ElementSchema.Theme.Keys, label: null));
                    }
                    break;

                case NavBlock nav:
                    foreach (var link in nav.Members.OfType<NavLinkDecl>())
                        foreach (var prop in link.Props)
                            ValidateProp("link", ElementSchema.All["link"], prop, label: link.Label, diagnostics);
                    break;
            }
        }
    }

    // ── View ───────────────────────────────────────────────────────

    private static void ValidateView(ViewDecl view, List<Diagnostic> diagnostics)
    {
        if (view.OnLoadTarget is { } target && view.OnLoadLocation is { } onLoadLoc)
        {
            var name = target.StartsWith("vm.", StringComparison.Ordinal) ? target[3..] : target;
            if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length)
            {
                var stem = name[..^"Async".Length];
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning, "FS2006",
                    $"'onload -> vm.{name};' is the legacy form; it now generates {stem}Command, which calls On{stem}Async()",
                    onLoadLoc,
                    $"write onload -> vm.{stem}; and implement On{stem}Async()"));
            }
        }

        // Manifest pass first: it repairs bare enum values (Orientation: Horizontal Spacing: 8) in place, then checks
        // generic elements and pass-through keys under the locality rule.
        GenericValidator.Validate(view, diagnostics);

        foreach (var element in view.Body)
            ValidateElement(element, diagnostics, inModal: false);
        foreach (var prop in view.PropertyElements)
            ValidateNestedElements(prop, diagnostics, inModal: false);

        var firstByName = new Dictionary<string, NamedElement>(StringComparer.Ordinal);
        foreach (var named in view.NamedElements)
        {
            if (firstByName.TryAdd(named.Name, named))
                continue;
            var first = firstByName[named.Name];
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1042",
                $"Duplicate name '{named.Name}' (already declared at line {first.Location.Line})",
                named.Location,
                "names are unique within a view, so ref(Name) and code-behind have exactly one target"));
        }
    }

    /// <summary>
    /// Schema checks for a generic element (<c>Expander</c>, <c>lvc.CartesianChart</c>): lowercase props are validated
    /// against <see cref="ElementSchema.Universal"/>; PascalCase/dotted keys, the label, the positional binding, children
    /// and arrow are checked by <see cref="GenericValidator"/> (manifest + locality rule).
    /// </summary>
    private static void ValidateGenericElement(Element el, List<Diagnostic> diagnostics, bool inModal)
    {
        foreach (var prop in el.Props)
        {
            if (prop.Key is "_label" or "_binding")
                continue;
            if (AstExtensions.IsPassThroughKey(prop.Key))
            {
                ValidateNestedElements(prop, diagnostics, inModal);
                continue;
            }
            if (ElementSchema.Universal.TryGetValue(prop.Key, out var spec))
                CheckValue(el.Keyword, prop, spec, diagnostics);
            else
                diagnostics.Add(UnknownProp(el.Keyword, prop, ElementSchema.Universal.Keys, LabelOf(el)));
        }

        if (el.Body is BlockBody block)
        {
            foreach (var child in block.Children)
            {
                if (child is ChildElement ce)
                    ValidateElement(ce.Element, diagnostics, inModal);
            }
        }
    }

    /// <summary>Validates the elements held by a property element (<c>Flyout: MenuFlyout { ... }</c>, <c>Key: { ... }</c>).</summary>
    private static void ValidateNestedElements(PropPair prop, List<Diagnostic> diagnostics, bool inModal)
    {
        switch (prop.Value)
        {
            case ElementValue ev:
                ValidateElement(ev.Element, diagnostics, inModal);
                break;
            case ElementsValue evs:
                foreach (var element in evs.Elements)
                    ValidateElement(element, diagnostics, inModal);
                break;
        }
    }

    /// <summary>
    /// An action given without an arrow: a pass-through <c>Flyout:</c> (the button opens it), <c>NavigateUri:</c> (the
    /// link opens it), <c>Command:</c>, or an <c>on Event -> ...</c> handler. Such a button/link is not FS1023.
    /// </summary>
    private static bool HasPassThroughAction(Element el) =>
        el.Events.Count > 0 || el.Props.Any(p => p.Key is "Flyout" or "NavigateUri" or "Command");

    /// <summary>Keywords that do nothing without an arrow target (FS1023).</summary>
    private static readonly HashSet<string> ActionKeywords = new(StringComparer.Ordinal) { "button", "filePicker", "link" };

    private static void ValidateElement(Element el, List<Diagnostic> diagnostics, bool inModal)
    {
        if (el.IsGeneric)
        {
            ValidateGenericElement(el, diagnostics, inModal);
            return;
        }

        if (!ElementSchema.All.TryGetValue(el.Keyword, out var schema))
        {
            diagnostics.Add(UnknownKeyword(el.Keyword, el.Location));
            return;
        }

        foreach (var prop in el.Props)
        {
            switch (prop.Key)
            {
                case "_label":
                    if (!schema.AllowsLabel)
                        diagnostics.Add(NotAllowed(el, "a \"label\"", prop.Location));
                    break;
                case "_binding":
                    var isItem = prop.Value is BindingValue { Property: var p } && p.StartsWith("item.", StringComparison.Ordinal);
                    if (isItem && !schema.AllowsItemBinding)
                        diagnostics.Add(NotAllowed(el, "a positional item.X binding", prop.Location));
                    else if (!isItem && !schema.AllowsBinding)
                        diagnostics.Add(NotAllowed(el, "a positional vm.X binding", prop.Location));
                    break;
                default:
                    if (AstExtensions.IsPassThroughKey(prop.Key))
                        ValidateNestedElements(prop, diagnostics, inModal); // the key itself is checked by GenericValidator
                    else
                        ValidateProp(el.Keyword, schema, prop, LabelOf(el), diagnostics);
                    break;
            }
        }

        if (el.Body is BlockBody && !schema.AllowsChildren)
            diagnostics.Add(NotAllowed(el, "a { } block", el.Location));

        if (el.NavTarget is not null && !schema.AllowsArrow)
            diagnostics.Add(NotAllowed(el, "an arrow target (->)", el.Location));

        if (el.NavTarget is null && ActionKeywords.Contains(el.Keyword) && !HasPassThroughAction(el))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning, "FS1023",
                $"{el.Keyword} has no action; add -> vm.XCommand or -> navigate(View)",
                el.Location,
                el.Props.FirstOrDefault(p => p.Value is ElementValue { Element.NavTarget: not null }) is { } taken
                    ? $"the -> after {taken.Key}: belongs to the {((ElementValue)taken.Value).Element.Keyword} value; write -> before {taken.Key}: or move {taken.Key}: into the {{ }} block"
                    : inModal ? "inside a dialog or confirm, -> cancel closes it" : null));
        }
        else if (el.NavTarget == XamlGenerator.CancelTarget && schema.AllowsArrow && (!inModal || el.Keyword != "button"))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1024",
                inModal
                    ? $"'-> cancel' is only valid on a button (this is '{el.Keyword}')"
                    : "'-> cancel' is only valid on a button inside a dialog or confirm",
                el.Location,
                "write button \"Cancel\" -> cancel; inside dialog { } / confirm { }, or use -> vm.XCommand"));
        }

        if (el.Body is BlockBody block)
        {
            // -> cancel closes the nearest dialog/confirm; a list/table template is row-level, not the modal.
            var childInModal = el.Keyword is "dialog" or "confirm" || (inModal && el.Keyword is not ("list" or "table"));
            foreach (var child in block.Children)
            {
                if (child is ChildElement ce)
                    ValidateElement(ce.Element, diagnostics, childInModal);
            }
        }
    }

    private static void ValidateProp(string keyword, KeywordSchema schema, PropPair prop, string? label, List<Diagnostic> diagnostics)
    {
        if (ElementSchema.TryGetProp(keyword, prop.Key, out var spec))
        {
            CheckValue(keyword, prop, spec, diagnostics);
            return;
        }

        var owners = ElementSchema.KeywordsWithProp(prop.Key).ToList();
        if (owners.Count > 0)
        {
            var allowed = AllowedProps(schema);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1002",
                $"Property '{prop.Key}' is not applicable to '{keyword}' (it belongs to {string.Join(", ", owners)})",
                prop.Location,
                allowed.Count > 0 ? $"'{keyword}' accepts: {string.Join(", ", allowed)}" : $"'{keyword}' accepts no properties"));
            return;
        }

        diagnostics.Add(UnknownProp(keyword, prop, AllowedProps(schema), label));
    }

    private static Diagnostic UnknownProp(string keyword, PropPair prop, IEnumerable<string> candidates, string? label)
    {
        string? hint;
        if (prop.Key == "value" && ElementSchema.All.TryGetValue(keyword, out var schema) && schema.AllowsBinding)
        {
            var labelPart = label is not null ? $"\"{label}\" " : "";
            var binding = prop.Value is BindingValue bv ? $"vm.{bv.Property}" : "vm.X";
            hint = $"use positional binding: {keyword} {labelPart}{binding};";
        }
        else
        {
            var suggestion = Closest(prop.Key, candidates)
                             ?? Closest(prop.Key, ElementSchema.All.Values.SelectMany(s => s.Props.Keys).Distinct());
            hint = suggestion is not null ? $"did you mean '{suggestion}'?" : null;
        }
        return new Diagnostic(DiagnosticSeverity.Error, "FS1001",
            $"Unknown property '{prop.Key}' on '{keyword}'", prop.Location, hint);
    }

    private static string? LabelOf(Element el) =>
        el.Props.FirstOrDefault(p => p.Key == "_label")?.Value is StringValue sv ? sv.Value : null;

    private static void CheckValue(string keyword, PropPair prop, PropSpec spec, List<Diagnostic> diagnostics)
    {
        var value = prop.Value;

        if (prop.Key == "icon" && keyword != "theme" && value is IdentValue or StringValue)
        {
            var written = value is IdentValue iv ? iv.Name : ((StringValue)value).Value;
            if (written.Length > 0 && UnknownIcon(written) is { } diagnostic)
                diagnostics.Add(diagnostic with { Location = prop.Location });
        }

        if (value is ElementValue or ElementsValue)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1005",
                $"Property '{prop.Key}' on '{keyword}' expects {Expected(spec)}, got {Describe(value)}",
                prop.Location,
                "element values belong to PascalCase keys (Flyout: MenuFlyout { ... })"));
            return;
        }

        if (spec.Enum is { Length: > 0 } allowedValues)
        {
            var text = value switch
            {
                IdentValue iv => iv.Name,
                StringValue sv => sv.Value,
                _ => null,
            };
            if (text is null || !allowedValues.Contains(text, StringComparer.Ordinal))
            {
                var suggestion = text is null ? null : Closest(text, allowedValues);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1003",
                    $"Invalid value '{Describe(value)}' for '{prop.Key}' on '{keyword}'",
                    prop.Location,
                    $"allowed: {string.Join(", ", allowedValues)}" + (suggestion is not null ? $"; did you mean '{suggestion}'?" : "")));
            }
            return;
        }

        var ok = spec.Kind switch
        {
            PropKind.String => value is StringValue,
            PropKind.Int => value is IntValue,
            PropKind.Float => value is FloatValue or IntValue,
            PropKind.Bool => value is BoolValue,
            PropKind.Color => value is ColorValue,
            PropKind.Binding => value is BindingValue b && !b.Property.StartsWith("item.", StringComparison.Ordinal),
            PropKind.ItemBinding => value is BindingValue b && b.Property.StartsWith("item.", StringComparison.Ordinal),
            PropKind.Range => value is RangeValue,
            PropKind.List => value is ListValue,
            PropKind.Ident => value is IdentValue or StringValue,
            PropKind.Width => value is IntValue or FloatValue or IdentValue or StringValue,
            _ => true,
        };

        // A bare flag (`bold`) arrives as BoolValue(true); a non-bare prop written bare is an error.
        if (ok && value is BoolValue && !spec.Bare && spec.Kind != PropKind.Bool && spec.Kind != PropKind.Any)
            ok = false;

        if (!ok)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1005",
                $"Property '{prop.Key}' on '{keyword}' expects {Expected(spec)}, got {Describe(value)}",
                prop.Location,
                $"write {prop.Key}: {Example(spec)}"));
        }
    }

    private static Diagnostic NotAllowed(Element el, string what, SourceLocation location) =>
        new(DiagnosticSeverity.Error, "FS1004",
            $"'{el.Keyword}' does not take {what}",
            location,
            Shape(el.Keyword));

    /// <summary>A one-line description of what a keyword accepts, e.g. <c>form: stat "label" vm.X variant: ...</c>.</summary>
    private static string Shape(string keyword)
    {
        var s = ElementSchema.All[keyword];
        var parts = new List<string> { keyword };
        if (s.AllowsLabel) parts.Add("\"label\"");
        if (s.AllowsBinding) parts.Add("vm.X");
        if (s.AllowsItemBinding) parts.Add("item.X");
        parts.AddRange(AllowedProps(s).Select(p => ElementSchema.TryGetProp(keyword, p, out var spec) && spec.Bare ? p : $"{p}: ..."));
        if (s.AllowsArrow) parts.Add("-> target");
        parts.Add(s.AllowsChildren ? "{ ... }" : ";");
        return "form: " + string.Join(" ", parts);
    }

    private static List<string> AllowedProps(KeywordSchema schema) =>
        schema.Props.Keys.Concat(ElementSchema.Universal.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static string Expected(PropSpec spec) => spec.Kind switch
    {
        PropKind.String => "a string",
        PropKind.Int => "an integer",
        PropKind.Float => "a number",
        PropKind.Bool => "true or false",
        PropKind.Color => "a #hex color",
        PropKind.Binding => "a vm.X binding",
        PropKind.ItemBinding => "an item.X binding",
        PropKind.Range => "a range like 1..10",
        PropKind.List => "a list like [\"a\", \"b\"]",
        PropKind.Ident => "a name",
        PropKind.Width => "a width (200, auto, \"2*\")",
        _ => "a value",
    };

    private static string Example(PropSpec spec) => spec.Kind switch
    {
        PropKind.String => "\"text\"",
        PropKind.Int => "3",
        PropKind.Float => "1.5",
        PropKind.Bool => "true",
        PropKind.Color => "#0078d4",
        PropKind.Binding => "vm.X",
        PropKind.ItemBinding => "item.X",
        PropKind.Range => "1..10",
        PropKind.List => "[\"a\", \"b\"]",
        PropKind.Ident => "name",
        PropKind.Width => "200",
        _ => "value",
    };

    private static string Describe(PropValue value) => value switch
    {
        StringValue sv => $"\"{sv.Value}\"",
        IntValue iv => iv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FloatValue fv => fv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ColorValue cv => cv.Hex,
        BoolValue bv => bv.Value ? "true" : "false",
        BindingValue b => b.Property.StartsWith("item.", StringComparison.Ordinal) ? b.Property : "vm." + b.Property,
        RangeValue rv => $"{rv.Start}..{rv.End}",
        IdentValue iv => iv.Name,
        NullValue => "null",
        ListValue => "a list",
        ElementValue ev => $"an element ({ev.Element.Keyword})",
        ElementsValue => "an element block",
        ThemeResourceValue tr => $"theme({tr.Key})",
        StaticResourceValue sr => $"resource({sr.Key})",
        LoadValue lv => $"load({(lv.Path.StartsWith("item.", StringComparison.Ordinal) ? lv.Path : "vm." + lv.Path)})",
        RefValue rv => $"ref({rv.Name})" + (rv.MemberPath is { } mp ? "." + mp : ""),
        _ => value.ToString() ?? "",
    };

    // ── "Did you mean" ─────────────────────────────────────────────

    /// <summary>The closest candidate within Levenshtein distance 2 (case-insensitive), or null.</summary>
    public static string? Closest(string input, IEnumerable<string> candidates, int maxDistance = 2)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates.OrderBy(c => c, StringComparer.Ordinal))
        {
            if (candidate == input) continue;
            var d = Levenshtein(input.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (d <= maxDistance && d < bestDistance)
            {
                best = candidate;
                bestDistance = d;
            }
        }
        return best;
    }

    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
