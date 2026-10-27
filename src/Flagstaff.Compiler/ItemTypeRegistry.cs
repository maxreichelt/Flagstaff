using System.Text;

namespace Flagstaff.Compiler;

/// <summary>One inferred property of a row/item type (<c>of T</c>).</summary>
/// <param name="Name">Property name (<c>Total</c> for <c>item.Total</c>).</param>
/// <param name="CSharpType">Inferred C# type.</param>
/// <param name="Confidence">How the type was determined; higher wins, first wins on ties.</param>
/// <param name="Location">Where the winning type came from.</param>
/// <param name="Target">The CLR type of the control property the winning usage binds (<see cref="InferredMember.Target"/>).</param>
/// <param name="Format">The <c>format:</c> of the winning usage.</param>
/// <param name="InferredType">Set in contracts when <see cref="CSharpType"/> comes from the hand-written partial: the .flag's type.</param>
public sealed record ItemTypeProperty(string Name, string CSharpType, InferenceConfidence Confidence, SourceLocation Location,
    string? Target = null, FormatKind? Format = null, string? InferredType = null);

/// <summary>
/// Project-wide registry of item types (<c>table/list/select ... of T</c>): <c>type → prop → (csType, confidence, location)</c>.
/// Owned by <see cref="FlagstaffCompiler"/>, fed by every view it compiles, and emitted once as <c>T.g.cs</c>
/// from <see cref="FlagstaffCompiler.GenerateAppScaffold"/>, so two views sharing <c>of Order</c> produce one
/// merged <c>Order.g.cs</c>.
/// <para>
/// <see cref="Collect"/> also checks the data-scoping rules while it walks a view:
/// FS2005 (<c>vm.X</c> bound inside a list row template, <c>item.X</c> bound outside one),
/// FS2010 (conflicting explicit types for one item property), FS2011 (<c>empty</c> placement),
/// FS2012 (<c>col</c> placement / non-column table children), FS2013 (<c>display:</c> without <c>items: ... of T</c>),
/// FS1005 (shape of <c>select options:</c> and <c>col width:</c>).
/// </para>
/// </summary>
public sealed class ItemTypeRegistry
{
    private readonly Dictionary<string, List<ItemTypeProperty>> _types = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    /// <summary>Registered item types in first-seen order, each with its properties in first-seen order.</summary>
    public IReadOnlyList<(string TypeName, IReadOnlyList<ItemTypeProperty> Properties)> Types =>
        _order.Select(t => (t, (IReadOnlyList<ItemTypeProperty>)_types[t])).ToList();

    /// <summary>Makes sure <paramref name="typeName"/> exists (even without properties).</summary>
    public void AddType(string typeName)
    {
        if (typeName.Contains('.')) return; // qualified (third-party) type names are never generated classes
        if (_types.ContainsKey(typeName)) return;
        _types[typeName] = [];
        _order.Add(typeName);
    }

    /// <summary>
    /// Records one usage of <c>item.<paramref name="prop"/></c> on <paramref name="typeName"/>.
    /// Two Explicit usages with different types report FS2010 (naming both locations).
    /// </summary>
    public void Add(string typeName, string prop, string csType, InferenceConfidence confidence,
        SourceLocation location, List<Diagnostic>? diagnostics = null, string? target = null, FormatKind? format = null)
    {
        AddType(typeName);
        var props = _types[typeName];
        var index = props.FindIndex(p => p.Name == prop);
        if (index < 0)
        {
            props.Add(new ItemTypeProperty(prop, csType, confidence, location, target, format));
            return;
        }

        var existing = props[index];
        if (confidence == InferenceConfidence.Explicit && existing.Confidence == InferenceConfidence.Explicit)
        {
            if (existing.CSharpType != csType && existing.Location != location)
            {
                diagnostics?.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS2010",
                    $"{typeName}.{prop} is {csType} here but {existing.CSharpType} at {existing.Location.ToShortString()}",
                    location,
                    $"use the same format:/control for item.{prop} everywhere {typeName} is used"));
            }
            return;
        }
        if (confidence > existing.Confidence)
            props[index] = new ItemTypeProperty(prop, csType, confidence, location, target, format);
    }

    /// <summary>
    /// One <c>T.g.cs</c> partial class per registered type. A type no binding gave properties (<c>select ... of Person</c>
    /// without <c>display:</c>) is an empty partial class, so the generated code that names it compiles; the developer
    /// adds members in their own partial.
    /// </summary>
    public List<OutputFile> Generate(string appNamespace) => Generate(appNamespace, UserDeclarations.Empty, null);

    /// <summary>
    /// As <see cref="Generate(string)"/>, skipping properties the hand-written partial <c>{ns}.ViewModels.T</c> already
    /// declares (one FS2016 warning each into <paramref name="diagnostics"/>).
    /// </summary>
    public List<OutputFile> Generate(string appNamespace, UserDeclarations user, List<Diagnostic>? diagnostics)
    {
        var files = new List<OutputFile>();
        foreach (var typeName in _order)
        {
            var props = _types[typeName];
            var userMembers = user.Members($"{appNamespace}.ViewModels.{typeName}");

            var sb = new StringBuilder();
            sb.AppendLine("// Auto-generated by Flagstaff — overwritten on next flagstaff compile.");
            sb.AppendLine($"// Contract: {typeName} has only the properties the .flag files bind (item.X). Add members in your own");
            sb.AppendLine($"// 'public partial class {typeName}' in namespace {appNamespace}.ViewModels (it must be 'partial').");
            var skipped = new List<string>();
            var body = new StringBuilder();
            foreach (var p in props)
            {
                if (userMembers.FirstOrDefault(u => u.ProvidedProperty == p.Name) is { } declaredBy)
                {
                    skipped.Add($"{p.Name} ({declaredBy.File})");
                    diagnostics?.Add(SkippedDiagnostic(typeName, p, declaredBy));
                    continue;
                }
                var defaultValue = p.CSharpType == "string" ? " = \"\";" : "";
                body.AppendLine($"    public {p.CSharpType} {p.Name} {{ get; set; }}{defaultValue}");
            }
            if (skipped.Count > 0)
                sb.AppendLine($"// Declared by you, so not generated here: {string.Join(", ", skipped)}.");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine($"namespace {appNamespace}.ViewModels;");
            sb.AppendLine();
            sb.AppendLine($"public partial class {typeName}");
            sb.AppendLine("{");
            sb.Append(body);
            sb.AppendLine("}");
            files.Add(new OutputFile($"{typeName}.g.cs", sb.ToString()));
        }
        return files;
    }

    /// <summary>The bound properties of <paramref name="typeName"/> the hand-written partial already declares.</summary>
    public IReadOnlyList<string> DeclaredByUser(string typeName, string appNamespace, UserDeclarations user)
    {
        if (!_types.TryGetValue(typeName, out var props))
            return [];
        var members = user.Members($"{appNamespace}.ViewModels.{typeName}");
        return props.Where(p => members.Any(u => u.ProvidedProperty == p.Name)).Select(p => p.Name).ToList();
    }

    /// <summary>
    /// The properties of <paramref name="typeName"/> for its contract: a property the hand-written partial declares has
    /// the hand-written type (<see cref="ItemTypeProperty.InferredType"/> keeps the .flag's when they differ).
    /// </summary>
    public IReadOnlyList<ItemTypeProperty> ContractProperties(string typeName, string appNamespace, UserDeclarations user)
    {
        if (!_types.TryGetValue(typeName, out var props))
            return [];
        var members = user.Members($"{appNamespace}.ViewModels.{typeName}");
        return props.Select(p => members.FirstOrDefault(u => u.ProvidedProperty == p.Name)?.Type is { Length: > 0 } declared
                                 && !UserDeclarations.SameType(declared, p.CSharpType)
                ? p with { CSharpType = DeclaredTypes.Written(declared), InferredType = p.CSharpType }
                : p)
            .ToList();
    }

    private static Diagnostic SkippedDiagnostic(string typeName, ItemTypeProperty p, UserMember declaredBy)
    {
        var usage = $"item.{p.Name} ({p.Location.FileName} line {p.Location.Line})";
        if (UserDeclarations.SameType(declaredBy.Type, p.CSharpType))
        {
            return new Diagnostic(DiagnosticSeverity.Warning, "FS2016",
                $"'{typeName}.{p.Name}' is also declared in {declaredBy.File}; the generator skipped it",
                declaredBy.Location,
                $"delete it from your partial; the .flag already generates it from {usage}");
        }
        return ViewContract.TypeDiagnostic($"{typeName}.{p.Name}", declaredBy, p.CSharpType, DeclaredTypes.Check(declaredBy.Type!, p), usage);
    }

    // ── Collection + scoping checks ────────────────────────────────

    /// <summary>Walks a view: records item-type properties and reports the scoping diagnostics listed on the class.</summary>
    public void Collect(ViewDecl view, List<Diagnostic> diagnostics)
    {
        foreach (var el in view.Body)
            Walk(el, parent: null, template: null, diagnostics);
    }

    /// <summary>The C# type a format clause implies for a bound value, or null.</summary>
    internal static string? FormatType(FormatClause? format) => format?.Kind switch
    {
        FormatKind.Currency => "decimal",
        FormatKind.Date => "DateTimeOffset?",
        FormatKind.Percent => "double",
        FormatKind.Number => "double",
        _ => null,
    };

    /// <summary>The type of an <c>item.X</c> keyword binding and the CLR type of the property it binds (see <see cref="InferredMember.Target"/>).</summary>
    private static (string Type, InferenceConfidence Confidence, string? Target) TypeForUsage(Element el)
    {
        if (FormatType(el.Format) is { } formatted)
            return (formatted, InferenceConfidence.Explicit, null);
        return el.Keyword switch
        {
            "checkbox" or "radio" => ("bool", InferenceConfidence.Explicit, "bool?"),
            "toggle" => ("bool", InferenceConfidence.Explicit, "bool"),
            "number" or "progress" => ("double", InferenceConfidence.Explicit, "double"),
            "datepicker" => ("DateTimeOffset?", InferenceConfidence.Explicit, "DateTimeOffset?"),
            _ => ("string", InferenceConfidence.Default, null),
        };
    }

    /// <param name="template">The enclosing list/table whose row template we are in (null outside templates).</param>
    private void Walk(Element el, Element? parent, Element? template, List<Diagnostic> diagnostics)
    {
        var parentKeyword = parent?.Keyword;

        // ── Placement ──
        if (el.Keyword == "empty")
        {
            if (parentKeyword is not ("table" or "list"))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS2011",
                    "'empty' must be a child of table or list", el.Location,
                    "table items: vm.Items of T { col ...; empty \"No items yet.\"; }"));
            }
            else if (parent!.Props.FirstOrDefault(p => p.Key == "items")?.Value is not BindingValue)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS2011",
                    $"'empty' needs the enclosing {parentKeyword} to have items: vm.X", el.Location,
                    $"{parentKeyword} items: vm.Items of T {{ ... }}"));
            }
            return;
        }

        if (parentKeyword == "table" && el.Keyword is not ("col" or "line" or "button"))
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS2012",
                $"'{el.Keyword}' is not allowed inside a table; a table contains col, button (row actions) and empty", el.Location,
                "use list items: vm.X of T { ... } for free-form rows"));
            return;
        }
        if (parentKeyword == "table" && el.Keyword == "button")
            template = parent; // row action: its bindings/conditions are row-scoped
        if (el.Keyword == "col" && parentKeyword != "table")
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS2012",
                "'col' must be a child of table", el.Location,
                "use column { } for a vertical layout stack"));
            return;
        }

        // ── Table columns: item.X typed by format: ──
        if (parentKeyword == "table" && el.Keyword != "button")
        {
            CheckWidth(el, diagnostics);
            foreach (var prop in el.Props)
            {
                if (prop.Key is "bind" or "_binding" && prop.Value is BindingValue { Property: var p }
                    && p.StartsWith("item.", StringComparison.Ordinal) && parent!.OfType is { } rowType)
                {
                    var type = FormatType(el.Format) ?? "string";
                    Add(rowType, p["item.".Length..], type,
                        el.Format is null ? InferenceConfidence.Default : InferenceConfidence.Explicit,
                        prop.Location, diagnostics, format: el.Format?.Kind);
                }
            }
            return;
        }

        // ── Bindings in or out of a row template ──
        foreach (var prop in el.Props)
        {
            if (prop.Value is not BindingValue { Property: var path })
                continue;
            if (el.Keyword == "select" && prop.Key == "display")
                continue; // select-scoped, checked in CheckSelect

            var isItem = path.StartsWith("item.", StringComparison.Ordinal);
            if (template is not null && !isItem)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS2005",
                    $"'vm.{path}' cannot be bound inside an item template; the template is bound to the row",
                    prop.Location,
                    "use item.X for row values, or move the element outside the list"));
            }
            else if (template is null && isItem)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS2005",
                    $"'{path}' is only valid inside an item template (list/table row)",
                    prop.Location,
                    "use vm.X outside item templates"));
            }
            else if (isItem && template!.OfType is { } rowType)
            {
                var manifestTyped = TypeInference.IsManifestTyped(el, prop.Key);
                var (type, confidence, target) = manifestTyped
                    ? TypeInference.ManifestBindingFor(el, prop.Key, prop.TypeArg) // generic elements / pass-through keys
                    : TypeForUsage(el);
                Add(rowType, path["item.".Length..], type, confidence, prop.Location, diagnostics, target,
                    manifestTyped ? null : el.Format?.Kind);
            }
        }

        if (template?.OfType is { } condType)
        {
            if (el.When is { } when) CollectConditionTypes(condType, when, diagnostics);
            if (el.Enabled is { } enabled) CollectConditionTypes(condType, enabled, diagnostics);
        }

        if (el.Keyword == "select")
            CheckSelect(el, diagnostics);

        if (!el.IsGeneric && el.Keyword is "table" or "list" or "select" && el.OfType is { } ofType)
            AddType(ofType);
        AddCollectionItemTypes(el);

        // Property-element values (`Flyout: MenuFlyout { MenuFlyoutItem Text: item.Name; }`) are scoped like children,
        // without the placement rules of their owner (parent: null). A table/list's ContextFlyout: is on each row
        // (XamlGenerator.IsRowPropertyElement); its other property elements are on the ListView, outside the row.
        foreach (var prop in el.Props)
        {
            if (prop.Value is not (ElementValue or ElementsValue))
                continue;
            var propTemplate = !el.IsGeneric && el.Keyword is "table" or "list" && XamlGenerator.IsRowPropertyElement(el, prop) ? el : template;
            IEnumerable<Element> values = prop.Value is ElementValue ev ? [ev.Element] : ((ElementsValue)prop.Value).Elements;
            foreach (var value in values)
                Walk(value, parent: null, propTemplate, diagnostics);
        }

        if (el.Body is BlockBody block)
        {
            var childTemplate = el.Keyword == "list" && !el.IsGeneric || XamlGenerator.IsGenericItemTemplate(el) ? el : template;
            foreach (var child in block.Children.OfType<ChildElement>())
                Walk(child.Element, el, childTemplate, diagnostics);
        }
    }

    /// <summary>
    /// A WinUI generic element whose collection property is bound with an item type (<c>ListView ItemsSource: vm.Rows
    /// of Row</c>, or the element's <c>of T</c>): <c>Row</c> is an item class like <c>list ... of Row</c>. Built-in types
    /// and third-party elements (whose <c>of T</c> names a library type) register nothing.
    /// </summary>
    private void AddCollectionItemTypes(Element el)
    {
        if (!el.IsGeneric || GenericValidator.ManifestTypeOf(el) is not { } owner)
            return;
        foreach (var prop in el.Props)
        {
            if (prop.Value is not BindingValue || !AstExtensions.IsPassThroughKey(prop.Key)
                || (prop.TypeArg ?? el.OfType) is not { } itemType || TypeInference.IsBuiltIn(itemType)
                || GenericValidator.ResolveProperty(owner, prop.Key) is not { } mprop || !TypeInference.IsCollectionProperty(mprop))
                continue;
            AddType(itemType);
        }
        if (XamlGenerator.IsGenericItemTemplate(el))
            AddType(el.OfType!);
    }

    private void CheckSelect(Element el, List<Diagnostic> diagnostics)
    {
        if (el.Props.FirstOrDefault(p => p.Key == "options") is { } options)
        {
            var ok = options.Value is RangeValue
                     || (options.Value is ListValue list && list.Items.Count > 0 && list.Items.All(i => i is StringValue));
            if (!ok)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS1005",
                    "Property 'options' on 'select' expects a range like 2019..2026 or a list of strings like [\"Open\", \"Closed\"]",
                    options.Location,
                    "write options: [\"Open\", \"Closed\"] or options: 1..10"));
            }
        }

        if (el.Props.FirstOrDefault(p => p.Key == "display") is { Value: BindingValue display } displayProp)
        {
            var hasItems = el.Props.Any(p => p.Key == "items" && p.Value is BindingValue);
            if (!hasItems || el.OfType is null)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS2013",
                    "'display:' needs items: vm.X of T on the select", displayProp.Location,
                    "select \"Vendor\" vm.Vendor items: vm.Vendors of Vendor display: item.Name;"));
            }
            else
            {
                Add(el.OfType, display.Property["item.".Length..], "string", InferenceConfidence.Default,
                    displayProp.Location, diagnostics);
            }
        }
    }

    private static void CheckWidth(Element col, List<Diagnostic> diagnostics)
    {
        var width = col.Props.FirstOrDefault(p => p.Key == "width");
        if (width is null) return;
        var ok = XamlGenerator.ParseWidth(width.Value) is not null;
        if (!ok)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS1005",
                "Property 'width' on 'col' expects auto, a positive number, * or a star width like 2*",
                width.Location, "write width: auto, width: 120 or width: 2*"));
        }
    }

    private void CollectConditionTypes(string rowType, CondExpr cond, List<Diagnostic> diagnostics)
    {
        switch (cond)
        {
            case PathCond p when p.Path.StartsWith("item.", StringComparison.Ordinal):
                Add(rowType, p.Path["item.".Length..], "bool", InferenceConfidence.Comparison, p.Location, diagnostics);
                break;
            case NotCond n:
                CollectConditionTypes(rowType, n.Inner, diagnostics);
                break;
            case BinaryCond b:
                CollectConditionTypes(rowType, b.Left, diagnostics);
                CollectConditionTypes(rowType, b.Right, diagnostics);
                break;
            case CompareCond c:
                AddOperand(c.Left, c.Right);
                AddOperand(c.Right, c.Left);
                break;
        }

        void AddOperand(Operand operand, Operand other)
        {
            if (operand is PathOperand path && path.Path.StartsWith("item.", StringComparison.Ordinal))
            {
                var isLiteral = other is LiteralOperand;
                var type = other is LiteralOperand lit ? TypeInference.LiteralType(lit.Value) : "string";
                Add(rowType, path.Path["item.".Length..], type,
                    isLiteral ? InferenceConfidence.Comparison : InferenceConfidence.Default,
                    path.Location, diagnostics);
            }
        }
    }
}
