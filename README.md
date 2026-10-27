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
<PackageReference Include="Flagstaff" Version="0.3.0-alpha.1" />
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
6. **Lint** the generated XAML for mistakes the WinUI XAML compiler reports poorly (`FS3xxx`), and measure what is still in `xaml { }` (the escape ratio `flagstaff check` prints).
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

The thesis is that LLMs produce correct native Windows apps more often in Flagstaff than in raw XAML.
[`bench/`](bench/README.md) measures it: ten app specs (five business, five consumer/creative) under three
conditions (raw WinUI XAML + C#, Flagstaff keywords only, Flagstaff with generic elements), with build-error feedback
for up to five fix rounds. Third run, on the compiler that skips members the hand-written C# already declares
(FS2016/FS2017), with a deliberately cheap model (`z-ai/glm-5.3`, 5 samples per pair, $2.83). Time and tokens are
medians over the runs that reached a build:

| | raw XAML | Flagstaff keywords | Flagstaff generic |
|---|---|---|---|
| Builds on the first answer | 14/50 | 26/50 (p = 0.02) | 27/50 (p = 0.01) |
| Builds within 5 fix rounds | 50/50 | 49/50 | 50/50 |
| Model calls to a build (mean) | 2.32 | 1.69 | 1.78 |
| First answer free of XAML/Flagstaff errors | 28/50 | 44/50 (p < 0.001) | 42/50 (p = 0.004) |
| Wall time to a building app | 53 s | 50 s | 54 s |
| Output tokens to a building app (visible answer + reasoning) | 6.9k (5.2k + 1.0k) | 6.5k (2.2k + 4.6k) | 6.5k (2.2k + 4.4k) |
| Input tokens per app (system prompt counted once) | 8.1k (7.0k) | 19k (19k) | 24k (24k) |
| Cost per building app | $0.015 | $0.022 | $0.020 |
| UI source lines per app (.xaml / .flag) | 135 | 59 | 58 |

**Confirmed on first-answer builds in this run; not on builds within the fix rounds**, where every condition
reaches 49–50 of 50. Flagstaff first answers build almost twice as often as raw XAML (Fisher p = 0.02 and 0.01) and
need fewer model calls to a build; the first two runs did not separate the conditions
([run 2](bench/results-run2.md): 13, 16, 16 of 50; [run 1](bench/results-run1.md): 9, 14, 12). The compiler and the
prompts changed together between runs, and this is one model and one run, so a repeat is needed before calling it
robust. Flagstaff first answers also have significantly fewer UI-layer errors and less than half the visible code
(p < 0.001), but this model spends the saving on reasoning, so time and output tokens to a build are about the same,
and the language reference in the prompt makes each app 1.3–1.5× as expensive. Details, per-app results, error
codes and caveats: [bench/results.md](bench/results.md).

## Status

**0.3.0-alpha.** Expect breaking changes before 1.0.

- Windows only, WinUI 3 (Windows App SDK) only. WPF, UWP, and MAUI are not planned.
- Keywords cover layout (`row`, `split`, `cards`, `fill`, a pinned `toolbar`), forms, stat cards, tables and `list` templates with row actions and empty states, `when(...)`/`enabled(...)` conditions, alerts, hoisted `dialog`/`confirm` overlays, and auto-dismissing toasts.
- 0.3 adds generic elements for every other WinUI control, XAML props and property elements on keywords (`Margin:`, `Flyout:`, `ContextFlyout:`, `KeyboardAccelerators:`), `on Event -> target` handlers, `ref(Name)` element references, and keyed resources, styles, visual states, and storyboards. `xaml { }` remains for what the compiler has not learned yet.
- The language reference is [LANG.md](LANG.md) (also `flagstaff spec`) and the release history is [CHANGELOG.md](CHANGELOG.md).
- The [benchmark](#benchmark) above uses one cheap model; its third run shows a significant advantage in first-answer builds (not yet repeated).

### What remains

Planned, not promised:

- Control template internals (`ControlTemplate`, `{TemplateBinding}`); today they need `xaml { }`.
- Navigation parameters and a back button.
- Text interpolation in labels (`text "Hello {vm.Name}";`).
- A small optional runtime library for behavior that generated code cannot express well.

## Prior art

- **Microsoft Reactor** is a React-style C# runtime library for WinUI 3. Flagstaff is a compiler instead, and its output is XAML + MVVM with no runtime dependency.
- **Microsoft's WinUI agent plugin** helps agents write XAML directly. Flagstaff gives agents a smaller language and checks it before any XAML exists. **Ammy** (WPF XAML sugar) explored a similar idea earlier.
- **OpenUI Lang** and **Google A2UI** are web protocols that describe UI for rendering at runtime. Flagstaff works at compile time and targets native desktop.

## Building from source

```powershell
dotnet build
dotnet test
```

[docs/building.md](docs/building.md) covers building WinUI 3 apps (and why to use `MSBuild.exe`). [CONTRIBUTING.md](CONTRIBUTING.md) covers snapshots and adding keywords.

## License

[MIT](LICENSE) © 2026 Maximilian Reichelt and Flagstaff Contributors
