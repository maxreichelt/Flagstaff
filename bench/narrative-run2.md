> **Run 2** (second run, on the 0.3.0-alpha.1 compiler at commit `a409cd2`, with prompts generated from that commit's LANG.md). Kept for comparison; the current results are in [`results.md`](results.md).

## What the numbers say

**Run 2.** `z-ai/glm-5.3` via OpenRouter, 10 app specs × 3 conditions × 5 samples (seeds 7–11), temperature 0.2,
up to 5 fix rounds, 32k output tokens with an 8k reasoning budget: the same settings as run 1
([`results-run1.md`](results-run1.md)). 346 model calls, $3.24 (plus $0.13 for a 6-pair smoke run). What changed
since run 1 is the compiler (0.3.0-alpha.1: contract hints on `flagstaff build` errors, a contract header in every
generated file, better FS1030/FS1031/FS1051 hints, FontIcon accepted) **and** the prompts, regenerated from the new
LANG.md: the generic condition now gets the plain `flagstaff spec` (with its Generic Elements, Pass-Through, Events
and Resources sections and the new "The hand-written half" subsection) instead of the interim document, and the
keywords prompt grew too (about 18.8k tokens, was about 14.7k; generic about 23.5k, was about 19.7k; raw 1.0k).
The two changes cannot be separated in this run. The only harness change is that each round now records model and
build seconds and each run the prompt sizes.

**The thesis is still not confirmed on build success.** All three conditions moved up, and the gaps between them
stayed small:

- *First-answer builds*: raw 13/50, keywords 16/50, generic 16/50 (Fisher p = 0.66 for both against raw). Within
  five fix rounds: 50, 50 and 49 of 50 (the one failure is a generic timesheet run that still had 3 C# errors).
- *UI-layer correctness* is now the one significant difference: 41/50 generic first answers have no
  XAML-compiler/Flagstaff/XML error, against 30/50 raw (p = 0.03); keywords 39/50 (p = 0.08). Flagstaff's own
  diagnostics are rare (9 FS and 5–6 `WMC` errors over every round in each Flagstaff condition, against 60 `WMC` errors raw).
- *The C# contract is still what stops Flagstaff first answers.* 15 of the 34 failing keyword first answers and
  12 of the 34 failing generic ones failed **only** on contract errors against the generated code (redeclared
  `[ObservableProperty]` members, CS0102/CS0756/CS0111; `partial` methods with the wrong accessibility, CS8799;
  undeclared ones, CS0759). The hints work as feedback: the contract errors were gone after one fix round in 14 of
  16 (keywords) and 12 of 15 (generic) runs, and `CS0102`/`CS0756` were hinted on every occurrence. They do not
  prevent the mistake in the first place. If those answers had built, first-answer success would be about 31/50
  and 28/50 against 13/50 raw (p < 0.01): that is the size of the remaining opportunity, not a measured result.
- *Sketch app*: the generic condition went from 0/5 to 5/5 (all in 3 calls); FS1030 on `InkCanvas` now names the
  WinUI 3 replacement and was hinted all five times it fired.

**Time and tokens.** Medians, successful runs only:

- *Wall time to a building app* has close medians, raw 49 s, keywords 55 s, generic 53 s, but the keywords
  condition is slower as a distribution (Mann–Whitney p = 0.04; generic p = 0.14). The means (60 / 87 / 72 s)
  show the longer Flagstaff tail, driven by long reasoning on the
  consumer apps (notes, media player, settings). Build time is identical (16–17 s median per run); the difference
  is all model latency.
- *Output tokens to a building app* are about the same in total (6.6k raw, 6.9k keywords, 7.1k generic; p = 0.38
  and 0.79), but their make-up is opposite. The visible answer (code and prose) is less than half as long in
  Flagstaff (median 2.7k and 2.4k vs 5.7k tokens, p < 0.001), and the hidden reasoning is far longer (median
  5.2k and 4.2k vs 0.3k). GLM 5.3 reasons through the language reference before it writes; the reasoning budget
  of 8k is not a hard cap with this provider (one keywords call on the notes app spent all 32k output tokens reasoning; 2 keywords answers were cut off and 3
  rounds returned no files).
- *Input tokens* are 4–5× higher for Flagstaff because the spec is the system prompt and is resent with every
  call: median 40k (keywords) and 50k (generic) vs 6.7k (raw) per app. Counting the system prompt once, it is
  21k and 26k vs 5.7k, so most of the difference is the reference itself, not longer conversations.
- *Cost per building app*: $0.016 raw, $0.026 keywords, $0.023 generic. Flagstaff costs 1.5–1.7× as much per app
  with this model, all of it input tokens and reasoning.

**Run 1 vs run 2** (means unless noted; run 1's per-call data was lost, so its medians and time split are not
available):

| | raw r1 | raw r2 | keywords r1 | keywords r2 | generic r1 | generic r2 |
|---|---|---|---|---|---|---|
| Builds on the first answer | 9/50 | 13/50 | 14/50 | 16/50 | 12/50 | 16/50 |
| Builds within 5 fix rounds | 48/50 | 50/50 | 49/50 | 50/50 | 45/50 | 49/50 |
| First answer free of UI-layer errors | 30/50 | 30/50 | 38/50 | 39/50 | 32/50 | 41/50 |
| Model calls to a build (successes) | 2.58 | 2.40 | 2.49 | 2.28 | 2.49 | 2.16 |
| C# errors after the first answer | 1.5 | 0.6 | 3.9 | 1.2 | 2.3 | 7.8* |
| Output tokens, first answer | 5402 | 5730 | 6959 | 9363 | 6902 | 7512 |
| Output tokens, all rounds | 8635 | 8098 | 9620 | 12003 | 9808 | 8967 |
| Input tokens, all rounds | 12765 | 10655 | 43834 | 47849 | 61669 | 57276 |
| Wall time per app, s (all runs) | 80 | 60 | 104 | 87 | 99 | 72 |
| Cost per building app | $0.017 | $0.016 | $0.027 | $0.026 | $0.031 | $0.023 |
| Total cost | $0.80 | $0.82 | $1.34 | $1.28 | $1.42 | $1.14 |

\* Two generic first answers account for 318 of the 400 errors: one redeclared every generated property of three
ViewModels (152 errors, all hinted, fixed in the next round), one wrote a syntactically broken page partial (166
C# parse errors). Without them the generic mean is 1.7. The median is 1 in every condition in both runs.

None of the run-1 → run-2 changes in build success is significant by itself (Fisher p = 0.47, 0.83 and 0.50 for
first-answer builds; 0.20 for the generic condition's 45 → 49 within the fix rounds). The direction is consistent
(fewer calls, fewer C# errors, the sketch app fixed), but the raw condition improved too, with nothing changed
for it except the day: provider variance is as large as the effect. Wall time dropped in every condition, raw
included, so it reflects the machine and the provider's load more than the compiler.

**What the run does not show.** Builds, not working apps: nothing was launched, runtime binding failures are not
measured, and the checklist is graded only by control presence (93–97%). One cheap model, five samples: a
10–15-point difference in first-build success would need several hundred runs per condition.

**What the model still trips over** (all rounds, first answers in parentheses; see Error codes below):

- **Redeclared generated members**, still the top Flagstaff error: `CS0102` 32 (30) keywords, 61 (59) generic;
  `CS0756`/`CS0111` 60/63 in one generic answer. The hint fixes it in one round; prevention would be better:
  have the generator skip a property the hand-written partial already declares as `[ObservableProperty]` (or a
  plain property of the same name and type), or have `flagstaff build` drop the duplicate and warn. Either turns
  15 + 12 failing first answers into builds.
- **`partial` method accessibility** (`CS8799`, 7, never hinted): the model writes `public partial Task OnXAsync`
  against the generated `private partial`. Add a hint naming the generated signature, or generate it with the
  accessibility the hand-written half uses.
- **Missing `using`s in page partials** (`CS0246`/`CS0103` on `Colors`, `Color`, `NavigationEventArgs`,
  `TabView*EventArgs`, `PointerPoint`, `FileSavePicker`, `RenderTargetBitmap`): mostly unhinted (generic `CS0103`
  6 of 38 hinted, `CS0246` 9 of 19; keywords 1 of 20). The 0.3 hint for `Color`/`Colors` did not fire in
  `Views.cs` in these runs. A generated `global using` file for the WinUI namespaces (`Microsoft.UI`,
  `Microsoft.UI.Xaml.*`, `Microsoft.UI.Input`, `Windows.UI`, `Windows.Storage.Pickers`) would remove the class.
- **Generated-member collisions the hints do not cover**: `CS0111` on a generated page method (`OnViewLoaded` in
  `Generated/PlayerView.xaml.cs`), `CS1061` from the XAML-generated `NotesView.g.cs` for handler names used in
  `xaml { }` but not written (`OnAddTab`), `CS0260` on a service class (`InvoiceStore`, smoke run).
- **Inferred types used wrongly**: `DateTimeOffset?` from a date picker used as `DateTime` (`.Date`, `CS1061`), and
  `CS0029` (7 keywords, 20 generic, all generic ones hinted).
- **`.flag` errors are rare and hinted**: `FS1032` (icon names), `FS1031` (`Header` on `MenuBarItem`), `FS1030`
  (`InkCanvas`), `FS1004`. Escape ratio fell to 8% (keywords) and 5% (generic) of `.flag` lines, and `FS3011`
  (an escape block expressible as a generic element) is the only frequent lint.
- **Harness note**: hints name `.flag` files by a path relative to the harness's working directory
  (`bench/runs/.../Flags/x.flag`), because the CLI is run from the repository root; harmless, but noisy.
