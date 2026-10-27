namespace Flagstaff.Compiler;

/// <summary>One .flag input: its path (used in diagnostics) and its text.</summary>
public sealed record SourceFile(string Path, string Content);

/// <summary>
/// Top-level compiler entry point. Pipeline per file:
/// lex → parse → <see cref="SemanticValidator"/> → (stop on errors) → type inference →
/// <see cref="ConditionCompiler"/> → XAML / code-behind / ViewModel generation → XAML lint.
/// <see cref="CompileAll"/> runs that for every file (app files first), then the app scaffold.
/// </summary>
public sealed class FlagstaffCompiler
{
    public string AppNamespace { get; set; } = "FlagstaffApp";
    public bool NamespaceIsOverridden { get; set; }
    public string? StylesSource { get; set; }

    /// <summary>
    /// Report FS3011 for <c>xaml { }</c> blocks expressible as generic elements (default true; set false to keep
    /// library output free of the nudge). <see cref="CompilationResult.Stats"/> is computed either way.
    /// </summary>
    public bool ReportExpressibleEscapes { get; set; } = true;

    /// <summary>
    /// Members the hand-written C# partials already declare (the CLI fills this from a syntax scan of the project).
    /// Generated members they declare are skipped with warning FS2016; implemented <c>partial On{X}Async</c> methods
    /// no command declares get a declaration (FS2017). Default: none.
    /// </summary>
    public UserDeclarations UserDeclarations { get; set; } = UserDeclarations.Empty;

    /// <summary>
    /// Emit <c>GlobalUsings.g.cs</c> (with the app scaffold): <c>global using</c> for the WinUI, CommunityToolkit and
    /// app ViewModels namespaces hand-written partials keep needing (default true).
    /// </summary>
    public bool EmitGlobalUsings { get; set; } = true;

    public AppDecl? AppDeclaration { get; private set; }
    public List<string> ViewModelTypes { get; } = [];

    /// <summary>Item types (<c>of T</c>) from every compiled view; emitted once by <see cref="GenerateAppScaffold"/>.</summary>
    public ItemTypeRegistry ItemTypes { get; } = new();

    /// <summary>
    /// Compiles a whole project: app files (named <c>app.flag</c> or containing an <c>app</c>
    /// declaration) first, then the remaining files sorted by path, then the App scaffold and
    /// converters. Returns one combined result.
    /// </summary>
    public CompilationResult CompileAll(IEnumerable<SourceFile> files)
    {
        var combined = new CompilationResult();
        var parsed = new List<(SourceFile Source, FlagFile? Ast, List<Diagnostic> ParseDiagnostics, bool IsApp)>();

        foreach (var source in files)
        {
            var parseDiagnostics = new List<Diagnostic>();
            var ast = ParseFile(source.Content, source.Path, parseDiagnostics);
            var isApp = System.IO.Path.GetFileName(source.Path).Equals("app.flag", StringComparison.OrdinalIgnoreCase)
                        || (ast?.Declarations.OfType<AppDecl>().Any() ?? false);
            parsed.Add((source, ast, parseDiagnostics, isApp));
        }

        var ordered = parsed
            .OrderBy(p => p.IsApp ? 0 : 1)
            .ThenBy(p => p.Source.Path, StringComparer.Ordinal);

        foreach (var (source, ast, parseDiagnostics, _) in ordered)
        {
            var fileResult = new CompilationResult();
            fileResult.Diagnostics.AddRange(parseDiagnostics);
            if (ast is not null)
                CompileParsed(ast, fileResult);
            CollectStats(source.Path, source.Content, ast, fileResult);
            SortSourceDiagnostics(fileResult, source.Path);
            Merge(combined, fileResult);
        }

        Merge(combined, GenerateAppScaffold());
        return combined;
    }

    /// <summary>Compiles one .flag source. Lex/parse errors are returned as diagnostics (FS1050/FS1051).</summary>
    public CompilationResult Compile(string source, string fileName = "<input>")
    {
        var result = new CompilationResult();
        var file = ParseFile(source, fileName, result.Diagnostics);
        if (file is not null)
            CompileParsed(file, result);
        CollectStats(fileName, source, file, result);
        SortSourceDiagnostics(result, fileName);
        return result;
    }

    private void CollectStats(string path, string content, FlagFile? ast, CompilationResult result)
    {
        var stats = CompilationStats.ForFile(path, content, ast);
        result.Stats.Merge(stats);
        if (ReportExpressibleEscapes)
            result.Diagnostics.AddRange(XamlLinter.LintEscapeBlocks(stats.EscapeBlocksDetail));
    }

    public CompilationResult GenerateAppScaffold()
    {
        var results = new CompilationResult();

        if (AppDeclaration is not null)
        {
            results.Files.Add(AppScaffoldGenerator.GenerateAppXaml(
                AppDeclaration, AppNamespace, StylesSource));
            results.Files.Add(AppScaffoldGenerator.GenerateAppXamlCs(
                AppNamespace, ViewModelTypes));
        }

        results.Files.AddRange(ConverterGenerator.GenerateAll(AppNamespace));
        results.Files.AddRange(ItemTypes.Generate(AppNamespace, UserDeclarations, results.Diagnostics));
        results.ItemTypes.AddRange(ItemTypes.Types.Select(t => new ItemTypeContract(t.TypeName, ItemTypes.ContractProperties(t.TypeName, AppNamespace, UserDeclarations),
            ItemTypes.DeclaredByUser(t.TypeName, AppNamespace, UserDeclarations))));
        if (AppDeclaration is not null && EmitGlobalUsings)
            results.Files.Add(AppScaffoldGenerator.GenerateGlobalUsings(AppNamespace, ViewModelTypes.Count > 0 || ItemTypes.Types.Count > 0));

        Lint(results);
        return results;
    }

    // ── Pipeline ───────────────────────────────────────────────────

    private static FlagFile? ParseFile(string source, string fileName, List<Diagnostic> diagnostics)
    {
        Parser? parser = null;
        try
        {
            var tokens = new Lexer(source, fileName).Tokenize(tolerant: true);
            parser = new Parser(tokens);
            var file = parser.Parse();
            diagnostics.AddRange(parser.Diagnostics);
            return file;
        }
        catch (FlagstaffParseException ex)
        {
            if (parser is not null)
                diagnostics.AddRange(parser.Diagnostics);
            diagnostics.Add(ex.ToDiagnostic());
            return null;
        }
    }

    private void CompileParsed(FlagFile file, CompilationResult results)
    {
        results.Diagnostics.AddRange(SemanticValidator.Validate(file));
        if (results.HasErrors)
            return;

        foreach (var decl in file.Declarations)
        {
            switch (decl)
            {
                case AppDecl app:
                    AppDeclaration = app;
                    if (!NamespaceIsOverridden)
                        AppNamespace = app.Name;
                    var xamlGen = new XamlGenerator { StylesSource = StylesSource, Uses = file.Uses };
                    results.Files.Add(new OutputFile(
                        "MainWindow.xaml",
                        xamlGen.GenerateApp(app, AppNamespace)));
                    results.Files.Add(new OutputFile(
                        "MainWindow.xaml.cs",
                        CodeBehindGenerator.GenerateAppCodeBehind(app, AppNamespace)));
                    break;

                case ViewDecl view:
                    CompileView(view, results, file.Uses);
                    break;
            }
        }

        Lint(results);
    }

    private void CompileView(ViewDecl view, CompilationResult results, IReadOnlyList<UseDecl> uses)
    {
        if (view.Binding is not null)
            ViewModelTypes.Add(view.Binding.ViewModelType);

        var members = TypeInference.InferMembers(view, results.Diagnostics);
        if (view.Binding is not null && !UserDeclarations.IsEmpty)
            members = DeclaredTypes.Apply(members, UserDeclarations.Members($"{AppNamespace}.ViewModels.{view.Binding.ViewModelType}"));
        ItemTypes.Collect(view, results.Diagnostics);
        var conditions = new ConditionCompiler(view, members, DeclaredItemPropertyType);

        var viewXamlGen = new XamlGenerator { Uses = uses };
        results.Files.Add(new OutputFile(
            $"{view.Name}.xaml",
            viewXamlGen.GenerateView(view, AppNamespace, conditions)));
        var contract = ViewContract.For(view, members, viewXamlGen.ExtraViewModelMembers, UserDeclarations, AppNamespace);
        results.Files.Add(new OutputFile(
            $"{view.Name}.xaml.cs",
            CodeBehindGenerator.GenerateViewCodeBehind(view, AppNamespace, conditions, viewXamlGen.ExtraCodeBehindMembers, contract)));
        results.Diagnostics.AddRange(conditions.Diagnostics);
        results.Diagnostics.AddRange(viewXamlGen.Diagnostics);
        results.Diagnostics.AddRange(contract.Diagnostics);

        results.Contracts.Add(contract);
        var vmSource = ViewModelGenerator.Generate(view, AppNamespace, viewXamlGen.ExtraViewModelMembers, contract);
        if (!string.IsNullOrEmpty(vmSource))
        {
            var vmTypeName = view.Binding!.ViewModelType;
            results.Files.Add(new OutputFile(
                $"{vmTypeName}.g.cs",
                vmSource));
        }
    }

    /// <summary>The hand-written type of item property <paramref name="property"/> of <paramref name="itemType"/>, or null.</summary>
    private string? DeclaredItemPropertyType(string itemType, string property) =>
        UserDeclarations.Members($"{AppNamespace}.ViewModels.{itemType}").FirstOrDefault(u => u.ProvidedProperty == property)?.Type is { Length: > 0 } type
            ? DeclaredTypes.Written(type)
            : null;

    private void Lint(CompilationResult results)
    {
        foreach (var outputFile in results.Files)
        {
            if (outputFile.Name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                results.Diagnostics.AddRange(XamlLinter.Lint(outputFile.Name, outputFile.Content, AppNamespace));
        }
    }

    /// <summary>Orders diagnostics in the .flag source by position (stable), ahead of lint diagnostics on generated files.</summary>
    private static void SortSourceDiagnostics(CompilationResult result, string path)
    {
        var sorted = result.Diagnostics
            .Select((d, i) => (Diagnostic: d, Index: i))
            .OrderBy(x => x.Diagnostic.Location.File == path ? 0 : 1)
            .ThenBy(x => x.Diagnostic.Location.File == path ? x.Diagnostic.Location.Line : 0)
            .ThenBy(x => x.Diagnostic.Location.File == path ? x.Diagnostic.Location.Column : 0)
            .ThenBy(x => x.Index)
            .Select(x => x.Diagnostic)
            .ToList();
        result.Diagnostics.Clear();
        result.Diagnostics.AddRange(sorted);
    }

    private static void Merge(CompilationResult into, CompilationResult from)
    {
        foreach (var file in from.Files)
        {
            // Defensive: a file name produced twice keeps the first (item types are emitted once by the scaffold).
            if (!into.Files.Any(f => f.Name == file.Name))
                into.Files.Add(file);
        }
        into.Diagnostics.AddRange(from.Diagnostics);
        into.Contracts.AddRange(from.Contracts);
        foreach (var itemType in from.ItemTypes)
        {
            if (into.ItemTypes.All(t => t.TypeName != itemType.TypeName))
                into.ItemTypes.Add(itemType);
        }
        into.Stats.Merge(from.Stats);
    }
}

public sealed class CompilationResult
{
    public List<OutputFile> Files { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];

    /// <summary>One <see cref="ViewContract"/> per compiled view (what the generated page and ViewModel declare).</summary>
    public List<ViewContract> Contracts { get; } = [];

    /// <summary>The generated item types (<c>of T</c>), each a <c>public partial class</c> in <c>T.g.cs</c>.</summary>
    public List<ItemTypeContract> ItemTypes { get; } = [];

    /// <summary>Escape ratio, generic ratio and the <c>xaml { }</c> block list (see <see cref="CompilationStats"/>).</summary>
    public CompilationStats Stats { get; } = new();

    /// <summary>True when any diagnostic has <see cref="DiagnosticSeverity.Error"/>.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

public sealed record OutputFile(string Name, string Content);

/// <summary>
/// A compiler message. Codes: FS1000–1099 lexer/parser/schema, FS2000–2099 semantic/type,
/// FS3000–3099 XAML lint. See src/Flagstaff.Compiler/README.md for the full table.
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceLocation Location,
    string? Hint = null)
{
    /// <summary>Canonical MSBuild format: <c>path(line,col): error FS1001: message [hint: ...]</c>.</summary>
    public string ToMsBuildString()
    {
        var category = Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info",
        };
        var hint = Hint is null ? "" : $" [hint: {Hint}]";
        return $"{Location.File}({Location.Line},{Location.Column}): {category} {Code}: {Message}{hint}";
    }

    public override string ToString() => ToMsBuildString();
}

public enum DiagnosticSeverity { Info, Warning, Error }
