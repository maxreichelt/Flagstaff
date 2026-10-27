using Flagstaff.Compiler;

namespace Flagstaff.Cli;

/// <summary>
/// Hints for C# that names a type WinUI 3 does not have, or one from a namespace <c>Generated/GlobalUsings.g.cs</c>
/// leaves out on purpose (CS0246, CS0234, CS0103 in the hand-written half): the UWP-only controls share FS1030's table
/// (<see cref="GenericValidator.UwpOnlyControl"/>), so C# gets the same "WinUI 3 has no X; use Y" as a <c>.flag</c> file.
/// </summary>
internal static class WinUiTypeHints
{
    private const string InkHint = "WinUI 3 (Windows App SDK) has no inking API: InkCanvas, InkPresenter and Windows.UI.Input.Inking are UWP-only";

    /// <summary>Microsoft.UI.Xaml.Shapes: not a global using, because its <c>Path</c> clashes with <c>System.IO.Path</c>.</summary>
    private static readonly HashSet<string> Shapes = new(StringComparer.Ordinal)
    {
        "Shape", "Polyline", "Polygon", "Line", "Ellipse", "Rectangle", "Path",
    };

    /// <summary>Common types of namespaces the global usings leave out (each would make a common name ambiguous).</summary>
    private static readonly Dictionary<string, (string Namespace, string Why)> ExcludedNamespaceTypes = new(StringComparer.Ordinal)
    {
        ["StorageFile"] = ("Windows.Storage", "its FileAttributes clashes with System.IO"),
        ["StorageFolder"] = ("Windows.Storage", "its FileAttributes clashes with System.IO"),
        ["FileAccessMode"] = ("Windows.Storage", "its FileAttributes clashes with System.IO"),
        ["FileIO"] = ("Windows.Storage", "its FileAttributes clashes with System.IO"),
        ["ApplicationData"] = ("Windows.Storage", "its FileAttributes clashes with System.IO"),
        ["KnownFolders"] = ("Windows.Storage", "its FileAttributes clashes with System.IO"),
        ["CreationCollisionOption"] = ("Windows.Storage", "its FileAttributes clashes with System.IO"),
        ["NameCollisionOption"] = ("Windows.Storage", "its FileAttributes clashes with System.IO"),
        ["IStorageFile"] = ("Windows.Storage", "its FileAttributes clashes with System.IO"),
        ["IRandomAccessStream"] = ("Windows.Storage.Streams", "its Buffer clashes with System.Buffer"),
        ["InMemoryRandomAccessStream"] = ("Windows.Storage.Streams", "its Buffer clashes with System.Buffer"),
        ["RandomAccessStreamReference"] = ("Windows.Storage.Streams", "its Buffer clashes with System.Buffer"),
        ["DataReader"] = ("Windows.Storage.Streams", "its Buffer clashes with System.Buffer"),
        ["DataWriter"] = ("Windows.Storage.Streams", "its Buffer clashes with System.Buffer"),
        ["VirtualKey"] = ("Windows.System", "its DispatcherQueue clashes with Microsoft.UI.Dispatching"),
        ["VirtualKeyModifiers"] = ("Windows.System", "its DispatcherQueue clashes with Microsoft.UI.Dispatching"),
        ["Launcher"] = ("Windows.System", "its DispatcherQueue clashes with Microsoft.UI.Dispatching"),
        ["DataPackage"] = ("Windows.ApplicationModel.DataTransfer", "it is rarely needed"),
        ["Clipboard"] = ("Windows.ApplicationModel.DataTransfer", "it is rarely needed"),
    };

    /// <summary>The hint for a type name C# could not resolve, or null. <paramref name="inNamespace"/> is CS0234's namespace.</summary>
    public static string? ForTypeName(string name, string? inNamespace = null)
    {
        if (inNamespace is not null && Uwp(inNamespace) is { } uwpNamespace)
            return uwpNamespace.Replace("{name}", name, StringComparison.Ordinal);
        if (GenericValidator.UwpOnlyControl(name) is { } replacement)
            return $"WinUI 3 (Windows App SDK) has no {name} (it is a UWP control, as FS1030 says in a .flag file): {replacement}";
        if (name is "Ink" or "Inking" || name.StartsWith("Ink", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3]))
            return $"{InkHint}; {GenericValidator.UwpOnlyControls["InkCanvas"]}";
        if (Shapes.Contains(name))
        {
            return $"{name} is Microsoft.UI.Xaml.Shapes.{name}: add 'using Microsoft.UI.Xaml.Shapes;' to this file (it is not a global using "
                   + "because its Path clashes with System.IO.Path; with both, write Microsoft.UI.Xaml.Shapes.Path)";
        }
        if (name == "Canvas" && inNamespace is not null && inNamespace.StartsWith("Microsoft.Graphics", StringComparison.Ordinal))
        {
            return "Microsoft.Graphics.Canvas is Win2D, which the project does not reference; the XAML Canvas is Microsoft.UI.Xaml.Controls.Canvas "
                   + "(already imported by Generated/GlobalUsings.g.cs)";
        }
        if (name is "Colors" or "ColorHelper" && inNamespace == "Windows.UI")
            return $"use Microsoft.UI.{name} (Microsoft.UI is imported by Generated/GlobalUsings.g.cs); Color is aliased to Windows.UI.Color";
        if (ExcludedNamespaceTypes.TryGetValue(name, out var excluded))
            return $"{name} is in {excluded.Namespace}: add 'using {excluded.Namespace};' to this file (not a global using: {excluded.Why})";
        return null;
    }

    /// <summary>CS0234 under <c>Windows.UI.Xaml</c>: the UWP namespace; WinUI 3 is <c>Microsoft.UI.Xaml</c>.</summary>
    private static string? Uwp(string ns) =>
        ns == "Windows.UI.Xaml" || ns.StartsWith("Windows.UI.Xaml.", StringComparison.Ordinal)
            ? $"{ns} is UWP; WinUI 3 uses {ns.Replace("Windows.UI.Xaml", "Microsoft.UI.Xaml", StringComparison.Ordinal)}.{{name}} "
              + "(Microsoft.UI.Xaml and its Controls, Data, Input, Media and Navigation namespaces are imported by Generated/GlobalUsings.g.cs; "
              + "Shapes needs 'using Microsoft.UI.Xaml.Shapes;')"
            : null;

    /// <summary>
    /// CS0115 on <c>OnPointerPressed(PointerRoutedEventArgs)</c> and the like: only <c>Control</c> subclasses have these
    /// virtuals; a <c>Canvas</c>/<c>Panel</c>/<c>Grid</c> subclass subscribes to the event.
    /// </summary>
    public static string ForMissingOverride(string method, string argsType) =>
        $"{method} is a virtual of Control; a Canvas, Panel, Grid or Border subclass has none: subscribe to the event instead "
        + $"({method[2..]} += (sender, e) => ...;, with {argsType}) in the constructor or OnViewCreated, or derive from Control/UserControl";
}
