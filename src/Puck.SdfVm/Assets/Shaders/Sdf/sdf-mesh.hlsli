// The mesh pass's reads of the mesh region, SdfMeshRegion's raw word layout: one twenty-word record a draw (its
// row-vector object-to-world matrix row by row, its material, the word its first index sits at, its index count and
// the word its first position sits at), then the positions, three words each, then the indices. KEEP IN SYNC with
// SdfMeshRegion.Write. The includer's interface declares the region, sdfMeshRegion: sdf-world and sdf-mesh both do.
#ifndef SDF_MESH_HLSLI
#define SDF_MESH_HLSLI

static const uint SdfMeshDrawWords = 20u;
static const uint SdfMeshMaterialWord = 16u;
static const uint SdfMeshIndexWord = 17u;
static const uint SdfMeshIndexCountWord = 18u;
static const uint SdfMeshPositionWord = 19u;
// The bit the view starts at in the index a mesh draw call pushes; the bits below it name the draw. KEEP IN SYNC with
// SdfWorldInterfaces.MeshViewShift.
static const uint SdfMeshViewShift = 24u;
static const uint SdfMeshDrawMask = ((1u << SdfMeshViewShift) - 1u);

// The first word of a draw's record.
uint sdfMeshRecord(uint draw) {
    return (draw * SdfMeshDrawWords);
}
// The material a draw's triangles shade with.
int sdfMeshMaterial(uint draw) {
    return asint(sdfMeshRegion[(sdfMeshRecord(draw) + SdfMeshMaterialWord)]);
}
// The world position of a draw's index `k`: the position that index names, under the draw's matrix (p * M, the row-vector
// convention, so the fourth row is the translation).
float3 sdfMeshWorldPosition(uint record, uint k) {
    uint index = sdfMeshRegion[(sdfMeshRegion[(record + SdfMeshIndexWord)] + k)];
    uint word = (sdfMeshRegion[(record + SdfMeshPositionWord)] + (3u * index));
    float3 p = asfloat(uint3(sdfMeshRegion[word], sdfMeshRegion[(word + 1u)], sdfMeshRegion[(word + 2u)]));
    float3 row0 = asfloat(uint3(sdfMeshRegion[record], sdfMeshRegion[(record + 1u)], sdfMeshRegion[(record + 2u)]));
    float3 row1 = asfloat(uint3(sdfMeshRegion[(record + 4u)], sdfMeshRegion[(record + 5u)], sdfMeshRegion[(record + 6u)]));
    float3 row2 = asfloat(uint3(sdfMeshRegion[(record + 8u)], sdfMeshRegion[(record + 9u)], sdfMeshRegion[(record + 10u)]));
    float3 row3 = asfloat(uint3(sdfMeshRegion[(record + 12u)], sdfMeshRegion[(record + 13u)], sdfMeshRegion[(record + 14u)]));

    return ((((p.x * row0) + (p.y * row1)) + (p.z * row2)) + row3);
}

// What the mesh pass's vertex stage hands its fragment stage: the clip position, the world position the fragment
// measures the ray parameter to, and the triangle's face normal, which the fragment stage turns toward the camera.
struct MeshVertex {
    float4 position : SV_Position;
    float3 world : WORLD;
    nointerpolation float3 normal : NORMAL;
};

#endif
