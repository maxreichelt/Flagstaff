# Flagstaff

[![CI](https://github.com/maxreichelt/Flagstaff/actions/workflows/ci.yml/badge.svg)](https://github.com/maxreichelt/Flagstaff/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/Flagstaff.CLI.svg?label=Flagstaff.CLI)](https://www.nuget.org/packages/Flagstaff.CLI)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**A small, compiled UI language for native Windows apps. You write `.flag` files; Flagstaff generates the WinUI 3 XAML and CommunityToolkit.Mvvm code you would otherwise write by hand.**

## Why

- **Native WinUI 3, no WebView.** The output is ordinary XAML pages, code-behind, and ViewModels. There is no runtime library and no extra rendering layer.
- **A syntax that LLMs and people get right.** Every element has the same shape (`keyword "label" vm.Binding key: value;`). The compiler takes care of the details that usually break by accident: `x:Bind` paths, `DataTemplate` types, converters, command wiring, and DI registration.
- **All of WinUI, not just the keywords.** Keywords cover the common 80% of business apps with Flagstaff's defaults. Every other WinUI control is a *generic element* (`Expander Header: "Advanced" { ... }`) in the same syntax, checked against a manifest generated from the Windows App SDK: unknown types, properties, events, and wrong value shapes are errors at the `.flag` line.
- **The locality rule.** Each line is right or wrong on its own. Where XAML depends on text elsewhere (`Grid.Row` vs. the parent's `RowDefinitions`, `ElementName` strings, resource keys), Flagstaff has a form the compiler resolves and checks: `Grid.Row:` against the generic `Grid`'s rows, `ref(Name)` against `name:` declarations, `resource(Key)` against declared keys.
- **You own the output.** Generated files are plain and readable. Your logic goes in partial classes next to them, so you can regenerate at any time, or stop using Flagstaff and keep the code.

## Example

`Flags/app.flag` and `Flags/tasks.flag`:

```flag
app TaskTrack {
    theme { accent: #2b6cb0; }
    nav side { brand "TaskTrack"; link "Tasks" icon: list -> TasksView; }
}

view TasksView {
    bind vm: TasksViewModel;
    onload -> vm.Load;

    toolbar {
        heading "Tasks" icon: list;
        spacer;
        button "New task" -> vm.NewTaskCommand;
    }
    cards cols: 2 {
        stat "Done" vm.DoneRatio format: percent;
        stat "Overdue" vm.OverdueCount variant: danger when(vm.OverdueCount > 0);
    }
    table items: vm.Tasks of TaskItem {
        col "Title" bind: item.Title;
        col "Due"   bind: item.DueDate format: date;
    }
}
```

These are excerpts from the real compiler output. `Generated/TasksView.xaml`:

```xml
<Page x:Class="TaskTrack.TasksView" x:Name="FlagstaffPage" Loaded="OnViewLoaded" ...>
  ...
  <Button Content="New task" Command="{x:Bind ViewModel.NewTaskCommand, Mode=OneWay}" />
  ...
  <TextBlock Text="{x:Bind ViewModel.DoneRatio, Mode=OneWay, Converter={StaticResource PercentConverter}}" ... />
  <Border Style="{StaticResource FlagstaffStatCard}"
          Visibility="{x:Bind local:TasksView.When_1(ViewModel.OverdueCount), Mode=OneWay}">
  ...
  <ListView ItemsSource="{x:Bind ViewModel.Tasks, Mode=OneWay}">
    <ListView.ItemTemplate>
      <DataTemplate x:DataType="vm:TaskItem">
        ...
        <TextBlock Grid.Column="1" Text="{x:Bind DueDate, Converter={StaticResource DateConverter}}" />
```

`Generated/TasksViewModel.g.cs` (types are inferred from usage):

```csharp
public partial class TasksViewModel : ObservableObject
{
    [ObservableProperty] private double _doneRatio;
    [ObservableProperty] private int _overdueCount;
    [ObservableProperty] private ObservableCollection<TaskItem> _tasks = new();

    [RelayCommand] private async Task LoadAsync() { await OnLoadAsync(); }
    private partial Task OnLoadAsync();
    [RelayCommand] private async Task NewTaskAsync() { await OnNewTaskAsync(); }
    private partial Task OnNewTaskAsync();
}
```

You write the logic in `ViewModels/TasksViewModel.cs`:

```csharp
public partial class TasksViewModel
{
    private partial async Task OnLoadAsync() { /* fill Tasks, DoneRatio, OverdueCount */ }
    private partial async Task OnNewTaskAsync() { /* ... */ }
}
```

The compiler also generates `TaskItem.g.cs`, `TasksView.xaml.cs` (with the `Loaded` handler and the `When_1` function), `MainWindow`, `App.xaml`/`App.xaml.cs` (DI and ViewModel registration), and the converters.

Controls without a keyword are PascalCase generic elements. They nest with keywords in both directions, bind with the mode the manifest gives each property (`IsExpanded` and `Value` two-way here), and type their ViewModel members from it (`bool ShowAdvanced`, `double CacheMb`):

```flag
view SettingsView {
    bind vm: SettingsViewModel;

    Expander Header: "Advanced" IsExpanded: vm.ShowAdvanced {
        toggle "Telemetry" vm.Telemetry;
        Slider Header: "Cache size (MB)" Minimum: 64 Maximum: 2048 Value: vm.CacheMb;
    }
    TabView {
        TabViewItem Header: "General" IsClosable: false { textbox "Display name" vm.DisplayName; }
        TabViewItem Header: "Account" IsClosable: false { button "Sign out" -> vm.SignOutCommand variant: danger; }
    }
}
```

## Quick start

Requirements: Windows 10/11, .NET 10 SDK, and Visual Studio 2022+ with the .NET desktop development workload (Flagstaff builds with its MSBuild; see [docs/building.md](docs/building.md)).

```powershell
dotnet tool install -g Flagstaff.CLI
flagstaff init MyApp          # options: -o <app dir> (default ./MyApp), --nav side|top, --agent claude|cursor|none
cd MyApp
flagstaff run
```

`flagstaff doctor` checks your environment. `flagstaff new view Reports --icon document` adds a screen.

To add Flagstaff to an existing WinUI 3 project, reference the package next to `Microsoft.WindowsAppSDK` and put your `.flag` files in `Flags/`:

```xml
<PackageReference Include="Flagstaff" Version="0.3.0-alpha.2" />
```

The package adds MSBuild targets that compile `Flags/` into `Generated/` before XAML compilation. You can override `FlagstaffInputDir`, `FlagstaffOutputDir`, `FlagstaffNamespace`, and `FlagstaffCliPath`.

## Using Flagstaff with AI agents

Flagstaff is designed for coding agents to write. [AGENTS.md](AGENTS.md) has the full contract, and `flagstaff init --agent claude|cursor` adds the matching agent files to a new app. The loop:

```text
edit Flags/*.flag
flagstaff check --format json     # diagnostics with codes (FS1001) and hints
flagstaff stubs --update          # create/update ViewModels/*.cs partials
implement On*Async bodies in ViewModels/*.cs
flagstaff build --format json     # MSBuild + XAML compiler errors as JSON
flagstaff run
```

`flagstaff spec --section <name>` prints the relevant part of the language reference, which helps keep an agent's context small.

## How it works

1. **Lex and parse** each `.flag` file into an AST that records source locations.
2. **Validate** keyword elements against the keyword schema, and generic elements and XAML props against the **control manifest** (every `DependencyObject` type in `Microsoft.UI.Xaml.*` with its properties, CLR types, content property, events, and enum members, generated from the Windows App SDK metadata, plus a small hand-curated overlay for binding modes, keyword equivalents, and attached-property locality). Unknown names get suggestions (`FS1xxx`).
3. **Check the locality rule**: `Grid.Row` against the parent `Grid`'s definitions, `ref(Name)` against `name:` declarations, `Setter Property:` against the style's `TargetType`, attached properties against an allowlist.
4. **Infer types** for every `vm.X` from how it is used (`checkbox` gives `bool`, `format: currency` gives `decimal`, `of T` gives `ObservableCollection<T>`, `Slider Value:` gives the manifest's `double`), then compile `when(...)`/`enabled(...)` conditions into typed static functions (`FS2xxx`).
5. **Emit** XAML, code-behind (including generated `on Event ->` handlers), ViewModel partials, and item types for each view.
6. **Lint** the generated XAML for mistakes the WinUI XAML compiler reports poorly (`FS3xxx`), and measure how much of the source is `xaml { }` (the escape ratio `flagstaff check` prints).
7. **Scaffold** `App.xaml`, `App.xaml.cs`, `MainWindow`, and converters. MSBuild then compiles everything as a normal WinUI 3 project.

| Generated (overwritten on every compile, never edit) | Hand-written (yours, never touched) |
|---|---|
| `Generated/*View.xaml`, `*View.xaml.cs` | `Flags/*.flag` |
| `Generated/*ViewModel.g.cs` (properties, commands) | `ViewModels/*ViewModel.cs` (`On*Async` bodies, logic) |
| `Generated/<ItemType>.g.cs` | extra partials for item types, services, models |
| `Generated/App.xaml`, `App.xaml.cs`, `MainWindow.*` | `App.Services.cs` (`ConfigureServices`, `OnAppStarting`, `OnAppLaunched`) |
| `Generated/*Converter.g.cs`, `Converters.g.cs` | anything inside `xaml { }` blocks in `.flag` files |

`.flag` files have three layers, in order of preference: **keyword > generic element > `xaml { }`**.

## Benchmark

We examined whether LLMs produce correct native Windows apps more often in Flagstaff than in raw XAML. A benchmark
gave an inexpensive open-weight model (`z-ai/glm-5.3`) ten app specs (five business, five consumer/creative) under three
conditions (raw WinUI XAML + C#, Flagstaff keywords only, Flagstaff with generic elements), with build errors
returned for up to five fix rounds, 5 samples per pair. Results (Fisher p against raw XAML in brackets):

| | raw XAML | Flagstaff keywords | Flagstaff generic |
|---|---|---|---|
| Builds on the first answer | 14/50 | 26/50 (p = 0.02) | 27/50 (p = 0.01) |
| Builds within 5 fix rounds | 50/50 | 49/50 | 50/50 |
| First answer free of UI-layer (XAML/Flagstaff) errors | 28/50 | 44/50 (p < 0.001) | 42/50 (p = 0.004) |
| Model calls to a build (mean) | 2.32 | 1.69 | 1.78 |
| Cost per building app | $0.015 | $0.022 | $0.020 |
| UI source lines per app (.xaml / .flag) | 135 | 59 | 58 |

**The advantage holds for first-answer builds with one inexpensive model, not for builds within the fix rounds**, where
every condition reaches 49–50 of 50. Measurements with older compiler versions and prompts did not separate the
conditions, so the result needs replication before it counts as robust. The language reference in the prompt makes
each app 1.3–1.5× as expensive. The harness, app specs, prompts and raw results are kept outside this repository.

## Status

**0.3.0-alpha.** Expect breaking changes before 1.0.

- Windows only, WinUI 3 (Windows App SDK) only. WPF, UWP, and MAUI are not planned.
- Keywords cover layout (`row`, `split`, `cards`, `fill`, a pinned `toolbar`), forms, stat cards, tables and `list` templates with row actions and empty states, `when(...)`/`enabled(...)` conditions, alerts, hoisted `dialog`/`confirm` overlays, and auto-dismissing toasts.
- Generic elements cover every other WinUI control, with XAML props and property elements on keywords (`Margin:`, `Flyout:`, `ContextFlyout:`, `KeyboardAccelerators:`), `on Event -> target` handlers, `ref(Name)` element references, and keyed resources, styles, visual states, and storyboards. `xaml { }` covers the rest (control templates, other markup extensions).
- The language reference is [LANG.md](LANG.md) (also `flagstaff spec`) and the release history is [CHANGELOG.md](CHANGELOG.md).
- The [benchmark](#benchmark) above uses one inexpensive model and shows a significant advantage in first-answer builds; it has not been replicated.

### Remaining work

- Control template internals (`ControlTemplate`, `{TemplateBinding}`), which need `xaml { }`.
- Navigation parameters and a back button.
- Text interpolation in labels (`text "Hello {vm.Name}";`).
- A small optional runtime library for behavior that generated code cannot express well.

## Approach

Flagstaff takes a different approach to letting agents build coherent UIs in .NET. Other methods have been new
component models delivered as C# runtime libraries, tooling that feeds build errors and documentation back to a model
while it emits XAML, or a local web view in place of native UI. Flagstaff instead designs the interface the model
writes. XAML is a format Microsoft designed from the start to be produced by visual design tools as much as written by
hand; `.flag` is an alternative front end to it, built around what lets a model write correctly the first time rather
than around patching each observed failure with another tool. Concretely:

- **Familiar.** Braces, `key: value` props, and quoted labels: the shape of code models already write fluently, with
  no closing tags to match.
- **One shape.** Every element is `keyword "label" vm.Binding key: value when(...) { children }`, so the whole
  language fits in a prompt and syntax not yet seen is predictable.
- **Local.** Everything about an element sits on that element: its binding, props, condition, event handlers, and
  children. There is no cross-file wiring of names, handlers, or templates to keep in sync, and the compiler checks
  each line in the context of its parent.
- **Checked before any XAML exists.** Unknown keywords, props, and values, type mismatches, and layout that would
  silently do nothing are reported with a code, a location, and a hint, instead of surfacing as XAML compiler output
  or a runtime binding failure.

The premise is that an iterate-until-it-builds loop treats code the way reinforcement learning treats weights: any
configuration that produces the behaviour is as good as any other. That is fine when only the running app matters.
It is not fine when the code must also be readable and intuitive, because a model usually starts from a coherent
form and then tweaks it, cycle by cycle, until the build passes, and what passes is rarely the form it started with.
Code generated correctly early stays close to its design, and code that stays close to its design is easier to
integrate, troubleshoot, and modify later.

## Building from source

```powershell
dotnet build
dotnet test
```

[docs/building.md](docs/building.md) covers building WinUI 3 apps (and why to use `MSBuild.exe`). [CONTRIBUTING.md](CONTRIBUTING.md) covers snapshots and adding keywords.

## License

[MIT](LICENSE) © 2026 Maximilian Reichelt and Flagstaff Contributors.

The license covers Flagstaff itself: the compiler, the CLI, the build package, and this repository. It does not
extend to what Flagstaff produces for you. The following are yours, under whatever terms you choose, and carry no
obligation to reproduce the MIT notice:

- the XAML, C#, and other files the compiler writes into your project (`Generated/`);
- the files that `flagstaff init` and `flagstaff new view` create from Flagstaff's templates;
- the `Flagstaff.Stdlib` resources as embedded in an application you build with it.
