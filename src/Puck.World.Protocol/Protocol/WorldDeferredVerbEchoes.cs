using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>One answer the console owes a registered line: the line it prints, when it prints one, and whether the line
/// counts as a refusal in <c>wire.errors</c>.</summary>
/// <param name="Line">The line to print, or <see langword="null"/> when the line's answer is printed elsewhere (the
/// server's own narration, a typed completion, the transport's codec refusal).</param>
/// <param name="IsError">Whether <paramref name="Line"/> belongs on stderr.</param>
/// <param name="Counts">Whether the answer counts one refusal.</param>
public readonly record struct WorldDeferredVerbAnswer(string? Line, bool IsError, bool Counts);
/// <summary>
/// The console's registered lines and their verdicts, one table for every row the console addresses. A line is
/// registered when the console's own link mints its correlation (<see cref="WorldConsoleServerLink"/>), keyed by the
/// row and that correlation, since correlations are per-row link: a submission through a row's bare link (a host's own
/// reload, a replay, a rule or addon) never registers. <c>wire.errors</c> counts a refused registered line, and a codec
/// refusal on a console link; nothing else.
/// </summary>
/// <remarks>
/// <para>A buffered submission (a mutation, a rebuild, an undo) is answered by its echo at the next tick boundary, so
/// its entry waits for it. Every other submission applies inside the submit, so its echo, when it has one, has arrived
/// by the time the submit returns, and its entry is dropped then. A rebuild or undo verb additionally names itself
/// and its settlement (<see cref="Register"/>), so its verdict prints as its own line, <c>[world.reset: …]</c>; any
/// other line's answer is the server's narration or its typed completion, and its verdict only counts. A grant-table
/// echo never settles a buffered entry, because a rebuild replays its document's grants under its own
/// correlation.</para>
/// <para>Past <see cref="Capacity"/> pending entries the oldest is evicted: its settlement settles as an unknown
/// outcome so a settling session is released, nothing prints and nothing counts, and the table remembers the line so
/// its verdict still answers and counts by its real outcome when it arrives. Past <see cref="EvictedMemory"/>
/// remembered lines the oldest is forgotten: it prints once and counts once as an unknown outcome, since its verdict
/// could no longer be told from an unregistered one, and that verdict answers nothing.</para>
/// <para>Rows tick on their own threads, so the table takes a lock; every answer is raised outside it.</para>
/// </remarks>
public sealed class WorldDeferredVerbEchoes {
    /// <summary>The pending-entry bound. Buffered verdicts drain at the next tick boundary, so the steady-state
    /// population is one stdin batch's worth; the bound only matters when a verdict never fires.</summary>
    public const int Capacity = 256;
    /// <summary>The number of evicted lines the table remembers until their verdicts arrive.</summary>
    public const int EvictedMemory = Capacity;
    /// <summary>The row a submission is keyed under when its link names none: a single-row host's, or a test's.</summary>
    public const string DefaultRow = "";

    private readonly Dictionary<Key, Entry> m_evicted = [];
    private readonly Queue<Key> m_evictedOrder = new();
    private readonly Lock m_gate = new();
    private readonly Queue<Key> m_order = new();
    private readonly Dictionary<Key, Entry> m_pending = [];

    /// <summary>Reports a per-verb typed mutation result, independently of world-local echo correlations.</summary>
    public event Action<CommandResult>? Completed;
    /// <summary>Reports each answer the table owes a registered line: a verdict, a forgotten line's unknown outcome, or a
    /// console link's codec refusal. A host prints <see cref="WorldDeferredVerbAnswer.Line"/> and counts
    /// <see cref="WorldDeferredVerbAnswer.Counts"/>.</summary>
    public event Action<WorldDeferredVerbAnswer>? Answered;

    /// <summary>Gets the number of lines waiting for their verdict.</summary>
    public int PendingCount {
        get {
            lock (m_gate) {
                return m_pending.Count;
            }
        }
    }
    /// <summary>Gets the number of evicted lines still remembered.</summary>
    public int EvictedCount {
        get {
            lock (m_gate) {
                return m_evicted.Count;
            }
        }
    }

    private static void Raise<T>(Action<T>? callbacks, T value) {
        if (callbacks is null) {
            return;
        }

        foreach (var callback in Delegate.EnumerateInvocationList(d: callbacks)) {
            try {
                callback(value);
            } catch (Exception) {
                // A console output failure must not abort the authority's pending-edit drain.
            }
        }
    }
    private static WorldDeferredVerbAnswer Verdict(Entry entry, bool rejected, string message) {
        if (entry.Verb is not { } verb) {
            return new WorldDeferredVerbAnswer(
                Counts: rejected,
                IsError: rejected,
                Line: null
            );
        }

        var line = $"[{verb}: {message}]";

        entry.Settlement?.Settle(result: (rejected
            ? CommandResult.Error(output: line)
            : new CommandResult(Output: line)
        ));

        return new WorldDeferredVerbAnswer(
            Counts: rejected,
            IsError: rejected,
            Line: line
        );
    }

    /// <summary>Creates the row handle a console link registers its submissions through.</summary>
    /// <param name="row">The row the link submits to; its echoes answer through <see cref="Answer"/> with the same
    /// name.</param>
    /// <returns>The row handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public WorldDeferredVerbRow ForRow(string row) {
        ArgumentNullException.ThrowIfNull(argument: row);

        return new WorldDeferredVerbRow(
            echoes: this,
            row: row
        );
    }
    /// <summary>Answers one echo from a row's authority: a verdict for a registered line settles it, prints the rebuild
    /// or undo verb's own line, and counts a refusal. An echo from a remote connection, one with no local correlation
    /// (a rule or addon), a grant-table replay of a buffered line, and an echo no console line registered answer
    /// nothing.</summary>
    /// <param name="row">The row whose authority raised the echo.</param>
    /// <param name="local">Whether the echo answers the row's local connection.</param>
    /// <param name="correlationId">The echo's correlation id.</param>
    /// <param name="rejected">Whether the echo is a refusal.</param>
    /// <param name="grantTable">Whether the echo is a grant-table outcome.</param>
    /// <param name="message">The echo's outcome line, without brackets.</param>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> or <paramref name="message"/> is
    /// <see langword="null"/>.</exception>
    public void Answer(string row, bool local, long correlationId, bool rejected, bool grantTable, string message) {
        ArgumentNullException.ThrowIfNull(argument: row);
        ArgumentNullException.ThrowIfNull(argument: message);

        if (
            !local ||
            (correlationId == 0)
        ) {
            return;
        }

        var key = new Key(
            Correlation: correlationId,
            Row: row
        );
        Entry? entry;

        lock (m_gate) {
            if (!TryTakeAnswerable(
                entries: m_pending,
                entry: out entry,
                grantTable: grantTable,
                key: key
            )) {
                _ = TryTakeAnswerable(
                    entries: m_evicted,
                    entry: out entry,
                    grantTable: grantTable,
                    key: key
                );
            }
        }

        if (entry is not null) {
            Raise(
                callbacks: Answered,
                value: Verdict(
                    entry: entry,
                    message: message,
                    rejected: rejected
                )
            );
        }
    }
    /// <summary>Registers a rebuild or undo verb against the correlation its console link minted, so its verdict prints
    /// as the verb's own line and settles its session.</summary>
    /// <param name="row">The row the correlation belongs to.</param>
    /// <param name="correlationId">The correlation the submission minted, or <c>0</c> when it minted none.</param>
    /// <param name="verb">The submitting verb, exactly as its response line spells it (e.g. <c>world.reset</c>).</param>
    /// <param name="settlement">The submission's settlement, including any synchronous ingress refusal.</param>
    /// <returns>The verdict itself when <paramref name="settlement"/> is already settled (a synchronous refusal),
    /// or an unknown outcome when the submission minted no correlation or one already registered, so the line's own
    /// result reports and counts it; otherwise a result with no output of its own that settles with the tick-boundary
    /// verdict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/>, <paramref name="verb"/> or
    /// <paramref name="settlement"/> is <see langword="null"/>.</exception>
    public CommandResult Register(string row, long correlationId, string verb, CommandSettlement settlement) {
        ArgumentNullException.ThrowIfNull(argument: row);
        ArgumentNullException.ThrowIfNull(argument: verb);
        ArgumentNullException.ThrowIfNull(argument: settlement);

        if (settlement.IsSettled) {
            return CommandResult.Settling(settlement: settlement);
        }
        if (correlationId == 0) {
            settlement.Settle(result: CommandResult.Error(output: $"[{verb}: no local verdict is available; inspect state before any retry]"));

            return CommandResult.Settling(settlement: settlement);
        }

        var key = new Key(
            Correlation: correlationId,
            Row: row
        );
        var duplicate = false;
        List<WorldDeferredVerbAnswer>? forgotten = null;

        lock (m_gate) {
            if (m_pending.TryGetValue(
                key: key,
                value: out var entry
            )) {
                if (entry.Verb is null) {
                    entry.Verb = verb;
                    entry.Settlement = settlement;
                } else {
                    duplicate = true;
                }
            } else {
                // A link that registers nothing at mint (a test's bare transport) registers here.
                Track(
                    buffered: true,
                    forgotten: ref forgotten,
                    key: key,
                    settlement: settlement,
                    verb: verb
                );
            }
        }

        RaiseAll(answers: forgotten);

        if (duplicate) {
            settlement.Settle(result: CommandResult.Error(output: $"[{verb}: duplicate verdict correlation; inspect state before any retry]"));
        }

        return CommandResult.Settling(settlement: settlement);
    }

    internal void Minted(string row, long correlationId, bool buffered) {
        List<WorldDeferredVerbAnswer>? forgotten = null;

        lock (m_gate) {
            Track(
                buffered: buffered,
                forgotten: ref forgotten,
                key: new Key(
                    Correlation: correlationId,
                    Row: row
                ),
                settlement: null,
                verb: null
            );
        }

        RaiseAll(answers: forgotten);
    }
    internal void Publish(CommandResult result) => Raise(
        callbacks: Completed,
        value: result
    );
    internal void RefusedCodec() => Raise(
        callbacks: Answered,
        value: new WorldDeferredVerbAnswer(
            Counts: true,
            IsError: true,
            Line: null
        )
    );
    // A synchronous submission's echo has arrived by the time its submit returns or throws, or never will.
    internal void Submitted(string row, long correlationId, bool buffered) {
        if (buffered) {
            return;
        }

        lock (m_gate) {
            _ = m_pending.Remove(key: new Key(
                Correlation: correlationId,
                Row: row
            ));
        }
    }

    private void RaiseAll(List<WorldDeferredVerbAnswer>? answers) {
        foreach (var answer in (answers ?? [])) {
            Raise(
                callbacks: Answered,
                value: answer
            );
        }
    }
    // Takes an entry an echo answers: a grant-table echo never answers a buffered line, since a rebuild replays its
    // document's grants under the rebuild's own correlation.
    private static bool TryTakeAnswerable(Dictionary<Key, Entry> entries, Key key, bool grantTable, out Entry? entry) {
        if (
            !entries.TryGetValue(
            key: key,
            value: out entry
        ) ||
            (grantTable && entry.Buffered)
        ) {
            entry = null;

            return false;
        }

        _ = entries.Remove(key: key);

        return true;
    }
    // Adds a pending entry and evicts past the bound; runs under the gate and collects any forgotten line's answer for
    // the caller to raise once the gate is released.
    private void Track(Key key, bool buffered, string? verb, CommandSettlement? settlement, ref List<WorldDeferredVerbAnswer>? forgotten) {
        if (!m_pending.TryAdd(
            key: key,
            value: new Entry {
                Buffered = buffered,
                Settlement = settlement,
                Verb = verb,
            }
        )) {
            return;
        }

        m_order.Enqueue(item: key);

        while (m_pending.Count > Capacity) {
            var evictedKey = m_order.Dequeue();

            if (m_pending.Remove(
                key: evictedKey,
                value: out var evicted
            )) {
                Evict(
                    entry: evicted,
                    forgotten: ref forgotten,
                    key: evictedKey
                );
            }
        }

        // Taken entries leave stale keys queued: compact the head, and rebuild from the live set should stale keys pile
        // up behind a long-lived head (a cold path, so the allocation is fine).
        while (
            (m_order.Count > 0) &&
            !m_pending.ContainsKey(key: m_order.Peek())
        ) {
            _ = m_order.Dequeue();
        }

        if (m_order.Count > (2 * Capacity)) {
            var live = m_order.Where(predicate: m_pending.ContainsKey).ToArray();

            m_order.Clear();

            foreach (var liveKey in live) {
                m_order.Enqueue(item: liveKey);
            }
        }
    }
    // An evicted line releases its session as an unknown outcome and is remembered until its verdict arrives; the
    // oldest remembered line past the memory bound is forgotten, answering once as an unknown outcome.
    private void Evict(Key key, Entry entry, ref List<WorldDeferredVerbAnswer>? forgotten) {
        var verb = (entry.Verb ?? "world.console");

        entry.Settlement?.Settle(result: CommandResult.Error(output: $"[{verb}: evicted unanswered — {Capacity} later submissions were pending before its verdict arrived; its verdict answers when it arrives]"));
        m_evicted[key] = entry;
        m_evictedOrder.Enqueue(item: key);

        while (m_evictedOrder.Count > EvictedMemory) {
            var forgottenKey = m_evictedOrder.Dequeue();

            if (m_evicted.Remove(
                key: forgottenKey,
                value: out var lost
            )) {
                (forgotten ??= []).Add(item: new WorldDeferredVerbAnswer(
                    Counts: true,
                    IsError: true,
                    Line: $"[{(lost.Verb ?? "world.console")}: unanswered — its verdict did not arrive before {EvictedMemory} later lines were evicted; inspect state before any retry]"
                ));
            }
        }
    }

    private readonly record struct Key(string Row, long Correlation);
    private sealed class Entry {
        public bool Buffered { get; init; }
        public CommandSettlement? Settlement { get; set; }
        public string? Verb { get; set; }
    }
}
/// <summary>One row's handle on the console's <see cref="WorldDeferredVerbEchoes"/>: what a console link registers each
/// submission it mints through.</summary>
public sealed class WorldDeferredVerbRow {
    internal WorldDeferredVerbRow(WorldDeferredVerbEchoes echoes, string row) {
        Echoes = echoes;
        Row = row;
    }

    /// <summary>Gets the table the row registers into.</summary>
    public WorldDeferredVerbEchoes Echoes { get; }
    /// <summary>Gets the row's name.</summary>
    public string Row { get; }

    // Whether a payload waits for the next tick boundary for its verdict, rather than applying inside the submit.
    internal static bool IsBuffered(WorldSubmissionPayload payload) => (payload is WorldSubmissionPayload.Mutation or WorldSubmissionPayload.Rebuild or WorldSubmissionPayload.Undo);
    internal void Minted(long correlationId, WorldSubmissionPayload payload) => Echoes.Minted(
        buffered: IsBuffered(payload: payload),
        correlationId: correlationId,
        row: Row
    );
    internal void RefusedCodec() => Echoes.RefusedCodec();
    internal void Submitted(long correlationId, WorldSubmissionPayload payload) => Echoes.Submitted(
        buffered: IsBuffered(payload: payload),
        correlationId: correlationId,
        row: Row
    );
}
