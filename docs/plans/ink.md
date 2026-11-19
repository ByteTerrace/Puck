# Ink

Outlines in Puck are edge signals the engine computes once per view and an ink
phase that authors write in the shade program
([the ruling](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)).
This plan holds the research round, the design that follows from it, and the
build order.

## What exists

The outline today is a fixed edge detector in engine code
(`shade/sdf-toon-outline.hlsli`): a depth jump tested against the surface's
tangent plane, and a normal crease, read from the visibility buffer at up to 16
neighbour taps per styled pixel inside the views pass. Authors set four values
per material or as the world default (`ToonStyleDocument`: width up to four
output pixels, the distance at which width halves, the crease cosine, an ink
multiplier). The nearer surface owns a line; screens, portals and sky never ink.

What authors cannot do today: ink object or material boundaries, the light
terminator or inner lines by curvature; colour a line from the surface or the
light; vary width by curvature, light or distance in metres; taper or break a
line up in a way that holds still in world space; ink glass or water; choose
which material's ink wins at a boundary.

The cel and toon vocabulary in engine contracts (`ToonStyleDocument`,
`PaletteToonSelectDocument`, `render.toon`, `SdfToon`, the pass members
`ToonOutline` and `ToonInk`, `sdf-toon.hlsli`, the `world.toon` verb and its
lever) is a leak under [the engine is generic](../decisions/engine-design.md#the-engine-is-generic):
what it names is an edge pass, a shade-case selection by state, and a
diagnostic over authored phases.

## The research round

Three lanes, each with its own focus and verified citations, then one
synthesis into this page's design.

| Lane | Focus |
|---|---|
| `ink-shipped` | Shipped stylized games and offline line art: which edges they ink, how width, colour and breakup are authored, and what each costs |
| `ink-gpu` | Screen-space and GPU techniques: edge detection over a visibility buffer, distance to the edge at constant cost, temporal stability, dynamic resolution, tile classification, measured costs |
| `ink-field` | What a signed-distance renderer can do that others cannot: contours, creases and curvature from the field and the program's structure, world-space stable lines, and how Puck's pipeline would carry them |

## Design

To be written by the synthesis.

## Build order

To be written by the synthesis. The removal of the toon vocabulary is one of
its steps, with no aliases kept.
