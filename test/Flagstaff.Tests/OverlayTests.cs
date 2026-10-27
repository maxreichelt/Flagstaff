using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>Hoisted overlays: dialog, confirm sugar, toast auto-dismiss, FS1020–FS1022.</summary>
public class OverlayTests
{
    private static CompilationResult Compile(string source) =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile(source, "test.flag");

    private static string File(CompilationResult result, string name) =>
        result.Files.Single(f => f.Name == name).Content;

    // ── Hoisting ───────────────────────────────────────────────────

    [Fact]
    public void Nested_overlays_render_in_the_overlay_layer_only()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                card "C" {
                    group "G" {
                        toast "Saved" when(vm.ShowSaved);
                        dialog "Edit" when(vm.IsEditOpen) { button "Close" -> vm.CloseCommand; }
                    }
                }
            }
            """);

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var xaml = File(result, "V.xaml");
        var overlayStart = xaml.IndexOf("<Grid x:Name=\"FlagstaffOverlay\" Grid.Row=\"0\" Grid.RowSpan=\"2\">", StringComparison.Ordinal);
        Assert.True(overlayStart > xaml.IndexOf("</ScrollViewer>", StringComparison.Ordinal));
        Assert.True(xaml.IndexOf("<InfoBar", StringComparison.Ordinal) > overlayStart);
        Assert.True(xaml.IndexOf("ViewModel.IsEditOpen", StringComparison.Ordinal) > overlayStart);
        Assert.Contains("<!-- toast \"Saved\": rendered in FlagstaffOverlay -->", xaml);
    }

    [Fact]
    public void View_without_overlays_has_no_overlay_layer()
    {
        var xaml = File(Compile("""
            view V {
                bind vm: VM;
                text "A";
            }
            """), "V.xaml");

        Assert.DoesNotContain("FlagstaffOverlay", xaml);
    }

    [Fact]
    public void Dialog_is_registered_and_uses_dialog_styles()
    {
        Assert.Contains("dialog", ElementSchema.All.Keys);
        Assert.Contains("dialog", ConditionCompiler.BareBoolKeywords);

        var xaml = File(Compile("""
            view V {
                bind vm: VM;
                dialog "Edit" message: "Change it." when(vm.IsEditOpen) {
                    textbox "Name" vm.Name;
                    button "Save" -> vm.SaveCommand;
                }
            }
            """), "V.xaml");

        Assert.Contains("Style=\"{StaticResource FlagstaffDialogBackdrop}\"", xaml);
        Assert.Contains("Style=\"{StaticResource FlagstaffDialogCard}\"", xaml);
        Assert.Contains("<TextBlock Text=\"Change it.\" TextWrapping=\"Wrap\" />", xaml);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.IsEditOpen, Mode=OneWay, Converter={StaticResource BoolToVisibility}}\"", xaml);
    }

    // ── FS1020 ─────────────────────────────────────────────────────

    [Fact]
    public void Overlay_inside_table_template_is_fs1020()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Orders of Order {
                    col "Name" bind: item.Name;
                    confirm "Delete?" when(vm.IsDeleteVisible) -> vm.DeleteCommand;
                }
            }
            """);

        var d = Assert.Single(result.Diagnostics, x => x.Code == "FS1020");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(new SourceLocation("test.flag", 5, 9), d.Location);
        Assert.DoesNotContain("FlagstaffOverlay", File(result, "V.xaml"));
    }

    [Fact]
    public void Toast_nested_below_a_table_is_fs1020()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Orders of Order {
                    col "Name" bind: item.Name;
                    group "G" { toast "Hi" when(vm.ShowHi); }
                }
            }
            """);

        Assert.Single(result.Diagnostics, x => x.Code == "FS1020");
    }

    // ── FS1021 ─────────────────────────────────────────────────────

    [Fact]
    public void Toast_without_bind_vm_is_fs1021()
    {
        var result = Compile("""
            view V {
                toast "Saved" when(vm.ShowSaved);
            }
            """);

        var d = Assert.Single(result.Diagnostics, x => x.Code == "FS1021");
        Assert.Contains("bind vm:", d.Message);
        Assert.Equal(2, d.Location.Line);
    }

    [Fact]
    public void Dialog_without_when_is_fs1021()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                dialog "Edit" { button "Close" -> vm.CloseCommand; }
            }
            """);

        var d = Assert.Single(result.Diagnostics, x => x.Code == "FS1021");
        Assert.Contains("when(vm.Flag)", d.Message);
    }

    // ── FS1022 ─────────────────────────────────────────────────────

    [Fact]
    public void Confirm_with_arrow_and_buttons_is_fs1022()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                confirm "Delete?" when(vm.IsDeleteVisible) -> vm.DeleteCommand {
                    button "Yes" -> vm.YesCommand;
                }
            }
            """);

        Assert.Single(result.Diagnostics, x => x.Code == "FS1022");
    }

    [Fact]
    public void Confirm_without_arrow_or_buttons_is_fs1022()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                confirm "Delete?" when(vm.IsDeleteVisible);
            }
            """);

        Assert.Single(result.Diagnostics, x => x.Code == "FS1022");
    }

    [Fact]
    public void ConfirmLabel_with_own_buttons_is_fs1022_at_the_prop()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                confirm "Delete?" when(vm.IsDeleteVisible) confirmLabel: "Delete" {
                    button "Yes" -> vm.YesCommand;
                }
            }
            """);

        var d = Assert.Single(result.Diagnostics, x => x.Code == "FS1022");
        Assert.Equal(new SourceLocation("test.flag", 3, 48), d.Location);
    }

    // ── Confirm sugar / Cancel command ─────────────────────────────

    [Fact]
    public void Confirm_sugar_generates_cancel_command_and_buttons()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                confirm "Delete?" when(vm.IsDeleteVisible) confirmLabel: "Delete" -> vm.DeleteCommand;
            }
            """);

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var vm = File(result, "VM.g.cs");
        Assert.Contains("[RelayCommand] private void CancelIsDeleteVisible() => IsDeleteVisible = false;", vm);
        Assert.Contains("[RelayCommand] private async Task DeleteAsync()", vm);
        Assert.DoesNotContain("partial void On", vm);

        var xaml = File(result, "V.xaml");
        Assert.Contains("<Button Content=\"Delete\" Style=\"{StaticResource AccentButtonStyle}\" Command=\"{x:Bind ViewModel.DeleteCommand, Mode=OneWay}\" />", xaml);
        Assert.Contains("<Button Content=\"Cancel\" Command=\"{x:Bind ViewModel.CancelIsDeleteVisibleCommand, Mode=OneWay}\" />", xaml);
    }

    [Fact]
    public void Cancel_command_is_generated_once_per_flag()
    {
        var vm = File(Compile("""
            view V {
                bind vm: VM;
                confirm "A?" when(vm.IsAskVisible) -> vm.ACommand;
                confirm "B?" when(vm.IsAskVisible) -> vm.BCommand;
            }
            """), "VM.g.cs");

        Assert.Single(vm.Split('\n'), l => l.Contains("CancelIsAskVisible()"));
    }

    [Fact]
    public void Cancel_command_defers_to_a_user_command_of_the_same_name()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                button "Close" -> vm.CancelIsDeleteVisibleCommand;
                confirm "Delete?" when(vm.IsDeleteVisible) -> vm.DeleteCommand;
            }
            """);

        var vm = File(result, "VM.g.cs");
        Assert.DoesNotContain("private void CancelIsDeleteVisible()", vm);
        Assert.Contains("CancelIsDeleteVisibleAsync()", vm);
    }

    [Fact]
    public void Confirm_with_own_buttons_gets_no_generated_buttons()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                confirm "Delete?" when(vm.IsDeleteVisible) {
                    button "No" -> vm.NoCommand;
                    button "Yes" -> vm.YesCommand;
                }
            }
            """);

        Assert.DoesNotContain("CancelIsDeleteVisible", File(result, "VM.g.cs"));
        Assert.DoesNotContain("AccentButtonStyle", File(result, "V.xaml"));
    }

    // ── Toast duration ─────────────────────────────────────────────

    [Fact]
    public void Toast_duration_generates_dispatcher_timer_in_code_behind()
    {
        var cs = File(Compile("""
            view V {
                bind vm: VM;
                toast "Saved" when(vm.ShowSaved) variant: success duration: 3000;
            }
            """), "V.xaml.cs");

        Assert.Contains("ViewModel.PropertyChanged += OnToastFlagChanged;", cs);
        Assert.Contains("case nameof(ViewModel.ShowSaved) when ViewModel.ShowSaved:", cs);
        Assert.Contains("TimeSpan.FromMilliseconds(3000)", cs);
        Assert.Contains("Tick += (_, _) => ViewModel.ShowSaved = false;", cs);
        Assert.DoesNotContain("partial void OnShowSaved", cs);
    }

    [Fact]
    public void Toast_without_duration_has_no_timer()
    {
        var cs = File(Compile("""
            view V {
                bind vm: VM;
                toast "Saved" when(vm.ShowSaved);
            }
            """), "V.xaml.cs");

        Assert.DoesNotContain("PropertyChanged", cs);
        Assert.DoesNotContain("DispatcherQueueTimer", cs);
    }
}
