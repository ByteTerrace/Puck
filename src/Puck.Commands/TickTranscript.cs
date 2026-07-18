namespace Puck.Commands;

/// <summary>
/// An observation-only, fixed-capacity ring buffer of per-tick command activity — the console-addressable shape of
/// the same raw material the determinism machinery already computes internally (see
/// <see cref="DeterminismHarness"/> / <see cref="HashTrace"/>, which fold a scripted record/replay check into the
/// identical "per tick: what ran, what did the hash do" shape). Records, per tick: which commands dispatched (as
/// short caller-formatted text — see <see cref="RecordCommand"/>) and an OPTIONAL state-hash bracket. Nothing here
/// is read by simulation code and nothing it stores feeds a hash: recording a tick has zero effect on determinism.
/// <para>
/// Zero steady-state allocation: the ring array and every slot's fixed command list are allocated once, at
/// construction, and reused forever (a sealed slot is overwritten in place, never replaced). The per-call cost is a
/// string-reference write (<see cref="RecordCommand"/>) or, on <see cref="RecordTick"/>, an array copy bounded by
/// <see cref="MaxCommandsPerTick"/> — no list/array allocation on either path.
/// </para>
/// <para>
/// This type only assembles the shape; the host decides what "a tick" and "a command" mean. Call
/// <see cref="RecordCommand"/> any number of times to queue commands for the tick in progress, then
/// <see cref="RecordTick"/> exactly once to seal them (opening a fresh pending bucket) — typically from a per-tick
/// simulation hook that also has a cheap state-hash function on hand to sample before/after.
/// </para>
/// </summary>
public sealed class TickTranscript {
    /// <summary>The default ring capacity (ticks retained).</summary>
    public const int DefaultCapacity = 256;

    /// <summary>The maximum commands recorded per tick — see the type remarks. Provisioned for the 128-player
    /// vision's worst tick (every participant issuing a command the same tick, plus system margin); overflow past
    /// this is COUNTED, never silently lost, so the bound only limits debug visibility, never correctness.</summary>
    public const int MaxCommandsPerTick = 160;

    private readonly TickTranscriptEntry m_pending = new();
    private readonly TickTranscriptEntry[] m_ring;
    private int m_count;
    private int m_writeIndex;

    /// <summary>Initializes a new, empty transcript.</summary>
    /// <param name="capacity">The ring capacity (ticks retained); defaults to <see cref="DefaultCapacity"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than 1.</exception>
    public TickTranscript(int capacity = DefaultCapacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: capacity, other: 1);

        m_ring = new TickTranscriptEntry[capacity];

        for (var index = 0; (index < capacity); index++) {
            m_ring[index] = new TickTranscriptEntry();
        }
    }

    /// <summary>Gets the ring capacity (ticks retained).</summary>
    public int Capacity => m_ring.Length;

    /// <summary>Gets how many ticks are currently recorded (caps at <see cref="Capacity"/>).</summary>
    public int Count => m_count;

    /// <summary>Queues one command as having run during the tick currently being assembled — sealed by the next
    /// <see cref="RecordTick"/> call. Bounded per tick (see <see cref="MaxCommandsPerTick"/>); excess commands are
    /// counted (<see cref="TickTranscriptEntry.OverflowCount"/>) but not stored.</summary>
    /// <param name="text">A short, human-readable rendering of the command (e.g. <c>"boot 0"</c>) — this type
    /// stores exactly what it is given; formatting is the caller's job.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public void RecordCommand(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        m_pending.AddCommand(text: text);
    }

    /// <summary>Seals the currently-pending commands into a new ring slot for <paramref name="tick"/>, tagged with
    /// the optional hash bracket, then opens a fresh pending bucket. Call once per simulated tick.</summary>
    /// <param name="tick">The tick number just simulated.</param>
    /// <param name="hashBefore">The state hash sampled before the tick stepped, or <see langword="null"/> when
    /// unavailable / not sampled.</param>
    /// <param name="hashAfter">The state hash sampled after the tick stepped, or <see langword="null"/>.</param>
    /// <returns>The sealed entry (the ring slot just written — see <see cref="TickTranscriptEntry"/>'s reuse
    /// warning: it is overwritten in place on this transcript's next wrap-around).</returns>
    public TickTranscriptEntry RecordTick(ulong tick, ulong? hashBefore, ulong? hashAfter) {
        var slot = m_ring[m_writeIndex];

        slot.Seal(tick: tick, hashBefore: hashBefore, hashAfter: hashAfter, pending: m_pending);
        m_pending.Clear();

        m_writeIndex = ((m_writeIndex + 1) % m_ring.Length);
        m_count = Math.Min(val1: (m_count + 1), val2: m_ring.Length);

        return slot;
    }

    /// <summary>Copies the most recently recorded ticks, oldest first, into a new list.</summary>
    /// <param name="count">The maximum number of ticks to return (clamped to <c>[0, Count]</c>).</param>
    /// <returns>Up to <paramref name="count"/> entries, oldest first. Each entry is a LIVE ring slot — see
    /// <see cref="TickTranscriptEntry"/>'s reuse warning.</returns>
    public IReadOnlyList<TickTranscriptEntry> LastEntries(int count) {
        var take = Math.Clamp(value: count, max: m_count, min: 0);
        var result = new List<TickTranscriptEntry>(capacity: take);

        for (var offset = (take - 1); (offset >= 0); offset--) {
            var index = (((((m_writeIndex - 1) - offset) % m_ring.Length) + m_ring.Length) % m_ring.Length);

            result.Add(item: m_ring[index]);
        }

        return result;
    }
}
