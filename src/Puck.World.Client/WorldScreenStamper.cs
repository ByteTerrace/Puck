using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Turns one authored or derived <see cref="WorldScreen"/> row into the sampled slab geometry shared by
/// every continuum-composed scene.</summary>
public static class WorldScreenStamper {
    /// <summary>Emits one sampled screen slab at its authored world-space frame.</summary>
    public static void Emit(SdfProgramBuilder builder, WorldScreen screen) {
        ArgumentNullException.ThrowIfNull(argument: builder);
        ArgumentNullException.ThrowIfNull(argument: screen);

        var unitRight = Vector3.Normalize(value: screen.Right);
        var unitUp = Vector3.Normalize(value: screen.Up);
        var normal = Vector3.Normalize(value: Vector3.Cross(
            vector1: unitRight,
            vector2: unitUp
        ));
        var center = (screen.Origin - (normal * screen.HalfDepth));
        // Local X/Y/Z = Right/Up/normal — the same right/up/forward-to-matrix frame Text() builds, so the slab's
        // emitted geometry is rotated to match the UV frame ScreenSlab's world-orientation overload stores for it.
        var orientation = Quaternion.CreateFromRotationMatrix(matrix: new Matrix4x4(
            m11: unitRight.X,
            m12: unitRight.Y,
            m13: unitRight.Z,
            m14: 0f,
            m21: unitUp.X,
            m22: unitUp.Y,
            m23: unitUp.Z,
            m24: 0f,
            m31: normal.X,
            m32: normal.Y,
            m33: normal.Z,
            m34: 0f,
            m41: 0f,
            m42: 0f,
            m43: 0f,
            m44: 1f
        ));

        var halfExtents = new Vector3(
            x: screen.HalfWidth,
            y: screen.HalfHeight,
            z: screen.HalfDepth
        );

        // The slab is a static instance of its own, so the mesh bake gives it a group sized to the slab and refined to
        // its thickness. Outside any instance it bakes in the world group, whose one lattice spans the far distance
        // about the origin: too coarse to hold a thin slab or keep a body behind it off its face.
        // The chain starts at the world point itself: it follows whatever the composition emitted before it, and an
        // instance before it may have left its pose on the point (a stamp-pool slot's TransformDynamic), which a segment
        // without its own ResetPoint would inherit.
        _ = builder
            .BeginInstance(
                boundCenter: center,
                boundRadius: (halfExtents.Length() + MathF.Max(0f, screen.Round)),
                detail: MathF.Min(MathF.Min(screen.HalfWidth, screen.HalfHeight), screen.HalfDepth)
            )
            .ResetPoint()
            .Translate(offset: center)
            .Rotate(rotation: orientation)
            .ScreenSlab(
            halfExtents: halfExtents,
            round: screen.Round,
            worldOrigin: screen.Origin,
            worldOrientation: orientation,
            screenIndex: screen.Index
        )
            .ResetPoint()
            .EndInstance();
    }
}
