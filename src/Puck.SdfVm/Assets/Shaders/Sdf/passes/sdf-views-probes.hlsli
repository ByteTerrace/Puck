// The probe lookup at the view's own rate (SdfWorldPackage.Parts.ViewsProbes): the irradiance volume's gather
// (sdfMeshProbesAt) evaluated for each cell of SDF_VIEWS_PROBE_RATE by SDF_VIEWS_PROBE_RATE pixels at one representative
// pixel of the cell, and at each other pixel of the cell whose receiver is not compatible with the representative's
// (sdfViewsProbesCompatible), each written into the cell's probe record. Views reads the gather its pixel's record names
// (sdfViewsProbesAt) and never gathers: the gather is the expensive part of the lookup (each corner's angular surface
// test) and the largest code in the volume, so it runs in the lookup's own lean kernel, balanced across the lookup's
// group, rather than in the shading kernel, where a quarter of foliage pixels gathering made every warp pay a whole gather
// at the shading kernel's occupancy. The cube reads that follow a gather stay per shading sample, along each pixel's own
// shading normal and mirror direction.
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
//     Bits 29 to 31 say which of the cell's other pixels, in raster order skipping the representative, hold a gather of
//     their own in the extra slot of that index.
// 11 to 31: the gather's six-axis cubes blended by its weights as the record holds them (sdfMeshProbeCube), in a probe
//     slot's cube packing (sdfMeshCubeRead): the sky shares in 11 to 13, the radiance in 14 to 22 and the sky light in 23
//     to 31, as half floats. A pixel that reuses the record reads its indirect light and reflection from these words, three
//     faces a direction, rather than every corner's cube; the cube is linear in its faces, so the answer is the corners'
//     up to the order of the sums and the halves' rounding.
// 32 onward: SDF_VIEWS_PROBE_RATE^2 - 1 extra slots of SdfViewsProbeExtraWords words, one for each pixel of the cell but the
//     representative: words 0 to 9 as words 0 to 9 above, and word 10 the share and measured flags as in the state word. A
//     pixel with its own gather reads its indirect light and reflection from its corners' cubes.
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
static const uint SdfViewsProbeExtraShift = 29u;
static const uint SdfViewsProbeExtraWord = 32u;
static const uint SdfViewsProbeExtraWords = 11u;

// Whether a receiver at `offset` from a representative whose normal is `representativeNormal`, with its own geometric
// normal `normal`, may read the representative's gather (the head of this file): within the reach, on its tangent plane
// and facing as it does. The probe lookup decides with it which pixels of a cell gather for themselves.
bool sdfViewsProbesCompatible(float3 offset, float3 representativeNormal, float3 normal) {
    return ((dot(offset, offset) <= (SdfViewsProbeReach * SdfViewsProbeReach))
        && (abs(dot(offset, representativeNormal)) <= SdfViewsProbePlane)
        && (dot(normal, representativeNormal) >= SdfViewsProbeFacing));
}
// The extra slot a non-representative pixel of a cell reads: its place among the cell's pixels in raster order with the
// representative's skipped. `pixel` and `representative` are each a pixel's place in the cell, x + SDF_VIEWS_PROBE_RATE * y.
uint sdfViewsProbeExtraSlot(uint pixel, uint representative) {
    return (pixel - ((pixel > representative) ? 1u : 0u));
}

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

// A gather's share and measured flags, as the state word and an extra slot's last word hold them.
uint sdfViewsProbesFlags(SdfMeshProbes probes) {
    return ((uint)round(saturate(probes.share) * 65535.0)
        | (probes.measured ? SdfViewsProbeMeasured : 0u) | (probes.remainderMeasured ? SdfViewsProbeRemainderMeasured : 0u));
}

#if defined(SDF_VIEWS_PROBES_PASS)
// Writes a gather's cages and weights from `at` (a record's first word or an extra slot's) and returns the probes as a
// reader of those words holds them, the weights quantized.
SdfMeshProbes sdfViewsProbesStoreGather(uint at, SdfMeshProbes probes) {
    SdfMeshProbes held = probes;

    sdfViewsProbeRecordBuffer[at] = probes.cage;
    sdfViewsProbeRecordBuffer[at + 1u] = probes.remainderCage;
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint low = (uint)round(saturate(probes.weight[2u * pair]) * 65535.0);
        uint high = (uint)round(saturate(probes.weight[(2u * pair) + 1u]) * 65535.0);

        held.weight[2u * pair] = (float(low) / 65535.0);
        held.weight[(2u * pair) + 1u] = (float(high) / 65535.0);
        sdfViewsProbeRecordBuffer[at + 2u + pair] = (low | (high << 16u));
    }
    return held;
}
// Writes the gather of the cell's non-representative pixel that reads extra slot `slot`.
void sdfViewsProbesStoreExtra(uint record, uint slot, SdfMeshProbes probes) {
    uint at = (record + SdfViewsProbeExtraWord + (slot * SdfViewsProbeExtraWords));

    sdfViewsProbesStoreGather(at, probes);
    sdfViewsProbeRecordBuffer[at + SdfViewsProbeStateWord] = sdfViewsProbesFlags(probes);
}
// Writes a cell's record: the representative's gather, its pixel within the cell and which other pixels hold gathers of
// their own (`extras`, a bit an extra slot), or with `present` false a cell whose every record word but the state is left
// as it was.
void sdfViewsProbesStore(uint record, SdfMeshProbes probes, uint lane, bool present, uint rateMask, uint extras) {
    uint rate = rateMask << SdfViewsRateShift;
    if (!present) {
        sdfViewsProbeRecordBuffer[record + SdfViewsProbeStateWord] = rate;
        return;
    }
    // The cube blends the weights as a reader of the record holds them, quantized.
    SdfMeshProbes held = sdfViewsProbesStoreGather(record, probes);

    sdfViewsProbeRecordBuffer[record + SdfViewsProbeStateWord] = (sdfViewsProbesFlags(probes)
        | (lane << SdfViewsProbeLaneShift) | SdfViewsProbePresent | rate | (extras << SdfViewsProbeExtraShift));

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
// Reads the gather whose cages and weights start at `at` (a record's first word or an extra slot's), with the share and
// measured flags of `flags`.
SdfMeshProbes sdfViewsProbesLoadGather(uint at, uint flags) {
    SdfMeshProbes probes = sdfMeshProbesNone();

    probes.cage = sdfViewsProbeRecordBuffer[at];
    probes.remainderCage = sdfViewsProbeRecordBuffer[at + 1u];
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint word = sdfViewsProbeRecordBuffer[at + 2u + pair];

        probes.weight[2u * pair] = (float(word & 0xFFFFu) / 65535.0);
        probes.weight[(2u * pair) + 1u] = (float(word >> 16u) / 65535.0);
    }
    probes.share = (float(flags & 0xFFFFu) / 65535.0);
    probes.measured = ((flags & SdfViewsProbeMeasured) != 0u);
    probes.remainderMeasured = ((flags & SdfViewsProbeRemainderMeasured) != 0u);
    return probes;
}

// The probes a views pixel shades with, as its cell's record names them: the representative's gather, with its blended
// cube, for the representative and every pixel compatible with it, and a pixel's own gather (the probe lookup ran it for
// the pixel, sdfViewsProbesCompatible) for every other; none for a cell with no representative, which no pixel of the cell
// wants (sdfViewsProbesWanted).
SdfMeshProbes sdfViewsProbesAt(SdfPixel p) {
#if defined(SDF_VIEWS_OMIT_PROBES)
    return sdfMeshProbesNone();
#else
    uint record = sdfViewsProbeRecord((p.pixel / SDF_VIEWS_PROBE_RATE), p.viewIndex);
    uint state = sdfViewsProbeRecordBuffer[record + SdfViewsProbeStateWord];

    if ((state & SdfViewsProbePresent) == 0u) {
        return sdfMeshProbesNone();
    }

    uint2 corner = (p.pixel % SDF_VIEWS_PROBE_RATE);
    uint lane = ((state >> SdfViewsProbeLaneShift) & 0xFFu);
    uint place = (corner.x + (SDF_VIEWS_PROBE_RATE * corner.y));

    if (place != lane) {
        uint slot = sdfViewsProbeExtraSlot(place, lane);

        if (((state >> (SdfViewsProbeExtraShift + slot)) & 1u) != 0u) {
            uint at = (record + SdfViewsProbeExtraWord + (slot * SdfViewsProbeExtraWords));

            return sdfViewsProbesLoadGather(at, sdfViewsProbeRecordBuffer[at + SdfViewsProbeStateWord]);
        }
    }

    SdfMeshProbes probes = sdfViewsProbesLoadGather(record, state);

    probes.cube = (record + 1u);
    return probes;
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
