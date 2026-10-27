#!/usr/bin/env python3
"""Aggregate bench/runs/<model>/*.json into bench/results.md.

    python bench/aggregate.py --model z-ai/glm-5.3
    python bench/aggregate.py --model z-ai/glm-5.3 --runs-dir run3-z-ai_glm-5.3   # another folder under bench/runs

The hand-written narrative in bench/narrative.md (if present) is inserted verbatim after the summary table.
"""
from __future__ import annotations

import argparse
import json
import re
import statistics
import sys
from collections import Counter
from pathlib import Path

BENCH = Path(__file__).resolve().parent
sys.path.insert(0, str(BENCH))
CONDITIONS = ["raw-xaml", "flagstaff-keywords", "flagstaff-generic"]


def mean(xs):
    xs = [x for x in xs if x is not None]
    return statistics.mean(xs) if xs else None


def fmt(x, digits=1, pct=False, money=False):
    if x is None:
        return "–"
    if pct:
        return f"{100 * x:.0f}%"
    if money:
        return f"${x:.3f}"
    return f"{x:.{digits}f}"


def fisher_p(a: int, n1: int, c: int, n2: int) -> float:
    """Two-sided Fisher exact test for a/n1 vs c/n2 successes."""
    from math import comb
    k, n = a + c, n1 + n2

    def p(x):
        return comb(n1, x) * comb(n2, k - x) / comb(n, k)

    obs = p(a)
    lo, hi = max(0, k - n2), min(k, n1)
    return min(1.0, sum(p(x) for x in range(lo, hi + 1) if p(x) <= obs * (1 + 1e-9)))


def median(xs):
    xs = [x for x in xs if x is not None]
    return statistics.median(xs) if xs else None


def mann_whitney_p(xs, ys):
    """Two-sided Mann-Whitney U test (normal approximation with tie and continuity correction).

    Uses scipy when it is installed; returns None when either sample is empty."""
    xs = [x for x in xs if x is not None]
    ys = [y for y in ys if y is not None]
    if not xs or not ys:
        return None
    try:
        from scipy.stats import mannwhitneyu  # type: ignore
        return float(mannwhitneyu(xs, ys, alternative="two-sided").pvalue)
    except ImportError:
        pass
    from math import erfc, sqrt
    n1, n2 = len(xs), len(ys)
    pooled = sorted([(v, 0) for v in xs] + [(v, 1) for v in ys])
    ranks = [0.0] * len(pooled)
    ties = 0.0
    i = 0
    while i < len(pooled):
        j = i
        while j + 1 < len(pooled) and pooled[j + 1][0] == pooled[i][0]:
            j += 1
        for k in range(i, j + 1):
            ranks[k] = (i + j) / 2 + 1
        t = j - i + 1
        ties += t ** 3 - t
        i = j + 1
    r1 = sum(r for r, (_, g) in zip(ranks, pooled) if g == 0)
    u = r1 - n1 * (n1 + 1) / 2
    mu = n1 * n2 / 2
    n = n1 + n2
    sigma = sqrt(n1 * n2 / 12 * ((n + 1) - ties / (n * (n - 1)))) if n > 1 else 0.0
    if sigma == 0:
        return 1.0
    z = (abs(u - mu) - 0.5) / sigma
    return min(1.0, erfc(max(z, 0.0) / sqrt(2)))


def round_model_seconds(rd: dict):
    v = rd.get("model_seconds", rd.get("latency"))
    return float(v) if v is not None else None


def rec_split_seconds(r: dict):
    """(model seconds, build seconds) summed over the rounds; None when a round lacks the field (run-1 shape)."""
    ms = [round_model_seconds(rd) for rd in r["rounds"]]
    bs = [rd.get("build_seconds") for rd in r["rounds"]]
    m = sum(ms) if ms and all(x is not None for x in ms) else None
    b = sum(bs) if bs and all(x is not None for x in bs) else None
    return m, b


def system_prompt_tokens(r: dict):
    """Estimate of the system prompt's tokens: round 1's prompt tokens split by character share.

    None for records without the prompt sizes (run-1 shape)."""
    sc, uc = r.get("system_prompt_chars"), r.get("user_prompt_chars")
    p1 = r["rounds"][0].get("tokens_in") if r["rounds"] else None
    if not sc or not uc or not p1:
        return None
    return p1 * sc / (sc + uc)


def visible_out(r: dict):
    """Completion tokens minus reasoning tokens: the answer the model actually wrote."""
    if r.get("reasoning_tokens") is None:
        return None
    return r["tokens_out"] - r["reasoning_tokens"]


def input_tokens_sys_once(r: dict):
    """Input tokens with the system prompt counted once instead of once per call."""
    s = system_prompt_tokens(r)
    if s is None:
        return None
    return r["tokens_in"] - s * (len(r["rounds"]) - 1)


def ui_errors(rnd: dict) -> int:
    """Errors in the UI layer: XAML compiler (WMC*), Flagstaff (FS*), XML well-formedness, empty answers."""
    n = sum(v for k, v in rnd["codes"].items() if k.startswith(("WMC", "FS", "XLS", "NO_")))
    return n + len(rnd.get("xml_errors") or [])


def runs_folder(model: str, runs_dir: Path | None = None) -> Path:
    """bench/runs/<model> by default; --runs-dir names another folder (relative to bench/runs, or a path)."""
    if runs_dir is not None:
        return runs_dir if runs_dir.is_absolute() else BENCH / "runs" / runs_dir
    return BENCH / "runs" / re.sub(r"[^\w.-]", "_", model)


def work_path(mdir: Path, rec: dict) -> Path:
    """The kept project; also found when the run folder was moved or renamed after the run."""
    work = BENCH / rec.get("work", "")
    if not work.exists() and "/work/" in rec.get("work", ""):
        work = mdir / "work" / rec["work"].split("/work/", 1)[1]
    return work


def fs_warnings(rnd: dict) -> dict:
    """FS2016/FS2017 counts of a round ({} for rounds recorded before run 3, which kept no warnings)."""
    return {k: v for k, v in (rnd.get("warning_codes") or {}).items() if k in ("FS2016", "FS2017")}


def load(model: str, runs_dir: Path | None = None) -> list[dict]:
    mdir = runs_folder(model, runs_dir)
    recs = []
    for f in sorted(mdir.glob("*__*.json")):
        r = json.loads(f.read_text(encoding="utf-8"))
        if r.get("status") == "done":
            # Recompute coverage from the kept project so spec edits to the controls list apply retroactively.
            work = work_path(mdir, r)
            if r.get("work") and work.exists():
                import run  # noqa: E402  (bench/run.py)
                r["coverage"] = run.control_coverage(work, r["app"], r["condition"])
            # Re-parse failed rounds whose build output the harness's first regex did not understand.
            import run  # noqa: E402
            for rd in r["rounds"]:
                if not rd["ok"] and not rd["codes"] and rd.get("build_tail"):
                    for line in rd["build_tail"].splitlines():
                        m = run.MSBUILD_DIAG.match(line) or run.MSBUILD_DIAG_NOLOC.match(line)
                        if m and m.group("sev") == "error":
                            rd["codes"][m.group("code")] = rd["codes"].get(m.group("code"), 0) + 1
                    rd["errors"] = sum(rd["codes"].values())
            recs.append(r)
    return recs


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="z-ai/glm-5.3")
    ap.add_argument("--out", type=Path, default=BENCH / "results.md")
    ap.add_argument("--runs-dir", type=Path, default=None,
                    help="run folder, relative to bench/runs (default: the model name, e.g. z-ai_glm-5.3)")
    args = ap.parse_args()
    mdir = runs_folder(args.model, args.runs_dir)
    recs = load(args.model, args.runs_dir)
    by_cond = {c: [r for r in recs if r["condition"] == c] for c in CONDITIONS}
    apps = sorted({r["app"] for r in recs})
    # Only (app, sample) units that finished under every condition enter the comparison.
    units = sorted({(r["app"], r.get("sample", 0)) for r in recs})
    complete = {u for u in units
                if all(any((r["app"], r.get("sample", 0)) == u for r in by_cond[c]) for c in CONDITIONS)}
    samples = sorted({u[1] for u in complete})
    fix_rounds = sorted({r.get("max_fix_rounds") for r in recs if r.get("max_fix_rounds") is not None})

    L = []
    L.append(f"# Benchmark results: `{args.model}`\n")
    L.append(f"Generated by `bench/aggregate.py` from `bench/runs/{mdir.name}/` ({len(recs)} finished app/condition runs; "
             f"{len({u[0] for u in complete})} apps x {len(samples)} sample(s) complete under all three conditions; "
             f"up to {', '.join(map(str, fix_rounds))} fix rounds after the first answer). See `bench/README.md` "
             f"for the method and caveats.\n")

    L.append("## Summary per condition\n")
    L.append("| Metric | " + " | ".join(CONDITIONS) + " |")
    L.append("|---|" + "---|" * len(CONDITIONS))

    def inc(r):
        return (r["app"], r.get("sample", 0)) in complete

    def row(label, fn, subset=lambda r: True):
        L.append(f"| {label} | " + " | ".join(fn([r for r in by_cond[c] if inc(r) and subset(r)])
                                             for c in CONDITIONS) + " |")

    def business(r):
        return int(r["app"][:2]) <= 5

    row("Runs (apps x samples)", lambda rs: str(len(rs)))
    row("Builds on the first answer", lambda rs: f"{sum(r['first_build_ok'] for r in rs)}/{len(rs)}" if rs else "–")
    row("Builds within the fix rounds", lambda rs: f"{sum(r['success'] for r in rs)}/{len(rs)}" if rs else "–")
    row("&nbsp;&nbsp;business apps 01–05: first answer / within fixes",
        lambda rs: f"{sum(r['first_build_ok'] for r in rs)}/{len(rs)} / {sum(r['success'] for r in rs)}/{len(rs)}"
        if rs else "–", business)
    row("&nbsp;&nbsp;consumer apps 06–10: first answer / within fixes",
        lambda rs: f"{sum(r['first_build_ok'] for r in rs)}/{len(rs)} / {sum(r['success'] for r in rs)}/{len(rs)}"
        if rs else "–", lambda r: not business(r))
    def p_vs_raw(key):
        def fn(rs):
            if not rs or rs[0]["condition"] == "raw-xaml":
                return "–"
            raw = [r for r in by_cond["raw-xaml"] if inc(r)]
            return f"p = {fisher_p(sum(r[key] for r in rs), len(rs), sum(r[key] for r in raw), len(raw)):.2f}"
        return fn

    row("&nbsp;&nbsp;Fisher exact vs raw-xaml, first answer", p_vs_raw("first_build_ok"))
    row("&nbsp;&nbsp;Fisher exact vs raw-xaml, within fixes", p_vs_raw("success"))
    row("Mean model calls to a build (successes)",
        lambda rs: fmt(mean([r["rounds_to_success"] for r in rs if r["success"]]), 2))
    row("Mean errors after the first answer", lambda rs: fmt(mean([r["rounds"][0]["errors"] for r in rs]), 1))
    row("&nbsp;&nbsp;of which UI layer (XAML compiler `WMC`, Flagstaff `FS`, XML)",
        lambda rs: fmt(mean([ui_errors(r["rounds"][0]) for r in rs]), 1))
    row("&nbsp;&nbsp;of which C# (`CS`)",
        lambda rs: fmt(mean([sum(v for k, v in r["rounds"][0]["codes"].items() if k.startswith("CS")) for r in rs]), 1))
    row("First answer free of UI-layer errors",
        lambda rs: f"{sum(ui_errors(r['rounds'][0]) == 0 for r in rs)}/{len(rs)}" if rs else "–")
    has_warn = any("warning_codes" in rd for r in recs for rd in r["rounds"])

    def warn_row(label, fn):
        row(label, lambda rs: (fn(rs) if rs and rs[0]["condition"] != "raw-xaml" else "n/a") if has_warn else "n/a")

    warn_row("First answers with FS2016/FS2017 (generator skipped or declared a member)",
             lambda rs: f"{sum(bool(fs_warnings(r['rounds'][0])) for r in rs)}/{len(rs)}")
    warn_row("&nbsp;&nbsp;of which built on the first answer",
             lambda rs: f"{sum(bool(fs_warnings(r['rounds'][0])) and r['first_build_ok'] for r in rs)}")
    row("Median errors after the first answer",
        lambda rs: fmt(statistics.median([r["rounds"][0]["errors"] for r in rs]), 0) if rs else "–")
    row("Mean output tokens, first answer", lambda rs: fmt(mean([r["rounds"][0]["tokens_out"] for r in rs]), 0))
    row("Mean output tokens, all rounds", lambda rs: fmt(mean([r["tokens_out"] for r in rs]), 0))
    row("Mean input tokens, all rounds", lambda rs: fmt(mean([r["tokens_in"] for r in rs]), 0))
    row("Mean cost per app", lambda rs: fmt(mean([r["cost"] for r in rs]), money=True))
    row("Total cost", lambda rs: fmt(sum(r["cost"] for r in rs), money=True))
    row("Mean wall time per app (s)", lambda rs: fmt(mean([r["wall_seconds"] for r in rs]), 0))
    row("Mean UI source lines (.xaml / .flag)", lambda rs: fmt(mean([r.get("ui_lines") for r in rs]), 0))
    row("Mean C# lines", lambda rs: fmt(mean([r.get("cs_lines") for r in rs]), 0))
    row("Required controls present (final)", lambda rs: fmt(mean([r["coverage"]["ratio"] for r in rs]), pct=True))
    row("XML well-formedness errors, first answer (raw)",
        lambda rs: fmt(mean([r.get("xml_errors_round1") for r in rs]), 2) if rs and rs[0]["condition"] == "raw-xaml"
        else "n/a")
    row("Escape ratio, final (`xaml { }` lines / .flag lines)",
        lambda rs: fmt(mean([(r.get("final_stats") or {}).get("escapeRatio") for r in rs]), pct=True)
        if rs and rs[0]["condition"] != "raw-xaml" else "n/a")
    row("Generic ratio, final (generic / all elements)",
        lambda rs: fmt(mean([(r.get("final_stats") or {}).get("genericRatio") for r in rs]), pct=True)
        if rs and rs[0]["condition"] != "raw-xaml" else "n/a")
    L.append("")

    # ---------------------------------------------------------------- time and tokens
    L.append("## Time and tokens per condition\n")
    L.append("\"To a building app\" = successful runs only, summed over the calls up to and including the green "
             "build. Cells are `median / mean`; p = two-sided Mann–Whitney U against raw-xaml. Wall time is the "
             "harness's end-to-end time per run (model calls, project setup, builds, `flagstaff check`), measured "
             "on one machine, one run at a time. The system prompt's share of the input is estimated from round 1's "
             "prompt tokens by character share. `n/a` = not recorded (run-1 records).\n")
    L.append("| Metric | " + " | ".join(CONDITIONS) + " |")
    L.append("|---|" + "---|" * len(CONDITIONS))

    def succ(rs):
        return [r for r in rs if r["success"]]

    def mm(vals, digits=0):
        vals = list(vals)
        if not vals or any(v is None for v in vals):
            return "n/a"
        return f"{fmt(median(vals), digits)} / {fmt(mean(vals), digits)}"

    def mw_row(label, getter, only_success=True):
        def fn(rs):
            if not rs or rs[0]["condition"] == "raw-xaml":
                return "–"
            raw = [r for r in by_cond["raw-xaml"] if inc(r)]
            a = [getter(r) for r in (succ(rs) if only_success else rs)]
            b = [getter(r) for r in (succ(raw) if only_success else raw)]
            if any(v is None for v in a + b):
                return "n/a"
            p = mann_whitney_p(a, b)
            return "n/a" if p is None else ("p < 0.001" if p < 0.001 else f"p = {p:.2f}")
        row(label, fn)

    row("Wall time to a building app, s", lambda rs: mm([r.get("wall_seconds") for r in succ(rs)]))
    mw_row("&nbsp;&nbsp;Mann–Whitney vs raw-xaml", lambda r: r.get("wall_seconds"))
    row("&nbsp;&nbsp;of which model latency, s", lambda rs: mm([rec_split_seconds(r)[0] for r in succ(rs)]))
    row("&nbsp;&nbsp;of which build time, s", lambda rs: mm([rec_split_seconds(r)[1] for r in succ(rs)]))
    row("Output tokens to a building app", lambda rs: mm([r["tokens_out"] for r in succ(rs)]))
    mw_row("&nbsp;&nbsp;Mann–Whitney vs raw-xaml", lambda r: r["tokens_out"])
    row("&nbsp;&nbsp;of which hidden reasoning", lambda rs: mm([r.get("reasoning_tokens") for r in succ(rs)]))
    row("&nbsp;&nbsp;of which visible answer (code and prose)",
        lambda rs: mm([visible_out(r) for r in succ(rs)]))
    mw_row("&nbsp;&nbsp;&nbsp;&nbsp;Mann–Whitney vs raw-xaml (visible)", visible_out)
    row("Output tokens, first answer (all runs)", lambda rs: mm([r["rounds"][0]["tokens_out"] for r in rs]))
    mw_row("&nbsp;&nbsp;Mann–Whitney vs raw-xaml", lambda r: r["rounds"][0]["tokens_out"], only_success=False)
    row("Input tokens per app (all runs, every call)", lambda rs: mm([r["tokens_in"] for r in rs]))
    row("&nbsp;&nbsp;system prompt (est.), sent with every call", lambda rs: mm([system_prompt_tokens(r) for r in rs]))
    row("&nbsp;&nbsp;input with the system prompt counted once (est.)",
        lambda rs: mm([input_tokens_sys_once(r) for r in rs]))
    row("Cost per building app (total cost / builds)",
        lambda rs: fmt(sum(r["cost"] for r in rs) / max(1, sum(r["success"] for r in rs)), money=True) if rs else "–")
    row("Cost of a successful run, median / mean",
        lambda rs: (f"{fmt(median([r['cost'] for r in succ(rs)]), money=True)} / "
                    f"{fmt(mean([r['cost'] for r in succ(rs)]), money=True)}") if succ(rs) else "–")
    L.append("")

    narrative = BENCH / "narrative.md"
    if narrative.exists():
        L.append(narrative.read_text(encoding="utf-8").rstrip() + "\n")

    L.append("## Per app\n")
    L.append("One cell per sample, separated by ` · `: `OK@n` = builds after n model calls (1 = first answer); "
             "`FAIL(e)` = still e errors after the last round. Then: errors after the first answer (mean over "
             "samples) and required-control coverage of the final project (mean).\n")
    L.append("| App | " + " | ".join(CONDITIONS) + " |")
    L.append("|---|" + "---|" * len(CONDITIONS))
    for a in apps:
        cells = []
        for c in CONDITIONS:
            rs = sorted((x for x in by_cond[c] if x["app"] == a), key=lambda x: x.get("sample", 0))
            if not rs:
                cells.append("not run")
                continue
            res = " · ".join(f"OK@{r['rounds_to_success']}" if r["success"] else f"FAIL({r['rounds'][-1]['errors']})"
                             for r in rs)
            cells.append(f"{res}; r1 err {fmt(mean([r['rounds'][0]['errors'] for r in rs]), 1)}; "
                         f"cov {fmt(mean([r['coverage']['ratio'] for r in rs]), pct=True)}")
        L.append(f"| {a} | " + " | ".join(cells) + " |")
    L.append("")

    L.append("### Time and output tokens to a building app, per app\n")
    L.append("Median over the successful samples: wall seconds / output tokens (all calls up to the green build), "
             "then `(successes/samples)`.\n")
    L.append("| App | " + " | ".join(CONDITIONS) + " |")
    L.append("|---|" + "---|" * len(CONDITIONS))
    for a in apps:
        cells = []
        for c in CONDITIONS:
            rs = [x for x in by_cond[c] if x["app"] == a]
            ok = [x for x in rs if x["success"]]
            if not ok:
                cells.append(f"– (0/{len(rs)})")
                continue
            w = median([x.get("wall_seconds") for x in ok])
            cells.append(f"{fmt(w, 0) if w is not None else 'n/a'} s / "
                         f"{fmt(median([x['tokens_out'] for x in ok]), 0)} tok ({len(ok)}/{len(rs)})")
        L.append(f"| {a} | " + " | ".join(cells) + " |")
    L.append("")

    L.append("## Error codes\n")
    L.append("Diagnostics the model hit, summed over every round (first answer in parentheses).\n")
    for c in CONDITIONS:
        allc, first = Counter(), Counter()
        for r in by_cond[c]:
            for i, rd in enumerate(r["rounds"]):
                for k, v in rd["codes"].items():
                    allc[k] += v
                    if i == 0:
                        first[k] += v
        top = ", ".join(f"`{k}` {v} ({first.get(k, 0)})" for k, v in allc.most_common(15)) or "none"
        L.append(f"- **{c}**: {top}")
    L.append("")
    if has_warn:
        L.append("Generator warnings on `flagstaff build` (FS2016 skipped member, FS2017 declared partial), summed "
                 "over every round (first answer in parentheses):\n")
        for c in CONDITIONS[1:]:
            allw, firstw = Counter(), Counter()
            for r in by_cond[c]:
                for i, rd in enumerate(r["rounds"]):
                    allw.update(fs_warnings(rd))
                    if i == 0:
                        firstw.update(fs_warnings(rd))
            L.append(f"- **{c}**: " + (", ".join(f"`{k}` {v} ({firstw.get(k, 0)})" for k, v in sorted(allw.items()))
                                        or "none"))
        L.append("")
    L.append("Lint warnings in the final Flagstaff sources (`flagstaff check`):\n")
    for c in CONDITIONS[1:]:
        w = Counter()
        for r in by_cond[c]:
            w.update(r.get("final_warning_codes") or {})
        L.append(f"- **{c}**: " + (", ".join(f"`{k}` {v}" for k, v in w.most_common(10)) or "none"))
    L.append("")
    L.append(f"Total spend for these pairs: {fmt(sum(r['cost'] for r in recs), money=True)}.\n")
    args.out.write_text("\n".join(L), encoding="utf-8")
    print("wrote", args.out)


if __name__ == "__main__":
    main()
