using Puck.Abstractions.Collections;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Selects the bodies an instance's boundary scan (its seam and portal faces, <c>WorldInstanceHost</c>'s
/// <c>ScanInstanceBoundaries</c>) visits on a tick: the bodies whose answer at a face can differ from the one the last
/// scan gave.</summary>
/// <remarks><para>A face's answer for a body is a function of the body's swept segment (its previous and current scan
/// origins), whether it can travel, the face, the instance's portal latches and refusals, the seats a queued crossing
/// holds and the body's retained arrival border. A body whose segment and travel stand, that holds no latch, refusal,
/// pending continuum or arrival border, and that the last scan found past no face (neither inside nor crossed) answers
/// outside every face again, which changes nothing, so the scan passes it by. A body at rest beyond a face answers
/// crossed on every scan, which the crossing hold (<c>HeldCrossingSeats</c>) relies on, so a body found past a face is
/// visited on the next scan too.</para>
/// <para>Every body is visited after anything else an answer reads may have moved: a new definition (its faces) or
/// population, a capacity change, a tick at or before the last scanned one (a rewind, a seek back, a first scan), and a
/// hold released since the last scan. A forward jump (several steps between scans, a restore ahead) needs none: what it
/// moved is compared slot by slot, and a restored latch or border selects its body. The portal scan adds its own: a door that opened or closed and an arrival
/// latch seeded since. The selection reads each slot's travel and segment once, so a tick costs a pass over the slots
/// and a face test only for the bodies selected.</para></remarks>
internal sealed class WorldBoundaryScan {
    private FixedVector3[] m_from = [];
    private FixedVector3[] m_to = [];
    private bool[] m_traveller = [];
    private int[] m_seats = [];
    private int m_count;
    private int[] m_all = [];
    private readonly MarkedIndexSet m_found = new();
    private readonly HashSet<int> m_held = [];
    private List<WorldAdjacencyFaceHit>?[] m_candidates = [];
    private object? m_definition;
    private object? m_population;
    private ulong m_tick;
    private long m_closures = -1;

    /// <summary>Gets the bodies <see cref="Select"/> chose, in ascending order.</summary>
    public ReadOnlySpan<int> Seats => m_seats.AsSpan(start: 0, length: m_count);
    /// <summary>Gets the seam sweep's per-body hits, reused across scans; the scan clears the entries it filled.</summary>
    public List<WorldAdjacencyFaceHit>?[] Candidates => m_candidates;

    /// <summary>Chooses the bodies this tick's scan visits and forgets which ones the last scan found past a face.</summary>
    /// <param name="instance">The scanned instance.</param>
    /// <param name="held">The seats a queued or in-flight crossing holds now.</param>
    /// <returns><see langword="true"/> when every body is visited.</returns>
    public bool Select(WorldInstance instance, IReadOnlyDictionary<int, ulong> held) {
        var server = instance.Server;
        var population = server.Population;
        var capacity = population.Capacity;
        var tick = instance.CompletedTicks;
        var released = false;

        foreach (var seat in m_held) { released |= !held.ContainsKey(key: seat); }
        var all = (released || !ReferenceEquals(objA: m_definition, objB: server.Configuration) || !ReferenceEquals(objA: m_population, objB: population) ||
            (m_traveller.Length != capacity) || (tick <= m_tick));

        if (m_traveller.Length != capacity) {
            m_from = new FixedVector3[capacity];
            m_to = new FixedVector3[capacity];
            m_traveller = new bool[capacity];
            m_seats = new int[capacity];
            m_candidates = new List<WorldAdjacencyFaceHit>?[capacity];
        }
        var occupancy = instance.PortalOccupancy;
        var borders = (server.TransferArrivalBorders != 0);

        m_count = 0;
        for (var seat = 0; (seat < capacity); seat++) {
            var traveller = WorldAdjacencyOwnership.TryTraveller(body: out var body, index: seat, population: population);
            var (from, to) = (traveller ? (body!.FixedPreviousPosition, body.FixedPosition) : default);
            var visit = (all || (traveller != m_traveller[seat]) || (from != m_from[seat]) || (to != m_to[seat]) ||
                m_found.Contains(index: seat) || occupancy.Holds(seat: seat) || occupancy.HoldsRefusal(seat: seat) ||
                (traveller && (body!.PendingContinuum is not null)) || (borders && server.TryTransferArrivalBorder(bodyIndex: seat, border: out _)));

            m_traveller[seat] = traveller;
            m_from[seat] = from;
            m_to[seat] = to;
            if (visit) { m_seats[m_count++] = seat; }
        }
        m_found.Clear();
        m_held.Clear();
        foreach (var seat in held.Keys) { _ = m_held.Add(item: seat); }
        m_definition = server.Configuration;
        m_population = population;
        m_tick = tick;

        return all;
    }
    /// <summary>Gets every body of a population of <paramref name="capacity"/> slots, in ascending order: what the
    /// portal scan visits when a door moved or an arrival latch was seeded since the last scan.</summary>
    /// <param name="capacity">The population's capacity.</param>
    /// <returns>The slots 0 through <paramref name="capacity"/> less one.</returns>
    public ReadOnlySpan<int> All(int capacity) {
        if (m_all.Length < capacity) {
            var all = new int[capacity];
            for (var seat = 0; (seat < capacity); seat++) { all[seat] = seat; }
            m_all = all;
        }
        return m_all.AsSpan(start: 0, length: capacity);
    }
    /// <summary>Reports whether a door opened or closed since the last scan, and records the closures' revision.</summary>
    /// <param name="revision">The population's portal closures' revision (<see cref="WorldPortalClosures.Revision"/>).</param>
    /// <returns><see langword="true"/> when the revision moved since the last scan read it.</returns>
    public bool ClosuresMoved(long revision) {
        var moved = (revision != m_closures);
        m_closures = revision;
        return moved;
    }
    /// <summary>Records that a body answered inside or crossed at a face, so the next scan visits it.</summary>
    /// <param name="seat">The body's index.</param>
    public void Found(int seat) => _ = m_found.Add(index: seat);
}
