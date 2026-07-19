using System.Numerics;

namespace Puck.Authoring;

/// <summary>
/// One defined chain's REST geometry, captured once when the chain is defined (<see cref="SculptModel.DefineChain"/>)
/// and never touched again by solving — only the GOAL moves, and every solve re-derives the live pose from this
/// frozen rest data. Shapes stay FLAT in <see cref="SculptShape"/> (no parent refs); a chain is purely a LIST of
/// shape ids in root→tip order plus the rest-frame data the IK math needs:
/// <list type="bullet">
/// <item><description>Joint pivots: the shapes' REST positions, root→tip (joint i is shape i's rest position).</description></item>
/// <item><description>Bone lengths: consecutive joint distances (Shapes.Count − 1 of them).</description></item>
/// <item><description>Per-shape rest offset/orientation IN ITS BONE FRAME: since a shape's own authored position may
/// not sit exactly on the bone, the offset is captured relative to the joint and the shape's rest rotation, so
/// <see cref="ChainSolver.PoseChain"/> can carry it along as the bone reorients.</description></item>
/// </list>
/// Solver output lands in ordinary shape transforms, so a recorded frame snapshots the pose and a chain never appears
/// in <see cref="CreationDocument.Frames"/> — the settled contract that lets any consumer inherit IK for free.
/// </summary>
/// <param name="Id">The chain's stable id (unique within the model).</param>
/// <param name="Name">The player-given name (the console/goal-cycling handle); null for an unnamed chain.</param>
/// <param name="ShapeIds">The member shape ids, ROOT→TIP order (2 for a single bone, 3 for a two-bone limb, N for
/// an N-1-bone spine).</param>
/// <param name="Kind"><see cref="ChainDocument.KindLimb"/> (exactly 3 shapes / 2 bones, solved by
/// <see cref="ChainSolver.SolveLimb"/>) or <see cref="ChainDocument.KindSpine"/> (any length ≥ 2, solved by
/// <see cref="ChainSolver.SolveSpine"/>).</param>
/// <param name="RestJoints">The rest-pose joint positions, root→tip (length = <see cref="ShapeIds"/>.Count).</param>
/// <param name="BoneLengths">Consecutive rest-joint distances (length = <see cref="ShapeIds"/>.Count − 1).</param>
/// <param name="RestOffsets">Each shape's rest position minus ITS joint (its bone's start), in world/rest axes
/// (length = <see cref="ShapeIds"/>.Count).</param>
/// <param name="RestOrientations">Each shape's rest orientation (length = <see cref="ShapeIds"/>.Count).</param>
/// <param name="Goal">The chain's live goal (model space) — the only field a solve reads as "live" input; defaults
/// to the rest tip position so a freshly defined chain solves to its own rest pose.</param>
/// <param name="Pole">The bend-direction hint for a "limb" chain (ignored by "spine"); defaults to a point above
/// the root so a fresh limb bends forward/up rather than along a degenerate axis.</param>
public sealed record SculptChain(
    int Id,
    string? Name,
    IReadOnlyList<int> ShapeIds,
    string Kind,
    IReadOnlyList<Vector3> RestJoints,
    IReadOnlyList<float> BoneLengths,
    IReadOnlyList<Vector3> RestOffsets,
    IReadOnlyList<Quaternion> RestOrientations,
    Vector3 Goal,
    Vector3 Pole
) {
    // Per-solve scratch, lazily sized once on first Solve() and reused every call after (BoneLengths never changes
    // for a chain's lifetime — only Goal/Pole move via `with`, which copies these array REFERENCES, not their
    // contents, so every live copy of a chain still shares one scratch pair). Avoids allocating a lengths copy, a
    // stiffness array, AND a joints array on every drag-frame solve.
    private float[]? m_stiffness;
    private Vector3[]? m_spineScratch;

    /// <summary>Captures a chain's rest geometry from the model's CURRENT shape transforms — call exactly once, when
    /// the chain is defined (a later reshape/re-place does not retroactively change the rest frame; redefine the
    /// chain to recapture it).</summary>
    /// <param name="id">The chain's stable id.</param>
    /// <param name="name">The player-given name (null = unnamed).</param>
    /// <param name="shapeIds">The member shape ids, root→tip order.</param>
    /// <param name="kind"><see cref="ChainDocument.KindLimb"/> or <see cref="ChainDocument.KindSpine"/> (defaults to
    /// limb when <paramref name="shapeIds"/> has exactly 3 members and the caller passes null, else spine).</param>
    /// <param name="positions">The member shapes' rest positions, root→tip (same order/length as
    /// <paramref name="shapeIds"/>).</param>
    /// <param name="rotations">The member shapes' rest orientations, root→tip.</param>
    /// <returns>The captured chain state, its goal seeded at the rest tip and its pole above the root.</returns>
    public static SculptChain Capture(int id, string? name, IReadOnlyList<int> shapeIds, string? kind, IReadOnlyList<Vector3> positions, IReadOnlyList<Quaternion> rotations) {
        ArgumentNullException.ThrowIfNull(shapeIds);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(rotations);

        var boneCount = (shapeIds.Count - 1);
        var lengths = new float[Math.Max(val1: boneCount, val2: 0)];

        for (var index = 0; (index < lengths.Length); index++) {
            lengths[index] = Vector3.Distance(value1: positions[index], value2: positions[(index + 1)]);
        }

        // Rest offset/orientation: with no separate "joint" authoring affordance, the joint IS the shape's own rest
        // position (offset zero) — a future per-shape joint-pivot knob could widen this without touching the solver.
        var offsets = new Vector3[shapeIds.Count];
        var resolvedKind = (kind ?? ((shapeIds.Count == 3) ? ChainDocument.KindLimb : ChainDocument.KindSpine));
        var restTip = ((positions.Count > 0) ? positions[^1] : Vector3.Zero);
        var root = ((positions.Count > 0) ? positions[0] : Vector3.Zero);

        return new SculptChain(
            BoneLengths: lengths,
            Goal: restTip,
            Id: id,
            Kind: resolvedKind,
            Name: name,
            Pole: (root + Vector3.UnitY),
            RestJoints: [.. positions],
            RestOffsets: offsets,
            RestOrientations: [.. rotations],
            ShapeIds: [.. shapeIds]
        );
    }

    /// <summary>Solves this chain's live pose from its frozen rest geometry and current <see cref="Goal"/>/
    /// <see cref="Pole"/>. A "limb" (exactly 3 shapes) uses <see cref="ChainSolver.SolveLimb"/>; anything else (2 or
    /// 4+ shapes) uses <see cref="ChainSolver.SolveSpine"/> — a 2-shape chain degrades to a single bone aimed at the
    /// goal, never a special case the caller must branch on.</summary>
    /// <param name="destination">Receives each member shape's solved (position, rotation), root→tip — CALLER-owned
    /// scratch of at least <see cref="ShapeIds"/>.Count entries, so a held goal drag solves with zero allocation.</param>
    public void Solve(Span<(Vector3 Position, Quaternion Rotation)> destination) {
        var count = ShapeIds.Count;

        if (count == 0) {
            return;
        }

        var root = RestJoints[0];

        if (string.Equals(a: Kind, b: ChainDocument.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) && (count == 3)) {
            var restDirection = ((BoneLengths[0] > 0f) ? ((RestJoints[1] - RestJoints[0]) / BoneLengths[0]) : Vector3.UnitY);

            var (mid, tip) = ChainSolver.SolveLimb(root: root, goal: Goal, lenA: BoneLengths[0], lenB: BoneLengths[1], pole: Pole, restDirection: restDirection);

            destination[0] = PoseJoint(index: 0, joint: root, solvedDirection: RestBoneDirection(index: 0));
            destination[1] = PoseJoint(index: 1, joint: mid, solvedDirection: SafeDirection(from: mid, to: tip, fallback: RestBoneDirection(index: 1)));
            destination[2] = PoseJoint(index: 2, joint: tip, solvedDirection: RestBoneDirection(index: 1));

            return;
        }

        // Bone count is fixed for this chain's whole lifetime (BoneLengths never changes after Capture — only Goal/
        // Pole move), so the stiffness ramp and the joints scratch buffer are computed/sized exactly ONCE and reused
        // by every later Solve() call (a held drag re-solves every frame).
        var boneCount = BoneLengths.Count;

        if ((m_stiffness is not { Length: > 0 } stiffness) || (stiffness.Length != boneCount)) {
            stiffness = new float[boneCount];

            // A linear stiffness ramp (root floppy, tip stiff) is the natural default for an unweighted spine —
            // the settled tail/tentacle read.
            for (var index = 0; (index < stiffness.Length); index++) {
                stiffness[index] = ((stiffness.Length > 1) ? ((index + 1f) / stiffness.Length) : 1f);
            }

            m_stiffness = stiffness;
        }

        if ((m_spineScratch is not { } joints) || (joints.Length != boneCount)) {
            joints = new Vector3[boneCount];
            m_spineScratch = joints;
        }

        // BoneLengths is always array-backed (Capture constructs it as a float[]) — an internal cast, never a
        // public-contract change, so the solve reads it as a span instead of copying it into a fresh array first.
        ChainSolver.SolveSpine(root: root, goal: Goal, lengths: (float[])BoneLengths, stiffness: stiffness, destination: joints);

        destination[0] = PoseJoint(index: 0, joint: root, solvedDirection: ((joints.Length > 0) ? SafeDirection(from: root, to: joints[0], fallback: RestBoneDirection(index: 0)) : RestBoneDirection(index: 0)));

        for (var index = 1; (index < count); index++) {
            var joint = joints[(index - 1)];
            var next = ((index < joints.Length) ? joints[index] : joint);
            var direction = ((index < (count - 1)) ? SafeDirection(from: joint, to: next, fallback: RestBoneDirection(index: index)) : RestBoneDirection(index: Math.Max(val1: (index - 1), val2: 0)));

            destination[index] = PoseJoint(index: index, joint: joint, solvedDirection: direction);
        }
    }

    private (Vector3, Quaternion) PoseJoint(int index, Vector3 joint, Vector3 solvedDirection) {
        var restDirection = RestBoneDirection(index: index);

        return ChainSolver.PoseChain(
            joint: joint,
            restBoneDirection: restDirection,
            restOffset: RestOffsets[index],
            restOrientation: RestOrientations[index],
            solvedDirection: solvedDirection
        );
    }

    // The rest bone direction OWNED by joint `index`: the direction to the NEXT joint for every link but the tip,
    // which inherits the last bone's direction (a tip has no bone of its own to orient by).
    private Vector3 RestBoneDirection(int index) {
        var boneIndex = Math.Clamp(value: index, min: 0, max: (BoneLengths.Count - 1));

        if (BoneLengths.Count == 0) {
            return Vector3.UnitY;
        }

        var length = BoneLengths[boneIndex];

        return ((length > 0.0001f) ? ((RestJoints[(boneIndex + 1)] - RestJoints[boneIndex]) / length) : Vector3.UnitY);
    }
    private static Vector3 SafeDirection(Vector3 from, Vector3 to, Vector3 fallback) {
        var delta = (to - from);
        var length = delta.Length();

        return ((length > 0.0001f) ? (delta / length) : fallback);
    }
}
