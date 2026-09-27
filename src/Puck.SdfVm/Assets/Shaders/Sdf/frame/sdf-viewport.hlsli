// The view every per-view pass renders, read from the pass block: the world kernels through sdf-world.hlsli, and the
// mesh pass's vertex and fragment stages, which cannot include the kernels' groupshared state, directly. The includer's
// interface declares the block, passGroup: sdf-world and sdf-mesh both do, member for member at the same offsets, and the
// host writes it through SdfFrameBlock.
#ifndef SDF_VIEWPORT_HLSLI
#define SDF_VIEWPORT_HLSLI
#include "../isa/sdf-isa.hlsli"

// The view the pass renders, gathered from the pass block into the rows the march, the shading and the mesh projection
// read it through.
struct ViewportData {
    float4 position;    // xyz = world position, w = the frame's presentation time in seconds (sceneTime)
    float4 right;       // xyz = right basis,   w = tan(fov / 2)
    float4 up;          // xyz = up basis,      w = aspect ratio
    float4 forward;     // xyz = forward basis, w = debug view mode (0 = final)
    // xy = the view's RENDER extent in pixels, which is the size of the output image its dispatch set writes: the host
    // sizes each view's image at the extent the render graph schedules for it, and the graph's place pass reconstructs
    // it into the view's rect. zw are zero.
    float4 extent;
    // x = the view's near distance (read through worldNearDistance below). yz = the off-axis (asymmetric) frustum's
    // tangent-space center offset (SdfAsymmetricFrustum) — (0,0) for an ordinary symmetric camera, consumed by
    // march/sdf-cone.hlsli's cameraRayDirection. w = the frame's FAR DISTANCE (read through worldFarDistance below).
    float4 lens;
};
ViewportData worldView() {
    ViewportData data;
    data.position = float4(passGroup.viewPosition, passGroup.sceneTime);
    data.right = float4(passGroup.viewRight, passGroup.tanHalfFieldOfView);
    data.up = float4(passGroup.viewUp, passGroup.aspectRatio);
    data.forward = float4(passGroup.viewForward, (float)passGroup.debugMode);
    data.extent = float4((float2)passGroup.imageExtent, 0.0, 0.0);
    data.lens = float4(passGroup.nearDistance, passGroup.frustumOffset, passGroup.farDistance);
    return data;
}

// The view camera's own near distance (CameraSnapshot.Near): the forward distance of the plane its image begins on,
// zero for a camera whose image begins at its eye. A border window's plane is its aperture, so nothing between its eye
// and the glass is seen. The bounded volumes composite from it. The host writes a finite, non-negative value.
float worldNearDistance(ViewportData view) {
    return view.lens.x;
}

// The forward distance the view's surfaces are rendered from (SdfFrameBlock.NearOf): the near plane, never nearer than
// SDF_MINIMUM_NEAR, which the mesh pass's reversed-Z depth needs. The beam's cone entry begins at that ray distance, a
// conservative start since no ray of the cone meets the plane nearer, sdfPixelAt raises each primary ray's start to the
// plane itself, and the mesh pass clips there.
float worldSurfaceNearDistance(ViewportData view) {
    return max(worldNearDistance(view), SDF_MINIMUM_NEAR);
}

// The ray distance at which a camera ray along the unit `rayDirection` crosses the plane at forward distance
// `forwardDistance`.
float worldRayDistanceAt(ViewportData view, float3 rayDirection, float forwardDistance) {
    return (forwardDistance / dot(rayDirection, view.forward.xyz));
}

// The frame's FAR DISTANCE — the depth at which every camera march ends: the fine march's far exit (the primary stage), the
// beam's cone proofs (entry, the gap search, the F1 far bound) and the "nothing proven" sentinel every tile plane
// carries, and the depth/overshoot debug ramps. It is WORLD DATA (render.farDistance → SdfFrame.FarDistance), never a
// shader constant: the host refuses a non-finite or non-positive value before writing it, so no kernel guards it.
float worldFarDistance(ViewportData view) {
    return view.lens.w;
}

// A view's render extent in pixels: its output image's size, written by the host as exact integers. Every consumer (the
// sky, the tile passes' coverage, the hit passes and views) reads this one value, so none can disagree on it.
uint2 worldViewDims(ViewportData view) {
    return max((uint2)view.extent.xy, uint2(1u, 1u));
}

#endif
