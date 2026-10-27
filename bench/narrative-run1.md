> **Run 1** (first run, on the pre-fix compiler at commit `f329224`, with the interim generic spec `conditions/generic-interim.md`). Kept for comparison; the current results are in [`results.md`](results.md).

## What the numbers say

**Run.** `z-ai/glm-5.3` via OpenRouter, 10 app specs × 3 conditions × 5 samples (seeds 7–11), temperature 0.2,
up to 5 fix rounds after the first answer, 412 model calls, $3.60 (plus $0.16 for the smoke run). The generic
condition used the interim spec (see `README.md`).

**The thesis is not confirmed by this run.** With a cheap model, none of the differences between the three
conditions is statistically distinguishable from noise:

- *First-answer builds* are rare everywhere: raw 9/50, keywords 14/50, generic 12/50 (Fisher p = 0.34 and 0.62
  against raw). The direction favours Flagstaff, the size does not support a claim.
- *Builds within five fix rounds* are near-universal: 48, 49 and 45 of 50. With error feedback, this model gets a
  WinUI app to build in every condition about 2.5 calls on average; the generic condition's five failures are all the
  sketch app (below).
- *UI-layer correctness* is where Flagstaff should win, and it does a little: 38/50 keyword-condition first answers
  have no XAML-compiler/Flagstaff/XML error, against 30/50 raw (p = 0.13) and 32/50 generic.
- *Where the errors are*: in the Flagstaff conditions almost all first-answer errors are C# errors in the
  hand-written half (3.9 and 2.3 per answer against 0.3–0.5 UI-layer errors). The dominant ones are contract errors
  against the generated code: re-declaring `[ObservableProperty]` fields the `.flag` file already generates (CS0102,
  CS0756), a page constructor that duplicates the generated one (CS0111), implementing `partial` methods the
  generator did not declare (CS0759), and using a property as `int` that inference typed as `string` (CS0029/CS0019,
  e.g. `stat "Open" vm.OpenCount;`). Flagstaff's own diagnostics (FS*) were rare: 5 in the keywords condition, 13
  in the generic one, over all rounds.
- *Closing-tag errors* in raw XAML, the failure mode the language was designed around, barely occur with this model:
  0.04 malformed XAML files per first answer (2 in 50). The raw condition's UI-layer errors are wrong members and
  types (`WMC0011`, `WMC1121`, `WMC1110`, undeclared prefixes), not nesting.
- *Size*: Flagstaff answers carry less than half the UI source (≈60 `.flag` lines vs ≈130 `.xaml` lines) and about
  a third less C#, but about as many output tokens, and the 14–18k-token language reference makes them 3–5× more
  expensive in input tokens (still under 3 cents per app).
- *Generic tier adoption*: with the generic documentation, the escape ratio fell from 24% to 5% of `.flag` lines and
  15% of elements were generic; success did not change measurably.

**Consumer apps.** The keyword condition did best on the consumer/creative apps (first answer 7/25 vs raw 3/25).
The generic condition failed the sketch app in all five samples, each time on C# in the page partial
(`Color` shadowed by a generated `vm.Color` property, `InkCanvas` used from C#). WinUI 3 on WindowsAppSDK 1.7 has
no `InkCanvas`, so the sketch spec asks for something the platform lacks; the raw condition passed 3/5 times by
writing its own `InkCanvas : Canvas` class, and LANG.md's escape-hatch section still lists `InkCanvas` as an example.

**What the run does not show.** It measures builds, not working apps: nothing was launched, so binding failures at
runtime are unmeasured, and the checklist is graded only by control presence (94–97% in all conditions). One cheap
model, five samples per pair: a real difference of 10–15 points in first-build success would need several hundred
runs per condition to show. The generic prompt is an interim document, and the Flagstaff prompts are much longer
than the raw one, which may hurt a small model as much as it helps.

**What it suggests for the compiler and docs.** The cheapest wins are on the C# side of the contract, which the
model gets wrong far more often than the `.flag` syntax: say in the generated `.g.cs` and in `flagstaff check` which
members exist (or make `flagstaff build` hint "remove your `[ObservableProperty] _x`; `x` is generated from
`Flags/…`" on CS0102/CS0756), document that pages already have both constructors, default `stat` values to a
numeric type when a comparison or a numeric literal is involved, and give FS1051 a hint (`xaml fill { }` was
written five times in four runs), and make FS1030 on `InkCanvas` say that WinUI 3 has no such control instead of
suggesting a third-party `use`.
