// The mote pass, one invocation a mote of the frame's form bands (frame/sdf-form-band.hlsli), run by the composite's
// recorder before the composite while any mote flies. A mote is generated, never stored: every quantity below is a closed
// form of its band's seed, its index in the band and the band's elapsed time, hashed by the integer PCG3D
// (field/sdf-hash.hlsli), so it reads the same on every backend and frame order never matters.
// - Its surface point is a point on its band's real surface as this frame draws it: one of the draw rows riding the band's
//   slots (SdfWorldTables.PackFormBandRows), a triangle of it and a point in the triangle, each posed through the mesh
//   door (sdfMeshWorldPosition: skin, displacement, pose and carry).
// - Its birth is when the band's front reaches that point (sdfFormBandAhead, the cut's own formula), so a mote leaves the
//   surface exactly where the edge dissolves it, or settles onto it exactly as the edge builds it.
// - Its path from the point is a drift along a hashed direction leaning along the band's axis, an acceleration along world
//   up and a swirl about the axis, walked backwards in time for a band that builds (its motes fly in).
// A visible mote (in front of the surface the composite composes at its pixel, sdfSkyPassSurface) adds its light, by the
// share of a texel its disc covers, into the motes' light (frame/sdf-mote-light.hlsli), which the composite adds before
// the exposure, so a mote glows through the bloom like any bright light.
#include "sdf-sky-pass.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-form-band.hlsli"
#include "../frame/sdf-mote-light.hlsli"

// The band whose motes hold the frame's mote `index`: the last band whose first mote is at or before it, in the order the
// tables wrote them (each band's first mote counts the motes of every band before it).
int sdfMoteBandOf(uint index, uint count) {
    int low = 0;
    int high = (int(count) - 1);
    int found = -1;

    [loop]
    for (uint step = 0u; ((step < 11u) && (low <= high)); step++) {
        int middle = ((low + high) >> 1);

        if (sdfFrameSampled[sdfFormBandWord(uint(middle)) + SDF_FORM_BAND_MOTE_WORD + 1u] <= index) {
            found = middle;
            low = (middle + 1);
        } else {
            high = (middle - 1);
        }
    }
    return found;
}
// A unit direction from two uniform numbers.
float3 sdfMoteDirection(float u, float v) {
    float z = ((2.0 * u) - 1.0);
    float r = sqrt(saturate(1.0 - (z * z)));
    float phi = (6.28318530718 * v);

    return float3((r * cos(phi)), (r * sin(phi)), z);
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint bands = sdfFormBandCount();

    if ((bands == 0u) || !sdfMoteLightBound()) { return; }

    uint total = min(sdfFrameSampled[SDF_FRAME_SAMPLED_MOTE_TOTAL_WORD], SDF_FORM_BAND_MAX_MOTES);
    uint index = id.x;

    if (index >= total) { return; }

    int found = sdfMoteBandOf(index, bands);

    if (found < 0) { return; }

    uint band = uint(found);
    uint word = sdfFormBandWord(band);
    uint first = sdfFrameSampled[word + SDF_FORM_BAND_MOTE_WORD + 1u];
    uint mote = (index - first);

    if (mote >= sdfFrameSampled[word + SDF_FORM_BAND_MOTE_WORD]) { return; }

    uint rows = min(sdfFrameSampled[word + SDF_FORM_BAND_MOTION_WORD + 2u], SDF_FORM_BAND_ROW_CAPACITY);

    if (rows == 0u) { return; }

    uint seed = sdfFrameSampled[word + SDF_FORM_BAND_SEED_WORD];
    uint3 h0 = sdfPcg3d(uint3(seed, mote, 0x6D6F7465u));
    uint3 h1 = sdfPcg3d(h0 ^ uint3(0x9E3779B9u, 0x85EBCA6Bu, mote));
    float3 u0 = (float3(h0) * SDF_INV_2POW32);
    float3 u1 = (float3(h1) * SDF_INV_2POW32);

    // The surface point: a row, a triangle of its whole mesh, and a point in the triangle.
    uint row = sdfFrameSampled[word + SDF_FORM_BAND_ROWS_WORD + (h0.x % rows)];

    if (row >= passGroup.meshDraws) { return; }

    uint record = sdfMeshRecord(row);

    if ((sdfMeshRecordWord(record + SDF_MESH_SELECTION_WORD + 3u) & SDF_MESH_ABSENT_FLAG) != 0u) { return; }

    uint triangles = (sdfMeshRecordWord(record + SDF_MESH_INDEX_COUNT_WORD) / 3u);

    if (triangles == 0u) { return; }

    uint corner = (3u * (h0.y % triangles));
    float3 a = sdfMeshWorldPosition(record, corner);
    float3 b = sdfMeshWorldPosition(record, (corner + 1u));
    float3 c = sdfMeshWorldPosition(record, (corner + 2u));
    float2 r = u1.xy;

    if ((r.x + r.y) > 1.0) { r = (1.0 - r); }

    float3 home = ((a + ((b - a) * r.x)) + ((c - a) * r.y));

    // Its birth: the moment the front reaches the point, from the front's place now.
    float4 origin = sdfFormBandFloats(word + SDF_FORM_BAND_ORIGIN_WORD);
    float4 axis = sdfFormBandFloats(word + SDF_FORM_BAND_AXIS_WORD);
    float4 sweep = sdfFormBandFloats(word + SDF_FORM_BAND_SWEEP_WORD);
    float span = max((axis.w - origin.w), 1.0e-4);
    float front = (origin.w + ((axis.w - origin.w) * saturate(sweep.z / max(sweep.w, 1.0e-6))));
    float reached = (sdfFormBandAhead(band, home) + front);
    float birth = (sweep.w * saturate((reached - origin.w) / span));
    float life = asfloat(sdfFrameSampled[word + SDF_FORM_BAND_MOTE_WORD + 2u]);
    float speed = asfloat(sdfFrameSampled[word + SDF_FORM_BAND_MOTE_WORD + 3u]);
    float rise = asfloat(sdfFrameSampled[word + SDF_FORM_BAND_MOTION_WORD]);
    float swirl = asfloat(sdfFrameSampled[word + SDF_FORM_BAND_MOTION_WORD + 1u]);
    bool swept = ((sdfFormBandFlags(band) & SDF_FORM_BAND_SWEPT_FLAG) != 0u);
    // How long the mote has flown from its point: a leaving mote counts up from its birth, an arriving one counts down to
    // it, so its path is the same curve walked the other way.
    float age = (swept ? (birth - sweep.z) : (sweep.z - birth));

    if (!(age > 0.0) || !(age < life)) { return; }

    // Its path: a drift leaning along the axis at a hashed share of the speed, a rise along world up, and a swirl about
    // the axis that grows with its age.
    float3 direction = normalize(sdfMoteDirection(u0.z, u1.z) + (axis.xyz * 0.6));
    float3 offset = ((direction * (speed * (0.5 + u0.x) * age)) + (float3(0.0, 1.0, 0.0) * (0.5 * rise * age * age)));
    float turn = (swirl * age);
    float cosine = cos(turn);
    float sine = sin(turn);
    float3 n = axis.xyz;

    offset = (((offset * cosine) + (cross(n, offset) * sine)) + (n * (dot(n, offset) * (1.0 - cosine))));

    float3 p = (home + offset);
    float k = (age / life);
    float twinkle = (0.8 + (0.2 * sin((age * 23.0) + (u1.z * 6.28318530718))));
    float4 light = sdfFormBandFloats(word + SDF_FORM_BAND_MOTE_LIGHT_WORD);
    float3 emitted = (light.xyz * (((1.0 - k) * (1.0 - k)) * twinkle));

    if (!any(emitted > 0.0)) { return; }

    // Where it lands on screen, by the view's own projection without its jitter (sdfSkyPassView), and whether the surface
    // the composite composes there stands in front of it.
    ViewportData view = sdfSkyPassView();
    float3 relative = (p - view.position.xyz);
    float w = dot(relative, view.forward.xyz);

    if (w <= worldNearDistance(view)) { return; }

    float tangent = view.right.w;
    float aspect = view.up.w;
    float2 ndc = float2((((dot(relative, view.right.xyz) / w) - view.lens.y) / (aspect * tangent)), (((dot(relative, view.up.xyz) / w) - view.lens.z) / tangent));
    float2 uv = float2(((ndc.x * 0.5) + 0.5), (0.5 - (ndc.y * 0.5)));

    if (any(uv < 0.0) || any(uv >= 1.0)) { return; }

    float2 pixel = (uv * float2(passGroup.extent));
    uint2 covered = min(uint2(pixel), (passGroup.extent - 1u));
    float3 weights;
    float t;

    sdfSkyPassSurface(covered, sdfSkyPassLit(int2(covered)).a, view.position.xyz, sdfSkyPassDirection(view, covered), weights, t);
    if ((t > 0.0) && (length(relative) > (t + light.w))) { return; }

    // Its disc's share of a texel of the half-extent grid, at most four texels' worth for a mote near the eye.
    float radius = ((((0.5 * light.w) / (w * tangent)) * (0.5 * float(passGroup.extent.y))) * 0.5);
    float area = min((3.14159265359 * radius * radius), 4.0);

    sdfMoteLightSplat(pixel, (emitted * area));
}
