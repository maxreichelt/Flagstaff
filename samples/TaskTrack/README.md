# TaskTrack — Flagstaff sample

A small project/task tracker written in Flagstaff: a side-nav shell, a dashboard, a task table with filters,
row actions, an edit dialog and a delete confirmation, a task form, a master-detail project list, and a settings
page. Data is fake and in-memory (`Services/InMemoryTaskStore.cs`); nothing is saved between runs.

| Path | What it is |
|---|---|
| `Flags/*.flag` | The UI, in Flagstaff |
| `Generated/` | Compiler output (XAML, code-behind, `*ViewModel.g.cs`, row types, `App.xaml`), regenerated on every build, not committed |
| `ViewModels/*ViewModel.cs` | Hand-written halves of the generated view models: every `private partial Task On{X}Async(...)` |
| `App.Services.cs` | `ConfigureServices` (registers the store) and `OnAppLaunched` (smoke mode) |

## What each view demonstrates

| View | File | Features |
|---|---|---|
| Dashboard | `dashboard.flag` | pinned toolbar with a `select ... items:`; `cards cols: 4` with `stat variant:` and a compound condition `when(vm.OverdueCount > 0 && !vm.IsLoading)`; cards with `span: 2` holding `summary`/`line` with `format: number / percent / date`; an `alert` with a `navigate(...)` button. Generic elements: a `Grid` with declared `ColumnDefinitions:`/`RowDefinitions:` placing the `cards` and an "Up next" `card` with `Grid.Row:`/`Grid.Column:` (the locality rule); `Page.Resources: { Style key: TightCard TargetType: Border BasedOn: resource(FlagstaffCard) { Setter ...; } }` applied with `Style: resource(TightCard)`; `VisualStateManager.VisualStateGroups:` with `AdaptiveTrigger MinWindowWidth: 900` and `Setter Target: ref(UpNext).Visibility` collapsing the side card on narrow windows |
| Tasks | `tasks.flag` | filter `select`s with `on SelectionChanged -> vm.ApplyFiltersCommand;` (generated code-behind handler); a toolbar "More" `button` with `Flyout: MenuFlyout { MenuFlyoutItem ... -> vm.ExportCommand; ... enabled(vm.HasDone); }`; `KeyboardAccelerators: { KeyboardAccelerator Key: "N" Modifiers: "Control"; }` on Add Task (Ctrl+N); `table ... fill` with `col width:` (fixed and `3*`) and `format: date`; row click `-> vm.EditCommand(item)`; row actions `button ... -> vm.CompleteCommand(item) enabled(item.Status != "Done")` and `-> vm.RequestDeleteCommand(item)`; a row-level `ContextFlyout: MenuFlyout { MenuFlyoutItem Text: "Open" -> vm.EditCommand(item); ... }` (right-click a row); `empty "No tasks match."`; a `dialog` form; `confirm ... -> vm.DeleteCommand;` sugar (generated Cancel); `toast ... duration:` |
| Task form | `task_form.flag` | universal props: `placeholder:`, `hint:`, `readonly`, `min:/max:/step:`, `width:`/`align:` on a `stack`; toolbar Save with `variant: primary enabled(vm.Title != "")`; a small `dialog "Pick assignee"` whose Cancel is `-> cancel`; a button with `icon:`; `toast ... duration:` |
| Projects | `projects.flag` | `split fill` master-detail: `list items: vm.Projects selected: vm.Selected of ProjectCard width: 320` with a `card` template, and `group "Details" when(vm.Selected != null) fill` with `summary`, `progress` and a conditional `alert`; a dismissible "Tip" `card ... load: load(vm.ShowHint) Background: theme(SystemFillColorAttentionBackgroundBrush)` (`x:Load`, unloaded once dismissed); a generic horizontal `StackPanel` holding the heading and a `badge vm.SelectedOverdue Background: theme(SystemFillColorCriticalBrush)` |
| Settings | `settings.flag` | generic `TabView` with two `TabViewItem`s (General / About) holding keyword `group`s, `textbox`, `toggle`, `link`; `TabView SelectedIndex: vm.SelectedTab` (inferred `int`); an `Expander Header: "Advanced" IsExpanded: vm.ShowAdvanced` with a generic `Slider ... Value: vm.DefaultEstimate` (inferred `double`, used by the task form) and a `toggle` (used by the task list); `toast ... duration:` |

Generic elements are WinUI types written in Flagstaff's element form (`Expander Header: "Advanced" { ... }`); they are
used only where no keyword exists, so `flagstaff check` reports no FS4001. The sample has no `xaml { }` block:
`flagstaff check samples\TaskTrack` prints `0 xaml blocks` and a non-zero generic count.

The view models show the ViewModel conventions: `private partial async Task OnSaveAsync()`, typed row-action parameters
(`OnEditAsync(TaskRow item)`), `ObservableCollection<string>` for `select ... items:` without `of`, `T?` selections,
and `partial void On{X}Changed` hooks from CommunityToolkit.Mvvm for filters and the selected project.

## Build

Build WinUI projects with Visual Studio's `MSBuild.exe` (`dotnet build` hides XAML compiler errors; see
[`docs/building.md`](../../docs/building.md)). From the repository root.

**Default mode** (in-repo compiler and Stdlib; the build runs `dotnet run --project src\Flagstaff.CLI -- compile`
before XAML compilation):

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" samples\TaskTrack\TaskTrack.csproj -restore -p:Platform=x64 -p:Configuration=Debug -v:m
```

**Package mode** (consumes the `Flagstaff` NuGet package from `artifacts/packages`, mapped by
[`samples/nuget.config`](../nuget.config); this is what CI builds):

```powershell
foreach ($p in 'src/Flagstaff.Stdlib', 'src/Flagstaff.CLI', 'src/Flagstaff.Build') { dotnet pack $p -c Release -o artifacts/packages }
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" samples\TaskTrack\TaskTrack.csproj -restore -p:Platform=x64 -p:Configuration=Release -p:FlagstaffFromPackage=true -warnaserror -v:m
```

Repacking keeps the version number, so delete `%USERPROFILE%\.nuget\packages\flagstaff*` (and the sample's `obj`)
before rebuilding against fresh packages. Both modes build from a clean clone: `Generated/` is written and added to
the build by `Flagstaff.targets`.

To check the flags without building: `dotnet run --project src\Flagstaff.CLI -- check samples\TaskTrack`.

## Run

```powershell
& "samples\TaskTrack\bin\x64\Debug\net10.0-windows10.0.22621.0\TaskTrack.exe"
```

## Smoke mode

With `FLAGSTAFF_SMOKE=1` set, the app visits every nav item and the task form 500 ms apart, opens and closes each
overlay once through the same commands the buttons use (the "Pick assignee" dialog, the "Edit task" dialog, the
"Delete task?" confirm and its generated Cancel, a toast), runs More > Export CSV, expands Settings > Advanced and
selects the "About" tab, dismisses the Projects tip, and exits with code 0. An unhandled exception, a failed
navigation, a missing command, or a page whose `LoadCommand` faulted exits with code 1.

```powershell
$env:FLAGSTAFF_SMOKE = "1"
$p = Start-Process "samples\TaskTrack\bin\x64\Debug\net10.0-windows10.0.22621.0\TaskTrack.exe" -PassThru
$p.WaitForExit(60000); $p.ExitCode
```

## Tests

`test/Flagstaff.Tests` compiles `Flags/*.flag` in place: the `tasktrack` snapshot case
(`Snapshots/tasktrack.*`) pins every generated file and requires zero diagnostics, and `TaskTrackSampleTests`
checks that `ViewModels/*.cs` implements every `On*Async` partial the generator declares. After editing a flag,
run the tests and review the `*.received.*` files.
