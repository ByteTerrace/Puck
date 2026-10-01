// An impostor card's surface: where the camera's ray meets the baked prototype the card stands for. The card is a quad the
// mesh pass places in front of the prototype's bounding sphere (sdf-mesh.vert.hlsl), and its fragments, and the hit passes
// after them, find the surface behind each pixel in the impostor's views (SdfBakedImpostor): a grid of orthographic views
// of the sphere along the directions an octahedral map decodes, +Y its pole, each one tile of the impostor atlases. The ray
// is taken in the prototype's object space, in units of the sphere's radius, and for each of the three views nearest the
// direction the camera looks from (weighted as a triangle of the view grid) it is marched through the sphere against that
// view's depth, which is a height field over the view's plane; the ray's hit is the weighted mean of the hits the views
// agree on, and a pixel no majority of the views cover is a miss. The includer's interface declares the impostor depth
// atlas, sdfImpostorDepth: the mesh pass's pass group and the world kernels' World group both do. KEEP IN SYNC with
// SdfBakedImpostor (the views' directions, bases and texel layout), SdfMeshImpostor and SdfMeshRegion.Write (the
// record's impostor words), and the CPU oracle in tests/Puck.SignedDistance.Tests (SdfImpostorOracle).
#ifndef FRAME_SDF_MESH_IMPOSTOR_HLSLI
#define FRAME_SDF_MESH_IMPOSTOR_HLSLI

#include "sdf-mesh.hlsli"

// The record words the impostor occupies. KEEP IN SYNC with SdfMeshRegion.Write.
static const uint SdfMeshImpostorWord = 31u;
// The steps a ray takes through the sphere against a view's depth.
static const uint SdfImpostorSteps = 8u;
// The depth, in the atlas's unit range, at and beyond which a view's texel is a miss: the baker writes 1 where its ray
// missed, and block compression bends it a little.
static const float SdfImpostorMissDepth = 0.98;
// The gap a texel that shows nothing reports, larger than any distance inside the sphere, so a hit found just past it
// lands at the step it was found at.
static const float SdfImpostorMissGap = 16.0;

// A draw's impostor: the bounding sphere in the draw's object space, the view grid and where the impostor sits in the atlases.
struct SdfImpostor {
    float3 center;
    float radius;
    uint views;
    uint viewTexels;
    float4 placement; // xy = scale, zw = offset, in atlas fractions
};
SdfImpostor sdfImpostorOf(uint record) {
    uint word = (record + SdfMeshImpostorWord);
    SdfImpostor impostor;

    impostor.center = asfloat(uint3(sdfMeshRegion[word], sdfMeshRegion[(word + 1u)], sdfMeshRegion[(word + 2u)]));
    impostor.radius = asfloat(sdfMeshRegion[(word + 3u)]);
    impostor.views = sdfMeshRegion[(word + 4u)];
    impostor.viewTexels = sdfMeshRegion[(word + 5u)];
    impostor.placement = asfloat(uint4(sdfMeshRegion[(word + 6u)], sdfMeshRegion[(word + 7u)], sdfMeshRegion[(word + 8u)], sdfMeshRegion[(word + 9u)]));

    return impostor;
}
// The uniform scale a draw's matrix gives its object space: the length of its first row.
float sdfImpostorScale(uint record) {
    return length(sdfMeshRow(record, 0u));
}
// The bounding sphere's center in world space.
float3 sdfImpostorWorldCenter(uint record, SdfImpostor impostor) {
    return (sdfMeshWorldDirection(record, impostor.center) + sdfMeshRow(record, 3u));
}

// The octahedral map of a direction onto [-1, 1]^2, +Y the pole: the inverse of sdfImpostorViewDirection's.
float2 sdfImpostorEncode(float3 v) {
    float2 p = (v.xz * (1.0 / ((abs(v.x) + abs(v.y)) + abs(v.z))));

    if (v.y < 0.0) {
        p = float2(((1.0 - abs(p.y)) * ((p.x >= 0.0) ? 1.0 : -1.0)), ((1.0 - abs(p.x)) * ((p.y >= 0.0) ? 1.0 : -1.0)));
    }

    return p;
}
// The unit direction from the center toward the camera of view (i, j) of a grid of `views` a side, as the baker decodes it.
float3 sdfImpostorViewDirection(uint i, uint j, uint views) {
    float2 q = (((float2((float)i, (float)j) + 0.5) / (float)views) * 2.0 - 1.0);
    float3 v = float3(q.x, ((1.0 - abs(q.x)) - abs(q.y)), q.y);

    if (v.y < 0.0) {
        v = float3(((1.0 - abs(q.y)) * ((q.x >= 0.0) ? 1.0 : -1.0)), v.y, ((1.0 - abs(q.x)) * ((q.y >= 0.0) ? 1.0 : -1.0)));
    }

    return normalize(v);
}
// A view's orthonormal basis: right and up in its plane, the direction toward its camera third, as the baker builds it.
void sdfImpostorViewBasis(float3 toward, out float3 right, out float3 up) {
    float3 reference = ((abs(toward.y) > 0.999) ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0));

    right = normalize(cross(reference, toward));
    up = cross(toward, right);
}

// The three views nearest a direction toward the camera, as a triangle of the view grid, and their weights.
struct SdfImpostorViews {
    uint2 cell[3];
    float3 weight;
};
SdfImpostorViews sdfImpostorViewsAt(SdfImpostor impostor, float3 toward) {
    float views = (float)impostor.views;
    float2 grid = (((sdfImpostorEncode(toward) * 0.5 + 0.5) * views) - 0.5);
    float2 base = floor(grid);
    float2 f = (grid - base);
    float2 last = float2(views - 1.0, views - 1.0);
    SdfImpostorViews chosen;

    if ((f.x + f.y) < 1.0) {
        chosen.cell[0] = (uint2)clamp(base, 0.0, last);
        chosen.cell[1] = (uint2)clamp((base + float2(1.0, 0.0)), 0.0, last);
        chosen.cell[2] = (uint2)clamp((base + float2(0.0, 1.0)), 0.0, last);
        chosen.weight = float3((1.0 - f.x - f.y), f.x, f.y);
    } else {
        chosen.cell[0] = (uint2)clamp((base + float2(1.0, 1.0)), 0.0, last);
        chosen.cell[1] = (uint2)clamp((base + float2(1.0, 0.0)), 0.0, last);
        chosen.cell[2] = (uint2)clamp((base + float2(0.0, 1.0)), 0.0, last);
        chosen.weight = float3((f.x + f.y - 1.0), (1.0 - f.y), (1.0 - f.x));
    }

    return chosen;
}

// The level of the impostor atlases a pixel reads: the one whose texels are nearest the pixel's size, from the pixel's
// footprint in world units at the surface, clamped to the chain.
uint sdfImpostorLevel(SdfImpostor impostor, uint record, float footprint, uint levels) {
    float texelsAcross = ((float)impostor.viewTexels / max((2.0 * impostor.radius * sdfImpostorScale(record)), 1.0e-6));
    float level = round(log2(max((footprint * texelsAcross), 1.0)));

    return min((uint)level, (levels - 1u));
}
// A texel of a view at a level: the tile edge at the level, and the atlas texel of the tile's origin.
struct SdfImpostorTile {
    float edge;
    float2 origin;
};
SdfImpostorTile sdfImpostorTileOf(SdfImpostor impostor, uint2 cell, uint level, float2 atlasSize) {
    SdfImpostorTile tile;

    tile.edge = (float)max((impostor.viewTexels >> level), 1u);
    tile.origin = (floor(((impostor.placement.zw * atlasSize) + 0.5)) + ((float2)cell * tile.edge));

    return tile;
}
// The view-plane coordinates of a point in the sphere's unit coordinates, as a fraction of the tile: right to the right,
// up to the top, as the baker lays the texels out.
float2 sdfImpostorTileFraction(float2 ab) {
    return float2((ab.x * 0.5 + 0.5), (0.5 - ab.y * 0.5));
}

// The depth of one view at a point of its plane, in the unit range, at a level; the nearest texel.
float sdfImpostorDepthAt(SdfImpostor impostor, uint2 cell, float2 ab, uint level) {
    uint width;
    uint height;
    uint levels;

    sdfImpostorDepth.GetDimensions(level, width, height, levels);

    SdfImpostorTile tile = sdfImpostorTileOf(impostor, cell, level, float2((float)width, (float)height));
    float2 local = clamp(floor((sdfImpostorTileFraction(ab) * tile.edge)), 0.0, (tile.edge - 1.0));

    return sdfImpostorDepth.Load(int3(int2((tile.origin + local)), (int)level)).r;
}
// The distance along the ray, in the units of the ray's parameter, at which it first passes behind one view's surface
// between its entry and exit of the sphere, or false where the view's depth shows nothing there. `start` is the ray's
// point at parameter zero and `travel` its change per unit parameter, in the sphere's unit coordinates.
bool sdfImpostorViewHit(SdfImpostor impostor, uint2 cell, float3 start, float3 travel, float entry, float leave, uint level, out float hit) {
    float3 toward = sdfImpostorViewDirection(cell.x, cell.y, impostor.views);
    float3 right;
    float3 up;

    sdfImpostorViewBasis(toward, right, up);
    hit = entry;

    float previousGap = SdfImpostorMissGap;
    float previousAt = entry;

    [loop] for (uint n = 0u; n <= SdfImpostorSteps; n++) {
        float at = lerp(entry, leave, ((float)n / (float)SdfImpostorSteps));
        float3 spot = (start + (travel * at));
        float depth = sdfImpostorDepthAt(impostor, cell, float2(dot(spot, right), dot(spot, up)), level);
        float gap = ((depth < SdfImpostorMissDepth) ? (dot(spot, toward) - (1.0 - (2.0 * depth))) : SdfImpostorMissGap);

        if (gap <= 0.0) {
            hit = ((n == 0u) ? at : lerp(previousAt, at, (previousGap / (previousGap - gap))));

            return true;
        }

        previousGap = gap;
        previousAt = at;
    }

    return false;
}
// The ray's hit with a draw's impostor: the parameter along the unit `direction` from `origin`, in world units, at which
// the surface the views show meets it. `footprint` is the world size of a pixel at the surface, which picks the level.
bool sdfImpostorTrace(uint draw, float3 origin, float3 direction, float footprint, out float t) {
    uint record = sdfMeshRecord(draw);
    SdfImpostor impostor = sdfImpostorOf(record);
    float radius = impostor.radius;
    float3 start = ((sdfMeshObjectFromWorld(record, (origin - sdfMeshRow(record, 3u))) - impostor.center) / radius);
    float3 travel = (sdfMeshObjectFromWorld(record, direction) / radius);
    float a = dot(travel, travel);
    float b = dot(start, travel);
    float c = (dot(start, start) - 1.0);
    float discriminant = ((b * b) - (a * c));

    t = 0.0;

    if (discriminant <= 0.0) {
        return false;
    }

    float root = sqrt(discriminant);
    float leave = ((root - b) / a);
    float entry = max(((-b - root) / a), 0.0);

    if (leave <= entry) {
        return false;
    }

    uint width;
    uint height;
    uint levels;

    sdfImpostorDepth.GetDimensions(0u, width, height, levels);

    uint level = sdfImpostorLevel(impostor, record, footprint, max(levels, 1u));
    SdfImpostorViews views = sdfImpostorViewsAt(impostor, normalize(-travel));
    float covered = 0.0;
    float sum = 0.0;

    [unroll] for (uint view = 0u; view < 3u; view++) {
        float hit;

        if (sdfImpostorViewHit(impostor, views.cell[view], start, travel, entry, leave, level, hit)) {
            covered += views.weight[view];
            sum += (views.weight[view] * hit);
        }
    }

    if (covered < 0.5) {
        return false;
    }

    t = (sum / covered);

    return true;
}

#endif
