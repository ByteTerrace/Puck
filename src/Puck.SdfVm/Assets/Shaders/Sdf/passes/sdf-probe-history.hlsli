// A history of irradiance gathers (sdfMeshProbesAt), one entry a cell of SDF_VIEWS_PROBE_RATE by SDF_VIEWS_PROBE_RATE
// render pixels of every view, kept for the view's next render, which reuses an entry for a receiver compatible with the
// one it was taken at (sdf-probe-reuse.hlsli) instead of gathering again. Two readers keep one, each in a history buffer
// pair of its own that it passes to every function here: the probe lookup the gathers of the opaque surface
// (SdfWorldPackage.ViewsProbeHistory, sdf-world-views-probes.comp.hlsl) and the glass pass the gathers of the nearest glass
// face (SdfWorldPackage.GlassProbeHistory, sdf-glass-stage.hlsli). Every view keeps them, a nested view at the extent it
// is shown at: reuse needs no jittered history, only the preceding render's camera and grid to find an entry by
// (previousView). A pass bound to storage that does not hold every cell of its view (the dummy) neither reads nor writes
// it (sdfProbeHistoryCovers).
//
// What makes a kept gather the gather its receiver would read now. A gather is a function of its receiver, the published
// windows and the published probes' geometry (cells, offsets, validity, surface maps) and nothing else once it is
// complete (sdfMeshProbesAtComplete: no corner it consulted was missing), since a relight changes only the light the
// reader blends again each frame. The pass block's two serials (SdfMeshIrradianceParameters.Geometry and Windows) move
// with every publication that changes those inputs: the geometry serial with a retrace of a held probe or a window laid
// out again, the window serial with a window's move. The geometry serial is unique to one publication of one volume
// across the process and never zero, so an entry another residency's volume wrote, or one no reader wrote since the
// buffer was zeroed, never stands; an entry therefore validates itself whatever render wrote it, and a pass that skipped
// cells (the glass pass dispatches only the tiles that hold glass) or drew nothing leaves entries that are still safe to
// read. An entry stands while its geometry serial is the pass block's. Past a window's move it stands only where it was
// found to survive one (SdfProbeHistorySurvives): its gather read no stage another window's place decides, and the level
// it chose still holds its receiver inside the cells of one cage, whose slots a move keeps since it reassigns only the
// slots of cells that left. A probe landing in a missing corner moves no serial, so an incomplete gather is never kept.
// The receiver a gather is reused for passes the reuse test against the receiver the gather was taken at, so reuse never
// drifts across frames.
//
// An entry is SDF_VIEWS_PROBE_HISTORY_WORDS words, one a plane: word w of the entry of cell index c lies at w times the
// plane's length plus c (sdfProbeHistoryRead, sdfProbeHistoryWrite), so the lanes of a wave, which take neighbouring
// cells, read and write each word in one run of the buffer rather than one sector a lane. Its words:
// 0 to 2: the receiver's surface point, as floats.
// 3: the normal the gather was taken at, as the visibility record packs a normal (sdfVisibilityPackNormal).
// 4 to 14: the gather as an extra slot holds it: the two cages, the sixteen UNORM16 weights, and in word 14 the share and
//     measured flags (sdfViewsProbesFlags) with, from bit SdfProbeHistoryHoldShift, the level its gather chose in two
//     bits and above them whether it survives a window's move that keeps that level holding its receiver inside one cage.
// 15: the geometry serial the gather stands under; zero where the entry holds no gather.
// 16: the window serial it was last found standing under.
#ifndef PASSES_SDF_PROBE_HISTORY_HLSLI
#define PASSES_SDF_PROBE_HISTORY_HLSLI
#include "sdf-probe-reuse.hlsli"
#include "../frame/sdf-reprojection.hlsli"

static const uint SdfProbeHistoryNormalWord = 3u;
static const uint SdfProbeHistoryGatherWord = 4u;
static const uint SdfProbeHistoryFlagsWord = 14u;
static const uint SdfProbeHistoryGeometryWord = 15u;
static const uint SdfProbeHistoryWindowsWord = 16u;
// The hold's place in the flags word, above the share and measured flags, and its survival bit.
static const uint SdfProbeHistoryHoldShift = 24u;
static const uint SdfProbeHistorySurvives = 4u;
#if SDF_VIEWS_PROBE_HISTORY_WORDS != 17
#error The probe history's entry layout holds seventeen words.
#endif

// A cell's entry index: its place among every view's cells, raster order within a view.
uint sdfProbeHistoryEntry(uint2 cell, uint viewIndex) {
    uint2 cells = sdfViewsProbeCells();

    return ((((viewIndex * cells.y) + cell.y) * cells.x) + cell.x);
}
// Word `word` of a preceding render's entry `entry`, and of this render's.
uint sdfProbeHistoryRead(StructuredBuffer<uint> history, uint entry, uint word) {
    uint count;
    uint stride;

    history.GetDimensions(count, stride);
    return history[(word * (count / SDF_VIEWS_PROBE_HISTORY_WORDS)) + entry];
}
void sdfProbeHistoryWrite(RWStructuredBuffer<uint> written, uint entry, uint word, uint value) {
    uint count;
    uint stride;

    written.GetDimensions(count, stride);
    written[(word * (count / SDF_VIEWS_PROBE_HISTORY_WORDS)) + entry] = value;
}

// Whether a history buffer of `count` words holds an entry for every cell of view `viewIndex`: the fragment's history
// does, and the dummy a pass with no history port binds does not.
bool sdfProbeHistoryCovers(uint count, uint viewIndex) {
    uint2 cells = sdfViewsProbeCells();

    return ((count / SDF_VIEWS_PROBE_HISTORY_WORDS) > sdfProbeHistoryEntry((cells - 1u), viewIndex));
}
// Whether the view keeps a history in `written`, and whether this render may read the preceding one's from `history`:
// the history it reads holds the view, the preceding render's camera is known and its render grid is this one's, so a
// preceding cell is addressed as this render's.
bool sdfProbeHistoryKeeps(RWStructuredBuffer<uint> written, uint viewIndex) {
    uint count;
    uint stride;

    written.GetDimensions(count, stride);
    return sdfProbeHistoryCovers(count, viewIndex);
}
bool sdfProbeHistoryReads(StructuredBuffer<uint> history, bool keeps, uint2 dims, uint viewIndex) {
    uint count;
    uint stride;

    history.GetDimensions(count, stride);
    return (keeps && sdfProbeHistoryCovers(count, viewIndex)
        && (passGroup.previousView[0].w != 0.0) && all(uint2(passGroup.previousView[4].xy) == dims));
}

// Whether the published windows hold a pushed receiver as a kept gather at level `chosen` needs them to: that level is
// still the one its gather chooses, its lattice holds the receiver inside the cells of one cage, and no finer level's
// boundary probes reach it, so the gather reads the same cage of the same slots.
bool sdfProbeHistoryHolds(float3 pushed, uint chosen) {
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
// A pushed receiver's hold for a gather it read, `windowBound` as the gather reported it: the level in bits 0 to 1 and
// the survival bit above them.
uint sdfProbeHistoryHoldOf(float3 pushed, bool windowBound) {
    uint chosen = sdfMeshIrradianceChosen(pushed);

    return (min(chosen, 3u) | ((!windowBound && sdfProbeHistoryHolds(pushed, chosen)) ? SdfProbeHistorySurvives : 0u));
}

// Writes entry `entry` of `written`: the gather a receiver at `surfacePoint`, taken at the normal `packedNormal` packs,
// read, with the hold `hold`, or, with `kept` false, an entry that holds none.
void sdfProbeHistoryStore(RWStructuredBuffer<uint> written, uint entry, bool kept, float3 surfacePoint, uint packedNormal, uint hold, SdfMeshProbes probes) {
    if (!kept) {
        sdfProbeHistoryWrite(written, entry, SdfProbeHistoryGeometryWord, 0u);
        return;
    }
    sdfProbeHistoryWrite(written, entry, 0u, asuint(surfacePoint.x));
    sdfProbeHistoryWrite(written, entry, 1u, asuint(surfacePoint.y));
    sdfProbeHistoryWrite(written, entry, 2u, asuint(surfacePoint.z));
    sdfProbeHistoryWrite(written, entry, SdfProbeHistoryNormalWord, packedNormal);
    sdfProbeHistoryWrite(written, entry, SdfProbeHistoryGatherWord, probes.cage);
    sdfProbeHistoryWrite(written, entry, SdfProbeHistoryGatherWord + 1u, probes.remainderCage);
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint low = (uint)round(saturate(probes.weight[2u * pair]) * 65535.0);
        uint high = (uint)round(saturate(probes.weight[(2u * pair) + 1u]) * 65535.0);

        sdfProbeHistoryWrite(written, entry, SdfProbeHistoryGatherWord + 2u + pair, (low | (high << 16u)));
    }
    sdfProbeHistoryWrite(written, entry, SdfProbeHistoryFlagsWord, (sdfViewsProbesFlags(probes) | (hold << SdfProbeHistoryHoldShift)));
    sdfProbeHistoryWrite(written, entry, SdfProbeHistoryGeometryWord, passGroup.meshIrradianceGeometry.x);
    sdfProbeHistoryWrite(written, entry, SdfProbeHistoryWindowsWord, passGroup.meshIrradianceGeometry.y);
}
// Copies a preceding entry, which a receiver of this render reused, into this render's entry `entry`: the receiver stays
// the one the gather was taken at, and the entry now stands under the pass block's window serial.
void sdfProbeHistoryCarry(StructuredBuffer<uint> history, RWStructuredBuffer<uint> written, uint entry, uint previous) {
    [unroll] for (uint word = 0u; (word < SDF_VIEWS_PROBE_HISTORY_WORDS); word++) {
        sdfProbeHistoryWrite(written, entry, word, ((word == SdfProbeHistoryWindowsWord) ? passGroup.meshIrradianceGeometry.y : sdfProbeHistoryRead(history, previous, word)));
    }
}
// The gather a preceding entry holds, its weights as the entry quantized them.
SdfMeshProbes sdfProbeHistoryLoad(StructuredBuffer<uint> history, uint previous) {
    SdfMeshProbes probes = sdfMeshProbesNone();
    uint flags = sdfProbeHistoryRead(history, previous, SdfProbeHistoryFlagsWord);

    probes.cage = sdfProbeHistoryRead(history, previous, SdfProbeHistoryGatherWord);
    probes.remainderCage = sdfProbeHistoryRead(history, previous, (SdfProbeHistoryGatherWord + 1u));
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint word = sdfProbeHistoryRead(history, previous, (SdfProbeHistoryGatherWord + 2u + pair));

        probes.weight[2u * pair] = (float(word & 0xFFFFu) / 65535.0);
        probes.weight[(2u * pair) + 1u] = (float(word >> 16u) / 65535.0);
    }
    probes.share = (float(flags & 0xFFFFu) / 65535.0);
    probes.measured = ((flags & SdfViewsProbeMeasured) != 0u);
    probes.remainderMeasured = ((flags & SdfViewsProbeRemainderMeasured) != 0u);
    return probes;
}
// Whether the preceding entry of `cell` in `history` holds a gather this render's receiver may read: kept under the pass
// block's geometry serial, and under its window serial or surviving the windows' move since (sdfProbeHistoryHolds), at a
// receiver the present one is compatible with within `tolerance` (sdfViewsProbeTolerance).
bool sdfProbeHistoryStands(StructuredBuffer<uint> history, uint2 cell, uint viewIndex, uint2 cells, float3 surfacePoint, float3 normal, float2 tolerance, out uint previous) {
    previous = sdfProbeHistoryEntry(cell, viewIndex);
    uint serial = passGroup.meshIrradianceGeometry.x;

    if (any(cell >= cells) || (serial == 0u) || (sdfProbeHistoryRead(history, previous, SdfProbeHistoryGeometryWord) != serial)) {
        return false;
    }

    float3 kept = asfloat(uint3(sdfProbeHistoryRead(history, previous, 0u), sdfProbeHistoryRead(history, previous, 1u), sdfProbeHistoryRead(history, previous, 2u)));
    float3 keptNormal = sdfVisibilityUnpackNormal(sdfProbeHistoryRead(history, previous, SdfProbeHistoryNormalWord));

    if (!sdfViewsProbesCompatible((surfacePoint - kept), keptNormal, normal, tolerance)) {
        return false;
    }
    if (sdfProbeHistoryRead(history, previous, SdfProbeHistoryWindowsWord) == passGroup.meshIrradianceGeometry.y) {
        return true;
    }

    uint hold = (sdfProbeHistoryRead(history, previous, SdfProbeHistoryFlagsWord) >> SdfProbeHistoryHoldShift);

    return (((hold & SdfProbeHistorySurvives) != 0u)
        && sdfProbeHistoryHolds((kept + (keptNormal * SDF_MESH_IRRADIANCE_NORMAL_BIAS)), (hold & 3u)));
}
// Finds a preceding entry of `history` a receiver of this render may reuse: the receiver projected into the preceding
// view lands in one preceding cell, and the cells beside it toward that point are tried after it, since the preceding
// receivers stood anywhere in their cells. Returns whether one stands, and in `previous` its index.
bool sdfProbeHistoryFind(StructuredBuffer<uint> history, uint viewIndex, float3 surfacePoint, float3 normal, float2 tolerance, out uint previous) {
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

        if (((candidate == 0u) || any(at != cell)) && sdfProbeHistoryStands(history, at, viewIndex, cells, surfacePoint, normal, tolerance, previous)) {
            return true;
        }
    }
    return false;
}

#endif
