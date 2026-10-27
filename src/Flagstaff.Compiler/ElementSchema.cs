using System.Reflection;

namespace Flagstaff.Compiler;

/// <summary>The value shape a property accepts.</summary>
public enum PropKind
{
    /// <summary><c>"text"</c></summary>
    String,
    /// <summary><c>3</c></summary>
    Int,
    /// <summary><c>1.5</c> or <c>3</c></summary>
    Float,
    /// <summary><c>true</c> / <c>false</c> (or a bare flag when <see cref="PropSpec.Bare"/>)</summary>
    Bool,
    /// <summary><c>#c0392b</c></summary>
    Color,
    /// <summary><c>vm.X</c></summary>
    Binding,
    /// <summary><c>item.X</c></summary>
    ItemBinding,
    /// <summary><c>2019..2026</c></summary>
    Range,
    /// <summary><c>["a", "b"]</c> / <c>[1, 2]</c></summary>
    List,
    /// <summary>A bare identifier (<c>home</c>) or a string; constrained by <see cref="PropSpec.Enum"/> when set.</summary>
    Ident,
    /// <summary>A grid width: <c>200</c>, <c>auto</c>, <c>"2*"</c>.</summary>
    Width,
    /// <summary>Any value.</summary>
    Any,
}

/// <param name="Kind">The accepted value shape.</param>
/// <param name="Enum">When set, the allowed identifier values.</param>
/// <param name="Bare">True for flags written without a value (<c>bold</c>).</param>
public sealed record PropSpec(PropKind Kind, string[]? Enum = null, bool Bare = false);

/// <summary>What one element keyword accepts.</summary>
/// <param name="AllowsLabel">Positional <c>"label"</c>.</param>
/// <param name="AllowsBinding">Positional <c>vm.X</c>.</param>
/// <param name="AllowsChildren">A <c>{ ... }</c> block.</param>
/// <param name="AllowsArrow"><c>-> vm.XCommand</c> / <c>-> navigate(View)</c> / <c>-> View</c>.</param>
/// <param name="AllowsItemBinding">Positional <c>item.X</c> (row/item templates).</param>
/// <param name="Props">Named properties (positional <c>_label</c>/<c>_binding</c> are not listed).</param>
public sealed record KeywordSchema(
    string Keyword,
    bool AllowsLabel,
    bool AllowsBinding,
    bool AllowsChildren,
    bool AllowsArrow,
    bool AllowsItemBinding,
    IReadOnlyDictionary<string, PropSpec> Props)
{
    /// <summary>
    /// Manifest short type name of the emitter's <em>root tag</em>: the tag <c>CommonAttrs()</c> is appended to, which
    /// receives pass-through attributes (<c>Margin: "0,8"</c>) and property elements (<c>Flyout: MenuFlyout { }</c>).
    /// Null when the keyword has no root tag of its own (<c>xaml</c>, <c>col</c>, <c>empty</c>). Set from
    /// <see cref="ElementSchema.RootTypes"/>; the per-keyword table is in the compiler README.
    /// </summary>
    public string? RootType { get; init; }
}

/// <summary>
/// Marks a <c>private static void RegisterXxx()</c> method in an <see cref="ElementSchema"/>
/// partial file. Marked methods run once, after <c>RegisterCore()</c>, in ordinal name order.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SchemaRegistrationAttribute : Attribute;

/// <summary>
/// The single source of truth for element keywords and their properties. The parser derives its
/// keyword set from <see cref="All"/>; <see cref="SemanticValidator"/> checks every element against it.
/// <para>
/// To add keywords from a new partial file (<c>ElementSchema.Lists.cs</c>, ...):
/// <code>
/// public static partial class ElementSchema
/// {
///     [SchemaRegistration]
///     private static void RegisterLists()
///     {
///         Register("list", children: true, props: new() { ["items"] = Bind, ["selected"] = Bind });
///         Extend("table", new() { ["sortable"] = Bare });
///         RegisterUniversal("margin", Width);
///     }
/// }
/// </code>
/// </para>
/// </summary>
public static partial class ElementSchema
{
    // Created lazily by the registration helpers so registration order across partial files
    // never depends on static field initializer order.
    private static Dictionary<string, KeywordSchema>? s_keywords;
    private static Dictionary<string, PropSpec>? s_universal;

    static ElementSchema()
    {
        RegisterCore();

        var extra = typeof(ElementSchema)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<SchemaRegistrationAttribute>() is not null)
            .OrderBy(m => m.Name, StringComparer.Ordinal);
        foreach (var method in extra)
            method.Invoke(null, null);

        s_keywords ??= new(StringComparer.Ordinal);
        s_universal ??= new(StringComparer.Ordinal);

        foreach (var (keyword, rootType) in RootTypes)
        {
            if (s_keywords.TryGetValue(keyword, out var schema))
                s_keywords[keyword] = schema with { RootType = rootType };
        }
    }

    /// <summary>
    /// <see cref="KeywordSchema.RootType"/> per keyword: the WinUI type of the tag each emitter appends
    /// <c>CommonAttrs()</c> to (see the compiler README, "Pass-through on keyword elements"). Variants:
    /// <c>heading</c> with <c>icon:</c> emits a <c>StackPanel</c>; <c>badge</c> bound to a non-int emits a
    /// <c>Border</c> (an int-bound badge is an <c>InfoBadge</c>); <c>stack</c>/<c>column</c>/<c>group</c> become a
    /// <c>Grid</c> when a child has <c>fill</c>. Pass-through props are validated against this declared type and land
    /// on the emitted tag; a prop the variant tag lacks is FS1031 (<c>XamlGenerator.CheckVariantTag</c>).
    /// Keywords not listed (<c>xaml</c>, <c>col</c>, <c>empty</c>) have none.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RootTypes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Inputs
        ["textbox"] = "TextBox", ["textarea"] = "TextBox", ["number"] = "NumberBox", ["select"] = "ComboBox",
        ["checkbox"] = "CheckBox", ["radio"] = "RadioButton", ["toggle"] = "ToggleSwitch",
        ["datepicker"] = "CalendarDatePicker", ["button"] = "Button", ["filePicker"] = "Button",
        ["link"] = "HyperlinkButton",
        // Display
        ["image"] = "Image", ["progress"] = "ProgressBar", ["spinner"] = "ProgressRing",
        ["text"] = "TextBlock", ["heading"] = "TextBlock", ["badge"] = "InfoBadge",
        ["card"] = "Border", ["stat"] = "Border", ["separator"] = "Border", ["spacer"] = "Border",
        // Layout
        ["group"] = "StackPanel", ["stack"] = "StackPanel", ["column"] = "StackPanel", ["summary"] = "StackPanel",
        ["row"] = "Grid", ["cards"] = "Grid", ["split"] = "Grid", ["toolbar"] = "Grid", ["line"] = "Grid",
        // Data
        ["table"] = "ListView", ["list"] = "ListView",
        // Overlays
        ["alert"] = "InfoBar", ["toast"] = "InfoBar", ["dialog"] = "Grid", ["confirm"] = "Grid",
    };

    /// <summary>Every element keyword, keyed by keyword.</summary>
    public static IReadOnlyDictionary<string, KeywordSchema> All => s_keywords!;

    // ── Extension point ────────────────────────────────────────────
    /// <summary>
    /// EXTENSION POINT: properties every keyword accepts (e.g. layout props such as <c>margin</c>).
    /// Empty in the core schema; Wave 1 streams populate it with <see cref="RegisterUniversal"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, PropSpec> Universal => s_universal!;

    /// <summary>Properties accepted inside <c>app { theme { ... } }</c>.</summary>
    public static IReadOnlyDictionary<string, PropSpec> Theme { get; } = new Dictionary<string, PropSpec>(StringComparer.Ordinal)
    {
        ["accent"] = new(PropKind.Color),
        ["font"] = new(PropKind.String),
        ["backdrop"] = new(PropKind.Ident, ["mica", "acrylic", "none"]),
        ["titlebar"] = new(PropKind.Ident, ["extended", "default"]),
        ["styles"] = new(PropKind.String),
    };

    /// <summary>Looks up a property for a keyword: the keyword's own props first, then <see cref="Universal"/>.</summary>
    public static bool TryGetProp(string keyword, string prop, out PropSpec spec)
    {
        if (All.TryGetValue(keyword, out var schema) && schema.Props.TryGetValue(prop, out var own))
        {
            spec = own;
            return true;
        }
        if (Universal.TryGetValue(prop, out var universal))
        {
            spec = universal;
            return true;
        }
        spec = null!;
        return false;
    }

    /// <summary>The keywords that declare <paramref name="prop"/> (excluding universal props).</summary>
    public static IEnumerable<string> KeywordsWithProp(string prop) =>
        All.Values.Where(s => s.Props.ContainsKey(prop)).Select(s => s.Keyword).OrderBy(k => k, StringComparer.Ordinal);

    // ── Registration helpers ───────────────────────────────────────

    /// <summary>Registers a new keyword. Throws if the keyword already exists (use <see cref="Extend"/>).</summary>
    internal static void Register(
        string keyword,
        bool label = false,
        bool binding = false,
        bool children = false,
        bool arrow = false,
        bool itemBinding = false,
        Dictionary<string, PropSpec>? props = null)
    {
        s_keywords ??= new(StringComparer.Ordinal);
        if (s_keywords.ContainsKey(keyword))
            throw new InvalidOperationException($"Keyword '{keyword}' is already registered; use Extend() to add props.");
        s_keywords[keyword] = new KeywordSchema(
            keyword, label, binding, children, arrow, itemBinding,
            new Dictionary<string, PropSpec>(props ?? [], StringComparer.Ordinal));
    }

    /// <summary>Adds (or replaces) props on an already registered keyword.</summary>
    internal static void Extend(string keyword, Dictionary<string, PropSpec> props)
    {
        if (s_keywords is null || !s_keywords.TryGetValue(keyword, out var existing))
            throw new InvalidOperationException($"Keyword '{keyword}' is not registered.");
        var merged = new Dictionary<string, PropSpec>(existing.Props, StringComparer.Ordinal);
        foreach (var (key, spec) in props)
            merged[key] = spec;
        s_keywords[keyword] = existing with { Props = merged };
    }

    /// <summary>Replaces the structural flags of an already registered keyword.</summary>
    internal static void Reshape(string keyword, Func<KeywordSchema, KeywordSchema> change)
    {
        if (s_keywords is null || !s_keywords.TryGetValue(keyword, out var existing))
            throw new InvalidOperationException($"Keyword '{keyword}' is not registered.");
        s_keywords[keyword] = change(existing) with { Keyword = keyword };
    }

    /// <summary>Adds a property every keyword accepts (see <see cref="Universal"/>).</summary>
    internal static void RegisterUniversal(string prop, PropSpec spec)
    {
        s_universal ??= new(StringComparer.Ordinal);
        s_universal[prop] = spec;
    }

    // ── PropSpec shorthands (properties, so they never depend on initializer order) ──

    internal static PropSpec Str => new(PropKind.String);
    internal static PropSpec Int => new(PropKind.Int);
    internal static PropSpec Float => new(PropKind.Float);
    internal static PropSpec Bool => new(PropKind.Bool);
    /// <summary>A bare flag such as <c>bold</c>.</summary>
    internal static PropSpec Bare => new(PropKind.Bool, Bare: true);
    internal static PropSpec Color => new(PropKind.Color);
    internal static PropSpec Bind => new(PropKind.Binding);
    internal static PropSpec ItemBind => new(PropKind.ItemBinding);
    internal static PropSpec IntRange => new(PropKind.Range);
    internal static PropSpec ListOfValues => new(PropKind.List);
    internal static PropSpec Ident => new(PropKind.Ident);
    internal static PropSpec Width => new(PropKind.Width);
    internal static PropSpec AnyValue => new(PropKind.Any);
    internal static PropSpec OneOf(params string[] values) => new(PropKind.Ident, values);
}
