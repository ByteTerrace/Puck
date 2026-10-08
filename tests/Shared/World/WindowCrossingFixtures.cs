using System.Numerics;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World.Testing;

/// <summary>The face geometry a window crossing passes through.</summary>
internal static class WindowCrossingFixtures {
    // The (Right, Up, Normal) triad WorldFaceCatalog derives for an unrotated face at an authored yaw.
    internal static WorldFaceGeometry Face(Vector3 origin, float yawDegrees) {
        var yaw = (yawDegrees * (MathF.PI / 180f));

        return new WorldFaceGeometry(
            Origin: origin,
            Right: new Vector3(
                x: MathF.Cos(x: yaw),
                y: 0f,
                z: -MathF.Sin(x: yaw)
            ),
            Up: Vector3.UnitY,
            Normal: new Vector3(
                x: MathF.Sin(x: yaw),
                y: 0f,
                z: MathF.Cos(x: yaw)
            ),
            HalfWidth: 1.5f,
            HalfHeight: 1.5f
        );
    }
    internal static WorldFaceFrame Frame(WorldFaceGeometry face) => new(
        Origin: FixedVector3.FromVector3(value: face.Origin),
        Right: FixedVector3.FromVector3(value: face.Right),
        Up: FixedVector3.FromVector3(value: face.Up),
        Normal: FixedVector3.FromVector3(value: face.Normal),
        HalfWidth: FixedQ4816.FromDouble(value: face.HalfWidth),
        HalfHeight: FixedQ4816.FromDouble(value: face.HalfHeight),
        HalfDepth: FixedQ4816.Zero
    );
}
