namespace Puck.Launcher;

/// <summary>
/// Holds a host's first fixed step while redirected standard input is still arriving. The standard-input reader
/// claims it before any host can step and releases it when input ends, when a read fails, when input is an
/// interactive terminal, or when the writer had written nothing by the time the reader first looked. A momentarily
/// empty pipe after the first byte never releases it: a writer pausing between chunks cannot be told apart from one
/// that has finished, so the host instead steps once the console session itself waits for a step
/// (<see cref="Puck.Commands.TextCommandSource.AdministrativeSessionAwaitsStep"/>).
/// </summary>
/// <remarks>A backlog nothing claims never holds, so a host without a standard-input reader steps immediately. The
/// reader releases only after queuing every line it has read, which is what lets a host sample the release before
/// draining the console.</remarks>
public sealed class StandardInputBacklog {
    private volatile bool m_holding;

    /// <summary>Gets a value indicating whether the backlog still holds the first step.</summary>
    public bool IsHolding => m_holding;

    /// <summary>Holds the first step. A reader calls this before any host can step.</summary>
    public void Claim() => m_holding = true;
    /// <summary>Stops holding the first step. A reader calls this only after queuing every line it has read.</summary>
    public void Release() => m_holding = false;
}
