# Ink from the field

This memo is the `ink-field` lane of [the ink plan](../ink.md)'s research
round. The [ruling](../../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)
says the engine computes a view's edge signals and authors write what an edge
looks like. Most engines find those edges by comparing neighbouring pixels.
Puck's surfaces come from a signed-distance program, so this memo asks which
edge signals Puck can derive from the field and from the program's structure
instead, how its pipeline would carry each one, what each costs and where each
fails. Its two companions are [the survey of shipped line art](research-shipped.md)
and [the memo on finding edges on the GPU](research-gpu.md). Where this memo
says a line needs a comparison of neighbouring pixels, the second of those is
where that comparison is designed.

It rests on a reading of the current source, on the papers listed under
[Sources](#sources), each of which was opened, and on one small prototype
described under [What was run](#what-was-run). Statements about Puck cite the
file and symbol they were read from. A statement that is reasoned and not read
says so.

## Summary

**The frame has no field to ask.** Puck's renderer does not march rays
([the decision](../../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)).
A background bake samples the field and contours it into meshes, and every view
rasterizes those meshes. The field exists in the bake kernel and in the CPU
evaluator, and nowhere in a frame's pixel kernels. So a signal "from the field"
reaches a pixel in one of three ways: the bake leaves it in the mesh, the
frame computes it from the triangle a pixel hit, or a shade program evaluates
an analytic function at the hit. The signal a ray marcher gets for nothing, how
nearly a ray missed, does not exist in Puck. Its useful half can be computed
another way ([silhouettes](#1-silhouettes-from-near-misses)).

**Lines come in three kinds, and each kind has a different best carrier.**

| Kind of line | Examples | Best carrier |
|---|---|---|
| The level line of a quantity that varies smoothly over the surface | The outline of a rounded form, the light terminator, the middle of a smooth blend, a ripple on water | Analytic: the quantity divided by its slope is the distance to the line. One pixel, no neighbours |
| A jump between two pixels | A sharp crease, the boundary of two parts or two materials, one object in front of another | A comparison of neighbouring pixels, which [the GPU memo](research-gpu.md) designs. The field supplies what is compared: an identity that means "part" |
| A line the program already describes | The seam where two shapes meet, the two edges of a fillet, an authored stripe | The program: the bake records how close each vertex is to the nearest other shape, and a paint draws one shape's surface on another |

**The first kind already works with what a shade program can read.** A
template written for this memo draws a terminator line and an inside outline of
constant pixel width from `surface.curvature`, `surface.footprint`,
`light.cosine` and the normal. It is admitted by the current compiler and its
static cost is about a third of the standard `cel` program's
([what was run](#what-was-run)). It needs one engine change to be correct away
from spheres: the stamp carries mean curvature, and the outline and the
terminator each need the curvature in one direction.

**The second kind needs an identity Puck does not have.** A pixel's identity is
its draw, and a draw is not an object. A large static mesh is cut into chunks
that are separate draws, and a whole static creation is one draw however many
shapes it holds. An edge detector that compares draw identities would ink chunk
seams and miss part boundaries. The program knows which shape won each surface
point, and the bake already tracks the winner to choose a material. A part
identity can travel the same way.

**The third kind is what only a signed-distance program has.** A smooth blend
between two shapes has no crease, no change of material and no jump in depth.
No pixel comparison can find it, and artists draw it: the line where an arm
joins a body. The blend's own arithmetic says where it is.

**Four findings change premises the plan and its companion memos rest on.**

- The engine holds a second built-in ink besides the outline: a line by
  curvature with its own colour (`SdfCurvature`). The ink plan does not list
  it.
- A sharp crease reads zero curvature on both of its sides. The survey's
  capability list offers `surface.curvature` for ridges and valleys on smooth
  forms, which is right, and the limit is strict: a sharp edge is invisible to
  it.
- The survey also lists an object boundary found by distinct draw sources. A
  draw is a chunk or a whole creation, so that comparison draws the wrong
  lines. The GPU memo reaches the same conclusion from the staging side, and
  this memo says how the missing identity can be carried.
- A nested view's outline width is measured in the nested image's pixels, so a
  line seen through a portal can differ in width from the same line seen
  directly.

The memo ends with [steps and their checks](#steps-and-their-checks), the
[questions it leaves and who owns each](#open-questions-and-who-owns-each), and
[three bets](#three-bets).

## How a pixel gets its surface

Five stages touch a surface between the program and the screen. What each one
knows decides what an edge signal can be made from.

| Stage | What it knows | Where |
|---|---|---|
| The program | Every shape, its blend and blend radius, its material, its group, and the slot that moves it | `SdfProgram`, `SdfProgramBuilder` |
| The bake | The field's value and gradient at lattice corners and at each edge crossing; at any point, which shape won and with which material and slot | `passes/sdf-mesh-bake.comp.hlsl`, `sdfComposeCandidate` in `field/sdf-blend.hlsli`, `SdfSampledLattice.GradientPoints` |
| The mesh | Per vertex a position and a normal (`SdfMeshRecord.VertexWords`, six words); per triangle a material (`SdfMesh.TriangleMaterials`); for a skinned group, each vertex's second owner, its share and the share's gradient (`SdfMeshInfluence`) | `SdfMesh` |
| The visibility record | Per pixel, eight words: distance along the ray, identity (the draw), material, triangle, slot, normal, mean curvature with ambient occlusion, and four shadow visibilities | `frame/sdf-visibility.hlsli`, `SdfWorldPackage.VisibilityRecordByteLength` |
| The shade program | The stamp: the record's values plus the pixel's world size at the hit, the ray, the lanes and, in a light's phase, that light | `SdfShadeOps.Inputs` |

All kernel paths in this memo are under
`src/Puck.SdfVm/Assets/Shaders/Sdf/`.

Three facts about these stages matter throughout.

The frame's kernels call no field query. The bake kernel is the one place the
interpreter runs on the GPU, and
[the rendering skill](../../../.claude/skills/rendering/SKILL.md#editing-kernels)
explains why a second call site is not added lightly: each is a full inlined
copy of the interpreter.

A shade program reads only its stamp. It has no buffer address and cannot read
a neighbouring pixel, so anything an ink phase is to know arrives as a stamp
member. Its phases are the members of `SdfShadePhase`, which
`WorldShadeLowering` reads as block names, so an ink phase is a fourth member
with its own outputs.

The mesh a pixel sees changes with distance in two ways. A group is baked again
at a coarser or finer cell as its nearest viewer moves (`SdfMeshTier`: the
coarsest cell under `SdfMeshTier.PixelShare` of a display pixel). And each
baked mesh carries two coarser levels, chosen per draw so that the level's
position error stays within `SdfMeshDetail.CoarserPixels` of a display pixel.
The second rule bounds error, not triangle size. On a sphere of one metre
radius, an error of four millimetres allows triangles eighteen centimetres
wide, so where a pixel is four millimetres those triangles span about
forty-five pixels. That figure is arithmetic from the rule, not a measurement.

## One rule turns a scalar into a line

Take a sphere of radius `R` seen from far away. Let `g` be the cosine between
the surface normal and the direction to the eye: one at the centre of the disc,
zero on the outline. A point where the cosine is `g` appears at distance
`R * sqrt(1 - g * g)` from the disc's centre, so it lies

```text
inside = R * (1 - sqrt(1 - g * g))
```

metres inside the outline. Divide by the world size of a pixel at that depth
and the answer is in pixels. An outline two pixels wide is every pixel whose
`inside` is under two pixels.

Compare the usual shortcut, a fixed threshold on `g`. With a pixel of four
millimetres, the two-pixel outline of a one-metre sphere is where `g` is under
0.126. Apply the same threshold to a sphere a tenth the size and its outline is
0.2 pixels wide. The threshold draws a line whose width follows the radius. The
formula draws one whose width is constant.

A general surface is not a sphere, and near any point the sphere that fits is
the one whose radius is one over the surface's curvature in the plane of the
view ray and the normal. That curvature is called the radial curvature.
Kindlmann and others use the same two quantities for contours of constant
thickness in volume rendering, as Bénard and Hertzmann describe it [2, section
8.4.2]: a threshold on the cosine alone gives a contour that is "thicker on
areas of low radial curvatures", and a function of the cosine and the radial
curvature gives approximately constant thickness.

The general rule is the same idea. Where a quantity varies smoothly over the
surface, the distance from a pixel to the line where it equals some value is
the difference divided by how fast the quantity changes per metre. Divide again
by the pixel's world size (`surface.footprint`) and it is a distance in pixels.
The line needs no neighbouring pixel, it is placed more finely than a pixel,
and its width does not step when the render grid changes. DeCarlo and others
anticipated it for suggestive contours [4, section 3.5]: a fragment shader
"directly comparing the interpolated" curvature to its thresholds "might also
provide better control over the screen-space width of the rendered lines".

The rule fails at a jump. A sharp crease, a part boundary and an occlusion have
no slope to divide by. Those are the second kind of line.

## The six signals

### 1. Silhouettes from near misses

**What the signal is.** A sphere tracer steps along a ray by the distance to
the nearest surface. A ray that misses an object narrowly has still measured
how narrowly: the smallest distance it saw. Hart's sphere tracing paper
reports an earlier tracer that "kept track of the smallest distance encountered
by a ray that misses the object" [6, section 2.6.1], and for antialiasing it
takes the point of a grazing ray "closest to the surface" as the sample for a
silhouette pixel [6, section 3]. An outline falls out of this for nothing: ink
a missed pixel whose near miss is under the line's width.

**What Puck has.** No march, so no near miss. A pixel that misses holds
identity zero and the far distance (`sdfSurfaceStage`), and nothing records how
close the miss was.

**How Puck could get it, and what each way costs.**

- *March the field in the frame.* The decision that removed the frame's
  interpreter rules this out: the cost that remained after every tuning was
  "the fixed price of each program walk". Every pixel near a silhouette would
  walk the whole program several times, and the views kernel would gain a
  second copy of the interpreter. The same decision records that a baked
  distance volume marched by texture reads was built and lost the scene's
  materials, so a volume kept only for near misses would be a second
  representation of the world for one signal. Not recommended.
- *Draw a shell.* Hart's "cover" is the surface moved outward by a pixel's
  radius, and a ray that meets the cover has passed within a pixel of the
  surface [6, section 3]. In mesh terms this is the inverted hull that many
  toon renderers draw. Puck could bake it exactly, since an offset of a
  distance field is another level of the same field. It costs a second mesh for
  every outlined group in bake time and residency, a second raster of those
  draws, and a width fixed in metres. Not recommended.
- *Compute the inside half analytically.* The outline rule above gives, for a
  pixel that hit, how far inside the outline it lies. The current outline
  already draws only on the nearer surface (`sdfToonEdge`, the `step(s.t, t)`
  factor), so the inside half is the half Puck draws. This costs a few
  arithmetic operations per pixel and no reads. Recommended for rounded forms.
- *Search neighbouring pixels.* This is the current method and the GPU
  memo's subject. It remains necessary, for the reason below.

**Where the analytic outline fails.**

- *Sharp silhouettes.* A box's outline is a crease. The cosine never
  approaches zero on its faces and their curvature is zero, so the rule draws
  nothing. Sharp outlines are jumps and need the neighbour comparison.
- *The mesh's outline is not the smooth surface's.* The cosine is read from
  interpolated vertex normals, and the pixel is covered by flat triangles.
  Bénard and Hertzmann describe this disagreement for contours taken from
  interpolated normals [2, section 6.2]: such contours "are not the contours of
  the mesh", and "gaps and other small errors may appear". Of a mesh extracted
  from an implicit surface they say that unless it is highly refined "the mesh
  contours will largely disagree with the smooth contours" [2, section 8.3].
  For a line drawn per pixel the effect is a width that varies along the
  outline by about the triangle's deviation from the surface, which the level
  rule holds under a pixel. It will show on the coarse terrain
  ([below](#where-each-signal-fails)).
- *Mean curvature is the wrong curvature.* The record carries mean curvature
  (`sdfMeshMeanCurvature`: "Half the surface divergence of the interpolated
  unit normal"). On a sphere that equals the radial curvature. On a cylinder
  seen from the side it is half of it, so the rule judges each pixel twice as
  far inside as it is and draws the line at half width. On a saddle it can be
  zero where the radial curvature is not. The surface pass can compute the
  radial curvature from what it already holds: `sdfMeshMeanCurvature` forms the
  change of the normal across the triangle from the three positions and three
  normals, and the change along one direction is a projection of the same
  terms. The view direction is known there, so the surface pass can write the
  radial curvature into the record beside the mean
  ([what that costs](#what-the-pipeline-would-carry)).

### 2. Creases and inner lines from curvature

**What Puck has.** Two things, and they do not meet.

The record's curvature is computed inside one triangle from its three vertex
normals. That is the per-face step of the standard method for meshes, which
Rusinkiewicz builds from "edge vectors" and "normal differences" [8, section
3.1]. Bénard and Hertzmann note of volume data that "simply computing finite
differences over the previously estimated normals would provide a very crude
approximation" [2, section 8.4.2]. Puck's version is less crude than that
warning suggests on a finely baked mesh, because the vertex normals are
measured field gradients interpolated across the lattice
(`SdfSampledLattice.GradientPoints`), not normals estimated from triangles. It
is constant within a triangle, so on a coarser level it steps at triangle
edges.

A sharp crease, though, carries no curvature at all. The contour marks a vertex
whose surface planes turn by more than thirty degrees as a feature
(`SdfContour.Features`) and then gives each face its own copy of the vertex
with that face's normal (`SdfContour.SplitAtCreases`). Each side then keeps its
own face's normals, so the turn across the crease appears in no triangle, and
the crease itself covers no pixel. On a box every face reads zero.
`surface.curvature` therefore sees fillets and misses every sharp edge.
The current outline finds sharp creases as a turn between neighbouring pixels'
normals, which is the right tool. The feature flags are dropped when the
contour lands as an `SdfMesh`.

**What the field could add.**

- *The curvature in every direction, per vertex.* The surface's curvature at a
  point is a function of the field's gradient and second derivatives alone
  (Goldman expects the formulas for implicit surfaces "to depend only on the
  gradient", the Hessian and its adjoint, and derives them [5, section 4]; the
  tutorial's form is the projection of the Hessian onto the tangent plane [2,
  section 8.1]). The bake already measures the gradient at the corners of every
  crossed cell, so the second derivatives are differences of values it holds,
  though no code forms them now. Stored per vertex as three half-floats and
  interpolated,
  this would be continuous across triangles at every level. It costs two more
  words per vertex on a six-word vertex, or nothing if the normal is packed
  from three floats to the sixteen-bit pair the visibility record already uses.
  It has one defect a triangle's own curvature does not: on a skinned mesh it
  is the rest pose's curvature, wrong exactly where a joint bends.
- *The crease's kind and strength, per triangle edge.* The contour knows which
  vertices lie on a crease, and the two faces' normals give the crease's angle
  and whether it is a ridge or a valley. A few bits per triangle edge, stored beside the triangle's material, would let the
  surface pass compute the exact distance from a pixel to a crease its own
  triangle owns, from the barycentric weights it already computes. This is the
  wireframe method of Bærentzen and others, which switches colour where "the
  fragment being drawn is within a given distance of the edge" [9]. It is exact
  and needs no neighbour, but it reaches only one triangle deep. On large flat
  faces that is far enough. Beside a curved surface, where triangles are under
  a pixel, it is not. So it refines a neighbour search and does not replace
  one.

**Recommendation.** Leave sharp creases to the neighbour comparison. Give the
stamp the curvature along a chosen direction from the triangle first, since it
costs no storage and follows a skinned pose, and read whether it steps visibly
on coarser levels before paying for a per-vertex tensor.

### 3. Edges from the program

**What the program knows that a mesh does not.** Which shape a surface point
belongs to, which other shape is nearest to it and how far away, which blend
joined them and with what radius.

**What reaches a pixel.** The draw, the material and the slot. The winner rule
in `sdfComposeCandidate` keeps the material and instance of the winning operand.
A union winner carries its slot. Cuts and intersections keep the subject slot.
The bake writes distance and material alongside the slot or gradient.

**A draw is not an object.** An identity comparison is the obvious way to find
object boundaries, and the record's identity is the draw's ordinal. Two rules
break the equation of draw and object. A static creation bakes as one group
whatever its shape count (`SdfInstanceRange.BakesWithPrevious`), so its parts
share a draw. And a large static mesh is cut into chunks along a grid whose
cell starts at `SdfMeshChunks.Side` metres, each chunk a draw of its own, so
one terrain sector is up to `SdfMeshChunks.MostChunks` draws. A detector
comparing identities would draw a grid of lines across the terrain and none
between two shapes of one creation, such as a statue modelled with its plinth.
[The field programs plan](../field-programs.md#what-it-costs-today) lists what
the bake drops, and the identity of the winning shape belongs on that list.

**A part identity.** The fix is an identity that means what an author means: a
number a shape, a group or a creation declares, whose neutral default is none,
so a world that declares nothing gains no lines and no triangles. It rides the
winner rule as the material does. The bake already samples the winner at every
vertex and every quad's quarter centres to place material boundaries
(`SdfMeshMaterialCut`), and it does so in the sample mode that leaves two of
its five words empty. The mesh-bake kernel takes one field sample in that mode
and leaves both gradient words zero.
[Mesh-bake kernel](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-bake.comp.hlsl).

A part and one more value fit in words three and four with no new dispatch and
no larger sample. The material cut can then place part boundaries on mesh edges
as exactly as it places material boundaries, where an author asked for a line.
The cost is real and is paid once: the bake kernel's bytecode keys every stored
mesh (`SdfMeshBakeIdentity`), so every stored mesh is baked again, and the CPU
evaluator must track the same winner (`SdfFieldEvaluator`). The draw record has
no free word (its sixty-four are assigned in `SdfMeshRecord`), so an identity
shared by a group's chunks has to share a word, such as spare bits of the flags
word. How many are spare was not counted.

**The seam of a smooth blend.** This is the line a mesh renderer has no way to
find.
Puck's smooth union weighs its two operands by a share
(`isa/sdf-field-blend-formulas.hlsli:7`, generated from `SdfFieldFormulas`):

```hlsl
    precise float h = clamp((0.5 + ((0.5 * (b - a)) / k)), 0.0, 1.0);
```

The share is one half exactly where the two shapes are equally near, which is
the middle of the fillet. It reaches zero and one where the fillet ends. So the
difference of the two distances, which this memo will call the margin, is zero
on the seam's centre line and equals the blend radius at the fillet's two
edges. For a hard union the margin on one shape's surface is simply the
distance to the other shape, and it is zero on the seam. The margin is the
"one more value" for the sample's spare word. It varies smoothly, so the rule
for smooth quantities draws the centre line, or the fillet's two edges, at a
constant width.

Two existing mechanisms show this is not new machinery.

- The groove and pipe blends already compute the distance to the curve where
  two surfaces cross, as `length(float2(current, candidate))` in `blendShape`.
  The field's vocabulary has held a seam distance all along.
- A skinned group already stores the share and its gradient per vertex on the
  GPU. `SdfFieldEvaluator.ReadInfluences` reads owner shares "through the same
  field composition and scopes as the render point reading", for any mapping of
  shapes to owners, and `SdfMeshSkin.Build` packs the second owner, the share
  and the gradient beside each vertex (`SdfMeshRecord.SkinVertexWords`). For a
  moving blend, the line where a limb joins a body can be drawn from data that
  is resident now.

**Where the margin fails.** It is a per-vertex value read by interpolation, so
it is only as good as the tessellation. A fillet is curved and the bake refines
where the surface turns, so a smooth blend is well sampled. A flat face is
merged into a few large triangles (`SdfMeshPlanarMerge`), and a triangle that
spans from one seam to another would interpolate a false line of zero margin
along its edge. So the margin is for smooth blends. Hard seams are jumps of
part identity and belong to the neighbour comparison.

**A seam as a paint.** A palette row's paints draw a primitive's surface on
whatever it crosses. In `sdfApplyPaints` a paint's shape is "evaluated at the
hit through the one shape dispatch (evaluateShape) and never through the
field", and it colours the pixels "within its stroke of the shape's surface,
the edge antialiased over `footprint`". An author can therefore draw the seam
between two shapes today by painting one shape onto the other's material. That
line is exact, it rides the creation's frame, and it does not depend on the
bake's cell, since the paint is evaluated at the hit and not at vertices. It
suits the few seams an author cares most about. It does not scale to every
seam, because every pixel of the material evaluates every one of its paints,
and only the primitives `SdfPaintShape.Of` admits can be painted.

### 4. Suggestive contours, and lines that hold still

**Suggestive contours.** These are the lines an artist adds inside a form where
no outline exists yet: "where a true contour would first appear with a minimal
change in viewpoint" [3, abstract]. Formally they are where the radial
curvature is zero and increasing toward the viewer, or equivalently where the
cosine between the normal and the view is a local minimum along the view's
direction on the surface [3, sections 2.1 and 2.2].

A signed-distance source helps here: Burns and others extract them directly
from an implicit function, from the same normal and its derivative [1, section
4.1]. Per pixel, the recipe is the smooth-quantity rule applied to the radial
curvature. DeCarlo's interactive method passes the radial curvature and its
derivative as texture coordinates per vertex and lets the rasterizer
interpolate them [4, section 3.5]. Puck would need the per-vertex curvature
tensor of the previous section, since the quantity whose zero is sought must be
continuous across triangles.

Three things argue against building this first.

- It needs the derivative of curvature, which is a third derivative of the
  field. The derivative test exists because "spurious zero crossings" appear "from noise" [3, section
  3.1], and the method needs two thresholds and a fade to be watchable [4,
  section 3.4].
- The lines are not stable. Some regions are "fleeting: they move quickly over
  the surface for small camera changes" [4, section 1].
- They are a minority of the lines people draw. In Cole's study, artists' lines
  are mostly "described by well-known, simple mathematical properties, such as
  the locations of occluding contours and large gradients of image intensity"
  [10, section 1].

The image-space form of the definition, a valley of the cosine, is a neighbour
computation over the record's normal word, so it belongs in the GPU memo's
pass if it is wanted sooner.

**Holding still.** The ink plan wants a line's breakup to hold still in world
space. Whether that is possible depends on the line, not on the engine.

A line that does not depend on the view is fixed on the surface: a crease, a
seam, a part or material boundary, a paint. Bénard and Hertzmann say such lines
"can leverage the underlying surface parameterization" [2, section 9.5]. Puck
needs no parameterization. `surface.layerPosition` is the hit in the creation's
own frame, and `lattice` is a solid noise of a point, so a breakup pattern
taken there rides the creation and never slides. This is authorable now.

A line that depends on the view cannot hold still, because it is not fixed to
the surface. "View-dependent lines such as contours move on the surface and
their geometry and even topology change from frame to frame" [2, section 9.5].
A pattern glued to the surface stays still while the outline sweeps across it,
so the outline's dashes change as the camera moves. A pattern glued to the
screen stays still under the outline while the object slides beneath. The
tutorial calls the general problem "mostly open" [2, section 9.5]. The stamp
already offers each anchor an author might choose: the creation's frame
(`surface.layerPosition`), the world (`surface.position`), the normal, and the
ray, which is the screen. The engine's part is to tell the ink phase which kind
of line it is shading, so a style can pick an anchor per kind.

One limit is real. A per-pixel ink has no coordinate along the line, so it
cannot taper a stroke from end to end or lay a brush texture along it. The
tutorial notes the same of the nearest image-space method, which "does not
support a brush stroke texture that curves around the object" [2, section 2.1].
What a pixel can read is strength: a crease's angle, a blend's margin, the
curvature. A line that thins where its crease flattens is a taper by strength,
and those values are the ones to expose.

### 5. The light terminator

**What Puck has.** A light's phase reads `light.cosine`, the cosine between the
normal and that light, and `light.visibility`, its filtered shadow. The
standard `cel` template steps the cosine at a threshold. The terminator is the
level line of the cosine at that threshold.

**How to draw it.** The cosine varies smoothly on a curved surface. It changes
by the surface's curvature along the light's direction, times the sine of the
angle to the light, per metre. So the smooth-quantity rule applies with stamp
members a light phase can already read, and the prototype does exactly that
([what was run](#what-was-run)). No engine change is needed for a first
version. The same directional curvature that the outline needs makes it right
on forms that are not spheres, here along the light and not the view. Lights
are walked in the views pass, after the record is written, so this direction is
not known to the surface pass. The views stage has the pixel's triangle
(`sdfViewsStage` resolves the hit) and can form the change of the normal there,
at the cost of reading the triangle's three vertices again, for pixels whose
program asks. A read flag of the kind `SdfShadeReads` already holds for the
footprint and the lanes keeps other pixels from paying.

The GPU memo finds the same line another way: it brackets the cosine between
two neighbouring pixels and interpolates the crossing. The two agree on a
curved surface. The analytic form needs no neighbour and no extra pass. The
bracket also finds the terminator on a faceted form, where the analytic form
has nothing to divide by.

**Where it fails.**

- *Flat and faceted forms.* A flat face has one cosine. Its terminator is the
  crease where a lit face meets an unlit one, which is a jump.
- *Cast shadows.* The edge of a cast shadow is a level line of
  `light.visibility`, and that value has no slope a pixel can compute alone.
  Its neighbours are already at hand, though: the unbinned views kernel gathers
  each pixel's shadow words over a four-by-four window into group memory
  (`sdfViewsGatherFilter`), and the binned frame's filter pass does the same
  gather. A shadow-edge slope computed there costs arithmetic and no new reads.
  It needs a place in the filter record, whose two words are full.
- *Shadow demand.* A program whose light response is not provably zero on the
  far side makes the surface pass query the cascades on every side
  (`sdfMaterialShadowDemand`). The prototype reported a demand of minus one,
  which means every side, and so did `cel`.

### 6. Glass and water

**What Puck has.** Glass is a pass of its own. It keeps, per pixel, the nearest
glass front face (its distance, draw and triangle) and integer sums of the
glass the ray crosses before the opaque hit. Its shading runs the same phases
as opaque shading on up to two interfaces, and its stamp has two members opaque
shading lacks: `surface.thickness`, the glass the ray crosses, and
`surface.contact`, from the distance between the interface and the opaque
surface behind it (`passes/sdf-glass-stage.hlsli`, the `sdfShadeStamp` call).
The current outline is not called there, so glass has no ink.

**What can be drawn with no neighbour.**

- *The rim of a glass body.* The outline rule works on the near interface
  unchanged: the glass stage computes the interface's normal and curvature
  through the same `sdfMeshSurfaceNormal`.
- *A rim by thickness.* For a closed body the glass the ray crosses falls to
  zero at the outline. On a sphere the chord `c` and the distance inside the
  outline are related by `inside = c * c * curvature / 8` near the edge, so
  thickness gives a second estimate of the same line on a rounded body. This is
  arithmetic from the sphere, not a reading. On a flat pane the thickness is
  constant up to the edge, and the rim is a jump.
- *The shoreline.* Where water meets its bed, `surface.contact` goes to zero.
  The standard water already raises foam from it. As a line of constant width
  it needs the angle between the water and the bed, and the bed's normal is in
  the opaque record at the same pixel.
- *Ripples and foam lines.* These are level lines of the flow's noise. The
  `latticeGradient` op returns that noise's slope, so the smooth-quantity rule
  draws ripples of constant pixel width with operations that exist. A flow
  moves shading and never geometry, so these lines cost no bake.

**What needs neighbours.** A glass body in front of another, and a glass edge
against an opaque one behind it, are jumps. The glass target holds the draw and
triangle of the nearest front face at the render grid, so the comparison has
its inputs for the near interface. The second interface is reached only
through the sums' record (`sdfGlassSecondInterface`).

**What comes for free.** Ink drawn on opaque surfaces is in the lit image
before the glass pass reads it, so lines seen through water bend and blur with
the refraction. In a temporal view the glass pass already raises a pixel's
reactivity where the refracted read lands a pixel or more away, so those lines
are not smeared by history.

## What the pipeline would carry

| Carrier | Holds now | Would hold | Cost |
|---|---|---|---|
| The bake's sample (five words) | Value, material, slot or gradient | In the material-sampling mode's two empty words: the part and the margin | No new dispatch. One rebake of every stored mesh. A second tracked value in the winner rule and its CPU twin |
| The mesh, per triangle | A material | The part; optionally crease kind for each edge | Bits in the material word, which `sdfMeshMaterialOf` adds whole today, or a second word per triangle at every level (`SdfMeshLevel`) |
| The mesh, per vertex | Position, normal; for a skin, second owner, share and gradient | Optionally the margin; later, a curvature tensor | One word per vertex for the margin. Two for the tensor, or none if the normal is packed |
| The visibility record (eight words, all used) | Mean curvature in a half-word | Radial curvature beside it | The mean has readers (edge weathering, the terrain's bands), so both must fit: two coarser values in the half-word, or a ninth word, which is about fifteen megabytes for one view at 2560 by 1440 |
| The views pass | Shades from the record; resolves the pixel's triangle | Curvature along a light, for programs that read it | Three vertex reads per such pixel, behind a read flag |
| The filter pass and bins (binned frames) | Filtered occlusion and shadows; each tile's bin | The shadow edge's slope; whether a tile holds any edge | A wider filter record. The tile test is the GPU memo's |
| The shade program | Three phases, a stamp | An ink phase and edge members in the stamp | Admission prices a phase for every pixel of a group (`SdfShadeProgram.MaxWork`). The phases are straight-line, so "run only where an edge is near" must be a test around the phase, not inside it |

## Where each signal fails

| Signal | Dynamic resolution | Temporal reprojection | Coarse terrain | Nested views and portals |
|---|---|---|---|---|
| Analytic outline | Holds: width is a division, not a tap radius | Slides over the surface, so history disagrees until clipped | The outline's shape is the mesh's, faceted at the cell | Width is in the nested image's pixels |
| Analytic terminator | Holds | Fixed while the light and the surface are still | Curvature finer than the cell is absent | Same |
| Sharp crease, part and material boundaries (by neighbours) | Quantized to the render grid | Fixed on the surface, reprojects cleanly | Creases under the cell are absent; part boundaries are placed to a thirty-second of an edge | Same |
| Blend margin | Holds | Fixed on the surface | Sampled at the cell; a seam finer than it is lost | Same |
| Paint seam | Holds | Fixed on the surface | Independent of the cell; shifted where the mesh is off the surface | Same |
| Suggestive contour | Holds | Fleeting by nature | Not usable | Same |
| Glass rim, shoreline | Holds | Refracted lines get no history | The bed's cell moves the shoreline | Same |

**Dynamic resolution.** `surface.footprint` is the world size of one pixel of
the grid the views pass runs on (`sdfPixelAt`), which is the render grid. A
width written against it is in render pixels, so it doubles on screen when the
render grid halves. The current outline corrects for this by scaling its
authored width by the grid (`SdfFrameBlock.Write` passes
`SdfViewSnapshot.RenderGrid` into the outline's style rows). An ink phase needs the
same correction made once, in the footprint it is given. Below one render
pixel, a line cannot be drawn thinner, only fainter, which is what the current
code does for widths under one.

**Temporal reprojection.** History follows the surface point
(`sdfPreviousPoint`). A line fixed to the surface is therefore in the same
place in history and accumulates like any texture. A stationary view keeps a
window of every jitter phase, so a line thinner than a render pixel converges
to its true coverage when nothing moves. A line that depends on the view moves
over the surface, so history holds no line where the line now is, and the
resolve's neighbourhood clip has to remove the old one. The views pass already
writes a reactivity for pixels whose history is not to be trusted, and an ink
phase could raise it where it draws a view-dependent line. How a thin dark line
survives the clipped resolve in motion is not something reading settles. It is
a [question for a capture](#open-questions-and-who-owns-each).

**The coarse terrain.** The terrain plan's
[reading of a sector](../terrain-pass.md#what-set-a-sectors-cell) is a cell of
two and a half to three and a half metres where the relief asks for one and a
half. Everything borne by vertices is that coarse there: normals, curvature,
margins. A group's replacement by a finer bake changes those values at once,
so a line placed by them jumps when the new mesh takes the draws. Two things do
not depend on the cell. A paint is evaluated at the hit. And a shade program
can evaluate the relief's own noise at the hit, which the terrain plan already
names as open work (its
[detail relief](../terrain-pass.md#slope-altitude-and-curvature-materials)).
The slope of that noise gives ridge and gully lines finer than any mesh the
bake will produce there. Nothing can sharpen the terrain's outline against the
sky except the geometry.

**Nested views and portals.** A signal measured in metres on the surface is the
same through a portal as seen directly, which is an advantage of every signal
in this memo over a pixel filter. Widths are the problem. The only
view-dependent factor in the current width is the render grid, so a line
authored two pixels wide is two pixels of the window's own image. That image is
then resampled to the size its glass shows. Its extent follows that size only
in quantized steps, can lag a moving camera, and is reduced where the world's
nested-view table says so (`WorldRenderSettings.NestedRestriction`). So a body
standing half through a portal, which two views draw, can change outline width
at the glass. This is a reading of the code and was not captured. The engine
already computes the right unit for another purpose: for a window fitted to its
glass, `SdfViewSnapshot.DemandHeight` is the display pixels the glass spans
from the viewer's eye, kept so that the bake's demand is measured as the viewer
sees it. A footprint for ink should be measured the same way. The survey
reaches the same unit from the authoring side: widths in display pixels or in
metres. Two smaller points: a screen's or portal's picture is never inked again by
the view that shows it, which the current code already holds, and a draw cut by
a portal's plane carries that plane in its record
(`SdfMeshRecord.ClipPlaneWord`), so an ink phase can be told how near the cut a
pixel is and draw nothing there.

**Levels and skins.** Curvature taken from one triangle is constant within it.
On a coarser level a triangle can span many pixels
([above](#how-a-pixel-gets-its-surface)), so a width that depends on curvature
may step at triangle edges. A per-vertex tensor would not step, and would be
wrong on a bent skin. Which defect shows more is a question for a capture.

## What was run

One question could be settled without a GPU: whether the smooth-quantity lines
can be written with the stamp as it stands, and what admission charges for
them.

A scratch world placed one sphere under this template and was checked with the
lane's build of the CLI (`puck compile --validate`):

```text
function inkPixels(value, slope) = abs(value) / max(slope * surface.footprint, 0.0000001)
function inkLine(pixels, width) = 1 - smoothstep(max(width - 1, 0), width, pixels)

shade template inkProbe(threshold, width, inkColor) {
  light {
    let sine = sqrt(max(1 - light.cosine * light.cosine, 0))
    let terminator = inkLine(inkPixels(light.cosine - threshold, abs(surface.curvature) * sine), width)
    diffuse = light.color * (light.weight * light.reach * max(light.cosine, 0) * light.visibility * (1 - terminator))
  }
  compose {
    let g = saturate(-dot(surface.normal, surface.rayDirection))
    let inside = (1 - sqrt(max(1 - g * g, 0))) / max(abs(surface.curvature), 0.0000001)
    let contour = inkLine(inside / max(surface.footprint, 0.0000001), width)
    let breakup = smoothstep(0.35, 0.65, lattice(surface.layerPosition * 40, uint(7)))
    let lit = surface.albedo * (compose.radiance + compose.indirect) * compose.attenuation * compose.adaptation
    color = mix(lit, inkColor * lit, contour * breakup)
  }
}
```

What it showed:

- The template is admitted. The same file with a stamp member that does not
  exist is refused by name (`PUCK126`), so the check does run admission.
- A headless World booted on it reports, on the first line of `world.budget`,
  a worst static cost of 1,499,440 for `inkProbe`. The same world with the
  sphere under the standard `cel` reports 4,627,619 for `cel`. Both are far
  under `SdfShadeProgram.MaxWork`.
- Both report a shadow demand of minus one.

What it did not show: a pixel. No windowed World was started, so the line's
look, its width on screen and its behaviour in motion are unverified. That
check is requested [below](#open-questions-and-who-owns-each).

What it decided: a terminator line and an inside outline are authored material
in the standard package, not engine code, and the engine's first job for them
is the right curvature.

## The invariant

An edge signal is a function of the surface point, the view, the light and the
program. It is never a function of the render grid, of which draw or chunk a
triangle fell in, or of which of two views is looking.

What enforces it for the analytic signals is the stamp. A shade program reads
only the members `SdfShadeOps.Inputs` lists and has no way to address a buffer
or a neighbour, so an ink phase cannot depend on anything the engine did not
choose to hand it. If the stamp's edge members and its footprint are defined in
metres and display pixels, every program inherits the property.

Where the guarantee stops: a signal found by comparing neighbouring pixels
depends on the grid by construction, and this memo does not design that pass.
The bake's values are sampled at vertices, so they hold only to the mesh's
resolution. And nothing yet enforces that a comparison uses the part and not
the draw. That is a rule for the neighbour pass to make structural, for
example by giving the comparison a word that holds only the part and the
material.

## Steps and their checks

Each step names a check a machine can run. None is built.

1. **Give the stamp curvature along a direction.** The surface pass writes the
   radial curvature, and the views stage forms the curvature along a light for
   programs that read it. *Check:* a device law, in the manner of
   `SdfReprojectionDeviceLawTests`, shades a triangle of a cylinder of known
   radius and reads one over the radius across the axis and zero along it, on
   both backends. In a capture, the template above draws the same width on a
   sphere and on a capsule's side. With mean curvature the capsule's line is
   half as wide.
2. **Give ink one footprint, in display pixels.** The footprint an ink phase
   reads accounts for the render grid and, in a nested view, for the size its
   image is shown at. *Check:* a line's width in output pixels is the same at
   render scales of one and one half, and a body standing through a portal
   shows one width on both sides of the glass, each within a pixel.
3. **Carry a part through the winner rule.** A shape declares a part, the
   winner rule tracks it, the material sampling returns it, and the mesh holds
   it per triangle. *Check:* `puck mesh audit --cpu` on a chunked static union
   counts no part change across a chunk seam and one along each declared
   boundary. `puck mesh identity --against` names the bake kernel as what
   differs between the two builds. A world that declares no part bakes the same
   triangle counts as before.
4. **Carry the margin for smooth blends.** *Check:* on one of the gallery's
   smooth-blend exhibits, the mesh audit reports a zero margin within one cell
   of the surface points where the two shapes' distances are equal, measured
   against the CPU field.
5. **Add the ink phase and its stamp members.** This is the synthesis's step.
   From this lane it needs: the line's kind (fixed to the surface or dependent
   on the view), the footprint of step 2, the curvature of step 1, the margin,
   and the distance to the draw's clip plane. *Check:* the shade spelling pairs
   and `ShadeLoweringTests` admit a four-phase program, and a program with no
   ink phase compiles to the words it has now.

Steps 1 and 2 are independent of everything else and of each other. Steps 3 and
4 share one edit to the bake kernel and should land together, in the batch of
kernel edits [the terrain plan](../terrain-pass.md#order-of-work) already
waits for, so the stored meshes are baked again once.

## Open questions and who owns each

| Question | Owner | What would show it | What it decides |
|---|---|---|---|
| Does the template's line look right, and hold its width? | The lead's windowed pass | A world with a sphere and a capsule under the template from [what was run](#what-was-run), settled stills from three distances with the exposure pinned | Whether step 1 is needed before the standard package ships such a line |
| Does curvature from one triangle step visibly on coarser levels? | The lead's windowed pass | The same world under `world.detail 0`, `1` and `2`, stills of the terminator line | Per-triangle curvature, or the per-vertex tensor |
| Does a line one or two pixels wide survive the temporal resolve in motion? | The lead's windowed pass | A presented burst of eight frames inside a sustained pan, with `world.temporal on`, at render scales of one and one half | Whether a view-dependent line raises reactivity, and the smallest width the standard offers |
| What does re-reading the triangle in the views stage cost? | The lead's quiet window | `world.counters gpu pass sdf.world$views` and `world.gpu-timing` before and after step 1, on the benchmark route | Curvature along a light in views, or a tensor in the record |
| Which lines are drawn analytically and which by the neighbour pass, where both can? | The synthesis | This memo beside [the GPU memo](research-gpu.md), which designs the neighbour pass and prices it | The terminator and the rounded outline are the two lines both can draw. The neighbour pass is needed in any case for jumps |
| Which signals does the standard package need first? | The synthesis | [The survey](research-shipped.md)'s capability list | The order of steps 1 to 4 |
| Does a body cut by a portal draw a crease line at the glass today? | The lead's windowed pass | A still of a body standing through a gallery portal with the outline on | Whether the clip distance in step 5 is a fix or only a control. This memo did not confirm the defect, it only reasons that the body's normal and the slab's differ there |

## Systems considered

Each mechanism below was read before proposing anything beside it.

| Capability | Existing mechanism | Verdict |
|---|---|---|
| An outline from depth and normal jumps | `sdfToonOutline` and `sdfToonEdge`, `shade/sdf-toon-outline.hlsli` | The neighbour detector the ink plan replaces. It ignores identity, so it does not ink chunk seams today |
| A line by curvature with its own colour | `applyCurvatureShading`, fed by `SdfCurvature` from `render.lighting.curvature` | A second built-in ink the plan should remove with the first. An authored phase reading `surface.curvature` replaces it |
| Curvature at a pixel | `sdfMeshMeanCurvature`, written to the record by `sdfSurfaceStage` and read as `surface.curvature` | Extended by step 1, not duplicated |
| Sharp features in the contour | `SdfContour.Features`, `SdfContour.SplitAtCreases`, the collapse's feature weights | Holds what a crease flag would need. Nothing carries it to the mesh |
| Which shape won a point | `sdfComposeCandidate`, and `SdfFieldEvaluator` on the CPU | Extended by step 3 |
| Exact boundaries on the mesh | `SdfMeshMaterialCut` | Reused for part boundaries |
| The share of each owner at a point | `SdfFieldEvaluator.ReadInfluences`, `SdfMeshSkin.Build`, `SdfMeshInfluence` | Already resident for skinned groups. Its per-vertex CPU walk is too costly to run on every static vertex, which is why step 4 uses the bake's sample |
| The distance to a seam | The groove and pipe arms of `blendShape` | The same quantity as the margin |
| A crisp line on a surface from a shape | `sdfApplyPaints`, `SdfPaintShape.Of`, and the `shape` op | The way to author a seam now |
| A line of constant width from noise | The `lattice`, `latticeGradient` and `lines` ops in `SdfShadeOps.Table` | Used as they are |
| A neighbourhood already gathered | `sdfViewsGatherFilter` and the binned filter record | Where a shadow-edge slope would be computed |
| Distrusting history at a pixel | The reactivity the views and glass stages write | Used as it is |
| Cost that only readers pay | `SdfShadeReads` | The model for step 1's flag |

## Three bets

**1. Lines from scalars before lines from pixels.** The outline of a rounded
form and the light terminator are level lines, and the stamp can already draw
them at a constant width for a third of what `cel` costs. Give the stamp
curvature along a direction, taken from the triangle the passes already read,
and ship both lines as programs in the standard package. They need no
neighbour, no new buffer and no bake. The risk is that curvature from one
triangle steps on coarser levels, and a capture settles that before more is
spent.

**2. A part, not a draw.** Whatever the neighbour pass becomes, it must not
compare draw identities, because a draw is a chunk or a whole creation and
only by accident a part. Carry a part and a seam margin through the winner
rule, in the
two words the bake's material samples leave empty. This gives the neighbour
pass the identity the ruling's "object and material boundaries" assumes, and it
gives authors the one line only a signed-distance program can find, the seam of
a smooth blend.

**3. One unit for width.** Measure ink in display pixels at the surface, with a
footprint the engine supplies per view. A width is corrected for the render
grid now, and not for the size a nested view is shown at. One footprint makes a
line the same width at any render scale, through a window, and on both sides of
a portal. It is the smallest of the three changes. The survey asks for the same
unit, and the GPU memo asks for output pixels and metres, which is the same
thing in a view that is not nested.

Suggestive contours and a per-vertex curvature tensor are not among the bets.
Both are real advantages of a field, both are costly, and the evidence is that
most of what artists draw is found without them.

## Sources

Each was opened and read for the passages cited.

1. M. Burns, J. Klawe, S. Rusinkiewicz, A. Finkelstein and D. DeCarlo, "Line
   Drawings from Volume Data", ACM Transactions on Graphics 24(3), 2005.
   <https://pixl.cs.princeton.edu/pubs/Burns_2005_LDF/volines.pdf>. Read for
   contours and suggestive contours taken from an implicit function and its
   normal.
2. P. Bénard and A. Hertzmann, "Line Drawings from 3D Models: A Tutorial",
   Foundations and Trends in Computer Graphics and Vision 11(1-2), 2019.
   <https://arxiv.org/abs/1810.01175>. Read for image-space lines and their
   limits (chapter 2), contours from interpolated normals (section 6.2),
   implicit surfaces and meshes extracted from them (chapter 8, including
   Kindlmann's thickness control) and coherence in animation (section 9.5).
   Kindlmann's own paper could not be fetched, so its result is cited as this
   tutorial reports it.
3. D. DeCarlo, A. Finkelstein, S. Rusinkiewicz and A. Santella, "Suggestive
   Contours for Conveying Shape", ACM Transactions on Graphics 22(3), 2003.
   <https://gfx.cs.princeton.edu/pubs/DeCarlo_2003_SCF/DeCarlo2003.pdf>.
4. D. DeCarlo, A. Finkelstein and S. Rusinkiewicz, "Interactive Rendering of
   Suggestive Contours with Temporal Coherence", NPAR 2004.
   <https://gfx.cs.princeton.edu/pubs/DeCarlo_2004_IRO/npar04sug.pdf>.
5. R. Goldman, "Curvature formulas for implicit curves and surfaces", Computer
   Aided Geometric Design 22, 2005.
   <https://www.cgeo.uliege.be/CAO/Goldman_Curvature_formulas_implicit.pdf>.
6. J. C. Hart, "Sphere Tracing: A Geometric Method for the Antialiased Ray
   Tracing of Implicit Surfaces", The Visual Computer 12, 1996.
   <https://graphics.stanford.edu/courses/cs348b-20-spring-content/uploads/hart.pdf>.
7. T. Ju, F. Losasso, S. Schaefer and J. Warren, "Dual Contouring of Hermite
   Data", ACM Transactions on Graphics 21(3), 2002.
   <https://people.engr.tamu.edu/schaefer/research/dualcontour.pdf>. Read as
   background on how a contour from edge crossings and normals reproduces sharp
   features. Puck's contouring is its own and no claim above rests on this
   paper.
8. S. Rusinkiewicz, "Estimating Curvatures and Their Derivatives on Triangle
   Meshes", 3DPVT 2004.
   <https://pixl.cs.princeton.edu/pubs/Rusinkiewicz_2004_ECA/curvpaper.pdf>.
9. J. A. Bærentzen and others, "Single-pass Wireframe Rendering", SIGGRAPH 2006
   sketch. The project page was read, not the paper:
   <https://people.compute.dtu.dk/janba/Wireframe>.
10. F. Cole and others, "Where Do People Draw Lines?", ACM Transactions on
    Graphics 27(3), 2008.
    <https://gfx.cs.princeton.edu/pubs/Cole_2008_WDP/cole_2008_wdp.pdf>.
11. T. Judd, F. Durand and E. Adelson, "Apparent Ridges for Line Drawing", ACM
    Transactions on Graphics 26(3), 2007.
    <https://people.csail.mit.edu/tjudd/apparentLines.pdf>. Read as a second
    view-dependent line definition. Like suggestive contours it needs a
    curvature that is continuous across triangles and its derivative, so the
    same verdict applies.
