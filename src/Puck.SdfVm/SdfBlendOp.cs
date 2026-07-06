namespace Puck.SdfVm;

// Values must match Shaders/Sdf/sdf-vm.hlsli (SDF_BLEND_*).
public enum SdfBlendOp : uint {
    Union = 0,
    SmoothUnion = 1, // blend radius = instruction Data1.x
    Subtraction = 2,
    Intersection = 3,
    /// <summary>Symmetric difference: solid where exactly one of the fields is solid (hollow where they overlap).</summary>
    Xor = 4,
    /// <summary>Intersection with a smooth seam (blend radius = instruction Data1.x).</summary>
    SmoothIntersection = 5,
    /// <summary>Subtraction with a smooth (filleted) carve seam (blend radius = instruction Data1.x).</summary>
    SmoothSubtraction = 6,
}
