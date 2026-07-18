using Puck.Maths;

namespace Puck.Demo.Garden;

/// <summary>One branch/trunk segment: a rounded-cone body from <see cref="Base"/> along <see cref="Direction"/> for
/// <see cref="Length"/>, tapering from <see cref="RadiusStart"/> to <see cref="RadiusEnd"/>. <see cref="Depth"/> is
/// the branch order (0 = trunk) — the reveal-ladder gate a growth stage compares against. Every field is fixed-point
/// or an integer: the segment IS the tree's structure, decided once by <see cref="GardenTreeGenerator.Generate"/>
/// and never touched again (a growth stage only SCALES a frontier segment's rendered length/radius — see
/// <see cref="GardenTreeStructure"/> — it never recomputes a position).</summary>
internal readonly record struct GardenSegment(FixedVector3 Base, FixedVector3 Direction, FixedQ4816 Length, FixedQ4816 RadiusStart, FixedQ4816 RadiusEnd, int Depth);

/// <summary>One canopy/leaf cluster: a sphere at <see cref="Position"/>, radius <see cref="Radius"/>, born at branch
/// order <see cref="Depth"/> (a terminal branch's tip earns one).</summary>
internal readonly record struct GardenLeaf(FixedVector3 Position, FixedQ4816 Radius, int Depth);

// One pending branch node awaiting expansion — the L-system's "X(depth, base, pitch, yaw, length, radiusStart,
// radiusEnd)" alphabet symbol (see GardenTreeGenerator's remarks), queued rather than recursed into (see Generate).
internal readonly record struct GardenPendingBranch(FixedVector3 Base, int PitchIndex, int YawIndex, FixedQ4816 Length, FixedQ4816 RadiusStart, FixedQ4816 RadiusEnd, int Depth);

/// <summary>
/// The deterministic expansion of one seed into a bounded branch/leaf list — a small parametric L-system:
/// <list type="bullet">
/// <item><b>Alphabet</b>: <c>X(depth, base, pitch, yaw, length, radiusStart, radiusEnd)</c> — a pending branch node
/// (<see cref="GardenPendingBranch"/>); <c>F</c> — a drawn rounded-cone segment (a realized <see cref="GardenSegment"/>);
/// <c>L</c> — a drawn leaf sphere (a realized <see cref="GardenLeaf"/>).</item>
/// <item><b>Axiom</b>: one <c>X</c> at the origin, pitch/yaw drawn once per seed (the trunk's own slight lean),
/// length/radius drawn once per seed from a small preset range.</item>
/// <item><b>Production</b>: <c>X(d) → F [ X(d+1, pitch+step, yaw+0) ] [ X(d+1, pitch+step, yaw+spacing) ] { [ X(d+1,
/// pitch+step, yaw+2·spacing) ] when a third child is drawn }</c> for <c>d &lt; MaxRecursionDepth</c> and the branch
/// budget not yet spent; every child's <c>length</c>/<c>radius</c> shrink by the seed's own preset factor. A node
/// that cannot recurse (budget spent, length below the minimum, or the recursion-depth backstop) resolves to <c>L</c>
/// instead of <c>F [...]</c>.</item>
/// </list>
/// THE EXPANSION ORDER IS BREADTH-FIRST (a FIFO queue of pending <c>X</c> nodes — <see cref="Generate"/> — never
/// direct recursion): the branch budget is a SHARED, GLOBAL resource every pending node draws against in the order
/// it was born, so it spends evenly across the whole crown's width before any one branch's lineage can consume it.
/// Every node at order <c>d</c>
/// finishes before any node at order <c>d+1</c> starts, so the budget fans out level by level into a proper crown.
/// <para>
/// Two SEPARATE budgets (<see cref="MaxBranchPrimitives"/>, <see cref="MaxLeafPrimitives"/>) bound the expansion —
/// branches and leaves each get a guaranteed share, so a bushy draw can never crowd the canopy out entirely. Every
/// decision (branch count, pitch step, shrink factor, yaw jitter, leaf jitter, palette) is drawn from
/// <see cref="GardenRng"/> — a seeded, integer-only stream — and every length/radius/position computed in
/// <see cref="FixedQ4816"/>/<see cref="FixedVector3"/> arithmetic, so <see cref="Generate"/> is a pure function of
/// (seed, worstCase): the same seed always expands to the same structure, bit for bit, on every machine.
/// </para>
/// <para>
/// THE PROBE CONTRACT: <c>worstCase: true</c> forces every count/length/shrink draw to its MOST GENEROUS legal value
/// (3 children every node, the slowest shrink preset, the tallest trunk) instead of drawing from <see cref="GardenRng"/>
/// — this guarantees the expansion saturates BOTH budgets (<see cref="MaxPrimitivesPerTree"/> exactly) before the
/// recursion-depth backstop or the minimum-length gate could cut it short on their own, so no real seed's draw (whose
/// counts are always ≥ the forced minimum the saturation proof relies on) can ever exceed it. See
/// <see cref="GardenRenderer"/> for where this feeds <see cref="Puck.SdfVm.SdfEmitContext.Probe"/>.
/// </para>
/// </summary>
internal static class GardenTreeGenerator {
    /// <summary>The recursion-depth backstop (never the actual binding constraint under normal presets — the branch
    /// budget saturates first; see the probe-contract remarks).</summary>
    internal const int MaxRecursionDepth = 6;
    /// <summary>The trunk+branch segment budget per tree.</summary>
    internal const int MaxBranchPrimitives = 16;
    /// <summary>The leaf-cluster budget per tree.</summary>
    internal const int MaxLeafPrimitives = 10;
    /// <summary>The total primitive ceiling per tree (<see cref="MaxBranchPrimitives"/> + <see cref="MaxLeafPrimitives"/>)
    /// — well inside the ≤40-per-tree envelope budget.</summary>
    internal const int MaxPrimitivesPerTree = (MaxBranchPrimitives + MaxLeafPrimitives);
    /// <summary>The number of trunk/leaf color palettes a seed can draw (visible seed-to-seed variety).</summary>
    internal const int PaletteCount = 4;
    /// <summary>How many sim ticks one growth stage spans — <see cref="GardenRenderer"/> and the <c>garden.list</c>
    /// verb both key off this. The overworld's fixed sim rate is 240 Hz (<c>LauncherWindowHostedService.TargetUpdateRate</c>),
    /// so 720 ticks ≈ 3 real seconds/stage — a watchable "it's growing" cadence (a few real seconds per rung, not an
    /// instant pop and not a glacial wait) rather than a value tuned against ticks-per-frame (which varies with real
    /// present timing — see docs/agent-guide.md's "tick allocation sits near a wall-clock boundary" note).</summary>
    internal const ulong TicksPerStage = 720UL;

    private static readonly FixedQ4816 MinBranchLength = FixedQ4816.FromDouble(value: 0.32);
    // A SHORTER trunk than the first cut (2.5-3.3) on purpose: with a long trunk the branch budget's first couple
    // of generations are still climbing a bare pole, so the whole crown bunches up at the very tip. 1.7-2.3 lets
    // real branching start a level or two sooner, spreading the canopy down the upper trunk instead of pinning it
    // all to one point (the "grace" pass — see GardenRenderer's aesthetic remarks).
    private static readonly FixedQ4816 RootLengthBase = FixedQ4816.FromDouble(value: 1.7);
    private static readonly FixedQ4816 RootLengthStep = FixedQ4816.FromDouble(value: 0.12);
    private static readonly FixedQ4816 RootRadiusBase = FixedQ4816.FromDouble(value: 0.15);
    private static readonly FixedQ4816 RootRadiusStep = FixedQ4816.FromDouble(value: 0.02);
    private static readonly FixedQ4816 TipRadiusFactor = FixedQ4816.FromDouble(value: 0.72);
    private static readonly FixedQ4816 LeafRadiusScale = FixedQ4816.FromDouble(value: 1.9);
    private static readonly FixedQ4816 LeafRadiusFloor = FixedQ4816.FromDouble(value: 0.22);
    // Sorted ascending (least to most generous shrink); the probe contract relies on the LAST entry being the
    // slowest shrink (the tallest/bushiest legal draw) — keep it sorted if this range ever grows.
    private static readonly FixedQ4816[] ShrinkPresets = [
        FixedQ4816.FromDouble(value: 0.60),
        FixedQ4816.FromDouble(value: 0.70),
        FixedQ4816.FromDouble(value: 0.80),
    ];

    /// <summary>Expands one seed into a bounded tree structure.</summary>
    /// <param name="seed">The planted seed — the whole structure is a pure function of this.</param>
    /// <param name="worstCase">
    /// <see langword="true"/> for the ONE construction-time capacity probe: every draw takes its most generous legal
    /// value instead of consulting <see cref="GardenRng"/> (see the type's probe-contract remarks). <see langword="false"/>
    /// for an ordinary planted garden.
    /// </param>
    /// <returns>The expanded structure (segments, leaves, and the deepest branch order actually reached).</returns>
    internal static GardenTreeStructure Generate(uint seed, bool worstCase) {
        var rng = new GardenRng(seed: seed);
        var segments = new List<GardenSegment>(capacity: MaxBranchPrimitives);
        var leaves = new List<GardenLeaf>(capacity: MaxLeafPrimitives);
        var pending = new Queue<GardenPendingBranch>();
        var branchBudget = MaxBranchPrimitives;
        var leafBudget = MaxLeafPrimitives;
        var maxDepthReached = 0;

        var paletteIndex = (worstCase ? 0 : rng.NextRange(minInclusive: 0, maxExclusive: PaletteCount));
        var pitchStep = (worstCase ? 2 : (1 + rng.NextRange(minInclusive: 0, maxExclusive: 2)));
        var shrinkIndex = (worstCase ? (ShrinkPresets.Length - 1) : rng.NextRange(minInclusive: 0, maxExclusive: ShrinkPresets.Length));
        var shrink = ShrinkPresets[shrinkIndex];
        var trunkPitchIndex = (worstCase ? 0 : rng.NextRange(minInclusive: 0, maxExclusive: 2));
        var trunkYawIndex = (worstCase ? 0 : rng.NextRange(minInclusive: 0, maxExclusive: GardenDirectionTable.YawSteps));
        var lengthDraw = (worstCase ? 5 : rng.NextRange(minInclusive: 0, maxExclusive: 6));
        var radiusDraw = (worstCase ? 3 : rng.NextRange(minInclusive: 0, maxExclusive: 4));

        var rootLength = (RootLengthBase + (RootLengthStep * FixedQ4816.FromInteger(value: lengthDraw)));
        var rootRadius = (RootRadiusBase + (RootRadiusStep * FixedQ4816.FromInteger(value: radiusDraw)));
        var tipRadius = (rootRadius * TipRadiusFactor);

        pending.Enqueue(item: new GardenPendingBranch(Base: FixedVector3.Zero, PitchIndex: trunkPitchIndex, YawIndex: trunkYawIndex, Length: rootLength, RadiusStart: rootRadius, RadiusEnd: tipRadius, Depth: 0));

        while (pending.TryDequeue(result: out var node)) {
            maxDepthReached = Math.Max(val1: maxDepthReached, val2: node.Depth);

            if ((branchBudget <= 0) || (node.Depth >= MaxRecursionDepth) || (node.Length < MinBranchLength)) {
                EmitLeaf(leaves: leaves, leafBudget: ref leafBudget, maxDepthReached: ref maxDepthReached, rng: ref rng, position: node.Base, radius: node.RadiusEnd, depth: node.Depth);

                continue;
            }

            var direction = GardenDirectionTable.Direction(pitchIndex: node.PitchIndex, yawIndex: node.YawIndex);
            var tip = (node.Base + (direction * node.Length));

            segments.Add(item: new GardenSegment(Base: node.Base, Direction: direction, Length: node.Length, RadiusStart: node.RadiusStart, RadiusEnd: node.RadiusEnd, Depth: node.Depth));
            branchBudget--;

            var childLength = (node.Length * shrink);
            var childRadiusStart = node.RadiusEnd;
            var childRadiusEnd = (node.RadiusEnd * shrink);

            if ((branchBudget <= 0) || (childLength < MinBranchLength)) {
                EmitLeaf(leaves: leaves, leafBudget: ref leafBudget, maxDepthReached: ref maxDepthReached, rng: ref rng, position: tip, radius: node.RadiusEnd, depth: (node.Depth + 1));

                continue;
            }

            // Every child is QUEUED regardless of the budget remaining right now — the budget check happens when
            // each is later DEQUEUED (the line above), which is what makes the whole expansion breadth-first: a
            // node born late in a wide generation still gets its fair turn against the budget before any
            // grandchild does, rather than losing out to an early sibling's whole descending lineage.
            var childCount = (worstCase ? 3 : (2 + rng.NextRange(minInclusive: 0, maxExclusive: 2)));
            var childPitch = Math.Min(val1: (node.PitchIndex + pitchStep), val2: GardenDirectionTable.MaxPitchIndex);
            var spacing = (GardenDirectionTable.YawSteps / childCount);

            for (var i = 0; (i < childCount); i++) {
                var jitter = rng.NextRange(minInclusive: -1, maxExclusive: 2);
                var childYaw = ((node.YawIndex + (spacing * i)) + jitter);

                pending.Enqueue(item: new GardenPendingBranch(Base: tip, PitchIndex: childPitch, YawIndex: childYaw, Length: childLength, RadiusStart: childRadiusStart, RadiusEnd: childRadiusEnd, Depth: (node.Depth + 1)));
            }
        }

        return new GardenTreeStructure(segments: segments, leaves: leaves, maxDepth: maxDepthReached, paletteIndex: paletteIndex);
    }

    // A leaf born from an early-exit path (budget/length limits) always sits ONE order deeper than the node that
    // spawned it — the depth this call receives, never that node's own Depth. Folding it into maxDepthReached HERE
    // (not just at the dequeue loop's top) is load-bearing: otherwise the leaf could carry a Depth past the
    // structure's reported MaxDepth, making it unreachable by GardenGrowth's clamped Stage.
    // A small deterministic position/radius jitter (fixed-point, drawn from the SAME seeded stream every other
    // choice comes from) keeps a cluster of leaves from reading as one uniform pom-pom — a fuller, less repetitive
    // canopy silhouette without spending extra primitive budget.
    private static void EmitLeaf(List<GardenLeaf> leaves, ref int leafBudget, ref int maxDepthReached, ref GardenRng rng, FixedVector3 position, FixedQ4816 radius, int depth) {
        if (leafBudget <= 0) {
            return;
        }

        var baseRadius = FixedQ4816.Max(x: LeafRadiusFloor, y: (radius * LeafRadiusScale));
        var radiusJitter = (baseRadius * FixedQ4816.FromRawBits(value: rng.NextRange(minInclusive: -9830, maxExclusive: 9831))); // ±15%
        var leafRadius = FixedQ4816.Max(x: LeafRadiusFloor, y: (baseRadius + radiusJitter));
        var jitterSpan = (baseRadius * FixedQ4816.FromDouble(value: 0.55));
        var jitteredPosition = new FixedVector3(
            X: (position.X + (jitterSpan * FixedQ4816.FromRawBits(value: rng.NextRange(minInclusive: -32768, maxExclusive: 32769)))),
            Y: (position.Y + (jitterSpan * FixedQ4816.FromRawBits(value: rng.NextRange(minInclusive: -16384, maxExclusive: 24577)))),
            Z: (position.Z + (jitterSpan * FixedQ4816.FromRawBits(value: rng.NextRange(minInclusive: -32768, maxExclusive: 32769))))
        );

        leaves.Add(item: new GardenLeaf(Position: jitteredPosition, Radius: leafRadius, Depth: depth));
        leafBudget--;
        maxDepthReached = Math.Max(val1: maxDepthReached, val2: depth);
    }
}

/// <summary>The full expansion result of one seed (see <see cref="GardenTreeGenerator.Generate"/>): the bounded
/// segment/leaf lists, the deepest branch order the structure actually reached (the reveal ladder's own stage
/// count — a stubby seed's tree finishes growing sooner than a tall one's), and the palette this seed drew.</summary>
internal sealed class GardenTreeStructure(IReadOnlyList<GardenSegment> segments, IReadOnlyList<GardenLeaf> leaves, int maxDepth, int paletteIndex) {
    /// <summary>Every trunk/branch segment, in generation order (parents before children).</summary>
    internal IReadOnlyList<GardenSegment> Segments { get; } = segments;
    /// <summary>Every leaf cluster.</summary>
    internal IReadOnlyList<GardenLeaf> Leaves { get; } = leaves;
    /// <summary>The deepest branch order reached — also this tree's own final growth-stage index.</summary>
    internal int MaxDepth { get; } = maxDepth;
    /// <summary>The trunk/leaf color palette this seed drew (see <see cref="GardenRenderer"/>).</summary>
    internal int PaletteIndex { get; } = paletteIndex;
}
