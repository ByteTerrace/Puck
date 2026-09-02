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
            value: (((upperLength * upperLength) + (reach * reach) - (lowerLength * lowerLength)) / (2f * upperLength * reach))
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
            bend = Perpendicular(direction: direction);
        }

        var normal = Vector3.Normalize(value: bend);

        solvedMid = (root + (direction * (upperLength * cosine)) + (normal * (upperLength * sine)));
        solvedTip = (root + (direction * reach));
    }
    /// <summary>Probes a query field for the surface an effector's tip is reaching toward.</summary>
    /// <param name="field">The static-scene query field, or <see langword="null"/> when none is resolved.</param>
    /// <param name="origin">The tip's world position — where the probe starts.</param>
    /// <param name="towards">The probe direction in the creation's own frame; rotated by
    /// <paramref name="rootRotation"/> so it stays body-relative.</param>
    /// <param name="rootRotation">The body/placement root's world attitude.</param>
    /// <param name="reach">How far to march, world units; a non-positive reach probes nothing.</param>
    /// <param name="standoff">How far off the hit surface, along its own normal, the target sits.</param>
    /// <param name="target">The world-space target; <paramref name="origin"/> when nothing answered.</param>
    /// <returns><see langword="true"/> when the march hit.</returns>
    /// <remarks>The march starts <see cref="ProbeBackoff"/> behind the origin, so a tip already resting on the surface
    /// it probes answers the same way one reaching toward it does.</remarks>
    public static bool TryProbeSurface(SdfFieldEvaluator? field, Vector3 origin, Vector3 towards, Quaternion rootRotation, float reach, float standoff, out Vector3 target) {
        target = origin;

        if (
            (field is null) ||
            (reach <= 0f) ||
            (towards.LengthSquared() <= 0f)
        ) {
            return false;
        }

        var direction = Vector3.Normalize(value: Vector3.Transform(
            rotation: rootRotation,
            value: towards
        ));
        var start = (origin - (direction * ProbeBackoff));

        if (!field.Raycast(
            dir: FixedVector3.FromVector3(value: direction),
            hit: out var hit,
            maxDist: FixedQ4816.FromDouble(value: (reach + ProbeBackoff)),
            origin: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: start))
        )) {
            return false;
        }

        // The hit point is rebuilt from the marched distance rather than read off the returned FixedPosition, so the
        // answer stays in the same float world frame the origin was expressed in whichever cell the march ended in.
        var point = (start + (direction * ((float)((double)hit.Distance))));

        target = point;

        if (standoff <= 0f) {
            return true;
        }

        // The march reports no normal, so the surface's own is the field gradient at the hit — the standoff is along
        // the SURFACE's normal, not back along the probe, so a foot on a slope stands off perpendicular to it.
        if (
            field.TryFieldGradient(
            gradient: out var gradient,
            position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))
        ) &&
            (gradient.ToVector3() is { } normal) &&
            (normal.LengthSquared() > 0f)
        ) {
            target = (point + (Vector3.Normalize(value: normal) * standoff));
        }

        return true;
    }
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
    /// <summary>Returns the shortest rotation carrying one direction onto another.</summary>
    /// <param name="from">The direction to rotate; need not be normalized.</param>
    /// <param name="to">The direction to rotate onto; need not be normalized.</param>
    /// <returns>The rotation, or the identity when either direction is degenerate.</returns>
    public static Quaternion FromTo(Vector3 from, Vector3 to) {
        var fromLength = from.Length();
        var toLength = to.Length();

        if (
            (fromLength < MinLength) ||
            (toLength < MinLength)
        ) {
            return Quaternion.Identity;
        }

        var a = (from / fromLength);
        var b = (to / toLength);
        var cosine = Math.Clamp(
            max: 1f,
            min: -1f,
            value: Vector3.Dot(
                vector1: a,
                vector2: b
            )
        );

        if (cosine < (-1f + MinLength)) {
            // Antiparallel: every axis perpendicular to the pair is a half turn, and no cross product names one.
            return Quaternion.CreateFromAxisAngle(
                angle: MathF.PI,
                axis: Perpendicular(direction: a)
            );
        }

        // The half-way form: (cross(a, b), 1 + dot(a, b)) normalized. It stays exact through the near-parallel case an
        // acos/normalized-axis pair loses to rounding, which is what lets a sweep close the last millimetre instead
        // of stalling once every bone is merely nearly aligned.
        var axis = Vector3.Cross(
            vector1: a,
            vector2: b
        );

        return Quaternion.Normalize(value: new Quaternion(
            w: (1f + cosine),
            x: axis.X,
            y: axis.Y,
            z: axis.Z
        ));
    }
    /// <summary>Returns a unit direction perpendicular to another.</summary>
    /// <param name="direction">The direction to be perpendicular to; assumed non-degenerate.</param>
    /// <returns>The perpendicular direction.</returns>
    public static Vector3 Perpendicular(Vector3 direction) {
        // Cross with whichever axis the direction is least aligned to, so the cross product never collapses.
        var axis = ((MathF.Abs(x: direction.X) < 0.7f)
            ? Vector3.UnitX
            : Vector3.UnitY
        );

        return Vector3.Normalize(value: Vector3.Cross(
            vector1: direction,
            vector2: axis
        ));
    }

    // One bone's turn about a posed pivot: recorded on that bone in its own delta frame, and carried on every
    // descendant joint, descendant parent frame, and the tip.
    private static void Turn(Span<Vector3> posedJoints, Span<Quaternion> parentRotations, Span<Quaternion> corrections, ref Vector3 posedTip, int bone, Vector3 pivot, Quaternion turn) {
        if (turn == Quaternion.Identity) {
            return;
        }

        var parent = parentRotations[bone];

        corrections[bone] = ((Quaternion.Conjugate(value: parent) * turn * parent) * corrections[bone]);

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
}
