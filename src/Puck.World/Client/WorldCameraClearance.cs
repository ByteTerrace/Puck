using System.Numerics;
using Puck.Maths;
using Puck.SdfVm.Queries;

namespace Puck.World.Client;

/// <summary>Keeps a chase eye and its sightline out of rendered static geometry while preserving the authored
/// target and orbit radius.</summary>
internal static class WorldCameraClearance {
    private static readonly FixedQ4816 EyeRadius = FixedQ4816.FromDouble(value: 0.3);
    private static readonly FixedQ4816 TargetSkin = FixedQ4816.FromDouble(value: 0.15);
    private static readonly FixedQ4816 ObstructedTargetSkin = FixedQ4816.FromDouble(value: 1.1);

    public static Vector3 Resolve(SdfFieldEvaluator? field, Vector3 desiredEye, Vector3 target) {
        if (field is null) {
            return desiredEye;
        }

        var fixedTarget = FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: target));
        var targetObstructed = field.Overlap(center: fixedTarget, radius: EyeRadius);

        if (!targetObstructed && IsClear(field: field, eye: desiredEye, target: target, targetSkin: TargetSkin)) {
            return desiredEye;
        }

        var offset = (desiredEye - target);
        ReadOnlySpan<float> turns = (targetObstructed
            ? [(MathF.PI / 4f), (-MathF.PI / 4f), (MathF.PI / 2f), (-MathF.PI / 2f), (3f * MathF.PI / 4f), (-3f * MathF.PI / 4f), MathF.PI]
            : [(MathF.PI / 4f), (-MathF.PI / 4f), (MathF.PI / 2f), (-MathF.PI / 2f), (3f * MathF.PI / 4f), (-3f * MathF.PI / 4f), MathF.PI]);

        foreach (var turn in turns) {
            var candidate = (target + Vector3.Transform(value: offset, rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: turn)));

            // A portal frame can legitimately surround the arrival pivot itself. In that case every sightline
            // marched all the way to the pivot reports the same frame as a hit, even though an orbit around it can
            // frame the body cleanly. Ignore only the target-local shell while choosing a new eye; the eye and the
            // rest of the sightline still use the normal body-sized clearance.
            var targetSkin = (targetObstructed ? ObstructedTargetSkin : TargetSkin);

            if (IsClear(field: field, eye: candidate, target: target, targetSkin: targetSkin)) {
                return candidate;
            }
        }

        return desiredEye;
    }

    private static bool IsClear(SdfFieldEvaluator field, Vector3 eye, Vector3 target, FixedQ4816 targetSkin) {
        var fixedEye = FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: eye));

        if (field.Overlap(center: fixedEye, radius: EyeRadius)) {
            return false;
        }

        var delta = FixedVector3.FromVector3(value: (target - eye));
        var length = delta.Length;
        var marchLength = (length - targetSkin);

        if (marchLength <= FixedQ4816.Zero) {
            return true;
        }

        return !field.SphereCast(origin: fixedEye, dir: delta.Normalize(), radius: EyeRadius, maxDist: marchLength, hit: out _);
    }
}
