using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>The local console's pending-verb table for deferred rebuild and undo verdicts: a verb registers
/// the correlation id its submission minted, and the composition root's <c>WorldServer.EchoTap</c> subscriber takes
/// the entry back when the tick-boundary verdict fires, printing a per-verb line the submitting script can account
/// beside the verb-agnostic <c>[world.mutation …]</c> narration — a refusal on stderr
/// (<c>[world.reset: …]</c>), an acceptance on stdout. Each entry carries the <see cref="CommandSettlement"/> the
/// verb's handler returned, so the same verdict settles the submitting line for a session that settles results.</summary>
/// <remarks>Register and take both run on the tick thread (a console handler submits inline over loopback; the echo
/// tap fires from the drain), so the table carries no lock. Correlation <c>0</c> means "no local correlation" (a
/// codec refusal, a federated link) and never registers: no local verdict will name it, so it reports an unknown
/// outcome. This table belongs to one local authority. Mutations use their typed completion callback instead of
/// this table, so multiple worlds sharing a console cannot collide on world-local correlations.
/// Past <see cref="Capacity"/> pending entries the oldest is evicted, so a verdict that never
/// fires cannot grow the table; an evicted entry settles as an unknown outcome rather than holding its session, and
/// <see cref="Evicted"/> reports it, since no echo will answer it.</remarks>
public sealed class WorldDeferredVerbEchoes {
    /// <summary>Reports a per-verb typed mutation result, independently of world-local echo correlations.</summary>
    public event Action<CommandResult>? Completed;
    /// <summary>Reports a registered verb evicted before its verdict arrived. The error is that line's only answer:
    /// no echo will name its correlation again, so a host prints it and counts it as a refusal, as it counts a
    /// rejected echo.</summary>
    public event Action<CommandResult>? Evicted;

    internal void Publish(CommandResult result) => Publish(
        callbacks: Completed,
        result: result
    );

    private static void Publish(Action<CommandResult>? callbacks, CommandResult result) {
        if (callbacks is null) {
            return;
        }
        foreach (var callback in Delegate.EnumerateInvocationList(d: callbacks)) {
            try {
                callback(result);
            } catch (Exception) {
                // A console output failure must not abort the authority's pending-edit drain.
            }
        }
    }

    /// <summary>The pending-entry bound. Rebuilds and undos drain at the next tick boundary, so the steady-state population is
    /// one stdin batch's worth; the bound only matters when a verdict never fires.</summary>
    public const int Capacity = 256;
    /// <summary>The number of most recent evictions whose correlation ids the table remembers, so a verdict that
    /// arrives after its line was evicted is known to have been answered by <see cref="Evicted"/> already
    /// (<see cref="TryTakeEvicted"/>). A verdict arriving after this many later evictions reads as one no line
    /// registered.</summary>
    public const int EvictedMemory = Capacity;

    private readonly Queue<long> m_evictedOrder = new();
    private readonly HashSet<long> m_evicted = [];
    private readonly Queue<long> m_order = new();
    private readonly Dictionary<long, (string Verb, CommandSettlement Settlement)> m_verbs = [];

    /// <summary>Registers the submitting verb for one minted correlation id; correlation <c>0</c> (no local
    /// correlation) settles with an unknown-outcome error.</summary>
    /// <param name="correlationId">The correlation id the submission's envelope minted.</param>
    /// <param name="verb">The submitting verb, exactly as its response line spells it (e.g. <c>world.reset</c>).</param>
    /// <returns>The result the verb's handler returns: the verdict itself when <paramref name="settlement"/> is
    /// already settled (a synchronous ingress refusal, correlation <c>0</c>, a duplicate correlation), so the line's
    /// own result reports and counts it; otherwise no output of its own, settling with the tick-boundary verdict.</returns>
    /// <param name="settlement">The submission's settlement, including any synchronous ingress refusal.</param>
    public CommandResult Register(long correlationId, string verb, CommandSettlement? settlement = null) {
        settlement ??= new CommandSettlement();
        if (settlement.IsSettled) {
            return CommandResult.Settling(settlement: settlement);
        }

        if (correlationId == 0) {
            settlement.Settle(result: CommandResult.Error(output: $"[{verb}: no local verdict is available; inspect state before any retry]"));

            return CommandResult.Settling(settlement: settlement);
        }

        if (m_verbs.TryAdd(
            key: correlationId,
            value: (verb, settlement)
        )) {
            m_order.Enqueue(item: correlationId);

            // Evict oldest-first past the bound; an id whose entry was already taken dequeues as a no-op.
            while (m_verbs.Count > Capacity) {
                var evictedId = m_order.Dequeue();

                if (m_verbs.Remove(key: evictedId, value: out var evicted)) {
                    var unanswered = CommandResult.Error(output: $"[{evicted.Verb}: evicted unanswered — {Capacity} later submissions were pending before its verdict arrived; inspect state before any retry]");

                    RememberEvicted(correlationId: evictedId);
                    evicted.Settlement.Settle(result: unanswered);
                    Publish(
                        callbacks: Evicted,
                        result: unanswered
                    );
                }
            }

            // Keep the id queue itself bounded: taken entries leave stale ids queued, so compact the head and,
            // should stale ids pile up behind a long-lived head, rebuild from the live set (cold control-plane
            // path — the allocation is fine here).
            while (
                (m_order.Count > 0) &&
                !m_verbs.ContainsKey(key: m_order.Peek())
            ) {
                _ = m_order.Dequeue();
            }

            if (m_order.Count > (2 * Capacity)) {
                var live = m_order.Where(predicate: m_verbs.ContainsKey).ToArray();

                m_order.Clear();

                foreach (var id in live) {
                    m_order.Enqueue(item: id);
                }
            }
        } else {
            settlement.Settle(result: CommandResult.Error(output: $"[{verb}: duplicate verdict correlation; inspect state before any retry]"));
        }

        return CommandResult.Settling(settlement: settlement);
    }
    /// <summary>Takes the registered verb for one correlation id, removing the entry.</summary>
    /// <param name="correlationId">The verdict's correlation id.</param>
    /// <param name="verb">The verb registered at submit, when one was.</param>
    /// <param name="settlement">The submitting line's pending verdict, which the caller settles.</param>
    /// <returns><see langword="true"/> when an entry existed.</returns>
    public bool TryTake(long correlationId, out string verb, out CommandSettlement? settlement) {
        if (m_verbs.Remove(
            key: correlationId,
            value: out var entry
        )) {
            verb = entry.Verb;
            settlement = entry.Settlement;

            return true;
        }

        verb = string.Empty;
        settlement = null;

        return false;
    }
    /// <summary>Takes a remembered eviction: a verdict for this correlation id arrived after its line was evicted,
    /// and <see cref="Evicted"/> already answered and counted that line, so the verdict is neither printed nor
    /// counted again. The id is forgotten, so only its first verdict matches.</summary>
    /// <param name="correlationId">The verdict's correlation id.</param>
    /// <returns><see langword="true"/> when the id is one of the last <see cref="EvictedMemory"/> evictions and no
    /// verdict has taken it yet.</returns>
    public bool TryTakeEvicted(long correlationId) => m_evicted.Remove(item: correlationId);

    // The queue holds exactly the last EvictedMemory evictions and the set those no verdict has taken yet.
    private void RememberEvicted(long correlationId) {
        _ = m_evicted.Add(item: correlationId);
        m_evictedOrder.Enqueue(item: correlationId);

        while (m_evictedOrder.Count > EvictedMemory) {
            _ = m_evicted.Remove(item: m_evictedOrder.Dequeue());
        }
    }
}
