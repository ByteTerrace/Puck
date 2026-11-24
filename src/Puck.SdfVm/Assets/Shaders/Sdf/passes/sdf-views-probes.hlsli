// The probe lookup at a rate coarser than the view's (SdfWorldPackage.Parts.ViewsProbes): the irradiance volume's gather
// (sdfMeshProbesAt) evaluated for each cell of SDF_VIEWS_PROBE_RATE by SDF_VIEWS_PROBE_RATE pixels at one representative
// pixel of the cell, and every pixel blending the representatives of the four cells about it. The lookup resolves each
// gather into a slot: everything a shading sample reads of the volume, so views reads its pixel's slots
// (sdfViewsProbesAt) and never holds a gather, a corner or a weight. The gather is the expensive part of the lookup (each
// corner's angular surface test) and the largest code in the volume, and a wave of the shading kernel runs whatever any
// of its lanes runs, so one pixel that read its corners made its whole wave pay for sixteen; the lookup's own lean kernel
// runs each gather and each resolve a lane.
//
// Which representatives a pixel blends is the reuse test (sdf-probe-reuse.hlsli): the lookup weighs each of the four by
// its bilinear share and by how nearly the pixel's receiver lies on the representative's surface (sdfProbeBlend in the
// lookup's kernel), so a blend never steps across a depth or crease edge the record shows, and a surface's pixels
// read the volume's light interpolated between its cells, never a cell's edge. A pixel no representative about it
// serves joins one of its cell's extras, a gather taken at the first such pixel that every later pixel of the cell
// compatible with it shares; a cell holds at most SdfViewsProbeExtras, and a pixel past them blends the cells about it
// by their bilinear shares alone.
//
// The lookup writes each pixel's choice as its mix, one word a pixel: bit 31 set where the pixel reads the volume at all;
// with bit 30 clear, four seven-bit shares in bits 0 to 27, of the cells at the pixel's base cell (sdfViewsProbeMixBase)
// and beside it along x, along y and along both; with bit 30 set, the cell's extra in bits 0 to 1.
//
// A slot is SDF_VIEWS_PROBE_SLOT_WORDS words:
// 0: the gather's chosen stage's share as UNORM16 in bits 0 to 15, its measured flag in bit 16 and the remainder's in bit
//     17 (sdfViewsProbesFlags). A cell's representative's slot also holds, in bits 18 to 25, the representative's pixel
//     within the cell (x + SDF_VIEWS_PROBE_RATE * y); in bit 26 whether a representative was gathered at all (a cell with
//     no pixel that wants the lookup holds none); in bits 27 and 28 the axes shared shading is selected along; and in bits
//     29 to 30 how many extras the cell holds.
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
// The buffer holds three regions, each sized by the cells. The records: a slot a cell, the representative's, each word in
// a plane of its own (sdfViewsProbeWord): a plane holds one word of every cell, so the lanes of a wave, which take
// neighbouring cells or pixels, read and write each word in one run of the buffer rather than one sector a lane. A
// record's last word (SdfViewsProbeExtrasWord) names the first of the cell's extra slots in the pool. The pool follows the
// planes: a count of the slots taken this render (which the recorder zeroes before the lookup's dispatch), then the
// slots, each whole. A cell's extra slots are adjacent in the pool; a group whose extras the pool has no room for keeps
// none, and its pixels read their representatives'. The mixes follow the pool, a word a pixel in raster order
// (sdfViewsProbeMixWord).
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
// The most extras a cell holds, and a mix's flags.
static const uint SdfViewsProbeExtras = 3u;
static const uint SdfViewsProbeMixServed = (1u << 31u);
static const uint SdfViewsProbeMixExtra = (1u << 30u);
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
#if SDF_VIEWS_PROBE_WORDS != ((SDF_VIEWS_PROBE_RECORD_WORDS + SDF_VIEWS_PROBE_POOL_WORDS) + (SDF_VIEWS_PROBE_RATE * SDF_VIEWS_PROBE_RATE))
#error A cell's words are its record, its share of the pool and a mix a pixel.
#endif

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
    uint words = (SDF_VIEWS_PROBE_POOL_WORDS * sdfViewsProbePlane());

    return ((words > 0u) ? ((words - 1u) / SDF_VIEWS_PROBE_SLOT_WORDS) : 0u);
}
// The buffer index of a pixel's mix: the mixes follow the pool, every view's pixels in raster order over the extent the
// cells cover.
uint sdfViewsProbeMixWord(uint2 pixel, uint viewIndex) {
    uint2 extent = (sdfViewsProbeCells() * SDF_VIEWS_PROBE_RATE);

    return (((SDF_VIEWS_PROBE_RECORD_WORDS + SDF_VIEWS_PROBE_POOL_WORDS) * sdfViewsProbePlane()) + ((((viewIndex * extent.y) + pixel.y) * extent.x) + pixel.x));
}
// The lower cell on each axis of the four a pixel blends, which is one before the first cell for the first pixels of an
// axis, and in `fraction` the pixel's share of the cell after it: the bilinear shares of cells whose centres lie a cell
// apart, each cell's representative taken as standing at its centre.
int2 sdfViewsProbeMixBase(uint2 pixel, out float2 fraction) {
    uint2 shifted = (pixel + (SDF_VIEWS_PROBE_RATE / 2u));

    fraction = ((float2(shifted % SDF_VIEWS_PROBE_RATE) + 0.5) / float(SDF_VIEWS_PROBE_RATE));
    return (int2(shifted / SDF_VIEWS_PROBE_RATE) - 1);
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
// Writes a cell's record: its representative's slot, the representative's pixel within the cell, how many extras the cell
// holds and the pool slot of the first of them.
void sdfViewsProbesStore(uint record, SdfMeshProbes probes, uint lane, uint rateMask, uint extras, uint firstExtra) {
    sdfViewsProbesStoreSlot(sdfViewsProbeRecordSlot(record), probes,
        ((lane << SdfViewsProbeLaneShift) | SdfViewsProbePresent | (rateMask << SdfViewsRateShift) | (extras << SdfViewsProbeExtraShift)));
    sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeExtrasWord)] = firstExtra;
}
#else
// The probes a views pixel shades with, as its mix names them: the slots of the representatives about it, each with its
// share of the blend, or its cell's extra alone; none for a pixel that does not read the volume (sdfViewsProbesWanted)
// and none where no slot it names answers. A slot whose gather no probe answered leaves the blend, so a pixel beside an
// unmeasured cell reads the measured ones about it. The struct carries only the slots and their shares (and a share of
// one where any answers); it holds no cage and no corner weight, and every reader below reads the slots' words.
SdfMeshProbes sdfViewsProbesAt(SdfPixel p) {
    SdfMeshProbes probes = sdfMeshProbesNone();

#if !defined(SDF_VIEWS_OMIT_PROBES)
    uint mix = sdfViewsProbeRecordBuffer[sdfViewsProbeMixWord(p.pixel, p.viewIndex)];

    if ((mix & SdfViewsProbeMixServed) == 0u) {
        return probes;
    }
    if ((mix & SdfViewsProbeMixExtra) != 0u) {
        uint record = sdfViewsProbeRecord((p.pixel / SDF_VIEWS_PROBE_RATE), p.viewIndex);
        uint state = sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeStateWord)];
        SdfViewsProbeSlot slot = sdfViewsProbeRecordSlot(record);

        // A cell whose group the pool had no room for kept no extra, and its pixels read its representative's slot.
        if ((mix & 3u) < ((state >> SdfViewsProbeExtraShift) & 3u)) {
            slot = sdfViewsProbePoolSlot(sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeExtrasWord)] + (mix & 3u));
            state = sdfViewsProbeRecordBuffer[slot.first];
        }
        if ((state & 0xFFFFu) != 0u) {
            probes.share = 1.0;
            probes.slots.x = (slot.first + 1u);
            probes.slotStride = slot.stride;
            probes.slotWeights.x = 1.0;
        }
        return probes;
    }

    float2 fraction;
    int2 base = sdfViewsProbeMixBase(p.pixel, fraction);
    float4 weights = float4(float(mix & 0x7Fu), float((mix >> 7u) & 0x7Fu), float((mix >> 14u) & 0x7Fu), float((mix >> 21u) & 0x7Fu));
    uint4 slots = uint4(0u, 0u, 0u, 0u);

    // The lookup gives a share only to a cell inside the view that holds a representative.
    [unroll] for (uint corner = 0u; (corner < 4u); corner++) {
        if (weights[corner] > 0.0) {
            uint record = sdfViewsProbeRecord(uint2(base + int2((corner & 1u), (corner >> 1u))), p.viewIndex);

            if ((sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeStateWord)] & 0xFFFFu) == 0u) {
                weights[corner] = 0.0;
            }
            slots[corner] = (record + 1u);
        }
    }

    float total = dot(weights, float4(1.0, 1.0, 1.0, 1.0));

    if (total > 0.0) {
        probes.share = 1.0;
        probes.slots = slots;
        probes.slotStride = sdfViewsProbePlane();
        probes.slotWeights = (weights / total);
    }
#endif
    return probes;
}

// The share of the probes about one slot's receiver that see a traced light (sdfMeshLightVisibility), from the slot whose
// first word lies at `first`, its words `stride` apart: every measured corner's summed weight for a light all of them
// see, zero for one none sees, and the listed share for a light they disagree on.
float sdfViewsSlotLightVisibility(uint first, uint stride, uint lightIndex) {
    uint word = (lightIndex >> 5u);
    uint bit = (1u << (lightIndex & 31u));

    if ((sdfViewsProbeRecordBuffer[first + ((SdfViewsProbeLightsWord + word) * stride)] & bit) != 0u) {
        return asfloat(sdfViewsProbeRecordBuffer[first + (SdfViewsProbeLightsWeightWord * stride)]);
    }
    if ((sdfViewsProbeRecordBuffer[first + ((SdfViewsProbeLightsAnyWord + word) * stride)] & bit) == 0u) {
        return 0.0;
    }

    float share = 0.0;

    [unroll] for (uint pair = 0u; (pair < (SdfViewsProbePartialLights / 2u)); pair++) {
        uint entries = sdfViewsProbeRecordBuffer[first + ((SdfViewsProbePartialWord + pair) * stride)];

        [unroll] for (uint side = 0u; (side < 2u); side++) {
            uint entry = ((entries >> (16u * side)) & 0xFFFFu);

            if (((entry & SdfViewsProbePartialValid) != 0u) && (((entry >> 8u) & 0x7Fu) == lightIndex)) {
                share = (float(entry & 0xFFu) / 255.0);
            }
        }
    }
    return share;
}
// The share of the probes about a views pixel that see a traced light: its slots' shares, blended.
float sdfViewsLightVisibility(SdfMeshProbes probes, uint lightIndex) {
    if (lightIndex >= (32u * SDF_MESH_IRRADIANCE_LIGHT_WORDS)) {
        return 1.0;
    }

    float share = 0.0;

    [unroll] for (uint slot = 0u; (slot < 4u); slot++) {
        if (probes.slotWeights[slot] > 0.0) {
            share += (probes.slotWeights[slot] * sdfViewsSlotLightVisibility((probes.slots[slot] - 1u), probes.slotStride, lightIndex));
        }
    }
    return share;
}
// The traced lights the probes about one slot's receiver may see: its `any`, which holds every light a corner of its
// gather sees.
uint4 sdfViewsSlotLightsSeen(uint first, uint stride) {
    uint at = (first + (SdfViewsProbeLightsAnyWord * stride));

    return uint4(sdfViewsProbeRecordBuffer[at], sdfViewsProbeRecordBuffer[at + stride],
        sdfViewsProbeRecordBuffer[at + (2u * stride)], sdfViewsProbeRecordBuffer[at + (3u * stride)]);
}
// The traced lights a cell's representative's probes may see, which every pixel that blends the cell may see through it:
// none for a cell outside the view or without a representative, since no pixel blends it, and every light for one whose
// representative no probe answers, since a pixel left with no answering slot lights unoccluded (sdfMeshAnswers).
uint4 sdfViewsCellLightsSeen(int2 cell, uint viewIndex) {
    if (any(cell < 0) || any(cell >= int2(sdfViewsProbeCells()))) {
        return uint4(0u, 0u, 0u, 0u);
    }

    uint record = sdfViewsProbeRecord(uint2(cell), viewIndex);
    uint state = sdfViewsProbeRecordBuffer[sdfViewsProbeWord(record, SdfViewsProbeStateWord)];

    if ((state & SdfViewsProbePresent) == 0u) {
        return uint4(0u, 0u, 0u, 0u);
    }
    if ((state & 0xFFFFu) == 0u) {
        return uint4(0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu);
    }
    return sdfViewsSlotLightsSeen(record, sdfViewsProbePlane());
}
// The traced lights a views pixel may see beyond those of the cells about it (sdfViewsCellLightsSeen): every light for a
// pixel that reads no slot, which lights unoccluded; its extra's for a pixel that reads one; none for a pixel that blends
// cells.
uint4 sdfViewsPixelLightsSeen(SdfPixel p) {
    uint mix = sdfViewsProbeRecordBuffer[sdfViewsProbeMixWord(p.pixel, p.viewIndex)];

    if ((mix & SdfViewsProbeMixServed) == 0u) {
        return uint4(0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu);
    }
    if ((mix & SdfViewsProbeMixExtra) == 0u) {
        return uint4(0u, 0u, 0u, 0u);
    }

    SdfMeshProbes probes = sdfViewsProbesAt(p);

    if (!sdfMeshAnswers(probes)) {
        return uint4(0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu);
    }
    return sdfViewsSlotLightsSeen((probes.slots.x - 1u), probes.slotStride);
}

// One of a pixel's slots' two six-face colour cubes, whose first word is `word`, about a direction: each axis's face
// toward the direction (sdfMeshCubeFace), weighted by the direction's squared component, as sdfMeshCubeRead weighs a
// probe's, the slots blended by their shares.
float3 sdfViewsCubeAt(SdfMeshProbes probes, uint word, float3 direction) {
    float3 squared = (direction * direction);
    uint3 faces = uint3(sdfMeshCubeFace(direction, 0u), sdfMeshCubeFace(direction, 1u), sdfMeshCubeFace(direction, 2u));
    float3 value = 0.0;

    [unroll] for (uint slot = 0u; (slot < 4u); slot++) {
        if (probes.slotWeights[slot] > 0.0) {
            uint first = ((probes.slots[slot] - 1u) + (word * probes.slotStride));
            float3 cube = 0.0;

            [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
                cube += (squared[axis] * puckUnpackSharedExponent(sdfViewsProbeRecordBuffer[first + (faces[axis] * probes.slotStride)]));
            }
            value += (probes.slotWeights[slot] * cube);
        }
    }
    return value;
}
// A pixel's slots' sky share about a direction, from each one's two words of UNORM10 faces.
float sdfViewsSkyShareAt(SdfMeshProbes probes, float3 direction) {
    float3 squared = (direction * direction);
    uint3 faces = uint3(sdfMeshCubeFace(direction, 0u), sdfMeshCubeFace(direction, 1u), sdfMeshCubeFace(direction, 2u));
    float sky = 0.0;

    [unroll] for (uint slot = 0u; (slot < 4u); slot++) {
        if (probes.slotWeights[slot] > 0.0) {
            uint first = ((probes.slots[slot] - 1u) + (SdfViewsProbeSkyWord * probes.slotStride));
            uint2 words = uint2(sdfViewsProbeRecordBuffer[first], sdfViewsProbeRecordBuffer[first + probes.slotStride]);
            float share = 0.0;

            [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
                share += (squared[axis] * (float((((faces[axis] < 3u) ? words.x : words.y) >> (10u * (faces[axis] % 3u))) & 0x3FFu) / 1023.0));
            }
            sky += (probes.slotWeights[slot] * share);
        }
    }
    return sky;
}
// The views pass's indirect light and reflection under the irradiance volume (sdfMeshIndirectSeen, sdfMeshReflectionSeen):
// from the pixel's slots where the volume answers it, and otherwise the camera sample's, which reads no probe.
float3 sdfViewsIndirectSeen(SdfMeshProbes probes, float3 normal) {
    if (sdfMeshAnswers(probes)) {
        return sdfViewsCubeAt(probes, SdfViewsProbeReceivedWord, normal);
    }
    return sdfMeshIndirectUnanswered(normal);
}
float3 sdfViewsReflectionSeen(SdfMeshProbes probes, float3 direction, float3 environment) {
    if (sdfMeshAnswers(probes)) {
        return ((environment * sdfViewsSkyShareAt(probes, direction)) + sdfViewsCubeAt(probes, SdfViewsProbeRadianceWord, direction));
    }
    return sdfMeshReflectionUnanswered(direction, environment);
}
#endif

#endif
