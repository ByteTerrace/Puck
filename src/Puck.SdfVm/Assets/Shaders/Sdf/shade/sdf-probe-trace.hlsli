// Software traversal of SdfMeshIrradianceScene.ExportGpu. KEEP IN SYNC with the CPU Walk and TriangleLeaves.
#ifndef SDF_PROBE_TRACE_HLSLI
#define SDF_PROBE_TRACE_HLSLI
#include "../isa/sdf-render.hlsli"
#include "../field/sdf-octahedral.hlsli"
#ifndef SDF_PROBE_RESULT_STRIDE
#error Probe traversal requires its entry point's generated result stride.
#endif

// Separate stacks for the top hierarchy and the geometry it enters. Depth-major addressing keeps a wave's
// pushes adjacent, and avoids dynamically indexed thread-local arrays on Vulkan.
groupshared uint sdfProbeTopStack[SDF_PROBE_STACK_DEPTH * SDF_PROBE_GROUP_SIZE];
groupshared uint sdfProbeGeometryStack[SDF_PROBE_STACK_DEPTH * SDF_PROBE_GROUP_SIZE];

// The service resets and reads the assertion word after this job's payload, independently of retained slot capacity.
void sdfProbeStackOverflow() {
    uint probes, stride;
    sdfProbeOrigins.GetDimensions(probes, stride);
    InterlockedAdd(sdfProbeHitsRW[probes * SDF_PROBE_RESULT_STRIDE], 1u);
}

float3 sdfProbeVector(uint word) {
    return asfloat(uint3(sdfProbeScene[word], sdfProbeScene[word + 1], sdfProbeScene[word + 2]));
}
float3 sdfProbeVertex(uint word) {
    return asfloat(uint3(sdfMeshRegion[word], sdfMeshRegion[word + 1], sdfMeshRegion[word + 2]));
}
bool sdfProbeSlab(uint node, float3 origin, float3 inverse, float maximum, out float entry) {
    precise float3 a = (sdfProbeVector(node) - origin) * inverse;
    precise float3 b = (sdfProbeVector(node + 3) - origin) * inverse;
    float3 near = min(a, b), far = max(a, b);
    entry = max(max(near.x, near.y), max(near.z, 0.0));
    float exit = min(min(far.x, far.y), min(far.z, maximum));
    return entry <= exit;
}
// The same near-first child order, including ties, as the CPU. Addresses are words, not node ordinals.
void sdfProbeChildren(uint node, float3 origin, float3 inverse, float maximum, out uint first, out uint second) {
    uint left = sdfProbeScene[node + 6] >> SDF_PROBE_NODE_COUNT_BITS;
    uint right = left + SDF_PROBE_NODE_WORDS;
    float a, b;
    bool hitA = sdfProbeSlab(left, origin, inverse, maximum, a);
    bool hitB = sdfProbeSlab(right, origin, inverse, maximum, b);
    first = 0; second = 0;
    if (hitA && hitB) { first = a <= b ? left : right; second = a <= b ? right : left; }
    else if (hitA) { first = left; }
    else if (hitB) { first = right; }
}
float3 sdfProbeTransform(uint instance, float3 value, bool isPoint) {
    precise float3 result = (value.x * sdfProbeVector(instance)) + (value.y * sdfProbeVector(instance + 4));
    result = result + value.z * sdfProbeVector(instance + 8);
    if (isPoint) { result = result + sdfProbeVector(instance + 12); }
    return result;
}
void sdfProbeTriangle(uint instance, uint primitive, out float3 v0, out float3 e1, out float3 e2) {
    uint index = sdfProbeScene[instance + SDF_PROBE_GEOMETRY_WORD] + 3 * primitive;
    uint vertex = sdfProbeScene[instance + SDF_PROBE_GEOMETRY_WORD + 1];
    v0 = sdfProbeVertex(vertex + SDF_MESH_VERTEX_WORDS * sdfMeshRegion[index]);
    e1 = sdfProbeVertex(vertex + SDF_MESH_VERTEX_WORDS * sdfMeshRegion[index + 1]) - v0;
    e2 = sdfProbeVertex(vertex + SDF_MESH_VERTEX_WORDS * sdfMeshRegion[index + 2]) - v0;
}
bool sdfProbeTriangleHit(float3 origin, float3 direction, float3 v0, float3 e1, float3 e2, float maximum, out float distance) {
    distance = 0;
    precise float3 p = cross(direction, e2);
    precise float determinant = dot(e1, p);
    if (abs(determinant) < 1.0e-12) { return false; }
    precise float inverse = 1.0 / determinant;
    precise float3 s = origin - v0;
    precise float u = dot(s, p) * inverse;
    if (u < 0 || u > 1) { return false; }
    precise float3 q = cross(s, e1);
    precise float v = dot(direction, q) * inverse;
    if (v < 0 || u + v > 1) { return false; }
    distance = dot(e2, q) * inverse;
    return distance > 1.0e-4 && distance < maximum;
}
// One walk for nearest-hit and boolean visibility. The latter ends at its first accepted two-sided triangle.
bool sdfProbeWalk(float3 origin, float3 direction, float maximum, uint lane, bool anyHit, out float distance, out float3 normal, out uint material, out uint2 identity) {
    distance = maximum; normal = 0; material = 0;
    identity = uint2(0xffffffffu, 0xffffffffu);
    uint root = sdfProbeScene[0];
    float3 inverse = 1.0 / direction;
    float entryDistance;
    if (root == 0 || !sdfProbeSlab(root, origin, inverse, maximum, entryDistance)) { return false; }
    uint top = 1, hitInstance = 0, hitTriangle = 0;
    sdfProbeTopStack[lane] = root;
    [loop] while (top > 0) {
        uint node = sdfProbeTopStack[(--top) * SDF_PROBE_GROUP_SIZE + lane];
        uint link = sdfProbeScene[node + 6];
        uint count = link & ((1u << SDF_PROBE_NODE_COUNT_BITS) - 1u);
        if (count == 0) {
            uint first, second;
            sdfProbeChildren(node, origin, inverse, distance, first, second);
            if (top + (first != 0 ? 1u : 0u) + (second != 0 ? 1u : 0u) > SDF_PROBE_STACK_DEPTH) {
                sdfProbeStackOverflow();
                return anyHit;
            }
            if (second != 0) { sdfProbeTopStack[(top++) * SDF_PROBE_GROUP_SIZE + lane] = second; }
            if (first != 0) { sdfProbeTopStack[(top++) * SDF_PROBE_GROUP_SIZE + lane] = first; }
            continue;
        }
        uint entry = link >> SDF_PROBE_NODE_COUNT_BITS;
        [loop] for (uint i = 0; i < count; i++) {
            uint instance = sdfProbeScene[entry + i * SDF_PROBE_ENTRY_WORDS];
            bool identity = sdfProbeScene[instance + SDF_PROBE_IDENTITY_WORD] != 0;
            float3 localOrigin = identity ? origin : sdfProbeTransform(instance, origin, true);
            float3 localDirection = identity ? direction : sdfProbeTransform(instance, direction, false);
            float3 localInverse = identity ? inverse : 1.0 / localDirection;
            uint depth = 1;
            sdfProbeGeometryStack[lane] = sdfProbeScene[entry + i * SDF_PROBE_ENTRY_WORDS + 1];
            [loop] while (depth > 0) {
                uint inner = sdfProbeGeometryStack[(--depth) * SDF_PROBE_GROUP_SIZE + lane];
                uint innerLink = sdfProbeScene[inner + 6];
                uint innerCount = innerLink & ((1u << SDF_PROBE_NODE_COUNT_BITS) - 1u);
                uint innerFirst = innerLink >> SDF_PROBE_NODE_COUNT_BITS;
                if (innerCount == 0) {
                    uint first, second;
                    sdfProbeChildren(inner, localOrigin, localInverse, distance, first, second);
                    if (depth + (first != 0 ? 1u : 0u) + (second != 0 ? 1u : 0u) > SDF_PROBE_STACK_DEPTH) {
                        sdfProbeStackOverflow();
                        return anyHit;
                    }
                    if (second != 0) { sdfProbeGeometryStack[(depth++) * SDF_PROBE_GROUP_SIZE + lane] = second; }
                    if (first != 0) { sdfProbeGeometryStack[(depth++) * SDF_PROBE_GROUP_SIZE + lane] = first; }
                    continue;
                }
                [loop] for (uint t = 0; t < innerCount; t++) {
                    uint primitive = sdfProbeScene[innerFirst + t];
                    float3 v0, e1, e2;
                    sdfProbeTriangle(instance, primitive, v0, e1, e2);
                    float hit;
                    if (sdfProbeTriangleHit(localOrigin, localDirection, v0, e1, e2, distance, hit)) {
                        if (anyHit) { return true; }
                        distance = hit; hitInstance = instance; hitTriangle = primitive;
                    }
                }
            }
        }
    }
    if (hitInstance == 0) { return false; }
    identity = uint2(sdfProbeScene[hitInstance + SDF_PROBE_INSTANCE_INDEX_WORD], hitTriangle);
    float3 v0, e1, e2;
    sdfProbeTriangle(hitInstance, hitTriangle, v0, e1, e2);
    precise float3 n = cross(e1, e2);
    if (sdfProbeScene[hitInstance + SDF_PROBE_IDENTITY_WORD] == 0) {
        uint matrix = hitInstance + SDF_PROBE_NORMAL_WORD;
        n = (n.x * sdfProbeVector(matrix) + n.y * sdfProbeVector(matrix + 3)) + n.z * sdfProbeVector(matrix + 6);
    }
    precise float length = sqrt(dot(n, n));
    normal = length > 0 ? n / length : float3(0, 1, 0);
    uint materials = sdfProbeScene[hitInstance + SDF_PROBE_GEOMETRY_WORD + 2];
    material = sdfProbeScene[hitInstance + SDF_PROBE_GEOMETRY_WORD + 3] + (materials == 0xffffffffu ? 0 : sdfMeshRegion[materials + hitTriangle]);
    return true;
}
bool sdfProbeIntersect(float3 origin, float3 direction, float maximum, uint lane, out float distance, out float3 normal, out uint material, out uint2 identity) {
    return sdfProbeWalk(origin, direction, maximum, lane, false, distance, normal, material, identity);
}
bool sdfProbeOccluded(float3 origin, float3 direction, float maximum, uint lane) {
    float distance; float3 normal; uint material; uint2 identity;
    return sdfProbeWalk(origin, direction, maximum, lane, true, distance, normal, material, identity);
}
uint2 sdfProbeHit(float3 origin, float3 direction, float maximum, uint lane, out uint2 identity) {
    float distance; float3 normal; uint material;
    if (!sdfProbeIntersect(origin, direction, maximum, lane, distance, normal, material, identity)) { return uint2(0x7f800000u, 0); }
    bool back = dot(normal, direction) > 0;
    if (back) { normal = -normal; }
    int2 packed = (int2)round(sdfOctEncode(normal) * SDF_MESH_IRRADIANCE_DEPTH_NORMAL_SCALE);
    uint word = ((uint(packed.x) & 255u) << 16) | ((uint(packed.y) & 255u) << 24);
    word |= material < SDF_PROBE_UNKNOWN_MATERIAL ? material : SDF_PROBE_UNKNOWN_MATERIAL;
    return uint2(asuint(back ? -distance : distance), word);
}
#endif
