#ifndef PUCK_SDF_PARTS_HLSLI
#define PUCK_SDF_PARTS_HLSLI

// Root composition admission is rebuilt by SdfProgram.PartPrograms.cs. Header .x stores the compiled count
// in bits 0..30 and independent-tracing admission in bit 31. Beam and primary share this decision.
bool sdfCanTracePartsIndependently() {
#ifndef SDF_VM_DISABLE_PART_PROGRAMS
    uint table = sdfProgramLayout.partProgramOffset;
    return table != 0u && (sdfWords[table].x & 0x80000000u) != 0u;
#else
    return false;
#endif
}

// Compiled whole-scope programs: entry = (shared leaf run, placement binding run, count|dynamic flag, scope scale).
// Each leaf = (canonical shape instruction, optional domain instruction + 1, 0, 0); each placement binding =
// (pose slot + 1, material, 0, 0). Geometry payloads and flags come from the canonical instructions, not the
// placement that first happened to render. KEEP IN SYNC with SdfProgram.PartPrograms.cs.
void sdfComposePartProgram(inout SdfHit parent, float3 worldPosition, uint4 part, uint dataOffset, bool trackMaterial) {
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
    child.frameSlot = -1;

    [loop]
    for (uint leaf = 0u; leaf < (part.z & 0x7FFFFFFFu); leaf++) {
        uint4 code = sdfWords[part.x + leaf];
        uint4 shape = sdfWords[1u + code.x];
        if (!sdfShapeEnabled(shape.y)) {
            continue;
        }
        uint4 binding = sdfWords[part.y + leaf];
        float3 localPosition = worldPosition;
        float4 lanes = 0.0;
        int slot = -1;
#ifdef SDF_DYNAMIC_TRANSFORMS
        if (binding.x != 0u) {
            uint pose = binding.x - 1u;
            slot = (int)pose;
            float4 position = sdfDynamicTransforms[3u * pose];
            float4 orientation = sdfDynamicTransforms[3u * pose + 1u];
            localPosition = rotatePointByInverseQuaternion(worldPosition - position.xyz, orientation);
            if (trackMaterial) lanes = sdfDynamicTransforms[3u * pose + 2u];
        }
#endif

        float distanceScale = 1.0;
        if (code.y != 0u) {
            uint domainIndex = code.y - 1u;
            uint4 domain = sdfWords[1u + domainIndex];
            float4 data0 = asfloat(sdfWords[dataOffset + 2u * domainIndex]);
            // These operations retain the generic scalar walk's arithmetic order. The compiler admits at most one
            // domain op after the pose and before the primitive; arbitrary chains stay in the reference VM.
            if (domain.x == SDF_OP_SCALE) {
                localPosition /= data0.xyz;
                distanceScale *= data0.w;
            }
#ifndef SDF_STRIP_HEAVY
            else if (domain.x == SDF_OP_AXIAL_PROFILE) {
                float4 data1 = asfloat(sdfWords[dataOffset + 2u * domainIndex + 1u]);
                uint axis = domain.y;
                float rawT = (data0.z - localPosition[axis]) * data0.w;
                float t = saturate(rawT);
                float rawS = data1.y + data0.x * t + data0.y * sin(SDF_PI * t);
                float scale = max(rawS, SDF_FLARE_MIN_SCALE);
                float invScale = 1.0 / scale;
                [unroll] for (uint component = 0u; component < 3u; component++) {
                    if (component != axis) localPosition[component] *= invScale;
                }
                distanceScale *= data1.x;
            } else if (domain.x == SDF_OP_SHEAR) {
                uint target = domain.y, driver = domain.z;
                float t = localPosition[driver];
                localPosition[target] += ((data0.z * t + data0.y) * t + data0.x) * t;
            }
#endif
        }

        float4 shapeData0 = asfloat(sdfWords[dataOffset + 2u * code.x]);
        float4 shapeData1 = asfloat(sdfWords[dataOffset + 2u * code.x + 1u]);
        float candidate = evaluateShape(shape.y & SDF_SHAPE_TYPE_MASK, localPosition, shapeData0, shapeData1) * distanceScale;
        sdfComposeCandidate(child, candidate, shape.z, trackMaterial ? (int)binding.y : 0,
            lanes, slot, shapeData1.x, trackMaterial);
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
    sdfComposeCandidate(parent, candidate, SDF_BLEND_UNION, child.material, child.lanes, child.frameSlot, 0.0, trackMaterial);
    if (trackMaterial && wins) {
        sdfMaterialBlendWeight = childWeight;
        sdfMaterialBlendOther = childOther;
    }
}

#endif
