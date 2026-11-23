// The probe lookup at the view's own rate (SdfWorldPackage.Parts.ViewsProbes): the irradiance volume's gather
// (sdfMeshProbesAt) evaluated for each cell of SDF_VIEWS_PROBE_RATE by SDF_VIEWS_PROBE_RATE pixels at one representative
// pixel of the cell, and at each other pixel of the cell whose receiver is not compatible with the representative's
// (sdfViewsProbesCompatible). The lookup resolves each gather into a slot: everything a shading sample reads of the
// volume, so views reads its pixel's slot (sdfViewsProbesAt) and never holds a gather, a corner or a weight. The gather is
// the expensive part of the lookup (each corner's angular surface test) and the largest code in the volume, and a wave of
// the shading kernel runs whatever any of its lanes runs, so one pixel that read its corners made its whole wave pay for
// sixteen; the lookup's own lean kernel runs each gather and each resolve a lane.
//
// Which pixels share a gather is the reuse test (sdf-probe-reuse.hlsli): a pixel across a depth or crease edge from its
// representative fails it and gathers exactly, so the lookup never steps across a visibility edge it can see in the
// record. The representative itself reads its own gather.
//
// A slot is SDF_VIEWS_PROBE_SLOT_WORDS words:
// 0: the gather's chosen stage's share as UNORM16 in bits 0 to 15, its measured flag in bit 16 and the remainder's in bit
//     17 (sdfViewsProbesFlags). A cell's representative's slot also holds, in bits 18 to 25, the representative's pixel
//     within the cell (x + SDF_VIEWS_PROBE_RATE * y); in bit 26 whether a representative was gathered at all (a cell with
//     no pixel that wants the lookup holds none); in bits 27 and 28 the axes shared shading is selected along; and in bits
//     29 to 31 which of the cell's other pixels, in raster order skipping the representative, hold a slot of their own.
// 1 to 14: the gather's six-axis cubes blended by its weights (sdfMeshProbeCube), packed for what a shading sample reads
//     of them, a word a face (+X, -X, +Y, -Y, +Z, -Z): in 1 to 6 the light a surface receives, the sky light and the
//     surfaces' radiance summed, and in 7 to 12 the radiance alone, which a reflection adds to its share of the sky, each
//     a shared-exponent colour (Shared/shared-exponent.hlsli); in 13 and 14 the sky shares as UNORM10, three faces a word.
//     The cube is linear in its faces, so its value about a direction is the corners' up to the order of the sums and the
//     packing's rounding.
// 15 to 23: the traced lights' agreement (sdfMeshLightAgreement): four words of the light bits every measured answering
//     corner holds, four of the bits any holds, and the sum of those corners' weights as a float.
// 24 to 27: the lights the corners disagree on, at most SdfViewsProbePartialLights of them, sixteen bits each, two a word:
//     bit 15 set, the light in bits 8 to 14 and its share of the probes (sdfMeshLightVisibility) as UNORM8 below. A light
//     past that many is rounded to the nearer of all and none and its agreement bits say which, so every light whose bit
//     is in `any` and not in `all` is in the list.
//
// The records hold a slot a cell, the representative's, each word in a plane of its own (sdfViewsProbeWord): a plane holds
// one word of every cell, so the lanes of a wave, which take neighbouring cells or pixels, read and write each word in one
// run of the buffer rather than one sector a lane. A record's last word (SdfViewsProbeExtrasWord) names the first of the
// cell's extra slots in the pool. The pool follows the planes in the same buffer: a count of the slots taken this render
// (which the recorder zeroes before the lookup's dispatch), then the slots, each whole. A cell's extra slots are adjacent
// in the pool, in the order of its extras' bits; a group whose extras the pool has no room for keeps none, and its pixels
// read their representatives'.
#ifndef PASSES_SDF_VIEWS_PROBES_HLSLI
#define PASSES_SDF_VIEWS_PROBES_HLSLI
#include "../shade/sdf-mesh-irradiance.hlsli"
#include "sdf-probe-reuse.hlsli"
#include "Shared/shared-exponent.hlsli"

#if defined(SDF_VIEWS_PROBES_PASS)
#define sdfViewsProbeRecordBuffer sdfViewsProbeRecordsRW
#else
#define sdfViewsProbeRecordBuffer sdfViewsProbeRecords
#include "../shade/sdf-light.hlsli"
#endif

static const uint SdfViewsProbeStateWord = 0u;
static const uint SdfViewsProbeReceivedWord = 1u;
static const uint SdfViewsProbeRadianceWord = 7u;
static const uint SdfViewsProbeSkyWord = 13u;
static const uint SdfViewsProbeLightsWord = 15u;
static const uint SdfViewsProbePartialWord = 24u;
static const uint SdfViewsProbePartialLights = 8u;
static const uint SdfViewsProbePartialValid = 0x8000u;
static const uint SdfViewsProbeLaneShift = 18u;
static const uint SdfViewsRateShift = 27u;
static const uint SdfViewsProbePresent = (1u << 26u);
static const uint SdfViewsProbeExtraShift = 29u;
#if SDF_MESH_IRRADIANCE_LIGHT_WORDS != 4
#error A slot's light agreement holds a uint4 of light bits, and a partial light's index seven bits.
#endif
static const uint SdfViewsProbeLightsAnyWord = (SdfViewsProbeLightsWord + SDF_MESH_IRRADIANCE_LIGHT_WORDS);
static const uint SdfViewsProbeLightsWeightWord = (SdfViewsProbeLightsAnyWord + SDF_MESH_IRRADIANCE_LIGHT_WORDS);
#if SDF_VIEWS_PROBE_SLOT_WORDS != 28
#error A slot's layout holds twenty-eight words.
#endif
// The record's word past its slot: the pool slot of the cell's first extra.
static const uint SdfViewsProbeExtrasWord = SDF_VIEWS_PROBE_SLOT_WORDS;
#if SDF_VIEWS_PROBE_RECORD_WORDS != (SDF_VIEWS_PROBE_SLOT_WORDS + 1)
#error A record is its representative's slot and the cell's first extra slot.
#endif

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

// A cell's record: its place among every view's cells, raster order within a view.
uint sdfViewsProbeRecord(uint2 cell, uint viewIndex) {
    uint2 cells = sdfViewsProbeCells();

    return ((((viewIndex * cells.y) + cell.y) * cells.x) + cell.x);
}
// The cells the records hold: the length of one plane.
uint sdfViewsProbePlane() {
    uint count;
    uint stride;

    sdfViewsProbeRecordBuffer.GetDimensions(count, stride);
    return (count / SDF_VIEWS_PROBE_WORDS);
}
// The buffer index of word `word` of record `record`.
uint sdfViewsProbeWord(uint record, uint word) {
    return ((word * sdfViewsProbePlane()) + record);
}
// The buffer index of the pool's count of slots taken, which its slots follow, and the slots it has room for.
uint sdfViewsProbePoolWord() {
    return (SDF_VIEWS_PROBE_RECORD_WORDS * sdfViewsProbePlane());
}
uint sdfViewsProbePoolCapacity() {
    uint words = ((SDF_VIEWS_PROBE_WORDS - SDF_VIEWS_PROBE_RECORD_WORDS) * sdfViewsProbePlane());

    return ((words > 0u) ? ((words - 1u) / SDF_VIEWS_PROBE_SLOT_WORDS) : 0u);
}
// A slot's words in the buffer: word w lies at `first` + w * `stride`. A record's slot strides a plane; a pool slot's
// words are adjacent.
struct SdfViewsProbeSlot {
    uint first;
    uint stride;
};
SdfViewsProbeSlot sdfViewsProbeRecordSlot(uint record) {
    SdfViewsProbeSlot slot;

    slot.first = record;
    slot.stride = sdfViewsProbePlane();
    return slot;
}
SdfViewsProbeSlot sdfViewsProbePoolSlot(uint entry) {
    SdfViewsProbeSlot slot;

    slot.first = ((sdfViewsProbePoolWord() + 1u) + (entry * SDF_VIEWS_PROBE_SLOT_WORDS));
    slot.stride = 1u;
    return slot;
}
// The pool slot of the extra a cell's state names by its extra slot `extra` (sdfViewsProbeExtraSlot), the cell's first
// lying at `first`: the cell's extras lie in the order of their bits.
uint sdfViewsProbeExtraEntry(uint first, uint extrasMask, uint extra) {
    return (first + countbits(extrasMask & ((1u << extra) - 1u)));
}

#if defined(SDF_VIEWS_PROBES_PASS)
// The lowest light of a light mask, or 32 * SDF_MESH_IRRADIANCE_LIGHT_WORDS for an empty one.
uint sdfViewsProbeLowestLight(uint4 lights) {
    if (lights.x != 0u) { return firstbitlow(lights.x); }
    if (lights.y != 0u) { return (32u + firstbitlow(lights.y)); }
    if (lights.z != 0u) { return (64u + firstbitlow(lights.z)); }
    if (lights.w != 0u) { return (96u + firstbitlow(lights.w)); }
    return (32u * SDF_MESH_IRRADIANCE_LIGHT_WORDS);
}
// One light's bit in a light mask.
uint4 sdfViewsProbeLightBit(uint light) {
    uint word = (light >> 5u);
    uint bit = (1u << (light & 31u));

    return uint4(((word == 0u) ? bit : 0u), ((word == 1u) ? bit : 0u), ((word == 2u) ? bit : 0u), ((word == 3u) ? bit : 0u));
}
// Resolves a gather into `slot`: its share and flags with `state` (a representative's cell bits, zero for an extra), its
// blended cubes, its lights' agreement and the lights its corners disagree on.
void sdfViewsProbesStoreSlot(SdfViewsProbeSlot slot, SdfMeshProbes probes, uint state) {
    sdfViewsProbeRecordBuffer[slot.first + (SdfViewsProbeStateWord * slot.stride)] = (sdfViewsProbesFlags(probes) | state);

    float sky[6];
    float3 radiance[6];
    float3 skyLight[6];

    sdfMeshProbeCube(probes, sky, radiance, skyLight);
    [unroll] for (uint face = 0u; (face < 6u); face++) {
        sdfViewsProbeRecordBuffer[slot.first + ((SdfViewsProbeReceivedWord + face) * slot.stride)] = puckPackSharedExponent(skyLight[face] + radiance[face]);
        sdfViewsProbeRecordBuffer[slot.first + ((SdfViewsProbeRadianceWord + face) * slot.stride)] = puckPackSharedExponent(radiance[face]);
    }
    [unroll] for (uint side = 0u; (side < 2u); side++) {
        uint3 shares = (uint3)round(saturate(float3(sky[3u * side], sky[(3u * side) + 1u], sky[(3u * side) + 2u])) * 1023.0);

        sdfViewsProbeRecordBuffer[slot.first + ((SdfViewsProbeSkyWord + side) * slot.stride)] = (shares.x | (shares.y << 10u) | (shares.z << 20u));
    }

    // The traced lights every measured answering corner sees and any sees, and those corners' weights. A light the
    // corners disagree on is listed with its share; past the list's length it is rounded to all or none.
    uint4 everyCorner;
    uint4 someCorner;
    float weight;

    sdfMeshLightAgreement(probes, everyCorner, someCorner, weight);

    uint4 partial = (someCorner & ~everyCorner);
    uint4 listed = uint4(0u, 0u, 0u, 0u);
    uint count = 0u;

    [loop] while (any(partial != 0u)) {
        uint light = sdfViewsProbeLowestLight(partial);
        uint4 bit = sdfViewsProbeLightBit(light);
        float share = sdfMeshLightVisibility(probes, light);

        partial &= ~bit;
        if (count < SdfViewsProbePartialLights) {
            uint entry = ((SdfViewsProbePartialValid | (light << 8u)) | (uint)round(saturate(share) * 255.0));
            uint shifted = (entry << (16u * (count & 1u)));
            uint word = (count >> 1u);

            listed |= uint4(((word == 0u) ? shifted : 0u), ((word == 1u) ? shifted : 0u), ((word == 2u) ? shifted : 0u), ((word == 3u) ? shifted : 0u));
            count++;
        } else if ((2.0 * share) >= weight) {
            everyCorner |= bit;
        } else {
            someCorner &= ~bit;
        }
    }
    [unroll] for (uint lightWord = 0u; (lightWord < SDF_MESH_IRRADIANCE_LIGHT_WORDS); lightWord++) {
        sdfViewsProbeRecordBuffer[slot.first + ((SdfViewsProbeLightsWord + lightWord) * slot.stride)] = sdfComponent(everyCorner, lightWord);
        sdfViewsProbeRecordBuffer[slot.first + ((SdfViewsProbeLightsAnyWord + lightWord) * slot.stride)] = sdfComponent(someCorner, lightWord);
        sdfViewsProbeRecordBuffer[slot.first + ((SdfViewsProbePartialWord + lightWord) * slot.stride)] = sdfComponent(listed, lightWord);
    }
    sdfViewsProbeRecordBuffer[slot.first + (SdfViewsProbeLightsWeightWord * slot.stride)] = asuint(weight);
}
// Writes the state of a cell with no representative: no pixel of it wants the lookup, and its slot's other words stay
// as they were.
void sdfViewsProbesStoreEmpty(uint record, uint rateMask) {
    sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeStateWord)] = (rateMask << SdfViewsRateShift);
}
// Writes a cell's record: its representative's slot, the representative's pixel within the cell, which other pixels hold
// slots of their own (`extras`, a bit an extra slot) and the pool slot of the first of them.
void sdfViewsProbesStore(uint record, SdfMeshProbes probes, uint lane, uint rateMask, uint extras, uint firstExtra) {
    sdfViewsProbesStoreSlot(sdfViewsProbeRecordSlot(record), probes,
        ((lane << SdfViewsProbeLaneShift) | SdfViewsProbePresent | (rateMask << SdfViewsRateShift) | (extras << SdfViewsProbeExtraShift)));
    sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeExtrasWord)] = firstExtra;
}
#else
// The probes a views pixel shades with, as its cell's record names them: the representative's slot for the representative
// and every pixel compatible with it, and a pixel's own slot (the probe lookup ran its gather, sdfViewsProbesCompatible)
// for every other; none for a cell with no representative, which no pixel of the cell wants (sdfViewsProbesWanted). The
// struct carries the gather's share and measured flags and names the slot (SdfMeshProbes.record and recordAt, one more
// than its first word and its stride); it holds no cage and no weight, and every reader below reads the slot's words.
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
    uint extras = (state >> SdfViewsProbeExtraShift);
    SdfViewsProbeSlot slot = sdfViewsProbeRecordSlot(record);
    uint flags = state;

    if (place != lane) {
        uint extra = sdfViewsProbeExtraSlot(place, lane);

        if (((extras >> extra) & 1u) != 0u) {
            slot = sdfViewsProbePoolSlot(sdfViewsProbeExtraEntry(sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeExtrasWord)], extras, extra));
            flags = sdfViewsProbeRecordBuffer[slot.first];
        }
    }
    probes.share = (float(flags & 0xFFFFu) / 65535.0);
    probes.measured = ((flags & SdfViewsProbeMeasured) != 0u);
    probes.remainderMeasured = ((flags & SdfViewsProbeRemainderMeasured) != 0u);
    probes.record = (slot.first + 1u);
    probes.recordAt = slot.stride;
#endif
    return probes;
}
// The buffer index of word `word` of the slot `probes` names.
uint sdfViewsProbeSlotWord(SdfMeshProbes probes, uint word) {
    return ((probes.record - 1u) + (word * probes.recordAt));
}

// The share of the probes about a views pixel that see a traced light (sdfMeshLightVisibility), from its slot: every
// measured corner's summed weight for a light all of them see, zero for one none sees, and the listed share for a light
// they disagree on.
float sdfViewsLightVisibility(SdfMeshProbes probes, uint lightIndex) {
    if (lightIndex >= (32u * SDF_MESH_IRRADIANCE_LIGHT_WORDS)) {
        return 1.0;
    }
    if (probes.record == 0u) {
        return 0.0;
    }

    uint word = (lightIndex >> 5u);
    uint bit = (1u << (lightIndex & 31u));

    if ((sdfViewsProbeRecordBuffer[sdfViewsProbeSlotWord(probes, (SdfViewsProbeLightsWord + word))] & bit) != 0u) {
        return asfloat(sdfViewsProbeRecordBuffer[sdfViewsProbeSlotWord(probes, SdfViewsProbeLightsWeightWord)]);
    }
    if ((sdfViewsProbeRecordBuffer[sdfViewsProbeSlotWord(probes, (SdfViewsProbeLightsAnyWord + word))] & bit) == 0u) {
        return 0.0;
    }

    float share = 0.0;

    [unroll] for (uint pair = 0u; (pair < (SdfViewsProbePartialLights / 2u)); pair++) {
        uint entries = sdfViewsProbeRecordBuffer[sdfViewsProbeSlotWord(probes, (SdfViewsProbePartialWord + pair))];

        [unroll] for (uint side = 0u; (side < 2u); side++) {
            uint entry = ((entries >> (16u * side)) & 0xFFFFu);

            if (((entry & SdfViewsProbePartialValid) != 0u) && (((entry >> 8u) & 0x7Fu) == lightIndex)) {
                share = (float(entry & 0xFFu) / 255.0);
            }
        }
    }
    return share;
}
// The traced lights a views pixel's probes may see: a bit for every light of the table whose sdfViewsLightVisibility at
// the pixel can be above zero. Every light where no probe answers the pixel, since its lights then go unoccluded
// (sdfMeshAnswers); otherwise its slot's `any`, which holds every light a corner of its gather sees.
uint4 sdfViewsLightsSeen(SdfPixel p) {
    SdfMeshProbes probes = sdfViewsProbesAt(p);

    if (!sdfMeshAnswers(probes)) {
        return uint4(0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu);
    }

    uint first = sdfViewsProbeSlotWord(probes, SdfViewsProbeLightsAnyWord);

    return uint4(sdfViewsProbeRecordBuffer[first], sdfViewsProbeRecordBuffer[first + probes.recordAt],
        sdfViewsProbeRecordBuffer[first + (2u * probes.recordAt)], sdfViewsProbeRecordBuffer[first + (3u * probes.recordAt)]);
}

// One of a slot's two six-face colour cubes, whose first word is `word`, about a direction: each axis's face toward the
// direction (sdfMeshCubeFace), weighted by the direction's squared component, as sdfMeshCubeRead weighs a probe's.
float3 sdfViewsCubeAt(SdfMeshProbes probes, uint word, float3 direction) {
    uint first = sdfViewsProbeSlotWord(probes, word);
    float3 squared = (direction * direction);
    float3 value = 0.0;

    [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
        value += (squared[axis] * puckUnpackSharedExponent(sdfViewsProbeRecordBuffer[first + (sdfMeshCubeFace(direction, axis) * probes.recordAt)]));
    }
    return value;
}
// A slot's sky share about a direction, from its two words of UNORM10 faces.
float sdfViewsSkyShareAt(SdfMeshProbes probes, float3 direction) {
    uint first = sdfViewsProbeSlotWord(probes, SdfViewsProbeSkyWord);
    uint2 words = uint2(sdfViewsProbeRecordBuffer[first], sdfViewsProbeRecordBuffer[first + probes.recordAt]);
    float3 squared = (direction * direction);
    float sky = 0.0;

    [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
        uint face = sdfMeshCubeFace(direction, axis);

        sky += (squared[axis] * (float((((face < 3u) ? words.x : words.y) >> (10u * (face % 3u))) & 0x3FFu) / 1023.0));
    }
    return sky;
}
// The views pass's indirect light and reflection under the irradiance volume (sdfMeshIndirectSeen, sdfMeshReflectionSeen):
// from the pixel's slot where the volume answers it, and otherwise the camera sample's, which reads no probe.
float3 sdfViewsIndirectSeen(SdfMeshProbes probes, float3 normal) {
    if (sdfMeshAnswers(probes) && (probes.record != 0u)) {
        return sdfViewsCubeAt(probes, SdfViewsProbeReceivedWord, normal);
    }
    return sdfMeshIndirectUnanswered(normal);
}
float3 sdfViewsReflectionSeen(SdfMeshProbes probes, float3 direction, float3 environment) {
    if (sdfMeshAnswers(probes) && (probes.record != 0u)) {
        return ((environment * sdfViewsSkyShareAt(probes, direction)) + sdfViewsCubeAt(probes, SdfViewsProbeRadianceWord, direction));
    }
    return sdfMeshReflectionUnanswered(direction, environment);
}
#endif

#endif
