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
public sealed record IrradianceFramePlan(IReadOnlyList<IrradianceBrickKey> Allocated, IReadOnlyList<IrradianceBrickKey> Evicted, IReadOnlyList<IrradianceBrickKey> Refused, IReadOnlyList<IrradianceBrickKey> Classified, IReadOnlyList<IrradianceUpdate> Traces);
/// <summary>
/// The host's schedule for a residency's cache: which bricks each level allocates from its pool, which it classifies and
/// which probe strata it traces each frame, within fixed budgets. It is a pure function of its inputs and its own state:
/// the order cameras and bounds arrive in changes nothing, ties end in the lattice key, and a first stratum anywhere is
/// traced before a later stratum anywhere, so new demand never waits behind convergence.
/// </summary>
public sealed class IrradianceSchedule {
    private sealed class Brick {
        public bool Classified { get; set; }
        public IrradianceUpdateReason Reason { get; set; }
        public bool[] Traced { get; init; } = [];
    }

    private readonly IReadOnlyList<IrradianceLevel> m_levels;
    private readonly IReadOnlyList<int> m_pools;
    private readonly int m_traceBudget;
    private readonly int m_classifyBudget;
    private readonly double m_exitDistance;
    private readonly SortedDictionary<IrradianceBrickKey, Brick> m_bricks = [];

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

        m_levels = levels;
        m_pools = pools;
        m_traceBudget = traceBudget;
        m_classifyBudget = classifyBudget;
        m_exitDistance = exitDistance;
    }

    /// <summary>Gets the bricks allocated, in key order.</summary>
    public IReadOnlyList<IrradianceBrickKey> Allocated => m_bricks.Keys.ToArray();
    /// <summary>Gets whether every allocated brick is classified and every probe stratum traced.</summary>
    public bool IsComplete => m_bricks.Values.All(predicate: static brick => (brick.Classified && brick.Traced.All(predicate: static traced => traced)));

    /// <summary>Returns whether a change of geometry within a sphere dirties a probe: whether the sphere meets the ball
    /// the probe's rays sweep, which holds every ray that missed as well as every ray that hit.</summary>
    /// <param name="probe">The probe's position, in world units.</param>
    /// <param name="reach">The probe's rays' reach, in world units.</param>
    /// <param name="changed">The sphere the change lies within.</param>
    /// <returns><see langword="true"/> when the probe must be traced again.</returns>
    public static bool Dirties(Double3 probe, double reach, IrradianceSphere changed) =>
        ((probe - changed.Center).Length <= (reach + changed.Radius));
    /// <summary>Marks a change of geometry: every probe whose reach meets the bounds before or after the change is
    /// traced again, and every brick the change can reach is classified again.</summary>
    /// <param name="previous">The changed geometry's bounds before the change.</param>
    /// <param name="current">Its bounds after the change.</param>
    public void MarkGeometry(IrradianceSphere previous, IrradianceSphere current) {
        foreach (var (key, brick) in m_bricks) {
            var level = m_levels[key.Level];
            var reach = (IsCoarsest(level: key.Level) ? m_exitDistance : level.Reach);
            var index = 0;
            var any = false;

            foreach (var probe in IrradianceLattice.ProbesOf(brick: key)) {
                var position = IrradianceLattice.Position(key: probe, level: level);

                if (Dirties(changed: previous, probe: position, reach: reach) || Dirties(changed: current, probe: position, reach: reach)) {
                    for (var stratum = 0; (stratum < level.Strata); stratum++) {
                        brick.Traced[((index * level.Strata) + stratum)] = false;
                    }

                    any = true;
                }

                index++;
            }

            if (any) {
                brick.Classified = false;
                brick.Reason = IrradianceUpdateReason.Geometry;
            }
        }
    }
    /// <summary>Schedules one frame.</summary>
    /// <param name="inputs">What the frame sees.</param>
    /// <returns>The frame's plan; the schedule assumes it is carried out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputs"/> is <see langword="null"/>.</exception>
    public IrradianceFramePlan Frame(IrradianceFrameInputs inputs) {
        ArgumentNullException.ThrowIfNull(argument: inputs);

        var desired = new SortedSet<IrradianceBrickKey>();
        var refused = new List<IrradianceBrickKey>();
        var ranks = new Dictionary<IrradianceBrickKey, double>();

        for (var level = 0; (level < m_levels.Count); level++) {
            var demanded = Demand(inputs: inputs, level: level);

            foreach (var (key, rank) in demanded) {
                ranks[key] = rank;
            }

            for (var index = 0; (index < demanded.Count); index++) {
                if (index < m_pools[level]) {
                    _ = desired.Add(item: demanded[index].Key);
                } else {
                    refused.Add(item: demanded[index].Key);
                }
            }
        }

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

        var classified = m_bricks
            .Where(predicate: static pair => !pair.Value.Classified)
            .Select(selector: static pair => pair.Key)
            .OrderByDescending(keySelector: static key => key.Level)
            .ThenBy(keySelector: key => ranks.GetValueOrDefault(defaultValue: double.MaxValue, key: key))
            .ThenBy(keySelector: static key => key)
            .Take(count: m_classifyBudget)
            .ToList();

        foreach (var key in classified) {
            m_bricks[key].Classified = true;
        }

        var candidates = new List<(int Stratum, int Level, double Rank, IrradianceProbeKey Probe, IrradianceUpdateReason Reason, IrradianceBrickKey Brick, int Slot)>();

        foreach (var (key, brick) in m_bricks) {
            if (!brick.Classified) {
                continue;
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
            .Take(count: m_traceBudget)
            .ToList();

        foreach (var trace in traces) {
            m_bricks[trace.Brick].Traced[trace.Slot] = true;
        }

        evicted.Sort();
        refused.Sort();

        return new IrradianceFramePlan(
            Allocated: allocated,
            Classified: classified,
            Evicted: evicted,
            Refused: refused,
            Traces: traces.Select(selector: static trace => new IrradianceUpdate(Probe: trace.Probe, Reason: trace.Reason, Stratum: trace.Stratum)).ToList()
        );
    }

    private bool IsCoarsest(int level) => ((level == (m_levels.Count - 1)) || (m_levels[level].Reach <= 0.0));
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
