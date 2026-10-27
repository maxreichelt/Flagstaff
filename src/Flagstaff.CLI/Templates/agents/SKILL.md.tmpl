---
name: flagstaff
description: Use when creating or editing Flagstaff .flag files, or the ViewModels and App.Services.cs of a Flagstaff WinUI 3 app (an app with a Flags/ directory or a Flagstaff package reference), or when fixing FS diagnostics.
---

# Flagstaff

Flagstaff compiles `.flag` files (in `Flags/`) to WinUI 3 XAML and CommunityToolkit.Mvvm ViewModel partials (in `Generated/`).
You write the screens in `.flag` and the logic in `ViewModels/*.cs`.

## Loop (run from the folder containing the .csproj)

1. `flagstaff spec --section <name>` before writing unfamiliar syntax (`--list` shows the sections).
2. `flagstaff check --format json`: fix every diagnostic until `"ok": true`. Apply the `hint`.
3. `flagstaff stubs --update`: creates `ViewModels/*.cs` partials and appends missing `On*Async` stubs.
4. Implement each body as `private partial async Task OnSaveAsync() { ... }`.
5. `flagstaff build --format json`: fix until `"ok": true`. Never use `dotnet build`; it hides XAML compiler errors.
6. `flagstaff run --no-build`.

JSON output: `{ tool, version, command, ok, diagnostics: [{severity, code, message, file, line, column, hint}], outputs }` on stdout.
Exit codes: 0 ok, 1 diagnostics/build errors, 2 usage, 3 environment (`flagstaff doctor`), 4 crash.

## Rules

- Never edit `Generated/`. Change the `.flag` file instead.
- Don't redeclare `[ObservableProperty]` or `[RelayCommand]` members (or their `_fields`) that a `.flag` file already uses; the top of each `Generated/*.g.cs` lists them. Implement only the `On*Async` methods `flagstaff stubs` lists. If you declare one anyway, the generator skips its own (warning FS2016); prefer deleting yours.
- Page partials (`public sealed partial class XView`) never declare a constructor (both are generated): use `onload -> vm.Load;` and `partial void OnViewCreated() { ... }`. Item types (`of T`) are `public partial class T` in `<App>.ViewModels`.
- `stat`/`text`/`line` without `format:` bind a `string`; `format: number(0)` makes a `double`. `flagstaff build` hints C# errors against generated members: read the hint.
- DI registrations and startup code go in `App.Services.cs` (`partial void ConfigureServices(IServiceCollection services)`, `partial void OnAppStarting(List<Func<Task>> steps)` for async startup, `partial void OnAppLaunched()`).
- Prefer keyword > generic element > `xaml { }`. No keyword? Write the WinUI type as a PascalCase generic element (`TreeView name: Nodes;`). Use `xaml { }` only for what the compiler cannot express yet (control templates). Never invent lowercase keywords.
- Locality rule: reference other elements with `ref(Name)` (a `name:` in the same view), never a string. `Grid.Row:`/`Grid.Column:` only on direct children of a generic `Grid` with `RowDefinitions:`/`ColumnDefinitions:`.
- Diagnostics: FS10xx syntax/schema/layout, FS20xx types and `vm.`/`item.` scope, FS30xx lint of the generated XAML, FS40xx style (`TextBox` → `textbox`).

## Syntax reminders

- Views start with `bind vm: NameViewModel;` then `onload -> vm.Load;` (implemented as `OnLoadAsync()`).
- Commands always end in `Command`: `button "Save" -> vm.SaveCommand variant: primary;` → `OnSaveAsync()`.
- Bindings are positional: `stat "Open" vm.OpenCount;`, `textbox "Name" vm.Name placeholder: "Full name";`.
- Tables: `table items: vm.Orders of Order { col "Name" bind: item.Name; button "Edit" -> vm.EditCommand(item); empty "None."; }` → `OnEditAsync(Order item)`.
- Free-form rows: `list items: vm.Orders of Order { text item.Name; checkbox "Done" item.IsDone; }`. Inside rows use `item.X`, never `vm.X`.
- Layout: `row cols: 2 { ... }`, `split { a width: 320; b; }`, `cards cols: 3 { stat ...; }`, `fill` for the element that takes the remaining height.
- Conditions: `when(vm.Count > 0 && !vm.IsBusy)`, `enabled(vm.Selected != null)`.
- Overlays: `dialog "Edit" when(vm.IsEditOpen) { ...; button "Cancel" -> cancel; button "Save" -> vm.SaveCommand; }`, `confirm "Delete?" when(vm.IsDeleteOpen) -> vm.ConfirmDeleteCommand;`, `toast "Saved." when(vm.ShowSaved) duration: 3000;`. Your code sets and clears the flags.
- Generic elements and XAML props: `Expander Header: "More" IsExpanded: vm.ShowMore { ... }`, `textbox "Name" vm.Name Margin: "0,8";`, `button "More" { Flyout: MenuFlyout { MenuFlyoutItem Text: "Copy" -> vm.CopyCommand; } }`.
- Events and references: `on TextChanged -> vm.SearchCommand;`, `Setter Target: ref(Nav).DisplayMode Value: Inline;`, `Style: resource(Key)` for a `key:` in `Page.Resources: { ... }`.
- Every leaf element ends with `;`.

## When in doubt

Run `flagstaff spec --section <name>` and follow it exactly. See `AGENTS.md` for the full contract.
