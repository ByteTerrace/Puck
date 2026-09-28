// A textured mesh hit's surface, read from the mesh atlases (SdfMeshAtlas): albedo, the octahedral normal pair, occlusion,
// each texel's palette entry and emitted light, which the includer's interface declares in its World group (sdf-world
// does; the mesh pass's sdf-mesh interface does not, so its stages never include this module). A textured draw's vertices
// carry texture coordinates already moved into the atlases (SdfMeshRegion.Write), so a hit interpolates them at its
// barycentric coordinates and samples each atlas at the level whose texels match the pixel's footprint, blending the two
// nearest levels. Each level is sampled bilinearly inside the hit's own tile: the coordinate is clamped half a texel
// inside the tile at that level, so no tap reads a neighbouring quad's texels, which is what the bake's tile-aware chains
// are built for. The normal pair is not filtered as codes: the octahedral map folds, so two neighbouring codes across a
// fold decode to unrelated directions; each of its four taps is decoded to a direction first, and the directions are
// weighted. The palette entry is read without filtering at the nearer level. KEEP IN SYNC with
// SdfBakeTier.TileTexels, SdfMeshTextures.Usages and SdfSurfaceTextures (the albedo is sRGB-encoded, the emission linear
// albedo times emissive strength).
#ifndef FRAME_SDF_MESH_TEXTURES_HLSLI
#define FRAME_SDF_MESH_TEXTURES_HLSLI

#include "../field/sdf-octahedral.hlsli"
#include "sdf-mesh.hlsli"

// The edge of one quad's tile at level 0, in texels.
static const float SdfMeshTileTexels = 4.0;
// The sampler every atlas level is read through: GpuSamplerFilter.Linear.
static const uint SdfMeshLinearSampler = 1u;

// Where a textured hit reads the atlases: its texture coordinate in them, the level its footprint wants (fractional,
// clamped to the chain), and the chain's level count.
struct SdfMeshTexel {
    float2 uv;
    float level;
    uint levels;
};

// Whether a draw's mesh is textured in the atlases the frame binds.
bool sdfMeshTextured(uint draw) {
    return ((sdfMeshRegion[(sdfMeshRecord(draw) + SdfMeshFlagsWord)] & SdfMeshTexturesFlag) != 0u);
}
// The atlas texture coordinate of a draw's index `k`.
float2 sdfMeshTexcoord(uint record, uint k) {
    uint word = (sdfMeshVertexAt(record, k) + 6u);

    return asfloat(uint2(sdfMeshRegion[word], sdfMeshRegion[(word + 1u)]));
}
// Where a textured draw's triangle is read at a world point on it, for a pixel whose footprint there is `footprint`
// world units: the texture coordinate interpolated at the point, and the level at which one texel spans the footprint,
// from the triangle's texels per world unit (the square root of its area in atlas texels over its area in world units).
SdfMeshTexel sdfMeshTexelAt(uint draw, uint triangleIndex, float3 surfacePoint, float footprint) {
    uint record = sdfMeshRecord(draw);
    uint first = (3u * triangleIndex);
    float3 a = sdfMeshWorldPosition(record, first);
    float3 b = sdfMeshWorldPosition(record, (first + 1u));
    float3 c = sdfMeshWorldPosition(record, (first + 2u));
    float2 uvA = sdfMeshTexcoord(record, first);
    float2 uvB = sdfMeshTexcoord(record, (first + 1u));
    float2 uvC = sdfMeshTexcoord(record, (first + 2u));
    float3 weights;
    uint width;
    uint height;
    uint levels;

    sdfMeshBarycentric(a, b, c, surfacePoint, weights);
    sdfMeshAlbedo.GetDimensions(0u, width, height, levels);

    float2 size = float2((float)width, (float)height);
    float2 edgeB = ((uvB - uvA) * size);
    float2 edgeC = ((uvC - uvA) * size);
    float texelArea = abs(((edgeB.x * edgeC.y) - (edgeB.y * edgeC.x)));
    float worldArea = length(cross((b - a), (c - a)));
    float density = sqrt((texelArea / max(worldArea, 1.0e-20)));
    SdfMeshTexel texel;

    texel.uv = (((weights.x * uvA) + (weights.y * uvB)) + (weights.z * uvC));
    texel.levels = max(levels, 1u);
    texel.level = clamp(log2(max((footprint * density), 1.0e-6)), 0.0, (float)(texel.levels - 1u));

    return texel;
}
// The continuous texel coordinate of `uv` at a level, clamped half a texel inside its tile there, and the level's extent.
float2 sdfMeshTileClamped(Texture2D<float4> atlas, float2 uv, uint level, out float2 size) {
    uint width;
    uint height;
    uint levels;

    atlas.GetDimensions(level, width, height, levels);
    size = float2((float)width, (float)height);

    float tile = max((SdfMeshTileTexels / (float)(1u << level)), 1.0);
    float2 at = (uv * size);
    float2 origin = (floor((at / tile)) * tile);

    return clamp(at, (origin + 0.5), (origin + (tile - 0.5)));
}
// One atlas at one level, bilinear inside the tile.
float4 sdfMeshAtlasLevel(Texture2D<float4> atlas, float2 uv, uint level) {
    float2 size;
    float2 at = sdfMeshTileClamped(atlas, uv, level, size);

    return atlas.SampleLevel(samplers[SdfMeshLinearSampler], (at / size), (float)level);
}
// One atlas at a texel's fractional level, blending the two nearest levels.
float4 sdfMeshAtlasSample(Texture2D<float4> atlas, SdfMeshTexel texel) {
    uint low = (uint)floor(texel.level);
    uint high = min((low + 1u), (texel.levels - 1u));

    return lerp(sdfMeshAtlasLevel(atlas, texel.uv, low), sdfMeshAtlasLevel(atlas, texel.uv, high), frac(texel.level));
}
// The linear albedo at a texel: the sRGB-encoded atlas decoded after filtering.
float3 sdfMeshTexelAlbedo(SdfMeshTexel texel) {
    float3 encoded = saturate(sdfMeshAtlasSample(sdfMeshAlbedo, texel).rgb);

    return lerp(pow(((encoded + 0.055) / 1.055), 2.4), (encoded / 12.92), step(encoded, 0.04045));
}
// The object-space direction at one level: the four bilinear taps inside the tile, each pair decoded, weighted.
float3 sdfMeshNormalLevel(float2 uv, uint level) {
    float2 size;
    float2 at = (sdfMeshTileClamped(sdfMeshNormals, uv, level, size) - 0.5);
    float tile = max((SdfMeshTileTexels / (float)(1u << level)), 1.0);
    float2 origin = (floor(((at + 0.5) / tile)) * tile);
    int2 low = int2(floor(at));
    int2 high = int2(min((float2(low) + 1.0), ((origin + tile) - 1.0)));
    float2 weight = frac(at);
    float3 d00 = sdfOctDecode(((sdfMeshNormals.Load(int3(low, (int)level)).rg * 2.0) - 1.0));
    float3 d10 = sdfOctDecode(((sdfMeshNormals.Load(int3(int2(high.x, low.y), (int)level)).rg * 2.0) - 1.0));
    float3 d01 = sdfOctDecode(((sdfMeshNormals.Load(int3(int2(low.x, high.y), (int)level)).rg * 2.0) - 1.0));
    float3 d11 = sdfOctDecode(((sdfMeshNormals.Load(int3(high, (int)level)).rg * 2.0) - 1.0));

    return lerp(lerp(d00, d10, weight.x), lerp(d01, d11, weight.x), weight.y);
}
// The world normal at a texel: its two levels' directions blended in the mesh's object space, under the draw's normal
// matrix.
float3 sdfMeshTexelNormal(uint draw, SdfMeshTexel texel) {
    uint low = (uint)floor(texel.level);
    uint high = min((low + 1u), (texel.levels - 1u));
    float3 direction = lerp(sdfMeshNormalLevel(texel.uv, low), sdfMeshNormalLevel(texel.uv, high), frac(texel.level));

    return normalize(sdfMeshNormalToWorld(sdfMeshRecord(draw), direction));
}
// The baked ambient occlusion at a texel.
float sdfMeshTexelOcclusion(SdfMeshTexel texel) {
    return saturate(sdfMeshAtlasSample(sdfMeshOcclusion, texel).r);
}
// The linear light a texel emits.
float3 sdfMeshTexelEmission(SdfMeshTexel texel) {
    return max(sdfMeshAtlasSample(sdfMeshEmission, texel).rgb, 0.0);
}
// The material a texel shades with: the draw's material plus the texel's palette entry, read without filtering at the
// nearer level.
int sdfMeshTexelMaterial(uint draw, SdfMeshTexel texel) {
    uint level = min((uint)round(texel.level), (texel.levels - 1u));
    float2 size;
    float2 at = sdfMeshTileClamped(sdfMeshMaterials, texel.uv, level, size);
    float entry = sdfMeshMaterials.Load(int3(int2(floor(at)), (int)level)).r;

    return (asint(sdfMeshRegion[(sdfMeshRecord(draw) + SdfMeshMaterialWord)]) + (int)round((entry * 255.0)));
}

#endif
