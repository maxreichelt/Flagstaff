# Contributing to Flagstaff

Thanks for helping. Flagstaff is small on purpose. Every keyword has to earn its place by making WinUI 3 apps easier and more reliable to write, for people and for LLMs.

Open an issue before starting large changes. New keywords or syntax go through the **Language proposal** template.

## Development setup

- Windows 10/11
- .NET 10 SDK (`global.json` pins the feature band)
- Visual Studio 2022+ with the **.NET desktop development** workload. This is only needed to build the WinUI sample and Stdlib; see [docs/building.md](docs/building.md).

```powershell
git clone https://github.com/maxreichelt/Flagstaff
cd Flagstaff
dotnet build
dotnet test
```

Run the CLI from source:

```powershell
dotnet run --project src\Flagstaff.CLI -- check samples\TaskTrack\Flags --format json
dotnet run --project src\Flagstaff.CLI -- compile path\to\Flags -o $env:TEMP\fs-out
```

`Directory.Build.props` sets `TreatWarningsAsErrors`, so analyzer warnings fail the build.

## Repository layout

| Path | What |
|---|---|
| `src/Flagstaff.Compiler` | Lexer, parser, schema, validator, type inference, generators, linter. Read [its README](src/Flagstaff.Compiler/README.md) first. |
| `src/Flagstaff.CLI` | The `flagstaff` .NET tool |
| `src/Flagstaff.Stdlib` | Default styles and the MSBuild `build/` props and targets |
| `test/Flagstaff.Tests` | xUnit tests and Verify snapshots (`Cases/`, `Snapshots/`) |
| `samples/TaskTrack` | End-to-end sample app |
| `LANG.md`, `docs/grammar.ebnf` | Language reference and grammar |

## Tests and snapshots

```powershell
dotnet test                                              # everything
dotnet test --filter "FullyQualifiedName~SnapshotTests"  # snapshots only
```

Each directory under `test/Flagstaff.Tests/Cases/` is one compilation: an `app.flag` and at least one view. Every generated file, plus the case's diagnostics, is compared with `test/Flagstaff.Tests/Snapshots/<case>.<file>.verified.<ext>`.

When output changes:

1. Run `dotnet test`. Mismatches write `*.received.*` files next to the `*.verified.*` files.
2. **Review the diff** (`git diff --no-index X.verified.xaml X.received.xaml`, or your diff tool). Every changed line should be intended.
3. Accept a snapshot by renaming `*.received.*` to `*.verified.*`, overwriting the old one. New cases start with no `.verified` file, so the first run always produces `.received` files.
4. Re-run `dotnet test` until it passes, then commit the `.verified` files. `*.received.*` is gitignored.

## Adding a keyword

1. **Schema.** Register the keyword in a new partial file `src/Flagstaff.Compiler/ElementSchema.<Area>.cs` with a `[SchemaRegistration]` method (`Register`, `Extend`, `Reshape`, `RegisterUniversal`). Don't edit the core list.
2. **Emitter.** Add a method to the matching `XamlGenerator.<Area>.cs`, or to a new partial for a new area, and add one `case` to the `EmitElement` switch in `XamlGenerator.cs`. Use `Bind(...)`, `Conditions.VisibilityAttr(el)`, and `Conditions.EnabledAttr(el)` so the keyword works inside item templates and supports `when`/`enabled`.
3. **Type inference.** If the keyword binds `vm.X`, teach `TypeInference` its type.
4. **Snapshot case.** Add or extend a directory under `test/Flagstaff.Tests/Cases/` and accept the snapshots (see above). Add a diagnostics case for misuse.
5. **Docs.** Add an entry to `LANG.md` (syntax, emitted control, inferred types, and a common-mistake line if one is likely) and to `docs/grammar.ebnf` if the grammar changes.
6. **Changelog.** Add a line under `## [Unreleased]` in `CHANGELOG.md`.

Ask whether the keyword handles **context-dependent** XAML (binding roots, templates, grid placement, code-behind wiring). Self-contained controls are usually better served by `xaml { }`.

## Diagnostics

Every user-facing error has a stable code, a precise location, and a hint whenever a fix can be suggested.

| Range | Stage |
|---|---|
| FS1000–FS1099 | Lexer, parser, schema validation |
| FS2000–FS2099 | Semantic analysis and types |
| FS3000–FS3099 | Lint of generated XAML |

The full table is in [src/Flagstaff.Compiler/README.md](src/Flagstaff.Compiler/README.md#diagnostic-codes). New codes take the next free number in their range and get a row in that table. Never reuse a retired code.

## Commits

- Write the subject in the imperative, at most about 72 characters ("Add list keyword", "Fix x:DataType on nested templates").
- Keep each commit to one logical change, and commit snapshot updates together with the change that caused them.
- Pure moves and refactors go in their own commit.
- If an AI assistant co-wrote the change, credit it with a `Co-Authored-By:` trailer.

## Pull request checklist

- [ ] `dotnet build` and `dotnet test` pass locally.
- [ ] New or changed output is covered by a snapshot case, and the `.verified` files were reviewed.
- [ ] New diagnostics have a code, a hint, and a test.
- [ ] `LANG.md` and `CHANGELOG.md` are updated for user-visible changes.
- [ ] The sample still builds with `flagstaff build` (for generator or MSBuild changes).
- [ ] Nothing under `Generated/` or `*.received.*` is committed.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).

## Releasing

Pushing a tag `v<semver>` (for example `v0.3.0-alpha.1`) runs `.github/workflows/release.yml`: it packs
`Flagstaff`, `Flagstaff.CLI` and `Flagstaff.Stdlib` at that version, publishes them to nuget.org, and creates a
GitHub release with the CLI zip. Publishing uses [nuget.org trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
(GitHub OIDC), so no API key is stored. One-time setup:

1. On nuget.org, open your username menu → **Trusted Publishing** → add a policy with repository owner
   `maxreichelt`, repository `Flagstaff`, workflow file `release.yml`, no environment. A new policy is active for
   7 days and becomes permanent after its first successful publish.
2. Set the repository variable `NUGET_USER` to the nuget.org profile name (not the email):
   `gh variable set NUGET_USER --body <profile-name>`.
3. Prove the pipeline without publishing: `gh workflow run release.yml --ref main` runs the same workflow as a dry run
   (pack, zip, OIDC login; packages uploaded as a run artifact; no push, no release).
