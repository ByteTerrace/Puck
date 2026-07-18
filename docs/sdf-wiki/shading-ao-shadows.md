# Shading, ambient occlusion, and shadows

World shading runs after the marcher accepts a hit. The field, analytic normal,
material state, and configured lights feed a shared shader path on both GPU
backends.

## Ambient occlusion

The default ambient path samples three points along the hit normal. Each probe
uses the same scaled-distance contract as the marcher. The result modulates
ambient contribution only; it does not darken direct light a second time.

Cone AO and bent normals are optional quality candidates. They require a clear
budget, stable behavior on thin geometry, and evidence that the additional
field evaluations improve the target scenes.

## Shadows

Soft shadows march toward each relevant light and estimate penumbra from the
occluder distance. A per-pixel grid gather limits the candidate light set.
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
