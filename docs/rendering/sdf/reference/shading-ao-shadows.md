# Shading, ambient occlusion, and shadows

World shading runs on each pixel's visibility record, which the surface pass
writes from the rasterized mesh target. The record's normal, material, frame
slot, occlusion and shadow visibilities and the configured lights feed one
shader path on both GPU backends; no shading pass queries the field.

## Coverage

A rasterized pixel is wholly a hit or wholly sky, so the lit image carries no
partial silhouette coverage and the views pass adds no edge filter. Temporal
reconstruction antialiases silhouettes over a view's history
([antialiasing and filtering](antialiasing-and-filtering.md)).

## Sky lighting

The shared sky environment supplies second-order spherical-harmonic ambient
irradiance and map reflections with analytic panel highlights. Ambient and
reflection gains live in `render.environment`, default to one and skip their
respective work at zero. Lighting visibility decides which layers contribute;
camera visibility independently decides which layers appear behind geometry.
The [environment map](../handbook/frame-rendering.md#the-environment-map)
describes projection, refresh thresholds and counted work.

## Ambient occlusion

The surface pass estimates occlusion in screen space (`sdfMeshAmbient` in
`passes/sdf-surface-stage.hlsli`): twelve normal-oriented hemisphere samples,
within 0.75 metres or a quarter of the hit's distance, whichever is smaller,
each occluding when the surface the camera sees through its pixel lies in front
of it by no more than that radius. The disk rotates with each temporal sample so
the resolve averages its rotations. The result modulates ambient contribution
only; it does not darken direct light a second time. Occluders off screen or
behind a nearer surface do not occlude.

## Shadows

The first two stable shadow slots' directional lights cast shadows from the
mesh draws through their cascades
([the shadow cascades](../handbook/frame-rendering.md#the-shadow-cascades)).
The surface pass answers each with a percentage-closer soft shadow from the
first cascade holding the point: a twelve-tap blocker search sizes the penumbra
from the light's penumbra slope and the gap to the blockers, and a sixteen-tap
filter of that width, at most fourteen texels, answers. The visibilities occupy
the K word's 8-bit lanes. A directional outside those slots shades unshadowed,
scaled by ambient occlusion. During a handoff the outgoing light's own
occlusion deficit fades to zero by the handoff's weight, keeping its radiance,
and the incoming light lights unshadowed until it holds the slot.

A cascade's texel bounds the shadow's resolution, and its normal offset
(`SdfMeshShadowCascades.NormalOffsetTexels`) bounds its acne; a body thinner
than a texel can miss its own shadow.

## Curvature and stylization

Curvature shading reads the record's curvature, which the surface pass writes
as zero: the mesh carries no curvature, so authored cavity, rim and ink
shading draw nothing. Keep diagnostic controls explicit: normal source, AO
contribution and shadow visibility should be inspectable without changing
simulation state.
