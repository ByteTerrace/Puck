// The frame's environment rows and screen tables: the environment row reader, the screen surfaces' frames, the mapping
// each screen is drawn from, which screen slots hold a source this frame and which screen hits shade as a screen.
#ifndef FRAME_SDF_ENVIRONMENT_HLSLI
#define FRAME_SDF_ENVIRONMENT_HLSLI
// The environment: SdfEnvironment's lanes, row for row, in the pass block's environment array, with the host bakes (unit
// directions, the sun-disc exponent, the twinkle phase, the integrated cloud offsets and spin). The generated
// SDF_ENV_*_ROW indices place each row, and frame/sdf-lights.hlsli decodes the lanes of each as SdfEnvironment lays them
// out.
float4 worldEnvRow(uint row) { return passGroup.environment[row]; }

#ifdef SDF_SCREEN_SOURCES
// A declared ScreenSlab instance's world-space front-face frame (see Puck.SignedDistance.SdfScreenSurface) — Stage 1 ONLY
// (binding 10/11 are not part of the beam prepass's descriptor set). Indexed DIRECTLY by screen index
// (0..31, the same slot SetScreenSource/screenSources binds) — not by declaration order — so a hit resolves its
// surface with no search; an unfilled slot's entry is never read (no material id can address it: the host packs an
// entry only when SdfProgramBuilder registers that screen index).
struct ScreenSurfaceData {
    float4 right;   // xyz = unit world-space U axis, w = half-width
    float4 up;      // xyz = unit world-space V axis (V=0 at top), w = half-height
    float4 origin;  // xyz = world-space front-face center, w = unused (pad)
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
// The mapping a screen is drawn from (SdfWorldTables.SetScreenMapping, the draw form of the mapping its row publishes,
// Puck.Commands.SourceDraw), indexed by screen index like ScreenSurfaceData. A face point (u, v), v = 0 at the top, runs
// the warp's rows to the face point the glass sampled; outside the unit square it lies on the bezel. That point runs the
// image rows to the source, normalized to its extent; outside the half-open crop, [left, right) x [top, bottom), it lies
// on a letterbox bar when imageV.w is set.
// Every sample is clamped to sampleClamp, the crop inset by half a source pixel, and reads through the samplers element
// state.y names (an SDF_FILTER_* value). state.x is set while a source is bound this frame. An unmapped screen's rows
// before state are zero.
struct ScreenMappingData {
    float4 warpU;       // xyz = the warped u's coefficients of u, v and 1; w = the face distance one warped unit spans
    float4 warpV;       // xyz = the warped v's coefficients of u, v and 1; w = the face distance one warped unit spans
    float4 imageU;      // xyz = the source u's coefficients of the warped u, v and 1; w = 1 when the screen is mapped
    float4 imageV;      // xyz = the source v's coefficients of the warped u, v and 1; w = 1 when the fit letterboxes
    float4 crop;        // the crop: left, top, right, bottom
    float4 sampleClamp; // the crop inset by half a source pixel: left, top, right, bottom
    float4 state;       // x = 1 while a source is bound this frame, y = the sampler (an SDF_FILTER_* value), zw = 0
};
static const uint WorldScreenMappingRows = 7u;
ScreenMappingData worldScreenMapping(uint screenIndex) {
    uint row = (screenIndex * WorldScreenMappingRows);
    ScreenMappingData data;
    data.warpU = screenMappings[row];
    data.warpV = screenMappings[(row + 1u)];
    data.imageU = screenMappings[(row + 2u)];
    data.imageV = screenMappings[(row + 3u)];
    data.crop = screenMappings[(row + 4u)];
    data.sampleClamp = screenMappings[(row + 5u)];
    data.state = screenMappings[(row + 6u)];
    return data;
}
// The screen source images: screenSources, one sampled image per screen index, each read through the samplers element
// its row names. A screen with no source bound this frame holds a valid filler view; the shader never samples an unbound
// screen (screenSourceBound gates it), so the filler's content never reaches the image. Every screen index is bounded
// by the generated SDF_MAX_SCREEN_SURFACES before it indexes a per-screen table.
// Per-frame screen LIGHT records (sdfScreenLights): entry i carries screen i's emitted light (rgb = the framebuffer's
// average color this frame, a = intensity gain). A light's geometry (position/orientation/extent) is the SAME
// screenSurfaces[i] entry above — a screen is an area emitter, so it needs only its color here. KEEP IN SYNC with
// SdfWorldTables.PackScreenLights.

bool screenSourceBound(uint screenIndex) {
    return (worldScreenMapping(screenIndex).state.x != 0.0);
}
// One past the highest screen whose source is bound this frame, or zero when none is.
uint screenLightLoopBound() {
    return passGroup.screenCount;
}
// Whether a hit on this material shades as a screen: a screen-instance id (above SDF_SCREEN_MATERIAL, from
// SdfProgramBuilder's screen-surface ScreenSlab overload) whose slot carries a glyph decal, or a source bound this frame
// and a mapping to draw it from.
// sampleScreenSurface shades exactly these hits, and the surface pass leaves them without a normal.
bool sdfScreenSurfaceShades(int material) {
    if (material <= SDF_SCREEN_MATERIAL) {
        return false; // the plain sentinel: no declared instance, so no screen table lookup.
    }

    uint screenIndex = (uint)(material - SDF_SCREEN_MATERIAL - 1);

    // An out-of-bounds structured-buffer read is zeroed on Direct3D 12 by spec but only defined under robustBufferAccess
    // on Vulkan, so the bound makes both backends agree by construction rather than by driver luck. Falling back to the
    // material-shaded path is the same answer a zeroed entry would produce here (no decal, no bound source), and the host
    // refuses such an id, so no valid program reaches this branch and no composed pixel moves.
    if (screenIndex >= SDF_MAX_SCREEN_SURFACES) {
        return false;
    }

#if defined(SDF_GLYPH_ATLAS)
    uint4 decal = sdfDecalCells[screenIndex];

    if ((decal.x > 0u) && (decal.y > 0u)) {
        return true;
    }
#endif

    // Declared, but neither a decal nor a mapped, bound source this frame: the material-shaded fallback applies.
    return (screenSourceBound(screenIndex) && (worldScreenMapping(screenIndex).imageU.w != 0.0));
}
#endif

#endif
