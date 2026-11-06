# Sampled fields

A **sampled field** is a small 3D photograph of distance: a cubic-voxel lattice
over a box, one float per voxel, sampled by trilinear interpolation. It is the
one place values the analytic program cannot express enter the field: a height
field's baked reference (`SdfHeightField`, which `WorldFieldEmitter` publishes),
or any producer whose surface is a function of data rather than of
instructions. The `√3` scale keeps the
interpolant safe to sample, and the values travel beside the program rather than
in it, so they can change every few ticks without rebuilding the program. Puck
has two kinds of baking: these fields, baked by their producer on the CPU, and
the frame's mesh bake, which samples each residency's program into the meshes
every view draws ([the bake](frame-rendering.md#the-bake)).

## The field, the snapshot and the set

Three types carry a sampled field, all in `Puck.SignedDistance`:

- `SdfSampledField` is the producer's cell. Its lattice dimensions are fixed for
  its life; `Publish` replaces its current values with a new snapshot.
- `SdfSampledSnapshot` is one immutable set of voxel values. A snapshot never
  changes.
- `SdfSampledSet` is the snapshots of a program's fields at one moment, by
  handle. `SdfSampledSet.Capture` answers the previous set itself when no field
  moved, so consumers compare sets by reference.

A producer emits the instruction with `SdfProgramBuilder.SampledValues`, naming
the field. The program assigns each distinct field a **handle**
(`SdfProgram.SampledFields`) and writes it into the instruction's Data1.z lane;
no voxel enters the packed words. A field whose dimensions change is a new field,
and so a new program.

Each frame the composition captures the held program's fields into the frame's
set (`SdfFrame.Sampled`) after every emitter's `PrepareFrame` has published its
values. Publishing moves the set and no program word: the program, its analysis,
its upload and the meshes of every group that reads no moved field stand.

## The mesh bake samples what it hashed

The mesh identity covers the values: `SdfMeshGeometryBuilder` hashes a sampled
instruction's snapshot in place of its handle, as it hashes a side table's
values in place of its packed offset, and keeps the snapshot beside the poses it
keeps (`SdfMeshGeometryBuilder.Sampled`). The bake reads only the handles some
instruction samples (`SdfProgram.BakedSampledFields`), and compares their own
identity, `SdfSampledSet.Baked`, which stands while those snapshots stand: a
height field's heights handle, which only a draw reads, can move every frame and
cost the bake nothing. A frame whose baked set moved resamples
(`SdfMeshPathBake.Resample`): it walks only the groups that read a sampled field
and plans again those whose geometry hashed a snapshot the set no longer holds
(`SdfMeshBakeGroup.Refresh`, from each group's planned box); every other group,
its mesh and any lattice in flight stand. A group still baking an older snapshot
keeps baking it while the newest plan waits as its successor, started once it
lands, so a field republished faster than one bake (a wetting front crossing
columns) still lands meshes and settles on its latest snapshot. A changed
reference therefore keys a new mesh for that field's group alone, and a revisited
one keys a mesh the library or store already holds. `world.residency` counts
restarts, resamples, the readers they walked and the coalesced successors.

The bake kernel is the only kernel that samples a distance field (it defines
`SDF_SAMPLED_REGIONS`; every other kernel answers `SDF_FAR_DISTANCE`). It reads
the values from `sdfSampledValues`, a table each bake job writes from the
snapshots its groups sealed (`SdfMeshBakeGroup.SampledSnapshots`,
`SdfMeshBakeBatch.WriteSampled`): a word per handle naming where its snapshot
starts, then the snapshots. A batch carries one snapshot a handle, so a lattice
samples exactly the values its mesh is keyed by, never a newer publication. The
residency's tables bind a filler there, and the program upload carries none of
it.

## The √3 rule keeps sampling safe

The true distance to a surface is exactly 1-Lipschitz, and each axis
finite-difference of its samples is therefore at most 1. But the trilinear
**interpolant** between samples has a gradient that can reach `√3`: all three
axis slopes near 1 at once, at a crease (the 2D `min(x, y)` corner is the
intuition: its interpolated gradient reaches `√2`). A field whose gradient
exceeds 1 can underestimate how fast a surface approaches.

The fix costs nothing at runtime: **the producer divides every stored value by
`λ = √3`.** The interpolant of `c/√3` is 1-Lipschitz by construction, so it needs
no change to the program's `stepScale` and leaves the zero set where it was.

Outside the box, the field reports `distance-to-box + boundaryFloor` (a
host-baked margin term), a valid positive lower bound. The producer keeps the
zero set a margin inside the box, so its surface never touches a face and no
seam can show.

Sampling is a plain storage-buffer read with **manual trilinear interpolation**:
eight explicit loads followed by interpolation along x, y, then z. Each
interpolation explicitly subtracts, multiplies and adds with floating-point
contraction disabled on every intermediate, so the same voxel values and
fractional coordinate produce the same result on both GPU backends. A `precise`
result alone does not constrain the `lerp` intrinsic's SPIR-V `FMix` arithmetic.

## Sampled fields are presentation

- **Never simulation.** CPU physics queries and the field evaluator
  ([Queries and determinism](queries-and-determinism.md)) never read a sampled
  field; the evaluator refuses the shape by name. A height field's bodies and
  media read the lattice's own fixed-point cells.
- **Never durable.** Snapshots are derived from document state each frame; they
  are never written to documents, replays or the store. The meshes baked from
  them are caches keyed by their contents.
## Height fields move as they are drawn

A height field (`SdfHeightField`) is a lattice of columns, each a box from one
cell below the origin to its height. It owns two sampled fields. Its
*reference* is the columns' distance lattice with every raised column's top at
one reference height (`SdfHeightField.ReferenceCells` above the origin); the
bake samples and keys it, and it is published again only when a column is
raised from nothing or lowered to nothing. Its *heights* hold one value a
column, which no instruction samples and the mesh identity never hashes.
`SdfProgramBuilder.HeightField` emits the reference as an instance's sampled
instruction and names the heights as that instance's displacement
(`SdfProgram.HeightFields`), so the field's mesh is keyed by its extent, cell and
raised columns, never by its heights.

The heights reach the render passes through the frame's sampled table,
`sdfFrameSampled` (`SdfFrameSampled`): a word per handle naming where its entry
starts, each height field's entry its grid and its heights this frame and the
frame before, written into a region that owes only the words that moved. A
height field's draw names its handle in its record
(`SdfMeshRecord.DisplacementWord`), and every read of a mesh vertex passes
through `sdfMeshDisplaced` (`frame/sdf-mesh.hlsli`): the reference top's last
cell, which holds the top face and its edge rounding, rises by the bilinear
height over the column centres less the reference, the band below scales from
the base, and the normal turns by the map's inverse transpose, the height's
gradient on the top face. The mesh, shadow and glass passes, the surface pass
and the resolve's reprojection all read that one resolver, so they see one
surface; the draw's box spans its displacement's band of heights. A height field
is dynamic for lighting, so the irradiance volume's static scene leaves it out.
The guarantee and its limits are in the
[water plan](../../../plans/water-rendering.md#field-surfaces-move-at-draw-time).

---

## Related resources

- `SdfSampledField`, `SdfSampledSnapshot`, `SdfSampledSet` and
  `SdfProgramBuilder.SampledValues` in
  [`src/Puck.SignedDistance`](../../../../src/Puck.SignedDistance/), and the
  enum doc in [`SdfShapeType.cs`](../../../../src/Puck.SignedDistance/SdfShapeType.cs).
- The `SampledRegion` lane layout and the bake's table: the sampled rows in
  [the sync pairs](../../../../.claude/skills/rendering/references/sync-pairs.md).
- The frame's mesh bake: [the bake](frame-rendering.md#the-bake),
  `SdfMeshPathBake` in [`src/Puck.SdfVm`](../../../../src/Puck.SdfVm/), and
  its lattice and contouring, `SdfSampledLattice` and `SdfDualContouring`, in
  [`src/Puck.SignedDistance/Baking`](../../../../src/Puck.SignedDistance/Baking/).
- The baked-volume family rejection and its single reconsider-trigger:
  [Rejected and conditional SDF techniques](../reference/negative-results-and-rejections.md).
