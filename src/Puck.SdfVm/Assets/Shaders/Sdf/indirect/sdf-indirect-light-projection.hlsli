#ifndef SDF_INDIRECT_LIGHT_PROJECTION_HLSLI
#define SDF_INDIRECT_LIGHT_PROJECTION_HLSLI
#include "../isa/sdf-indirect-layout.hlsli"

// Shared by the production bank lookup and its device proof. Up.w is near axial depth, toward.w is far ray distance,
// and right.w is the orthographic half-width; image Y increases down. No resource or validity policy lives in this math.
bool sdfIndirectLightProject(float3 surfacePoint, float3 origin, float4 right, float4 up, float4 toward, uint resolution,
    out uint2 pixel, out float travel, out float3 ray) {
    pixel = 0u;
    travel = 0.0;
    ray = 0.0;
    if (resolution == 0u || resolution > SdfIndirectLightResolution || !isfinite(right.w) || right.w <= 0.0 || any(!isfinite(surfacePoint)) ||
        any(!isfinite(origin)) || any(!isfinite(right.xyz)) || any(!isfinite(up)) || any(!isfinite(toward))) { return false; }
    float3 offset = surfacePoint - origin;
    float axial = -dot(offset, toward.xyz);
    if (axial < up.w || !isfinite(axial)) { return false; }
    float2 plane = float2(dot(offset, right.xyz), dot(offset, up.xyz)) / right.w;
    if (any(!isfinite(plane)) || any(abs(plane) >= 1.0)) { return false; }
    // Rounding a point just inside the edge can produce exactly resolution. Bound the conversion as well as the plane.
    pixel = min((uint2)((float2(plane.x, -plane.y) + 1.0) * (0.5 * resolution)), resolution - 1u);
    ray = -toward.xyz;
    travel = axial;
    return travel <= toward.w;
}

float sdfIndirectLightCompare(float travel, float depth, float radius, float3 normal, float3 ray) {
    float cosine = dot(normal, -ray);
    if (cosine <= 0.0) { return 0.0; }
    float bias = radius * (1.0 + sqrt(max(0.0, 1.0 - cosine * cosine))) / cosine + 0.002;
    return travel <= depth + bias ? 1.0 : 0.0;
}
#endif
