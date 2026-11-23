// The probe lookup pass (SdfWorldPackage.Parts.ViewsProbes): one invocation a cell of SDF_VIEWS_PROBE_RATE by
// SDF_VIEWS_PROBE_RATE pixels of the render extent, in 8x8 groups. Each cell's representative is its first pixel, from
// the cell's centre outward, whose hit shades with the volume (sdfViewsProbesWanted); a group finds its own cells' and
// those of the ring of cells about it, each the same function of the visibility records whichever group asks. Each
// invocation then writes its cell's pixels' mixes (sdf-views-probes.hlsli): a pixel's shares of the representatives of
// the four cells about it, each cell's bilinear share weighed by how nearly the pixel lies on the representative's
// surface (sdfProbeBlend), or, for a pixel none of them serves, one of the cell's extras, a gather of its own that the
// cell's later unserved pixels compatible with it share.
// The group then runs every gather its cells asked for as one list, a gather a lane: the view first reuses its
// preceding render's gather where one stands for the receiver (sdf-probe-history.hlsli), and the gathers no entry
// serves run as a second list, so a cell of foliage with four gathers costs the group four lanes rather than one lane
// four gathers while its neighbours wait. Either way a job's gather lands in group memory, and one last list resolves
// every job's into a slot, a job a lane: the representative's in its cell's record and every extra's in the pool, which
// views reads in place of gathering. The kernel therefore holds the resolve once, and a lane that reused a gather
// resolves beside one that gathered. Each representative's gather is written into the history for the next render.
#define SDF_VIEWS_PROBES_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-probes.hlsli"
#include "sdf-probe-history.hlsli"
#include "sdf-views-rate.hlsli"

#if SDF_VIEWS_PROBE_RATE != 4
#error The centre-outward order of a cell's pixels (sdfProbeOrder) and a job's place are a four-pixel cell's.
#endif
static const uint SdfProbeCellPixels = (SDF_VIEWS_PROBE_RATE * SDF_VIEWS_PROBE_RATE);
static const uint SdfProbeGroupCells = (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE);
// A cell's gathers at most: its representative's and its extras'.
static const uint SdfProbeCellJobs = (1u + SdfViewsProbeExtras);
// The group's cells with the ring of cells about them, whose representatives the group's pixels blend.
static const uint SdfProbeRingEdge = (SDF_VISIBILITY_GROUP_EDGE + 2u);
static const uint SdfProbeRingCells = (SdfProbeRingEdge * SdfProbeRingEdge);

// The group's gathers: each names its cell's lane in the group in bits 0 to 5, its pixel's place in the cell in bits 6 to
// 9, and, for an extra, one more than its place among the cell's extras in bits 10 to 11 (zero for the representative).
groupshared uint sdfProbeJobs[SdfProbeGroupCells * SdfProbeCellJobs];
groupshared uint sdfProbeJobCount;
// The gathers no preceding entry served, each its place in sdfProbeJobs.
groupshared uint sdfProbeMisses[SdfProbeGroupCells * SdfProbeCellJobs];
groupshared uint sdfProbeMissCount;
// Each job's gather, as a history entry holds one (sdf-probe-history.hlsli): its two cages, its sixteen UNORM16 weights
// in eight words, then its share and measured flags (sdfViewsProbesFlags).
static const uint SdfProbeGatherWords = 11u;
groupshared uint sdfProbeGathers[(SdfProbeGroupCells * SdfProbeCellJobs) * SdfProbeGatherWords];
// Each cell's state for its representative's record: the representative's place in bits 0 to 7, the extras' count in bits
// 8 to 15 and the coarse-shading rate above them.
groupshared uint sdfProbeCellState[SdfProbeGroupCells];
// The group's extras: how many its cells hold, each cell's first among them, and the pool slot of the group's first, or
// SdfProbeNoExtras for a group the pool has no room for, whose cells then keep none.
groupshared uint sdfProbeExtraCount;
groupshared uint sdfProbeCellExtras[SdfProbeGroupCells];
groupshared uint sdfProbeExtraBase;
static const uint SdfProbeNoExtras = 0xFFFFFFFFu;
// The representatives of the group's cells and its ring, a row of SdfProbeRingEdge cells from the cell before the
// group's first: each one's surface point with one in W where the cell holds one, and its packed normal.
groupshared float4 sdfProbeRingPoint[SdfProbeRingCells];
groupshared uint sdfProbeRingNormal[SdfProbeRingCells];
groupshared uint sdfProbeRingPlace[SdfProbeRingCells];

// The surface point, the geometric normal (and its packed word) and the metres the pixel spans at its hit, formed exactly
// as the views stage forms them.
void sdfProbeReceiver(ViewportData view, uint2 pixel, uint viewIndex, out float3 surfacePoint, out float3 normal, out uint packedNormal, out float2 tolerance) {
    SdfPixel p = sdfPixelAt(view, pixel, viewIndex);
    uint record = worldVisibilityRecord(pixel, viewIndex);
    float t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);

    surfacePoint = sdfPixelSurfacePoint(p, t);
    packedNormal = sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord];
    normal = sdfVisibilityUnpackNormal(packedNormal);
    tolerance = sdfViewsProbeTolerance((p.pixelFootprint * t), abs(dot(p.rayDirection, normal)));
}
// A cell's pixels from its centre outward: the place (x + SDF_VIEWS_PROBE_RATE * y) of the pixel at `rank`, the four
// about the centre first, then the edges' middles, then the corners.
uint sdfProbeOrder(uint rank) {
    return ((((rank < 8u) ? 0x7421A965u : 0xFC30EDB8u) >> (4u * (rank & 7u))) & 15u);
}
// Whether a pixel is a hit that shades with the volume.
bool sdfProbeWants(uint2 pixel, uint viewIndex, int viewMode) {
    SdfVisibility visibility = sdfLoadVisibility(worldVisibilityRecord(pixel, viewIndex));

    return (sdfVisibilityHit(visibility) && sdfViewsProbesWanted(viewMode, visibility.material));
}
// Finds cell `cell`'s representative and leaves it in the ring's entry `ring`: none for a cell outside the view or with
// no pixel that wants the lookup.
void sdfProbeRepresentative(ViewportData view, uint viewIndex, int2 cell, uint2 dims, int viewMode, uint ring) {
    float4 found = float4(0.0, 0.0, 0.0, 0.0);
    uint packedNormal = 0u;
    uint place = 0u;

#if !defined(SDF_VIEWS_OMIT_PROBES)
    if (all(cell >= 0) && all(cell < int2(sdfViewsProbeCells()))) {
        uint2 origin = (uint2(cell) * SDF_VIEWS_PROBE_RATE);

        [loop] for (uint rank = 0u; ((rank < SdfProbeCellPixels) && (found.w == 0.0)); rank++) {
            uint candidate = sdfProbeOrder(rank);
            uint2 pixel = (origin + uint2((candidate % SDF_VIEWS_PROBE_RATE), (candidate / SDF_VIEWS_PROBE_RATE)));

            if (all(pixel < dims) && sdfProbeWants(pixel, viewIndex, viewMode)) {
                float3 surfacePoint;
                float3 normal;
                float2 tolerance;

                sdfProbeReceiver(view, pixel, viewIndex, surfacePoint, normal, packedNormal, tolerance);
                found = float4(surfacePoint, 1.0);
                place = candidate;
            }
        }
    }
#endif
    sdfProbeRingPoint[ring] = found;
    sdfProbeRingNormal[ring] = packedNormal;
    sdfProbeRingPlace[ring] = place;
}
// The ring's cell `ring` (0 to 35) as an offset from the group's first cell: the row before the group, the row after it,
// then the column before and the column after.
int2 sdfProbeRingCell(uint ring) {
    int edge = (int)SDF_VISIBILITY_GROUP_EDGE;

    if (ring < SdfProbeRingEdge) { return int2(((int)ring - 1), -1); }
    if (ring < (2u * SdfProbeRingEdge)) { return int2(((int)(ring - SdfProbeRingEdge) - 1), edge); }
    if (ring < ((2u * SdfProbeRingEdge) + SDF_VISIBILITY_GROUP_EDGE)) { return int2(-1, (int)(ring - (2u * SdfProbeRingEdge))); }
    return int2(edge, (int)(ring - ((2u * SdfProbeRingEdge) + SDF_VISIBILITY_GROUP_EDGE)));
}
// The ring entry of the cell at `offset` from the group's first cell, each axis from -1 to SDF_VISIBILITY_GROUP_EDGE.
uint sdfProbeRingEntry(int2 offset) {
    return (uint)(((offset.y + 1) * (int)SdfProbeRingEdge) + (offset.x + 1));
}

// A job's cell, pixel and record.
struct SdfProbeJob {
    uint cellLane;
    uint extra;
    uint2 cell;
    uint2 pixel;
    uint record;
};
SdfProbeJob sdfProbeJobOf(uint job, uint2 firstCell, uint viewIndex) {
    SdfProbeJob decoded;
    uint place = ((job >> 6u) & 15u);

    decoded.cellLane = (job & 63u);
    decoded.extra = ((job >> 10u) & 3u);
    decoded.cell = (firstCell + uint2((decoded.cellLane % SDF_VISIBILITY_GROUP_EDGE), (decoded.cellLane / SDF_VISIBILITY_GROUP_EDGE)));
    decoded.pixel = ((decoded.cell * SDF_VIEWS_PROBE_RATE) + uint2((place % SDF_VIEWS_PROBE_RATE), (place / SDF_VIEWS_PROBE_RATE)));
    decoded.record = sdfViewsProbeRecord(decoded.cell, viewIndex);
    return decoded;
}
// Leaves job `at`'s gather in group memory, its weights quantized as a history entry holds them.
void sdfProbeGatherKeep(uint at, SdfMeshProbes probes) {
    uint first = (at * SdfProbeGatherWords);

    sdfProbeGathers[first] = probes.cage;
    sdfProbeGathers[first + 1u] = probes.remainderCage;
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint low = (uint)round(saturate(probes.weight[2u * pair]) * 65535.0);
        uint high = (uint)round(saturate(probes.weight[(2u * pair) + 1u]) * 65535.0);

        sdfProbeGathers[(first + 2u) + pair] = (low | (high << 16u));
    }
    sdfProbeGathers[first + 10u] = sdfViewsProbesFlags(probes);
}
// Leaves in job `at`'s place the gather preceding entry `previous` of `history` holds, word for word.
void sdfProbeGatherCarry(uint at, StructuredBuffer<uint> history, uint previous) {
    uint first = (at * SdfProbeGatherWords);

    [unroll] for (uint word = 0u; (word < 10u); word++) {
        sdfProbeGathers[first + word] = sdfProbeHistoryRead(history, previous, (SdfProbeHistoryGatherWord + word));
    }
    sdfProbeGathers[first + 10u] = (sdfProbeHistoryRead(history, previous, SdfProbeHistoryFlagsWord) & ((1u << SdfProbeHistoryHoldShift) - 1u));
}
// Job `at`'s gather from group memory.
SdfMeshProbes sdfProbeGatherOf(uint at) {
    SdfMeshProbes probes = sdfMeshProbesNone();
    uint first = (at * SdfProbeGatherWords);
    uint flags = sdfProbeGathers[first + 10u];

    probes.cage = sdfProbeGathers[first];
    probes.remainderCage = sdfProbeGathers[first + 1u];
    [unroll] for (uint pair = 0u; (pair < 8u); pair++) {
        uint word = sdfProbeGathers[(first + 2u) + pair];

        probes.weight[2u * pair] = (float(word & 0xFFFFu) / 65535.0);
        probes.weight[(2u * pair) + 1u] = (float(word >> 16u) / 65535.0);
    }
    probes.share = (float(flags & 0xFFFFu) / 65535.0);
    probes.measured = ((flags & SdfViewsProbeMeasured) != 0u);
    probes.remainderMeasured = ((flags & SdfViewsProbeRemainderMeasured) != 0u);
    return probes;
}
// Resolves a job's gather into its slot: the representative's in its cell's record, or an extra's in the pool. A group
// the pool has no room for stores no extra, and its records name none.
void sdfProbeStoreJob(SdfProbeJob job, SdfMeshProbes probes) {
    uint state = sdfProbeCellState[job.cellLane];
    uint extras = ((state >> 8u) & 0xFFu);
    uint base = sdfProbeExtraBase;
    uint first = ((base == SdfProbeNoExtras) ? 0u : (base + sdfProbeCellExtras[job.cellLane]));

    if (job.extra == 0u) {
        sdfViewsProbesStore(job.record, probes, (state & 0xFFu), (state >> 16u), ((base == SdfProbeNoExtras) ? 0u : extras), first);
    } else if (base != SdfProbeNoExtras) {
        sdfViewsProbesStoreSlot(sdfViewsProbePoolSlot(first + (job.extra - 1u)), probes, 0u);
    }
}
// The share, zero to one, with which a receiver blends the gather of a representative of one of the four cells about it,
// which it lies at `offset` from: by how far it stands off the representative's tangent plane and how far their normals
// turn, each falling to zero over the outer half of what is allowed, so a representative leaves a pixel's blend
// gradually rather than at a step. The reuse test's reach takes no part: the four cells stand within a cell and a half
// of the pixel on the screen however far that is along a surface seen at a grazing angle, and blending them is the
// lookup's resolution there. The distance allowed off the plane is the normal bias every receiver is already pushed by,
// or a cell's pixels where those are wider, or what a smooth surface leaves its own tangent plane by over the distance
// between the two: that distance along the surface times the sine of the turn between their normals, with
// SdfProbeBlendSlope for the relief a baked surface carries. A depth edge between parallel surfaces has no turn to
// allow it, so its two sides blend only where they stand within the bias of one plane.
static const float SdfProbeBlendSlope = 0.02;
float sdfProbeBlend(float3 offset, float3 representativeNormal, float3 normal, float2 tolerance) {
    float plane = abs(dot(offset, representativeNormal));
    float along = sqrt(max((dot(offset, offset) - (plane * plane)), 0.0));
    float allowed = max(max(SdfViewsProbeReach, (float(SDF_VIEWS_PROBE_RATE) * tolerance.y)), (along * (SdfProbeBlendSlope + length(cross(normal, representativeNormal)))));
    float facing = ((dot(normal, representativeNormal) - SdfViewsProbeFacing) / (0.5 * (1.0 - SdfViewsProbeFacing)));

    return (saturate(2.0 * (1.0 - (plane / allowed))) * saturate(facing));
}
// The mix of a pixel that blends the four cells from `base`, by `weights`, which hold a positive share: each cell's share
// of their sum in seven bits, the largest never rounding to nothing.
uint sdfProbeMixOf(float4 weights) {
    uint4 shares = (uint4)round((weights / dot(weights, float4(1.0, 1.0, 1.0, 1.0))) * 127.0);

    return (SdfViewsProbeMixServed | shares.x | (shares.y << 7u) | (shares.z << 14u) | (shares.w << 21u));
}

[numthreads(SDF_VISIBILITY_GROUP_EDGE, SDF_VISIBILITY_GROUP_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint3 groupId : SV_GroupID, uint groupLane : SV_GroupIndex) {
    uint viewIndex = worldViewOf(id.z);

    // Uniform across the group: every lane of it shares id.z.
    if (viewIndex >= passGroup.viewportCount) {
        return;
    }

    ViewportData view = worldView();
    uint2 dims = worldViewDims(view);
    uint2 origin = (id.xy * SDF_VIEWS_PROBE_RATE);
    int viewMode = (int)round(view.forward.w);
    uint2 firstCell = (groupId.xy * SDF_VISIBILITY_GROUP_EDGE);
    int2 local = int2(id.xy - firstCell);
    // Uniform across the view.
    bool keeps = sdfProbeHistoryKeeps(sdfViewsProbeHistoryRW, viewIndex);
    bool reads = sdfProbeHistoryReads(sdfViewsProbeHistory, keeps, dims, viewIndex);

    if (groupLane == 0u) {
        sdfProbeJobCount = 0u;
        sdfProbeMissCount = 0u;
        sdfProbeExtraCount = 0u;
        sdfProbeExtraBase = SdfProbeNoExtras;
    }

    uint rate = sdfRateTile(view, viewIndex, id.xy, origin);

    // Every cell's representative: each lane its own cell's, and the first lanes a cell of the ring each.
    sdfProbeRepresentative(view, viewIndex, int2(id.xy), dims, viewMode, sdfProbeRingEntry(local));
    if (groupLane < (SdfProbeRingCells - SdfProbeGroupCells)) {
        int2 ring = sdfProbeRingCell(groupLane);

        sdfProbeRepresentative(view, viewIndex, (int2(firstCell) + ring), dims, viewMode, sdfProbeRingEntry(ring));
    }
    GroupMemoryBarrierWithGroupSync();
    if (all(origin < dims) && all(id.xy < sdfViewsProbeCells())) {
        uint own = sdfProbeRingEntry(local);
        bool present = (sdfProbeRingPoint[own].w != 0.0);
        uint lane = sdfProbeRingPlace[own];
        // The cell's extras: how many, each one's pixel's place in four bits, and each one's receiver.
        uint extras = 0u;
        uint extraPlaces = 0u;
        float3 extraPoint[SdfViewsProbeExtras] = { float3(0.0, 0.0, 0.0), float3(0.0, 0.0, 0.0), float3(0.0, 0.0, 0.0) };
        float3 extraNormal[SdfViewsProbeExtras] = { float3(0.0, 0.0, 0.0), float3(0.0, 0.0, 0.0), float3(0.0, 0.0, 0.0) };

        [loop] for (uint place = 0u; place < SdfProbeCellPixels; place++) {
            uint2 pixel = (origin + uint2((place % SDF_VIEWS_PROBE_RATE), (place / SDF_VIEWS_PROBE_RATE)));

            if (any(pixel >= dims)) {
                continue;
            }

            uint mix = 0u;

            // A cell with no representative holds no pixel that wants the lookup.
            if (present && sdfProbeWants(pixel, viewIndex, viewMode)) {
                float2 fraction;
                int2 base = (sdfViewsProbeMixBase(pixel, fraction) - int2(firstCell));
                float4 bilinear = float4(((1.0 - fraction.x) * (1.0 - fraction.y)), (fraction.x * (1.0 - fraction.y)), ((1.0 - fraction.x) * fraction.y), (fraction.x * fraction.y));
                float3 surfacePoint;
                float3 normal;
                uint packedNormal;
                float2 tolerance;
                float4 weights = float4(0.0, 0.0, 0.0, 0.0);
                float4 held = float4(0.0, 0.0, 0.0, 0.0);

                sdfProbeReceiver(view, pixel, viewIndex, surfacePoint, normal, packedNormal, tolerance);
                [unroll] for (uint corner = 0u; (corner < 4u); corner++) {
                    uint ring = sdfProbeRingEntry(base + int2((corner & 1u), (corner >> 1u)));
                    float4 representative = sdfProbeRingPoint[ring];

                    if (representative.w != 0.0) {
                        held[corner] = bilinear[corner];
                        weights[corner] = (bilinear[corner]
                            * sdfProbeBlend((surfacePoint - representative.xyz), sdfVisibilityUnpackNormal(sdfProbeRingNormal[ring]), normal, tolerance));
                    }
                }
                if (dot(weights, float4(1.0, 1.0, 1.0, 1.0)) > 1.0e-4) {
                    mix = sdfProbeMixOf(weights);
                } else {
                    // No representative about the pixel serves it: an extra of the cell it is compatible with, a new one
                    // while the cell has room, or the cells about it by their bilinear shares alone.
                    uint extra = SdfViewsProbeExtras;

                    [unroll] for (uint candidate = 0u; (candidate < SdfViewsProbeExtras); candidate++) {
                        if ((extra == SdfViewsProbeExtras) && (candidate < extras)
                            && sdfViewsProbesCompatible((surfacePoint - extraPoint[candidate]), extraNormal[candidate], normal, tolerance)) {
                            extra = candidate;
                        }
                    }
                    if ((extra == SdfViewsProbeExtras) && (extras < SdfViewsProbeExtras)) {
                        extra = extras;
                        [unroll] for (uint added = 0u; (added < SdfViewsProbeExtras); added++) {
                            if (added == extras) {
                                extraPoint[added] = surfacePoint;
                                extraNormal[added] = normal;
                            }
                        }
                        extraPlaces |= (place << (4u * extras));
                        extras++;
                    }
                    mix = ((extra < SdfViewsProbeExtras) ? (SdfViewsProbeMixServed | SdfViewsProbeMixExtra | extra) : sdfProbeMixOf(held));
                }
            }
            sdfViewsProbeRecordsRW[sdfViewsProbeMixWord(pixel, viewIndex)] = mix;
        }

        if (!present) {
            sdfViewsProbesStoreEmpty(sdfViewsProbeRecord(id.xy, viewIndex), rate);
            if (keeps) {
                sdfProbeHistoryStore(sdfViewsProbeHistoryRW, sdfProbeHistoryEntry(id.xy, viewIndex), false, float3(0.0, 0.0, 0.0), 0u, 0u, sdfMeshProbesNone());
            }
        } else {
            uint first;

            InterlockedAdd(sdfProbeJobCount, (1u + extras), first);
            sdfProbeCellState[groupLane] = (lane | (extras << 8u) | (rate << 16u));
            sdfProbeCellExtras[groupLane] = 0u;
            if (extras != 0u) {
                uint firstExtra;

                InterlockedAdd(sdfProbeExtraCount, extras, firstExtra);
                sdfProbeCellExtras[groupLane] = firstExtra;
            }
            sdfProbeJobs[first] = (groupLane | (lane << 6u));
            [loop] for (uint extra = 0u; extra < extras; extra++) {
                sdfProbeJobs[(first + 1u) + extra] = (groupLane | (((extraPlaces >> (4u * extra)) & 15u) << 6u) | ((extra + 1u) << 10u));
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // The group takes its extras' pool slots together, so its cells' slots are adjacent and one lane counts them.
    if ((groupLane == 0u) && (sdfProbeExtraCount != 0u)) {
        uint taken;

        InterlockedAdd(sdfViewsProbeRecordsRW[sdfViewsProbePoolWord()], sdfProbeExtraCount, taken);
        if ((taken + sdfProbeExtraCount) <= sdfViewsProbePoolCapacity()) {
            sdfProbeExtraBase = taken;
        }
    }
    GroupMemoryBarrierWithGroupSync();

    uint jobs = sdfProbeJobCount;

    // The group's gathers a preceding entry serves, a lane each, striding by the group's lanes; every other goes to the
    // misses. A view that reads no history sends every gather there.
    [loop] for (uint at = groupLane; at < jobs; at += SdfProbeGroupCells) {
        uint job = sdfProbeJobs[at];
        SdfProbeJob decoded = sdfProbeJobOf(job, firstCell, viewIndex);
        float3 surfacePoint;
        float3 normal;
        uint packedNormal;
        float2 tolerance;
        uint previous;

        sdfProbeReceiver(view, decoded.pixel, viewIndex, surfacePoint, normal, packedNormal, tolerance);
        if (reads && sdfProbeHistoryFind(sdfViewsProbeHistory, viewIndex, surfacePoint, normal, tolerance, previous)) {
            sdfProbeGatherCarry(at, sdfViewsProbeHistory, previous);
            if (decoded.extra == 0u) {
                sdfProbeHistoryCarry(sdfViewsProbeHistory, sdfViewsProbeHistoryRW, sdfProbeHistoryEntry(decoded.cell, viewIndex), previous);
            }
        } else {
            uint slot;

            InterlockedAdd(sdfProbeMissCount, 1u, slot);
            sdfProbeMisses[slot] = at;
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // The gathers no entry served, a lane each.
    uint misses = sdfProbeMissCount;

    [loop] for (uint missed = groupLane; missed < misses; missed += SdfProbeGroupCells) {
        uint missedJob = sdfProbeMisses[missed];
        SdfProbeJob decoded = sdfProbeJobOf(sdfProbeJobs[missedJob], firstCell, viewIndex);
        float3 surfacePoint;
        float3 normal;
        uint packedNormal;
        float2 tolerance;
        bool complete;
        bool windowBound;

        sdfProbeReceiver(view, decoded.pixel, viewIndex, surfacePoint, normal, packedNormal, tolerance);

        SdfMeshProbes probes = sdfMeshProbesAtComplete(surfacePoint, normal, complete, windowBound);

        sdfProbeGatherKeep(missedJob, probes);
        if (keeps && (decoded.extra == 0u)) {
            // A gather no probe answers names no cage to keep; an incomplete one would stand past the probe that lands.
            uint hold = sdfProbeHistoryHoldOf((surfacePoint + (normal * SDF_MESH_IRRADIANCE_NORMAL_BIAS)), windowBound);

            sdfProbeHistoryStore(sdfViewsProbeHistoryRW, sdfProbeHistoryEntry(decoded.cell, viewIndex), (complete && (probes.share > 0.0)), surfacePoint, packedNormal, hold, probes);
        }
        sdfWorkTexels += 1u;
    }
    GroupMemoryBarrierWithGroupSync();

    // Every job's gather into its slot, a job a lane.
    [loop] for (uint resolved = groupLane; resolved < jobs; resolved += SdfProbeGroupCells) {
        sdfProbeStoreJob(sdfProbeJobOf(sdfProbeJobs[resolved], firstCell, viewIndex), sdfProbeGatherOf(resolved));
    }

    // The pass counts each gather it ran, never one it reused.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
