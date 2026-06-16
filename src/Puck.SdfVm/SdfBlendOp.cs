namespace Puck.SdfVm;

// Values must match Shaders/sdf-vm.glsl (SDF_BLEND_*).
public enum SdfBlendOp : uint {
    Union = 0,
    SmoothUnion = 1, // blend radius = instruction Data1.x
    Subtraction = 2,
    Intersection = 3,
}
