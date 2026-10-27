# overlay.json

Hand-curated knowledge that WinUI metadata cannot express. `winui-manifest.json` is generated
(`dotnet run --project tools/ManifestGen`); this file is not. JSON has no comments, so the notes live here.
`ManifestTests.Overlay_NamesResolveAgainstManifest` fails when a name below no longer exists in the manifest.

Type keys are **short** WinUI type names (`TextBox`, not `Microsoft.UI.Xaml.Controls.TextBox`).

## `input`: two-way default (`ControlManifest.DefaultMode`)

Properties a user edits. A binding on one of these is emitted `Mode=TwoWay`; everything else is `OneWay`.
Lookup walks the base chain, so a property listed on a base applies to every subclass:

- `SelectedItem` / `SelectedIndex` / `SelectedValue` are declared on `Selector` (the metadata confirms it);
  `ListView`, `GridView`, `ComboBox`, `ListBox`, `FlipView` inherit the default. The `ListViewBase`, `TabView`
  and `Pivot` entries are redundant but harmless (`TabView` and `Pivot` declare their own `SelectedItem`).
- `Value` is declared on `RangeBase` (`Slider`, `ProgressBar`, `ScrollBar`); `NumberBox` and `RatingControl`
  declare their own.
- Empty lists (`FlyoutBase`, `RichEditBox`, `ContentDialog`, `MenuFlyoutItem`) document types that were
  considered and have no two-way property.

## `keywordFor`: keyword equivalents (`ControlManifest.KeywordFor`, FS4001)

The Flagstaff keyword that covers this exact WinUI type. Exact type only, not subclasses. Every keyword
must exist in `ElementSchema.All`. List a type only when the keyword covers what the generic element is used for:
`Grid` and `StackPanel` are deliberately absent (a generic `Grid` with definitions is the sanctioned way to place
children in explicit cells, and `StackPanel Orientation: Horizontal` has no keyword), so they get no FS4001.

## `valueShapes`: friendly value forms (`ControlManifest.ShapeFor`)

Keyed by the short name of the property type; for `DependencyObject` types the base chain is walked
(`LinearGradientBrush` resolves through `Brush`). Shapes: `thickness`, `cornerradius`, `gridlength`, `brush`,
`color`, `uri`, `string`, `icon`, `any` (object). Primitive types, enums and collections are decided by the
manifest itself; anything else is `Unknown`.

## `attachedLocality`: the locality rule (`ControlManifest.Locality`)

Whether an attached property may pass through (docs/universal-element.md, "The tractability principle"):

- `{ "local": true }`: correct from the element alone; passes through.
- `{ "local": false, "checkedBy": "grid" }`: must agree with the parent grid's definitions (FS1038).
- `{ "local": false, "checkedBy": "ref" }`: names another element; written as `ref(Name)` and resolved
  against the view's `name:` declarations (FS1039).
- `Owner.*` covers every attached property of that owner. An exact key wins over a wildcard.
- Absent → `Unknown`; the validator must not pass an unknown attached property through unchecked.

`Storyboard.TargetName` is `ref`-checked (it names an element). `Storyboard.TargetProperty` is local: its partner is
`Storyboard.TargetName` on the same animation (or its Storyboard), so `GenericValidator.ValidateResources` checks a
simple property name (`"Opacity"`) against the named element's type (FS1031) and the animation's value type (FS1032);
a parenthesised path (`"(UIElement.Opacity)"`, `"(Border.Background).(SolidColorBrush.Color)"`) passes as written. `VisualStateManager.VisualStateGroups` and
`FlyoutBase.AttachedFlyout` are local (they hold elements, not references).

## `referenceProperties`: how `ref(Name)` is emitted (`ControlManifest.ReferenceEmitFor`)

- `name`: the element's `x:Name` (`ElementName=`, `TargetName=`, `Target="{x:Bind Name}"`).
- `target-path`: a `TargetPropertyPath` (`Setter Target="Name.Property"`).

Lookup falls back through the base chain (`MenuFlyout.Target` → `FlyoutBase.Target`). `Storyboard.Target`
was dropped from the original proposal: WinUI exposes only `Storyboard.SetTarget(...)` as a method, not a
XAML-settable attached property.

## `extraAttached`: attached properties missing from metadata

`VisualStateManager.VisualStateGroups` has no `GetX`/`SetX` pair in WinUI 3 metadata (the XAML parser handles
it), but it is the standard way to write visual states, so the overlay declares it with its collection type.
