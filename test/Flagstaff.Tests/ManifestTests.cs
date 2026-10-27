using System.Diagnostics;
using System.Text.Json;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>The control manifest (generated from WinUI metadata) and its overlay, as seen through <see cref="ControlManifest"/>.</summary>
public class ManifestTests
{
    private static readonly ControlManifest M = ControlManifest.Default;

    private static ManifestType Type(string name)
    {
        Assert.True(M.TryGetType(name, out var t), $"type {name} missing from the manifest");
        return t;
    }

    private static ManifestProperty Prop(string type, string name)
    {
        Assert.True(M.TryGetProperty(Type(type), name, out var p, out _), $"{type}.{name} missing");
        return p;
    }

    [Fact]
    public void Manifest_HasWinUITypes()
    {
        Assert.True(M.Types.Count() > 300);
        Assert.True(M.TryGetType("Microsoft.UI.Xaml.Controls.Expander", out var full));
        Assert.Same(Type("Expander"), full);
        Assert.False(M.TryGetType("Expandr", out _));
    }

    [Fact]
    public void Expander_IsExpanded_IsTwoWayBool()
    {
        var expander = Type("Expander");
        Assert.True(expander.IsControl);
        Assert.True(expander.IsUIElement);
        Assert.False(expander.IsAbstract);
        Assert.True(M.TryGetProperty(expander, "IsExpanded", out var p, out var declaring));
        Assert.Equal("bool", p.TypeName);
        Assert.True(p.IsDependencyProperty);
        Assert.Same(expander, declaring);
        Assert.Equal(BindingMode.TwoWay, M.DefaultMode(expander, p));
        Assert.Equal(ValueShape.Bool, M.ShapeFor(p));
        Assert.Equal("bool", M.CSharpTypeFor(p));
        // Expander declares no ContentProperty; it inherits Content from ContentControl.
        Assert.Null(expander.ContentProperty);
        Assert.Equal("Content", M.ContentPropertyOf(expander));
        Assert.True(M.HasEvent(expander, "Expanding"));
    }

    [Fact]
    public void ContentProperties_FollowMetadata()
    {
        // TabView's attribute is the WinUI 2 era Windows.UI.Xaml.Markup.ContentPropertyAttribute.
        Assert.Equal("TabItems", M.ContentPropertyOf(Type("TabView")));
        Assert.Equal("Children", M.ContentPropertyOf(Type("StackPanel")));
        Assert.Equal("Children", M.ContentPropertyOf(Type("Grid")));
        Assert.Equal("Content", M.ContentPropertyOf(Type("Button")));
        Assert.Equal("Items", M.ContentPropertyOf(Type("MenuFlyout")));
        Assert.Equal("Child", M.ContentPropertyOf(Type("Border")));
        Assert.Equal("Inlines", M.ContentPropertyOf(Type("TextBlock")));
        Assert.Null(M.ContentPropertyOf(Type("TextBox")));
    }

    [Fact]
    public void GridRow_IsAttachedInt_CheckedByGrid()
    {
        Assert.True(M.TryGetAttachedProperty("Grid.Row", out var row));
        Assert.Equal("Grid", row.Owner);
        Assert.Equal("int", row.TypeName);
        Assert.Equal(AttachedLocality.CheckedByGrid, M.Locality("Grid.Row"));
        Assert.Equal(AttachedLocality.CheckedByGrid, M.Locality("Grid.ColumnSpan"));
        Assert.True(M.TryGetAttachedProperty("Microsoft.UI.Xaml.Controls.Grid.Row", out _));
    }

    [Fact]
    public void Locality_Wildcards_And_Unknown()
    {
        Assert.True(M.TryGetAttachedProperty("AutomationProperties.Name", out var name));
        Assert.Equal("string", name.TypeName);
        Assert.Equal(AttachedLocality.Local, M.Locality("AutomationProperties.Name"));
        Assert.Equal(AttachedLocality.Local, M.Locality("ToolTipService.ToolTip"));
        Assert.Equal(AttachedLocality.Local, M.Locality("Canvas.Left"));
        Assert.Equal(AttachedLocality.Local, M.Locality("RelativePanel.AlignLeftWithPanel"));
        Assert.Equal(AttachedLocality.CheckedByRef, M.Locality("RelativePanel.Below"));
        Assert.Equal(AttachedLocality.Unknown, M.Locality("SomePanel.Weight"));
        Assert.False(M.TryGetAttachedProperty("SomePanel.Weight", out _));
    }

    [Fact]
    public void VisualStateGroups_IsAttached_ViaOverlay()
    {
        // Not exposed as a GetX/SetX pair in WinUI 3 metadata; overlay.json "extraAttached" supplies it.
        Assert.True(M.TryGetAttachedProperty("VisualStateManager.VisualStateGroups", out var p));
        Assert.Contains("VisualStateGroup", p.TypeName);
        Assert.Equal(AttachedLocality.Local, M.Locality("VisualStateManager.VisualStateGroups"));
    }

    [Fact]
    public void Button_HasCommandAndClick()
    {
        var button = Type("Button");
        Assert.True(M.HasCommand(button));
        Assert.True(M.HasEvent(button, "Click"));
        Assert.True(M.TryGetEvent(button, "Click", out var click));
        Assert.Equal("Microsoft.UI.Xaml.RoutedEventArgs", click.ArgsTypeName);
        Assert.True(M.DerivesFrom(button, "ButtonBase"));
        Assert.True(M.DerivesFrom(button, "Microsoft.UI.Xaml.Controls.Control"));
        Assert.False(M.DerivesFrom(button, "Panel"));
        Assert.False(M.HasCommand(Type("TextBlock")));
        Assert.Equal("button", M.KeywordFor(button));
    }

    [Fact]
    public void Slider_Value_IsTwoWayDouble_ViaRangeBase()
    {
        var slider = Type("Slider");
        Assert.True(M.TryGetProperty(slider, "Value", out var value, out var declaring));
        Assert.Equal("double", value.TypeName);
        Assert.Equal("RangeBase", declaring.Name);
        Assert.Equal(BindingMode.TwoWay, M.DefaultMode(slider, value));
        Assert.Equal("double", M.CSharpTypeFor(value));
        Assert.Equal(ValueShape.Double, M.ShapeFor(value));
    }

    [Fact]
    public void DefaultMode_IsOneWay_ForNonInputs()
    {
        var textBox = Type("TextBox");
        Assert.Equal(BindingMode.TwoWay, M.DefaultMode(textBox, Prop("TextBox", "Text")));
        Assert.Equal(BindingMode.OneWay, M.DefaultMode(textBox, Prop("TextBox", "PlaceholderText")));
        // SelectedItem lives on Selector; ListView inherits the TwoWay default.
        Assert.Equal(BindingMode.TwoWay, M.DefaultMode(Type("ListView"), Prop("ListView", "SelectedItem")));
    }

    [Fact]
    public void KeywordFor_FromOverlay()
    {
        Assert.Equal("textbox", M.KeywordFor(Type("TextBox")));
        Assert.Equal("toggle", M.KeywordFor(Type("ToggleSwitch")));
        Assert.Null(M.KeywordFor(Type("Expander")));
    }

    [Fact]
    public void SuggestTypes_FindsCloseNames()
    {
        Assert.Contains("Expander", M.SuggestTypes("Expandr"));
        Assert.Contains("TabView", M.SuggestTypes("Tabview"));
        Assert.Empty(M.SuggestTypes("Zzzzzzzzzz"));
        Assert.True(M.SuggestTypes("Grid", max: 2).Count <= 2);
    }

    [Fact]
    public void SuggestProperties_WalksBaseChain()
    {
        Assert.Contains("IsExpanded", M.SuggestProperties(Type("Expander"), "IsExpandd"));
        Assert.Contains("Margin", M.SuggestProperties(Type("Expander"), "Margn"));
    }

    [Fact]
    public void ReferenceEmit_FromOverlay()
    {
        Assert.Equal(ReferenceEmit.TargetPath, M.ReferenceEmitFor("Setter.Target"));
        Assert.Equal(ReferenceEmit.Name, M.ReferenceEmitFor("Storyboard.TargetName"));
        Assert.Equal(ReferenceEmit.Name, M.ReferenceEmitFor("RelativePanel.Below"));
        Assert.Equal(ReferenceEmit.Name, M.ReferenceEmitFor("MenuFlyout.Target")); // via FlyoutBase.Target
        Assert.Equal(ReferenceEmit.None, M.ReferenceEmitFor("Button.Content"));
        Assert.Equal(ValueShape.Reference, M.ShapeFor(Prop("Setter", "Target")));
    }

    [Fact]
    public void CSharpTypeFor_MapsManifestTypes()
    {
        var orientation = Prop("StackPanel", "Orientation");
        Assert.NotNull(orientation.EnumMembers);
        Assert.Contains("Horizontal", orientation.EnumMembers!);
        Assert.Equal("Microsoft.UI.Xaml.Controls.Orientation", M.CSharpTypeFor(orientation));
        Assert.Equal(ValueShape.Enum, M.ShapeFor(orientation));

        Assert.Equal("Microsoft.UI.Xaml.Visibility", M.CSharpTypeFor(Prop("Button", "Visibility")));
        Assert.Equal("bool?", M.CSharpTypeFor(Prop("CheckBox", "IsChecked")));
        Assert.Equal("DateTimeOffset?", M.CSharpTypeFor(Prop("CalendarDatePicker", "Date")));
        Assert.Equal("TimeSpan", M.CSharpTypeFor(Prop("TimePicker", "Time")));
        Assert.Equal("string", M.CSharpTypeFor(Prop("TextBox", "Text")));
        Assert.Equal("int", M.CSharpTypeFor(Prop("ListView", "SelectedIndex")));
        Assert.Equal("object", M.CSharpTypeFor(Prop("ListView", "SelectedItem")));
        Assert.Equal("System.Collections.ObjectModel.ObservableCollection<object>", M.CSharpTypeFor(Prop("TabView", "TabItems")));
        Assert.Equal("string", M.CSharpTypeFor(Prop("HyperlinkButton", "NavigateUri")));
    }

    [Fact]
    public void ShapeFor_ValueShapes()
    {
        Assert.Equal(ValueShape.Thickness, M.ShapeFor(Prop("Button", "Margin")));
        Assert.Equal(ValueShape.CornerRadius, M.ShapeFor(Prop("Button", "CornerRadius")));
        Assert.Equal(ValueShape.Brush, M.ShapeFor(Prop("Button", "Background")));
        Assert.Equal(ValueShape.String, M.ShapeFor(Prop("TextBlock", "Text")));
        Assert.Equal(ValueShape.Int, M.ShapeFor(Prop("ListView", "SelectedIndex")));
        Assert.Equal(ValueShape.Object, M.ShapeFor(Prop("Button", "Content")));
        Assert.Equal(ValueShape.Collection, M.ShapeFor(Prop("StackPanel", "Children")));
        Assert.Equal(ValueShape.GridLength, M.ShapeFor(Prop("ColumnDefinition", "Width")));
        Assert.Equal(ValueShape.Uri, M.ShapeFor(Prop("Image", "Source")));
        Assert.Equal(ValueShape.Icon, M.ShapeFor(Prop("AppBarButton", "Icon")));
        Assert.Equal(ValueShape.Color, M.ShapeFor(Prop("ColorPicker", "Color")));
    }

    [Fact]
    public void Properties_CollectionAndReadOnlyFlags()
    {
        var children = Prop("StackPanel", "Children");
        Assert.True(children.IsCollection);
        Assert.False(children.IsReadOnly);
        Assert.True(Prop("Button", "ActualWidth").IsReadOnly);
        Assert.False(Prop("Button", "Width").IsReadOnly);
        Assert.True(Type("Control").IsAbstract); // composable base: no public constructor
    }

    [Fact]
    public void ValueTypes_AreKnown()
    {
        Assert.True(M.TryGetValueType("Symbol", out var symbol));
        Assert.True(symbol.IsEnum);
        Assert.Contains("Save", symbol.EnumMembers!);
        foreach (var name in new[] { "Orientation", "Visibility", "Thickness", "CornerRadius", "GridLength" })
            Assert.True(M.TryGetValueType(name, out _), name);
    }

    /// <summary>Every name the overlay mentions must exist in the generated manifest (catches WinUI renames).</summary>
    [Fact]
    public void Overlay_NamesResolveAgainstManifest()
    {
        using var overlay = JsonDocument.Parse(ReadResource("overlay.json"));
        var root = overlay.RootElement;
        var problems = new List<string>();

        foreach (var entry in root.GetProperty("input").EnumerateObject())
        {
            if (!M.TryGetType(entry.Name, out var type)) { problems.Add($"input: type {entry.Name}"); continue; }
            foreach (var prop in entry.Value.EnumerateArray().Select(p => p.GetString()!))
                if (!M.TryGetProperty(type, prop, out _, out _)) problems.Add($"input: {entry.Name}.{prop}");
        }
        foreach (var entry in root.GetProperty("keywordFor").EnumerateObject())
        {
            if (!M.TryGetType(entry.Name, out _)) problems.Add($"keywordFor: type {entry.Name}");
            if (!ElementSchema.All.ContainsKey(entry.Value.GetString()!)) problems.Add($"keywordFor: keyword {entry.Value.GetString()}");
        }
        foreach (var entry in root.GetProperty("attachedLocality").EnumerateObject())
        {
            if (entry.Name.EndsWith(".*", StringComparison.Ordinal))
            {
                var owner = entry.Name[..^2];
                if (!M.TryGetType(owner, out _) && !M.AttachedProperties.Any(a => a.Owner == owner))
                    problems.Add($"attachedLocality: owner {owner}");
            }
            else if (!M.TryGetAttachedProperty(entry.Name, out _)) problems.Add($"attachedLocality: {entry.Name}");
        }
        foreach (var entry in root.GetProperty("referenceProperties").EnumerateObject())
        {
            var dot = entry.Name.LastIndexOf('.');
            var (owner, member) = (entry.Name[..dot], entry.Name[(dot + 1)..]);
            var ok = M.TryGetAttachedProperty(entry.Name, out _)
                     || (M.TryGetType(owner, out var t) && M.TryGetProperty(t, member, out _, out _));
            if (!ok) problems.Add($"referenceProperties: {entry.Name}");
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void Load_IsFast()
    {
        var manifest = ReadResource("winui-manifest.json");
        var overlay = ReadResource("overlay.json");
        var sw = Stopwatch.StartNew();
        var loaded = ControlManifest.Load(manifest, overlay);
        sw.Stop();
        Assert.True(loaded.TryGetType("Expander", out _));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"manifest load took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The committed manifest matches what tools/ManifestGen generates from the pinned WindowsAppSDK. Runs the
    /// tool's <c>--check</c> when the package's .winmd files are in the NuGet cache (CI also runs it as a step).
    /// </summary>
    [Fact]
    public async Task ManifestGen_Check_PassesAgainstCommittedFile()
    {
        var root = FindRepoRoot();
        if (root is null || !WindowsAppSdkInCache(root)) return;

        var psi = new ProcessStartInfo("dotnet", ["run", "--project", Path.Combine(root, "tools", "ManifestGen"), "--", "--check"])
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        await p.WaitForExitAsync(cts.Token);
        Assert.True(p.ExitCode == 0, $"ManifestGen --check failed:\n{await stdout}\n{await stderr}");
    }

    private static string ReadResource(string name)
    {
        using var s = typeof(ControlManifest).Assembly.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"resource {name} missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Flagstaff.slnx")) && Directory.Exists(Path.Combine(dir.FullName, "tools", "ManifestGen")))
                return dir.FullName;
        return null;
    }

    private static bool WindowsAppSdkInCache(string root)
    {
        var props = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));
        var m = System.Text.RegularExpressions.Regex.Match(props, "\"Microsoft\\.WindowsAppSDK\" Version=\"([^\"]+)\"");
        if (!m.Success) return false;
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(cache))
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return File.Exists(Path.Combine(cache, "microsoft.windowsappsdk", m.Groups[1].Value.ToLowerInvariant(), "lib", "uap10.0", "Microsoft.UI.Xaml.winmd"));
    }
}
