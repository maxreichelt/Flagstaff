namespace Flagstaff.Compiler;

/// <summary>How strongly a usage determines a member's C# type. Higher wins; first wins on ties.</summary>
public enum InferenceConfidence
{
    /// <summary>No type information: <c>string</c> (textbox, text, stat without format:, badge, ...).</summary>
    Default,
    /// <summary>From a condition: the literal in <c>vm.X &gt; 5</c>, or <c>bool</c> for a bare <c>when(vm.X)</c>.</summary>
    Comparison,
    /// <summary>Keyword/clause-driven: number, checkbox, toggle, radio, datepicker, format:, of T, items, selected, range select.</summary>
    Explicit,
}

/// <summary>
/// Inferred C# type for a ViewModel property or command.
/// </summary>
/// <param name="Name">Member name without the <c>vm.</c> prefix (commands keep their <c>Command</c> suffix).</param>
/// <param name="Confidence">How the type was determined (see <see cref="InferenceConfidence"/>).</param>
/// <param name="ParameterType">For commands invoked as <c>-> vm.XCommand(item)</c>: the row type.</param>
/// <param name="Location">Where the winning type came from.</param>
/// <param name="Origin">The usage that produced the winning type, as written (<c>stat "Open" vm.OpenCount</c>,
/// <c>when(vm.IsBusy)</c>, <c>button "Save" -> vm.Save</c>); used by contract comments and build hints.</param>
/// <param name="Target">The CLR type of the control property the winning usage binds directly (<c>bool?</c> for a
/// checkbox's <c>IsChecked</c>, <c>double</c> for a NumberBox's <c>Value</c>, <c>object</c> for <c>SelectedItem</c>,
/// <c>string</c> for <c>Text</c>), or null (a condition, a <c>format:</c> converter, an unvalidated element). Used to
/// check a hand-written type against the usage (<see cref="DeclaredTypes"/>, FS2018).</param>
/// <param name="Format">The <c>format:</c> of the winning usage (its value goes through a converter).</param>
/// <param name="InferredType">Set when <see cref="CSharpType"/> comes from a hand-written declaration
/// (<see cref="DeclaredTypes.Apply"/>): the type the .flag alone implies.</param>
public sealed record InferredMember(
    string Name,
    InferredKind Kind,
    string CSharpType,
    string? DefaultValue = null,
    InferenceConfidence Confidence = InferenceConfidence.Default,
    string? ParameterType = null,
    SourceLocation? Location = null,
    string? Origin = null,
    string? Target = null,
    FormatKind? Format = null,
    string? InferredType = null)
{
    /// <summary>The type comes from a hand-written declaration; <see cref="InferredType"/> is the .flag's.</summary>
    public bool IsDeclared => InferredType is not null;
}

public enum InferredKind { Property, Command }

/// <summary>
/// Walks a ViewDecl AST and collects all vm.X references, inferring their C# types from usage.
/// Reports FS2004 (explicit type vs incompatible comparison literal) and FS2007
/// (<c>(item)</c> command argument outside a typed table/list, for arrows and <c>on</c> events) and FS2015
/// (<c>on Event -> vm.X</c> where <c>vm.X</c> is a property).
/// </summary>
public static class TypeInference
{
    private const string ObservableCollection = "System.Collections.ObjectModel.ObservableCollection";

    public static List<InferredMember> InferMembers(ViewDecl view) => InferMembers(view, null);

    public static List<InferredMember> InferMembers(ViewDecl view, List<Diagnostic>? diagnostics)
    {
        var walker = new Walker(diagnostics);

        if (view.OnLoadTarget is { } onLoad)
        {
            walker.Register(OnLoadCommandStem(onLoad) + "Command", InferredKind.Command, "IRelayCommand",
                InferenceConfidence.Explicit, view.OnLoadLocation ?? view.Location, origin: $"onload -> {onLoad}");
        }

        foreach (var prop in view.PropertyElements)
            walker.WalkValue(prop.Value);
        foreach (var element in view.Body)
            walker.WalkElement(element);

        // on Click -> vm.Title where vm.Title is bound as a property elsewhere: almost certainly not the intended command.
        foreach (var (name, evt) in walker.EventTargets)
        {
            if (walker.Members.TryGetValue(name, out var member) && member.Kind == InferredKind.Property)
                diagnostics?.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS2015",
                    $"'on {evt.EventName} -> vm.{name}' targets a property (vm.{name} is {member.CSharpType}, from {member.Location?.ToShortString() ?? "this view"}), not a command",
                    evt.Location,
                    $"write -> vm.{name}Command (generated as {name}Async()), or name the command differently"));
        }

        return walker.Members.Values.ToList();
    }

    /// <summary>
    /// The command stem for an <c>onload</c> target: <c>vm.Load</c>, <c>vm.LoadAsync</c> (legacy) and
    /// <c>vm.LoadCommand</c> all map to <c>Load</c> (→ <c>LoadCommand</c> / <c>OnLoadAsync()</c>).
    /// </summary>
    public static string OnLoadCommandStem(string target)
    {
        var name = target.StartsWith("vm.", StringComparison.Ordinal) ? target[3..] : target;
        if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length)
            name = name[..^"Async".Length];
        if (name.EndsWith("Command", StringComparison.Ordinal) && name.Length > "Command".Length)
            name = name[..^"Command".Length];
        return name;
    }

    /// <summary>
    /// Normalizes an arrow command target: <c>vm.Save</c> → <c>vm.SaveCommand</c> (the generated
    /// <c>[RelayCommand] SaveAsync()</c> is exposed as <c>SaveCommand</c>); <c>vm.SaveCommand</c> is unchanged.
    /// </summary>
    public static string CommandPath(string target) =>
        target.EndsWith("Command", StringComparison.Ordinal) && target.Length > "vm.Command".Length ? target : target + "Command";

    /// <summary>
    /// The C# type of a binding under a PascalCase key (or a generic element's positional binding, which binds the
    /// overlay's input property), for ViewModel members and item-type members (<see cref="ItemTypeRegistry"/>):
    /// <list type="bullet">
    /// <item>A collection property (<see cref="IsCollectionProperty"/>: <c>ItemsSource</c>, <c>Items</c>, <c>MenuItems</c>,
    /// manifest collections) → <c>ObservableCollection&lt;T&gt;</c> with the prop's own <c>of T</c> or the element's,
    /// else <c>ObservableCollection&lt;object&gt;</c>.</item>
    /// <item><c>SelectedItem</c> (object) with an <c>of T</c> → <c>T?</c>; any other property with its own <c>of T</c> →
    /// <c>T</c> (<c>T?</c> for a non-built-in type).</item>
    /// <item>Otherwise the manifest's CLR type at Explicit confidence; <c>object</c> properties (<c>Header</c>,
    /// <c>Content</c>) and unknown properties (already reported) stay Default <c>string</c>.</item>
    /// <item>An element the manifest does not know (third-party, FS4002): the prop's own <c>of T</c> (a built-in type as
    /// is, any other type as <c>ObservableCollection&lt;T&gt;</c>), else Default <c>object?</c> (FS4003).</item>
    /// </list>
    /// </summary>
    internal static (string Type, InferenceConfidence Confidence) ManifestTypeFor(Element el, string key, string? typeArg = null)
    {
        var (type, confidence, _) = ManifestBindingFor(el, key, typeArg);
        return (type, confidence);
    }

    /// <summary>
    /// <see cref="ManifestTypeFor"/> plus the CLR type of the bound property itself (<see cref="InferredMember.Target"/>):
    /// <c>object</c> for collection and <c>object</c> properties, the manifest's type otherwise, null when unknown.
    /// </summary>
    internal static (string Type, InferenceConfidence Confidence, string? Target) ManifestBindingFor(Element el, string key, string? typeArg = null)
    {
        const InferenceConfidence explicitly = InferenceConfidence.Explicit;
        var owner = GenericValidator.ManifestTypeOf(el);
        if (owner is null && el.IsGeneric)
        {
            if (typeArg is null)
                return ("object?", InferenceConfidence.Default, null);
            return IsBuiltIn(typeArg) ? (typeArg, explicitly, null) : ($"{ObservableCollection}<{typeArg}>", explicitly, null);
        }

        var prop = key == "_binding"
            ? owner is not null ? GenericValidator.FindInputProperty(owner) : null
            : GenericValidator.ResolveProperty(owner, key);
        if (prop is null)
            return typeArg is not null ? (Scalar(typeArg), explicitly, null) : ("string", InferenceConfidence.Default, null);

        var target = ControlManifest.Default.CSharpTypeFor(prop);
        var itemType = typeArg ?? (el.IsGeneric ? el.OfType : null);
        if (IsCollectionProperty(prop))
            return ($"{ObservableCollection}<{itemType ?? "object"}>", explicitly, "object");
        if (prop.Name == "SelectedItem" && prop.TypeName == "object" && itemType is not null)
            return ($"{itemType}?", explicitly, target);
        if (typeArg is not null)
            return (Scalar(typeArg), explicitly, target);

        return target == "object" ? ("string", InferenceConfidence.Default, target) : (target, explicitly, target);
    }

    /// <summary>
    /// A property bound to a collection of items: the manifest marks it a collection, or it is an <c>object</c>
    /// property named <c>ItemsSource</c>, <c>MenuItemsSource</c>, <c>FooterMenuItemsSource</c>, <c>Items</c> or
    /// <c>MenuItems</c>.
    /// </summary>
    internal static bool IsCollectionProperty(ManifestProperty prop) =>
        prop.IsCollection
        || prop.TypeName == "object" && prop.Name is "ItemsSource" or "MenuItemsSource" or "FooterMenuItemsSource" or "Items" or "MenuItems";

    private static readonly HashSet<string> BuiltInTypes = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal",
        "char", "string", "object", "DateTime", "DateTimeOffset", "TimeSpan", "Guid",
    };

    /// <summary>A C# built-in or common BCL value type name (<c>double</c>, <c>string</c>, <c>DateTimeOffset</c>): never an item class.</summary>
    internal static bool IsBuiltIn(string typeName) => BuiltInTypes.Contains(typeName);

    /// <summary><c>of T</c> on a single-valued binding: built-in types as written, any other type nullable (<c>Row?</c>).</summary>
    private static string Scalar(string typeName) => IsBuiltIn(typeName) && typeName != "object" ? typeName : typeName + "?";

    /// <summary>True when a binding under <paramref name="key"/> is typed by the manifest (<see cref="ManifestTypeFor"/>).</summary>
    internal static bool IsManifestTyped(Element el, string key) =>
        AstExtensions.IsPassThroughKey(key) || (el.IsGeneric && key == "_binding");

    /// <summary>The C# type a condition literal implies: int, double, string, bool, or string? for null.</summary>
    public static string LiteralType(PropValue literal) => literal switch
    {
        IntValue => "int",
        FloatValue => "double",
        BoolValue => "bool",
        NullValue => "string?",
        _ => "string",
    };

    private sealed class Walker(List<Diagnostic>? diagnostics)
    {
        public Dictionary<string, InferredMember> Members { get; } = [];

        /// <summary>
        /// Enclosing elements; a null entry marks a ListView-level property element of a table/list (<c>Header:</c>),
        /// which is outside the row template, so <c>(item)</c> there has no row (FS2007).
        /// </summary>
        private readonly Stack<Element?> _parents = new();

        public void WalkValue(PropValue value)
        {
            if (value is ElementValue ev)
                WalkElement(ev.Element);
            else if (value is ElementsValue evs)
                evs.Elements.ForEach(WalkElement);
        }

        public void WalkElement(Element el)
        {
            foreach (var prop in el.Props)
                InferFromProp(prop, el);

            if (el.When is { } when)
                WalkCondition(when);
            if (el.Enabled is { } enabled)
                WalkCondition(enabled);

            if (el.NavTarget is { } target && target.StartsWith("vm.", StringComparison.Ordinal))
            {
                var parameterType = el.NavTargetArg == "item"
                    ? RowParameterType(el.Keyword is "table" or "list" ? el : null, target, el.Location)
                    : null;
                Register(CommandPath(target)["vm.".Length..], InferredKind.Command, "IRelayCommand",
                    InferenceConfidence.Explicit, el.Location, parameterType,
                    $"{Describe(el)} -> {target}{(el.NavTargetArg is { } arg ? $"({arg})" : "")}");
            }

            // on Event -> vm.XCommand[(item)]: the same command as an arrow. (item) is the selected/clicked item for
            // SelectionChanged/ItemClick on an element with `of T` (XamlGenerator.ItemSourceOf), else the enclosing row.
            foreach (var evt in el.Events)
            {
                if (!evt.Target.StartsWith("vm.", StringComparison.Ordinal))
                    continue;
                string? parameterType = null;
                if (evt.Arg == "item")
                {
                    parameterType = XamlGenerator.ItemSourceOf(el, evt) != XamlGenerator.EventItemSource.None
                        ? el.OfType
                        : RowParameterType(null, evt.Target, evt.Location);
                }
                var name = CommandPath(evt.Target)["vm.".Length..];
                Register(name, InferredKind.Command, "IRelayCommand", InferenceConfidence.Explicit, evt.Location, parameterType,
                    $"{el.Keyword} on {evt.EventName} -> {evt.Target}");
                if (!evt.Target.EndsWith("Command", StringComparison.Ordinal))
                    EventTargets.Add((evt.Target["vm.".Length..], evt));
            }

            _parents.Push(el);
            // Property-element values (`Flyout: MenuFlyout { MenuFlyoutItem -> vm.XCommand; }`) bind like children. On a
            // table/list only ContextFlyout: is row-level (XamlGenerator.IsRowPropertyElement); the others sit on the ListView.
            foreach (var prop in el.Props)
            {
                if (prop.Value is not (ElementValue or ElementsValue))
                    continue;
                var outsideRow = el.Keyword is "table" or "list" && !el.IsGeneric && !XamlGenerator.IsRowPropertyElement(el, prop)
                                 || XamlGenerator.IsGenericItemTemplate(el);
                if (outsideRow)
                    _parents.Push(null);
                WalkValue(prop.Value);
                if (outsideRow)
                    _parents.Pop();
            }
            if (el.Body is BlockBody block)
            {
                foreach (var child in block.Children)
                {
                    if (child is ChildElement ce)
                        WalkElement(ce.Element);
                }
            }
            _parents.Pop();
        }

        private static bool IsRowOwner(Element p) =>
            !p.IsGeneric && p.Keyword is "table" or "list" || XamlGenerator.IsGenericItemTemplate(p);

        /// <summary><c>on X -> vm.Y</c> targets written without the <c>Command</c> suffix (FS2015 when <c>Y</c> is a property).</summary>
        public List<(string Name, EventBinding Event)> EventTargets { get; } = [];

        /// <summary>
        /// The row type for <c>-> vm.XCommand(item)</c>: the <c>of T</c> of <paramref name="self"/> (a table/list's own
        /// arrow) or of the nearest enclosing row template (table/list, or a generic items control with <c>of T</c> and a
        /// body). A ListView-level property element (<c>Header:</c>) is outside its own list's template, so the walk skips
        /// that list and binds the next enclosing row (an inner list's header inside an outer list's template is the outer
        /// row). FS2007 when there is no row, it has no <c>of T</c>, or the only candidate is the skipped list.
        /// </summary>
        private string? RowParameterType(Element? self, string target, SourceLocation location)
        {
            var owner = self;
            var outsideRow = false;
            if (owner is null)
            {
                var skipNext = false;
                foreach (var p in _parents)
                {
                    if (p is null)
                    {
                        skipNext = true; // pushed right above the list whose property element we are in
                        continue;
                    }
                    if (!IsRowOwner(p))
                        continue;
                    if (skipNext)
                    {
                        skipNext = false;
                        outsideRow = true;
                        continue;
                    }
                    owner = p;
                    break;
                }
            }
            if (owner?.OfType is { } ofType)
                return ofType;
            if (owner is null && outsideRow)
            {
                diagnostics?.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS2007",
                    $"'{target}(item)' is outside the row: only ContextFlyout: on a table/list is attached to each row",
                    location,
                    "move the command into ContextFlyout: or a row action, or remove (item)"));
            }
            else
            {
                diagnostics?.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS2007",
                    owner is null
                        ? $"'{target}(item)' is only valid inside a table or list"
                        : $"'{target}(item)' needs the row type: add 'of TypeName' to the enclosing {owner.Keyword}",
                    location,
                    owner is null ? "remove (item), or move the element into a table/list row" : $"{owner.Keyword} items: vm.Items of TypeName {{ ... }}"));
            }
            return null;
        }

        private void InferFromProp(PropPair prop, Element el)
        {
            // load: load(vm.X) → x:Load, a bool.
            if (prop.Value is LoadValue { Path: var loadPath } && !loadPath.StartsWith("item.", StringComparison.Ordinal))
            {
                Register(loadPath, InferredKind.Property, "bool", InferenceConfidence.Explicit, prop.Location,
                    origin: $"{el.Keyword} load: load(vm.{loadPath})", target: "bool");
                return;
            }

            if (prop.Value is not BindingValue binding)
                return;

            // item.X bindings are row-level, not ViewModel-level
            if (binding.Property.StartsWith("item.", StringComparison.Ordinal))
                return;

            var manifestTyped = IsManifestTyped(el, prop.Key);
            var (type, confidence, target) = manifestTyped
                ? ManifestBindingFor(el, prop.Key, prop.TypeArg)
                : InferTypeFromContext(el.Keyword, prop.Key, el);
            var converted = manifestTyped ? null : el.Format?.Kind;
            var written = prop.Key == "_binding" ? $"vm.{binding.Property}" : $"{prop.Key}: vm.{binding.Property}";
            var format = prop.Key == "_binding" && el.Format is { } f
                ? $" format: {f.Kind.ToString().ToLowerInvariant()}{(f.Precision is { } p ? $"({p})" : "")}"
                : "";
            Register(binding.Property, InferredKind.Property, type, confidence, prop.Location,
                origin: $"{Describe(el)} {written}{format}", target: target, format: converted);
        }

        /// <summary><c>stat "Open"</c> / <c>textbox "Name"</c> / <c>Slider</c>: the keyword and its label, if any.</summary>
        private static string Describe(Element el) =>
            el.Props.FirstOrDefault(p => p.Key == "_label")?.Value is StringValue label
                ? $"{el.Keyword} \"{label.Value.ReplaceLineEndings(" ")}\""
                : el.Keyword;

        /// <summary>A keyword binding's type, and the CLR type of the control property it binds (Target).</summary>
        private static (string Type, InferenceConfidence Confidence, string? Target) InferTypeFromContext(string keyword, string propKey, Element el)
        {
            const InferenceConfidence explicitly = InferenceConfidence.Explicit;

            switch (el.Format?.Kind)
            {
                case FormatKind.Currency: return ("decimal", explicitly, null);
                case FormatKind.Date: return ("DateTimeOffset?", explicitly, null);
                case FormatKind.Percent: return ("double", explicitly, null);
                case FormatKind.Number: return ("double", explicitly, null);
            }

            var hasItems = el.Props.Any(p => p.Key == "items");

            if (propKey == "items")
            {
                var itemType = el.OfType ?? (keyword == "select" ? "string" : "object");
                return ($"{ObservableCollection}<{itemType}>", explicitly, "object");
            }

            if (propKey == "selected")
                return ($"{el.OfType ?? "object"}?", explicitly, "object");

            return keyword switch
            {
                "checkbox" or "radio" => ("bool", explicitly, "bool?"),
                "toggle" => ("bool", explicitly, "bool"),
                "number" or "progress" => ("double", explicitly, "double"),
                "datepicker" => ("DateTimeOffset?", explicitly, "DateTimeOffset?"),
                "select" when HasIntRange(el) => ("int", explicitly, "object"),
                "select" when hasItems => ($"{el.OfType ?? "string"}?", explicitly, "object"),
                _ => ("string", InferenceConfidence.Default, null),
            };
        }

        private static bool HasIntRange(Element el) =>
            el.Props.Any(p => p.Value is RangeValue);

        private void WalkCondition(CondExpr cond)
        {
            switch (cond)
            {
                case PathCond path:
                    if (VmName(path.Path) is { } name)
                        Register(name, InferredKind.Property, "bool", InferenceConfidence.Comparison, path.Location,
                            origin: $"when({path.Path})");
                    break;
                case NotCond not:
                    WalkCondition(not.Inner);
                    break;
                case BinaryCond binary:
                    WalkCondition(binary.Left);
                    WalkCondition(binary.Right);
                    break;
                case CompareCond compare:
                    RegisterOperand(compare.Left, compare.Right);
                    RegisterOperand(compare.Right, compare.Left);
                    break;
            }
        }

        private void RegisterOperand(Operand operand, Operand other)
        {
            if (operand is not PathOperand path || VmName(path.Path) is not { } name)
                return;
            if (other is LiteralOperand literal)
                Register(name, InferredKind.Property, LiteralType(literal.Value), InferenceConfidence.Comparison, literal.Location,
                    origin: $"{path.Path} compared with {LiteralType(literal.Value) switch { "int" => "an int", "double" => "a double", "bool" => "a bool", "string?" => "null", _ => "a string" }} literal".Replace("null literal", "null", StringComparison.Ordinal));
            else
                Register(name, InferredKind.Property, "string", InferenceConfidence.Default, path.Location,
                    origin: $"{path.Path} compared with another path");
        }

        private static string? VmName(string path) =>
            path.StartsWith("vm.", StringComparison.Ordinal) ? path["vm.".Length..] : null;

        public void Register(string name, InferredKind kind, string type,
            InferenceConfidence confidence, SourceLocation location, string? parameterType = null, string? origin = null,
            string? target = null, FormatKind? format = null)
        {
            if (!Members.TryGetValue(name, out var existing))
            {
                Members[name] = new InferredMember(name, kind, type, DefaultForType(type), confidence, parameterType, location, origin,
                    target, format);
                return;
            }

            if (existing.Kind == InferredKind.Command || kind == InferredKind.Command)
            {
                if (existing.Kind == InferredKind.Command && kind == InferredKind.Command
                    && existing.ParameterType is null && parameterType is not null)
                    Members[name] = existing with { ParameterType = parameterType };
                return;
            }

            if (confidence > existing.Confidence)
            {
                if (existing.Confidence == InferenceConfidence.Comparison && confidence == InferenceConfidence.Explicit
                    && !IsCompatible(type, existing.CSharpType))
                    ReportConflict(name, type, location, existing.CSharpType, existing.Location ?? location);

                Members[name] = existing with
                {
                    CSharpType = type,
                    DefaultValue = DefaultForType(type),
                    Confidence = confidence,
                    Location = location,
                    Origin = origin,
                    Target = target,
                    Format = format,
                };
            }
            else if (confidence < existing.Confidence
                     && confidence == InferenceConfidence.Comparison && existing.Confidence == InferenceConfidence.Explicit
                     && !IsCompatible(existing.CSharpType, type))
            {
                ReportConflict(name, existing.CSharpType, existing.Location ?? location, type, location);
            }
        }

        private void ReportConflict(string name, string explicitType, SourceLocation explicitLocation,
            string comparisonType, SourceLocation comparisonLocation)
        {
            var what = comparisonType switch
            {
                "bool" => "used as a bool condition",
                "string?" => "compared with null",
                _ => $"compared with a {comparisonType} literal",
            };
            diagnostics?.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS2004",
                $"vm.{name} is {explicitType} (from {explicitLocation.ToShortString()}) but is {what} here",
                comparisonLocation,
                $"compare vm.{name} with a {explicitType.TrimEnd('?')} value"));
        }
    }

    private static readonly HashSet<string> ValueTypes =
    [
        "int", "long", "short", "byte", "double", "float", "decimal", "bool",
        "DateTimeOffset", "DateTime", "TimeSpan", "Guid",
    ];

    /// <summary>Can a property of <paramref name="explicitType"/> be compared with a literal implying <paramref name="comparisonType"/>?</summary>
    internal static bool IsCompatible(string explicitType, string comparisonType)
    {
        var baseType = explicitType.TrimEnd('?');
        return comparisonType switch
        {
            "int" => baseType is "int" or "long" or "short" or "byte" or "double" or "float" or "decimal",
            "double" => baseType is "double" or "float" or "decimal",
            "bool" => baseType == "bool",
            "string" => baseType == "string",
            "string?" => explicitType.EndsWith('?') || !ValueTypes.Contains(baseType),
            _ => true,
        };
    }

    private static string? DefaultForType(string type) => type switch
    {
        "string" => "\"\"",
        _ when type.StartsWith("System.Collections", StringComparison.Ordinal) => "new()",
        _ => null
    };
}
