namespace Puck.SdfVm;

// Values must match Shaders/Sdf/sdf-vm.hlsli (SDF_OP_*); the numbering is the legacy ISA and is non-sequential.
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
    /// <summary>Bends space about the local X axis: the XY plane rotates by <c>rate * x</c> radians (Data0.x = rate).
    /// NOT an isometry (space stretches tangentially) — keep rates moderate so the march stays stable.</summary>
    BendX = 5,
    /// <summary>Bends the XY plane by <c>rate * y</c> radians (Data0.x = rate).</summary>
    BendY = 6,
    /// <summary>Bends the YZ plane by <c>rate * y</c> radians (Data0.x = rate). QUIRK, kept deliberately: like
    /// <see cref="BendY"/> it keys on the local Y coordinate (not Z).</summary>
    BendZ = 7,
    /// <summary>Elongates the shape that follows by clamping the point into a box (Data0.xyz = extents): the shape's
    /// cross-section is swept over <c>±extents</c> — the classic capsule-from-sphere operator.</summary>
    Elongate = 8,
    ShapeBlend = 9,
    Repeat = 11,
    RepeatLimited = 12,
    SymmetryX = 13,
    SymmetryY = 14,
    SymmetryZ = 15,
    /// <summary>Shells the ENTIRE field accumulated so far: <c>d = abs(d) − thickness</c> (Data0.x = thickness) turns
    /// solids into hollow skins. A FIELD op, not a point op — order objects so it follows everything it should shell.</summary>
    Onion = 16,
    /// <summary>Inflates the ENTIRE field accumulated so far by a radius: <c>d −= radius</c> (Data0.x = radius) rounds
    /// and fattens everything before it. A FIELD op, not a point op. (The legacy ISA's "Round"; named Dilate because
    /// the box primitive's corner rounding already owns that word.)</summary>
    Dilate = 17,
    /// <summary>Folds the evaluation point's in-plane coordinates onto the fundamental cell of a wallpaper symmetry
    /// group (all 17 IUC groups; square/rectangular lattices plus the equilateral hex lattice for P3 and up). The
    /// lattice reduction is <see cref="RepeatLimited"/> restricted to two axes (P1 is bit-identical to it); the
    /// per-cell stage composes mirrors/rotations keyed on the lattice parity. Every branch is an isometry, so
    /// distances are preserved. Instruction lanes: Shape = <see cref="SdfWallpaperGroup"/>, Blend =
    /// <see cref="SdfWallpaperPlane"/>, Material = the parity-material stride (the cell key — checker parity or hex
    /// 3-coloring — strides the material id of later shape wins in the chain; 0 keeps the fold purely geometric).
    /// Data0.xy = cell extents (hex: pitch = x, y must equal it), Data1.xy = RepeatLimited-style cell limits,
    /// Data1.z = the symmetry-LOD distance threshold (0 = off): past it the lattice keeps its copies but the in-cell
    /// folds are skipped — upright copies, cheaper and shimmer-free at range.</summary>
    WallpaperFold = 18,
    /// <summary>Twists space about the local Y axis: the XZ plane rotates by <c>rate * y</c> radians (Data0.x = rate).
    /// NOT an isometry — keep rates moderate. (The legacy ISA numbered this 4, now owned by
    /// <see cref="TransformDynamic"/>; slot 20 is its new home.)</summary>
    TwistY = 20,
}
