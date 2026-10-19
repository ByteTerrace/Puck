// The mesh pass's and the surface and views passes' reads of the mesh region, SdfMeshRegion's raw word layout: one
// thirty-three-word record a draw (its row-vector object-to-world matrix row by row, its material, the word its first
// index sits at, its index count, the word its first vertex sits at, its attribute flags, the word its first triangle
// material sits at, its normal matrix, the inverse transpose of the matrix's upper 3x3, row by row, the dynamic-transform
// slot whose frame its mesh is stored in, or -1, and the word its mesh's members sit at), then the vertices, six words
// each (position, normal), then the triangle materials, then the indices, then each mesh's members. KEEP IN SYNC with
// SdfMeshRegion.Write. The includer's interface declares the region, sdfMeshRegion, and the frame's dynamic transforms,
// sdfDynamicTransforms: sdf-world and sdf-mesh both do.
#ifndef SDF_MESH_HLSLI
#define SDF_MESH_HLSLI
#include "../field/sdf-quaternion.hlsli"

static const uint SdfMeshDrawWords = 33u;
static const uint SdfMeshFrameSlotWord = 31u;
static const uint SdfMeshSkinWord = 32u;
static const uint SdfMeshMemberWords = 8u;
static const uint SdfMeshMaterialWord = 16u;
static const uint SdfMeshIndexWord = 17u;
static const uint SdfMeshIndexCountWord = 18u;
static const uint SdfMeshVertexWord = 19u;
static const uint SdfMeshFlagsWord = 20u;
static const uint SdfMeshTriangleMaterialWord = 21u;
static const uint SdfMeshNormalMatrixWord = 22u;
static const uint SdfMeshVertexWords = 6u;
// A record's attribute flags: its mesh carries a normal per vertex, a palette entry per triangle and a member per vertex.
// KEEP IN SYNC with SdfMeshRegion.NormalsFlag, MaterialsFlag and SkinFlag.
static const uint SdfMeshNormalsFlag = 1u;
static const uint SdfMeshMaterialsFlag = 2u;
static const uint SdfMeshSkinFlag = 4u;
// The bit the view starts at in the index a mesh draw call pushes; the bits below it name the draw. KEEP IN SYNC with
// SdfWorldInterfaces.MeshViewShift.
static const uint SdfMeshViewShift = 24u;
static const uint SdfMeshDrawMask = ((1u << SdfMeshViewShift) - 1u);

// The first word of a draw's record.
uint sdfMeshRecord(uint draw) {
    return (draw * SdfMeshDrawWords);
}

// The material a draw's triangle shades with: the draw's material, plus the triangle's palette entry when its mesh
// carries one.
int sdfMeshMaterial(uint draw, uint triangleIndex) {
    uint record = sdfMeshRecord(draw);
    int material = asint(sdfMeshRegion[(record + SdfMeshMaterialWord)]);

    if ((sdfMeshRegion[(record + SdfMeshFlagsWord)] & SdfMeshMaterialsFlag) != 0u) {
        material += asint(sdfMeshRegion[(sdfMeshRegion[(record + SdfMeshTriangleMaterialWord)] + triangleIndex)]);
    }

    return material;
}
// The vertex a draw's index `k` names.
uint sdfMeshIndexAt(uint record, uint k) {
    return sdfMeshRegion[(sdfMeshRegion[(record + SdfMeshIndexWord)] + k)];
}
// The first word of the vertex a draw's index `k` names.
uint sdfMeshVertexAt(uint record, uint k) {
    return (sdfMeshRegion[(record + SdfMeshVertexWord)] + (SdfMeshVertexWords * sdfMeshIndexAt(record, k)));
}
// The first word of the member a draw's vertex rides (its slot offset, then its baked position and orientation), or zero
// for a vertex that rides the draw's matrix: a baked group's shape on a slot of its own moves its vertices as that slot
// moves, so they follow it from where it stood when the mesh was baked (SdfMesh.Members).
uint sdfMeshMemberEntry(uint record, uint vertex) {
    if ((sdfMeshRegion[(record + SdfMeshFlagsWord)] & SdfMeshSkinFlag) == 0u) {
        return 0u;
    }

    uint skin = sdfMeshRegion[(record + SdfMeshSkinWord)];
    uint member = sdfMeshRegion[(((skin + 1u) + (SdfMeshMemberWords * sdfMeshRegion[skin])) + vertex)];

    return ((member == 0xFFFFFFFFu) ? 0u : ((skin + 1u) + (SdfMeshMemberWords * member)));
}
// The slot a member rides: the draw's frame slot plus the member's offset.
uint sdfMeshMemberSlot(uint record, uint entry) {
    return (uint)(asint(sdfMeshRegion[(record + SdfMeshFrameSlotWord)]) + asint(sdfMeshRegion[entry]));
}
// A point or direction in a mesh's object space carried into the frame of the member at `entry`, by the inverse of where
// the member stood when the mesh was baked.
float3 sdfMeshMemberLocal(uint entry, float3 v, bool isPoint) {
    float3 position = asfloat(uint3(sdfMeshRegion[(entry + 1u)], sdfMeshRegion[(entry + 2u)], sdfMeshRegion[(entry + 3u)]));
    float4 orientation = asfloat(uint4(sdfMeshRegion[(entry + 4u)], sdfMeshRegion[(entry + 5u)], sdfMeshRegion[(entry + 6u)], sdfMeshRegion[(entry + 7u)]));

    return rotatePointByInverseQuaternion((isPoint ? (v - position) : v), orientation);
}
// One row of a draw's matrix.
float3 sdfMeshRow(uint record, uint row) {
    uint word = (record + (4u * row));

    return asfloat(uint3(sdfMeshRegion[word], sdfMeshRegion[(word + 1u)], sdfMeshRegion[(word + 2u)]));
}
// A direction in a draw's object space, under its matrix without the translation (v * M, the row-vector convention).
float3 sdfMeshWorldDirection(uint record, float3 v) {
    return (((v.x * sdfMeshRow(record, 0u)) + (v.y * sdfMeshRow(record, 1u))) + (v.z * sdfMeshRow(record, 2u)));
}
// The object position of the vertex a draw's index `k` names.
float3 sdfMeshObjectPosition(uint record, uint k) {
    uint word = sdfMeshVertexAt(record, k);
    return asfloat(uint3(sdfMeshRegion[word], sdfMeshRegion[(word + 1u)], sdfMeshRegion[(word + 2u)]));
}
// Its world position: under the draw's row-vector matrix, whose fourth row carries translation, or, for a vertex riding a
// member, under that member's slot in this frame.
float3 sdfMeshWorldPosition(uint record, uint k) {
    float3 object = sdfMeshObjectPosition(record, k);
    uint entry = sdfMeshMemberEntry(record, sdfMeshIndexAt(record, k));

    if (entry != 0u) {
        uint slot = sdfMeshMemberSlot(record, entry);

        return (rotatePointByQuaternion(sdfMeshMemberLocal(entry, object, true), sdfDynamicTransforms[((3u * slot) + 1u)]) + sdfDynamicTransforms[(3u * slot)].xyz);
    }

    return (sdfMeshWorldDirection(record, object) + sdfMeshRow(record, 3u));
}
// An object-space normal under a draw's normal matrix (the inverse transpose of its matrix, so a nonuniform scale keeps
// the normal perpendicular to the surface).
float3 sdfMeshNormalToWorld(uint record, float3 n) {
    uint rows = (record + SdfMeshNormalMatrixWord);
    float3 row0 = asfloat(uint3(sdfMeshRegion[rows], sdfMeshRegion[(rows + 1u)], sdfMeshRegion[(rows + 2u)]));
    float3 row1 = asfloat(uint3(sdfMeshRegion[(rows + 3u)], sdfMeshRegion[(rows + 4u)], sdfMeshRegion[(rows + 5u)]));
    float3 row2 = asfloat(uint3(sdfMeshRegion[(rows + 6u)], sdfMeshRegion[(rows + 7u)], sdfMeshRegion[(rows + 8u)]));

    return (((n.x * row0) + (n.y * row1)) + (n.z * row2));
}

// The world normal of a draw's index `k`: the normal the vertex it names carries, under the draw's normal matrix, or
// turned with its member's slot for a vertex riding one. Meaningful only for a mesh that carries normals.
float3 sdfMeshWorldNormal(uint record, uint k) {
    uint word = (sdfMeshVertexAt(record, k) + 3u);
    float3 normal = asfloat(uint3(sdfMeshRegion[word], sdfMeshRegion[(word + 1u)], sdfMeshRegion[(word + 2u)]));
    uint entry = sdfMeshMemberEntry(record, sdfMeshIndexAt(record, k));

    if (entry != 0u) {
        return rotatePointByQuaternion(sdfMeshMemberLocal(entry, normal, false), sdfDynamicTransforms[((3u * sdfMeshMemberSlot(record, entry)) + 1u)]);
    }

    return sdfMeshNormalToWorld(record, normal);
}
// The barycentric weights of a point on the triangle (a, b, c), in that order, or false for a triangle with no area.
bool sdfMeshBarycentric(float3 a, float3 b, float3 c, float3 p, out float3 weights) {
    float3 ab = (b - a);
    float3 ac = (c - a);
    float3 ap = (p - a);
    float d00 = dot(ab, ab);
    float d01 = dot(ab, ac);
    float d11 = dot(ac, ac);
    float d20 = dot(ap, ab);
    float d21 = dot(ap, ac);
    float denominator = ((d00 * d11) - (d01 * d01));

    weights = float3((1.0 / 3.0), (1.0 / 3.0), (1.0 / 3.0));

    if (abs(denominator) <= 1.0e-20) {
        return false;
    }

    float v = (((d11 * d20) - (d01 * d21)) / denominator);
    float w = (((d00 * d21) - (d01 * d20)) / denominator);

    weights = float3(((1.0 - v) - w), v, w);

    return true;
}
// A surface normal of a triangle whose face is `face`, turned to face a camera looking along `rayDirection`. The pass
// culls nothing, so when the camera sees the triangle's back (an open mesh from behind, or a mirrored copy, whose matrix
// reverses its winding) the normal is turned toward the camera. A normal that still leans away from the ray (an
// interpolated or sampled normal tilted past a grazing ray) is clamped to face the camera, by removing the part of it
// along the ray and a sliver more.
float3 sdfMeshFaceCamera(float3 normal, float3 face, float3 rayDirection) {
    normal = normalize(normal);
    normal = ((dot(normal, face) < 0.0) ? -normal : normal);
    normal = ((dot(face, rayDirection) > 0.0) ? -normal : normal);

    float along = dot(normal, rayDirection);

    return ((along > 0.0) ? normalize(normal - (rayDirection * (along + 1.0e-3))) : normal);
}
// The surface normal of a draw's triangle at a world point on it, seen along `rayDirection`: the vertex normals
// interpolated at the point's barycentric coordinates when the mesh carries them, the face normal otherwise, facing the
// camera (sdfMeshFaceCamera). `faceNormal` is the triangle's own unit normal on the camera's side, the plane the
// rasterized surface lies in.
float3 sdfMeshSurfaceNormal(uint draw, uint triangleIndex, float3 surfacePoint, float3 rayDirection, out float3 faceNormal) {
    uint record = sdfMeshRecord(draw);
    uint first = (3u * triangleIndex);
    float3 a = sdfMeshWorldPosition(record, first);
    float3 b = sdfMeshWorldPosition(record, (first + 1u));
    float3 c = sdfMeshWorldPosition(record, (first + 2u));
    float3 face = cross((b - a), (c - a));
    float3 normal = face;
    float3 weights;

    faceNormal = (face * rsqrt(max(dot(face, face), 1.0e-30)));
    faceNormal = ((dot(faceNormal, rayDirection) > 0.0) ? -faceNormal : faceNormal);

    if (((sdfMeshRegion[(record + SdfMeshFlagsWord)] & SdfMeshNormalsFlag) != 0u) && sdfMeshBarycentric(a, b, c, surfacePoint, weights)) {
        float3 interpolated = (((weights.x * sdfMeshWorldNormal(record, first)) + (weights.y * sdfMeshWorldNormal(record, (first + 1u)))) + (weights.z * sdfMeshWorldNormal(record, (first + 2u))));

        if (dot(interpolated, interpolated) > 1.0e-20) {
            normal = interpolated;
        }
    }

    return sdfMeshFaceCamera(normal, face, rayDirection);
}

// What the mesh pass's vertex stage hands its fragment stage: the clip position, the world position the fragment
// measures the ray parameter to, and the triangle, which the surface pass resolves the surface of.
struct MeshVertex {
    float4 position : SV_Position;
    float3 world : WORLD;
    nointerpolation uint triangleIndex : MESHTRIANGLE;
};

#endif
