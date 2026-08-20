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

## Degenerate profile admission

A shape whose exact core divides by a quantity derived from its own dimensions
carries an admission rule sized to the coarsest representation that evaluates
it, not to exact equality with zero. The deterministic field evaluator works in
Q48.16, so a quantity the shader resolves as a tiny positive float reads as
exactly zero there, and the two representations diverge across a whole window
of near-degenerate dimensions rather than at one point.

The trapezoid is the worked case. Its core projects onto the slanted side by
dividing by that side's squared length, and `FixedVector2.Dot` accumulates both
products exactly then rounds once, so the squared slant reads zero whenever
`round(2^16·Δr)² + round(2^17·halfHeight)² <= 2^15`. Allowing each raw
component half a quantum above the real value it came from, the widest real
slant inside that set is `sqrt(2^15 + 2·181 + 0.5)/2^16 ≈ 0.002778`;
`SdfProgramBuilder.MinTrapezoidProfileSlant` refuses below `0.003`. The rule is
a conjunction over both profile directions — equal half-widths with real height
is a cylinder and admissible, a flat profile with unequal half-widths is a disc
and admissible — so only a sliver vanishing in both directions at once is
refused.

The refusal sits at the doors that admit a shape: the builder, and the creation
document validator by way of `SdfSolidGeometry.TryValidateScaledPrimitive`,
because a cone is spelled as a revolved trapezoid and an authored per-axis
scale reaches its dimensions directly. `SdfProgramBuilder.Ellipse` nudges a
perfect circle apart instead of refusing it; the difference is that a circle
has a nearby non-degenerate ellipse to nudge toward, while a vanishing
trapezoid has extent in neither profile direction.

## Screen surface frames

A screen surface packs an origin and two world axes. The shader projects a hit
point onto each axis independently to produce a UV, while the slab's own
geometry rides a rotation derived from the same pair and the server's collider
projects the slab half-extents onto it.

The axes must therefore be unit and orthogonal. A merely linearly independent
pair is admissible arithmetic and inadmissible geometry: the UV, the rendered
solid, and the collided solid stop describing the same object, and the
disagreement grows without bound as the pair approaches parallel. Orthogonality
is refused at every door that accepts a frame — the builder, the program
constructor, and the world document validator — rather than documented as a
caller obligation, because none of the three consumers can detect the skew from
what it is handed.

The same reasoning governs a text run's right/up pair: the pen places glyphs
along the raw axes while each glyph's geometry rides the derived rotation.

## Material identifier bands

Material identifiers below the screen sentinel index the packed palette; the
sentinel itself selects procedural screen shading, and identifiers above it
decode to a direct screen-surface index. Both ends of the band are bounded on
the host, and the shader bounds the decoded index before it reads any screen
table, so an out-of-range identifier cannot resolve to a driver-dependent read.

## Text tiers

`Glyph` creates marchable extruded geometry from the atlas distance channel.
`GlyphDecal` is a material-level tier for dense reading text on a screen slab.
Keep these paths separate: coverage is suitable for shading, while geometry
requires a conservative distance field.
