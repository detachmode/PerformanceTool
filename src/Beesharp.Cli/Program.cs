using Beesharp.Cli;

// Ctrl+C stops the run like the UI's Stop button: bees in flight become canceled, the shutdown program still runs, the run is saved, the exit code is not 0.
// A second Ctrl+C ends the process at once.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (stop.IsCancellationRequested) return; // second time: default behavior, the process ends
    e.Cancel = true;
    try { stop.Cancel(); } catch (ObjectDisposedException) { }
};
return await CliApp.RunAsync(args, Console.Out, Console.Error, stop.Token);
