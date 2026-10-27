# Flagstaff.CLI

The command-line tool for Flagstaff, a terse `.flag` language that compiles to WinUI 3 XAML and a
CommunityToolkit.Mvvm scaffold.

```powershell
dotnet tool install -g Flagstaff.CLI
flagstaff init MyApp          # a buildable WinUI 3 app with Flags/app.flag and Flags/home.flag
cd MyApp
flagstaff check               # diagnostics with codes, locations and hints (also --format json)
flagstaff stubs --update      # adds the On{X}Async methods your .flag files require
flagstaff build               # compiles Flags/, then builds with MSBuild
flagstaff run
```

Other commands: `compile`, `watch`, `new view`, `doctor`, `spec` (the language reference, optionally one
`--section`). Every command supports `--format json` for tools and coding agents.

Repository, language reference, and samples: https://github.com/maxreichelt/Flagstaff
