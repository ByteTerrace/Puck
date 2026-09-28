namespace Puck.SignedDistance;

/// <summary>A coordinate plane of an instruction's local frame, named by the two axes it spans (the third is its normal).
/// It rides the Blend lane of <see cref="SdfOp.WallpaperFold"/> (the plane the fold tiles) and the Shape lane of
/// <see cref="SdfOp.RotatePlane"/> (the plane that rotates). The kernels read each member as <c>SDF_PLANE_*</c> from the
/// generated <c>sdf-isa.hlsli</c>.</summary>
public enum SdfPlane : uint {
    /// <summary>X and Z: the ground, facing Y.</summary>
    XZ = 0,
    /// <summary>X and Y: a wall facing Z.</summary>
    XY = 1,
    /// <summary>Y and Z: a wall facing X.</summary>
    YZ = 2,
}
