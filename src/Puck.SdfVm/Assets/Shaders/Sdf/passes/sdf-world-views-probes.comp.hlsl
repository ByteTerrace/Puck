// The probe lookup pass (SdfWorldPackage.Parts.ViewsProbes): one invocation a cell of SDF_VIEWS_PROBE_RATE by
// SDF_VIEWS_PROBE_RATE pixels of the render extent, in 8x8 groups. Each classifies its cell: the representative is the
// cell's first pixel in raster order whose hit shades with the volume (sdfViewsProbesWanted), and every other such pixel
// whose receiver is not compatible with the representative's (sdfViewsProbesCompatible) takes a gather of its own.
// The group then runs every gather its cells asked for as one list, a gather a lane: the view first reuses its
// preceding render's gather where one stands for the receiver (sdf-probe-history.hlsli), and the gathers no entry
// serves run as a second list, so a cell of foliage with four gathers costs the group four lanes rather than one lane
// four gathers while its neighbours wait. Either way a job's gather lands in group memory, and one last list resolves
// every job's into a slot (sdf-views-probes.hlsli), a job a lane: the representative's in its cell's record and every
// other in the pool, which views reads in place of gathering. The kernel therefore holds the resolve once, and a lane
// that reused a gather resolves beside one that gathered. Each representative's gather is written into the history for
// the next render.
#define SDF_VIEWS_PROBES_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-probes.hlsli"
#include "sdf-probe-history.hlsli"
#include "sdf-views-rate.hlsli"

static const uint SdfProbeCellPixels = (SDF_VIEWS_PROBE_RATE * SDF_VIEWS_PROBE_RATE);
static const uint SdfProbeGroupCells = (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE);

// The group's gathers: each names its cell's lane in the group in bits 0 to 5, its pixel's place in the cell in bits 6 to
// 7, and, for a pixel other than the representative, one more than its extra slot in bits 8 to 9 (zero for the
// representative).
groupshared uint sdfProbeJobs[SdfProbeGroupCells * SdfProbeCellPixels];
groupshared uint sdfProbeJobCount;
// The gathers no preceding entry served, each its place in sdfProbeJobs.
groupshared uint sdfProbeMisses[SdfProbeGroupCells * SdfProbeCellPixels];
groupshared uint sdfProbeMissCount;
// Each job's gather, as a history entry holds one (sdf-probe-history.hlsli): its two cages, its sixteen UNORM16 weights
// in eight words, then its share and measured flags (sdfViewsProbesFlags).
static const uint SdfProbeGatherWords = 11u;
groupshared uint sdfProbeGathers[(SdfProbeGroupCells * SdfProbeCellPixels) * SdfProbeGatherWords];
// Each cell's state for its representative's record: the representative's place in bits 0 to 7, the extras mask in bits
// 8 to 15 and the coarse-shading rate above them.
groupshared uint sdfProbeCellState[SdfProbeGroupCells];
// The group's extras: how many its cells hold, each cell's first among them, and the pool slot of the group's first, or
// SdfProbeNoExtras for a group the pool has no room for, whose cells then keep none.
groupshared uint sdfProbeExtraCount;
groupshared uint sdfProbeCellExtras[SdfProbeGroupCells];
groupshared uint sdfProbeExtraBase;
static const uint SdfProbeNoExtras = 0xFFFFFFFFu;

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
    uint place = ((job >> 6u) & 3u);

    decoded.cellLane = (job & 63u);
    decoded.extra = ((job >> 8u) & 3u);
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
        sdfViewsProbesStoreSlot(sdfViewsProbePoolSlot(sdfViewsProbeExtraEntry(first, extras, (job.extra - 1u))), probes, 0u);
    }
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

    GroupMemoryBarrierWithGroupSync();
    if (all(origin < dims) && all(id.xy < sdfViewsProbeCells())) {
        int viewMode = (int)round(view.forward.w);
        uint lane = 0u;
        bool present = false;
        uint extras = 0u;

#if !defined(SDF_VIEWS_OMIT_PROBES)
        float3 representativePoint = float3(0.0, 0.0, 0.0);
        float3 representativeNormal = float3(0.0, 0.0, 0.0);
        float2 representativeTolerance = float2(0.0, 0.0);

        [loop] for (uint candidate = 0u; candidate < SdfProbeCellPixels; candidate++) {
            uint2 pixel = (origin + uint2((candidate % SDF_VIEWS_PROBE_RATE), (candidate / SDF_VIEWS_PROBE_RATE)));

            if (any(pixel >= dims)) {
                continue;
            }

            SdfVisibility visibility = sdfLoadVisibility(worldVisibilityRecord(pixel, viewIndex));

            if (!sdfVisibilityHit(visibility) || !sdfViewsProbesWanted(viewMode, visibility.material)) {
                continue;
            }

            float3 surfacePoint;
            float3 normal;
            uint packedNormal;
            float2 tolerance;

            sdfProbeReceiver(view, pixel, viewIndex, surfacePoint, normal, packedNormal, tolerance);
            if (!present) {
                lane = candidate;
                present = true;
                representativePoint = surfacePoint;
                representativeNormal = normal;
                representativeTolerance = tolerance;
            } else if (!sdfViewsProbesCompatible((surfacePoint - representativePoint), representativeNormal, normal, representativeTolerance)) {
                extras |= (1u << sdfViewsProbeExtraSlot(candidate, lane));
            }
        }
#endif

        if (!present) {
            sdfViewsProbesStoreEmpty(sdfViewsProbeRecord(id.xy, viewIndex), rate);
            if (keeps) {
                sdfProbeHistoryStore(sdfViewsProbeHistoryRW, sdfProbeHistoryEntry(id.xy, viewIndex), false, float3(0.0, 0.0, 0.0), 0u, 0u, sdfMeshProbesNone());
            }
        } else {
            uint first;

            InterlockedAdd(sdfProbeJobCount, (1u + countbits(extras)), first);
            sdfProbeCellState[groupLane] = (lane | (extras << 8u) | (rate << 16u));
            sdfProbeCellExtras[groupLane] = 0u;
            if (extras != 0u) {
                uint firstExtra;

                InterlockedAdd(sdfProbeExtraCount, countbits(extras), firstExtra);
                sdfProbeCellExtras[groupLane] = firstExtra;
            }
            sdfProbeJobs[first] = (groupLane | (lane << 6u));
            [loop] for (uint candidate = 0u, job = (first + 1u); candidate < SdfProbeCellPixels; candidate++) {
                if ((candidate != lane) && (((extras >> sdfViewsProbeExtraSlot(candidate, lane)) & 1u) != 0u)) {
                    sdfProbeJobs[job] = (groupLane | (candidate << 6u) | ((sdfViewsProbeExtraSlot(candidate, lane) + 1u) << 8u));
                    job++;
                }
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
    uint2 firstCell = (groupId.xy * SDF_VISIBILITY_GROUP_EDGE);

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
