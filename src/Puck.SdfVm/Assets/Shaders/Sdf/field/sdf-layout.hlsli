// The field-scope save, the visible instance ranges, the per-invocation program layout and the compiled parts.
#ifndef FIELD_SDF_LAYOUT_HLSLI
#define FIELD_SDF_LAYOUT_HLSLI
// Each parent entry in the dual walk's bounded scope stack carries distance, material and gradient together.
struct SdfFieldSave {
    float distance;
    int material;
    float4 lanes;
    int instanceIndex;
    int frameSlot;
    float3 gradient;
};

// The merge cursors' "exhausted" sentinel: no segment remains on that side (an impossible segment-directory index).
// Numerically equal to SDF_INSTANCE_MASK_ALL by coincidence only — the two name unrelated contracts.
#define SDF_SEGMENT_NONE 0xFFFFFFFFu

// Advances the visible-instance cursor to the next SET BIT of the caller's instance mask (ascending instance index,
// so ascending segment index — instances' segment ranges are disjoint and ascend with declaration order) and loads
// that instance's identity and [segmentFirst, segmentEnd) directory range. The identity selects a complete shared
// part program where supported; all three outputs are SDF_SEGMENT_NONE when no visible instance remains.
// `maskWordIndex`/`maskWordBits` carry the enumeration across calls: the current word index and
// its remaining (unconsumed) bits — the caller initializes them to (0xFFFFFFFFu, 0u), so the word-fetch loop below
// wraps onto word 0 on the first call. An empty range (an instance declared around zero instructions) is skipped,
// never surfaced.
void sdfNextVisibleInstanceRange(uint instanceMaskBase, uint instanceOffset, uint instanceCount, inout uint maskWordIndex, inout uint maskWordBits, out uint segmentFirst, out uint segmentEnd, out uint instanceIndex) {
    segmentFirst = SDF_SEGMENT_NONE;
    segmentEnd = SDF_SEGMENT_NONE;
    instanceIndex = SDF_SEGMENT_NONE;
    if (instanceCount > SDF_MAX_INSTANCES) { return; }
    uint wordCount = sdfInstanceMaskWordCount(instanceCount);
    bool hasSummary = sdfInstanceMaskHasSummary(instanceMaskBase);
#ifdef SDF_INSTANCE_MASKS
    if (hasSummary) {
        uint capacity;
        uint stride;
        sdfInstanceMasks.GetDimensions(capacity, stride);
        uint summaryCount = (wordCount + 31u) >> 5u;
        if (instanceMaskBase > capacity || wordCount + summaryCount > capacity - instanceMaskBase) { return; }
    }
#endif

    [loop]
    for (uint candidate = 0u; candidate < SDF_MAX_INSTANCES; candidate++) {
        if ((maskWordBits == 0u) && hasSummary) {
#ifdef SDF_INSTANCE_MASKS
            uint nextWord = (maskWordIndex + 1u);

            if (nextWord < wordCount) {
                uint summaryBase = (instanceMaskBase + wordCount);
                uint summaryCount = ((wordCount + 31u) >> 5u);
                uint summaryIndex = (nextWord >> 5u);
                uint summaryBits = (sdfInstanceMasks[summaryBase + summaryIndex] & (0xFFFFFFFFu << (nextWord & 31u)));

                [loop]
                for (uint advance = 0u; advance < SDF_INSTANCE_MASK_MAX_WORDS; advance++) {
                    if (summaryBits != 0u || summaryIndex + 1u >= summaryCount) { break; }
                    summaryIndex++;
                    summaryBits = sdfInstanceMasks[summaryBase + summaryIndex];
                }

                if (summaryBits != 0u) {
                    maskWordIndex = ((summaryIndex << 5u) + (uint)firstbitlow(summaryBits));
                    maskWordBits = sdfInstanceMaskWord(instanceMaskBase, maskWordIndex, instanceCount);
                } else {
                    maskWordIndex = wordCount;
                }
            } else {
                maskWordIndex = wordCount;
            }
#endif
        }

        [loop]
        for (uint advance = 0u; advance < SDF_INSTANCE_MASK_MAX_WORDS; advance++) {
            if (maskWordBits != 0u || maskWordIndex + 1u >= wordCount) { break; }
            maskWordIndex++;
            maskWordBits = sdfInstanceMaskWord(instanceMaskBase, maskWordIndex, instanceCount);
        }

        if (maskWordBits == 0u) {
            segmentFirst = SDF_SEGMENT_NONE;
            segmentEnd = SDF_SEGMENT_NONE;
            instanceIndex = SDF_SEGMENT_NONE;
            return;
        }

        instanceIndex = ((maskWordIndex << 5u) + firstbitlow(maskWordBits));

        maskWordBits &= (maskWordBits - 1u);

        uint entryBase = sdfInstanceEntryOffset(instanceOffset, instanceIndex);
        if (instanceIndex >= instanceCount || !sdfProgramRange(entryBase, SDF_BOUND_RECORD_VECTORS, 1u)) { break; }
        // A PARKED instance (a reserved-pool slot with no live content this rebuild) packs a negative-radius sentinel in
        // its bound (SdfProgram.ParkedBoundRadius): it contributes nothing to any sample, so skip its whole segment range,
        // which makes a walk under the SDF_INSTANCE_MASK_ALL sentinel cost track LIVE content, not reserved capacity.
        float parkedRadius = asfloat(sdfProgramWord(entryBase)).w;

        if (parkedRadius < 0.0) {
            continue;
        }

        uint4 instanceMeta = sdfProgramWord(entryBase + 1u);

        if (instanceMeta.z < instanceMeta.w) {
            segmentFirst = instanceMeta.z;
            segmentEnd = instanceMeta.w;
            return;
        }
    }
    segmentFirst = SDF_SEGMENT_NONE;
    segmentEnd = SDF_SEGMENT_NONE;
    instanceIndex = SDF_SEGMENT_NONE;
}

// The program's LAYOUT — every offset/count mapCore needs to walk the instruction stream. There is one program per
// dispatch, so this is dispatch-uniform; deriving it from sdfProgramWord(0) on every call would reload it per sample,
// since DXC does not merge StructuredBuffer loads (see sdfInstanceDirectoryOffsetFrom above). sdfLoadProgramLayout is
// the ONE decode point; mapCore reads the cached static.
struct SdfProgramLayout {
    bool valid;
    uint instructionCount;
    uint vectorCount;
    uint dataOffset;         // header.z: instruction data table offset
    uint boundsOffset;       // per-shape bounding-sphere table offset
    uint segmentOffset;      // segment directory offset
    uint segmentCount;       // segment directory's segment count
    uint rigidPlanOffset;    // rigid-leaf execution plan offset (segmentHeader.z)
    uint partProgramOffset;  // whole-scope shared programs (instanceHeader.y); zero when none qualify
    float stepScale;         // per-program Lipschitz step clamp (1/L; already >0-guarded)
    uint instanceOffset;     // instance directory offset
    uint instanceCount;      // packed (unclamped) instance count
    uint worldSegmentOffset; // world-segment list offset
};

// Every kernel calling a field evaluator must initialize this per-thread cache once at entry.
// A skipped initialization leaves zero counts and no optimization admission.
static SdfProgramLayout sdfProgramLayout = (SdfProgramLayout)0;

// The ONE per-invocation layout decode. Same loads, same order as mapCore's former inline sequence.
SdfProgramLayout sdfLoadProgramLayout() {
    SdfProgramLayout layout = (SdfProgramLayout)0;
    layout.stepScale = 1.0;
    uint vectors = sdfProgramVectorCount();
    if (vectors == 0u) { return layout; }
    uint4 header = sdfProgramWord(0);
    uint instructions = SDF_PROGRAM_INSTRUCTION_COUNT(header);
    if (!sdfProgramRange(SDF_PROGRAM_HEADER_VECTORS, instructions, 1u)
        || !sdfProgramRange(SDF_PROGRAM_DATA_OFFSET(header), instructions, SDF_INSTRUCTION_DATA_VECTORS)
        || !sdfProgramRange(SDF_PROGRAM_MATERIAL_OFFSET(header), SDF_PROGRAM_MATERIAL_COUNT(header), SDF_MATERIAL_VECTORS_PER_ENTRY)) { return layout; }
    uint boundsOffset = (SDF_PROGRAM_MATERIAL_OFFSET(header) + (SDF_MATERIAL_VECTORS_PER_ENTRY * SDF_PROGRAM_MATERIAL_COUNT(header)));
    if (!sdfProgramRange(boundsOffset, instructions, SDF_BOUND_RECORD_VECTORS)) { return layout; }
    uint segmentOffset = (boundsOffset + (SDF_BOUND_RECORD_VECTORS * SDF_PROGRAM_INSTRUCTION_COUNT(header)));
    if (segmentOffset >= vectors) { return layout; }
    uint4 segmentHeader = sdfProgramWord(segmentOffset);
    uint segmentCount = SDF_SEGMENT_COUNT(segmentHeader);
    if (!sdfProgramRange(segmentOffset + SDF_DIRECTORY_HEADER_VECTORS, segmentCount, SDF_BOUND_RECORD_VECTORS)) { return layout; }
    float stepScale = asfloat(SDF_SEGMENT_STEP_SCALE(segmentHeader));
    uint instanceOffset = sdfInstanceDirectoryOffsetFrom(segmentOffset, segmentCount);
    if (instanceOffset >= vectors) { return layout; }
    uint instanceCount = SDF_INSTANCE_COUNT(sdfProgramWord(instanceOffset));
    if (instanceCount > SDF_MAX_INSTANCES
        || !sdfProgramRange(instanceOffset + SDF_DIRECTORY_HEADER_VECTORS, instanceCount, SDF_BOUND_RECORD_VECTORS)) { return layout; }
    uint worldOffset = instanceOffset + SDF_DIRECTORY_HEADER_VECTORS + SDF_BOUND_RECORD_VECTORS * instanceCount;
    if (worldOffset >= vectors) { return layout; }
    uint4 worldHeader = sdfProgramWord(worldOffset);
    if (!sdfProgramRange(worldOffset + SDF_DIRECTORY_HEADER_VECTORS, SDF_WORLD_SEGMENT_COUNT(worldHeader), 1u)) { return layout; }
    layout.valid = true;
    layout.vectorCount = vectors;
    layout.instructionCount = instructions;

    layout.dataOffset = SDF_PROGRAM_DATA_OFFSET(header);
    layout.boundsOffset = boundsOffset;
    layout.segmentOffset = segmentOffset;
    layout.segmentCount = segmentCount;
    layout.rigidPlanOffset = SDF_SEGMENT_RIGID_PLAN_OFFSET(segmentHeader);
    layout.partProgramOffset = SDF_INSTANCE_PART_PROGRAMS(sdfProgramWord(instanceOffset));
    layout.stepScale = ((stepScale > 0.0) ? stepScale : 1.0);
    layout.instanceOffset = instanceOffset;
    layout.instanceCount = instanceCount;
    layout.worldSegmentOffset = (instanceOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * instanceCount));

    return layout;
}

// The generic evaluator mapCore runs, under the SDF_INSTANCE_MASK_ALL sentinel or a bake lattice's instance mask. It
// merges the WORLD-SEGMENT list (always evaluated, and every segment of a program with no instance) with the
// VISIBLE instances' segment ranges by ascending segment index — the flat stream's
// order, so order-dependent blend ops (SmoothSubtraction, Xor, ...) compose exactly as an unmasked walk would — and
// a call costs O(world segments + visible instances' segments): a masked-out instance's segments are never loaded,
// never owner-tested, never bound-tested. With the SDF_INSTANCE_MASK_ALL sentinel every instance enumerates as
// visible (validity-masked to the real instance count), so the merge degenerates to the full ascending walk.
// The validated payload span and the interpreter's index < instructionCount guard dominate these reads.
#define SDF_VM_LOAD_DATA0 float4 data0 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index)])
#define SDF_VM_LOAD_DATA1 float4 data1 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index) + 1u])

#endif
