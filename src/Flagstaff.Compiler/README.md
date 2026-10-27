# Flagstaff.Compiler — internals

This is the contributor guide for the compiler. The language itself is described in `LANG.md`.

## Pipeline

`FlagstaffCompiler.CompileAll(IEnumerable<SourceFile>)` is the entry point (the CLI uses it):

1. **Parse every file**: `Lexer.Tokenize(tolerant: true)` → `Parser.Parse()`. Unexpected characters become
   `Invalid` tokens and are reported as FS1050 where the parser meets them. A hard parse error stops that file
   (FS1051). The parser also records recoverable errors in `Parser.Diagnostics`: unknown keywords (FS1000, skipped to
   the next `;` or past a balanced `{ }`) and bad command arguments (FS1052).
2. **Order**: app files first (named `app.flag` or containing an `app` declaration), then the other files sorted by
   path (ordinal).
3. **Per file**: `SemanticValidator.Validate` (schema check, FS1000–FS1005, FS2006). **If the file has any error, code
   generation is skipped for that file**; the other files still compile.
4. **Per view**: `TypeInference.InferMembers(view, diagnostics)` (FS2004, FS2007) → `new ConditionCompiler(view, members)`
   → `XamlGenerator.GenerateView(view, ns, conditions)` → `CodeBehindGenerator.GenerateViewCodeBehind(view, ns, conditions)`
   (FS2003, FS2005 are collected from `conditions.Diagnostics`) → `ViewModelGenerator.Generate`. Before generation,
   `ItemTypes.Collect(view, diagnostics)` feeds the project-wide item type registry (see below).
5. **Lint** every generated `.xaml` (FS30xx).
6. **Scaffold**: `GenerateAppScaffold()` (App.xaml, App.xaml.cs, converters, one `T.g.cs` per item type,
   `GlobalUsings.g.cs`) + lint.

The result is one `CompilationResult { Files, Diagnostics, HasErrors }`. Diagnostics in a source file are sorted by
position. `Compile(source, fileName)` and `GenerateAppScaffold()` also work individually (used by tests).

```csharp
public sealed record SourceFile(string Path, string Content);
public CompilationResult CompileAll(IEnumerable<SourceFile> files);

public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message,
                                SourceLocation Location, string? Hint = null)
{
    public string ToMsBuildString(); // path(line,col): error FS1001: message [hint: ...]
}
```

`FlagstaffParseException` is what direct `Lexer`/`Parser` callers see; it carries `Code`, `Reason`, `Hint` and
`ToDiagnostic()`.

Every AST node that users write carries a `SourceLocation` (`Element`, `PropPair`, `ViewDecl`, `AppDecl`,
`NavLinkDecl`, `CondExpr`, `Operand`). An element's location is its keyword token; a prop's is its key (or the label
string / binding for `_label` / `_binding`).

## Element schema (`ElementSchema*.cs`)

The schema is the single source of truth for keywords. `Parser.ElementKeywords` is `ElementSchema.All.Keys`.

```csharp
public enum PropKind { String, Int, Float, Bool, Color, Binding, ItemBinding, Range, List, Ident, Width, Any }
public sealed record PropSpec(PropKind Kind, string[]? Enum = null, bool Bare = false);
public sealed record KeywordSchema(string Keyword, bool AllowsLabel, bool AllowsBinding, bool AllowsChildren,
    bool AllowsArrow, bool AllowsItemBinding, IReadOnlyDictionary<string, PropSpec> Props);

ElementSchema.All        // IReadOnlyDictionary<string, KeywordSchema>
ElementSchema.Universal  // EXTENSION POINT: props every keyword accepts (empty in core)
ElementSchema.Theme      // props of app { theme { } }
ElementSchema.TryGetProp(keyword, prop, out spec)   // keyword props, then Universal
```

- The positional `"label"` and `vm.X` / `item.X` binding are **flags** on `KeywordSchema`, not props (the parser stores
  them as `_label` / `_binding`).
- `format:`, `when(...)`, `enabled(...)`, `of T` and `->` are **clauses** handled by the parser, not props.
- **Arrow targets** (`Element.NavTarget`): `vm.XCommand` (or `vm.X`, normalized to `vm.XCommand` by
  `TypeInference.CommandPath`), a view name from `navigate(View)` / `-> View`, or the reserved `cancel`
  (`XamlGenerator.CancelTarget`): `button "Cancel" -> cancel;` inside a `dialog`/`confirm` binds the generated
  `Cancel{Flag}Command` of the nearest modal (FS1024 anywhere else or on a non-button). `button`, `filePicker` and
  `link` all go through `XamlGenerator.ActionAttrs`; without `->` they are FS1023 (warning).
- A bare word in prop position (`bold`) parses as `PropPair(name, BoolValue(true))`; only props with `Bare: true`
  accept it.

Core keywords live in `ElementSchema.Core.cs`. To add keywords, create your own partial file — no shared list to edit:

```csharp
namespace Flagstaff.Compiler;

public static partial class ElementSchema
{
    [SchemaRegistration]                   // runs after RegisterCore(), in method-name order
    private static void RegisterLists()
    {
        Register("list", label: false, binding: false, children: true,
                 props: new() { ["items"] = Bind, ["selected"] = Bind, ["wrap"] = Bare });
        Extend("table", new() { ["sortable"] = Bare });        // add props to an existing keyword
        Reshape("col", s => s with { AllowsChildren = true }); // change structural flags
        RegisterUniversal("margin", Width);                    // every keyword accepts margin:
    }
}
```

Shorthands: `Str, Int, Float, Bool, Bare, Color, Bind, ItemBind, IntRange, ListOfValues, Ident, Width, AnyValue,
OneOf("a", "b")`. `Register` throws on a duplicate keyword. Remember to add the keyword's case to the
`XamlGenerator.EmitElement` switch.

## Semantic validator (`SemanticValidator.cs`)

Runs after parsing, before code generation. Checks every element against the schema: unknown keyword, unknown prop,
prop of another keyword, enum value, value shape, and label/binding/children/arrow permission; a `button`/`filePicker`/
`link` without `->` (FS1023, warning) and `-> cancel` outside a dialog/confirm button (FS1024). Suggestions use
Levenshtein distance ≤ 2 (`SemanticValidator.Closest`). Also warns FS2006 for the legacy `onload -> vm.XAsync;`.
Before the schema walk it runs `GenericValidator` (manifest checks and the locality rule for generic elements and
pass-through keys; see "Generic elements" below).

## Conditions (`when(...)` / `enabled(...)`)

```
cond    := or
or      := and ("||" and)*
and     := unary ("&&" unary)*
unary   := "!" unary | primary
primary := "(" cond ")" | operand [cmpop operand]
operand := vm.X | item.X | int | float | "string" | true | false | null
```

AST: `CondExpr(Location)` → `PathCond(Path)`, `NotCond(Inner)`, `BinaryCond(Left, LogicalOp, Right)`,
`CompareCond(Operand Left, ComparisonOp, Operand Right)`; `Operand` → `PathOperand(Path)`, `LiteralOperand(PropValue)`.
Paths include their prefix (`"vm.Count"`, `"item.Status"`). `Element.When` and `Element.Enabled` are `CondExpr?`.
`ConditionCompiler.Print(cond)` prints a condition back in .flag syntax.

### ConditionCompiler API

One instance per view, built after type inference and passed to both the XAML and the code-behind generator.

```csharp
public sealed class ConditionCompiler
{
    public ConditionCompiler(ViewDecl view, IEnumerable<InferredMember> members);
    public string VisibilityAttr(Element el);   // "" or  Visibility="{x:Bind ...}"
    public string EnabledAttr(Element el);      // "" or  IsEnabled="{x:Bind ...}"
    public string? BareBoolPath(Element el);    // "ViewModel.X" for when(vm.X), else null (toast IsOpen, dialogs)
    public bool InItemTemplate { get; set; }    // set while emitting a DataTemplate over a row type
    public IReadOnlyList<CompiledCondition> Functions { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public static IReadOnlySet<string> BareBoolKeywords { get; } // toast, confirm, dialog
}

public enum ConditionReturn { Visibility, Bool }
public sealed record CompiledCondition(string Name, ConditionReturn Return,
    IReadOnlyList<(string Path, string CSharpType)> Parameters, string CSharpExpression,
    string SourceText, SourceLocation Location)
{
    public IReadOnlyList<string> ParameterNames { get; init; }
    public string BindArguments { get; }        // "ViewModel.Count, ViewModel.Status" / "Status"
    public string RenderMethod(string indent = "    ");
}
```

Emitters append the attribute strings to the root tag of the element:
`Line($"<Button Content=\"...\"{Conditions.VisibilityAttr(el)}{Conditions.EnabledAttr(el)} />")`.

- Trivial `when(vm.Flag)` → `Visibility="{x:Bind ViewModel.Flag, Mode=OneWay, Converter={StaticResource BoolToVisibility}}"`.
  When every path is `item.X` (row templates) the `Mode` is omitted (OneTime; see "Item bindings are OneTime").
- Trivial `enabled(vm.Flag)` → `IsEnabled="{x:Bind ViewModel.Flag, Mode=OneWay}"`.
- Anything else → a `public static` function on the Page: `When_n` (returns `Microsoft.UI.Xaml.Visibility`) or
  `Enabled_n` (returns `bool`), numbered per kind in document order (within one element, `when` before `enabled`):

  ```xml
  Visibility="{x:Bind local:InvoiceListView.When_1(ViewModel.Count, ViewModel.Status), Mode=OneWay}"
  ```
  ```csharp
  // when(vm.Count > 5 && vm.Status != "Closed") at invoices.flag:12
  public static Microsoft.UI.Xaml.Visibility When_1(int count, string status) => (count > 5 && status != "Closed") ? Visibility.Visible : Visibility.Collapsed;
  ```
- Parameter types come from type inference; item types named by `of T` are emitted as `ViewModels.T` (the Page lives
  in `{ns}`, the item types in `{ns}.ViewModels`), also inside generics (`ObservableCollection<ViewModels.T>`).
- Parameters are the distinct paths in document order; names are the camelCased last segment, deduplicated with
  numeric suffixes and `@`-escaped when they are C# keywords. `item.X` binds as `X`.
- Literals: ints `5`, floats `5.5`, `m` suffix when the other side is `decimal`, escaped C# strings, `true/false`,
  `null` (only for reference/nullable types).
- Functions are compiled lazily when an emitter asks for the attribute, so `InItemTemplate` reflects the emission
  context. Only conditions that were emitted end up in `Functions`. Names are assigned up front by the constructor in
  document order: view-level property elements (`Page.Resources:`, `Page.BottomAppBar:`), then the body, each element's
  property values (flyout items) before its children, so `When_n` inside a `Flyout:` is deterministic.
- Conditions do not use the `Converters` helpers (`IsPositive`, `IsZero`, ...); `Converters.g.cs` is generated for
  the `empty` state of tables/lists (`Converters.IsZero`).

## Type inference (`TypeInference.cs`)

```csharp
public enum InferenceConfidence { Default, Comparison, Explicit }
public sealed record InferredMember(string Name, InferredKind Kind, string CSharpType, string? DefaultValue = null,
    InferenceConfidence Confidence = InferenceConfidence.Default, string? ParameterType = null,
    SourceLocation? Location = null);
TypeInference.InferMembers(view)                       // no diagnostics
TypeInference.InferMembers(view, List<Diagnostic>?)    // FS2004, FS2007
TypeInference.OnLoadCommandStem("vm.LoadAsync")        // "Load"
```

A member keeps the type from its highest-confidence usage; on ties the first usage wins.

| Confidence | Sources |
|---|---|
| Explicit | `number` / `progress` (double), `checkbox`/`toggle`/`radio` (bool), `datepicker` (DateTimeOffset?), `format:` (currency→decimal, date→DateTimeOffset?, percent/number→double), `items:` (`ObservableCollection<T>`; `select` without `of` → `ObservableCollection<string>`), `selected:` (`T?`), `select` positional with `items:` (`T?`, `string?` without `of`), `select` with a range (int), commands |
| Comparison | the literal in a `CompareCond` (`int`, `double`, `string`, `bool`; `== null` → `string?`), and `bool` for a bare `when(vm.X)` |
| Default | `string` — textbox, textarea, text, badge, image, `stat` without `format:`, path-vs-path comparisons |

An Explicit type that is incompatible with a Comparison literal (an `int` property compared with `"x"`, a `bool`
compared with `1`, `null` against a non-nullable value type) is FS2004 at the comparison, naming the explicit usage's
location. Commands written `-> vm.EditCommand(item)` get `ParameterType` = the `of T` of the nearest enclosing
`table`/`list`; outside one (or without `of`) it is FS2007.

## ViewModel conventions (`ViewModelGenerator.cs`, `CodeBehindGenerator.cs`)

Every command delegates to an **extended partial method** that the developer implements in a hand-written partial:

```csharp
[RelayCommand] private async Task SaveAsync() { await OnSaveAsync(); }
private partial Task OnSaveAsync();

[RelayCommand] private async Task EditAsync(Order item) { await OnEditAsync(item); }   // -> vm.EditCommand(item)
private partial Task OnEditAsync(Order item);
```

`onload -> vm.Load;` generates `LoadAsync()`/`OnLoadAsync()` as above (so `LoadCommand`), with
`[RelayCommand(FlowExceptionsToTaskScheduler = true)]` so a failure only surfaces through the awaited task (CommunityToolkit
8.4 would otherwise also re-throw it on the UI thread and crash the app). The Page's `Loaded` handler runs
`await ViewModel.LoadCommand.ExecuteAsync(null);` inside a try/catch that `Debug.WriteLine`s the exception and, in
DEBUG builds, re-throws it (`#if DEBUG throw; #endif`) so developers see load errors instead of an empty page. The legacy
`onload -> vm.LoadAsync;` produces the same `LoadCommand` plus warning FS2006; it never calls a user `LoadAsync`.
Commands are de-duplicated by method name. `-> vm.Save` and `-> vm.SaveCommand` are the same command (`SaveCommand`).

### The contract (`Contract.cs`)

`ViewContract.For(view, members, extraViewModelMembers)` describes what the generator declares for one view: the page
constructors, the ViewModel properties (type, confidence, and `InferredMember.Origin`, the usage as written, e.g.
`stat "Open" vm.OpenCount`), the commands, and the `On{X}Async` delegates; `ItemTypeContract` the item types.
`FlagstaffCompiler` collects them in `CompilationResult.Contracts` / `ItemTypes`. They feed three consumers:

- header comments in `{Vm}.g.cs` (`ViewContract.ViewModelHeader`), `{View}.xaml.cs` (`PageHeader`) and `T.g.cs`;
- `flagstaff check --format json` / `flagstaff stubs` (`contract`, `itemTypes`; CLI `ContractOutput`);
- `flagstaff build` (CLI `ContractHints`): C# errors in the hand-written half against generated members (CS0102,
  CS0756, CS0111, CS0759, CS8795, CS0029/CS0266/CS0019/CS1503, CS0260, CS1061/CS0117, CS0103, CS0246, CS0104; also
  WMC0011 via `ControlManifest.SuggestProperties`) get a
  hint naming the member, the `.flag` line it comes from, and the fix. Matching is by code + message regex; type
  mismatches read the offending source line to find the generated property it names. Also CS8799 (delegate
  accessibility), CS0111 on generated page methods (`OnViewLoaded`), CS1061 from the XAML compiler's `.g.cs` (a
  handler named in `xaml { }`), CS1061 on `DateTimeOffset?`, and CS0260 on non-generated classes. CS8817 (a
  `partial void` delegate or toolkit hook), CS8799 and CS0759 on CommunityToolkit `On{Prop}Changed`/`Changing` hooks
  (no access modifier; the property's type), CS8795 (another parameter list does not implement the delegate); a
  type mismatch on a member the user declared says so. `WinUiTypeHints`: CS0246/CS0234/CS0103 on UWP-only types
  (`GenericValidator.UwpOnlyControl`, FS1030's table), the inking API, shapes (`using Microsoft.UI.Xaml.Shapes;`),
  `Windows.UI.Xaml.*`, and common types of the namespaces the global usings leave out; CS0115 on `OnPointer*`
  overrides in a non-Control subclass.

**Hand-written declarations (`UserDeclarations.cs`, FS2016/FS2017).** `FlagstaffCompiler.UserDeclarations` is a
Roslyn-free model of the members the hand-written partials declare, keyed by full type name: fields, properties,
methods (accessibility as written, `partial`, body or not, parameters, attributes) and constructors (whether they call
`InitializeComponent()`). The CLI fills it (`UserDeclarationScanner`: a syntax-only parse of the project's `.cs` files
outside `bin/`, `obj/`, `Generated/`) in `CompileCommand.CompileInMemory`, the one path behind `check`, `compile`,
`build`, `watch`, and the MSBuild target (`flagstaff compile`); `stubs` scans too. `ViewContract.For` plans the skips,
the generators follow the contract, and each skip is an FS2016 warning at the C# declaration
(`MSBuildWarningsNotAsErrors` in `Flagstaff.targets` keeps it a warning under `-warnaserror`):

| Hand-written | Generated |
|---|---|
| property, `[ObservableProperty]` field (`_x`/`x`/`m_x`), partial property `X` | no field; the header says "declared by you"; another type is used everywhere (below) |
| plain field named `_x` (no attribute) | field `x` instead (CommunityToolkit gives the same property) |
| `[RelayCommand]` method or `XCommand` property for a generated command | neither the command nor `OnXAsync` |
| `{access} partial Task OnXAsync(...)` with a body, access not `private` | `{access} partial Task OnXAsync(...);` |
| `{access} partial void OnXAsync(...)` with a body (access may be empty) | `{access} partial void OnXAsync(...);`; the command calls it without `await` |
| `partial Task OnXAsync(...);` (no body) | no declaration (theirs is the defining one) |
| non-partial `OnXAsync(...)` | no declaration; the command calls it (without `await` when it returns `void`) |
| only `OnXAsync()` for a row command (`-> vm.X(item)`) | `OnXAsync();`; the command takes the row and drops it |
| only `OnXAsync(T? item)` / `OnXAsync(object item)` for a row command of `T` | the declaration with their parameter type; the command passes the row |
| only an `OnXAsync` with another parameter list (a parameter where no row is passed, another row type, two or more) | FS2019 (error), naming the generated signature and `-> vm.X(item)`; no FS2017 |
| non-partial method `XAsync(...)` (the command method's name) | command method `X` (still `XCommand`); it calls `OnXAsync`, or `XAsync` when they wrote no delegate |
| `partial ... OnYAsync(...)` with a body and no declaration anywhere | its declaration, with their signature (FS2017) |
| page `View()` and/or `View(Vm)` | that constructor is not generated (see below) |
| page `ViewModel` property | no `ViewModel` property |
| item type property (`{ns}.ViewModels.T`) | not in `T.g.cs`; its type is used in row conditions and the contract |

**The hand-written type is authoritative (`DeclaredTypes.cs`, FS2018).** `TypeInference` records, with each
member's winning usage, the CLR type of the control property it binds (`InferredMember.Target`: `bool?` for a
checkbox's `IsChecked`, `double` for `Value`, `object` for `SelectedItem`/`ItemsSource`, the manifest type for a XAML
prop) and its `format:` (`InferredMember.Format`; `ItemTypeProperty` has both too). `FlagstaffCompiler.CompileView`
applies `DeclaredTypes.Apply` before conditions are compiled: a ViewModel property the user declares with another
type gets that type as `CSharpType` (`InferredType` keeps the `.flag`'s), so `When_n`/`Enabled_n` parameters, the
badge's `InfoBadge` choice (`int`), the contract (header, `check` JSON `type`/`inferredType`, `stubs`, build hints)
all use it. Row conditions get item property types from `ConditionCompiler`'s `itemPropertyType` lookup (row types
tracked per element while numbering, like `ItemTypeRegistry.Walk`); `ItemTypeRegistry.ContractProperties` types
the item contract. In conditions a declared `bool?` is emitted as `x == true` (a bare `enabled(vm.X)` then goes
through `Enabled_n`, since `IsEnabled` is `bool`), and a declared non-built-in type compared with a string literal as
`$"{x}" == "Open"` (enums by name). `DeclaredTypes.Check` decides whether the declared type works with the winning
usage, following what `x:Bind` accepts (verified with the XAML compiler: `int` → `double` binds, `decimal` →
`double`, `DateTime?` → `DateTimeOffset?` and `bool?` → `bool` are WMC1121): a numeric target needs an implicit C#
conversion, `bool`/`DateTimeOffset?` targets need that type (`bool?` only for `bool?` targets), text and `object`
targets take anything, `format:` needs a number (date: `DateTimeOffset`/`DateTime`/`DateOnly`), `of T` collections
need element type `T`, conditions need a bool, a number for numeric literals, a string or non-built-in type for
string literals, a nullable type for `null`. Enums, structs and unknown names pass. When it works, FS2016 says the
generator "uses your T"; when not, FS2018 (error, at the C# declaration) names the usage, both types, the type to
declare and, where there is one, the `.flag` fix (`remove format: currency`, `write of OrderRow`).

**Page constructors and `OnViewCreated`.** The generated page declares `partial void OnViewCreated();` and calls it at
the end of the generated constructor, after `InitializeComponent()` and the ViewModel wiring; that hook, not a
constructor, is where page setup goes. When the page partial declares `View()` (or `View(Vm)`) anyway, the generator
drops its own constructor of that shape instead of failing with CS0111, and keeps the rule "the first answer builds,
and `InitializeComponent` and the ViewModel wiring run once": `ViewModel` becomes `get => _viewModel ??=
App.GetService<Vm>(); private set` (resolved on first use, so x:Bind finds it and their constructor may still assign
it; toast wiring happens in the setter path), the other generated constructor stays, and when their constructor does
not call `InitializeComponent()` the page overrides `OnNavigatedTo` to call it (the XAML compiler's
`InitializeComponent` returns early once loaded, so a second call is a no-op). If the page partial also overrides
`OnNavigatedTo`, FS2016 says the page stays empty until their constructor calls `InitializeComponent()`. A
hand-written `OnViewCreated` with an access modifier gets a declaration with the same modifier.

**Global usings.** With an `app`, the scaffold emits `GlobalUsings.g.cs`:
`AppScaffoldGenerator.GlobalUsingNamespaces` (`Microsoft.UI`, `.Dispatching`, `.Input`, `Microsoft.UI.Xaml` and
`.Controls`, `.Controls.Primitives`, `.Data`, `.Input`, `.Media`, `.Media.Imaging`, `.Navigation`, `Windows.Foundation`,
`Windows.Graphics.Imaging`, `Windows.Storage.Pickers`, CommunityToolkit `ComponentModel`/`Input`,
`Microsoft.Extensions.DependencyInjection`, `System.Collections.ObjectModel`), `Color = Windows.UI.Color`, and
`{ns}.ViewModels` when anything is generated there.
Left out because a type name would be ambiguous (CS0104): `Windows.UI` (`WindowId`), `Windows.System`
(`DispatcherQueue*`), `Microsoft.UI.Xaml.Shapes` (`Path`), `Windows.Storage` (`FileAttributes`, `StorageProvider` in
app code), `Windows.Storage.Streams` (`Buffer`), `System.ComponentModel` (`BindableAttribute`). The CLI's
build hints (`WinUiTypeHints`) name the `using` for their common types (CS0246/CS0103: `Polyline` →
`using Microsoft.UI.Xaml.Shapes;`, `FileAccessMode` → `using Windows.Storage;`) and give UWP-only types the FS1030
replacement (`InkCanvas`, `InkPresenter`, `Windows.UI.Input.Inking`, `MediaElement`, `WebView`; `Windows.UI.Xaml.*` →
`Microsoft.UI.Xaml.*`).

*Checking a candidate namespace.* Read the public top-level type names (`System.Reflection.Metadata`: `TypeDefinitions`
and `ExportedTypes`, generic arity stripped) of the Windows SDK projection
(`microsoft.windows.sdk.net.ref/10.0.22621.*/lib/net8.0`), the Windows App SDK 1.7 `lib/net6.0-windows10.0.18362.0`
folder, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection(.Abstractions) and the .NET reference pack
(`packs/Microsoft.NETCore.App.Ref/10.*/ref/net10.0`), and intersect the candidate's names with those of every global
using and of the SDK's implicit usings (`System`, `System.Collections.Generic`, `System.IO`, `System.Linq`,
`System.Net.Http`, `System.Threading`, `System.Threading.Tasks`). The scan reports `Path` for `Microsoft.UI.Xaml.Shapes`
and `FileAttributes` for `Windows.Storage`, and nothing for `Windows.Graphics.Imaging`. Also check that apps built with Flagstaff
are unlikely to declare types with the candidate's names.

### App hooks (`AppScaffoldGenerator.GenerateAppXamlCs`)

`App.xaml.cs` declares three partial methods for the hand-written half of `App`:

```csharp
partial void ConfigureServices(IServiceCollection services);   // after the ViewModel registrations
partial void OnAppStarting(List<Func<Task>> steps);            // after BuildServiceProvider, before MainWindow
partial void OnAppLaunched();                                   // after MainWindow.Activate()
```

`OnLaunched` is `async void`: after `OnAppStarting` it awaits every step in order (try/catch: `Debug.WriteLine`, re-thrown
in DEBUG), then creates and activates `MainWindow`, then calls `OnAppLaunched()`. Async seeding goes in a step; never
block the UI thread on it in `OnAppLaunched`.

## XamlGenerator partial files

| File | Contents | Area |
|---|---|---|
| `XamlGenerator.cs` | fields, `EmitElement` dispatch switch, shared helpers (`Line/Indent/Dedent/Esc/GetLabel/GetBindingAttr/GetFirstBinding/GetPropValue/GetIntProp/CountChildren/GetIconSymbol/NormalizeIconName/ValidSymbols/WriteXamlNamespaces/BindPath`), `Conditions` | shared: one `case` per keyword |
| `XamlGenerator.App.cs` | `GenerateApp`, `EmitNav`, `EmitBackdrop`, `GetThemeIdent` | app shell / navigation |
| `XamlGenerator.Layout.cs` | `GenerateView` (page root), toolbar, heading, spacer, separator, text, row, column (= stack), stack, split, group, card, cards, `EmitVerticalContainer`, `EmitGrid`, `LayoutGrid`, `ChildElements`/`SlotChildren`, `EmitInSlot` | layout |
| `XamlGenerator.Common.cs` | `CommonAttrs(el)`, `CommonAttrsKeywords`/`AppliesCommonAttrs(el)`, `ControlKeywords`/`IsControl(el)`, `ParseWidth`/`WidthToXaml` (the one width parser), `BindExpr`/`OneWayFor`/`RawBinding`, `FormatConverter`, `Diagnostics` | layout |
| `XamlGenerator.Inputs.cs` | button (`ActionAttrs`, `EmitButtonControl`, `CancelTarget`), textbox, number, checkbox, radio, datepicker, filePicker, textarea, toggle, image, progress, spinner, badge, summary, line, stat, link | inputs / display |
| `XamlGenerator.Tables.cs` | `EmitTable` (col widths, row actions + header placeholders, `EmitGhostButton`), `EmitSelect` | tables / lists / select |
| `XamlGenerator.Lists.cs` | `EmitList`, `EmitCollection` (empty state), `SelectionAttrs`, `ItemClickAttrs` | tables / lists / select |
| `XamlGenerator.Overlays.cs` | alert, `IsHoisted`, overlay hoisting + `EmitOverlayLayer`, dialog/confirm (`EmitModal`, `EnsureCancelCommand`), toast | overlays / dialogs |
| `XamlGenerator.Escape.cs` | `EmitRawXaml` | escape hatch |
| `XamlGenerator.PassThrough.cs` | `PassThroughAttrs`, `EmitRoot`/`EmitTag`, `EmitPropertyElements`, view-level `Page.*` / `VisualStateManager.VisualStateGroups`, FS1033 (`ReportUnsupportedPassThrough`), row-level `ContextFlyout` on table/list (`EmitRowPropertyElements`), variant-tag FS1031 (`CheckVariantTag`) | universal element |
| `XamlGenerator.Generic.cs` | `EmitGeneric`, `RenderAttr`, generic property elements / content / arrow | universal element |
| `XamlGenerator.Events.cs` | `EventAttrs`, `EventHandler` (OnEvent_n), `ItemSourceOf`, `ArrowEvent`, `ReportUnemittedEvents` | events |

Layout: `ElementSchema.Layout.cs` registers the universal props `width` (auto / 240 / `*` / `2*` / `10*`; the
lexer reads `*`/`N*` as a `Star` token, parsed to `IdentValue`; `XamlGenerator.ParseWidth` is the only parser, used by
layout children, `col`, `LayoutValidator` and `ItemTypeRegistry`), `span`, `fill`, `align`, `tooltip`, `name`, plus
`placeholder`/`hint` on textbox/textarea/number/select (`hint` also on datepicker), `readonly`/`min`/`max`/`step` on
inputs, `variant` and `icon` on button, `icon` on filePicker and `wrap` on row. Every Layout/Inputs
emitter appends `CommonAttrs(el)` (visibility, enabled for controls, then the props); every emitter that calls it is
listed in `CommonAttrsKeywords`, and on the other keywords `align`/`tooltip`/`name` are FS1002. Grid
containers (`row`, `cards`, `split`, vertical containers with a `fill` child) wrap each child in a `<Border>` that
carries `Grid.Row`/`Grid.Column`/`Grid.ColumnSpan`. Hoisted overlays (`IsHoisted`: toast/confirm/dialog) never take a
row, column or slot: grid containers place `SlotChildren(el)` and emit only the overlays' marker comments. `LayoutValidator.cs` runs from `GenerateView`; its diagnostics go
to `XamlGenerator.Diagnostics` (`Report`), which `FlagstaffCompiler.CompileView` adds to the result.

Page root: `Page > Grid` (rows `Auto`, `*`): the first top-level `toolbar` is pinned in row 0; row 1 is
`ScrollViewer > StackPanel Spacing=16 Padding=24`, or, when a top-level element has `fill`, a `Grid RowSpacing=16
Padding=24` with a `*` row for it. `EmitOverlayLayer(view)` is called inside the root Grid after the body.

New areas get a new partial file (`XamlGenerator.<Area>.cs`). `XamlGenerator.BindPath(path)` maps `vm.X` →
`ViewModel.X` and `item.X` → `X`. Emitters bind parser-stored properties through the private `Bind(property)`
(`X` → `ViewModel.X`, `item.X` → `X`), so an emitter works unchanged inside an item template.

### Cross-cutting hooks

- The Page root carries `x:Name="FlagstaffPage"`; templates reach page-level commands with
  `{Binding ElementName=FlagstaffPage, Path=ViewModel.XCommand}`.
- `GenerateView` calls `EmitOverlayLayer(view)` (in `XamlGenerator.Overlays.cs`) after the scrolling body; hoisted
  toasts/dialogs are emitted there.
- `XamlGenerator.Diagnostics` collects generation-time diagnostics (FS1020–FS1022); `FlagstaffCompiler.CompileView`
  adds them to the result.
- `XamlGenerator.ExtraCodeBehindMembers` / `ExtraViewModelMembers` collect verbatim C# members that an emitter needs
  (event handlers, helper commands); `FlagstaffCompiler.CompileView` passes them to `CodeBehindGenerator` and
  `ViewModelGenerator`, which append them at the end of the class.

## Generic elements (`GenericValidator.cs`, `XamlGenerator.Generic.cs`)

The universal element: any WinUI type in Flagstaff's element form, validated and typed
through the control manifest under the **locality rule** (an attribute passes through only when its correctness is
local to the element; anything else is compiler-checked or rejected). Grammar: `docs/grammar.ebnf` (the
generic-tier productions).

AST contract (`Ast.cs`):

- `FlagFile.Uses`: `UseDecl(Assembly, Prefix, Location)` from `use "<assembly>" as prefix;` (file top only, else
  FS1051). `XamlGenerator.Uses` (set per file by `FlagstaffCompiler`) emits `xmlns:prefix="using:<assembly>"`.
- `Element.IsGeneric`: the element name is a PascalCase type (`Expander`) or `prefix.Type` (`lvc.CartesianChart`;
  an undeclared prefix is FS1036, the element is still parsed). `Keyword` is the name as written,
  `NamespacePrefix` is `lvc` or null, `TypeName` is the name without prefix (equals `Keyword` for keyword
  elements). The parser accepts any PascalCase name; `GenericValidator` checks it against the manifest. Label/binding are stored as
  `_label`/`_binding`; `when`/`enabled`/`format`/`of`/`->` are parsed as for keywords.
- `Element.Events`: `EventBinding(EventName, Target, Arg, Location)` from `on Click -> vm.XCommand[(item)]` (any
  element; `Target` uses the arrow forms: `vm.XCommand`, a view name, `cancel`). An `on X -> y;` line inside the
  element's block is added to `Events` exactly like the header clause.
- Keys may be dotted (`Grid.Row`, `AutomationProperties.Name`); `AstExtensions.IsPassThroughKey(key)` is true when the
  key starts uppercase. An identifier path followed by `:` is always a key, never a new element.
- Values under a pass-through key: `{` starts an `ElementsValue` (elements only). A PascalCase name (or
  `prefix.Type`) directly followed by `;`, `}`, `->`, `when`, `enabled(`, `format`, `of` or `on` is a **bare name**
  (`IdentValue`: `Orientation: Horizontal;`, `Fill: Red when(...)`); so is a name the manifest knows as a value and
  not as a type (`GenericValidator.IsBareValueName`: any WinUI enum member or named color), whatever follows, so
  `StackPanel Orientation: Horizontal Spacing: 8 { ... }` keeps `Spacing` and the block on the StackPanel. Otherwise (followed by its own props, label or block) it starts an
  `ElementValue`, a full element that ends with its own `;` or `{ }`. Keys whose last segment ends in `Type` always
  take a type name (`TargetType: Button { Setter ...; }` is a Style with a Setter child, not a `Button` value); so does
  a `Property:` key (`Setter Property: Padding Value: "12,8";`, `Setter Property: Grid.Row ...`).
  Under lowercase keys a PascalCase name stays an `IdentValue` (`name: SearchBox`). A name that is neither (a typo:
  `Orientation: Horizonal Spacing: 8`) still starts an element; the validator reports FS1032 with the closest member.
  `GenericValidator.TryRepairBareEnum` rewrites such an `ElementValue` (no body/label/clauses, name a member of the
  property's enum or a named color) back to an `IdentValue` and returns the props to move onto the owner; it runs
  before validation and is the fallback for ASTs the parser rule does not cover.
- **Header-trailing element value.** When a header `ElementValue` ends with its own `;` and the header does not
  visibly continue (a key, `when`/`enabled(`/`format`/`of`/`on`, `->` or `{`), that `;` also ends the owner:
  `button "Save" Icon: SymbolIcon Symbol: "Save";` is one button (a redundant second `;` is accepted). A value
  ending with `{ }` does not end the owner (`... Flyout: MenuFlyout { ... };`; a missing `;` gets an FS1051 hint).
  Clauses written after a value element belong to the value element (`Icon: SymbolIcon Symbol: "Save" -> vm.X;` puts
  the arrow on `SymbolIcon`); FS1023's hint points this out on `button`/`link`/`filePicker`.
- Value forms (any key): `theme(K)` → `ThemeResourceValue`, `resource(K)` → `StaticResourceValue`, `load(vm.X)` →
  `LoadValue` (path stored like `BindingValue`), `ref(Name)[.A.B]` → `RefValue(Name, "A.B" | null)`.
- **Property lines.** A `Key: ...` line (pass-through key) inside an element's block is a property of that element:
  it is appended to `Props` exactly as in the header, and a block holding only property lines becomes `EmptyBody`, so
  `button "x" { Flyout: MenuFlyout { ... } }` and `button "x" Flyout: MenuFlyout { ... };` give the same AST
  (the same holds for `on` lines). Element
  and block values need no `;` on a property line; scalar values do. At view level such lines go to
  `ViewDecl.PropertyElements` (`Page.Resources: { ... }`). Lowercase key lines in a block remain FS1000.
- **`of T` per binding.** An `of T` written directly after a pass-through binding (`ItemsSource: vm.Rows of Row`,
  `Value: vm.Volume of double`) is stored on that prop as `PropPair.TypeArg`; `Element.OfType` is also set (the last `of`
  wins), which is what keyword elements read. Nested binding paths (`vm.A.B`) are FS1051 with the hint "nested paths are
  not supported; expose `B` on the ViewModel".
- `ViewDecl.NamedElements`: every `name:` in the view (children, property-element values and view-level property
  elements included), in source order with duplicates kept; `SemanticValidator` reports duplicates as FS1042.

Schema checks (`SemanticValidator`): generic elements skip FS1000/1001/1002/1004 except for lowercase props, which are
checked against `ElementSchema.Universal`; pass-through keys are left to `GenericValidator` (below); elements nested in
property values and in generic elements' children are validated as usual; an element value under a lowercase key is
FS1005. `LayoutValidator` reports `uid:` as FS1002 on keywords that do not apply `CommonAttrs` (generic elements: see
the end of this section).
Validation (`GenericValidator.Validate(view, diagnostics)`, called first by `SemanticValidator.ValidateView`; it walks
children, property-element values and view-level property elements, repairing bare enums as it goes):

- Generic element: FS1030 unknown/abstract type (`SuggestTypes`; the hint names `use "<assembly>" as prefix;`); FS4001
  warning when `KeywordFor(type)` exists; FS1004 for a label when the content property is not `string`/`object` (or
  the element also has children) and for a positional binding when the type has no overlay input property
  (`FindInputProperty`: the first of `Value`, `Text`, `IsChecked`, `IsOn`, `IsExpanded`, `IsOpen`, ... that
  `DefaultMode` makes TwoWay); FS1035 for children without a content property, several children of a one-object
  content property that is not `object`/`UIElement`, or children of the wrong item type (`MenuFlyout { text ...; }`);
  FS2014 for `->` without `HasCommand` (`vm.X`) / `HasEvent(Click)` (navigate), and for `-> cancel`; FS1012 warning for
  `enabled()` on a non-Control; FS1002 for `when()` on a non-UIElement and `width/align/tooltip/fill/span` on a
  non-FrameworkElement; FS2005 for any binding, condition or command inside a generic `FrameworkTemplate`
  (`DataTemplate { }`), whose binding root the compiler does not know (use `list ... of T`). Lowercase props are still
  checked against `ElementSchema.Universal` by `SemanticValidator`.
- **UI content** (`CheckUiContent`): a WinUI type that is not a `UIElement` (flyouts and menu items excepted) as a
  direct child of the view body or of a keyword element is FS1035 with a placement hint (`RowDefinition belongs in Grid
  RowDefinitions: { ... }`, `Style ... belongs in Page.Resources:`). Generic parents are judged by their content
  property's item type (`CheckChildren`); property elements and resource dictionaries take any type.
- **`of T` must type something** (`CheckOfTypeUsed`, FS1047): on a generic element it must follow a binding, or type a
  collection property / `SelectedItem`, or make the body an item template; otherwise it would be ignored silently.
- `GenericValidator.CheckPassThroughProps(el, rootType, view, diagnostics)` (keyword elements use it with their root
  type, see `GenericValidator.KeywordRootType`): FS1031 unknown property (suggestions, else the type's declared
  properties); FS1032 value shape (`ShapeFor`: bool/int/double/string/enum members/Thickness/CornerRadius/GridLength/
  Brush/Color/Uri/Icon; bindings, `theme()` and `resource()` always pass; an element value must derive from the
  property type); `load(vm.X)` only under the universal `load:` (FS1046 under `Load:`, FS1032 elsewhere). `Setter.Value`
  is skipped here and typed by `ValidateResources` (see "Resources, states and value forms"). Keyword elements without a root type get the
  dotted-key and `ref()` checks only.
- Third-party (`prefix.Type`): FS4002 warning once; own properties, label, children and arrow pass unvalidated;
  dotted keys and `ref()` are still checked (they do not depend on the type). A binding under an own property without
  `of T` infers `object?` and is FS4003 (warning): `Series: vm.Series of ISeries` → `ObservableCollection<ISeries>` (any
  non-built-in type is a collection), `Value: vm.Volume of double` → `double` (built-ins: `bool`, numeric types, `char`,
  `string`, `object`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`). The generated ViewModel names the type
  unqualified, so a library type needs a `global using` in the app. Third-party `of T` types are never item classes. An undeclared prefix is FS1036 (parser).
- **Locality** of dotted keys: `Owner.Member` that is the element's own property qualified (`Grid.RowDefinitions`) is
  an own property. Otherwise the attached property must exist (FS1037 for a misspelled owner, with a suggestion;
  FS1031 for an unknown member of a known owner; FS1041 for an owner that is no WinUI type) and `Locality(key)`
  decides: `Local` → shape check; `CheckedByGrid` (`Grid.Row/Column/RowSpan/ColumnSpan`) → the element's **direct
  parent** (`GenericValidator.ParentOf`; property-element values have none) must be a generic `Grid` with
  `RowDefinitions:` / `ColumnDefinitions:` (count of `RowDefinition`/`ColumnDefinition` elements) and the value a
  literal int; `Row >= rows` or `Row + RowSpan > rows` is FS1038 with the declared count; keyword layouts never count
  as a Grid. `CheckedByRef` → the value must be `ref(Name)` (FS1040); `Unknown` → FS1041 (hint: keyword layout,
  `ref()`, `xaml { }`; the allowlist is `Manifest/overlay.json`).
- **References**: a property with `ReferenceEmitFor != None` (`Setter.Target`, `TeachingTip.Target`,
  `RelativePanel.Below`, ...) takes `ref(Name)` only (FS1040 for strings; TargetPath needs `.Member`, Name forbids it).
  Every `ref(Name)` resolves against `ViewDecl.NamedElements` (FS1039, with a suggestion); `.Member` is checked
  against the named element's manifest type when it has one (FS1031).

Emission (`EmitGeneric`): open tag `Type` / `prefix:Type` (`WriteXamlNamespaces` emits `xmlns:prefix="using:<assembly>"`
from `Uses`); attributes in order: label → content property; a single string child of a `string`/`object` content
property → that property; positional binding → the input property; scalar pass-through props via `RenderAttr`; the
arrow; then `CommonAttrs(el)` (`IsEnabled` only when `IsControl(el)`; `PassThroughAttrs` returns `""` for generic
elements). Then property elements (`<Type.Key>`, dotted keys verbatim; `Icon: SymbolIcon;` → an empty element), then
children through `EmitElement` (keywords nest; `TextContent` → `TextBlock`); two or more children of a non-collection
content property are wrapped in `<StackPanel Spacing="8">`. Self-closes when empty. `-> vm.XCommand` reuses
`ActionAttrs` (item-template form included); `-> navigate(View)` adds `Click="OnNavigate_n"` and a handler
(`Frame.Navigate(typeof(View))`, args typed from the manifest's `Click` event) to `ExtraCodeBehindMembers`.

`internal string RenderAttr(string key, PropValue value, ManifestType? ownerType, ManifestProperty? prop)` returns
` Key="..."` (leading space), or `""` for element values and bare type names (the caller emits those as property
elements): `vm.X` → `{x:Bind ViewModel.X, Mode=TwoWay|OneWay}` (`DefaultMode`; OneWay for read-only and unknown
properties), `item.X` → `{x:Bind X}`; `theme(K)` → `{ThemeResource K}`; `resource(K)` → `{StaticResource K}`;
`load(vm.X)` → `x:Load="{x:Bind ViewModel.X, Mode=OneWay}"`; `ref(N).M` → `N.M` (TargetPath), `N` or `{x:Bind N}`
(Name: bare for `string`/`object` properties; `{Binding ElementName=N}` inside templates), or an element binding
`{x:Bind N.M, Mode=OneWay}` on an ordinary property; enum members (in the manifest's spelling: `wrap` → `Wrap`; the
validator matches enum members case-insensitively), named colors, `*`, literals and `{x:Null}` as text (GridLength
through `ParseWidth`). Type-name properties (`XamlGenerator.IsTypeNameProperty`: manifest type `Type`, i.e. `TypeName`
projected as `System.Type`, on `Style.TargetType`, `ControlTemplate.TargetType`, `Frame.SourcePageType`; and
`Setter.Property`, a `DependencyProperty`) always render a bare name as an attribute string, never as a property
element: `TargetType="Button"`, `TargetType="local:Card"` (`local.Card`), `Property="Grid.Row"`. It is the only
attribute renderer: pass-through on keyword elements calls it with the keyword's root type.

Type inference: a binding under a PascalCase key, or a generic element's positional binding, is typed by
`TypeInference.ManifestTypeFor(el, key, typeArg)`, for ViewModel members and for `item.X` row-type members
(`ItemTypeRegistry`); `load: load(vm.X)` → `bool`. In order: a collection property (`IsCollectionProperty`: manifest
collection, or an `object` `ItemsSource`/`MenuItemsSource`/`FooterMenuItemsSource`/`Items`/`MenuItems`) →
`ObservableCollection<T>` with the prop's own `of T`, else the element's, else `object`; `SelectedItem` (object) with an
`of T` → `T?`; any other prop with its own `of T` → `T` (`T?` for a non-built-in type); otherwise `CSharpTypeFor` at
Explicit (`object` and unknown properties → Default `string`). Unvalidated elements: see FS4003 above.

**Item templates on WinUI items controls** (`XamlGenerator.IsGenericItemTemplate`): a generic element with `of T`, child
elements and an `ItemTemplate` property (`ListView ItemsSource: vm.Rows of Row SelectedItem: vm.Sel { text item.Title;
}`) emits its body as `<ListView.ItemTemplate><DataTemplate x:DataType="vm:Row">` with `InItemTemplate` set, exactly
like `list` (several children are stacked in a `StackPanel Spacing="4"`). The body is a row everywhere: `item.X` types
`Row` (registered as an item class), `vm.X` is FS2005, `(item)` commands take `Row`, overlays are FS1020, and
`CheckChildren` does not judge it as items. Snapshot: `generic-itemsource`. Type inference
and `ConditionCompiler` numbering also walk property-element values, as do `ItemTypeRegistry.Collect` (row scope as
above) and `CodeBehindGenerator.HasNavigateButtons` (a `button -> navigate(V)` inside a flyout gets the shared click
handler).

`LayoutValidator` treats generic elements as having their own emitter (`AppliesCommonAttrs`), so `name:`, `align:`,
`tooltip:`, `uid:` and a pixel `width:` are accepted; `enabled()` on them is judged by `GenericValidator`. `uid:`
(universal, `x:Uid`) is emitted by `CommonAttrs`, which also appends `PassThroughAttrs(el)` (keyword pass-through, see
below) and `EventAttrs(el)` (see Events). `EmitElement`'s `default:` arm routes generic elements
to `EmitGeneric`.

## Events (`XamlGenerator.Events.cs`)

`on Event -> target [(item)];` (header clause or a line in the block, both in
`Element.Events`) on any element whose type has the event: a generic element's manifest type, or a keyword's declared
`KeywordSchema.RootType` (base chain walked, `ControlManifest.TryGetEvent`).

- **Emission.** `EventAttrs(el)` (appended by `CommonAttrs`; the table/list ListView, `alert`/`toast` InfoBar and
  `dialog`/`confirm` Grid append it next to `PassThroughAttrs`) writes `Event="OnEvent_n"` per clause (n 1-based per
  view, in emission order, so deterministic) and adds the handler to `ExtraCodeBehindMembers`. Results are cached per
  element, so an emitter that calls `CommonAttrs` twice gets no second handler. `on ItemClick` on a `ListViewBase`
  also writes `IsItemClickEnabled="True"` unless the element sets it.
- **Handler** `private void OnEvent_n(object sender, <Args> e)`; `<Args>` is the manifest's `ArgsTypeName` (already
  a full name: `Microsoft.UI.Xaml.RoutedEventArgs`, `...Controls.SelectionChangedEventArgs`,
  `...Input.TappedRoutedEventArgs`, ...); third-party elements get `object` (binds any delegate). Bodies:

  | Target | Body |
  |---|---|
  | `vm.SaveCommand` / `vm.Save` | `if (ViewModel.SaveCommand.CanExecute(null)) ViewModel.SaveCommand.Execute(null);` |
  | `vm.OpenCommand(item)` in a table/list row | `if (sender is Microsoft.UI.Xaml.FrameworkElement { DataContext: ViewModels.T item } && ...CanExecute(item)) ...Execute(item);` (a template's content has the row as DataContext) |
  | `(item)` on `SelectionChanged` of an element with `of T` | `if (e.AddedItems.Count > 0 && e.AddedItems[0] is ViewModels.T item && ...)` (a cleared selection runs nothing) |
  | `(item)` on `ItemClick` of an element with `of T` | `if (e.ClickedItem is ViewModels.T item && ...)` |
  | `navigate(View)` | `Frame.Navigate(typeof(View));` |
  | `cancel` (inside dialog/confirm) | `ViewModel.Cancel{Flag}Command.Execute(null);` (`UsesCancelTarget` counts events) |

  `XamlGenerator.ItemSourceOf(el, evt)` decides the `(item)` source (args type `SelectionChangedEventArgs` /
  `ItemClickEventArgs` and `el.OfType`); `TypeInference` uses it so the command's `ParameterType` matches. Async
  commands are not awaited (fire and forget, like `Command=`); an `AsyncRelayCommand` that is still running reports
  `CanExecute == false`, so a burst of `TextChanged` runs it once. `SelectionChanged` also fires when a two-way
  `SelectedItem` binding sets the selection; the bound VM property is already updated when the command runs.
- **Inference.** `TypeInference` registers `vm.X` event targets as commands exactly like arrows (`[RelayCommand]
  XAsync()` / `XAsync(T item)` + `OnXAsync`); `(item)` without a row (or a selection/click source) is FS2007; a target
  `vm.X` (no `Command` suffix) that the view also binds as a property is FS2015.
- **Validation** (`GenericValidator.ValidateEvents`, at the end of `Validate`): FS1034 unknown event (closest names
  within Levenshtein 2, else the type's events nearest-type-first, 8 listed; keywords without a root tag such as `col`
  cannot take events), FS1043 the same event twice on one element or an event the element's own arrow wires
  (`ArrowEvent`: `Click` for navigating `button`/`link`/`filePicker`/generic elements, `ItemClick` for a `table`/`list`
  with `->`), FS1024 `-> cancel` outside a dialog/confirm (any element inside one is fine). `on` clauses an emitter did
  not write (a `spacer` that a toolbar/row consumes) are FS1033 (`ReportUnemittedEvents`, end of `GenerateView`).
  Buttons with `on Click` are not FS1023. Variant tags (`heading` with `icon:` → StackPanel) are validated against the
  declared RootType here and re-checked against the emitted tag by `CheckVariantTag` (FS1034, see below).

## Pass-through on keyword elements (`XamlGenerator.PassThrough.cs`)

On a keyword element (`!IsGeneric`):

- A **PascalCase key with a scalar value** (`Margin: "0,8"`, `MaxLength: 80`, `Style: resource(Compact)`,
  `Opacity: vm.Fade`, `AutomationProperties.Name: "Save"`) becomes an attribute on the keyword's **root tag**:
  `CommonAttrs(el)` appends `PassThroughAttrs(el)` (table, list, alert, toast, dialog and confirm, which do not use
  `CommonAttrs`, append it directly). Values are rendered by `RenderAttr` with the manifest property looked up on
  `KeywordSchema.RootType` (binding mode, enum spelling); `item.X` binds to the row. Every prop was validated first
  (`GenericValidator.KeywordRootType` → `CheckPassThroughProps`: FS1031/FS1032, `ref(X).Member` against keyword
  targets too, and the locality rule for dotted keys), so attached properties are emitted as written: `Local` ones
  (`AutomationProperties.*`, ...) anywhere, `Grid.Row`/`Column`/spans only on a direct child of a generic `Grid` with
  definitions (elsewhere FS1038, and errors stop generation). A table/list with an `empty` child carries its Grid
  placement on the wrapper Grid (`PlacementAttrs`), not the ListView.
- A **PascalCase key with an element or block value** (`Flyout: MenuFlyout { }`, `ContextFlyout: ...`,
  `KeyboardAccelerators: { KeyboardAccelerator ...; }`) becomes a property element inside the root tag:
  `<Button.Flyout>...</Button.Flyout>` (a dotted key is used as written: `<VisualStateManager.VisualStateGroups>`).
  The value's elements are emitted through `EmitElement`, so keywords and generic elements nest. Property elements
  come right after the open tag, before the emitter's own content.
- If a pass-through attribute repeats one the emitter writes (`Style:` on `stat`/`card`, `Spacing:` on `stack`,
  `TextWrapping:` on `textarea`), the pass-through value replaces it (the tag never has a duplicate attribute).
- **Variant tags.** Props are validated against the declared `RootType` and land on the tag the emitter writes. Where
  a keyword writes a different tag (`heading` with `icon:` → `StackPanel`, a label/string `badge` → `Border`,
  `stack`/`group`/`column` with a `fill` child → `Grid`), `EmitRoot` (`CheckVariantTag`) reports FS1031 for a prop the
  emitted tag lacks (`heading "x" icon: home TextWrapping: wrap`) and FS1034 for an `on Event` it lacks (`heading "x"
  icon: home on SelectionChanged -> ...`); both hints name the tag actually written. Props and events both tags
  have (`Margin`, `ContextFlyout`, `on Tapped`) pass and land on the emitted tag.

**Row-level property elements on `table`/`list`.** `ContextFlyout:` on a `table` or `list` belongs to the row: it is
written on the row template's root (`<Grid.ContextFlyout>` on the table's row Grid, `<StackPanel.ContextFlyout>` on
the list's template StackPanel, which also gets `Background="Transparent"` so the whole row is right-clickable) inside
the `DataTemplate`, emitted with `Conditions.InItemTemplate = true`. So `MenuFlyoutItem Text: "Open" ->
vm.OpenCommand(item)` takes the row-action form (`Command="{Binding ElementName=FlagstaffPage,
Path=ViewModel.OpenCommand}" CommandParameter="{x:Bind}"`, VM `OpenAsync(Order item)`), and `item.X` bindings and
`enabled(item.X)` resolve to the row (and type the row class); `vm.X` bindings there are FS2005, as in any row.
Every other property element on a table/list (`KeyboardAccelerators:`, `Header:`, `Footer:`) stays on the ListView,
outside its own row: `(item)` there binds the **enclosing** template's row if there is one (an inner `ListView ... of
Member { Header: ... }` inside `list ... of Group` → `Group`; `TypeInference.RowParameterType` skips the list that owns
the property element and walks on), else FS2007; `item.X` likewise resolves to the enclosing row, else FS2005. `XamlGenerator.IsRowPropertyElement` is
the single rule; `TypeInference`, `ItemTypeRegistry` and the emitters use it.

Helpers (all private to `XamlGenerator`):

- `string PassThroughAttrs(Element el)`: the attribute text (leading space each), `""` for generic elements.
- `void EmitRoot(Element el, string tag, string attrs, Action? body)`: `<tag attrs>` + `EmitPropertyElements(el, tag)`
  + `body` + `</tag>`, or `<tag attrs />` when there is neither. Keyword emitters write their root tag through it, so a
  keyword without property elements or body gets a self-closing tag.
  `EmitTag(tag, attrs, body)` is the same without property elements (the page body, table ghost buttons).
- `void EmitPropertyElements(Element el, string tag)`: the `<tag.Key>` blocks.
- `EmitRowPropertyElements(Element el, string tag)`: a table/list's row-level property elements (see above).

View level (`ViewDecl.PropertyElements`): `Page.Resources: { ... }` (and any other key) is written as
`<Page.Resources>` before the page's root Grid; `VisualStateManager.VisualStateGroups: { ... }` is written as the
first child of the root Grid (WinUI reads visual state groups from the root element of the page content).

**FS1033.** At the end of `GenerateView`, `ReportUnsupportedPassThrough` walks the view (children and property-element
values) and reports every keyword element whose pass-through attributes or property elements no emitter wrote:
property elements on `dialog` / `confirm` / `toast` (their emitters do not write them), anything on `col`, `empty`, `xaml`, or a `spacer`
that a toolbar/row/split consumes structurally. Nothing is dropped silently.

### Root tag per keyword (`KeywordSchema.RootType`)

`ElementSchema.RootTypes` sets `KeywordSchema.RootType` (the manifest short type name) after registration; the
validator checks pass-through props against it. `Margin:`, `Style:`, `AutomationProperties.*` and property elements on a
keyword land on this tag.

| Keyword | Root tag | Notes |
|---|---|---|
| `textbox`, `textarea` | `TextBox` | the `Text` binding stays `TwoWay` |
| `number` | `NumberBox` | |
| `select` | `ComboBox` | |
| `checkbox` | `CheckBox` | |
| `radio` | `RadioButton` | |
| `toggle` | `ToggleSwitch` | |
| `datepicker` | `CalendarDatePicker` | |
| `button`, `filePicker` | `Button` | with `icon:` the icon/label StackPanel is the content, after the property elements |
| `link` | `HyperlinkButton` | |
| `image` | `Image` | |
| `progress` | `ProgressBar` | |
| `spinner` | `ProgressRing` | |
| `text` | `TextBlock` | |
| `heading` | `TextBlock` | with `icon:` the tag is a `StackPanel` (RootType stays `TextBlock`; a TextBlock-only prop is FS1031) |
| `badge` | `InfoBadge` | only when bound to an `int`; a label or string binding emits a `Border` pill (an InfoBadge-only prop is FS1031) |
| `card`, `stat`, `separator` | `Border` | the card's/stat's content StackPanel is inside |
| `spacer` | `Border` | only in vertical containers; elsewhere FS1033 |
| `group`, `stack`, `column`, `summary` | `StackPanel` | `group`/`stack`/`column` become a `Grid` when a child has `fill` |
| `row`, `cards`, `split`, `toolbar`, `line` | `Grid` | |
| `table`, `list` | `ListView` | with an `empty` child the ListView sits in a wrapper Grid (which carries `when()`) |
| `alert` | `InfoBar` | |
| `toast` | `InfoBar` | scalar attributes only (property elements: FS1033) |
| `dialog`, `confirm` | `Grid` | the overlay root in `FlagstaffOverlay`; scalar attributes only |
| `xaml`, `col`, `empty` | none | FS1033 |

## Resources, states and value forms (`GenericValidator.ValidateResources`, `XamlLinter`)

Resources, styles, visual states and storyboards are generic elements in property elements; nothing here is a new
keyword. What this layer adds is the checks that make their cross-references local (the locality rule):
each of `key:`, `resource()`, `Setter Property:`, `Setter Target: ref()`, `Storyboard.TargetName/TargetProperty`
is judged at its own `.flag` line.

```flag
Page.Resources: {
    Style key: CompactStat TargetType: Border {
        Setter Property: Padding Value: "12,8";
        Setter Property: Background Value: theme(AccentFillColorDefaultBrush);
    }
    SolidColorBrush key: Accent Color: "#2563eb";
    Storyboard key: FadeIn {
        DoubleAnimation Storyboard.TargetName: ref(Panel) Storyboard.TargetProperty: "Opacity" From: 0 To: 1 Duration: "0:0:0.3";
    }
}
VisualStateManager.VisualStateGroups: {
    VisualStateGroup {
        VisualState name: Wide {
            StateTriggers: { AdaptiveTrigger MinWindowWidth: 720; }
            Setters: { Setter Target: ref(Nav).DisplayMode Value: Inline; }
        }
    }
}
stat "Total" vm.Total Style: resource(CompactStat);
```

- **Placement.** View-level `Page.Resources:` is written before the root Grid; view-level
  `VisualStateManager.VisualStateGroups:` is the first child of the root Grid (`EmitViewPropertyElements` /
  `EmitViewVisualStateGroups`). A generic element's own `Resources:` / `Grid.Resources:` works the same way.
- **`key:`** (universal, `PropKind.Ident`) → `x:Key`, written first by `EmitGeneric`. Valid only on a generic element
  directly inside a resources property element (`IsResourcesKey`: `Resources`, `*.Resources`, `ThemeDictionaries`).
  Elsewhere,
  or on a keyword element, FS1044. An entry without `key:` is FS1044 unless it is a `Style` with `TargetType:` (an
  implicit style, keyed `{TargetType}`); a repeated key in one dictionary is FS1045 (implicit styles for the same
  type included). Different dictionaries may reuse a key.
- **`resource(K)`** → `{StaticResource K}`. FS3010 (warning) comes from `XamlLinter` on the generated page: the key
  must be in `XamlLinter.KnownResources` (Styles.xaml, the generated App.xaml, WinUI built-ins the generator uses;
  `ResourceTests.Every_App_and_stdlib_key_is_known_to_the_linter` keeps it in sync) or declared by an `x:Key` in the
  same page (`DeclaredKeys`). Declaration order inside one dictionary is not checked (a `BasedOn: resource(X)` must
  follow `X`). **`theme(K)`** → `{ThemeResource K}`; theme keys are not checked (WinUI's theme dictionary is not in
  the manifest).
- **`load: load(vm.X)`** (universal, `PropKind.Any`; FS1032 for any other value) → `x:Load="{x:Bind ViewModel.X,
  Mode=OneWay}"`, emitted by `CommonAttrs`, so it works on generic elements and on keywords that apply `CommonAttrs`
  (FS1002 on the others, like `uid:`; on hoisted overlays the FS1002 hint says the prop is not supported there instead
  of suggesting a wrapper). `vm.X` infers `bool`. WinUI requires `x:Name` with `x:Load` (WMC0907): without `name:` the
  element gets `x:Name="FlagstaffLoad_n"` (n per view in document order: view property elements, then the body,
  property values before children; `NumberLoadNames`), and the linter's FS3012 rejects any `x:Load` left without a name.
  The PascalCase spelling `Load: load(vm.X)` is FS1046 (`GenericValidator.CheckLoad`). **`uid: "K"`** → `x:Uid="K"`.
- **Brush and Color values**: named colors (`Red`, `CornflowerBlue`) and `#rgb`/`#argb` hex (bare `#2563eb` or quoted)
  render verbatim; anything else on a Brush/Color property is FS1032.
- **Styles** (`CheckStyle`): `TargetType:` is a type name (rendered `TargetType="Button"`); each `Setter Property: P`
  must be a property of that type (`ResolveProperty`: own or attached, FS1031 with a suggestion), and `Value:` must fit
  P's shape (FS1032). Setter values are typed leniently because XAML writes them as strings: `"True"`, `"240"`,
  `"12,8"`, an enum member bare or quoted, `theme()`/`resource()`. Third-party or unknown target types keep the plain
  `Setter.Value` (object) check. `BasedOn: resource(X)` is an ordinary value.
- **Visual-state setters** (`CheckStateSetter`): `Target: ref(N).M` resolves `N` against the view's `name:`
  declarations (FS1039) and `M` against N's manifest type, generic or keyword root type (`card name: Nav` → `Border`;
  FS1031); a string target is FS1040. `Value:` is typed against `M` as above. A bare name under `Setter.Value` is always
  a value (`Value: Inline` → `Value="Inline"`, even though `Inline` is also a WinUI type); an object value takes the
  element form (`Value: SolidColorBrush Color: Red;`). `VisualState name: Wide` → `x:Name="Wide"`.
- **Storyboards** (`CheckAnimation`): `Storyboard.TargetName: ref(N)` (on the animation, or inherited from its
  `Storyboard`) → `Storyboard.TargetName="N"`. `Storyboard.TargetProperty` is allowlisted as local (overlay.json) and
  is a string: a simple name (`"Opacity"`) must be a property of N's type (FS1031) and match the animation
  (`DoubleAnimation*` → number, `ColorAnimation*` → Color, `PointAnimation*` → Point; FS1032); a parenthesised path
  (`"(UIElement.Opacity)"`, `"(Border.Background).(SolidColorBrush.Color)"`) passes as written. `Duration`,
  `BeginTime` and `KeyTime` are strings in XAML time syntax (`"0:0:0.3"`); a number is not milliseconds. Starting a
  storyboard is code (`((Storyboard)Resources["FadeIn"]).Begin()` in the page partial); `on Event ->` does not
  start storyboards.
- **Overlays in property elements.** `CollectOverlays` walks property-element values (and view-level property
  elements), so `button "x" { Flyout: Flyout { toast "Hi" when(vm.T); } }` hoists the toast into `FlagstaffOverlay`
  (the flyout keeps the marker comment). Inside a row template (a `list`/`table` body, a row `ContextFlyout:`, or a
  generic `DataTemplate`) it is FS1020.

### What needs `xaml { }`

What the element form cannot express (compare the escape analyzer's `Reasons`):

- **Control templates**: `ControlTemplate` internals and `{TemplateBinding X}` (a generic `ControlTemplate` is a
  template scope: FS2005 for bindings inside; there is no `templated(X)` value form).
- **Other markup extensions**: `{RelativeSource Self|TemplatedParent}`, `{Binding}` with `ElementName`/`Path` written
  by hand, `{CustomResource}`, `{x:Bind}` with functions, converters or
  `FallbackValue`/`TargetNullValue`, and any custom markup extension.
- **x: directives without a prop**: `x:Phase`, `x:DeferLoadStrategy` (superseded by `load:`), `x:FieldModifier`,
  `x:DefaultBindMode`, `x:Shared`, and a literal `x:Load="False"`. (`x:Key`, `x:Uid`, `x:Load="{x:Bind ...}"` are
  `key:`, `uid:`, `load:`; the escape analyzer counts them as expressible.)
- **Nested binding paths** (`vm.Customer.Name`): FS1051; expose the value on the ViewModel (or row type).
- **Unknown types**: third-party types without a `use` + manifest entry pass with FS4002; `local:` user controls;
  types outside `Microsoft.UI.Xaml.*` (e.g. `Windows.UI.Xaml` interop) are not in the manifest.
- **Unchecked references**: storyboard paths through attached or nested properties are passed as written; key
  declaration order inside one dictionary, and theme keys, are not checked.
- **ResourceDictionary as an element**: the manifest gives `ResourceDictionary` no content property and no
  `ThemeDictionaries`, so `ResourceDictionary { ... }` with entries (theme dictionaries, a keyed nested dictionary) is
  FS1035/FS1031; `MergedDictionaries: { ResourceDictionary Source: "..."; }` works. (`ValidateResources` keys
  `ThemeDictionaries` entries and a `ResourceDictionary`'s children, so these work once the manifest/overlay describes them.)
## Overlays (`XamlGenerator.Overlays.cs`, `ElementSchema.Overlays.cs`)

- **Hoisting.** `toast`, `confirm` and `dialog` at any depth (document order, including inside another dialog) are
  collected by `XamlGenerator.CollectOverlays(view)`. In place they emit only a marker comment; `EmitOverlayLayer`
  renders them inside `<Grid x:Name="FlagstaffOverlay" Grid.Row="0" Grid.RowSpan="2">` (over toolbar row and body):
  modals first, then a bottom-right toast `StackPanel` (`Margin="24" Spacing="8"`). No overlays → no layer. An
  ancestor's `when()` does not apply to a hoisted overlay; only its own flag does. Property-element values are walked
  too (`Flyout: Flyout { toast ...; }` is hoisted). Inside a `list`/`table` (or a row `ContextFlyout:`, or a generic
  `DataTemplate`) → FS1020.
  `alert` stays inline (flow content).
- **`dialog "Title" message: "..." when(vm.Flag) { ... }`** → `EmitModal`: a Grid with the flag's Visibility, a
  `FlagstaffDialogBackdrop` Border, and a centered `FlagstaffDialogCard` (MaxWidth 480, Padding 24) holding the title
  (`SubtitleTextBlockStyle`), the message, the non-button children (and text lines) stacked, and the buttons
  right-aligned.
- **`confirm`** is the same modal. With buttons in its block it is emitted exactly like `dialog`. Without buttons it
  needs `-> target` and gets two generated buttons, in ContentDialog order: the confirming button (`confirmLabel:`
  or `"OK"`, `AccentButtonStyle`, or `FlagstaffDangerButton` for `variant: danger`, a key in
  `XamlLinter.KnownResources`) bound to the arrow target, then Cancel bound to a generated VM command:
  `[RelayCommand] private void Cancel{Flag}() => {Flag} = false;` (`when(vm.IsDeleteVisible)` →
  `CancelIsDeleteVisibleCommand`; once per flag; skipped if the view already uses a command of that name). The
  confirming command is the user's and must clear the flag itself.
- **`-> cancel`.** A `dialog` (or a `confirm` with its own buttons) whose block contains a `button ... -> cancel;` gets
  the same generated `Cancel{Flag}` command (`EnsureCancelCommand`); the button binds `ViewModel.Cancel{Flag}Command`.
- **`toast "..." when(vm.Flag) variant: ... duration: ms`** → `InfoBar` (`IsOpen` two-way, `IsClosable`). With
  `duration:` the code-behind subscribes to `ViewModel.PropertyChanged` in the constructor and, whenever the flag
  becomes true, (re)starts a one-shot `DispatcherQueueTimer` (one field + `Restart{Flag}Timer()` per flag) that sets
  it back to false. Without `duration:` there is no auto-dismiss. No `partial void On{X}Changed` is ever generated.
## Data: tables, lists, select, item types

```
table items: vm.Orders selected: vm.Sel of Order -> vm.OpenCommand(item) {
    col "Customer" bind: item.Customer width: "2*";      // width: auto | N | "N*" (header and rows share widths)
    col "Rate"     bind: item.Rate format: number(2);    // NumberConverter, ConverterParameter=F2
    button "Edit" -> vm.EditCommand(item);               // row action: trailing Auto column
    empty "No orders yet.";                              // shown while Orders.Count == 0
}
list items: vm.Contacts of Contact -> vm.OpenCommand(item) {   // ListView, DataTemplate x:DataType="vm:Contact"
    text item.Name;                                       // ordinary elements, positional item.X
    button "Call" -> vm.CallCommand(item);
}
select "Status" vm.Status options: ["Open", "Closed"];   // <x:String> items (options: 1..10 → <x:Int32>)
select "Vendor" vm.Vendor items: vm.Vendors of Vendor display: item.Name;   // DisplayMemberPath="Name"
```

- **Row templates.** `list` children are emitted through `EmitElement` with `Conditions.InItemTemplate = true`, so
  `Bind()` roots `item.X` at the row and `when(item.X)` compiles against the row. Keywords that accept a positional
  `item.X` are listed in `ElementSchema.ItemBindableKeywords` (`ElementSchema.Data.cs`).
- **Buttons in templates** (any depth, also table row actions) use the same `EmitButton` (variant, `icon:` and
  `CommonAttrs` apply); `ActionAttrs` switches to `Command="{Binding ElementName=FlagstaffPage, Path=ViewModel.XCommand}"`
  plus `CommandParameter="{x:Bind}"` for `(item)`.
- **Row-action widths.** Each row's action column is `Auto`, so a collapsed `when()` button would shrink it and shift
  the other columns. A row action with `when()` therefore sits in a one-cell Grid over an invisible copy of itself
  (`EmitGhostButton`: `Opacity="0" IsHitTestVisible="False" IsTabStop="False"`), and the header's action column holds
  invisible copies of all row actions, so header and rows have the same Auto width and line up.
- **Item bindings are OneTime.** Row types are plain generated classes (no `INotifyPropertyChanged`), so every
  `item.X` binding in a template, and every `when()`/`enabled()` whose paths are all `item.X`, omits `Mode`
  (x:Bind's OneTime default, like `col`); `vm.X` keeps `Mode=OneWay`. OneWay on a plain class is WMC1506. Replace the
  row object to show new values.
- **Row click.** `-> vm.XCommand(item)` / `-> navigate(View)` on a table/list → `IsItemClickEnabled="True"
  ItemClick="OnItemClick_n"` and a handler in `ExtraCodeBehindMembers`.
- **Empty state.** With an `empty` child, a `Grid` (carrying the collection's `when()`) holds the ListView and
  `<TextBlock Style="{StaticResource FlagstaffEmptyState}" Visibility="{x:Bind local:Converters.IsZero(ViewModel.X.Count), Mode=OneWay}" />`:
  for a `fill` collection both share one `*` cell (text `VerticalAlignment="Center"`), so the ListView stays bounded
  and virtualizes; otherwise the text is in a second Auto row below the ListView.
- **`options:`** is `PropKind.Any` in the schema; its shape (range, or non-empty list of strings) is checked by
  `ItemTypeRegistry.Collect` (FS1005).
- **`width:`** (`WidthToXaml` over `ParseWidth`): `auto` → `Auto`, positive numbers verbatim, `*` / `N*` (also quoted)
  verbatim; anything else is FS1005. `Auto` columns size per grid (WinUI has no SharedSizeGroup), so prefer fixed or
  star widths for `col` when rows must align exactly.
- **`format:`** on `col`, `stat`, `line` and `text` with a binding uses `FormatConverter`: currency/percent/date
  converters, `number(N)` → `NumberConverter` with `ConverterParameter=FN`.
- **`NumberConverter`** (`ConverterGenerator.GenerateNumberConverter`, `NumberConverter.g.cs`, registered in App.xaml
  as `NumberConverter`) formats double/decimal/int with the ConverterParameter (`F2`), `G` without one.
- Styles (`Styles.xaml`, `region: tables`): `FlagstaffTableRow`, `FlagstaffTableItemContainer`,
  `FlagstaffListItemContainer`, `FlagstaffEmptyState`.

### Item type registry (`ItemTypeRegistry.cs`)

```csharp
public sealed record ItemTypeProperty(string Name, string CSharpType, InferenceConfidence Confidence, SourceLocation Location);
public sealed class ItemTypeRegistry
{
    public IReadOnlyList<(string TypeName, IReadOnlyList<ItemTypeProperty> Properties)> Types { get; }
    public void AddType(string typeName);
    public void Add(string typeName, string prop, string csType, InferenceConfidence confidence,
                    SourceLocation location, List<Diagnostic>? diagnostics = null);   // FS2010 on explicit conflicts
    public void Collect(ViewDecl view, List<Diagnostic> diagnostics);                  // walk + scoping checks
    public List<OutputFile> Generate(string appNamespace);                             // one T.g.cs per type
}
FlagstaffCompiler.ItemTypes   // accumulates across Compile()/CompileAll() calls on one compiler instance
```

Types: `table`/`list`/`select ... of T`, a WinUI generic element's collection binding with an item type, and a generic
item template (`IsGenericItemTemplate`). Every registered type gets a `T.g.cs`, an empty `public partial class T { }`
when no binding gave it properties (`select ... items: vm.People of Person` without `display:`), so the generated code
that names it compiles. Sources: table `col bind: item.X` (+ `format:`), `select display: item.X`, every `item.X` binding inside a `list`
template (typed by the control: checkbox/radio/toggle → bool, number/progress → double, datepicker →
DateTimeOffset?, `format:` → its type, else string) and `item.X` in template conditions (Comparison confidence).
Highest confidence wins; two Explicit usages with different types are FS2010. Item types are emitted once, by
`GenerateAppScaffold()`, not per view.

## Control manifest (`Manifest/`)

What the compiler knows about WinUI types beyond the keywords. Consulted by
`GenericValidator`, `XamlGenerator.Generic.cs`, type inference (`TypeInference.ManifestTypeFor`) and the parser's
bare-name rule (`GenericValidator.IsBareValueName`); keyword elements use it through their root type (pass-through
props and events).

- `winui-manifest.json` is **generated** by `tools/ManifestGen` from `Microsoft.UI.Xaml.winmd` +
  `Microsoft.UI.winmd` in the WindowsAppSDK package pinned in `Directory.Packages.props`: every
  `DependencyObject` type in `Microsoft.UI.Xaml.*` (base type, abstract/Control/UIElement flags, declared
  `ContentProperty`, properties with normalized type names, DP/collection/read-only flags and enum members,
  attached properties, events with args types), the owners of attached properties that are not
  `DependencyObject`s (`AutomationProperties`, `ToolTipService`), and a value-type table (enums and structs).
  Regenerate after a WindowsAppSDK bump with `dotnet run --project tools/ManifestGen`; CI runs `-- --check`
  and fails on a stale file. `-- --stats` prints counts and short-name collisions. Output is written gzipped
  (`.json.gz`) if it ever exceeds 2 MB.
- `overlay.json` is **hand-curated** (notes in `overlay.md`): two-way input properties, keyword equivalents,
  value shapes, attached-property locality, `ref(Name)` emission, attached properties missing from metadata.
- `ControlManifest.Default` (lazy, ~50 ms cold) answers: `TryGetType` (short or full name),
  `TryGetProperty` / `TryGetEvent` / `ContentPropertyOf` / `DerivesFrom` (all walk the base chain),
  `TryGetAttachedProperty("Grid.Row")`, `DefaultMode`, `KeywordFor`, `CSharpTypeFor`, `ShapeFor`, `Locality`,
  `ReferenceEmitFor`, `HasCommand`, `HasEvent`, `SuggestTypes`, `SuggestProperties` (Levenshtein ≤ 2),
  `TryGetValueType`. Records and enums are in `ManifestTypes.cs`.
- ManifestGen also reads `Windows.Foundation.UniversalApiContract.winmd` from `Microsoft.Windows.SDK.NET.Ref` (pinned in
  `Directory.Packages.props`) for the members of `Windows.*` enums that WinUI properties use (`VirtualKey`,
  `VirtualKeyModifiers`, `DayOfWeek`, `FontStyle`, ...); those types are value types only, never manifest types. So
  `KeyboardAccelerator Key: S Modifiers: Control;` parses bare (an enum member beats a type of the same name:
  `TryRepairBareEnum`). `FontWeight` is a struct: the overlay gives it the `string` shape (`FontWeight: "SemiBold"`).

## Compilation stats and the escape ratio

`CompilationResult.Stats` (`CompilationStats.cs`) measures how much of a project is
written in the `xaml { }` escape and why each block needs it. `Compile` and `CompileAll` fill it for every file, including files with errors (an unparsed file only
counts lines).

- Per file (`FileStats`) and as project totals: `FlagLines` (non-blank lines not starting with `//`),
  `EscapeBlocks`, `EscapeLines` (non-blank lines inside `xaml { }` bodies), `EscapeRatio` = EscapeLines /
  FlagLines, `KeywordElements`, `GenericElements` (`Element.IsGeneric`), `GenericRatio` = generic / (keyword +
  generic). Element counts walk view bodies, children and property-element values; the `xaml` blocks themselves
  are not counted as elements.
- `EscapeBlocksDetail`: one `EscapeBlockStats` per block (file, line/column of the `xaml` keyword, line count,
  `RootTypes` as written, `Reasons`, `Findings` such as `x-directive: x:Uid`).
- `EscapeAnalyzer.Analyze` classifies the raw text. It wraps the fragment in a root declaring the default
  presentation namespace, `x:`, and a placeholder namespace for every other prefix used, then parses it with
  `XElement`. Reasons (`EscapeReason`, all that apply, in this order):
  - `malformed`: does not parse, or has no element (reported alone);
  - `unknown-type`: an element type outside the presentation namespace (`lvc:`, `local:`) or not in
    `ControlManifest.Default` (property elements such as `Grid.ColumnDefinitions` are skipped);
  - `markup-extension`: any `{Name ...}` in an attribute value other than `x:Bind`, `StaticResource`,
    `ThemeResource` (nested ones too, e.g. `Converter={CustomResource C}`); `{}` escapes are ignored;
  - `x-directive`: an `x:` attribute without a Flagstaff prop: `x:Phase`, `x:DeferLoadStrategy`, `x:FieldModifier`,
    `x:DefaultBindMode`, `x:Shared`, `x:Load` with a literal value. `x:Name`/`x:DataType` are ignored; `x:Key`,
    `x:Uid` and `x:Load="{x:Bind ...}"` are expressible and recorded as findings `prop: x:Uid -> uid:`;
  - `event-handler`: kept in `EscapeReason` for the JSON contract but never produced: an attribute naming a
    manifest event of its element (`Click="OnClick"`) is expressible with `on Click ->` and recorded as the finding
    `event: Click`;
  - `expressible`: none of the above (findings may still list `event:`/`prop:` items).
- **FS3011** (warning, `XamlLinter.LintEscapeBlocks`) is reported for each `expressible` block at the `xaml`
  keyword in the .flag file. It is emitted from the compiler (after the stats are collected), not by linting the
  generated page, when `FlagstaffCompiler.ReportExpressibleEscapes` is true (the default; set it false to opt out).
  The hint writes the block's first root as a generic element (`EscapeBlockStats.Suggestion` from
  `EscapeAnalyzer.Suggest`: one line, `...` after 4 props, 3 children or 3 levels; attributes as `Key: value`,
  `x:Bind ViewModel.X` → `vm.X` (`item.X` inside a template), resources → `resource()`/`theme()`, `x:` directives →
  their props, handlers → `on Event -> vm.{Handler without On}Command`, an `ItemTemplate` over `x:DataType` `T` →
  `of T` with the template as the body), names the keyword when the root type has one, and adds
  `replace Click="..." with on Click -> vm.XCommand` for `event:` findings the suggestion leaves out and
  `write x:Uid -> uid:` for `prop:` findings.
- CLI: `check` and `compile` add `"stats": { flagLines, escapeBlocks, escapeLines, escapeRatio, keywordElements,
  genericElements, genericRatio, files: [{file, flagLines, escapeLines, generic, keyword}], escape: [{file,
  line, lines, reasons, rootTypes, findings}] }` (ratios rounded to 4 places). Text mode prints
  `stats: 170 flag lines, 1 xaml block (1 line, 0.6%), 106 elements (0 generic)` after the diagnostics and one
  `  xaml <file>(<line>): <reasons> [<root types>]` line per block. `flagstaff check --stats-only` prints only the
  stats (the JSON envelope has no diagnostics; the exit code still reflects errors).

## Diagnostic codes

| Code | Severity | Meaning |
|---|---|---|
| FS1000 | error | Unknown element keyword (suggestion + hint: write the WinUI type in PascalCase; `xaml { }` only for what the manifest cannot express); `when`/`enabled`/`if` written as an element hint the clause placement (`button "Save" -> vm.Save when(vm.CanSave);`) |
| FS1001 | error | Unknown property (suggestion; `value:` hints the positional binding) |
| FS1002 | error | Property exists, but not on this keyword (lists the allowed props) |
| FS1003 | error | Invalid enum value (variant, theme backdrop/titlebar, `format:` kind) |
| FS1004 | error | Label / positional binding / `{ }` block / `->` not allowed on this keyword |
| FS1005 | error | Property value has the wrong shape (e.g. `cols: "3"`, a bare non-flag prop, `options: [1]`, `width: wide`) |
| FS1010 | error | More than one `fill` child in the same vertical container |
| FS1011 | error | `row cols: N` children need more than N columns (no `wrap`), or `span` wider than `cols` |
| FS1012 | warning | Layout prop with no effect where it is (`width: auto` outside a grid, `span` outside row/cards, ...) |
| FS1013 | warning | `fill` inside a scrolling region |
| FS1020 | error | `toast` / `confirm` / `dialog` inside a `list` / `table` template (overlays are page-level) |
| FS1021 | error | `toast` / `confirm` / `dialog` in a view without `bind vm:`, or without `when(vm.Flag)` |
| FS1022 | error | `confirm` with both `->` and its own buttons, with neither, or `confirmLabel:` next to its own buttons |
| FS1023 | warning | `button` / `filePicker` / `link` without `->` (it does nothing) |
| FS1024 | error | `-> cancel` outside a `dialog` / `confirm`, or on something other than a `button`; `on Event -> cancel` outside one |
| FS1033 | error | Pass-through attribute or property element on a keyword element no emitter wrote (XamlGenerator, `ReportUnsupportedPassThrough`); also an unwritten `on Event` (`ReportUnemittedEvents`) |
| FS1030 | error | Unknown or abstract WinUI type in a generic element (`SuggestTypes`); a UWP-only control (`GenericValidator.UwpOnlyControls`: `InkCanvas`, `InkToolbar*`, `MediaElement`, `WebView`, `CaptureElement`, `SearchBox`, `SettingsFlyout`) names its WinUI 3 replacement |
| FS1034 | error | `on Event` names no event of the element's manifest type / keyword root type, or a keyword without a root tag (`ValidateEvents`); an event a variant tag lacks (`CheckVariantTag`) |
| FS1031 | error | Unknown property on the type (suggestions: `ControlManifest.PropertySynonyms` first, e.g. `Header` → `Title` on `MenuBarItem`, then Levenshtein), unknown attached member, unknown member in `ref(Name).Member`, a `Setter Property:` not on the Style's `TargetType`, or a `Storyboard.TargetProperty` not on the target |
| FS1032 | error | Generic value shape mismatch (expected CLR type / enum members), including `Setter Value:` against its target property and an animation against its `Storyboard.TargetProperty`; `load()` outside `load:` |
| FS1035 | error | Children on a type without content property, too many for a one-object property, or wrong item type; a non-UIElement as content of the body or a keyword (`CheckUiContent`) |
| FS1036 | error | `prefix.Type` element with an undeclared `use` prefix (parser) |
| FS1037 | error | Unknown attached-property owner (`Gird.Row`, with a suggestion) |
| FS1038 | error | `Grid.Row/Column/RowSpan/ColumnSpan` not on a direct child of a generic `Grid` with definitions, non-literal, or past them |
| FS1039 | error | `ref(Name)` names no `name:` declaration in the view |
| FS1040 | error | Reference property (`Setter.Target`, `TeachingTip.Target`, `RelativePanel.*`) not written as `ref(Name)` |
| FS1041 | error | Attached property not in the locality allowlist (`Manifest/overlay.json`) |
| FS1042 | error | Duplicate `name:` in a view (SemanticValidator, from `ViewDecl.NamedElements`) |
| FS1043 | error | Duplicate `on Event` on one element, or an event the element's arrow already wires (`ValidateEvents`) |
| FS1044 | error | `key:` outside a resources block or on a keyword element; a resource entry without `key:` (implicit `Style TargetType:` excepted) (`ValidateResources`) |
| FS1045 | error | Duplicate key in one resources block (`ValidateResources`) |
| FS1046 | error | `Load: load(vm.X)`: use the universal prop `load:` (`CheckLoad`) |
| FS1047 | error | `of T` on a generic element that types nothing (`CheckOfTypeUsed`) |
| FS1048 | error | Keyword `icon:` names no `Symbol` enum member (`SemanticValidator.UnknownIcon`: `IconSynonyms`, else the closest member, in kebab-case) |
| FS1050 | error | Unexpected character |
| FS1051 | error | Parse error (missing `;`, unbalanced braces, bad condition, nested binding path `vm.A.B`, props on `xaml` with a wrap hint, ...) |
| FS1052 | error | Command argument other than `(item)` |
| FS2003 | error | `toast` / `confirm` / `dialog` need a bare `when(vm.Bool)` |
| FS2004 | error | Type mismatch in a condition (explicit type vs literal, `null` vs value type, ordering strings/bools) |
| FS2005 | error | `vm.X` (condition or binding) inside an item template, or `item.X` outside one |
| FS2006 | warning | Legacy `onload -> vm.XAsync;` — write `onload -> vm.X;` and implement `OnXAsync()` |
| FS2007 | error | `-> vm.XCommand(item)` / `on Event -> vm.XCommand(item)` outside a `table`/`list` with `of T` (or a SelectionChanged/ItemClick source with `of T`) |
| FS2010 | error | Conflicting explicit types for one item property (`Order.Total` currency here, date elsewhere) |
| FS2011 | error | `empty` outside a `table`/`list`, or its collection has no `items:` |
| FS2012 | error | `col` outside a `table`, or a `table` child other than `col` / `button` / `empty` |
| FS2013 | error | `select display:` without `items: vm.X of T` |
| FS2014 | error | `->` on a generic element without a `Command` property (`vm.X`) or `Click` event (navigate), or `-> cancel` |
| FS2015 | error | `on Event -> vm.X` where `vm.X` is a property of the view (TypeInference) |
| FS2016 | warning | A generated member is also declared in a hand-written partial (`UserDeclarations`): the generator skips (or adapts) its own (`ViewContract.For`, `ItemTypeRegistry.Generate`) |
| FS2017 | warning | An implemented `partial On{X}Async` in a ViewModel partial has no declaration: the generator declares it (`ViewContract.UndeclaredPartials`) |
| FS2018 | error | A hand-written member's type cannot work with the winning `.flag` usage (`DeclaredTypes.Check`: binding target, `format:` converter, `of T` collection, condition literal), at the C# declaration (`ViewContract.TypeDiagnostic`; `ItemTypeRegistry` for item properties) |
| FS2019 | error | The only hand-written `On{X}Async` has a parameter list its command cannot call (a parameter where no row is passed, another row type, two or more parameters): at the C# declaration, naming the generated signature and `-> vm.X(item)` (`ViewContract.PlanCommand`) |
| FS3001 | error | Generated XAML is not well-formed XML |
| FS3002 | error | `<FontIcon>` without `Glyph` renders nothing |
| FS3003 | warning | Unknown `Symbol` / NavigationViewItem `Icon` value in the generated XAML (in practice: inside `xaml { }`; keyword `icon:` is FS1048) |
| FS3004 | error | `Grid.Column` exceeds the column count |
| FS3005 | warning | `x:Bind ViewModel.` inside a DataTemplate |
| FS3006 | error | DataTemplate uses x:Bind without `x:DataType` |
| FS3007 | error | `<Window.Resources>` (not valid in WinUI 3) |
| FS3008 | error | Duplicate `x:Name` |
| FS3009 | warning | `x:Class` namespace does not match the app namespace |
| FS3010 | warning | Unknown `StaticResource` key: not in `XamlLinter.KnownResources` and no `x:Key` in the same page |
| FS3011 | warning | `xaml { }` block expressible as a generic element (from `EscapeAnalyzer`; at the `.flag` `xaml` keyword; on by default) |
| FS3012 | error | `x:Load` without `x:Name` in generated XAML (safety net; `load:` gets `FlagstaffLoad_n`) |
| FS4001 | warning | Generic element whose type has a keyword (`TextBox` → `textbox`) |
| FS4002 | warning | `prefix.Type` element not in the manifest: own properties pass through unvalidated |
| FS4003 | warning | Binding on an unvalidated element without `of T`: infers `object` (`CheckUntypedBindings`) |

Ranges: FS1000–1099 lexer/parser/schema, FS2000–2099 semantic/type, FS3000–3099 XAML lint. FS2001/FS2002 are unused.
