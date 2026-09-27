// The view every per-view pass renders, read from the pass block: the world kernels through sdf-world.hlsli, and the
// mesh pass's vertex and fragment stages, which cannot include the kernels' groupshared state, directly. The includer's
// interface declares the block, passGroup: sdf-world and sdf-mesh both do, member for member at the same offsets, and the
// host writes it through SdfFrameBlock.
#ifndef SDF_VIEWPORT_HLSLI
#define SDF_VIEWPORT_HLSLI

// The mesh projection's near plane at forward distance ConeNear. The beam starts conservatively at ray distance ConeNear;
// renderView raises each primary ray's start to the plane. KEEP IN SYNC with SdfWorldTables.ConeNear and ViewProjection.
static const float ConeNear = 0.02;

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
    // x is zero. yz = the off-axis (asymmetric) frustum's tangent-space center offset (SdfAsymmetricFrustum) — (0,0)
    // for an ordinary symmetric camera, consumed by march/sdf-cone.hlsli's cameraRayDirection. w = the frame's FAR DISTANCE
    // (read through worldFarDistance below).
    float4 lens;
};
ViewportData worldView() {
    ViewportData data;
    data.position = float4(passGroup.viewPosition, passGroup.sceneTime);
    data.right = float4(passGroup.viewRight, passGroup.tanHalfFieldOfView);
    data.up = float4(passGroup.viewUp, passGroup.aspectRatio);
    data.forward = float4(passGroup.viewForward, (float)passGroup.debugMode);
    data.extent = float4((float2)passGroup.imageExtent, 0.0, 0.0);
    data.lens = float4(0.0, passGroup.frustumOffset, passGroup.farDistance);
    return data;
}

// The frame's FAR DISTANCE — the depth at which every camera march ends: the fine march's far exit (renderView), the
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
