using System.Numerics;
using Puck.SdfVm.Debug;

namespace Puck.SdfVm;

/// <summary>The narrow engine-side seam the <see cref="SdfCarveBakePlanner"/> drives (carve-bake plan §3): request a
/// sliced background bake into a pool slot, poll a slot's state, and ask whether the engine even owns a pool. A
/// <see cref="SdfWorldEngine"/> IS this service (its <see cref="SdfWorldEngine.RequestBrickBake"/>/
/// <see cref="SdfWorldEngine.GetBrickState"/> match the shape), so the planner reaches the engine through this
/// three-member surface without naming the concrete engine type — the poll-each-produced-frame contract the plan's
/// §3 describes with "no callbacks, no cross-thread seams".</summary>
public interface ISdfBrickBakeService {
    /// <summary>Whether this engine provisions a brick pool at all (its <c>BrickPoolVoxelCapacity</c> was non-zero).
    /// A pool-less engine cannot bake, so the planner never proposes one — it emits every carve analytically and the
    /// scene renders exactly as it does today.</summary>
    bool BrickBakeAvailable { get; }

    /// <summary>Polls a slot's current bake state and monotonic serial (see <see cref="SdfWorldEngine.GetBrickState"/>).</summary>
    /// <param name="slot">The pool slot, in <c>[0, <see cref="SdfBrickPoolLayout.MaxBricks"/>)</c>.</param>
    /// <returns>The slot's state and serial.</returns>
    BrickBakeStatus GetBrickState(int slot);

    /// <summary>Requests a sliced background bake of a settled-carve bin's union field into a slot (see
    /// <see cref="SdfWorldEngine.RequestBrickBake"/>). Does NOT wait; re-requesting a slot restarts it and bumps the
    /// serial.</summary>
    /// <param name="slot">The pool slot, in <c>[0, <see cref="SdfBrickPoolLayout.MaxBricks"/>)</c>.</param>
    /// <param name="request">The bake request (box, cell size, dims, 1/λ, and the sphere carves).</param>
    void RequestBrickBake(int slot, BrickBakeRequest request);
}

/// <summary>
/// The SETTLE PLANNER (carve-bake plan §4): content-blind plumbing that watches a live carve pool, bins carves by
/// centre into a uniform lattice, and hands a settled cluster's UNION field off to a background GPU bake — then emits
/// that bin as ONE <see cref="SdfShapeType.SampledRegion"/> instance the kernels sample O(1) instead of its dozens/
/// hundreds of analytic subtraction instances. An in-flight or freshly edited cluster stays fully analytic; a bin
/// hands off only when its bake reaches <see cref="BrickBakeState.Ready"/>, and hands back the instant a carve inside
/// its bounds is added or removed (per-region invalidation, the Dreams "recompute only the touched region" note).
/// <para>
/// The planner is the representation-of-record's cache manager, NOT a new representation: deleting every brick (turn
/// the switch <see cref="Enabled"/> off) reproduces the identical analytic scene, slower. A brick holds ONLY the
/// settled hard-Subtraction carve union; smooth carves and sub-<see cref="MinCarveVoxelRadius"/>-voxel carves always
/// stay analytic (plan §4). Two seams consume it: the interactive SDF-debug carve pool (settle
/// <see cref="DefaultSettleFrames"/>) and the <c>sdf.carves</c> bench workload (settle 0 — immediate, so the warm
/// window absorbs the bake and the sampled window measures the baked steady state).
/// </para>
/// <para>
/// PER-FRAME LIFECYCLE. <see cref="Advance"/> runs once per produced frame with the current carve list, a monotonic
/// content revision, and the engine's <see cref="ISdfBrickBakeService"/>: it re-bins, counts quiet frames, polls bake
/// states, requests newly-settled bakes, and flips <see cref="BrickBakeState.Ready"/> bins to bricks — returning
/// <see langword="true"/> when the emit plan changed so the caller bumps its own content revision (the ordinary
/// revision-bump rebuild the grid cull already uses; no bespoke handoff machinery). <see cref="Emit"/> then partitions
/// the carve list at rebuild time: one <c>SampledRegion</c> per adopted bin, analytic for everything else — and, when
/// <see cref="Enabled"/> is off or nothing has baked yet, a byte-identical analytic emission.
/// </para>
/// </summary>
public sealed class SdfCarveBakePlanner {
    /// <summary>The uniform bin lattice's cell edge in world units (plan §4 — brick-sized boxes of edge 6.0). Carves
    /// bin by NEAREST cell centre (round, not floor) so a cluster authored around a lattice point stays in ONE bin
    /// instead of splintering across the eight cells meeting at that point.</summary>
    public const float BinEdge = 6.0f;

    /// <summary>The minimum hard-Subtraction carve count a bin needs to be bake-eligible (plan §4): below this the
    /// analytic instances are cheaper than a brick's fixed pool + instruction footprint.</summary>
    public const int MinHardCarvesToBake = 16;

    /// <summary>A carve thinner than this many nominal voxels (<c>radius &lt; MinCarveVoxelRadius · h</c>, h =
    /// <see cref="BinEdge"/>/<see cref="SdfBrickPoolLayout.BrickDim"/>) can't meet the trilinear fidelity budget, so it
    /// stays analytic and never joins a bin's bakeable set (plan §4).</summary>
    public const int MinCarveVoxelRadius = 4;

    /// <summary>The settle window for the interactive debug pool: a bin bakes after this many produced frames with no
    /// membership change (plan §4 — 120 ≈ 2 s at 60 fps). The bench adapter and <c>sdf.bake now</c> use 0.</summary>
    public const int DefaultSettleFrames = 120;

    // λ = √3 folded into the STORED brick values (plan §1's step-safety contract): the trilinear interpolant of c/λ is
    // 1-Lipschitz, so a brick applies NO stepScale tax. The bake writes c·InvLambda; the outside-box boundary floor is
    // margin·InvLambda. KEEP IN SYNC with the √3 the sdf-brick-bake.comp baker and sdfSampledRegion assume.
    private const float InvLambda = 0.5773502691896258f;   // 1/√3 (λ = √3)

    // The per-slot request-buffer carve ceiling (KEEP IN SYNC with SdfWorldEngine.MaxBrickCarvesPerBake). A bin holding
    // more than this stays analytic rather than throwing at RequestBrickBake — it cannot happen with the debug pool's
    // 4096-carve cap even if every carve lands in one bin, but the guard keeps the planner total.
    private const int MaxCarvesPerBrick = 4096;

    // THE PROCESS-WIDE FEATURE GATE (plan §5). A single static flag every planner instance consults — the interactive
    // debug pool AND the bench's carves workload — so the one `sdf.carve-bake` switch (BenchInstaller) reaches every
    // planner without any single frame source owning them all (the GpuTimingControl.Shared static-control precedent).
    // Enabled by default: dense analytic carve clusters benefit from the sampled-region cache.
    // subtraction instances bake to ONE SampledRegion brick, collapsing the GPU frame 120 ms → 3.31 ms (beam
    // 7.7 → 0.37 ms, views 111 → 2.9 ms) at native/shadows-on, so the recommended demo pairing (carve-bake ON,
    // shadow-proxy OFF) ships on. An explicit `sdf.carve-bake off` still emits analytic, BIT-IDENTICAL to pre-arc —
    // and deleting every brick reproduces the identical scene (the cache-not-representation contract, plan §1). Post
    // world stages render static programs with no planner, so this default never reaches them.
    private static volatile bool s_enabled = true;

    /// <summary>The process-wide carve-bake feature gate (plan §5). Off (the default) makes every planner emit analytic
    /// always — bit-identical to today; on lets settled clusters bake. Flipped by the <c>sdf.carve-bake</c> switch.</summary>
    public static bool Enabled {
        get => s_enabled;
        set => s_enabled = value;
    }

    private sealed class Bin {
        public BinPhase Phase;
        public int Slot = -1;
        public int QuietFrames;
        public ulong Signature;      // order-independent hash of the current bakeable membership
        public ulong BakeSignature;  // membership hash captured at RequestBrickBake (invalidation compares against it)
        public ulong BakeSerial;     // the slot serial our bake owns (a mismatch = a re-request superseded us)
        public int AdoptedFrame;     // the frame this bin last became a brick (LRU eviction order)
        public bool Seen;            // touched this Advance (unseen bins have lost all their carves — prune them)
        // The baked brick geometry (valid while Baking/Brick) — Emit reads these VERBATIM so the SampledRegion lanes
        // match the pool contents the bake wrote (never recomputed from possibly-changed carves).
        public Vector3 BoxMin;
        public float CellSize;
        public int DimX;
        public int DimY;
        public int DimZ;
        public float BoundaryFloor;
    }
    private enum BinPhase {
        Analytic, // emitted as its member carves (the default, and the whole scene when Enabled is off)
        Baking,   // a bake is in flight for this bin's slot; still emitted analytic until Ready
        Brick,    // adopted: emitted as one SampledRegion sampling the baked slot
    }

    private readonly int m_settleFrames;
    private readonly Dictionary<CellKey, Bin> m_bins = [];
    private readonly Bin?[] m_slotOwners = new Bin?[SdfBrickPoolLayout.MaxBricks];
    // Reused per-Advance scratch (cleared each frame) so the steady state allocates nothing while the mode is up.
    private readonly Dictionary<CellKey, CellAccum> m_scratch = [];
    private int m_frame;
    private int m_lastRevision;
    private bool m_forceSettle;
    // Set when AcquireSlot LRU-evicts an adopted brick mid-Advance (a Brick→Analytic transition inside the request
    // path): the emit plan changed, so Advance must return true even though the request itself does not flip emission.
    private bool m_evictionChangedEmit;

    /// <summary>Initializes a planner with the given settle window (produced frames of quiescence before a bin bakes).
    /// The interactive debug pool passes <see cref="DefaultSettleFrames"/>; the bench passes 0 (immediate).</summary>
    /// <param name="settleFrames">Quiet produced frames before an eligible bin bakes; clamped to ≥ 0.</param>
    public SdfCarveBakePlanner(int settleFrames = DefaultSettleFrames) {
        m_settleFrames = Math.Max(val1: 0, val2: settleFrames);
    }

    /// <summary>Advances the planner one produced frame against the current carve list (plan §3/§4): re-bins, counts
    /// quiet frames, polls bake states, requests newly-settled bakes, and adopts <see cref="BrickBakeState.Ready"/>
    /// bins. Off (<see cref="Enabled"/> false, or a pool-less engine) it releases any adopted bins back to analytic.
    /// Returns <see langword="true"/> when the emit plan changed, so the caller bumps its content revision to force the
    /// ordinary rebuild that re-runs <see cref="Emit"/>.</summary>
    /// <param name="carves">The live carve pool (the frame source's authored subtraction list).</param>
    /// <param name="carveRevision">A monotonic content revision — a change signals the carve set may have edited.</param>
    /// <param name="bakes">The engine's bake service (poll/request). Never null on the live path.</param>
    /// <returns>Whether the adopted-brick set changed (the caller should bump its revision).</returns>
    public bool Advance(IReadOnlyList<SdfCarve> carves, int carveRevision, ISdfBrickBakeService bakes) {
        ArgumentNullException.ThrowIfNull(carves);
        ArgumentNullException.ThrowIfNull(bakes);

        if (!s_enabled || !bakes.BrickBakeAvailable) {
            m_lastRevision = carveRevision;

            return ReleaseAll();
        }

        var changed = false;
        var effectiveSettle = (m_forceSettle ? 0 : m_settleFrames);

        m_forceSettle = false;
        m_lastRevision = carveRevision;

        // 1. Accumulate this frame's BAKEABLE membership per cell (hard subtraction, radius ≥ the fidelity floor).
        //    Smooth and sub-4-voxel carves are excluded here — they always stay analytic (plan §4).
        m_scratch.Clear();

        var minRadius = (MinCarveVoxelRadius * (BinEdge / SdfBrickPoolLayout.BrickDim));

        for (var index = 0; (index < carves.Count); index++) {
            var carve = carves[index];

            if (carve.Smooth || (carve.Radius < minRadius)) {
                continue;
            }

            var key = CellKey.For(center: carve.Center);

            if (!m_scratch.TryGetValue(key: key, value: out var accum)) {
                accum = new CellAccum();
                m_scratch[key] = accum;
            }

            accum.Add(carve: carve);
        }

        foreach (var bin in m_bins.Values) {
            bin.Seen = false;
        }

        // 2. Reconcile each cell against its bin: invalidate a changed baked bin, poll an in-flight one, count quiet
        //    frames on an analytic one, and request a bake once eligible + settled.
        foreach (var (key, accum) in m_scratch) {
            if (!m_bins.TryGetValue(key: key, value: out var bin)) {
                bin = new Bin();
                m_bins[key] = bin;
            }

            bin.Seen = true;

            var signature = accum.Signature;
            var eligible = ((accum.Count >= MinHardCarvesToBake) && (accum.Count <= MaxCarvesPerBrick));

            // INVALIDATION: a baked/baking bin whose membership no longer matches what it baked reverts to analytic
            // (re-emitted this same rebuild) and re-settles from scratch — the per-region invalidation of plan §3/§4.
            if ((bin.Phase != BinPhase.Analytic) && (signature != bin.BakeSignature)) {
                ReleaseSlot(bin: bin);
                bin.Phase = BinPhase.Analytic;
                bin.QuietFrames = 0;
                changed = true;
            }

            switch (bin.Phase) {
                case BinPhase.Baking: {
                        var status = bakes.GetBrickState(slot: bin.Slot);

                        if (status.Serial != bin.BakeSerial) {
                            // Our bake was superseded (a re-request grabbed the slot) — fall back to analytic and re-settle.
                            ReleaseSlot(bin: bin);
                            bin.Phase = BinPhase.Analytic;
                            bin.QuietFrames = 0;
                            changed = true;
                        } else if (status.State == BrickBakeState.Ready) {
                            bin.Phase = BinPhase.Brick;
                            bin.AdoptedFrame = m_frame;
                            changed = true;
                        }

                        break;
                    }

                case BinPhase.Brick:
                    // Stable adopted brick — nothing to do (invalidation above already caught any edit).
                    break;

                default: {
                        // Analytic: count quiet frames (reset on any membership change) and bake once eligible + settled.
                        // A bake REQUEST does not change the emit plan (the bin stays emitted analytic until Ready), so it
                        // does NOT set `changed` — only the later Ready adoption (above) flips the emitted geometry.
                        bin.QuietFrames = ((signature == bin.Signature) ? (bin.QuietFrames + 1) : 0);
                        bin.Signature = signature;

                        if (eligible && (bin.QuietFrames >= effectiveSettle)) {
                            _ = TryRequestBake(bin: bin, accum: accum, carves: carves, key: key, bakes: bakes);
                        }

                        break;
                    }
            }
        }

        // 3. Prune bins that lost every bakeable carve this frame (a cleared/emptied cluster) — free any slot they held.
        changed |= PruneUnseen();
        // Fold in any LRU eviction that reverted a brick to analytic mid-loop (see AcquireSlot).
        changed |= m_evictionChangedEmit;
        m_evictionChangedEmit = false;
        m_frame++;

        return changed;
    }

    /// <summary>Emits the carve pool at rebuild time (plan §2/§4): one <see cref="SdfShapeType.SampledRegion"/> instance
    /// per adopted bin, and analytic subtraction instances for every carve not represented by a brick. When
    /// <see cref="Enabled"/> is off — or nothing has been adopted yet — this is a byte-identical analytic emission (the
    /// switch's OFF proof). Called from <see cref="Puck.SdfVm.Debug.SdfDebugRenderer"/> in place of the raw carve loop.</summary>
    /// <param name="builder">The program builder (carves emit LAST, after the subject/floor).</param>
    /// <param name="carves">The live carve pool.</param>
    /// <param name="material">The carve-cavity material id.</param>
    public void Emit(SdfProgramBuilder builder, IReadOnlyList<SdfCarve> carves, int material) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(carves);

        if (!s_enabled) {
            EmitAnalytic(builder: builder, carves: carves, material: material);

            return;
        }

        var minRadius = (MinCarveVoxelRadius * (BinEdge / SdfBrickPoolLayout.BrickDim));
        var anyBrick = false;

        // Pass 1: one SampledRegion per adopted bin, in slot order (a deterministic emission order across frames).
        for (var slot = 0; (slot < m_slotOwners.Length); slot++) {
            if (m_slotOwners[slot] is { Phase: BinPhase.Brick } bin) {
                EmitBrick(builder: builder, bin: bin, slot: slot, material: material);
                anyBrick = true;
            }
        }

        if (!anyBrick) {
            // Nothing adopted — the whole pool is analytic, byte-identical to the raw carve loop.
            EmitAnalytic(builder: builder, carves: carves, material: material);

            return;
        }

        // Pass 2: analytic for every carve NOT folded into an adopted brick (smooth/thin carves, and any carve whose
        // bin has not adopted a brick). A brick-represented carve is a hard, ≥-fidelity carve whose cell is adopted.
        for (var index = 0; (index < carves.Count); index++) {
            var carve = carves[index];
            var represented = (!carve.Smooth
                && (carve.Radius >= minRadius)
                && m_bins.TryGetValue(key: CellKey.For(center: carve.Center), value: out var bin)
                && (bin.Phase == BinPhase.Brick));

            if (!represented) {
                EmitOneCarve(builder: builder, carve: carve, material: material);
            }
        }
    }

    /// <summary>Folds the planner's WORST CASE into a capacity probe (plan §4): <see cref="SdfBrickPoolLayout.MaxBricks"/>
    /// full-resolution <c>SampledRegion</c> instances — so a frozen envelope reserves room for every slot being baked
    /// on top of a full analytic carve pool (the worst mixed case). Never rendered; static because the worst case is
    /// pool-shaped, not instance-state.</summary>
    /// <param name="builder">The probe builder (already carrying the analytic carve worst case).</param>
    /// <param name="material">The carve-cavity material id.</param>
    public static void EmitWorstCaseBricks(SdfProgramBuilder builder, int material) {
        ArgumentNullException.ThrowIfNull(builder);

        // A full BrickDim³ box at unit cell — the wordiest/most-bounded a slot can emit. Position is irrelevant to size.
        const float cell = (BinEdge / SdfBrickPoolLayout.BrickDim);

        var extent = (SdfBrickPoolLayout.BrickDim * cell);
        var boundRadius = (0.5f * new Vector3(x: extent, y: extent, z: extent).Length());

        for (var slot = 0; (slot < SdfBrickPoolLayout.MaxBricks); slot++) {
            builder.BeginInstance(boundCenter: new Vector3(value: (0.5f * extent)), boundRadius: boundRadius);
            _ = builder.ResetPoint().SampledRegion(
                boxMin: Vector3.Zero,
                cellSize: cell,
                dimX: SdfBrickPoolLayout.BrickDim,
                dimY: SdfBrickPoolLayout.BrickDim,
                dimZ: SdfBrickPoolLayout.BrickDim,
                brickWordOffset: SdfBrickPoolLayout.SlotWordOffset(slot: slot),
                boundaryFloor: 0f,
                material: material
            );
            builder.EndInstance();
        }
    }

    /// <summary>Forces every currently-eligible bin to settle on the NEXT <see cref="Advance"/> (settle window collapsed
    /// to 0 for that pass) — the <c>sdf.bake now</c> accelerator and the bench's settle-0 seam. Returns a status line.</summary>
    /// <returns>A human-readable summary of what will bake.</returns>
    public string SettleNow() {
        if (!s_enabled) {
            return "carve-bake is off (sdf.carve-bake on to enable) — nothing to settle.";
        }

        m_forceSettle = true;

        var (analytic, baking, brick) = CountPhases();
        var pending = (analytic + baking); // analytic bins bake next frame; baking ones are already in flight

        return $"settling now — {pending} bin(s) will bake on the next frame ({brick} already adopted). Watch sdf.bake status.";
    }

    /// <summary>The live per-phase bin counts (<c>Analytic</c> / <c>Baking</c> / <c>Brick</c>). The readiness signal a
    /// settle-0 bench adapter polls so its warm window absorbs the bake and its sampled window measures the baked steady
    /// state (plan §4/§6): a carves scene is "settled" once no bin is still <c>Baking</c> and at least one has adopted a
    /// <c>Brick</c>. Zero-allocation — a cheap walk of the live bin map.</summary>
    public (int Analytic, int Baking, int Brick) PhaseCounts => CountPhases();

    /// <summary>Describes the planner's live state for <c>sdf.bake status</c>: the feature gate, per-phase bin counts,
    /// slot occupancy, and the settle window.</summary>
    /// <returns>A one-line status.</returns>
    public string DescribeStatus() {
        var (analytic, baking, brick) = CountPhases();
        var slotsUsed = 0;

        foreach (var owner in m_slotOwners) {
            if (owner is not null) {
                slotsUsed++;
            }
        }

        return $"carve-bake {(s_enabled ? "on" : "off")} | bins {m_bins.Count} ({brick} brick, {baking} baking, {analytic} analytic) | slots {slotsUsed}/{SdfBrickPoolLayout.MaxBricks} | settle {m_settleFrames}f | rev {m_lastRevision}";
    }

    // ── internals ──────────────────────────────────────────────────────────────────────────────────────────────────

    // Requests a bake for an eligible, settled bin: acquires a slot (free, else LRU-evict an adopted brick), computes
    // the brick box/dims/cell from the bin's member carves, stores that geometry for Emit, and dispatches the bake.
    // Returns false (and leaves the bin analytic) when no slot can be freed this frame — it retries next frame.
    private bool TryRequestBake(Bin bin, CellAccum accum, IReadOnlyList<SdfCarve> carves, CellKey key, ISdfBrickBakeService bakes) {
        var slot = AcquireSlot(forBin: bin);

        if (slot < 0) {
            return false;
        }

        // Brick box = the members' tight AABB grown by margin m = maxRadius + 2h, so every carve surface sits ≥ m
        // inside the box and the zero set never touches a face (plan §1). The cubic cell is h, enlarged only if the box
        // would otherwise exceed BrickDim voxels on its longest axis (keeps dims ≤ BrickDim; fidelity degrades a hair).
        var h = (BinEdge / SdfBrickPoolLayout.BrickDim);
        var margin = (accum.MaxRadius + (2f * h));
        var boxMin = (accum.Min - new Vector3(value: margin));
        var boxMax = (accum.Max + new Vector3(value: margin));
        var extent = (boxMax - boxMin);
        var cell = MathF.Max(x: h, y: (MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z)) / SdfBrickPoolLayout.BrickDim));
        var dimX = Math.Clamp(value: (int)MathF.Ceiling(x: (extent.X / cell)), min: 1, max: SdfBrickPoolLayout.BrickDim);
        var dimY = Math.Clamp(value: (int)MathF.Ceiling(x: (extent.Y / cell)), min: 1, max: SdfBrickPoolLayout.BrickDim);
        var dimZ = Math.Clamp(value: (int)MathF.Ceiling(x: (extent.Z / cell)), min: 1, max: SdfBrickPoolLayout.BrickDim);

        // Gather this bin's member carves (hard, ≥ fidelity) as the bake's sphere list. Baking is rare (once per
        // settle), so this second filtering pass over the pool is cheap.
        var minRadius = (MinCarveVoxelRadius * h);
        var members = new List<Vector4>(capacity: accum.Count);

        for (var index = 0; (index < carves.Count); index++) {
            var carve = carves[index];

            if (!carve.Smooth && (carve.Radius >= minRadius) && CellKey.For(center: carve.Center).Equals(other: key)) {
                members.Add(item: new Vector4(value: carve.Center, w: carve.Radius));
            }
        }

        bakes.RequestBrickBake(slot: slot, request: new BrickBakeRequest(
            BoxMin: boxMin,
            CellSize: cell,
            DimX: dimX,
            DimY: dimY,
            DimZ: dimZ,
            InverseLambda: InvLambda,
            Carves: members.ToArray()
        ));

        bin.Slot = slot;
        bin.BakeSerial = bakes.GetBrickState(slot: slot).Serial;
        bin.BakeSignature = accum.Signature;
        bin.Phase = BinPhase.Baking;
        bin.BoxMin = boxMin;
        bin.CellSize = cell;
        bin.DimX = dimX;
        bin.DimY = dimY;
        bin.DimZ = dimZ;
        bin.BoundaryFloor = (margin * InvLambda);
        m_slotOwners[slot] = bin;

        return true;
    }

    // Finds a free slot, or evicts the least-recently-adopted BRICK bin (LRU) when all slots are taken — the evicted bin
    // reverts to analytic (plan §4). Returns -1 when every slot is mid-bake (no adopted brick to evict) — the caller
    // retries next frame. With the demo's defaults a 9th eligible bin never arises, so eviction stays theoretical.
    private int AcquireSlot(Bin forBin) {
        for (var slot = 0; (slot < m_slotOwners.Length); slot++) {
            if (m_slotOwners[slot] is null) {
                return slot;
            }
        }

        Bin? victim = null;
        var victimSlot = -1;

        for (var slot = 0; (slot < m_slotOwners.Length); slot++) {
            var owner = m_slotOwners[slot];

            if ((owner is { Phase: BinPhase.Brick }) && ((victim is null) || (owner.AdoptedFrame < victim.AdoptedFrame))) {
                victim = owner;
                victimSlot = slot;
            }
        }

        if (victim is null) {
            return -1;
        }

        victim.Phase = BinPhase.Analytic;
        victim.Slot = -1;
        victim.QuietFrames = 0;
        m_slotOwners[victimSlot] = null;
        // The evicted brick stops being emitted (its carves revert to analytic) — an emit-plan change the caller must
        // rebuild on, or the stale program keeps a SampledRegion pointing at a slot about to hold different content.
        m_evictionChangedEmit = true;

        return ((victim == forBin) ? -1 : victimSlot);
    }

    // Emits one adopted bin as a single SampledRegion subtraction instance (plan §2). The bound is the box circumsphere
    // — a real cull bound, so PackInstances/the grid/beam treat it as any Subtraction-blend instance. The lane values
    // come from the bin's stored geometry (what the bake wrote), never recomputed.
    private static void EmitBrick(SdfProgramBuilder builder, Bin bin, int slot, int material) {
        var extent = new Vector3(x: (bin.DimX * bin.CellSize), y: (bin.DimY * bin.CellSize), z: (bin.DimZ * bin.CellSize));
        var center = (bin.BoxMin + (0.5f * extent));

        builder.BeginInstance(boundCenter: center, boundRadius: (0.5f * extent.Length()));
        _ = builder.ResetPoint().SampledRegion(
            boxMin: bin.BoxMin,
            cellSize: bin.CellSize,
            dimX: bin.DimX,
            dimY: bin.DimY,
            dimZ: bin.DimZ,
            brickWordOffset: SdfBrickPoolLayout.SlotWordOffset(slot: slot),
            boundaryFloor: bin.BoundaryFloor,
            material: material
        );
        builder.EndInstance();
    }

    // The analytic emission of ONE carve — a static world-level subtraction instance bounded by the carve radius (the
    // packer adds the float-safety padding + smooth halo). KEEP IN SYNC with SdfDebugRenderer.EmitCarves' per-carve
    // body: the Enabled-off / nothing-baked paths must reproduce that emission exactly (the switch's byte-identity).
    private static void EmitOneCarve(SdfProgramBuilder builder, SdfCarve carve, int material) {
        var blend = (carve.Smooth ? SdfBlendOp.SmoothSubtraction : SdfBlendOp.Subtraction);

        builder.BeginInstance(boundCenter: carve.Center, boundRadius: carve.Radius);
        _ = builder.ResetPoint().Translate(offset: carve.Center).Sphere(radius: carve.Radius, material: material, blend: blend, smooth: (carve.Smooth ? carve.SmoothK : 0f));
        builder.EndInstance();
    }
    private static void EmitAnalytic(SdfProgramBuilder builder, IReadOnlyList<SdfCarve> carves, int material) {
        for (var index = 0; (index < carves.Count); index++) {
            EmitOneCarve(builder: builder, carve: carves[index], material: material);
        }
    }

    // Reverts every bin to analytic and frees every slot (the Enabled-off / pool-less path). Returns whether anything
    // was actually adopted/baking (so the caller bumps its revision to rebuild the now-analytic scene exactly once).
    private bool ReleaseAll() {
        var changed = false;

        foreach (var bin in m_bins.Values) {
            if (bin.Phase != BinPhase.Analytic) {
                changed = true;
            }
        }

        m_bins.Clear();
        Array.Clear(array: m_slotOwners);
        m_forceSettle = false;
        m_evictionChangedEmit = false;

        return changed;
    }

    // Drops bins untouched this Advance (their cluster lost every bakeable carve) and frees any slot they held.
    private bool PruneUnseen() {
        List<CellKey>? dead = null;

        foreach (var (key, bin) in m_bins) {
            if (!bin.Seen) {
                (dead ??= []).Add(item: key);
            }
        }

        if (dead is null) {
            return false;
        }

        var changed = false;

        foreach (var key in dead) {
            var bin = m_bins[key];

            if (bin.Phase != BinPhase.Analytic) {
                changed = true;
            }

            ReleaseSlot(bin: bin);
            _ = m_bins.Remove(key: key);
        }

        return changed;
    }
    private void ReleaseSlot(Bin bin) {
        if ((bin.Slot >= 0) && (bin.Slot < m_slotOwners.Length)) {
            m_slotOwners[bin.Slot] = null;
        }

        bin.Slot = -1;
    }
    private (int Analytic, int Baking, int Brick) CountPhases() {
        var analytic = 0;
        var baking = 0;
        var brick = 0;

        foreach (var bin in m_bins.Values) {
            switch (bin.Phase) {
                case BinPhase.Baking:
                    baking++;

                    break;
                case BinPhase.Brick:
                    brick++;

                    break;
                default:
                    analytic++;

                    break;
            }
        }

        return (analytic, baking, brick);
    }

    // A bin's lattice cell — carves bin by NEAREST cell centre (round) so an origin-straddling cluster stays whole.
    private readonly record struct CellKey(int X, int Y, int Z) {
        public static CellKey For(Vector3 center) => new(
            X: (int)MathF.Round(x: (center.X / BinEdge)),
            Y: (int)MathF.Round(x: (center.Y / BinEdge)),
            Z: (int)MathF.Round(x: (center.Z / BinEdge))
        );
    }

    // Per-cell accumulation for one Advance: the bakeable member count, an order-independent membership signature, and
    // the tight AABB + max radius the brick box derives from. A class so it mutates in place inside the scratch map.
    private sealed class CellAccum {
        public int Count;
        public ulong Signature;
        public float MaxRadius;
        public Vector3 Min = new(value: float.PositiveInfinity);
        public Vector3 Max = new(value: float.NegativeInfinity);

        public void Add(SdfCarve carve) {
            Count++;
            // Sum of a per-carve hash — commutative, so pool ORDER never changes the signature (only membership does).
            Signature += Hash(carve: carve);
            MaxRadius = MathF.Max(x: MaxRadius, y: carve.Radius);
            Min = Vector3.Min(value1: Min, value2: (carve.Center - new Vector3(value: carve.Radius)));
            Max = Vector3.Max(value1: Max, value2: (carve.Center + new Vector3(value: carve.Radius)));
        }

        private static ulong Hash(SdfCarve carve) {
            var h = 1469598103934665603UL; // FNV-1a offset basis

            h = Mix(h: h, value: BitConverter.SingleToUInt32Bits(value: carve.Center.X));
            h = Mix(h: h, value: BitConverter.SingleToUInt32Bits(value: carve.Center.Y));
            h = Mix(h: h, value: BitConverter.SingleToUInt32Bits(value: carve.Center.Z));
            h = Mix(h: h, value: BitConverter.SingleToUInt32Bits(value: carve.Radius));

            return h;
        }
        private static ulong Mix(ulong h, uint value) => ((h ^ value) * 1099511628211UL); // FNV-1a prime
    }
}
