using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.World.Client;

/// <summary>Keeps a chase eye and its sightline out of rendered static geometry without changing the authored
/// azimuth or elevation. Obstruction may shorten the camera boom; only explicit look input may steer it.</summary>
public static class WorldCameraClearance {
    private static readonly FixedQ4816 EyeRadius = FixedQ4816.FromDouble(value: 0.3);
    private static readonly FixedQ4816 TargetSkin = FixedQ4816.FromDouble(value: 0.15);
    private static readonly FixedQ4816 ObstructedTargetSkin = FixedQ4816.FromDouble(value: 1.1);
    private static readonly FixedQ4816 RetractionSkin = FixedQ4816.FromDouble(value: 0.05);

    private static bool IsClear(SdfFieldEvaluator field, Vector3 eye, Vector3 target, FixedQ4816 targetSkin) {
        var fixedEye = FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: eye));

        if (field.Overlap(
            center: fixedEye,
            radius: EyeRadius
        )) {
            return false;
        }

        var delta = FixedVector3.FromVector3(value: (target - eye));
        var length = delta.Length;
        var marchLength = (length - targetSkin);

        if (marchLength <= FixedQ4816.Zero) {
            return true;
        }

        return !field.SphereCast(
            origin: fixedEye,
            dir: delta.Normalize(),
            radius: EyeRadius,
            maxDist: marchLength,
            hit: out _
        );
    }

    public static Vector3 Resolve(SdfFieldEvaluator? field, Vector3 desiredEye, Vector3 target) {
        if (field is null) {
            return desiredEye;
        }

        var fixedTarget = FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: target));
        var targetObstructed = field.Overlap(
            center: fixedTarget,
            radius: EyeRadius
        );

        if (
            !targetObstructed &&
            IsClear(
            eye: desiredEye,
            field: field,
            target: target,
            targetSkin: TargetSkin
        )
        ) {
            return desiredEye;
        }

        var offset = FixedVector3.FromVector3(value: (desiredEye - target));
        var boomLength = offset.Length;

        if (boomLength <= FixedQ4816.Zero) {
            return desiredEye;
        }

        // Sweep OUT from the target along the authored boom. A target-local portal frame can surround the pivot;
        // skip only that already-known shell, exactly as the old sightline check did. Crucially, no fallback rotates
        // the boom: collision is allowed to change distance, never yaw or pitch.
        var targetSkin = (targetObstructed
            ? ObstructedTargetSkin
            : TargetSkin
        );

        if (targetSkin >= boomLength) {
            return desiredEye;
        }

        var direction = offset.Normalize();
        var sweepOrigin = (fixedTarget + (direction * targetSkin));

        if (field.Overlap(
            center: sweepOrigin,
            radius: EyeRadius
        )) {
            // There is no same-ray clearance answer outside the ignored target shell. Preserve the authored camera
            // instead of inventing a steering input; the renderer may clip, but the player's orientation cannot jump.
            return desiredEye;
        }

        var sweepLength = (boomLength - targetSkin);

        if (!field.SphereCast(
            dir: direction,
            hit: out var hit,
            maxDist: sweepLength,
            origin: sweepOrigin,
            radius: EyeRadius
        )) {
            return desiredEye;
        }

        var clearTravel = FixedQ4816.Max(
            x: FixedQ4816.Zero,
            y: (hit.Distance - RetractionSkin)
        );
        var retracted = (fixedTarget + (direction * (targetSkin + clearTravel)));

        return retracted.Local.ToVector3();
    }
}
