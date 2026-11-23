using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The closed world-events vocabulary; machine-memory watches are
/// addon-scoped — each mounted guest declares its own watch rows, so it is computed inside
/// <see cref="IWorldAddonHost"/>'s implementation directly rather than here; see <c>Addons.WorldAddonRuntime</c>'s
/// remarks. Mirrors <c>Puck.Scripting.AddonAbi.ObservationVerbs</c>'s event verbs one-for-one.</summary>
public enum WorldEventFamily : byte {
    /// <summary>A body entered a named region.</summary>
    RegionEnter,
    /// <summary>A body left a named region.</summary>
    RegionExit,
    /// <summary>A seat became human-occupied.</summary>
    SeatJoin,
    /// <summary>A seat stopped being human-occupied.</summary>
    SeatLeave,
    /// <summary>Two bodies began overlapping (a proximity edge — see <see cref="WorldEventFeed"/>'s own remarks;
    /// this is not the physical contact resolver).</summary>
    CollisionBegin,
    /// <summary>Two bodies stopped overlapping.</summary>
    CollisionEnd,
    /// <summary>A route (possession/mirror/machine engagement) was established.</summary>
    RouteEngaged,
    /// <summary>A route was dissolved.</summary>
    RouteDisengaged,
    /// <summary>An authored <c>adjacencies</c> row received a delivered neighbour refresh after having been dropped
    /// — the federation seam is live again.</summary>
    LinkEstablished,
    /// <summary>An authored <c>adjacencies</c> row went <see cref="WorldAdjacency.LivenessGraceSeconds"/> without a
    /// delivered neighbour refresh — the federation seam is dark.</summary>
    LinkDropped,
}
/// <summary>One world-scoped event edge for this tick, in pinned iteration order. <see cref="GateA"/> (and,
/// for the two-body families, <see cref="GateB"/>) name the <see cref="GrantSubject"/>(s) an addon must hold an
/// event-budgeted <see cref="WorldCapability.Observe"/> grant over to receive it — either gate suffices when both
/// are present (the collision/route families' own "subject-filtered by the grant" rule).</summary>
/// <param name="Family">The event family.</param>
/// <param name="GateA">The first gating subject.</param>
/// <param name="GateB">The second gating subject, for two-body families; <see langword="null"/> for a one-subject
/// family (region, seat).</param>
/// <param name="A">The wire payload's first lane (a body/seat index, or an encoded route target).</param>
/// <param name="B">The wire payload's second lane (a region ordinal, or an encoded route target); zero when unused.</param>
public readonly record struct WorldEventEdge(WorldEventFamily Family, GrantSubject GateA, GrantSubject? GateB, long A, long B);
/// <summary>
/// Computes the world-scoped event families once per tick — collision pairs, region enter/exit, seat
/// join/leave, route/engagement transitions, and federation link established/dropped — as a flat, pinned-order edge
/// list every mounted addon's host filters by its own grants. All but link liveness are deterministic by construction:
/// body positions, seat occupancy, the document's region rows, and route commands are already sim-lane state, so
/// replay covers them by re-execution and nothing about them is taped. The link family is the one exception — see
/// its own paragraph below.
/// </summary>
/// <remarks>
/// <para><b>Collision pairs are a proximity test, not the physical contact resolver.</b>
/// <see cref="IContactField"/> resolves a body against static solid scene rows, screens, and creation placements, while
/// <see cref="WorldBodyContactMode.Solid"/> separately opts a kit into bounded dynamic-body depenetration. This feed
/// does not reuse either solver's admitted pair set: it asks <see cref="FixedDynamicBodyContacts.Overlaps"/>, the
/// contact narrowphase's own answer at zero margin, about selected pairs of the bodies' collider volumes in full 3D, so
/// an event never reports a pair the dynamic-body solver would not depenetrate nor misses one it would. A kit with no
/// collider has no event volume and therefore emits no collision pair. Discovery uses the broadphase body contacts use (<see cref="FixedSweepGrid"/>,
/// so both see the same candidates) under the authored <see cref="WorldCollisionEvents"/> limits; established pairs are
/// retained first. A saturated crowd therefore has
/// bounded work and memory and may omit lower-priority new proximity pairs. It is deliberately not a claim about
/// physical contact response.</para>
/// <para><b>Collision pairs are sensed only for listened bodies.</b> A collision edge reaches only an addon holding an
/// event-budgeted observe grant over one of its bodies, so a pair is sensed only when <see cref="WorldEventListeners"/>
/// names one of its bodies: every body under a wildcard grant, else the bodies the grants name. A crowd no grant
/// observes costs collision sensing nothing, and with named bodies the work follows their neighbourhoods. The listened
/// set is grant-table state, so the edges stay deterministic; it is checkpointed, and a body that joins it searches its
/// whole neighbourhood once.</para>
/// <para>Frozen bodies retain their region and overlap results. Collection visits awake/changed bodies and their
/// nearby frozen neighbours, in stable sweep/slot order. A changed region frame rechecks every occupant; a changed
/// collision policy reconsiders the complete listened image. Begin-budget work left over is resumed on later
/// collections.</para>
/// <para><b>Route edges are pushed, not diffed.</b> <see cref="WorldGrants"/> reports changes from the Control-route
/// storage every producer shares, including engagement, repair, and direct grant/revoke outcomes. The server resolves
/// a Seat/Peer principal to its body index and queues the corresponding edge; principals that drive no body produce no
/// fabricated source. <see cref="Collect"/> drains the queue at the pinned collection point so a route transition and
/// this tick's other edges arrive in the same batch.</para>
/// <para><b>Link edges are the one taped family.</b> Whether a federation neighbour delivered a refreshed projection
/// on a given tick is transport ingress, not sim state: nothing in the document or the population determines it, so
/// it cannot be re-derived. <see cref="ObserveLinkDelivery(string)"/> is the one input, called once per authored
/// adjacency row whose delivered snapshot tick advanced; the live path calls it from <c>WorldServer.Step</c> right
/// after the adjacency source freezes the tick's projection graph, and <c>WorldReplaySnapshot.Drive</c> calls it from
/// the tape's own <c>LinkDelivery</c> entries. Everything downstream — the per-tick count
/// <see cref="LinkStalenessTicks"/> reports, the grace comparison, and both edges — derives from that boolean plus
/// the local tick, so a taped run reproduces the identical edge sequence and the identical <c>$link:</c> values. The
/// tape does not carry the delivered CONTENT (poses, definition revisions): a replay reproduces WHEN a seam went
/// dark, never what the neighbour was showing.</para>
/// <para>Link edges gate on <see cref="GrantSubject.Adjacency"/> — the authored row's own name, <c>Region</c>'s twin
/// for the federation seam — so an addon receives them through an <c>observe adjacency:&lt;name&gt;</c> row carrying
/// an event budget, exactly as the region family gates on its placement id.</para>
/// </remarks>
public sealed class WorldEventFeed {
    private readonly List<WorldEventEdge> m_edges = [];
    private readonly List<WorldEventEdge> m_pendingRoutes = [];
    // Per-adjacency link liveness, keyed by the authored adjacency row name (the same "identity is the name, never a
    // reorderable ordinal" rule m_regionOccupancy follows).
    private readonly Dictionary<string, LinkState> m_links = new(comparer: StringComparer.Ordinal);
    private readonly bool[] m_seatOccupied = new bool[WorldBodiesLimits.LocalSeatCount];
    // Each local seat body's innermost place as of its latest region edge, published for presentation readers on other
    // threads (a string reference written whole); derived from the occupancy latches, so a checkpoint carries none.
    private readonly string?[] m_seatPlaces = new string?[WorldBodiesLimits.LocalSeatCount];
    private readonly bool[] m_seatPlacesStale = new bool[WorldBodiesLimits.LocalSeatCount];
    // Pairwise overlap state, keyed by the ascending (a, b) body-index pair. Its degree is bounded by the authored
    // collision-events policy, so a dense few-thousand-body crowd never becomes a quadratic table.
    private readonly HashSet<(int A, int B)> m_overlapping = [];
    // Region containment, per (regionId, bodyIndex). Keyed by region id (== the carrying placement's Id) rather
    // than a derived ordinal, so a document edit that reorders placements does not silently reinterpret stale state
    // under a reused ordinal — a region's identity is its name, exactly as GrantSubject.Region already keys it.
    private ulong m_occupancySequence;
    private sealed record RegionOccupancy(bool[] Bodies) {
        internal ulong Version { get; set; }
    }
    private readonly Dictionary<string, RegionOccupancy> m_regionOccupancy = new(comparer: StringComparer.Ordinal);
    internal ulong OccupancyVersion(string region) => m_regionOccupancy.GetValueOrDefault(region)?.Version ?? 0UL;

    private readonly FixedSweepSphere[] m_collisionBodies;
    private readonly FixedSweepGrid m_collisionGrid;
    private readonly byte[] m_collisionDegrees;
    private readonly bool[] m_bodyChanged;
    private readonly bool[] m_collisionIncluded;
    private readonly int[] m_nearbySleeping;
    private readonly int[] m_queryHits;

    // The bodies collision edges are collected for as of the latest collection: every body under m_listenAll, else
    // m_listenedBodies (ascending, distinct) with m_listened as its membership. A pair is sensed only when one of its
    // bodies is listened, so this is the only state that decides which pairs exist; it is checkpointed beside the
    // overlap table, and a body that joins it searches its whole neighbourhood once, as a policy edit does.
    private readonly bool[] m_listened;
    private List<int> m_listenedBodies = [];
    private List<int> m_listenScratch = [];
    private readonly List<int> m_freshListened = [];
    private bool m_listenAll;
    private readonly FixedSweepSphere[] m_listenSpheres;
    private readonly FixedSweepGrid m_listenGrid;

    // Only membership matters here: the complete candidate image is sorted by X/slot before any pair is visited.
    private readonly HashSet<int> m_collisionPending = [];
    private readonly Dictionary<string, (FixedSpatialShape Shape, bool HasCenter)> m_regionFrames = new(comparer: StringComparer.Ordinal);

    private WorldCollisionEvents? m_collisionPolicy;
    private WorldDefinition? m_regionDefinition;
    // m_regionDefinition's sensing placements in placement order (their region ordinals), so a tick visits its regions
    // and never every placement.
    private WorldPlaces m_places = WorldPlaces.None;
    // m_places' rows as the tick reads them, by region ordinal, filled as each row is first collected under
    // m_regionDefinition: the row's record in m_regionOccupancy, the frame it was last sensed in (m_regionFrames holds
    // the same frame by id for PlacesAt), and that frame's bounds grown by its slack. A body is tested against a region
    // only inside those bounds or while it is an occupant, so a tick's region work follows the bodies near each region
    // and not the product of regions and moved bodies. The grown bounds must hold every point the shape's own Contains
    // accepts: the slack, a sixty-fourth of the widest extent on every axis and a few thousandths of a unit, is far past the rounding by
    // which an oriented box's containment and its world extent can disagree. Restore replaces the occupancy records,
    // and drops m_regionDefinition so the rows are filled again.
    private RegionRow[] m_regionRows = [];
    private readonly FixedVector3[] m_changedPoints;
    private readonly bool[] m_changedPresent;

    private const int RegionBoundsShift = 6;
    private const long RegionBoundsSlackRaws = 256L;

    private struct RegionRow {
        public RegionOccupancy? Occupancy;
        public FixedSpatialShape Shape;
        public FixedAabb Bounds;
        public bool HasCenter;
        public bool Framed;
    }

    private readonly ulong[] m_collisionPairKeys;
    private readonly CollisionTransition[] m_collisionTransitions;
    private readonly int m_maximumTrackedPairs;

    /// <summary>Initializes a bounded event feed for one population.</summary>
    /// <param name="capacity">The population's authored body capacity.</param>
    public WorldEventFeed(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: capacity,
            other: WorldBodiesLimits.IndexLimit
        );

        m_maximumTrackedPairs = MaximumTrackedPairsForCapacity(capacity: capacity);
        m_collisionBodies = new FixedSweepSphere[capacity];
        m_collisionGrid = new FixedSweepGrid(capacity: capacity);
        m_collisionDegrees = new byte[capacity];
        m_bodyChanged = new bool[capacity];
        m_changedPoints = new FixedVector3[capacity];
        m_changedPresent = new bool[capacity];
        m_collisionIncluded = new bool[capacity];
        m_nearbySleeping = new int[capacity];
        m_queryHits = new int[capacity];
        m_listened = new bool[capacity];
        m_listenSpheres = new FixedSweepSphere[capacity];
        m_listenGrid = new FixedSweepGrid(capacity: capacity);
        m_collisionPairKeys = new ulong[m_maximumTrackedPairs];
        m_collisionTransitions = new CollisionTransition[checked((m_maximumTrackedPairs * 2))];
        _ = m_overlapping.EnsureCapacity(capacity: m_maximumTrackedPairs);
    }

    /// <summary>Gets the box-overlapping new-pair broadphase candidates inspected by the latest collection pass.</summary>
    public int CollisionCandidates { get; private set; }
    /// <summary>Gets the bodies whose new-pair search exhausted its candidate budget in the latest pass.</summary>
    public int CollisionLimitedBodies { get; private set; }
    /// <summary>Gets the established overlap pairs retained after the latest pass.</summary>
    public int CollisionTrackedPairs => m_overlapping.Count;
    /// <summary>Exact collision overlap tests performed by this tick's collection.</summary>
    public int CollisionOverlapTests { get; private set; }
    /// <summary>Gets the bodies the latest collection placed in its collision broadphase.</summary>
    public int CollisionBroadphaseBodies { get; private set; }
    /// <summary>Gets the bodies whose collision edges a held event-budgeted observe grant could deliver as of the latest
    /// collection: the population's capacity under a wildcard grant.</summary>
    public int CollisionListenedBodies { get; private set; }
    /// <summary>Gets the exact body/region containment tests this tick's collection performed: every body against a
    /// region whose frame changed, and otherwise each changed body inside a region's bounds or leaving it.</summary>
    public int RegionBodyTests { get; private set; }

    internal void AppendDiscoveryHash(ref Fnv1aHash hash) {
        hash.Add(value: ((byte)((m_collisionPolicy is null) ? 0 : 1)));
        if (m_collisionPolicy is { } policy) {
            hash.Add(value: policy.CandidateBudget);
            hash.Add(value: policy.MaxPairsPerBody);
            hash.Add(value: policy.BeginBudget);
        }
        hash.Add(value: ((byte)(m_listenAll ? 1 : 0)));
        hash.Add(value: m_listenedBodies.Count);
        foreach (var index in m_listenedBodies) { hash.Add(value: index); }
        hash.Add(value: m_collisionPending.Count);
        if (m_collisionPending.Count > 0) {
            for (var index = 0; (index < m_bodyChanged.Length); index++) {
                if (m_collisionPending.Contains(item: index)) { hash.Add(value: index); }
            }
        }
    }

    /// <summary>Gets this tick's collected edges, in pinned order (seats, then regions, then collisions, then links,
    /// then routes). Valid only between one <see cref="Collect"/> call and the next.</summary>
    public IReadOnlyList<WorldEventEdge> Edges => m_edges;

    /// <summary>Returns the absolute overlap-pair storage ceiling for a population capacity.</summary>
    internal static int MaximumTrackedPairsForCapacity(int capacity) =>
        checked(((capacity * WorldCollisionEvents.MaximumPairsPerBody) / 2));
    /// <summary>Preflights a checkpoint without changing this feed.</summary>
    internal void ValidateCheckpoint(WorldEventFeedCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        if (
            (checkpoint.Edges is null) ||
            (checkpoint.PendingRoutes is null) ||
            (checkpoint.SeatOccupied is null) ||
            (checkpoint.Overlapping is null) ||
            (checkpoint.RegionOccupancy is null) ||
            (checkpoint.Links is null) ||
            (checkpoint.CollisionPending is null) ||
            (checkpoint.CollisionListened is null)
        ) {
            throw new InvalidOperationException(message: "event feed checkpoint contains a null collection.");
        }
        if (checkpoint.SeatOccupied.Length != m_seatOccupied.Length) {
            throw new InvalidOperationException(message: $"event feed checkpoint carries {checkpoint.SeatOccupied.Length} seat latches; this world requires {m_seatOccupied.Length}.");
        }
        var previousPending = -1;

        if ((checkpoint.CollisionPolicy is { } policy) && ((policy.CandidateBudget < 0) || (policy.CandidateBudget > WorldCollisionEvents.MaximumCandidateBudget) ||
            (policy.MaxPairsPerBody < 0) || (policy.MaxPairsPerBody > WorldCollisionEvents.MaximumPairsPerBody) ||
            (policy.BeginBudget < 0) || (policy.BeginBudget > WorldCollisionEvents.MaximumBeginBudget))) {
            throw new InvalidOperationException(message: "event feed checkpoint carries an invalid collision discovery policy.");
        }
        foreach (var index in checkpoint.CollisionPending) {
            if ((index <= previousPending) || (((uint)index) >= ((uint)m_bodyChanged.Length))) {
                throw new InvalidOperationException(message: "event feed checkpoint carries invalid collision discovery work.");
            }
            previousPending = index;
        }
        var previousListened = -1;

        foreach (var index in checkpoint.CollisionListened) {
            if ((index <= previousListened) || (((uint)index) >= ((uint)m_listened.Length)) || checkpoint.CollisionListenAll) {
                throw new InvalidOperationException(message: "event feed checkpoint carries invalid collision listeners.");
            }
            previousListened = index;
        }
        if (checkpoint.Overlapping.Count > m_maximumTrackedPairs) {
            throw new InvalidOperationException(message: $"event feed checkpoint carries {checkpoint.Overlapping.Count} overlap pairs; this population admits at most {m_maximumTrackedPairs}.");
        }

        Array.Clear(array: m_collisionDegrees);
        var overlapCount = 0;

        foreach (var pair in checkpoint.Overlapping) {
            if (
                (pair.A < 0) ||
                (pair.A >= pair.B) ||
                (((uint)pair.B) >= ((uint)m_collisionDegrees.Length)) ||
                (++m_collisionDegrees[pair.A] > WorldCollisionEvents.MaximumPairsPerBody) ||
                (++m_collisionDegrees[pair.B] > WorldCollisionEvents.MaximumPairsPerBody)
            ) {
                throw new InvalidOperationException(message: $"event feed checkpoint carries invalid or over-capacity overlap pair ({pair.A}, {pair.B}).");
            }
            m_collisionPairKeys[overlapCount++] = CollisionPairKey(
                a: pair.A,
                b: pair.B
            );
        }
        Array.Sort(
            array: m_collisionPairKeys,
            index: 0,
            length: overlapCount
        );
        for (var index = 1; (index < overlapCount); index++) {
            if (m_collisionPairKeys[index] == m_collisionPairKeys[(index - 1)]) {
                var key = m_collisionPairKeys[index];

                throw new InvalidOperationException(message: $"event feed checkpoint duplicates overlap pair ({((int)PackedIntPair.High(value: key))}, {((int)PackedIntPair.Low(value: key))}).");
            }
        }

        var regionNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var row in checkpoint.RegionOccupancy) {
            if (
                string.IsNullOrWhiteSpace(value: row.Region) ||
                !regionNames.Add(item: row.Region)
            ) {
                throw new InvalidOperationException(message: $"event feed checkpoint carries an empty or repeated region name '{row.Region}'.");
            }
            if (
                (row.Occupancy is null) ||
                (row.Occupancy.Length != m_collisionBodies.Length)
            ) {
                throw new InvalidOperationException(message: $"event feed checkpoint region '{row.Region}' carries {(row.Occupancy?.Length ?? -1)} body latches; this world requires {m_collisionBodies.Length}.");
            }
        }

        var linkNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var link in checkpoint.Links) {
            if (
                string.IsNullOrWhiteSpace(value: link.Adjacency) ||
                !linkNames.Add(item: link.Adjacency) ||
                (link.StaleTicks < 0L)
            ) {
                throw new InvalidOperationException(message: $"event feed checkpoint carries invalid or repeated link state '{link.Adjacency}'.");
            }
        }
        foreach (var edge in checkpoint.Edges.Concat(second: checkpoint.PendingRoutes)) {
            if (!Enum.IsDefined(value: edge.Family)) {
                throw new InvalidOperationException(message: $"event feed checkpoint carries unknown event family '{edge.Family}'.");
            }
        }
    }

    private void AddCollisionTransition(int a, int b, bool now, ref int count) {
        m_collisionTransitions[count++] = new CollisionTransition(
            Key: CollisionPairKey(
                a: a,
                b: b
            ),
            Now: now
        );
    }
    private void CollectCollisions(WorldPopulation population, WorldCollisionEvents policy, ReadOnlySpan<int> changedBodies, WorldEventListeners listeners) {
        CollisionCandidates = 0;
        CollisionLimitedBodies = 0;
        CollisionOverlapTests = 0;
        CollisionBroadphaseBodies = 0;
        var policyChanged = (m_collisionPolicy != policy);

        m_collisionPolicy = policy;

        var everyoneJoined = UpdateListeners(capacity: population.Capacity, listeners: listeners);

        CollisionListenedBodies = (m_listenAll ? population.Capacity : m_listenedBodies.Count);
        // No listened body and no relationship left to end: nothing to sense, so the crowd costs nothing.
        if (!m_listenAll && (m_listenedBodies.Count == 0) && (m_overlapping.Count == 0)) {
            m_collisionPending.Clear();
            return;
        }
        Array.Clear(array: m_collisionDegrees);
        Array.Clear(array: m_collisionIncluded);

        var pairCount = 0;

        foreach (var pair in m_overlapping) {
            m_collisionPairKeys[pairCount++] = CollisionPairKey(
                a: pair.A,
                b: pair.B
            );
        }
        Array.Sort(
            array: m_collisionPairKeys,
            index: 0,
            length: pairCount
        );

        // Existing overlap relationships have continuity priority over new discoveries. Validate them in pair order,
        // retaining the first pairs admitted by a newly-lowered degree limit and ending the rest deterministically. A
        // pair neither of whose bodies is listened any longer ends.
        var transitionCount = 0;

        for (var ordinal = 0; (ordinal < pairCount); ordinal++) {
            var key = m_collisionPairKeys[ordinal];
            var a = ((int)PackedIntPair.High(value: key));
            var b = ((int)PackedIntPair.Low(value: key));
            var retained = (
                (policy.MaxPairsPerBody > 0) &&
                (Listened(index: a) || Listened(index: b)) &&
                (m_collisionDegrees[a] < policy.MaxPairsPerBody) &&
                (m_collisionDegrees[b] < policy.MaxPairsPerBody) &&
                population.IsActive(index: a) &&
                population.IsActive(index: b) &&
                (population.EntryBody(index: a) is { Collider: not null } bodyA) &&
                (population.EntryBody(index: b) is { Collider: not null } bodyB) &&
                ((!policyChanged && !m_bodyChanged[a] && !m_bodyChanged[b]) || TestOverlap(
                a: bodyA,
                b: bodyB
            ))
            );

            if (retained) {
                m_collisionDegrees[a]++;
                m_collisionDegrees[b]++;
            } else {
                _ = m_overlapping.Remove(item: (a, b));
                AddCollisionTransition(
                    a: a,
                    b: b,
                    count: ref transitionCount,
                    now: false
                );
            }
        }

        var bodyCount = 0;
        var begins = 0;

        if (policy.MaxPairsPerBody > 0) {
            if (m_listenAll) {
                DiscoverAll(changed: changedBodies, reconsider: (policyChanged || everyoneJoined));
            } else {
                DiscoverListened(changed: changedBodies, reconsider: policyChanged);
            }
        }
        CollisionBroadphaseBodies = bodyCount;

        // A spent begin budget leaves discovery to resume on later collections for every listened body with room.
        if ((policy.BeginBudget > 0) && (begins >= policy.BeginBudget)) {
            for (var ordinal = 0; (ordinal < bodyCount); ordinal++) {
                var index = m_collisionBodies[ordinal].Index;

                if ((m_collisionDegrees[index] < policy.MaxPairsPerBody) && Listened(index: index)) { m_collisionPending.Add(item: index); }
            }
        }

        Array.Sort(
            array: m_collisionTransitions,
            index: 0,
            length: transitionCount
        );
        for (var ordinal = 0; (ordinal < transitionCount); ordinal++) {
            var transition = m_collisionTransitions[ordinal];
            var a = ((int)PackedIntPair.High(value: transition.Key));
            var b = ((int)PackedIntPair.Low(value: transition.Key));

            m_edges.Add(item: new WorldEventEdge(
                Family: (transition.Now
                ? WorldEventFamily.CollisionBegin
                : WorldEventFamily.CollisionEnd),
                GateA: GrantSubject.Body(index: a),
                GateB: GrantSubject.Body(index: b),
                A: a,
                B: b
            ));
        }

        // Every body listened: the awake (changed) bodies, any pending work and the sleepers near each, swept once in
        // sweep order, so an unchanged body searches changed bodies only. The grid culls on every axis of the bodies'
        // bounding spheres before it charges a candidate, and the exact compound-volume test follows. Both the
        // inspected-candidate and retained-degree bounds hold even when every body occupies the same point.
        void DiscoverAll(ReadOnlySpan<int> changed, bool reconsider) {
            // A policy edit, or a wildcard listener arriving, can admit a previously unsensed unchanged pair.
            // Reconsider the complete image once for that edit, never on an ordinary tick.
            if (reconsider) {
                Array.Fill(array: m_bodyChanged, value: true);
                for (var index = 0; (index < population.Capacity); index++) { Include(index: index); }
            }
            foreach (var index in changed) { IncludeWithSleepers(index: index); }
            foreach (var index in m_collisionPending) { IncludeWithSleepers(index: index); }
            m_collisionPending.Clear();

            var grid = m_collisionGrid;

            grid.Rebuild(spheres: m_collisionBodies.AsSpan(start: 0, length: bodyCount));
            for (var leftOrdinal = 0; (leftOrdinal < bodyCount); leftOrdinal++) {
                if (begins >= policy.BeginBudget) {
                    break;
                }
                var left = grid.Sphere(ordinal: leftOrdinal);

                if (m_collisionDegrees[left.Index] >= policy.MaxPairsPerBody) {
                    continue;
                }
                grid.BeginPairs(activeOnly: !left.Active, candidateBudget: policy.CandidateBudget, leftOrdinal: leftOrdinal);
                while (grid.NextPair(rightOrdinal: out var rightOrdinal)) {
                    CollisionCandidates++;
                    if (!TryBegin(left: left.Index, right: grid.Sphere(ordinal: rightOrdinal).Index)) {
                        break;
                    }
                }
                if (grid.PairsLimited) {
                    CollisionLimitedBodies++;
                }
            }
        }

        // Only some bodies listened: the broadphase holds the listened bodies, the sleepers near each that changed, and
        // the changed bodies whose bounds meet a listened body's, found against a grid of the listened bodies alone. Each
        // listened body then queries its own bounds, so the work follows the listened bodies' neighbourhoods and an
        // unlistened crowd adds only one bounds test per changed body. A pair of two listened bodies is taken from its
        // lesser index; a pair of two unchanged bodies is never visited.
        void DiscoverListened(ReadOnlySpan<int> changed, bool reconsider) {
            foreach (var index in m_freshListened) { m_bodyChanged[index] = true; }
            if (reconsider) {
                foreach (var index in m_listenedBodies) { m_bodyChanged[index] = true; }
            }

            var listenCount = 0;
            var reach = FixedAabb.Empty;

            foreach (var index in m_listenedBodies) {
                if (!population.IsActive(index: index) || (population.EntryBody(index: index) is not { Collider: not null } body)) { continue; }
                var sphere = new FixedSweepSphere(Index: index, Center: body.FixedPosition, Radius: body.BroadphaseRadius);

                m_listenSpheres[listenCount++] = sphere;
                reach = reach.Enclose(other: sphere.Bounds);
                IncludeWithSleepers(index: index, sleepers: m_bodyChanged[index]);
            }
            if (listenCount == 0) {
                m_collisionPending.Clear();
                return;
            }
            m_listenGrid.Rebuild(spheres: m_listenSpheres.AsSpan(start: 0, length: listenCount));
            foreach (var index in changed) { IncludeNear(index: index); }
            foreach (var index in m_collisionPending) { IncludeNear(index: index); }
            m_collisionPending.Clear();

            var grid = m_collisionGrid;

            grid.Rebuild(spheres: m_collisionBodies.AsSpan(start: 0, length: bodyCount));
            foreach (var left in m_listenedBodies) {
                if (begins >= policy.BeginBudget) {
                    break;
                }
                if (!m_collisionIncluded[left] || (m_collisionDegrees[left] >= policy.MaxPairsPerBody)) {
                    continue;
                }
                var leftBody = population.EntryBody(index: left)!;
                var hits = grid.Query(box: FixedAabb.Around(center: leftBody.FixedPosition, radius: leftBody.BroadphaseRadius), destination: m_queryHits);
                var inspected = 0;

                for (var hit = 0; (hit < hits); hit++) {
                    var right = m_queryHits[hit];

                    if ((right == left) || (!m_bodyChanged[left] && !m_bodyChanged[right]) || (m_listened[right] && (right < left))) {
                        continue;
                    }
                    if (inspected >= policy.CandidateBudget) {
                        CollisionLimitedBodies++;
                        break;
                    }
                    inspected++;
                    CollisionCandidates++;
                    if (!TryBegin(left: left, right: right)) {
                        break;
                    }
                }
            }

            void IncludeNear(int index) {
                if (
                    m_listened[index] ||
                    m_collisionIncluded[index] ||
                    !population.IsActive(index: index) ||
                    (population.EntryBody(index: index) is not { Collider: not null } body)
                ) {
                    return;
                }
                var bounds = FixedAabb.Around(center: body.FixedPosition, radius: body.BroadphaseRadius);

                if (bounds.Intersects(other: reach) && (m_listenGrid.Query(box: bounds, destination: m_queryHits) > 0)) { Include(index: index); }
            }
        }

        void IncludeWithSleepers(int index, bool sleepers = true) {
            Include(index: index);
            if (sleepers && (population.EntryBody(index: index) is { } body) && population.IsActive(index: index)) {
                var nearby = population.NearbySleepingBodies(body: body, destination: m_nearbySleeping, reach: body.BroadphaseRadius);

                for (var neighbour = 0; (neighbour < nearby); neighbour++) { Include(index: m_nearbySleeping[neighbour]); }
            }
        }
        void Include(int index) {
            if (
                m_collisionIncluded[index] ||
                !population.IsActive(index: index) ||
                (population.EntryBody(index: index) is not { Collider: not null } body)
            ) {
                return;
            }
            m_collisionIncluded[index] = true;
            m_collisionBodies[bodyCount++] = new FixedSweepSphere(
                Index: index,
                Center: body.FixedPosition,
                Radius: body.BroadphaseRadius,
                Active: m_bodyChanged[index]
            );
        }
        // Begins the pair when both bodies have degree left and their volumes overlap; false once the begin budget is
        // spent.
        bool TryBegin(int left, int right) {
            if (
                (m_collisionDegrees[left] >= policy.MaxPairsPerBody) ||
                (m_collisionDegrees[right] >= policy.MaxPairsPerBody)
            ) {
                return true;
            }

            var a = Math.Min(
                val1: left,
                val2: right
            );
            var b = Math.Max(
                val1: left,
                val2: right
            );

            if (
                m_overlapping.Contains(item: (a, b)) ||
                !TestOverlap(
                a: population.EntryBody(index: left)!,
                b: population.EntryBody(index: right)!
            )
            ) {
                return true;
            }

            _ = m_overlapping.Add(item: (a, b));
            m_collisionDegrees[a]++;
            m_collisionDegrees[b]++;
            begins++;
            AddCollisionTransition(
                a: a,
                b: b,
                count: ref transitionCount,
                now: true
            );
            return (begins < policy.BeginBudget);
        }
        bool TestOverlap(WorldBody a, WorldBody b) {
            CollisionOverlapTests++;
            return Overlaps(a: a, b: b);
        }
    }
    private bool Listened(int index) => (m_listenAll || m_listened[index]);
    // Reads this collection's listeners and records which bodies joined them since the last; true when a wildcard
    // listener arrived, so every body joined at once.
    private bool UpdateListeners(WorldEventListeners listeners, int capacity) {
        var everyone = listeners.Listened(bodies: m_listenScratch, capacity: capacity);
        var wasEveryone = m_listenAll;

        m_freshListened.Clear();
        if (!everyone && !wasEveryone) {
            var previous = 0;

            foreach (var index in m_listenScratch) {
                while ((previous < m_listenedBodies.Count) && (m_listenedBodies[previous] < index)) { previous++; }
                if ((previous >= m_listenedBodies.Count) || (m_listenedBodies[previous] != index)) { m_freshListened.Add(item: index); }
            }
        }
        foreach (var index in m_listenedBodies) { m_listened[index] = false; }
        foreach (var index in m_listenScratch) { m_listened[index] = true; }
        (m_listenedBodies, m_listenScratch) = (m_listenScratch, m_listenedBodies);
        m_listenAll = everyone;
        return (everyone && !wasEveryone);
    }
    // The link pass. Sign/unit: StaleTicks counts SIMULATION ticks since the last delivered refresh, 0 on the tick a
    // refresh landed. A row whose compiled grace is zero (unauthored) is held at 0 and emits nothing, so an
    // unauthored world is unchanged; a grace with no tick mapping (a positive value at rate 0) accumulates staleness
    // but never drops.
    private void CollectLinks(WorldDefinition definition) {
        var ordinal = 0;

        foreach (var row in (definition.Adjacencies ?? [])) {
            if (row is null) {
                continue;
            }

            var thisOrdinal = ordinal++;
            var name = row.Name.Value;

            if (!m_links.TryGetValue(
                key: name,
                value: out var state
            )) {
                state = new LinkState();
                m_links[name] = state;
            }

            var grace = definition.AdjacencyLivenessGraceTicks(adjacency: row);

            if (grace.IsZero) {
                state.Dropped = false;
                state.PendingRefresh = false;
                state.StaleTicks = 0L;

                continue;
            }
            if (state.PendingRefresh) {
                state.PendingRefresh = false;
                state.StaleTicks = 0L;

                if (state.Dropped) {
                    state.Dropped = false;
                    m_edges.Add(item: new WorldEventEdge(
                        Family: WorldEventFamily.LinkEstablished,
                        GateA: GrantSubject.Adjacency(name: name),
                        GateB: null,
                        A: thisOrdinal,
                        B: 0L
                    ));
                }

                continue;
            }
            if (state.StaleTicks < long.MaxValue) {
                state.StaleTicks++;
            }
            if (
                grace.IsNever ||
                state.Dropped ||
                (state.StaleTicks < grace.Ticks)
            ) {
                continue;
            }

            state.Dropped = true;
            m_edges.Add(item: new WorldEventEdge(
                Family: WorldEventFamily.LinkDropped,
                GateA: GrantSubject.Adjacency(name: name),
                GateB: null,
                A: thisOrdinal,
                B: state.StaleTicks
            ));
        }
    }
    private void CollectRegions(WorldDefinition definition, WorldPopulation population, ReadOnlySpan<int> changedBodies) {
        if (!ReferenceEquals(objA: m_regionDefinition, objB: definition)) {
            m_regionFrames.Clear();
            m_regionDefinition = definition;
            m_places = WorldPlaces.Of(definition: definition);
            m_regionRows = new RegionRow[m_places.Rows.Length];
            Array.Fill(array: m_seatPlacesStale, value: true);
        }

        var rows = m_places.Rows;
        var regionRows = m_regionRows;

        // Each changed body's point, read once for every region.
        for (var changed = 0; (changed < changedBodies.Length); changed++) {
            var body = changedBodies[changed];

            m_changedPresent[changed] = (population.IsActive(index: body) && (population.EntryBody(index: body) is not null));
            m_changedPoints[changed] = (m_changedPresent[changed] ? population.EntryBody(index: body)!.FixedPosition : default);
        }

        for (var thisOrdinal = 0; (thisOrdinal < rows.Length); thisOrdinal++) {
            var (placement, region, staticShape) = rows[thisOrdinal];
            ref var row = ref regionRows[thisOrdinal];

            if (row.Occupancy is not { } occupancy) {
                if (!m_regionOccupancy.TryGetValue(
                    key: placement.Id,
                    value: out occupancy
                )) {
                    occupancy = new RegionOccupancy(new bool[population.Capacity]);
                    m_regionOccupancy[placement.Id] = occupancy;
                }

                row.Occupancy = occupancy;
            }

            // An ATTACHED region's sensing volume stands in the resolved live pose (the SAME fixed-point resolve
            // world.attachments answers), never the row's inert static Position — an inactive target body resolves
            // no center, so the region contributes nothing this tick (every occupant exits, mirroring the row's
            // established "contributes nothing" verdict) rather than sensing at a stale last-known point.
            var hasCenter = true;
            var shape = staticShape;

            if (placement.Attach is { } attach) {
                hasCenter = WorldPlacementAttachment.TryResolve(
                    attach: attach,
                    population: population,
                    position: out var center,
                    reason: out _,
                    yawRadians: out var yawRadians
                );
                shape = region.ShapeAt(origin: center, yawRadians: yawRadians);
            }

            var frameChanged = (!row.Framed || (row.HasCenter != hasCenter) || (row.Shape != shape));

            if (frameChanged) {
                var bounds = shape.Bounds;
                var extent = bounds.Extent;

                m_regionFrames[placement.Id] = (shape, hasCenter);
                row.Framed = true;
                row.HasCenter = hasCenter;
                row.Shape = shape;
                // Every axis grows by the widest extent's share: a long box's stored axes round across axes, so the
                // slack a thin axis needs follows the long one.
                row.Bounds = bounds.Grow(margin: FixedQ4816.FromRawBits(value: ((Math.Max(val1: extent.X.Value, val2: Math.Max(val1: extent.Y.Value, val2: extent.Z.Value)) >> RegionBoundsShift) + RegionBoundsSlackRaws)));
            }

            var bodyCount = (frameChanged ? population.Capacity : changedBodies.Length);

            for (var bodyOrdinal = 0; (bodyOrdinal < bodyCount); bodyOrdinal++) {
                var body = (frameChanged ? bodyOrdinal : changedBodies[bodyOrdinal]);
                bool inside;

                if (frameChanged) {
                    RegionBodyTests++;
                    inside = (
                        hasCenter &&
                        population.IsActive(index: body) &&
                        (population.EntryBody(index: body) is { } entry) &&
                        shape.Contains(point: entry.FixedPosition)
                    );
                } else {
                    // A changed body outside the region's bounds that was not an occupant has no edge to raise.
                    var near = (hasCenter && m_changedPresent[bodyOrdinal] && row.Bounds.Contains(point: m_changedPoints[bodyOrdinal]));

                    if (!near && !occupancy.Bodies[body]) {
                        continue;
                    }

                    RegionBodyTests++;
                    inside = (near && shape.Contains(point: m_changedPoints[bodyOrdinal]));
                }

                if (EmitTransition(
                    latch: ref occupancy.Bodies[body],
                    now: inside,
                    onClear: new WorldEventEdge(
                        Family: WorldEventFamily.RegionExit,
                        GateA: GrantSubject.Region(name: placement.Id),
                        GateB: null,
                        A: body,
                        B: thisOrdinal
                    ),
                    onSet: new WorldEventEdge(
                        Family: WorldEventFamily.RegionEnter,
                        GateA: GrantSubject.Region(name: placement.Id),
                        GateB: null,
                        A: body,
                        B: thisOrdinal
                    )
                )) {
                    occupancy.Version = ++m_occupancySequence;
                    if (body < m_seatPlaces.Length) { m_seatPlacesStale[body] = true; }
                }
            }
        }
        // A seat's innermost place is named again only on the tick one of its region edges fires.
        for (var seat = 0; (seat < m_seatPlaces.Length); seat++) {
            if (m_seatPlacesStale[seat]) {
                m_seatPlacesStale[seat] = false;
                Volatile.Write(location: ref m_seatPlaces[seat], value: ((PlacesOf(body: seat) is { Count: > 0 } places) ? places[0] : null));
            }
        }
    }
    private void CollectSeats(WorldPopulation population) {
        for (var seat = 0; (seat < WorldBodiesLimits.LocalSeatCount); seat++) {
            _ = EmitTransition(
                latch: ref m_seatOccupied[seat],
                now: population.IsHumanOccupied(bodyIndex: seat),
                onClear: new WorldEventEdge(
                    Family: WorldEventFamily.SeatLeave,
                    GateA: GrantSubject.Seat(index: seat),
                    GateB: null,
                    A: seat,
                    B: 0L
                ),
                onSet: new WorldEventEdge(
                    Family: WorldEventFamily.SeatJoin,
                    GateA: GrantSubject.Seat(index: seat),
                    GateB: null,
                    A: seat,
                    B: 0L
                )
            );
        }
    }
    private static ulong CollisionPairKey(int a, int b) => PackedIntPair.Pack(high: ((uint)a), low: ((uint)b));
    // The shared latched-transition body every per-tick edge-detection pass (collision overlap, region containment,
    // seat occupancy) follows: skip when `now` agrees with `latch`, else store `now` into `latch` and emit
    // `onSet`/`onClear`. `latch` names a bool[] slot at every call site except the collision pass's HashSet-backed
    // membership, which reads its latch into a local first and writes the set back only on a reported change.
    private bool EmitTransition(ref bool latch, bool now, WorldEventEdge onSet, WorldEventEdge onClear) {
        if (now == latch) {
            return false;
        }

        latch = now;
        m_edges.Add(item: (now
            ? onSet
            : onClear));

        return true;
    }
    // The overlap sense of two bodies is the contact narrowphase's own answer at zero margin, so an event never reports
    // a pair the solver does not depenetrate, nor misses one it does.
    private static bool Overlaps(WorldBody a, WorldBody b) =>
        ((a.Collider is not null) && (b.Collider is not null) && FixedDynamicBodyContacts.Overlaps(
            leftPosition: a.FixedPosition,
            leftOrientation: a.FixedOrientation,
            leftVolumes: a.ScaledColliderVolumes(),
            rightPosition: b.FixedPosition,
            rightOrientation: b.FixedOrientation,
            rightVolumes: b.ScaledColliderVolumes()
        ));
    private void QueueRoute(WorldEventFamily family, int sourceBody, GrantSubject target) {
        // Encoding (see AddonAbi.ObservationVerbs.EventRouteEngaged's own doc): B >= 0 is a screen index; B < 0 is
        // -(bodyIndex + 1). A screen index is always >= 0 and a body index is always >= 0, so the sign alone
        // disambiguates without a third payload lane.
        var encodedTarget = ((target.Kind == GrantSubjectKind.Body)
            ? -(target.Value + 1)
            : target.Value
        );
        var targetGate = ((target.Kind is GrantSubjectKind.Body or GrantSubjectKind.Screen)
            ? (GrantSubject?)target
            : null
        );

        m_pendingRoutes.Add(item: new WorldEventEdge(
            Family: family,
            GateA: GrantSubject.Body(index: sourceBody),
            GateB: targetGate,
            A: sourceBody,
            B: encodedTarget
        ));
    }

    /// <summary>Captures this feed's live state.</summary>
    public WorldEventFeedCheckpoint Capture() => new(
        Edges: [.. m_edges],
        PendingRoutes: [.. m_pendingRoutes],
        CollisionPolicy: m_collisionPolicy,
        CollisionPending: [.. m_collisionPending.Order()],
        CollisionListenAll: m_listenAll,
        CollisionListened: [.. m_listenedBodies],
        SeatOccupied: [.. m_seatOccupied],
        Overlapping: [.. m_overlapping.OrderBy(keySelector: static pair => pair.A).ThenBy(keySelector: static pair => pair.B)],
        RegionOccupancy: [.. m_regionOccupancy.Select(selector: static pair => (pair.Key, ((bool[])pair.Value.Bodies.Clone())))],
        Links: [.. m_links.Select(selector: static pair => new WorldEventLinkState(
                Adjacency: pair.Key,
                DeliveredTick: pair.Value.DeliveredTick,
                Dropped: pair.Value.Dropped,
                PendingRefresh: pair.Value.PendingRefresh,
                StaleTicks: pair.Value.StaleTicks
            ))]
    );
    /// <summary>Computes this tick's edge list — the one call site, from <see cref="WorldServer.Step"/> after the
    /// population advances (so positions/occupancy reflect this tick's settled state) and before the addon runtime's
    /// read pump stages them for delivery.</summary>
    /// <param name="definition">The live world definition (its placements, for region rows).</param>
    /// <param name="population">The live population (positions, activity, seat occupancy).</param>
    /// <param name="listeners">The bodies collision edges are collected for; a pair with no listened body is never
    /// sensed.</param>
    public void Collect(WorldDefinition definition, WorldPopulation population, WorldEventListeners listeners) {
        m_edges.Clear();
        RegionBodyTests = 0;
        Array.Clear(array: m_bodyChanged);
        var changedBodies = population.EventBodies();

        foreach (var index in changedBodies) { m_bodyChanged[index] = true; }
        foreach (var index in m_collisionPending) { m_bodyChanged[index] = true; }

        CollectSeats(population: population);
        CollectRegions(
            changedBodies: changedBodies,
            definition: definition,
            population: population
        );
        CollectCollisions(
            population: population,
            policy: definition.Collision.Events,
            changedBodies: changedBodies,
            listeners: listeners
        );
        CollectLinks(definition: definition);

        m_edges.AddRange(collection: m_pendingRoutes);
        m_pendingRoutes.Clear();
        population.CompleteBodyEvents();
        CountWork();
    }
    private void CountWork() {
        var work = WorldEventWork.Process;
        var collisionEdges = 0;

        foreach (var edge in m_edges) {
            if (edge.Family is WorldEventFamily.CollisionBegin or WorldEventFamily.CollisionEnd) { collisionEdges++; }
        }
        work.Add(kind: WorldEventWork.Collects, amount: 1L);
        work.Add(kind: WorldEventWork.CollisionListenedBodies, amount: CollisionListenedBodies);
        work.Add(kind: WorldEventWork.CollisionBroadphaseBodies, amount: CollisionBroadphaseBodies);
        work.Add(kind: WorldEventWork.CollisionCandidates, amount: CollisionCandidates);
        work.Add(kind: WorldEventWork.CollisionPairTests, amount: CollisionOverlapTests);
        work.Add(kind: WorldEventWork.CollisionOverlaps, amount: m_overlapping.Count);
        work.Add(kind: WorldEventWork.RegionBodyTests, amount: RegionBodyTests);
        work.Add(kind: WorldEventWork.Edges, amount: m_edges.Count);
        work.Add(kind: WorldEventWork.CollisionEdges, amount: collisionEdges);
    }
    /// <summary>Lists the regions (places) one body stands in as of the latest collection: the same occupancy latches
    /// region edges and <c>$region:</c> rule reads come from, innermost first (the smallest volume, so a room inside a
    /// wing lists before the wing), ties in placement order.</summary>
    /// <param name="body">The body's 0-based entity index.</param>
    /// <returns>The carrying placements' ids, empty when the body stands in none or the feed has not collected yet.</returns>
    public IReadOnlyList<string> PlacesOf(int body) => m_places.Holding(holds: (row, _) => IsOccupant(placementId: row.Placement.Id, body: body));
    /// <summary>Gets a local seat body's innermost place as of its latest region edge, safe to read from any thread:
    /// the reading a HUD binds (<c>seat.&lt;n&gt;.place</c>).</summary>
    /// <param name="seat">The 0-based local seat, which is its body index.</param>
    /// <returns>The innermost place's placement id, or <see langword="null"/> while the body stands in none.</returns>
    public string? SeatPlace(int seat) => ((((uint)seat) < ((uint)m_seatPlaces.Length)) ? Volatile.Read(location: ref m_seatPlaces[seat]) : null);
    /// <summary>Lists the regions (places) whose volume holds a point, in the frames the latest collection sensed them
    /// in, innermost first as <see cref="PlacesOf"/> orders them.</summary>
    /// <param name="point">The world point.</param>
    /// <returns>The carrying placements' ids, empty when no region holds the point or the feed has not collected yet.</returns>
    public IReadOnlyList<string> PlacesAt(FixedVector3 point) => m_places.Holding(holds: (row, _) => (
        m_regionFrames.TryGetValue(key: row.Placement.Id, value: out var frame) &&
        frame.HasCenter &&
        frame.Shape.Contains(point: point)
    ));
    /// <summary>Reads one region's occupancy two ways: the latches the region pass keeps, which <see cref="OccupantCount"/>
    /// and a rule's <c>region(...)</c> count, and the active bodies whose position the region's volume holds in the frame
    /// the latest collection sensed it in. Read between ticks, the two agree unless a body moved without the pass seeing
    /// it.</summary>
    /// <param name="placementId">The region-carrying placement's stable id.</param>
    /// <param name="population">The population the feed collects, whose bodies are tested.</param>
    /// <param name="latched">Receives the latched bodies' indices in ascending order, cleared first.</param>
    /// <param name="inside">Receives the indices of the active bodies the volume holds in ascending order, cleared first.</param>
    /// <returns><see langword="true"/> when the latest collection sensed the placement as a region; otherwise the lists
    /// stay empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="population"/>, <paramref name="latched"/> or
    /// <paramref name="inside"/> is <see langword="null"/>.</exception>
    public bool AuditRegion(string placementId, WorldPopulation population, List<int> latched, List<int> inside) {
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: latched);
        ArgumentNullException.ThrowIfNull(argument: inside);
        latched.Clear();
        inside.Clear();
        if (!m_regionFrames.TryGetValue(key: placementId, value: out var frame)) {
            return false;
        }
        if (m_regionOccupancy.TryGetValue(key: placementId, value: out var occupancy)) {
            for (var body = 0; (body < occupancy.Bodies.Length); body++) {
                if (occupancy.Bodies[body]) { latched.Add(item: body); }
            }
        }
        for (var body = 0; (frame.HasCenter && (body < population.Capacity)); body++) {
            if (population.IsActive(index: body) && (population.EntryBody(index: body) is { } entry) && frame.Shape.Contains(point: entry.FixedPosition)) {
                inside.Add(item: body);
            }
        }

        return true;
    }
    /// <summary>Reports whether one body is inside a placement's region as of this tick's collection.</summary>
    /// <param name="placementId">The placement carrying the region facet.</param>
    /// <param name="body">The body's 0-based entity index.</param>
    /// <returns><see langword="true"/> when the body is an occupant.</returns>
    public bool IsOccupant(string placementId, int body) => (
        m_regionOccupancy.TryGetValue(
        key: placementId,
        value: out var occupancy
    ) &&
        (((uint)body) < ((uint)occupancy.Bodies.Length)) &&
        occupancy.Bodies[body]
    );
    /// <summary>Returns whether the adjacency row named <paramref name="adjacencyName"/> is currently dropped — the
    /// link pass's own latched verdict as of the most recent <see cref="Collect"/> (set on the tick the staleness
    /// reaches the row's compiled <c>livenessGraceSeconds</c>, cleared on the next delivered refresh), so a consumer
    /// asks the one comparison this feed owns instead of re-spelling it from <see cref="LinkStalenessTicks"/> and the
    /// compiled grace. <see langword="false"/> for a row whose sensing is disabled (an unauthored grace), a row with
    /// no tick mapping (a positive grace at simulation rate 0), and a row this feed has never seen — exactly the
    /// cases the pass itself never drops.</summary>
    /// <param name="adjacencyName">The authored <c>adjacencies</c> row name.</param>
    /// <returns>Whether the row's last emitted edge state is dropped.</returns>
    public bool LinkDropped(string adjacencyName) => (m_links.TryGetValue(
        key: adjacencyName,
        value: out var state
    ) && state.Dropped);
    /// <summary>Returns the simulation ticks since the adjacency row named <paramref name="adjacencyName"/> last
    /// received a delivered neighbour refresh, as of the most recent <see cref="Collect"/> — the live quantity a
    /// world rule's <c>$link:&lt;adjacencyName&gt;</c> reserved channel reads (see <c>WorldRuleFacts.LinkPrefix</c>).
    /// </summary>
    /// <param name="adjacencyName">The authored <c>adjacencies</c> row name.</param>
    /// <returns>The staleness in simulation ticks; <c>0</c> for a fresh edge, for an edge whose
    /// <c>livenessGraceSeconds</c> is unauthored, and for a row this feed has never seen.</returns>
    public long LinkStalenessTicks(string adjacencyName) => (m_links.TryGetValue(
        key: adjacencyName,
        value: out var state
    )
        ? state.StaleTicks
        : 0L
    );
    /// <summary>Records that the adjacency row named <paramref name="adjacencyName"/> received a delivered neighbour
    /// refresh, to be consumed by the next <see cref="Collect"/>. The one link-liveness input — see this type's own
    /// remarks for why it is taped rather than re-derived.</summary>
    /// <param name="adjacencyName">The authored <c>adjacencies</c> row name.</param>
    public void ObserveLinkDelivery(string adjacencyName) {
        ArgumentException.ThrowIfNullOrEmpty(argument: adjacencyName);

        if (!m_links.TryGetValue(
            key: adjacencyName,
            value: out var state
        )) {
            state = new LinkState();
            m_links[adjacencyName] = state;
        }

        state.PendingRefresh = true;
    }
    /// <summary>Records a delivered neighbour image for <paramref name="adjacencyName"/> and reports whether it is a
    /// REFRESH — a strictly higher neighbour snapshot tick than any previously observed on this edge. The live
    /// path's entry point: a repeated or reordered delivery of the same neighbour tick is not a refresh and must not
    /// re-arm the link (nor reach the tape).</summary>
    /// <param name="adjacencyName">The authored <c>adjacencies</c> row name.</param>
    /// <param name="deliveredTick">The neighbour's own simulation tick on the delivered image.</param>
    /// <returns><see langword="true"/> when this delivery advanced the edge.</returns>
    public bool ObserveLinkDelivery(string adjacencyName, ulong deliveredTick) {
        ArgumentException.ThrowIfNullOrEmpty(argument: adjacencyName);

        if (!m_links.TryGetValue(
            key: adjacencyName,
            value: out var state
        )) {
            state = new LinkState();
            m_links[adjacencyName] = state;
        }
        if (deliveredTick <= state.DeliveredTick) {
            return false;
        }

        state.DeliveredTick = deliveredTick;
        state.PendingRefresh = true;

        return true;
    }
    /// <summary>Returns the live occupant count of the placement region named <paramref name="placementId"/> as of the most
    /// recent <see cref="Collect"/> — the same per-(region, body) occupancy the region pass already tracks for the
    /// addon events feed, read directly for a world rule's <c>$region:&lt;id&gt;</c> reserved channel (see
    /// <see cref="RuleFacts"/>) rather than duplicated.</summary>
    /// <param name="placementId">The region-carrying placement's stable id.</param>
    /// <returns>The occupant count, or zero for a placement this tick has never seen carry a region.</returns>
    public int OccupantCount(string placementId) {
        if (!m_regionOccupancy.TryGetValue(
            key: placementId,
            value: out var occupancy
        )) {
            return 0;
        }

        var count = 0;

        foreach (var occupied in occupancy.Bodies) {
            if (occupied) {
                count++;
            }
        }

        return count;
    }
    /// <summary>Queues a route-disengaged edge after <see cref="WorldGrants"/> reports an effective route change.</summary>
    /// <param name="sourceBody">The routed body's entity index.</param>
    /// <param name="target">The route target that was cleared.</param>
    public void QueueRouteDisengaged(int sourceBody, GrantSubject target) {
        QueueRoute(
            family: WorldEventFamily.RouteDisengaged,
            sourceBody: sourceBody,
            target: target
        );
    }
    /// <summary>Queues a route-engaged edge after <see cref="WorldGrants"/> reports an effective route change.
    /// Drained by the next <see cref="Collect"/> call.</summary>
    /// <param name="sourceBody">The routed body's entity index.</param>
    /// <param name="target">The route target (a screen or a body).</param>
    public void QueueRouteEngaged(int sourceBody, GrantSubject target) {
        QueueRoute(
            family: WorldEventFamily.RouteEngaged,
            sourceBody: sourceBody,
            target: target
        );
    }
    /// <summary>Restores this feed's live state from a previously captured checkpoint.</summary>
    public void Restore(WorldEventFeedCheckpoint checkpoint) {
        ValidateCheckpoint(checkpoint: checkpoint);
        m_regionFrames.Clear();
        m_regionDefinition = null;
        m_collisionPolicy = checkpoint.CollisionPolicy;
        m_collisionPending.Clear();
        foreach (var index in checkpoint.CollisionPending) { m_collisionPending.Add(item: index); }
        foreach (var index in m_listenedBodies) { m_listened[index] = false; }
        m_listenedBodies.Clear();
        m_listenedBodies.AddRange(collection: checkpoint.CollisionListened);
        foreach (var index in m_listenedBodies) { m_listened[index] = true; }
        m_listenAll = checkpoint.CollisionListenAll;

        m_edges.Clear();
        m_edges.AddRange(collection: checkpoint.Edges);
        m_pendingRoutes.Clear();
        m_pendingRoutes.AddRange(collection: checkpoint.PendingRoutes);
        m_links.Clear();
        foreach (var link in checkpoint.Links) {
            m_links[link.Adjacency] = new LinkState {
                DeliveredTick = link.DeliveredTick,
                Dropped = link.Dropped,
                PendingRefresh = link.PendingRefresh,
                StaleTicks = link.StaleTicks,
            };
        }
        checkpoint.SeatOccupied.CopyTo(
            array: m_seatOccupied,
            index: 0
        );

        m_overlapping.Clear();
        foreach (var pair in checkpoint.Overlapping) {
            _ = m_overlapping.Add(item: pair);
        }
        m_regionOccupancy.Clear();
        foreach (var row in checkpoint.RegionOccupancy) {
            m_regionOccupancy[row.Region] = new RegionOccupancy((bool[])row.Occupancy.Clone()) { Version = ++m_occupancySequence };
        }
        // The restored latches may name other places; each seat's is named again at the next collection.
        Array.Fill(array: m_seatPlacesStale, value: true);
    }

    private readonly record struct CollisionTransition(ulong Key, bool Now) : IComparable<CollisionTransition> {
        public int CompareTo(CollisionTransition other) => Key.CompareTo(value: other.Key);
    }
    // One authored adjacency row's liveness. Reference type deliberately: the collection pass mutates in place, and a
    // struct in a Dictionary would need a re-store per field write.
    private sealed class LinkState {
        public ulong DeliveredTick;
        public bool Dropped;
        public bool PendingRefresh;
        public long StaleTicks;
    }
}
