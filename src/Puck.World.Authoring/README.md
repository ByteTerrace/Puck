# Puck.World.Authoring

The authored-content document families `Puck.World` embeds inline:
`puck.creation.v1` (`CreationDocument`/`CreationCanonicalizer`),
`puck.music.v1` (`MusicDocument`), and `puck.judge.v1` (`JudgeDocument`), all
riding the shared `DocumentCanonicalizer` core in `Puck.Assets` — which also
owns the `puck.audio.v1`/`puck.synth.v1` families (`Puck.Assets.Documents`),
so the ROM forges can consume them without a world assembly. `CreationFrame`
and `GridSnap` live here too. Host-side float on purpose —
authoring/presentation math, outside the simulation-state determinism
contract.

The SM83 ROM forge is `Puck.HumbleGamingBrick.Forge` (see the `rom-forge`
skill); the AGB forge is `Puck.AdvancedGamingBrick.Forge`.

## The creation author frame

Every `puck.creation.v1` position, rotation, and camera offset is authored in
ONE frame — right-handed, +Y up, +Z the front a shape faces, +X screen-right
when looking at that front — a 180° yaw about +Y away from the engine's own
frame (+Y up, −Z forward). `CreationFrame` is the one place that crosses
between them; nothing else in the document or the engine names either frame.

The primitive vocabulary and its dimensions belong to `Puck.SignedDistance`:
`SdfSolidPrimitive` names the shapes and `SdfSolidGeometry` is the one place
that decides a primitive's unit shape, so an authored `scale` of `(1,1,1)` is
the primitive's unit size. `CreationGeometry` keeps only the document-shaped
half — the reach a whole creation implies, shapes and text runs together.

| Primitive | Unit shape | `scale` reads as |
|---|---|---|
| Sphere | r = 1 | radius |
| Box | half-extents (1,1,1) | half-extent per axis |
| Capsule | r = 1, endpoint (0, 0.5, 0) | `x`/`z` = radius, `y` = cylindrical section length (total height = 2·radius + length) |
| Cylinder | r = 1, half-height 1 | `x`/`z` = radius, `y` = half-height |
| Cone | base r = 1, half-height 1, apex r = 0 | `x`/`z` = base radius, `y` = half-height |
| Ellipsoid | radii (1,1,1) | radius per axis |
| RoundCone | lower r = 1, upper r = 0.5, height 1 | scaled per axis |
| Torus | major 1, minor 0.4 | scaled per axis |

## Shape domain operators

`ShapeDocument.Domain` (`ShapeDomainOp`) is a `$type`-discriminated, ordered
list mirroring `SdfProgramBuilder`'s domain-operator family, applied in
creation space — after the placement/creation frame chain, before the
shape's own translate/rotate/scale. An absent/empty list is a no-op and
keeps a creation's canonical bytes and hash unchanged.

The render path applies them as point folds. The contact paths — the analytic
collider set and the fixed-point solid field — take the rigid copies
`SdfDomainExpansion` derives instead, so contact carries every copy the fold
draws. An op with no expansion is refused by name on a solid placement.

| `$type` | Builder call | Contact |
|---|---|---|
| `symmetry` | `SymmetryPlane(normal, offset)` | 2 copies |
| `repeat` | `RepeatLimited(spacing, limit)` | one copy per lattice cell; needs a whole-number `limit` (an absent one is unbounded and refuses) |
| `polar` | `RepeatPolar(count, axis, mirror, materialStride)` | `count` copies, doubled when `mirror` is set |
| `wallpaper` | `WallpaperFold(group, cell, limit, plane, materialStride, lodDistance)` | none — refused on a solid placement |

Copies compose across the list, capped by `SdfDomainExpansion.DefaultCopyBudget`.
Expansion is exact only for a prototype inside the fold's fundamental domain:
on a symmetry plane's positive side, inside a repeat's centre cell, between a
polar sector's walls. A prototype straddling a wall renders clipped and
collides whole.
