// The pixel passes' common head. The surface pass (SDF_SURFACE_PASS) and views each define their pass macro, include
// this head, then their own stage, then the entry point (sdf-pixel-entry.hlsli), so a pixel kernel's include set is the
// world modules (sdf-world.hlsli) and the stage it compiles: an edit to the other pass's stage recompiles nothing here.
// Both read their resources through the sdf-world interface: the surface pass writes the visibility records through
// sdfVisibilityRecordsRW, and views reads them through sdfVisibilityRecords (frame/sdf-visibility.hlsli).
#ifndef PASSES_SDF_PIXEL_PASS_HLSLI
#define PASSES_SDF_PIXEL_PASS_HLSLI
#define SDF_DYNAMIC_TRANSFORMS
#ifndef SDF_SURFACE_PASS
#define SDF_VIEWS_PASS
// Views samples the bound screens and the glyph atlas their decals draw from.
#define SDF_SCREEN_SOURCES
#define SDF_GLYPH_ATLAS
#endif
#include "sdf-world.hlsli"
#include "../frame/sdf-pixel.hlsli"

#endif
