## Generic Elements

Any WinUI type can be written in the element form: a **PascalCase element name is a WinUI type** (keywords are
lowercase, so there is no ambiguity). The compiler validates it against a manifest generated from WinUI's metadata:
unknown types (FS1030), unknown properties (FS1031), wrong value shapes (FS1032), children where the type has no
content property (FS1035). Prefer a keyword whenever one exists (`TextBox` → `textbox`, warning FS4001); use a
generic element for everything the keywords do not cover (`Expander`, `TabView`, `MenuBar`, `Slider`,
`MediaPlayerElement`, `InkCanvas`, `GridView`, `FlipView`, `SplitView`, `TeachingTip`, ...). With generic elements,
`xaml { }` is needed only for the leftovers listed at the end of this section.

```flag
view SettingsView {
    bind vm: SettingsViewModel;

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
}
```

Rules:

- **PascalCase keys are XAML properties** of the element's type: `Header: "Advanced"` → `Header="Advanced"`.
  Values: strings, numbers, `true`/`false`, `#rrggbb` colors, named colors (`Red`, `CornflowerBlue`), enum members
  written bare (`Orientation: Horizontal`, `TextWrapping: Wrap`, `Placement: Bottom`), `"0,8,0,0"` for a
  Thickness, `vm.X` / `item.X` bindings, and the value forms below.
- **Lowercase keys keep their Flagstaff meaning.** `when()`, `enabled()`, `name:`, `width:`, `align:`, `tooltip:`,
  `fill`, `span:`, `uid:`, `load:` work on a generic element exactly as on a keyword element.
- **Bindings compile to `x:Bind`.** `Value: vm.CacheMb` → `Value="{x:Bind ViewModel.CacheMb, Mode=TwoWay}"`.
  Input properties (`Text`, `Value`, `IsChecked`, `IsOn`, `SelectedItem`, `SelectedIndex`, `Date`, `IsExpanded`,
  `IsOpen`, ...) bind `TwoWay`; everything else `OneWay`. The `vm.X` type comes from the property's CLR type
  (`Slider.Value` → `double`, `Expander.IsExpanded` → `bool`). Inside a `list`/`table` row, `item.X` binds to the row.
- **Positional binding** on a generic element binds its input property: `Slider vm.Volume Minimum: 0 Maximum: 100;`
  binds `Value`. A **label** sets the content property when it is a string/object (`Button "Save"`).
- **A string child is content**: `Button { "Save"; }` → `<Button Content="Save"/>`.
- **`-> vm.XCommand`** works on any type with a `Command` property (`Button`, `AppBarButton`, `MenuFlyoutItem`,
  `HyperlinkButton`, ...) and generates the command as `button` does; `-> navigate(View)` works on any type with a
  `Click` event. FS2014 otherwise.
- **Keywords and generic elements nest freely** in both directions: a `textbox` inside a `TabViewItem`, a `Slider`
  inside a `card`, an `Expander` inside a `list` row.
- **Templates.** Inside a generic `DataTemplate { }` the compiler does not know the binding root, so any binding,
  condition or command there is FS2005. For item templates use `list items: vm.X of T { ... }` (its row may contain
  generic elements), or a `xaml { }` block with `x:DataType`.

```flag
list items: vm.Projects of Project {
    Expander Header: item.Name IsExpanded: item.IsOpen {
        text item.Notes;
    }
    Button Content: "Open" -> vm.OpenCommand(item);
}

TabView SelectedIndex: vm.TabIndex IsAddTabButtonVisible: false {
    TabViewItem Header: "Today" IsClosable: false {
        IconSource: SymbolIconSource Symbol: Calendar;
        stack {
            heading "Today";
            Slider vm.Volume Minimum: 0 Maximum: 100 Orientation: Horizontal Margin: "0,8,0,0";
            CalendarDatePicker Date: vm.Due PlaceholderText: "Due date";
        }
    }
    TabViewItem Header: vm.DraftTitle IsClosable: false when(vm.HasDraft) {
        Border Padding: 12 CornerRadius: 4 Background: theme(CardBackgroundFillColorDefaultBrush) {
            TextBlock Text: vm.Draft TextWrapping: Wrap;
        }
    }
}
```

### Property elements

A PascalCase key followed by an **element** is a property element; a key followed by a **bare block** `{ ... }` is a
collection property:

```flag
Button Content: "More" AutomationProperties.Name: "More actions" {
    Flyout: MenuFlyout {
        Placement: Bottom;
        MenuFlyoutItem Text: "Duplicate" -> vm.DuplicateCommand;
        MenuFlyoutItem Text: "Archive" -> vm.ArchiveCommand enabled(vm.CanArchive);
        MenuFlyoutSeparator;
        ToggleMenuFlyoutItem Text: "Pinned" IsChecked: vm.IsPinned;
    }
}

StackPanel Orientation: Horizontal Spacing: 8 {
    AppBarButton Label: "Save" -> vm.SaveCommand Icon: SymbolIcon Symbol: Save;
    AppBarButton Label: "Undo" Icon: Undo -> vm.UndoCommand;
}
```

- `Flyout: MenuFlyout { ... }` → `<Button.Flyout><MenuFlyout>...</MenuFlyout></Button.Flyout>`.
- A `Key: ...` line inside an element's block is a property of that element, exactly like the same key in the header
  (`Placement: Bottom;` above). Scalar values end with `;`; element and block values need no `;`.
- **Bare enum values**: a PascalCase name followed by `;`, `}`, `->`, `when`, `enabled(`, `format`, `of` or `on` is a
  value, as is any enum member or named color (`Orientation: Horizontal Spacing: 8 { ... }` keeps `Spacing` and the
  block on the StackPanel). Otherwise a PascalCase name after a key starts an element value.
- **An element value that ends with `;` also ends its owner** when nothing else follows:
  `button "Save" Icon: SymbolIcon Symbol: "Save";` is one button. Clauses written after a value element belong to the
  value element: write `-> vm.X` **before** `Icon: SymbolIcon ...` (as `AppBarButton Label: "Undo" Icon: Undo ->
  vm.UndoCommand;` does with a bare value), otherwise the arrow lands on the icon.
- Keys whose name ends in `Type` (`TargetType:`) and `Setter Property:` take a type or property name.

### Pass-through on keyword elements

The same two rules work on **keyword** elements:

```flag
button "More" Margin: "0,8,0,0" AutomationProperties.Name: "More actions" {
    Flyout: MenuFlyout {
        MenuFlyoutItem Text: "Duplicate" -> vm.DuplicateCommand;
        MenuFlyoutItem Text: "Archive"   -> vm.ArchiveCommand enabled(vm.CanArchive);
    }
}

table items: vm.Orders of Order {
    col "Client" bind: item.Client;
    ContextFlyout: MenuFlyout {
        MenuFlyoutItem Text: "Open" -> vm.OpenCommand(item);
        MenuFlyoutItem Text: "Mark" -> vm.MarkCommand(item) enabled(item.CanMark);
    }
}

toolbar {
    button "Save" -> vm.SaveCommand {
        KeyboardAccelerators: {
            KeyboardAccelerator Key: "S" Modifiers: "Control";
        }
    }
}
```

- A **PascalCase key with a scalar value** (`Margin: "0,8"`, `MaxLength: 80`, `Style: resource(Compact)`,
  `Opacity: vm.Fade`, `AutomationProperties.Name: "Save"`) becomes an attribute on the keyword's **root tag**, checked
  against that tag's type (FS1031/FS1032). If it repeats an attribute the keyword writes itself, your value wins.
- A **PascalCase key with an element or block value** (`Flyout: MenuFlyout { }`, `ContextFlyout:`,
  `KeyboardAccelerators: { }`) becomes a property element inside the root tag.
- `ContextFlyout:` on a `table`/`list` belongs to each **row**: `-> vm.XCommand(item)` and `item.X` refer to the row
  (`vm.X` there is FS2005). Other property elements on a table/list stay on the ListView.
- `dialog`, `confirm` and `toast` take scalar attributes only; `col`, `empty`, `xaml` and a `spacer` in a toolbar/row
  take none (FS1033).

| Keyword | Root tag |
|---|---|
| `textbox`, `textarea` | `TextBox` |
| `number` | `NumberBox` |
| `select` | `ComboBox` |
| `checkbox` / `radio` / `toggle` | `CheckBox` / `RadioButton` / `ToggleSwitch` |
| `datepicker` | `CalendarDatePicker` |
| `button`, `filePicker` | `Button` |
| `link` | `HyperlinkButton` |
| `image` / `progress` / `spinner` | `Image` / `ProgressBar` / `ProgressRing` |
| `text`, `heading` | `TextBlock` (`heading` with `icon:` writes a StackPanel: TextBlock-only props are FS1031) |
| `badge` | `InfoBadge` when bound to an `int`, otherwise a `Border` pill |
| `card`, `stat`, `separator`, `spacer` | `Border` |
| `group`, `stack`, `column`, `summary` | `StackPanel` (a `Grid` when a child has `fill`) |
| `row`, `cards`, `split`, `toolbar`, `line` | `Grid` |
| `table`, `list` | `ListView` |
| `alert`, `toast` | `InfoBar` |
| `dialog`, `confirm` | `Grid` (the overlay root) |
| `xaml`, `col`, `empty` | none (FS1033) |

### Attached properties, references and locality

A property passes through only when its correctness is local to the element; anything that depends on another
element is checked by the compiler or rejected.

- **Dotted keys** are attached properties: `AutomationProperties.Name: "Save"`, `ToolTipService.Placement: Top`
  pass through. A misspelled owner is FS1037 (`Gird.Row`); an unknown member FS1031; an owner that is not a WinUI type,
  or an attached property not on the allowlist, is FS1041 (use a keyword layout, `ref()`, or `xaml { }`).
- **`Grid.Row` / `Grid.Column` / `Grid.RowSpan` / `Grid.ColumnSpan`** are allowed only on a **direct child of a
  generic `Grid`** that declares `RowDefinitions:` / `ColumnDefinitions:`, with literal ints inside the declared
  count (FS1038 otherwise). Keyword layouts (`row`, `split`, `cards`) never count as a Grid; inside them use `span:`
  and `width:`.

```flag
Grid RowDefinitions: { RowDefinition Height: "Auto"; RowDefinition Height: "*"; }
     ColumnDefinitions: { ColumnDefinition Width: "*"; ColumnDefinition Width: "320"; } {
    textbox "Search" vm.Query Grid.Row: 0 Grid.ColumnSpan: 2;
    Border Grid.Row: 1 Grid.Column: 1 Padding: 12 { text "Side panel"; }
}
```

- **References to named elements use `ref(Name)`**, never a string: give the target `name: Name`, then write
  `ref(Name)` (the element itself) or `ref(Name).Member` (one of its properties). `Setter Target:`,
  `TeachingTip Target:`, `Storyboard.TargetName:`, `RelativePanel.Below:` take `ref()` only (FS1040 for a string).
  An unknown name is FS1039; a member the named element's type lacks is FS1031; the same `name:` twice is FS1042.
  `TextBlock Text: ref(Query).Text` binds to another element's property.

```flag
SplitView name: Nav DisplayMode: Overlay IsPaneOpen: vm.PaneOpen OpenPaneLength: 280 {
    Pane: StackPanel Padding: 12 {
        button "Save" name: SaveButton -> vm.SaveCommand;
    }
    TextBox name: Query PlaceholderText: "Search";
    TextBlock Text: ref(Query).Text Margin: 24;
}
TeachingTip Target: ref(SaveButton) Title: "Saved" Subtitle: "Your changes are stored." IsOpen: vm.ShowTip;
```

### Events

`on Event -> target;` attaches an event of the element's type (generic type or keyword root tag) to a command, a
navigation or `cancel`. Write it in the header or as a line inside the element's block; repeat it for several events.

```flag
Slider Value: vm.Volume Minimum: 0 Maximum: 100 on ValueChanged -> vm.VolumeChangedCommand;
TextBox Text: vm.Query on TextChanged -> vm.SearchCommand;
Border Padding: 12 on Tapped -> navigate(DetailView) {
    text "Open details";
}
card "Hover me" {
    on PointerEntered -> vm.Hover;
    on PointerExited -> vm.Unhover;
    text "A card with pointer handlers";
}
select "Owner" vm.Owner items: vm.Owners of Person on SelectionChanged -> vm.AssignCommand(item);
GridView ItemsSource: vm.Rows on ItemClick -> vm.OpenCommand;
list items: vm.Orders of Order on SelectionChanged -> vm.PreviewCommand(item) {
    text item.Number;
}
dialog "Edit" when(vm.IsEditing) {
    textbox "Name" vm.Name on KeyDown -> cancel;
    button "Close" -> cancel;
}
```

- The compiler generates a code-behind handler that runs the command (`if (CanExecute) Execute`), navigates, or
  cancels the dialog. `vm.X` targets become `[RelayCommand]`s with `On{X}Async()` exactly like `->` targets; the
  event arguments are not passed to the ViewModel.
- `(item)` passes the row inside a `table`/`list` row, the selected item on `SelectionChanged`, or the clicked item
  on `ItemClick`, of an element with `of T`. FS2007 otherwise.
- FS1034: the type has no such event (suggestions listed). FS1043: the same event twice, or an event the element's
  own `->` already handles (`Click` with `-> navigate(...)`, `ItemClick` on a `table`/`list` with `->`).
  FS2015: `on Event -> vm.X` where `vm.X` is also bound as a property.
- Anything that needs the event arguments or a control's API (`MediaPlayer.Play()`, `InkCanvas.InkPresenter`,
  starting a storyboard) is C# in your own page partial (`public sealed partial class XView { ... }`, e.g. in
  `OnNavigatedTo` or a handler you attach in code), using `name:` to reach the element.

### Resources, styles, visual states and value forms

Resources, styles, visual states and storyboards are generic elements in property elements; nothing here is a new
keyword. Each cross-reference is checked at its own line.

```flag
view DashboardView {
    bind vm: DashboardViewModel;

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
            VisualState name: Narrow {
                StateTriggers: { AdaptiveTrigger MinWindowWidth: 0; }
            }
        }
    }

    stat "Total" vm.Total Style: resource(CompactStat);
    SplitView name: Nav DisplayMode: Overlay OpenPaneLength: 280 {
        Pane: StackPanel Padding: 12 { button "Inbox" -> vm.InboxCommand; }
        Border name: Panel Padding: 16 BorderBrush: resource(Accent) BorderThickness: 1 { text "Ready"; }
    }
}
```

- **Placement.** View-level `Page.Resources: { }` is written before the page's root Grid; view-level
  `VisualStateManager.VisualStateGroups: { }` is the first child of the root Grid (where WinUI reads visual states).
  A generic element's own `Resources:` / `Grid.Resources:` works the same way.
- **`key: Name`** → `x:Key`. Valid only on a generic element directly inside a resources block; elsewhere, or on a
  keyword element, FS1044. Every entry needs `key:` except a `Style` with `TargetType:` (an implicit style);
  a repeated key in one block is FS1045.
- **Value forms** (any key): `resource(K)` → `{StaticResource K}` (K must be declared with `key: K` in the view or be
  a Flagstaff/App resource, else FS3010); `theme(K)` → `{ThemeResource K}` (not checked);
  `load: load(vm.X)` → `x:Load` bound to a `bool` (the element is created only while it is true);
  `uid: "K"` → `x:Uid`; `ref(Name)[.Member]` (above).
- **Brush and Color values**: named colors (`Red`, `CornflowerBlue`) and `#rgb`/`#argb`/`#rrggbb` hex (bare or
  quoted); anything else on a Brush/Color property is FS1032.
- **Styles**: `TargetType:` is a type name; each `Setter Property: P` must be a property of that type (FS1031) and
  `Value:` must fit P (FS1032). Setter values are written as XAML strings or bare enum members: `"True"`, `"240"`,
  `"12,8"`, `Inline`, `theme(...)`, `resource(...)`. `BasedOn: resource(X)` must follow `X`.
- **Visual-state setters**: `Setter Target: ref(N).M Value: ...` (N declared with `name:`, M a property of N's type,
  keyword root types included: `card name: Summary` → `Border`). A bare name under `Value:` is always a value
  (`Value: Inline`); an object value takes the element form (`Value: SolidColorBrush Color: Red;`).
  `VisualState name: Wide` → `x:Name="Wide"`.
- **Storyboards**: `Storyboard.TargetName: ref(N)` on the animation or on its `Storyboard`;
  `Storyboard.TargetProperty:` is a string: a simple property name of N's type matching the animation
  (`DoubleAnimation` → number, `ColorAnimation` → Color) or a parenthesised path (`"(UIElement.Opacity)"`).
  `Duration`/`BeginTime`/`KeyTime` are XAML time strings (`"0:0:0.3"`). Starting a storyboard is code
  (`((Storyboard)Resources["FadeIn"]).Begin();` in the page partial).
- Overlays (`toast`, `confirm`, `dialog`) written inside a property element (a `Flyout:`) are hoisted like any other
  overlay; inside a row template they are FS1020.

### What still needs `xaml { }`

- `ControlTemplate` internals and `{TemplateBinding}`; other markup extensions (`{RelativeSource}`, hand-written
  `{Binding ... ElementName=...}`, `x:Bind` with functions, converters or `FallbackValue`).
- `x:` directives without a prop (`x:Phase`, `x:DeferLoadStrategy`, `x:FieldModifier`).
- `DataTemplate`s that bind (use `list ... of T`, or `xaml { }` with `x:DataType`).
- Event handlers other than `on Event ->`; types outside `Microsoft.UI.Xaml.*`; `ResourceDictionary` elements with
  entries (theme dictionaries).

A `xaml { }` block that could be written as generic elements warns FS3011.
