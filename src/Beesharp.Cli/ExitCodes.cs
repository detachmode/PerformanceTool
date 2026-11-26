using Beesharp.Services;

namespace Beesharp.Cli;

/// <summary>The exit codes of <c>beesharp run</c> (brain 0054): a pipeline fails on anything but 0.</summary>
public static class ExitCodes
{
    /// <summary>All bees succeeded.</summary>
    public const int Ok = HeadlessExitCodes.Ok;
    /// <summary>At least one bee failed (canceled bees count as failed), the run was stopped with Ctrl+C, or the shutdown program failed.</summary>
    public const int BeesFailed = HeadlessExitCodes.BeesFailed;
    /// <summary>The run could not run: bad arguments, preset or environment not found, a script that does not compile, unknown placeholders, the preflight failed.</summary>
    public const int CouldNotRun = HeadlessExitCodes.CouldNotRun;
    /// <summary><c>beesharp runs trace</c>: the trace is not stored with the run (not selected, not found in the trace store, no trace store configured); the message gives the likely reason (brain 0065).</summary>
    public const int TraceNotFound = 3;
}
