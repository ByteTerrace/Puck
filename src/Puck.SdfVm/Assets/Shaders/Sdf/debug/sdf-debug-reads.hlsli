// The debug views' field reads: the slice's plane and the reads the views stage's field loop
// (passes/sdf-views-field.hlsli) takes for the debug view, ahead of sdfDebugView.
#ifndef DEBUG_SDF_DEBUG_READS_HLSLI
#define DEBUG_SDF_DEBUG_READS_HLSLI
#ifdef SDF_VIEWS_PASS
#include "sdf-overshoot.hlsli"

// The slice's plane (case 7). Default plane: through the WORLD ORIGIN with normal = camera forward (the debug subject
// sits at the origin — a camera-locked slice). The pass block's slice axis and offset optionally select a world-axis
// plane instead (the `sdf.slice` verb; camera-locked while the axis is 0).
struct SdfDebugSlice {
    bool slicing;
    bool parallel;
    float planeT;
};

SdfDebugSlice sdfDebugSliceOf(SdfPixel p) {
    SdfDebugSlice slice;
    slice.slicing = (p.viewMode == 7);
    slice.parallel = false;
    slice.planeT = 0.0;

    if (slice.slicing) {
        float3 sliceNormal = p.view.forward.xyz; // already unit (the camera basis)
        float planeOffset = 0.0;               // the plane is dot(p, n) = planeOffset
        int sliceAxis = (int)round(passGroup.debugSliceAxis);

        if (sliceAxis == 1) { sliceNormal = float3(1.0, 0.0, 0.0); planeOffset = passGroup.debugSliceOffset; }
        else if (sliceAxis == 2) { sliceNormal = float3(0.0, 1.0, 0.0); planeOffset = passGroup.debugSliceOffset; }
        else if (sliceAxis == 3) { sliceNormal = float3(0.0, 0.0, 1.0); planeOffset = passGroup.debugSliceOffset; }

        float denominator = dot(p.rayDirection, sliceNormal);

        slice.parallel = (abs(denominator) < 1.0e-4);

        if (!slice.parallel) {
            slice.planeT = ((planeOffset - dot(p.rayOrigin, sliceNormal)) / denominator);
        }
    }

    return slice;
}

// The debug views' field reads: the slice's one sample of the UNMASKED field at its plane (case 7), and the overshoot
// detector's two marches, clamped then unclamped (case 9). sdfViewsFieldReads runs them and hands their depths to
// sdfDebugView as its first and second read.
uint sdfDebugReadCount(SdfPixel p, SdfDebugSlice slice) {
    return ((p.viewMode == 9) ? 2u : ((slice.slicing && !slice.parallel && !(slice.planeT < 0.0)) ? 1u : 0u));
}
SdfOvershootMarch sdfDebugReadBegin(SdfPixel p, SdfDebugSlice slice, uint read) {
    return sdfOvershootBegin(p.rayOrigin, p.rayDirection, (slice.slicing ? slice.planeT : p.marchStart), p.firstExit, p.secondEntry, p.farDistance, (slice.slicing ? SDF_INSTANCE_MASK_ALL : p.instanceMaskBase), p.pixelFootprint, ((read == 0u) ? 1.0 : (1.0 / sdfStepScale())), slice.slicing);
}
#endif
#endif
