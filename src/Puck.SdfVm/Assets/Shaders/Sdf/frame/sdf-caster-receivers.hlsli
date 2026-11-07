// Receiver occupancy shares the selection kernel's scratch allocation with its depth pyramid.
// A cell covers 16x16 cascade texels. Higher levels OR their children; a positive
// answer is deliberately conservative. Only current camera samples populate it.

uint2 sdfReceiverDims() {
    return sdfOcclusionExtent(worldViewDims(worldView()), SDF_RECEIVER_SHIFT);
}
uint sdfReceiverBase() {
    uint levels = sdfMeshOcclusionCandidates[0].y;
    return sdfOcclusionWord(worldViewDims(worldView()), levels, uint2(0u, 0u)) + 1u;
}
uint sdfReceiverWord(uint level, uint2 texel) {
    uint2 dims = sdfReceiverDims();
    uint base = sdfReceiverBase();
    if (level != 0u) {
        base += dims.x * dims.y + sdfOcclusionWord(dims, level, uint2(0u, 0u));
    }
    return base + texel.y * sdfOcclusionExtent(dims, level).x + texel.x;
}
uint sdfReceiverTexel(uint level, uint2 texel) {
    return sdfMeshOcclusionDepthRW[sdfReceiverWord(level, texel)];
}

// These are the exact camera rows used for box selection, plus this sample's
// render grid and jitter. The pass block itself already holds the light camera.
ViewportData sdfReceiverView() {
    ViewportData view;
    view.position = asfloat(sdfMeshOcclusionCandidates[5]);
    view.right = asfloat(sdfMeshOcclusionCandidates[6]);
    view.up = asfloat(sdfMeshOcclusionCandidates[7]);
    view.forward = asfloat(sdfMeshOcclusionCandidates[8]);
    float4 sample = asfloat(sdfMeshOcclusionCandidates[SDF_RECEIVER_ROW]);
    view.extent = float4(sample.xy, 0.0, 0.0);
    float2 jitter = sample.zw / sample.xy * float2(2.0, -2.0);
    float2 offset = asfloat(sdfMeshOcclusionCandidates[9]).xy;
    view.lens = float4(view.forward.w, offset + jitter * float2(view.up.w, 1.0) * view.right.w, view.position.w);
    return view;
}

void sdfReceiverMark(uint index) {
    ViewportData camera = sdfReceiverView();
    uint2 dims = worldViewDims(camera);
    if (index >= dims.x * dims.y) { return; }
    uint2 pixel = uint2(index % dims.x, index / dims.x);
    float4 hit = meshVisibility.Load(int3(pixel, 0));
    if (hit.y < 1.0 || !(hit.x < worldFarDistance(camera))) { return; }
    float3 receiver = camera.position.xyz + cameraRayDirection(camera, (float2(pixel) + 0.5) / float2(dims)) * hit.x;
    ViewportData light = worldView();
    float3 relative = receiver - light.position.xyz;
    float2 plane = float2(dot(relative, light.right.xyz) / (light.right.w * light.up.w), dot(relative, light.up.xyz) / light.right.w);
    // Clamp rather than discard out-of-map receivers: normal offset and the
    // filter can bring an edge receiver's sample back inside the cascade.
    uint2 cells = sdfReceiverDims();
    float2 uv = float2(plane.x * 0.5 + 0.5, 0.5 - plane.y * 0.5);
    uint2 at = min(uint2(saturate(uv) * float2(cells)), cells - 1u);
    InterlockedOr(sdfMeshOcclusionDepthRW[sdfReceiverWord(0u, at)], 1u);
}

void sdfReceiverReduce(uint index, uint level) {
    uint2 dims = sdfReceiverDims();
    uint2 extent = sdfOcclusionExtent(dims, level);
    if (index >= extent.x * extent.y) { return; }
    uint2 below = sdfOcclusionExtent(dims, level - 1u);
    uint2 at = uint2(index % extent.x, index / extent.x);
    uint2 a = at << 1u;
    uint2 b = min(a + 1u, below - 1u);
    sdfMeshOcclusionDepthRW[sdfReceiverWord(level, at)] =
        sdfReceiverTexel(level - 1u, a) | sdfReceiverTexel(level - 1u, uint2(b.x, a.y)) |
        sdfReceiverTexel(level - 1u, uint2(a.x, b.y)) | sdfReceiverTexel(level - 1u, b);
}

// A caster can affect only receiver samples inside its projected box expanded
// by the complete filtering/normal-offset reach, supplied by SdfMeshCull.
// Main-camera visibility of the caster is never consulted here.
bool sdfReceiverMayShadow(float3 center, float3 x, float3 y, float3 z) {
    ViewportData light = worldView();
    float3 relative = center - light.position.xyz;
    float2 middle = float2(dot(relative, light.right.xyz), dot(relative, light.up.xyz));
    float reach = asfloat(sdfMeshOcclusionCandidates[9]).w;
    float2 radius = float2(sdfSelectionRadius(light.right.xyz, x, y, z), sdfSelectionRadius(light.up.xyz, x, y, z)) + reach;
    float2 scale = 0.5 / (light.right.w * float2(light.up.w, 1.0));
    float2 uv = float2(middle.x * scale.x + 0.5, 0.5 - middle.y * scale.y);
    uint2 dims = sdfReceiverDims();
    // Include neighbouring cells at arithmetic/raster edges; uncertainty keeps.
    int2 first = int2(floor((uv - radius * scale) * float2(dims))) - 1;
    int2 last = int2(floor((uv + radius * scale) * float2(dims))) + 1;
    uint2 a = uint2(clamp(first, int2(0, 0), int2(dims) - 1));
    uint2 b = uint2(clamp(last, int2(0, 0), int2(dims) - 1));
    uint level = 0u;
    [loop]
    while (any((b >> level) - (a >> level) > 1u)) { level++; }
    a >>= level;
    b >>= level;
    return (sdfReceiverTexel(level, a) | sdfReceiverTexel(level, uint2(b.x, a.y)) |
        sdfReceiverTexel(level, uint2(a.x, b.y)) | sdfReceiverTexel(level, b)) != 0u;
}
