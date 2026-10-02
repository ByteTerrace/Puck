using System.Runtime.CompilerServices;
using Puck.Abstractions.Machines;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The counted cost of an in-session history since it was switched on. Every count is deterministic: it
/// depends on the ticks, inputs, and keyframes the history saw, never on time.</summary>
/// <param name="TicksRecorded">The ticks whose input and authoritative hash the ring captured.</param>
/// <param name="HashFolds">The authoritative hash folds the history took, one per recorded tick and one per
/// re-simulated tick.</param>
/// <param name="KeyframesCaptured">The checkpoint keyframes captured.</param>
/// <param name="KeyframeBytesCaptured">The encoded bytes of every captured keyframe, summed.</param>
/// <param name="KeyframeBytesStored">The bytes the captured keyframes added to the chunk store: their changed
/// regions, past the chunks an earlier keyframe already held.</param>
/// <param name="KeyframesDeferred">The keyframe captures a boundary refused, each retried at the next tick.</param>
/// <param name="SegmentsEvicted">The keyframe spans dropped from the oldest end to stay within the budget.</param>
/// <param name="Seeks">The seeks that moved the live world.</param>
/// <param name="TicksResimulated">The ticks re-simulated by seeks, diffs, and replay-edits.</param>
/// <param name="InPlaceRestores">The keyframe restores that reused the live document's structure.</param>
/// <param name="RebuildRestores">The keyframe restores that rebuilt the document through the load door first,
/// because a structural edit landed after the keyframe.</param>
/// <param name="BranchesKept">The futures kept under a name when live input resumed behind the head.</param>
/// <param name="FuturesDiscarded">The futures discarded when live input resumed behind the head.</param>
public readonly record struct WorldHistoryCounters(
    long TicksRecorded,
    long HashFolds,
    long KeyframesCaptured,
    long KeyframeBytesCaptured,
    long KeyframeBytesStored,
    long KeyframesDeferred,
    long SegmentsEvicted,
    long Seeks,
    long TicksResimulated,
    long InPlaceRestores,
    long RebuildRestores,
    long BranchesKept,
    long FuturesDiscarded
);
/// <summary>One future kept under a name when live input resumed behind the head.</summary>
/// <param name="Name">The branch's name.</param>
/// <param name="ForkTick">The tick the branch leaves the timeline after: its first tick is <c>ForkTick + 1</c>.</param>
/// <param name="Ticks">The recorded tick groups, in order, in the replay tape's own entry format.</param>
/// <param name="AuthoritativeHashes">The authoritative hash recorded after each of <paramref name="Ticks"/>.</param>
/// <param name="Bytes">The bytes the branch holds against the history's budget.</param>
public sealed record WorldHistoryBranch(string Name, ulong ForkTick, IReadOnlyList<WorldReplayTickInput> Ticks, ulong[] AuthoritativeHashes, long Bytes) {
    /// <summary>Gets the tick the branch's recorded future ends at.</summary>
    public ulong HeadTick => (ForkTick + ((ulong)Ticks.Count));
}
/// <summary>A read-back of an in-session history: its window, its keyframe spacing, what it holds against its
/// budget, and its counted cost.</summary>
/// <param name="On">Whether the history is recording.</param>
/// <param name="Oldest">The oldest tick a seek can reach (the oldest keyframe), or <see langword="null"/> while no
/// keyframe has been captured.</param>
/// <param name="Cursor">The tick the live world sits at, or <see langword="null"/> while no keyframe has been
/// captured.</param>
/// <param name="Head">The newest recorded tick, or <see langword="null"/> while no keyframe has been captured.</param>
/// <param name="Keyframes">The keyframes held.</param>
/// <param name="Interval">The current keyframe spacing, in ticks.</param>
/// <param name="BudgetBytes">The memory budget, in bytes.</param>
/// <param name="KeyframeBytes">The keyframe bytes held: every distinct chunk's payload once, plus each keyframe's
/// chunk references.</param>
/// <param name="KeyframeEncodedBytes">The encoded size of the keyframes held, before chunks are shared — the ratio to
/// <paramref name="KeyframeBytes"/> is what sharing saves.</param>
/// <param name="InputBytes">The input and per-tick bookkeeping bytes held, including reserved array capacity.</param>
/// <param name="BranchBytes">The bytes held by kept branches.</param>
/// <param name="Waiting">Why the history holds no window yet, or <see langword="null"/> once it does.</param>
/// <param name="Counters">The counted cost since the history was switched on.</param>
/// <param name="Branches">The kept branches, oldest first.</param>
public readonly record struct WorldHistoryStatus(
    bool On,
    ulong? Oldest,
    ulong? Cursor,
    ulong? Head,
    int Keyframes,
    int Interval,
    long BudgetBytes,
    long KeyframeBytes,
    long KeyframeEncodedBytes,
    long InputBytes,
    long BranchBytes,
    string? Waiting,
    WorldHistoryCounters Counters,
    IReadOnlyList<WorldHistoryBranch> Branches
) {
    /// <summary>Gets every byte the history holds against its budget.</summary>
    public long BytesHeld => ((KeyframeBytes + InputBytes) + BranchBytes);
}
/// <summary>
/// The in-session history behind <c>world.history</c>: deterministic time travel over the running boot world. While
/// on it keeps a ring of keyframe spans — a full authority checkpoint every <see cref="WorldHistoryStatus.Interval"/>
/// ticks, then each following tick's server input in the replay tape's own entries (the
/// <see cref="WorldReplayTape"/>'s capture feeds both) and the authoritative hash that tick reached. A seek restores
/// the nearest keyframe at or before its target and re-simulates the recorded input through the live server's own
/// doors, proving every re-simulated tick against the recorded hash.
/// </summary>
/// <remarks>
/// <para>Keyframes are held in a <see cref="WorldHistoryChunkStore"/>, so a keyframe costs the regions that changed
/// since the ones already held. The keyframe spacing balances that cost against input bytes — the bytes the last
/// keyframe added over the mean recorded input per tick — and never captures more than
/// <see cref="KeyframeWorkBytesPerTick"/> of encoded checkpoint per tick on average, clamped between an eighth of a
/// second and four seconds of simulation; a small world keyframes often and a large one keeps its seek and capture
/// costs bounded. The oldest span is evicted whenever the held bytes exceed the budget; kept branches whose fork the
/// window no longer reaches go with it.</para>
/// <para>Steady state is allocation-free on a tick that captures no keyframe: the capture's accumulators are reused
/// and the span's per-tick arrays grow only until they fit the interval. A keyframe tick pays the checkpoint capture
/// and its encoding.</para>
/// <para>Single-threaded on the boot world's step thread, like the tape it rides: the capture closes each tick from
/// <see cref="WorldServerStepShell.Step"/>, and the console verbs run between steps.</para>
/// </remarks>
public sealed partial class WorldHistory {
    /// <summary>The default memory budget, in bytes.</summary>
    public const long DefaultBudgetBytes = ((64L * 1024L) * 1024L);
    /// <summary>The most encoded checkpoint bytes a keyframe costs per recorded tick, averaged over its interval: the
    /// floor the spacing keeps so capturing and encoding keyframes stays a bounded share of each tick.</summary>
    public const long KeyframeWorkBytesPerTick = (64L * 1024L);

    // The smallest budget a history accepts.
    private const long MinimumBudgetBytes = (1024L * 1024L);
    // The per-tick bookkeeping a span reserves beside each tick's intents: the hash, the step width, and the
    // intent-end offset.
    private const int TickBookkeepingBytes = ((sizeof(ulong) + sizeof(ulong)) + sizeof(int));

    private readonly WorldServer m_server;
    private readonly WorldReplayTape m_tape;
    private readonly IReadOnlyList<IMachineEngine> m_engines;
    private readonly Func<IReadOnlyList<WorldScreen>, IEnumerable<IMachineEngine>, string?, WorldOutputHub?, IWorldMachineHost> m_machineHostFactory;
    private readonly WorldStateRoot m_stateRoot;

    private readonly WorldHistoryChunkStore m_chunks = new();
    private readonly List<Segment> m_segments = [];
    private readonly Stack<Segment> m_recycled = new();
    private readonly List<WorldHistoryBranch> m_branches = [];
    private long m_budgetBytes = DefaultBudgetBytes;

    private WorldHistoryCounters m_counters;
    private ulong m_cursor;
    private int m_interval;
    private bool m_on;
    // Set by branch, consumed by the first live tick behind the head: the name the discarded future is kept under.
    private string? m_pendingBranchName;
    private string? m_waiting;

    /// <summary>Initializes a new instance of the <see cref="WorldHistory"/> class over the boot world's server and
    /// the tape whose capture feeds it.</summary>
    /// <param name="server">The boot world's authoritative server.</param>
    /// <param name="tape">The boot world's replay tape; its capture closes each tick into this history.</param>
    /// <param name="engines">The registered screen-machine engines a shadow re-simulation boots against.</param>
    /// <param name="machineHostFactory">Builds the machine host a shadow re-simulation runs over.</param>
    /// <param name="stateRoot">The host's state root; a shadow's owned-world catalog lives under it while it runs.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldHistory(WorldServer server, WorldReplayTape tape, IEnumerable<IMachineEngine> engines, Func<IReadOnlyList<WorldScreen>, IEnumerable<IMachineEngine>, string?, WorldOutputHub?, IWorldMachineHost> machineHostFactory, WorldStateRoot stateRoot) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: tape);
        ArgumentNullException.ThrowIfNull(argument: engines);
        ArgumentNullException.ThrowIfNull(argument: machineHostFactory);
        ArgumentNullException.ThrowIfNull(argument: stateRoot);

        m_server = server;
        m_tape = tape;
        m_engines = [.. engines];
        m_machineHostFactory = machineHostFactory;
        m_stateRoot = stateRoot;
    }

    /// <summary>Gets whether the history is recording.</summary>
    public bool On => m_on;

    // One keyframe span: the checkpoint at KeyframeTick, then the recorded input and hash of each following tick.
    private sealed class Segment {
        public List<(int Offset, WorldReplayEntry Entry)> Authority { get; } = [];

        public long AuthorityBytes { get; set; }
        public int Count { get; set; }
        public string? DocumentDirectory { get; set; }
        public WorldHistoryFingerprint Fingerprint { get; set; }

        public ulong[] Hashes { get; set; } = new ulong[16];
        public int[] IntentEnds { get; set; } = new int[16];
        public IntentSubmission[] Intents { get; set; } = new IntentSubmission[16];
        public WorldHistoryChunkStore.Chunk[] Keyframe { get; set; } = [];

        public ulong KeyframeHash { get; set; }
        public int KeyframeLength { get; set; }
        public ulong KeyframeTick { get; set; }

        public ulong[] StepTicks { get; set; } = new ulong[16];

        public ulong HeadTick => (KeyframeTick + ((ulong)Count));
        public long InputBytes => (((((long)Hashes.Length) * TickBookkeepingBytes) + (((long)Intents.Length) * Unsafe.SizeOf<IntentSubmission>())) + AuthorityBytes);
        public int IntentCount => ((Count == 0)
            ? 0
            : IntentEnds[(Count - 1)]
        );

        public void Reset() {
            Authority.Clear();
            AuthorityBytes = 0L;
            Count = 0;
            DocumentDirectory = null;
            Keyframe = [];
            KeyframeLength = 0;
            KeyframeHash = 0UL;
            KeyframeTick = 0UL;
            Fingerprint = default;
        }
    }

    // What a keyframe's in-place restore relies on: no structural edit (a journaled mutation, a rebuild, an undo, a
    // compaction, or a solid rebuild) landed between it and the live document.
    internal readonly record struct WorldHistoryFingerprint(WorldDefinition? Base, int JournalCount, WorldMutation? LastMutation, ulong LastJournalTick, int SolidRevision) {
        public static WorldHistoryFingerprint Of(WorldServer server) {
            var journal = server.Document.Journal;
            var last = ((journal.Count > 0)
                ? journal[^1]
                : default);

            return new WorldHistoryFingerprint(
                Base: server.Document.Base,
                JournalCount: journal.Count,
                LastMutation: last.Mutation,
                LastJournalTick: last.Tick,
                SolidRevision: server.Document.SolidRevision
            );
        }
        public bool Matches(WorldHistoryFingerprint other) => (
            ReferenceEquals(objA: Base, objB: other.Base) &&
            (JournalCount == other.JournalCount) &&
            ReferenceEquals(objA: LastMutation, objB: other.LastMutation) &&
            (LastJournalTick == other.LastJournalTick) &&
            (SolidRevision == other.SolidRevision)
        );
    }

    private long BranchBytes {
        get {
            var total = 0L;

            foreach (var branch in m_branches) {
                total += branch.Bytes;
            }

            return total;
        }
    }
    private ulong Head => m_segments[^1].HeadTick;
    private long HeldBytes {
        get {
            var total = (BranchBytes + m_chunks.PayloadBytes);

            foreach (var segment in m_segments) {
                total += (WorldHistoryChunkStore.ReferenceCost(chunks: segment.Keyframe) + segment.InputBytes);
            }

            return total;
        }
    }

    // A held keyframe's encoded bytes, rebuilt from its chunks.
    private static byte[] KeyframeBytes(Segment segment) => WorldHistoryChunkStore.Load(
        chunks: segment.Keyframe,
        length: segment.KeyframeLength
    );

    private ulong Oldest => m_segments[0].KeyframeTick;

    // Appends one tick to the newest span, copying its input out of the capture's reused accumulators.
    private void Append(in WorldReplayTickInput input, ulong authoritativeHash, ulong stepTicks) {
        var segment = m_segments[^1];
        var offset = segment.Count;

        if (offset == segment.Hashes.Length) {
            var grown = Math.Max(
                val1: (offset * 2),
                val2: 16
            );

            segment.Hashes = Grow(array: segment.Hashes, length: grown);
            segment.StepTicks = Grow(array: segment.StepTicks, length: grown);
            segment.IntentEnds = Grow(array: segment.IntentEnds, length: grown);
        }

        var start = segment.IntentCount;
        var intents = input.Intents;
        var end = (start + intents.Count);

        if (end > segment.Intents.Length) {
            segment.Intents = Grow(
                array: segment.Intents,
                length: Math.Max(
                    val1: end,
                    val2: (segment.Intents.Length * 2)
                )
            );
        }

        for (var index = 0; (index < intents.Count); index++) {
            segment.Intents[(start + index)] = intents[index];
        }

        var authority = input.Authority;

        for (var index = 0; (index < authority.Count); index++) {
            var entry = authority[index];

            segment.Authority.Add(item: (offset, entry));
            segment.AuthorityBytes += MeasureEntry(entry: entry);
        }

        segment.Hashes[offset] = authoritativeHash;
        segment.StepTicks[offset] = stepTicks;
        segment.IntentEnds[offset] = end;
        segment.Count = (offset + 1);
    }
    // Drops every recorded tick after the cursor — the future a live tick behind the head replaces — keeping it as a
    // named branch when one was asked for.
    private void BranchAtCursor() {
        var name = m_pendingBranchName;

        m_pendingBranchName = null;

        if (name is not null) {
            var ticks = new List<WorldReplayTickInput>();
            var hashes = new List<ulong>();
            var bytes = 0L;

            for (var tick = (m_cursor + 1UL); (tick <= Head); tick++) {
                var (segment, offset) = Locate(tick: tick);

                ticks.Add(item: InputAt(offset: offset, segment: segment));
                hashes.Add(item: segment.Hashes[offset]);
                bytes += (TickBookkeepingBytes + ((long)(IntentRange(offset: offset, segment: segment).Count * Unsafe.SizeOf<IntentSubmission>())));
            }

            foreach (var (_, entry) in EntriesBetween(from: m_cursor, to: Head)) {
                bytes += MeasureEntry(entry: entry);
            }

            _ = m_branches.RemoveAll(match: branch => string.Equals(a: branch.Name, b: name, comparisonType: StringComparison.Ordinal));
            m_branches.Add(item: new WorldHistoryBranch(
                AuthoritativeHashes: [.. hashes],
                Bytes: bytes,
                ForkTick: m_cursor,
                Name: name,
                Ticks: ticks
            ));
            m_counters = (m_counters with { BranchesKept = (m_counters.BranchesKept + 1L) });
        } else {
            m_counters = (m_counters with { FuturesDiscarded = (m_counters.FuturesDiscarded + 1L) });
        }

        while (m_segments[^1].KeyframeTick > m_cursor) {
            Recycle(segment: m_segments[^1]);
            m_segments.RemoveAt(index: (m_segments.Count - 1));
        }

        var newest = m_segments[^1];
        var keep = ((int)(m_cursor - newest.KeyframeTick));

        newest.Count = keep;
        _ = newest.Authority.RemoveAll(match: item => (item.Offset >= keep));
        newest.AuthorityBytes = 0L;

        foreach (var (_, entry) in newest.Authority) {
            newest.AuthorityBytes += MeasureEntry(entry: entry);
        }
    }
    // Clears the window: a later tick anchors a fresh one at its first keyframe.
    private void ClearWindow(string waiting) {
        foreach (var segment in m_segments) {
            Recycle(segment: segment);
        }

        m_segments.Clear();
        m_branches.Clear();
        m_pendingBranchName = null;
        m_waiting = waiting;
    }
    // Drops the oldest spans while the history holds more than its budget, always keeping the newest; a kept branch
    // whose fork the window no longer reaches goes with it.
    private void EnforceBudget() {
        while (
            (m_segments.Count > 1) &&
            (HeldBytes > m_budgetBytes)
        ) {
            Recycle(segment: m_segments[0]);
            m_segments.RemoveAt(index: 0);
            m_counters = (m_counters with { SegmentsEvicted = (m_counters.SegmentsEvicted + 1L) });
        }

        // A loop, not RemoveAll over a capturing lambda: this runs every recorded tick, and the closure would
        // allocate on each.
        for (var index = (m_branches.Count - 1); (index >= 0); index--) {
            if (m_branches[index].ForkTick < Oldest) {
                m_branches.RemoveAt(index: index);
            }
        }

        while (
            (m_branches.Count > 0) &&
            (HeldBytes > m_budgetBytes)
        ) {
            m_branches.RemoveAt(index: 0);
        }
    }
    private static T[] Grow<T>(T[] array, int length) {
        var grown = new T[length];

        Array.Copy(
            destinationArray: grown,
            length: array.Length,
            sourceArray: array
        );

        return grown;
    }
    // The keyframe spacing that balances the bytes a keyframe added against input bytes, kept wide enough that its
    // encoding averages at most KeyframeWorkBytesPerTick, and clamped to the rate-derived bounds.
    private int IntervalFor(long addedBytes, long encodedBytes) {
        var rate = Math.Max(
            val1: 1,
            val2: m_server.Definition.SimulationRateHz
        );
        var minimum = Math.Max(
            val1: 1,
            val2: (rate / 8)
        );
        var maximum = Math.Max(
            val1: minimum,
            val2: (rate * 4)
        );
        var ticks = 0L;
        var inputBytes = 0L;

        foreach (var segment in m_segments) {
            ticks += segment.Count;
            inputBytes += (((segment.IntentCount * ((long)Unsafe.SizeOf<IntentSubmission>())) + segment.AuthorityBytes) + (((long)segment.Count) * TickBookkeepingBytes));
        }

        var perTick = Math.Max(
            val1: TickBookkeepingBytes,
            val2: ((ticks > 0L)
                ? (inputBytes / ticks)
                : TickBookkeepingBytes)
        );

        var balanced = ((addedBytes + (perTick - 1L)) / perTick);
        var work = ((encodedBytes + (KeyframeWorkBytesPerTick - 1L)) / KeyframeWorkBytesPerTick);

        return ((int)Math.Clamp(
            max: maximum,
            min: minimum,
            value: Math.Max(
                val1: balanced,
                val2: work
            )
        ));
    }
    private static ArraySegment<IntentSubmission> IntentRange(Segment segment, int offset) {
        var start = ((offset == 0)
            ? 0
            : segment.IntentEnds[(offset - 1)]);

        return new ArraySegment<IntentSubmission>(
            array: segment.Intents,
            count: (segment.IntentEnds[offset] - start),
            offset: start
        );
    }
    // The recorded input of one tick, rebuilt in the tape's own shape.
    private static WorldReplayTickInput InputAt(Segment segment, int offset) {
        var authority = new List<WorldReplayEntry>();

        foreach (var (entryOffset, entry) in segment.Authority) {
            if (entryOffset == offset) {
                authority.Add(item: entry);
            }
        }

        return new WorldReplayTickInput(
            Authority: authority,
            Intents: [.. IntentRange(offset: offset, segment: segment)]
        );
    }
    // The span holding a recorded tick, and the tick's offset inside it.
    private (Segment Segment, int Offset) Locate(ulong tick) {
        for (var index = (m_segments.Count - 1); (index >= 0); index--) {
            var segment = m_segments[index];

            if (tick > segment.KeyframeTick) {
                return (segment, ((int)((tick - segment.KeyframeTick) - 1UL)));
            }
        }

        throw new ArgumentOutOfRangeException(paramName: nameof(tick));
    }
    // The span whose keyframe is the latest at or before a tick.
    private Segment KeyframeAtOrBefore(ulong tick) {
        for (var index = (m_segments.Count - 1); (index >= 0); index--) {
            if (m_segments[index].KeyframeTick <= tick) {
                return m_segments[index];
            }
        }

        throw new ArgumentOutOfRangeException(paramName: nameof(tick));
    }
    // Every recorded authority entry of the ticks after `from` up to and including `to`, with its tick.
    private IEnumerable<(ulong Tick, WorldReplayEntry Entry)> EntriesBetween(ulong from, ulong to) {
        foreach (var segment in m_segments) {
            if (
                (segment.HeadTick <= from) ||
                (segment.KeyframeTick >= to)
            ) {
                continue;
            }

            foreach (var (offset, entry) in segment.Authority) {
                var tick = ((segment.KeyframeTick + ((ulong)offset)) + 1UL);

                if (
                    (tick > from) &&
                    (tick <= to)
                ) {
                    yield return (tick, entry);
                }
            }
        }
    }
    private static long MeasureEntry(WorldReplayEntry entry) {
        var writer = new WireWriter(capacity: 64);

        try {
            WorldReplaySnapshot.WriteEntry(
                entry: entry,
                writer: writer
            );
        } catch (WorldReplayCodecException) {
            return 0L;
        }

        return writer.Length;
    }
    private void Narrate(string text) {
        if (m_server.Output.HasNarrationSink) {
            m_server.Output.Narrate(
                channel: "world.history",
                text: text
            );
        }
    }
    private Segment Rent() => ((m_recycled.Count > 0)
        ? m_recycled.Pop()
        : new Segment());
    private void Recycle(Segment segment) {
        m_chunks.Release(chunks: segment.Keyframe);
        segment.Reset();
        m_recycled.Push(item: segment);
    }
    // Captures a keyframe at the live tick and opens a new span on it; a refused capture is counted and retried at
    // the next tick while the current span keeps growing.
    private bool TryKeyframe(ulong tick, ulong authoritativeHash) {
        string reason;
        WorldAuthorityCheckpoint? checkpoint;

        try {
            if (UncapturableLiveState() is { } unsupported) {
                checkpoint = null;
                reason = unsupported;
            } else if (!m_server.TryCaptureCheckpoint(
                checkpoint: out checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out reason
            )) {
                checkpoint = null;
            }
        } catch (InvalidOperationException exception) {
            checkpoint = null;
            reason = exception.Message;
        }

        if (checkpoint is null) {
            m_counters = (m_counters with { KeyframesDeferred = (m_counters.KeyframesDeferred + 1L) });

            if (m_segments.Count == 0) {
                m_waiting = $"no keyframe yet — {reason}";
            }

            return false;
        }

        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var previous = ((m_segments.Count > 0)
            ? m_segments[^1]
            : null);
        var segment = Rent();

        segment.Keyframe = m_chunks.Store(
            addedBytes: out var addedBytes,
            blob: bytes
        );
        segment.KeyframeLength = bytes.Length;
        segment.KeyframeHash = authoritativeHash;
        segment.KeyframeTick = tick;
        segment.DocumentDirectory = m_server.Definition.DocumentDirectory;
        segment.Fingerprint = WorldHistoryFingerprint.Of(server: m_server);
        m_segments.Add(item: segment);
        m_counters = (m_counters with {
            KeyframesCaptured = (m_counters.KeyframesCaptured + 1L),
            KeyframeBytesCaptured = (m_counters.KeyframeBytesCaptured + bytes.LongLength),
            KeyframeBytesStored = (m_counters.KeyframeBytesStored + addedBytes),
        });
        m_interval = IntervalFor(
            addedBytes: addedBytes,
            encodedBytes: bytes.LongLength
        );
        Reserve(
            intentsPerTick: (((previous is { Count: > 0 } last)
                ? ((last.IntentCount + (last.Count - 1)) / last.Count)
                : 1)),
            segment: segment,
            ticks: (m_interval + 1)
        );
        m_waiting = null;

        return true;
    }
    // Sizes a new span's per-tick arrays for its whole interval on the keyframe tick, which allocates anyway, so the
    // ticks that follow append without growing them.
    private static void Reserve(Segment segment, int ticks, int intentsPerTick) {
        if (segment.Hashes.Length < ticks) {
            segment.Hashes = new ulong[ticks];
            segment.StepTicks = new ulong[ticks];
            segment.IntentEnds = new int[ticks];
        }

        var intents = (ticks * Math.Max(
            val1: 1,
            val2: intentsPerTick
        ));

        if (segment.Intents.Length < intents) {
            segment.Intents = new IntentSubmission[intents];
        }
    }

    /// <summary>Notes a tick the capture did not see — a live drive stepping recorded input through the server's own
    /// doors. The window no longer describes the live timeline, so it is cleared; the first captured tick after the
    /// drive anchors a fresh one.</summary>
    /// <param name="reason">Why the tick went uncaptured, for the read-back.</param>
    internal void NoteUncapturedTick(string reason) {
        if (
            !m_on ||
            ((m_segments.Count == 0) && (m_waiting == reason))
        ) {
            return;
        }

        if (m_segments.Count > 0) {
            Narrate(text: $"[world.history: cleared — {reason}; the next captured tick anchors a new window]");
        }

        ClearWindow(waiting: reason);
    }
    /// <summary>Records one closed tick: its input, copied out of the capture's accumulators, and the authoritative
    /// hash it reached. The first tick after the history switches on (or after its window cleared) anchors the window
    /// at a keyframe instead; a tick taken while the cursor sits behind the head first drops the future it replaces,
    /// keeping it as a branch when one was named.</summary>
    /// <param name="input">The closed tick's input; read during this call only.</param>
    /// <param name="authoritativeHash">The authoritative hash the tick reached.</param>
    internal void NoteTick(in WorldReplayTickInput input, ulong authoritativeHash) {
        if (!m_on) {
            return;
        }

        var tick = (m_server.NextInputTick - 1UL);

        if (m_segments.Count == 0) {
            if (TryKeyframe(authoritativeHash: authoritativeHash, tick: tick)) {
                m_cursor = tick;
            }

            return;
        }

        if (tick != (m_cursor + 1UL)) {
            Narrate(text: $"[world.history: cleared — the live world moved from tick {m_cursor} to {tick} outside the history; a new window starts here]");
            ClearWindow(waiting: "the live timeline jumped");

            if (TryKeyframe(authoritativeHash: authoritativeHash, tick: tick)) {
                m_cursor = tick;
            }

            return;
        }

        if (m_cursor < Head) {
            BranchAtCursor();
        }

        Append(
            authoritativeHash: authoritativeHash,
            input: in input,
            stepTicks: m_server.Tick.LastStepTicks
        );
        m_cursor = tick;
        m_counters = (m_counters with {
            HashFolds = (m_counters.HashFolds + 1L),
            TicksRecorded = (m_counters.TicksRecorded + 1L),
        });

        if (m_segments[^1].Count >= m_interval) {
            _ = TryKeyframe(authoritativeHash: authoritativeHash, tick: tick);
        }

        EnforceBudget();
    }

    /// <summary>Switches the history off and releases everything it holds.</summary>
    public void Off() {
        if (!m_on) {
            return;
        }

        ClearWindow(waiting: "off");
        m_recycled.Clear();
        m_on = false;
        m_tape.DetachHistory();
    }
    /// <summary>Returns the history's read-back.</summary>
    /// <returns>The status.</returns>
    public WorldHistoryStatus Status() {
        var keyframeBytes = m_chunks.PayloadBytes;
        var encodedBytes = 0L;
        var inputBytes = 0L;

        foreach (var segment in m_segments) {
            keyframeBytes += WorldHistoryChunkStore.ReferenceCost(chunks: segment.Keyframe);
            encodedBytes += segment.KeyframeLength;
            inputBytes += segment.InputBytes;
        }

        var windowed = (m_segments.Count > 0);

        return new WorldHistoryStatus(
            BranchBytes: BranchBytes,
            Branches: [.. m_branches],
            BudgetBytes: m_budgetBytes,
            Counters: m_counters,
            Cursor: (windowed ? m_cursor : null),
            Head: (windowed ? Head : null),
            InputBytes: inputBytes,
            Interval: m_interval,
            KeyframeBytes: keyframeBytes,
            KeyframeEncodedBytes: encodedBytes,
            Keyframes: m_segments.Count,
            Oldest: (windowed ? Oldest : null),
            On: m_on,
            Waiting: (windowed ? null : m_waiting)
        );
    }
    /// <summary>Switches the history on with a memory budget, or changes the budget of a history already on (the
    /// oldest spans are evicted at the next tick when it shrank). The window anchors at the first keyframe the next
    /// closed tick captures.</summary>
    /// <param name="budgetBytes">The memory budget, in bytes.</param>
    /// <param name="refusal">Why the history did not switch on, when it did not.</param>
    /// <returns><see langword="true"/> when the history is on.</returns>
    public bool TryOn(long budgetBytes, out string refusal) {
        if (budgetBytes < MinimumBudgetBytes) {
            refusal = $"a budget of {budgetBytes} bytes is below the {MinimumBudgetBytes}-byte floor one keyframe span needs";

            return false;
        }

        m_budgetBytes = budgetBytes;
        refusal = string.Empty;

        if (m_on) {
            return true;
        }

        m_on = true;
        m_counters = default;
        m_interval = Math.Max(
            val1: 1,
            val2: (m_server.Definition.SimulationRateHz / 8)
        );
        m_waiting = "waiting for the next tick to capture the first keyframe";
        m_tape.AttachHistory(history: this);

        return true;
    }
}
