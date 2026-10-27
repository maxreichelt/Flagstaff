## What the numbers say

**Run 3.** `z-ai/glm-5.3` via OpenRouter, 10 app specs × 3 conditions × 5 samples (seeds 7–11), temperature 0.2,
up to 5 fix rounds, 32k output tokens with an 8k reasoning budget: the same settings as runs 1 and 2
([`results-run2.md`](results-run2.md), [`results-run1.md`](results-run1.md)). 294 model calls, $2.83 (plus $0.15 for
a 6-pair smoke run). What changed since run 2 is the compiler (the generator skips a member the hand-written C#
already declares and warns FS2016; an implemented `partial On{X}Async` no command declares gets a declaration,
FS2017; a hand-written page constructor replaces the generated one; `partial void OnViewCreated()`;
`Generated/GlobalUsings.g.cs`; hints for CS8799, CS0111 on generated page methods, `xaml { }` handlers and
`DateTimeOffset?`) **and** the prompts, regenerated from the LANG.md that documents it (keywords about 19.0k tokens,
was 18.8k; generic 23.8k, was 23.5k; raw 1.0k, unchanged). The two cannot be separated. The only harness change is
that each Flagstaff round now records its build warnings, so FS2016/FS2017 can be counted; the success rule is
unchanged (zero errors; neither project template sets `TreatWarningsAsErrors`).

**The thesis is confirmed on first-answer builds in this run, not on builds within the fix rounds:**

- *First-answer builds*: raw 14/50, keywords 26/50, generic 27/50 (Fisher p = 0.024 and 0.014 against raw; both
  stay under 0.05 with a Bonferroni correction for the two comparisons, keywords only just). Run 2 had 13, 16, 16. Within five fix rounds every condition is at 49–50 of 50 (the one
  failure is a keywords sketch run that ended on one C# error), so there is nothing left to separate there.
- *Model calls to a build* fell to a mean of 1.69 (keywords) and 1.78 (generic) against 2.32 raw (Mann–Whitney
  p = 0.002 and 0.01); the median Flagstaff run builds on the first answer (median errors after the first answer:
  0, raw 1).
- *UI-layer correctness* holds and is now significant for both Flagstaff conditions: 44/50 (keywords) and 42/50
  (generic) first answers have no XAML-compiler/Flagstaff/XML error, against 28/50 raw (p < 0.001 and p = 0.004).
- *Run 3 vs run 2, per condition*: first-answer builds keywords 16 → 26 (p = 0.07), generic 16 → 27 (p = 0.04),
  raw 13 → 14 (p = 1.00); pooled over both Flagstaff conditions 32 → 53 of 100 (p = 0.004). Calls to a build fell
  in both Flagstaff conditions (p = 0.01 and 0.03). Raw stayed where it was, which makes provider drift a less
  likely explanation than between runs 1 and 2 (raw 9 → 13 with nothing changed), but does not rule it out.

**What FS2016/FS2017 did.** 10 of 100 Flagstaff first answers declared a member the generator also generates
(keywords 5: 3 with FS2016, 2 with FS2017; generic 5, all FS2016; 8 + 12 FS2016 warnings on first answers). In
run 2, 20 first answers failed with CS0102/CS0756 for that reason. **3 first answers built only because of the
skip** (all generic: an item property `Invoice.Number`, `Ticket.ClosedOn`, and a `PlayAsync` method with the name
of the generated command method); the other 7 failed on something else (a hand-written item type whose property
types disagree with what the `.flag` infers, CS0029/CS0019; `DateTimeOffset?` from a date picker; a missing
service class; and both FS2017 cases, see below). So the direct rescue is 3 apps. The larger change is that the
model redeclared generated members about half as often (10 first answers instead of 20) and that contract-only
failures nearly vanished: 13 keywords and 12 generic first answers failed only on CS0102/CS0756/CS0111/CS8799/CS0759
in run 2, 0 and 2 in run 3 (1 and 3 when the new near-misses CS8795/CS8817 are added; run 2's narrative counted 15
and 12 with a looser rule).
Which part of that comes from the new prompt text ("the generator skips its own; prefer deleting yours",
`OnViewCreated`), from the global usings, or from sampling, this design cannot say.

**Time and tokens.** Medians, successful runs only:

- *Wall time to a building app*: raw 53 s, keywords 50 s, generic 54 s (Mann–Whitney p = 0.34 and 0.49); means
  61 / 78 / 68 s, the Flagstaff tail again driven by long reasoning on a few calls. No significant change from
  run 2 in any condition (p = 0.74, 0.39, 0.84). Build time is 13–16 s median per run in every condition.
- *Output tokens to a building app*: about the same in total (6.9k raw, 6.5k keywords, 6.5k generic; p = 0.72 and
  1.00). The visible answer is less than half as long in Flagstaff (2.2k and 2.2k vs 5.2k, p < 0.001); the hidden
  reasoning is longer (4.6k and 4.4k vs 1.0k). One keywords call spent all 32k tokens reasoning and returned no
  files (sketch).
- *Input tokens*: with the system prompt counted once, 19k (keywords) and 24k (generic) vs 7k raw per app; as
  billed, medians 19k / 24k / 8k (down from 40k / 50k / 7k in run 2, because most Flagstaff runs now need one call).
- *Cost per building app*: $0.015 raw, $0.022 keywords, $0.020 generic (run 2: $0.016, $0.026, $0.023). Flagstaff
  still costs 1.3–1.5× as much per app with this model, all of it the language reference in the prompt and the
  reasoning.
- *Code size*: 59 and 58 `.flag` lines against 135 `.xaml` lines per app, and 148 / 137 C# lines against 219.

**Runs 1, 2 and 3** (means unless noted; run 1's per-call data was lost, so its medians are not available):

| | raw r1 | raw r2 | raw r3 | kw r1 | kw r2 | kw r3 | gen r1 | gen r2 | gen r3 |
|---|---|---|---|---|---|---|---|---|---|
| Builds on the first answer | 9/50 | 13/50 | 14/50 | 14/50 | 16/50 | 26/50 | 12/50 | 16/50 | 27/50 |
| Builds within 5 fix rounds | 48/50 | 50/50 | 50/50 | 49/50 | 50/50 | 49/50 | 45/50 | 49/50 | 50/50 |
| First answer free of UI-layer errors | 30/50 | 30/50 | 28/50 | 38/50 | 39/50 | 44/50 | 32/50 | 41/50 | 42/50 |
| Model calls to a build (successes) | 2.58 | 2.40 | 2.32 | 2.49 | 2.28 | 1.69 | 2.49 | 2.16 | 1.78 |
| C# errors after the first answer | 1.5 | 0.6 | 0.6 | 3.9 | 1.2 | 0.7 | 2.3 | 7.8 | 0.9 |
| Wall time to a build, median s | – | 49 | 53 | – | 55 | 50 | – | 53 | 54 |
| Wall time per app, s (all runs) | 80 | 60 | 61 | 104 | 87 | 79 | 99 | 72 | 68 |
| Output tokens to a build, median visible / reasoning | – | 5.7k / 0.3k | 5.2k / 1.0k | – | 2.7k / 5.2k | 2.2k / 4.6k | – | 2.4k / 4.2k | 2.2k / 4.4k |
| Output tokens, all rounds | 8635 | 8098 | 8440 | 9620 | 12003 | 11231 | 9808 | 8967 | 9385 |
| Input tokens per app, system prompt once, median | – | 5.7k | 7.0k | – | 21.5k | 19.4k | – | 26.3k | 24.1k |
| Input tokens, all rounds (billed) | 12765 | 10655 | 9637 | 43834 | 47849 | 36642 | 61669 | 57276 | 44958 |
| Cost per building app | $0.017 | $0.016 | $0.015 | $0.027 | $0.026 | $0.022 | $0.031 | $0.023 | $0.020 |
| UI source lines (.xaml / .flag) | 131 | 139 | 135 | 62 | 54 | 59 | 58 | 57 | 58 |
| C# lines | 216 | 219 | 219 | 148 | 153 | 148 | 149 | 151 | 137 |
| Total cost | $0.80 | $0.82 | $0.74 | $1.34 | $1.28 | $1.08 | $1.42 | $1.14 | $1.02 |

**What is and is not significant.** Significant in run 3: first-answer builds against raw (p = 0.024 keywords,
0.014 generic), calls to a build against raw (p = 0.002, 0.01), UI-layer-error-free first answers against raw
(p < 0.001, 0.004), shorter visible answers (p < 0.001), and the run-2 → run-3 gain for generic (p = 0.04) and for
both Flagstaff conditions pooled (p = 0.004). Not significant: builds within the fix rounds (ceiling), wall time
and total output tokens against raw, the keywords run-2 → run-3 gain on its own (p = 0.07), and any time or token
change between runs. **Confounds**: the compiler and the prompts changed together; this is the third run of the same
design, and the first in which first-answer success separates, so one more run (or a second model) is the honest
way to call it robust; providers do not guarantee determinism, and raw moved by four apps between runs 1 and 2
with nothing changed. The p-values are not corrected for the number of metrics reported.

**What the run does not show.** Builds, not working apps: nothing was launched, runtime binding failures are not
measured, and the checklist is graded only by control presence (93% raw, 97% Flagstaff; the sketch app is 53–67%
everywhere because WinUI 3 has no `InkCanvas`). One cheap model, five samples per pair.

**What the model still trips over** (all rounds, first answers in parentheses; see Error codes below):

- **Item and ViewModel types the model declares differently from what the `.flag` infers**: `CS0029` 10 (9)
  keywords, 16 (14) generic; `CS0019` 9 (6) keywords; `CS1662` 5 (5) generic, a consequence of `CS0029` in a
  lambda. All `CS0029`/`CS0019` occurrences were hinted. The timesheet app is the generic condition's worst
  (0/5 first answers): `vm.Billable` bound to a checkbox is `bool` while the model's code and the item column
  treat it as `string`, and the date picker's `DateTimeOffset?` is used as a `DateTime` (`CS1061` 12, hinted).
  When FS2016 skips an item property the model declared with another type (`Product.Quantity` as `int` where the
  `.flag` infers `double`), the warning says so, but the build then fails where the generated code or the model's
  own code assumed the other type. Next step for the compiler: when the hand-written declaration exists, type
  the generated bindings from it instead of only warning (or make that FS2016 variant an error with the fix).
- **Signature near-misses on `On{X}Async`**: FS2017 fired twice for a hand-written `OnDeleteAsync(Contact)` /
  `OnSearchAsync()` where the `.flag` command has another parameter list; the build then fails with `CS8795`
  (the real delegate is unimplemented) or on another error. `CS8817` (`private partial void OnLowOnlyAsync()`
  against the generated `Task`) and `CS8799` on a toolkit hook (`private partial void OnVolumeChanged(double)`,
  where the toolkit declares it without `private`) were not hinted. Next step: FS2017 should name the command it
  probably meant and its generated signature; add hints for `CS8817` and for `CS8799` on `On{Prop}Changed` hooks.
- **The sketch app**: `CS0246`/`CS0234` on `InkCanvas`, `InkPresenter`, `Microsoft.UI.Xaml.Controls.Canvas`,
  `Polyline`, `Windows.UI.Input.Inking` in `Views.cs` (about 30 keywords errors, all unhinted), and `CS0115` on
  pointer overrides in custom controls. `FS1030` names the replacement in `.flag`, but nothing does in C#. Next
  step: add `Microsoft.UI.Xaml.Shapes` (and `Windows.Graphics.Imaging`, `CS0103` on `BitmapEncoder`) to
  `GlobalUsings.g.cs`, and give `CS0246`/`CS0234` on the UWP inking types the same hint as FS1030.
- **Escape hatch use went up in keywords** (15% of `.flag` lines in `xaml { }`, was 8%; `FS3011` 28 in the final
  sources); the generic condition is at 8%.
- **Generated page members**: `CS0111` twice, both hinted (`OnViewLoaded`, and a second `OnViewCreated`
  implementation).
