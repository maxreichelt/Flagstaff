using System.Text.Json.Nodes;

namespace Flagstaff.Cli.Tests;

/// <summary>Runs the CLI in-process against a temporary directory.</summary>
internal sealed class TempProject : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "flagstaff-cli-tests", Guid.NewGuid().ToString("N")[..12]);

    public TempProject() => Directory.CreateDirectory(Dir);

    public string Path_(string relative) => Path.Combine(Dir, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Write(string relative, string content)
    {
        var path = Path_(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Read(string relative) => File.ReadAllText(Path_(relative));

    public bool Exists(string relative) => File.Exists(Path_(relative));

    public CliRun Run(params string[] args) => CliRun.Invoke(Dir, args);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record CliRun(int ExitCode, string Stdout, string Stderr)
{
    public static CliRun Invoke(string workingDirectory, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Cli.Run(args, stdout, stderr, workingDirectory);
        return new CliRun(code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Parses stdout as exactly one JSON document (fails the test otherwise).</summary>
    public JsonObject Json()
    {
        var node = JsonNode.Parse(Stdout);
        Assert.NotNull(node);
        return node!.AsObject();
    }
}

internal static class Samples
{
    public const string App = """
        app TestApp {
            nav side {
                brand "TestApp";
                link "Home" icon: home -> HomeView;
            }
        }
        """;

    public const string Home = """
        view HomeView {
            bind vm: HomeViewModel;
            onload -> vm.Load;

            toolbar {
                heading "Home" icon: home;
                spacer;
                button "Save" -> vm.SaveCommand;
            }

            stat "Count" vm.Count;
        }
        """;

    public const string Orders = """
        view OrdersView {
            bind vm: OrdersViewModel;

            table items: vm.Orders of Order {
                col "Customer" bind: item.Customer;
            }
            button "Refresh" -> vm.RefreshCommand;
        }
        """;
}
