// The mesh pass's and the hit passes' reads of the mesh region, SdfMeshRegion's raw word layout: one thirty-one-word
// record a draw (its row-vector object-to-world matrix row by row, its material, the word its first index sits at, its
// index count, the word its first vertex sits at, its attribute flags, the word its first triangle material sits at,
// and its normal matrix, the inverse transpose of the matrix's upper 3x3, row by row), then the vertices, eight words each (position, normal, texture coordinate), then the triangle materials, then the
// indices. KEEP IN SYNC with SdfMeshRegion.Write. The includer's interface declares the region, sdfMeshRegion: sdf-world
// and sdf-mesh both do.
#ifndef SDF_MESH_HLSLI
#define SDF_MESH_HLSLI

static const uint SdfMeshDrawWords = 31u;
static const uint SdfMeshMaterialWord = 16u;
static const uint SdfMeshIndexWord = 17u;
static const uint SdfMeshIndexCountWord = 18u;
static const uint SdfMeshVertexWord = 19u;
static const uint SdfMeshFlagsWord = 20u;
static const uint SdfMeshTriangleMaterialWord = 21u;
static const uint SdfMeshNormalMatrixWord = 22u;
static const uint SdfMeshVertexWords = 8u;
// A record's attribute flags: its mesh carries a normal per vertex, a palette entry per triangle, and surface textures
// the mesh atlases hold (frame/sdf-mesh-textures.hlsli). KEEP IN SYNC with SdfMeshRegion.NormalsFlag, MaterialsFlag and
// TexturesFlag.
static const uint SdfMeshNormalsFlag = 1u;
static const uint SdfMeshMaterialsFlag = 2u;
static const uint SdfMeshTexturesFlag = 4u;
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
// The first word of the vertex a draw's index `k` names.
uint sdfMeshVertexAt(uint record, uint k) {
    uint index = sdfMeshRegion[(sdfMeshRegion[(record + SdfMeshIndexWord)] + k)];

    return (sdfMeshRegion[(record + SdfMeshVertexWord)] + (SdfMeshVertexWords * index));
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
// The world position of a draw's index `k`: the position that index names, under the draw's matrix (p * M, the row-vector
// convention, so the fourth row is the translation).
float3 sdfMeshWorldPosition(uint record, uint k) {
    uint word = sdfMeshVertexAt(record, k);
    float3 p = asfloat(uint3(sdfMeshRegion[word], sdfMeshRegion[(word + 1u)], sdfMeshRegion[(word + 2u)]));

    return (sdfMeshWorldDirection(record, p) + sdfMeshRow(record, 3u));
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
// The world normal of a draw's index `k`: the normal the vertex it names carries, under the draw's normal matrix.
// Meaningful only for a mesh that carries normals.
float3 sdfMeshWorldNormal(uint record, uint k) {
    uint word = (sdfMeshVertexAt(record, k) + 3u);

    return sdfMeshNormalToWorld(record, asfloat(uint3(sdfMeshRegion[word], sdfMeshRegion[(word + 1u)], sdfMeshRegion[(word + 2u)])));
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
    normal = ((dot(face, rayDirection) > 0.0) ? -normal : normal);

    float along = dot(normal, rayDirection);

    return ((along > 0.0) ? normalize(normal - (rayDirection * (along + 1.0e-3))) : normal);
}
// The face of a draw's triangle in world space, wound as its indices name it.
float3 sdfMeshFace(uint record, uint triangleIndex) {
    uint first = (3u * triangleIndex);
    float3 a = sdfMeshWorldPosition(record, first);

    return cross((sdfMeshWorldPosition(record, (first + 1u)) - a), (sdfMeshWorldPosition(record, (first + 2u)) - a));
}
// The surface normal of a draw's triangle at a world point on it, seen along `rayDirection`: the vertex normals
// interpolated at the point's barycentric coordinates when the mesh carries them, the face normal otherwise, facing the
// camera (sdfMeshFaceCamera).
float3 sdfMeshSurfaceNormal(uint draw, uint triangleIndex, float3 surfacePoint, float3 rayDirection) {
    uint record = sdfMeshRecord(draw);
    uint first = (3u * triangleIndex);
    float3 a = sdfMeshWorldPosition(record, first);
    float3 b = sdfMeshWorldPosition(record, (first + 1u));
    float3 c = sdfMeshWorldPosition(record, (first + 2u));
    float3 face = cross((b - a), (c - a));
    float3 normal = face;
    float3 weights;

    if (((sdfMeshRegion[(record + SdfMeshFlagsWord)] & SdfMeshNormalsFlag) != 0u) && sdfMeshBarycentric(a, b, c, surfacePoint, weights)) {
        float3 interpolated = (((weights.x * sdfMeshWorldNormal(record, first)) + (weights.y * sdfMeshWorldNormal(record, (first + 1u)))) + (weights.z * sdfMeshWorldNormal(record, (first + 2u))));

        if (dot(interpolated, interpolated) > 1.0e-20) {
            normal = ((dot(interpolated, face) < 0.0) ? -interpolated : interpolated);
        }
    }

    return sdfMeshFaceCamera(normal, face, rayDirection);
}

// What the mesh pass's vertex stage hands its fragment stage: the clip position, the world position the fragment
// measures the ray parameter to, and the triangle, which the hit passes resolve the surface of.
struct MeshVertex {
    float4 position : SV_Position;
    float3 world : WORLD;
    nointerpolation uint triangleIndex : MESHTRIANGLE;
};

#endif
