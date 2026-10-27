#!/usr/bin/env python3
"""Flagstaff benchmark harness (docs/universal-element.md, Phase 5).

For each app spec x condition: create a project, ask the model for the whole app, write the files, build, feed the
build errors back (up to --max-fix-rounds fix rounds), and record what happened in
bench/runs/<model>/<app>__<condition>.json. Resumable: finished pairs are skipped.

    python bench/run.py --model z-ai/glm-5.3 --cap 25 --apps smoke --max-fix-rounds 2
    python bench/run.py --model z-ai/glm-5.3 --cap 25 --apps all
    python bench/run.py --write-prompts          # regenerate bench/conditions/flagstaff-*.md from `flagstaff spec`

The API key comes from OPENROUTER_API_KEY, else from --key-file. It is held in memory only and never logged.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

import requests

BENCH = Path(__file__).resolve().parent
REPO = BENCH.parent
SPECS = BENCH / "specs"
CONDITIONS = BENCH / "conditions"
SKELETON_RAW = BENCH / "skeleton" / "raw"
RUNS = BENCH / "runs"
PACKAGES = REPO / "artifacts" / "packages"
CLI_PROJECT = REPO / "src" / "Flagstaff.CLI"
CLI_DLL = CLI_PROJECT / "bin" / "Release" / "net10.0" / "Flagstaff.CLI.dll"
DEFAULT_KEY_FILE = Path(r"C:\Users\maxre\source\repos\maxreichelt\Flagstaff\OPENROUTER")
MSBUILD = Path(r"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe")
API_URL = "https://openrouter.ai/api/v1/chat/completions"

CONDITION_NAMES = ["raw-xaml", "flagstaff-keywords", "flagstaff-generic"]
SHORT = {"raw-xaml": "raw", "flagstaff-keywords": "kw", "flagstaff-generic": "gen"}
SMOKE_APPS = ["01-invoices", "07-notes"]
APP_NAME = "BenchApp"

# Sections that belong to the generic tier; removed from the keywords-only prompt when `flagstaff spec` has them.
GENERIC_SECTIONS = ["Generic Elements", "Pass-Through Props", "Events", "Resources and States"]

ISOLATION_FILES = {
    "Directory.Build.props": "<Project></Project>\n",
    "Directory.Build.targets": "<Project></Project>\n",
    "Directory.Packages.props": "<Project><PropertyGroup><ManagePackageVersionsCentrally>false"
                                "</ManagePackageVersionsCentrally></PropertyGroup></Project>\n",
}


def nuget_config() -> str:
    return f"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="flagstaff-artifacts" value="{PACKAGES}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="flagstaff-artifacts"><package pattern="Flagstaff" /><package pattern="Flagstaff.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"""


# ----------------------------------------------------------------------------------------------- prompts

OUTPUT_PROTOCOL = """## Output protocol

- Output every file you create or change as one fenced code block whose **first line** is a header comment with the
  path relative to the project folder: `// file: Views/MainPage.xaml.cs`. Use that `// file:` header for every file
  type (.cs, .xaml, .flag, .csproj); the harness removes the header line before writing the file.
- Each block holds the complete file content. No diffs, no ellipses, no "rest unchanged".
- Do not ask questions; make reasonable assumptions and write the whole app in one answer. Keep prose outside the
  code blocks to a minimum.
- The harness writes your files, builds the project, and sends back the build errors. When you get errors,
  output only the files you change, complete, with the same headers.
"""

FLAGSTAFF_HEADER = """You are an expert Windows developer. You write WinUI 3 desktop apps in **Flagstaff**, a DSL (`.flag` files) that
compiles to WinUI 3 XAML, code-behind and CommunityToolkit.Mvvm ViewModel partials. The complete language reference
follows this introduction. Read it carefully: only the syntax it documents compiles.

## The project

The harness created the project with `flagstaff init {app}`; it builds with `flagstaff build` (MSBuild, x64, Debug).
Root namespace: `{app}`. ViewModels live in `{app}.ViewModels`. It contains:

- `{app}.csproj`: WindowsAppSDK 1.7, the `Flagstaff` package (build targets, Flagstaff.Stdlib, CommunityToolkit.Mvvm
  8.4, Microsoft.Extensions.DependencyInjection). Do not change it.
- `App.Services.cs` (shown below), `app.manifest`.
- `Flags/` and `ViewModels/` are empty. `Generated/` is written by the build; never write files there.

```csharp
// file: App.Services.cs
{app_services}```

You write: `Flags/app.flag` (the `app` declaration with theme and navigation), one `Flags/<name>.flag` per screen,
`ViewModels/<Name>ViewModel.cs` for each view (the hand-written half: implement every generated
`private partial Task On{{X}}Async(...)` the view's commands need, and load sample data), item-type partials and
services as needed, `App.Services.cs` if you register services, and page partials
(`public sealed partial class XView` in the root namespace, e.g. `Views.cs`) for code that needs a control's API.
There is no `flagstaff stubs` step: write every `On{{X}}Async` yourself.

{protocol}
{extra}
# Language reference

"""

RAW_HEADER = """You are an expert Windows developer. Write a WinUI 3 app (WindowsAppSDK 1.7, CommunityToolkit.Mvvm 8.4, .NET 10,
unpackaged) in XAML and C#, using MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`,
`ObservableObject`) and `{{x:Bind}}` bindings.

## The project

The harness provides this skeleton; it builds with MSBuild (`-restore -p:Platform=x64 -p:Configuration=Debug`).
Root namespace: `{app}`. The skeleton's `MainWindow` is a placeholder: replace it. You may change `App.xaml` and
`App.xaml.cs`; do not change the `.csproj` or `app.manifest` (no additional NuGet packages are available).

{skeleton}
You write: `MainWindow.xaml` / `MainWindow.xaml.cs` (navigation shell if the app has several screens), one page per
screen (e.g. `Views/InvoicesPage.xaml` + `.xaml.cs`), ViewModels (e.g. `ViewModels/InvoicesViewModel.cs`), models,
services and converters as needed. Every XAML file must have its code-behind file with the matching `x:Class`.

{protocol}"""

GENERIC_NOTE = """## Generic elements are available

This build of Flagstaff includes the generic-element tier (any WinUI type in the element form, pass-through
properties, events, resources and visual states). The language reference below ends with a section
**Generic Elements** that documents it; where the older text says generic elements are "coming in 0.3", they are
available now. Prefer a keyword where one exists; use generic elements instead of `xaml { }` for everything else.
"""

KEYWORDS_NOTE = """## Keywords and the escape hatch

Use the keywords documented below. For controls without a keyword (`Expander`, `TabView`, `MenuBar`, `Slider`,
`MediaPlayerElement`, `InkCanvas`, `GridView`, `FlipView`, visual states, ...) use the `xaml { }` escape hatch
as the reference describes, and C# in page partials for control APIs.
"""


def cli(*args: str, cwd: Path | None = None, timeout: int = 900) -> subprocess.CompletedProcess:
    return subprocess.run(["dotnet", str(CLI_DLL), *args], cwd=cwd, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout)


def split_sections(text: str) -> list[tuple[str, str]]:
    """Split markdown into (## heading, chunk) pairs; the preamble has heading ''."""
    parts, current, title = [], [], ""
    in_fence = False
    for line in text.splitlines(keepends=True):
        if line.startswith("```"):
            in_fence = not in_fence
        if not in_fence and line.startswith("## "):
            parts.append((title, "".join(current)))
            current, title = [], line[3:].strip()
        current.append(line)
    parts.append((title, "".join(current)))
    return parts


def write_prompts() -> None:
    spec = cli("spec").stdout
    if not spec.strip():
        sys.exit("`flagstaff spec` printed nothing")
    services = (SKELETON_RAW.parent / "flagstaff-App.Services.cs").read_text(encoding="utf-8")

    def header(extra: str) -> str:
        return FLAGSTAFF_HEADER.format(app=APP_NAME, app_services=services, protocol=OUTPUT_PROTOCOL, extra=extra)

    skeleton = ""
    for name in ("BenchApp.csproj", "App.xaml", "App.xaml.cs", "MainWindow.xaml", "MainWindow.xaml.cs"):
        lang = {"csproj": "xml", "xaml": "xml", "cs": "csharp"}[name.rsplit(".", 1)[1]]
        skeleton += (f"```{lang}\n// file: {name}\n" + (SKELETON_RAW / name).read_text(encoding="utf-8")
                     + "```\n\n")
    (CONDITIONS / "raw-xaml.md").write_text(RAW_HEADER.format(app=APP_NAME, skeleton=skeleton,
                                                              protocol=OUTPUT_PROTOCOL), encoding="utf-8")

    # flagstaff-keywords: the spec without the generic-tier sections.
    kept = [chunk for title, chunk in split_sections(spec)
            if not any(title.lower().startswith(g.lower()) for g in GENERIC_SECTIONS)]
    (CONDITIONS / "flagstaff-keywords.md").write_text(header(KEYWORDS_NOTE) + "".join(kept), encoding="utf-8")

    # flagstaff-generic: the full spec, plus the interim generic-tier documentation when the spec lacks it.
    generic = spec
    has_generic = any(t.lower().startswith("generic elements") for t, _ in split_sections(spec))
    if not has_generic:
        interim = (CONDITIONS / "generic-interim.md").read_text(encoding="utf-8")
        generic = spec.rstrip() + "\n\n" + interim
    (CONDITIONS / "flagstaff-generic.md").write_text(header(GENERIC_NOTE if not has_generic else "") + generic,
                                                      encoding="utf-8")
    print("wrote raw-xaml.md, flagstaff-keywords.md and flagstaff-generic.md in", CONDITIONS,
          "(interim generic docs)" if not has_generic else "")


def system_prompt(condition: str) -> str:
    return (CONDITIONS / f"{condition}.md").read_text(encoding="utf-8")


def user_prompt(app: str) -> str:
    text = (SPECS / f"{app}.md").read_text(encoding="utf-8")
    text = re.sub(r"<!--.*?-->", "", text, flags=re.S).strip()
    return ("Build this app.\n\n" + text +
            "\n\nOutput all files now, following the output protocol.")


def required_controls(app: str) -> list[list[str]]:
    m = re.search(r"<!--\s*controls:(.*?)-->", (SPECS / f"{app}.md").read_text(encoding="utf-8"), re.S)
    if not m:
        return []
    return [[a.strip() for a in grp.split("|") if a.strip()] for grp in m.group(1).split(";") if grp.strip()]


# ----------------------------------------------------------------------------------------------- model

def load_key(key_file: Path) -> str:
    key = os.environ.get("OPENROUTER_API_KEY", "").strip()
    if not key:
        try:
            raw = key_file.read_text(encoding="utf-8")
        except OSError:
            sys.exit(f"No OPENROUTER_API_KEY and no readable key file at {key_file}")
        for line in raw.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if "=" in line:
                line = line.split("=", 1)[1].strip().strip('"').strip("'")
            key = line
            break
    if not key:
        sys.exit("Empty API key")
    return key


class Model:
    def __init__(self, model: str, key: str, max_tokens: int, temperature: float, reasoning_tokens: int = 0):
        self.model, self._key, self.max_tokens, self.temperature = model, key, max_tokens, temperature
        self.reasoning_tokens = reasoning_tokens
        self.seed = 7

    def chat(self, messages: list[dict]) -> dict:
        body = {
            "model": self.model,
            "messages": messages,
            "temperature": self.temperature,
            "max_tokens": self.max_tokens,
            "seed": self.seed,
            "usage": {"include": True},
        }
        if self.reasoning_tokens:
            # Bound the hidden reasoning so it cannot eat the whole output budget (seen with GLM 5.3).
            body["reasoning"] = {"max_tokens": self.reasoning_tokens}
        headers = {
            "Authorization": f"Bearer {self._key}",
            "Content-Type": "application/json",
            "HTTP-Referer": "https://github.com/maxreichelt/Flagstaff",
            "X-Title": "Flagstaff benchmark",
        }
        delay = 5.0
        last = ""
        for attempt in range(7):
            t0 = time.time()
            try:
                r = requests.post(API_URL, headers=headers, json=body, timeout=900)
            except requests.RequestException as e:
                last = f"network: {type(e).__name__}"
            else:
                if r.status_code == 200:
                    try:
                        data = r.json()
                    except ValueError:
                        last = "invalid JSON body"
                    else:
                        if "choices" in data and data["choices"]:
                            data["_latency"] = time.time() - t0
                            return data
                        last = f"no choices: {str(data.get('error'))[:200]}"
                elif r.status_code in (408, 409, 425, 429) or r.status_code >= 500:
                    last = f"HTTP {r.status_code}"
                else:
                    # Do not echo headers; the body is the provider's error message.
                    raise RuntimeError(f"HTTP {r.status_code}: {r.text[:300]}")
            print(f"    retry {attempt + 1}: {last}; sleeping {delay:.0f}s", flush=True)
            time.sleep(delay)
            delay = min(delay * 2, 120)
        raise RuntimeError(f"model call failed after retries: {last}")


# ----------------------------------------------------------------------------------------------- files

FENCE = re.compile(r"^```[^\n]*\n(.*?)^```[ \t]*$", re.S | re.M)
HEADER = re.compile(r"^\s*(?://|#|<!--|/\*)\s*file:\s*(?P<path>[^\s*>]+?)\s*(?:-->|\*/)?\s*$", re.I)
BEFORE = re.compile(r"(?:file:\s*|\*\*|`)(?P<path>[\w./\\-]+\.(?:cs|xaml|flag|csproj|json|xml|manifest))`?\**:?\s*$",
                    re.I)


def parse_files(text: str) -> tuple[dict[str, str], int]:
    """Return {path: content} from fenced blocks with a `// file:` header, and the count of blocks without one."""
    files: dict[str, str] = {}
    unnamed = 0
    for m in FENCE.finditer(text):
        body = m.group(1)
        lines = body.split("\n")
        path = None
        if lines and (h := HEADER.match(lines[0])):
            path, body = h.group("path"), "\n".join(lines[1:])
        else:
            # Tolerate a path on the line just before the fence.
            prev = text[:m.start()].rstrip("\n").rsplit("\n", 1)[-1]
            if b := BEFORE.search(prev):
                path = b.group("path")
        if not path:
            unnamed += 1
            continue
        files[path.replace("\\", "/").lstrip("./")] = body.rstrip() + "\n"
    if not files:
        # Fallback: `// file:` headers without fences (seen once with GLM 5.3). Split the text at the headers.
        chunks = re.split(r"^[ \t]*//[ \t]*file:[ \t]*(\S+)[ \t]*$", text, flags=re.M)
        for i in range(1, len(chunks) - 1, 2):
            body = re.sub(r"^```[^\n]*$", "", chunks[i + 1], flags=re.M).strip("\n")
            if body.strip():
                files[chunks[i].replace("\\", "/").lstrip("./")] = body.rstrip() + "\n"
    return files, unnamed


def safe_write(project: Path, files: dict[str, str], condition: str) -> tuple[list[str], list[str]]:
    written, rejected = [], []
    root = project.resolve()
    for rel, content in files.items():
        parts = Path(rel).parts
        if (not rel or Path(rel).is_absolute() or ".." in parts
                or (parts and parts[0].lower() in ("bin", "obj", "generated"))
                or Path(rel).name.lower() in ("nuget.config", "directory.build.props", "directory.build.targets",
                                              "directory.packages.props", "global.json")):
            rejected.append(rel)
            continue
        target = (project / rel).resolve()
        if root not in target.parents:
            rejected.append(rel)
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")
        written.append(rel)
    return written, rejected


# ----------------------------------------------------------------------------------------------- projects

def create_project(work: Path, condition: str) -> None:
    if work.exists():
        shutil.rmtree(work)
    if condition == "raw-xaml":
        shutil.copytree(SKELETON_RAW, work)
    else:
        work.parent.mkdir(parents=True, exist_ok=True)
        r = cli("init", APP_NAME, "-o", str(work), "-q")
        if r.returncode != 0:
            raise RuntimeError(f"flagstaff init failed: {r.stderr[-500:]}")
        for rel in ("Flags/home.flag", "Flags/app.flag", "ViewModels/HomeViewModel.cs", "README.md"):
            p = work / rel
            if p.exists():
                p.unlink()
    for name, content in ISOLATION_FILES.items():
        (work / name).write_text(content, encoding="utf-8")
    (work / "nuget.config").write_text(nuget_config(), encoding="utf-8")


MSBUILD_DIAG = re.compile(
    r"^\s*(?P<file>[^\s(][^(]*?)\((?P<line>\d+)(?:,(?P<col>\d+))?(?:[-,\d]*)\)\s*:\s*(?:[\w ]+ )?"
    r"(?P<sev>error|warning) (?P<code>[A-Z]+\d+)\s*:\s*(?P<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")
MSBUILD_DIAG_NOLOC = re.compile(
    r"^\s*(?P<file>(?:[A-Za-z]:)?[^:]*?)?\s*:\s*(?:[\w ]+ )?(?P<sev>error) (?P<code>[A-Z]+\d+)\s*:\s*(?P<msg>.*?)"
    r"(?:\s+\[[^\]]+\])?\s*$")


def rel_to(project: Path, f: str | None) -> str:
    if not f:
        return ""
    try:
        p = Path(f)
        if not p.is_absolute():
            # The CLI reports paths relative to its working directory; MSBuild relative to the project.
            p = Path.cwd() / p if (Path.cwd() / p).exists() else project / p
        return str(p.resolve().relative_to(project.resolve())).replace("\\", "/")
    except (ValueError, OSError):
        return f.replace("\\", "/")


def build_raw(project: Path) -> dict:
    t0 = time.time()
    proc = subprocess.run([str(MSBUILD), f"{APP_NAME}.csproj", "-restore", "-p:Platform=x64",
                           "-p:Configuration=Debug", "-v:m", "-nologo", "-clp:NoSummary"],
                          cwd=project, capture_output=True, text=True, encoding="utf-8", errors="replace",
                          timeout=1200)
    diags, seen = [], set()
    for line in proc.stdout.splitlines():
        m = MSBUILD_DIAG.match(line) or MSBUILD_DIAG_NOLOC.match(line)
        if not m or m.group("sev") != "error":
            continue
        gd = m.groupdict()
        d = {"severity": "error", "code": gd["code"], "message": gd["msg"].strip(),
             "file": rel_to(project, gd.get("file")), "line": int(gd["line"]) if gd.get("line") else None,
             "column": int(gd["col"]) if gd.get("col") else None, "hint": None}
        k = (d["code"], d["file"], d["line"], d["column"], d["message"])
        if k not in seen:
            seen.add(k)
            diags.append(d)
    ok = proc.returncode == 0
    out = {"ok": ok, "diagnostics": diags, "seconds": round(time.time() - t0, 1)}
    if not ok and not diags:
        out["tail"] = proc.stdout[-2500:]
    return out


def build_flagstaff(project: Path) -> dict:
    t0 = time.time()
    proc = cli("build", str(project), "--format", "json", "-q", timeout=1200)
    try:
        data = json.loads(proc.stdout)
    except ValueError:
        return {"ok": False, "diagnostics": [], "seconds": round(time.time() - t0, 1),
                "tail": (proc.stdout + proc.stderr)[-2500:]}
    diags, warns = [], []
    seen = set()
    for d in data.get("diagnostics", []):
        sev = d.get("severity")
        if sev not in ("error", "warning"):
            continue
        d = dict(d)
        d["file"] = rel_to(project, d.get("file"))
        k = (sev, d.get("code"), d["file"], d.get("line"), d.get("column"), d.get("message"))
        if k not in seen:
            seen.add(k)
            (diags if sev == "error" else warns).append(d)
    # Warnings never fail a round (success = the build's own ok, i.e. zero errors); they are recorded so the
    # FS2016/FS2017 skips of the generator (run 3) can be counted.
    out = {"ok": bool(data.get("ok")), "diagnostics": diags, "warnings": warns,
           "seconds": round(time.time() - t0, 1)}
    if not out["ok"] and not diags:
        out["tail"] = (proc.stderr or "")[-2500:] + (data.get("error") or "")
    return out


def check_flagstaff(project: Path) -> dict:
    proc = cli("check", str(project / "Flags"), "--format", "json", "-q")
    try:
        data = json.loads(proc.stdout)
    except ValueError:
        return {}
    stats = data.get("stats") or {}
    warn = {}
    for d in data.get("diagnostics", []):
        if d.get("severity") == "warning":
            warn[d["code"]] = warn.get(d["code"], 0) + 1
    return {"ok": data.get("ok"), "errors": data.get("errors"), "warnings": data.get("warnings"),
            "warning_codes": warn,
            "stats": {k: v for k, v in stats.items() if k not in ("files", "escape")},
            "escape_reasons": [e.get("reasons") for e in stats.get("escape", [])]}


def xml_wellformed_errors(project: Path) -> list[str]:
    errs = []
    for f in project.rglob("*.xaml"):
        if any(p in ("bin", "obj", "Generated") for p in f.relative_to(project).parts):
            continue
        try:
            ET.parse(f)
        except ET.ParseError as e:
            errs.append(f"{f.relative_to(project).as_posix()}: {e}")
    return errs


def count_lines(project: Path, pattern: str, exclude=("bin", "obj", "Generated")) -> int:
    n = 0
    for f in project.rglob(pattern):
        if any(p in exclude for p in f.relative_to(project).parts):
            continue
        for line in f.read_text(encoding="utf-8", errors="replace").splitlines():
            s = line.strip()
            if s and not s.startswith("//"):
                n += 1
    return n


def control_coverage(project: Path, app: str, condition: str) -> dict:
    groups = required_controls(app)
    texts = []
    xaml_root = project / "Generated" if condition != "raw-xaml" else project
    for f in list(xaml_root.rglob("*.xaml")) + list(project.rglob("*.cs")):
        if any(p in ("bin", "obj") for p in f.relative_to(project).parts):
            continue
        texts.append(f.read_text(encoding="utf-8", errors="replace"))
    blob = "\n".join(texts)
    found = []
    for grp in groups:
        hit = any(re.search(rf"<(?:\w+:)?{re.escape(t)}[\s/>.]|\bnew {re.escape(t)}\b", blob) for t in grp)
        found.append(hit)
    return {"groups": ["|".join(g) for g in groups], "found": found,
            "ratio": round(sum(found) / len(found), 3) if found else None}


def format_diags(diags: list[dict], limit: int = 40) -> str:
    lines = []
    for d in diags[:limit]:
        loc = d.get("file") or ""
        if d.get("line"):
            loc += f"({d['line']},{d.get('column') or 1})"
        hint = f" [hint: {d['hint']}]" if d.get("hint") else ""
        lines.append(f"{loc}: error {d.get('code')}: {d.get('message')}{hint}")
    if len(diags) > limit:
        lines.append(f"... and {len(diags) - limit} more errors")
    return "\n".join(lines)


# ----------------------------------------------------------------------------------------------- ledger

def model_dir(model: str, runs_dir: Path | None = None) -> Path:
    """bench/runs/<model> by default; --runs-dir names another folder (relative to bench/runs, or a path)."""
    if runs_dir is not None:
        return runs_dir if runs_dir.is_absolute() else RUNS / runs_dir
    return RUNS / re.sub(r"[^\w.-]", "_", model)


def ledger_costs(mdir: Path) -> list[float]:
    """Every paid call ever made for this model directory (redone and abandoned pairs included)."""
    costs = []
    f = mdir / "ledger.jsonl"
    if f.exists():
        for line in f.read_text(encoding="utf-8").splitlines():
            try:
                costs.append(float(json.loads(line)["cost"]))
            except (ValueError, KeyError):
                pass
    return costs


def spent(mdir: Path) -> float:
    return sum(ledger_costs(mdir))


def ledger_add(mdir: Path, app: str, condition: str, rnd: int, cost: float, usage: dict) -> None:
    with open(mdir / "ledger.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps({"time": time.strftime("%Y-%m-%dT%H:%M:%S"), "app": app, "condition": condition,
                            "round": rnd, "cost": cost, "prompt_tokens": usage.get("prompt_tokens"),
                            "completion_tokens": usage.get("completion_tokens")}) + "\n")


# ----------------------------------------------------------------------------------------------- one pair

def pair_id(app: str, condition: str, sample: int) -> str:
    return f"{app}__{condition}" + (f"__s{sample}" if sample else "")


def run_pair(model: Model, app: str, condition: str, mdir: Path, max_fix_rounds: int, cap: float,
             per_call_estimate: list[float], sample: int = 0) -> dict:
    out_file = mdir / f"{pair_id(app, condition, sample)}.json"
    work = mdir / "work" / app[:2] / (SHORT[condition] + (str(sample) if sample else ""))
    create_project(work, condition)
    messages = [{"role": "system", "content": system_prompt(condition)},
                {"role": "user", "content": user_prompt(app)}]
    sys_chars, user_chars = len(messages[0]["content"]), len(messages[1]["content"])
    model.seed = 7 + sample
    rec = {"app": app, "condition": condition, "sample": sample, "seed": model.seed, "model": model.model,
           "status": "running",
           "rounds": [], "cost": 0.0, "tokens_in": 0, "tokens_out": 0, "reasoning_tokens": 0,
           "started": time.strftime("%Y-%m-%dT%H:%M:%S"), "work": str(work.relative_to(BENCH)).replace("\\", "/"),
           # Timing split (model latency vs build) and prompt sizes; absent in run-1 records.
           "model_seconds": 0.0, "build_seconds": 0.0,
           "system_prompt_chars": sys_chars, "user_prompt_chars": user_chars}
    transcript = mdir / "transcripts" / f"{pair_id(app, condition, sample)}.json"
    transcript.parent.mkdir(parents=True, exist_ok=True)
    t_start = time.time()
    build = None
    all_written: set[str] = set()
    for rnd in range(1, max_fix_rounds + 2):
        already = spent(mdir)
        est = max(per_call_estimate) if per_call_estimate else 0.10
        if already + est > cap:
            rec["status"] = "budget"
            break
        print(f"  round {rnd}: calling model", flush=True)
        resp = model.chat(messages)
        msg = resp["choices"][0]["message"]
        text = msg.get("content") or ""
        finish = resp["choices"][0].get("finish_reason")
        usage = resp.get("usage") or {}
        cost = float(usage.get("cost") or 0.0)
        per_call_estimate.append(cost)
        ledger_add(mdir, app, condition, rnd, cost, usage)
        reasoning = (usage.get("completion_tokens_details") or {}).get("reasoning_tokens") or 0
        rec["cost"] += cost
        rec["tokens_in"] += usage.get("prompt_tokens") or 0
        rec["tokens_out"] += usage.get("completion_tokens") or 0
        rec["reasoning_tokens"] += reasoning
        messages.append({"role": "assistant", "content": text or "(empty answer)"})
        files, unnamed = parse_files(text)
        written, rejected = safe_write(work, files, condition)
        xml_errs = xml_wellformed_errors(work) if condition == "raw-xaml" else []
        all_written.update(written)
        print(f"  round {rnd}: {len(written)} files, {usage.get('completion_tokens')} out tokens "
              f"({reasoning} reasoning), ${cost:.4f}, finish={finish}", flush=True)
        if written:
            build = build_raw(work) if condition == "raw-xaml" else build_flagstaff(work)
        else:
            build = {"ok": False, "diagnostics": [], "seconds": 0.0}
        # A green build of the untouched skeleton is not a success: the model must have written the UI.
        has_ui = (any(f.lower().endswith(".xaml") for f in all_written) if condition == "raw-xaml"
                  else "Flags/app.flag" in all_written and any(f.startswith("Flags/") and f != "Flags/app.flag"
                                                               and f.endswith(".flag") for f in all_written))
        synthetic = None
        if not written:
            synthetic = "NO_FILES"
        elif build["ok"] and not has_ui:
            synthetic = "NO_UI"
            build["ok"] = False
        codes: dict[str, int] = {}
        for d in build["diagnostics"]:
            codes[d.get("code") or "?"] = codes.get(d.get("code") or "?", 0) + 1
        if synthetic:
            codes[synthetic] = 1
        r = {"round": rnd, "ok": build["ok"], "errors": len(build["diagnostics"]) + (1 if synthetic else 0),
             "codes": codes,
             "diagnostics": build["diagnostics"][:60], "xml_errors": xml_errs,
             "files_written": written, "files_rejected": rejected, "unnamed_blocks": unnamed,
             "tokens_in": usage.get("prompt_tokens"), "tokens_out": usage.get("completion_tokens"),
             "reasoning_tokens": reasoning, "cost": cost, "latency": round(resp.get("_latency", 0), 1),
             "model_seconds": round(resp.get("_latency", 0), 1),
             "build_seconds": build["seconds"], "finish_reason": finish}
        rec["model_seconds"] = round(rec["model_seconds"] + r["model_seconds"], 1)
        rec["build_seconds"] = round(rec["build_seconds"] + (build["seconds"] or 0), 1)
        if build.get("warnings") is not None:
            wc: dict[str, int] = {}
            for d in build["warnings"]:
                wc[d.get("code") or "?"] = wc.get(d.get("code") or "?", 0) + 1
            r["warning_codes"] = wc
            # The generator's skip/declare warnings in full (FS20xx); other warnings only as counts.
            r["fs_warnings"] = [{k: d.get(k) for k in ("code", "message", "file", "line")}
                                for d in build["warnings"] if str(d.get("code", "")).startswith("FS20")][:40]
        if condition != "raw-xaml" and written:
            r["check"] = check_flagstaff(work)
            r["fs3001"] = codes.get("FS3001", 0)
        if "tail" in build:
            r["build_tail"] = build["tail"]
        rec["rounds"].append(r)
        print(f"  round {rnd}: build {'OK' if build['ok'] else 'FAILED'} ({r['errors']} errors: "
              f"{', '.join(f'{k}x{v}' for k, v in sorted(codes.items(), key=lambda kv: -kv[1])[:6])})", flush=True)
        transcript.write_text(json.dumps({"messages": messages}, indent=1), encoding="utf-8")
        out_file.write_text(json.dumps(rec, indent=1), encoding="utf-8")
        if build["ok"] or rnd == max_fix_rounds + 1:
            break
        cut = finish == "length"
        if synthetic == "NO_FILES":
            feedback = ("Your answer contained no files" +
                        (" (it was cut off at the output token limit before any file was complete)" if cut else "") +
                        ". Output every file now as a fenced code block whose first line is `// file: <path>`. "
                        "Be concise.")
        elif synthetic == "NO_UI":
            feedback = ("The project builds, but you have not written the app's UI yet "
                        + ("(no .xaml file)." if condition == "raw-xaml" else "(Flags/app.flag and the view files).")
                        + " Output the missing files.")
        else:
            feedback = "The build failed with these errors:\n\n" + format_diags(build["diagnostics"])
            if not build["diagnostics"]:
                feedback += "(no parsed diagnostics; build output tail)\n" + build.get("tail", "")[-2000:]
            if xml_errs:
                feedback += "\n\nXML well-formedness errors:\n" + "\n".join(xml_errs[:20])
            if cut:
                feedback += ("\n\nYour previous answer was cut off at the output token limit; the last file may be "
                             "incomplete or missing.")
            feedback += ("\n\nFix these errors. Output only the changed files, complete, with the same `// file:` "
                         "headers.")
        messages.append({"role": "user", "content": feedback})
    rec["wall_seconds"] = round(time.time() - t_start, 1)
    rounds = rec["rounds"]
    rec["first_build_ok"] = bool(rounds and rounds[0]["ok"])
    rec["success"] = bool(rounds and rounds[-1]["ok"])
    rec["rounds_to_success"] = len(rounds) if rec["success"] else None
    if rec["status"] == "running":
        rec["status"] = "done"
    if condition == "raw-xaml":
        rec["ui_lines"] = count_lines(work, "*.xaml")
        rec["xml_errors_round1"] = len(rounds[0]["xml_errors"]) if rounds else None
    else:
        rec["ui_lines"] = count_lines(work / "Flags", "*.flag")
        last_check = next((r["check"] for r in reversed(rounds) if r.get("check")), {})
        rec["final_stats"] = last_check.get("stats")
        rec["final_warning_codes"] = last_check.get("warning_codes")
    rec["cs_lines"] = count_lines(work, "*.cs")
    rec["coverage"] = control_coverage(work, app, condition)
    out_file.write_text(json.dumps(rec, indent=1), encoding="utf-8")
    return rec


# ----------------------------------------------------------------------------------------------- main

def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model", default="z-ai/glm-5.3")
    ap.add_argument("--cap", type=float, default=25.0, help="stop when the cumulative cost (USD) reaches this")
    ap.add_argument("--apps", default="smoke", help="all | smoke | comma-separated spec ids (01-invoices,...)")
    ap.add_argument("--conditions", default="all", help="all | comma-separated condition names")
    ap.add_argument("--max-fix-rounds", type=int, default=5,
                    help="fix rounds after the first answer (model calls per pair = 1 + this)")
    ap.add_argument("--max-tokens", type=int, default=32000)
    ap.add_argument("--temperature", type=float, default=0.2)
    ap.add_argument("--reasoning-tokens", type=int, default=8000,
                    help="reasoning budget per call for reasoning models (0 = provider default)")
    ap.add_argument("--key-file", type=Path, default=DEFAULT_KEY_FILE)
    ap.add_argument("--samples", type=int, default=1,
                    help="independent samples per app/condition (seed 7, 8, ...); sample 0 is the plain pair file")
    ap.add_argument("--redo", action="store_true", help="rerun pairs that already have a finished record")
    ap.add_argument("--runs-dir", type=Path, default=None,
                    help="output folder, relative to bench/runs (default: the model name, e.g. z-ai_glm-5.3)")
    ap.add_argument("--write-prompts", action="store_true", help="regenerate bench/conditions/flagstaff-*.md and exit")
    ap.add_argument("--no-cli-build", action="store_true", help="do not rebuild the Flagstaff CLI first")
    args = ap.parse_args()

    if not args.no_cli_build:
        r = subprocess.run(["dotnet", "build", str(CLI_PROJECT), "-c", "Release", "-v", "q", "-nologo"],
                           capture_output=True, text=True)
        if r.returncode != 0:
            sys.exit("building the Flagstaff CLI failed:\n" + r.stdout[-2000:])
    if args.write_prompts:
        write_prompts()
        return
    if not list(PACKAGES.glob("Flagstaff.*.nupkg")):
        sys.exit(f"No Flagstaff packages in {PACKAGES}: run dotnet pack (see bench/README.md)")

    all_apps = sorted(p.stem for p in SPECS.glob("*.md"))
    apps = all_apps if args.apps == "all" else SMOKE_APPS if args.apps == "smoke" else args.apps.split(",")
    conds = CONDITION_NAMES if args.conditions == "all" else args.conditions.split(",")
    mdir = model_dir(args.model, args.runs_dir)
    mdir.mkdir(parents=True, exist_ok=True)
    model = Model(args.model, load_key(args.key_file), args.max_tokens, args.temperature, args.reasoning_tokens)
    per_call: list[float] = ledger_costs(mdir)

    # Sample-major, then app, then condition: a budget stop leaves whole samples and comparable pairs.
    for sample in range(args.samples):
      for app in apps:
        for cond in conds:
            out_file = mdir / f"{pair_id(app, cond, sample)}.json"
            if out_file.exists() and not args.redo:
                prev = json.loads(out_file.read_text(encoding="utf-8"))
                # A pair finished with fewer fix rounds than requested is rerun only if it failed.
                if prev.get("status") == "done" and (prev.get("success")
                                                     or prev.get("max_fix_rounds", 0) >= args.max_fix_rounds):
                    print(f"skip {app} / {cond} / sample {sample} (done)")
                    continue
            total = spent(mdir)
            if total >= args.cap:
                print(f"cap reached (${total:.2f} >= ${args.cap:.2f}); stopping")
                return
            print(f"{app} / {cond} / sample {sample}  (spent so far ${total:.4f})", flush=True)
            if out_file.exists():
                out_file.unlink()
            rec = run_pair(model, app, cond, mdir, args.max_fix_rounds, args.cap, per_call, sample)
            rec["max_fix_rounds"] = args.max_fix_rounds
            out_file.write_text(json.dumps(rec, indent=1), encoding="utf-8")
            print(f"  -> {'OK' if rec['success'] else 'FAIL'} after {len(rec['rounds'])} round(s), "
                  f"${rec['cost']:.4f}, {rec['wall_seconds']}s", flush=True)
            if rec["status"] == "budget":
                print("cap reached; stopping")
                return
    print(f"total spent: ${spent(mdir):.4f}")


if __name__ == "__main__":
    main()
