using Puck.Maths;

namespace Puck.Physics;

/// <summary>Shared sphere/oriented-box narrowphase for static and dynamic contact queries.</summary>
internal static class FixedSphereBoxContact {
    /// <summary>Finds the shortest translation that moves a sphere out of a box. The normal points from the box
    /// toward the sphere. An interior center exits through the nearest face, with X/Y/Z tie order.</summary>
    internal static bool TryPush(FixedVector3 sphereCenter, FixedQ4816 radius, FixedVector3 boxCenter,
        FixedQuaternion boxRotation, FixedVector3 halfExtents, out FixedContactPush push) {
        var local = boxRotation.RotateInverse(vector: (sphereCenter - boxCenter));
        var closest = FixedVector3.Clamp(maximum: halfExtents, minimum: -halfExtents, value: local);
        var delta = (local - closest);

        if (delta == FixedVector3.Zero) {
            var (normal, _, gap) = FixedAxisMath.BoxInteriorExit(halfExtents: halfExtents, local: local);
            push = new FixedContactPush(
                Normal: boxRotation.Rotate(vector: normal),
                Penetration: (radius + gap)
            );
            return (push.Penetration > FixedQ4816.Zero);
        }

        // Length uses the full-width raw square sum; a sub-millimetre distance must not round to zero before sqrt.
        var distance = delta.Length;

        if (distance >= radius) {
            push = default;
            return false;
        }

        push = new FixedContactPush(
            Normal: boxRotation.Rotate(vector: (delta / distance)),
            Penetration: (radius - distance)
        );
        return true;
    }
}
