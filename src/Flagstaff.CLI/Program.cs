using Flagstaff.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C asks long-running commands (watch, build) to stop; a second one kills the process.
    if (!cts.IsCancellationRequested)
    {
        e.Cancel = true;
        cts.Cancel();
    }
};

return Cli.Run(args, Console.Out, Console.Error, Environment.CurrentDirectory, cts.Token);
