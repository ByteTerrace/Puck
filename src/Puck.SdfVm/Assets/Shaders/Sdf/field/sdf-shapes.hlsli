// The target-escape primitives (the star polygon, the convex polygon, the path, the glyph, the sampled region and the
// sweep) and evaluateShape. Every other primitive, and the 2D lift every profile shares, is a shared formula generated
// with its fixed-point twin (Puck.SignedDistance's SdfFieldFormulas, isa/sdf-field-shape-formulas.hlsli).
#ifndef FIELD_SDF_SHAPES_HLSLI
#define FIELD_SDF_SHAPES_HLSLI
// The glyph median decode (puckMsdfReconstruct) is the overlays' too.
#include "Shared/msdf.hlsli"
// The ellipse's projection core, a primitive of each reader that the ellipse formula calls.
#include "sdf-ellipse.hlsli"
#include "../isa/sdf-field-shape-formulas.hlsli"
#ifdef SDF_FIELD_DUAL
#include "../isa/sdf-field-shape-dual.hlsli"
#endif
// The gauge-envelope device probe distinguishes the clamped endpoints.
bool sdfSuperellipsoidNormIsClamped(float approximate, float norm, float exponent) {
    return ((asuint(approximate) & 0x7F800000u) == 0x7F800000u)
        || approximate != sdfClampSuperellipsoidNorm(approximate, norm, exponent);
}

// === The 2D-primitive family: exact 2D SDFs lifted to 3D solids =====================================================
// Each primitive is an exact 2D signed-distance field LIFTED to 3D (data1.y): EXTRUDED along Z (a prism) or REVOLVED
// around Y (a lathe), by the formulas' shared sdfExtrudeChamfer2D and sdfRevolve2D. data1.w is the family-wide
// edge-rounding radius the host insets the profile by, offset back out here; data1.z is the extrude's cap bevel.

// Extrude an exact 2D distance d (evaluated on the XY plane) to half-height h along Z: exact for any exact d.
precise float sdfExtrude2D(float d, float pz, float h) {
    return sdfBoxDistance2(float2(d, (abs(pz) - h)));
}
// Exact star-polygon SDF (n points, inner-radius control m): r = outer radius, an = pi/n (baked), ecs = (cos(pi/m), sin(pi/m))
// (baked). ecs = (0, 1) collapses this to the exact regular n-gon (m = 2). The GLSL `mod` is FLOOR modulo — use the
// house sdfFloorMod so a negative atan2 sector index folds correctly (HLSL fmod truncates and would mis-fold).
precise float sdfStar2D(float2 p, float r, float an, float2 ecs) {
    float2 acs = float2(cos(an), sin(an));
    float bn = (sdfFloorMod(atan2(p.x, p.y), (2.0 * an)) - an);
    p = (length(p) * float2(cos(bn), abs(sin(bn))));
    p = (p - (r * acs));
    p = (p + (ecs * clamp((-dot(p, ecs)), 0.0, ((r * acs.y) / ecs.y))));
    return (length(p) * sign(p.x));
}
// Regular polygon AND star share this: data0 = (r, an, ecs.x, lift), data1.z = ecs.y (the polygon bakes ecs = (0, 1)).
precise float sdfPolyStar(float3 p, float4 data0, float4 data1) {
    float2 ecs = float2(data0.z, data1.z);

    return (((data1.y > 0.5)
        ? sdfExtrude2D(sdfStar2D(p.xy, data0.x, data0.y, ecs), p.z, data0.w)
        : sdfStar2D(sdfRevolve2D(p, data0.w), data0.x, data0.y, ecs)) - data1.w);
}
// One vertex of a SDF_SHAPE_CONVEX_POLYGON side table (see its own define comment): two packed (x, y) vertices per
// uvec4 word in sdfWords, so vertex index i lives at word (tableOffset + (i >> 1)), lane .xy for an even index and
// .zw for an odd one.
precise float2 sdfPolygonVertex(uint tableOffset, uint index) {
    uint4 packed = sdfProgramWord((tableOffset + (index >> 1u)));

    return (((index & 1u) != 0u) ? asfloat(packed.zw) : asfloat(packed.xy));
}
// Exact signed distance to the convex polygon (`count` vertices starting at `tableOffset`, clockwise — this form is
// correct for any simple polygon, convex or not, which is why convexity is validated at authoring time rather than
// here). The running minimum squared distance to every edge SEGMENT (projection clamped to [0, 1], not the infinite
// line), signed by one even/odd crossing-parity flip per edge (iq's sdPolygon) — sqrt-free per edge, one sqrt total.
precise float sdfConvexPolygon2D(float2 p, uint tableOffset, uint count) {
    if (count < 3u || count > SDF_MAX_CONVEX_VERTICES || !sdfProgramRange(tableOffset, (count + 1u) / 2u, 1u)) { return SDF_FAR_DISTANCE; }
    float2 firstVertex = sdfPolygonVertex(tableOffset, 0u);
    float2 previous = sdfPolygonVertex(tableOffset, (count - 1u));
    float2 delta0 = (p - firstVertex);
    float d = dot(delta0, delta0);
    float s = 1.0;

    for (uint i = 0u; i < SDF_MAX_CONVEX_VERTICES; i++) {
        if (i >= count) { break; }
        float2 vertex = sdfPolygonVertex(tableOffset, i);
        float2 e = (previous - vertex);
        float2 w = (p - vertex);
        float2 b = (w - (e * clamp((dot(w, e) / dot(e, e)), 0.0, 1.0)));

        d = min(d, dot(b, b));

        bool3 c = bool3((p.y >= vertex.y), (p.y < previous.y), ((e.x * w.y) > (e.y * w.x)));

        if (all(c) || all(!c)) {
            s = -s;
        }

        previous = vertex;
    }

    return (s * sqrt(d));
}
// Lifted wrapper, the family's usual convention: data0.w = lift amount, data1.y = lift mode, data1.z = cap chamfer,
// data1.w = edge-rounding radius. tableOffset/count are packed into data0.x's reinterpreted uint bits (KEEP IN SYNC
// with SdfProgramBuilder.ConvexPolygon / SdfProgram's table-offset patch).
precise float sdfConvexPolygonSolid(float3 p, float4 data0, float4 data1) {
    uint packed = asuint(data0.x);
    uint count = ((packed & ((1u << SDF_CONVEX_COUNT_BITS) - 1u)) + 1u);
    uint tableOffset = (packed >> SDF_CONVEX_COUNT_BITS);

    return (((data1.y > 0.5)
        ? sdfExtrudeChamfer2D(sdfConvexPolygon2D(p.xy, tableOffset, count), p.z, data0.w, data1.z)
        : sdfConvexPolygon2D(sdfRevolve2D(p, data0.w), tableOffset, count)) - data1.w);
}
// Path tables: two float4 words per edge, (A.xy,B.xy), (radiusA,radiusB,0,0).
// The stroke segment is the exact convex hull of endpoint disks, including containment when one radius
// dominates the entire segment. It does not subtract an approximation margin or move the zero surface.
precise float sdfPathStrokeEdge(float2 p, float2 a, float2 b, float ra, float rb) {
    float2 ab = b - a;
    float len = length(ab);
    if (abs(ra - rb) >= len) {
        return (ra >= rb) ? length(p - a) - ra : length(p - b) - rb;
    }
    float2 u = ab / len;
    float2 v = p - a;
    float x = dot(v, u);
    float y = abs(v.x * u.y - v.y * u.x);
    float k = (ra - rb) / len;
    float c = sqrt(max(0.0, 1.0 - k * k));
    float along = x * c - y * k;
    if (along < 0.0) return length(v) - ra;
    if (along > len * c) return length(p - b) - rb;
    return x * k + y * c - ra;
}
precise float sdfPathSolid(float3 p, float4 data0, float4 data1) {
    uint offset = asuint(data0.x);
    if (!isfinite(data0.y) || data0.y < 0.0 || data0.y > (float)SDF_MAX_PATH_EDGES) { return SDF_FAR_DISTANCE; }
    uint count = (uint)data0.y;
    if (!sdfProgramRange(offset, count, 2u)) { return SDF_FAR_DISTANCE; }
    float distance = SDF_FAR_DISTANCE;
    float squared = SDF_FAR_DISTANCE * SDF_FAR_DISTANCE;
    bool inside = false;
    bool stroke = data1.y > 0.5;
    for (uint i = 0u; i < SDF_MAX_PATH_EDGES; ++i) {
        if (i >= count) { break; }
        float4 edge = asfloat(sdfProgramWord(offset + 2u * i));
        float2 a = edge.xy;
        float2 b = edge.zw;
        if (stroke) {
            float2 radii = asfloat(sdfProgramWord(offset + 2u * i + 1u).xy);
            distance = min(distance, sdfPathStrokeEdge(p.xy, a, b, radii.x, radii.y));
        } else {
            float2 e = b - a;
            float2 w = p.xy - a;
            float2 d = w - e * saturate(dot(w, e) / dot(e, e));
            squared = min(squared, dot(d, d));
            bool3 crossing = bool3(p.y >= a.y, p.y < b.y, e.x * w.y > e.y * w.x);
            if (all(crossing) || all(!crossing)) inside = !inside;
        }
    }
    if (!stroke) distance = (inside ? -1.0 : 1.0) * sqrt(squared);
    return sdfExtrudeChamfer2D(distance, p.z, data0.w, 0.0);
}
// === end 2D-primitive family =======================================================================================

// === SDF_SHAPE_GLYPH: a font atlas sampled as a DISTANCE-level field =================================================
// Text as REAL geometry: a glyph cell's exact 2D quad (the box in XY) extruded along Z, its interior refined by the
// atlas's true signed distance so the LETTER — not the cell — is the surface. It marches, blends, extrudes, and (with
// Subtraction) ENGRAVES into any surface. data0 = (packedUvMin, packedUvMax, distanceScale, extrudeHalfDepth); data1 =
// (smooth, halfWidth, halfHeight, samplingCorrection). distanceScale = atlas distanceRange(texels) × worldPerTexel
// (host-baked), so the encoded [0,1] field maps to LOCAL units before its derivative-bound correction.
//
// LIPSCHITZ: bilinear alpha is NOT generally 1-Lipschitz. Data1.w carries the host's reciprocal derivative bound,
// computed from decoded pixels (or worst-case RGBA8 differences), packed UVs and cell scale. Correct only the
// sampled term before max with the exact quad: both terms then have bound <=1, including the far-field seam.
// Extrusion preserves that bound and the reconstructed zero set. KEEP IN SYNC with GlyphSamplingCorrection.

// Host-baked unorm2x16 of an atlas UV, unpacked: the low 16 bits are u, the high 16 v, each /65535. An integer bit op,
// so it is bit-identical across DXC's SPIR-V and DXIL targets. Packing the four UV-rect components into two lanes frees
// data1.x for the ISA-wide smooth-blend radius (the ≤0.06-texel error at 4K is sub-texel at any authoring scale).
precise float2 sdfGlyphUnpackUv(float packed) {
    uint bits = asuint(packed);

    return (float2((bits & 0xFFFFu), (bits >> 16u)) * (1.0 / 65535.0));
}
// The extruded-quad FALLBACK: the glyph cell as a plain box, exact and 1-Lipschitz. Every kernel WITHOUT the atlas
// bound evaluates this — a conservative UNDERESTIMATE of the true glyph distance, since the letter is strictly inside
// its cell, so a bound skip never holes. halfWidth/halfHeight in data1.yz, extrudeHalfDepth in data0.w.
precise float sdfGlyphQuad(float3 p, float4 data0, float4 data1) {
    return sdfExtrude2D(sdfBoxDistance2(abs(p.xy) - float2(data1.y, data1.z)), p.z, data0.w);
}

#ifdef SDF_GLYPH_ATLAS
// Alpha holds single-channel boundary-distance samples from `puck font-atlas` or a compatible imported atlas.
// RGB median is coverage-only: channel conflicts need not define a conservative march field.
// Edge-clamped; SampleLevel(…, 0) because
// implicit-derivative filtering is undefined in a compute kernel's non-uniform control flow.
precise float sdfGlyphTexelAlpha(int2 texel, int2 dims) {
    int2 clamped = clamp(texel, int2(0, 0), (dims - int2(1, 1)));
    float2 uv = ((float2(clamped) + 0.5) / float2(dims));

    return sdfGlyphAtlas.SampleLevel(samplers[SDF_FILTER_NEAREST], uv, 0.0).a;
}
// Manual bilinear of the true single-channel field: four point taps + arithmetic lerp, NOT a hardware LINEAR sampler,
// so the reconstruction is bit-stable across both DXC backends (a driver's bilinear can differ ±1 LSB — the exact
// class WorldHighContrast is calibrated for, but manual keeps the field itself identical).
precise float sdfGlyphSampleField(float2 uv) {
    uint2 udims;
    sdfGlyphAtlas.GetDimensions(udims.x, udims.y);

    int2 dims = int2(udims);
    float2 texelF = ((uv * float2(dims)) - 0.5);
    float2 baseF = floor(texelF);
    float2 f = (texelF - baseF);
    int2 b = int2(baseF);
    float a00 = sdfGlyphTexelAlpha((b + int2(0, 0)), dims);
    float a10 = sdfGlyphTexelAlpha((b + int2(1, 0)), dims);
    float a01 = sdfGlyphTexelAlpha((b + int2(0, 1)), dims);
    float a11 = sdfGlyphTexelAlpha((b + int2(1, 1)), dims);

    return lerp(lerp(a00, a10, f.x), lerp(a01, a11, f.x), f.y);
}
// One texel's RGB pseudo-distances (edge-clamped, same tap discipline as sdfGlyphTexelAlpha).
precise float3 sdfGlyphTexelRgb(int2 texel, int2 dims) {
    int2 clamped = clamp(texel, int2(0, 0), (dims - int2(1, 1)));
    float2 uv = ((float2(clamped) + 0.5) / float2(dims));

    return sdfGlyphAtlas.SampleLevel(samplers[SDF_FILTER_NEAREST], uv, 0.0).rgb;
}
// Per-channel manual bilinear then MEDIAN-OF-3 — the classic MSDF reconstruction, for SHADE-TIME consumers ONLY (the
// GlyphDecal tier): median restores the sharp corners the single channel rounds, and its C0 kinks at channel-crossover
// lines are harmless to a coverage threshold — but they kink a marched field, so GEOMETRY keeps sdfGlyphSampleField.
// A replicated single-channel atlas (the runtime exact-EDT generator) medians to exactly its own value, so this decode
// is bit-identical to the alpha path there and only diverges — toward crisper corners — on a true MTSDF atlas.
precise float sdfGlyphSampleFieldMedian(float2 uv) {
    uint2 udims;
    sdfGlyphAtlas.GetDimensions(udims.x, udims.y);

    int2 dims = int2(udims);
    float2 texelF = ((uv * float2(dims)) - 0.5);
    float2 baseF = floor(texelF);
    float2 f = (texelF - baseF);
    int2 b = int2(baseF);
    float3 s00 = sdfGlyphTexelRgb((b + int2(0, 0)), dims);
    float3 s10 = sdfGlyphTexelRgb((b + int2(1, 0)), dims);
    float3 s01 = sdfGlyphTexelRgb((b + int2(0, 1)), dims);
    float3 s11 = sdfGlyphTexelRgb((b + int2(1, 1)), dims);

    return puckMsdfReconstruct(s00, s10, s01, s11, f);
}
// The lettering stroke rule. The mesh bake sets both values for each lattice from its group (SdfMeshBakeRequest.Glyphs):
// the cell its group's tier floors the sampling at, and the thinnest stroke the glyphs are taken to have in units of a
// glyph's distance scale (SdfProgram.GlyphStroke). Every other kernel leaves the cell zero, which thickens nothing.
static float sdfGlyphCell = 0.0;
static float sdfGlyphStroke = 0.0;

precise float sdfGlyph(float3 p, float4 data0, float4 data1) {
    float2 halfSize = float2(data1.y, data1.z);
    float dQuad = sdfBoxDistance2(abs(p.xy) - halfSize);
    float distanceScale = data0.z;
    float band = (0.5 * distanceScale);
    float halfDepth = data0.w;
    float widen = 0.0;

    // A glyph baked at a cell its strokes or relief are too thin for loses them between samples. Its thinnest stroke
    // widens toward SDF_GLYPH_THICKEN_CELLS cells, as far as the encoded band measures, and its relief to a cell, so the
    // bake drawing it at any tier keeps every stroke. Where that widening leaves the stroke under SDF_GLYPH_HOLD_CELLS
    // cells the glyph bakes as its ink box instead: the cell less the band (the atlas pads each cell by at least the
    // band, so the box holds every stroke), widened to SDF_GLYPH_THICKEN_CELLS cells inside the cell. The group's box
    // grows by the cell to hold the raised relief (SdfMeshBakeGroup.GlyphCell).
    if (sdfGlyphCell > 0.0) {
        float stroke = (sdfGlyphStroke * distanceScale);

        widen = clamp((0.5 * ((SDF_GLYPH_THICKEN_CELLS * sdfGlyphCell) - stroke)), 0.0, (SDF_GLYPH_WIDEN_SHARE * band));
        halfDepth = max(halfDepth, sdfGlyphCell);
        if ((stroke + (2.0 * widen)) < (SDF_GLYPH_HOLD_CELLS * sdfGlyphCell)) {
            float2 ink = max((halfSize - band), 0.0);
            float grow = min(max(((0.5 * SDF_GLYPH_THICKEN_CELLS * sdfGlyphCell) - min(ink.x, ink.y)), 0.0), band);

            return sdfExtrude2D((sdfBoxDistance2(abs(p.xy) - ink) - grow), p.z, halfDepth);
        }
    }

    float dPlane = (dQuad - widen);

    // Band cull: beyond ±distanceRange/2 of an edge the encoded field saturates, so a tap there tells us nothing the
    // exact quad distance dQuad does not already bound — skip the texture read entirely (the whole perf trick, and the
    // conservative far-field at once). Inside the band, refine to the letter's true distance. The max() with dQuad
    // keeps the band→proxy seam a valid UNDERESTIMATE: the saturated atlas UNDER-reports, so the exact quad wins there.
    // A widening is taken from the true distance before its derivative correction, so the correction still bounds it.
    if (dQuad < band) {
        float2 uvMin = sdfGlyphUnpackUv(data0.x);
        float2 uvMax = sdfGlyphUnpackUv(data0.y);
        float2 uv = lerp(uvMin, uvMax, clamp((((p.xy / halfSize) * 0.5) + 0.5), 0.0, 1.0));
        float encoded = sdfGlyphSampleField(uv);   // true single-channel distance; 0.5 = edge, > 0.5 inside the glyph

        dPlane = max(((((0.5 - encoded) * distanceScale) - widen) * data1.w), (dQuad - widen));
    }

    return sdfExtrude2D(dPlane, p.z, halfDepth);
}
#endif
// === end SDF_SHAPE_GLYPH ============================================================================================

// === SDF_SHAPE_SAMPLED_REGION: a sampled distance field ==================================================================
// A cubic-voxel lattice of distances sampled by manual trilinear interpolation. data0 = (boxMin.xyz, cellSize); data1 =
// (smooth, packedDims, handle, boundaryFloor). The values are not program words: sdfSampledValues holds a directory of one
// word per handle (the word the field's snapshot starts at, 0xFFFFFFFF for none) and then the snapshots, which each mesh
// bake job writes from the snapshots its groups hashed (SdfMeshBakeGpuWork), so a lattice samples exactly the values its
// mesh is keyed by. Stored values are pre-scaled 1/lambda (lambda = sqrt(3)), which makes the interpolant 1-Lipschitz with
// no stepScale change. Eight explicit loads and ordered, noncontracting arithmetic interpolate the same voxel values alike
// across SPIR-V and DXIL. KEEP IN SYNC with SdfProgramBuilder.SampledValues and SdfMeshBakeGpuWork's sampled table.
#ifdef SDF_SAMPLED_REGIONS
// One voxel, clamped to the lattice's own [0, dims-1] so the stencil never reads past its snapshot (clamp-to-edge): the
// zero set lies strictly inside the box by the bake margin, so the clamped border half-voxel is far field. The index is
// x-fastest: base + i + j*dx + k*dx*dy, as SdfSampledSnapshot lays it out.
precise float sdfSampledVoxel(uint baseWord, uint3 dims, int3 coord) {
    int3 c = clamp(coord, int3(0, 0, 0), (int3(dims) - int3(1, 1, 1)));
    uint index = baseWord + (uint)c.x + ((uint)c.y * dims.x) + ((uint)c.z * dims.x * dims.y);

    return asfloat(sdfSampledValues[index]);
}
// A precise result of the lerp intrinsic can still lower to SPIR-V FMix, whose arithmetic need not match DXIL's.
// Spell every operation and mark each intermediate so both backends retain the same subtraction, product and sum.
precise float sdfSampledInterpolate(float left, float right, float weight) {
    precise float difference = (right - left);
    precise float weighted = (weight * difference);
    precise float result = (left + weighted);

    return result;
}
#endif
precise float sdfSampledRegion(float3 p, float4 data0, float4 data1) {
#ifdef SDF_SAMPLED_REGIONS
    uint words = 0u;
    uint stride;
    sdfSampledValues.GetDimensions(words, stride);
    uint handle = asuint(data1.z);
    if (handle >= words) { return SDF_FAR_DISTANCE; }
    uint baseWord = sdfSampledValues[handle];
    float cellSize = data0.w;
    uint packedDims = asuint(data1.y);
    uint3 dims = uint3((packedDims & SDF_SAMPLED_REGION_DIM_MASK), ((packedDims >> 10) & SDF_SAMPLED_REGION_DIM_MASK), ((packedDims >> 20) & SDF_SAMPLED_REGION_DIM_MASK));
    uint voxelCount = dims.x * dims.y * dims.z;
    if (any(dims == 0u) || baseWord > words || voxelCount > words - baseWord
        || !isfinite(cellSize) || cellSize <= 0.0 || !all(isfinite(p)) || !all(isfinite(data0.xyz))) { return SDF_FAR_DISTANCE; }
    float3 boxMin = data0.xyz;
    float3 extent = (float3(dims) * cellSize);
    // Local coordinate in voxel units: [0, dims] spans the box.
    float3 local = ((p - boxMin) / cellSize);
    if (!all(isfinite(local))) { return SDF_FAR_DISTANCE; }

    // Outside the box: dist(p, box) + boundaryFloor is a valid (scaled) lower bound on the distance to any interior zero.
    // Discontinuous at the box face, but legal under Boolean composition: the surface is strictly interior by the margin.
    if (any(local < 0.0) || any(local > float3(dims))) {
        float3 outside = max((boxMin - p), (p - (boxMin + extent)));

        return (length(max(outside, 0.0)) + data1.w);
    }

    // Sample centres sit at integer voxel indices (voxel i is centred at local = i + 0.5), so the stencil works in
    // (local - 0.5) space; the border half-voxel clamps to the edge voxel.
    float3 sampleCoord = (local - 0.5);
    float3 baseF = floor(sampleCoord);
    float3 f = (sampleCoord - baseF);
    int3 b = int3(baseF);

    float c000 = sdfSampledVoxel(baseWord, dims, (b + int3(0, 0, 0)));
    float c100 = sdfSampledVoxel(baseWord, dims, (b + int3(1, 0, 0)));
    float c010 = sdfSampledVoxel(baseWord, dims, (b + int3(0, 1, 0)));
    float c110 = sdfSampledVoxel(baseWord, dims, (b + int3(1, 1, 0)));
    float c001 = sdfSampledVoxel(baseWord, dims, (b + int3(0, 0, 1)));
    float c101 = sdfSampledVoxel(baseWord, dims, (b + int3(1, 0, 1)));
    float c011 = sdfSampledVoxel(baseWord, dims, (b + int3(0, 1, 1)));
    float c111 = sdfSampledVoxel(baseWord, dims, (b + int3(1, 1, 1)));

    // Interpolate x, then y, then z, with contraction disabled on each operation inside sdfSampledInterpolate.
    float c00 = sdfSampledInterpolate(c000, c100, f.x);
    float c10 = sdfSampledInterpolate(c010, c110, f.x);
    float c01 = sdfSampledInterpolate(c001, c101, f.x);
    float c11 = sdfSampledInterpolate(c011, c111, f.x);
    float c0 = sdfSampledInterpolate(c00, c10, f.y);
    float c1 = sdfSampledInterpolate(c01, c11, f.y);

    return sdfSampledInterpolate(c0, c1, f.z);
#else
    return SDF_FAR_DISTANCE;
#endif
}
// === end SDF_SHAPE_SAMPLED_REGION ====================================================================================

// === SDF_SHAPE_SWEEP: a quadratic Bezier curve swept with a tapering, bulging radius, optionally as helical strands
// (KEEP IN SYNC with Puck.SignedDistance.SdfProgramBuilder.Sweep / Puck.SignedDistance.Queries.SdfFieldEvaluator).
// One curve's table entry: 3 fixed uvec4 words at tableOffset in sdfWords — (A.xyz, radiusStart), (B.xyz,
// radiusEnd), (C.xyz, bulge). KEEP IN SYNC with SdfProgram.PackSweepCurves.
void sdfSweepCurve(uint tableOffset, out float3 a, out float3 b, out float3 c, out float radiusStart, out float radiusEnd, out float bulge) {
    uint4 wordsA = sdfProgramWord(tableOffset);
    uint4 wordsB = sdfProgramWord((tableOffset + 1u));
    uint4 wordsC = sdfProgramWord((tableOffset + 2u));

    a = asfloat(wordsA.xyz);
    radiusStart = asfloat(wordsA.w);
    b = asfloat(wordsB.xyz);
    radiusEnd = asfloat(wordsB.w);
    c = asfloat(wordsC.xyz);
    bulge = asfloat(wordsC.w);
}
precise float3 sdfBezierPoint(float3 a, float3 b, float3 c, float t) {
    float3 ab = lerp(a, b, t);
    float3 bc = lerp(b, c, t);

    return lerp(ab, bc, t);
}
precise float3 sdfBezierDerivative(float3 a, float3 b, float3 c, float t) {
    return (2.0 * (lerp((b - a), (c - b), t)));
}
// The closest parameter t in [0, 1] on the quadratic Bezier (a, b, c) to p — the standard closed-form
// closest-point-on-quadratic-bezier (a depressed-cubic solve, iq's construction), degenerating to the A-C line
// segment's own closest point when b is (numerically) the exact midpoint of a and c — the quadratic coefficient
// vanishes there and the cubic solve's 1/dot(coefficient, coefficient) would divide by zero.
precise float sdfSweepClosestT(float3 p, float3 a, float3 b, float3 c) {
    float3 coefA = (b - a);
    float3 coefB = ((a - (2.0 * b)) + c);
    float3 coefC = (coefA * 2.0);
    float3 d = (a - p);
    float bb = dot(coefB, coefB);

    if (bb < 1e-10) {
        float3 ac = (c - a);

        return saturate(dot((p - a), ac) / max(dot(ac, ac), 1e-20));
    }

    float kk = (1.0 / bb);
    float kx = (kk * dot(coefA, coefB));
    float ky = ((kk * ((2.0 * dot(coefA, coefA)) + dot(d, coefB))) / 3.0);
    float kz = (kk * dot(d, coefA));
    float p1 = (ky - (kx * kx));
    float p3 = ((p1 * p1) * p1);
    float q = ((kx * ((2.0 * kx * kx) - (3.0 * ky))) + kz);
    float h = ((q * q) + (4.0 * p3));

    if (h >= 0.0) {
        h = sqrt(h);

        float2 x = ((float2(h, -h) - q) * 0.5);
        float2 uv = (sign(x) * pow(abs(x), float2(1.0 / 3.0, 1.0 / 3.0)));

        return saturate((uv.x + uv.y) - kx);
    }

    float z = sqrt(-p1);
    float v = (acos(clamp((q / ((p1 * z) * 2.0)), -1.0, 1.0)) / 3.0);
    float m = cos(v);
    float n = (sin(v) * 1.7320508);
    float3 ts = saturate(((float3((m + m), (-n - m), (n - m)) * z) - kx));
    float3 qa = (d + ((coefC + (coefB * ts.x)) * ts.x));
    float3 qb = (d + ((coefC + (coefB * ts.y)) * ts.y));
    float3 qc = (d + ((coefC + (coefB * ts.z)) * ts.z));
    float da = dot(qa, qa);
    float db = dot(qb, qb);
    float dc = dot(qc, qc);
    float best = min(da, min(db, dc));

    return ((best == da) ? ts.x : ((best == db) ? ts.y : ts.z));
}
// The sweep's radius profile: a linear taper plus a mid-span bulge that vanishes at both ends (the exponent softens
// the bulge's rise off zero — KEEP IN SYNC with SdfProgramBuilder.Sweep's remarks).
precise float sdfSweepRadiusAt(float t, float radiusStart, float radiusEnd, float bulge) {
    float taper = lerp(radiusStart, radiusEnd, t);
    float s = max(sin((SDF_PI * t)), 0.0);

    return (taper + (bulge * pow(s, 0.65)));
}
// The general path's samples along t (SDF_SWEEP_SAMPLES, a power of two, so every sample's parameter is exact) and its
// golden-section rounds about the nearest sample (SDF_SWEEP_ROUNDS) are SdfFieldEvaluator's, generated into sdf-isa.hlsli.
// A strand's centre at t: the centreline point, orbited by strandOffset across the tangent at the phase the twist
// reaches there from the strand's own phase.
precise float3 sdfSweepStrandPoint(float3 a, float3 b, float3 c, float t, float twist, float strandOffset, float phase) {
    float3 base = sdfBezierPoint(a, b, c, t);

    if (strandOffset == 0.0) { return base; }

    float3 tangent = sdfBezierDerivative(a, b, c, t);
    float tangentLength = length(tangent);
    float3 tangentDir = ((tangentLength > 1e-8) ? (tangent / tangentLength) : float3(0.0, 1.0, 0.0));
    float3 refAxis = ((abs(tangentDir.y) < 0.999) ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0));
    float3 u = normalize(cross(tangentDir, refAxis));
    float3 v = cross(tangentDir, u);
    float sinP, cosP;

    sincos(((t * twist * SDF_TAU) + phase), sinP, cosP);

    return (base + (strandOffset * ((u * cosP) + (v * sinP))));
}
// The gap from p to the sphere a strand sweeps at t. Each gap is 1-Lipschitz in p, so their least over t is the swept
// solid's field: exact outside it, a bound inside.
precise float sdfSweepGap(float3 p, float3 a, float3 b, float3 c, float t, float twist, float strandOffset, float phase, float radiusStart, float radiusEnd, float bulge) {
    return (length(p - sdfSweepStrandPoint(a, b, c, t, twist, strandOffset, phase)) - sdfSweepRadiusAt(t, radiusStart, radiusEnd, bulge));
}
// The sweep is the union of the spheres of radius r(t) about each strand's centre. A tube of one radius about the
// centreline takes the closed-form nearest parameter (sdfSweepClosestT), exact for it. Any other sweep takes the least
// gap over t: the nearest of SDF_SWEEP_SAMPLES + 1 samples, then golden-section rounds within a sample either side of it,
// keeping the least gap any probe measured. The centreline's own nearest parameter is not the nearest sphere's once the
// radius or the strand varies along t: the radius read there overestimates the distance and jumps where that parameter
// crosses the centreline's medial axis.
precise float sdfSweep(float3 p, float4 data0, float4 data1) {
    uint tableOffset = asuint(data0.x);
    float strandsFloat = data0.y;
    float twist = data0.z;
    float strandOffset = data0.w;
    float3 a, b, c;
    float radiusStart, radiusEnd, bulge;

    sdfSweepCurve(tableOffset, a, b, c, radiusStart, radiusEnd, bulge);

    if (!isfinite(strandsFloat) || strandsFloat < 1.0 || strandsFloat > (float)SDF_MAX_SWEEP_STRANDS) { return SDF_FAR_DISTANCE; }
    if ((radiusStart == radiusEnd) && (bulge == 0.0) && (strandOffset == 0.0)) {
        return (length(p - sdfBezierPoint(a, b, c, sdfSweepClosestT(p, a, b, c))) - radiusStart);
    }

    uint strandCount = (uint)(strandsFloat + 0.5);
    float best = SDF_FAR_DISTANCE;
    float spacing = (1.0 / float(SDF_SWEEP_SAMPLES));

    for (uint strand = 0u; strand < SDF_MAX_SWEEP_STRANDS; strand++) {
        if (strand >= strandCount) { break; }

        float phase = ((float(strand) * SDF_TAU) / float(strandCount));
        float nearest = 0.0;
        float nearestGap = SDF_FAR_DISTANCE;

        [loop]
        for (uint probe = 0u; (probe <= SDF_SWEEP_SAMPLES); probe++) {
            float t = (float(probe) * spacing);
            float gap = sdfSweepGap(p, a, b, c, t, twist, strandOffset, phase, radiusStart, radiusEnd, bulge);

            if (gap < nearestGap) {
                nearestGap = gap;
                nearest = t;
            }
        }

        float low = max((nearest - spacing), 0.0);
        float high = min((nearest + spacing), 1.0);
        float left = lerp(low, high, 0.381966);
        float right = lerp(low, high, 0.618034);
        float leftGap = sdfSweepGap(p, a, b, c, left, twist, strandOffset, phase, radiusStart, radiusEnd, bulge);
        float rightGap = sdfSweepGap(p, a, b, c, right, twist, strandOffset, phase, radiusStart, radiusEnd, bulge);

        [loop]
        for (uint refinement = 0u; (refinement < SDF_SWEEP_ROUNDS); refinement++) {
            nearestGap = min(nearestGap, min(leftGap, rightGap));
            if (leftGap < rightGap) {
                high = right;
                right = left;
                rightGap = leftGap;
                left = lerp(low, high, 0.381966);
                leftGap = sdfSweepGap(p, a, b, c, left, twist, strandOffset, phase, radiusStart, radiusEnd, bulge);
            } else {
                low = left;
                left = right;
                leftGap = rightGap;
                right = lerp(low, high, 0.618034);
                rightGap = sdfSweepGap(p, a, b, c, right, twist, strandOffset, phase, radiusStart, radiusEnd, bulge);
            }
        }
        best = min(best, min(nearestGap, min(leftGap, rightGap)));
    }

    return best;
}
// === end SDF_SHAPE_SWEEP =============================================================================================

// The ONE shape dispatch. Single-return (a result variable rather than returning inside the switch) so the compiler's
// flow analysis sees the value is always initialized. data1's .x lane is the ISA-wide smooth-blend radius; lanes .yzw
// carry HOST-BAKED derived constants per shape (see SdfProgramBuilder). Ids that share a body FALL THROUGH: DXC inlines
// each `case` separately, so a duplicated arm duplicates the whole primitive (the Box/ScreenSlab and Polygon/Star pairs
// cost ~10% of this kernel's instructions when written twice).
precise float evaluateShape(uint shapeType, float3 p, float4 data0, float4 data1) {
    sdfWorkShapes++;
    float result = SDF_FAR_DISTANCE;

    switch (shapeType) {
#include "../isa/sdf-field-shapes.hlsli"
        default: return asfloat(0x7fc00000u);
    }

    return result;
}

#ifdef SDF_FIELD_DUAL
SDF_DUAL_LOCAL_TAPS(sdfPolyStar)
SDF_DUAL_LOCAL_TAPS(sdfConvexPolygonSolid)
SDF_DUAL_LOCAL_TAPS(sdfPathSolid)
SDF_DUAL_LOCAL_TAPS(sdfGlyphQuad)
#ifdef SDF_GLYPH_ATLAS
SDF_DUAL_LOCAL_TAPS(sdfGlyph)
#endif
SDF_DUAL_LOCAL_TAPS(sdfSampledRegion)
SDF_DUAL_LOCAL_TAPS(sdfSweep)
// The shape dispatch over a dual point: the same arms, each resolving to its formula's dual overload (or an escape's
// local taps above).
SdfDual evaluateShape(uint shapeType, SdfDual3 p, float4 data0, float4 data1) {
    sdfWorkShapes++;
    SdfDual result = sdfdLift(SDF_FAR_DISTANCE);

    switch (shapeType) {
#include "../isa/sdf-field-shapes.hlsli"
        default: return sdfdLift(asfloat(0x7fc00000u));
    }

    return result;
}
#endif
#endif
