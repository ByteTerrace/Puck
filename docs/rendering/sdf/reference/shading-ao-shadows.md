# Shading, ambient occlusion, and shadows

World shading runs after the marcher accepts a hit. The field, analytic normal,
material state, and configured lights feed a shared shader path on both GPU
backends.

## Silhouette coverage and filtering

The deferred views pass softens grazing silhouette hits only when a cardinal
neighbor sees sky. It reads the completed primary pass at the current view's
render resolution; an empty tile uses the beam's current-frame proof because
its primary records may be stale outside the indirect dispatch. An exhausted
neighbor ray provides no visibility proof and cannot enable the blend.

The weight uses the terminal field residual divided by the hit threshold,
both in clamped field units, and the normal's grazing angle. A small field
rise behind a surface is insufficient evidence of sky: it also occurs between
grass and the ground, where sky blending creates pale outlines. This filter
adds no field query. Geometry-to-geometry edges remain unfiltered, and the
monolithic reference omits the filter because it has no completed neighbor
records. Check silhouettes, ground-backed grass, changing cameras, multiple
views and reduced render scales when changing this path.

## Ambient occlusion

The default ambient path samples three points along the hit normal. Each probe
uses the same scaled-distance contract as the marcher. The result modulates
ambient contribution only; it does not darken direct light a second time.

Cone AO and bent normals are optional quality candidates. They require a clear
budget, stable behavior on thin geometry, and evidence that the additional
field evaluations improve the target scenes.

## Shadows

Soft shadows march toward each relevant light and estimate penumbra from the
occluder distance. An 8×8 workgroup grid gather limits the candidate instance
set for the shadow ray; each pixel then consumes the shared mask.
Shadow steps must honor program `stepScale`, fold-safe bounds, and the same
conservative sampled-region behavior as primary rays.

Light culling must never exclude a light that can affect the pixel. Oversized
cells cost work; undersized influence bounds create visible discontinuities.

## Curvature and stylization

Curvature shading requires additional derivative information beyond the
default analytic normal. Treat it as a selectable presentation feature, not as
part of field correctness. Validate it against silhouettes, smooth blends,
small primitives, and moving cameras.

Keep diagnostic controls explicit: normal source, AO contribution, shadow
visibility, light-cell occupancy, and march termination should be inspectable
without changing simulation state.
