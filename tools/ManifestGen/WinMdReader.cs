using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Flagstaff.ManifestGen;

// ── Model written to the manifest ────────────────────────────────────────────

internal sealed record PropertyInfoModel(
    string Name, string TypeName, bool IsDependencyProperty, bool IsCollection, bool IsReadOnly,
    IReadOnlyList<string>? EnumMembers);

internal sealed record AttachedModel(string Name, string TypeName, IReadOnlyList<string>? EnumMembers);

internal sealed record EventModel(string Name, string ArgsTypeName);

internal sealed record TypeModel(
    string FullName, string Name, string Namespace, string? BaseType, bool IsAbstract, bool IsControl,
    bool IsUIElement, string? ContentProperty, IReadOnlyList<PropertyInfoModel> Properties,
    IReadOnlyList<AttachedModel> AttachedProperties, IReadOnlyList<EventModel> Events);

/// <summary>A type that owns attached properties but is not a DependencyObject (AutomationProperties, ToolTipService).</summary>
internal sealed record AttachedOwnerModel(string FullName, string Name, string Namespace, IReadOnlyList<AttachedModel> AttachedProperties);

internal sealed record ValueTypeModel(
    string FullName, string Name, string Namespace, bool IsEnum, bool IsValueType,
    IReadOnlyList<string>? EnumMembers, IReadOnlyList<PropertyInfoModel>? Fields);

internal sealed record ManifestModel(
    IReadOnlyList<string> Sources, IReadOnlyList<TypeModel> Types, IReadOnlyList<AttachedOwnerModel> AttachedOwners,
    IReadOnlyList<ValueTypeModel> ValueTypes);

// ── Decoded type signatures ──────────────────────────────────────────────────

/// <summary>A decoded metadata type: full name (generic definitions keep the backtick arity) plus type arguments.</summary>
internal sealed record TypeSig(string FullName, ImmutableArray<TypeSig> Args, bool IsArray = false)
{
    public static TypeSig Simple(string name) => new(name, ImmutableArray<TypeSig>.Empty);
    public override string ToString() =>
        Args.IsDefaultOrEmpty ? FullName : $"{FullName}<{string.Join(",", Args)}>";
}

internal sealed class SigProvider :
    ISignatureTypeProvider<TypeSig, object?>, ICustomAttributeTypeProvider<TypeSig>
{
    public static readonly SigProvider Instance = new();

    public TypeSig GetPrimitiveType(PrimitiveTypeCode typeCode) => TypeSig.Simple("System." + typeCode switch
    {
        PrimitiveTypeCode.Boolean => "Boolean",
        PrimitiveTypeCode.Byte => "Byte",
        PrimitiveTypeCode.SByte => "SByte",
        PrimitiveTypeCode.Char => "Char",
        PrimitiveTypeCode.Int16 => "Int16",
        PrimitiveTypeCode.UInt16 => "UInt16",
        PrimitiveTypeCode.Int32 => "Int32",
        PrimitiveTypeCode.UInt32 => "UInt32",
        PrimitiveTypeCode.Int64 => "Int64",
        PrimitiveTypeCode.UInt64 => "UInt64",
        PrimitiveTypeCode.Single => "Single",
        PrimitiveTypeCode.Double => "Double",
        PrimitiveTypeCode.String => "String",
        PrimitiveTypeCode.Object => "Object",
        PrimitiveTypeCode.Void => "Void",
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString(),
    });

    public TypeSig GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        TypeSig.Simple(WinMdReader.FullNameOf(reader, handle));

    public TypeSig GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        TypeSig.Simple(WinMdReader.FullNameOf(reader, handle));

    public TypeSig GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public TypeSig GetGenericInstantiation(TypeSig genericType, ImmutableArray<TypeSig> typeArguments) =>
        new(genericType.FullName, typeArguments);

    public TypeSig GetSZArrayType(TypeSig elementType) => elementType with { IsArray = true };
    public TypeSig GetArrayType(TypeSig elementType, ArrayShape shape) => elementType with { IsArray = true };
    public TypeSig GetByReferenceType(TypeSig elementType) => elementType;
    public TypeSig GetPointerType(TypeSig elementType) => elementType;
    public TypeSig GetPinnedType(TypeSig elementType) => elementType;
    public TypeSig GetModifiedType(TypeSig modifier, TypeSig unmodifiedType, bool isRequired) => unmodifiedType;
    public TypeSig GetFunctionPointerType(MethodSignature<TypeSig> signature) => TypeSig.Simple("System.IntPtr");
    public TypeSig GetGenericMethodParameter(object? genericContext, int index) => TypeSig.Simple("!!" + index);
    public TypeSig GetGenericTypeParameter(object? genericContext, int index) => TypeSig.Simple("!" + index);

    // Custom attributes
    public TypeSig GetSystemType() => TypeSig.Simple("System.Type");
    public TypeSig GetTypeFromSerializedName(string name) => TypeSig.Simple(name);
    public PrimitiveTypeCode GetUnderlyingEnumType(TypeSig type) => PrimitiveTypeCode.Int32;
    public bool IsSystemType(TypeSig type) => type.FullName == "System.Type";
}

// ── Reader ───────────────────────────────────────────────────────────────────

internal sealed class WinMdReader : IDisposable
{
    private const string DependencyObject = "Microsoft.UI.Xaml.DependencyObject";
    private const string UIElement = "Microsoft.UI.Xaml.UIElement";
    private const string Control = "Microsoft.UI.Xaml.Controls.Control";
    private const string DependencyPropertyType = "Microsoft.UI.Xaml.DependencyProperty";

    private static readonly HashSet<string> CollectionInterfaces = new(StringComparer.Ordinal)
    {
        "Windows.Foundation.Collections.IVector`1",
        "Windows.Foundation.Collections.IObservableVector`1",
        "Microsoft.UI.Xaml.Interop.IBindableVector",
        "Microsoft.UI.Xaml.Interop.IBindableObservableVector",
        "System.Collections.Generic.IList`1",
    };

    private sealed record Def(MetadataReader Reader, TypeDefinitionHandle Handle, string FullName)
    {
        public TypeDefinition Type => Reader.GetTypeDefinition(Handle);
    }

    private readonly List<PEReader> _peReaders = [];
    private readonly Dictionary<string, Def> _defs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _derivesDo = new(StringComparer.Ordinal);

    public WinMdReader(IEnumerable<string> winmdPaths)
    {
        foreach (var path in winmdPaths)
        {
            var pe = new PEReader(File.OpenRead(path));
            _peReaders.Add(pe);
            var reader = pe.GetMetadataReader();
            foreach (var h in reader.TypeDefinitions)
            {
                var name = FullNameOf(reader, h);
                if (name == "<Module>") continue;
                _defs.TryAdd(name, new Def(reader, h, name));
            }
        }
    }

    public void Dispose()
    {
        foreach (var pe in _peReaders) pe.Dispose();
    }

    public ManifestModel Read(IReadOnlyList<string> sources)
    {
        var types = new List<TypeModel>();
        var owners = new List<AttachedOwnerModel>();
        var referencedValueTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var def in _defs.Values.OrderBy(d => d.FullName, StringComparer.Ordinal))
        {
            var td = def.Type;
            if (!IsPublic(td) || IsInterface(td)) continue;
            var ns = def.Reader.GetString(td.Namespace);
            if (!ns.StartsWith("Microsoft.UI.Xaml", StringComparison.Ordinal)) continue;
            if (IsEnumOrStruct(def) || IsDelegate(def)) continue;

            var attached = ReadAttached(def, referencedValueTypes);
            if (!DerivesFrom(def.FullName, DependencyObject))
            {
                if (attached.Count > 0)
                    owners.Add(new AttachedOwnerModel(def.FullName, ShortName(def.FullName), ns, attached));
                continue;
            }

            types.Add(new TypeModel(
                def.FullName,
                ShortName(def.FullName),
                ns,
                BaseOf(def)?.ToString() is { } b && b != "System.Object" ? b : null,
                IsAbstract(def),
                DerivesFrom(def.FullName, Control),
                DerivesFrom(def.FullName, UIElement),
                ContentPropertyOf(def),
                ReadProperties(def, referencedValueTypes),
                attached,
                ReadEvents(def)));
        }

        // Value types: every public enum/struct in Microsoft.UI.* plus any referenced from a property.
        var valueTypes = new List<ValueTypeModel>();
        foreach (var def in _defs.Values.OrderBy(d => d.FullName, StringComparer.Ordinal))
        {
            var td = def.Type;
            if (!IsPublic(td) || !IsEnumOrStruct(def)) continue;
            var ns = def.Reader.GetString(td.Namespace);
            if (!ns.StartsWith("Microsoft.UI", StringComparison.Ordinal) && !referencedValueTypes.Contains(def.FullName))
                continue;
            var isEnum = IsEnum(def);
            valueTypes.Add(new ValueTypeModel(
                def.FullName, ShortName(def.FullName), ns, isEnum, true,
                isEnum ? EnumMembersOf(def) : null,
                isEnum ? null : StructFields(def)));
        }

        return new ManifestModel(sources, types, owners, valueTypes);
    }

    // ── Properties ──────────────────────────────────────────────────────────

    private List<PropertyInfoModel> ReadProperties(Def def, HashSet<string> referenced)
    {
        var reader = def.Reader;
        var td = def.Type;
        var staticNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ph in td.GetProperties())
        {
            var p = reader.GetPropertyDefinition(ph);
            var acc = p.GetAccessors();
            if (acc.Getter.IsNil) continue;
            var getter = reader.GetMethodDefinition(acc.Getter);
            if ((getter.Attributes & MethodAttributes.Static) == 0) continue;
            var sig = p.DecodeSignature(SigProvider.Instance, null);
            if (sig.ReturnType.FullName == DependencyPropertyType)
                staticNames.Add(reader.GetString(p.Name));
        }

        var result = new List<PropertyInfoModel>();
        foreach (var ph in td.GetProperties())
        {
            var p = reader.GetPropertyDefinition(ph);
            var acc = p.GetAccessors();
            if (acc.Getter.IsNil) continue;
            var getter = reader.GetMethodDefinition(acc.Getter);
            if ((getter.Attributes & MethodAttributes.Static) != 0) continue;
            if ((getter.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;

            var name = reader.GetString(p.Name);
            var type = p.DecodeSignature(SigProvider.Instance, null).ReturnType;
            var hasSetter = !acc.Setter.IsNil &&
                (reader.GetMethodDefinition(acc.Setter).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
            var isDp = staticNames.Contains(name + "Property");
            var isCollection = IsCollection(type);
            if (!isDp && !hasSetter && !isCollection) continue;

            Note(type, referenced);
            result.Add(new PropertyInfoModel(
                name, Normalize(type), isDp, isCollection, !hasSetter && !isCollection, EnumMembersFor(type)));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    private List<AttachedModel> ReadAttached(Def def, HashSet<string> referenced)
    {
        var reader = def.Reader;
        var td = def.Type;
        var getters = new Dictionary<string, TypeSig>(StringComparer.Ordinal);
        var setters = new HashSet<string>(StringComparer.Ordinal);
        var dpNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mh in td.GetMethods())
        {
            var m = reader.GetMethodDefinition(mh);
            if ((m.Attributes & MethodAttributes.Static) == 0) continue;
            if ((m.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
            var name = reader.GetString(m.Name);
            var sig = m.DecodeSignature(SigProvider.Instance, null);
            if (name.StartsWith("get_", StringComparison.Ordinal) && name.EndsWith("Property", StringComparison.Ordinal)
                && sig.ReturnType.FullName == DependencyPropertyType && sig.ParameterTypes.Length == 0)
                dpNames.Add(name[4..^"Property".Length]);
            else if (name.StartsWith("Get", StringComparison.Ordinal) && name.Length > 3 && sig.ParameterTypes.Length == 1)
                getters[name[3..]] = sig.ReturnType;
            else if (name.StartsWith("Set", StringComparison.Ordinal) && name.Length > 3 && sig.ParameterTypes.Length == 2)
                setters.Add(name[3..]);
        }

        var result = new List<AttachedModel>();
        foreach (var (name, type) in getters)
        {
            if (!dpNames.Contains(name)) continue;
            // Read-only attached collections (VisualStateManager.VisualStateGroups) have only a getter.
            if (!setters.Contains(name) && !IsCollection(type)) continue;
            Note(type, referenced);
            result.Add(new AttachedModel(name, Normalize(type), EnumMembersFor(type)));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    private List<EventModel> ReadEvents(Def def)
    {
        var reader = def.Reader;
        var result = new List<EventModel>();
        foreach (var eh in def.Type.GetEvents())
        {
            var e = reader.GetEventDefinition(eh);
            var adder = e.GetAccessors().Adder;
            if (adder.IsNil) continue;
            var add = reader.GetMethodDefinition(adder);
            if ((add.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
            if ((add.Attributes & MethodAttributes.Static) != 0) continue;
            var handler = TypeOfHandle(reader, e.Type);
            result.Add(new EventModel(reader.GetString(e.Name), Normalize(ArgsOf(handler))));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    private TypeSig ArgsOf(TypeSig handler)
    {
        switch (handler.FullName)
        {
            case "Windows.Foundation.TypedEventHandler`2" when handler.Args.Length == 2:
                return handler.Args[1];
            case "Windows.Foundation.EventHandler`1" when handler.Args.Length == 1:
                return handler.Args[0];
        }
        if (_defs.TryGetValue(handler.FullName, out var d) && IsDelegate(d))
        {
            foreach (var mh in d.Type.GetMethods())
            {
                var m = d.Reader.GetMethodDefinition(mh);
                if (d.Reader.GetString(m.Name) != "Invoke") continue;
                var sig = m.DecodeSignature(SigProvider.Instance, null);
                if (sig.ParameterTypes.Length >= 2) return sig.ParameterTypes[1];
                if (sig.ParameterTypes.Length == 1) return sig.ParameterTypes[0];
            }
        }
        return TypeSig.Simple("System.Object");
    }

    // ── Content property ────────────────────────────────────────────────────

    private static string? ContentPropertyOf(Def def)
    {
        var reader = def.Reader;
        foreach (var ah in def.Type.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(ah);
            // Controls that originate in WinUI 2 (TabView, Expander, ...) carry the
            // Windows.UI.Xaml.Markup attribute instead; both mean the same thing.
            if (AttributeTypeName(reader, attr) is not ("Microsoft.UI.Xaml.Markup.ContentPropertyAttribute"
                or "Windows.UI.Xaml.Markup.ContentPropertyAttribute")) continue;
            var value = attr.DecodeValue(SigProvider.Instance);
            foreach (var named in value.NamedArguments)
                if (named.Name == "Name" && named.Value is string s) return s;
            foreach (var fixedArg in value.FixedArguments)
                if (fixedArg.Value is string s) return s;
        }
        return null;
    }

    private static string? AttributeTypeName(MetadataReader reader, CustomAttribute attr)
    {
        switch (attr.Constructor.Kind)
        {
            case HandleKind.MethodDefinition:
                var md = reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                return FullNameOf(reader, md.GetDeclaringType());
            case HandleKind.MemberReference:
                var mr = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                return mr.Parent.Kind switch
                {
                    HandleKind.TypeReference => FullNameOf(reader, (TypeReferenceHandle)mr.Parent),
                    HandleKind.TypeDefinition => FullNameOf(reader, (TypeDefinitionHandle)mr.Parent),
                    _ => null,
                };
            default:
                return null;
        }
    }

    // ── Type helpers ────────────────────────────────────────────────────────

    private static TypeSig TypeOfHandle(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => TypeSig.Simple(FullNameOf(reader, (TypeDefinitionHandle)handle)),
        HandleKind.TypeReference => TypeSig.Simple(FullNameOf(reader, (TypeReferenceHandle)handle)),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(SigProvider.Instance, null),
        _ => TypeSig.Simple("System.Object"),
    };

    private static TypeSig? BaseOf(Def def)
    {
        var bt = def.Type.BaseType;
        return bt.IsNil ? null : TypeOfHandle(def.Reader, bt);
    }

    private bool DerivesFrom(string fullName, string baseName)
    {
        var current = fullName;
        for (var guard = 0; guard < 64; guard++)
        {
            if (current == baseName) return true;
            if (!_defs.TryGetValue(current, out var def)) return false;
            var b = BaseOf(def);
            if (b is null) return false;
            current = b.FullName;
        }
        return false;
    }

    private bool IsCollection(TypeSig type)
    {
        if (type.IsArray) return false;
        if (CollectionInterfaces.Contains(type.FullName)) return true;
        return ImplementsCollection(type.FullName, 0);
    }

    private bool ImplementsCollection(string fullName, int depth)
    {
        if (depth > 32 || !_defs.TryGetValue(fullName, out var def)) return false;
        foreach (var ih in def.Type.GetInterfaceImplementations())
        {
            var iface = TypeOfHandle(def.Reader, def.Reader.GetInterfaceImplementation(ih).Interface);
            if (CollectionInterfaces.Contains(iface.FullName)) return true;
        }
        var b = BaseOf(def);
        return b is not null && ImplementsCollection(b.FullName, depth + 1);
    }

    private IReadOnlyList<string>? EnumMembersFor(TypeSig type)
    {
        type = Unwrap(type);
        return _defs.TryGetValue(type.FullName, out var d) && IsEnum(d) ? EnumMembersOf(d) : null;
    }

    private static TypeSig Unwrap(TypeSig type) =>
        type.FullName is "Windows.Foundation.IReference`1" or "System.Nullable`1" && type.Args.Length == 1 ? type.Args[0] : type;

    private static IReadOnlyList<string> EnumMembersOf(Def def)
    {
        var list = new List<string>();
        foreach (var fh in def.Type.GetFields())
        {
            var f = def.Reader.GetFieldDefinition(fh);
            if ((f.Attributes & FieldAttributes.Static) == 0) continue; // value__
            list.Add(def.Reader.GetString(f.Name));
        }
        return list; // declaration order (matches the numeric order WinRT emits)
    }

    private IReadOnlyList<PropertyInfoModel> StructFields(Def def)
    {
        var list = new List<PropertyInfoModel>();
        foreach (var fh in def.Type.GetFields())
        {
            var f = def.Reader.GetFieldDefinition(fh);
            if ((f.Attributes & FieldAttributes.Static) != 0) continue;
            var type = f.DecodeSignature(SigProvider.Instance, null);
            list.Add(new PropertyInfoModel(def.Reader.GetString(f.Name), Normalize(type), false, false, false, EnumMembersFor(type)));
        }
        return list;
    }

    private void Note(TypeSig type, HashSet<string> referenced)
    {
        type = Unwrap(type);
        if (_defs.TryGetValue(type.FullName, out var d) && IsEnumOrStruct(d)) referenced.Add(type.FullName);
    }

    /// <summary>
    /// WinRT composable base classes (Control, Panel, FrameworkElement) are not marked abstract in metadata;
    /// what makes them unusable as a XAML element is that they have no public constructor.
    /// </summary>
    private static bool IsAbstract(Def def)
    {
        if ((def.Type.Attributes & TypeAttributes.Abstract) != 0) return true;
        foreach (var mh in def.Type.GetMethods())
        {
            var m = def.Reader.GetMethodDefinition(mh);
            if ((m.Attributes & (MethodAttributes.Static | MethodAttributes.MemberAccessMask)) == MethodAttributes.Public
                && def.Reader.GetString(m.Name) == ".ctor")
                return false;
        }
        return true;
    }

    private static bool IsPublic(TypeDefinition td) =>
        (td.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public;

    private static bool IsInterface(TypeDefinition td) => (td.Attributes & TypeAttributes.Interface) != 0;

    private bool IsEnum(Def def) => BaseOf(def)?.FullName == "System.Enum";
    private bool IsEnumOrStruct(Def def) => BaseOf(def)?.FullName is "System.Enum" or "System.ValueType";
    private bool IsDelegate(Def def) => BaseOf(def)?.FullName is "System.MulticastDelegate" or "System.Delegate";

    public static string ShortName(string fullName)
    {
        var i = fullName.LastIndexOf('.');
        return i < 0 ? fullName : fullName[(i + 1)..];
    }

    public static string FullNameOf(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        var name = reader.GetString(td.Name);
        if (td.IsNested) return FullNameOf(reader, td.GetDeclaringType()) + "+" + name;
        var ns = reader.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string FullNameOf(MetadataReader reader, TypeReferenceHandle handle)
    {
        var tr = reader.GetTypeReference(handle);
        var name = reader.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullNameOf(reader, (TypeReferenceHandle)tr.ResolutionScope) + "+" + name;
        var ns = reader.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>Normalizes a WinRT metadata type to the manifest's type-name vocabulary.</summary>
    public static string Normalize(TypeSig type)
    {
        string core = type.FullName switch
        {
            "System.Boolean" => "bool",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Char" => "char",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.String" => "string",
            "System.Object" => "object",
            "System.Guid" => "Guid",
            // The WinAppSDK .winmd already references some .NET projections (System.Nullable`1,
            // System.Uri, System.Collections.Generic.IList`1); raw WinRT names are mapped the same way.
            "Windows.Foundation.DateTime" or "System.DateTimeOffset" => "DateTimeOffset",
            "Windows.Foundation.TimeSpan" or "System.TimeSpan" => "TimeSpan",
            "Windows.Foundation.Uri" or "System.Uri" => "Uri",
            "System.Type" => "Type",
            "Windows.Foundation.IReference`1" or "System.Nullable`1" when type.Args.Length == 1 => Normalize(type.Args[0]) + "?",
            _ => type.Args.IsDefaultOrEmpty
                ? type.FullName
                : StripArity(type.FullName) + "<" + string.Join(", ", type.Args.Select(Normalize)) + ">",
        };
        return type.IsArray ? core + "[]" : core;
    }

    private static string StripArity(string name)
    {
        var i = name.IndexOf('`');
        return i < 0 ? name : name[..i];
    }
}
