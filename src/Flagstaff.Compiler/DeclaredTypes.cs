namespace Flagstaff.Compiler;

/// <summary>Why a hand-written type cannot work with the <c>.flag</c> usage (FS2018).</summary>
/// <param name="Need">What the usage needs, as a phrase (<c>a bool</c>, <c>a number (format: currency)</c>).</param>
/// <param name="Suggest">The type to declare instead.</param>
/// <param name="AlternativeFix">Another fix in the <c>.flag</c> file (<c>remove format: currency</c>), or null.</param>
public sealed record TypeConflict(string Need, string Suggest, string? AlternativeFix = null);

/// <summary>
/// The hand-written type is authoritative. When a hand-written partial declares a member the <c>.flag</c> also binds
/// (FS2016 skip) with another type, everything the generator emits about that member uses the hand-written type:
/// condition function parameters (<c>When_n</c>), the badge control choice, the contract (header comments, <c>check</c>
/// JSON, <c>stubs</c>, build hints), and item-row conditions. <see cref="Check"/> decides whether that type can work
/// with how the <c>.flag</c> uses the member; if not, the compiler reports FS2018 (an error naming both types and the
/// fix) instead of letting the XAML or C# compiler fail on generated code.
/// <para>
/// The rules follow what <c>x:Bind</c> accepts (verified against the XAML compiler): a numeric property accepts any
/// type with an implicit C# conversion to it (<c>int</c> → <c>double</c>, not <c>decimal</c> → <c>double</c>,
/// WMC1121); <c>bool</c> and <c>DateTimeOffset?</c> properties accept only those types (<c>bool?</c> only where the
/// property is <c>bool?</c>: checkbox, radio); text and <c>object</c> properties (<c>Text</c>, <c>SelectedItem</c>)
/// accept anything. A <c>format:</c> goes through a converter: any number for currency/percent/number, a
/// <c>DateTimeOffset</c>/<c>DateTime</c>/<c>DateOnly</c> for date. Collections must hold the <c>of T</c> type.
/// Conditions need a bool, a number for numeric literals, a string or a non-built-in type (an enum, compared by its
/// name) for string literals, and a nullable type for <c>null</c>. Enums, structs and other names the generator does
/// not know are passed through verbatim.
/// </para>
/// </summary>
public static class DeclaredTypes
{
    private const string ObservableCollection = "ObservableCollection<";

    /// <summary>
    /// Returns <paramref name="members"/> with the hand-written type of every property a member of
    /// <paramref name="declarations"/> provides with another type (<see cref="InferredMember.CSharpType"/> = theirs,
    /// <see cref="InferredMember.InferredType"/> = the .flag's).
    /// </summary>
    public static List<InferredMember> Apply(IEnumerable<InferredMember> members, IReadOnlyList<UserMember> declarations)
    {
        var result = new List<InferredMember>();
        foreach (var m in members)
        {
            if (m.Kind == InferredKind.Property
                && declarations.FirstOrDefault(u => u.ProvidedProperty == m.Name) is { Type: { Length: > 0 } written }
                && !UserDeclarations.SameType(written, m.CSharpType))
            {
                result.Add(m with { CSharpType = Written(written), InferredType = m.CSharpType, DefaultValue = null });
            }
            else
            {
                result.Add(m);
            }
        }
        return result;
    }

    /// <summary>The hand-written type as the generator emits it (namespaces and aliases as written, without <c>global::</c>).</summary>
    public static string Written(string type) =>
        System.Text.RegularExpressions.Regex.Replace(type, @"\s+", " ").Replace("global::", "", StringComparison.Ordinal).Trim();

    /// <summary>Checks a hand-written type against the winning usage of a ViewModel member.</summary>
    public static TypeConflict? Check(string declared, InferredMember m) =>
        Check(declared, m.InferredType ?? m.CSharpType, m.Confidence, m.Target, m.Format);

    /// <summary>Checks a hand-written type against the winning usage of an item property.</summary>
    public static TypeConflict? Check(string declared, ItemTypeProperty p) =>
        Check(declared, p.InferredType ?? p.CSharpType, p.Confidence, p.Target, p.Format);

    /// <summary>
    /// Null when <paramref name="declared"/> works with a usage that implies <paramref name="inferred"/> (at
    /// <paramref name="confidence"/>, binding a property of type <paramref name="target"/>, through
    /// <paramref name="format"/>'s converter); otherwise what the usage needs.
    /// </summary>
    public static TypeConflict? Check(string declared, string inferred, InferenceConfidence confidence, string? target, FormatKind? format)
    {
        var d = UserDeclarations.NormalizeType(declared);
        var i = UserDeclarations.NormalizeType(inferred);
        if (d == i)
            return null;
        var core = Core(d);
        var nullable = d.EndsWith('?');

        switch (confidence)
        {
            case InferenceConfidence.Default:
                return null;

            case InferenceConfidence.Comparison:
                // The literal's type as TypeInference.LiteralType wrote it (NormalizeType would drop the ? of string?).
                return inferred.Trim() switch
                {
                    "bool" => core == "bool" ? null : new TypeConflict("a bool (it is a condition)", "bool"),
                    "int" or "double" => IsNumber(core) ? null
                        : new TypeConflict("a number (it is compared with a number)", i, "compare it with a string (== \"Name\") if it is an enum"),
                    "string" => core == "string" || !TypeInference.IsBuiltIn(core) ? null
                        : new TypeConflict("a string or an enum (it is compared with a string)", "string", "compare it with a value of its own type"),
                    "string?" => nullable || !IsValueType(core) ? null
                        : new TypeConflict($"a type that can be null (it is compared with null; {d} never is)", d + "?", "remove the == null / != null test"),
                    _ => null,
                };
        }

        if (format is { } kind)
        {
            return kind == FormatKind.Date
                ? core is "DateTimeOffset" or "DateTime" or "DateOnly" ? null
                    : new TypeConflict("a DateTimeOffset or DateTime (format: date)", "DateTimeOffset?", "remove format: date from that line")
                : IsNumber(core) ? null
                    : new TypeConflict($"a number (format: {kind.ToString().ToLowerInvariant()})", i, $"remove format: {kind.ToString().ToLowerInvariant()} from that line");
        }

        if (i.StartsWith(ObservableCollection, StringComparison.Ordinal))
        {
            var wanted = ElementType(i)!;
            var element = ElementType(d);
            if (element is null)
                return new TypeConflict($"a collection of {wanted}", i);
            if (wanted is "object" or "string" || Core(UserDeclarations.NormalizeType(element)) == Core(wanted))
                return null;
            return new TypeConflict($"a collection of {wanted} (of {wanted})", $"ObservableCollection<{wanted}>",
                $"write of {element} in the .flag file");
        }

        if (target is null)
            return null;
        var t = UserDeclarations.NormalizeType(target);
        var targetCore = Core(t);
        var targetNullable = t.EndsWith('?');
        switch (targetCore)
        {
            case "object" or "string":
                return null;
            case "bool":
                return core == "bool" && (targetNullable || !nullable) ? null
                    : new TypeConflict(targetNullable ? "a bool or bool?" : "a bool (not bool?)", "bool");
            case "DateTimeOffset":
                return core == "DateTimeOffset" && (targetNullable || !nullable) ? null
                    : new TypeConflict(targetNullable ? "a DateTimeOffset? (DateTime does not convert)" : "a DateTimeOffset", t);
        }
        if (IsNumber(targetCore))
        {
            return Converts(core, targetCore) && (targetNullable || !nullable) ? null
                : new TypeConflict($"a number that converts to {t} ({string.Join(", ", ImplicitSources(targetCore))})", t);
        }
        // Another built-in (TimeSpan, Guid): exactly that type. Enums, structs and unknown names pass through.
        return TypeInference.IsBuiltIn(targetCore) && TypeInference.IsBuiltIn(core) && core != targetCore
            ? new TypeConflict($"a {t}", t)
            : null;
    }

    private static string Core(string type) => type.TrimEnd('?');

    private static readonly string[] Numbers =
        ["sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal"];

    private static bool IsNumber(string core) => Numbers.Contains(core);

    private static bool IsValueType(string core) =>
        IsNumber(core) || core is "bool" or "char" or "DateTime" or "DateTimeOffset" or "DateOnly" or "TimeOnly" or "TimeSpan" or "Guid";

    /// <summary>The C# implicit numeric conversions into <paramref name="target"/> (plus the identity).</summary>
    private static IEnumerable<string> ImplicitSources(string target) => target switch
    {
        "double" => ["sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double"],
        "float" => ["sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "float"],
        "decimal" => ["sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "decimal"],
        "long" => ["sbyte", "byte", "short", "ushort", "int", "uint", "long"],
        "int" => ["sbyte", "byte", "short", "ushort", "int"],
        _ => [target],
    };

    private static bool Converts(string source, string target) => ImplicitSources(target).Contains(source);

    /// <summary><c>Order</c> for <c>ObservableCollection&lt;Order&gt;</c>, <c>List&lt;Order&gt;</c>, <c>Order[]</c>; null for a scalar.</summary>
    internal static string? ElementType(string type)
    {
        var t = type.TrimEnd('?');
        if (t.EndsWith("[]", StringComparison.Ordinal))
            return t[..^2];
        var open = t.IndexOf('<');
        if (open <= 0 || !t.EndsWith('>'))
            return null;
        var args = t[(open + 1)..^1];
        // One type argument (a nested generic argument is fine): IEnumerable<T>, List<T>, ObservableCollection<T>.
        var depth = 0;
        foreach (var c in args)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0) return null;
        }
        return args;
    }
}
