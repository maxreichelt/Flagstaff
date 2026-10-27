using System.Globalization;
using System.Text;

namespace Flagstaff.Compiler;

/// <summary>What a generated condition function returns.</summary>
public enum ConditionReturn { Visibility, Bool }

/// <summary>
/// A <c>when(...)</c> / <c>enabled(...)</c> condition compiled to a <c>public static</c> function on the
/// Page class (<c>When_n</c> returns Visibility, <c>Enabled_n</c> returns bool).
/// </summary>
/// <param name="Name"><c>When_1</c>, <c>Enabled_2</c>, ...</param>
/// <param name="Parameters">Distinct source paths in document order (<c>vm.Count</c>, <c>item.Status</c>) with their C# types.</param>
/// <param name="CSharpExpression">The bool expression over the parameter names, e.g. <c>count &gt; 5 &amp;&amp; status != "Closed"</c>.</param>
/// <param name="SourceText">The condition as written in the .flag file (normalized spacing).</param>
public sealed record CompiledCondition(
    string Name,
    ConditionReturn Return,
    IReadOnlyList<(string Path, string CSharpType)> Parameters,
    string CSharpExpression,
    string SourceText,
    SourceLocation Location)
{
    /// <summary>C# parameter names, parallel to <see cref="Parameters"/> (camelCase last segment, deduplicated).</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary>The x:Bind argument list, e.g. <c>ViewModel.Count, ViewModel.Status</c> (item paths bind as <c>X</c>).</summary>
    public string BindArguments => string.Join(", ", Parameters.Select(p => XamlGenerator.BindPath(p.Path)));

    /// <summary>Renders the comment + one-line method for the Page code-behind (needs <c>using Microsoft.UI.Xaml;</c>).</summary>
    public string RenderMethod(string indent = "    ")
    {
        var parameters = string.Join(", ", Parameters.Select((p, i) => $"{p.CSharpType} {ParameterNames[i]}"));
        var clause = Return == ConditionReturn.Visibility ? "when" : "enabled";
        var sb = new StringBuilder();
        sb.Append(indent).Append($"// {clause}({SourceText}) at {Location.FileName}:{Location.Line}").AppendLine();
        sb.Append(indent);
        if (Return == ConditionReturn.Visibility)
            sb.Append($"public static Microsoft.UI.Xaml.Visibility {Name}({parameters}) => ({CSharpExpression}) ? Visibility.Visible : Visibility.Collapsed;");
        else
            sb.Append($"public static bool {Name}({parameters}) => {CSharpExpression};");
        sb.AppendLine();
        return sb.ToString();
    }
}

/// <summary>
/// Compiles <c>when(...)</c> and <c>enabled(...)</c> conditions for one view. Build one instance per
/// view (after type inference, before XAML emission) and pass it to both the XAML and the
/// code-behind generators.
/// <list type="bullet">
/// <item>Trivial <c>when(vm.Flag)</c> → BoolToVisibility converter binding.</item>
/// <item>Trivial <c>enabled(vm.Flag)</c> → <c>IsEnabled="{x:Bind ViewModel.Flag, Mode=OneWay}"</c>.</item>
/// <item>Anything else → a static <c>When_n</c> / <c>Enabled_n</c> function on the Page (numbered in document order).</item>
/// </list>
/// Diagnostics: FS2003 (toast/confirm/dialog need a bare <c>vm.Bool</c>), FS2004 (literal type
/// mismatch inside a condition), FS2005 (<c>vm.X</c> inside an item template, or <c>item.X</c> outside one).
/// </summary>
public sealed class ConditionCompiler
{
    /// <summary>Keywords whose <c>when()</c> drives a two-way IsOpen-style bool and so must be a bare <c>vm.Bool</c>.</summary>
    public static IReadOnlySet<string> BareBoolKeywords { get; } = new HashSet<string>(StringComparer.Ordinal) { "toast", "confirm", "dialog" };

    private readonly string _viewName;
    private readonly Dictionary<string, InferredMember> _members;
    private readonly Dictionary<Element, (string Name, int Order)> _whenNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, (string Name, int Order)> _enabledNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, string> _visibilityCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, string> _enabledCache = new(ReferenceEqualityComparer.Instance);
    private readonly List<(int Order, CompiledCondition Function)> _functions = [];
    private readonly List<Diagnostic> _diagnostics = [];
    private int _whenCount;
    private int _enabledCount;
    private int _order;

    /// <summary>The row type (<c>of T</c>) of the item template each element is in (absent outside templates).</summary>
    private readonly Dictionary<Element, string> _rowTypes = new(ReferenceEqualityComparer.Instance);

    /// <summary>The hand-written type of an item property (<c>(Order, Status)</c> → <c>OrderStatus</c>), or null.</summary>
    private readonly Func<string, string, string?>? _itemPropertyType;

    /// <param name="members">The view's members; a member with a hand-written type (<see cref="DeclaredTypes.Apply"/>) types
    /// its condition parameters with that type.</param>
    /// <param name="itemPropertyType">The hand-written type of <c>item.X</c> for a row type, typing row conditions.</param>
    public ConditionCompiler(ViewDecl view, IEnumerable<InferredMember> members, Func<string, string, string?>? itemPropertyType = null)
    {
        _viewName = view.Name;
        _itemPropertyType = itemPropertyType;
        _members = new Dictionary<string, InferredMember>(StringComparer.Ordinal);
        foreach (var m in members)
            _members.TryAdd(m.Name, m);

        // Document order: view-level property elements (Page.Resources:, VisualStateManager.VisualStateGroups:), then the
        // body; Number walks property-element values (flyouts) before children, as they are emitted.
        foreach (var prop in view.PropertyElements)
        {
            if (prop.Value is ElementValue ev)
                Number(ev.Element, null);
            else if (prop.Value is ElementsValue evs)
                evs.Elements.ForEach(e => Number(e, null));
        }
        foreach (var element in view.Body)
            Number(element, null);
    }

    /// <summary>The view's members as this compiler types them (hand-written types applied).</summary>
    public IReadOnlyCollection<InferredMember> Members => _members.Values;

    /// <summary>Row/item types named by <c>of T</c> in this view; they live in <c>{ns}.ViewModels</c>.</summary>
    private readonly HashSet<string> _userTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Qualifies the view's item types in a C# type emitted into the Page (namespace <c>{ns}</c>):
    /// <c>ProjectCard?</c> → <c>ViewModels.ProjectCard?</c>, also inside generic arguments.
    /// </summary>
    internal string QualifyType(string csType)
    {
        if (_userTypes.Count == 0)
            return csType;
        return System.Text.RegularExpressions.Regex.Replace(csType, @"(?<![\w.])[A-Za-z_]\w*",
            m => _userTypes.Contains(m.Value) ? "ViewModels." + m.Value : m.Value);
    }

    /// <summary>
    /// Set while emitting an item template (DataTemplate over a row type). Inside, conditions may only
    /// use <c>item.X</c>; <c>vm.X</c> reports FS2005. Outside, <c>item.X</c> reports FS2005.
    /// </summary>
    public bool InItemTemplate { get; set; }

    /// <summary>Generated functions, in document order. Emitted by <see cref="CodeBehindGenerator"/>.</summary>
    public IReadOnlyList<CompiledCondition> Functions =>
        _functions.OrderBy(f => f.Order).Select(f => f.Function).ToList();

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary><c>""</c> or <c> Visibility="{x:Bind ...}"</c> for the element's <c>when(...)</c>.</summary>
    public string VisibilityAttr(Element el)
    {
        if (el.When is null)
            return "";
        if (_visibilityCache.TryGetValue(el, out var cached))
            return cached;

        string attr;
        if (el.When is PathCond path)
        {
            CheckContext(path.Path, path.Location);
            attr = $" Visibility=\"{{x:Bind {XamlGenerator.BindPath(path.Path)}{ModeFor([path.Path])}, Converter={{StaticResource BoolToVisibility}}}}\"";
        }
        else
        {
            var (name, order) = NameFor(el, _whenNames, "When_", ref _whenCount);
            var fn = Compile(el.When, name, ConditionReturn.Visibility, RowOf(el));
            _functions.Add((order, fn));
            attr = $" Visibility=\"{{x:Bind local:{_viewName}.{name}({fn.BindArguments}){ModeFor(fn.Parameters.Select(p => p.Path))}}}\"";
        }
        _visibilityCache[el] = attr;
        return attr;
    }

    /// <summary><c>""</c> or <c> IsEnabled="{x:Bind ...}"</c> for the element's <c>enabled(...)</c>.</summary>
    public string EnabledAttr(Element el)
    {
        if (el.Enabled is null)
            return "";
        if (_enabledCache.TryGetValue(el, out var cached))
            return cached;

        string attr;
        if (IsBareEnabled(el, RowOf(el)) && el.Enabled is PathCond path)
        {
            CheckContext(path.Path, path.Location);
            attr = $" IsEnabled=\"{{x:Bind {XamlGenerator.BindPath(path.Path)}{ModeFor([path.Path])}}}\"";
        }
        else
        {
            var (name, order) = NameFor(el, _enabledNames, "Enabled_", ref _enabledCount);
            var fn = Compile(el.Enabled, name, ConditionReturn.Bool, RowOf(el));
            _functions.Add((order, fn));
            attr = $" IsEnabled=\"{{x:Bind local:{_viewName}.{name}({fn.BindArguments}){ModeFor(fn.Parameters.Select(p => p.Path))}}}\"";
        }
        _enabledCache[el] = attr;
        return attr;
    }

    /// <summary>
    /// <c>, Mode=OneWay</c> unless every path is an <c>item.X</c> path: row types are plain generated classes, so
    /// bindings over them stay OneTime (x:Bind's default; OneWay would be WMC1506).
    /// </summary>
    private static string ModeFor(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        return list.Count > 0 && list.All(p => p.StartsWith("item.", StringComparison.Ordinal)) ? "" : ", Mode=OneWay";
    }

    /// <summary>
    /// For keywords whose <c>when()</c> is a two-way bool (toast IsOpen, dialog): the bound path
    /// (<c>ViewModel.X</c>) when <c>when(vm.X)</c> is bare, else null (FS2003 is reported at construction).
    /// </summary>
    public string? BareBoolPath(Element el) =>
        el.When is PathCond path ? XamlGenerator.BindPath(path.Path) : null;

    /// <summary>
    /// A bare <c>enabled(vm.X)</c> binds <c>IsEnabled</c> directly, unless <c>X</c> is a hand-written <c>bool?</c>
    /// (<c>IsEnabled</c> is <c>bool</c>; an <c>Enabled_n</c> function compares it with <c>true</c>).
    /// </summary>
    private bool IsBareEnabled(Element el, string? row) =>
        el.Enabled is PathCond path && TypeOf(path.Path, new(), row) != "bool?";

    private string? RowOf(Element el) => _rowTypes.GetValueOrDefault(el);

    // ── Numbering (document order) ─────────────────────────────────

    /// <param name="row">The row type of the enclosing item template (null outside one): a table's or list's children,
    /// a generic items control's template children, and a table/list's row-level property elements (ContextFlyout:).</param>
    private void Number(Element el, string? row)
    {
        if (row is not null)
            _rowTypes[el] = row;
        if (el.OfType is { } ofType)
            _userTypes.Add(ofType);
        if (BareBoolKeywords.Contains(el.Keyword) && el.When is not null
            && !(el.When is PathCond { Path: var whenPath } && whenPath.StartsWith("vm.", StringComparison.Ordinal)))
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS2003",
                $"'{el.Keyword}' needs a bare bool in when(): it binds two-way to show and dismiss",
                el.When!.Location,
                $"add a bool property, e.g. {el.Keyword} ... when(vm.Show{Capitalize(el.Keyword)})"));
        }

        if (el.When is not null and not PathCond)
            _whenNames[el] = ($"When_{++_whenCount}", _order++);
        if (el.Enabled is not null && !IsBareEnabled(el, row))
            _enabledNames[el] = ($"Enabled_{++_enabledCount}", _order++);

        // Property-element values (`Flyout: MenuFlyout { MenuFlyoutItem ... enabled(...); }`) come before the children,
        // as they are emitted. Row scoping follows ItemTypeRegistry.Walk.
        var isRowOwner = !el.IsGeneric && el.Keyword is "table" or "list";
        foreach (var prop in el.Props)
        {
            var propRow = isRowOwner && XamlGenerator.IsRowPropertyElement(el, prop) ? el.OfType : row;
            if (prop.Value is ElementValue ev)
                Number(ev.Element, propRow);
            else if (prop.Value is ElementsValue evs)
                evs.Elements.ForEach(e => Number(e, propRow));
        }

        if (el.Body is BlockBody block)
        {
            var childRow = isRowOwner || XamlGenerator.IsGenericItemTemplate(el) ? el.OfType : row;
            foreach (var child in block.Children)
            {
                if (child is ChildElement ce)
                    Number(ce.Element, childRow);
            }
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private (string Name, int Order) NameFor(Element el, Dictionary<Element, (string Name, int Order)> names, string prefix, ref int counter)
    {
        if (names.TryGetValue(el, out var assigned))
            return assigned;
        // An element the constructor never saw (synthesized by a generator): number it now.
        assigned = ($"{prefix}{++counter}", _order++);
        names[el] = assigned;
        return assigned;
    }

    // ── Compilation ────────────────────────────────────────────────

    private CompiledCondition Compile(CondExpr cond, string name, ConditionReturn ret, string? row)
    {
        var paths = new List<(string Path, SourceLocation Location)>();
        CollectPaths(cond, paths);
        var distinct = paths.DistinctBy(p => p.Path).ToList();
        foreach (var (path, location) in distinct)
            CheckContext(path, location);

        var localTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        InferLocalTypes(cond, localTypes);

        var parameters = new List<(string Path, string CSharpType)>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, _) in distinct)
        {
            parameters.Add((path, QualifyType(TypeOf(path, localTypes, row))));
            names[path] = UniqueName(path, used);
        }

        _row = row;
        var expression = Emit(cond, 0, names, parameters.ToDictionary(p => p.Path, p => p.CSharpType, StringComparer.Ordinal));
        return new CompiledCondition(name, ret, parameters, expression, Print(cond, 0), cond.Location)
        {
            ParameterNames = distinct.Select(p => names[p.Path]).ToList(),
        };
    }

    private void CheckContext(string path, SourceLocation location)
    {
        var isItem = path.StartsWith("item.", StringComparison.Ordinal);
        if (InItemTemplate && !isItem)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS2005",
                $"'{path}' cannot be used in a condition inside an item template; the template is bound to the row",
                location,
                "use item.X for row values, or move the condition outside the list/table"));
        }
        else if (!InItemTemplate && isItem)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS2005",
                $"'{path}' is only valid inside an item template (list/table row)",
                location,
                "use vm.X outside item templates"));
        }
    }

    private static void CollectPaths(CondExpr cond, List<(string, SourceLocation)> paths)
    {
        switch (cond)
        {
            case PathCond p: paths.Add((p.Path, p.Location)); break;
            case NotCond n: CollectPaths(n.Inner, paths); break;
            case BinaryCond b: CollectPaths(b.Left, paths); CollectPaths(b.Right, paths); break;
            case CompareCond c:
                if (c.Left is PathOperand l) paths.Add((l.Path, l.Location));
                if (c.Right is PathOperand r) paths.Add((r.Path, r.Location));
                break;
        }
    }

    /// <summary>Types implied by the condition itself (used for item.X and for vm.X missing from inference).</summary>
    private static void InferLocalTypes(CondExpr cond, Dictionary<string, string> types)
    {
        switch (cond)
        {
            case PathCond p: types.TryAdd(p.Path, "bool"); break;
            case NotCond n: InferLocalTypes(n.Inner, types); break;
            case BinaryCond b: InferLocalTypes(b.Left, types); InferLocalTypes(b.Right, types); break;
            case CompareCond c:
                Add(c.Left, c.Right);
                Add(c.Right, c.Left);
                break;
        }

        void Add(Operand operand, Operand other)
        {
            if (operand is PathOperand path)
                types.TryAdd(path.Path, other is LiteralOperand lit ? TypeInference.LiteralType(lit.Value) : "string");
        }
    }

    /// <summary>The row type of the condition being emitted (for <see cref="IsExplicit"/> during <see cref="Emit"/>).</summary>
    private string? _row;

    private string TypeOf(string path, Dictionary<string, string> localTypes, string? row)
    {
        if (path.StartsWith("vm.", StringComparison.Ordinal) && _members.TryGetValue(path[3..], out var member))
            return member.CSharpType;
        if (DeclaredItemType(path, row) is { } declared)
            return declared;
        return localTypes.GetValueOrDefault(path, "string");
    }

    /// <summary>The hand-written type of <c>item.X</c> in a row of <paramref name="row"/>, or null.</summary>
    private string? DeclaredItemType(string path, string? row) =>
        row is not null && path.StartsWith("item.", StringComparison.Ordinal) ? _itemPropertyType?.Invoke(row, path["item.".Length..]) : null;

    /// <summary>
    /// The path's type is settled: an Explicit vm member (already checked by TypeInference, FS2004) or a hand-written
    /// declaration (checked against its usage, FS2018; a string literal compares with its name).
    /// </summary>
    private bool IsExplicit(string path) =>
        path.StartsWith("vm.", StringComparison.Ordinal)
            ? _members.TryGetValue(path[3..], out var m) && (m.Confidence == InferenceConfidence.Explicit || m.IsDeclared)
            : DeclaredItemType(path, _row) is not null;

    /// <summary>A type a condition compares with a string literal by its name (<c>$"{status}" == "Open"</c>): an enum or other non-built-in type.</summary>
    private static bool ComparesByName(string? type) =>
        type is not null && type.TrimEnd('?') is var core && core != "string" && !TypeInference.IsBuiltIn(core.Replace("System.", "", StringComparison.Ordinal));

    private static string UniqueName(string path, HashSet<string> used)
    {
        var last = path[(path.LastIndexOf('.') + 1)..];
        var baseName = last.Length == 0 ? "value" : char.ToLowerInvariant(last[0]) + last[1..];
        baseName = new string(baseName.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        var candidate = baseName;
        for (int n = 2; !used.Add(candidate); n++)
            candidate = baseName + n.ToString(CultureInfo.InvariantCulture);
        return CSharpKeywords.Contains(candidate) ? "@" + candidate : candidate;
    }

    // Precedence: 1 ||, 2 &&, 3 comparison, 4 operand of !
    private string Emit(CondExpr cond, int parentPrec, Dictionary<string, string> names, Dictionary<string, string> types)
    {
        switch (cond)
        {
            case PathCond p when types[p.Path] == "bool?":
                // A hand-written bool? (null counts as false).
                return 3 < parentPrec ? $"({names[p.Path]} == true)" : $"{names[p.Path]} == true";
            case PathCond p:
                return names[p.Path];
            case NotCond n:
                return "!" + Emit(n.Inner, 4, names, types);
            case BinaryCond b:
            {
                var prec = b.Op == LogicalOp.Or ? 1 : 2;
                var op = b.Op == LogicalOp.Or ? "||" : "&&";
                var text = $"{Emit(b.Left, prec, names, types)} {op} {Emit(b.Right, prec, names, types)}";
                return prec < parentPrec ? $"({text})" : text;
            }
            case CompareCond c:
            {
                var leftType = OperandType(c.Left, types);
                var rightType = OperandType(c.Right, types);
                CheckComparison(c, leftType, rightType);
                var text = $"{EmitCompared(c.Left, leftType, c.Right, rightType, names)} {OpText(c.Op)} {EmitCompared(c.Right, rightType, c.Left, leftType, names)}";
                return 3 < parentPrec ? $"({text})" : text;
            }
            default:
                throw new InvalidOperationException($"Unknown condition node {cond.GetType().Name}");
        }
    }

    private static string? OperandType(Operand operand, Dictionary<string, string> types) => operand switch
    {
        PathOperand p => types[p.Path],
        LiteralOperand l => TypeInference.LiteralType(l.Value),
        _ => null,
    };

    private void CheckComparison(CompareCond c, string? leftType, string? rightType)
    {
        foreach (var (operand, otherOperand, otherType) in new[] { (c.Left, c.Right, rightType), (c.Right, c.Left, leftType) })
        {
            if (operand is not LiteralOperand literal || otherType is null)
                continue;

            // Explicitly typed vm members were already checked by TypeInference (FS2004).
            var otherPath = (otherOperand as PathOperand)?.Path;
            if (otherPath is not null && IsExplicit(otherPath))
                continue;

            var literalType = TypeInference.LiteralType(literal.Value);
            if (literal.Value is NullValue)
            {
                if (!TypeInference.IsCompatible(otherType, "string?"))
                    Report(literal.Location, $"null is only valid for reference or nullable types, but this is {otherType}");
                continue;
            }

            if (otherOperand is PathOperand && !TypeInference.IsCompatible(otherType, literalType))
                Report(literal.Location, $"{otherPath} is {otherType} but is compared with a {literalType} literal");
        }

        if (c.Op is ComparisonOp.Gt or ComparisonOp.Lt or ComparisonOp.Gte or ComparisonOp.Lte)
        {
            foreach (var t in new[] { leftType, rightType })
            {
                if (t?.TrimEnd('?') is "string" or "bool")
                {
                    Report(c.Location, $"'{OpText(c.Op)}' cannot compare {t} values");
                    break;
                }
            }
        }

        void Report(SourceLocation location, string message) =>
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS2004", message, location,
                "compare values of the same type; ordering (< > <= >=) needs numbers"));
    }

    /// <summary>
    /// <see cref="EmitOperand"/>, except that a path of a hand-written enum (or other non-built-in) type compared with a
    /// string literal compares its name: <c>$"{status}" == "Open"</c>.
    /// </summary>
    private static string EmitCompared(Operand operand, string? type, Operand other, string? otherType, Dictionary<string, string> names) =>
        operand is PathOperand p && other is LiteralOperand { Value: StringValue } && ComparesByName(type)
            ? $"$\"{{{names[p.Path]}}}\""
            : EmitOperand(operand, otherType, names);

    private static string EmitOperand(Operand operand, string? otherType, Dictionary<string, string> names)
    {
        if (operand is PathOperand p)
            return names[p.Path];

        var literal = ((LiteralOperand)operand).Value;
        var otherBase = otherType?.TrimEnd('?');
        return literal switch
        {
            IntValue i => i.Value.ToString(CultureInfo.InvariantCulture) + (otherBase == "decimal" ? "m" : ""),
            FloatValue f => FloatLiteral(f.Value, otherBase),
            StringValue s => CSharpString(s.Value),
            BoolValue b => b.Value ? "true" : "false",
            NullValue => "null",
            _ => throw new InvalidOperationException($"Unsupported literal {literal.GetType().Name}"),
        };
    }

    private static string FloatLiteral(double value, string? otherBase)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (otherBase == "decimal")
            return text + "m";
        if (otherBase == "float")
            return text + "f";
        // Keep it a double literal: 5.0 prints as "5".
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    private static string CSharpString(string raw)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in raw)
        {
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ when char.IsControl(ch) => $"\\u{(int)ch:x4}",
                _ => ch.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    private static string OpText(ComparisonOp op) => op switch
    {
        ComparisonOp.Gt => ">",
        ComparisonOp.Lt => "<",
        ComparisonOp.Gte => ">=",
        ComparisonOp.Lte => "<=",
        ComparisonOp.Eq => "==",
        _ => "!=",
    };

    // ── Source text ────────────────────────────────────────────────

    /// <summary>Prints a condition back in .flag syntax (minimal parentheses).</summary>
    public static string Print(CondExpr cond) => Print(cond, 0);

    private static string Print(CondExpr cond, int parentPrec)
    {
        switch (cond)
        {
            case PathCond p:
                return p.Path;
            case NotCond n:
                return "!" + Print(n.Inner, 4);
            case BinaryCond b:
            {
                var prec = b.Op == LogicalOp.Or ? 1 : 2;
                var text = $"{Print(b.Left, prec)} {(b.Op == LogicalOp.Or ? "||" : "&&")} {Print(b.Right, prec)}";
                return prec < parentPrec ? $"({text})" : text;
            }
            case CompareCond c:
            {
                var text = $"{PrintOperand(c.Left)} {OpText(c.Op)} {PrintOperand(c.Right)}";
                return 3 < parentPrec ? $"({text})" : text;
            }
            default:
                return cond.ToString();
        }
    }

    private static string PrintOperand(Operand operand) => operand switch
    {
        PathOperand p => p.Path,
        LiteralOperand { Value: StringValue s } => $"\"{s.Value}\"",
        LiteralOperand { Value: IntValue i } => i.Value.ToString(CultureInfo.InvariantCulture),
        LiteralOperand { Value: FloatValue f } => f.Value.ToString("R", CultureInfo.InvariantCulture),
        LiteralOperand { Value: BoolValue b } => b.Value ? "true" : "false",
        LiteralOperand { Value: NullValue } => "null",
        _ => operand.ToString(),
    };

    private static readonly HashSet<string> CSharpKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    ];
}
