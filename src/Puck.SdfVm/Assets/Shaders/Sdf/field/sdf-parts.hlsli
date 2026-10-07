#ifndef PUCK_SDF_PARTS_HLSLI
#define PUCK_SDF_PARTS_HLSLI

// Root composition admission is rebuilt by SdfProgram.PartPrograms.cs. Header .x stores the compiled count
// in bits 0..30 and independent-tracing admission in bit 31. Beam and primary share this decision.
bool sdfCanTracePartsIndependently() {
#ifndef SDF_VM_DISABLE_PART_PROGRAMS
    uint table = sdfProgramLayout.partProgramOffset;
    return table != 0u && (sdfProgramWord(table).x & 0x80000000u) != 0u;
#else
    return false;
#endif
}

// Compiled whole-scope programs: entry = (shared leaf run, placement binding run, count|dynamic flag, scope scale).
// Each leaf = (canonical shape instruction, optional domain instruction + 1, 0, 0); each placement binding =
// (packed pose slot (SDF_TRANSFORM_SLOT_UNPACK), material, original shape instruction, 0). Geometry payloads and flags come from the canonical instructions, not the
// placement that first happened to render. KEEP IN SYNC with SdfProgram.PartPrograms.cs.
void sdfComposePartProgram(inout SdfHit parent, float3 worldPosition, uint4 part, uint dataOffset, int instanceIndex, bool trackMaterial) {
    float parentWeight = sdfMaterialBlendWeight;
    int parentOther = sdfMaterialBlendOther;
    if (trackMaterial) {
        sdfMaterialBlendWeight = 0.0;
        sdfMaterialBlendOther = 0;
    }

    SdfHit child;
    child.distance = SDF_FAR_DISTANCE;
    child.material = 0;
    child.lanes = 0.0;
    child.instanceIndex = -1;
    child.frameSlot = SDF_TRANSFORM_SLOT_NONE;

    uint leafCount = part.z & 0x7FFFFFFFu;
    if (!sdfProgramRange(part.x, leafCount, 1u) || !sdfProgramRange(part.y, leafCount, 1u)) {
        parent = sdfIsaErrorHit();
        return;
    }

    [loop]
    for (uint leaf = 0u; leaf < leafCount; leaf++) {
        uint4 code = sdfProgramWord(part.x + leaf);
        if (code.x >= sdfProgramLayout.instructionCount || code.y > sdfProgramLayout.instructionCount) { parent = sdfIsaErrorHit(); return; }
        uint4 shape = sdfProgramWord(SDF_PROGRAM_HEADER_VECTORS + code.x);
        if (!sdfShapeEnabled(SDF_INSTRUCTION_SHAPE(shape))) {
            continue;
        }
        uint4 binding = sdfProgramWord(part.y + leaf);
        if (!sdfTapeShapeLive(binding.z)) { sdfTapeSkippedShape(shape); continue; }
        float3 localPosition = worldPosition;
        float4 lanes = 0.0;
        int slot = SDF_TRANSFORM_SLOT_NONE;
#ifdef SDF_DYNAMIC_TRANSFORMS
        if (binding.x != SDF_TRANSFORM_SLOT_STATIC_WORD) {
            uint pose = (uint)SDF_TRANSFORM_SLOT_UNPACK(binding.x);
            slot = (int)pose;
            float4 position = sdfDynamicTransformRow(3u * pose);
            float4 orientation = sdfDynamicTransformRow(3u * pose + 1u);
            localPosition = rotatePointByInverseQuaternion(worldPosition - position.xyz, orientation);
            if (trackMaterial) lanes = sdfDynamicTransformRow(3u * pose + 2u);
        }
#endif

        float distanceScale = 1.0;
        if (code.y != 0u) {
            uint domainIndex = code.y - 1u;
            uint4 domain = sdfProgramWord(SDF_PROGRAM_HEADER_VECTORS + domainIndex);
            float4 data0 = asfloat(sdfProgramWord(dataOffset + SDF_INSTRUCTION_DATA_VECTORS * domainIndex));
            // These operations retain the generic scalar walk's arithmetic order. The compiler admits at most one
            // domain op after the pose and before the primitive; arbitrary chains stay in the reference VM.
            if (SDF_INSTRUCTION_OP(domain) == SDF_OP_SCALE) {
                localPosition /= data0.xyz;
                distanceScale *= data0.w;
            }
#ifndef SDF_STRIP_HEAVY
            else if (SDF_INSTRUCTION_OP(domain) == SDF_OP_AXIAL_PROFILE) {
                float4 data1 = asfloat(sdfProgramWord(dataOffset + SDF_INSTRUCTION_DATA_VECTORS * domainIndex + 1u));
                uint axis = SDF_INSTRUCTION_SHAPE(domain);
                if (axis >= 3u) { parent = sdfIsaErrorHit(); return; }
                float rawT = (data0.z - localPosition[axis]) * data0.w;
                float t = saturate(rawT);
                float rawS = data1.y + data0.x * t + data0.y * sin(SDF_PI * t);
                float scale = max(rawS, SDF_FLARE_MIN_SCALE);
                float invScale = 1.0 / scale;
                [unroll] for (uint component = 0u; component < 3u; component++) {
                    if (component != axis) localPosition[component] *= invScale;
                }
                distanceScale *= data1.x;
            } else if (SDF_INSTRUCTION_OP(domain) == SDF_OP_SHEAR) {
                uint target = SDF_INSTRUCTION_SHAPE(domain), driver = SDF_INSTRUCTION_BLEND(domain);
                if (target >= 3u || driver >= 3u) { parent = sdfIsaErrorHit(); return; }
                float t = localPosition[driver];
                localPosition[target] += ((data0.z * t + data0.y) * t + data0.x) * t;
            }
#endif
        }

        float4 shapeData0 = asfloat(sdfProgramWord(dataOffset + SDF_INSTRUCTION_DATA_VECTORS * code.x));
        float4 shapeData1 = asfloat(sdfProgramWord(dataOffset + SDF_INSTRUCTION_DATA_VECTORS * code.x + 1u));
        float candidate = evaluateShape(SDF_INSTRUCTION_SHAPE(shape) & SDF_SHAPE_TYPE_MASK, localPosition, shapeData0, shapeData1) * distanceScale;
        if (sdfTapeDecided(binding.z)) { child.distance = sdfTapeWinnerSeed(SDF_INSTRUCTION_BLEND(shape)); }
        sdfComposeCandidate(child, candidate, SDF_INSTRUCTION_BLEND(shape), trackMaterial ? (int)binding.y : 0,
            lanes, instanceIndex, slot, shapeData1.x, trackMaterial);
    }

    // Same hard-union PopField semantics as the generic scalar walk. Losing scopes retain the parent's seam;
    // a winning scope carries its internal material seam through its baked candidate scale.
    float candidate = child.distance * asfloat(part.w);
    bool wins = candidate < parent.distance;
    float childWeight = sdfMaterialBlendWeight;
    int childOther = sdfMaterialBlendOther;
    if (trackMaterial) {
        sdfMaterialBlendWeight = parentWeight;
        sdfMaterialBlendOther = parentOther;
    }
    sdfComposeCandidate(parent, candidate, SDF_BLEND_UNION, child.material, child.lanes, child.instanceIndex, child.frameSlot, 0.0, trackMaterial);
    if (trackMaterial && wins) {
        sdfMaterialBlendWeight = childWeight;
        sdfMaterialBlendOther = childOther;
    }
}

#endif
