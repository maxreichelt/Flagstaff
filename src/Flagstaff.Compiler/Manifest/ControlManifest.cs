using System.IO.Compression;
using System.Text.Json;

namespace Flagstaff.Compiler;

/// <summary>
/// What the compiler knows about WinUI types: <c>winui-manifest.json</c> (generated from
/// <c>Microsoft.UI.Xaml.winmd</c> by <c>tools/ManifestGen</c>) plus the hand-curated <c>overlay.json</c>
/// (two-way inputs, keyword equivalents, value shapes, attached-property locality, reference properties;
/// see <c>overlay.md</c>). Loaded once, immutable after load.
/// </summary>
public sealed class ControlManifest
{
    private const string ManifestResource = "winui-manifest.json";
    private const string OverlayResource = "overlay.json";

    private static readonly Lazy<ControlManifest> s_default = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The embedded WinUI manifest plus overlay, parsed on first use.</summary>
    public static ControlManifest Default => s_default.Value;

    private readonly Dictionary<string, ManifestType> _byFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManifestType> _byShortName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManifestAttachedProperty> _attached = new(StringComparer.Ordinal); // "Grid.Row"
    private readonly Dictionary<string, string> _ownerFullToShort = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManifestValueType> _valueTypesByFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManifestValueType> _valueTypesByShortName = new(StringComparer.Ordinal);
    private readonly Dictionary<object, ManifestType> _declaringType = new(ReferenceEqualityComparer.Instance);
    private readonly List<ManifestType> _types = [];
    private readonly Overlay _overlay;

    private ControlManifest(JsonElement manifest, JsonElement overlay)
    {
        _overlay = Overlay.Parse(overlay);
        var extraAttached = _overlay.ExtraAttached;

        foreach (var t in Array(manifest, "types"))
        {
            var name = Str(t, "name")!;
            var props = new Dictionary<string, ManifestProperty>(StringComparer.Ordinal);
            foreach (var p in Array(t, "properties"))
            {
                var prop = new ManifestProperty(
                    Str(p, "name")!, Str(p, "typeName")!, Bool(p, "isDependencyProperty"), Bool(p, "isCollection"),
                    Bool(p, "isReadOnly"), Strings(p, "enumMembers"));
                props[prop.Name] = prop;
            }
            var attached = new Dictionary<string, ManifestAttachedProperty>(StringComparer.Ordinal);
            foreach (var a in Array(t, "attachedProperties"))
                attached[Str(a, "name")!] = new ManifestAttachedProperty(name, Str(a, "name")!, Str(a, "typeName")!);
            foreach (var (key, typeName) in extraAttached)
            {
                var (owner, member) = Split(key);
                if (owner == name) attached[member] = new ManifestAttachedProperty(name, member, typeName);
            }
            var events = new Dictionary<string, ManifestEvent>(StringComparer.Ordinal);
            foreach (var e in Array(t, "events"))
                events[Str(e, "name")!] = new ManifestEvent(Str(e, "name")!, Str(e, "argsTypeName")!);

            var type = new ManifestType(
                Str(t, "fullName")!, name, Str(t, "namespace")!, Str(t, "baseType"), Bool(t, "isAbstract"),
                Bool(t, "isControl"), Bool(t, "isUIElement"), Str(t, "contentProperty"), props, attached, events);
            _types.Add(type);
            _byFullName[type.FullName] = type;
            foreach (var p in props.Values) _declaringType[p] = type;
            foreach (var a in attached.Values) _attached[$"{name}.{a.Name}"] = a;
            _ownerFullToShort[type.FullName] = name;
        }

        foreach (var o in Array(manifest, "attachedOwners"))
        {
            var name = Str(o, "name")!;
            _ownerFullToShort[Str(o, "fullName")!] = name;
            foreach (var a in Array(o, "attachedProperties"))
                _attached[$"{name}.{Str(a, "name")}"] = new ManifestAttachedProperty(name, Str(a, "name")!, Str(a, "typeName")!);
        }

        // Short names: unique in the current WinUI metadata, but prefer Controls, then Microsoft.UI.Xaml, if that changes.
        foreach (var type in _types.OrderBy(t => NamespaceRank(t.Namespace)).ThenBy(t => t.Namespace, StringComparer.Ordinal))
            _byShortName.TryAdd(type.Name, type);

        foreach (var v in Array(manifest, "valueTypes"))
        {
            var vt = new ManifestValueType(Str(v, "fullName")!, Str(v, "name")!, Str(v, "namespace")!, Bool(v, "isEnum"), Strings(v, "enumMembers"));
            _valueTypesByFullName[vt.FullName] = vt;
        }
        foreach (var vt in _valueTypesByFullName.Values.OrderBy(v => NamespaceRank(v.Namespace)).ThenBy(v => v.Namespace, StringComparer.Ordinal))
            _valueTypesByShortName.TryAdd(vt.Name, vt);
    }

    /// <summary>Parses a manifest and an overlay (tests and tools; the compiler uses <see cref="Default"/>).</summary>
    public static ControlManifest Load(string manifestJson, string overlayJson)
    {
        using var manifest = JsonDocument.Parse(manifestJson);
        using var overlay = JsonDocument.Parse(overlayJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return new ControlManifest(manifest.RootElement, overlay.RootElement);
    }

    private static ControlManifest LoadEmbedded()
    {
        var asm = typeof(ControlManifest).Assembly;
        using var manifest = OpenResource(ManifestResource + ".gz") is { } gz
            ? JsonDocument.Parse(new GZipStream(gz, CompressionMode.Decompress))
            : JsonDocument.Parse(OpenResource(ManifestResource) ?? throw Missing(ManifestResource));
        using var overlay = JsonDocument.Parse(OpenResource(OverlayResource) ?? throw Missing(OverlayResource),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return new ControlManifest(manifest.RootElement, overlay.RootElement);

        Stream? OpenResource(string name) => asm.GetManifestResourceStream(name);
        static InvalidOperationException Missing(string name) =>
            new($"Embedded resource '{name}' is missing from Flagstaff.Compiler.");
    }

    // ── Types ───────────────────────────────────────────────────────────────

    /// <summary>All types, ordered by full name.</summary>
    public IEnumerable<ManifestType> Types => _types;

    /// <summary>Looks up a type by short name (<c>Expander</c>) or full name (<c>Microsoft.UI.Xaml.Controls.Expander</c>).</summary>
    public bool TryGetType(string name, out ManifestType type) =>
        _byShortName.TryGetValue(name, out type!) || _byFullName.TryGetValue(name, out type!);

    /// <summary>Looks up an enum or struct by short or full name (<c>Symbol</c>, <c>Microsoft.UI.Xaml.Thickness</c>).</summary>
    public bool TryGetValueType(string name, out ManifestValueType valueType) =>
        _valueTypesByShortName.TryGetValue(name, out valueType!) || _valueTypesByFullName.TryGetValue(name, out valueType!);

    /// <summary>The type and its bases, most derived first.</summary>
    public IEnumerable<ManifestType> BaseChain(ManifestType type)
    {
        for (ManifestType? t = type; t is not null; t = t.BaseType is { } b && _byFullName.TryGetValue(b, out var next) ? next : null)
            yield return t;
    }

    public bool DerivesFrom(ManifestType type, string fullOrShortBaseName)
    {
        foreach (var t in BaseChain(type))
            if (t.FullName == fullOrShortBaseName || t.Name == fullOrShortBaseName) return true;
        return false;
    }

    // ── Members ─────────────────────────────────────────────────────────────

    /// <summary>Finds a property on the type or a base; <paramref name="declaringType"/> is where it is declared.</summary>
    public bool TryGetProperty(ManifestType type, string name, out ManifestProperty prop, out ManifestType declaringType)
    {
        foreach (var t in BaseChain(type))
        {
            if (t.Properties.TryGetValue(name, out prop!))
            {
                declaringType = t;
                return true;
            }
        }
        prop = null!;
        declaringType = null!;
        return false;
    }

    /// <summary>Every attached property, including those of non-DependencyObject owners (AutomationProperties).</summary>
    public IEnumerable<ManifestAttachedProperty> AttachedProperties => _attached.Values;

    /// <summary>Looks up <c>Owner.Name</c> (<c>Grid.Row</c>, <c>AutomationProperties.Name</c>); the owner may be a full name.</summary>
    public bool TryGetAttachedProperty(string dottedName, out ManifestAttachedProperty prop) =>
        _attached.TryGetValue(NormalizeDotted(dottedName), out prop!);

    public bool TryGetEvent(ManifestType type, string name, out ManifestEvent evt)
    {
        foreach (var t in BaseChain(type))
            if (t.Events.TryGetValue(name, out evt!)) return true;
        evt = null!;
        return false;
    }

    public bool HasEvent(ManifestType type, string name) => TryGetEvent(type, name, out _);

    /// <summary>True when the type (or a base) has a <c>Command</c> property, so <c>-> vm.XCommand</c> binds it.</summary>
    public bool HasCommand(ManifestType type) => TryGetProperty(type, "Command", out _, out _);

    public string? ContentPropertyOf(ManifestType type)
    {
        foreach (var t in BaseChain(type))
            if (t.ContentProperty is { } c) return c;
        return null;
    }

    // ── Overlay-driven answers ──────────────────────────────────────────────

    /// <summary>TwoWay when the overlay lists the property as input on the type or any base, else OneWay.</summary>
    public BindingMode DefaultMode(ManifestType type, ManifestProperty prop)
    {
        foreach (var t in BaseChain(type))
        {
            if ((_overlay.Input.TryGetValue(t.Name, out var names) || _overlay.Input.TryGetValue(t.FullName, out names))
                && names.Contains(prop.Name))
                return BindingMode.TwoWay;
        }
        return BindingMode.OneWay;
    }

    /// <summary>The Flagstaff keyword that covers this exact type (<c>TextBox</c> → <c>textbox</c>), or null.</summary>
    public string? KeywordFor(ManifestType type) =>
        _overlay.KeywordFor.TryGetValue(type.Name, out var k) ? k : null;

    /// <summary>The C# type a ViewModel member bound to this property gets.</summary>
    public string CSharpTypeFor(ManifestProperty prop)
    {
        if (prop.IsCollection) return "System.Collections.ObjectModel.ObservableCollection<object>";
        var nullable = prop.TypeName.EndsWith('?');
        var core = nullable ? prop.TypeName[..^1] : prop.TypeName;
        var q = nullable ? "?" : "";
        switch (core)
        {
            case "bool" or "byte" or "sbyte" or "char" or "short" or "ushort" or "int" or "uint" or "long" or "ulong"
                or "float" or "double" or "string" or "DateTimeOffset" or "TimeSpan" or "Guid":
                return core + q;
            case "Uri":
                return "string";
            case "object":
                return "object";
        }
        if (prop.EnumMembers is not null) return core + q;
        return core.Contains('<') ? "object" : core + q;
    }

    public ValueShape ShapeFor(ManifestProperty prop)
    {
        if (prop.IsCollection) return ValueShape.Collection;
        if (_declaringType.TryGetValue(prop, out var declaring) && ReferenceEmitFor($"{declaring.Name}.{prop.Name}") != ReferenceEmit.None)
            return ValueShape.Reference;
        return ShapeForTypeName(prop.TypeName, prop.EnumMembers is not null);
    }

    /// <summary>The value shape for a normalized type name (attached properties use this too).</summary>
    public ValueShape ShapeForTypeName(string typeName, bool isEnum = false)
    {
        var core = typeName.TrimEnd('?');
        switch (core)
        {
            case "bool": return ValueShape.Bool;
            case "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong": return ValueShape.Int;
            case "float" or "double": return ValueShape.Double;
            case "string" or "char": return ValueShape.String;
        }
        if (isEnum || (_valueTypesByFullName.TryGetValue(core, out var vt) && vt.IsEnum)) return ValueShape.Enum;

        var shortName = ShortName(core);
        if (_overlay.ValueShapes.TryGetValue(shortName, out var shape)) return shape;
        if (_byFullName.TryGetValue(core, out var type))
        {
            foreach (var t in BaseChain(type))
                if (_overlay.ValueShapes.TryGetValue(t.Name, out shape)) return shape;
        }
        return ValueShape.Unknown;
    }

    /// <summary>Overlay locality for an attached property (<c>Owner.*</c> wildcards apply); Unknown when absent.</summary>
    public AttachedLocality Locality(string dottedName)
    {
        var key = NormalizeDotted(dottedName);
        if (_overlay.Locality.TryGetValue(key, out var l)) return l;
        var (owner, _) = Split(key);
        return owner.Length > 0 && _overlay.Locality.TryGetValue(owner + ".*", out l) ? l : AttachedLocality.Unknown;
    }

    /// <summary>
    /// How a <c>ref(Name)</c> value is emitted for <c>Type.Property</c> (<c>Setter.Target</c> → TargetPath).
    /// When the exact key is not listed, the property is looked up on the type's bases
    /// (<c>Flyout.Target</c> resolves through <c>FlyoutBase.Target</c>).
    /// </summary>
    public ReferenceEmit ReferenceEmitFor(string dottedOrTypeDotProp)
    {
        var key = NormalizeDotted(dottedOrTypeDotProp);
        if (_overlay.References.TryGetValue(key, out var r)) return r;
        var (owner, member) = Split(key);
        if (owner.Length > 0 && TryGetType(owner, out var type))
        {
            foreach (var t in BaseChain(type))
                if (_overlay.References.TryGetValue($"{t.Name}.{member}", out r)) return r;
        }
        return ReferenceEmit.None;
    }

    // ── Suggestions ─────────────────────────────────────────────────────────

    /// <summary>Type short names within Levenshtein distance 2 (case-insensitive), closest first.</summary>
    public IReadOnlyList<string> SuggestTypes(string name, int max = 5) =>
        Suggest(name, _byShortName.Keys, max);

    /// <summary>
    /// Property names on the type or its bases: first the <see cref="PropertySynonyms"/> of <paramref name="name"/> the
    /// type has (<c>Header</c> → <c>Title</c> on <c>MenuBarItem</c>), then names within Levenshtein distance 2, closest first.
    /// </summary>
    public IReadOnlyList<string> SuggestProperties(ManifestType type, string name, int max = 8)
    {
        var all = BaseChain(type).SelectMany(t => t.Properties.Keys).Distinct(StringComparer.Ordinal).ToList();
        var synonyms = PropertySynonyms.TryGetValue(name, out var s)
            ? s.Where(all.Contains).ToList()
            : [];
        return synonyms.Concat(Suggest(name, all, max)).Distinct(StringComparer.Ordinal).Take(max).ToList();
    }

    /// <summary>
    /// Common property-name mistakes (another control's name for the same idea, an HTML/CSS or WPF habit) and what to
    /// suggest instead, in order; only names the type actually has are suggested.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> PropertySynonyms = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Header"] = ["Title", "Label", "Content", "Text"],
        ["Title"] = ["Header", "Text", "Label", "Content"],
        ["Text"] = ["Label", "Content", "Header", "Title", "PlaceholderText"],
        ["Content"] = ["Text", "Child", "Label", "Header"],
        ["Label"] = ["Content", "Header", "Text", "Title"],
        ["Caption"] = ["Header", "Title", "Label", "Content", "Text"],
        ["Items"] = ["ItemsSource", "MenuItems", "Children"],
        ["Children"] = ["Items", "Content", "Child"],
        ["Child"] = ["Content", "Children"],
        ["Placeholder"] = ["PlaceholderText"],
        ["Icon"] = ["IconSource", "Symbol", "Glyph"],
        ["Image"] = ["Source", "ImageSource"],
        ["Url"] = ["Source", "NavigateUri"],
        ["Uri"] = ["Source", "NavigateUri"],
        ["Href"] = ["NavigateUri"],
        ["Value"] = ["Text", "IsChecked", "IsOn", "SelectedItem", "Date", "Time", "Color"],
        ["Checked"] = ["IsChecked", "IsOn"],
        ["IsChecked"] = ["IsOn"],
        ["IsOn"] = ["IsChecked"],
        ["Selected"] = ["IsSelected", "SelectedItem"],
        ["Enabled"] = ["IsEnabled"],
        ["Visible"] = ["Visibility"],
        ["IsVisible"] = ["Visibility"],
        ["Min"] = ["Minimum"],
        ["Max"] = ["Maximum"],
        ["Color"] = ["Foreground", "Background", "Fill", "Stroke"],
        ["TextColor"] = ["Foreground"],
        ["FontColor"] = ["Foreground"],
        ["BackgroundColor"] = ["Background"],
        ["Bold"] = ["FontWeight"],
        ["Size"] = ["FontSize", "Width"],
    };

    private static IReadOnlyList<string> Suggest(string input, IEnumerable<string> candidates, int max)
    {
        var lower = input.ToLowerInvariant();
        return candidates
            .Where(c => c != input)
            .Select(c => (Name: c, Distance: SemanticValidator.Levenshtein(lower, c.ToLowerInvariant())))
            .Where(x => x.Distance <= 2)
            .OrderBy(x => x.Distance).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    internal static int NamespaceRank(string ns) => ns switch
    {
        "Microsoft.UI.Xaml.Controls" => 0,
        "Microsoft.UI.Xaml" => 1,
        _ => 2,
    };

    /// <summary>"Microsoft.UI.Xaml.Controls.Grid.Row" → "Grid.Row"; short forms are returned unchanged.</summary>
    private string NormalizeDotted(string dotted)
    {
        var (owner, member) = Split(dotted);
        return owner.Contains('.') && _ownerFullToShort.TryGetValue(owner, out var shortOwner) ? $"{shortOwner}.{member}" : dotted;
    }

    private static (string Owner, string Member) Split(string dotted)
    {
        var i = dotted.LastIndexOf('.');
        return i < 0 ? ("", dotted) : (dotted[..i], dotted[(i + 1)..]);
    }

    private static string ShortName(string fullName)
    {
        var lt = fullName.IndexOf('<');
        var head = lt < 0 ? fullName : fullName[..lt];
        var i = head.LastIndexOf('.');
        return i < 0 ? head : head[(i + 1)..];
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string>? Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : null;

    private sealed record Overlay(
        IReadOnlyDictionary<string, HashSet<string>> Input,
        IReadOnlyDictionary<string, string> KeywordFor,
        IReadOnlyDictionary<string, ValueShape> ValueShapes,
        IReadOnlyDictionary<string, AttachedLocality> Locality,
        IReadOnlyDictionary<string, ReferenceEmit> References,
        IReadOnlyDictionary<string, string> ExtraAttached)
    {
        public static Overlay Parse(JsonElement root)
        {
            var input = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var p in Object(root, "input"))
                input[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);

            var keywordFor = Object(root, "keywordFor").ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

            var shapes = Object(root, "valueShapes").ToDictionary(p => p.Name, p => p.Value.GetString() switch
            {
                "bool" => ValueShape.Bool,
                "int" => ValueShape.Int,
                "double" => ValueShape.Double,
                "string" => ValueShape.String,
                "enum" => ValueShape.Enum,
                "thickness" => ValueShape.Thickness,
                "cornerradius" => ValueShape.CornerRadius,
                "gridlength" => ValueShape.GridLength,
                "brush" => ValueShape.Brush,
                "color" => ValueShape.Color,
                "uri" => ValueShape.Uri,
                "icon" => ValueShape.Icon,
                "any" or "object" => ValueShape.Object,
                "collection" => ValueShape.Collection,
                "reference" => ValueShape.Reference,
                var other => throw new InvalidOperationException($"overlay.json: unknown value shape '{other}' for '{p.Name}'"),
            }, StringComparer.Ordinal);

            var locality = Object(root, "attachedLocality").ToDictionary(p => p.Name, p =>
            {
                var local = p.Value.TryGetProperty("local", out var l) && l.ValueKind == JsonValueKind.True;
                var checkedBy = p.Value.TryGetProperty("checkedBy", out var c) ? c.GetString() : null;
                return local ? AttachedLocality.Local : checkedBy switch
                {
                    "grid" => AttachedLocality.CheckedByGrid,
                    "ref" => AttachedLocality.CheckedByRef,
                    _ => AttachedLocality.Unknown,
                };
            }, StringComparer.Ordinal);

            var refs = Object(root, "referenceProperties").ToDictionary(p => p.Name, p =>
                (p.Value.TryGetProperty("emit", out var e) ? e.GetString() : null) switch
                {
                    "name" => ReferenceEmit.Name,
                    "target-path" => ReferenceEmit.TargetPath,
                    _ => ReferenceEmit.None,
                }, StringComparer.Ordinal);

            var extra = Object(root, "extraAttached").ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

            return new Overlay(input, keywordFor, shapes, locality, refs, extra);
        }

        private static IEnumerable<JsonProperty> Object(JsonElement e, string name) =>
            e.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object ? o.EnumerateObject() : [];
    }
}
