using System.Text.RegularExpressions;

namespace Flagstaff.Compiler;

/// <summary>A ViewModel property the generated partial declares (<c>[ObservableProperty] private T _x;</c> → <c>X</c>).</summary>
/// <param name="Origin">The usage that typed it, as written (<see cref="InferredMember.Origin"/>).</param>
/// <param name="DeclaredBy">The hand-written member that already declares it: the generator skips it (FS2016).</param>
/// <param name="FieldOverride">The generated field's name when a plain hand-written field took <c>_x</c> (<c>x</c> then).</param>
/// <param name="CSharpType">The member's type: the hand-written one when <see cref="DeclaredBy"/> declares another type
/// than the .flag implies (<see cref="DeclaredTypes"/>; <see cref="InferredType"/> is then the .flag's).</param>
public sealed record ContractProperty(
    string Name,
    string CSharpType,
    InferenceConfidence Confidence,
    SourceLocation? Location,
    string? Origin,
    UserMember? DeclaredBy = null,
    string? FieldOverride = null,
    string? InferredType = null)
{
    /// <summary>The backing field the generated partial declares: <c>_openCount</c> for <c>OpenCount</c>.</summary>
    public string FieldName => FieldOverride ?? ViewContract.FieldNameOf(Name);
}

/// <summary>How the command delegate <c>On{X}Async</c> is declared.</summary>
public enum DelegateMode
{
    /// <summary>The generated partial declares <c>{access} partial Task On{X}Async(...);</c>.</summary>
    Partial,

    /// <summary>The hand-written half already has the defining declaration (no body): the generator skips its own.</summary>
    UserDeclared,

    /// <summary>The hand-written half has a non-partial method of that name (or the command calls another of its methods): no declaration.</summary>
    UserMethod,
}

/// <summary>
/// A command the generated partial declares: <c>[RelayCommand] SaveAsync()</c> → <c>SaveCommand</c>, delegating to the
/// partial method <c>OnSaveAsync()</c> the hand-written partial implements (<see cref="Delegate"/>; null for the
/// generated <c>Cancel{Flag}</c> commands of dialogs and confirms, which have no delegate).
/// </summary>
/// <param name="Method">The generated <c>[RelayCommand]</c> method (<c>SaveAsync</c>; <c>Save</c> when a hand-written
/// <c>SaveAsync</c> took the name).</param>
/// <param name="DeclaredBy">The hand-written member that already provides the command: nothing is generated (FS2016).</param>
public sealed record ContractCommand(
    string Name,
    string Method,
    string? Delegate,
    string? ParameterType,
    SourceLocation? Location,
    string? Origin,
    UserMember? DeclaredBy = null)
{
    /// <summary><c>Save</c> for <c>SaveCommand</c>.</summary>
    public string Stem => Name.EndsWith("Command", StringComparison.Ordinal) ? Name[..^"Command".Length] : Name;

    public DelegateMode DelegateMode { get; init; } = DelegateMode.Partial;

    /// <summary>The accessibility of the generated delegate declaration (the hand-written one's, when it differs).</summary>
    public string DelegateAccessibility { get; init; } = "private";

    /// <summary>The hand-written method the command calls instead of <see cref="Delegate"/> (a method named like the command method).</summary>
    public string? CallTarget { get; init; }

    /// <summary>The called method returns <c>void</c> (a hand-written non-partial method, or a <c>partial void</c>
    /// delegate): the command does not await it.</summary>
    public bool CallReturnsVoid { get; init; }

    /// <summary>
    /// The delegate's parameter type: <see cref="ParameterType"/> (the row the command receives), unless the
    /// hand-written delegate takes no parameter (null: the command drops the row) or the row as another written type
    /// (<c>Order?</c>, <c>object</c>).
    /// </summary>
    public string? DelegateParameterType { get; init; } = ParameterType;

    /// <summary>The hand-written delegate is <c>partial void</c>: the generated declaration returns <c>void</c> too.</summary>
    public bool DelegateReturnsVoid { get; init; }

    /// <summary><c>private partial Task OnSaveAsync()</c> / <c>private partial Task OnEditAsync(Order item)</c>.</summary>
    public string? DelegateSignature => Delegate is null
        ? null
        : $"{(DelegateAccessibility.Length > 0 ? DelegateAccessibility + " " : "")}partial {(DelegateReturnsVoid ? "void" : "Task")} {Delegate}"
          + (DelegateParameterType is null ? "()" : $"({DelegateParameterType} item)");
}

/// <summary>
/// What the compiler generates for one view, i.e. the contract the hand-written half must respect: the page's
/// constructors (<c>{View}.xaml.cs</c>) and the ViewModel's properties, commands and command delegates
/// (<c>{Vm}.g.cs</c>). Emitted as header comments in those files, exposed by <c>flagstaff check --format json</c> /
/// <c>flagstaff stubs</c>, and used by <c>flagstaff build</c> to explain C# errors against generated members.
/// <para>
/// With <see cref="UserDeclarations"/> (the CLI's scan of the hand-written C#), a member the hand-written half already
/// declares is not generated (<see cref="ContractProperty.DeclaredBy"/>, <see cref="ContractCommand.DeclaredBy"/>,
/// <see cref="UserConstructors"/>) and <see cref="Diagnostics"/> carries one FS2016 warning per skip; an implemented
/// <c>partial On{X}Async</c> no command declares is declared for it (<see cref="UndeclaredPartials"/>, FS2017).
/// </para>
/// </summary>
public sealed record ViewContract(
    string ViewName,
    string? ViewModelType,
    SourceLocation Location,
    IReadOnlyList<ContractProperty> Properties,
    IReadOnlyList<ContractCommand> Commands)
{
    /// <summary>The generated page code-behind: <c>{View}.xaml.cs</c>.</summary>
    public string PageFile => ViewName + ".xaml.cs";

    /// <summary>The generated ViewModel partial: <c>{Vm}.g.cs</c> (null without <c>bind vm:</c>).</summary>
    public string? ViewModelFile => ViewModelType is null ? null : ViewModelType + ".g.cs";

    /// <summary>Hand-written page constructors that replace generated ones: <c>{View}()</c> and <c>{View}({Vm})</c>.</summary>
    public IReadOnlyList<UserMember> UserConstructors { get; init; } = [];

    /// <summary>A hand-written page <c>ViewModel</c> property: the generated one is skipped.</summary>
    public UserMember? UserViewModelProperty { get; init; }

    /// <summary>The page partial overrides <c>OnNavigatedTo</c> (the generated InitializeComponent fallback cannot).</summary>
    public bool UserOverridesOnNavigatedTo { get; init; }

    /// <summary>The accessibility the hand-written <c>OnViewCreated</c> implementation uses (<c>""</c> = none, the default).</summary>
    public string OnViewCreatedAccessibility { get; init; } = "";

    /// <summary>Implemented <c>partial On{X}Async</c> methods of the ViewModel no command declares (declared for them, FS2017).</summary>
    public IReadOnlyList<UserMember> UndeclaredPartials { get; init; } = [];

    /// <summary>FS2016/FS2017 warnings about hand-written declarations (empty without <see cref="UserDeclarations"/>).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    public bool HasUserParameterlessConstructor => UserConstructors.Any(c => c.ParameterList.Count == 0);

    public bool HasUserViewModelConstructor => UserConstructors.Any(c => c.ParameterList.Count == 1);

    /// <summary>
    /// A hand-written page constructor does not call <c>InitializeComponent()</c>: the generated page calls it from
    /// <c>OnNavigatedTo</c> (unless the page partial overrides that too).
    /// </summary>
    public bool NeedsInitializeFallback => UserConstructors.Any(c => !c.CallsInitializeComponent) && !UserOverridesOnNavigatedTo;

    /// <summary>The page constructors the generated code-behind declares.</summary>
    public IReadOnlyList<string> Constructors
    {
        get
        {
            var all = new List<string>();
            if (!HasUserParameterlessConstructor)
                all.Add($"{ViewName}()");
            if (ViewModelType is not null && !HasUserViewModelConstructor)
                all.Add($"{ViewName}(ViewModels.{ViewModelType} viewModel)");
            return all;
        }
    }

    /// <summary>The command delegates the hand-written ViewModel partial implements, in declaration order.</summary>
    public IEnumerable<ContractCommand> Delegates => Commands.Where(c => c.Delegate is not null && c.DeclaredBy is null);

    public ContractProperty? FindProperty(string name) =>
        Properties.FirstOrDefault(p => p.Name == name || p.FieldName == name || FieldNameOf(p.Name) == name);

    /// <summary><c>_openCount</c> for <c>OpenCount</c> (the CommunityToolkit field naming the generator uses).</summary>
    public static string FieldNameOf(string property) =>
        "_" + char.ToLowerInvariant(property[0]) + property[1..];

    private static readonly Regex GeneratedCancelCommand = new(@"\[RelayCommand\]\s*private\s+void\s+(?<name>\w+)\s*\(", RegexOptions.CultureInvariant);

    /// <summary>The <c>Cancel{Flag}</c> method name of a generated dialog/confirm cancel command, or null.</summary>
    public static string? CancelCommandMethod(string extraMember) =>
        GeneratedCancelCommand.Match(extraMember) is { Success: true } m ? m.Groups["name"].Value : null;

    /// <summary>
    /// Builds the contract from the view's inferred members (<see cref="TypeInference.InferMembers(ViewDecl)"/>) and
    /// the extra ViewModel members the XAML generator emitted (the <c>Cancel{Flag}</c> relay commands). With
    /// <paramref name="user"/>, members the hand-written partials (<c>{ns}.ViewModels.{Vm}</c>, <c>{ns}.{View}</c>)
    /// already declare are marked as theirs and reported (FS2016/FS2017).
    /// </summary>
    public static ViewContract For(ViewDecl view, IEnumerable<InferredMember> members, IEnumerable<string>? extraViewModelMembers = null,
        UserDeclarations? user = null, string? appNamespace = null)
    {
        var properties = new List<ContractProperty>();
        var commands = new List<ContractCommand>();
        var diagnostics = new List<Diagnostic>();
        var vmType = view.Binding?.ViewModelType;
        var vmUser = vmType is not null && user is not null && appNamespace is not null
            ? user.Members($"{appNamespace}.ViewModels.{vmType}")
            : [];
        var pageUser = user is not null && appNamespace is not null ? user.Members($"{appNamespace}.{view.Name}") : [];
        var undeclared = new List<UserMember>();
        // Delegates whose parameter list cannot match their command (FS2019): not also declared for them (FS2017).
        var mismatched = new HashSet<UserMember>(ReferenceEqualityComparer.Instance);

        if (vmType is not null)
        {
            foreach (var m in members)
            {
                if (m.Kind == InferredKind.Property)
                {
                    properties.Add(PlanProperty(m, vmUser, diagnostics));
                    continue;
                }
                var stem = m.Name.EndsWith("Command", StringComparison.Ordinal) && m.Name.Length > "Command".Length
                    ? m.Name[..^"Command".Length]
                    : m.Name;
                if (commands.Any(c => c.Stem == stem))
                    continue;
                commands.Add(PlanCommand(stem, m, vmUser, diagnostics, mismatched));
            }
            foreach (var extra in extraViewModelMembers ?? [])
            {
                if (CancelCommandMethod(extra) is { } stem && commands.All(c => c.Name != stem + "Command"))
                {
                    var declaredBy = vmUser.FirstOrDefault(u => u.ProvidedCommand == stem + "Command");
                    if (declaredBy is not null)
                    {
                        diagnostics.Add(Warn(declaredBy, $"'{stem}Command' is also declared in {declaredBy.File} ({declaredBy.Describe()}); the generator skipped it",
                            "delete it from your partial; the dialog/confirm -> cancel in the .flag already generates it"));
                    }
                    commands.Add(new ContractCommand(stem + "Command", stem, null, null, null, "dialog/confirm -> cancel", declaredBy));
                }
            }
            undeclared.AddRange(PlanUndeclaredPartials(commands, vmUser.Where(u => !mismatched.Contains(u)).ToList(), diagnostics));
        }

        var userCtors = pageUser
            .Where(u => u.Kind == UserMemberKind.Constructor
                        && (u.ParameterList.Count == 0
                            || vmType is not null && u.ParameterList.Count == 1 && IsViewModelType(u.ParameterList[0].Type, vmType)))
            .ToList();
        var userViewModel = vmType is null ? null : pageUser.FirstOrDefault(u => u.Kind == UserMemberKind.Property && u.Name == "ViewModel");
        var overridesNavigatedTo = pageUser.Any(u => u.Kind == UserMemberKind.Method && u.Name == "OnNavigatedTo" && u.HasBody);
        var onViewCreated = pageUser.FirstOrDefault(u => u.Kind == UserMemberKind.Method && u.Name == "OnViewCreated" && u.IsPartial && u.HasBody);

        foreach (var ctor in userCtors)
        {
            var signature = $"{view.Name}({(ctor.ParameterList.Count == 0 ? "" : vmType)})";
            var init = ctor.CallsInitializeComponent
                ? ""
                : overridesNavigatedTo
                    ? "; it does not call InitializeComponent(), so the page stays empty: call it first"
                    : "; it does not call InitializeComponent(), so the generated OnNavigatedTo does";
            diagnostics.Add(Warn(ctor, $"'{signature}' is also declared in {ctor.File}; the generator skipped its own constructor{init}",
                "move the body into 'partial void OnViewCreated() { ... }' (runs once, after InitializeComponent and the ViewModel wiring) and delete the constructor"));
        }
        if (userViewModel is not null)
        {
            diagnostics.Add(Warn(userViewModel, $"'ViewModel' is also declared in {userViewModel.File}; the generator skipped it",
                $"delete it from your partial; the generated page declares 'public ViewModels.{vmType} ViewModel' and sets it in its constructor"));
        }

        return new ViewContract(view.Name, vmType, view.Location, properties, commands)
        {
            UserConstructors = userCtors,
            UserViewModelProperty = userViewModel,
            UserOverridesOnNavigatedTo = overridesNavigatedTo,
            OnViewCreatedAccessibility = onViewCreated?.Accessibility ?? "",
            UndeclaredPartials = undeclared,
            Diagnostics = diagnostics,
        };
    }

    private static bool IsViewModelType(string written, string vmType)
    {
        var t = written.Replace("global::", "", StringComparison.Ordinal);
        return t == vmType || t.EndsWith("." + vmType, StringComparison.Ordinal);
    }

    private static Diagnostic Warn(UserMember at, string message, string hint, string code = "FS2016") =>
        new(DiagnosticSeverity.Warning, code, message, at.Location, hint);

    /// <summary><c>stat "Open" vm.OpenCount (dashboard.flag line 6)</c>.</summary>
    internal static string Usage(SourceLocation? location, string? origin) =>
        (origin ?? "the .flag file") + (location is { } l ? $" ({l.FileName} line {l.Line})" : "");

    /// <summary>
    /// A hand-written member declared with another type than the .flag infers: FS2016 when that type works with the
    /// usage (the generator uses it, <see cref="DeclaredTypes"/>), FS2018 (error) when it cannot.
    /// </summary>
    /// <param name="member"><c>Billable</c> or <c>Entry.Billable</c>.</param>
    internal static Diagnostic TypeDiagnostic(string member, UserMember declaredBy, string inferred, TypeConflict? conflict, string usage)
    {
        var declared = DeclaredTypes.Written(declaredBy.Type ?? "");
        var inferredShort = ShortType(inferred);
        if (conflict is null)
        {
            return Warn(declaredBy,
                $"'{member}' is also declared in {declaredBy.File} as {declared}, but the .flag infers {inferredShort}; the generator skipped it and uses your {declared}",
                $"fine if {declared} is what you want (generated conditions and bindings use it); delete yours to get the inferred {inferredShort} (from {usage})");
        }
        return new Diagnostic(DiagnosticSeverity.Error, "FS2018",
            $"'{member}' is declared in {declaredBy.File} as {declared}, but {usage} needs {conflict.Need}; the .flag alone infers {inferredShort}",
            declaredBy.Location,
            $"declare it as {ShortType(conflict.Suggest)}, or delete yours (the .flag then generates {inferredShort})"
            + (conflict.AlternativeFix is { } alternative ? $", or {alternative}" : ""));
    }

    private static ContractProperty PlanProperty(InferredMember m, IReadOnlyList<UserMember> vmUser, List<Diagnostic> diagnostics)
    {
        var declaredBy = vmUser.FirstOrDefault(u => u.ProvidedProperty == m.Name);
        if (declaredBy is not null)
        {
            var inferredType = m.InferredType ?? m.CSharpType;
            if (UserDeclarations.SameType(declaredBy.Type, inferredType))
            {
                diagnostics.Add(Warn(declaredBy, $"'{m.Name}' is also declared in {declaredBy.File}; the generator skipped it",
                    $"delete it from your partial; the .flag already generates it from {Usage(m.Location, m.Origin)}"));
            }
            else
            {
                diagnostics.Add(TypeDiagnostic(m.Name, declaredBy, inferredType, DeclaredTypes.Check(declaredBy.Type!, m), Usage(m.Location, m.Origin)));
            }
            return new ContractProperty(m.Name, m.CSharpType, m.Confidence, m.Location, m.Origin, declaredBy,
                InferredType: m.IsDeclared ? m.InferredType : null);
        }

        // A plain field with the generated field's name (no [ObservableProperty]): generate the field under the other
        // name CommunityToolkit accepts (openCount → OpenCount), so the property still exists.
        var field = FieldNameOf(m.Name);
        var plain = vmUser.FirstOrDefault(u => u.Kind == UserMemberKind.Field && u.Name == field);
        if (plain is not null)
        {
            var alternative = char.ToLowerInvariant(m.Name[0]) + m.Name[1..];
            if (alternative != field && vmUser.All(u => u.Name != alternative))
            {
                diagnostics.Add(Warn(plain, $"'{field}' is a plain field in {plain.File} with the name of the generated [ObservableProperty] field; the generator named its field {alternative}",
                    $"delete your field and set the property {m.Name} (generated from {Usage(m.Location, m.Origin)}); writing the field skips change notification"));
                return new ContractProperty(m.Name, m.CSharpType, m.Confidence, m.Location, m.Origin, FieldOverride: alternative);
            }
        }
        return new ContractProperty(m.Name, m.CSharpType, m.Confidence, m.Location, m.Origin);
    }

    private static bool ParametersMatch(UserMember u, string? parameterType) =>
        parameterType is null
            ? u.ParameterList.Count == 0
            : u.ParameterList.Count == 1 && UserDeclarations.SameType(u.ParameterList[0].Type, parameterType);

    private static string Parameters(string? parameterType) => parameterType is null ? "" : $"{parameterType} item";

    /// <summary>A hand-written delegate parameter that can take the command's row: the row type (nullable or not) or <c>object</c>.</summary>
    private static bool AcceptsRow(string written, string row)
    {
        var w = UserDeclarations.NormalizeType(written).TrimEnd('?');
        return w == "object" || w == UserDeclarations.NormalizeType(row).TrimEnd('?');
    }

    private static string Signature(UserMember u) => $"{u.Name}({string.Join(", ", u.ParameterList.Select(p => p.Type))})";

    /// <param name="mismatched">Receives hand-written delegates whose parameters cannot match the command (FS2019).</param>
    private static ContractCommand PlanCommand(string stem, InferredMember m, IReadOnlyList<UserMember> vmUser, List<Diagnostic> diagnostics,
        HashSet<UserMember> mismatched)
    {
        var name = stem + "Command";
        var delegateName = $"On{stem}Async";
        var convention = $"private partial async Task {delegateName}({Parameters(m.ParameterType)}) {{ ... }}";

        // 1. The hand-written half provides the command itself: generate neither the command nor its delegate.
        var userCommand = vmUser.FirstOrDefault(u => u.ProvidedCommand == name);
        if (userCommand is not null)
        {
            diagnostics.Add(Warn(userCommand, $"'{name}' is also declared in {userCommand.File} ({userCommand.Describe()}); the generator skipped it and {delegateName}",
                $"delete it from your partial and implement {convention}; the .flag already generates {name} from {Usage(m.Location, m.Origin)}"));
            return new ContractCommand(name, stem + "Async", delegateName, m.ParameterType, m.Location, m.Origin, userCommand);
        }

        // 2. The delegate as the hand-written half declares it.
        var userDelegates = vmUser.Where(u => u.Kind == UserMemberKind.Method && u.Name == delegateName && ParametersMatch(u, m.ParameterType)).ToList();
        var implementation = userDelegates.FirstOrDefault(u => u.HasBody);
        var definition = userDelegates.FirstOrDefault(u => u.IsPartial && !u.HasBody);
        var mode = DelegateMode.Partial;
        var accessibility = "private";
        string? callTarget = null;
        var returnsVoid = false;
        var delegateParameter = m.ParameterType;
        var delegateVoid = false;

        // 2a. Only a delegate with another parameter list (a near miss). The command passes the row or nothing, so a
        // delegate without parameters (the row is dropped) or taking the row as T?/object works; anything else cannot
        // be called and is FS2019 instead of a second declaration that fails with CS8795.
        if (implementation is null && definition is null
            && vmUser.FirstOrDefault(u => u.Kind == UserMemberKind.Method && u.Name == delegateName && u.HasBody) is { } nearMiss)
        {
            var count = nearMiss.ParameterList.Count;
            if (m.ParameterType is not null && (count == 0 || count == 1 && AcceptsRow(nearMiss.ParameterList[0].Type, m.ParameterType)))
            {
                implementation = nearMiss;
                delegateParameter = count == 0 ? null : nearMiss.ParameterList[0].Type;
                diagnostics.Add(count == 0
                    ? Warn(nearMiss, $"'{delegateName}()' in {nearMiss.File} takes no parameter, but {Usage(m.Location, m.Origin)} passes the {m.ParameterType} row; "
                                     + $"the generator declared it without one and {name} drops the row",
                        $"to use the row, implement {convention}")
                    : Warn(nearMiss, $"'{Signature(nearMiss)}' in {nearMiss.File} takes the {m.ParameterType} row as {nearMiss.ParameterList[0].Type}; the generator declared it so",
                        $"fine as is; the convention is {convention}"));
            }
            else
            {
                foreach (var u in vmUser.Where(u => u.Kind == UserMemberKind.Method && u.Name == delegateName))
                    mismatched.Add(u);
                var generated = $"private partial Task {delegateName}({Parameters(m.ParameterType)})";
                var hint = m.ParameterType is not null
                    ? $"implement {convention}; the command passes the {m.ParameterType} row"
                    : count == 1
                        ? $"to receive the row, call it from inside a table or list of {nearMiss.ParameterList[0].Type} with -> vm.{stem}(item) "
                          + $"(a row action button, a ContextFlyout: item, or the table's own ->) and implement {delegateName}({nearMiss.ParameterList[0].Type} item); "
                          + $"otherwise implement {convention}"
                        : $"implement {convention}; a command passes at most the row (-> vm.{stem}(item) in a table/list)";
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "FS2019",
                    $"'{Signature(nearMiss)}' in {nearMiss.File} does not match its command: {Usage(m.Location, m.Origin)} declares '{generated}'"
                    + (m.ParameterType is null ? " (no row is passed)" : ""),
                    nearMiss.Location, hint));
            }
        }

        if (implementation is { IsPartial: false })
        {
            mode = DelegateMode.UserMethod;
            returnsVoid = implementation.Type == "void";
            diagnostics.Add(Warn(implementation, $"'{delegateName}' in {implementation.File} is not partial; the generator skipped its declaration and {name} calls yours",
                $"fine as is; the convention is {convention}"));
        }
        else if (definition is not null)
        {
            mode = DelegateMode.UserDeclared;
            diagnostics.Add(Warn(definition, $"'{delegateName}' is declared without a body in {definition.File}; the generator skipped its own declaration",
                $"delete that declaration and implement {convention} once, with a body"));
        }
        else if (implementation is { IsPartial: true, Type: "void" })
        {
            // partial void OnXAsync() (CS8817 against a Task declaration): declare it void, with its accessibility, and call it.
            delegateVoid = true;
            returnsVoid = true;
            accessibility = implementation.Accessibility;
            diagnostics.Add(Warn(implementation, $"'{delegateName}' is 'partial void' in {implementation.File}; the generator declared it void to match and {name} does not await it",
                $"prefer {convention}: a Task lets {name} await it and report failures"));
        }
        else if (implementation is { IsPartial: true } && implementation.Accessibility.Length > 0 && implementation.Accessibility != "private")
        {
            accessibility = implementation.Accessibility;
            diagnostics.Add(Warn(implementation, $"'{delegateName}' is '{accessibility} partial' in {implementation.File}; the generator declared it {accessibility} to match",
                $"the generated declaration is private by convention: {convention}"));
        }

        // 3. A hand-written method with the generated command method's name (SaveAsync): name ours Save (SaveCommand
        // either way); without any delegate in the hand-written half, the command calls that method.
        var method = stem + "Async";
        var collision = vmUser.FirstOrDefault(u => u.Kind == UserMemberKind.Method && !u.IsRelayCommand && u.Name == method
                                                   && u.ParameterList.Count == (m.ParameterType is null ? 0 : 1));
        if (collision is not null)
        {
            method = stem;
            if (implementation is null && definition is null)
            {
                mode = DelegateMode.UserMethod;
                callTarget = collision.Name;
                returnsVoid = collision.Type == "void";
                diagnostics.Add(Warn(collision, $"'{collision.Name}' in {collision.File} has the name of the generated command method; the generator named its method {stem} and {name} calls yours",
                    $"fine as is; the convention is {convention}"));
            }
            else
            {
                diagnostics.Add(Warn(collision, $"'{collision.Name}' in {collision.File} has the name of the generated command method; the generator named its method {stem} ({name} is unchanged)",
                    $"rename yours or delete it; {name} calls {delegateName}"));
            }
        }

        return new ContractCommand(name, method, delegateName, m.ParameterType, m.Location, m.Origin)
        {
            DelegateMode = mode,
            DelegateAccessibility = accessibility,
            CallTarget = callTarget,
            CallReturnsVoid = returnsVoid,
            DelegateParameterType = delegateParameter,
            DelegateReturnsVoid = delegateVoid,
        };
    }

    /// <summary>
    /// Implemented <c>partial On{X}Async</c> methods with no declaration anywhere (CS0759 otherwise): the generator
    /// declares them with the hand-written signature, FS2017.
    /// </summary>
    private static List<UserMember> PlanUndeclaredPartials(List<ContractCommand> commands, IReadOnlyList<UserMember> vmUser, List<Diagnostic> diagnostics)
    {
        var result = new List<UserMember>();
        foreach (var u in vmUser)
        {
            if (u.Kind != UserMemberKind.Method || !u.IsPartial || !u.HasBody || u.Name.Length <= "OnAsync".Length
                || !u.Name.StartsWith("On", StringComparison.Ordinal) || !u.Name.EndsWith("Async", StringComparison.Ordinal))
                continue;
            if (commands.Any(c => c.Delegate == u.Name && c.DeclaredBy is null && c.DelegateMode == DelegateMode.Partial && ParametersMatch(u, c.DelegateParameterType)))
                continue;
            var types = u.ParameterList.Select(p => UserDeclarations.NormalizeType(p.Type)).ToList();
            if (vmUser.Any(d => d.Kind == UserMemberKind.Method && d.IsPartial && !d.HasBody && d.Name == u.Name
                                && d.ParameterList.Select(p => UserDeclarations.NormalizeType(p.Type)).SequenceEqual(types)))
                continue;
            if (result.Any(r => r.Name == u.Name && r.ParameterList.Select(p => UserDeclarations.NormalizeType(p.Type)).SequenceEqual(types)))
                continue;

            var signature = $"{u.Name}({string.Join(", ", u.ParameterList.Select(p => p.Type))})";
            var generated = commands.FirstOrDefault(c => c.Delegate == u.Name && c.DeclaredBy is null);
            var stem = u.Name[2..^"Async".Length];
            var hint = generated is not null
                ? $"the .flag declares '{generated.DelegateSignature}' (from {Usage(generated.Location, generated.Origin)}); implement that signature instead"
                : stem is "Load" or "Init" or "Initialize"
                    ? $"nothing calls it: run it on page load with 'onload -> vm.{stem};' in the view, or drop 'partial'"
                    : $"nothing calls it: bind it in the .flag (button \"...\" -> vm.{stem};) or drop 'partial'";
            diagnostics.Add(Warn(u, $"'{signature}' is implemented as a partial method in {u.File}, but no .flag command declares it; the generator declared it so the build succeeds",
                hint, "FS2017"));
            result.Add(u);
        }
        return result;
    }

    /// <summary>The comment block at the top of <c>{Vm}.g.cs</c> (lines without newline, each starting with <c>//</c>).</summary>
    public List<string> ViewModelHeader()
    {
        var lines = new List<string>
        {
            $"// Contract (from {Location.FileName}): your own 'partial class {ViewModelType}' must NOT declare these again.",
        };
        if (Properties.Count > 0)
        {
            lines.Add($"//   [ObservableProperty] properties (set {Properties[0].Name}, not the field {Properties[0].FieldName}):");
            foreach (var p in Properties)
            {
                var decl = $"{ShortType(p.CSharpType)} {p.Name}";
                var line = p.Origin is null ? $"//     {decl}" : $"//     {decl}  <- {p.Origin}";
                lines.Add(p.DeclaredBy is null ? line : $"{line}  (declared by you in {p.DeclaredBy.File}; not generated)");
            }
        }
        if (Commands.Count > 0)
        {
            var list = Commands.Select(c => (c.ParameterType is null ? c.Name : $"{c.Name}({c.ParameterType})")
                                            + (c.DeclaredBy is null ? "" : $" (declared by you in {c.DeclaredBy.File}; not generated)"));
            lines.Add($"//   Commands: {string.Join(", ", list)}");
        }
        var delegates = Delegates.Where(d => d.DelegateMode != DelegateMode.UserMethod).ToList();
        if (delegates.Count > 0)
        {
            lines.Add("//   Implement exactly these in your partial ('flagstaff stubs --update' adds them; no others are declared):");
            foreach (var d in delegates)
                lines.Add($"//     {d.DelegateSignature};" + (d.DelegateMode == DelegateMode.UserDeclared ? "  (declared by you)" : ""));
        }
        if (UndeclaredPartials.Count > 0)
        {
            lines.Add("//   Also declared, for partial methods of yours no .flag command uses (nothing calls them): "
                      + string.Join(", ", UndeclaredPartials.Select(u => u.Name)));
        }
        return lines;
    }

    /// <summary><c>ObservableCollection&lt;Order&gt;</c> for <c>System.Collections.ObjectModel.ObservableCollection&lt;Order&gt;</c>.</summary>
    public static string ShortType(string type) =>
        type.Replace("System.Collections.ObjectModel.", "", StringComparison.Ordinal);

    /// <summary>The comment block at the top of <c>{View}.xaml.cs</c>.</summary>
    public List<string> PageHeader()
    {
        var declares = new List<string>();
        var ctors = Constructors.Select(c => c.Replace(" viewModel", "", StringComparison.Ordinal)).ToList();
        if (ctors.Count > 0)
            declares.Add($"the constructor{(ctors.Count > 1 ? "s" : "")} {string.Join(" and ", ctors)}");
        if (ViewModelType is not null && UserViewModelProperty is null)
            declares.Add("the ViewModel property");
        declares.Add("'partial void OnViewCreated()'");
        var lines = new List<string>
        {
            "// Contract: this partial declares "
            + (declares.Count == 1 ? declares[0] : $"{string.Join(", ", declares.Take(declares.Count - 1))} and {declares[^1]}") + ".",
        };
        if (UserConstructors.Count > 0)
        {
            lines.Add($"// Your constructor in {UserConstructors[0].File} replaces the generated one"
                      + (NeedsInitializeFallback ? " (InitializeComponent runs from OnNavigatedTo)" : "") + ";");
            lines.Add("// prefer moving its body into OnViewCreated() and deleting it.");
        }
        else
        {
            lines.Add($"// Never write a constructor in your 'partial class {ViewName}': put page setup in 'partial void OnViewCreated() {{ ... }}',");
            lines.Add("// which runs once at the end of the generated constructor (after InitializeComponent and the ViewModel wiring).");
        }
        return lines;
    }
}

/// <summary>An item type (<c>of T</c>) the compiler generates as <c>public partial class T</c> in <c>T.g.cs</c>.</summary>
/// <param name="DeclaredByUser">Bound properties the hand-written partial already declares (not generated, FS2016).</param>
public sealed record ItemTypeContract(string TypeName, IReadOnlyList<ItemTypeProperty> Properties, IReadOnlyList<string>? DeclaredByUser = null)
{
    public string File => TypeName + ".g.cs";
}
