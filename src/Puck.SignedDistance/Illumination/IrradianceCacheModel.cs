namespace Puck.SignedDistance.Illumination;

/// <summary>What one of a probe's rays ended on.</summary>
public enum IrradianceHitKind {
    /// <summary>A surface; the record holds its point, normal and material.</summary>
    Hit = 0,
    /// <summary>A point where the ray passed its level's reach and stood in a coarser cell with support it reads.</summary>
    Continuation = 1,
    /// <summary>The far distance, past which the ray reads the sky.</summary>
    Exit = 2,
    /// <summary>A march that could not finish its proof; the ray carries no light.</summary>
    Unresolved = 3,
}
/// <summary>One stored ray of a probe: a function of the geometry alone.</summary>
/// <param name="Kind">What the ray ended on.</param>
/// <param name="Point">The surface point, the continuation point, or the last proven point, in world units.</param>
/// <param name="Normal">The field's unit gradient toward positive, free space, for a hit.</param>
/// <param name="Material">The surface's material for a hit.</param>
public readonly record struct IrradianceHitRecord(IrradianceHitKind Kind, Double3 Point, Double3 Normal, int Material);
/// <summary>The transport rules a model applies; each defaults on, and turning one off is the ablation a law uses to
/// show it discriminates.</summary>
/// <param name="ExitDistance">The far distance, in world units, past which a ray reads the sky.</param>
/// <param name="Partition">Whether a receiver reads only the corners its cell's partition joins it to; off, it reads
/// every free corner.</param>
/// <param name="SupportSeeking">Whether a ray past its level's reach marches on until it stands in coarser support; off,
/// it reads the sky at its reach.</param>
/// <param name="IntervalCheck">Whether a continuation reads only coarser rays whose hit lies beyond its point; off, it
/// reads each coarser corner's nearest ray whatever it hit.</param>
/// <param name="Feedback">The share of the cache's own irradiance that re-enters at a hit, between zero and one.</param>
/// <param name="ProofAllowance">The receiver proofs a frame's view lookups may issue (<see cref="IrradianceCacheModel.BeginFrame"/>);
/// a lookup that needs one past it reads no light that frame. Proofs a trace or a solve needs are charged to them, not
/// to this allowance.</param>
/// <param name="HitReprojection">Whether a continuation reads each coarser corner's ray whose stored end best continues
/// the finer ray, seen from the finer ray's end; off, it reads the corner's ray nearest in direction.</param>
public sealed record IrradianceModelOptions(double ExitDistance, bool Partition = true, bool SupportSeeking = true, bool IntervalCheck = true, double Feedback = 1.0, int ProofAllowance = int.MaxValue, bool HitReprojection = true);
/// <summary>
/// The radiance cache's transport on the CPU, the GPU's reference: probes placed against the field, cells partitioned by
/// exact segment traces, rays stored as hits with support-seeking continuation into coarser levels, a finite lighting
/// solve over two generations, and the apply a receiver reads. Every answer is deterministic, and every ordering the GPU
/// is free to choose (the order probes are swept in, how a sweep splits across frames) leaves the result unchanged.
/// </summary>
public sealed class IrradianceCacheModel {
    private const int ProofSlots = 8;
    // The cone, in radians, about a continuation's direction within which a coarser probe's rays are candidates.
    private const double ReprojectionCone = 0.5;

    private sealed class Probe(IrradianceProbeKey key, IrradianceProbePlacement placement) {
        public IrradianceProbeKey Key { get; } = key;
        public IrradianceProbePlacement Placement { get; } = placement;

        public Continuation[]?[]? Continuations { get; set; }
        public IrradianceHitRecord[]? Hits { get; set; }

        public IrradianceContributions?[][] Radiance { get; } = [[], []];
    }
    private readonly record struct Continuation(IrradianceProbeKey Probe, int Ray, double Weight);
    // A receiver proof: the corners an anchor point's own traces reached, and the clearance certified around it. Any
    // point whose certified ball meets the anchor's is joined to it by a clear segment, so it reads the same corners.
    private readonly record struct Proof(Double3 Anchor, double Clearance, int Mask);
    private enum Lookup {
        Proven,
        Unallocated,
        Deferred,
    }

    private readonly IrradianceField m_field;
    private readonly IrradianceSurfaces m_surfaces;
    private readonly IReadOnlyList<IrradianceLevel> m_levels;
    private readonly IrradianceModelOptions m_options;
    private readonly SortedDictionary<IrradianceProbeKey, IrradianceProbePlacement?> m_allocated = [];
    private readonly Dictionary<IrradianceProbeKey, Probe> m_probes = [];
    private readonly Dictionary<IrradianceProbeKey, IrradianceCellPartition> m_cells = [];
    private readonly Dictionary<(IrradianceProbeKey Cell, int X, int Y, int Z), List<Proof>> m_proofs = [];

    private int m_published = -1;
    private int m_allowance;

    /// <summary>Initializes a new instance of the <see cref="IrradianceCacheModel"/> class.</summary>
    /// <param name="field">The field.</param>
    /// <param name="surfaces">The surfaces, direct light and sky.</param>
    /// <param name="levels">The levels, finest first.</param>
    /// <param name="options">The transport rules.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="levels"/> is empty.</exception>
    public IrradianceCacheModel(IrradianceField field, IrradianceSurfaces surfaces, IReadOnlyList<IrradianceLevel> levels, IrradianceModelOptions options) {
        ArgumentNullException.ThrowIfNull(argument: field);
        ArgumentNullException.ThrowIfNull(argument: surfaces);
        ArgumentNullException.ThrowIfNull(argument: levels);
        ArgumentNullException.ThrowIfNull(argument: options);

        if (levels.Count == 0) {
            throw new ArgumentException(message: "A cache has at least one level.", paramName: nameof(levels));
        }

        m_field = field;
        m_surfaces = surfaces;
        m_levels = levels;
        m_options = options;
        m_allowance = options.ProofAllowance;
    }

    /// <summary>Gets the count of stored rays that ended unresolved.</summary>
    public int UnresolvedRays { get; private set; }
    /// <summary>Gets the count of stored rays that continued into a coarser level.</summary>
    public int ContinuedRays { get; private set; }
    /// <summary>Gets the count of stored rays that left the world.</summary>
    public int ExitedRays { get; private set; }
    /// <summary>Gets the count of complete lighting sweeps published.</summary>
    public int SweepsPublished { get; private set; }
    /// <summary>Gets the count of receiver proofs issued: traces from a receiver's point to its cell's corners.</summary>
    public int ProofsIssued { get; private set; }
    /// <summary>Gets the count of lookups that reused a cached proof.</summary>
    public int ProofsReused { get; private set; }
    /// <summary>Gets the count of view lookups that needed a proof past the frame's allowance and read no light.</summary>
    public int LookupsDeferred { get; private set; }

    /// <summary>Starts a frame: resets the allowance of receiver proofs the frame's view lookups may issue.</summary>
    public void BeginFrame() => m_allowance = m_options.ProofAllowance;
    /// <summary>Allocates every brick of a level whose box meets a world-space box.</summary>
    /// <param name="level">The level's index.</param>
    /// <param name="min">The box's least corner, in world units.</param>
    /// <param name="max">The box's greatest corner, in world units.</param>
    public void Allocate(int level, Double3 min, Double3 max) {
        var spacing = m_levels[level].Spacing;
        var side = (IrradianceLattice.BrickProbes * spacing);

        for (var z = ((int)Math.Floor(d: (min.Z / side))); (z <= ((int)Math.Floor(d: (max.Z / side)))); z++) {
            for (var y = ((int)Math.Floor(d: (min.Y / side))); (y <= ((int)Math.Floor(d: (max.Y / side)))); y++) {
                for (var x = ((int)Math.Floor(d: (min.X / side))); (x <= ((int)Math.Floor(d: (max.X / side)))); x++) {
                    foreach (var key in IrradianceLattice.ProbesOf(brick: new IrradianceBrickKey(Level: level, X: x, Y: y, Z: z))) {
                        _ = m_allocated.TryAdd(key: key, value: null);
                    }
                }
            }
        }
    }
    /// <summary>Places every allocated probe against the field.</summary>
    public void Classify() {
        foreach (var key in m_allocated.Keys.ToArray()) {
            var placement = IrradianceCells.Place(
                field: m_field,
                lattice: IrradianceLattice.Position(key: key, level: m_levels[key.Level]),
                spacing: m_levels[key.Level].Spacing
            );

            m_allocated[key] = placement;
            m_probes[key] = new Probe(key: key, placement: placement);
        }

        m_cells.Clear();
        m_proofs.Clear();
    }
    /// <summary>Traces every lit probe's rays, coarsest level first, so a finer ray's continuation finds its coarser
    /// support traced.</summary>
    public void Trace() {
        for (var level = (m_levels.Count - 1); (level >= 0); level--) {
            foreach (var probe in ProbesOf(level: level)) {
                TraceProbe(probe: probe);
            }
        }
    }
    /// <summary>Runs the finite lighting solve: one direct sweep from zero, then <paramref name="bounces"/> feedback
    /// sweeps, each reading the irradiance the preceding sweep published and writing the other generation, coarsest
    /// level first. A sweep publishes only once every probe of it is written.</summary>
    /// <param name="bounces">The count of feedback sweeps.</param>
    /// <param name="reverseOrder">Whether to sweep each level's probes in reverse order, which must change nothing.</param>
    /// <param name="probesPerStep">The count of probes one step shades before the next begins, as a sweep split across
    /// frames; zero for a whole sweep at once. It must change nothing.</param>
    public void Solve(int bounces, bool reverseOrder = false, int probesPerStep = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: bounces);

        m_published = -1;

        var levels = new IReadOnlyList<IrradianceProbeKey>[m_levels.Count];

        for (var level = 0; (level < levels.Length); level++) {
            var keys = ProbesOf(level: level).Where(predicate: static probe => (probe.Hits is not null)).Select(selector: static probe => probe.Key).ToArray();

            if (reverseOrder) { Array.Reverse(array: keys); }
            levels[level] = keys;
        }
        var schedule = new IrradianceSolveSchedule(levels, bounces, ((probesPerStep > 0) ? probesPerStep : int.MaxValue));

        while (schedule.Plan() is { } batch) {
            foreach (var key in batch.Probes) { ShadeProbe(feedback: (batch.Sweep > 0), probe: m_probes[key], write: batch.WriteGeneration); }
            schedule.Submitted();
            if (batch.CompletesSweep) { m_published = schedule.PublishedGeneration; SweepsPublished++; }
        }
    }
    /// <summary>Returns the normalized irradiance a receiver reads from the published generation: from the finest level
    /// whose cell around its point has readable, traced corners, each weighted by trilinear position and facing and
    /// renormalized over them.</summary>
    /// <param name="surface">The receiver's surface point, in world units.</param>
    /// <param name="normal">The receiver's unit normal.</param>
    /// <returns>The irradiance; zero when a surface lies within the launch's first certified sample, or when the frame's
    /// proof allowance ran out before this receiver was proven; or <see langword="null"/> when no level supports the
    /// receiver, where a view shades as it does with indirect light off.</returns>
    public Double3? Irradiance(Double3 surface, Double3 normal) =>
        Contributions(normal: normal, surface: surface)?.Total;
    /// <summary>Returns the independently transported sources of the same published receiver answer. Proof admission,
    /// corner weights and normalization are shared with <see cref="Irradiance"/>.</summary>
    /// <param name="surface">The receiver's surface point.</param>
    /// <param name="normal">The receiver's unit normal.</param>
    /// <returns>The source contributions, or null when no published level supports the receiver.</returns>
    public IrradianceContributions? Contributions(Double3 surface, Double3 normal) =>
        ((m_published < 0) ? null : IrradianceAt(budgeted: true, generation: m_published, normal: normal, surface: surface));
    /// <summary>Returns the mask of corners a receiver reads at a level, bit n for corner n of the cell around its
    /// point.</summary>
    /// <param name="level">The level's index.</param>
    /// <param name="surface">The receiver's surface point, in world units.</param>
    /// <param name="normal">The receiver's unit normal.</param>
    /// <returns>The mask, zero when the receiver's launch is not certified, or −1 when the cell is not wholly
    /// allocated.</returns>
    public int ReadableCorners(int level, Double3 surface, Double3 normal) {
        if (IrradianceCells.Launch(field: m_field, height: (IrradianceCells.ReceiverBias * m_levels[level].Spacing), normal: normal, surface: surface) is not { } launch) {
            return 0;
        }

        return CornersFor(budgeted: false, cell: out _, clearance: launch.Clearance, corners: out _, level: level, lookup: out _, point: launch.Point);
    }
    /// <summary>Returns a probe's own irradiance about a normal in the published generation: the cosine-weighted mean of
    /// its resolved rays' radiance, unresolved rays excluded rather than read as black or sky.</summary>
    /// <param name="key">The probe.</param>
    /// <param name="normal">The unit normal.</param>
    /// <returns>The irradiance, or <see langword="null"/> when the probe is not traced, nothing is published, or no
    /// ray in this hemisphere has resolved lighting.</returns>
    public Double3? ProbeIrradianceOf(IrradianceProbeKey key, Double3 normal) =>
        (((m_published >= 0) && m_probes.TryGetValue(key: key, value: out var probe) && (probe.Hits is not null))
            ? ProbeIrradiance(generation: m_published, normal: normal, probe: probe)?.Total
            : null);
    /// <summary>Returns the cosine-weighted share of a probe's hemisphere about a normal whose rays are unresolved: the
    /// share of its irradiance the probe estimates from its other rays. A published generation includes failed
    /// reflected support and continuations; before publication only unresolved transport contributes.</summary>
    /// <param name="key">The probe.</param>
    /// <param name="normal">The unit normal.</param>
    /// <returns>The share, between zero and one; zero when the probe is not traced.</returns>
    public double UnresolvedShareOf(IrradianceProbeKey key, Double3 normal) {
        if (!m_probes.TryGetValue(key: key, value: out var probe) || (probe.Hits is not { } hits)) {
            return 0.0;
        }

        var level = m_levels[key.Level];
        var unresolved = 0.0;
        var total = 0.0;

        for (var ray = 0; (ray < level.Rays); ray++) {
            var cosine = Double3.Dot(a: normal, b: IrradianceLattice.Direction(key: key, level: level, ray: ray));

            if (cosine > 0.0) {
                total += cosine;
                var unknown = ((hits[ray].Kind == IrradianceHitKind.Unresolved)
                    || ((m_published >= 0) && !probe.Radiance[m_published][ray].HasValue));

                unresolved += (unknown ? cosine : 0.0);
            }
        }

        return ((total > 0.0) ? (unresolved / total) : 0.0);
    }
    /// <summary>Returns a probe's placement and whether it is traced.</summary>
    /// <param name="key">The probe.</param>
    /// <param name="placement">Its placement.</param>
    /// <returns><see langword="true"/> when the probe is allocated and classified.</returns>
    public bool TryGetProbe(IrradianceProbeKey key, out IrradianceProbePlacement placement) {
        if (m_probes.TryGetValue(key: key, value: out var probe)) {
            placement = probe.Placement;

            return true;
        }

        placement = default;

        return false;
    }
    /// <summary>Returns a probe's stored rays.</summary>
    /// <param name="key">The probe.</param>
    /// <returns>Its rays, or an empty list when it is not traced.</returns>
    public IReadOnlyList<IrradianceHitRecord> HitsOf(IrradianceProbeKey key) =>
        ((m_probes.TryGetValue(key: key, value: out var probe) && (probe.Hits is { } hits)) ? hits : []);
    /// <summary>Returns the radiance a probe's ray carries in the published generation.</summary>
    /// <param name="key">The probe.</param>
    /// <param name="ray">The ray.</param>
    /// <returns>The radiance, or <see langword="null"/> when the probe is not traced, nothing is published, or this
    /// ray's transport or requested lighting support is unresolved.</returns>
    public Double3? RadianceOf(IrradianceProbeKey key, int ray) =>
        (((m_published >= 0) && m_probes.TryGetValue(key: key, value: out var probe) && (probe.Hits is not null))
            ? probe.Radiance[m_published][ray]?.Total
            : null);
    /// <summary>Returns a probe's ray nearest a direction.</summary>
    /// <param name="key">The probe.</param>
    /// <param name="direction">The unit direction.</param>
    /// <returns>The ray.</returns>
    public int NearestRayOf(IrradianceProbeKey key, Double3 direction) =>
        NearestRay(direction: direction, key: key, level: m_levels[key.Level]);

    private IEnumerable<Probe> ProbesOf(int level) {
        foreach (var (key, _) in m_allocated) {
            if ((key.Level == level) && m_probes.TryGetValue(key: key, value: out var probe) && probe.Placement.IsLit) {
                yield return probe;
            }
        }
    }
    private void TraceProbe(Probe probe) {
        var level = m_levels[probe.Key.Level];
        var coarsest = ((probe.Key.Level == (m_levels.Count - 1)) || (level.Reach <= 0.0));
        var hits = new IrradianceHitRecord[level.Rays];
        var continuations = new Continuation[]?[level.Rays];
        var origin = probe.Placement.Position;

        for (var ray = 0; (ray < level.Rays); ray++) {
            var direction = IrradianceLattice.Direction(key: probe.Key, level: level, ray: ray);
            var reach = (coarsest ? m_options.ExitDistance : Math.Min(val1: level.Reach, val2: m_options.ExitDistance));
            var cast = m_field.Cast(direction: direction, maxDistance: reach, origin: origin);

            if (cast.Kind != IrradianceRayKind.Miss) {
                hits[ray] = RecordOf(cast: cast);

                continue;
            }

            if (coarsest || !m_options.SupportSeeking) {
                hits[ray] = new IrradianceHitRecord(Kind: IrradianceHitKind.Exit, Material: 0, Normal: Double3.Zero, Point: cast.Point);
                ExitedRays++;

                continue;
            }

            var coarser = (probe.Key.Level + 1);
            var step = m_levels[coarser].Spacing;
            var point = cast.Point;
            var travelled = reach;

            while (true) {
                if (travelled >= m_options.ExitDistance) {
                    hits[ray] = new IrradianceHitRecord(Kind: IrradianceHitKind.Exit, Material: 0, Normal: Double3.Zero, Point: point);
                    ExitedRays++;

                    break;
                }

                var support = ContinuationOf(direction: direction, level: coarser, point: point);

                if (support.Length > 0) {
                    hits[ray] = new IrradianceHitRecord(Kind: IrradianceHitKind.Continuation, Material: 0, Normal: Double3.Zero, Point: point);
                    continuations[ray] = support;
                    ContinuedRays++;

                    break;
                }

                var advance = Math.Min(val1: step, val2: (m_options.ExitDistance - travelled));
                var next = m_field.Cast(direction: direction, maxDistance: advance, origin: point);

                if (next.Kind != IrradianceRayKind.Miss) {
                    hits[ray] = RecordOf(cast: next);

                    break;
                }

                point = next.Point;
                travelled += advance;
            }
        }

        probe.Hits = hits;
        probe.Continuations = continuations;
        probe.Radiance[0] = new IrradianceContributions?[level.Rays];
        probe.Radiance[1] = new IrradianceContributions?[level.Rays];
    }
    private IrradianceHitRecord RecordOf(IrradianceRay cast) {
        if ((cast.Kind == IrradianceRayKind.Unresolved) || !m_field.TryGradient(gradient: out var normal, point: cast.Point)) {
            UnresolvedRays++;

            return new IrradianceHitRecord(Kind: IrradianceHitKind.Unresolved, Material: 0, Normal: Double3.Zero, Point: cast.Point);
        }

        // Keep the signed field's outward orientation, including finite-stencil normals at grazing edges.
        return new IrradianceHitRecord(Kind: IrradianceHitKind.Hit, Material: cast.Material, Normal: normal, Point: cast.Point);
    }
    // The coarser support a continuation at a point reads: the corners of the coarser cell the point reaches, each
    // through its nearest ray, kept only where that ray's own end lies beyond the point along the direction, so no
    // interval the finer ray already crossed is counted again.
    private Continuation[] ContinuationOf(int level, Double3 point, Double3 direction) {
        if (!m_field.TryClampedDistance(distance: out var clearance, material: out _, point: point) || (clearance <= 0.0)) {
            return [];
        }

        var mask = CornersFor(budgeted: false, cell: out var cell, clearance: clearance, corners: out var corners, level: level, lookup: out _, point: point);

        if (mask <= 0) {
            return [];
        }

        var support = new List<Continuation>();

        for (var corner = 0; (corner < IrradianceLattice.CellCorners); corner++) {
            if ((mask & (1 << corner)) == 0) {
                continue;
            }

            var key = IrradianceLattice.Corner(cell: cell, corner: corner);

            if (!m_probes.TryGetValue(key: key, value: out var probe) || (probe.Hits is null) || !corners[corner].IsLit) {
                continue;
            }

            var ray = (m_options.HitReprojection
                ? ReprojectedRay(direction: direction, key: key, level: m_levels[level], point: point, probe: probe)
                : NearestRay(direction: direction, key: key, level: m_levels[level]));

            if (ray < 0) {
                continue;
            }

            var record = probe.Hits[ray];
            var accepted = record.Kind switch {
                IrradianceHitKind.Exit => true,
                IrradianceHitKind.Unresolved => false,
                _ => (!m_options.IntervalCheck || (Double3.Dot(a: (record.Point - point), b: direction) > 0.0)),
            };

            if (accepted) {
                support.Add(item: new Continuation(
                    Probe: key,
                    Ray: ray,
                    Weight: Math.Max(val1: IrradianceLattice.Trilinear(cell: cell, corner: corner, level: m_levels[level], point: point), val2: 1.0e-6)
                ));
            }
        }

        return support.ToArray();
    }
    // The coarser probe's stored ray whose own end best continues the finer ray: among its rays within
    // ReprojectionCone of the direction, the one whose hit (or continuation) point, seen from the finer ray's end, lies
    // nearest the direction, and lies beyond the end; an exit by its direction alone. -1 when none qualifies.
    private static int ReprojectedRay(IrradianceProbeKey key, IrradianceLevel level, Double3 point, Double3 direction, Probe probe) {
        var best = -1;
        var bestCosine = double.MinValue;
        var cone = Math.Cos(d: ReprojectionCone);

        for (var ray = 0; (ray < level.Rays); ray++) {
            var rayDirection = IrradianceLattice.Direction(key: key, level: level, ray: ray);

            if (Double3.Dot(a: rayDirection, b: direction) < cone) {
                continue;
            }

            var record = probe.Hits![ray];
            var cosine = record.Kind switch {
                IrradianceHitKind.Exit => Double3.Dot(a: rayDirection, b: direction),
                IrradianceHitKind.Unresolved => double.MinValue,
                _ => ((Double3.Dot(a: (record.Point - point), b: direction) > 0.0) ? Double3.Dot(a: (record.Point - point).Normalize(), b: direction) : double.MinValue),
            };

            if (cosine > bestCosine) {
                best = ray;
                bestCosine = cosine;
            }
        }

        return ((bestCosine > double.MinValue) ? best : -1);
    }
    private static int NearestRay(IrradianceProbeKey key, IrradianceLevel level, Double3 direction) {
        var best = 0;
        var bestDot = double.MinValue;

        for (var ray = 0; (ray < level.Rays); ray++) {
            var dot = Double3.Dot(a: IrradianceLattice.Direction(key: key, level: level, ray: ray), b: direction);

            if (dot > bestDot) {
                best = ray;
                bestDot = dot;
            }
        }

        return best;
    }
    // The readable corner mask of the cell around a free-space point with a certified clearance: a cached proof whose
    // anchor's ball meets the point's, or a fresh proof (the point's own traces), charged to the frame's allowance
    // when the lookup is a view's. -1 when the cell is not wholly allocated or the allowance ran out.
    private int CornersFor(int level, Double3 point, double clearance, bool budgeted, out IrradianceProbeKey cell, out IrradianceProbePlacement[] corners, out Lookup lookup) {
        cell = IrradianceLattice.CellOf(level: m_levels[level], levelIndex: level, point: point);
        corners = new IrradianceProbePlacement[IrradianceLattice.CellCorners];

        for (var corner = 0; (corner < IrradianceLattice.CellCorners); corner++) {
            if (!m_probes.TryGetValue(key: IrradianceLattice.Corner(cell: cell, corner: corner), value: out var probe)) {
                lookup = Lookup.Unallocated;

                return -1;
            }

            corners[corner] = probe.Placement;
        }

        lookup = Lookup.Proven;

        if (!m_options.Partition) {
            var all = 0;

            for (var corner = 0; (corner < IrradianceLattice.CellCorners); corner++) {
                all |= (corners[corner].IsFree ? (1 << corner) : 0);
            }

            return all;
        }

        var slotSize = (m_levels[level].Spacing / ProofSlots);
        var slot = (cell, ((int)Math.Floor(d: (point.X / slotSize))), ((int)Math.Floor(d: (point.Y / slotSize))), ((int)Math.Floor(d: (point.Z / slotSize))));

        if (m_proofs.TryGetValue(key: slot, value: out var proofs)) {
            foreach (var proof in proofs) {
                if ((proof.Anchor - point).Length <= (proof.Clearance + clearance)) {
                    ProofsReused++;

                    return proof.Mask;
                }
            }
        }

        if (budgeted) {
            if (m_allowance <= 0) {
                LookupsDeferred++;
                lookup = Lookup.Deferred;

                return -1;
            }

            m_allowance--;
        }

        if (!m_cells.TryGetValue(key: cell, value: out var partition)) {
            partition = IrradianceCells.Partition(corners: corners, field: m_field, spacing: m_levels[level].Spacing);
            m_cells[cell] = partition;
        }

        var mask = IrradianceCells.ReadableCorners(corners: corners, field: m_field, partition: partition, point: point);

        // A failed attempt establishes no reachable corner; it cannot rule out nearby receivers.
        if (mask > 0) {
            if (proofs is null) {
                proofs = [];
                m_proofs[slot] = proofs;
            }

            proofs.Add(item: new Proof(Anchor: point, Clearance: clearance, Mask: mask));
        }

        ProofsIssued++;

        return mask;
    }
    private void ShadeProbe(Probe probe, int write, bool feedback) {
        var read = write ^ 1;
        var level = m_levels[probe.Key.Level];
        var hits = probe.Hits!;
        var radiance = probe.Radiance[write];

        for (var ray = 0; (ray < hits.Length); ray++) {
            var record = hits[ray];

            radiance[ray] = record.Kind switch {
                IrradianceHitKind.Hit => ShadeHit(feedback: feedback, read: read, record: record),
                IrradianceHitKind.Exit => new IrradianceContributions(Sky: m_surfaces.Sky(arg: IrradianceLattice.Direction(key: probe.Key, level: level, ray: ray)), Direct: default, Feedback: default, Emission: default, Screens: default),
                IrradianceHitKind.Continuation => ShadeContinuation(support: probe.Continuations![ray]!, write: write),
                _ => null,
            };
        }
    }
    private IrradianceContributions? ShadeHit(IrradianceHitRecord record, int read, bool feedback) {
        var reflected = Double3.Zero;
        var albedo = m_surfaces.Reflection(record.Point, record.Normal, record.Material);

        if (feedback && (m_options.Feedback > 0.0) && (albedo != Double3.Zero)) {
            var previous = IrradianceAt(budgeted: false, generation: read, normal: record.Normal, surface: record.Point);

            if (previous is not { } value) { return null; }
            reflected = (value.Total * m_options.Feedback);
        }

        return new IrradianceContributions(
            Direct: Double3.Multiply(a: albedo, b: m_surfaces.Direct(record.Point, record.Normal, record.Material)),
            Feedback: Double3.Multiply(a: albedo, b: reflected),
            Emission: m_surfaces.Emission(record.Material), Sky: default,
            Screens: Double3.Multiply(a: albedo, b: m_surfaces.Screens(record.Point, record.Normal, record.Material)));
    }
    private IrradianceContributions? ShadeContinuation(Continuation[] support, int write) {
        var sum = default(IrradianceContributions);
        var weight = 0.0;

        foreach (var entry in support) {
            if (m_probes[entry.Probe].Radiance[write][entry.Ray] is not { } radiance) { continue; }
            sum += (radiance * entry.Weight);
            weight += entry.Weight;
        }

        return ((weight > 0.0) ? (sum * (1.0 / weight)) : null);
    }
    private IrradianceContributions? IrradianceAt(int generation, Double3 surface, Double3 normal, bool budgeted) {
        for (var level = 0; (level < m_levels.Count); level++) {
            var spacing = m_levels[level].Spacing;

            if (IrradianceCells.Launch(field: m_field, height: (IrradianceCells.ReceiverBias * spacing), normal: normal, surface: surface) is not { } launch) {
                // A view may display its no-indirect fallback; a solve cannot reuse that fallback as known black.
                return (budgeted ? default(IrradianceContributions) : null);
            }

            var point = launch.Point;
            var mask = CornersFor(budgeted: budgeted, cell: out var cell, clearance: launch.Clearance, corners: out var corners, level: level, lookup: out var lookup, point: point);

            if (lookup == Lookup.Deferred) {
                return default(IrradianceContributions);
            }

            if (mask <= 0) {
                continue;
            }

            var sum = default(IrradianceContributions);
            var total = 0.0;

            for (var corner = 0; (corner < IrradianceLattice.CellCorners); corner++) {
                if (((mask & (1 << corner)) == 0) || !corners[corner].IsLit) {
                    continue;
                }

                var key = IrradianceLattice.Corner(cell: cell, corner: corner);
                var probe = m_probes[key];

                if (probe.Hits is null) {
                    continue;
                }

                var toward = (probe.Placement.Position - surface).Normalize();
                var facing = (Math.Max(val1: Double3.Dot(a: normal, b: toward), val2: 0.0) + 0.01);
                var weight = (IrradianceLattice.Trilinear(cell: cell, corner: corner, level: m_levels[level], point: point) * facing);

                if (weight <= 0.0) {
                    continue;
                }

                if (ProbeIrradiance(generation: generation, normal: normal, probe: probe) is not { } value) { continue; }
                sum += (value * weight);
                total += weight;
            }

            if (total > 0.0) {
                return (sum * (1.0 / total));
            }
        }

        return null;
    }
    private IrradianceContributions? ProbeIrradiance(Probe probe, int generation, Double3 normal) {
        var level = m_levels[probe.Key.Level];
        var radiance = probe.Radiance[generation];
        var sum = default(IrradianceContributions);
        var total = 0.0;

        for (var ray = 0; (ray < level.Rays); ray++) {
            if (radiance[ray] is not { } value) {
                continue;
            }

            var cosine = Double3.Dot(a: normal, b: IrradianceLattice.Direction(key: probe.Key, level: level, ray: ray));

            if (cosine > 0.0) {
                sum += (value * cosine);
                total += cosine;
            }
        }

        return ((total > 0.0) ? (sum * (1.0 / total)) : null);
    }
}
