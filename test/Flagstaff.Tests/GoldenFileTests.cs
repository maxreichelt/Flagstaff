using Flagstaff.Compiler;

namespace Flagstaff.Tests;

public class GoldenFileTests
{
    private static string GoldenDir =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "GoldenFiles");

    [Fact]
    public void Compiles_minimal_app_to_xaml()
    {
        var source = File.ReadAllText(Path.Combine(GoldenDir, "minimal_app.flag"));
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile(source, "minimal_app.flag");

        var mainXaml = result.Files.First(f => f.Name == "MainWindow.xaml");
        Assert.Contains("NavigationView", mainXaml.Content);
        Assert.Contains("TestApp", mainXaml.Content);
        Assert.Contains("NavigationViewItem", mainXaml.Content);
        Assert.Contains("Home", mainXaml.Content);

        var scaffold = compiler.GenerateAppScaffold();
        var appXaml = scaffold.Files.First(f => f.Name == "App.xaml");
        Assert.Contains("SystemAccentColor", appXaml.Content);

        var mainCs = result.Files.First(f => f.Name == "MainWindow.xaml.cs");
        Assert.Contains("class MainWindow", mainCs.Content);
        Assert.Contains("InitializeComponent", mainCs.Content);
    }

    [Fact]
    public void Compiles_minimal_view_to_xaml()
    {
        // First compile the app to set the namespace
        var appSource = File.ReadAllText(Path.Combine(GoldenDir, "minimal_app.flag"));
        var compiler = new FlagstaffCompiler();
        compiler.Compile(appSource, "minimal_app.flag");

        var source = File.ReadAllText(Path.Combine(GoldenDir, "minimal_view.flag"));
        var result = compiler.Compile(source, "minimal_view.flag");

        var viewXaml = result.Files.First(f => f.Name == "HomeView.xaml");
        Assert.Contains("<Page", viewXaml.Content);
        Assert.Contains("FlagstaffToolbar", viewXaml.Content);
        Assert.Contains("FlagstaffStatCard", viewXaml.Content);
        Assert.Contains("FlagstaffCard", viewXaml.Content);
        Assert.Contains("ComboBox", viewXaml.Content);
        Assert.Contains("x:Bind", viewXaml.Content);
        Assert.Contains("TextBox", viewXaml.Content);
        Assert.Contains("CheckBox", viewXaml.Content);
        Assert.Contains("InfoBar", viewXaml.Content);

        var viewCs = result.Files.First(f => f.Name == "HomeView.xaml.cs");
        Assert.Contains("class HomeView", viewCs.Content);
        Assert.Contains("HomeViewModel", viewCs.Content);
    }

    [Fact]
    public void Generates_viewmodel_shape()
    {
        var appSource = File.ReadAllText(Path.Combine(GoldenDir, "minimal_app.flag"));
        var compiler = new FlagstaffCompiler();
        compiler.Compile(appSource, "minimal_app.flag");

        var source = File.ReadAllText(Path.Combine(GoldenDir, "minimal_view.flag"));
        var result = compiler.Compile(source, "minimal_view.flag");

        var vmCs = result.Files.First(f => f.Name == "HomeViewModel.g.cs");
        Assert.Contains("partial class HomeViewModel", vmCs.Content);
        Assert.Contains("ObservableObject", vmCs.Content);
        Assert.Contains("[ObservableProperty]", vmCs.Content);
        Assert.Contains("int _year", vmCs.Content);
        Assert.Contains("string _pendingCount", vmCs.Content); // stat without format: is Default string
        Assert.Contains("int _alertCount", vmCs.Content);
        Assert.Contains("decimal _total", vmCs.Content);
        Assert.Contains("decimal _netTotal", vmCs.Content);
        Assert.Contains("string _userName", vmCs.Content);
        Assert.Contains("bool _isActive", vmCs.Content);
        Assert.Contains("bool _hasWarning", vmCs.Content);
        Assert.Contains("[RelayCommand]", vmCs.Content);
        Assert.Contains("Save", vmCs.Content);
    }

    [Fact]
    public void Output_file_count_is_correct()
    {
        var appSource = File.ReadAllText(Path.Combine(GoldenDir, "minimal_app.flag"));
        var compiler = new FlagstaffCompiler();
        var appResult = compiler.Compile(appSource, "minimal_app.flag");
        Assert.Equal(2, appResult.Files.Count); // MainWindow.xaml + MainWindow.xaml.cs

        var source = File.ReadAllText(Path.Combine(GoldenDir, "minimal_view.flag"));
        var viewResult = compiler.Compile(source, "minimal_view.flag");
        Assert.Equal(3, viewResult.Files.Count); // .xaml + .xaml.cs + .g.cs
    }

    [Fact]
    public void Xaml_output_is_deterministic()
    {
        var source = File.ReadAllText(Path.Combine(GoldenDir, "minimal_app.flag"));
        var compiler1 = new FlagstaffCompiler();
        var result1 = compiler1.Compile(source, "minimal_app.flag");

        var compiler2 = new FlagstaffCompiler();
        var result2 = compiler2.Compile(source, "minimal_app.flag");

        Assert.Equal(result1.Files.Count, result2.Files.Count);
        for (int i = 0; i < result1.Files.Count; i++)
        {
            Assert.Equal(result1.Files[i].Name, result2.Files[i].Name);
            Assert.Equal(result1.Files[i].Content, result2.Files[i].Content);
        }
    }
}
