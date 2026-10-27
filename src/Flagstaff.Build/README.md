# Flagstaff

Flagstaff is a terse `.flag` language for native Windows apps. The compiler turns `Flags/*.flag` into WinUI 3 XAML,
code-behind, and a CommunityToolkit.Mvvm scaffold (ViewModel partials with inferred property types, commands,
item types, converters, DI registration, and a NavigationView shell). You write the `.flag` files and the
`On{X}Async` method bodies; everything else is generated on every build.

This package adds the MSBuild integration: referencing it compiles `Flags/*.flag` into `Generated/` before the
XAML compiler runs, and brings in `Flagstaff.Stdlib` for the default styles. It ships the compiler, so no separate
tool install is needed to build.

```xml
<PackageReference Include="Flagstaff" Version="0.3.0-alpha.2" />
```

A new app is quickest to start from the CLI (`dotnet tool install -g Flagstaff.CLI`, then `flagstaff init MyApp`),
which writes a project that already references this package.

Language reference, samples, and the CLI: https://github.com/maxreichelt/Flagstaff
