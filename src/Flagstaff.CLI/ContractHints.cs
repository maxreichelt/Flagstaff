using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Cli;

/// <summary>
/// Explains C# errors in the hand-written half that collide with, or misuse, what the compiler generated
/// (<see cref="ViewContract"/>, <see cref="ItemTypeContract"/>): after <c>flagstaff build</c> parses MSBuild's output,
/// each CS diagnostic without a hint is matched against <see cref="Rules"/> (code + message pattern) and gets a
/// hint naming the generated member, the <c>.flag</c> usage it comes from, and the fix.
/// </summary>
internal sealed partial class ContractHints
{
    private readonly IReadOnlyList<ViewContract> _views;
    private readonly IReadOnlyList<ItemTypeContract> _itemTypes;
    private readonly string _namespace;
    private readonly Func<SourceLocation, string> _flagLocation;
    private readonly Func<string, int, string?> _readLine;

    /// <param name="views">The contracts of the compiled views.</param>
    /// <param name="itemTypes">The generated item types.</param>
    /// <param name="rootNamespace">The app namespace (item types and ViewModels live in <c>{ns}.ViewModels</c>).</param>
    /// <param name="flagLocation">Renders a .flag location for a hint (<c>Flags/dashboard.flag line 31</c>).</param>
    /// <param name="readLine">Reads one source line (1-based) of a diagnostic's file, or null.</param>
    public ContractHints(IReadOnlyList<ViewContract> views, IReadOnlyList<ItemTypeContract> itemTypes, string rootNamespace,
        Func<SourceLocation, string>? flagLocation = null, Func<string, int, string?>? readLine = null)
    {
        _views = views;
        _itemTypes = itemTypes;
        _namespace = rootNamespace;
        _flagLocation = flagLocation ?? (l => $"{l.FileName} line {l.Line}");
        _readLine = readLine ?? ((_, _) => null);
    }

    /// <summary>A rule: diagnostic code, message pattern, and the hint (null when the match is not about generated code).</summary>
    private sealed record Rule(string Code, Regex Message, Func<ContractHints, Match, CliDiagnostic, string?> Hint);

    private static readonly Rule[] Rules =
    [
        new("CS0102", DuplicateDefinition(), (h, m, d) => h.Duplicate(m.Groups["type"].Value, m.Groups["name"].Value)),
        new("CS0756", MultipleDefiningDeclarations(), (h, _, d) => h.DuplicateDefiningDeclaration(d)),
        new("CS0111", DuplicateMember(), (h, m, _) => h.DuplicateMember(m.Groups["type"].Value, m.Groups["name"].Value)),
        new("CS0759", NoDefiningDeclaration(), (h, m, _) => h.UndeclaredDelegate(m.Groups["type"].Value, m.Groups["name"].Value)),
        new("CS8795", MissingImplementation(), (h, m, _) => h.MissingDelegate(m.Groups["type"].Value, m.Groups["name"].Value)),
        new("CS0029", ConversionMessage(), (h, _, d) => h.TypeMismatch(d)),
        new("CS0266", ConversionMessage(), (h, _, d) => h.TypeMismatch(d)),
        new("CS0019", OperatorMessage(), (h, _, d) => h.TypeMismatch(d)),
        new("CS1503", ArgumentMessage(), (h, _, d) => h.TypeMismatch(d)),
        new("CS8799", AccessibilityMismatch(), (h, _, d) => h.PartialAccessibility(d)),
        new("CS8817", ReturnTypeMismatch(), (h, _, d) => h.PartialReturnType(d)),
        new("CS0260", MissingPartial(), (h, m, _) => h.MissingPartialModifier(m.Groups["type"].Value)),
        new("CS1061", NullableNoDefinition(), (h, m, d) => h.NullableMember(m.Groups["type"].Value, m.Groups["name"].Value, d)),
        new("CS1061", NoDefinition(), (h, m, d) => h.MissingMember(m.Groups["type"].Value, m.Groups["name"].Value, d)),
        new("CS0117", NoDefinition(), (h, m, d) => h.MissingMember(m.Groups["type"].Value, m.Groups["name"].Value, d)),
        new("CS0103", NameNotInContext(), (h, m, d) => h.NameNotInScope(m.Groups["name"].Value, d)),
        new("CS0103", NameNotInContext(), (_, m, _) => WinUiTypeHints.ForTypeName(m.Groups["name"].Value)),
        new("CS0246", TypeNotFound(), (h, m, _) => h.ItemTypeNamespace(m.Groups["name"].Value)),
        new("CS0246", TypeNotFound(), (_, m, _) => WinUiTypeHints.ForTypeName(m.Groups["name"].Value)),
        new("CS0234", NotInNamespace(), (_, m, _) => WinUiTypeHints.ForTypeName(m.Groups["name"].Value, m.Groups["ns"].Value)),
        new("CS0115", NoOverride(), (_, m, _) => WinUiTypeHints.ForMissingOverride(m.Groups["name"].Value, m.Groups["args"].Value)),
        new("CS0104", AmbiguousReference(), (h, m, _) => h.AmbiguousItemType(m.Groups["name"].Value)),
        new("WMC0011", UnknownXamlMember(), (_, m, _) => UnknownMember(m.Groups["type"].Value, m.Groups["name"].Value)),
    ];

    [GeneratedRegex(@"The type '(?<type>[\w.]+)' already contains a definition for '(?<name>\w+)'")]
    private static partial Regex DuplicateDefinition();

    [GeneratedRegex(@"A partial (?:method|property) may not have multiple defining declarations")]
    private static partial Regex MultipleDefiningDeclarations();

    [GeneratedRegex(@"Type '(?<type>[\w.]+)' already defines a member called '(?<name>[\w.]+)' with the same parameter types")]
    private static partial Regex DuplicateMember();

    [GeneratedRegex(@"No defining declaration found for implementing declaration of partial method '(?<type>[\w.]+)\.(?<name>\w+)\(")]
    private static partial Regex NoDefiningDeclaration();

    [GeneratedRegex(@"Partial method '(?<type>[\w.]+)\.(?<name>\w+)\(.*must have an implementation part")]
    private static partial Regex MissingImplementation();

    [GeneratedRegex(@"[Cc]annot (?:implicitly )?convert type '[^']+' to '[^']+'")]
    private static partial Regex ConversionMessage();

    [GeneratedRegex(@"Operator '[^']+' cannot be applied to operands? of type '[^']+'")]
    private static partial Regex OperatorMessage();

    [GeneratedRegex(@"[Cc]annot convert from '[^']+' to '[^']+'")]
    private static partial Regex ArgumentMessage();

    [GeneratedRegex(@"Both partial (?:member|method) declarations must have identical accessibility modifiers")]
    private static partial Regex AccessibilityMismatch();

    [GeneratedRegex(@"Both partial (?:member|method) declarations must have the same return type")]
    private static partial Regex ReturnTypeMismatch();

    /// <summary>A CommunityToolkit property hook: <c>OnVolumeChanged</c>, <c>OnVolumeChanging</c>.</summary>
    [GeneratedRegex(@"\bOn(?<prop>[A-Z]\w*?)(?<kind>Changed|Changing)\b")]
    private static partial Regex ToolkitHook();

    [GeneratedRegex(@"'(?<type>[\w.]+\?)' does not contain a definition for '(?<name>\w+)'")]
    private static partial Regex NullableNoDefinition();

    [GeneratedRegex(@"Missing partial modifier on declaration of type '(?<type>[\w.]+)'")]
    private static partial Regex MissingPartial();

    [GeneratedRegex(@"'(?<type>[\w.]+)' does not contain a definition for '(?<name>\w+)'")]
    private static partial Regex NoDefinition();

    [GeneratedRegex(@"The name '(?<name>\w+)' does not exist in the current context")]
    private static partial Regex NameNotInContext();

    [GeneratedRegex(@"The type or namespace name '(?<name>\w+)' could not be found")]
    private static partial Regex TypeNotFound();

    [GeneratedRegex(@"The type or namespace name '(?<name>\w+)' does not exist in the namespace '(?<ns>[\w.]+)'")]
    private static partial Regex NotInNamespace();

    /// <summary>CS0115 on a routed-event virtual (<c>OnPointerPressed(PointerRoutedEventArgs)</c>) in a non-Control subclass.</summary>
    [GeneratedRegex(@"\.(?<name>On\w+)\((?<args>\w*RoutedEventArgs)\)': no suitable method found to override")]
    private static partial Regex NoOverride();

    [GeneratedRegex(@"'(?<name>\w+)' is an ambiguous reference between")]
    private static partial Regex AmbiguousReference();

    [GeneratedRegex(@"Unknown member '(?<name>[\w.]+)' on element '(?<type>[\w:.]+)'")]
    private static partial Regex UnknownXamlMember();

    [GeneratedRegex(@"(?<type>[A-Za-z_]\w*)\.g\.cs$", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedFileType();

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex Identifier();

    /// <summary>The codes that have rules (for docs and tests).</summary>
    public static IEnumerable<string> Codes => Rules.Select(r => r.Code).Distinct();

    /// <summary>Returns <paramref name="diagnostics"/> with a hint on every CS diagnostic a rule explains.</summary>
    public List<CliDiagnostic> Apply(IEnumerable<CliDiagnostic> diagnostics) => diagnostics.Select(Apply).ToList();

    public CliDiagnostic Apply(CliDiagnostic d)
    {
        if (d.Hint is not null || d.Code is null
            || !(d.Code.StartsWith("CS", StringComparison.Ordinal) || d.Code.StartsWith("WMC", StringComparison.Ordinal)))
            return d;
        foreach (var rule in Rules)
        {
            if (rule.Code != d.Code)
                continue;
            var m = rule.Message.Match(d.Message);
            if (m.Success && rule.Hint(this, m, d) is { } hint)
                return d with { Hint = hint };
        }
        return d;
    }

    // ── Lookups ────────────────────────────────────────────────────

    private static string Simple(string type) => type[(type.LastIndexOf('.') + 1)..];

    private ViewContract? ViewModel(string type)
    {
        var name = Simple(type);
        return _views.FirstOrDefault(v => v.ViewModelType == name);
    }

    private ViewContract? Page(string type)
    {
        var name = Simple(type);
        return _views.FirstOrDefault(v => v.ViewName == name);
    }

    private ItemTypeContract? ItemType(string type)
    {
        var name = Simple(type);
        return _itemTypes.FirstOrDefault(t => t.TypeName == name);
    }

    private string From(SourceLocation? location, string? origin) =>
        location is { } l
            ? origin is null ? _flagLocation(l) : $"{_flagLocation(l)} ({origin})"
            : origin ?? "the .flag file";

    private static string List(IEnumerable<string> names, int max = 12)
    {
        var all = names.ToList();
        return all.Count == 0 ? "none"
            : all.Count <= max ? string.Join(", ", all)
            : string.Join(", ", all.Take(max)) + $", ... ({all.Count - max} more)";
    }

    private static string DelegateList(ViewContract vm) =>
        List(vm.Delegates.Select(c => c.DelegateParameterType is null ? $"{c.Delegate}()" : $"{c.Delegate}({c.DelegateParameterType} item)"));

    // ── Hints ──────────────────────────────────────────────────────

    private string? Duplicate(string type, string name)
    {
        if (ViewModel(type) is { } vm)
        {
            if (vm.FindProperty(name) is { } p)
            {
                return $"'{p.Name}' ({ViewContract.ShortType(p.CSharpType)}) is generated from {From(p.Location, p.Origin)} into Generated/{vm.ViewModelFile}; "
                       + $"delete your declaration of {p.FieldName}/{p.Name}, the generated partial already has it";
            }
            if (FindCommand(vm, name) is { } c)
                return CommandHint(vm, c);
            return null;
        }
        if (ItemType(type) is { } item && item.Properties.FirstOrDefault(p => p.Name == name) is { } ip)
        {
            return $"'{item.TypeName}.{ip.Name}' ({ip.CSharpType}) is generated from item.{ip.Name} in {From(ip.Location, null)} into Generated/{item.File}; "
                   + "delete your declaration, the generated partial already has it";
        }
        if (Page(type) is { } page && name == "ViewModel" && page.ViewModelType is not null)
            return $"the generated page partial Generated/{page.PageFile} already declares 'ViewModel' ({page.ViewModelType}); delete yours";
        return null;
    }

    private static ContractCommand? FindCommand(ViewContract vm, string name) =>
        vm.Commands.FirstOrDefault(c => c.Name == name || c.Method == name || c.Delegate == name
                                        || c.Method + "Command" == name);

    private string CommandHint(ViewContract vm, ContractCommand c) =>
        c.Delegate is null
            ? $"'{c.Name}' is generated for a dialog/confirm in Generated/{vm.ViewModelFile}; delete your declaration"
            : $"'{c.Name}' is generated from {From(c.Location, c.Origin)} into Generated/{vm.ViewModelFile}; "
              + $"delete your [RelayCommand] {c.Method}/{c.Name} and implement only '{c.DelegateSignature}' with a body";

    private string? DuplicateDefiningDeclaration(CliDiagnostic d)
    {
        // A declaration in the user's own file: `private partial Task OnSaveAsync();` repeated without a body.
        if (d.File is not null && d.Line is { } line && _readLine(d.File, line) is { } text)
        {
            foreach (var vm in _views.Where(v => v.ViewModelType is not null))
            {
                if (vm.Delegates.FirstOrDefault(c => Regex.IsMatch(text, $@"\b{Regex.Escape(c.Delegate!)}\b")) is { } c)
                {
                    return $"Generated/{vm.ViewModelFile} already declares '{c.DelegateSignature};'; write only the implementation, with a body: "
                           + $"private partial async Task {c.Delegate}({(c.DelegateParameterType is null ? "" : $"{c.DelegateParameterType} item")}) {{ ... }}";
                }
            }
        }
        // The CommunityToolkit generator's On{X}Changing/Changed partial methods, duplicated because an [ObservableProperty]
        // field is declared twice: the generated file is named after the type (...ViewModels.OrdersViewModel.g.cs).
        if (d.File is not null && GeneratedFileType().Match(d.File) is { Success: true } m && ViewModel(m.Groups["type"].Value) is { } owner)
        {
            return $"follow-on error of an [ObservableProperty] declared twice: Generated/{owner.ViewModelFile} already declares "
                   + $"{List(owner.Properties.Select(p => p.Name))}; delete your own fields/properties with these names (see the CS0102 errors)";
        }
        return null;
    }

    private string? DuplicateMember(string type, string name)
    {
        if (Page(type) is { } page && Simple(name) == page.ViewName)
        {
            return $"Generated/{page.PageFile} already declares the constructor{(page.Constructors.Count > 1 ? "s" : "")} "
                   + $"{string.Join(" and ", page.Constructors)}; delete your constructor and put its body in 'partial void OnViewCreated() {{ ... }}' "
                   + "(runs once after InitializeComponent; named elements (name:) and ViewModel are set). Load data with 'onload -> vm.Load' in the view";
        }
        if (Page(type) is { } generatedPage && GeneratedPageMethods.TryGetValue(Simple(name), out var purpose))
        {
            return $"Generated/{generatedPage.PageFile} already declares '{Simple(name)}' ({purpose}); rename yours, "
                   + "or put page setup in 'partial void OnViewCreated() { ... }' (runs once after InitializeComponent)";
        }
        if (ViewModel(type) is { } vm && FindCommand(vm, Simple(name)) is { } c)
        {
            if (c.Delegate == Simple(name))
            {
                return $"Generated/{vm.ViewModelFile} declares '{c.DelegateSignature};' (from {From(c.Location, c.Origin)}); "
                       + "implement it once, as 'private partial async Task ...' with a body";
            }
            return CommandHint(vm, c);
        }
        return null;
    }

    /// <summary>Methods the generated page code-behind declares, and what they are for.</summary>
    private static readonly Dictionary<string, string> GeneratedPageMethods = new(StringComparer.Ordinal)
    {
        ["OnViewLoaded"] = "the Loaded handler that runs 'onload -> vm.X'",
        ["OnNavigateButtonClick"] = "the click handler of navigate(View) buttons",
        ["OnToastFlagChanged"] = "the toast duration: timer",
        ["OnViewCreated"] = "the setup hook; implement it once, as 'partial void OnViewCreated() { ... }'",
    };

    private string? PartialAccessibility(CliDiagnostic d)
    {
        if (d.File is null || d.Line is not { } line || _readLine(d.File, line) is not { } text)
            return null;
        if (Regex.IsMatch(text, @"\bOnViewCreated\b"))
            return "the generated page declares 'partial void OnViewCreated();' with no access modifier; write 'partial void OnViewCreated() { ... }'";
        if (ToolkitHookSignature(text) is { } hook)
        {
            return $"CommunityToolkit.Mvvm declares '{hook};' with no access modifier (it is not a Flagstaff delegate); drop 'private'/'public': "
                   + $"{hook} {{ ... }}";
        }
        foreach (var vm in _views.Where(v => v.ViewModelType is not null))
        {
            if (vm.Delegates.FirstOrDefault(c => Regex.IsMatch(text, $@"\b{Regex.Escape(c.Delegate!)}\b")) is { } c)
            {
                return $"Generated/{vm.ViewModelFile} declares '{c.DelegateSignature};' (from {From(c.Location, c.Origin)}); write yours with the same "
                       + $"accessibility: {c.DelegateSignature!.Replace("partial Task", "partial async Task", StringComparison.Ordinal)} {{ ... }}";
            }
        }
        return null;
    }

    /// <summary>
    /// <c>partial void OnVolumeChanged(double value)</c> for a line naming a CommunityToolkit hook, with the type of the
    /// generated property when the contract has it (else <c>T</c>); null when the line names no hook.
    /// </summary>
    private string? ToolkitHookSignature(string text)
    {
        if (ToolkitHook().Match(text) is not { Success: true } m)
            return null;
        var prop = m.Groups["prop"].Value;
        var type = _views.SelectMany(v => v.Properties).FirstOrDefault(p => p.Name == prop) is { } p ? ViewContract.ShortType(p.CSharpType) : null;
        return $"partial void On{prop}{m.Groups["kind"].Value}({type ?? "T"} value)";
    }

    /// <summary>CS8817: a delegate written <c>partial void</c> against the generated <c>partial Task</c>, or a toolkit hook returning something.</summary>
    private string? PartialReturnType(CliDiagnostic d)
    {
        if (d.File is null || d.Line is not { } line || _readLine(d.File, line) is not { } text)
            return null;
        if (ToolkitHookSignature(text) is { } hook)
            return $"CommunityToolkit.Mvvm declares '{hook};', which returns void: write {hook} {{ ... }}";
        foreach (var vm in _views.Where(v => v.ViewModelType is not null))
        {
            if (vm.Delegates.FirstOrDefault(c => Regex.IsMatch(text, $@"\b{Regex.Escape(c.Delegate!)}\b")) is { } c)
            {
                return $"Generated/{vm.ViewModelFile} declares '{c.DelegateSignature};' (from {From(c.Location, c.Origin)}): return a Task, not void: "
                       + $"{c.DelegateSignature!.Replace("partial Task", "partial async Task", StringComparison.Ordinal)} {{ ... }}";
            }
        }
        return null;
    }

    private string? UndeclaredDelegate(string type, string name)
    {
        if (ViewModel(type) is not { } vm)
            return null;
        if (ToolkitHook().Match(name) is { Success: true } hook && vm.Properties.FirstOrDefault(p => p.Name == hook.Groups["prop"].Value) is { } property)
        {
            var shortType = ViewContract.ShortType(property.CSharpType);
            return $"CommunityToolkit.Mvvm declares 'partial void {name}({shortType} value)' for the property {property.Name} ({shortType}, from "
                   + $"{From(property.Location, property.Origin)}); write exactly that signature (also 'partial void {name}({shortType} oldValue, {shortType} newValue)'), "
                   + "without an access modifier";
        }
        var command = name.StartsWith("On", StringComparison.Ordinal) && name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 7
            ? name[2..^5]
            : null;
        var declared = DelegateList(vm);
        var fix = command is "Load" or "Init" or "Initialize"
            ? $"to run it when the page loads, add 'onload -> vm.{command};' after 'bind vm:' in Flags/{ShortFile(vm)}; or drop 'partial' and make it a normal method"
            : command is not null
            ? $"bind a command to it in Flags/ (button \"...\" -> vm.{command};) or drop 'partial' and make it a normal method"
            : "drop 'partial' and make it a normal method";
        return $"the generator declares delegates only for commands the .flag files use; declared for {vm.ViewModelType}: {declared}. "
               + $"'{name}' is not one of them: {fix}. 'flagstaff stubs' lists them";
    }

    private static string ShortFile(ViewContract vm) => vm.Location.FileName.Length > 0 ? vm.Location.FileName : "<view>.flag";

    private string? MissingDelegate(string type, string name)
    {
        if (ViewModel(type) is not { } vm || vm.Delegates.FirstOrDefault(c => c.Delegate == name) is not { } c)
            return null;
        return $"implement it in your ViewModels/{vm.ViewModelType}.cs: {c.DelegateSignature!.Replace("partial Task", "partial async Task", StringComparison.Ordinal)} {{ ... }} "
               + $"(from {From(c.Location, c.Origin)}; 'flagstaff stubs --update' adds a stub); an {name} with other parameters or another return type "
               + "does not implement it: change that one to exactly this signature";
    }

    private string? TypeMismatch(CliDiagnostic d)
    {
        if (d.File is null || d.Line is not { } line || _readLine(d.File, line) is not { } text)
            return null;
        var identifiers = Identifier().Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var message = d.Message.Replace("System.Collections.ObjectModel.", "", StringComparison.Ordinal)
            .Replace("System.", "", StringComparison.Ordinal);
        bool Mentions(string type) =>
            message.Contains($"'{ViewContract.ShortType(type)}'", StringComparison.Ordinal)
            || message.Contains($"'{type.TrimEnd('?')}'", StringComparison.Ordinal);

        foreach (var vm in _views)
        {
            foreach (var p in vm.Properties)
            {
                if (!identifiers.Contains(p.Name) || !Mentions(p.CSharpType))
                    continue;
                var shortType = ViewContract.ShortType(p.CSharpType);
                if (p.CSharpType == "string" && p.Confidence == InferenceConfidence.Default)
                {
                    return $"'{p.Name}' is inferred string (no type information) from {From(p.Location, p.Origin)}. To make it a number, "
                           + $"add format: number to that line (double; int values convert, number(0) shows no decimals), or compare it in a "
                           + $"condition (when(vm.{p.Name} > 0) types it int); to keep it a string, assign text: {p.Name} = count.ToString()";
                }
                if (p.DeclaredBy is not null)
                {
                    return $"'{p.Name}' is declared by you as {shortType} in {p.DeclaredBy.File} (the generated code uses that type; the .flag binds it in "
                           + $"{From(p.Location, p.Origin)}); convert your value to {shortType}";
                }
                return $"'{p.Name}' is generated as {shortType} from {From(p.Location, p.Origin)}; convert your value to {shortType}, "
                       + "or change that .flag usage (format:, keyword) to type it differently";
            }
        }
        foreach (var item in _itemTypes)
        {
            foreach (var p in item.Properties)
            {
                if (!identifiers.Contains(p.Name) || !Mentions(p.CSharpType))
                    continue;
                if (item.DeclaredByUser?.Contains(p.Name) == true)
                {
                    return $"'{item.TypeName}.{p.Name}' is declared by you as {p.CSharpType} (the .flag binds item.{p.Name} in {From(p.Location, null)}); "
                           + $"convert your value to {p.CSharpType}";
                }
                var nullable = p.CSharpType.EndsWith('?') ? $" (nullable: use {p.CSharpType} in your code, or .Value / ?? a default)" : "";
                return $"'{item.TypeName}.{p.Name}' is generated as {p.CSharpType} from item.{p.Name} in {From(p.Location, null)}{nullable}; "
                       + "match that type, or change the .flag usage (format:) to type it differently";
            }
        }
        return null;
    }

    private string? MissingPartialModifier(string type)
    {
        if (ItemType(type) is { } item)
            return $"'{item.TypeName}' is generated as a partial class in Generated/{item.File} (namespace {_namespace}.ViewModels); add 'partial' to your class: public partial class {item.TypeName}";
        if (ViewModel(type) is { } vm)
            return $"'{vm.ViewModelType}' is generated as a partial class in Generated/{vm.ViewModelFile}; declare yours as 'public partial class {vm.ViewModelType}'";
        if (Page(type) is { } page)
            return $"'{page.ViewName}' is generated as a partial class in Generated/{page.PageFile}; declare yours as 'public sealed partial class {page.ViewName}'";
        return $"'{Simple(type)}' is not generated by Flagstaff; another declaration of it in your code is 'partial': add 'partial' to every declaration of {Simple(type)}, or remove it from all of them";
    }

    private string? MissingMember(string type, string name, CliDiagnostic d)
    {
        if (Page(type) is { } page && d.File is not null && GeneratedFileType().IsMatch(d.File.Replace(".g.i.cs", ".g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{name}' is an event handler named in {page.ViewName}'s XAML (a xaml {{ }} block in {ShortFile(page)}) that your page partial does not declare: "
                   + $"add 'private void {name}(object sender, RoutedEventArgs e) {{ ... }}' (with the event's argument type) to 'partial class {page.ViewName}', "
                   + "or write the control as a generic element with 'on Event -> vm.XCommand' instead of xaml { }";
        }
        if (ItemType(type) is { } item)
        {
            return $"'{item.TypeName}' only has the properties the .flag files bind ({List(item.Properties.Select(p => p.Name))}); "
                   + $"bind item.{name} in a template/col, or declare it in your own 'public partial class {item.TypeName}' (namespace {_namespace}.ViewModels)";
        }
        if (ViewModel(type) is { } vm)
        {
            return $"{vm.ViewModelType} generates only {List(vm.Properties.Select(p => p.Name).Concat(vm.Commands.Select(c => c.Name)))}; "
                   + $"bind vm.{name} in a .flag file, or declare it in your own partial class";
        }
        return null;
    }

    /// <summary>CS1061 on a nullable value (<c>DateTimeOffset?</c> from a datepicker used as a DateTime).</summary>
    private string? NullableMember(string type, string name, CliDiagnostic d)
    {
        var shortType = Simple(type.TrimEnd('?')) + "?";
        var line = d.File is not null && d.Line is { } l ? _readLine(d.File, l) : null;
        var identifiers = line is null ? [] : Identifier().Matches(line).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        bool Matches(string csType) => Simple(csType.TrimEnd('?')) + "?" == shortType && csType.EndsWith('?');
        var useInstead = shortType == "DateTimeOffset?" ? "DateTime (a DateTime?)" : name;
        foreach (var vm in _views)
        {
            if (vm.Properties.FirstOrDefault(p => identifiers.Contains(p.Name) && Matches(p.CSharpType)) is { } p)
            {
                return $"vm.{p.Name} is {shortType} (from {From(p.Location, p.Origin)}); it can be null: use {p.Name}?.{useInstead}, "
                       + $"or {p.Name}.Value.{name} after a null check";
            }
        }
        foreach (var item in _itemTypes)
        {
            if (item.Properties.FirstOrDefault(p => identifiers.Contains(p.Name) && Matches(p.CSharpType)) is { } p)
            {
                return $"'{item.TypeName}.{p.Name}' is {shortType} (from item.{p.Name} in {From(p.Location, null)}); it can be null: use {p.Name}?.{name} "
                       + $"or {p.Name}.Value.{name} after a null check";
            }
        }
        return $"the value is {shortType}{(shortType == "DateTimeOffset?" ? " (datepicker bindings are nullable)" : "")}: use ?.{name}, or .Value.{name} after a null check";
    }

    private string? NameNotInScope(string name, CliDiagnostic d)
    {
        if (d.File is not null && d.File.Replace('\\', '/').Contains("/Generated/", StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var vm in _views.Where(v => v.ViewModelType is not null))
        {
            if (vm.Properties.FirstOrDefault(p => p.Name == name) is { } p)
            {
                return $"'{name}' is a property of the ViewModel ({vm.ViewModelType}.{name}, generated from {From(p.Location, p.Origin)}), not of the page: "
                       + $"in a page partial write ViewModel.{name}; if you meant a type, qualify it "
                       + (name is "Color" or "Colors" ? "(Windows.UI.Color, Microsoft.UI.Colors)" : "with its namespace")
                       + " or add its using";
            }
            if (vm.Commands.FirstOrDefault(c => c.Name == name) is { } c)
                return $"'{name}' is a command of the ViewModel ({vm.ViewModelType}); in a page partial write ViewModel.{name}";
        }
        return null;
    }

    /// <summary>WMC0011 (usually from a <c>xaml { }</c> block): the manifest's suggestion, synonyms first.</summary>
    private static string? UnknownMember(string type, string name)
    {
        var shortType = type[(type.LastIndexOfAny([':', '.']) + 1)..];
        if (name.Contains('.') || !ControlManifest.Default.TryGetType(shortType, out var t))
            return null;
        var suggestions = ControlManifest.Default.SuggestProperties(t, name);
        return suggestions.Count > 0
            ? $"{shortType} has no '{name}'; did you mean '{suggestions[0]}'?"
              + (suggestions.Count > 1 ? $" (close: {string.Join(", ", suggestions.Skip(1).Take(4))})" : "")
              + "; written as a generic element instead of xaml { }, flagstaff check catches this before the build"
            : null;
    }

    private string? ItemTypeNamespace(string name) =>
        ItemType(name) is { } item
            ? $"'{item.TypeName}' is generated in namespace {_namespace}.ViewModels (Generated/{item.File}); add 'using {_namespace}.ViewModels;'"
            : null;

    private string? AmbiguousItemType(string name) =>
        ItemType(name) is { } item
            ? $"'of {item.TypeName}' in the .flag generates {_namespace}.ViewModels.{item.TypeName} (Generated/{item.File}); do not declare a second "
              + $"{item.TypeName}: add members in 'public partial class {item.TypeName}' in namespace {_namespace}.ViewModels"
            : null;
}
