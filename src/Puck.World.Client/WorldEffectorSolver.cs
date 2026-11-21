using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.World.Client;

/// <summary>
/// The inverse-kinematics solve a <c>Puck.World.Authoring.CreationEffectorDocument</c> asks for: bend a chain of
/// already-posed bones so its tip reaches a target, and report each bone's correction in the frame that bone's own
/// animation delta lives in.
/// </summary>
/// <remarks>
/// Every position here is creation space after the drivers and the parent chain have composed — the pose the author's
/// swings and slides produced, which is what the solve corrects. Two bones close analytically; three or more sweep by
/// cyclic coordinate descent.
/// <para>A correction is returned as a rotation about the bone's REST joint, expressed in the bone's own delta frame
/// (<c>conj(parent) · turn · parent</c>), so the caller folds it into that bone's own delta and the ordinary parent
/// chaining carries it to every descendant. That is the one form under which an ancestor corrected later in the same
/// sweep carries an already-corrected descendant instead of overwriting it.</para>
/// <para>Presentation-only float: nothing here reaches simulation state.</para>
/// </remarks>
public static class WorldEffectorSolver {
    /// <summary>The shortest bone, target offset, or bend lever the solve treats as a direction, world units. Below it
    /// there is no axis to rotate about and the configuration is left as the drivers posed it.</summary>
    public const float MinLength = 1e-5f;
    /// <summary>How far back along the probe direction <see cref="TryProbeSurface"/> starts its march, world units. A
    /// tip resting on the surface it probes would otherwise march from inside the geometry.</summary>
    public const float ProbeBackoff = 0.05f;
    /// <summary>The tip-to-target distance a sweep stops at, world units — a hundredth of a millimetre, far inside a
    /// pixel at any camera distance a limb is visible from.</summary>
    public const float ReachedTolerance = 1e-5f;

    // One bone's turn about a posed pivot: recorded on that bone in its own delta frame, and carried on every
    // descendant joint, descendant parent frame, and the tip.
    private static void Turn(Span<Vector3> posedJoints, Span<Quaternion> parentRotations, Span<Quaternion> corrections, ref Vector3 posedTip, int bone, Vector3 pivot, Quaternion turn) {
        if (turn == Quaternion.Identity) {
            return;
        }

        var parent = parentRotations[bone];

        corrections[bone] = (((Quaternion.Conjugate(value: parent) * turn) * parent) * corrections[bone]);

        for (var index = (bone + 1); (index < posedJoints.Length); index++) {
            posedJoints[index] = (pivot + Vector3.Transform(
                rotation: turn,
                value: (posedJoints[index] - pivot)
            ));
            parentRotations[index] = (turn * parentRotations[index]);
        }

        posedTip = (pivot + Vector3.Transform(
            rotation: turn,
            value: (posedTip - pivot)
        ));
    }

    /// <summary>Returns the shortest rotation carrying one direction onto another.</summary>
    /// <param name="from">The direction to rotate; need not be normalized.</param>
    /// <param name="to">The direction to rotate onto; need not be normalized.</param>
    /// <returns><see cref="VectorFunctions.ShortestArc"/> of the pair, or the identity when either direction is
    /// shorter than <see cref="MinLength"/>, whose direction is rounding noise.</returns>
    public static Quaternion FromTo(Vector3 from, Vector3 to) => (((from.Length() < MinLength) || (to.Length() < MinLength))
        ? Quaternion.Identity
        : VectorFunctions.ShortestArc(
            from: from,
            to: to
        )
    );
    /// <summary>Returns whether a driver phase falls inside a plant window.</summary>
    /// <param name="phase">The driver's wrapped phase, radians in [0, 2π).</param>
    /// <param name="from">The window's opening phase, radians.</param>
    /// <param name="to">The window's closing phase, radians.</param>
    /// <returns><see langword="true"/> while the phase is inside. A window whose <paramref name="from"/> exceeds its
    /// <paramref name="to"/> names the interval through the phase origin rather than an empty one.</returns>
    public static bool InWindow(float phase, float from, float to) => ((from <= to)
        ? ((phase >= from) && (phase <= to))
        : ((phase >= from) || (phase <= to))
    );
    /// <summary>Solves a chain in place and accumulates each bone's correction.</summary>
    /// <param name="posedJoints">Each bone's joint, root→tip, in posed creation space; advanced to the solved
    /// configuration.</param>
    /// <param name="parentRotations">Each bone's parent-chain rotation (identity for a bone hanging off the creation
    /// root); advanced as ancestors turn, so a later bone's correction converts against the frame it will actually
    /// ride.</param>
    /// <param name="corrections">Each bone's accumulated correction, a rotation about that bone's rest joint in the
    /// bone's own delta frame. Pass identities in; the solve pre-multiplies onto them.</param>
    /// <param name="posedTip">The tip's posed position; advanced to the solved position.</param>
    /// <param name="target">Where the tip is asked to be, posed creation space.</param>
    /// <param name="iterations">The cyclic-coordinate-descent sweep count (ignored by the two-bone solve).</param>
    public static void Solve(Span<Vector3> posedJoints, Span<Quaternion> parentRotations, Span<Quaternion> corrections, ref Vector3 posedTip, Vector3 target, int iterations) {
        var count = posedJoints.Length;

        if (count < 2) {
            return;
        }
        if (count == 2) {
            SolveTwoBone(
                mid: posedJoints[1],
                root: posedJoints[0],
                solvedMid: out var solvedMid,
                solvedTip: out var solvedTip,
                target: target,
                tip: posedTip
            );
            // Root first: the turn that carries the mid joint onto its solved place also carries the tip, so the
            // second turn is measured against the already-carried tip rather than the original one.
            Turn(
                bone: 0,
                corrections: corrections,
                parentRotations: parentRotations,
                pivot: posedJoints[0],
                posedJoints: posedJoints,
                posedTip: ref posedTip,
                turn: FromTo(
                    from: (posedJoints[1] - posedJoints[0]),
                    to: (solvedMid - posedJoints[0])
                )
            );
            Turn(
                bone: 1,
                corrections: corrections,
                parentRotations: parentRotations,
                pivot: posedJoints[1],
                posedJoints: posedJoints,
                posedTip: ref posedTip,
                turn: FromTo(
                    from: (posedTip - posedJoints[1]),
                    to: (solvedTip - posedJoints[1])
                )
            );

            return;
        }

        var tolerance = (ReachedTolerance * ReachedTolerance);

        for (var sweep = 0; (sweep < iterations); sweep++) {
            if ((posedTip - target).LengthSquared() <= tolerance) {
                return;
            }

            // Tip-most bone first: each turn points the whole remaining sub-chain at the target, and the ancestors
            // that turn afterwards carry those turns with them.
            for (var bone = (count - 1); (bone >= 0); bone--) {
                var pivot = posedJoints[bone];

                Turn(
                    bone: bone,
                    corrections: corrections,
                    parentRotations: parentRotations,
                    pivot: pivot,
                    posedJoints: posedJoints,
                    posedTip: ref posedTip,
                    turn: FromTo(
                        from: (posedTip - pivot),
                        to: (target - pivot)
                    )
                );
            }
        }
    }
    /// <summary>Solves the two-bone case in closed form: the bend stays in the plane the driver-posed limb already
    /// bends in, so the authored pose chooses which way an elbow or a knee folds.</summary>
    /// <param name="root">The root joint.</param>
    /// <param name="mid">The middle joint, as the drivers posed it.</param>
    /// <param name="tip">The tip, as the drivers posed it.</param>
    /// <param name="target">Where the tip is asked to be.</param>
    /// <param name="solvedMid">The middle joint of the solved configuration.</param>
    /// <param name="solvedTip">The tip of the solved configuration — the target when it is within reach, and the
    /// fully extended point along the root-to-target direction when it is not.</param>
    public static void SolveTwoBone(Vector3 root, Vector3 mid, Vector3 tip, Vector3 target, out Vector3 solvedMid, out Vector3 solvedTip) {
        solvedMid = mid;
        solvedTip = tip;

        var upper = (mid - root);
        var lower = (tip - mid);
        var upperLength = upper.Length();
        var lowerLength = lower.Length();
        var toTarget = (target - root);
        var distance = toTarget.Length();

        if (
            (upperLength < MinLength) ||
            (lowerLength < MinLength) ||
            (distance < MinLength)
        ) {
            return;
        }

        var direction = (toTarget / distance);
        // Clamped to the annulus the two bones can reach: beyond the sum the limb extends straight at the target,
        // and inside the difference it folds as far as it folds. MinLength keeps the folded case off the degenerate
        // radius where the bend plane vanishes.
        var reach = Math.Clamp(
            max: (upperLength + lowerLength),
            min: (MathF.Abs(x: (upperLength - lowerLength)) + MinLength),
            value: distance
        );
        var cosine = Math.Clamp(
            max: 1f,
            min: -1f,
            value: ((((upperLength * upperLength) + (reach * reach)) - (lowerLength * lowerLength)) / ((2f * upperLength) * reach))
        );
        var sine = MathF.Sqrt(x: MathF.Max(
            x: 0f,
            y: (1f - (cosine * cosine))
        ));
        // The driver-posed mid joint's offset from the new root-to-target line IS the bend direction: keeping it is
        // what makes the solve a correction to the authored pose rather than a pose of its own.
        var bend = (upper - (direction * Vector3.Dot(
            vector1: upper,
            vector2: direction
        )));

        if (bend.LengthSquared() < (MinLength * MinLength)) {
            bend = VectorFunctions.Perpendicular(direction: direction);
        }

        var normal = Vector3.Normalize(value: bend);

        solvedMid = ((root + (direction * (upperLength * cosine))) + (normal * (upperLength * sine)));
        solvedTip = (root + (direction * reach));
    }
    /// <summary>Probes a query field for the surface an effector's tip is reaching toward.</summary>
    /// <param name="answer">The effector's held probe answer (<see cref="WorldSurfaceQuery"/>): reused for
    /// bit-identical inputs over the same field, and replaced by every probe that marches.</param>
    /// <param name="field">The static-scene query field, or <see langword="null"/> when none is resolved.</param>
    /// <param name="origin">The tip's world position — where the probe starts.</param>
    /// <param name="towards">The probe direction in the creation's own frame; rotated by
    /// <paramref name="rootRotation"/> so it stays body-relative.</param>
    /// <param name="rootRotation">The body/placement root's world attitude.</param>
    /// <param name="reach">How far to march, world units; a non-positive reach probes nothing.</param>
    /// <param name="standoff">How far off the hit surface, along its own normal, the target sits.</param>
    /// <param name="target">The world-space target; <paramref name="origin"/> when nothing answered.</param>
    /// <param name="normal">The hit surface's unit world normal, the field gradient at the hit: back along the probe
    /// where the march missed or the field reports no gradient, and world up when the request marches nothing (no
    /// field, reach or direction).</param>
    /// <returns><see langword="true"/> when the march hit.</returns>
    /// <remarks>The march starts <see cref="ProbeBackoff"/> behind the origin, so a tip already resting on the surface
    /// it probes answers the same way one reaching toward it does.</remarks>
    public static bool TryProbeSurface(ref WorldFieldAnswer<WorldSurfaceQuery, WorldSurfaceAnswer> answer, SdfFieldEvaluator? field, Vector3 origin, Vector3 towards, Quaternion rootRotation, float reach, float standoff, out Vector3 target, out Vector3 normal) {
        if ((field is null) || (reach <= 0f) || (towards.LengthSquared() <= 0f)) {
            WorldFieldQueryWork.Surface.Skip();
            target = origin;
            normal = Vector3.UnitY;
            return false;
        }

        var answered = answer.Ask(asked: new WorldSurfaceQuery(Field: field, Origin: origin, Reach: reach, Rotation: rootRotation, Standoff: standoff, Towards: towards, Normal: true));

        target = answered.Target;
        normal = answered.Normal;

        return answered.Hit;
    }
    /// <summary>Probes a query field for the surface point under a resting effector's latched stance, only to decide
    /// whether that stance has drifted past its replant distance: the march is reused while the decision provably
    /// stands (<see cref="WorldSurfaceQuery.Covers"/>), and no surface normal is computed.</summary>
    /// <param name="answer">The effector's held probe answer, shared with <see cref="TryProbeSurface"/>.</param>
    /// <param name="field">The static-scene query field, or <see langword="null"/> when none is resolved.</param>
    /// <param name="origin">The tip's world position — where the probe starts.</param>
    /// <param name="towards">The probe direction in the creation's own frame.</param>
    /// <param name="rootRotation">The body/placement root's world attitude.</param>
    /// <param name="reach">How far to march, world units; a non-positive reach probes nothing.</param>
    /// <param name="latch">The latched stance's surface point, world units.</param>
    /// <param name="distance">The replant distance, world units: the drift a stance holds through.</param>
    /// <returns><see langword="true"/> when the probe hit a surface point farther than <paramref name="distance"/>
    /// from <paramref name="latch"/>.</returns>
    public static bool StanceDrifted(ref WorldFieldAnswer<WorldSurfaceQuery, WorldSurfaceAnswer> answer, SdfFieldEvaluator? field, Vector3 origin, Vector3 towards, Quaternion rootRotation, float reach, Vector3 latch, float distance) {
        if ((field is null) || (reach <= 0f) || (towards.LengthSquared() <= 0f)) {
            WorldFieldQueryWork.Surface.Skip();
            return false;
        }

        var answered = answer.Ask(asked: new WorldSurfaceQuery(Field: field, Origin: origin, Reach: reach, Rotation: rootRotation, Standoff: 0f, Towards: towards,
            Latch: latch, Slack: MathF.Max(x: 0f, y: distance)));

        return (answered.Hit && (Vector3.Distance(value1: latch, value2: answered.Point) > distance));
    }
}
/// <summary>The answer to a <see cref="WorldSurfaceQuery"/>.</summary>
/// <param name="Hit">Whether the march hit.</param>
/// <param name="Target">The world-space target off the hit, or the probe's origin when nothing answered. Meaningful only
/// where <paramref name="HasNormal"/> holds, since the standoff is taken along the normal.</param>
/// <param name="Normal">The hit surface's unit world normal; back along the probe where the march missed or the field
/// reports no gradient. Meaningful only where <paramref name="HasNormal"/> holds.</param>
/// <param name="Point">The surface point the march hit, or the probe's origin when it missed.</param>
/// <param name="HasNormal">Whether the march took the field gradient: <see cref="Normal"/> and <see cref="Target"/>
/// are the hit's own.</param>
public readonly record struct WorldSurfaceAnswer(bool Hit, Vector3 Target, Vector3 Normal, Vector3 Point, bool HasNormal);
/// <summary>
/// An effector's surface probe (<see cref="WorldEffectorSolver.TryProbeSurface"/>): a ray marched from behind the tip
/// along a body-relative direction, its hit lifted off the field gradient by a standoff.
/// </summary>
/// <remarks>
/// <para>A ray's hit is not continuous in its origin, so no drift bound holds for a target: an answer that names a
/// target is reused only for bit-identical inputs over the same field instance. The presentation field is immutable
/// and replaced whole when the static scene changes (<see cref="WorldClient.StaticField"/>), so the instance is the
/// field's change revision.</para>
/// <para>A stance question (<see cref="Slack"/> positive, <see cref="WorldEffectorSolver.StanceDrifted"/>) asks only
/// whether the hit lies farther than <see cref="Slack"/> from <see cref="Latch"/>. Over a smooth surface no steeper
/// than <see cref="StanceSlope"/> allows, the hit moves at most <see cref="StanceDriftFactor"/> times the probe ray's
/// drift, so a held hit within the slack by that margin answers the question as a fresh march would. The latched foot
/// draws its latch, never the held hit, so where a step edge breaks that bound the reuse only delays a replant by a
/// drift of at most <see cref="Slack"/> over <see cref="StanceDriftFactor"/>; it never floats or sinks a foot.</para>
/// </remarks>
/// <param name="Field">The static-scene query field.</param>
/// <param name="Origin">The tip's world position, where the probe starts.</param>
/// <param name="Towards">The probe direction in the creation's own frame; nonzero.</param>
/// <param name="Rotation">The body/placement root's world attitude.</param>
/// <param name="Reach">How far to march, world units; positive.</param>
/// <param name="Standoff">How far off the hit surface, along its own normal, the target sits.</param>
/// <param name="Normal">Whether the asker reads the normal and the target, which the field gradient at the hit gives.</param>
/// <param name="Latch">A stance question's latched surface point, world units.</param>
/// <param name="Slack">A stance question's replant distance, world units; zero for a question reused only on
/// bit-identical inputs.</param>
public readonly record struct WorldSurfaceQuery(SdfFieldEvaluator Field, Vector3 Origin, Vector3 Towards, Quaternion Rotation, float Reach, float Standoff,
    bool Normal = false, Vector3 Latch = default, float Slack = 0f) : IWorldFieldQuery<WorldSurfaceQuery, WorldSurfaceAnswer> {
    /// <summary>How much farther than the probe ray a stance question's hit may move: the hit on a plane at angle
    /// θ from the ray's normal moves 1/cos θ times the ray, so 2 covers a surface up to <see cref="StanceSlope"/>.</summary>
    public const float StanceDriftFactor = 2f;
    /// <summary>The steepest surface, degrees from facing the probe, over which <see cref="StanceDriftFactor"/>
    /// bounds a stance question's hit.</summary>
    public const float StanceSlope = 60f;

    /// <inheritdoc/>
    public static WorldFieldQueryFamily Family => WorldFieldQueryWork.Surface;

    /// <inheritdoc/>
    public bool Covers(in WorldSurfaceQuery asked, in WorldSurfaceAnswer held) {
        if (
            (asked.Normal && !held.HasNormal) ||
            !Bits(value: Towards.X, other: asked.Towards.X) || !Bits(value: Towards.Y, other: asked.Towards.Y) || !Bits(value: Towards.Z, other: asked.Towards.Z) ||
            !Bits(value: Reach, other: asked.Reach)
        ) {
            return false;
        }
        if (
            Bits(value: Origin.X, other: asked.Origin.X) && Bits(value: Origin.Y, other: asked.Origin.Y) && Bits(value: Origin.Z, other: asked.Origin.Z) &&
            Bits(value: Rotation.X, other: asked.Rotation.X) && Bits(value: Rotation.Y, other: asked.Rotation.Y) &&
            Bits(value: Rotation.Z, other: asked.Rotation.Z) && Bits(value: Rotation.W, other: asked.Rotation.W) &&
            (!asked.Normal || Bits(value: Standoff, other: asked.Standoff))
        ) {
            return true;
        }
        if (!(asked.Slack > 0f) || !held.Hit) {
            return false;
        }

        // Every point of the probe ray moves by at most |dOrigin| + length * |dDirection| between the two questions.
        // Both marches start from origins rounded to the field's Q48.16 grid, which the float drift does not see: a few
        // grid steps along the origin and along the ray are charged too.
        var length = (Reach + WorldEffectorSolver.ProbeBackoff);
        var drift = (Vector3.Distance(value1: Origin, value2: asked.Origin) + (length * Vector3.Distance(value1: Direction(), value2: asked.Direction())) +
            ((4f + (8f * length)) / 65536f));

        return ((Vector3.Distance(value1: asked.Latch, value2: held.Point) + (StanceDriftFactor * drift)) <= asked.Slack);
    }
    /// <inheritdoc/>
    public WorldSurfaceAnswer March() {
        var direction = Direction();
        var start = (Origin - (direction * WorldEffectorSolver.ProbeBackoff));

        if (!Field.Raycast(
            dir: FixedVector3.FromVector3(value: direction),
            hit: out var hit,
            maxDist: FixedQ4816.FromDouble(value: (Reach + WorldEffectorSolver.ProbeBackoff)),
            origin: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: start))
        )) {
            return new WorldSurfaceAnswer(Hit: false, Normal: -direction, Point: Origin, Target: Origin, HasNormal: Normal);
        }

        // The hit point is rebuilt from the marched distance rather than read off the returned FixedPosition, so the
        // answer stays in the same float world frame the origin was expressed in whichever cell the march ended in.
        var point = (start + (direction * ((float)hit.Distance)));
        var normal = -direction;

        if (!Normal) {
            return new WorldSurfaceAnswer(Hit: true, Normal: normal, Point: point, Target: point, HasNormal: false);
        }

        // The march reports no normal, so the surface's own is the field gradient at the hit: the standoff is along the
        // surface's normal, not back along the probe, so a foot on a slope stands off perpendicular to it.
        if (
            Field.TryFieldGradient(
                gradient: out var gradient,
                position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))
            ) &&
            (gradient.ToVector3() is { } surface) &&
            (surface.LengthSquared() > 0f)
        ) {
            normal = Vector3.Normalize(value: surface);
        }

        return new WorldSurfaceAnswer(Hit: true, Normal: normal, Point: point, HasNormal: true, Target: (point + (normal * MathF.Max(
            x: 0f,
            y: Standoff
        ))));
    }

    // The probe's unit world direction.
    private Vector3 Direction() => Vector3.Normalize(value: Vector3.Transform(
        rotation: Rotation,
        value: Towards
    ));
    private static bool Bits(float value, float other) => (BitConverter.SingleToInt32Bits(value: value) == BitConverter.SingleToInt32Bits(value: other));
}
