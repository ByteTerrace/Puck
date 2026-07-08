namespace Puck.SdfVm;

/// <summary>The rotation axis of an angular domain-repeat (<see cref="SdfProgramBuilder.RepeatPolar"/> /
/// <see cref="SdfOp.RepeatPolar"/>): the fold acts in the plane PERPENDICULAR to it, leaving the axial coordinate
/// untouched. Values MUST match the <c>SDF_POLAR_AXIS_*</c> defines in Assets/Shaders/Sdf/sdf-vm.hlsli (read from the
/// instruction's Shape lane).</summary>
public enum SdfPolarAxis : uint {
    /// <summary>Repeat about the local X axis — the fold acts in the YZ plane.</summary>
    X = 0,
    /// <summary>Repeat about the local Y axis — the fold acts in the XZ (ground) plane. The default: the common
    /// "arranged on the ground around a centre" case (columns of a rotunda, spokes of a wheel lying flat).</summary>
    Y = 1,
    /// <summary>Repeat about the local Z axis — the fold acts in the XY plane (a clock face / gear facing +Z).</summary>
    Z = 2,
}
