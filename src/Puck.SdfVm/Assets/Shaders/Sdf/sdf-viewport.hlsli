// The viewport table every per-view pass reads: the world kernels through sdf-world.hlsli, and the mesh pass's vertex
// and fragment stages, which cannot include the kernels' groupshared state, directly. The includer's interface declares
// the table, viewports: sdf-world and sdf-mesh both do. KEEP IN SYNC with SdfWorldEngine.PackViewports.
#ifndef SDF_VIEWPORT_HLSLI
#define SDF_VIEWPORT_HLSLI

// The near plane every camera ray starts at: the beam's cone march and the mesh pass's reversed-Z projection, whose depth is
// ConeNear / d at forward distance d. KEEP IN SYNC with SdfWorldEngine.ConeNear, the near plane ViewProjection shares.
static const float ConeNear = 0.02;

// The viewport table — cameras + regions — as DATA: six float4 rows per view in the viewports buffer, read through
// worldViewport.
struct ViewportData {
    float4 position;    // xyz = world position, w = time (seconds)
    float4 right;       // xyz = right basis,   w = tan(fov / 2)
    float4 up;          // xyz = up basis,      w = aspect ratio
    float4 forward;     // xyz = forward basis, w = debug view mode (0 = final)
    // xy = the view's RENDER extent in pixels, which is the size of the output image its dispatch set writes: the host
    // sizes each view's image at the extent the render graph schedules for it, and the graph's place pass reconstructs
    // it into the view's rect. zw are zero.
    float4 extent;
    // x is zero. yz = the off-axis (asymmetric) frustum's tangent-space center offset (SdfAsymmetricFrustum) — (0,0)
    // for an ordinary symmetric camera, consumed by cameraRayDirection below. w = the frame's FAR DISTANCE
    // (SdfFrame.FarDistance, read through worldFarDistance below).
    // KEEP IN SYNC with SdfWorldEngine.PackViewports (the 96-byte row).
    float4 lens;
};
static const uint WorldViewportRows = 6u;
ViewportData worldViewport(uint view) {
    uint row = (view * WorldViewportRows);
    ViewportData data;
    data.position = viewports[row];
    data.right = viewports[(row + 1u)];
    data.up = viewports[(row + 2u)];
    data.forward = viewports[(row + 3u)];
    data.extent = viewports[(row + 4u)];
    data.lens = viewports[(row + 5u)];
    return data;
}

// The frame's FAR DISTANCE — the depth at which every camera march ends: the fine march's far exit (renderView), the
// beam's cone proofs (entry, the gap search, the F1 far bound) and the "nothing proven" sentinel every tile plane
// carries, and the depth/overshoot debug ramps. It is WORLD DATA (render.farDistance → SdfFrame.FarDistance, packed
// per view row by SdfWorldEngine.PackViewports — the one buffer every kernel that marches already binds), never a
// shader constant: the host refuses a non-finite or non-positive value before packing, so no kernel guards it.
float worldFarDistance(ViewportData view) {
    return view.lens.w;
}

// A view's render extent in pixels: its output image's size, packed by the host as exact integers. Every consumer (the
// sky, the tile passes' coverage, the hit passes and views) reads this one value, so none can disagree on it.
uint2 worldViewDims(ViewportData view) {
    return max((uint2)view.extent.xy, uint2(1u, 1u));
}

#endif
