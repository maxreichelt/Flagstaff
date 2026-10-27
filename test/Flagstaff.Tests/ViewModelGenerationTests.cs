using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// The generated ViewModel partial and item classes: async commands that delegate to <c>On{X}Async</c>, property
/// types, and item types (<c>of T</c>) emitted once per project with the scaffold.
/// </summary>
public class ViewModelGenerationTests
{
    private static CompilationResult CompileView(string viewSource, string appName = "TestApp")
    {
        var compiler = new FlagstaffCompiler { AppNamespace = appName };
        return compiler.Compile(viewSource, "test.flag");
    }

    /// <summary>Compiles one view, then the scaffold (item types such as <c>Order.g.cs</c> are emitted there).</summary>
    private static CompilationResult CompileViewWithScaffold(string viewSource, string appName = "TestApp")
    {
        var compiler = new FlagstaffCompiler { AppNamespace = appName };
        var result = compiler.Compile(viewSource, "test.flag");
        result.Files.AddRange(compiler.GenerateAppScaffold().Files);
        return result;
    }

    // ── Commands ───────────────────────────────────────────────────

    [Fact]
    public void Commands_are_generated_as_async()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                button "Save" -> vm.SaveCommand;
            }
            """);

        var vmCs = result.Files.First(f => f.Name == "VM.g.cs");
        Assert.Contains("async Task", vmCs.Content);
        Assert.Contains("SaveAsync", vmCs.Content);
        Assert.Contains("using System.Threading.Tasks;", vmCs.Content);
        Assert.DoesNotContain("private partial void", vmCs.Content);
    }

    [Fact]
    public void Commands_delegate_to_on_method()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                button "Save" -> vm.SaveCommand;
                button "Delete" -> vm.DeleteCommand;
            }
            """);

        var vmCs = result.Files.First(f => f.Name == "VM.g.cs");
        Assert.Contains("await OnSaveAsync()", vmCs.Content);
        Assert.Contains("await OnDeleteAsync()", vmCs.Content);
        Assert.DoesNotContain("{ }", vmCs.Content);
    }

    // ── Property types ─────────────────────────────────────────────

    [Fact]
    public void Badge_binding_works_for_string_values()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                badge vm.StatusDisplay;
            }
            """);

        var vmCs = result.Files.First(f => f.Name == "VM.g.cs");
        Assert.Contains("string _statusDisplay", vmCs.Content);
    }

    [Fact]
    public void Toggle_infers_bool_type_in_viewmodel()
    {
        var result = CompileView("""
            view SettingsView {
                bind vm: SettingsViewModel;
                toggle "Batch Mode" vm.IsBatchMode;
            }
            """);
        var vmCs = result.Files.First(f => f.Name == "SettingsViewModel.g.cs").Content;
        Assert.Contains("bool", vmCs);
        Assert.Contains("_isBatchMode", vmCs);
    }

    // ── Item types ─────────────────────────────────────────────────

    [Fact]
    public void Table_with_of_type_generates_item_class()
    {
        var result = CompileViewWithScaffold("""
            view V {
                bind vm: VM;
                table items: vm.Expenses of Expense {
                    col "Vendor" bind: item.Vendor;
                    col "Amount" bind: item.Amount format: currency;
                    col "Date" bind: item.ExpenseDate format: date;
                    col "Status" bind: item.StatusDisplay;
                }
            }
            """);

        var itemType = result.Files.FirstOrDefault(f => f.Name == "Expense.g.cs");
        Assert.NotNull(itemType);
        Assert.Contains("public partial class Expense", itemType.Content);
        Assert.Contains("public string Vendor { get; set; }", itemType.Content);
        Assert.Contains("public decimal Amount { get; set; }", itemType.Content);
        Assert.Contains("public DateTimeOffset? ExpenseDate { get; set; }", itemType.Content);
        Assert.Contains("public string StatusDisplay { get; set; }", itemType.Content);
    }

    [Fact]
    public void Item_type_generated_in_viewmodels_namespace()
    {
        var result = CompileViewWithScaffold("""
            view V {
                bind vm: VM;
                table items: vm.Items of MyRow {
                    col "Name" bind: item.Name;
                }
            }
            """);

        var itemType = result.Files.First(f => f.Name == "MyRow.g.cs");
        Assert.Contains("namespace TestApp.ViewModels;", itemType.Content);
    }

    [Fact]
    public void Duplicate_table_types_are_deduplicated()
    {
        var result = CompileViewWithScaffold("""
            view V {
                bind vm: VM;
                table items: vm.ListA of Shared {
                    col "A" bind: item.A;
                }
                table items: vm.ListB of Shared {
                    col "B" bind: item.B;
                }
            }
            """);

        var itemFiles = result.Files.Where(f => f.Name == "Shared.g.cs").ToList();
        Assert.Single(itemFiles);
        Assert.Contains("public string A { get; set; }", itemFiles[0].Content);
        Assert.Contains("public string B { get; set; }", itemFiles[0].Content);
    }

    [Fact]
    public void Item_types_are_not_emitted_per_view()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                table items: vm.Items of MyRow {
                    col "Name" bind: item.Name;
                }
            }
            """);

        Assert.DoesNotContain(result.Files, f => f.Name == "MyRow.g.cs");
    }
}
