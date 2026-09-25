namespace Puck.SignedDistance;

// The packed-layout constants the kernels decode words with. Puck.SdfVm.SdfIsaHlsl generates each into
// sdf-isa.hlsli under the HLSL name its summary gives.
public sealed partial class SdfProgram {
    /// <summary>The bound mode of a record that carries no bound: the interpreter evaluates it in full
    /// (<c>SDF_BOUND_NONE</c>).</summary>
    public const uint BoundModeNone = 0;
    /// <summary>The bound mode of a record whose bound center moves with a dynamic transform slot
    /// (<c>SDF_BOUND_DYNAMIC</c>).</summary>
    public const uint BoundModeDynamic = 2;
    /// <summary>The bound mode of a record whose bound center is fixed (<c>SDF_BOUND_STATIC</c>).</summary>
    public const uint BoundModeStatic = 1;
    /// <summary>The material float4 stride, written by PackMaterials and read by the kernels' <c>sdfMaterialLoad</c>
    /// (<c>SDF_MATERIAL_VECTORS_PER_ENTRY</c>).</summary>
    public const int MaterialVectorsPerEntry = 20;
    /// <summary>Bit 0 of the instance directory header's <c>.z</c> word: no instruction carries
    /// <see cref="SdfInstruction.Detail"/>, so the kernels skip detail admission (<c>SDF_NO_DETAIL_SHAPES_FLAG</c>).</summary>
    public const uint NoDetailShapesFlag = 1u;
    /// <summary>The second-highest bit of a rigid leaf's shape index: the leaf rides a fold run, described by the slot
    /// that follows it (<c>SDF_RIGID_LEAF_FOLDED</c>).</summary>
    public const uint RigidLeafFoldedFlag = 0x40000000u;
    /// <summary>The high bit of a rigid leaf's shape index: the host-collapsed local rotation is identity, so the kernel
    /// neither loads nor applies it (<c>SDF_RIGID_LEAF_IDENTITY_ROTATION</c>).</summary>
    public const uint RigidLeafIdentityRotationFlag = 0x80000000u;
    /// <summary>The longest fold run, in instructions from its first fold through its last, a rigid leaf carries; longer
    /// runs stay on the generic interpreter (<c>SDF_RIGID_LEAF_MAX_FOLD_RUN</c>).</summary>
    public const int RigidLeafMaxFoldRun = 8;
    /// <summary>The bits of a rigid leaf's shape index below its two flags, <see cref="RigidLeafIdentityRotationFlag"/>
    /// and <see cref="RigidLeafFoldedFlag"/> (<c>SDF_RIGID_LEAF_SHAPE_MASK</c>).</summary>
    public const uint RigidLeafShapeMask = ~(RigidLeafIdentityRotationFlag | RigidLeafFoldedFlag);
    /// <summary>The low byte of a segment's bound-mode word, which holds the bound mode beside
    /// <see cref="SegmentRigidPlanFlag"/> (<c>SDF_SEGMENT_BOUND_MASK</c>).</summary>
    public const uint SegmentBoundModeMask = 0xFFu;
    /// <summary>The bits of an instance record's segmentEnd lane that hold the segment range's end; the high bit is
    /// <see cref="ShadowTransparentInstanceFlag"/> (<c>SDF_INSTANCE_SEGMENT_END_MASK</c>).</summary>
    public const uint SegmentEndMask = 0x7FFFFFFFu;
    /// <summary>The high bit of a segment's bound mode: the segment owns a host-compiled rigid-leaf plan. The low byte
    /// remains the bound mode (<c>SDF_SEGMENT_RIGID_PLAN</c>).</summary>
    public const uint SegmentRigidPlanFlag = 0x80000000u;
    /// <summary>The per-instance shadow-transparent flag, OR'd into the high bit of the instance meta's segmentEnd lane
    /// (i1.w) for an instance whose compose only removes material (a pure Subtraction-family carve). The soft-shadow
    /// gather reads it under the <c>sdf.shadow-proxy</c> lever to omit the instance from the shadow occluder set
    /// (marching the pre-carve union hull); mapCore masks it off with <see cref="SegmentEndMask"/>, so segmentEnd
    /// stays the true directory range and every rendered pixel is byte-identical. Segment-directory indices are far below
    /// 2^31, so this high bit is free (<c>SDF_INSTANCE_SHADOW_TRANSPARENT_BIT</c>).</summary>
    public const uint ShadowTransparentInstanceFlag = 0x80000000u;
    /// <summary>The high bit of a ShapeBlend instruction's shape lane: <see cref="SdfInstruction.Detail"/>. Shape-type
    /// ids are far below 2^31, so the bit is free (<c>SDF_SHAPE_DETAIL_FLAG</c>).</summary>
    public const uint ShapeDetailFlag = 0x80000000u;
    /// <summary>The next-highest bit of a ShapeBlend instruction's shape lane: <see cref="SdfInstruction.Secondary"/> is
    /// <see langword="false"/> (<c>SDF_SHAPE_NO_SECONDARY_FLAG</c>).</summary>
    public const uint ShapeNoSecondaryFlag = 0x40000000u;
    /// <summary>The bits of a ShapeBlend instruction's shape lane below <see cref="ShapeDetailFlag"/> and
    /// <see cref="ShapeNoSecondaryFlag"/>: the <see cref="SdfShapeType"/> id (<c>SDF_SHAPE_TYPE_MASK</c>).</summary>
    public const uint ShapeTypeMask = ~(ShapeDetailFlag | ShapeNoSecondaryFlag);
}
