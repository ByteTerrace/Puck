// The SDF virtual machine — HLSL port of sdf-vm.glsl (dual-source; the GLSL stays the Vulkan/SPIR-V form).
// A program is a flat uint4 word stream that map() interprets per sample point: transform opcodes mutate the
// evaluation point, SHAPE opcodes evaluate a primitive and blend it into the running nearest-surface result.
// KEEP IN SYNC with sdf-vm.glsl and Puck.SdfVm (SdfOp/SdfShapeType/SdfBlendOp and the SdfProgram word layout).
#ifndef SDF_VM_HLSLI
#define SDF_VM_HLSLI

// Program word stream (each element one uint4 = 16 bytes). A read-only StructuredBuffer. On Vulkan it is the
// storage buffer at set 0, binding 1 (what the backend's descriptor layout expects); on DirectX it is an SRV at
// t0 (DirectXGpuPipelineFactory's storage-buffer SRV slot — the program is never written, the buffer is on an
// upload heap where UAVs are invalid, and an SRV avoids the pixel-shader u0/render-target clash). Layout:
//   words[0]              = (instructionCount, materialCount, dataOffset, materialOffset)
//   words[1 .. 1+N)       = instruction headers (op, shapeType, blendOp, materialId)
//   words[dataOffset ..]  = instruction data, 2 uint4 per instruction (data0, data1 as float bits)
//   words[matOffset ..]   = materials, 1 uint4 each (albedo.rgb as float bits, w reserved)
[[vk::binding(1, 0)]] StructuredBuffer<uint4> sdfWords : register(t0);

#ifdef SDF_DYNAMIC_TRANSFORMS
// Per-frame dynamic entity transforms (the world render path only). Each moving entity (player/enemy/carried screen)
// owns a slot: element 2*slot is its world position (xyz), 2*slot+1 its orientation quaternion (xyzw). The
// SDF_OP_TRANSFORM_DYNAMIC opcode reads its rigid transform from here by slot index, so an entity moves by updating
// this small buffer instead of re-uploading the static program — the same way the camera moves via the per-frame
// viewport table. register(t2) follows the program (t0) and the world viewport table (t1, in sdf-world.hlsli).
[[vk::binding(9, 0)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t2);
#endif

// --- opcodes (mirror Puck.Demo.Sdf.SdfOp / legacy AvatarSdfInstructionOp numbering) ---
#define SDF_OP_RESET             0u
#define SDF_OP_TRANSLATE         1u
#define SDF_OP_ROTATE            2u
#define SDF_OP_SCALE             3u
#define SDF_OP_TRANSFORM_DYNAMIC 4u
#define SDF_OP_SHAPE             9u
#define SDF_OP_REPEAT          11u
#define SDF_OP_REPEAT_LIMITED  12u
#define SDF_OP_SYMMETRY_X      13u
#define SDF_OP_SYMMETRY_Y      14u
#define SDF_OP_SYMMETRY_Z      15u

// --- primitives ---
#define SDF_SHAPE_BOX          0u
#define SDF_SHAPE_SPHERE       2u
#define SDF_SHAPE_TORUS        3u
#define SDF_SHAPE_PLANE        5u
#define SDF_SHAPE_ROUND_CONE   11u
#define SDF_SHAPE_SCREEN_SLAB  14u

// --- blend operators ---
#define SDF_BLEND_UNION         0u
#define SDF_BLEND_SMOOTH_UNION  1u
#define SDF_BLEND_SUBTRACTION   2u
#define SDF_BLEND_INTERSECTION  3u

// Material sentinel: a SCREEN_SLAB shades to a procedural "screen" rather than a table albedo.
#define SDF_SCREEN_MATERIAL 65535

struct SdfHit {
    float distance;
    int material;
};

float3 rotatePointByInverseQuaternion(float3 p, float4 q) {
    float3 u = -q.xyz;
    return (p + (2.0 * cross(u, ((q.w * p) + cross(u, p)))));
}

float sdfSphere(float3 p, float radius) {
    return (length(p) - radius);
}
float sdfBox(float3 p, float3 halfExtents, float round) {
    float3 q = (abs(p) - (halfExtents - round));
    return ((length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0)) - round);
}
float sdfTorus(float3 p, float major, float minor) {
    float2 q = float2((length(p.xz) - major), p.y);
    return (length(q) - minor);
}
float sdfPlane(float3 p, float3 normal, float offset) {
    return (dot(p, normal) + offset);
}
float sdfRoundCone(float3 p, float lowerRadius, float upperRadius, float height) {
    float2 q = float2(length(p.xz), p.y);
    float b = ((lowerRadius - upperRadius) / max(height, 0.0001));
    float a = sqrt(max(1.0 - (b * b), 0.0));
    float k = dot(q, float2(-b, a));

    if (k < 0.0) {
        return (length(q) - lowerRadius);
    }

    if (k > (a * height)) {
        return (length(q - float2(0.0, height)) - upperRadius);
    }

    return (dot(q, float2(a, b)) - lowerRadius);
}

// Single-return (a result variable rather than returning inside the switch) so the compiler's flow analysis
// sees the value is always initialized — no "potentially uninitialized" warning.
float evaluateShape(uint shapeType, float3 p, float4 data0) {
    float result = 1.0e9;

    switch (shapeType) {
        case SDF_SHAPE_SPHERE:      result = sdfSphere(p, data0.x); break;
        case SDF_SHAPE_BOX:         result = sdfBox(p, data0.xyz, data0.w); break;
        case SDF_SHAPE_SCREEN_SLAB: result = sdfBox(p, data0.xyz, data0.w); break;
        case SDF_SHAPE_TORUS:       result = sdfTorus(p, data0.x, data0.y); break;
        case SDF_SHAPE_PLANE:       result = sdfPlane(p, normalize(data0.xyz), data0.w); break;
        case SDF_SHAPE_ROUND_CONE:  result = sdfRoundCone(p, data0.x, data0.y, data0.z); break;
    }

    return result;
}
float blendShape(float current, float candidate, uint blendOp, float smoothRadius) {
    float result = min(current, candidate); // SDF_BLEND_UNION (the default)

    switch (blendOp) {
        case SDF_BLEND_SMOOTH_UNION: {
            float k = max(smoothRadius, 0.0001);
            float h = clamp((0.5 + ((0.5 * (candidate - current)) / k)), 0.0, 1.0);
            result = (lerp(candidate, current, h) - ((k * h) * (1.0 - h)));
            break;
        }
        case SDF_BLEND_SUBTRACTION:  result = max(current, -candidate); break;
        case SDF_BLEND_INTERSECTION: result = max(current, candidate); break;
    }

    return result;
}

SdfHit map(float3 worldPosition) {
    uint4 header = sdfWords[0];
    uint instructionCount = header.x;
    uint dataOffset = header.z;

    float3 localPosition = worldPosition;
    float distanceScale = 1.0;
    SdfHit result;

    result.distance = 1.0e9;
    result.material = 0;

    [loop]
    for (uint index = 0u; (index < instructionCount); index++) {
        uint4 instructionHeader = sdfWords[1u + index];
        uint op = instructionHeader.x;
        float4 data0 = asfloat(sdfWords[dataOffset + (2u * index)]);
        float4 data1 = asfloat(sdfWords[dataOffset + (2u * index) + 1u]);

        switch (op) {
            case SDF_OP_RESET: {
                localPosition = worldPosition;
                distanceScale = 1.0;
                break;
            }
            case SDF_OP_TRANSLATE: {
                localPosition -= data0.xyz;
                break;
            }
            case SDF_OP_ROTATE: {
                localPosition = rotatePointByInverseQuaternion(localPosition, data0);
                break;
            }
            case SDF_OP_SCALE: {
                float3 scale = max(abs(data0.xyz), float3(0.0001, 0.0001, 0.0001));
                localPosition /= scale;
                distanceScale *= min(scale.x, min(scale.y, scale.z));
                break;
            }
#ifdef SDF_DYNAMIC_TRANSFORMS
            case SDF_OP_TRANSFORM_DYNAMIC: {
                // A rigid transform sourced from a per-frame buffer slot (data0.x): place the shape at the slot's world
                // position + orientation by moving the sample point into the shape's local frame (translate then
                // inverse-rotate), exactly like an immediate Translate + Rotate would.
                uint dynamicSlot = (uint)data0.x;
                float4 dynamicPosition = sdfDynamicTransforms[(2u * dynamicSlot)];
                float4 dynamicOrientation = sdfDynamicTransforms[((2u * dynamicSlot) + 1u)];
                localPosition = rotatePointByInverseQuaternion((localPosition - dynamicPosition.xyz), dynamicOrientation);
                break;
            }
#endif
            case SDF_OP_REPEAT: {
                float3 spacing = max(data0.xyz, float3(0.001, 0.001, 0.001));
                localPosition -= (spacing * round(localPosition / spacing));
                break;
            }
            case SDF_OP_REPEAT_LIMITED: {
                float3 spacing = max(data0.xyz, float3(0.001, 0.001, 0.001));
                localPosition -= (spacing * clamp(round(localPosition / spacing), -data1.xyz, data1.xyz));
                break;
            }
            case SDF_OP_SYMMETRY_X: { localPosition.x = abs(localPosition.x); break; }
            case SDF_OP_SYMMETRY_Y: { localPosition.y = abs(localPosition.y); break; }
            case SDF_OP_SYMMETRY_Z: { localPosition.z = abs(localPosition.z); break; }
            case SDF_OP_SHAPE: {
                uint shapeType = instructionHeader.y;
                uint blendOp = instructionHeader.z;
                int material = (int)instructionHeader.w;
                float candidate = (evaluateShape(shapeType, localPosition, data0) * distanceScale);

                if ((blendOp != SDF_BLEND_SUBTRACTION) && (candidate < result.distance)) {
                    result.material = material;
                }

                result.distance = blendShape(result.distance, candidate, blendOp, data1.x);
                break;
            }
        }
    }

    return result;
}

float3 sdfMaterialAlbedo(int material) {
    uint4 header = sdfWords[0];
    uint materialOffset = header.w;

    return asfloat(sdfWords[materialOffset + (uint)material]).rgb;
}

#endif
