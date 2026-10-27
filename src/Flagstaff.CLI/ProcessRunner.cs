using System.Diagnostics;

namespace Flagstaff.Cli;

/// <summary>Runs child processes, streaming their output line by line.</summary>
internal static class ProcessRunner
{
    public sealed record Result(int ExitCode, List<string> Lines, bool Started, string? StartError);

    /// <summary>Runs <paramref name="fileName"/>; every output line (stdout and stderr) goes to <paramref name="onLine"/>.</summary>
    public static Result Run(string fileName, IEnumerable<string> arguments, string workingDirectory,
        Action<string>? onLine = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["MSBUILDTERMINALLOGGER"] = "off";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        var lines = new List<string>();
        var gate = new object();
        void Handle(string? line)
        {
            if (line is null)
                return;
            lock (gate)
            {
                lines.Add(line);
                onLine?.Invoke(line);
            }
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new Result(-1, lines, false, ex.Message);
        }

        using (process)
        {
            process.OutputDataReceived += (_, e) => Handle(e.Data);
            process.ErrorDataReceived += (_, e) => Handle(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var registration = cancellationToken.Register(() => TryKill(process));
            if (!process.WaitForExit(timeout ?? Timeout.InfiniteTimeSpan))
            {
                TryKill(process);
                return new Result(-1, lines, true, $"{Path.GetFileName(fileName)} timed out");
            }
            process.WaitForExit(); // flush the async output readers
            return new Result(process.ExitCode, lines, true, null);
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
