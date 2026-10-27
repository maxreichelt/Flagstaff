# The Universal Element: making the escape hatch a floor, not a hole

Status: proposal. Written against compiler state at commit `972a38e` (6.7k lines, 42 keywords).

## 1. The issue

Flagstaff today has two tiers and nothing between them:

1. **Keywords.** 42 of them. Each has a hand-written emitter, schema entry, type-inference rules, and docs.
   Everything inside a keyword is understood by the compiler: bindings become `x:Bind` with the right root and
   mode, `when()` becomes `Visibility`, `item.X` works inside row templates, and ViewModel members are generated.
2. **`xaml { }`.** Verbatim text. The lexer captures the braces as one `RawXaml` token
   ([Lexer.cs:206](../src/Flagstaff.Compiler/Lexer.cs#L206)), the parser wraps it in `RawXamlBody`, and the
   generator copies the lines out ([XamlGenerator.Escape.cs](../src/Flagstaff.Compiler/XamlGenerator.Escape.cs)).
   The compiler knows nothing about what is inside.

The escape hatch is a **leaf**, and that property is the source of every coverage gap that matters:

| Consequence | What it blocks | Example |
|---|---|---|
| **Cannot contain Flagstaff elements** | Any container WinUI control | `TabView`, `Expander`, `SplitView`, `Pivot`, `SwipeControl`, `ItemsRepeater` with a template. To use one, the entire subtree must be raw XAML, so every binding inside it is hand-written and unchecked. |
| **Cannot attach to Flagstaff elements** | Anything that is a *property* of an existing element | Flyouts and context menus (`Button.Flyout`, `UIElement.ContextFlyout`), keyboard accelerators, `AutomationProperties.*`, `Margin`, `Style`, `CornerRadius`, transitions, `VisualStateManager` groups. An escape block can only sit *beside* a keyword element, never *on* it. |
| **Invisible to inference and conditions** | ViewModel generation, `when()`, `item.X` | A binding inside `xaml { }` produces no `[ObservableProperty]`; the user hand-writes the member and must avoid the name in `.flag`. `when(vm.X)` cannot be put on the escaped control. Inside a `list` row the escape block does not know it is in a `DataTemplate`. |
| **Invisible to validation** | Loud errors | Mistakes inside the block surface only from the XAML compiler (or at runtime, silently, for bad binding paths), which is exactly the failure mode Flagstaff exists to remove. |

Two second-order effects follow:

- **Coverage is asymptotic.** Each new control costs a keyword (emitter + schema + inference + docs + tests). The
  marginal keyword covers less than the last one. "95% of business apps" is reachable this way; "everything
  including creative and consumer apps" is not, because the WinUI surface (hundreds of types, thousands of
  dependency properties) is far larger than any keyword list.
- **The two tiers have different grammars.** A model writing a `.flag` file must switch from Flagstaff syntax to
  XML syntax, and the escape region brings back every XAML problem at once: closing tags, property-element
  syntax, attached properties, binding root changes, namespace prefixes. The worst XAML moments are the ones
  pushed into the escape hatch.

### Why nesting is the real failure mode for LLMs

The earlier analysis in this project (LANG.md, "Design philosophy") identified *context-dependent* XAML as what
models get wrong. Looking at it structurally, XAML forces four kinds of nesting that each require the writer to
hold state across distance:

1. **Property-element syntax.** `<Button><Button.Flyout><MenuFlyout><MenuFlyoutItem .../></MenuFlyout></Button.Flyout></Button>`
   is four levels deep to express "a button with a menu". Every property that holds an object doubles the depth.
2. **Closing tags.** Every level must be closed with the exact same name, in order. This is where generation
   desynchronises: a model that drops or swaps one closing tag produces an error whose message points somewhere
   else.
3. **Attached properties on children that describe the parent.** `Grid.Row="2" Grid.Column="1"` on a child must
   agree with `RowDefinitions`/`ColumnDefinitions` declared on the parent, possibly 80 lines up.
4. **Scope changes.** Inside a `DataTemplate`, the binding root silently changes; `x:DataType` must be declared;
   `ViewModel.X` no longer resolves. Inside a `ControlTemplate`, `TemplateBinding` replaces `x:Bind`.

Flagstaff keywords already remove all four for the common controls. The universal element form
(`keyword "label" vm.X key: value when(...) { children }`) removes 1 and 2 by construction, the layout keywords
remove 3, and the compiler's `InItemTemplate` tracking removes 4. The question this document answers is: **can
the same four properties be guaranteed for every WinUI element, not just the 42 with keywords?** They can, and the
mechanism is cheaper than the keywords were.

### The tractability principle

The goal is not to make UI code *easier* for a model. Models write code well. The goal is to convert the parts of
XAML that are **intractable** for a model into parts that are tractable, and to leave everything else alone.

What makes a construct intractable is the combination of two properties:

- **Non-local correctness.** Whether a line is right depends on text somewhere else: a closing tag 60 lines
  down, a `RowDefinitions` block in the parent, an `x:Name` declared in another subtree, a resource key in
  another file, a ViewModel member in another project. A model generating left to right cannot check these, and
  a model editing cannot see them.
- **Silent failure.** The mistake compiles. A wrong `x:Bind` path, a missing `Mode=TwoWay`, a `Grid.Row` past the
  last definition, a `StaticResource` key with a typo: each produces a blank control, a one-way field, or an
  element drawn in the wrong cell, with no diagnostic at the line that caused it.

Ordinary code has neither property in its common forms: a C# statement is checked against declarations the
compiler resolves, and a mistake is an error on that line. That is why models are good at it. XAML has both
properties in its most common forms, and the escape hatch hands them straight back to the model.

The test for every construct in this proposal is therefore:

> Can an element be judged correct from its own line and its parent's keyword alone, and if it is wrong, does the
> compiler say so at that line?

Any construct that fails the test is not allowed to pass through unchanged. It gets a local form, or the compiler
resolves the non-local reference itself and reports the error at the `.flag` line. Density, brevity and
familiarity are welcome side effects; they are not the criterion.

| XAML construct | Why it is non-local | Flagstaff form that passes the test |
|---|---|---|
| Closing tags | Must match an opener arbitrarily far up | None exist: `{ }` with one closer |
| Property-element syntax `<Button.Flyout>` | Nesting depth, closer must match | `Flyout: MenuFlyout { }` |
| `Grid.Row="2"` on a child | Must agree with the parent's `RowDefinitions` | Layout keywords place children by position. On a generic `Grid`, `Grid.Row:` is validated against the parent's declared rows (FS1038), never emitted unchecked |
| `x:Bind ViewModel.X` inside a `DataTemplate` | Binding root changed by an ancestor | `vm.X` / `item.X`; the compiler tracks scope and reports FS2005 |
| `Mode=TwoWay` | Default depends on the property, omitted silently | Manifest decides per property |
| `x:Name` references: `ElementName=`, `Storyboard.TargetName`, `Setter Target="Nav.Prop"` | Reference a declaration elsewhere in the tree; typos are silent | `ref(Nav).DisplayMode` value form; the compiler resolves it against `name:` declarations in the view and reports FS1039 |
| `{StaticResource Key}` / `{ThemeResource Key}` | Key declared in another dictionary | `resource(Key)` / `theme(Key)`; FS3010 already checks keys |
| Converter registration | Declared in `App.xaml`, used by path | `format:` registers and references in one place |
| `ViewModel.X` member existence | Another file, another language | Generated from usage |
| `x:DataType` on a template | Required, silently wrong if missing | `of T` on the collection |
| `Click="OnFoo"` handler | Method in code-behind must exist with the right signature | `on Click -> vm.FooCommand`; handler generated |

Everything in the right-hand column is either local by construction or resolved by the compiler with a
diagnostic at the line. The rule for the manifest-driven pass-through is then simple: **an attribute passes
through when its correctness is local to the element; otherwise it is translated or checked.** `Margin: "0,8"`
passes through. `Grid.Row: 2` does not pass through unchecked, even though the lexer can carry it.

## 2. The proposal

Replace the opaque escape hatch with a **universal element**: any WinUI type, written in Flagstaff's own element
form, fully understood by the compiler. Keywords become the preferred, opinionated layer on top of a core that
can express all of XAML. `xaml { }` is kept, but its job shrinks to "things the compiler has not learned yet",
and that set is measured and driven toward zero.

The proposal has three parts. The first two are language changes; the third is the mechanism that makes them
cheap.

### 2a. Generic elements: PascalCase keyword = WinUI type

Flagstaff keywords are lowercase. WinUI types are PascalCase. A PascalCase element keyword therefore has no
ambiguity and needs no sigil:

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

- **PascalCase keys are XAML attributes**, passed through by name when their correctness is local.
  `Header: "Advanced"` → `Header="Advanced"`. Dotted keys are attached properties:
  `AutomationProperties.Name: "Save"` passes through; `Grid.Row: 1` is checked against the enclosing generic
  `Grid`'s declared rows (FS1038) and is an error outside one. See the tractability principle for the rule.
- **References to named elements use `ref(Name)`**, never a string: `ref(Nav).DisplayMode`,
  `Storyboard.Target: ref(Panel)`. The compiler resolves `Name` against `name:` declarations in the same view
  (FS1039 when missing) and emits the `ElementName`/`TargetName` form itself.
- **Lowercase keys keep their Flagstaff meaning.** `when()`, `enabled()`, `name:`, `width:`, `align:`, `tooltip:`,
  `fill`, `span:` all work on a generic element exactly as on a keyword element, because `CommonAttrs()` and the
  layout wrappers are applied to it like any other.
- **Binding values compile to `x:Bind`.** `Value: vm.CacheMb` → `Value="{x:Bind ViewModel.CacheMb, Mode=TwoWay}"`.
  The mode comes from the manifest (2c): properties the manifest marks as *input* (`Text`, `Value`, `IsChecked`,
  `IsOn`, `SelectedItem`, `Date`, `IsExpanded`, ...) default to `TwoWay`; everything else is `OneWay`. Inside a
  `list`/`table` row, `item.X` binds to the row, as it does today.
- **`-> vm.XCommand`** is allowed when the manifest says the type has a `Command` property (`ButtonBase`,
  `MenuFlyoutItem`, `AppBarButton`, ...) and generates the command exactly as `button` does.
- **Flagstaff elements and generic elements nest freely in both directions.** A `textbox` inside a `TabViewItem`,
  a `Slider` inside a `card`, a `list` whose row template contains an `Expander`.
- **A string child is content.** `Button { "Save"; }` → `<Button Content="Save"/>` via the manifest's
  content-property entry; a type without one gets a `TextBlock`, as `list` rows do today.

### 2b. Property elements and attribute pass-through on keyword elements

The same two rules apply to keyword elements, which is what makes attachments possible:

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
    }
}
```

- A **PascalCase key with a scalar value** on a keyword element becomes an attribute on the emitter's *root tag*
  (the tag `CommonAttrs()` is already appended to). Each keyword documents which tag that is; for `stat` it is the
  card `Border`, for `textbox` the `TextBox`.
- A **PascalCase key followed by an element** (`Flyout: MenuFlyout { ... }`) is property-element syntax:
  `<Button.Flyout><MenuFlyout>...</MenuFlyout></Button.Flyout>`. A key followed by a bare block
  (`VisualStateManager.VisualStateGroups: { VisualStateGroup { ... } }`) is a collection property element.
- Resources become expressible the same way: `Page.Resources: { ... }` at view level. Styles, storyboards and
  visual states are all "an object with properties that hold objects", which is exactly what the form expresses.

This is the step that collapses XAML's nesting. Property-element syntax disappears into `Key: Type { }`; closing
tags disappear entirely; the depth of the `.flag` tree equals the *logical* depth of the UI, not the XAML depth.

### 2c. The control manifest: generated knowledge instead of hand-written keywords

Generic elements only pay off if the compiler can **validate and type** them. Hand-maintaining that knowledge
would recreate the keyword cost. Instead, generate it:

- A build-time tool (`tools/ManifestGen`) reads `Microsoft.WinUI.winmd` from the WinAppSDK package with
  `System.Reflection.Metadata` and emits `winui-manifest.json`: every type deriving from `UIElement` or
  `FlyoutBase`, its base chain, its dependency properties with CLR types, attached properties, the
  `ContentProperty`, whether it derives from `Control` (so `enabled()` is valid), and known events.
- A small **hand-curated overlay** adds what metadata cannot say: which properties are input (two-way default),
  which generic types have a keyword equivalent (`TextBox → textbox`), and friendly value shapes
  (`Thickness` accepts `"0,8"` or a number).
- `ControlManifest` is loaded once, like `ElementSchema`, and consulted by the parser (is `Expander` a type?),
  the validator (does `Expander` have `IsExpanded`? is `"yes"` a valid `bool`?), type inference (`Slider.Value`
  is `double`), and the generator (content property, binding mode, `Control`-ness).
- Third-party libraries: `use "LiveChartsCore.SkiaSharpView.WinUI" as lvc;` at the top of a file declares an
  `xmlns`, and `lvc.CartesianChart Series: vm.Series` is a generic element in that namespace. If the assembly is
  available, `ManifestGen` reads it too; if not, the element is passed through unvalidated with a warning (FS4002).

The manifest is what turns "support all of WinUI" from a multi-year keyword backlog into one tool and one JSON
file, and it regenerates when WinAppSDK changes (which also answers how to keep up with WinUI releases routinely).

### 2d. What `xaml { }` is left for

After 2a–2c, raw XAML is needed only for:

- markup extensions beyond `x:Bind`/`StaticResource` (`{ThemeResource}`, `{TemplateBinding}`, `{RelativeSource}`),
  until those get value forms;
- `x:` directives other than `x:Name`/`x:DataType` (`x:Load`, `x:Uid`, `x:Phase`), until those get props;
- anything the manifest does not know.

Each of those is a small, enumerable gap with an obvious Flagstaff form, which is the point: the remaining escape
usage becomes a **list of compiler work items**, not an open-ended hole. The compiler should report the
**escape ratio** (lines inside `xaml { }` / total `.flag` lines) per project so the trend is visible.

## 3. Why this approach wins

### Against "keep adding keywords"

| | Keywords only | Universal element + keywords |
|---|---|---|
| Cost per new control | Emitter + schema + inference + docs + tests, every time | Zero (manifest) |
| Coverage after the work | 42 → maybe 80 controls, business apps only | All of WinUI plus third-party, any app category |
| Keeps up with WinAppSDK | Each change is hand-ported | Regenerate the manifest; keyword emitters unchanged |
| Opinionated defaults | Yes | Yes, through keywords, and a lint nudges toward them (FS4001: "`TextBox` has a keyword: use `textbox`") |
| Round-trip from XAML | Not possible (escape blocks only grow) | Mechanical: every XAML element has a generic form; a "keyword folding" pass recovers keywords where patterns match |

Keywords are not displaced; they become **sugar over a complete core**. That is the shape of every DSL that has
aged well: a small total core, with conveniences layered on it, rather than a convenience list with a trapdoor.

### For LLM authoring specifically

The point is not that `.flag` is shorter or friendlier. It is that every construct in it passes the tractability
test, and the escape hatch today is the one place that test is waived. Closing this gap is what the generic
element does; the items below are the specific consequences.

1. **One grammar, no mode switch.** Everything from a toolbar to a storyboard is `Name key: value { children }`.
   The model never leaves Flagstaff syntax, so the escape-hatch tax (XML, closing tags, prefixes) is gone.
2. **Nesting depth drops by roughly half and closing tags drop to zero.** Property-element syntax is the main
   depth multiplier in XAML, and it vanishes. Closing-tag mismatch is the most common structural desync in
   generated XML and it cannot happen in a brace language with a single closer.
3. **Context is always carried by the compiler.** `vm.X` and `item.X` mean the same thing inside an `Expander`,
   a `TabViewItem` or a `MenuFlyout`; `when()` works on anything; the `DataTemplate` scope rule is enforced by
   `ConditionCompiler.InItemTemplate` for generic elements too.
4. **Every mistake is loud and local.** Unknown type → FS with a suggestion from the manifest; unknown property →
   FS with the type's property list; wrong value shape → FS with the CLR type. All reported at the `.flag` line,
   before any XAML compiler runs. Today a mistake inside `xaml { }` is reported by `XamlCompiler.exe`, which
   BUILD.md documents as swallowing errors under `dotnet build`.
5. **WinUI property names are in pretraining; XAML structure is the problem.** Models know that `Expander` has
   `IsExpanded` and `TabViewItem` has `Header`. Generic elements let them use that knowledge while the compiler
   handles the structural part they get wrong. This is the opposite split from `xaml { }`, which asks the model to
   do the structure by hand.

### What it does not solve (be honest in the benchmark)

- **Semantic complexity stays.** A `Storyboard` with keyframes, or a `VisualStateManager` with adaptive triggers,
  is flatter in Flagstaff but no simpler to think about. Flagstaff makes the *syntax* tractable, not the concepts.
  Creative/consumer apps will exercise exactly these areas, so the gain there is real but smaller than for forms.
- **Event handlers** (`Click`, `SelectionChanged`, `PointerPressed`) need code-behind, which is imperative. Phase 3
  proposes `on Click -> vm.XCommand` for the common case; the rest remains hand-written page partials.
- **Loss of opinion.** A project written entirely in generic elements is "XAML in braces". The FS4001 lint and
  the docs' rule ("always prefer a keyword") are the mitigation; the escape ratio and a "generic ratio" should
  both be tracked.
- **It must be measured.** The claim "LLMs produce correct apps more often in Flagstaff than in XAML" is the
  product's thesis and is still unproven. Phase 5 is the benchmark that settles it, and it should include
  consumer-style apps that the keyword tier cannot build today.

## 4. Implementation

Mapped to the current compiler. Estimates are relative to the existing 6.7k-line compiler.

### Phase 0: the manifest (prerequisite, ~1 week)

- `tools/ManifestGen`: reads `Microsoft.WinUI.winmd` via `System.Reflection.Metadata`; emits
  `src/Flagstaff.Compiler/Manifest/winui-manifest.json` (types, base chain, DPs with CLR types, attached DPs,
  `ContentProperty`, `Control`-derived, events). Committed; regenerated per WinAppSDK bump.
- `Manifest/overlay.json`, hand-curated: input (two-way) properties, keyword equivalents, value-shape hints.
- `ControlManifest.cs`: load, lookup by type (with namespace), property lookup walking the base chain,
  Levenshtein suggestion helper (reuse whatever `SemanticValidator.UnknownKeyword` uses).
- Roughly 500 lines plus the JSON.

### Phase 1: generic elements (~2 weeks)

- **Lexer** ([Lexer.cs](../src/Flagstaff.Compiler/Lexer.cs)): dotted identifiers as prop keys (`Grid.Row`,
  `AutomationProperties.Name`); `use "<assembly>" as <prefix>;` declaration. Remove the `xaml {` special-case
  only in Phase 4.
- **AST** ([Ast.cs](../src/Flagstaff.Compiler/Ast.cs)): `Element` gains `bool IsGeneric` (keyword is a type name)
  and `string? NamespacePrefix`. `PropValue` gains `ElementValue(Element)` and `ElementsValue(List<Element>)` for
  property elements. `FlagFile` gains `List<UseDecl>`.
- **Parser** ([Parser.cs:186](../src/Flagstaff.Compiler/Parser.cs#L186)): `IsElementKeyword` also accepts a
  PascalCase identifier (or `prefix.PascalCase`) that the manifest knows; parse `Key: Type ... { }` and
  `Key: { ... }` as `ElementValue`/`ElementsValue`. The "missing `;`" heuristic at
  [Parser.cs:254](../src/Flagstaff.Compiler/Parser.cs#L254) must treat a PascalCase identifier followed by `:` as a
  key, not a new element.
- **Schema/validation** ([SemanticValidator.cs](../src/Flagstaff.Compiler/SemanticValidator.cs),
  [LayoutValidator.cs](../src/Flagstaff.Compiler/LayoutValidator.cs)): for generic elements, PascalCase props are
  checked against the manifest (new FS1030 unknown type, FS1031 unknown property, FS1032 value shape); lowercase
  props go through `ElementSchema.Universal` as today. `CommonAttrsKeywords` and `ControlKeywords`
  ([XamlGenerator.Common.cs:16](../src/Flagstaff.Compiler/XamlGenerator.Common.cs#L16)) become predicates that
  return true for generic elements (the second only when the manifest says `Control`).
- **Generator**: new `XamlGenerator.Generic.cs` with `EmitGeneric(el)`: open tag = type name (+ `xmlns` prefix);
  attributes = pass-through props (bindings via `BindExpr` with manifest-chosen mode, `item.` via existing
  `Bind`) + `CommonAttrs(el)`; then property-element children (`<Type.Key>`), then content children through
  `EmitElement` (so keywords nest inside), then text children through the content property. Dispatch: the
  `default:` arm of `EmitElement` ([XamlGenerator.cs:71](../src/Flagstaff.Compiler/XamlGenerator.cs#L71)) routes
  `IsGeneric` elements here. Grid placement inside `row`/`split`/`cards` is already applied by the container's
  wrapper, so nothing to do. `use` declarations add `xmlns:prefix` in `WriteXamlNamespaces()`.
- **Type inference** ([TypeInference.cs](../src/Flagstaff.Compiler/TypeInference.cs)): in `InferFromProp`, a
  binding value on a PascalCase key registers the member with the manifest's CLR type at `Explicit` confidence
  (unknown → `Default`/`string`). `-> vm.XCommand` on a generic element follows the existing command path when
  the manifest reports a `Command` property; otherwise a new FS2014.
- **ViewModel generation**: no change; it consumes `InferredMember`s.
- **Locality checks** (new, in the validator): a pass-through allowlist decision per attached property in the
  overlay (`local: true` for `AutomationProperties.*`, `ToolTipService.*`, `Canvas.Left/Top`, `ScrollViewer.*`;
  `local: false` for `Grid.Row/Column/RowSpan/ColumnSpan` and anything else that references the parent).
  Non-local ones are validated against the enclosing generic `Grid`'s `RowDefinitions:`/`ColumnDefinitions:`
  (FS1038, reusing the overflow logic behind FS3004 but at the `.flag` line). `ref(Name)` values are resolved
  against the view's `name:` declarations (FS1039) and emitted as `ElementName=`/`TargetName=`/`Target=` per the
  manifest property type. String-valued targets for these properties are rejected (FS1040) so the unchecked form
  cannot be written.
- **Linter** ([XamlLinter.cs](../src/Flagstaff.Compiler/XamlLinter.cs)): unchanged (it still runs on output);
  add FS4001 (warning) "generic `X` has keyword `y`" in the validator, driven by the overlay.
- **Tests**: golden files for Expander/TabView/Slider/MenuFlyout; nesting both ways; `item.X` inside a generic
  element inside a `list`; every new diagnostic; `LangDocTests` coverage of the new LANG.md section.
- **Docs**: LANG.md "Generic elements" section (replacing most of "XAML Escape"); `flagstaff spec --section generic`.

### Phase 2: pass-through on keyword elements (~1 week)

- `CommonAttrs()` appends PascalCase scalar props to the root tag, binding-aware. This alone unlocks margins,
  accessibility names, styles and most "small design-tool edits".
- Property elements on keyword elements need the emitter to accept body content after the root tag. Introduce a
  helper (`EmitRoot(el, tag, attrs, body)`) and migrate emitters one by one; the self-closing ones (`textbox`,
  `button`, `toggle`, ...) first, since flyouts and accelerators land on those. Emitters not yet migrated report
  FS1002 for property elements, so nothing is silently dropped.
- Document per keyword which emitted tag is the root.

### Phase 3: events and remaining XAML forms (~1–2 weeks)

- `on Click -> vm.XCommand;` and `on SelectionChanged -> vm.XCommand;` as a clause on any element: the code-behind
  generator ([CodeBehindGenerator.cs](../src/Flagstaff.Compiler/CodeBehindGenerator.cs)) emits the handler that
  invokes the command, as `navigate()` buttons do today. Manifest validates the event name.
- Value forms for `{ThemeResource Key}` (`theme(Key)`), `{StaticResource Key}` (`resource(Key)`), `x:Uid`
  (`uid:`), `x:Load` (`load(vm.X)`).
- `Page.Resources: { ... }` and `VisualStateManager.VisualStateGroups: { ... }` work from Phase 1 as property
  elements; verify with goldens. `Setter Target: ref(Nav).DisplayMode` uses the `ref()` form from Phase 1, so a
  visual state cannot name an element that does not exist.

### Phase 4: shrink the hatch (ongoing)

- `flagstaff compile` reports the escape ratio and lists each `xaml { }` block with the reason the generic form
  could not be used (unknown type / markup extension / directive). That list is the backlog.
- Keyword candidates come from data: a generic pattern that recurs across projects (e.g. `Expander` with a
  header and a stack) is a keyword candidate, and keywords added this way are justified by usage, not by guess.
- Optional, later: express the stdlib keywords themselves as expansions into generic elements (a keyword = a
  template). This unifies the two tiers and makes a keyword cost a template instead of an emitter. It is a
  rewrite of `XamlGenerator.*.cs` and should only be attempted once Phase 1 has stabilised.

### Phase 5: the benchmark (in parallel with Phase 2)

- Ten app specs: five business (forms, tables, dashboards), five consumer/creative (media player with
  `MediaPlayerElement` and transport flyouts; notes app with `TabView` + `Expander` + `MenuBar`; sketch app with
  `InkCanvas` and a toolbar; photo browser with `GridView` + `FlipView`; settings app with visual states).
- Same model, same prompt discipline, three conditions: raw WinUI XAML/C#, Flagstaff keywords only (today),
  Flagstaff with generic elements.
- Metrics: first-build success; binding failures in debug output on first run; fix iterations to a working app;
  tokens generated; depth and closing-tag errors in the raw-XAML condition.
- This is the number that decides whether the thesis holds. Publish it in the README either way.

## 5. Decisions to make before Phase 1

1. **Generic element syntax.** Recommended: bare PascalCase (`Expander { }`). Alternative: a sigil (`@Expander`),
   which is more visible but is one more token for the model to get wrong. The case rule is unambiguous because
   every Flagstaff keyword is lowercase (the one camelCase keyword, `filePicker`, starts lowercase).
2. **Namespace prefixes.** `lvc.CartesianChart` (dot) is recommended because `:` is the prop separator. It
   is distinguishable from an attached-property key by position (element position vs key position).
3. **Two-way default.** Manifest overlay marks input properties as two-way; everything else one-way. An explicit
   `mode:` escape is deferred until a case needs it.
4. **Universal lowercase props on generic elements.** Recommended: all of them apply (`when`, `enabled`, `name`,
   `width`, `align`, `tooltip`, `fill`, `span`), so a generic element is a first-class layout child.
5. **Whether `xaml { }` stays at all.** Recommended: yes, through Phase 3, then decide based on the escape ratio.
6. **The locality rule is not optional.** Pass-through of non-local attributes (`Grid.Row`, string element
   targets) would be the quickest way to ship Phase 1 and would reintroduce the exact failure mode the project
   exists to remove. If a construct cannot be made local or compiler-checked in a phase, it stays in `xaml { }`
   for that phase rather than passing through unchecked.

## 6. Summary

Models write code well because code is local and loud. XAML is non-local and silent in its most common forms, and
the escape hatch is the one place Flagstaff hands those forms back to the model. Making the element form
universal, backed by a manifest generated from WinUI's own metadata and governed by the locality rule, covers the
whole framework for a fixed cost, keeps every non-local construct out of `.flag` files or under compiler check,
and turns the remaining escape usage into a measurable backlog. Keywords stay as the opinionated layer the product
is built on; they stop being the only way to say anything.
