// The probe lookup pass (SdfWorldPackage.Parts.ViewsProbes): one invocation a cell of SDF_VIEWS_PROBE_RATE by
// SDF_VIEWS_PROBE_RATE pixels of the render extent, in 8x8 groups. Each classifies its cell: the representative is the
// cell's first pixel in raster order whose hit shades with the volume (sdfViewsProbesWanted), and every other such pixel
// whose receiver is not compatible with the representative's (sdfViewsProbesCompatible) takes a gather of its own. The
// group then runs every gather its cells asked for as one list, a gather a lane, so a cell of foliage with four gathers
// costs the group four lanes rather than one lane four gathers while its neighbours wait; each gather is written into
// its cell's record (sdf-views-probes.hlsli), which views reads in place of gathering.
#define SDF_VIEWS_PROBES_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-probes.hlsli"
#include "sdf-views-rate.hlsli"

static const uint SdfProbeCellPixels = (SDF_VIEWS_PROBE_RATE * SDF_VIEWS_PROBE_RATE);
static const uint SdfProbeGroupCells = (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE);

// The group's gathers: each names its cell's lane in the group in bits 0 to 5, its pixel's place in the cell in bits 6 to
// 7, and, for a pixel other than the representative, one more than its extra slot in bits 8 to 9 (zero for the
// representative).
groupshared uint sdfProbeJobs[SdfProbeGroupCells * SdfProbeCellPixels];
groupshared uint sdfProbeJobCount;
// Each cell's state for its representative's record: the representative's place in bits 0 to 7, the extras mask in bits
// 8 to 15 and the coarse-shading rate above them.
groupshared uint sdfProbeCellState[SdfProbeGroupCells];

// The surface point and geometric normal the views stage shades a pixel's hit at, formed exactly as it forms them.
void sdfProbeReceiver(ViewportData view, uint2 pixel, uint viewIndex, out float3 surfacePoint, out float3 normal) {
    SdfPixel p = sdfPixelAt(view, pixel, viewIndex);
    uint record = worldVisibilityRecord(pixel, viewIndex);
    float t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);
    precise float3 rayTravel = (p.rayDirection * t);

    surfacePoint = (p.rayOrigin + rayTravel);
    normal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);
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

    if (groupLane == 0u) {
        sdfProbeJobCount = 0u;
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

            sdfProbeReceiver(view, pixel, viewIndex, surfacePoint, normal);
            if (!present) {
                lane = candidate;
                present = true;
                representativePoint = surfacePoint;
                representativeNormal = normal;
            } else if (!sdfViewsProbesCompatible((surfacePoint - representativePoint), representativeNormal, normal)) {
                extras |= (1u << sdfViewsProbeExtraSlot(candidate, lane));
            }
        }
#endif

        if (!present) {
            sdfViewsProbesStore(sdfViewsProbeRecord(id.xy, viewIndex), sdfMeshProbesNone(), 0u, false, rate, 0u);
        } else {
            uint first;

            InterlockedAdd(sdfProbeJobCount, (1u + countbits(extras)), first);
            sdfProbeCellState[groupLane] = (lane | (extras << 8u) | (rate << 16u));
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

    // The group's gathers, a lane each, striding by the group's lanes.
    uint jobs = sdfProbeJobCount;
    uint2 firstCell = (groupId.xy * SDF_VISIBILITY_GROUP_EDGE);

    [loop] for (uint at = groupLane; at < jobs; at += SdfProbeGroupCells) {
        uint job = sdfProbeJobs[at];
        uint cellLane = (job & 63u);
        uint place = ((job >> 6u) & 3u);
        uint extra = ((job >> 8u) & 3u);
        uint2 cell = (firstCell + uint2((cellLane % SDF_VISIBILITY_GROUP_EDGE), (cellLane / SDF_VISIBILITY_GROUP_EDGE)));
        uint2 pixel = ((cell * SDF_VIEWS_PROBE_RATE) + uint2((place % SDF_VIEWS_PROBE_RATE), (place / SDF_VIEWS_PROBE_RATE)));
        uint record = sdfViewsProbeRecord(cell, viewIndex);
        float3 surfacePoint;
        float3 normal;

        sdfProbeReceiver(view, pixel, viewIndex, surfacePoint, normal);

        SdfMeshProbes probes = sdfMeshProbesAt(surfacePoint, normal);

        if (extra == 0u) {
            uint state = sdfProbeCellState[cellLane];

            sdfViewsProbesStore(record, probes, (state & 0xFFu), true, (state >> 16u), ((state >> 8u) & 0xFFu));
        } else {
            sdfViewsProbesStoreExtra(record, (extra - 1u), probes);
        }
        sdfWorkTexels += 1u;
    }

    // The pass counts each gather it ran.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
