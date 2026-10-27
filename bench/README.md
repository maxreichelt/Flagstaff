# Flagstaff benchmark

The product thesis is that **LLMs produce correct native Windows apps more often in Flagstaff than in raw XAML**
(docs/universal-element.md, Phase 5). This folder measures it: the same model gets the same ten app specs under
three conditions, the harness builds what it writes, feeds the build errors back, and records how often and how fast
it reaches a building app. The default model is deliberately cheap (`z-ai/glm-5.3` through OpenRouter): a language
that needs a frontier model to be written correctly is not the claim.

Results: [`results.md`](results.md) (run 3, on the compiler that skips members the hand-written C# declares,
FS2016/FS2017); earlier runs are kept as [`results-run2.md`](results-run2.md) /
[`narrative-run2.md`](narrative-run2.md) (0.3.0-alpha.1 with the contract hints) and
[`results-run1.md`](results-run1.md) / [`narrative-run1.md`](narrative-run1.md) (pre-fix compiler).

## What is measured

Per app × condition (`bench/runs/<model>/<app>__<condition>.json`):

| Metric | Meaning |
|---|---|
| First-build success | The first answer builds (Flagstaff: `flagstaff build`; raw: MSBuild), errors only, warnings allowed |
| Rounds to success | Model calls until the build succeeds (1 = first answer); fail after 1 + N fix rounds |
| Diagnostics per round | Error count, codes and messages after each round (the round-1 count is the headline) |
| Tokens and cost | Prompt, completion and reasoning tokens, and OpenRouter's reported cost per call; to a building app (successes only), on the first answer, and input tokens with the system prompt counted once (estimated from round 1 by character share; `system_prompt_chars`/`user_prompt_chars`) |
| Wall time | End-to-end seconds per pair (`wall_seconds`), split per round into `model_seconds` (API latency) and `build_seconds`; reported to a building app (successes only) as median and mean, compared with raw by a two-sided Mann–Whitney U test |
| UI source lines | Non-blank lines of `.xaml` (raw) or `.flag` (Flagstaff) in the final project; C# lines separately |
| XML errors (raw) | XAML files that are not well-formed XML (Python's parser, before the build): depth/closing-tag errors |
| Escape / generic ratio | From `flagstaff check --format json` on the final `.flag` files: `xaml { }` lines / `.flag` lines, generic / all elements |
| Control coverage | Share of the spec's required WinUI controls (`<!-- controls: -->` in each spec) that appear in the final XAML (Flagstaff: `Generated/*.xaml`) or C# |

## Conditions

All three use the same user prompt (the spec, `bench/specs/*.md` minus the hidden controls comment) and the same
output protocol (fenced blocks whose first line is `// file: <path>`); only the system prompt differs.

| Condition | System prompt | Project |
|---|---|---|
| `raw-xaml` | `conditions/raw-xaml.md`: "Write a WinUI 3 app (WindowsAppSDK 1.7, CommunityToolkit.Mvvm 8.4) ..." plus the skeleton files | `skeleton/raw/` (csproj, App, placeholder MainWindow) |
| `flagstaff-keywords` | `conditions/flagstaff-keywords.md`: `flagstaff spec` with the generic-tier sections removed; the `xaml { }` escape hatch allowed | `flagstaff init BenchApp`, minus the sample view |
| `flagstaff-generic` | `conditions/flagstaff-generic.md`: full `flagstaff spec` plus the generic-element documentation | same |

The condition files are generated (`python bench/run.py --write-prompts`) and committed, so a run is reproducible
from the repository; regenerate them when LANG.md changes.

**Spec for `flagstaff-generic`.** LANG.md (and so `flagstaff spec`) now documents the generic tier, so
`--write-prompts` uses the plain spec for the generic condition and strips the "Generic Elements", "Pass-Through
Props", "Events" and "Resources and States" sections from the keywords prompt. The interim spec
(`conditions/generic-interim.md`, a condensation of the compiler README's generic-tier sections, appended to the
spec) is no longer used; it is kept because the first run ([`results-run1.md`](results-run1.md)) used it.
Prompt sizes for run 3: raw 4.2k characters, keywords 70.6k (run 2: 69.7k, run 1: 54.6k), generic 87.7k (run 2: 86.8k,
run 1: 72.6k).

## Apps

Five business apps (`01`–`05`: invoices, CRM master-detail, inventory dashboard, ticket tracker with filters and a
confirm-delete, timesheet with date pickers and totals) and five consumer/creative apps (`06`–`10`: media player with
`MediaPlayerElement` and transport flyouts, notes with `TabView`/`Expander`/`MenuBar`, sketch with `InkCanvas`, photo
browser with `GridView`/`FlipView`, settings with adaptive visual states). Each spec is a plain-English description
plus a 6–10 item acceptance checklist; the checklist is given to the model but graded only through control coverage
(see caveats).

## Running it

Prerequisites: Windows, .NET SDK 10, Visual Studio 2026 (MSBuild.exe with the WinUI XAML compiler), Python 3 with
`requests`, and the Flagstaff packages packed into `artifacts/packages`:

```powershell
foreach ($p in 'src/Flagstaff.Stdlib','src/Flagstaff.CLI','src/Flagstaff.Build') { dotnet pack $p -c Release -o artifacts/packages }
$env:OPENROUTER_API_KEY = '...'          # or --key-file <path> (a file with the key, optionally KEY=...)
python bench/run.py --model z-ai/glm-5.3 --cap 25 --apps smoke --max-fix-rounds 2   # 2 apps x 3 conditions
python bench/run.py --model z-ai/glm-5.3 --cap 25 --apps all                        # 10 apps x 3 conditions, 5 fix rounds
python bench/run.py --model z-ai/glm-5.3 --cap 25 --apps all --samples 5            # 5 samples each (seeds 7..11)
python bench/aggregate.py --model z-ai/glm-5.3                                      # writes bench/results.md
# run 3 wrote to its own folder:
python bench/run.py --model z-ai/glm-5.3 --cap 10 --apps all --samples 5 --runs-dir run3-z-ai_glm-5.3
python bench/aggregate.py --model z-ai/glm-5.3 --runs-dir run3-z-ai_glm-5.3
```

- `--apps all|smoke|01-invoices,07-notes`, `--conditions raw-xaml,flagstaff-generic`, `--max-fix-rounds N`
  (default 5), `--runs-dir <folder under bench/runs>` (default: the model name), `--max-tokens` (default 32000), `--reasoning-tokens` (default 8000: a reasoning budget sent as
  `"reasoning": {"max_tokens": N}`; in the smoke run one GLM 5.3 call spent all 32k tokens reasoning and returned
  nothing), `--temperature` (0.2), `--redo`.
- **What counts as a build**: an answer with no files (`NO_FILES`) is not built and counts as a failed round; a
  green build of a project the model has not written the UI for (raw: no `.xaml`; Flagstaff: no `app.flag` plus a
  view) is `NO_UI`, also a failed round. A truncated answer (`finish_reason: length`) is built as far as it got, and
  the feedback says it was cut off.
- `--samples N` runs N independent samples per pair (seed 7 + sample; sample 0 is the plain `<app>__<cond>.json`).
- **Resumable**: a finished pair is skipped (a failed pair is rerun when more fix rounds are requested).
- **Cost cap**: costs come from OpenRouter (`"usage": {"include": true}`) and every call is appended to
  `runs/<model>/ledger.jsonl` (redone pairs included); before every call the harness checks that the ledger total
  plus the most expensive call so far stays under `--cap`, and stops otherwise.
- Calls retry with exponential backoff on 429/5xx/network errors. Requests send `HTTP-Referer` and `X-Title`.
- Everything under `bench/runs/` is gitignored: per-pair JSON, full transcripts (`transcripts/`), and the generated
  projects (`work/<app>/<raw|kw|gen>/`, kept for inspection; paths are short because WinUI's output paths hit
  MAX_PATH). Each project gets its own `nuget.config` (Flagstaff packages from `artifacts/packages`, the rest from
  nuget.org) and empty `Directory.Build.*`/`Directory.Packages.props` so the repository's central package
  management does not leak in.
- **Where the data lives.** `bench/runs/` is not in git. The raw data of runs 2 and 3 is kept in the main
  checkout's `bench/runs/`: run 2 in `z-ai_glm-5.3/` (its smoke run in `smoke-z-ai_glm-5.3/`), run 3 in
  `run3-z-ai_glm-5.3/` (its smoke run in `run3-smoke-z-ai_glm-5.3/`). Run 1's per-pair data was not kept.
- **Warnings** never fail a round: success is `flagstaff build`'s (or MSBuild's) zero-error result, and neither the
  `flagstaff init` project nor the raw skeleton sets `TreatWarningsAsErrors` (the Flagstaff targets also keep
  FS2016/FS2017 out of `-warnaserror`). Since run 3 each Flagstaff round records its warning codes
  (`warning_codes`) and the FS20xx warnings in full (`fs_warnings`), so first answers the generator's FS2016/FS2017
  skip rescued can be counted; earlier records have no warnings, and `aggregate.py` shows `n/a` for them.
- The API key is read into memory only; it is never printed, logged or written to disk by the harness.

## Caveats

- **Run 3** (`results.md`): same apps, seeds, temperature, token budgets and fix rounds as runs 1 and 2, on the
  compiler that skips members the hand-written C# declares (FS2016/FS2017, `OnViewCreated`, global usings, more
  hints), with prompts regenerated from its LANG.md (keywords 70.6k characters, generic 87.7k, raw 4.2k). Again the
  compiler and the prompts changed together. Harness change: build warnings are recorded per round (the success
  rule is unchanged), and the run wrote to `runs/run3-z-ai_glm-5.3/` (`--runs-dir`).
- **Run 2** (`results-run2.md`): same apps, seeds, temperature, token budgets and fix rounds as run 1, on the
  0.3.0-alpha.1 compiler (contract hints, contract headers, better `.flag` hints) with prompts regenerated from the
  new LANG.md. The compiler and the prompts changed together, so run-1 vs run-2 differences cannot be attributed to
  either one alone. The only harness change is the extra timing and prompt-size fields.
- **Run 1** (`results-run1.md`): 10 apps × 3 conditions × 5 samples, $3.60. Harness fixes made during it:
  after the smoke run, the empty-answer/`NO_UI` rule and the reasoning budget; after sample 0, a fallback parser
  for `// file:` headers without code fences (one keywords-condition answer used them; that pair,
  `07-notes__flagstaff-keywords`, was rerun with the same seed, and both runs are in the ledger), and a regex fix
  for MSBuild errors without a line number (`aggregate.py` re-parses those rounds from the saved build output).
- **The sketch spec asks for a control WinUI 3 does not have.** WindowsAppSDK 1.7 has no `InkCanvas`; the spec is
  kept as written (an app spec from a user would say the same), so every condition has to work around it.
- **Sample size.** Ten apps, one sample per pair, one model. Differences of one or two apps are noise. The
  temperature is 0.2 and a seed is sent, but providers do not guarantee determinism.
- **"Builds" is not "correct".** The headline metric is a successful build. The checklist is not graded beyond the
  automatic control-coverage check, and the apps are not run, so binding failures at runtime (a Phase 5 metric in
  the design doc) are not measured. A model can reach a green build by deleting features; coverage and UI line
  counts are reported to make that visible, and the generated projects are kept for manual review.
- **Prompt length.** The generic prompt is longer than the keywords prompt, and both are 15–20× the raw prompt;
  that costs input tokens and may help or hurt a small model. Input tokens are reported both as billed (system
  prompt with every call) and with the system prompt counted once.
- **Asymmetric scaffolding.** Flagstaff generates `App.xaml`, `MainWindow`, navigation, converters and the
  ViewModel boilerplate; the raw condition gets a minimal skeleton and writes all of that itself. That asymmetry is
  the product, not a bias in the harness, but it means the conditions do not ask for the same amount of code.
- **No `flagstaff stubs`.** The Flagstaff conditions must write every `On{X}Async` themselves (missing ones are
  CS8795 errors fed back like any other); a real agent would run `flagstaff stubs --update`.
- **Feedback format differs by condition**: Flagstaff errors come with the compiler's hints, MSBuild errors do not.
  That is also part of what is being measured.
- Error counts are deduplicated per (code, file, line, column, message). A Flagstaff parse error stops code
  generation for its file, so a broken `.flag` file can hide C# errors that a raw project would report all at once;
  compare first-build success and rounds rather than raw error counts across conditions.
