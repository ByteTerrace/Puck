// The probe lookup's history (SdfWorldPackage.ViewsProbeHistory): the gather each cell's representative read, kept for the
// view's next render, whose lookup reuses it for a receiver compatible with the one it was taken at instead of gathering
// again. Every view's fragment holds the history's two allocations, a nested view's at the extent it is shown at: reuse
// needs no jittered history, only the preceding render's camera and grid, which every view's pass block carries
// (previousView), and an epoch that breaks (another binding, a parked view shown again) clears that camera. A pass bound
// to storage that does not hold every cell of its view (the dummy) neither reads nor writes it
// (sdfViewsProbeHistoryCovers).
//
// What makes a kept gather the gather its receiver would read now. A gather is a function of its receiver, the published
// windows and the published probes' geometry (cells, offsets, validity, surface maps) and nothing else once it is
// complete (sdfMeshProbesAtComplete: no corner it consulted was missing), since a relight changes only the light the
// lookup blends again each frame. The pass block's two serials (SdfMeshIrradianceParameters.Geometry and Windows) move
// with every publication that changes those inputs: the geometry serial with a retrace of a held probe or a window laid
// out again, the window serial with a window's move. An entry stands while its geometry serial is the pass block's. Past
// a window's move it stands only where it was found to survive one (sdfViewsProbeHistorySurvives): its gather read no
// stage another window's place decides, and the level it chose still holds its receiver inside the cells of one cage,
// whose slots a move keeps since it reassigns only the slots of cells that left. A probe landing in a missing corner moves
// no serial, so an incomplete gather is never kept. The receiver a gather is reused for passes the same test a pixel
// passes to share its cell representative's gather within one frame (sdfViewsProbesCompatible), always against the
// receiver the gather was taken at, so reuse never drifts across frames.
//
// An entry is SDF_VIEWS_PROBE_HISTORY_WORDS words, one a plane: word w of the entry of cell index c lies at w times the
// plane's length plus c (sdfViewsProbeHistoryRead, sdfViewsProbeHistoryWrite), so the lanes of a wave, which take
// neighbouring cells, read and write each word in one run of the buffer rather than one sector a lane. Its words:
// 0 to 2: the receiver's surface point, as floats.
// 3: its geometric normal as the visibility record packs it (sdfVisibilityPackNormal).
// 4 to 14: the gather as an extra slot holds it: the two cages, the sixteen UNORM16 weights and the share and measured
//     flags (sdfViewsProbesFlags).
// 15: SdfViewsProbeHistoryKept where the entry holds a gather, zero otherwise (a zeroed allocation holds none).
// 16: the geometry serial the gather stands under.
// 17: the window serial it was last found standing under.
// 18: the level its gather chose in bits 0 to 1, and in bit 2 whether it survives a window's move that keeps that level
//     holding its receiver inside one cage.
#ifndef PASSES_SDF_VIEWS_PROBE_HISTORY_HLSLI
#define PASSES_SDF_VIEWS_PROBE_HISTORY_HLSLI
#include "sdf-views-probes.hlsli"
#include "../frame/sdf-reprojection.hlsli"

static const uint SdfViewsProbeHistoryNormalWord = 3u;
static const uint SdfViewsProbeHistoryGatherWord = 4u;
static const uint SdfViewsProbeHistoryStateWord = 15u;
static const uint SdfViewsProbeHistoryGeometryWord = 16u;
static const uint SdfViewsProbeHistoryWindowsWord = 17u;
static const uint SdfViewsProbeHistoryHoldWord = 18u;
static const uint SdfViewsProbeHistorySurvives = 4u;
static const uint SdfViewsProbeHistoryKept = 0x4B455054u;

// A cell's entry index: its place among every view's cells, raster order within a view.
uint sdfViewsProbeHistoryEntry(uint2 cell, uint viewIndex) {
    uint2 cells = sdfViewsProbeCells();

    return ((((viewIndex * cells.y) + cell.y) * cells.x) + cell.x);
}
// Word `word` of the preceding frame's entry `entry`, and of this frame's: a plane holds one word of every entry.
uint sdfViewsProbeHistoryRead(uint entry, uint word) {
    uint count;
    uint stride;

    sdfViewsProbeHistory.GetDimensions(count, stride);
    return sdfViewsProbeHistory[(word * (count / SDF_VIEWS_PROBE_HISTORY_WORDS)) + entry];
}
void sdfViewsProbeHistoryWrite(uint entry, uint word, uint value) {
    uint count;
    uint stride;

    sdfViewsProbeHistoryRW.GetDimensions(count, stride);
    sdfViewsProbeHistoryRW[(word * (count / SDF_VIEWS_PROBE_HISTORY_WORDS)) + entry] = value;
}

// Whether a history buffer's planes hold an entry for every cell of view `viewIndex`: the fragment's history does, and
// the dummy a pass with no history port binds does not, so a view is kept or read only through storage that holds it.
bool sdfViewsProbeHistoryCovers(uint count, uint viewIndex) {
    uint2 cells = sdfViewsProbeCells();

    return ((count / SDF_VIEWS_PROBE_HISTORY_WORDS) > sdfViewsProbeHistoryEntry((cells - 1u), viewIndex));
}
// Whether the view keeps a history, and whether this render may read the preceding one's: the history it reads holds the
// view, the preceding render's camera is known and its render grid is this one's, so a preceding cell is addressed as
// this render's.
bool sdfViewsProbeHistoryKeeps(uint viewIndex) {
    uint count;
    uint stride;

    sdfViewsProbeHistoryRW.GetDimensions(count, stride);
    return sdfViewsProbeHistoryCovers(count, viewIndex);
}
bool sdfViewsProbeHistoryReads(uint2 dims, uint viewIndex) {
    uint count;
    uint stride;

    sdfViewsProbeHistory.GetDimensions(count, stride);
    return (sdfViewsProbeHistoryKeeps(viewIndex) && sdfViewsProbeHistoryCovers(count, viewIndex)
        && (passGroup.previousView[0].w != 0.0) && all(uint2(passGroup.previousView[4].xy) == dims));
}

// Whether the published windows hold a pushed receiver as a kept gather at level `chosen` needs them to: that level is
// still the one its gather chooses, its lattice holds the receiver inside the cells of one cage, and no finer level's
// boundary probes reach it, so the gather reads the same cage of the same slots.
bool sdfViewsProbeHistoryHolds(float3 pushed, uint chosen) {
    if ((chosen >= SDF_MESH_IRRADIANCE_LEVELS) || (sdfMeshIrradianceChosen(pushed) != chosen)) {
        return false;
    }

    int3 low = passGroup.meshIrradianceLevels[chosen].xyz;
    float3 unclamped = ((pushed / sdfMeshIrradianceSpacing(chosen)) - 0.5);
    bool held = (all(unclamped >= (float3)low) && all(unclamped < (float3)(low + sdfMeshIrradianceDims(chosen) - 1)));

    [unroll] for (uint finer = 0u; (finer < SDF_MESH_IRRADIANCE_LEVELS); finer++) {
        float3 spacing = sdfMeshIrradianceSpacing(finer);
        int3 dims = sdfMeshIrradianceDims(finer);

        if ((finer < chosen) && all(spacing > 0.0) && all(dims > 0)) {
            held = (held && (sdfMeshIrradianceBeyond(pushed, spacing, passGroup.meshIrradianceLevels[finer].xyz, dims) > sdfMeshIrradianceReach(spacing)));
        }
    }
    return held;
}
// A pushed receiver's hold word for a gather it read, `windowBound` as the gather reported it.
uint sdfViewsProbeHistoryHoldOf(float3 pushed, bool windowBound) {
    uint chosen = sdfMeshIrradianceChosen(pushed);

    return (min(chosen, 3u) | ((!windowBound && sdfViewsProbeHistoryHolds(pushed, chosen)) ? SdfViewsProbeHistorySurvives : 0u));
}

// Writes a cell's entry: the gather a receiver at `surfacePoint` with the geometric normal `normal` (`packedNormal`, packed)
// read, with the hold word `hold`, or, with `kept` false, an entry that holds none.
void sdfViewsProbeHistoryStore(uint entry, bool kept, float3 surfacePoint, uint packedNormal, uint hold, SdfMeshProbes probes) {
    if (!kept) {
        sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryStateWord, 0u);
        return;
    }
    sdfViewsProbeHistoryWrite(entry, 0u, asuint(surfacePoint.x));
    sdfViewsProbeHistoryWrite(entry, 1u, asuint(surfacePoint.y));
    sdfViewsProbeHistoryWrite(entry, 2u, asuint(surfacePoint.z));
    sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryNormalWord, packedNormal);
    sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryGatherWord, probes.cage);
    sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryGatherWord + 1u, probes.remainderCage);
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint low = (uint)round(saturate(probes.weight[2u * pair]) * 65535.0);
        uint high = (uint)round(saturate(probes.weight[(2u * pair) + 1u]) * 65535.0);

        sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryGatherWord + 2u + pair, (low | (high << 16u)));
    }
    sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryGatherWord + 10u, sdfViewsProbesFlags(probes));
    sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryStateWord, SdfViewsProbeHistoryKept);
    sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryGeometryWord, passGroup.meshIrradianceGeometry.x);
    sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryWindowsWord, passGroup.meshIrradianceGeometry.y);
    sdfViewsProbeHistoryWrite(entry, SdfViewsProbeHistoryHoldWord, hold);
}
// Copies a preceding entry, which a receiver of this frame reused, into this frame's entry: the receiver stays the one
// the gather was taken at, and the entry now stands under the pass block's window serial.
void sdfViewsProbeHistoryCarry(uint entry, uint previous) {
    [unroll] for (uint word = 0u; (word < SDF_VIEWS_PROBE_HISTORY_WORDS); word++) {
        sdfViewsProbeHistoryWrite(entry, word, ((word == SdfViewsProbeHistoryWindowsWord) ? passGroup.meshIrradianceGeometry.y : sdfViewsProbeHistoryRead(previous, word)));
    }
}
// The gather a preceding entry holds, its weights as the entry quantized them.
SdfMeshProbes sdfViewsProbeHistoryLoad(uint previous) {
    SdfMeshProbes probes = sdfMeshProbesNone();
    uint flags = sdfViewsProbeHistoryRead(previous, (SdfViewsProbeHistoryGatherWord + 10u));

    probes.cage = sdfViewsProbeHistoryRead(previous, SdfViewsProbeHistoryGatherWord);
    probes.remainderCage = sdfViewsProbeHistoryRead(previous, (SdfViewsProbeHistoryGatherWord + 1u));
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint word = sdfViewsProbeHistoryRead(previous, (SdfViewsProbeHistoryGatherWord + 2u + pair));

        probes.weight[2u * pair] = (float(word & 0xFFFFu) / 65535.0);
        probes.weight[(2u * pair) + 1u] = (float(word >> 16u) / 65535.0);
    }
    probes.share = (float(flags & 0xFFFFu) / 65535.0);
    probes.measured = ((flags & SdfViewsProbeMeasured) != 0u);
    probes.remainderMeasured = ((flags & SdfViewsProbeRemainderMeasured) != 0u);
    return probes;
}
// Whether the preceding entry of `cell` holds a gather this frame's receiver may read: kept under the pass block's
// geometry serial, and under its window serial or surviving the windows' move since (sdfViewsProbeHistoryHolds), at a
// receiver the present one is compatible with within `tolerance` (sdfViewsProbeTolerance).
bool sdfViewsProbeHistoryStands(uint2 cell, uint viewIndex, uint2 cells, float3 surfacePoint, float3 normal, float2 tolerance, out uint previous) {
    previous = sdfViewsProbeHistoryEntry(cell, viewIndex);
    if (any(cell >= cells) || (sdfViewsProbeHistoryRead(previous, SdfViewsProbeHistoryStateWord) != SdfViewsProbeHistoryKept)
        || (sdfViewsProbeHistoryRead(previous, SdfViewsProbeHistoryGeometryWord) != passGroup.meshIrradianceGeometry.x)) {
        return false;
    }

    float3 kept = asfloat(uint3(sdfViewsProbeHistoryRead(previous, 0u), sdfViewsProbeHistoryRead(previous, 1u), sdfViewsProbeHistoryRead(previous, 2u)));
    float3 keptNormal = sdfVisibilityUnpackNormal(sdfViewsProbeHistoryRead(previous, SdfViewsProbeHistoryNormalWord));

    if (!sdfViewsProbesCompatible((surfacePoint - kept), keptNormal, normal, tolerance)) {
        return false;
    }
    if (sdfViewsProbeHistoryRead(previous, SdfViewsProbeHistoryWindowsWord) == passGroup.meshIrradianceGeometry.y) {
        return true;
    }

    uint hold = sdfViewsProbeHistoryRead(previous, SdfViewsProbeHistoryHoldWord);

    return (((hold & SdfViewsProbeHistorySurvives) != 0u)
        && sdfViewsProbeHistoryHolds((kept + (keptNormal * SDF_MESH_IRRADIANCE_NORMAL_BIAS)), (hold & 3u)));
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
