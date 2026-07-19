using System.Numerics;

namespace Puck.Authoring;

/// <summary>
/// Pure, static IK math for a sculpt model's chain rig (<see cref="SculptChain"/> captures rest geometry and calls in
/// here every time a goal moves). Deliberately in <c>System.Numerics</c> floats — this is HOST-SIDE AUTHORING/RENDER
/// math, never simulation state, so it never belongs in <c>Puck.Maths</c>' fixed-point world. The two-bone solver is
/// analytic: the mid joint sits at the circle-circle intersection of the two bone-length spheres centered on the root
/// and the (reach-clamped) goal, found via the law of cosines rather than an iterative solve. The math is the settled
/// authoring-oracle behavior, preserved value-for-value — a persisted creation's pose must re-derive identically.
/// </summary>
public static class ChainSolver {
    // Keeps the law-of-cosines argument strictly inside [-1, 1] (float rounding can walk it just past an edge) and
    // keeps the reachable-length clamp strictly inside the bone-length triangle inequality (division by a
    // near-degenerate side would blow up the bend angle).
    private const float Epsilon = 0.0001f;

    /// <summary>Solves a two-bone (three-joint) limb by the law of cosines: root and the two bone lengths are fixed,
    /// the goal is the desired tip position, and the pole biases which way the elbow/knee bends. Out-of-reach goals
    /// (closer than <c>|lenA-lenB|</c> or farther than <c>lenA+lenB</c>) fall back to a straight chain aimed at the
    /// goal direction rather than producing a NaN; a goal at (or extremely near) the root falls back to
    /// <paramref name="restDirection"/> so the limb holds a sane pose instead of collapsing to a point.</summary>
    /// <param name="root">The chain's fixed root joint.</param>
    /// <param name="goal">The desired tip position.</param>
    /// <param name="lenA">The root→mid bone length (must be positive).</param>
    /// <param name="lenB">The mid→tip bone length (must be positive).</param>
    /// <param name="pole">A world-space point the bend leans toward (e.g. a knee/elbow-forward hint).</param>
    /// <param name="restDirection">The unit direction the chain holds when the goal is degenerate (≈ the root).</param>
    /// <returns>The solved mid and tip joint positions.</returns>
    public static (Vector3 Mid, Vector3 Tip) SolveLimb(Vector3 root, Vector3 goal, float lenA, float lenB, Vector3 pole, Vector3 restDirection) {
        // Floor each bone off zero before solving. A limb whose root bone has zero length (two shapes stacked at one
        // point) would otherwise divide by 2*lenA*distance = 0 in the law of cosines and normalize a zero-length
        // mid→root — producing a NaN tip that strict JSON serialization later rejects uncaught. Real authored bones
        // dwarf Epsilon, so this is a no-op for any non-degenerate capture.
        lenA = MathF.Max(x: lenA, y: Epsilon);
        lenB = MathF.Max(x: lenB, y: Epsilon);

        var toGoal = (goal - root);
        var distance = toGoal.Length();
        var rest = ((restDirection.LengthSquared() > Epsilon) ? Vector3.Normalize(value: restDirection) : Vector3.UnitY);

        // Degenerate goal (essentially at the root): no meaningful direction to solve toward — hold rest, straight.
        if (distance < Epsilon) {
            var mid0 = (root + (rest * lenA));

            return (mid0, (mid0 + (rest * lenB)));
        }

        var dir = (toGoal / distance);
        // Clamp the EFFECTIVE distance into the triangle-inequality envelope; the direction stays the true direction
        // to the (possibly unreachable) goal, so an out-of-reach target reads as "reaching as far as it can" rather
        // than snapping to some other pose.
        var minReach = (MathF.Abs(x: (lenA - lenB)) + Epsilon);
        var maxReach = ((lenA + lenB) - Epsilon);
        var clampedDistance = Math.Clamp(value: distance, min: MathF.Min(x: minReach, y: maxReach), max: MathF.Max(x: minReach, y: maxReach));

        // Law of cosines: the angle at ROOT between the direction-to-goal and the root→mid bone.
        var cosAngleA = Math.Clamp(
            value: ((((lenA * lenA) + (clampedDistance * clampedDistance)) - (lenB * lenB)) / ((2f * lenA) * clampedDistance)),
            min: -1f,
            max: 1f
        );
        var angleA = MathF.Acos(x: cosAngleA);

        // The bend axis: perpendicular to both the reach direction and the pole hint, so the elbow/knee leans toward
        // the pole. A pole nearly colinear with dir has no well-defined perpendicular — fall back to any axis
        // perpendicular to dir (world-up, or world-right if dir is itself nearly vertical).
        var poleOffset = (pole - root);
        var bendAxis = Vector3.Cross(vector1: dir, vector2: poleOffset);

        if (bendAxis.LengthSquared() < Epsilon) {
            bendAxis = Vector3.Cross(vector1: dir, vector2: Vector3.UnitY);

            if (bendAxis.LengthSquared() < Epsilon) {
                bendAxis = Vector3.Cross(vector1: dir, vector2: Vector3.UnitX);
            }
        }

        bendAxis = Vector3.Normalize(value: bendAxis);

        var mid = (root + (Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(angle: angleA, axis: bendAxis)) * lenA));
        var midToGoalDir = ((clampedDistance > Epsilon) ? Vector3.Normalize(value: (mid - root)) : dir);
        // The tip completes the second bone FROM MID, aimed back toward the goal direction along the same bend
        // plane (mirroring mid's angle at the root by the exterior angle at mid, law of cosines again).
        var cosAngleB = Math.Clamp(
            value: ((((lenA * lenA) + (lenB * lenB)) - (clampedDistance * clampedDistance)) / ((2f * lenA) * lenB)),
            min: -1f,
            max: 1f
        );
        var angleB = (MathF.PI - MathF.Acos(x: cosAngleB));
        var tip = (mid + (Vector3.Transform(midToGoalDir, Quaternion.CreateFromAxisAngle(angle: -angleB, axis: bendAxis)) * lenB));

        return (mid, tip);
    }

    /// <summary>Solves an N-link spine/tail by a single-pass root→tip drag: each link's direction blends the
    /// PREVIOUS link's direction toward the instantaneous direction to the goal, weighted by that link's stiffness
    /// (0 = keeps the previous direction verbatim — a floppy, lagging tip; 1 = points straight at the goal — a stiff
    /// link). Closed-form per link (no relaxation iteration), so it costs exactly <c>lengths.Length</c> steps.</summary>
    /// <param name="root">The chain's fixed root joint.</param>
    /// <param name="goal">The desired tip position (the last joint does not necessarily reach it — see
    /// <paramref name="stiffness"/>).</param>
    /// <param name="lengths">Each link's bone length, root→tip order.</param>
    /// <param name="stiffness">Each link's blend weight toward the goal direction, root→tip order (same length as
    /// <paramref name="lengths"/>); values are clamped to [0, 1].</param>
    /// <param name="destination">Receives the solved joint positions AFTER the root, root→tip order (length =
    /// <paramref name="lengths"/>.Length) — CALLER-owned scratch, so a repeated solve (a held goal/pole drag) never
    /// allocates a fresh array per call.</param>
    public static void SolveSpine(Vector3 root, Vector3 goal, ReadOnlySpan<float> lengths, ReadOnlySpan<float> stiffness, Span<Vector3> destination) {
        var joint = root;
        var previousDirection = Vector3.UnitY;

        for (var index = 0; (index < lengths.Length); index++) {
            var toGoal = (goal - joint);
            var desiredDirection = ((toGoal.LengthSquared() > Epsilon) ? Vector3.Normalize(value: toGoal) : previousDirection);
            var weight = Math.Clamp(value: ((index < stiffness.Length) ? stiffness[index] : 1f), min: 0f, max: 1f);
            var blended = Vector3.Lerp(amount: weight, value1: previousDirection, value2: desiredDirection);
            var direction = ((blended.LengthSquared() > Epsilon) ? Vector3.Normalize(value: blended) : desiredDirection);

            joint += (direction * MathF.Max(x: lengths[index], y: 0f));
            destination[index] = joint;
            previousDirection = direction;
        }
    }

    /// <summary>Poses one link of a solved chain: the shortest-arc rotation from the link's REST bone direction to
    /// its SOLVED bone direction, composed onto the shape's rest orientation, plus the joint position with the
    /// shape's rest offset rotated the same way. Shared by limb and spine posing (both reduce to "here is the
    /// solved joint and bone direction for this link").</summary>
    /// <param name="joint">The link's solved start joint (its root end).</param>
    /// <param name="solvedDirection">The link's solved bone direction (unit; joint → next joint).</param>
    /// <param name="restBoneDirection">The link's REST bone direction (unit, captured when the chain was defined).</param>
    /// <param name="restOffset">The shape's rest position relative to the joint, in the chain's rest frame.</param>
    /// <param name="restOrientation">The shape's rest orientation.</param>
    /// <returns>The shape's posed position and orientation.</returns>
    public static (Vector3 Position, Quaternion Rotation) PoseChain(Vector3 joint, Vector3 solvedDirection, Vector3 restBoneDirection, Vector3 restOffset, Quaternion restOrientation) {
        var swing = ShortestArc(from: restBoneDirection, to: solvedDirection);
        var rotation = Quaternion.Normalize(value: (swing * restOrientation));
        var position = (joint + Vector3.Transform(rotation: swing, value: restOffset));

        return (position, rotation);
    }

    /// <summary>The shortest-arc (swing-only) rotation that takes unit vector <paramref name="from"/> to unit
    /// vector <paramref name="to"/>. Falls back to identity when the vectors already coincide, and to a stable
    /// 180°-flip axis when they are exactly opposed (no unique shortest arc exists there).</summary>
    /// <param name="from">The starting unit direction.</param>
    /// <param name="to">The target unit direction.</param>
    /// <returns>The unit quaternion rotating <paramref name="from"/> onto <paramref name="to"/>.</returns>
    public static Quaternion ShortestArc(Vector3 from, Vector3 to) {
        var a = ((from.LengthSquared() > Epsilon) ? Vector3.Normalize(value: from) : Vector3.UnitY);
        var b = ((to.LengthSquared() > Epsilon) ? Vector3.Normalize(value: to) : Vector3.UnitY);
        var dot = Vector3.Dot(vector1: a, vector2: b);

        // Opposed vectors: no unique rotation plane — pick any axis perpendicular to a and rotate 180°.
        if (dot < (-1f + Epsilon)) {
            var axis = Vector3.Cross(vector1: Vector3.UnitX, vector2: a);

            if (axis.LengthSquared() < Epsilon) {
                axis = Vector3.Cross(vector1: Vector3.UnitY, vector2: a);
            }

            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(value: axis), MathF.PI);
        }

        var cross = Vector3.Cross(vector1: a, vector2: b);
        // The half-angle construction (w = 1+dot, xyz = cross) avoids an explicit acos/sin pair; normalizing at the
        // end folds in the missing 2x factor from the half-angle identities.
        var quaternion = new Quaternion(w: (1f + dot), x: cross.X, y: cross.Y, z: cross.Z);

        return Quaternion.Normalize(value: quaternion);
    }
}
