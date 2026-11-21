// The probe lookup pass (SdfWorldPackage.Parts.ViewsProbes): one invocation a cell of SDF_VIEWS_PROBE_RATE by
// SDF_VIEWS_PROBE_RATE pixels of the render extent, in 8x8 groups. Each gathers the irradiance volume's probes once, at the
// cell's first pixel in raster order whose hit shades with the volume (sdfViewsProbesWanted), and writes the gather and
// that pixel into the cell's probe record, which views reads for the cell's compatible pixels (sdf-views-probes.hlsli).
#define SDF_VIEWS_PROBES_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-probes.hlsli"
#include "sdf-views-rate.hlsli"

[numthreads(SDF_VISIBILITY_GROUP_EDGE, SDF_VISIBILITY_GROUP_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint viewIndex = worldViewOf(id.z);

    if (viewIndex >= passGroup.viewportCount) {
        return;
    }

    ViewportData view = worldView();
    uint2 dims = worldViewDims(view);
    uint2 origin = (id.xy * SDF_VIEWS_PROBE_RATE);

    uint rate = sdfRateTile(view, viewIndex, id.xy, origin);
    if (all(origin < dims) && all(id.xy < sdfViewsProbeCells())) {
        int viewMode = (int)round(view.forward.w);
        uint lane = 0u;
        bool present = false;

#if !defined(SDF_VIEWS_OMIT_PROBES)
        [loop] for (uint candidate = 0u; (candidate < (SDF_VIEWS_PROBE_RATE * SDF_VIEWS_PROBE_RATE)) && !present; candidate++) {
            uint2 pixel = (origin + uint2((candidate % SDF_VIEWS_PROBE_RATE), (candidate / SDF_VIEWS_PROBE_RATE)));

            if (any(pixel >= dims)) {
                continue;
            }

            SdfVisibility visibility = sdfLoadVisibility(worldVisibilityRecord(pixel, viewIndex));

            if (sdfVisibilityHit(visibility) && sdfViewsProbesWanted(viewMode, visibility.material)) {
                lane = candidate;
                present = true;
            }
        }
#endif

        SdfMeshProbes probes = sdfMeshProbesNone();

        if (present) {
            uint2 pixel = (origin + uint2((lane % SDF_VIEWS_PROBE_RATE), (lane / SDF_VIEWS_PROBE_RATE)));
            SdfPixel p = sdfPixelAt(view, pixel, viewIndex);
            uint record = worldVisibilityRecord(pixel, viewIndex);
            float t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);
            float3 normal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);
            // The surface point exactly as views forms it (sdfViewsStage), so the representative reads its own gather.
            precise float3 rayTravel = (p.rayDirection * t);
            precise float3 surfacePoint = (p.rayOrigin + rayTravel);

            probes = sdfMeshProbesAt(surfacePoint, normal);
            sdfWorkTexels = 1u;
        }
        sdfViewsProbesStore(sdfViewsProbeRecord(id.xy, viewIndex), probes, lane, present, rate);
    }

    // The pass counts a cell whose gather it wrote.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
