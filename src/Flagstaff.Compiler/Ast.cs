namespace Flagstaff.Compiler;

/// <summary>Root of a .flag file: <c>use</c> declarations, then zero or more top-level declarations.</summary>
public sealed record FlagFile(List<TopLevelDecl> Declarations, List<UseDecl> Uses)
{
    public FlagFile(List<TopLevelDecl> declarations) : this(declarations, []) { }
}

/// <summary><c>use "&lt;assembly&gt;" as prefix;</c> at the top of a file: declares <c>xmlns:prefix="using:&lt;assembly&gt;"</c>.</summary>
public sealed record UseDecl(string Assembly, string Prefix, SourceLocation Location);

// ── Top-level ──────────────────────────────────────────────────────

public abstract record TopLevelDecl;
public sealed record AppDecl(string Name, List<AppMember> Members, SourceLocation Location) : TopLevelDecl;

/// <param name="OnLoadLocation">Location of the <c>onload</c> keyword, when present.</param>
public sealed record ViewDecl(
    string Name,
    BindDecl? Binding,
    string? OnLoadTarget,
    List<Element> Body,
    SourceLocation Location,
    SourceLocation? OnLoadLocation = null) : TopLevelDecl
{
    /// <summary>
    /// View-level property elements: <c>Page.Resources: { ... }</c> lines directly in the view body
    /// (keys are pass-through keys, see <see cref="AstExtensions.IsPassThroughKey"/>).
    /// </summary>
    public List<PropPair> PropertyElements { get; init; } = [];

    /// <summary>
    /// Every <c>name:</c> declaration in the view, in source order, including elements nested in children and in
    /// property-element values. Duplicates are kept (the validator reports them as FS1042).
    /// </summary>
    public IReadOnlyList<NamedElement> NamedElements { get; init; } = [];
}

/// <summary>An element declared with <c>name: X</c> (<see cref="ViewDecl.NamedElements"/>); the target of <c>ref(X)</c>.</summary>
public sealed record NamedElement(string Name, Element Owner, SourceLocation Location);

// ── App members ────────────────────────────────────────────────────

public abstract record AppMember;
public sealed record ThemeBlock(List<PropPair> Properties) : AppMember;
public sealed record NavBlock(NavMode Mode, List<NavMember> Members) : AppMember;

public enum NavMode { Top, Side }

public abstract record NavMember;
public sealed record BrandDecl(string Text) : NavMember;
public sealed record NavLinkDecl(string Label, List<PropPair> Props, string Target, SourceLocation Location) : NavMember;

// ── View members ───────────────────────────────────────────────────

public sealed record BindDecl(string ViewModelType);

// ── Elements (the recursive heart of the DSL) ─────────────────────

/// <summary>
/// One element in the universal form
/// <c>keyword "label" vm.Binding key: value ... when(...) enabled(...) -> target { }</c>.
/// </summary>
/// <param name="When">Visibility condition from <c>when(...)</c>.</param>
/// <param name="NavTarget">Arrow target: <c>vm.XCommand</c> or a view name from <c>navigate(View)</c>.</param>
/// <param name="Location">Location of the keyword token.</param>
/// <param name="Enabled">Enablement condition from <c>enabled(...)</c>.</param>
/// <param name="NavTargetArg">Command argument from <c>-> vm.XCommand(item)</c>; only <c>"item"</c> is valid.</param>
public sealed record Element(
    string Keyword,
    List<PropPair> Props,
    ElementBody Body,
    CondExpr? When,
    FormatClause? Format,
    string? OfType,
    string? NavTarget,
    SourceLocation Location,
    CondExpr? Enabled = null,
    string? NavTargetArg = null)
{
    /// <summary>
    /// True when the keyword is a WinUI type name (PascalCase, or <c>prefix.Type</c> for a <c>use</c> namespace)
    /// rather than a lowercase schema keyword. <see cref="Keyword"/> is then the name as written.
    /// </summary>
    public bool IsGeneric { get; init; }

    /// <summary>The <c>use</c> prefix of a namespaced generic element (<c>lvc</c> for <c>lvc.CartesianChart</c>), else null.</summary>
    public string? NamespacePrefix { get; init; }

    /// <summary>The type name without prefix (<c>CartesianChart</c>, <c>Expander</c>); equals <see cref="Keyword"/> for keyword elements.</summary>
    public string TypeName => NamespacePrefix is null ? Keyword : Keyword[(NamespacePrefix.Length + 1)..];

    /// <summary><c>on Event -> target;</c> clauses, in source order.</summary>
    public List<EventBinding> Events { get; init; } = [];
}

/// <summary>
/// <c>on EventName -> target [(item)]</c>. <paramref name="Target"/> uses the arrow forms:
/// <c>vm.XCommand</c>, a view name (from <c>navigate(View)</c> or a bare <c>-> View</c>), or <c>cancel</c>.
/// </summary>
/// <param name="Arg"><c>"item"</c> for <c>-> vm.XCommand(item)</c>, else null.</param>
public sealed record EventBinding(string EventName, string Target, string? Arg, SourceLocation Location);

public abstract record ElementBody;
public sealed record BlockBody(List<ElementOrText> Children) : ElementBody;
public sealed record EmptyBody : ElementBody;
public sealed record RawXamlBody(string Xaml) : ElementBody;

public abstract record ElementOrText;
public sealed record ChildElement(Element Element) : ElementOrText;
public sealed record TextContent(string Text) : ElementOrText;

// ── Properties ─────────────────────────────────────────────────────

/// <summary>
/// A named property. The positional label and binding are stored as the pseudo-props
/// <c>_label</c> and <c>_binding</c>.
/// </summary>
public sealed record PropPair(string Key, PropValue Value, SourceLocation Location)
{
    /// <summary>
    /// The <c>of T</c> written directly after this prop's binding (<c>ItemsSource: vm.Rows of Row</c>,
    /// <c>Value: vm.Volume of double</c>); only recorded for pass-through keys. The element's <see cref="Element.OfType"/>
    /// is still set (last <c>of</c> wins), so keyword elements are unaffected.
    /// </summary>
    public string? TypeArg { get; init; }
}

public abstract record PropValue;
public sealed record StringValue(string Value) : PropValue;
public sealed record IntValue(int Value) : PropValue;
public sealed record FloatValue(double Value) : PropValue;
public sealed record ColorValue(string Hex) : PropValue;
public sealed record BoolValue(bool Value) : PropValue;
/// <summary>A binding path: <c>Property</c> is the bare name for <c>vm.X</c>, or <c>item.X</c> for row bindings.</summary>
public sealed record BindingValue(string Property) : PropValue;
public sealed record RangeValue(int Start, int End) : PropValue;
public sealed record IdentValue(string Name) : PropValue;
public sealed record NullValue : PropValue;
public sealed record ListValue(List<PropValue> Items) : PropValue;

/// <summary>Property element with one object: <c>Flyout: MenuFlyout { ... }</c> / <c>Icon: SymbolIcon Symbol: "Save";</c>.</summary>
public sealed record ElementValue(Element Element) : PropValue;
/// <summary>Collection property element: <c>VisualStateManager.VisualStateGroups: { VisualStateGroup { ... } }</c>.</summary>
public sealed record ElementsValue(List<Element> Elements) : PropValue;
/// <summary><c>theme(Key)</c> → <c>{ThemeResource Key}</c>.</summary>
public sealed record ThemeResourceValue(string Key) : PropValue;
/// <summary><c>resource(Key)</c> → <c>{StaticResource Key}</c>.</summary>
public sealed record StaticResourceValue(string Key) : PropValue;
/// <summary><c>load(vm.X)</c> → <c>x:Load</c>; <paramref name="Path"/> is the parser-stored binding (<c>X</c> for vm.X, or <c>item.X</c>).</summary>
public sealed record LoadValue(string Path) : PropValue;
/// <summary><c>ref(Name)</c>, <c>ref(Nav).DisplayMode</c>, <c>ref(Nav).Pane.Width</c>: a reference to a <c>name:</c> declaration.</summary>
/// <param name="MemberPath">The dotted path after <c>ref(Name)</c> (<c>DisplayMode</c>, <c>Pane.Width</c>), or null.</param>
public sealed record RefValue(string Name, string? MemberPath) : PropValue;

// ── Conditions: when(...) / enabled(...) ───────────────────────────
//
//   cond    := or
//   or      := and ("||" and)*
//   and     := unary ("&&" unary)*
//   unary   := "!" unary | primary
//   primary := "(" cond ")" | operand [cmpop operand]
//   operand := vm.X | item.X | int | float | "string" | true | false | null

public abstract record CondExpr(SourceLocation Location);

/// <summary>Bare boolean path: <c>vm.X</c> or <c>item.X</c> (Path includes the prefix).</summary>
public sealed record PathCond(string Path, SourceLocation Location) : CondExpr(Location);
public sealed record NotCond(CondExpr Inner, SourceLocation Location) : CondExpr(Location);
public sealed record BinaryCond(CondExpr Left, LogicalOp Op, CondExpr Right, SourceLocation Location) : CondExpr(Location);
public sealed record CompareCond(Operand Left, ComparisonOp Op, Operand Right, SourceLocation Location) : CondExpr(Location);

public enum LogicalOp { And, Or }

public abstract record Operand(SourceLocation Location);
/// <summary><c>vm.X</c> or <c>item.X</c> (Path includes the prefix).</summary>
public sealed record PathOperand(string Path, SourceLocation Location) : Operand(Location);
/// <summary>Int, float, string, bool or null literal.</summary>
public sealed record LiteralOperand(PropValue Value, SourceLocation Location) : Operand(Location);

// ── Clauses ────────────────────────────────────────────────────────

public sealed record FormatClause(FormatKind Kind, int? Precision);

public enum ComparisonOp { Gt, Lt, Gte, Lte, Eq, Neq }
public enum FormatKind { Currency, Date, Percent, Number }

// ── Helpers ────────────────────────────────────────────────────────

public static class AstExtensions
{
    /// <summary>
    /// A XAML pass-through key: starts with an uppercase letter (<c>Margin</c>, <c>Flyout</c>); dotted attached
    /// properties are allowed (<c>Grid.Row</c>, <c>AutomationProperties.Name</c>). Lowercase keys are Flagstaff props.
    /// </summary>
    public static bool IsPassThroughKey(string key) => key.Length > 0 && char.IsUpper(key[0]);

    /// <summary>True when <paramref name="ident"/> starts with an uppercase letter (a WinUI type or member name).</summary>
    public static bool IsPascalCase(string ident) => ident.Length > 0 && char.IsUpper(ident[0]);
}
