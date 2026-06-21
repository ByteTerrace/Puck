namespace Puck.SdfVm;

// Values must match Shaders/sdf-vm.glsl (SDF_OP_*); the numbering is the legacy ISA and is non-sequential.
public enum SdfOp : uint {
    ResetPoint = 0,
    Translate = 1,
    Rotate = 2,
    Scale = 3,
    /// <summary>A rigid transform (translation + orientation) read at evaluation time from a per-frame dynamic-transform
    /// buffer slot (Data0.x = slot index) rather than from immediate instruction data: element <c>2*slot</c> is the
    /// position (xyz), <c>2*slot+1</c> the orientation quaternion. Lets a moving entity (player, enemy, carried screen)
    /// be repositioned each frame by updating a small buffer, WITHOUT re-uploading the static scene program. Honored only
    /// by shaders compiled with <c>SDF_DYNAMIC_TRANSFORMS</c> (the world path); a no-op elsewhere.</summary>
    TransformDynamic = 4,
    ShapeBlend = 9,
    Repeat = 11,
    RepeatLimited = 12,
    SymmetryX = 13,
    SymmetryY = 14,
    SymmetryZ = 15,
}
