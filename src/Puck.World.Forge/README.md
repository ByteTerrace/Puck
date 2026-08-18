# Puck.World.Forge

Two halves under one project.

`Framework/`, `Tune/`, `Games/`, `Sm83Emitter.cs`, `HgbImage.cs`,
`AudioDocumentCompiler.cs`, and `VerifyMachineSettle.cs` are the SM83 ROM
forge — see the `rom-forge` skill.

`Authoring/` is the authored-content document family `Puck.World` embeds
inline: `puck.creation.v1` (`CreationDocument`/`CreationCanonicalizer`),
`puck.audio.v1`, and `puck.synth.v1`, all riding the shared
`DocumentCanonicalizer` core. `CreationGeometry`, `CreationFrame`,
`CreationDocumentPatcher`, `ChainRig`/`ChainSolver`, `EditHistory<T>`, and
`GridSnap` live here too. `SculptModel` — the sculpt workbench's live edit
session — holds a `CreationDocument` directly (the document IS the model);
`CreationDocumentPatcher` is the generic dotted/indexed document-path walker
its `TrySet`/`TryRemove` ride, addressed by `Puck.World.EditorSculptCommandModule`'s
`editor.sculpt.set`/`.remove` verbs. Host-side float on purpose —
authoring/presentation math, outside the simulation-state determinism
contract.

## The creation author frame

Every `puck.creation.v1` position, rotation, and camera offset is authored in
ONE frame — right-handed, +Y up, +Z the front a shape faces, +X screen-right
when looking at that front (the sculpt workbench's own view) — a 180° yaw
about +Y away from the engine's own frame (+Y up, −Z forward). `CreationFrame`
is the one place that crosses between them; nothing else in the document or
the engine names either frame.

`CreationGeometry` is the one place that decides a primitive's unit shape, so
an authored `scale` of `(1,1,1)` IS the primitive's unit size:

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

| `$type` | Builder call | Fixed-point solid field |
|---|---|---|
| `symmetry` | `SymmetryPlane(normal, offset)` | yes |
| `repeat` | `RepeatLimited(spacing, limit)` | yes |
| `polar` | `RepeatPolar(count, axis, mirror, materialStride)` | no — render only; a solid placement collides against the unfolded shape |
| `wallpaper` | `WallpaperFold(group, cell, limit, plane, materialStride, lodDistance)` | no — render only; a solid placement collides against the unfolded shape |
