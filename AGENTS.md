# AGENTS.md: working with Flagstaff

Flagstaff is a small UI language (`.flag`) that compiles to WinUI 3 XAML and CommunityToolkit.Mvvm ViewModel partials.
In a Flagstaff app you write the screens in `.flag` and the logic in C#. The compiler writes everything in between.
This repository is the compiler itself. Its own conventions are in `CONTRIBUTING.md`, and the language reference is
`LANG.md` (shipped in the tool as `flagstaff spec`).

## The loop

Run these from the app directory (the one containing the `.csproj`):

```powershell
flagstaff check --format json     # 1. after every .flag edit; fix until "ok": true
flagstaff stubs --update          # 2. create ViewModels/*.cs partials, append missing On*Async stubs
#                                   3. implement the On*Async bodies in ViewModels/*.cs
flagstaff build --format json     # 4. MSBuild.exe + XAML compiler; fix until "ok": true
flagstaff run --no-build          # 5. launch the build you just made
```

Every command with `--format json` prints exactly one JSON object to stdout. Progress goes to stderr.

```json
{ "tool": "flagstaff", "version": "0.3.0-alpha.2", "command": "check", "ok": false,
  "diagnostics": [ { "severity": "error", "code": "FS1001", "message": "Unknown property 'value' on 'stat'",
                     "file": "Flags/dashboard.flag", "line": 7, "column": 20,
                     "hint": "use positional binding: stat \"Open\" vm.OpenCount;" } ],
  "outputs": [] }
```

Exit codes: `0` ok, `1` diagnostics or build errors, `2` bad usage, `3` environment problem (run `flagstaff doctor`),
`4` crash (report a bug).

| Command | Use |
|---|---|
| `flagstaff init <AppName> [-o dir] [--nav side\|top] [--agent claude\|cursor\|none]` | New runnable app in `dir` (default `./<AppName>`) |
| `flagstaff new view <Name> [--label "..."] [--icon name]` | Add a view file, its ViewModel partial, and a nav link |
| `flagstaff check [paths]` | Validate `.flag` files; writes nothing |
| `flagstaff compile [paths] [-o Generated]` | Write `Generated/` (the build does this too) |
| `flagstaff stubs [--update]` | Create or extend the hand-written ViewModel partials |
| `flagstaff build [-c Debug\|Release] [-p x64\|arm64]` | Build with MSBuild.exe |
| `flagstaff run [--no-build]` | Build and launch |
| `flagstaff watch [--run]` | Recompile (and relaunch) on every `.flag` change |
| `flagstaff doctor` | Check the SDK, MSBuild, and the project layout |
| `flagstaff spec [--section name] [--list]` | The language reference, or one section of it |

## Rules

1. **Never edit `Generated/`.** Every compile overwrites it. To change the output, change the `.flag` file.
2. **Hand-written code goes in partial classes**, for example `ViewModels/TasksViewModel.cs` next to the generated
   `TasksViewModel.g.cs`.
3. **Implement commands as** `private partial async Task OnSaveAsync() { ... }`. Don't declare `[RelayCommand]` or
   `[ObservableProperty]` for names that a `.flag` file already uses, because the generated file declares them.
4. **Prefer keyword > generic element > `xaml { }`.** Use a lowercase keyword when one exists (FS4001 warns on
   `TextBox` instead of `textbox`); otherwise write the WinUI type as a generic element (`TreeView name: Nodes;`);
   use `xaml { }` only for what the compiler cannot express (control templates, other markup extensions).
   Never invent lowercase keywords.
5. **Locality rule.** Every reference to another element is `ref(Name)` to a `name:` in the same view, never a
   string. `Grid.Row:`/`Grid.Column:` only on direct children of a generic `Grid` with `RowDefinitions:`/`ColumnDefinitions:`.
6. **Read the diagnostic code and hint** before changing anything. The hint usually contains the fix.
7. Build with `flagstaff build`, not `dotnet build`, which hides WinUI XAML compiler errors.

## Generated vs hand-written

| Generated (read-only) | Hand-written (yours) |
|---|---|
| `Generated/*View.xaml`, `*View.xaml.cs` | `Flags/*.flag` |
| `Generated/*ViewModel.g.cs`: `[ObservableProperty]` fields, `[RelayCommand]` wrappers, `partial` `On*Async` declarations | `ViewModels/*ViewModel.cs`: `On*Async` bodies, services, extra members |
| `Generated/<ItemType>.g.cs` (from `table`/`list`/`select ... of T`) | extra partials for item types and your models |
| `Generated/App.xaml`, `App.xaml.cs`, `MainWindow.*`, converters | `App.Services.cs`: `partial void ConfigureServices(IServiceCollection)`, `partial void OnAppStarting(List<Func<Task>> steps)` (async startup steps, awaited before the window), `partial void OnAppLaunched()` |

## The hand-written half

The generator owns every member a `.flag` file implies. The top of each generated `*.g.cs` / `*View.xaml.cs` lists
them, `flagstaff check --format json` prints them (`contract`, `itemTypes`), and `flagstaff build` hints C# errors
against them.

- ViewModel partial: never redeclare a generated property, its `_field`, or a command. Implement exactly the
  `On*Async` methods `flagstaff stubs` lists. Assign the inferred type: `stat`/`text`/`line` without `format:` are
  `string` (`format: number(0)` makes a `double`).
- Page partial (`public sealed partial class XView`): both constructors are generated; never write one. Put page
  setup in `partial void OnViewCreated() { ... }` (runs once, after `InitializeComponent`), load data with
  `onload -> vm.Load;`, and reach ViewModel members as `ViewModel.X`.
- If you declare a generated member yourself, the generator skips its own with warning FS2016 and uses your type;
  prefer deleting yours. A type that cannot work with the `.flag` usage (a `checkbox` on a `string`) is FS2018, and an
  `On*Async` whose parameters its command cannot pass (`OnDeleteAsync(Contact)` for `-> vm.Delete`) is FS2019: a
  command passes the row only as `-> vm.Delete(item)` inside a `table`/`list` of that type.
- Item types (`of Order`) are `public partial class Order` in `<App>.ViewModels` with only the `item.X` properties
  the views bind; add members in your own `public partial class Order` in that namespace.

## Conventions the compiler relies on

- `bind vm: TasksViewModel;` is the first line of every view.
- `onload -> vm.Load;` generates `LoadCommand`; implement `private partial async Task OnLoadAsync()`.
  `onload -> vm.LoadAsync;` also compiles but warns (FS2006).
- `button "Save" -> vm.SaveCommand;` generates `SaveCommand` and `OnSaveAsync()` (`-> vm.Save` is the same command).
- Row commands: `button "Edit" -> vm.EditCommand(item);` inside a `table`/`list` with `of Order` generates
  `OnEditAsync(Order item)`.
- Bindings are positional (`stat "Open" vm.OpenCount;`). Inside a `table`/`list` row use `item.X`, never `vm.X`.
- Tables: `table items: vm.Orders of Order { col "Name" bind: item.Name; button ...; empty "..."; }`.
  Free-form rows: `list items: vm.Orders of Order { text item.Name; ... }`.
- `vm.X` types are inferred: `checkbox`/`toggle` → `bool`, `number` → `double`, `format: currency` → `decimal`,
  `format: date` → `DateTimeOffset?`, `items: vm.X of T` → `ObservableCollection<T>`, `when(vm.X > 0)` → `int`,
  everything else `string` (`stat "Open" vm.OpenCount format: number(0);` for a number).
- `when(...)` / `enabled(...)` accept `!`, `&&`, `||`, parentheses, and comparisons with literals.
- `toast`, `dialog`, and `confirm` need `when(vm.Flag)`. Your code sets the flag to show them and clears it to close
  them (`confirm ... -> vm.XCommand` generates the Cancel button; in a `dialog`, `button "Cancel" -> cancel;`).
- Generic elements: `Expander Header: "More" IsExpanded: vm.ShowMore { ... }`. PascalCase keys are XAML properties,
  on keywords too (`textbox "Name" vm.Name Margin: "0,8";`); object values are `Key: Type { ... }`
  (`button "More" { Flyout: MenuFlyout { MenuFlyoutItem Text: "Copy" -> vm.CopyCommand; } }`).
- Events: `on TextChanged -> vm.SearchCommand` (also `navigate(View)`, `cancel`, `vm.XCommand(item)` in rows).
- References: `Setter Target: ref(Nav).DisplayMode`, `TeachingTip Target: ref(SaveButton)`; resources:
  `Page.Resources: { Style key: K TargetType: Border { ... } }` + `Style: resource(K)`, theme brushes `theme(K)`.

## Reading a diagnostic

```text
Flags/tasks.flag(12,9): error FS1000: Unknown element keyword 'treeview' [hint: use xaml { } for controls without a keyword]
```

`file(line,column): severity CODE: message [hint: ...]`. The code ranges:

- `FS10xx` parse, schema, layout, and overlays: unknown keyword, unknown or misplaced property, bad value.
- `FS20xx` semantic and type: condition types, `vm.`/`item.` scope, item types.
- `FS30xx` lint of the generated XAML. Usually caused by content in a `xaml { }` block.
- `FS40xx` style warnings: a generic element that has a keyword (`TextBox` → `textbox`), an unvalidated third-party type.
- `WMC`/`CS` codes come from `flagstaff build`. `CS8795`/`CS0103` about `On...Async` mean a missing
  implementation: run `flagstaff stubs --update`. C# errors against generated members (CS0102, CS0111, CS0759,
  CS0029, ...) carry a hint naming the `.flag` line that generates the member (see The hand-written half).

`flagstaff spec --section diagnostics` explains every code, and `flagstaff spec --section "common mistakes"` shows a
wrong/right pair for most of them.

## The most common mistakes

```flag
bind vm.DashboardViewModel;              // WRONG
bind vm: DashboardViewModel;             // RIGHT

stat "Count" value: vm.Count;            // WRONG: bindings are positional
stat "Count" vm.Count;                   // RIGHT

col "Name" vm.Name;                      // WRONG: columns bind to the row item
col "Name" bind: item.Name;              // RIGHT (and `col`, not `column`)

button "Cancel";                         // WRONG: no action (FS1023)
button "Cancel" -> cancel;               // RIGHT inside a dialog/confirm

list items: vm.Rows of Row { text vm.Title; }    // WRONG: FS2005
list items: vm.Rows of Row { text item.Title; }  // RIGHT

treeview items: vm.Nodes;                // WRONG: invented keyword
TreeView name: Nodes;                    // RIGHT: a WinUI type is a PascalCase generic element

TeachingTip Target: "SaveButton";        // WRONG: element references are never strings (FS1040)
TeachingTip Target: ref(SaveButton);     // RIGHT: SaveButton is a name: in this view (FS1039 if not)

stack { text "Total" Grid.Row: 1; }      // WRONG: keyword layouts place by position (FS1038)
row cols: 2 { text "Label"; text "Total"; }   // RIGHT (or a generic Grid with RowDefinitions:)
```

Also: every leaf element ends with `;`.

## When in doubt

Run `flagstaff spec --section <name>` (for example `tables`, `lists`, `inputs`, `conditions`, `overlays`,
`generic`, `locality`, `events`, `resources`, `viewmodel`) and follow it exactly. `flagstaff spec --list` shows all sections, and `flagstaff spec` prints the
whole reference.
