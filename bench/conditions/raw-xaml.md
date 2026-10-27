You are an expert Windows developer. Write a WinUI 3 app (WindowsAppSDK 1.7, CommunityToolkit.Mvvm 8.4, .NET 10,
unpackaged) in XAML and C#, using MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`,
`ObservableObject`) and `{x:Bind}` bindings.

## The project

The harness provides this skeleton; it builds with MSBuild (`-restore -p:Platform=x64 -p:Configuration=Debug`).
Root namespace: `BenchApp`. The skeleton's `MainWindow` is a placeholder: replace it. You may change `App.xaml` and
`App.xaml.cs`; do not change the `.csproj` or `app.manifest` (no additional NuGet packages are available).

```xml
// file: BenchApp.csproj
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>BenchApp</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWinUI>true</UseWinUI>
    <Platforms>x64;ARM64</Platforms>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <WindowsPackageType>None</WindowsPackageType>
    <EnableMsixTooling>true</EnableMsixTooling>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.7.260224002" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
  </ItemGroup>

</Project>
```

```xml
// file: App.xaml
<?xml version="1.0" encoding="utf-8"?>
<Application
    x:Class="BenchApp.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

```csharp
// file: App.xaml.cs
using Microsoft.UI.Xaml;

namespace BenchApp;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
```

```xml
// file: MainWindow.xaml
<?xml version="1.0" encoding="utf-8"?>
<Window
    x:Class="BenchApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <TextBlock Text="Replace this window." />
    </Grid>
</Window>
```

```csharp
// file: MainWindow.xaml.cs
using Microsoft.UI.Xaml;

namespace BenchApp;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```


You write: `MainWindow.xaml` / `MainWindow.xaml.cs` (navigation shell if the app has several screens), one page per
screen (e.g. `Views/InvoicesPage.xaml` + `.xaml.cs`), ViewModels (e.g. `ViewModels/InvoicesViewModel.cs`), models,
services and converters as needed. Every XAML file must have its code-behind file with the matching `x:Class`.

## Output protocol

- Output every file you create or change as one fenced code block whose **first line** is a header comment with the
  path relative to the project folder: `// file: Views/MainPage.xaml.cs`. Use that `// file:` header for every file
  type (.cs, .xaml, .flag, .csproj); the harness removes the header line before writing the file.
- Each block holds the complete file content. No diffs, no ellipses, no "rest unchanged".
- Do not ask questions; make reasonable assumptions and write the whole app in one answer. Keep prose outside the
  code blocks to a minimum.
- The harness writes your files, builds the project, and sends back the build errors. When you get errors,
  output only the files you change, complete, with the same headers.
