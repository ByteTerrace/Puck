// The views passes' split switches: each one, defined, compiles one part of the shading out of the views kernels and the
// probe lookup's, so the part's share of `sdf.world$views` (and `sdf.world$views-probes`) reads as the difference
// against a reload with none defined. They are a measuring instrument, never a quality setting: a kernel built with one
// shades wrongly by design. Nothing defines one in the shipped tree. To read a part, copy the `Sdf` kernel tree to a
// scratch directory, add `#define <switch>` inside the guard of the copy of this file, and in a running World:
//
//     world.gpu-timing on
//     world.shaders.reload <scratch>/Sdf
//
// then read `world.gpu-timing` once the reload reports applied and the timing window has refilled; a reload of the
// checkout's own tree restores the shipped kernels. Only the views kernels and the probe lookup include this file, so a
// reload recompiles them alone.
//
// SDF_VIEWS_OMIT_PROBES        the irradiance volume's lookup: no gather in the probe lookup and no record read in
//                              views, every hit reads the camera sample's light (sdfMeshIndirectSeen's unmeasured path).
// SDF_VIEWS_OMIT_LIGHTS        the light walk and its tiles: no light of the table or screen adds anything.
// SDF_VIEWS_OMIT_LIGHT_TILES   the light tiles alone (sdf-views-lights.hlsli): every pixel walks the view's whole light
//                              mask, so the difference is what the tiles save net of their own cost. This one shades
//                              alike, to within the tiles' reach margin.
// SDF_VIEWS_OMIT_MATERIALS     the material layers: paints, inset, weathering and flow.
// SDF_VIEWS_OMIT_REFLECTION    the environment the specular lobe reflects.
// SDF_VIEWS_OMIT_FILTER        the 4x4 occlusion and shadow filter: views shades with the surface pass's raw estimates.
// SDF_VIEWS_OMIT_INK           the edge search and the ink phase: no view widens its tile, seeds or measures an edge,
//                              and no program's ink phase runs, so no line is drawn.
// SDF_VIEWS_OMIT_SHADING       everything after the record's load: a hit shows its normal, so what remains is the load,
//                              the hit door, the debug view and the store.
#ifndef PASSES_SDF_VIEWS_SPLIT_HLSLI
#define PASSES_SDF_VIEWS_SPLIT_HLSLI
#endif
