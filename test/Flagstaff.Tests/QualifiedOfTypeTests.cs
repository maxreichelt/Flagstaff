using Flagstaff.Compiler;

namespace Flagstaff.Tests;

public class QualifiedOfTypeTests
{
    private static CompilationResult Compile(string source)
    {
        var compiler = new FlagstaffCompiler();
        return compiler.CompileAll([new SourceFile("v.flag", source)]);
    }

    [Fact]
    public void Of_accepts_a_qualified_type_name_on_third_party_bindings()
    {
        var result = Compile("""
            use "LiveChartsCore.SkiaSharpView.WinUI" as lvc;
            view V {
                bind vm: VM;
                lvc.CartesianChart Series: vm.S of LiveChartsCore.ISeries XAxes: vm.X of LiveChartsCore.Kernel.Sketches.ICartesianAxis;
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var vm = result.Files.Single(f => f.Name == "VM.g.cs").Content;
        Assert.Contains("ObservableCollection<LiveChartsCore.ISeries> _s", vm);
        Assert.Contains("ObservableCollection<LiveChartsCore.Kernel.Sketches.ICartesianAxis> _x", vm);
        Assert.DoesNotContain(result.Files, f => f.Name.EndsWith("ISeries.g.cs") || f.Name.EndsWith("ICartesianAxis.g.cs"));
    }

    [Fact]
    public void Of_with_a_bare_name_still_works()
    {
        var result = Compile("""
            use "Some.Lib" as sl;
            view V { bind vm: VM; sl.Chart Series: vm.S of ISeries; }
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("ObservableCollection<ISeries> _s", result.Files.Single(f => f.Name == "VM.g.cs").Content);
    }
}
