// The field-scope save, the visible instance ranges, the per-invocation program layout and the compiled parts.
#ifndef FIELD_SDF_LAYOUT_HLSLI
#define FIELD_SDF_LAYOUT_HLSLI
// The one-deep scoped-accumulator save slot for the dual walk carries distance, material, and gradient together.
struct SdfFieldSave {
    float distance;
    int material;
    float4 lanes;
    int frameSlot;
    float3 gradient;
};

// The merge cursors' "exhausted" sentinel: no segment remains on that side (an impossible segment-directory index).
// Numerically equal to SDF_INSTANCE_MASK_ALL by coincidence only — the two name unrelated contracts.
#define SDF_SEGMENT_NONE 0xFFFFFFFFu

// Advances the visible-instance cursor to the next SET BIT of the caller's per-tile mask (ascending instance index,
// so ascending segment index — instances' segment ranges are disjoint and ascend with declaration order) and loads
// that instance's identity and [segmentFirst, segmentEnd) directory range. The identity selects a complete shared
// part program where supported; all three outputs are SDF_SEGMENT_NONE when no visible instance remains.
// `maskWordIndex`/`maskWordBits` carry the enumeration across calls: the current word index and
// its remaining (unconsumed) bits — the caller initializes them to (0xFFFFFFFFu, 0u), so the word-fetch loop below
// wraps onto word 0 on the first call. An empty range (an instance declared around zero instructions) is skipped,
// never surfaced.
void sdfNextVisibleInstanceRange(uint instanceMaskBase, uint instanceOffset, uint instanceCount, inout uint maskWordIndex, inout uint maskWordBits, out uint segmentFirst, out uint segmentEnd, out uint instanceIndex) {
    uint wordCount = sdfInstanceMaskWordCount(instanceCount);
    bool hasSummary = sdfInstanceMaskHasSummary(instanceMaskBase);

    [loop]
    for (;;) {
        if ((maskWordBits == 0u) && hasSummary) {
#ifdef SDF_INSTANCE_MASKS
            uint nextWord = (maskWordIndex + 1u);

            if (nextWord < wordCount) {
                uint summaryBase = (instanceMaskBase + wordCount);
                uint summaryCount = ((wordCount + 31u) >> 5u);
                uint summaryIndex = (nextWord >> 5u);
                uint summaryBits = (sdfInstanceMasks[summaryBase + summaryIndex] & (0xFFFFFFFFu << (nextWord & 31u)));

                [loop]
                while ((summaryBits == 0u) && ((summaryIndex + 1u) < summaryCount)) {
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
        while ((maskWordBits == 0u) && ((maskWordIndex + 1u) < wordCount)) {
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
        // A PARKED instance (a reserved-pool slot with no live content this rebuild) packs a negative-radius sentinel in
        // its bound (SdfProgram.ParkedBoundRadius): it contributes nothing to any ray, so skip its whole segment range —
        // this is what makes the beam prepass's own cone march (which evaluates under the SDF_INSTANCE_MASK_ALL sentinel,
        // so it would otherwise walk every parked pool slot's segments) cost track LIVE content, not reserved capacity.
        // Stage 1 never reaches here for a parked instance (its per-tile mask bit is always 0), so this only fires on the
        // all-visible cone-march / full-eval paths — exactly the ones that lacked the beam's per-tile skip.
        float parkedRadius = asfloat(sdfWords[entryBase]).w;

        if (parkedRadius < 0.0) {
            continue;
        }

        uint4 instanceMeta = sdfWords[entryBase + 1u];
#ifdef SDF_DYNAMIC_TRANSFORMS
        // The per-instance soft-shadow-participation skip — active ONLY inside the soft-shadow march window (the flag is
        // false for every camera/AO/coverage enumeration and for the beam/cull kernels). A DYNAMIC instance whose packed
        // position.w > 0.5 is shadow-suppressed (host encoding: 0 casts / 1 suppressed), so drop its whole segment range
        // from the shadow enumeration — mirroring the parked-radius continue above. Static instances (no dynamic slot)
        // always cast: they never take this branch.
        if (sdfShadowParticipationActive && (instanceMeta.x == SDF_BOUND_DYNAMIC) && (sdfDynamicTransforms[3u * instanceMeta.y].w > 0.5)) {
            continue;
        }
#endif
        // Strip the shadow-transparent flag (i1.w's high bit — SDF_INSTANCE_SHADOW_TRANSPARENT_BIT) before using the
        // lane as segmentEnd: it is a shadow-gather-only classification, unrelated to the render range. The mask is the
        // identity when the flag is clear; a set flag never changes the segment range it names.
        uint metaSegmentEnd = (instanceMeta.w & SDF_INSTANCE_SEGMENT_END_MASK);

        if (instanceMeta.z < metaSegmentEnd) {
            segmentFirst = instanceMeta.z;
            segmentEnd = metaSegmentEnd;
            return;
        }
    }
}

// The program's LAYOUT — every offset/count mapCore/mapGradCore need to walk the instruction stream. There is one
// program per dispatch, so this is dispatch-uniform; mapCore/mapGradCore used to re-derive it from sdfWords[0] on
// EVERY call, and marchers call them once per step (up to 128 primary steps plus exhaustion re-evaluation) —
// DXC's no-GVN-over-StructuredBuffer-loads gap (see sdfInstanceDirectoryOffsetFrom above)
// turned that into a real per-step reload. sdfLoadProgramLayout is now the ONE decode point; mapCore/mapGradCore read
// the cached static instead.
struct SdfProgramLayout {
    uint dataOffset;         // header.z: instruction data table offset
    uint boundsOffset;       // per-shape bounding-sphere table offset
    uint segmentOffset;      // segment directory offset
    uint segmentCount;       // segment directory's segment count
    uint rigidPlanOffset;    // rigid-leaf execution plan offset (segmentHeader.z)
    uint partProgramOffset;  // whole-scope shared programs (instanceHeader.y); zero when none qualify
    bool noDetailShapes;    // instanceHeader.z bit 0: the visibility record's distance and attributes are valid for shading
    float stepScale;         // per-program Lipschitz step clamp (1/L; already >0-guarded)
    uint instanceOffset;     // instance directory offset
    uint instanceCount;      // packed (unclamped) instance count
    uint worldSegmentOffset; // world-segment list offset
    bool hasInstances;       // instanceCount != 0
};

// Every kernel calling a field evaluator must initialize this per-thread cache once at entry.
// A skipped initialization leaves zero counts and no optimization admission.
static SdfProgramLayout sdfProgramLayout = (SdfProgramLayout)0;

// The ONE per-invocation layout decode. Same loads, same order as mapCore's former inline sequence.
SdfProgramLayout sdfLoadProgramLayout() {
    uint4 header = sdfWords[0];
    uint boundsOffset = (SDF_PROGRAM_MATERIAL_OFFSET(header) + (SDF_MATERIAL_VECTORS_PER_ENTRY * SDF_PROGRAM_MATERIAL_COUNT(header)));
    uint segmentOffset = (boundsOffset + (SDF_BOUND_RECORD_VECTORS * SDF_PROGRAM_INSTRUCTION_COUNT(header)));
    uint4 segmentHeader = sdfWords[segmentOffset];
    uint segmentCount = SDF_SEGMENT_COUNT(segmentHeader);
    float stepScale = asfloat(SDF_SEGMENT_STEP_SCALE(segmentHeader));
    uint instanceOffset = sdfInstanceDirectoryOffsetFrom(segmentOffset, segmentCount);
    uint instanceCount = SDF_INSTANCE_COUNT(sdfWords[instanceOffset]);
    SdfProgramLayout layout;

    layout.dataOffset = SDF_PROGRAM_DATA_OFFSET(header);
    layout.boundsOffset = boundsOffset;
    layout.segmentOffset = segmentOffset;
    layout.segmentCount = segmentCount;
    layout.rigidPlanOffset = SDF_SEGMENT_RIGID_PLAN_OFFSET(segmentHeader);
    layout.partProgramOffset = SDF_INSTANCE_PART_PROGRAMS(sdfWords[instanceOffset]);
    layout.noDetailShapes = (SDF_INSTANCE_FLAGS(sdfWords[instanceOffset]) & SDF_NO_DETAIL_SHAPES_FLAG) != 0u;
    layout.stepScale = ((stepScale > 0.0) ? stepScale : 1.0);
    layout.instanceOffset = instanceOffset;
    layout.instanceCount = instanceCount;
    layout.worldSegmentOffset = (instanceOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * instanceCount));
    layout.hasInstances = (instanceCount != 0u);

    return layout;
}

// The generic evaluator both map() (every existing consumer; a zero-instance program's ONLY path) and mapMasked()
// (the world render path's Stage 1) share. A ZERO-INSTANCE program takes the linear fast path — every segment in
// directory order, the pre-instancing interpreter verbatim (`hasInstances` is read once, per-program-uniform, so the
// branch never diverges within one program's invocations). An INSTANCED program merges the WORLD-SEGMENT list
// (always evaluated) with the VISIBLE instances' segment ranges by ascending segment index — the flat stream's
// order, so order-dependent blend ops (SmoothSubtraction, Xor, ...) compose exactly as an unmasked walk would — and
// a call costs O(world segments + visible instances' segments): a masked-out instance's segments are never loaded,
// never owner-tested, never bound-tested. With the SDF_INSTANCE_MASK_ALL sentinel every instance enumerates as
// visible (validity-masked to the real instance count), so the merge degenerates to the full ascending walk.
#ifdef SDF_VM_EAGER_PAYLOADS
#define SDF_VM_LOAD_DATA0
#define SDF_VM_LOAD_DATA1
#else
#define SDF_VM_LOAD_DATA0 float4 data0 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index)])
#define SDF_VM_LOAD_DATA1 float4 data1 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index) + 1u])
#endif

#include "sdf-parts.hlsli"

#endif
