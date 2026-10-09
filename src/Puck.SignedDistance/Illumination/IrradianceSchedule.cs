namespace Puck.SignedDistance.Illumination;

/// <summary>A bounding sphere in world units; an infinite radius bounds a world segment no finite sphere holds.</summary>
/// <param name="Center">The centre, in world units.</param>
/// <param name="Radius">The radius, in world units.</param>
public readonly record struct IrradianceSphere(Double3 Center, double Radius);
/// <summary>Why a probe stratum is traced.</summary>
public enum IrradianceUpdateReason {
    /// <summary>The probe's brick was newly allocated.</summary>
    Demand = 0,
    /// <summary>Geometry changed within the probe's reach.</summary>
    Geometry = 1,
    /// <summary>The probe lacks a later stratum.</summary>
    Converge = 2,
}
/// <summary>One stratum of one probe to trace this frame.</summary>
/// <param name="Probe">The probe.</param>
/// <param name="Stratum">The stratum.</param>
/// <param name="Reason">Why it is traced.</param>
public readonly record struct IrradianceUpdate(IrradianceProbeKey Probe, int Stratum, IrradianceUpdateReason Reason);
/// <summary>What one frame's schedule sees.</summary>
/// <param name="Cameras">The positions of every view's camera reading the cache, in world units.</param>
/// <param name="Bounds">The instance bounds and world segments the cache's surfaces lie within.</param>
/// <param name="WorldMin">The least corner of the box no allocation leaves, in world units.</param>
/// <param name="WorldMax">The greatest corner of that box, in world units.</param>
public sealed record IrradianceFrameInputs(IReadOnlyList<Double3> Cameras, IReadOnlyList<IrradianceSphere> Bounds, Double3 WorldMin, Double3 WorldMax);
/// <summary>One frame's schedule: the brick table's changes and the ordered work list.</summary>
/// <param name="Allocated">The bricks newly allocated, in key order.</param>
/// <param name="Evicted">The bricks evicted, in key order.</param>
/// <param name="Refused">The bricks demanded but refused because their level's pool is full, in key order.</param>
/// <param name="Classified">The bricks to classify and partition this frame, in priority order.</param>
/// <param name="Traces">The probe strata to trace this frame, in priority order.</param>
public sealed record IrradianceFramePlan(IReadOnlyList<IrradianceBrickKey> Allocated, IReadOnlyList<IrradianceBrickKey> Evicted, IReadOnlyList<IrradianceBrickKey> Refused, IReadOnlyList<IrradianceBrickKey> Classified, IReadOnlyList<IrradianceUpdate> Traces) {
    /// <summary>Gets the bricks whose probe placements precede this frame's partitions.</summary>
    public IReadOnlyList<IrradianceBrickKey> Placed { get; init; } = [];
}
/// <summary>One queued change of geometry.</summary>
/// <param name="Sphere">The bounds the change lies within.</param>
/// <param name="Near">Whether only near transport sees it: a moving caster under the static far field.</param>
public readonly record struct IrradianceGeometryChange(IrradianceSphere Sphere, bool Near);
/// <summary>The transport an allocation still owes.</summary>
/// <param name="Placements">Bricks whose probes are not placed.</param>
/// <param name="Partitions">Bricks whose cells are not partitioned.</param>
/// <param name="Strata">Probe strata not traced.</param>
public readonly record struct IrradianceRemainingWork(int Placements, int Partitions, int Strata);
/// <summary>One plan's admission: an allowance and the price of each kind's item against it, in the same unit (the
/// residency prices instruction visits). A plan always admits at least one item of each kind it has work for.</summary>
/// <param name="Allowance">The plan's combined allowance.</param>
/// <param name="Place">One brick's placement.</param>
/// <param name="Classify">One brick's partition.</param>
/// <param name="Trace">One probe stratum's trace.</param>
public readonly record struct IrradiancePlanPrices(long Allowance, long Place, long Classify, long Trace) {
    /// <summary>Gets the prices in counted field evaluations, the schedule's own item bounds.</summary>
    /// <param name="allowance">The evaluation allowance.</param>
    /// <returns>The prices.</returns>
    public static IrradiancePlanPrices Evaluations(long allowance) =>
        new(Allowance: allowance, Classify: IrradianceSchedule.ClassifyEvaluations, Place: IrradianceSchedule.PlaceEvaluations, Trace: IrradianceSchedule.TraceEvaluations);
}
/// <summary>
/// The host's schedule for a residency's cache: which bricks each level allocates from its pool, which it classifies and
/// which probe strata it traces each frame, within fixed budgets. It is a pure function of its inputs and its own state:
/// the order cameras and bounds arrive in changes nothing, ties end in the lattice key, and a first stratum anywhere is
/// traced before a later stratum anywhere, so new demand never waits behind convergence.
/// </summary>
public sealed class IrradianceSchedule {
    /// <summary>Field evaluations for one brick's placement samples and gradients.</summary>
    public const int PlaceEvaluations = (SdfIndirectLayout.ProbesPerBrick * 3);
    /// <summary>Field evaluations for all directed partition segments of one brick.</summary>
    public const int ClassifyEvaluations = (((SdfIndirectLayout.ProbesPerBrick * IrradianceLattice.CellSegments) * 2) * SdfIndirectLayout.SegmentSteps);
    /// <summary>Field evaluations for one stratum, its hit gradients and feedback proofs.</summary>
    public const int TraceEvaluations = (IrradianceLattice.RaysPerStratum * ((SdfIndirectLayout.TraceSteps + 1) + SdfIndirectLayout.FeedbackSteps));
    /// <summary>The spacings past a level's near reach that its hits' launch and feedback proofs read: the hit's cell's
    /// farthest corner and the launch above it.</summary>
    public const double NearProofSpacings = 2.0;

    private sealed class Brick {
        public bool Classified { get; set; }
        public bool Placed { get; set; }
        public IrradianceUpdateReason Reason { get; set; }
        public bool[] Traced { get; init; } = [];
    }

    private readonly IReadOnlyList<IrradianceLevel> m_levels;
    private readonly IReadOnlyList<int> m_pools;
    private readonly int m_traceBudget;
    private readonly int m_classifyBudget;
    private readonly double m_exitDistance;
    private readonly SortedDictionary<IrradianceBrickKey, Brick> m_bricks = [];

    private DemandSnapshot? m_demand;

    private sealed record DemandSnapshot(IrradianceFrameInputs Inputs, SortedSet<IrradianceBrickKey> Desired,
        Dictionary<IrradianceBrickKey, double> Ranks, IReadOnlyList<IrradianceBrickKey> Refused);

    /// <summary>Initializes a new instance of the <see cref="IrradianceSchedule"/> class.</summary>
    /// <param name="levels">The levels, finest first.</param>
    /// <param name="pools">Each level's pool, in bricks.</param>
    /// <param name="traceBudget">The probe strata a frame traces at most.</param>
    /// <param name="classifyBudget">The bricks a frame classifies at most.</param>
    /// <param name="exitDistance">The far distance, in world units, the coarsest level's rays reach.</param>
    /// <exception cref="ArgumentNullException"><paramref name="levels"/> or <paramref name="pools"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="pools"/> does not name one pool a level.</exception>
    public IrradianceSchedule(IReadOnlyList<IrradianceLevel> levels, IReadOnlyList<int> pools, int traceBudget, int classifyBudget, double exitDistance) {
        ArgumentNullException.ThrowIfNull(argument: levels);
        ArgumentNullException.ThrowIfNull(argument: pools);

        if (pools.Count != levels.Count) {
            throw new ArgumentException(message: "Each level names one pool.", paramName: nameof(pools));
        }

        m_levels = levels.ToArray();
        m_pools = pools.ToArray();
        m_traceBudget = traceBudget;
        m_classifyBudget = classifyBudget;
        m_exitDistance = exitDistance;
    }

    /// <summary>Gets the bricks allocated, in key order.</summary>
    public IReadOnlyList<IrradianceBrickKey> Allocated => m_bricks.Keys.ToArray();
    /// <summary>Gets whether every allocated brick is classified and every probe stratum traced.</summary>
    public bool IsComplete => m_bricks.Values.All(predicate: static brick => (brick.Classified && brick.Traced.All(predicate: static traced => traced)));
    /// <summary>Gets the work the allocated bricks still owe: unplaced bricks, unpartitioned bricks and untraced strata.
    /// Demand not yet allocated is absent; the next plan's allocation adds it.</summary>
    public IrradianceRemainingWork Remaining {
        get {
            var placements = 0;
            var partitions = 0;
            var strata = 0;

            foreach (var brick in m_bricks.Values) {
                if (!brick.Placed) { placements++; }
                if (!brick.Classified) { partitions++; }
                foreach (var traced in brick.Traced) { if (!traced) { strata++; } }
            }
            return new IrradianceRemainingWork(Partitions: partitions, Placements: placements, Strata: strata);
        }
    }

    /// <summary>Returns whether this brick's cell partitions have been admitted after their latest invalidation.
    /// The GPU may read them only after the admitted classification pass in the same ordered submission.</summary>
    /// <param name="key">The allocated brick's world-space key.</param>
    /// <returns>Whether the existing schedule has admitted current partitions for this brick.</returns>
    public bool IsClassified(IrradianceBrickKey key) => (m_bricks.TryGetValue(key: key, value: out var brick) && brick.Classified);
    /// <summary>Returns whether a change of geometry within a sphere dirties a probe: whether the sphere meets the ball
    /// the probe's rays sweep, which holds every ray that missed as well as every ray that hit.</summary>
    /// <param name="probe">The probe's position, in world units.</param>
    /// <param name="reach">The probe's rays' reach, in world units.</param>
    /// <param name="changed">The sphere the change lies within.</param>
    /// <returns><see langword="true"/> when the probe must be traced again.</returns>
    public static bool Dirties(Double3 probe, double reach, IrradianceSphere changed) =>
        ((probe - changed.Center).Length <= (reach + changed.Radius));
    /// <summary>Gets how far a level's transport sees a change only near transport can see (<see cref="IrradianceGeometryChange.Near"/>):
    /// the level's first, near segment and the launch and feedback proofs of hits within it, widened by the probe's
    /// relocation allowance; the coarsest level's probes see it only through their own placement and partitions.</summary>
    /// <param name="level">The level index.</param>
    /// <returns>The reach, in world units.</returns>
    public double NearReach(int level) {
        var lattice = m_levels[level];
        var near = (((level + 1) < m_levels.Count) ? lattice.Reach : 0.0);

        return (near + ((NearProofSpacings + IrradianceCells.RelocationAllowance) * lattice.Spacing));
    }
    /// <summary>Marks a change of geometry: every probe whose possible path meets the bounds before or after the change
    /// is traced again, and every brick the change can reach is classified again. Without stored path bounds, every
    /// level uses the far distance because support-seeking can march beyond its nominal reach, widened by the probe's
    /// relocation allowance. Placement rewrites a whole brick, so all of that brick's strata are invalidated together;
    /// neighboring cells that read its corner probes must also be partitioned again.</summary>
    /// <param name="previous">The changed geometry's bounds before the change.</param>
    /// <param name="current">Its bounds after the change.</param>
    /// <returns>The dirty placement bricks, in key order; their submitted trace masks must be withdrawn together.</returns>
    public IReadOnlyList<IrradianceBrickKey> MarkGeometry(IrradianceSphere previous, IrradianceSphere current) =>
        MarkGeometry(changes: [new IrradianceGeometryChange(Near: false, Sphere: previous), new IrradianceGeometryChange(Near: false, Sphere: current)]);
    /// <summary>Marks changes of geometry, each withdrawing the transport that can see it: a far change every probe
    /// within the far distance, a near change only the probes within their level's <see cref="NearReach"/>.</summary>
    /// <param name="changes">The changed bounds.</param>
    /// <returns>The dirty placement bricks, in key order; their submitted trace masks must be withdrawn together.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="changes"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<IrradianceBrickKey> MarkGeometry(IReadOnlyList<IrradianceGeometryChange> changes) {
        ArgumentNullException.ThrowIfNull(argument: changes);
        var changed = new List<IrradianceBrickKey>();

        foreach (var (key, brick) in m_bricks) {
            var level = m_levels[key.Level];
            var far = (m_exitDistance + (IrradianceCells.RelocationAllowance * level.Spacing));
            var near = NearReach(level: key.Level);

            IrradianceLattice.BrickBox(brick: key, level: level, max: out var max, min: out var min);
            var any = false;

            foreach (var change in changes) {
                var reach = (change.Near ? near : far);

                // A brick whose box the change cannot reach holds no probe it can reach.
                if (BoxDistance(max: max, min: min, point: change.Sphere.Center) > (reach + change.Sphere.Radius)) { continue; }
                foreach (var probe in IrradianceLattice.ProbesOf(brick: key)) {
                    if (Dirties(changed: change.Sphere, probe: IrradianceLattice.Position(key: probe, level: level), reach: reach)) {
                        any = true;
                        break;
                    }
                }
                if (any) { break; }
            }

            if (any) {
                Array.Clear(array: brick.Traced);
                brick.Placed = false;
                brick.Classified = false;
                brick.Reason = IrradianceUpdateReason.Geometry;
                changed.Add(item: key);
            }
        }
        foreach (var key in changed) { InvalidateNeighborPartitions(changed: key); }
        return changed;
    }

    private void InvalidateNeighborPartitions(IrradianceBrickKey changed) {
        for (var z = 0; (z <= 1); z++) {
            for (var y = 0; (y <= 1); y++) {
                for (var x = 0; (x <= 1); x++) {
                    var neighbor = changed with { X = (changed.X - x), Y = (changed.Y - y), Z = (changed.Z - z) };

                    if (m_bricks.TryGetValue(key: neighbor, value: out var brick)) { brick.Classified = false; }
                }
            }
        }
    }

    /// <summary>Schedules one frame.</summary>
    /// <param name="inputs">What the frame sees.</param>
    /// <param name="prices">The plan's allowance and each kind's item price; absent admits every kind's count budget.</param>
    /// <returns>The frame's plan; the schedule assumes it is carried out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The allowance is negative or an item price is not positive.</exception>
    public IrradianceFramePlan Frame(IrradianceFrameInputs inputs, IrradiancePlanPrices? prices = null) {
        ArgumentNullException.ThrowIfNull(argument: inputs);
        var price = (prices ?? IrradiancePlanPrices.Evaluations(allowance: long.MaxValue));
        var remaining = price.Allowance;

        ArgumentOutOfRangeException.ThrowIfNegative(remaining);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price.Place);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price.Classify);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price.Trace);

        var demand = DemandFor(inputs: inputs);
        var desired = demand.Desired;
        var ranks = demand.Ranks;

        var evicted = m_bricks.Keys.Where(predicate: key => !desired.Contains(item: key)).ToList();

        foreach (var key in evicted) {
            _ = m_bricks.Remove(key: key);
        }

        var allocated = new List<IrradianceBrickKey>();

        foreach (var key in desired) {
            if (m_bricks.ContainsKey(key: key)) {
                continue;
            }

            var probes = ((IrradianceLattice.BrickProbes * IrradianceLattice.BrickProbes) * IrradianceLattice.BrickProbes);

            m_bricks[key] = new Brick { Reason = IrradianceUpdateReason.Demand, Traced = new bool[(probes * m_levels[key.Level].Strata)] };
            allocated.Add(item: key);
        }

        foreach (var changed in allocated.Concat(second: evicted)) {
            InvalidateNeighborPartitions(changed: changed);
        }
        var placed = m_bricks
            .Where(predicate: static pair => !pair.Value.Placed)
            .Select(selector: static pair => pair.Key)
            .OrderByDescending(keySelector: static key => key.Level)
            .ThenBy(keySelector: key => ranks.GetValueOrDefault(defaultValue: double.MaxValue, key: key))
            .ThenBy(keySelector: static key => key)
            .Take(count: ((int)Math.Min(val1: m_classifyBudget, val2: (remaining / price.Place))))
            .ToList();

        remaining -= (placed.Count * price.Place);
        foreach (var key in placed) { m_bricks[key].Placed = true; }

        var classified = m_bricks
            .Where(predicate: static pair => !pair.Value.Classified)
            .Where(predicate: pair => CornersPlaced(key: pair.Key))
            .Select(selector: static pair => pair.Key)
            .OrderByDescending(keySelector: static key => key.Level)
            .ThenBy(keySelector: key => ranks.GetValueOrDefault(defaultValue: double.MaxValue, key: key))
            .ThenBy(keySelector: static key => key)
            .Take(count: ((int)Math.Min(val1: m_classifyBudget, val2: (remaining / price.Classify))))
            .ToList();

        remaining -= (classified.Count * price.Classify);
        foreach (var key in classified) {
            m_bricks[key].Classified = true;
        }

        var candidates = new List<(int Stratum, int Level, double Rank, IrradianceProbeKey Probe, IrradianceUpdateReason Reason, IrradianceBrickKey Brick, int Slot)>();
        // A ray's support, launch and feedback proofs read whichever cells its path reaches, at its own and coarser
        // levels. Tracing only once every allocated brick is placed and partitioned makes each stored ray a function of
        // the complete lattice, never of how far the remaining placements and partitions had progressed.
        var latticeComplete = m_bricks.Values.All(predicate: static brick => (brick.Placed && brick.Classified));

        foreach (var (key, brick) in m_bricks) {
            if (!latticeComplete) {
                break;
            }

            var strata = m_levels[key.Level].Strata;
            var index = 0;
            var rank = ranks.GetValueOrDefault(defaultValue: double.MaxValue, key: key);

            foreach (var probe in IrradianceLattice.ProbesOf(brick: key)) {
                for (var stratum = 0; (stratum < strata); stratum++) {
                    var slot = ((index * strata) + stratum);

                    if (!brick.Traced[slot]) {
                        var reason = ((stratum == 0) ? brick.Reason : IrradianceUpdateReason.Converge);

                        candidates.Add(item: (stratum, key.Level, rank, probe, reason, key, slot));
                    }
                }

                index++;
            }
        }

        var traces = candidates
            .OrderBy(keySelector: static candidate => candidate.Stratum)
            .ThenByDescending(keySelector: static candidate => candidate.Level)
            .ThenBy(keySelector: static candidate => candidate.Rank)
            .ThenBy(keySelector: static candidate => candidate.Probe)
            .Take(count: ((int)Math.Min(val1: m_traceBudget, val2: (remaining / price.Trace))))
            .ToList();

        foreach (var trace in traces) {
            m_bricks[trace.Brick].Traced[trace.Slot] = true;
        }

        evicted.Sort();

        return new IrradianceFramePlan(
            Allocated: allocated,
            Classified: classified,
            Evicted: evicted,
            Refused: demand.Refused,
            Traces: traces.Select(selector: static trace => new IrradianceUpdate(Probe: trace.Probe, Reason: trace.Reason, Stratum: trace.Stratum)).ToList()
        ) { Placed = placed };
    }

    // Demand depends on source values, not trace progress. Own the source lists so in-place edits cannot change
    // the remembered inputs, and share only a read-only refusal list with callers of successive frame plans.
    private DemandSnapshot DemandFor(IrradianceFrameInputs inputs) {
        if ((m_demand is { } held) && (held.Inputs.WorldMin == inputs.WorldMin) && (held.Inputs.WorldMax == inputs.WorldMax) &&
            SameValues(first: held.Inputs.Cameras, second: inputs.Cameras) && SameValues(first: held.Inputs.Bounds, second: inputs.Bounds)) { return held; }
        var snapshot = inputs with { Cameras = inputs.Cameras.ToArray(), Bounds = inputs.Bounds.ToArray() };
        var desired = new SortedSet<IrradianceBrickKey>();
        var refused = new List<IrradianceBrickKey>();
        var ranks = new Dictionary<IrradianceBrickKey, double>();

        for (var level = 0; (level < m_levels.Count); level++) {
            var demanded = Demand(inputs: snapshot, level: level);

            for (var index = 0; (index < demanded.Count); index++) {
                if (index < m_pools[level]) {
                    _ = desired.Add(item: demanded[index].Key);
                    ranks[demanded[index].Key] = demanded[index].Rank;
                } else {
                    refused.Add(item: demanded[index].Key);
                }
            }
        }
        refused.Sort();
        return m_demand = new DemandSnapshot(snapshot, desired, ranks, refused.AsReadOnly());
    }
    private static bool SameValues<T>(IReadOnlyList<T> first, IReadOnlyList<T> second) where T : IEquatable<T> {
        if (first.Count != second.Count) { return false; }
        for (var index = 0; (index < first.Count); index++) {
            if (!first[index].Equals(other: second[index])) { return false; }
        }
        return true;
    }
    private bool CornersPlaced(IrradianceBrickKey key) {
        for (var z = 0; (z <= 1); z++) {
            for (var y = 0; (y <= 1); y++) {
                for (var x = 0; (x <= 1); x++) {
                    var neighbour = key with { X = (key.X + x), Y = (key.Y + y), Z = (key.Z + z) };

                    if (m_bricks.TryGetValue(key: neighbour, value: out var brick) && !brick.Placed) { return false; }
                }
            }
        }
        return true;
    }
    // A level's demanded bricks, nearest a camera first, ties by key: every brick inside the world box (and within the
    // level's radius of a camera, when it has one) whose box, widened by a cell and the relocation allowance, meets a
    // bound.
    private List<(IrradianceBrickKey Key, double Rank)> Demand(IrradianceFrameInputs inputs, int level) {
        var spacing = m_levels[level].Spacing;
        var side = (IrradianceLattice.BrickProbes * spacing);
        var margin = (spacing * (1.0 + IrradianceCells.RelocationAllowance));
        var radius = m_levels[level].Radius;
        var lowest = inputs.WorldMin;
        var highest = inputs.WorldMax;
        var bricks = new SortedSet<IrradianceBrickKey>();

        if (radius > 0.0) {
            foreach (var camera in inputs.Cameras) {
                var reach = new Double3(X: radius, Y: radius, Z: radius);

                AddBricks(bricks: bricks, level: level, max: Min(a: (camera + reach), b: highest), min: Max(a: (camera - reach), b: lowest), side: side);
            }
        } else {
            AddBricks(bricks: bricks, level: level, max: highest, min: lowest, side: side);
        }

        var demanded = new List<(IrradianceBrickKey Key, double Rank)>();

        foreach (var key in bricks) {
            IrradianceLattice.BrickBox(brick: key, level: m_levels[level], max: out var max, min: out var min);

            var rank = double.MaxValue;

            foreach (var camera in inputs.Cameras) {
                rank = Math.Min(val1: rank, val2: BoxDistance(max: max, min: min, point: camera));
            }

            if ((radius > 0.0) && (rank > radius)) {
                continue;
            }

            var meets = false;

            foreach (var bound in inputs.Bounds) {
                if (BoxDistance(max: max, min: min, point: bound.Center) <= (bound.Radius + margin)) {
                    meets = true;

                    break;
                }
            }

            if (meets) {
                demanded.Add(item: (key, rank));
            }
        }

        demanded.Sort(comparison: static (a, b) => {
            var order = a.Rank.CompareTo(value: b.Rank);

            return ((order == 0) ? a.Key.CompareTo(other: b.Key) : order);
        });

        return demanded;
    }
    private static void AddBricks(SortedSet<IrradianceBrickKey> bricks, int level, Double3 min, Double3 max, double side) {
        for (var z = ((int)Math.Floor(d: (min.Z / side))); (z <= ((int)Math.Floor(d: (max.Z / side)))); z++) {
            for (var y = ((int)Math.Floor(d: (min.Y / side))); (y <= ((int)Math.Floor(d: (max.Y / side)))); y++) {
                for (var x = ((int)Math.Floor(d: (min.X / side))); (x <= ((int)Math.Floor(d: (max.X / side)))); x++) {
                    _ = bricks.Add(item: new IrradianceBrickKey(Level: level, X: x, Y: y, Z: z));
                }
            }
        }
    }
    private static double BoxDistance(Double3 min, Double3 max, Double3 point) {
        var dx = Math.Max(val1: Math.Max(val1: (min.X - point.X), val2: 0.0), val2: (point.X - max.X));
        var dy = Math.Max(val1: Math.Max(val1: (min.Y - point.Y), val2: 0.0), val2: (point.Y - max.Y));
        var dz = Math.Max(val1: Math.Max(val1: (min.Z - point.Z), val2: 0.0), val2: (point.Z - max.Z));

        return new Double3(X: dx, Y: dy, Z: dz).Length;
    }
    private static Double3 Min(Double3 a, Double3 b) => new(X: Math.Min(val1: a.X, val2: b.X), Y: Math.Min(val1: a.Y, val2: b.Y), Z: Math.Min(val1: a.Z, val2: b.Z));
    private static Double3 Max(Double3 a, Double3 b) => new(X: Math.Max(val1: a.X, val2: b.X), Y: Math.Max(val1: a.Y, val2: b.Y), Z: Math.Max(val1: a.Z, val2: b.Z));
}
