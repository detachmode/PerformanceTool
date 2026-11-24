namespace Beesharp.Services;

/// <summary>Exit codes for headless <c>run</c> (both <c>beesharp run</c> and <c>Beesharp.Host</c> <c>run</c>, brain 0054): a CI pipeline fails on anything but 0.</summary>
public static class HeadlessExitCodes
{
    /// <summary>All bees succeeded.</summary>
    public const int Ok = 0;

    /// <summary>At least one bee failed (canceled bees count as failed), the run was stopped with Ctrl+C, or the shutdown program failed.</summary>
    public const int BeesFailed = 1;

    /// <summary>The run could not run: bad arguments, preset or environment not found, a script that does not compile, unknown placeholders, or the preflight failed.</summary>
    public const int CouldNotRun = 2;
}
