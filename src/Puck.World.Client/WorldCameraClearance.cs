using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.World.Client;

/// <summary>
/// Places a chase eye where it sees its subject from outside: out of rendered static geometry, out of the framed body
/// itself, and with a clear sightline to the target. Obstruction first shortens the boom along its own direction. When no
/// length of that boom clears both the geometry and the body, as with the body backed into a wall or a corner, the boom
/// rises toward the seat's up until one does. The boom's yaw never changes, so the frame the player steers in stays the
/// frame the player sees. Presentation-only: nothing here feeds movement.
/// </summary>
public static class WorldCameraClearance {
    /// <summary>The radius, in world units, of the sphere the eye keeps clear of geometry and of the framed body.</summary>
    public const float EyeRadius = 0.3f;
    /// <summary>The steepest elevation a crowded boom rises to, in radians above the seat's level plane: short of
    /// vertical, where an eye straight over its target leaves the look-at basis no defined right axis.</summary>
    public const float LiftCeiling = 1.4f;

    // A crowded boom's rise is scanned in this many even steps up to the ceiling, then bisected this many times between
    // the last blocked step and the first clear one, so the rise follows the obstruction by a fine angle rather than
    // jumping a whole step.
    private const int LiftRefinements = 4;
    private const int LiftSteps = 6;
    // A boom shorter than this has no direction to clear along.
    private const float MinimumBoom = 1e-4f;

    private static readonly FixedQ4816 FixedEyeRadius = FixedQ4816.FromDouble(value: EyeRadius);
    // A target inside geometry is inside a shell around the pivot, such as a portal frame a body walks through; the sweep
    // starts this far out along the boom, past that shell.
    private static readonly FixedQ4816 ObstructedTargetSkin = FixedQ4816.FromDouble(value: 1.1);
    private static readonly FixedQ4816 RetractionSkin = FixedQ4816.FromDouble(value: 0.05);

    // How far along the boom the eye can travel from the target before geometry stops it or its sightline, up to the
    // boom's whole length.
    private static float Clear(SdfFieldEvaluator field, bool targetObstructed, Vector3 target, Vector3 direction, float length) {
        var fixedLength = FixedQ4816.FromDouble(value: length);
        var skin = (targetObstructed
            ? ObstructedTargetSkin
            : FixedQ4816.Zero
        );

        if (skin >= fixedLength) {
            return length;
        }

        var fixedDirection = FixedVector3.FromVector3(value: direction).Normalize();
        var origin = (FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: target)) + (fixedDirection * skin));

        if (
            targetObstructed &&
            field.Overlap(
                center: origin,
                radius: FixedEyeRadius
            )
        ) {
            // The shell reaches past the skin, so this ray has no clearance answer; the boom stands as authored.
            return length;
        }

        if (!field.SphereCast(
            dir: fixedDirection,
            hit: out var hit,
            maxDist: (fixedLength - skin),
            origin: origin,
            radius: FixedEyeRadius
        )) {
            return length;
        }

        return ((float)((double)(skin + FixedQ4816.Max(
            x: FixedQ4816.Zero,
            y: (hit.Distance - RetractionSkin)
        ))));
    }
    // The signed distance from the eye to the nearest geometry, or negative infinity where the field cannot say.
    private static float Distance(SdfFieldEvaluator field, Vector3 eye) => (field.TryDistance(
        distance: out var distance,
        material: out _,
        position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: eye))
    )
        ? ((float)distance)
        : float.NegativeInfinity
    );
    private static Vector3 Lifted(Vector3 level, Vector3 up, float elevation) =>
        ((level * MathF.Cos(x: elevation)) + (up * MathF.Sin(x: elevation)));
    // Places the eye on one boom direction: as far out as geometry allows when that is past the body, which succeeds;
    // otherwise just past the body, which fails and is a fallback candidate.
    private static bool TryPlace(SdfFieldEvaluator field, bool targetObstructed, Vector3 target, in WorldChaseBody subject, Vector3 direction, float length, out Vector3 eye, out bool whole) {
        var keepOut = subject.Exit(
            direction: direction,
            origin: target,
            radius: EyeRadius
        );
        var clear = Clear(
            direction: direction,
            field: field,
            length: length,
            target: target,
            targetObstructed: targetObstructed
        );

        whole = (clear >= length);

        if (clear >= keepOut) {
            eye = (target + (direction * clear));

            return true;
        }

        eye = (target + (direction * keepOut));

        return false;
    }

    /// <summary>Returns where a chase eye stands this frame.</summary>
    /// <param name="field">The static geometry the eye keeps out of, or <see langword="null"/> for none, which keeps the
    /// eye out of the framed body alone.</param>
    /// <param name="desiredEye">The eye the rig resolved, in world units.</param>
    /// <param name="target">The point the eye looks at, in world units; the boom runs from here to
    /// <paramref name="desiredEye"/>.</param>
    /// <param name="subject">The body the eye frames and keeps out of.</param>
    /// <param name="up">The seat's up axis, which a crowded boom rises toward; zero counts as world up.</param>
    /// <returns><paramref name="desiredEye"/> itself when nothing obstructs it and it is outside the body; otherwise the
    /// shortened or risen eye. Where no rise up to <see cref="LiftCeiling"/> clears both, the eye stands just outside
    /// the body on whichever tried direction leaves it farthest from geometry.</returns>
    public static Vector3 Resolve(SdfFieldEvaluator? field, Vector3 desiredEye, Vector3 target, in WorldChaseBody subject, Vector3 up) {
        var offset = (desiredEye - target);
        var length = offset.Length();

        if (!(length > MinimumBoom)) {
            return desiredEye;
        }

        var direction = (offset / length);

        if (field is null) {
            var keepOut = subject.Exit(
                direction: direction,
                origin: target,
                radius: EyeRadius
            );

            return ((length >= keepOut)
                ? desiredEye
                : (target + (direction * keepOut))
            );
        }

        var targetObstructed = field.Overlap(
            center: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: target)),
            radius: FixedEyeRadius
        );

        if (TryPlace(
            direction: direction,
            eye: out var eye,
            field: field,
            length: length,
            subject: in subject,
            target: target,
            targetObstructed: targetObstructed,
            whole: out var whole
        )) {
            return (whole
                ? desiredEye
                : eye
            );
        }

        var best = eye;
        var bestDistance = Distance(
            eye: eye,
            field: field
        );
        var upAxis = ((up.LengthSquared() > 0f)
            ? Vector3.Normalize(value: up)
            : Vector3.UnitY
        );
        var rise = Vector3.Dot(
            vector1: direction,
            vector2: upAxis
        );
        var level = (direction - (upAxis * rise));
        var levelLength = level.Length();
        var elevation = MathF.Asin(x: Math.Clamp(
            max: 1f,
            min: -1f,
            value: rise
        ));

        if (
            (levelLength <= MinimumBoom) ||
            (elevation >= LiftCeiling)
        ) {
            return best;
        }

        level /= levelLength;

        var below = elevation;

        for (var step = 1; (step <= LiftSteps); step++) {
            var candidate = (elevation + (((LiftCeiling - elevation) * step) / LiftSteps));

            if (TryPlace(
                direction: Lifted(
                    elevation: candidate,
                    level: level,
                    up: upAxis
                ),
                eye: out var lifted,
                field: field,
                length: length,
                subject: in subject,
                target: target,
                targetObstructed: targetObstructed,
                whole: out _
            )) {
                for (var refinement = 0; (refinement < LiftRefinements); refinement++) {
                    var middle = ((below + candidate) * 0.5f);

                    if (TryPlace(
                        direction: Lifted(
                            elevation: middle,
                            level: level,
                            up: upAxis
                        ),
                        eye: out var refined,
                        field: field,
                        length: length,
                        subject: in subject,
                        target: target,
                        targetObstructed: targetObstructed,
                        whole: out _
                    )) {
                        candidate = middle;
                        lifted = refined;
                    } else {
                        below = middle;
                    }
                }

                return lifted;
            }

            below = candidate;

            var liftedDistance = Distance(
                eye: lifted,
                field: field
            );

            if (liftedDistance > bestDistance) {
                best = lifted;
                bestDistance = liftedDistance;
            }
        }

        return best;
    }
}
