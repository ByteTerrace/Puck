namespace Puck.SdfVm;

// Values must match Shaders/sdf-vm.glsl (SDF_OP_*); the numbering is the legacy ISA and is non-sequential.
public enum SdfOp : uint {
    ResetPoint = 0,
    Translate = 1,
    Rotate = 2,
    Scale = 3,
    ShapeBlend = 9,
    Repeat = 11,
    RepeatLimited = 12,
    SymmetryX = 13,
    SymmetryY = 14,
    SymmetryZ = 15,
}
