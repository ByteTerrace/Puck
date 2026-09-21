namespace Puck.Commands;

/// <summary>
/// The pending verdict of work a handler started and did not finish: a handler that hands an edit to an authority
/// which decides it later returns <see cref="CommandResult.Settling"/> over one of these, and whoever learns the
/// verdict settles it.
/// </summary>
/// <remarks>
/// The first <see cref="Settle"/> decides; a later one is ignored, so an eviction and a late verdict cannot disagree.
/// A settlement nobody settles holds a settling session's next line forever, so its creator owns settling it on every
/// path, a verdict that will never arrive included. Safe to settle from any thread; the continuation runs on the
/// settling thread.
/// </remarks>
public sealed class CommandSettlement {
    private readonly Lock m_gate = new();

    private Action<CommandResult>? m_continuation;
    private CommandResult? m_result;

    /// <summary>Gets whether a final verdict has already been supplied.</summary>
    public bool IsSettled {
        get {
            lock (m_gate) {
                return (m_result is not null);
            }
        }
    }

    // Runs the continuation with the verdict: inline when it is already known, otherwise when it arrives.
    internal void OnSettled(Action<CommandResult> continuation) {
        CommandResult settled;

        lock (m_gate) {
            if (m_result is not { } result) {
                m_continuation += continuation;

                return;
            }

            settled = result;
        }

        continuation(obj: settled);
    }

    /// <summary>Settles the pending work with its verdict.</summary>
    /// <param name="result">The verdict. A settlement it carries of its own is dropped: a verdict is final.</param>
    public void Settle(in CommandResult result) {
        Action<CommandResult>? continuation;
        var settled = (result with { Settlement = null });

        lock (m_gate) {
            if (m_result is not null) {
                return;
            }

            m_result = settled;
            continuation = m_continuation;
            m_continuation = null;
        }

        continuation?.Invoke(obj: settled);
    }
}
