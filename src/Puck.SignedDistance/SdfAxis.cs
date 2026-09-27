namespace Puck.SignedDistance;

/// <summary>A coordinate axis of an instruction's local frame. Its value is the component index a kernel reads the
/// point's coordinate at (X is <c>p[0]</c>), so a lane holding one indexes the point directly. It rides the Shape lane of
/// <see cref="SdfOp.RepeatPolar"/> (the rotation axis, whose fold acts in the plane perpendicular to it),
/// <see cref="SdfOp.AxialProfile"/> (the profile coordinate) and <see cref="SdfOp.Shear"/> (the displaced coordinate),
/// and the Blend lane of <see cref="SdfOp.RotatePlane"/> and <see cref="SdfOp.Shear"/> (the driving coordinate). The
/// kernels read each member as <c>SDF_AXIS_*</c> from the generated <c>sdf-isa.hlsli</c>.</summary>
public enum SdfAxis : uint {
    /// <summary>The local X axis; a polar repeat about it folds the YZ plane.</summary>
    X = 0,
    /// <summary>The local Y axis; a polar repeat about it folds the XZ (ground) plane, the common "arranged on the ground
    /// around a centre" case (columns of a rotunda, spokes of a wheel lying flat).</summary>
    Y = 1,
    /// <summary>The local Z axis; a polar repeat about it folds the XY plane (a clock face or a gear facing +Z).</summary>
    Z = 2,
}
