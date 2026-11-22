#ifndef PUCK_SDF_PARTS_HLSLI
#define PUCK_SDF_PARTS_HLSLI

// Root composition admission is rebuilt by SdfProgram.PartPrograms.cs. Header .x stores the compiled count
// in bits 0..30 and independent-tracing admission in bit 31.
bool sdfCanTracePartsIndependently() {
    uint table = sdfProgramLayout.partProgramOffset;
    return table != 0u && (sdfProgramWord(table).x & 0x80000000u) != 0u;
}

// The distance gain a part entry certifies (SdfProgram.InstanceDistanceGain, packed by EncodePartDistanceGain and
// decoded alike by DecodePartDistanceGain): outside the instance's packed bound its field, in every walk, is at least
// the gain times the gap to that bound. Zero means the instance is never rejected whole.
float sdfPartDistanceGain(uint4 part) {
    uint field = (part.z >> SDF_PART_DISTANCE_GAIN_SHIFT) & SDF_PART_DISTANCE_GAIN_MASK;
    return ((field == SDF_PART_DISTANCE_GAIN_MASK) ? 1.0 : ((float)field * SDF_PART_DISTANCE_GAIN_UNIT));
}

// The gap from worldPosition to a visible instance's packed bound (SdfProgram's instance directory entry), with a
// dynamic bound's centre riding its transform; -SDF_FAR_DISTANCE when the bound is unusable here (non-finite, a
// sentinel, or dynamic without the transform buffer), which rejects nothing and floors nothing.
float sdfInstanceGap(uint instanceOffset, uint instance, float3 worldPosition) {
    uint entry = sdfInstanceEntryOffset(instanceOffset, instance);
    uint4 meta = sdfProgramWord(entry + 1u);
    float4 bound = asfloat(sdfProgramWord(entry));
    if (!(bound.w >= 0.0) || !all(isfinite(bound))) { return -SDF_FAR_DISTANCE; }
    if (meta.x == SDF_BOUND_DYNAMIC || meta.x == SDF_BOUND_DYNAMIC_LOCAL) {
#ifdef SDF_DYNAMIC_TRANSFORMS
        if (meta.x == SDF_BOUND_DYNAMIC_LOCAL) {
            bound.xyz = rotatePointByQuaternion(bound.xyz, sdfDynamicTransformRow(3u * meta.y + 1u)) + sdfDynamicTransformRow(3u * meta.y).xyz;
        } else {
            bound.xyz = sdfDynamicBoundCenter(bound.xyz, meta.y);
        }
#else
        return -SDF_FAR_DISTANCE;
#endif
    } else if (meta.x != SDF_BOUND_STATIC) {
        return -SDF_FAR_DISTANCE;
    }
    return (length(worldPosition - bound.xyz) - bound.w);
}

// Whether a whole visible instance at gap from its bound cannot replace the incumbent: the per-sample twin of the
// segment walk's skip sphere. Outside the bound the instance's field is at least the gap times its distance gain
// (sdfPartDistanceGain: its scopes' rescales, its gauges' and non-uniform scales' slower growth, its smooth unions' sag
// absorbed by the packed radius), so a scaled gap at least the incumbent loses a hard union's strict test exactly and skipping the instance changes no
// distance or material. A zero gain (an approximate primitive, a warp, an optional clipper) is never rejected.
// It asks only where the instance joins as a hard union: a compiled part always does, and a generic instance does
// under the root-union certificate (sdfCanTracePartsIndependently). Inside the bound nothing is skipped.
bool sdfInstanceCannotWin(float gap, float gain, float incumbent) {
    // A scope starts at the finite far sentinel before its rescale, even when every leaf lies farther away; the gain
    // never exceeds that rescale.
    if (!(gain > 0.0) || incumbent >= SDF_FAR_DISTANCE * gain) { return false; }
    return ((gap > 0.0) && ((gap * gain) >= incumbent));
}

// Compiled whole-scope programs: entry = (shared leaf run, placement binding run, count|distance gain|dynamic,
// scope scale);
// an instance the table does not compile has a zero count, its distance gain and nothing else.
// Each leaf = (canonical shape instruction, optional domain instruction + 1, 0, 0); each placement binding =
// (packed pose slot (SDF_TRANSFORM_SLOT_UNPACK), material, original shape instruction, 0). Geometry payloads and flags come from the canonical instructions, not the
// placement that first happened to render. KEEP IN SYNC with SdfProgram.PartPrograms.cs.
void sdfComposePartProgram(inout SdfHit parent, float3 worldPosition, uint4 part, uint dataOffset, int instanceIndex, bool trackMaterial, inout SdfDynamicRoot root) {
    SdfHit child;
    child.distance = SDF_FAR_DISTANCE;
    child.material = 0;
    child.instanceIndex = -1;
    child.frameSlot = SDF_TRANSFORM_SLOT_NONE;

    uint leafCount = part.z & SDF_PART_LEAF_COUNT_MASK;
    if (!sdfProgramRange(part.x, leafCount, 1u) || !sdfProgramRange(part.y, leafCount, 1u)) {
        parent = sdfIsaErrorHit();
        return;
    }

    [loop]
    for (uint leaf = 0u; leaf < leafCount; leaf++) {
        uint4 code = sdfProgramWord(part.x + leaf);
        if (code.x >= sdfProgramLayout.instructionCount || code.y > sdfProgramLayout.instructionCount) { parent = sdfIsaErrorHit(); return; }
        uint4 shape = sdfFieldHeader(code.x);
        uint4 binding = sdfProgramWord(part.y + leaf);
        float3 localPosition = worldPosition;
        int slot = SDF_TRANSFORM_SLOT_NONE;
#ifdef SDF_DYNAMIC_TRANSFORMS
        if (binding.x != SDF_TRANSFORM_SLOT_STATIC_WORD) {
            uint pose = (uint)SDF_TRANSFORM_SLOT_UNPACK(binding.x);
            slot = (int)pose;
            localPosition = sdfWorldPointInSlot(worldPosition, pose, root);
        }
#endif

        float distanceScale = 1.0;
        if (code.y != 0u) {
            uint domainIndex = code.y - 1u;
            uint4 domain = sdfFieldHeader(domainIndex);
            float4 data0 = sdfFieldData(dataOffset, domainIndex, 0u);
            // These operations retain the generic scalar walk's arithmetic order. The compiler admits at most one
            // domain op after the pose and before the primitive; arbitrary chains stay in the reference VM.
            switch (SDF_INSTRUCTION_OP(domain)) {
#include "../isa/sdf-field-parts.hlsli"
                default: parent = sdfIsaErrorHit(); return;
            }
        }

        float4 shapeData0 = sdfFieldData(dataOffset, code.x, 0u);
        float4 shapeData1 = sdfFieldData(dataOffset, code.x, 1u);
        float candidate = evaluateShape(SDF_INSTRUCTION_SHAPE(shape), localPosition, shapeData0, shapeData1) * distanceScale;
        sdfComposeCandidate(child, candidate, SDF_INSTRUCTION_BLEND(shape), trackMaterial ? (int)binding.y : 0,
            instanceIndex, slot, shapeData1.x, 0.0, trackMaterial);
    }

    // Same hard-union PopField semantics as the generic scalar walk, through its baked candidate scale.
    float candidate = child.distance * asfloat(part.w);
    sdfComposeCandidate(parent, candidate, SDF_BLEND_UNION, child.material, child.instanceIndex, child.frameSlot, 0.0, 0.0, trackMaterial);
}

#endif
