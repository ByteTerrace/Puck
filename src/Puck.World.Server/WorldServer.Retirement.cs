using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>The refusal code every submission a stopped activation answers carries.</summary>
    public const string StoppedCode = "world.authority.stopped";

    private bool m_authorityRetiring;
    private string? m_stopReason;

    /// <summary>Whether this activation has permanently closed admission and simulation for retirement.
    /// A failed final save does not reopen it; only a new server activation can accept work again.</summary>
    public bool IsRetiring { get { lock (m_authorityGate) { return m_authorityRetiring; } } }

    /// <summary>Drains already accepted document edits and freezes this activation under the authority gate.
    /// Call on the host pump before capturing the final host and server checkpoint slices.</summary>
    /// <exception cref="InvalidOperationException">A dispatched external operation has not settled yet.</exception>
    /// <remarks>Buffered intents remain checkpoint data; retirement does not invent an extra simulation tick.
    /// Repeated calls are harmless. Storage failure leaves the same frozen state available for another save.</remarks>
    public void FreezeForRetirement() {
        lock (m_authorityGate) {
            if (m_authorityRetiring) { return; }
            if (Extensions.ExternalOperationsInFlight != 0) {
                throw new InvalidOperationException(message: "Authority retirement requires external operations to settle first.");
            }
            // No other thread can admit work during this drain. Accepted extension contributions still use
            // the ordinary submission door, so admission closes only after that existing queue has settled.
            m_mutationBudget.BeginTick();
            m_tick.DrainOrdered();
            Extensions.Drain();
            _ = m_tick.DrainPendingOps(tick: m_tick.CompletedTick);
            m_authorityRetiring = true;
        }
    }
    /// <summary>Stops this activation for good: admission closes, and every submission still pending (a mutation, a
    /// rebuild, an undo, from any submitter) is answered now with a refusal naming the stop, through the same answers a
    /// refusal at the tick boundary gives — the typed completion and the edit echo — so no submitter waits for a tick
    /// that never comes. A submission arriving afterwards is refused the same way. Repeated calls are harmless.</summary>
    /// <param name="reason">Why the activation stopped, as the refusals name it.</param>
    public void Stop(string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: reason);

        lock (m_authorityGate) {
            if (m_stopReason is not null) {
                return;
            }

            m_stopReason = reason;
            m_authorityRetiring = true;
            m_document.RefusePending(reason: reason);
        }
    }

    /// <summary>Returns the refusal a submission to this activation meets once it has frozen for retirement or stopped.</summary>
    /// <returns>The refusal.</returns>
    internal WorldSubmissionResult.Refusal RetiredRefusal() => ((m_stopReason is { } reason)
        ? new WorldSubmissionResult.Refusal(Code: StoppedCode, Detail: reason)
        : new WorldSubmissionResult.Refusal(Code: "world.authority.retiring", Detail: "authority is retiring and no longer admits submissions"));

    private void ThrowIfAuthorityRetiring() {
        if (m_authorityRetiring) {
            throw new InvalidOperationException(message: "The authority activation is retiring; use its current host.");
        }
    }
}
