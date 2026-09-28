// Temporal reconstruction uses unjittered output coordinates. Motion comes from the nearest current surface,
// while the history lookup starts at this output pixel, retaining its offset from that surface's render-grid tap.
#ifndef SDF_TEMPORAL_RESOLVE_HLSLI
#define SDF_TEMPORAL_RESOLVE_HLSLI
#include "sdf-viewport.hlsli"
#include "sdf-reprojection.hlsli"
#include "sdf-history.hlsli"

bool sdfHistoryPosition(uint2 pixel, uint2 nearestPixel, uint2 surface, out float2 previousUv, out float previousT) {
    float2 outputUv = (float2(pixel) + 0.5) / float2(passGroup.extent);
    float2 previousPixel;
    previousUv = 0.0;
    previousT = 0.0;
    ViewportData view = worldView();
    if (surface.y == 0u) {
        float3 direction = cameraRayDirection(view, outputUv - passGroup.jitter / float2(passGroup.imageExtent));
        if (!sdfProjectPrevious(direction, 0.0, previousPixel, previousT)) return false;
        previousUv = (previousPixel + passGroup.previousView[4].zw) / passGroup.previousView[4].xy;
        previousT = passGroup.farDistance;
    } else {
        float2 sourceUv = (float2(nearestPixel) + 0.5) / float2(passGroup.imageExtent);
        float3 direction = cameraRayDirection(view, sourceUv);
        float3 surfacePoint = view.position.xyz + direction * asfloat(surface.x);
        uint record = sdfVisibilityRecord(nearestPixel, 0u, passGroup.imageExtent);
        if (!sdfReprojection(record, surfacePoint, previousPixel, previousT)) return false;
        float2 previousSurfaceUv = (previousPixel + passGroup.previousView[4].zw) / passGroup.previousView[4].xy;
        float2 currentSurfaceUv = sourceUv + passGroup.jitter / float2(passGroup.imageExtent);
        previousUv = outputUv + previousSurfaceUv - currentSurfaceUv;
    }
    return all(previousUv >= 0.0) && all(previousUv < 1.0);
}
float4 sdfRectifyHistory(float4 history, float2 position) {
    int2 center = (int2)floor(position + 0.5);
    float4 lower = 3.402823e+38;
    float4 upper = -3.402823e+38;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            int2 tap = clamp(center + int2(x, y), int2(0, 0), int2(passGroup.imageExtent) - 1);
            float4 color = currentColor.Load(int3(tap, 0));
            lower = min(lower, color);
            upper = max(upper, color);
        }
    }
    return clamp(history, lower, upper);
}
float sdfReactivityAt(float2 position) {
    int2 origin = (int2)floor(position);
    float reactive = 0.0;
    // A single reactive source tap rejects the filtered footprint. Outside this frame's cull box the mask is unwritten.
    int first = passGroup.upscaleSharpness > 0.0 ? -1 : 0;
    int last = passGroup.upscaleSharpness > 0.0 ? 2 : 1;
    for (int y = first; y <= last; y++) {
        for (int x = first; x <= last; x++) {
            uint2 tap = (uint2)clamp(origin + int2(x, y), int2(0, 0), int2(passGroup.imageExtent) - 1);
            if (worldVisibilityCurrent(tap)) reactive = max(reactive, reactivity.Load(int3(tap, 0)));
        }
    }
    return saturate(reactive);
}
float4 sdfTemporalResolve(uint2 pixel, float2 position, uint2 nearestPixel, uint2 surface, float4 current) {
    // The sequence's first sample is the pixel center and reads no history, including after a cut or follow.
    if (passGroup.historyFrames == 0u || passGroup.debugMode != 0u) return current;
    float2 previousUv;
    float expectedDistance;
    if (!sdfHistoryPosition(pixel, nearestPixel, surface, previousUv, expectedDistance)) return current;
    float reactive = sdfReactivityAt(position);
    if (reactive >= 1.0) return current;
    float2 previousPosition = previousUv * float2(passGroup.extent) - 0.5;
    int2 origin = (int2)floor(previousPosition);
    float2 fraction = frac(previousPosition);
    float4 history = 0.0;
    float weight = 0.0;
    // Reject each bilinear tap independently so a neighboring identity cannot bleed through a valid center.
    for (int y = 0; y <= 1; y++) {
        for (int x = 0; x <= 1; x++) {
            int2 tap = origin + int2(x, y);
            if (any(tap < 0) || any(tap >= int2(passGroup.extent))) continue;
            uint2 oldSurface = historySurface[(uint)tap.y * passGroup.extent.x + (uint)tap.x];
            if (!sdfHistoryAccept(oldSurface, surface.y, expectedDistance)) continue;
            float contribution = (x == 0 ? 1.0 - fraction.x : fraction.x) * (y == 0 ? 1.0 - fraction.y : fraction.y);
            history += historyColor.Load(int3(tap, 0)) * contribution;
            weight += contribution;
        }
    }
    if (weight <= 0.0) return current;
    history = sdfRectifyHistory(history / weight, position);
    float historyWeight = min(float(passGroup.historyFrames) / (float(passGroup.historyFrames) + 1.0), 0.875);
    return lerp(current, history, historyWeight * weight * (1.0 - reactive));
}
#endif
