using System.Numerics;

namespace Puck.Forge.Authoring;

/// <summary>
/// One chain's REST GEOMETRY — everything an IK solve needs that a <see cref="ChainDocument"/> does not (and never
/// will) carry: joint pivots, bone lengths, and each member shape's rest offset/orientation in its bone frame.
/// Captured once when the chain is (re)defined (<see cref="Capture"/>) from the member shapes' CURRENT positions,
/// and never recomputed for that chain's lifetime — id/name/kind/shape-list/goal/pole are ordinary
/// <see cref="ChainDocument"/> fields living in the document itself; this is the derived solver cache
/// <see cref="SculptModel"/> keeps alongside it, keyed by the chain's stable id (ids are never reused within a
/// session, so a cache entry is valid forever once captured — undo/redo change which ids are VISIBLE in
/// <see cref="CreationDocument.Chains"/>, never what a given id's rest geometry means).
/// </summary>
public sealed class ChainRig {
    // Keeps the law-of-cosines argument strictly inside [-1, 1] (float rounding can walk it just past an edge).
    private const float Epsilon = 0.0001f;

    private readonly IReadOnlyList<Vector3> m_restJoints;
    private readonly IReadOnlyList<float> m_boneLengths;
    private readonly IReadOnlyList<Vector3> m_restOffsets;
    private readonly IReadOnlyList<Quaternion> m_restOrientations;

    // Per-solve scratch, lazily sized once and reused every call after (bone lengths never change for a rig's
    // lifetime) — a held goal/pole drag re-solves every frame without allocating.
    private float[]? m_stiffness;
    private Vector3[]? m_spineScratch;

    private ChainRig(IReadOnlyList<Vector3> restJoints, IReadOnlyList<float> boneLengths, IReadOnlyList<Vector3> restOffsets, IReadOnlyList<Quaternion> restOrientations, Vector3 restGoal, Vector3 restPole) {
        m_restJoints = restJoints;
        m_boneLengths = boneLengths;
        m_restOffsets = restOffsets;
        m_restOrientations = restOrientations;
        RestGoal = restGoal;
        RestPole = restPole;
    }

    /// <summary>The rest tip position — the default goal a fresh chain solves to (holds its own rest pose).</summary>
    public Vector3 RestGoal { get; }
    /// <summary>A point above the root — the default pole (bend-direction hint) a fresh limb bends forward/up by.</summary>
    public Vector3 RestPole { get; }

    /// <summary>Captures a chain's rest geometry from the CURRENT positions/orientations of its member shapes
    /// (root→tip order) — call exactly once, when the chain is (re)defined.</summary>
    /// <param name="positions">The member shapes' rest positions, root→tip.</param>
    /// <param name="rotations">The member shapes' rest orientations, root→tip (same length as
    /// <paramref name="positions"/>).</param>
    /// <returns>The captured rig.</returns>
    public static ChainRig Capture(IReadOnlyList<Vector3> positions, IReadOnlyList<Quaternion> rotations) {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(rotations);

        var boneCount = (positions.Count - 1);
        var lengths = new float[Math.Max(val1: boneCount, val2: 0)];

        for (var index = 0; (index < lengths.Length); index++) {
            lengths[index] = Vector3.Distance(value1: positions[index], value2: positions[(index + 1)]);
        }

        // Rest offset/orientation: with no separate "joint" authoring affordance, the joint IS the shape's own rest
        // position (offset zero) — a future per-shape joint-pivot knob could widen this without touching the solver.
        var offsets = new Vector3[positions.Count];
        var restTip = ((positions.Count > 0) ? positions[^1] : Vector3.Zero);
        var root = ((positions.Count > 0) ? positions[0] : Vector3.Zero);

        return new ChainRig(
            boneLengths: lengths,
            restGoal: restTip,
            restJoints: [.. positions],
            restOffsets: offsets,
            restOrientations: [.. rotations],
            restPole: (root + Vector3.UnitY)
        );
    }
    /// <summary>Solves this rig's live pose from its frozen rest geometry and the caller-supplied live
    /// <paramref name="goal"/>/<paramref name="pole"/> (read fresh from the owning <see cref="ChainDocument"/> every
    /// call, never cached here). A "limb" (exactly 3 joints) uses <see cref="ChainSolver.SolveLimb"/>; anything else
    /// (2 or 4+ joints) uses <see cref="ChainSolver.SolveSpine"/>.</summary>
    /// <param name="kind"><see cref="ChainDocument.KindLimb"/> or <see cref="ChainDocument.KindSpine"/>.</param>
    /// <param name="goal">The live goal position.</param>
    /// <param name="pole">The live pole (bend-direction hint; ignored by "spine").</param>
    /// <param name="destination">Receives each member's solved (position, rotation), root→tip — caller-owned scratch
    /// of at least the captured joint count.</param>
    public void Solve(string kind, Vector3 goal, Vector3 pole, Span<(Vector3 Position, Quaternion Rotation)> destination) {
        var count = m_restJoints.Count;

        if (count == 0) {
            return;
        }

        var root = m_restJoints[0];

        if (string.Equals(a: kind, b: ChainDocument.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) && (count == 3)) {
            var restDirection = ((m_boneLengths[0] > 0f) ? ((m_restJoints[1] - m_restJoints[0]) / m_boneLengths[0]) : Vector3.UnitY);

            var (mid, tip) = ChainSolver.SolveLimb(root: root, goal: goal, lenA: m_boneLengths[0], lenB: m_boneLengths[1], pole: pole, restDirection: restDirection);

            destination[0] = PoseJoint(index: 0, joint: root, solvedDirection: RestBoneDirection(index: 0));
            destination[1] = PoseJoint(index: 1, joint: mid, solvedDirection: SafeDirection(from: mid, to: tip, fallback: RestBoneDirection(index: 1)));
            destination[2] = PoseJoint(index: 2, joint: tip, solvedDirection: RestBoneDirection(index: 1));

            return;
        }

        var boneCount = m_boneLengths.Count;

        if ((m_stiffness is not { Length: > 0 } stiffness) || (stiffness.Length != boneCount)) {
            stiffness = new float[boneCount];

            // A linear stiffness ramp (root floppy, tip stiff) is the natural default for an unweighted spine.
            for (var index = 0; (index < stiffness.Length); index++) {
                stiffness[index] = ((stiffness.Length > 1) ? ((index + 1f) / stiffness.Length) : 1f);
            }

            m_stiffness = stiffness;
        }

        if ((m_spineScratch is not { } joints) || (joints.Length != boneCount)) {
            joints = new Vector3[boneCount];
            m_spineScratch = joints;
        }

        ChainSolver.SolveSpine(root: root, goal: goal, lengths: ((float[])m_boneLengths), stiffness: stiffness, destination: joints);

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
            restOffset: m_restOffsets[index],
            restOrientation: m_restOrientations[index],
            solvedDirection: solvedDirection
        );
    }
    // The rest bone direction OWNED by joint `index`: the direction to the NEXT joint for every link but the tip,
    // which inherits the last bone's direction (a tip has no bone of its own to orient by).
    private Vector3 RestBoneDirection(int index) {
        if (m_boneLengths.Count == 0) {
            return Vector3.UnitY;
        }

        var boneIndex = Math.Clamp(value: index, min: 0, max: (m_boneLengths.Count - 1));
        var length = m_boneLengths[boneIndex];

        return ((length > 0.0001f) ? ((m_restJoints[(boneIndex + 1)] - m_restJoints[boneIndex]) / length) : Vector3.UnitY);
    }
    private static Vector3 SafeDirection(Vector3 from, Vector3 to, Vector3 fallback) {
        var delta = (to - from);
        var length = delta.Length();

        return ((length > Epsilon) ? (delta / length) : fallback);
    }
}
