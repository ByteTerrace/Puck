# Materials and primitives

An SDF instruction produces both a distance and a material identity. Composition
operators determine the winning distance; the shared blend tail applies the
corresponding material rule.

## Composition

Hard operations use a strict winner comparison so ties are deterministic.
Smooth composition blends distance over an authored radius and records the
information needed for hit-only material interpolation. The interpolation is a
shading concern and must not change the march field.

Scoped fields isolate operations such as intersection, onion, dilation, and
displacement from the parent accumulator, then compose the scoped result back
through `PopField`. Their outward reach must be included in instance bounds.

## Primitive contract

Every shape has a packed lane layout shared by `SdfInstruction`,
`SdfProgramBuilder`, and `sdf-vm.hlsli`. A new primitive requires:

- a stable shape identifier and documented lane layout;
- host validation and conservative bound analysis;
- distance and gradient implementations;
- Lipschitz analysis when the field is not factor 1;
- behavior in reduced shader variants; and
- cross-backend verification.

Lifted two-dimensional profiles use `SdfLift.Revolve` or `SdfLift.Extrude`.
Regular polygons, stars, trapezoids, rounded rectangles, ellipses, glyphs, and
sampled regions are current ISA shapes; they are not builder-only macros.

## Text tiers

`Glyph` creates marchable extruded geometry from the atlas distance channel.
`GlyphDecal` is a material-level tier for dense reading text on a screen slab.
Keep these paths separate: coverage is suitable for shading, while geometry
requires a conservative distance field.
