// The probe lookup at the view's own rate (SdfWorldPackage.Parts.ViewsProbes): the irradiance volume's gather
// (sdfMeshProbesAt) evaluated once for each cell of SDF_VIEWS_PROBE_RATE by SDF_VIEWS_PROBE_RATE pixels, at one
// representative pixel of the cell, and written into the cell's probe record; views then reads the record for every pixel
// of the cell whose receiver is compatible with the representative's, and gathers for itself only where it is not
// (sdfViewsProbesAt). The gather is the expensive part of the lookup (each corner's angular surface test), while the
// cube reads that follow it stay per shading sample, along each pixel's own shading normal and mirror direction.
//
// What a reused gather may differ in: the representative's trilinear shares, facing fade and surface visibility, taken at
// a receiver within SdfViewsProbeReach of the pixel's, on the same tangent plane within SdfViewsProbePlane and facing within
// SdfViewsProbeFacing of it. The shares move at the probes' spacing and the facing fade at half of it, both metres or more;
// the surface test closes over SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE across a surface a probe measured, and a receiver
// that stays on its own plane does not cross the plane of a surface beside it. A pixel across a depth or crease edge from
// its representative fails the test and gathers exactly, so the lookup never steps across a visibility edge it can see in
// the record. The representative itself reads its own gather.
//
// A record is SDF_VIEWS_PROBE_WORDS words, addressed by cell like the visibility record by pixel:
// 0: the chosen stage's cage (SdfMeshProbes.cage).
// 1: the remainder stage's cage.
// 2 to 9: the sixteen corner weights as UNORM16, corner 2k in the low half of word 2 + k and corner 2k + 1 in its high half.
// 10: the chosen stage's share as UNORM16 in bits 0 to 15, its measured flag in bit 16, the remainder's in bit 17, the
//     representative's pixel within the cell (x + SDF_VIEWS_PROBE_RATE * y) in bits 18 to 25, and in bit 26 whether a
//     representative was gathered at all (a cell with no pixel that wants the lookup holds none). Bits 27..28 select shared shading along x and y.
// 11 to 31: the gather's six-axis cubes blended by its weights as the record holds them (sdfMeshProbeCube), in a probe
//     slot's cube packing (sdfMeshCubeRead): the sky shares in 11 to 13, the radiance in 14 to 22 and the sky light in 23
//     to 31, as half floats. A pixel that reuses the record reads its indirect light and reflection from these words, three
//     faces a direction, rather than every corner's cube; the cube is linear in its faces, so the answer is the corners'
//     up to the order of the sums and the halves' rounding.
#ifndef PASSES_SDF_VIEWS_PROBES_HLSLI
#define PASSES_SDF_VIEWS_PROBES_HLSLI
#include "../shade/sdf-mesh-irradiance.hlsli"

#if defined(SDF_VIEWS_PROBES_PASS)
#define sdfViewsProbeRecordBuffer sdfViewsProbeRecordsRW
#else
#define sdfViewsProbeRecordBuffer sdfViewsProbeRecords
#include "../shade/sdf-light.hlsli"
#endif

// The reuse test's tolerances (see the head of this file): the distance a reused receiver may lie from the pixel's, which
// is the normal bias every receiver is already pushed by; its distance off the representative's tangent plane, a quarter
// of the surface test's closing ramp; and the cosine its normal keeps with the pixel's.
static const float SdfViewsProbeReach = SDF_MESH_IRRADIANCE_NORMAL_BIAS;
static const float SdfViewsProbePlane = (0.25 * SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE);
static const float SdfViewsProbeFacing = 0.995;

static const uint SdfViewsProbeStateWord = 10u;
static const uint SdfViewsProbeSkyWord = 11u;
static const uint SdfViewsProbeRadianceWord = 14u;
static const uint SdfViewsProbeSkyLightWord = 23u;
static const uint SdfViewsProbeMeasured = (1u << 16u);
static const uint SdfViewsProbeRemainderMeasured = (1u << 17u);
static const uint SdfViewsProbeLaneShift = 18u;
static const uint SdfViewsRateShift = 27u;
static const uint SdfViewsProbePresent = (1u << 26u);

// Whether a pixel shades with the irradiance volume: a hit of an ordinary material in a view that shades finally, or any
// hit under the indirect debug view, which shows the volume's light on every surface.
bool sdfViewsProbesWanted(int viewMode, int material) {
    return (((material < SDF_SCREEN_MATERIAL) && worldFinalShadingMode(viewMode)) || (viewMode == SDF_DEBUG_VIEW_INDIRECT));
}

// The cells a view's records cover along each axis: the view's 8x8 tiles at SDF_VISIBILITY_GROUP_EDGE /
// SDF_VIEWS_PROBE_RATE cells a side, as SdfWorldPackage allocates them per tile of the extent.
uint2 sdfViewsProbeCells() {
    uint2 tiles = ((passGroup.imageExtent + (SDF_VISIBILITY_GROUP_EDGE - 1u)) / SDF_VISIBILITY_GROUP_EDGE);

    return (tiles * (SDF_VISIBILITY_GROUP_EDGE / SDF_VIEWS_PROBE_RATE));
}
uint sdfViewsProbeRecord(uint2 cell, uint viewIndex) {
    uint2 cells = sdfViewsProbeCells();

    return (SDF_VIEWS_PROBE_WORDS * ((((viewIndex * cells.y) + cell.y) * cells.x) + cell.x));
}

#if defined(SDF_VIEWS_PROBES_PASS)
// Writes a cell's record: the representative's gather and its pixel within the cell, or with `present` false a cell
// whose every record word but the state is left as it was.
void sdfViewsProbesStore(uint record, SdfMeshProbes probes, uint lane, bool present, uint rateMask) {
    uint rate = rateMask << SdfViewsRateShift;
    if (!present) {
        sdfViewsProbeRecordBuffer[record + SdfViewsProbeStateWord] = rate;
        return;
    }
    sdfViewsProbeRecordBuffer[record] = probes.cage;
    sdfViewsProbeRecordBuffer[record + 1u] = probes.remainderCage;
    // The cube blends the weights as a reader of the record holds them, quantized.
    SdfMeshProbes held = probes;
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint low = (uint)round(saturate(probes.weight[2u * pair]) * 65535.0);
        uint high = (uint)round(saturate(probes.weight[(2u * pair) + 1u]) * 65535.0);

        held.weight[2u * pair] = (float(low) / 65535.0);
        held.weight[(2u * pair) + 1u] = (float(high) / 65535.0);
        sdfViewsProbeRecordBuffer[record + 2u + pair] = (low | (high << 16u));
    }
    sdfViewsProbeRecordBuffer[record + SdfViewsProbeStateWord] = ((uint)round(saturate(probes.share) * 65535.0)
        | (probes.measured ? SdfViewsProbeMeasured : 0u) | (probes.remainderMeasured ? SdfViewsProbeRemainderMeasured : 0u)
        | (lane << SdfViewsProbeLaneShift) | SdfViewsProbePresent | rate);

    float sky[6];
    float3 radiance[6];
    float3 skyLight[6];

    sdfMeshProbeCube(held, sky, radiance, skyLight);
    [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
        sdfViewsProbeRecordBuffer[record + SdfViewsProbeSkyWord + axis] = (f32tof16(sky[2u * axis]) | (f32tof16(sky[(2u * axis) + 1u]) << 16u));
    }
    [unroll] for (uint word = 0u; (word < 9u); word++) {
        uint low = (2u * word);
        uint high = (low + 1u);

        sdfViewsProbeRecordBuffer[record + SdfViewsProbeRadianceWord + word] =
            (f32tof16(radiance[low / 3u][low % 3u]) | (f32tof16(radiance[high / 3u][high % 3u]) << 16u));
        sdfViewsProbeRecordBuffer[record + SdfViewsProbeSkyLightWord + word] =
            (f32tof16(skyLight[low / 3u][low % 3u]) | (f32tof16(skyLight[high / 3u][high % 3u]) << 16u));
    }
}
#else
// Reads a cell's gather; false for a cell with no representative.
bool sdfViewsProbesLoad(uint record, out SdfMeshProbes probes, out uint lane) {
    uint state = sdfViewsProbeRecordBuffer[record + SdfViewsProbeStateWord];

    probes = sdfMeshProbesNone();
    lane = ((state >> SdfViewsProbeLaneShift) & 0xFFu);
    if ((state & SdfViewsProbePresent) == 0u) {
        return false;
    }
    probes.cage = sdfViewsProbeRecordBuffer[record];
    probes.remainderCage = sdfViewsProbeRecordBuffer[record + 1u];
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint word = sdfViewsProbeRecordBuffer[record + 2u + pair];

        probes.weight[2u * pair] = (float(word & 0xFFFFu) / 65535.0);
        probes.weight[(2u * pair) + 1u] = (float(word >> 16u) / 65535.0);
    }
    probes.share = (float(state & 0xFFFFu) / 65535.0);
    probes.measured = ((state & SdfViewsProbeMeasured) != 0u);
    probes.remainderMeasured = ((state & SdfViewsProbeRemainderMeasured) != 0u);
    return true;
}

// The probes a views pixel shades with: its cell's gather where the representative's receiver is compatible with this
// pixel's (the head of this file), otherwise its own (sdfMeshProbesAt). `normal` is the hit's geometric normal, the one the
// probe lookup gathered the representative at.
SdfMeshProbes sdfViewsProbesAt(SdfPixel p, float3 surfacePoint, float3 normal) {
#if defined(SDF_VIEWS_OMIT_PROBES)
    return sdfMeshProbesNone();
#else
#if !defined(SDF_VIEWS_OMIT_PROBE_SHARING)
    uint2 cell = (p.pixel / SDF_VIEWS_PROBE_RATE);
    SdfMeshProbes cellProbes;
    uint lane;

    uint cellRecord = sdfViewsProbeRecord(cell, p.viewIndex);

    if (sdfViewsProbesLoad(cellRecord, cellProbes, lane)) {
        uint2 representative = ((cell * SDF_VIEWS_PROBE_RATE) + uint2((lane % SDF_VIEWS_PROBE_RATE), (lane / SDF_VIEWS_PROBE_RATE)));

        cellProbes.cube = (cellRecord + 1u);

        if (all(representative == p.pixel)) {
            return cellProbes;
        }

        uint record = worldVisibilityRecord(representative, p.viewIndex);
        float t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);
        float3 representativeNormal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);
        float3 direction = cameraRayDirection(p.view, ((float2(representative) + 0.5) / float2(worldViewDims(p.view))));
        precise float3 travel = (direction * t);
        precise float3 representativePoint = (p.rayOrigin + travel);
        float3 offset = (surfacePoint - representativePoint);

        if ((dot(offset, offset) <= (SdfViewsProbeReach * SdfViewsProbeReach))
            && (abs(dot(offset, representativeNormal)) <= SdfViewsProbePlane)
            && (dot(normal, representativeNormal) >= SdfViewsProbeFacing)) {
            return cellProbes;
        }
    }
#endif
    return sdfMeshProbesAt(surfacePoint, normal);
#endif
}

// The views pass's indirect light and reflection under the irradiance volume (sdfMeshIndirectSeen, sdfMeshReflectionSeen):
// from the record's blended cube where the probes are a record's (SdfMeshProbes.cube), otherwise from their corners.
void sdfViewsCubeAt(SdfMeshProbes probes, float3 direction, out float sky, out float3 radiance, out float3 skyLight) {
    uint record = (probes.cube - 1u);

    sdfMeshCubeRead(sdfViewsProbeRecordBuffer, (record + SdfViewsProbeSkyWord), (record + SdfViewsProbeRadianceWord),
        (record + SdfViewsProbeSkyLightWord), direction, sky, radiance, skyLight);
}
float3 sdfViewsIndirectSeen(SdfMeshProbes probes, float3 normal) {
    if ((probes.cube != 0u) && sdfMeshAnswers(probes)) {
        float sky;
        float3 radiance;
        float3 skyLight;

        sdfViewsCubeAt(probes, normal, sky, radiance, skyLight);
        return (skyLight + radiance);
    }
    return sdfMeshIndirectSeen(probes, normal);
}
float3 sdfViewsReflectionSeen(SdfMeshProbes probes, float3 direction, float3 environment) {
    if ((probes.cube != 0u) && sdfMeshAnswers(probes)) {
        float sky;
        float3 radiance;
        float3 skyLight;

        sdfViewsCubeAt(probes, direction, sky, radiance, skyLight);
        return ((environment * sky) + radiance);
    }
    return sdfMeshReflectionSeen(probes, direction, environment);
}
#endif

#endif
