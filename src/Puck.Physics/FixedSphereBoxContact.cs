using Puck.Maths;

namespace Puck.Physics;

/// <summary>Shared sphere/oriented-box narrowphase for static and dynamic contact queries.</summary>
internal static class FixedSphereBoxContact {
    /// <summary>Finds the shortest translation that moves a sphere out of a box. The normal points from the box
    /// toward the sphere. An interior center exits through the nearest face, with X/Y/Z tie order.</summary>
    internal static bool TryPush(FixedVector3 sphereCenter, FixedQ4816 radius, FixedVector3 boxCenter,
        FixedQuaternion boxRotation, FixedVector3 halfExtents, out FixedContactPush push) {
        var local = boxRotation.RotateInverse(vector: (sphereCenter - boxCenter));
        var (point, normal, distance) = FixedRoundedBoxContact.Nearest(halfExtents: halfExtents, point: local);

        // An interior center reads a negative distance, its exit gap, so its push is the radius plus that gap.
        if (distance >= radius) {
            push = default;
            return false;
        }
        push = new FixedContactPush(
            Normal: boxRotation.Rotate(vector: normal),
            IsEdge: FixedRoundedBoxContact.IsEdge(delta: (local - point)),
            Penetration: (radius - distance)
        );
        return true;
    }
}
