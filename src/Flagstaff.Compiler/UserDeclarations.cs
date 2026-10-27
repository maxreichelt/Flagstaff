namespace Flagstaff.Compiler;

/// <summary>What a hand-written member is, syntactically.</summary>
public enum UserMemberKind { Field, Property, Method, Constructor }

/// <summary>One parameter of a hand-written method or constructor, as written (<c>Order item</c>).</summary>
public sealed record UserParameter(string Type, string Name);

/// <summary>
/// One member of a hand-written partial class (<c>ViewModels/*.cs</c>, a page partial, an item type partial), as the
/// CLI's syntax scan of the project's C# files found it. The compiler stays free of Roslyn: this is the whole model.
/// </summary>
/// <param name="Location">Where the member's name is (for diagnostics; the file as the CLI displays it).</param>
/// <param name="File">The file relative to the project directory (<c>ViewModels/DashboardViewModel.cs</c>), used in
/// messages and generated headers so the output does not depend on the working directory.</param>
/// <param name="Type">Field/property type or method return type, as written (<c>ObservableCollection&lt;Order&gt;</c>).</param>
/// <param name="Accessibility">The accessibility modifiers as written (<c>public</c>, <c>protected internal</c>), or
/// <c>""</c> when none are written.</param>
/// <param name="HasBody">A method with a block or expression body (an implementing declaration).</param>
/// <param name="Attributes">Attribute names without namespace and <c>Attribute</c> suffix (<c>ObservableProperty</c>).</param>
/// <param name="CallsInitializeComponent">A constructor whose body calls <c>InitializeComponent()</c>.</param>
public sealed record UserMember(
    UserMemberKind Kind,
    string Name,
    SourceLocation Location,
    string File,
    string? Type = null,
    string Accessibility = "",
    bool IsPartial = false,
    bool HasBody = false,
    IReadOnlyList<string>? Attributes = null,
    IReadOnlyList<UserParameter>? Parameters = null,
    bool CallsInitializeComponent = false)
{
    public IReadOnlyList<UserParameter> ParameterList => Parameters ?? [];

    public bool HasAttribute(string name) => Attributes?.Contains(name, StringComparer.Ordinal) ?? false;

    public bool IsObservableProperty => HasAttribute("ObservableProperty");

    public bool IsRelayCommand => HasAttribute("RelayCommand");

    /// <summary>
    /// The ViewModel property this member provides: a property's own name, or the CommunityToolkit name of an
    /// <c>[ObservableProperty]</c> field (<c>_openCount</c>/<c>openCount</c>/<c>m_openCount</c> → <c>OpenCount</c>).
    /// </summary>
    public string? ProvidedProperty => Kind switch
    {
        UserMemberKind.Property => Name,
        UserMemberKind.Field when IsObservableProperty => ObservablePropertyName(Name),
        _ => null,
    };

    /// <summary>
    /// The command this member provides: the CommunityToolkit name of a <c>[RelayCommand]</c> method (<c>SaveAsync</c>,
    /// <c>Save</c>, <c>OnSave</c> → <c>SaveCommand</c>), or a property named <c>...Command</c>.
    /// </summary>
    public string? ProvidedCommand => Kind switch
    {
        UserMemberKind.Method when IsRelayCommand => RelayCommandName(Name),
        UserMemberKind.Property when Name.EndsWith("Command", StringComparison.Ordinal) && Name.Length > "Command".Length => Name,
        _ => null,
    };

    /// <summary>The declaration as a short phrase for messages: <c>[ObservableProperty] int _total</c>, <c>[RelayCommand] SaveAsync()</c>.</summary>
    public string Describe() => Kind switch
    {
        UserMemberKind.Field => (IsObservableProperty ? "[ObservableProperty] " : "") + $"{Type} {Name}",
        UserMemberKind.Property => $"{Type} {Name}",
        UserMemberKind.Method => (IsRelayCommand ? "[RelayCommand] " : "") + $"{Name}({string.Join(", ", ParameterList.Select(p => p.Type))})",
        _ => $"{Name}({string.Join(", ", ParameterList.Select(p => p.Type))})",
    };

    /// <summary>CommunityToolkit's property name for an <c>[ObservableProperty]</c> field.</summary>
    public static string ObservablePropertyName(string field)
    {
        var name = field.StartsWith("m_", StringComparison.Ordinal) ? field[2..] : field.TrimStart('_');
        return name.Length == 0 ? field : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>CommunityToolkit's command name for a <c>[RelayCommand]</c> method (strips <c>On</c> and <c>Async</c>).</summary>
    public static string RelayCommandName(string method)
    {
        var name = method;
        if (name.Length > 2 && name.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(name[2]))
            name = name[2..];
        if (name.Length > "Async".Length && name.EndsWith("Async", StringComparison.Ordinal))
            name = name[..^"Async".Length];
        return name + "Command";
    }
}

/// <summary>
/// The members the project's hand-written C# files declare in partial classes that share a name with a generated type,
/// keyed by full type name (<c>Contoso.ViewModels.DashboardViewModel</c>). Filled by the CLI (Roslyn syntax scan of the
/// project's <c>.cs</c> files outside <c>Generated/</c>), consumed by <see cref="FlagstaffCompiler"/>: a generated member
/// the hand-written half already declares is skipped (warning FS2016), and an implemented <c>partial On{X}Async</c>
/// nothing declares gets a declaration (warning FS2017).
/// </summary>
public sealed class UserDeclarations
{
    private readonly Dictionary<string, List<UserMember>> _types = new(StringComparer.Ordinal);

    /// <summary>No hand-written declarations (the default: generation is unchanged).</summary>
    public static UserDeclarations Empty { get; } = new();

    public bool IsEmpty => _types.Count == 0;

    public IEnumerable<string> TypeNames => _types.Keys;

    /// <summary>Records a member of the partial class <paramref name="fullTypeName"/>.</summary>
    public UserDeclarations Add(string fullTypeName, UserMember member)
    {
        if (ReferenceEquals(this, Empty))
            throw new InvalidOperationException("UserDeclarations.Empty is shared; create a new instance.");
        if (!_types.TryGetValue(fullTypeName, out var members))
            _types[fullTypeName] = members = [];
        members.Add(member);
        return this;
    }

    /// <summary>Records that the partial class exists (even without members).</summary>
    public UserDeclarations AddType(string fullTypeName)
    {
        if (ReferenceEquals(this, Empty))
            throw new InvalidOperationException("UserDeclarations.Empty is shared; create a new instance.");
        if (!_types.ContainsKey(fullTypeName))
            _types[fullTypeName] = [];
        return this;
    }

    public IReadOnlyList<UserMember> Members(string fullTypeName) =>
        _types.TryGetValue(fullTypeName, out var members) ? members : [];

    /// <summary>
    /// Compares a hand-written type with an inferred one, ignoring namespaces the generator writes out
    /// (<c>System.Collections.ObjectModel.</c>, <c>global::</c>), whitespace, and keyword aliases (<c>String</c>).
    /// </summary>
    public static bool SameType(string? written, string? inferred)
    {
        if (written is null || inferred is null)
            return true;
        return NormalizeType(written) == NormalizeType(inferred);
    }

    public static string NormalizeType(string type)
    {
        var t = StripReferenceNullable(string.Concat(type.Where(c => !char.IsWhiteSpace(c))).Replace("global::", "", StringComparison.Ordinal));
        foreach (var ns in new[] { "System.Collections.ObjectModel.", "System.Collections.Generic.", "System." })
            t = t.Replace(ns, "", StringComparison.Ordinal);
        foreach (var (clr, keyword) in Aliases)
            t = System.Text.RegularExpressions.Regex.Replace(t, $@"\b{clr}\b", keyword);
        return t;
    }

    /// <summary><c>string?</c> and <c>ObservableCollection&lt;T&gt;?</c> are the same type as without <c>?</c> (value types keep it).</summary>
    private static string StripReferenceNullable(string t) =>
        t.EndsWith('?') && (t[..^1] is "string" or "String" or "object" or "Object" || t.Contains('<')) ? t[..^1] : t;

    private static readonly (string Clr, string Keyword)[] Aliases =
    [
        ("String", "string"), ("Int32", "int"), ("Int64", "long"), ("Double", "double"), ("Decimal", "decimal"),
        ("Boolean", "bool"), ("Object", "object"), ("Single", "float"),
    ];
}
