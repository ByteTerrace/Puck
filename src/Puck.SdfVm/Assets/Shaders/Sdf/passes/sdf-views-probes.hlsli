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
// a receiver within the reach of the pixel's, on the same tangent plane (both sdfViewsProbeTolerance) and facing within
// SdfViewsProbeFacing of it. The shares move at the probes' spacing and the facing fade at half of it, both metres or more;
// the surface test closes over SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE across a surface a probe measured, and a receiver
// that stays on its own plane does not cross the plane of a surface beside it. A pixel across a depth or crease edge from
// its representative fails the test and gathers exactly, so the lookup never steps across a visibility edge it can see in
// the record. The representative itself reads its own gather.
//
// A record is SDF_VIEWS_PROBE_WORDS words, addressed by cell, each word in a plane of its own (sdfViewsProbeWord):
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
// SdfViewsProbeLightsWord onward: the representative's traced-light agreement (sdfViewsLightVisibility): four words of
//     the light bits every measured answering corner holds, four of the bits any holds, and the sum of those corners'
//     weights as a float, summed in corner order from the weights as the record holds them.
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
static const float SdfViewsProbeFacing = 0.98;
// The least cosine a pixel's footprint is stretched by along the surface it meets, so a surface seen edge-on shares no
// farther than eight footprints.
static const float SdfViewsProbeGrazing = 0.125;

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
#if SDF_MESH_IRRADIANCE_LIGHT_WORDS != 4
#error The record's light agreement holds a uint4 of light bits.
#endif
static const uint SdfViewsProbeLightsWord = (SdfViewsProbeExtraWord + (((SDF_VIEWS_PROBE_RATE * SDF_VIEWS_PROBE_RATE) - 1u) * SdfViewsProbeExtraWords));
static const uint SdfViewsProbeLightsAnyWord = (SdfViewsProbeLightsWord + SDF_MESH_IRRADIANCE_LIGHT_WORDS);
static const uint SdfViewsProbeLightsWeightWord = (SdfViewsProbeLightsAnyWord + SDF_MESH_IRRADIANCE_LIGHT_WORDS);

// The tolerances a reused gather is held to at a receiver whose pixel spans `footprint` metres across its ray, met at
// `cosine` (the absolute cosine between the ray and the receiver's geometric normal). In `x`, the reach it may lie from
// the receiver: the normal bias, or two pixels measured along the surface where those are wider, since a cell's
// representative stands up to a pixel's diagonal from any pixel of its cell and the preceding frame's a pixel further,
// and a pixel met at a grazing angle spans its footprint over the cosine along the surface (within SdfViewsProbeGrazing).
// In `y`, its distance off the representative's tangent plane: SdfViewsProbePlane, or half a pixel where that is wider.
// Where two pixels span more than the bias, a cell's pixels show the volume's light at the cell's own resolution on the
// screen, as the representative already shows it for the pixels it serves, and the plane and facing tests still keep a
// receiver from reading a gather across a crease or a depth edge.
float2 sdfViewsProbeTolerance(float footprint, float cosine) {
    return float2(max(SdfViewsProbeReach, ((2.0 * footprint) / max(cosine, SdfViewsProbeGrazing))), max(SdfViewsProbePlane, (0.5 * footprint)));
}
// Whether a receiver at `offset` from a representative whose normal is `representativeNormal`, with its own geometric
// normal `normal`, may read the representative's gather (the head of this file): within the reach of `tolerance`
// (sdfViewsProbeTolerance), on its tangent plane within the tolerance's plane distance, and facing as it does. The probe
// lookup decides with it which pixels of a cell gather for themselves, and which receivers reuse a preceding frame's
// gather (sdf-views-probe-history.hlsli).
bool sdfViewsProbesCompatible(float3 offset, float3 representativeNormal, float3 normal, float2 tolerance) {
    return ((dot(offset, offset) <= (tolerance.x * tolerance.x))
        && (abs(dot(offset, representativeNormal)) <= tolerance.y)
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
// A cell's record: its place among every view's cells, raster order within a view.
uint sdfViewsProbeRecord(uint2 cell, uint viewIndex) {
    uint2 cells = sdfViewsProbeCells();

    return ((((viewIndex * cells.y) + cell.y) * cells.x) + cell.x);
}
// The buffer index of word `word` of record `record`: a plane holds one word of every record, so the lanes of a wave,
// which take neighbouring cells or pixels, read and write each word in one run of the buffer rather than one sector a lane.
uint sdfViewsProbeWord(uint record, uint word) {
    uint count;
    uint stride;

    sdfViewsProbeRecordBuffer.GetDimensions(count, stride);
    return ((word * (count / SDF_VIEWS_PROBE_WORDS)) + record);
}

// A gather's share and measured flags, as the state word and an extra slot's last word hold them.
uint sdfViewsProbesFlags(SdfMeshProbes probes) {
    return ((uint)round(saturate(probes.share) * 65535.0)
        | (probes.measured ? SdfViewsProbeMeasured : 0u) | (probes.remainderMeasured ? SdfViewsProbeRemainderMeasured : 0u));
}

#if defined(SDF_VIEWS_PROBES_PASS)
// Writes a gather's cages and weights into `record` from its word `at` (zero, or an extra slot's first) and returns the
// probes as a reader of those words holds them, the weights quantized.
SdfMeshProbes sdfViewsProbesStoreGather(uint record, uint at, SdfMeshProbes probes) {
    SdfMeshProbes held = probes;

    sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, at)] = probes.cage;
    sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (at + 1u))] = probes.remainderCage;
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint low = (uint)round(saturate(probes.weight[2u * pair]) * 65535.0);
        uint high = (uint)round(saturate(probes.weight[(2u * pair) + 1u]) * 65535.0);

        held.weight[2u * pair] = (float(low) / 65535.0);
        held.weight[(2u * pair) + 1u] = (float(high) / 65535.0);
        sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (at + 2u + pair))] = (low | (high << 16u));
    }
    return held;
}
// Writes the gather of the cell's non-representative pixel that reads extra slot `slot`.
void sdfViewsProbesStoreExtra(uint record, uint slot, SdfMeshProbes probes) {
    uint at = (SdfViewsProbeExtraWord + (slot * SdfViewsProbeExtraWords));

    sdfViewsProbesStoreGather(record, at, probes);
    sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (at + SdfViewsProbeStateWord))] = sdfViewsProbesFlags(probes);
}
// Writes a cell's record: the representative's gather, its pixel within the cell and which other pixels hold gathers of
// their own (`extras`, a bit an extra slot), or with `present` false a cell whose every record word but the state is left
// as it was.
void sdfViewsProbesStore(uint record, SdfMeshProbes probes, uint lane, bool present, uint rateMask, uint extras) {
    uint rate = rateMask << SdfViewsRateShift;
    if (!present) {
        sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeStateWord)] = rate;
        return;
    }
    // The cube blends the weights as a reader of the record holds them, quantized.
    SdfMeshProbes held = sdfViewsProbesStoreGather(record, 0u, probes);

    sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeStateWord)] = (sdfViewsProbesFlags(probes)
        | (lane << SdfViewsProbeLaneShift) | SdfViewsProbePresent | rate | (extras << SdfViewsProbeExtraShift));

    float sky[6];
    float3 radiance[6];
    float3 skyLight[6];

    sdfMeshProbeCube(held, sky, radiance, skyLight);
    [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
        sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (SdfViewsProbeSkyWord + axis))] = (f32tof16(sky[2u * axis]) | (f32tof16(sky[(2u * axis) + 1u]) << 16u));
    }
    [unroll] for (uint word = 0u; (word < 9u); word++) {
        uint low = (2u * word);
        uint high = (low + 1u);

        sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (SdfViewsProbeRadianceWord + word))] =
            (f32tof16(radiance[low / 3u][low % 3u]) | (f32tof16(radiance[high / 3u][high % 3u]) << 16u));
        sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (SdfViewsProbeSkyLightWord + word))] =
            (f32tof16(skyLight[low / 3u][low % 3u]) | (f32tof16(skyLight[high / 3u][high % 3u]) << 16u));
    }

    // The traced lights every measured answering corner sees and any sees, and those corners' weights summed in the order
    // and from the values sdfMeshLightVisibility sums them in, so its answer where all agree is this sum to the bit.
    SdfMeshProbeStages stages = sdfMeshProbeStagesOf(held);
    uint4 all = uint4(0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu);
    uint4 any = uint4(0u, 0u, 0u, 0u);
    float weight = 0.0;
    bool measured = false;

    [loop] for (uint corner = 0u; (corner < 16u); corner++) {
        uint address;
        if (!sdfMeshProbeAt(held, stages, corner, address) || !((corner < 8u) ? held.measured : held.remainderMeasured)) {
            continue;
        }

        uint4 bits = uint4(sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_LIGHTS_WORD], sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_LIGHTS_WORD + 1u],
            sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_LIGHTS_WORD + 2u], sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_LIGHTS_WORD + 3u]);

        all &= bits;
        any |= bits;
        weight += sdfMeshProbeWeight(held, corner);
        measured = true;
    }
    all = (measured ? all : uint4(0u, 0u, 0u, 0u));
    [unroll] for (uint lightWord = 0u; (lightWord < SDF_MESH_IRRADIANCE_LIGHT_WORDS); lightWord++) {
        sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (SdfViewsProbeLightsWord + lightWord))] = sdfComponent(all, lightWord);
        sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (SdfViewsProbeLightsAnyWord + lightWord))] = sdfComponent(any, lightWord);
    }
    sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeLightsWeightWord)] = asuint(weight);
}
#else
// The probes a views pixel shades with, as its cell's record names them: the representative's gather, with its blended
// cube, for the representative and every pixel compatible with it, and a pixel's own gather (the probe lookup ran it for
// the pixel, sdfViewsProbesCompatible) for every other; none for a cell with no representative, which no pixel of the cell
// wants (sdfViewsProbesWanted). The struct carries the gather's share and measured flags and names the record words that
// hold its cages and weights (SdfMeshProbes.record), which a reader loads where it needs them (sdfViewsProbesWhole), so
// the views kernel keeps no corner weight live across its light walk.
SdfMeshProbes sdfViewsProbesAt(SdfPixel p) {
    SdfMeshProbes probes = sdfMeshProbesNone();

#if !defined(SDF_VIEWS_OMIT_PROBES)
    uint record = sdfViewsProbeRecord((p.pixel / SDF_VIEWS_PROBE_RATE), p.viewIndex);
    uint state = sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeStateWord)];

    if ((state & SdfViewsProbePresent) == 0u) {
        return probes;
    }

    uint2 corner = (p.pixel % SDF_VIEWS_PROBE_RATE);
    uint lane = ((state >> SdfViewsProbeLaneShift) & 0xFFu);
    uint place = (corner.x + (SDF_VIEWS_PROBE_RATE * corner.y));
    uint at = 0u;
    uint flags = state;

    if (place != lane) {
        uint slot = sdfViewsProbeExtraSlot(place, lane);

        if (((state >> (SdfViewsProbeExtraShift + slot)) & 1u) != 0u) {
            at = (SdfViewsProbeExtraWord + (slot * SdfViewsProbeExtraWords));
            flags = sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (at + SdfViewsProbeStateWord))];
        }
    }
    probes.share = (float(flags & 0xFFFFu) / 65535.0);
    probes.measured = ((flags & SdfViewsProbeMeasured) != 0u);
    probes.remainderMeasured = ((flags & SdfViewsProbeRemainderMeasured) != 0u);
    probes.record = (record + 1u);
    probes.recordAt = at;
    probes.cube = ((at == 0u) ? (record + 1u) : 0u);
#endif
    return probes;
}
// The probes sdfViewsProbesAt named with their cages and weights loaded from the record words that hold them, the weights
// as the record quantized them.
SdfMeshProbes sdfViewsProbesWhole(SdfMeshProbes probes) {
    if (probes.record == 0u) {
        return probes;
    }

    uint record = (probes.record - 1u);
    uint at = probes.recordAt;
    SdfMeshProbes whole = probes;

    whole.cage = sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, at)];
    whole.remainderCage = sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (at + 1u))];
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint word = sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (at + 2u + pair))];

        whole.weight[2u * pair] = (float(word & 0xFFFFu) / 65535.0);
        whole.weight[(2u * pair) + 1u] = (float(word >> 16u) / 65535.0);
    }
    whole.record = 0u;
    return whole;
}

// The share of the probes about a views pixel that see a traced light (sdfMeshLightVisibility, through
// sdfLightProbeVisibility): where the probes are a record's representative gather, the record's light agreement answers
// every light all measured corners see (their summed weight) or none sees (zero) without visiting a corner, and only a
// light the corners disagree on, or a pixel with a gather of its own, loads the gather and visits them.
float sdfViewsLightVisibility(SdfMeshProbes probes, uint lightIndex) {
    if ((probes.cube != 0u) && (lightIndex < (32u * SDF_MESH_IRRADIANCE_LIGHT_WORDS))) {
        uint record = (probes.cube - 1u);
        uint word = (lightIndex >> 5u);
        uint bit = (1u << (lightIndex & 31u));

        if ((sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (SdfViewsProbeLightsWord + word))] & bit) != 0u) {
            return asfloat(sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeLightsWeightWord)]);
        }
        if ((sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, (SdfViewsProbeLightsAnyWord + word))] & bit) == 0u) {
            return 0.0;
        }
    }
    return sdfMeshLightVisibility(sdfViewsProbesWhole(probes), lightIndex);
}

// The views pass's indirect light and reflection under the irradiance volume (sdfMeshIndirectSeen, sdfMeshReflectionSeen):
// from the record's blended cube where the probes are a record's (SdfMeshProbes.cube), otherwise from their corners.
void sdfViewsCubeAt(SdfMeshProbes probes, float3 direction, out float sky, out float3 radiance, out float3 skyLight) {
    uint record = (probes.cube - 1u);
    uint first = sdfViewsProbeWord(record, 0u);
    uint plane = (sdfViewsProbeWord(record, 1u) - first);

    sdfMeshCubeRead(sdfViewsProbeRecordBuffer, (first + (SdfViewsProbeSkyWord * plane)), (first + (SdfViewsProbeRadianceWord * plane)),
        (first + (SdfViewsProbeSkyLightWord * plane)), plane, direction, sky, radiance, skyLight);
}
float3 sdfViewsIndirectSeen(SdfMeshProbes probes, float3 normal) {
    if ((probes.cube != 0u) && sdfMeshAnswers(probes)) {
        float sky;
        float3 radiance;
        float3 skyLight;

        sdfViewsCubeAt(probes, normal, sky, radiance, skyLight);
        return (skyLight + radiance);
    }
    return sdfMeshIndirectSeen(sdfViewsProbesWhole(probes), normal);
}
float3 sdfViewsReflectionSeen(SdfMeshProbes probes, float3 direction, float3 environment) {
    if ((probes.cube != 0u) && sdfMeshAnswers(probes)) {
        float sky;
        float3 radiance;
        float3 skyLight;

        sdfViewsCubeAt(probes, direction, sky, radiance, skyLight);
        return ((environment * sky) + radiance);
    }
    return sdfMeshReflectionSeen(sdfViewsProbesWhole(probes), direction, environment);
}
#endif

#endif
