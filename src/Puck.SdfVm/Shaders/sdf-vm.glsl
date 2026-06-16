// The SDF virtual machine — a faithful subset of the legacy avatar-vm ISA. A program is a flat
// uvec4 word stream that map() interprets per sample point: transform opcodes mutate the evaluation
// point, SHAPE opcodes evaluate a primitive and blend it into the running nearest-surface result.
// The host binds the program SSBO (set 0 / binding 1 below) and supplies the camera + shading.
// KEEP IN SYNC with Puck.SdfVm (SdfOp/SdfShapeType/SdfBlendOp and the SdfProgram word layout).

// Program word stream. Layout (each element is one uvec4 = 16 bytes):
//   words[0]              = (instructionCount, materialCount, dataOffset, materialOffset)
//   words[1 .. 1+N)       = instruction headers (op, shapeType, blendOp, materialId)
//   words[dataOffset ..]  = instruction data, 2 uvec4 per instruction (data0, data1 as float bits)
//   words[matOffset ..]   = materials, 1 uvec4 each (albedo.rgb as float bits, w reserved)
layout(std430, set = 0, binding = 1) readonly buffer SdfProgramBuffer {
    uvec4 sdfWords[];
};

// --- opcodes (mirror Puck.Demo.Sdf.SdfOp / legacy AvatarSdfInstructionOp numbering) ---
#define SDF_OP_RESET           0u
#define SDF_OP_TRANSLATE       1u
#define SDF_OP_ROTATE          2u
#define SDF_OP_SCALE           3u
#define SDF_OP_SHAPE           9u
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

// Material sentinel: a SCREEN_SLAB shades to a procedural "screen" rather than a table albedo. The
// real per-view feed sampling arrives with the jumbotron phase. KEEP IN SYNC with SdfProgramBuilder.
#define SDF_SCREEN_MATERIAL 65535

struct SdfHit {
    float distance;
    int material;
};

vec3 rotatePointByInverseQuaternion(vec3 p, vec4 q) {
    vec3 u = -q.xyz;
    return (p + (2.0 * cross(u, ((q.w * p) + cross(u, p)))));
}

float sdfSphere(vec3 p, float radius) {
    return (length(p) - radius);
}
float sdfBox(vec3 p, vec3 halfExtents, float round) {
    vec3 q = (abs(p) - (halfExtents - round));
    return ((length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0)) - round);
}
float sdfTorus(vec3 p, float major, float minor) {
    vec2 q = vec2((length(p.xz) - major), p.y);
    return (length(q) - minor);
}
float sdfPlane(vec3 p, vec3 normal, float offset) {
    return (dot(p, normal) + offset);
}
float sdfRoundCone(vec3 p, float lowerRadius, float upperRadius, float height) {
    vec2 q = vec2(length(p.xz), p.y);
    float b = ((lowerRadius - upperRadius) / max(height, 0.0001));
    float a = sqrt(max(1.0 - (b * b), 0.0));
    float k = dot(q, vec2(-b, a));

    if (k < 0.0) {
        return (length(q) - lowerRadius);
    }

    if (k > (a * height)) {
        return (length(q - vec2(0.0, height)) - upperRadius);
    }

    return (dot(q, vec2(a, b)) - lowerRadius);
}

float evaluateShape(uint shapeType, vec3 p, vec4 data0) {
    switch (shapeType) {
        case SDF_SHAPE_SPHERE:     return sdfSphere(p, data0.x);
        case SDF_SHAPE_BOX:        return sdfBox(p, data0.xyz, data0.w);
        case SDF_SHAPE_SCREEN_SLAB:return sdfBox(p, data0.xyz, data0.w);
        case SDF_SHAPE_TORUS:      return sdfTorus(p, data0.x, data0.y);
        case SDF_SHAPE_PLANE:      return sdfPlane(p, normalize(data0.xyz), data0.w);
        case SDF_SHAPE_ROUND_CONE: return sdfRoundCone(p, data0.x, data0.y, data0.z);
    }

    return 1.0e9;
}
float blendShape(float current, float candidate, uint blendOp, float smoothRadius) {
    switch (blendOp) {
        case SDF_BLEND_SMOOTH_UNION: {
            float k = max(smoothRadius, 0.0001);
            float h = clamp((0.5 + ((0.5 * (candidate - current)) / k)), 0.0, 1.0);
            return (mix(candidate, current, h) - (((k * h) * (1.0 - h))));
        }
        case SDF_BLEND_SUBTRACTION:  return max(current, -candidate);
        case SDF_BLEND_INTERSECTION: return max(current, candidate);
    }

    return min(current, candidate);
}

SdfHit map(vec3 worldPosition) {
    uvec4 header = sdfWords[0];
    uint instructionCount = header.x;
    uint dataOffset = header.z;

    vec3 localPosition = worldPosition;
    float distanceScale = 1.0;
    SdfHit result;

    result.distance = 1.0e9;
    result.material = 0;

    for (uint index = 0u; (index < instructionCount); index++) {
        uvec4 instructionHeader = sdfWords[1u + index];
        uint op = instructionHeader.x;
        vec4 data0 = uintBitsToFloat(sdfWords[dataOffset + (2u * index)]);
        vec4 data1 = uintBitsToFloat(sdfWords[dataOffset + (2u * index) + 1u]);

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
                vec3 scale = max(abs(data0.xyz), vec3(0.0001));
                localPosition /= scale;
                distanceScale *= min(scale.x, min(scale.y, scale.z));
                break;
            }
            case SDF_OP_REPEAT: {
                vec3 spacing = max(data0.xyz, vec3(0.001));
                localPosition -= (spacing * round(localPosition / spacing));
                break;
            }
            case SDF_OP_REPEAT_LIMITED: {
                vec3 spacing = max(data0.xyz, vec3(0.001));
                localPosition -= (spacing * clamp(round(localPosition / spacing), -data1.xyz, data1.xyz));
                break;
            }
            case SDF_OP_SYMMETRY_X: { localPosition.x = abs(localPosition.x); break; }
            case SDF_OP_SYMMETRY_Y: { localPosition.y = abs(localPosition.y); break; }
            case SDF_OP_SYMMETRY_Z: { localPosition.z = abs(localPosition.z); break; }
            case SDF_OP_SHAPE: {
                uint shapeType = instructionHeader.y;
                uint blendOp = instructionHeader.z;
                int material = int(instructionHeader.w);
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

vec3 sdfMaterialAlbedo(int material) {
    uvec4 header = sdfWords[0];
    uint materialOffset = header.w;

    return uintBitsToFloat(sdfWords[materialOffset + uint(material)]).rgb;
}
