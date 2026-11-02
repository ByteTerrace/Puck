# Transmission and self-shadow: the first two surfaces along the light

A design for the shadow term a wrapped material reads past the terminator,
written after three review rounds of illumination step 1 each fixed one
class of defect and exposed the same defect in a new shape. It replaces the
cascades' exit layer with a term whose correctness rests on nothing a mesh's
topology or winding has to promise. Implementation is a later lane; the
steps at the end are its lanes.

Status: design, unimplemented. The code it describes is step 1's third round
(`passes/sdf-surface-stage.hlsli`, `SdfMeshRasterPass.cs`,
`SdfWorldPassRecorder.RecordExitDraws`, `WorldPlacementStamper.MeshOf`,
`SdfMeshDraw.EnclosesSolid`).

## The problem

Step 1 gave lit surfaces wrapped diffuse past the terminator,
`band(cosine) × visibility`, and had the surface pass query the cascades
wherever that response is positive ([decision](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)).
A receiver turned from the light must be shadowed by a real blocker (a roof,
a slab, a sheet) and must not be shadowed by its own body, whose lit side
stands between it and the light by construction. The three rounds:

1. A thickness estimate from the surface's curvature. Flat geometry has no
   curvature, so a millimetre wing let a slab 10 cm off through and a
   centimetre sheet shadowed itself.
2. An exit layer per cascade: the casters re-rendered with their light-facing
   faces culled, so a receiver turned from the light compares against the
   first face where light *leaves* a solid, which is its own unless another
   solid stands in front. Review found the normal offset hid blockers, open
   casters vanished from the layer (an entry with no exit), and two-sided
   authored meshes self-shadowed (their reversed duplicates survive the cull).
3. The offset points straight at the light, non-enclosing casters go into the
   exit layer whole as thin casters, and authored meshes are classified
   closed or open at load (`WorldPlacementStamper.ClosedOutward`). Review
   found grazing self-shadowing from the receiver-plane clamp, one signed
   volume orienting multiple shells wrong, T-junctions turning solids into
   thin casters, and skinned deformation inverting winding while
   `EnclosesSolid` stays true.

The exit layer reads "the first face turned from the light" as "where light
leaves the first solid". That equivalence holds only for geometry that is
closed, consistently wound outward, rigidly transformed and unclipped. The
bake guarantees all four for its own meshes. Nothing guarantees them for an
authored mesh (orientation inference for handmade meshes is known to be
unsound in general: Takayama, Jacobson, Kavan and Sorkine-Hornung 2014
report their own winding-number orientation "fundamentally flawed" for most
shared meshes, <https://users.cs.utah.edu/~ladislav/takayama14consistently/takayama14consistently.pdf>),
a portal clip removes a cap, a mirror flips a determinant, a member's motion
can fold a skin, and a collapsed level of detail can fold a triangle. Each
round's fix defended one of the four invariants; the next round broke
another. The design below asks nothing of them.

## The term

Each cascade holds two depth layers along the light, both from the same
kept draw set under the same cull:

- **L1**, the nearest surface of any facing: today's depth.
- **L2**, the nearest surface of any facing farther than `L1 + ε` along the
  light: a depth peel of L1 (Everitt, "Interactive Order-Independent
  Transparency", NVIDIA 2001,
  <https://developer.download.nvidia.com/assets/gamedev/docs/OrderIndependentTransparency.pdf>;
  its two-layer form is Wang and Molnar's second-depth map, UNC TR94-019,
  <https://www.cs.unc.edu/techreports/94-019.pdf>). The peel pass draws the
  cascade's kept draws with no culling, and its fragment stage loads the
  cascade's L1 at its texel and discards a fragment whose depth is within
  `ε` of it. `ε` is small: a millimetre, above the rasterizer's depth noise
  for one triangle drawn twice and below any blocker gap a shadow should
  show.

A receiver `P` on a triangle whose camera-side normal is turned from the
light reads one layer by one bit of its mesh, **`Solid`**:

- `Solid`: `P` is where light leaves a solid, so the surface in front of it
  along the light is its own entry. It is shadowed where **L2** lies in front
  of it: a second surface before its own exit means a blocker's entry, a
  blocker's exit, or a sheet, and in every case something other than its
  own body.
- not `Solid`: `P` is a sheet, with no body of its own in front of it. It is
  shadowed where **L1** lies in front of it, as a lit face is.

A receiver facing the light reads L1, as today. The percentage-closer
filter, the in-cone blocker search and the plane-extended per-tap comparison
are unchanged; only the layer a tap reads and the receiver-plane slope's
clamp change ([numerics](#the-receiver-plane-at-grazing-angles)).

### Why it is correct by construction

The term depends on two things: the depth order of surfaces along a light
ray, and the receiver's one bit. Neither reads a winding.

- A **blocker** of any class shadows: a closed body contributes an entry and
  an exit, a sheet one surface, a clipped body what remains of it, a mirrored
  copy its surfaces where they are drawn, a folded skin or a folded collapsed
  level its surfaces wherever they lie. For a `Solid` receiver, any two
  surfaces in front of it put L2 in front of it; a single sheet in front of a
  `Solid` receiver makes L1 the sheet and L2 the receiver's own entry, which is
  in front of it too. For a sheet receiver, any one surface in front puts L1
  in front. No blocker class needs an exit, a cap or an orientation to count.
- The **receiver's own body** never shadows a `Solid` receiver: its entry is L1
  and its exit is `P` itself, so L2 is `P`, the plane-extended comparison
  finds a zero gap across the filter disk, and the tap reads lit. A second
  chord of the same body (an arm over its torso) is a blocker, as it is
  physically and as the exit layer also had it.
- **Coincident surfaces** (a box standing on a slab, one authored triangle
  drawn in both windings) change nothing: `ε` applies between L1 and L2 only,
  and a duplicate of L2 is L2.
- The one piece of **inference** left is `Solid` for authored meshes, and its
  error is one-directional. A closed mesh read as a sheet compares against L1,
  its own entry, and loses its wrap past the terminator: it over-shadows, and
  nothing leaks. A sheet read as `Solid` would leak, and the edge-pairing test
  cannot make that error: an open mesh has an unpaired edge. The flag the
  author writes ([below](#the-solid-bit)) covers the test's blind spot, a
  T-junctioned closed mesh, in the author's own words.

Compare the exit layer, where a misoriented face is a missing exit, and a
body whose every drawn face faces the light (a single-sided light-facing
sheet treated as enclosing, a clipped body with its exit clipped away)
vanishes from the layer: a leak, which rounds 2 and 3 patched class by class.

### The `Solid` bit

`SdfMesh.Encloses` becomes `SdfMesh.Solid`, a receiver-only property with
this meaning: a face of this mesh turned from a light is where that light
leaves a solid. `SdfMeshDraw.EnclosesSolid` is deleted: the determinant and
clip-plane tests served the exit layer's culling and mean nothing here (a
mirrored or clipped `Solid` body's dark side still has its own entry in front
of it, or has had it clipped away, in which case L1 is `P` and L2 lies
behind, and it reads lit either way).

| Mesh | `Solid` | Why |
|---|---|---|
| Baked (`SdfMeshPathBake`), rigid or skinned, any level, any chunk | true | Closed by construction: the contour winds the field's boundary. A skin whose member folds a chord through itself is authoring Puck cannot repair; under this term its folded faces read against the wrong layer and over-shadow, never leak. |
| Authored, every edge paired after position welding | true | The edge test (`ClosedOutward`'s pairing, kept as `IsClosed`, its volume-sign flip deleted) |
| Authored, an unpaired edge | false | A sheet, a soup, or a T-junctioned solid |
| Authored with `solid: true` or `solid: false` on its `mesh` | as written | The author's word outranks the test |

`WorldPrototypeMesh` (`src/Puck.World.Schema/WorldPlacement.cs`) gains
`Solid: bool?`; `puck schema` carries it; `puck lint` warns when
`solid: true` is written on a mesh with an unpaired edge, naming the edge,
since the author may mean to close it. This is the explicit, cheap
declaration the brief asked about, and it meets "ergonomic is optimal": the
engine infers what the geometry means in the common case, so an author writes
nothing, and the flag exists for the one case geometry cannot settle. It is
not a spelling that costs more; it is a meaning the triangles do not carry.

Authored meshes return to two-sided camera drawing for every mesh
(`TwoSided`, as before round 3): no orientation code decides which faces the
camera sees, so a misoriented shell never loses its walls (round 3's visual
regression). The shadow passes draw the authored winding only (the first half
of the two-sided index list; `SdfMesh` records the count the cascades draw),
so a cascade rasterizes each authored triangle once. `Mirrored` stays as the
camera's concern for single-sided meshes.

### The receiver-plane at grazing angles

Round 3's remaining high is numeric and independent of the layer: the
receiver-plane slope's denominator is clamped to 0.2
(`sdf-surface-stage.hlsli`, `lightNormal.z`), so a plate within 11 degrees of
edge-on to the light compares its taps against a plane four or more times
too shallow and shadows itself. The clamp goes. The face's gradient is exact
for the triangle the cascade rasterized, so with the unclamped gradient a
planar receiver's own surface gives a zero gap at every tap at any angle, as
the reviewer's evaluation of the existing equations showed for the former
offset. The denominator keeps a floor of 0.02 (50 texels of depth per texel,
within 1.2 degrees of edge-on) for float stability alone. The toward-light
offset for receivers turned from the light stays (round 3's fix for the
inverse-cosine amplification of a normal offset; the lit side keeps its
normal offset, the standard receiver treatment, Isidoro, "Shadow Mapping:
GPU-based Tips and Techniques", GDC 2006,
<https://gdcvault.com/play/1013442/Shadow-Mapping-Tricks-and>), and the
2.5-texel slope allowance is re-derived against a curved receiver once the
plane is exact; the implementing lane measures whether it can fall.

The information floor stays and is stated: at a grazing angle the receiver's
own surface spans `|gradient|` of depth across one texel, and a blocker
inside that span at a tap along the gradient is indistinguishable from the
surface. Taps across the gradient still find it. The open-items case (a 2 cm
slab 0.10 m along the light from a 3 mm panel at n·l = -0.25, gradient about
3.9 texels per texel at a 1/64 m texel) is found by every tap beyond 1.6
texels along the gradient and by every tap across it.

## What each caster class gets

| Caster class | As a blocker | As a wrapped receiver |
|---|---|---|
| Baked SDF creation, static | Entry and exit in L1 and L2; exact | `Solid`; its own exit is L2; wrap survives in the open, shadowed under anything |
| Baked, skinned or deforming | Every drawn surface counts, however the members move | `Solid`; a folded chord over-shadows locally, never leaks |
| Baked, chunked or at a coarser level | Chunks and folded collapsed triangles are surfaces; no orientation read | As the static case |
| Authored, closed and paired | Entry and exit count whatever the winding | `Solid` by the edge test |
| Authored, multi-shell | Each shell's surfaces count; no per-shell orientation exists to be wrong | `Solid` by the edge test; camera sees every wall (two-sided) |
| Authored, T-junctioned closed | Counts whole | A sheet by the test (loses wrap) until the author writes `solid: true` |
| Authored, open or two-sided card, foliage card | One surface, drawn once in the cascade | A sheet: shadowed by anything in front, lit alone |
| Portal-clipped body | Its remaining surfaces count; no cap needed | `Solid`; entry present → L2 is its exit; entry clipped → L1 is itself and L2 behind; lit either way |
| Mirrored draw | Its surfaces where drawn | `Solid` from its mesh; no determinant test |
| Baked thin foliage (the standard's leaves and blades, closed slabs) | Entry and exit; where thinner than `ε` along the light, one surface | `Solid`; thinner than `ε`, L2 lies behind and it reads lit, which is right |
| Glass | Skipped by the cascades, as today | — |

## What the three rounds' findings become

Round 1 (`illum1-astra-findings.md`):

- [high] curvature thickness estimate: already deleted in round 2; the term
  needs no thickness.
- [medium] wrap missing from the surface pass's identity: fixed
  (`SdfWorldTables.Cadence.cs`, `m_wrapSignature`).
- [medium] cel style in the bake identity, [medium] the standard style in
  C# defaults: outside this design; they stay in the
  [Lighting and GI packet](open-items.md#lighting-and-gi).

Round 2 (`illum1fix-astra-findings.keep.md`):

- [high] the normal offset hid blockers: round 3's toward-light offset
  stands; no change.
- [high] an open or clipped caster disappears from the exit layer: gone by
  construction. Every drawn surface counts; a clipped body needs no cap.
- [high] two-sided authored meshes self-shadow: gone. Cascades draw one
  winding; the peel reads no winding; the receiver bit is the edge test or
  the author's flag.
- [medium] toon toggle save, [medium] debug world cel default, [low] stale
  handbook: outside this design.

Round 3 (`illum1fix2-astra-findings.keep.md`):

- [high] grazing self-shadowing from the plane clamp: the clamp goes
  ([numerics](#the-receiver-plane-at-grazing-angles)).
- [high] one signed volume cannot orient multiple shells: no orientation is
  computed or needed. `ClosedOutward`'s flip is deleted; authored meshes are
  two-sided for the camera.
- [medium] T-junctions make a solid a thin caster: a quality loss (lost
  wrap), never a leak, and the author's `solid: true` ends it. Splitting
  edges at vertices that lie on them is a possible refinement of the test,
  not required.
- [medium] skinned deformation inverts winding under `EnclosesSolid`: no
  winding is read. `EnclosesSolid` is deleted.

From the packet: the portal straddler's wrapped side shadowed by its own lit
side (a thin caster in the exit layer) reads lit; the grazing slab leak is
the stated information floor, narrowed by the exact plane.

## Cost

Measured today (illum1-fix, `world.counters gpu pass sdf.world$shadow-near`
at the stage, Direct3D 12): 460 draws and 2.62 M vertices over two render
passes, and a D32 image per cascade (16 MiB at 2048 texels). Both schemes
have the second image; the difference is the second render pass.

- **Vertices.** The exit layer draws enclosing draws front-culled and the
  rest whole; the peel draws every kept draw with no cull. Vertex work in the
  second pass rises from about half the depth pass's to the depth pass's.
  Authored meshes fall by half in every cascade (one winding).
- **Fragments.** The shadow fragment stage already exists
  (`sdf-mesh-shadow.frag.hlsl`, `[earlydepthstencil]`, counting). The peel's
  stage adds one `Load` of L1 and a `discard`, and loses `[earlydepthstencil]`
  (a discarded fragment must write no depth): the depth test still runs
  early, the write moves after the stage. Fragments that survive the test run
  a one-load shader; at 2048 texels and a depth complexity of three to five
  that is 5 to 10 M loads from a cache-resident image.
- **Estimate.** The second pass goes from about 0.5 to about 1.1 to 1.3 times
  the depth pass. With the near depth pass at roughly 0.3 to 0.5 ms on the
  RTX 2060 at the stage, the term costs 0.15 to 0.3 ms more per rendered
  cascade than the exit layer, two cascades a frame when the far one is
  rated: under 0.6 ms, under 4 % of the 60 FPS bar. The implementing lane
  measures it with `world.gpu-timing` on both backends at `debug.go stage`
  and the meadow, on a quiet machine, and records it here.
- **A world with no wrapped material** skips the peel pass entirely: the
  program's material table knows its wraps (`m_wrapSignature` already feeds
  the surface identity), so a frame whose palette has no positive wrap
  records no peel and the surface pass never reads L2.
- **Memory:** unchanged from today. **Backends:** `discard` is core in both;
  Vulkan's `shaderDemoteToHelperInvocation` is already on for the stamp fade
  (`clip`). L1 is read in the peel pass after its own render pass ends, in a
  sampled layout, as the surface pass reads it today.

## Composition

- **V5 caster culling** ([visibility](visibility.md#v5-multi-view-and-shadow-work)).
  Light-space occlusion of casters must test against the **second** layer's
  pyramid, not the first: a caster behind one sheet is exactly what L2
  records, and dropping it would let a `Solid` receiver under that sheet read
  its own exit as L2 and light up. A caster wholly behind L2 cannot change L1
  or L2 and may go. The RedLynx receiver mask (casters that reach no visible
  receiver) and the box cull are unaffected; both layers draw one kept set.
- **GI relight** (illumination step 2). A relit probe hit on a wrapped
  material turned from the sun needs the same term; the relight calls
  `sdfMeshShadow` through the same two layers, one implementation.
- **Cel shading.** `band(cosine) × visibility` is unchanged; this design
  supplies the visibility.
- **The far cascade's hold** (`SdfUpdateRates.ShadowFar`): both layers are
  drawn and held together under the held camera.

## Rejected

- **The exit layer** (rounds 2 and 3). Cheaper by half a depth pass per
  cascade, and sound when every drawn face's winding says which side the
  light is on. Its correctness is a conjunction of four invariants the raster
  path does not own; each review round broke one. Kept as the cheaper
  fallback only if the peel's measured cost fails the bar, with authored
  meshes drawn two-sided for the camera, one winding in the cascades, and
  per-shell orientation by ray parity; even then a clipped body with its exit
  clipped away leaks.
- **Thickness baked from the field** (per vertex or per texel, from the SDF
  at bake time). The chord through a body depends on the light's direction;
  a scalar along the normal follows it only at normal incidence, a
  directional basis (spherical harmonics of chord length) multiplies vertex
  words, and neither exists for an authored mesh or follows a skin's members.
  Round 1 was its curvature-shaped cousin. The industry's baked-thickness
  translucency (Barré-Brisebois and Bouchard, GDC 2011,
  <https://frostbite.com/frostbite/news/approximating-translucency-for-a-fast-cheap-and-convincing-subsurface-scattering-look>)
  is an approximation of this kind and says so.
- **Field visibility for SDF creations, raster for authored meshes.** A
  per-pixel walk of the receiver's own field toward the light is a second
  `mapCore` call site in a pixel kernel, which the renderer forbids ("one
  `mapCore` call site"; the frame walks no field), and it would leave the
  authored path on a different mechanism.
- **Draw identity in the cascade** (an id attachment: "L1 is mine" instead
  of the `Solid` bit). Fails for sheets of one draw shading each other (grass
  cards in one authored mesh), and that failure leaks; the edge test's
  failure over-shadows.
- **Shadow-map thickness as transmittance** (Jimenez, Whelan, Sundstedt and
  Gutierrez 2010, <https://graphics.unizar.es/papers/Real-Time-Realistic-Skin-Translucency.pdf>;
  Frostbite): attenuate the wrap by `exp(-(P - L1)/σ)`. One layer, no bit,
  physically motivated, and it changes the look: a thick body's dark side
  goes dark, which step 1 ruled out ("exact for a millimetre wing and a
  metre-thick plate alike") and which would darken the Moth's cheek. Noted
  under the owner's calls as a possible separate material term.
- **Woo's midpoint** (Graphics Gems III, 1992, pp. 338–342) for lit
  receivers: compare against `(L1 + L2) / 2` to drop the lit side's bias. The
  two layers make it free to try; it is not part of this design and the lane
  may evaluate it after the term lands.
- **Ray-traced shadows.** Exact and orientation-free, but a baseline bar
  device (the Deck) has no ray tracing; upper hardware enhances, never
  carries the baseline.
- **Stencil shadow volumes.** Orientation-dependent and fill-bound.

## Implementation steps

Each is one lane with a mechanical check; the lanes are serial except the
third, which can run beside the first.

1. **`transmit-peel`: the two layers.** `ShadowExitPipeline` becomes a peel
   pipeline (`Cull: None`, a `sdf-mesh-shadow-peel.frag.hlsl` that loads the
   cascade's L1 and discards within `ε`; the kernel interface gains the L1
   binding for the peel stage). `RecordExitDraws` becomes one pass over the
   kept draws. `sdfMeshShadowDepth(exit)` reads L2; `sdfMeshShadow` chooses
   L2 for a `Solid` receiver turned from the light and L1 otherwise.
   `SdfMeshDraw.EnclosesSolid` is deleted; `SdfMesh.Encloses` is renamed
   `Solid`. A palette with no positive wrap records no peel.
   Check: settled captures on both backends at the plinth stop (the Moth's
   cheek under the slab dark, in the open lit, the step 1 witness) and at
   `debug.go portals` with the avatar straddling the glass (its dark side
   keeps wrap, the packet's defect); `world.counters gpu pass
   sdf.world$shadow-near` shows the peel's draws equal to the depth pass's;
   `world.gpu-timing` for `shadow-near` and `shadow-far` recorded here against
   the estimate.
2. **`transmit-authored`: authored meshes.** `MeshOf` draws every authored
   mesh two-sided and records the single-winding count the cascades draw;
   `ClosedOutward` becomes `IsClosed` (pairing only, no flip);
   `WorldPrototypeMesh.Solid`; `puck schema`; the lint warning. Delete
   `Mirrored`'s dependency on `Encloses` where a two-sided mesh needs none.
   Check: a scratch world beside the debug world's shapes row with (a) two
   shells in one mesh, a 2 m cube wound outward and a 1 m cube wound inward,
   both showing every exterior wall; (b) a T-junctioned closed box with
   wrap, losing its far side's wrap without `solid: true` and keeping it
   with; (c) a wrapped open card behind an open card, shadowed; (d) a wrapped
   two-sided card alone, lit. Captures on both backends.
3. **`transmit-plane`: the receiver plane.** Remove the 0.2 clamp (floor
   0.02); measure the slope allowance against a curved receiver. Check:
   `world.debug-view shadow` on a flat wrapped plate at n·l = -0.05 in the
   open reads visibility 1 across it on both backends, and the 2 cm slab
   0.10 m from the 3 mm panel at n·l = -0.25 shadows it (the open-items
   case, today speckles).
4. **`transmit-docs`**, folded into whichever lane lands last: the
   [decision](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)
   rewritten for the two layers, the handbook's
   [shadow passage](../rendering/sdf/handbook/lighting-and-shading.md), the
   rendering skill's soft-shadow bullet, the V5 note in
   [visibility](visibility.md#v5-multi-view-and-shadow-work), and the packet.

## The owner's calls

- **Wrap stays thickness-independent.** This design keeps step 1's look: a
  wrapped material's dark side takes the light its wrap carries whether the
  body is a wing or a plate. A physically attenuated transmission (the
  shadow-map thickness above) is a different look and could be a separate
  authored material term later; it is not folded into `wrap`.
- **`solid` is author-facing vocabulary** on a prototype's `mesh`
  (`solid: true | false`). The engine infers it from the triangles when it
  can and the author's word wins.
- **The grazing floor is accepted as lit**: where a blocker lies inside the
  receiver's own per-texel depth span, the term reads the receiver lit rather
  than inventing a shadow. Shadow maps cannot resolve that case; the
  alternative is self-shadow across every grazing wrapped surface.

## References

- Wang and Molnar, "Second-Depth Shadow Mapping", UNC-CS TR94-019, 1994.
  <https://www.cs.unc.edu/techreports/94-019.pdf>
- Everitt, "Interactive Order-Independent Transparency", NVIDIA, 2001.
  <https://developer.download.nvidia.com/assets/gamedev/docs/OrderIndependentTransparency.pdf>
- Bavoil and Myers, "Order Independent Transparency with Dual Depth
  Peeling", NVIDIA, 2008 (a min/max peel gives the farthest layer, not the
  second-nearest, so it does not replace the peel).
  <https://developer.download.nvidia.com/SDK/10/opengl/src/dual_depth_peeling/doc/DualDepthPeeling.pdf>
- Woo, "The Shadow Depth Map Revisited", Graphics Gems III, 1992, pp. 338–342.
- Isidoro, "Shadow Mapping: GPU-based Tips and Techniques", GDC 2006.
  <https://gdcvault.com/play/1013442/Shadow-Mapping-Tricks-and>
- Jacobson, Kavan and Sorkine-Hornung, "Robust Inside-Outside Segmentation
  using Generalized Winding Numbers", SIGGRAPH 2013.
  <https://users.cs.utah.edu/~ladislav/jacobson13robust/jacobson13robust.pdf>;
  Takayama, Jacobson, Kavan and Sorkine-Hornung, "Consistently Orienting
  Facets in Polygon Meshes by Minimizing the Dirichlet Energy of Generalized
  Winding Numbers", 2014. <https://arxiv.org/pdf/1406.5431>
- Barré-Brisebois and Bouchard, "Approximating Translucency for a Fast,
  Cheap and Convincing Subsurface Scattering Look", GDC 2011.
  <https://frostbite.com/frostbite/news/approximating-translucency-for-a-fast-cheap-and-convincing-subsurface-scattering-look>
- Jimenez, Whelan, Sundstedt and Gutierrez, "Real-Time Realistic Skin
  Translucency", IEEE CG&A 30(4), 2010.
  <https://graphics.unizar.es/papers/Real-Time-Realistic-Skin-Translucency.pdf>
- Haar and Aaltonen, "GPU-Driven Rendering Pipelines", SIGGRAPH 2015 (the
  light-space caster cull V5 adopts), cited in [visibility](visibility.md).

Each citation was checked against its hosting page; the Woo chapter was
confirmed by its database and Crossref entries, not read.

---

[Plans](README.md) · [Illumination](illumination.md) · [Visibility](visibility.md) · [Decisions](../decisions/rendering.md)
