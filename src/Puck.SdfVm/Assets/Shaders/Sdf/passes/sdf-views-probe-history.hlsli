// The probe lookup's history (SdfWorldPackage.ViewsProbeHistory): the gather each cell's representative read, kept for the
// next frame's lookup, which reuses it for a receiver compatible with the one it was taken at instead of gathering again.
// A temporal view's alone: its fragment holds the history's two allocations and its pass block marks it temporal, and
// every other view binds the dummy at both members and never reads or writes them.
//
// What makes a kept gather the gather its receiver would read now: a gather is a function of its receiver, the published
// windows and the published probes' geometry (cells, offsets, validity, surface maps) and nothing else once it is
// complete (sdfMeshProbesAtComplete: no corner it consulted was missing), since a relight changes only the light the
// lookup blends again each frame. The geometry serial (SdfMeshIrradianceParameters.Geometry) moves with every publication
// that moves a window or changes a held probe's geometry, so an entry stands while its serial is the pass block's. A
// probe landing in a missing corner moves no serial, so an incomplete gather is never kept. The receiver it is reused for
// passes the same test a pixel passes to share its cell representative's gather within one frame
// (sdfViewsProbesCompatible), always against the receiver the gather was taken at, so reuse never drifts across frames.
//
// An entry is SDF_VIEWS_PROBE_HISTORY_WORDS words, addressed by cell like a probe record:
// 0 to 2: the receiver's surface point, as floats.
// 3: its geometric normal as the visibility record packs it (sdfVisibilityPackNormal).
// 4 to 14: the gather as an extra slot holds it: the two cages, the sixteen UNORM16 weights and the share and measured
//     flags (sdfViewsProbesFlags).
// 15: SdfViewsProbeHistoryKept where the entry holds a gather, zero otherwise (a zeroed allocation holds none).
// 16: the geometry serial the gather was taken under.
#ifndef PASSES_SDF_VIEWS_PROBE_HISTORY_HLSLI
#define PASSES_SDF_VIEWS_PROBE_HISTORY_HLSLI
#include "sdf-views-probes.hlsli"
#include "../frame/sdf-reprojection.hlsli"

static const uint SdfViewsProbeHistoryNormalWord = 3u;
static const uint SdfViewsProbeHistoryGatherWord = 4u;
static const uint SdfViewsProbeHistoryStateWord = 15u;
static const uint SdfViewsProbeHistorySerialWord = 16u;
static const uint SdfViewsProbeHistoryKept = 0x4B455054u;

uint sdfViewsProbeHistoryEntry(uint2 cell, uint viewIndex) {
    uint2 cells = sdfViewsProbeCells();

    return (SDF_VIEWS_PROBE_HISTORY_WORDS * ((((viewIndex * cells.y) + cell.y) * cells.x) + cell.x));
}

// Whether the view keeps a history at all, and whether this frame may read the preceding one's: the preceding view's
// camera is known and its render grid is this one's, so a preceding cell is addressed as this frame's.
bool sdfViewsProbeHistoryKeeps() {
    return (passGroup.temporal != 0u);
}
bool sdfViewsProbeHistoryReads(uint2 dims) {
    return (sdfViewsProbeHistoryKeeps() && (passGroup.previousView[0].w != 0.0) && all(uint2(passGroup.previousView[4].xy) == dims));
}

// Writes a cell's entry: the gather a receiver at `surfacePoint` with the packed geometric normal `packedNormal` read, or,
// with `kept` false, an entry that holds none.
void sdfViewsProbeHistoryStore(uint entry, bool kept, float3 surfacePoint, uint packedNormal, SdfMeshProbes probes) {
    if (!kept) {
        sdfViewsProbeHistoryRW[entry + SdfViewsProbeHistoryStateWord] = 0u;
        return;
    }
    sdfViewsProbeHistoryRW[entry] = asuint(surfacePoint.x);
    sdfViewsProbeHistoryRW[entry + 1u] = asuint(surfacePoint.y);
    sdfViewsProbeHistoryRW[entry + 2u] = asuint(surfacePoint.z);
    sdfViewsProbeHistoryRW[entry + SdfViewsProbeHistoryNormalWord] = packedNormal;
    sdfViewsProbeHistoryRW[entry + SdfViewsProbeHistoryGatherWord] = probes.cage;
    sdfViewsProbeHistoryRW[entry + SdfViewsProbeHistoryGatherWord + 1u] = probes.remainderCage;
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint low = (uint)round(saturate(probes.weight[2u * pair]) * 65535.0);
        uint high = (uint)round(saturate(probes.weight[(2u * pair) + 1u]) * 65535.0);

        sdfViewsProbeHistoryRW[entry + SdfViewsProbeHistoryGatherWord + 2u + pair] = (low | (high << 16u));
    }
    sdfViewsProbeHistoryRW[entry + SdfViewsProbeHistoryGatherWord + 10u] = sdfViewsProbesFlags(probes);
    sdfViewsProbeHistoryRW[entry + SdfViewsProbeHistoryStateWord] = SdfViewsProbeHistoryKept;
    sdfViewsProbeHistoryRW[entry + SdfViewsProbeHistorySerialWord] = passGroup.meshIrradianceGeometry;
}
// Copies a preceding entry, which a receiver of this frame reused, into this frame's entry unchanged: the receiver stays
// the one the gather was taken at.
void sdfViewsProbeHistoryCarry(uint entry, uint previous) {
    [unroll] for (uint word = 0u; (word < SDF_VIEWS_PROBE_HISTORY_WORDS); word++) {
        sdfViewsProbeHistoryRW[entry + word] = sdfViewsProbeHistory[previous + word];
    }
}
// The gather a preceding entry holds, its weights as the entry quantized them.
SdfMeshProbes sdfViewsProbeHistoryLoad(uint previous) {
    uint at = (previous + SdfViewsProbeHistoryGatherWord);
    SdfMeshProbes probes = sdfMeshProbesNone();
    uint flags = sdfViewsProbeHistory[at + 10u];

    probes.cage = sdfViewsProbeHistory[at];
    probes.remainderCage = sdfViewsProbeHistory[at + 1u];
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint word = sdfViewsProbeHistory[at + 2u + pair];

        probes.weight[2u * pair] = (float(word & 0xFFFFu) / 65535.0);
        probes.weight[(2u * pair) + 1u] = (float(word >> 16u) / 65535.0);
    }
    probes.share = (float(flags & 0xFFFFu) / 65535.0);
    probes.measured = ((flags & SdfViewsProbeMeasured) != 0u);
    probes.remainderMeasured = ((flags & SdfViewsProbeRemainderMeasured) != 0u);
    return probes;
}
// Whether the preceding entry of `cell` holds a gather this frame's receiver may read: kept under the pass block's
// geometry serial, at a receiver the present one is compatible with within `tolerance` (sdfViewsProbeTolerance).
bool sdfViewsProbeHistoryStands(uint2 cell, uint viewIndex, uint2 cells, float3 surfacePoint, float3 normal, float2 tolerance, out uint previous) {
    previous = sdfViewsProbeHistoryEntry(cell, viewIndex);
    if (any(cell >= cells) || (sdfViewsProbeHistory[previous + SdfViewsProbeHistoryStateWord] != SdfViewsProbeHistoryKept)
        || (sdfViewsProbeHistory[previous + SdfViewsProbeHistorySerialWord] != passGroup.meshIrradianceGeometry)) {
        return false;
    }

    float3 kept = asfloat(uint3(sdfViewsProbeHistory[previous], sdfViewsProbeHistory[previous + 1u], sdfViewsProbeHistory[previous + 2u]));
    float3 keptNormal = sdfVisibilityUnpackNormal(sdfViewsProbeHistory[previous + SdfViewsProbeHistoryNormalWord]);

    return sdfViewsProbesCompatible((surfacePoint - kept), keptNormal, normal, tolerance);
}
// Finds a preceding entry a receiver of this frame may reuse: the receiver projected into the preceding view lands in one
// preceding cell, and the cells beside it toward that point are tried after it, since the preceding representatives
// stood anywhere in their cells. Returns whether one stands, and in `previous` its first word.
bool sdfViewsProbeHistoryFind(uint viewIndex, float3 surfacePoint, float3 normal, float2 tolerance, out uint previous) {
    previous = 0u;

    float2 pixel;

    if (!sdfProjectView(passGroup.previousView, (surfacePoint - passGroup.previousView[0].xyz), pixel) || any(pixel < 0.0)) {
        return false;
    }

    uint2 cells = sdfViewsProbeCells();
    float2 place = (pixel / float(SDF_VIEWS_PROBE_RATE));
    uint2 cell = (uint2)place;
    // Toward the nearer neighbour on each axis, never below the first cell.
    int2 side = int2(select(frac(place) < 0.5, -1, 1));
    uint2 beside = uint2(clamp(int2(cell) + side, int2(0, 0), int2(cells) - 1));

    [loop] for (uint candidate = 0u; (candidate < 4u); candidate++) {
        uint2 at = uint2(((candidate & 1u) != 0u) ? beside.x : cell.x, ((candidate & 2u) != 0u) ? beside.y : cell.y);

        if (((candidate == 0u) || any(at != cell)) && sdfViewsProbeHistoryStands(at, viewIndex, cells, surfacePoint, normal, tolerance, previous)) {
            return true;
        }
    }
    return false;
}

#endif
