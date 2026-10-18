namespace Puck.SignedDistance;

// The packed-layout constants the kernels decode words with. Puck.SdfVm.SdfIsaHlsl generates each into
// sdf-isa.hlsli under the HLSL name its summary gives.
public sealed partial class SdfProgram {
    /// <summary>The vectors before the instruction headers: the program header alone. Instruction <c>i</c>'s header is
    /// vector <c>ProgramHeaderVectors + i</c> (<c>SDF_PROGRAM_HEADER_VECTORS</c>).</summary>
    public const int ProgramHeaderVectors = 1;
    /// <summary>The program header's lane holding the instruction count (<c>SDF_PROGRAM_INSTRUCTION_COUNT_LANE</c>).</summary>
    public const int ProgramInstructionCountLane = 0;
    /// <summary>The program header's lane holding the material count (<c>SDF_PROGRAM_MATERIAL_COUNT_LANE</c>).</summary>
    public const int ProgramMaterialCountLane = 1;
    /// <summary>The program header's lane holding the vector offset of the instruction data table
    /// (<c>SDF_PROGRAM_DATA_OFFSET_LANE</c>).</summary>
    public const int ProgramDataOffsetLane = 2;
    /// <summary>The program header's lane holding the vector offset of the material table
    /// (<c>SDF_PROGRAM_MATERIAL_OFFSET_LANE</c>).</summary>
    public const int ProgramMaterialOffsetLane = 3;
    /// <summary>An instruction header's lane holding its <see cref="SdfOp"/> (<c>SDF_INSTRUCTION_OP_LANE</c>).</summary>
    public const int InstructionOpLane = 0;
    /// <summary>An instruction header's lane holding <see cref="SdfInstruction.Shape"/>
    /// (<c>SDF_INSTRUCTION_SHAPE_LANE</c>).</summary>
    public const int InstructionShapeLane = 1;
    /// <summary>An instruction header's lane holding <see cref="SdfInstruction.Blend"/>
    /// (<c>SDF_INSTRUCTION_BLEND_LANE</c>).</summary>
    public const int InstructionBlendLane = 2;
    /// <summary>An instruction header's lane holding <see cref="SdfInstruction.Material"/>
    /// (<c>SDF_INSTRUCTION_MATERIAL_LANE</c>).</summary>
    public const int InstructionMaterialLane = 3;
    /// <summary>The data table's vectors per instruction: <see cref="SdfInstruction.Data0"/>, then
    /// <see cref="SdfInstruction.Data1"/> (<c>SDF_INSTRUCTION_DATA_VECTORS</c>).</summary>
    public const int InstructionDataVectors = 2;
    /// <summary>The vectors of a bound record, a shape's, a segment's or an instance's: its bound sphere, then its
    /// meta (<c>SDF_BOUND_RECORD_VECTORS</c>).</summary>
    public const int BoundRecordVectors = 2;
    /// <summary>The header vectors before a directory's records: the segment directory's, the instance directory's and
    /// the world-segment list's (<c>SDF_DIRECTORY_HEADER_VECTORS</c>).</summary>
    public const int DirectoryHeaderVectors = 1;
    /// <summary>The segment directory header's lane holding the segment count (<c>SDF_SEGMENT_COUNT_LANE</c>).</summary>
    public const int SegmentCountLane = 0;
    /// <summary>The segment directory header's lane holding the program's step scale, as float bits
    /// (<c>SDF_SEGMENT_STEP_SCALE_LANE</c>).</summary>
    public const int SegmentStepScaleLane = 1;
    /// <summary>The segment directory header's lane holding the rigid-leaf plan's vector offset
    /// (<c>SDF_SEGMENT_RIGID_PLAN_LANE</c>).</summary>
    public const int SegmentRigidPlanLane = 2;
    /// <summary>The instance directory header's lane holding the instance count (<c>SDF_INSTANCE_COUNT_LANE</c>).</summary>
    public const int InstanceCountLane = 0;
    /// <summary>The instance directory header's lane holding the part-program table's vector offset, zero when none
    /// qualify (<c>SDF_INSTANCE_PART_PROGRAMS_LANE</c>).</summary>
    public const int InstancePartProgramsLane = 1;
    /// <summary>The world-segment list header's lane holding the list's length
    /// (<c>SDF_WORLD_SEGMENT_COUNT_LANE</c>).</summary>
    public const int WorldSegmentCountLane = 0;
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
    public const int MaterialVectorsPerEntry = 21;
    /// <summary>The paint table's float4 stride, written by PackPaints and read by the views pass's paint layer
    /// (<c>SDF_PAINT_VECTORS_PER_ENTRY</c>): the frame origin and shape, the rotation, the residual scale, the shape's
    /// two data vectors, the color and stroke, the slot and bound center, the bound half-extents, then
    /// <see cref="PaintTableVectors"/> of side table.</summary>
    public const int PaintVectorsPerEntry = (8 + PaintTableVectors);
    /// <summary>The side-table vectors a paint carries inline: a convex polygon's eight vertices or a sweep's curve
    /// (<c>SDF_PAINT_TABLE_VECTORS</c>).</summary>
    public const int PaintTableVectors = 4;
    /// <summary>The most paints one material carries (<c>SDF_MAX_PAINTS_PER_MATERIAL</c>).</summary>
    public const int MaxPaintsPerMaterial = 8;
    /// <summary>The dynamic slot word of a paint in the frame of the slot that owns the hit
    /// (<c>SDF_PAINT_WINNING_FRAME</c>).</summary>
    public const uint PaintWinningFrame = uint.MaxValue;
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
    /// <summary>The high bit of a segment's bound mode: the segment owns a host-compiled rigid-leaf plan. The low byte
    /// remains the bound mode (<c>SDF_SEGMENT_RIGID_PLAN</c>).</summary>
    public const uint SegmentRigidPlanFlag = 0x80000000u;
    /// <summary>The bits a dynamic-transform slot occupies. A <see cref="SdfOp.TransformDynamic"/> instruction carries
    /// its slot in a float data lane, which holds every integer below 2^24 exactly, so a wider slot would round there;
    /// the kernels' transform row index, three times the slot plus two, stays inside 32 bits.</summary>
    public const int DynamicTransformSlotBits = 24;
    /// <summary>The largest legal dynamic-transform slot index; a program naming a larger one is refused.</summary>
    public const int MaxDynamicTransformSlot = ((1 << DynamicTransformSlotBits) - 1);
    /// <summary>The transform slot of geometry no dynamic transform places: what the winner's slot and a visibility
    /// record's transform-slot lane hold for a static hit, and a light's, volume's, rigid segment's or part binding's
    /// slot when nothing moves it (<c>SDF_TRANSFORM_SLOT_NONE</c>).</summary>
    public const int NoDynamicTransformSlot = -1;
    /// <summary>The unsigned word a rigid segment's slot lane and a part binding's pose lane hold for
    /// <see cref="NoDynamicTransformSlot"/>: the packed form of a static transform (<see cref="PackTransformSlot"/>,
    /// <c>SDF_TRANSFORM_SLOT_STATIC_WORD</c>).</summary>
    public const uint StaticTransformSlotWord = ((uint)(NoDynamicTransformSlot - NoDynamicTransformSlot));

    /// <summary>Packs a transform slot into the unsigned word a rigid segment's slot lane and a part binding's pose lane
    /// store: offset so <see cref="NoDynamicTransformSlot"/> packs to <see cref="StaticTransformSlotWord"/> and every
    /// dynamic slot to a word above it. <see cref="UnpackTransformSlot"/> inverts it, and the kernels spell that inverse
    /// <c>SDF_TRANSFORM_SLOT_UNPACK</c>.</summary>
    /// <param name="slot">The slot: <see cref="NoDynamicTransformSlot"/>, or a dynamic slot through
    /// <see cref="MaxDynamicTransformSlot"/>.</param>
    /// <returns>The packed word.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is below <see cref="NoDynamicTransformSlot"/>
    /// or above <see cref="MaxDynamicTransformSlot"/>.</exception>
    public static uint PackTransformSlot(int slot) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: NoDynamicTransformSlot,
            value: slot
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MaxDynamicTransformSlot,
            value: slot
        );

        return ((uint)(slot - NoDynamicTransformSlot));
    }
    /// <summary>Unpacks the word a rigid segment's slot lane or a part binding's pose lane stores
    /// (<see cref="PackTransformSlot"/>) back into its transform slot.</summary>
    /// <param name="word">The packed word.</param>
    /// <returns>The slot: <see cref="NoDynamicTransformSlot"/> for <see cref="StaticTransformSlotWord"/>, or the dynamic
    /// slot the word packs.</returns>
    public static int UnpackTransformSlot(uint word) => checked((((int)word) + NoDynamicTransformSlot));
}
