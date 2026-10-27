# Flagstaff Language Reference

Flagstaff 0.3. Read this once, then write `.flag` files. `flagstaff spec --section <name>` prints one `##` section
(case-insensitive prefix match: `--section tables`; `--list` lists them).

## Overview

Flagstaff compiles `.flag` files to WinUI 3 XAML, code-behind, and CommunityToolkit.Mvvm ViewModel partials.

- One `app` declaration per project (`Flags/app.flag`): theme and navigation shell.
- One `view` per screen (`Flags/<name>.flag`): a `Page` plus a generated `<Name>ViewModel.g.cs` partial.
- Every `vm.X` becomes an `[ObservableProperty]` (type inferred from usage) or a `[RelayCommand]`.
- Command bodies, data loading, and services are hand-written C# in `ViewModels/*.cs` (`On{X}Async` methods).
- `Generated/` is overwritten on every compile. Never edit it.

**Design boundary.** Three layers, in order of preference: **keyword > generic element > `xaml { }`**.

1. **Keywords** (lowercase: `textbox`, `table`, `card`) cover the common 80% of business apps: layout, forms, stat
   cards, tables, lists, alerts, dialogs, toasts, and navigation, with Flagstaff's defaults built in.
2. **Generic elements** (PascalCase: `Expander`, `TabView`, `MenuFlyout`) are any WinUI type, written in the same
   element form and checked against a control manifest generated from the Windows App SDK. PascalCase keys are
   XAML properties, on generic and keyword elements alike (`Margin:`, `Flyout:`).
3. **`xaml { }`** is verbatim XAML for what the compiler cannot express (see XAML Escape).

Everything the compiler accepts obeys the **locality rule**: a line is right or wrong on its own, or the compiler
checks the reference it makes and reports the error at that line (see Locality Rules). Not expressible:
navigation parameters on buttons, a back button, text interpolation (`"Hello {vm.Name}"`), declarative validation,
control template internals, drag and drop. Never invent a lowercase keyword; the compiler rejects it (FS1000).

```flag
view HelloView {
    bind vm: HelloViewModel;
    onload -> vm.Load;

    toolbar {
        heading "Hello" icon: home;
        spacer;
        button "Refresh" -> vm.RefreshCommand variant: primary;
    }
    textbox "Name" vm.Name placeholder: "Your name";
    text vm.Greeting when(vm.Name != "");
}
```

This generates `HelloView.xaml`, `HelloView.xaml.cs`, and `HelloViewModel.g.cs` with `string Name`,
`string Greeting`, `LoadCommand` → `OnLoadAsync()`, and `RefreshCommand` → `OnRefreshAsync()`.

## Universal Element Form

Every element inside a view has one shape. Label and binding come first, in that order; everything after them
may appear in any order:

```text
name ["label"] [vm.X | item.X] { key: value | Key: value | flag | when(c) | enabled(c) | format: kind | of Type
                                | -> target | on Event -> target } ( ";" | "{" children "}" )
```

| Part | Example | Notes |
|---|---|---|
| name | `textbox`, `Expander`, `lvc.CartesianChart` | A lowercase keyword from the tables below (`filePicker` is the only camelCase one), or a PascalCase WinUI type (see Generic Elements) |
| label | `"Name"` | Positional string. Plain text: no escapes, no interpolation |
| binding | `vm.Name`, `item.Name` | Positional, never `value: vm.X`. `item.X` only inside a `list`/`table` row |
| prop | `cols: 3`, `width: 2*`, `placeholder: "x"` | Lowercase: Flagstaff props. Values: string, int, float, `#rrggbb`, `vm.X`, `item.X`, `1..9`, `["a", "b"]`, identifier, `*`, `N*` |
| XAML prop | `Margin: "0,8"`, `Grid.Row: 1`, `Flyout: MenuFlyout { ... }` | PascalCase, may be dotted (attached properties): a XAML property of the type or root tag (see Pass-Through Props) |
| value form | `theme(K)`, `resource(K)`, `ref(Name).Member`, `load(vm.X)` | `{ThemeResource}`, `{StaticResource}`, an element reference, `x:Load` (see Resources and States) |
| flag | `bold`, `fill`, `wrap`, `readonly` | A bare word; only the flags listed per keyword |
| `when(c)` | `when(vm.Count > 0)` | Visibility (see Conditions) |
| `enabled(c)` | `enabled(vm.CanSave)` | `IsEnabled` on inputs and buttons |
| `format: kind` | `format: currency` | `currency`, `date`, `percent`, `number`, `number(2)` |
| `of Type` | `of Order` | Item type for `table`, `list`, `select items:` |
| `-> target` | `-> vm.SaveCommand`, `-> vm.EditCommand(item)`, `-> navigate(View)`, `-> cancel` | Commands, navigation, closing a dialog |
| `on Event -> target` | `on TextChanged -> vm.SearchCommand` | A generated event handler (see Events); repeatable |

- Leaves end with `;`. Blocks use `{ }` and need no `;` after `}`.
- A bare string inside a block (`"Some text.";`) is a wrapping text line (`;` optional).
- A `Key: value;` or `on Event -> target;` line inside a block belongs to the element, exactly as if written in its
  header.
- Comments run from `//` to the end of the line. There are no block comments.
- Identifiers may contain `-`: `icon: calendar-day` becomes the Symbol `CalendarDay`.

## App

One per project. Members: an optional `theme { }` and an optional `nav top|side { }`.

```flag
app Contoso {
    theme {
        accent: #2563eb;
        backdrop: mica;
        titlebar: extended;
        font: "Segoe UI Variable";
        styles: "ms-appx:///Flagstaff.Stdlib/Styles.xaml";
    }
    nav side {
        brand "Contoso";
        link "Dashboard" icon: home -> DashboardView;
        link "Orders" icon: shop -> OrdersView;
    }
}
```

| Theme prop | Values | Effect |
|---|---|---|
| `accent:` | `#rrggbb` | `SystemAccentColor`, Light1-3, Dark1-3, and `FlagstaffAccentBrush` in `App.xaml` |
| `font:` | `"Family"` | Defines the `FlagstaffFontFamily` string resource |
| `backdrop:` | `mica` (default), `acrylic`, `none` | Window backdrop |
| `titlebar:` | `extended` (default), `default` | Content extends into the title bar |
| `styles:` | `"path"` | Styles dictionary (default `ms-appx:///Flagstaff.Stdlib/Styles.xaml`; CLI `-s` wins) |

Without a `theme` block there is no backdrop and no extended title bar.

- `nav top` → `NavigationView PaneDisplayMode="Top"`; `nav side` → `"Left"`. Without `nav`, `MainWindow` holds a
  bare `Frame`.
- `brand "Text";` → pane header. `link "Label" icon: name -> ViewName;` → `NavigationViewItem`. The first link is
  selected at startup.
- Icons are WinUI `Symbol` names in lowercase or kebab-case: `home`, `shop`, `list`, `document`, `calendar`,
  `people`, `contact`, `setting`, `mail`, `find`, `filter`, `edit`, `save`, `delete`, `add`, `refresh`, `sync`,
  `clock`, `flag`, `favorite`, `globe`, `library`, `map`, `phone`, `send`, `attach`, `upload`, `download`.
  An unknown name is FS1048, with the closest `Symbol` (`previous-track` → `previous`).

## View

```flag
view OrdersView {
    bind vm: OrdersViewModel;
    onload -> vm.Load;

    heading "Orders";
}
```

- `bind vm: TypeName;` comes first. Without it the view has no ViewModel, and `vm.X`, overlays, and `onload` do
  not work.
- `onload -> vm.Load;` → `LoadCommand`, run from the page's `Loaded` event (exceptions are logged; DEBUG builds
  re-throw them).
  You implement `private partial async Task OnLoadAsync()`. `onload -> vm.LoadAsync;` warns FS2006.
- Page root: a `Grid` with an `Auto` row and a `*` row. The **first top-level `toolbar`** is pinned in the `Auto`
  row and does not scroll. The rest of the body is a `ScrollViewer > StackPanel Spacing=16 Padding=24`, unless a
  top-level element has `fill` (see Layout).
- Output: `Generated/<View>.xaml`, `<View>.xaml.cs`, `<ViewModel>.g.cs`, and one `<Type>.g.cs` per item type.

## Layout

| Syntax | Emits | Props |
|---|---|---|
| `toolbar { heading "X"; spacer; button "Y" -> vm.YCommand; }` | `Grid` (FlagstaffToolbar): items before `spacer` left, after it right | – |
| `row cols: 3 { ... }` | `Grid`, one column per slot, `ColumnSpacing=12` | `cols:` int (default: sum of child spans), `wrap` |
| `stack { ... }` | `StackPanel Spacing=8` | – |
| `column { ... }` | Same as `stack` (alias; not a table column) | – |
| `split { a; b; }` | `Grid`, one column per child (at least 2), `ColumnSpacing=16` | children's `width:` |
| `group "Title" { ... }` | Bold title + `StackPanel Spacing=4` | – |
| `card "Title" icon: list { ... }` | `Border` (FlagstaffCard) with an optional title | `icon:` |
| `cards cols: 3 { ... }` | Wrapping grid, `ColumnSpacing=12 RowSpacing=12` | `cols:` int (default 4) |
| `spacer;` | Toolbar: left/right split. Row/split: empty column. Elsewhere: 16 px gap | – |
| `separator;` | 1 px divider | – |

- **row**: children are placed left to right; `span: N` takes N columns. Without `wrap`, children that need more
  than `cols:` columns are FS1011; with `wrap`, they continue on a new line. A child's `width:` sets its column
  width (`auto`, pixels, `*`, `N*`; default `*`; the first child placed in a column decides).
- **cards**: always wraps; children may use `span:`; any element can be a child (`stat`, `card`, ...).
- **split**: column widths come from the children's `width:` (default `*`). `width: 2*` beside a default child
  is a 2:1 split; `width: 320` fixes a master pane.
- **fill**: a child of the page body, `stack`, `column`, `group`, `card`, `row`, or `split` takes the remaining
  height; its vertical container becomes a `Grid` with a `*` row. Every vertical container between it and the
  page body must be `fill` too (otherwise FS1013); one `fill` per container (FS1010). A top-level `fill` stops the
  page body from scrolling: use it for master-detail screens and long tables.

```flag
view InboxView {
    bind vm: InboxViewModel;

    toolbar {
        heading "Inbox" icon: mail;
        spacer;
        button "Compose" -> vm.ComposeCommand variant: primary;
    }
    row cols: 4 {
        textbox "Search" vm.Query width: 2*;
        select "Folder" vm.Folder options: ["Inbox", "Archive"] width: 160;
        spacer;
        button "Go" -> vm.SearchCommand width: auto;
    }
    split fill {
        list items: vm.Messages selected: vm.Selected of Message width: 320 {
            text item.Subject;
        }
        card "Message" {
            heading vm.SelectedSubject;
            textarea "Body" vm.Body readonly fill;
        }
    }
}
```

## Display

| Syntax | Emits | Props | `vm.X` type |
|---|---|---|---|
| `heading "Title" icon: home;` / `heading vm.Title;` | `TextBlock` (SubtitleTextBlockStyle), optional `SymbolIcon` | `icon:` | `string` |
| `text "Static";` / `text vm.Status;` | `TextBlock` | `bold` | `string`, or the `format:` type |
| `stat "Open" vm.OpenCount variant: warning;` | KPI card: caption + large value | `variant:` warning, danger, info, success | `string` unless `format:` or a comparison types it |
| `badge vm.Tier;` / `badge "New";` | Accent pill; `InfoBadge` when the member is `int` | – | `string` |
| `summary { line ...; }` | `StackPanel Spacing=4` of lines | – | – |
| `line "Net" vm.Net format: currency bold;` | Label left, value right | `bold` | `string`, or the `format:` type |
| `image vm.PhotoUrl;` / `image src: "ms-appx:///Assets/logo.png";` | `Image Stretch=Uniform` | `src:` | `string` |
| `progress vm.Done;` / `progress;` | `ProgressBar` (0–100; indeterminate without a binding) | – | `double` |
| `spinner;` | `ProgressRing IsActive=True` | – | – |

- `stat` variants select `FlagstaffStatCard{Success,Warning,Danger,Info}` styles.
- `stat`, `line`, and `text vm.X` apply the `format:` converter (see Format); `number(2)` shows two decimals.
- Without `format:` (or a comparison), `stat vm.OpenCount` is a `string`: assign `OpenCount = n.ToString()`. For a
  number write `stat "Open" vm.OpenCount format: number(0);` (`double`; an `int` assigns to it).

```flag
view KpiView {
    bind vm: KpiViewModel;

    cards cols: 3 {
        stat "Revenue" vm.Revenue format: currency;
        stat "Margin" vm.Margin format: percent variant: success;
        stat "Overdue" vm.OverdueCount variant: danger when(vm.OverdueCount > 0);
    }
    card "Totals" icon: document {
        summary {
            line "Subtotal" vm.Subtotal format: currency;
            line "Tax" vm.Tax format: currency;
            line "Total" vm.Total format: currency bold;
        }
    }
    progress vm.SyncProgress when(vm.IsSyncing);
    badge vm.Plan;
}
```

## Inputs

Inputs bind two-way to their positional `vm.X` (or `item.X` in a list row). The label becomes the `Header`
(`Content` for checkbox and radio).

| Syntax | Emits | Props | `vm.X` type |
|---|---|---|---|
| `textbox "Name" vm.Name;` | `TextBox` (updates `vm.X` on every keystroke) | `placeholder:`, `hint:`, `readonly` | `string` |
| `textarea "Notes" vm.Notes;` | Multi-line `TextBox` (wraps, MinHeight 80; every keystroke) | `placeholder:`, `hint:`, `readonly` | `string` |
| `number "Qty" vm.Qty min: 0 max: 99 step: 1;` | `NumberBox` (compact spin buttons) | `placeholder:`, `hint:`, `min:`, `max:`, `step:` | `double` |
| `checkbox "Billable" vm.IsBillable;` | `CheckBox` | – | `bool` |
| `radio "Express" vm.IsExpress;` | `RadioButton` | – | `bool` |
| `toggle "Notify" vm.Notify;` | `ToggleSwitch` | – | `bool` |
| `datepicker "Due" vm.Due hint: "Optional";` | `CalendarDatePicker` | `hint:` | `DateTimeOffset?` |
| `select "Status" vm.Status options: ["Open", "Closed"];` | `ComboBox` of `x:String` | `options:`, `placeholder:`, `hint:` | `string` |
| `select "Year" vm.Year options: 2020..2026;` | `ComboBox` of `x:Int32` | `options:` | `int` |
| `select "Tag" vm.Tag items: vm.Tags;` | `ComboBox ItemsSource` | `items:` | `string?`; `Tags` is `ObservableCollection<string>` |
| `select "Vendor" vm.Vendor items: vm.Vendors of Vendor display: item.Name;` | `ComboBox DisplayMemberPath="Name"` | `items:`, `display:` | `Vendor?`; `ObservableCollection<Vendor>` |
| `filePicker "Attach" -> vm.AttachCommand;` | `Button` (FlagstaffFilePickerButton) bound to the command | `icon:` | command |

- `placeholder:` → `PlaceholderText`; `hint:` → `Description` (text under the field; both also on every `select`);
  `readonly` → `IsReadOnly`;
  `min:`/`max:`/`step:` (ints or floats) → `Minimum`/`Maximum`/`SmallChange`.
- `select options:` is a non-empty list of strings or an int range (FS1005 otherwise). `display: item.X` needs
  `items: vm.X of T` (FS2013). Without `display:`, each item shows its `ToString()`.
- `filePicker` only wires the command. Open the `FileOpenPicker` in `OnAttachAsync()` (initialize it with
  `WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)`).

```flag
view EntryView {
    bind vm: EntryViewModel;

    row cols: 2 {
        textbox "Title" vm.Title placeholder: "Short summary";
        number "Amount" vm.Amount min: 0 step: 0.5;
    }
    row cols: 3 {
        select "Status" vm.Status options: ["Open", "Closed", "On hold"] placeholder: "Pick one";
        select "Vendor" vm.Vendor items: vm.Vendors of Vendor display: item.Name;
        datepicker "Due" vm.DueDate hint: "Leave empty for no deadline";
    }
    textarea "Notes" vm.Notes;
    stack {
        checkbox "Billable" vm.IsBillable;
        toggle "Notify me" vm.Notify;
    }
    filePicker "Attach receipt" -> vm.AttachCommand icon: attach;
}
```

## Buttons and Commands

| Syntax | Emits |
|---|---|
| `button "Save" -> vm.SaveCommand;` | `Button Command="{x:Bind ViewModel.SaveCommand}"` |
| `button "Save" -> vm.SaveCommand variant: primary;` | `AccentButtonStyle` |
| `button "Delete" -> vm.DeleteCommand variant: danger;` | `FlagstaffDangerButton` |
| `button "Add" -> vm.AddCommand icon: add;` | `SymbolIcon` + label (also on `filePicker` and `link`) |
| `button "Save" -> vm.SaveCommand enabled(vm.CanSave);` | `IsEnabled` bound to the condition |
| `button "Edit" -> vm.EditCommand(item);` | In a `table`/`list` row: the row is the `CommandParameter` |
| `button "Settings" -> navigate(SettingsView);` | `Click` handler: `Frame.Navigate(typeof(SettingsView))` |
| `button "Cancel" -> cancel;` | In a `dialog`/`confirm`: the generated `Cancel{Flag}Command` (clears the flag) |
| `link "Help" -> HelpView;` / `link "Run" -> vm.RunCommand;` | `HyperlinkButton` that navigates or runs the command |

- `-> vm.SaveCommand` generates `SaveCommand`, backed by `[RelayCommand] private async Task SaveAsync()`, which
  awaits `private partial Task OnSaveAsync();`. `-> vm.Save` is the same command.
- `-> vm.EditCommand(item)` generates `OnEditAsync(Order item)`, typed by the `of T` of the enclosing
  `table`/`list` (FS2007 without one). `(item)` is the only argument form (FS1052).
- A command used by several elements is generated once.
- `-> cancel` works only on a `button` inside a `dialog`/`confirm` (FS1024).
- A `button`, `filePicker`, or `link` needs an action, else FS1023 (warning). An action is `->`, an
  `on Click -> ...` clause, a `Flyout:` property element (`button "More" { Flyout: MenuFlyout { ... } }`), or a
  `NavigateUri:` on a `link`.
- `link "Help" -> HelpView;` navigates (the short form of `-> navigate(HelpView)`); `link -> vm.XCommand` runs it.
- Clauses written after an element value belong to that value: in `AppBarButton Label: "Save" Icon: SymbolIcon
  Symbol: Save -> vm.SaveCommand;` the arrow is on the `SymbolIcon` (FS2014). Put `->` before the property element.

## Tables

```flag
view InvoicesView {
    bind vm: InvoicesViewModel;

    table items: vm.Invoices selected: vm.Selected of Invoice -> vm.OpenCommand(item) {
        col "Client" bind: item.Client width: 2*;
        col "Hours"  bind: item.Hours format: number(1) width: 80;
        col "Amount" bind: item.Amount format: currency width: 120;
        col "Due"    bind: item.Due format: date width: auto;
        button "Edit"   -> vm.EditCommand(item);
        button "Delete" -> vm.DeleteCommand(item) variant: danger enabled(item.CanDelete);
        empty "No invoices yet.";
    }
}
```

| Part | Meaning |
|---|---|
| `table items: vm.X of T` | `ListView` with a header row; `vm.X` is `ObservableCollection<T>` |
| `selected: vm.Sel` | `SelectionMode=Single`, two-way `SelectedItem`; `vm.Sel` is `T?` |
| `-> vm.OpenCommand(item)` / `-> navigate(View)` | Row click (`IsItemClickEnabled`); `navigate` passes the row as the navigation parameter |
| `col "Header" bind: item.X;` | One column; `item.X` becomes a property of `T` |
| `col ... format: kind` | Converter on the cell and the property type (see Format) |
| `col ... width: auto` / `120` / `2*` | Column width (default `*`), shared by header and rows |
| `button "X" -> vm.XCommand(item);` | Row action in a trailing `Auto` column; `variant:`, `icon:`, universal props apply |
| `empty "Text";` | Shown while `vm.X.Count == 0` (FlagstaffEmptyState); may have its own `when()` |

- A `table` contains only `col`, `button`, and `empty` (FS2012). `col` outside a table is FS2012.
- Columns bind with `bind: item.X`: never `vm.X`, never positionally.
- `width: auto` sizes per row (WinUI has no shared size groups); prefer fixed or star widths when rows must align.
- Cells are one-time `x:Bind`s on plain item classes: to show a changed row, replace it in the collection.
- A row action with `when()` keeps its column width (an invisible copy holds the space); `enabled(item.X)` is
  usually clearer.
- `table` and `list` accept `when(...)`, but not `align:`, `tooltip:`, `name:`, or a pixel `width:` outside a
  grid (FS1002); wrap them in a `stack` for that.

## Lists

`list` is a `ListView` whose row template is ordinary elements bound with `item.X`.

```flag
view ContactsView {
    bind vm: ContactsViewModel;

    list items: vm.Contacts selected: vm.Selected of Contact -> vm.OpenCommand(item) {
        heading item.Name;
        text item.Email;
        badge item.Tier when(item.IsVip);
        checkbox "Active" item.IsActive;
        button "Call" -> vm.CallCommand(item) enabled(item.HasPhone);
        empty "No contacts.";
    }
}
```

- Same `items:`, `selected:`, `of T`, `->`, and `empty` as `table`. Rows stack vertically (`Spacing=4`).
- Positional `item.X` works on `heading`, `text`, `stat`, `line`, `badge`, `image`, `progress`, `textbox`,
  `textarea`, `number`, `checkbox`, `radio`, `toggle`, `datepicker`. Containers (`row`, `stack`, `card`, ...)
  work inside rows too.
- **Scope rule (FS2005):** inside a row, bindings and conditions use `item.X`; `vm.X` is an error. Outside a row,
  `item.X` is an error. Row buttons reach page commands with `-> vm.XCommand` or `-> vm.XCommand(item)`.
- Overlays (`toast`, `confirm`, `dialog`) cannot be inside a row (FS1020).
- Item property types: `checkbox`/`radio`/`toggle` → `bool`, `number`/`progress` → `double`, `datepicker` →
  `DateTimeOffset?`, `format:` → its type, otherwise `string`; `item.X` in a condition takes the literal's type.

### Item types

Every `of T` in every file feeds one registry. The compiler emits one `Generated/T.g.cs` per type, in
`<Namespace>.ViewModels`: a plain partial class with auto-properties for every `item.X` usage (table columns,
list templates, `select display:`, conditions). Two different explicit types for one property are FS2010.
Item bindings (`item.X`) are **OneTime**: the classes do not raise change notifications, so to update a row,
replace the item in the collection (or replace the collection).
Extend `T` in your own partial (`ViewModels/Order.cs`: `public partial class Order { ... }`).

## Overlays

`alert` is inline. `toast`, `confirm`, and `dialog` are **hoisted**: wherever they are written (inside cards,
groups, other dialogs), they render in one `FlagstaffOverlay` layer above the toolbar and body. An ancestor's
`when()` does not hide them; only their own flag does. They take no layout slot (row, column, or cell) where
they are written.

| Syntax | Emits | Props |
|---|---|---|
| `alert when(vm.HasError) variant: danger { "Text."; button ...; }` | Inline `InfoBar`: first string = message, elements = content | `variant:` warning, danger, info, success (default info) |
| `toast "Saved." when(vm.ShowSaved) variant: success duration: 3000;` | `InfoBar` at the bottom right, `IsOpen` two-way | `variant:`, `duration:` (ms) |
| `dialog "Title" message: "..." when(vm.IsOpen) { ... }` | Modal: backdrop + centered card | `message:` |
| `confirm "Delete?" message: "..." when(vm.IsOpen) -> vm.DeleteCommand;` | Modal with generated OK and Cancel buttons | `message:`, `variant:`, `confirmLabel:` |

Rules for `toast`, `confirm`, and `dialog`:

- They need `bind vm:` and a bare `when(vm.Flag)` (FS1021 without; FS2003 for `!vm.X`, comparisons, `item.X`).
  `vm.Flag` is a `bool`; your code sets it to `true` to show the overlay.
- **toast**: the user can close it. With `duration:`, the code-behind resets the flag to `false` after that many
  milliseconds (restarted each time it becomes `true`). Without it, the toast stays until closed or reset.
- **dialog**: title, message, the non-button children stacked, then the buttons right-aligned. Your button
  commands must set the flag back to `false`; `button "Cancel" -> cancel;` binds the generated
  `Cancel{Flag}Command` instead (also in a `confirm` with its own buttons).
- **confirm with `->`**: generates the confirming button (`confirmLabel:` or `"OK"`; `variant: danger` gives
  `FlagstaffDangerButton`, otherwise accent) bound to the target, and a **Cancel** button bound to a generated
  `Cancel{Flag}Command` that sets the flag to `false` (`when(vm.IsDeleteOpen)` → `CancelIsDeleteOpenCommand`).
  Your confirming command must clear the flag itself. Field children are allowed.
- **confirm with its own buttons** behaves like `dialog`. Do not combine `->` with buttons, or `confirmLabel:`
  with buttons, and do not omit both (FS1022).

```flag
view FilesView {
    bind vm: FilesViewModel;

    alert when(vm.HasSyncError) variant: danger {
        "Sync failed.";
        button "Retry" -> vm.RetryCommand;
    }
    button "Rename" -> vm.OpenRenameCommand;
    button "Delete" -> vm.RequestDeleteCommand variant: danger;

    dialog "Rename file" message: "Choose a new name." when(vm.IsRenameOpen) {
        textbox "Name" vm.NewName;
        button "Cancel" -> cancel;
        button "Save" -> vm.SaveNameCommand variant: primary;
    }
    confirm "Delete file?" message: "This cannot be undone." when(vm.IsDeleteOpen) variant: danger confirmLabel: "Delete" -> vm.DeleteCommand;
    toast "File deleted." when(vm.ShowDeleted) variant: success duration: 3000;
}
```

## Conditions

`when(c)` sets `Visibility`. `enabled(c)` sets `IsEnabled` on buttons, inputs, `select`, and `filePicker`.

```text
cond     := or
or       := and ("||" and)*
and      := unary ("&&" unary)*
unary    := "!" unary | primary
primary  := "(" cond ")" | operand [op operand]
op       := ">" | "<" | ">=" | "<=" | "==" | "!="
operand  := vm.X | item.X | int | float | "string" | true | false | null
```

| Condition | Compiles to |
|---|---|
| `when(vm.IsAdmin)` | `Visibility="{x:Bind ViewModel.IsAdmin, Converter=BoolToVisibility}"`; `IsAdmin` is `bool` |
| `enabled(vm.CanSave)` | `IsEnabled="{x:Bind ViewModel.CanSave}"` |
| anything else | A static `When_n`/`Enabled_n` function on the page, `x:Bind`-ed to its inputs |

Typing rules:

- A bare `vm.X` is `bool`. `vm.X > 5` makes `X` an `int`, `> 2.5` a `double`, `== "Open"` a `string`,
  `== true` a `bool`, `== null` a `string?`, unless a stronger usage types it first (see Type Inference).
- FS2004: a literal that does not fit an explicit type (a `number` compared with `"x"`, a `checkbox` with `1`),
  `null` against a non-nullable value type, or `<`, `>`, `<=`, `>=` on `string` or `bool`.
- Literal suffixes are added for you (`vm.Total > 100` against a `decimal` emits `100m`).
- FS2005: `vm.X` inside a `list`/`table` row, or `item.X` outside one.
- `toast`, `confirm`, and `dialog` accept only a bare `when(vm.Flag)` (FS2003).
- `enabled(...)` on a container (`stack`, `card`, ...) has no effect (FS1012); put it on each control.

```flag
view RulesView {
    bind vm: RulesViewModel;

    number "Quantity" vm.Quantity;
    select "Status" vm.Status options: ["Open", "Closed"];
    text "Large order" when(vm.Quantity >= 100 && vm.Status == "Open");
    text "Nothing selected" when(vm.Selected == null);
    button "Submit" -> vm.SubmitCommand enabled(!vm.IsBusy && (vm.Quantity > 0 || vm.AllowEmpty));
    table items: vm.Orders selected: vm.Selected of Order {
        col "Id" bind: item.Id;
        button "Cancel" -> vm.CancelOrderCommand(item) when(item.Status != "Shipped");
    }
}
```

## Universal Props

Every keyword accepts these props in the schema. Where one has no effect, the compiler says so.

| Prop | Values | Effect |
|---|---|---|
| `width:` | `240`, `auto`, `*`, `2*` | In a `row`/`split`/`cards`: the column width. Elsewhere: a pixel `Width` (`auto`/`*` warn FS1012) |
| `span:` | int ≥ 1 | Columns taken in a `row`/`cards` (FS1012 elsewhere; FS1011 if wider than `cols:`) |
| `fill` | flag | Takes the remaining height (see Layout) |
| `align:` | `left`, `center`, `right`, `stretch` | `HorizontalAlignment` |
| `tooltip:` | `"text"` | `ToolTipService.ToolTip` |
| `name:` | identifier | `x:Name`, for code-behind access (unique, FS3008) |
| `uid:` | `"text"` | `x:Uid`, the localization key for `.resw` resources |
| `load:` | `load(vm.X)` | `x:Load="{x:Bind ViewModel.X, Mode=OneWay}"`: the element is created only while `vm.X` (a `bool`) is true. Any other value is FS1032 |
| `key:` | identifier | `x:Key` on a generic element directly inside a resources block (`Page.Resources: { }`); use it with `resource(Key)`. Elsewhere, or on a keyword, FS1044 |

`name:` declarations are unique per view (FS1042) and are what `ref(Name)` resolves against (see Locality Rules).

**Where they do not apply (FS1002):** `align:`, `tooltip:`, `name:`, `uid:`, `load:`, and a pixel `width:` outside
a grid are errors on `table`, `list`, `alert`, `toast`, `confirm`, `dialog`, and `empty` (`col` takes none of them;
its `width:` is the column width). Wrap the element instead: `stack tooltip: "..." { table ...; }`. `span:` and
`fill` where they have no effect warn FS1012. Generic elements accept all of them (`when()` needs a `UIElement`,
the layout props a `FrameworkElement`, `enabled()` a `Control`).

## Format

| `format:` | Converter | Inferred type |
|---|---|---|
| `currency` | `CurrencyConverter` | `decimal` |
| `date` | `DateConverter` | `DateTimeOffset?` |
| `percent` | `PercentConverter` | `double` |
| `number` | `NumberConverter` (`G`) | `double` |
| `number(2)` | `NumberConverter` with `ConverterParameter=F2` (N decimals) | `double` |

`format:` formats `stat`, `line`, `col`, and bound `text` values, and types the `vm.X`/`item.X` it is written on. Unknown
kinds are FS1003.

## Type Inference

Each `vm.X` gets one C# type. Usages have a confidence; the highest wins (on ties, the first usage).

| Confidence | Usage | Type |
|---|---|---|
| Explicit | `checkbox`, `radio`, `toggle` | `bool` |
| Explicit | `number`, `progress` | `double` |
| Explicit | `datepicker` | `DateTimeOffset?` |
| Explicit | `format: currency` / `date` / `percent` / `number` | `decimal` / `DateTimeOffset?` / `double` / `double` |
| Explicit | `items: vm.X of T` (`table`, `list`, `select`) | `ObservableCollection<T>` (`select` without `of`: `<string>`) |
| Explicit | `selected: vm.X` with `of T` | `T?` |
| Explicit | `select vm.X items: ... of T` / without `of` | `T?` / `string?` |
| Explicit | `select vm.X options: 1..10` | `int` |
| Explicit | `-> vm.XCommand`, `on Event -> vm.XCommand` | command (`IAsyncRelayCommand`) |
| Explicit | `Key: vm.X` (XAML prop) or a generic element's positional `vm.X` | the property's CLR type from the manifest: `Value` → `double`, `IsExpanded` → `bool`, `SelectedIndex` → `int`, `Time` → `TimeSpan`, `Color` → `Windows.UI.Color` |
| Explicit | `load: load(vm.X)` | `bool` |
| Comparison | `when(vm.X)`, `vm.X > 5`, `> 2.5`, `== "a"`, `== null` | `bool`, `int`, `double`, `string`, `string?` |
| Default | `textbox`, `textarea`, `text`, `heading`, `badge`, `image`, `stat`, `line`, `select options: [...]` | `string` |
| Explicit | `ItemsSource: vm.X` (also `Items`, `MenuItemsSource`) / with `of T` | `ObservableCollection<object>` / `ObservableCollection<T>` |
| Default | `Key: vm.X` where the property is `object` (`Content`, `Header`, `Tag`) or not in the manifest | `string` |

- Strings default to `""` and collections to `new()`.
- `item.X` under a XAML prop types the row class the same way (`Expander IsExpanded: item.IsOpen` → `bool`).
- **Items controls.** `GridView ItemsSource: vm.Photos of Photo` makes `Photos` an `ObservableCollection<Photo>`
  and `SelectedItem: vm.Sel` a `Photo?`; the element's block is then its `ItemTemplate`, bound with `item.X` like a
  `list` row. Without `of T` the collection is `ObservableCollection<object>`. For a `ListView`, use `list`/`table`.
- A type inference cannot produce (`long`, `Guid`, your own class) is declared in your partial; do not use that
  member name in the `.flag` file.

## ViewModel Conventions

The compiler writes `Generated/<Vm>.g.cs`. You write `ViewModels/<Vm>.cs` (`flagstaff stubs` creates it).

```csharp
// Generated/OrdersViewModel.g.cs (never edit)
public partial class OrdersViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<Order> _orders = new();
    [ObservableProperty] private bool _isDeleteOpen;
    [RelayCommand] private async Task LoadAsync() { await OnLoadAsync(); }
    private partial Task OnLoadAsync();
    [RelayCommand] private async Task DeleteAsync(Order item) { await OnDeleteAsync(item); }
    private partial Task OnDeleteAsync(Order item);
    [RelayCommand] private async Task ConfirmDeleteAsync() { await OnConfirmDeleteAsync(); }
    private partial Task OnConfirmDeleteAsync();
    [RelayCommand] private void CancelIsDeleteOpen() => IsDeleteOpen = false;   // confirm ... -> / -> cancel
}
```

```csharp
// ViewModels/OrdersViewModel.cs (yours)
using System.Threading.Tasks;
using Contoso.Services;

namespace Contoso.ViewModels;

public partial class OrdersViewModel(IOrderService orders)
{
    private Order? _pendingDelete;

    private partial async Task OnLoadAsync()
    {
        Orders.Clear();
        foreach (var order in await orders.GetAllAsync())
            Orders.Add(order);
    }

    private partial Task OnDeleteAsync(Order item)
    {
        _pendingDelete = item;
        IsDeleteOpen = true;            // shows: confirm ... when(vm.IsDeleteOpen) -> vm.ConfirmDeleteCommand;
        return Task.CompletedTask;
    }

    private partial async Task OnConfirmDeleteAsync()
    {
        IsDeleteOpen = false;           // the confirming command clears the flag
        if (_pendingDelete is { } order)
        {
            await orders.DeleteAsync(order.Id);
            Orders.Remove(order);
        }
    }
}
```

- Implement every `On{X}Async` as `private partial async Task On{X}Async(...)` (or a non-async method returning
  a `Task`). A missing one is a C# error (CS8795); `flagstaff stubs --update` adds stubs.
- Do not declare `[ObservableProperty]` or `[RelayCommand]` members that the `.flag` file uses (FS2016).
- Set the generated property (`IsDeleteOpen`), not the field (`_isDeleteOpen`).
- ViewModels are registered as transient services; constructor parameters resolve from `ConfigureServices`.
- Pages are `public sealed partial class XView : Page` with a `ViewModel` property, a parameterless constructor
  (for `Frame.Navigate`), a DI constructor, and the hook `partial void OnViewCreated()`. Add your own partial for page
  code (`OnViewCreated`, `OnNavigatedTo`).

### The hand-written half

The generator owns every member a `.flag` file implies; your partials add only what it does not. Each generated
file starts with a comment listing its members, `flagstaff check --format json` prints them (`contract`,
`itemTypes`), and `flagstaff build` adds a hint to C# errors against them.

- **ViewModel**: never declare a property, field (`_openCount`), or `[RelayCommand]` the `.flag` binds. Implement
  exactly the `On{X}Async` delegates `flagstaff stubs` lists (a `partial On{X}Async` no command uses is declared for
  you, FS2017, but nothing calls it). Assign the inferred type: `stat`/`text`/`line` without `format:` are `string`.
- **Page**: both constructors exist; never add one. Page setup goes in `partial void OnViewCreated() { ... }`, which
  runs once at the end of the generated constructor (after `InitializeComponent`); load data with
  `onload -> vm.Load;`. ViewModel members are `ViewModel.X` there.
- **Item types** (`of Order`): `Generated/Order.g.cs` is `public partial class Order` in `<App>.ViewModels` with only
  the properties bound as `item.X`. Add others in your own `public partial class Order` in that namespace.

If your partial declares a generated member anyway (a property, a `[RelayCommand]`, an `On{X}Async` with other
accessibility, a page constructor, an item property), the generator skips its own and warns FS2016: the build
succeeds, but prefer deleting yours. `Generated/GlobalUsings.g.cs` imports the `Microsoft.UI.*`,
`Windows.Graphics.Imaging`, CommunityToolkit, and `<App>.ViewModels` namespaces in every C# file of the app (not
`Microsoft.UI.Xaml.Shapes`: write `using Microsoft.UI.Xaml.Shapes;` for `Polyline`, since its `Path` clashes with
`System.IO.Path`).

- **Your type wins.** A property or item property you declare with another type than the `.flag` infers keeps your
  type in everything generated about it (condition functions, the badge control, the contract, `stubs`), and FS2016
  names both types. If your type cannot work with how the `.flag` uses it (a `checkbox` on a `string`,
  `format: currency` on a `bool`, a `number` on a `decimal`, `of Order` over a collection of another type), that is
  error FS2018 with the type to declare. An enum compared with a string (`when(vm.Status == "Open")`) compares its
  name; a `bool?` counts `null` as false.
- **Delegate signatures.** A command passes the row (`-> vm.Edit(item)` in a `table`/`list` of `T`) or nothing, so
  its delegate is `On{X}Async(T item)` or `On{X}Async()`. If yours takes no parameter where a row is passed, takes
  the row as `T?`/`object`, or returns `void`, the generator declares it your way (FS2016). Any other parameter list
  (`OnDeleteAsync(Contact)` for `-> vm.Delete`) is error FS2019: implement the signature it names, or pass the row
  with `-> vm.Delete(item)` inside a `table`/`list` of `Contact`.

### App hooks

`Generated/App.xaml.cs` builds the DI container, registers every ViewModel, exposes `App.Services`,
`App.GetService<T>()`, and `App.MainWindow`, and calls three partial methods. Implement them in `App.Services.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Contoso;

public partial class App
{
    partial void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<Services.IOrderService, Services.OrderService>();
    }

    partial void OnAppStarting(List<Func<Task>> steps)
    {
        // awaited in order, before MainWindow is created
        steps.Add(() => GetService<Services.IOrderService>().SeedAsync());
    }

    partial void OnAppLaunched() { /* runs after MainWindow.Activate() */ }
}
```

`OnLaunched` is `async`: it awaits the `OnAppStarting` steps, then creates and activates `MainWindow`, then calls
`OnAppLaunched()`. Put async seeding in a step; never block the UI thread in `OnAppLaunched`.

`App.xaml` merges `XamlControlsResources` and the styles dictionary and registers the converters
`BoolToVisibility`, `CurrencyConverter`, `PercentConverter`, `DateConverter`, and `NumberConverter`.

## Navigation

| Source | Syntax | Result |
|---|---|---|
| App shell | `link "Orders" icon: shop -> OrdersView;` in `nav` | `NavigationViewItem`; the content frame navigates on selection |
| Button / link | `button "Settings" -> navigate(SettingsView);`, `link "Help" -> HelpView;` | `Frame.Navigate(typeof(SettingsView))`, no parameter |
| Row click | `table`/`list` `... of T -> navigate(OrderView) { ... }` | `Frame.Navigate(typeof(OrderView), clickedItem)` |
| Confirm | `confirm "Leave?" when(vm.IsLeaveOpen) -> navigate(HomeView);` | The confirming button navigates |

- `-> SettingsView` is a short form of `-> navigate(SettingsView)`. Targets are view names, resolved at runtime in
  the app namespace.
- Receive a row-click parameter in your own page partial: `public sealed partial class OrderView { protected
  override void OnNavigatedTo(NavigationEventArgs e) { ... } }`.
- Generic elements navigate the same way when their type has a `Click` event (`AppBarButton Label: "Settings"
  -> navigate(SettingsView);`), and any element can navigate from an event: `on Tapped -> navigate(View)`.
- There is no back button, no parameter on button navigation, and no navigation from ViewModels. `link` inside a
  view is a `HyperlinkButton`; `link "Docs" -> vm.OpenDocsCommand;` runs a command instead.

## Generic Elements

A PascalCase element name is a WinUI type: any non-abstract `DependencyObject` in `Microsoft.UI.Xaml.*`. The
compiler checks it against a control manifest generated from the Windows App SDK metadata (types, properties and
their CLR types, content properties, events, enum members). Use one when no keyword covers the control.

```flag
Expander Header: "Advanced" IsExpanded: vm.ShowAdvanced {
    toggle "Telemetry" vm.Telemetry;
    Slider Header: "Cache size (MB)" Minimum: 64 Maximum: 2048 Value: vm.CacheMb;
}
TabView {
    TabViewItem Header: "General" IsClosable: false {
        textbox "Display name" vm.DisplayName;
    }
    TabViewItem Header: "Account" IsClosable: false {
        button "Sign out" -> vm.SignOutCommand variant: danger;
    }
}
```

| Part | Rule | Example → XAML |
|---|---|---|
| Type | A WinUI type name; unknown or abstract types are FS1030 (with a suggestion) | `Expander` → `<Expander>` |
| `Key: value` | A property of the type, written as an attribute. Unknown: FS1031; wrong value shape: FS1032 | `Header: "Advanced"` → `Header="Advanced"` |
| `Key: vm.X` | `x:Bind`. **TwoWay** on input properties (`Text`, `Value`, `IsChecked`, `IsOn`, `Date`, `Time`, `Color`, `SelectedItem`, `SelectedIndex`, `IsExpanded`, `IsPaneOpen`, `IsOpen`), OneWay elsewhere | `Value: vm.CacheMb` → `Value="{x:Bind ViewModel.CacheMb, Mode=TwoWay}"` |
| `Key: item.X` | In a `list`/`table` row: binds to the row (OneTime) | `Expander Header: item.Name` |
| label | Sets the content property when it is `string` or `object` (else FS1004) | `ToggleButton "Bold"` → `Content="Bold"` |
| positional `vm.X` | Binds the type's input property TwoWay (FS1004 if it has none) | `ToggleButton "Bold" vm.IsBold;` → `IsChecked="{x:Bind ..., Mode=TwoWay}"` |
| lowercase props | The universal props (`when()`, `enabled()`, `name:`, `width:`, `align:`, `tooltip:`, `uid:`, `load:`, `key:`) mean what they mean on keywords | `Expander Header: "x" when(vm.IsAdmin)` |
| children | Go to the content property (`Content`, `Child`, `Children`, `Items`, ...). Keywords and generic elements nest both ways. Several children of a one-object property are wrapped in a `StackPanel`. No content property, or the wrong item type: FS1035 | `card { Slider; }`, `TabViewItem { textbox ...; }` |
| string child | Content (a `TextBlock` where the content property is not `string`/`object`) | `ToggleButton { "Bold"; }` → `Content="Bold"` |
| `-> vm.XCommand` | Only when the type has a `Command` property (`ButtonBase` types, `MenuFlyoutItem`, `AppBarButton`); else FS2014 | `MenuFlyoutItem Text: "Copy" -> vm.CopyCommand;` |
| `-> navigate(View)` | Only when the type has a `Click` event; else FS2014 (`-> cancel` is FS2014 too) | `AppBarButton Label: "Help" -> navigate(HelpView);` |
| `on Event -> target` | Any event of the type (see Events) | `Slider on ValueChanged -> vm.ApplyCommand;` |

**Prefer the keyword.** A type that has a keyword warns FS4001: `TextBox` → `textbox`, `NumberBox` → `number`,
`ComboBox` → `select`, `CheckBox` → `checkbox`, `RadioButton` → `radio`, `ToggleSwitch` → `toggle`,
`CalendarDatePicker` → `datepicker`, `Button` → `button`, `HyperlinkButton` → `link`, `ListView` → `list`,
`InfoBar` → `alert`, `ProgressBar` → `progress`, `ProgressRing` → `spinner`, `Image` → `image`,
`InfoBadge` → `badge`, `TextBlock` → `text`. Keywords carry Flagstaff's bindings, styles and layout, and accept the
same XAML props (see Pass-Through Props).

### Values

| Property type | Write | Examples |
|---|---|---|
| `bool` | `true` / `false` | `IsClosable: false` |
| number | bare int or float | `Minimum: 64`, `Opacity: 0.5` |
| enum | a bare member (any case) or quoted | `Orientation: Horizontal`, `TextWrapping: wrap`, `TabWidthMode: "SizeToContent"` |
| `Thickness`, `CornerRadius` | a number, or a quoted list | `Margin: 8`, `Padding: "12,8"`, `CornerRadius: "8,8,0,0"` |
| `GridLength` | `Auto`, `*`, `N*`, a number, or quoted | `Height: Auto`, `Width: 2*` |
| `Brush`, `Color` | a named color or `#rgb`/`#argb`/`#rrggbb`/`#aarrggbb` (bare or quoted), `theme(K)`, `resource(K)` | `Fill: CornflowerBlue`, `Background: theme(CardBackgroundFillColorDefaultBrush)` |
| `IconElement` | a bare `Symbol` name, or an element | `Icon: Save`, `Icon: SymbolIcon Symbol: Save;` |
| string, `Uri`, time spans, anything else | quoted | `Header: "Advanced"`, `Duration: "0:0:0.3"` |
| an object | an element value (below) | `Pane: StackPanel Padding: 12 { ... }` |

A PascalCase value under a PascalCase key is a **bare name** (an enum member, a color, a `Symbol`) when it is
followed by `;`, `}`, `->`, or a clause, or when it is a known enum member or color. Otherwise it starts an
element value. A misspelled member (`Orientation: Horizonal`) is FS1032 with the closest one. Keys ending in
`Type` (`TargetType: Button`) take a type name, and `Property:` takes a property name. Under lowercase keys a
PascalCase word is just a name (`name: SearchBox`).

### Property elements

An object-valued property is written `Key: value` too. The three forms, in the header or as a line in the block:

| Form | Example | XAML |
|---|---|---|
| `Key: Type props;` | `AppBarButton Label: "Save" Icon: SymbolIcon Symbol: Save;` | `<AppBarButton.Icon><SymbolIcon Symbol="Save" /></AppBarButton.Icon>` |
| `Key: Type props { children }` | `Flyout: MenuFlyout { MenuFlyoutItem Text: "Copy"; }` | `<Button.Flyout><MenuFlyout>...` |
| `Key: { elements }` | `RowDefinitions: { RowDefinition Height: Auto; RowDefinition; }` | `<Grid.RowDefinitions>...` (a collection) |

- **Header or body.** `Key:` lines inside the block belong to the element. `SplitView { Pane: StackPanel { ... } }`
  and `SplitView Pane: StackPanel { ... } { ... }` are the same. Element and block values need no `;` on a line of
  their own; scalar lines (`Placement: Bottom;`) do.
- In the header, a value that ends with `;` also ends its owner (`... Icon: SymbolIcon Symbol: Save;`). A value
  that ends with `}` does not: end the owner with `;` or continue with its `{ }` block.
- Clauses after a value element belong to the value element. Write `->`, `when()`, and `on` before it.

```flag
AppBarButton Label: "Inbox" -> vm.InboxCommand Icon: SymbolIcon Symbol: Mail;
StackPanel Orientation: Horizontal Spacing: 8 {
    ToggleButton "Bold" vm.IsBold;
    ToggleButton IsChecked: vm.IsItalic { "Italic"; }
}
```

### Third-party controls

`use "<assembly namespace>" as prefix;` at the top of the file (before `app`/`view`) declares
`xmlns:prefix="using:..."`; then `prefix.Type` is an element. Types outside the manifest warn FS4002 once: their
own props, label, children and `->` pass through unvalidated (dotted keys and `ref()` are checked). A prefix
without `use` is FS1036.
A binding on a third-party element takes its C# type from `of T`; `T` may be qualified (`Series: vm.Series of LiveChartsCore.ISeries`), and the name is emitted verbatim into the generated ViewModel, so no `global using` is needed.

```flag warn
use "LiveChartsCore.SkiaSharpView.WinUI" as lvc;

view ChartsView {
    bind vm: ChartsViewModel;

    lvc.CartesianChart Series: vm.Series Height: 400;
}
```

## Pass-Through Props

PascalCase keys work on keyword elements too. A **scalar value** becomes an attribute on the keyword's **root
tag**, checked against that tag's type (FS1031, FS1032) and the locality rule. If the emitter writes the same
attribute (`Style:` on `stat`, `Spacing:` on `stack`), yours replaces it.

```flag
textbox "Name" vm.Name Margin: "0,0,0,12" MaxLength: 80;
textarea "Notes" vm.Notes AutomationProperties.Name: "Notes" AutomationProperties.HelpText: "Free text";
text "Hint" Foreground: theme(TextFillColorSecondaryBrush) TextWrapping: wrap;
card "Details" Padding: 24 CornerRadius: 8 {
    text vm.Summary;
}
stack Spacing: 16 ToolTipService.Placement: Bottom {
    checkbox "Agree" vm.Agree IsThreeState: false;
}
```

| Keyword | Root tag | Notes |
|---|---|---|
| `textbox`, `textarea` | `TextBox` | |
| `number` / `select` / `checkbox` / `radio` / `toggle` / `datepicker` | `NumberBox` / `ComboBox` / `CheckBox` / `RadioButton` / `ToggleSwitch` / `CalendarDatePicker` | |
| `button`, `filePicker` | `Button` | with `icon:` the icon and label are the content |
| `link` | `HyperlinkButton` | |
| `image` / `progress` / `spinner` | `Image` / `ProgressBar` / `ProgressRing` | |
| `text`, `heading` | `TextBlock` | `heading` with `icon:` writes a `StackPanel`: a TextBlock-only prop there is FS1031 |
| `badge` | `InfoBadge` | only when bound to an `int`; otherwise a `Border` pill (an InfoBadge-only prop is FS1031) |
| `card`, `stat`, `separator`, `spacer` | `Border` | `spacer` only in vertical containers |
| `group`, `stack`, `column`, `summary` | `StackPanel` | becomes a `Grid` when a child has `fill` |
| `row`, `cards`, `split`, `toolbar`, `line` | `Grid` | |
| `table`, `list` | `ListView` | |
| `alert`, `toast` | `InfoBar` | `toast`: scalar attributes only |
| `dialog`, `confirm` | `Grid` | the overlay root; scalar attributes only |
| `col`, `empty`, `xaml` | none | FS1033 |

A key with an **element or block value** is a property element inside the root tag. The ones that matter most:

| Property element | On | Effect |
|---|---|---|
| `Flyout: MenuFlyout { ... }` / `Flyout: Flyout { ... }` | `button` | A drop-down menu or panel; counts as the button's action |
| `ContextFlyout: MenuFlyout { ... }` | any keyword with a root tag | Right-click menu |
| `ContextFlyout:` on `table`/`list` | the **row** | Written on each row, so `-> vm.XCommand(item)`, `item.X`, and `enabled(item.X)` refer to the clicked row |
| `KeyboardAccelerators: { KeyboardAccelerator Key: "S" Modifiers: "Control"; }` | `button`, inputs, ... | Shortcut keys |

```flag
button "More" Margin: "0,8,0,0" AutomationProperties.Name: "More actions" {
    Flyout: MenuFlyout {
        MenuFlyoutItem Text: "Duplicate" -> vm.DuplicateCommand;
        MenuFlyoutItem Text: "Archive" -> vm.ArchiveCommand enabled(vm.CanArchive);
        MenuFlyoutSeparator;
        ToggleMenuFlyoutItem Text: "Pinned" IsChecked: vm.IsPinned;
    }
}
button "Save" -> vm.SaveCommand KeyboardAccelerators: { KeyboardAccelerator Key: "S" Modifiers: "Control"; };
table items: vm.Orders of Order {
    col "Client" bind: item.Client;
    ContextFlyout: MenuFlyout {
        MenuFlyoutItem Text: "Open" -> vm.OpenCommand(item);
        MenuFlyoutItem Text: "Mark" -> vm.MarkCommand(item) enabled(item.CanMark);
    }
}
```

Other property elements on a `table`/`list` (`KeyboardAccelerators:`, `Header:`) stay on the `ListView`, outside
the row: `(item)` there is FS2007. A `toast`/`dialog`/`confirm` inside a flyout is hoisted like any overlay.

**FS1033** reports what no emitter can write: anything on `col`, `empty`, or `xaml`; property elements on
`dialog`, `confirm`, and `toast`; XAML props or `on` on a `spacer` inside a `toolbar`, `row`, or `split`. Nothing is
dropped silently. Wrap the element in a `stack` and put the props there.

## Locality Rules

**The test.** An element must be judgeable from its own line and its parent alone, and a mistake must be an error
at that line. XAML fails this wherever a line depends on text elsewhere: `Grid.Row="2"` must agree with
`RowDefinitions` far above, `ElementName="Nav"` must match an `x:Name` in another subtree, a resource key lives in
another dictionary. Flagstaff lets an attribute pass through only when its correctness is local; every non-local
reference has a form the compiler resolves and checks.

**Grid cells.** `Grid.Row`, `Grid.Column`, `Grid.RowSpan`, and `Grid.ColumnSpan` are allowed only on a direct
child of a generic `Grid` that declares `RowDefinitions:` / `ColumnDefinitions:`, as int literals, inside the
declared counts (FS1038 otherwise, naming the count). Keyword layouts (`row`, `split`, `cards`, `stack`) place
children by position and never take `Grid.*` props.

```flag
Grid RowDefinitions: { RowDefinition Height: Auto; RowDefinition Height: "*"; } ColumnDefinitions: { ColumnDefinition Width: 200; ColumnDefinition Width: "*"; } {
    text "Name" Grid.Row: 0 Grid.Column: 0;
    textbox vm.Name Grid.Row: 0 Grid.Column: 1;
    textarea "Notes" vm.Notes Grid.Row: 1 Grid.Column: 0 Grid.ColumnSpan: 2;
}
```

**Element references: `ref(Name)`.** Every reference to another element is `ref(Name)` (or `ref(Name).Member`),
never a string. `Name` must be a `name:` declared in the same view (FS1039, with a suggestion); `.Member` must be a
property of that element's type (FS1031). A reference property written as a string is FS1040.

| Where | Write | XAML |
|---|---|---|
| `Setter Target:` (visual states) | `ref(Nav).DisplayMode` | `Target="Nav.DisplayMode"` |
| `Storyboard.TargetName:` | `ref(Panel)` | `Storyboard.TargetName="Panel"` |
| `TeachingTip Target:`, `Flyout Target:` | `ref(SaveButton)` | `Target="{x:Bind SaveButton}"` |
| `RelativePanel.Below:`, `RightOf:`, `Align*With:`, ... | `ref(Search)` | `RelativePanel.RightOf="Search"` |
| any other property (an element binding) | `Text: ref(Query).Text` | `Text="{x:Bind Query.Text, Mode=OneWay}"` |

```flag
RelativePanel {
    textbox "Search" vm.Query name: Search RelativePanel.AlignLeftWithPanel: true;
    button "Go" -> vm.SearchCommand name: GoButton RelativePanel.RightOf: ref(Search) RelativePanel.AlignBottomWith: ref(Search);
}
text Text: ref(Search).Text;
TeachingTip Target: ref(GoButton) Title: "Search" Subtitle: "Press Go to search." IsOpen: vm.ShowTip;
```

**Attached properties.** A dotted key is either the element's own property (`Grid.RowDefinitions` on a `Grid`)
or an attached property, which must be on the allowlist:

| Attached property | Rule |
|---|---|
| `AutomationProperties.*`, `ToolTipService.*`, `Canvas.*`, `ScrollViewer.*`, `Typography.*`, `VariableSizedWrapGrid.*`, `NavigationView.*`, `ItemsControl.*`, `FlyoutBase.AttachedFlyout`, `RelativePanel.Align*WithPanel`, `Storyboard.TargetProperty`, `VisualStateManager.VisualStateGroups` | Local: passes through, value shape checked |
| `Grid.Row`, `Grid.Column`, `Grid.RowSpan`, `Grid.ColumnSpan` | Checked against the parent generic `Grid` (FS1038) |
| `RelativePanel.Above`/`Below`/`LeftOf`/`RightOf`/`Align*With`, `Storyboard.TargetName` | `ref(Name)` only (FS1040) |
| anything else (`VisualStateManager.CustomVisualStateManager`, ...) | FS1041 |

A misspelled owner (`Gird.Row`) is FS1037; an unknown member of a known owner (`Grid.IsSharedSizeScope`) is
FS1031.

**Deliberately rejected,** because their correctness lives elsewhere: `Grid.*` on children of keyword layouts or
outside a generic `Grid` (the layout keywords already place by position), element names as strings
(`Target: "Nav.DisplayMode"`, `ElementName=`), and attached properties whose meaning depends on other elements.
When you need one, use a keyword layout, `ref()`, or `xaml { }`.

## Events

`on Event -> target` wires any event of the element's type (a generic element's type, or a keyword's root tag; see
Pass-Through Props) to a generated code-behind handler. Write it in the header or as a line in the block; an
element may have several.

```flag
Slider Value: vm.Volume Minimum: 0 Maximum: 100 on ValueChanged -> vm.VolumeChangedCommand;
Border Padding: 12 on Tapped -> navigate(DetailView) {
    text "Open details";
}
card "Hover me" {
    on PointerEntered -> vm.Hover;
    on PointerExited -> vm.Unhover;
    text "A card with pointer handlers";
}
select "Owner" vm.Owner items: vm.Owners of Person on SelectionChanged -> vm.AssignCommand(item);
```

| Target | The handler `OnEvent_n(object sender, <EventArgs> e)` |
|---|---|
| `vm.XCommand` (or `vm.X`) | Runs `XCommand` if it can execute (generated like `->`: `OnXAsync()`) |
| `vm.XCommand(item)` inside a `list`/`table` row | Runs it with the row the sender belongs to |
| `vm.XCommand(item)` on `SelectionChanged` of an element with `of T` | Runs it with the newly selected item (a cleared selection runs nothing) |
| `vm.XCommand(item)` on `ItemClick` of an element with `of T` | Runs it with the clicked item |
| `navigate(View)` (or `View`) | `Frame.Navigate(typeof(View))` |
| `cancel` | Inside a `dialog`/`confirm`: clears its flag (FS1024 elsewhere) |

- The arguments type comes from the manifest. Commands are not awaited; a running async command reports
  `CanExecute == false`, so a burst of `TextChanged` events runs it once.
- `on ItemClick` on a `ListView`-based element also sets `IsItemClickEnabled="True"`.
- FS1034: the type has no such event (suggestions follow), or the keyword has no root tag (`col`, `empty`).
- FS1043: the same event twice on one element, or an event the element's `->` already wires (`Click` on a
  `button -> navigate(...)`, `ItemClick` on a `table`/`list` with `->`). A `button` with `->` may also take
  `on Click` when `->` is a command.
- FS2015: the target `vm.X` is bound as a property elsewhere in the view; name the command `vm.XCommand`.
- FS2007: `(item)` with no row and no `SelectionChanged`/`ItemClick` source with `of T`.
- Starting a storyboard is not an event target; call `Begin()` in your page partial.

## Resources and States

Resources, styles, visual states, and storyboards are generic elements in property elements. What Flagstaff adds
is a check for each cross-reference at its own line.

```flag
Page.Resources: {
    Style key: CompactStat TargetType: Border {
        Setter Property: Padding Value: "12,8";
        Setter Property: Background Value: theme(AccentFillColorDefaultBrush);
    }
    Style key: CompactStatWide TargetType: Border BasedOn: resource(CompactStat) {
        Setter Property: MinWidth Value: 240;
    }
    Style TargetType: Button {
        Setter Property: CornerRadius Value: 8;
    }
    SolidColorBrush key: Accent Color: "#2563eb";
    Storyboard key: FadeIn {
        DoubleAnimation Storyboard.TargetName: ref(Panel) Storyboard.TargetProperty: "Opacity" From: 0 To: 1 Duration: "0:0:0.3";
    }
}
VisualStateManager.VisualStateGroups: {
    VisualStateGroup name: Layout {
        VisualState name: Wide {
            StateTriggers: { AdaptiveTrigger MinWindowWidth: 720; }
            Setters: {
                Setter Target: ref(Nav).DisplayMode Value: Inline;
                Setter Target: ref(Summary).Padding Value: "24,16";
            }
        }
    }
}

SplitView name: Nav DisplayMode: Overlay OpenPaneLength: 280 {
    Pane: StackPanel Padding: 12 {
        button "Inbox" -> vm.InboxCommand;
    }
    stack {
        stat "Total" vm.Total Style: resource(CompactStat);
        card "Summary" name: Summary Style: resource(CompactStatWide) {
            text "Ready";
        }
        Border name: Panel BorderBrush: resource(Accent) BorderThickness: 1 load: load(vm.ShowPanel) uid: "Panel" {
            text "Animated panel";
        }
    }
}
```

| Construct | Rules |
|---|---|
| `Page.Resources: { ... }` | View level; written before the page's root `Grid`. A generic element's own `Resources:` works the same way |
| `key: K` | `x:Key`, only on a generic element directly inside a resources block (FS1044 elsewhere and on keywords). Every entry needs one, except an implicit `Style TargetType: T` (FS1044). A key twice in one block is FS1045 |
| `resource(K)` | `{StaticResource K}`. `K` must be a `key:` in the view's resources, a Flagstaff style, or a WinUI resource Flagstaff uses (FS3010, warning). In one block, declare `K` before a `BasedOn: resource(K)` that uses it |
| `theme(K)` | `{ThemeResource K}` (WinUI theme brushes such as `CardBackgroundFillColorDefaultBrush`); theme keys are not checked |
| `Style TargetType: T { Setter Property: P Value: V; }` | `P` must be a property of `T` (own or attached, FS1031); `V` must fit `P` (FS1032); setter values may be quoted (`"True"`, `"12,8"`) |
| `VisualStateManager.VisualStateGroups: { ... }` | View level: the first child of the page's root `Grid`. `VisualState name: Wide` → `x:Name`. `AdaptiveTrigger MinWindowWidth:` in `StateTriggers:` |
| `Setter Target: ref(N).P Value: V` | `N` is a `name:` in the view (FS1039), `P` a property of N's type or keyword root tag (FS1031); a string target is FS1040. A bare `Value: Inline` is a value; an object value is an element (`Value: SolidColorBrush Color: Red;`) |
| `Storyboard key: K { DoubleAnimation ... }` | `Storyboard.TargetName: ref(N)` (on the animation or the `Storyboard`). A simple `Storyboard.TargetProperty: "Opacity"` must be a property of N whose type fits the animation (`Double`/`Color`/`Point`; FS1032); a parenthesised path passes as written. Times are XAML strings (`"0:0:0.3"`), not milliseconds. Start it in code: `((Storyboard)Resources["FadeIn"]).Begin()` |
| `load: load(vm.X)` | `x:Load`: the element is created while `vm.X` (`bool`) is true |
| `uid: "K"` | `x:Uid`: the `.resw` localization key |

`ResourceDictionary { ... }` with entries and `ThemeDictionaries:` are not supported (FS1035/FS1031);
`MergedDictionaries: { ResourceDictionary Source: "ms-appx:///Styles/Extra.xaml"; }` works.

## XAML Escape

Prefer **keyword > generic element > `xaml { }`**. The escape copies XAML verbatim, and the compiler cannot check
anything inside it: bindings there create no ViewModel members, `when()` cannot reach it, and mistakes surface only
in the XAML compiler or at run time.

```flag
xaml {
    <Button Content="Flat"><Button.Template><ControlTemplate TargetType="Button">
        <Border Background="{TemplateBinding Background}" Padding="8"><ContentPresenter /></Border>
    </ControlTemplate></Button.Template></Button>
}
```

What needs it:

- **Control templates:** `ControlTemplate` internals and `{TemplateBinding X}`.
- **Other markup extensions:** `{RelativeSource}`, hand-written `{Binding}`, `{CustomResource}`, `{x:Bind}` with
  functions, converters, `FallbackValue` or `TargetNullValue`.
- **x: directives without a prop:** `x:Phase`, `x:DeferLoadStrategy` (use `load:`), `x:FieldModifier`,
  `x:DefaultBindMode`, `x:Shared`.
- **Event handlers** that `on Event -> target` cannot express (your own handler methods).
- **Unknown types:** `local:` user controls and types outside `Microsoft.UI.Xaml.*` (third-party types go through
  `use`).
- **Theme dictionaries** and `ResourceDictionary` entries (see Resources and States).

Rules:

- The body is copied verbatim (balanced `{ }` are allowed inside). Prefixes in scope: the default namespace,
  `x:`, `local:` (app namespace), `vm:` (`<Namespace>.ViewModels`), and every `use` prefix.
- The generated page is linted: FS3001 (not well-formed), FS3002 (`FontIcon` without `Glyph`), FS3007 (`Window.Resources`),
  FS3008 (duplicate `x:Name`), FS3010 (unknown `StaticResource`).
- `{x:Bind ViewModel.X}` works at page level. In a `DataTemplate` you write, set `x:DataType` (FS3006) and bind to
  the item, not `ViewModel.` (FS3005).
- `flagstaff check` warns **FS3011** on a block that would be a valid generic element (every type known, only
  `x:Bind`/`StaticResource`/`ThemeResource`): rewrite it in the element form.
- `flagstaff check` reports the **escape ratio** (`xaml` lines / `.flag` lines) and the share of generic elements
  after the diagnostics, with one line per block naming why it needs XAML (see CLI). Drive it down.

## Diagnostics

Format: `path(line,col): error FS1001: message [hint: ...]`. With `--format json`: `severity`, `code`,
`message`, `file`, `line`, `column`, `hint`. Read the hint first; it usually contains the fix. Parse and schema
errors stop code generation for that file; the other files compile.

| Code | Severity | Meaning |
|---|---|---|
| FS1000 | error | Unknown element keyword (suggestion; use `xaml { }`) |
| FS1001 | error | Unknown property (suggestion; `value:` → use the positional binding) |
| FS1002 | error | Property exists, but not on this keyword |
| FS1003 | error | Invalid enum value (`variant:`, `align:`, theme values, `format:` kind) |
| FS1004 | error | Label, positional binding, `{ }` block, or `->` not allowed on this keyword |
| FS1005 | error | Property value has the wrong shape (`cols: "3"`, `options: [1]`, `width: wide`, a bare non-flag) |
| FS1010 | error | More than one `fill` child in one vertical container |
| FS1011 | error | `row cols: N` children need more than N columns (no `wrap`), or `span:` wider than `cols:` |
| FS1012 | warning | Layout prop or `enabled()` with no effect where it is |
| FS1013 | warning | `fill` inside a scrolling region |
| FS1020 | error | `toast`/`confirm`/`dialog` inside a `list`/`table` row |
| FS1021 | error | `toast`/`confirm`/`dialog` without `bind vm:` or without `when(vm.Flag)` |
| FS1022 | error | `confirm` with both `->` and buttons, with neither, or `confirmLabel:` next to its own buttons |
| FS1023 | warning | `button` / `filePicker` / `link` without an action (no `->`, `on Click`, `Flyout:`, or `NavigateUri:`): it does nothing |
| FS1024 | error | `-> cancel` outside a `dialog` / `confirm`, or on something other than a `button` (`on Event -> cancel`: any element inside one) |
| FS1030 | error | Unknown (or abstract) WinUI type in a generic element (suggestion; third-party types need `use`), or a UWP-only control WinUI 3 lacks (`InkCanvas`, `MediaElement` → `MediaPlayerElement`, `WebView` → `WebView2`) |
| FS1031 | error | Unknown property on a generic element's type, unknown member in `ref(Name).Member`, a `Setter Property:` the Style's `TargetType` lacks, or a `Storyboard.TargetProperty` the target lacks |
| FS1032 | error | Generic property value has the wrong shape (expected CLR type or enum members), including `Setter Value:` against the property it sets and `load:` without `load(vm.X)` |
| FS1033 | error | XAML attribute (`Margin:`) or property element (`Flyout: MenuFlyout { }`) on a keyword that cannot carry it (`dialog`/`confirm`/`toast` property elements, `col`, `empty`, `xaml`, a `spacer` in a toolbar/row; also `on Event` there) |
| FS1034 | error | `on Event` names no event of the element's type (generic type or keyword root tag; suggestion), or a keyword without a tag (`col`, `empty`) |
| FS1035 | error | Children on a type without a content property, several children for a one-child property, or the wrong item type |
| FS1036 | error | `prefix.Type` element whose prefix has no `use "<assembly>" as prefix;` |
| FS1037 | error | Unknown attached-property owner (`Gird.Row`) |
| FS1038 | error | `Grid.Row`/`Column`/`RowSpan`/`ColumnSpan` outside a generic `Grid` with definitions, or past them |
| FS1039 | error | `ref(Name)` names no `name:` declaration in the view |
| FS1040 | error | Reference property written as a string (`Setter Target: "Nav.X"`): use `ref(Name)` |
| FS1041 | error | Attached property not in the locality allowlist (use a keyword layout, `ref()`, or `xaml { }`) |
| FS1042 | error | The same `name:` declared twice in a view |
| FS1043 | error | The same `on Event` twice on one element, or an event its own `->` already handles (`Click` with `-> navigate(...)`, `ItemClick` on a `table`/`list` with `->`) |
| FS1044 | error | `key:` outside a resources block (or on a keyword element), or a resource entry without `key:` (an implicit `Style TargetType: T` needs none) |
| FS1045 | error | The same key twice in one resources block (two implicit Styles for one type included) |
| FS1046 | error | `Load: load(vm.X)`: use the universal prop `load:` (`load: load(vm.X)`) |
| FS1047 | error | `of T` on a generic element that types nothing: write it right after the binding it types (`ItemsSource: vm.Photos of Photo`) |
| FS1048 | error | `icon:` names no WinUI `Symbol` member (suggestion: `previous-track` → `previous`) |
| FS1050 | error | Unexpected character (`&` → `&&`, `=` → `==`, `/` → `xaml { }`) |
| FS1051 | error | Parse error (missing `;`, unbalanced braces, bad condition or value, props on `xaml`) |
| FS1052 | error | Command argument other than `(item)` |
| FS2003 | error | `toast`/`confirm`/`dialog` need a bare `when(vm.Bool)` |
| FS2004 | error | Type mismatch in a condition |
| FS2005 | error | `vm.X` inside a row template, or `item.X` outside one |
| FS2006 | warning | `onload -> vm.XAsync;`: write `onload -> vm.X;` and implement `OnXAsync()` |
| FS2007 | error | `-> vm.XCommand(item)` / `on Event -> vm.XCommand(item)` outside a `table`/`list` with `of T` (`SelectionChanged`/`ItemClick` on an element with `of T` pass the selected/clicked item) |
| FS2010 | error | Conflicting explicit types for one item property |
| FS2011 | error | `empty` outside a `table`/`list`, or its collection has no `items:` |
| FS2012 | error | `col` outside a `table`, or a `table` child other than `col`/`button`/`empty` |
| FS2013 | error | `select display:` without `items: vm.X of T` |
| FS2014 | error | `->` on a generic element whose type has no `Command` property (or `Click` event for `navigate`) |
| FS2015 | error | `on Event -> vm.X` where `vm.X` is bound as a property in the view (not a command) |
| FS2016 | warning | Your C# partial already declares a member the `.flag` generates (property, command, `On{X}Async` declaration, page constructor, item property): the generator skips its own and uses your declaration (and its type); prefer deleting yours |
| FS2017 | warning | You implemented a `partial On{X}Async` that no `.flag` command declares: the generator declares it so the build succeeds, but nothing calls it |
| FS2018 | error | Your C# partial declares a member the `.flag` binds with a type that cannot work with that usage (a `checkbox` bound to a `string`, `format: currency` on a `bool`, a `number` on a `decimal`, `of Order` over a collection of another type): declare the type the hint names, or delete yours |
| FS2019 | error | Your `On{X}Async` has a parameter list its command cannot call (`OnDeleteAsync(Contact)` for `button "Delete" -> vm.Delete`, which passes no row): implement the signature the message names, or pass the row with `-> vm.Delete(item)` inside a `table`/`list` of that type |
| FS3001 | error | Generated XAML is not well-formed |
| FS3002 | error | `<FontIcon>` without `Glyph` renders nothing |
| FS3003 | warning | Unknown `Symbol` name inside `xaml { }` |
| FS3004 | error | `Grid.Column` exceeds the column count |
| FS3005 | warning | `x:Bind ViewModel.` inside a `DataTemplate` |
| FS3006 | error | `DataTemplate` uses `x:Bind` without `x:DataType` (e.g. `list` without `of T`) |
| FS3007 | error | `<Window.Resources>` (invalid in WinUI 3) |
| FS3008 | error | Duplicate `x:Name` |
| FS3009 | warning | `x:Class` namespace does not match the app namespace |
| FS3010 | warning | Unknown `StaticResource` key: `resource(Key)` with no `key: Key` in the view's `Page.Resources` and not a Flagstaff/App resource |
| FS3011 | warning | `xaml { }` block is expressible as a generic element (every type known, only `x:Bind`/`StaticResource`/`ThemeResource`); reported by `flagstaff check`/`compile`; the hint writes the block's root as that generic element |
| FS3012 | error | Generated XAML has `x:Load` without `x:Name` (safety net: `load:` without `name:` gets `x:Name="FlagstaffLoad_n"`) |
| FS4001 | warning | Generic element with a keyword equivalent (`TextBox` → use `textbox`) |
| FS4002 | warning | `prefix.Type` element not in the control manifest: its properties pass through unvalidated |
| FS4003 | warning | Binding on an unvalidated (`prefix.Type`) element infers `object`; add `of <Type>` (`Series: vm.Series of ISeries`, `Value: vm.Volume of double`) |

`flagstaff build` adds compiler codes: `CS8795`/`CS0103` for a missing `On{X}Async` (run
`flagstaff stubs --update`), and `WMC` codes from the XAML compiler.

## Common Mistakes

Each block shows a mistake, then the fix after `// RIGHT`. The fence label names the diagnostic.

```flag wrong FS1001
stat "Open" value: vm.OpenCount;     // WRONG: bindings are positional
// RIGHT
stat "Open" vm.OpenCount;
```
```flag wrong FS1051
view DashboardView {
    bind vm.DashboardViewModel;      // WRONG: bind vm: Type;
}
// RIGHT
view DashboardView {
    bind vm: DashboardViewModel;
}
```
```flag wrong FS1051
text "Saved"                         // WRONG: every leaf ends with ;
heading "Next";
// RIGHT
text "Saved";
heading "Next";
```
```flag wrong FS1000
treeview items: vm.Nodes;            // WRONG: invented keyword
// RIGHT
TreeView name: Nodes SelectionMode: Single;   // a WinUI type is a generic element
```
```flag wrong FS1002
table items: vm.Rows of Row tooltip: "Rows" { col "A" bind: item.A; }
// RIGHT
stack tooltip: "Rows" { table items: vm.Rows of Row { col "A" bind: item.A; } }
```
```flag wrong FS1003
alert when(vm.Failed) variant: error { "Failed."; }
// RIGHT
alert when(vm.Failed) variant: danger { "Failed."; }
```
```flag wrong FS1004
table items: vm.Rows of Row { column "Name" bind: item.Name; }   // WRONG: column is a layout stack
// RIGHT
table items: vm.Rows of Row { col "Name" bind: item.Name; }
```
```flag wrong FS1005
table items: vm.Rows of Row { col "Name" bind: item.Name width: wide; } // WRONG: auto, N, *, or N*
// RIGHT
table items: vm.Rows of Row { col "Name" bind: item.Name width: 2*; }
```
```flag wrong FS1023
button "Close";                      // WRONG: no action
// RIGHT
button "Close" -> vm.CloseCommand;
```
```flag wrong FS1024
button "Cancel" -> cancel;           // WRONG: cancel only closes a dialog/confirm
// RIGHT
dialog "Edit" when(vm.IsEditOpen) { button "Cancel" -> cancel; }
```
```flag wrong FS1030
Expandr Header: "Advanced";          // WRONG: not a WinUI type
// RIGHT
Expander Header: "Advanced";
```
```flag wrong FS1031
Expander Title: "Advanced";          // WRONG: Expander has Header, not Title
// RIGHT
Expander Header: "Advanced";
```
```flag wrong FS1032
StackPanel Orientation: Sideways;    // WRONG: Vertical or Horizontal
// RIGHT
StackPanel Orientation: Horizontal;
```
```flag wrong FS1033
table items: vm.Rows of Row { col "A" bind: item.A Margin: 4; }   // WRONG: col has no tag of its own
// RIGHT
table items: vm.Rows of Row Margin: 4 { col "A" bind: item.A; }
```
```flag wrong FS1034
Slider Value: vm.Volume on Click -> vm.ApplyCommand;   // WRONG: Slider has no Click event
// RIGHT
Slider Value: vm.Volume on ValueChanged -> vm.ApplyCommand;
```
```flag wrong FS1035
Rectangle Height: 8 { text "Label"; }   // WRONG: a Rectangle has no content
// RIGHT
Border Height: 8 { text "Label"; }
```
```flag wrong FS1036
lvc.CartesianChart Series: vm.Series;   // WRONG: the prefix needs use "<assembly>" as lvc; at the top of the file
```
```flag wrong FS1038
stack { text "Name" Grid.Row: 1; }   // WRONG: keyword layouts place children by position
// RIGHT
Grid RowDefinitions: { RowDefinition Height: Auto; RowDefinition; } { text "Name" Grid.Row: 1; }
```
```flag wrong FS1039
textbox "Name" vm.Name name: NameBox;
TeachingTip Target: ref(NameBx) Title: "Required" IsOpen: vm.ShowHint;   // WRONG: no such name:
// RIGHT
textbox "Name" vm.Name name: NameBox;
TeachingTip Target: ref(NameBox) Title: "Required" IsOpen: vm.ShowHint;
```
```flag wrong FS1040
textbox "Name" vm.Name name: NameBox;
TeachingTip Target: "NameBox" Title: "Required" IsOpen: vm.ShowHint;   // WRONG: references use ref()
// RIGHT
textbox "Name" vm.Name name: NameBox;
TeachingTip Target: ref(NameBox) Title: "Required" IsOpen: vm.ShowHint;
```
```flag wrong FS1041
text "Status" VisualStateManager.CustomVisualStateManager: true;   // WRONG: not a local attached property
// RIGHT
text "Status" AutomationProperties.LiveSetting: Polite;
```
```flag wrong FS1043
button "Settings" -> navigate(SettingsView) on Click -> vm.LogCommand;   // WRONG: -> navigate already handles Click
// RIGHT
button "Settings" -> navigate(SettingsView);
```
```flag wrong FS1044
stat "Total" vm.Total key: Big;      // WRONG: key: is for resources
// RIGHT
Page.Resources: { Style key: Big TargetType: Border { Setter Property: Padding Value: 24; } }
stat "Total" vm.Total Style: resource(Big);
```
```flag wrong FS1050
text "Ready" when(vm.IsLoaded & vm.HasData);
// RIGHT
text "Ready" when(vm.IsLoaded && vm.HasData);
```
```flag wrong FS1010
stack fill { textarea "A" vm.A fill; textarea "B" vm.B fill; }
// RIGHT
stack fill { split fill { textarea "A" vm.A fill; textarea "B" vm.B fill; } }
```
```flag wrong FS1011
row cols: 2 { textbox "First" vm.First; textbox "Last" vm.Last; textbox "Email" vm.Email; }
// RIGHT
row cols: 2 wrap { textbox "First" vm.First; textbox "Last" vm.Last; textbox "Email" vm.Email; }
```
```flag wrong FS1012
text "Total" span: 2;                // WRONG: span only works in row/cards
// RIGHT
row cols: 3 { text "Total" span: 2; text "42"; }
```
```flag wrong FS1013
card "Log" { textarea "Log" vm.Log fill; }   // WRONG: the page body scrolls
// RIGHT
card "Log" fill { textarea "Log" vm.Log fill; }
```
```flag wrong FS1020
list items: vm.Rows of Row { text item.Name; toast "Saved" when(vm.ShowSaved); }
// RIGHT
list items: vm.Rows of Row { text item.Name; }
toast "Saved" when(vm.ShowSaved);
```
```flag wrong FS1021
dialog "Edit" { button "Close" -> vm.CloseCommand; }
// RIGHT
dialog "Edit" when(vm.IsEditOpen) { button "Close" -> vm.CloseCommand; }
```
```flag wrong FS1022
confirm "Delete?" when(vm.IsDeleteOpen);
// RIGHT
confirm "Delete?" when(vm.IsDeleteOpen) -> vm.ConfirmDeleteCommand;
```
```flag wrong FS1052
table items: vm.Rows of Row { col "A" bind: item.A; button "Edit" -> vm.EditCommand(item.Id); }
// RIGHT
table items: vm.Rows of Row { col "A" bind: item.A; button "Edit" -> vm.EditCommand(item); }
```
```flag wrong FS2003
toast "Saved" when(!vm.IsDirty);
// RIGHT
toast "Saved" when(vm.ShowSaved);
```
```flag wrong FS2004
checkbox "Done" vm.Done;
text "Finished" when(vm.Done == 1);
// RIGHT
checkbox "Done" vm.Done;
text "Finished" when(vm.Done);
```
```flag wrong FS2005
list items: vm.Rows of Row { text vm.Title; }
// RIGHT
list items: vm.Rows of Row { text item.Title; }
```
```flag wrong FS2006
onload -> vm.LoadAsync;
// RIGHT
onload -> vm.Load;
```
```flag wrong FS2007
table items: vm.Orders { col "Id" bind: item.Id; button "Edit" -> vm.EditCommand(item); }
// RIGHT
table items: vm.Orders of Order { col "Id" bind: item.Id; button "Edit" -> vm.EditCommand(item); }
```
```flag wrong FS2010
table items: vm.Orders of Order { col "Total" bind: item.Total format: currency; col "Placed" bind: item.Total format: date; }
// RIGHT
table items: vm.Orders of Order { col "Total" bind: item.Total format: currency; col "Placed" bind: item.PlacedOn format: date; }
```
```flag wrong FS2011
empty "Nothing here.";
// RIGHT
table items: vm.Rows of Row { col "A" bind: item.A; empty "Nothing here."; }
```
```flag wrong FS2012
table items: vm.Rows of Row { text item.Name; }   // WRONG: free-form rows need list
// RIGHT
list items: vm.Rows of Row { text item.Name; }
```
```flag wrong FS2013
select "Vendor" vm.Vendor display: item.Name;
// RIGHT
select "Vendor" vm.Vendor items: vm.Vendors of Vendor display: item.Name;
```
```flag wrong FS2014
Border Padding: 12 -> vm.OpenCommand { text "Open"; }   // WRONG: Border has no Command
// RIGHT
Border Padding: 12 on Tapped -> vm.OpenCommand { text "Open"; }
```
```flag wrong FS2015
textbox "Query" vm.Query on TextChanged -> vm.Query;   // WRONG: vm.Query is a property
// RIGHT
textbox "Query" vm.Query on TextChanged -> vm.SearchCommand;
```
```flag wrong FS3002
xaml { <FontIcon /> }   // WRONG: no Glyph, renders nothing
// RIGHT
FontIcon Glyph: "";
```
```flag wrong FS1048
heading "Settings" icon: settings;   // WRONG: the Symbol is Setting
// RIGHT
heading "Settings" icon: setting;
```
```flag wrong FS1051
xaml fill { <local:SketchSurface /> }   // WRONG: xaml takes no props
// RIGHT
stack fill { xaml { <local:SketchSurface /> } }
```
```flag wrong FS3006
list items: vm.Rows { text item.Name; }
// RIGHT
list items: vm.Rows of Row { text item.Name; }
```
```flag wrong FS4001
TextBox Text: vm.Query;              // WARNING: TextBox has a keyword
// RIGHT
textbox vm.Query;
```

Mistakes in your C# partials that the compiler reports, because it reads them (see The hand-written half):

```csharp
// FS2018: checkbox "Billable" vm.Billable; needs a bool
[ObservableProperty] private string _billable = "";      // WRONG
[ObservableProperty] private bool _billable;             // RIGHT (or delete it: the .flag generates it)

// FS2019: button "Delete" -> vm.Delete; passes no row
private partial async Task OnDeleteAsync(Contact contact) { ... }   // WRONG
// RIGHT: pass the row from inside the table, then take it
//   table items: vm.Contacts of Contact { ...; button "Delete" -> vm.Delete(item); }
private partial async Task OnDeleteAsync(Contact item) { ... }

// CS8799 (build hint): CommunityToolkit declares its hooks without an access modifier
private partial void OnVolumeChanged(double value) { ... }          // WRONG
partial void OnVolumeChanged(double value) { ... }                  // RIGHT

// CS0246 (build hint): WinUI 3 has no InkCanvas/InkPresenter; shapes need their using
var stroke = new Polyline();                                         // add: using Microsoft.UI.Xaml.Shapes;
```

Mistakes the compiler cannot catch:

- Not clearing a dialog's flag in its Save command: the dialog stays open (for Cancel, use `-> cancel`).
- Editing `Generated/`: the next compile overwrites it.
- Setting `item.X` on an existing row: rows bind OneTime; replace the item in the collection.

## CLI

Run from the app folder (the one with the `.csproj`). Defaults: input `./Flags` (else `.`), output `./Generated`,
namespace from the project's `RootNamespace`, else the `app` name.

| Command | Does |
|---|---|
| `flagstaff init <AppName> [-o dir] [--nav side\|top] [--agent claude\|cursor\|none]` | Create a runnable WinUI 3 + Flagstaff project in `dir` (default `./<AppName>`), optionally with agent instructions |
| `flagstaff check [paths...] [-n ns] [--stats-only]` | Parse, generate in memory, and lint; writes nothing. Run after every edit. `--stats-only` prints only the stats (the exit code reflects errors) |
| `flagstaff compile [paths...] [-o dir] [-n ns] [-s styles] [--clean]` | Write `Generated/` and its manifest (the build also does this) |
| `flagstaff stubs [paths...] [--dir ViewModels] [-n ns] [--update]` | Create ViewModel partials with `On{X}Async` stubs; `--update` appends missing ones |
| `flagstaff build [project-or-dir] [-c Debug\|Release] [-p x64\|arm64] [--binlog]` | Build with Visual Studio's MSBuild.exe (reports XAML errors; C# errors against generated members get a hint) |
| `flagstaff run [project-or-dir] [--no-build]` | Build, then launch the app detached |
| `flagstaff watch [dir] [--run] [--once]` | Check and compile on every `.flag` change; `--run` rebuilds and relaunches |
| `flagstaff new view <Name> [--label "..."] [--icon name]` | Add `Flags/<name>.flag`, `ViewModels/<Name>ViewModel.cs`, and a nav link |
| `flagstaff doctor [dir]` | Check the .NET SDK, MSBuild.exe, the WinUI XAML compiler, and the project layout |
| `flagstaff spec [--section name] [--list]` | Print this reference, one section, or the section names |

Global options: `--format text|json` (json prints exactly one JSON document on stdout), `-q`/`--quiet`,
`--version`. Exit codes: `0` ok, `1` diagnostics or build errors, `2` usage, `3` environment (run
`flagstaff doctor`), `4` crash.

`check` and `compile` end with the project's stats: `.flag` lines, `xaml { }` blocks and their share of the lines
(the escape ratio), and keyword vs generic elements, then one line per escape block with the reasons it
needs XAML (`expressible`, `unknown-type`, `markup-extension`, `x-directive`, `event-handler`, `malformed`) and its
root types. `expressible` blocks also warn FS3011. With `--format json` the same data is a `stats` object
(`flagLines`, `escapeBlocks`, `escapeLines`, `escapeRatio`, `keywordElements`, `genericElements`, `genericRatio`,
`files`, `escape`).

```text
stats: 170 flag lines, 1 xaml block (1 line, 0.6%), 106 elements (12 generic)
  xaml Flags/folders.flag(9): markup-extension [Button]
```

The loop: edit `Flags/*.flag` → `flagstaff check --format json` until `"ok": true` → `flagstaff stubs --update`
→ implement the `On{X}Async` bodies → `flagstaff build --format json` until `"ok": true` → `flagstaff run`.
Do not build the app with `dotnet build`; it hides XAML compiler errors.

## Full Example

```flag
app TaskTrack {
    theme { accent: #2b6cb0; backdrop: mica; }
    nav side {
        brand "TaskTrack";
        link "Dashboard" icon: home -> DashboardView;
        link "Tasks" icon: list -> TasksView;
    }
}

view DashboardView {
    bind vm: DashboardViewModel;
    onload -> vm.Load;

    toolbar {
        heading "Dashboard" icon: home;
        spacer;
        button "Refresh" -> vm.RefreshCommand;
    }
    alert when(vm.OverdueCount > 0) variant: warning {
        "Some tasks are overdue.";
        button "Show tasks" -> navigate(TasksView);
    }
    cards cols: 3 {
        stat "Open" vm.OpenCount;
        stat "Done" vm.DoneRatio format: percent variant: success;
        stat "Overdue" vm.OverdueCount variant: danger;
    }
}

view TasksView {
    bind vm: TasksViewModel;
    onload -> vm.Load;

    toolbar {
        heading "Tasks" icon: list;
        spacer;
        textbox vm.Query placeholder: "Search" width: 240;
        button "New task" -> vm.NewTaskCommand variant: primary;
        button "More" {
            Flyout: MenuFlyout {
                MenuFlyoutItem Text: "Export" -> vm.ExportCommand;
                ToggleMenuFlyoutItem Text: "Show done" IsChecked: vm.ShowDone;
            }
        }
    }
    split fill {
        table items: vm.Tasks selected: vm.Selected of TaskItem width: 2* {
            col "Title" bind: item.Title width: 2*;
            col "Due" bind: item.Due format: date width: 120;
            col "Hours" bind: item.Hours format: number(1) width: 80;
            button "Delete" -> vm.DeleteCommand(item);
            empty "No tasks yet.";
        }
        card "Details" {
            text "Select a task." when(vm.Selected == null);
            stack when(vm.Selected != null) {
                textbox "Title" vm.Title;
                datepicker "Due" vm.Due;
                checkbox "Done" vm.IsDone;
                button "Save" -> vm.SaveCommand variant: primary enabled(vm.Title != "" && !vm.IsSaving);
            }
        }
    }

    dialog "New task" when(vm.IsNewOpen) {
        textbox "Title" vm.NewTitle;
        button "Cancel" -> cancel;
        button "Create" -> vm.CreateCommand variant: primary;
    }
    confirm "Delete task?" message: "This cannot be undone." when(vm.IsDeleteOpen) variant: danger confirmLabel: "Delete" -> vm.ConfirmDeleteCommand;
    toast "Saved." when(vm.ShowSaved) variant: success duration: 2500;
}
```

The hand-written side: `ViewModels/DashboardViewModel.cs` implements `OnLoadAsync` and `OnRefreshAsync`.
`ViewModels/TasksViewModel.cs` implements `OnLoadAsync`, `OnExportAsync`, `OnNewTaskAsync` (sets `IsNewOpen = true`),
`OnCreateAsync` (clears `IsNewOpen`; Cancel uses the generated `CancelIsNewOpenCommand`), `OnSaveAsync` (sets `ShowSaved = true`),
`OnDeleteAsync(TaskItem item)` (remembers the item, sets `IsDeleteOpen = true`), and `OnConfirmDeleteAsync`
(clears the flag, deletes). `App.Services.cs` registers the services the ViewModels take in their constructors.
