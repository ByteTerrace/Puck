// A screen slot's world-space face (screenSurfaces): the frame a sampled screen maps hits through and the face an emitter
// face (SdfScreenSurface.IsEmitter) lights from, read by the shading passes and the screens' emission reduction alike.
#ifndef FRAME_SDF_SCREEN_SURFACE_HLSLI
#define FRAME_SDF_SCREEN_SURFACE_HLSLI
// A resident screen's world-space face (Puck.SignedDistance.SdfScreenSurface), indexed by the slot the screen holds
// (sdfScreenSlot, below SDF_MAX_SCREEN_SURFACES); the host writes the row as the screen claims the slot
// (SdfWorldTables.WriteDeclaredSurface). KEEP IN SYNC with SdfWorldTables.WriteSurfaceRow.
struct ScreenSurfaceData {
    float4 right;   // xyz = unit world-space U axis, w = half-width
    float4 up;      // xyz = unit world-space V axis (V=0 at top), w = half-height
    float4 origin;  // xyz = world-space front-face center, w = the range its light reaches (SdfWorldTables' screen reaches)
};
static const uint WorldScreenSurfaceRows = 3u;
ScreenSurfaceData worldScreenSurface(uint screenIndex) {
    uint row = (screenIndex * WorldScreenSurfaceRows);
    ScreenSurfaceData data;
    data.right = screenSurfaces[row];
    data.up = screenSurfaces[(row + 1u)];
    data.origin = screenSurfaces[(row + 2u)];
    return data;
}
#endif
