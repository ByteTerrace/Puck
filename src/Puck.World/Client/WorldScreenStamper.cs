using System.Numerics;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>Turns one authored or derived <see cref="WorldScreen"/> row into the sampled slab geometry shared by
/// the boot scene and traveler-follow scenes.</summary>
internal static class WorldScreenStamper {
    /// <summary>Emits one sampled screen slab at its authored world-space frame.</summary>
    public static void Emit(SdfProgramBuilder builder, WorldScreen screen) {
        ArgumentNullException.ThrowIfNull(argument: builder);
        ArgumentNullException.ThrowIfNull(argument: screen);

        var normal = Vector3.Normalize(value: Vector3.Cross(vector1: screen.Right, vector2: screen.Up));
        var center = (screen.Origin - (normal * screen.HalfDepth));

        _ = builder
            .Translate(offset: center)
            .ScreenSlab(
                halfExtents: new Vector3(x: screen.HalfWidth, y: screen.HalfHeight, z: screen.HalfDepth),
                round: screen.Round,
                worldOrigin: screen.Origin,
                worldRight: screen.Right,
                worldUp: screen.Up,
                screenIndex: screen.Index
            )
            .ResetPoint();
    }
}
