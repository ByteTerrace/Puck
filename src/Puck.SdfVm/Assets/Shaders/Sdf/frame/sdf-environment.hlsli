// The frame's screen tables: the screen surfaces' frames, the mapping each screen is drawn from, which screen slots hold a
// source this frame and which screen hits shade as a screen.
#ifndef FRAME_SDF_ENVIRONMENT_HLSLI
#define FRAME_SDF_ENVIRONMENT_HLSLI

#ifdef SDF_SCREEN_SOURCES
#include "sdf-screen-mapping.hlsli"
// A declared ScreenSlab instance's world-space front-face frame (see Puck.SignedDistance.SdfScreenSurface). Indexed
// DIRECTLY by screen index
// (below SDF_MAX_SCREEN_SURFACES, the same slot SetScreenSource/screenSources binds) — not by declaration order — so a hit resolves its
// surface with no search; an unfilled slot's entry is never read (no material id can address it: the host packs an
// entry only when SdfProgramBuilder registers that screen index).
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
// The screen source images: screenSources, one sampled image per screen index, each read through the samplers element
// its row names. A screen with no source bound this frame holds a valid filler view; the shader never samples an unbound
// screen (screenSourceBound gates it), so the filler's content never reaches the image. Every screen index is bounded
// by the generated SDF_MAX_SCREEN_SURFACES before it indexes a per-screen table.
// The acquired-image reduction (sdfScreenLights) holds each screen's pixel-weighted whole-image mean, which direct area
// lighting reads. Geometry remains screenSurfaces[i].

bool screenSourceBound(uint screenIndex) {
    return (worldScreenMapping(screenIndex).state.x != 0.0);
}
// One past the highest screen whose source is bound this frame, or zero when none is.
uint screenLightLoopBound() {
    return passGroup.screenCount;
}
// The slot a screen material's hit shades through (sdfScreenSlot), or SdfNoScreenSlot for the plain sentinel or a screen
// holding none this frame.
uint sdfScreenSlotOfMaterial(int material) {
    return ((material > SDF_SCREEN_MATERIAL) ? sdfScreenSlot(material - SDF_SCREEN_MATERIAL - 1) : SdfNoScreenSlot);
}
// Whether a hit on this material shades as a screen: a screen-instance id (above SDF_SCREEN_MATERIAL, from
// SdfProgramBuilder's screen-surface ScreenSlab overload) whose slot carries a glyph decal, or a source bound this frame
// and a mapping to draw it from.
// sampleScreenSurface shades exactly these hits, and the surface pass leaves them without a normal.
bool sdfScreenSurfaceShades(int material) {
    // The plain sentinel names no screen, and a screen holding no slot this frame (not resident: out of view, shut, or
    // past the slots the nearest screens hold) shades through the material-shaded fallback, as one with no source does.
    uint screenIndex = sdfScreenSlotOfMaterial(material);

    if (screenIndex == SdfNoScreenSlot) {
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
