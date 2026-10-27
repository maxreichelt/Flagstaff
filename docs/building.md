# Building Flagstaff apps

This page covers building WinUI 3 apps that use Flagstaff, such as `samples/TaskTrack`. To build the compiler itself, see [CONTRIBUTING.md](../CONTRIBUTING.md).

## The rule: build with MSBuild.exe, not `dotnet build`

**Build WinUI 3 projects with Visual Studio's `MSBuild.exe`, or with `flagstaff build`, which calls it for you.**

`dotnet build` uses Core MSBuild, which runs the WinUI `XamlCompiler.exe` out of process. When the XAML compiler finds an error (an `x:Bind` path that doesn't exist, a type mismatch), it writes the error to `obj/**/output.json`. Core MSBuild does not surface that file. All you see is:

```text
error MSB3073: The command "XamlCompiler.exe ..." exited with code 1.
```

and nothing else. Visual Studio's MSBuild runs the same compiler and reports the real `WMC1121`-style errors with file and line. The Flagstaff MSBuild targets warn when they detect Core MSBuild.

If you are stuck with `dotnet build`, the real errors are still on disk:

```powershell
Get-ChildItem obj -Recurse -Filter output.json | Get-Content
```

## Prerequisites

| Component | Why |
|---|---|
| Visual Studio 2022 or later (any edition, including Build Tools) | Provides `MSBuild.exe` with full XAML compiler support |
| **.NET desktop development** workload | .NET and WinUI build tooling |
| .NET 10 SDK | Runtime and SDK |
| Windows App SDK C# templates (optional) | For creating projects in Visual Studio |

`flagstaff doctor` checks all of these and prints what is missing.

## Building

### With the CLI (recommended)

```powershell
cd samples\TaskTrack
flagstaff build                                  # Debug, x64
flagstaff build --configuration Release --platform ARM64
flagstaff build --format json                    # machine-readable diagnostics
```

`flagstaff build` finds `MSBuild.exe` with vswhere, restores, builds, and reports both Flagstaff and XAML/C# errors.

### With MSBuild directly

Find MSBuild with vswhere (in PowerShell):

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
& $msbuild samples\TaskTrack\TaskTrack.csproj /restore /p:Platform=x64 /p:Configuration=Debug /v:minimal
```

In a **Developer Command Prompt for VS**, `msbuild` is already on `PATH`:

```cmd
msbuild samples\TaskTrack\TaskTrack.csproj /restore /p:Platform=x64 /p:Configuration=Debug
```

## Key properties

| Property | Values | Notes |
|---|---|---|
| `Platform` | `x64`, `ARM64` (`x86` also works) | Required. WinUI 3 does not build as `AnyCPU`. |
| `Configuration` | `Debug`, `Release` | |
| `WindowsPackageType` | `None` | Unpackaged app (no MSIX) |
| `EnableMsixTooling` | `true` | Required even for unpackaged apps because it enables PRI generation. Flagstaff's props set it by default. |
| `UseWinUI` | `true` | Enables the WinUI XAML pipeline |
| `FlagstaffInputDir` | default `Flags` | Where `.flag` files are read from |
| `FlagstaffOutputDir` | default `Generated` | Where generated files are written |
| `FlagstaffNamespace` | default `$(RootNamespace)` | Root namespace of the generated code |
| `FlagstaffCliPath` | default `flagstaff` | Path to the CLI if it is not on `PATH` |

## What the build does

1. **Restore** pulls the Windows App SDK, CommunityToolkit.Mvvm, and Flagstaff packages.
2. **FlagstaffCompile** runs `flagstaff compile Flags -o Generated --namespace <ns>` before XAML compilation and marks `Generated/App.xaml` as the `ApplicationDefinition`.
3. **MarkupCompilePass1** turns `.xaml` into `.g.cs` / `.g.i.cs`.
4. **CoreCompile** compiles all C#: hand-written, generated, and XAML code-behind.
5. **MarkupCompilePass2** validates `x:Bind` expressions against the compiled types and writes `.xbf`.
6. **PRI generation** writes `resources.pri`.

## Clean build

```powershell
& $msbuild samples\TaskTrack\TaskTrack.csproj /t:Clean /p:Platform=x64 /p:Configuration=Debug
Remove-Item -Recurse -Force samples\TaskTrack\Generated
& $msbuild samples\TaskTrack\TaskTrack.csproj /restore /p:Platform=x64 /p:Configuration=Debug
```

`flagstaff compile --clean` empties the output directory before regenerating.

## Running

```powershell
flagstaff run                  # builds first; --no-build to skip
# or run the exe directly:
.\samples\TaskTrack\bin\x64\Debug\net10.0-windows10.0.22621.0\TaskTrack.exe
```

The target framework folder depends on the project's `TargetFramework`. Setting `FLAGSTAFF_SMOKE=1` makes the sample navigate through every view and exit, which is useful in CI.

`flagstaff watch` recompiles `.flag` files on save. `flagstaff watch --run` also restarts the app.

## Debugging in Visual Studio

Open the solution or `.csproj`, set the platform to **x64** (or ARM64), and press F5. Visual Studio runs the same MSBuild targets, so `Generated/` stays up to date. Breakpoints work in both the generated `.g.cs` files and your `ViewModels/*.cs`. To change the UI, edit the `.flag` file, not the generated XAML.

XAML Hot Reload applies to the generated `.xaml` files, but the next build overwrites those changes, so copy anything worth keeping back into the `.flag` source.
