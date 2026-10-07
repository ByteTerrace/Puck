// Publishes one aligned rectangle; every other texel retains its submitted depth.
#include "../isa/sdf-world.interface.hlsli"
#include "../frame/sdf-visibility.hlsli"
#include "../isa/sdf-indirect-layout.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint3 slice = sdfIndirectLightSlice(passGroup.lightSlice);
    uint2 columns = sdfIndirectLightColumns(passGroup.lightSlice);
    if (slice.x == 0u || id.y >= slice.z * SdfIndirectLightSliceRowEdge) { return; }
    if (id.x >= columns.y * SdfIndirectLightSliceRowEdge) { return; }
    uint2 pixel = uint2(id.x + columns.x * SdfIndirectLightSliceRowEdge, id.y + slice.y * SdfIndirectLightSliceRowEdge);
    if (any(pixel >= SdfIndirectLightResolution)) { return; }
    uint depthCount, depthStride;
    indirectLightDepthRW.GetDimensions(depthCount, depthStride);
    uint address = (slice.x - 1u) * SdfIndirectLightResolution * SdfIndirectLightResolution +
        pixel.y * SdfIndirectLightResolution + pixel.x;
    if (address >= depthCount) { return; }
    float depth = asfloat(0x7f800000u);
    uint boundsCount, boundsStride, visibilityCount, visibilityStride;
    cullBounds.GetDimensions(boundsCount, boundsStride);
    sdfVisibilityRecordBuffer.GetDimensions(visibilityCount, visibilityStride);
    bool valid = boundsCount >= 4u && all(passGroup.imageExtent == SdfIndirectLightResolution);
    uint record = 0u;
    if (valid) {
        record = sdfVisibilityRecord(pixel, 0u, passGroup.imageExtent);
        valid = record <= visibilityCount && SdfVisibilityWords <= visibilityCount - record;
    }
    if (!valid) {
        depth = asfloat(0x7fc00000u);
    } else if (SDF_VISIBILITY_CURRENT(pixel, cullBounds)) {
        SdfVisibility visibility = sdfLoadVisibility(record);
        if (visibility.identity != 0u || isnan(visibility.t)) { depth = visibility.t; }
    }
    indirectLightDepthRW[address] = depth;
    puckCountWork(0u, 1u);
}
