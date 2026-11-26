namespace Puck.World.Client;

/// <summary>
/// Maps a client's seat clock (<see cref="WorldClient.SeatClockTicks"/>) onto the ticks of an authority in another
/// process, anchored afresh at every delivery the client sees. Presentation time, never simulation state.
/// </summary>
/// <remarks>
/// <para>The two clocks are not one clock: each process steps its own simulation and drops the steps it owes when a
/// burst of them overruns its wall time (the fixed-step pump's shedding), so over a long run either may fall any distance
/// behind the other. An offset fixed once, or refreshed only by the client's own answered input, walks away with every
/// step either side drops. This mapping is anchored instead at each delivered tick: a delivery of authority tick
/// <c>T</c> first seen at seat clock tick <c>c</c> says the authority stood at least at <c>T</c> when the seat clock read
/// <c>c</c>.</para>
/// <para>The invariant: the mapped tick is a delivered tick carried forward on the seat clock from a delivery seen within
/// the last <see cref="Window"/> seat clock ticks (the least delayed of them), so the two clocks are compared over at
/// most that span and a drift between them moves the mapping by no more than it drifts within it. What enforces it:
/// <see cref="Observe"/> forgets every anchor older than the window before it takes the greatest of the rest, and
/// nothing else writes them. The latest anchor is kept however old, so a stalled link still maps.</para>
/// <para>Every member runs on the thread that pumps and presents.</para>
/// </remarks>
public sealed class WorldAuthorityClock {
    /// <summary>The seat clock ticks the anchor is the least delayed delivery of: about half a second at thirty ticks a
    /// second, long enough for a jittered link to deliver one tick promptly, short enough that the clocks drift apart by
    /// little within it and that steps the authority dropped all at once are taken in within it.</summary>
    public const int Window = 16;

    // The anchors seen within the window, oldest first from m_head: each delivery's T - c and the c it was seen at. At
    // most one is noted per delivered tick, and a link delivers at most one tick per authority tick, so twice the window
    // holds every anchor the window can.
    private readonly (ulong Seen, long Anchor)[] m_anchors = new (ulong, long)[(2 * Window)];
    private int m_head;
    private int m_count;
    private ulong m_delivered;
    private long m_first;

    /// <summary>Gets whether a delivery has anchored the mapping.</summary>
    public bool IsAnchored => (m_count > 0);
    /// <summary>Gets the authority tick less the seat clock tick, in ticks: what <see cref="TickAt"/> adds; zero until
    /// anchored.</summary>
    public long Offset { get; private set; }
    /// <summary>Gets how far, in ticks, the authority's clock has moved against the seat clock since the first anchor:
    /// negative when the authority has fallen behind. The steps the two processes dropped, a reading only.</summary>
    public long Moved => (IsAnchored ? (Offset - m_first) : 0L);

    /// <summary>Notes the authority's latest delivered tick as the seat clock reads now. A tick already noted changes
    /// nothing, so a client may note on every step; a tick before the last one noted (the authority sought back or was
    /// restored) anchors the mapping afresh.</summary>
    /// <param name="deliveredTick">The authority's latest delivered tick.</param>
    /// <param name="clientTick">The seat clock, in ticks of the authority's rate; never before an earlier call's.</param>
    public void Observe(ulong deliveredTick, ulong clientTick) {
        if (IsAnchored && (deliveredTick == m_delivered)) {
            return;
        }

        var first = (!IsAnchored || (deliveredTick < m_delivered));

        if (first) {
            m_count = 0;
        }
        if (m_count == m_anchors.Length) {
            m_head = ((m_head + 1) % m_anchors.Length);
            m_count--;
        }
        m_anchors[((m_head + m_count) % m_anchors.Length)] = (clientTick, unchecked((long)deliveredTick - (long)clientTick));
        m_count++;
        m_delivered = deliveredTick;

        // Forgets every anchor seen before the window, keeping the one just noted.
        while ((m_count > 1) && ((clientTick - m_anchors[m_head].Seen) >= Window)) {
            m_head = ((m_head + 1) % m_anchors.Length);
            m_count--;
        }

        var greatest = long.MinValue;

        for (var index = 0; (index < m_count); index++) {
            greatest = Math.Max(val1: greatest, val2: m_anchors[((m_head + index) % m_anchors.Length)].Anchor);
        }
        Offset = greatest;
        if (first) {
            m_first = greatest;
        }
    }
    /// <summary>Returns the authority tick the seat clock maps to: the tick the authority stood at, as near as its
    /// deliveries show, when the seat clock read <paramref name="clientTick"/>.</summary>
    /// <param name="clientTick">The seat clock, in ticks of the authority's rate.</param>
    /// <returns>The authority tick; meaningful only once <see cref="IsAnchored"/>.</returns>
    public long TickAt(ulong clientTick) => unchecked((long)clientTick + Offset);
    /// <summary>Forgets every anchor, as the seat starts timing another authority.</summary>
    public void Reset() {
        m_head = 0;
        m_count = 0;
        m_delivered = 0UL;
        m_first = 0L;
        Offset = 0L;
    }
}
