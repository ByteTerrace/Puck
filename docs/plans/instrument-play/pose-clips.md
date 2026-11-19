# Pose clips, body parts and reach

This is the design for three rig gaps the
[instrument and dance plan](../instrument-play.md#the-rig) names: poses that
do not blend and layers that cannot be masked (gap A), an effector that cannot
aim at its own body (gap B), and a press that reaches the rig a tick late
(gap C). It also names the shape mask once, as a body part, for every later
[play expression](../play-expressions.md#what-the-bards-first-wave-should-take-from-this),
and decides what happens to the rig's caps.

Steps 1 to 8 are built: the compiled rig (`CreationRig`), the one view the
pose pass reads through (`WorldRigReads`), the removed caps, parts as sets
(`CreationPartCompiler`), adding clips (`CreationClipCompiler`,
`VectorFunctions.Arc`), masks and replacing (`CreationRig.Share`,
`CreationMotion.ClipLimit`), reach and influence
(`CreationRig.EffectorShare`, the `shape` target, a body named by a cell), and
the guess (`WorldSeatInput.TryLead`, `WorldInputAnswer`). The rest of
the page is a design, and its [steps](#steps-each-with-its-check) are the build
lane's order. The sections below keep the reasoning each built step rests on. A reviewer
from another model family attacked the first version with twenty-one
counterexamples. This version changes the design where they held, and
[one table](#the-review-finding-by-finding) answers each. The plan's research
round has not reported, so [one section](#what-the-research-round-could-change)
says which choices it could overturn.

## Summary

- **The swing cap already binds.** The Moth's two thighs and her pelvis use
  all 8 of the swings a shape may carry, and her upper arms and torso use 7. A
  dance move cannot add one swing to a leg today.
- **A pose clip is key poses on joints.** Each key gives a shape a turn about
  its joint, written as an axis and an angle and measured from rest. Keys
  interpolate, and a driver plays the clip: the driver's phase is the position
  in the clip and its weight is the blend.
- **A driver becomes a layer.** It gains a mask of body parts and a mode. A
  replacing layer fades out the layers declared before it, on its parts only,
  by its own weight. Declaration order decides only those weights. The order
  in which turns compose on a shape is written on the shape and its clips, and
  no layer changes it.
- **A body part is a named set of shapes,** defined once in the creation and
  used by every mask. It extends the `parts` list a creation already has.
- **The rig is compiled once, and every reader takes the compiled form.** It
  lists what moves each shape (drivers, effector solves, an aligned tip,
  frames, followers), what the pose reads from outside, and how far the pose
  can reach. Slot ownership, the seam bound, the wake test, delivery to remote
  clients and the rate tiers all read that one list. Today each asks the
  document its own question, and they disagree.
- **An effector gains two targets:** a point on a shape of its own creation,
  and a body whose index is read from a state cell, optionally at one of that
  body's parts.
- **An effector stays a final constraint.** It solves after every layer, in
  the order effectors are declared. A layer sets how strongly it pulls, down
  to nothing, and a foot plant yields to a dance move without a rule.
- **The seat's own press can show early, as a guess.** This is narrower than
  "the screen reads the channel early", which cannot be made safe. While one
  of a seat's own inputs has been sent and not yet answered, that seat's
  screen may show the pose the input asks for. The guess ends when the
  authority's answer is presented, whatever the answer is. It runs only on a
  client that presents for one seat, so no other viewer can see it. It starts
  at the tick that takes the press in, not at the press itself.
- **The caps on swings, slides, drivers and effectors are removed.** None
  protects a representation. Counts are reported, and work is counted. The
  mesh format's limit of 255 moving members in one group is a separate limit
  that stays, and is reported when the document compiles.
- **Fingers and wrists get joints.** A clip needs a joint on a shape and no
  swing, so the Moth's hand can become segments on pivots.
- **Timeline frames stay.** Turning them into clips needs three things this
  page does not design, so that step is withdrawn.

## Where the rig stands

### The caps, and what uses them

Before step 3 a shape carried at most 8 swings and 4 slides, and a creation
32 drivers and 8 effectors. The Moth's thighs and pelvis used all 8 swings,
her upper arms and torso 7, and she declares 18 drivers and 2 effectors (a
count of `swing(` calls and list entries in
[her document](../../../worlds/debug/avatars/moth.puck)). Step 3 removed those
caps. A chain keeps its 8-bone limit (`CreationEffectorDocument.MaxChainBones`).

Her hand has no joints. The palm, the wrist and each finger are separate
shapes parented straight to the forearm, and a finger is one swept curve.
Nothing in the hand can bend.

### How a pose can be held today

| Mechanism | Blends | Layers | What stops it |
|---|---|---|---|
| A timeline frame (`FrameDocument`) | No: one frame holds at a time | No | A frame stores absolute poses, so a child does not follow a posed parent, and a held frame cannot sit on a moving torso |
| A look's `poses` and `cues` (`WorldLookMotion`) | No | No | They select a frame |
| A `constant` swing under a weighted driver (`CreationWave.Constant`) | Yes, by the driver's weight | Yes, by adding | One driver and one swing a shape for every pose, against the caps above |
| A swing whose wave is a `curves` row (`CreationWave.CurvePrefix`) | Yes, along one axis | Yes, by adding | A world curve row for each shape and axis, and the seam bound refuses a curve wave as unbounded |
| A `linear` swing on a state driver | Yes, in proportion to the cell | Yes, by adding | One axis, one pose |

Every mechanism that layers does so by adding. Adding is right for a lean on
top of a stride. It is wrong for a playing stance, where the arms must be at
the stance whatever the gait, the run and the jump would add to them.

### Who writes a shape's pose, and who asks

Five things can move a shape within its creation today:

- a swing or a slide under a driver;
- an effector's solve, on each bone of its chain;
- an effector's `align`, on its tip, which need not be a bone;
- a timeline frame;
- a look's part follower (`partDynamics`).

Nine places asked "does this shape move, and how far", and each read the
document for itself. Step 1 routes every one through the compiled rig:

- The owner rule (now `CreationRig.Owner`) decides whether a shape moves on its
  own and so gets a transform slot. `CreationMotionPlan`,
  `CreationRig.HasPartMotion` and the tight bound of a blend group rest on it.
- `CreationMotion.Envelope` bounds how far two sides of a smooth seam can
  move apart, which decides whether the seam is skinned or split
  ([the admitted envelope](../moving-blends.md#admitted-motion-envelope-f1)).
- `CreationMotion.MovingBlends` decides which seams join shapes that move
  apart.
- `WorldGaitDrivers.ComposeDelta` evaluates swings and slides each frame.
- `WorldRigidAssets.IsRigid` decides whether a creation needs a rig at all.
- The effector code takes a bone's hinge from its first swing's pivot.
- The world validator checks that a wave's curve row exists.
- The wake test of a registration (the pool's per-creation record) decides
  whether its pose is evaluated this frame.
- `WorldPresentationManifest` decides which state cells a remote client is
  sent.

They disagreed. The owner rule counted a shape that lies on an effector's
chain and did not count a tip the effector aligns. A tip that
hangs under the last bone with no swing of its own is turned by `align` every
frame and still shares that bone's slot. The wake test hashes the state cells
a registration read and nothing about another body it reaches for. The
manifest registers an effector target's cell for one kind of target only.

This is the hazard the design is built around. A new kind of motion that one
reader misses either never moves or lets a seam tear with no warning
([an invariant kept by convention](../../development/hazards.md#an-invariant-kept-by-convention)).

## The design

### 1. A body part is a named set of shapes

**The problem.** A mask needs a set of shapes, and the family of play
expressions wants that set named once so that a second tool on the hands
reuses it. A creation already has a `parts` list (`CreationPartDocument`). An
entry publishes one shape's transform under an id, which a look's
`partDynamics` and a part anchor read. It names one shape and no set.

**What we propose.** A part keeps its id and its anchor shape and gains a
cover:

- `shapes`: shape names. The part covers each named shape and every shape
  that hangs from it through `parent`. Omitted, it names the anchor shape, so
  the part covers the anchor and what hangs from it.
- `except`: ids of parts declared earlier, whose shapes are removed.

Parts may overlap. `CreationPartCompiler.Compile` computes each part's set
once, and `AuthoredPartTable` answers it. A mask is a list of part ids and
never a list of shape names, so membership has one definition.

**Why a subtree, and not a list.** The skeleton already says what a hand is:
everything parented under the palm. A listed set would drift the day a finger
gains a segment. `except` exists because the root shape carries the whole
body: "the trunk without the arms" has to be sayable.

**Why parts do not partition the body.** A partition would make a new part
shrink an older one, so declaring a hand for one tool would silently narrow
another tool's arm mask. With overlapping parts, adding a part changes no
existing mask.

**What it costs.** Part ids are local to a creation. The standard package
chooses the ids its tools expect (hands, feet, back), as authored convention.
The engine reserves no part name.

**Still to find out.** Nothing. No world in the tree authors `parts` today, so
the change migrates no document.

### 2. A pose clip is key poses on joints

**The problem.** Gap A. Key poses must interpolate, and a pose must not cost a
driver and a swing on every shape it touches.

**What we propose.** A creation gains a `clips` list. A clip has a name, the
driver that plays it, and keys:

- A key has a position `at`, an `ease`, and `poses`.
- A pose names a shape and gives it a `turn` and optionally a `slide`. A turn
  is one or more steps, each an axis and an angle, composed in the order
  written. It is measured from the shape's rest pose and turns the shape
  about its joint. A pose that gives neither a turn nor a slide is the rest
  pose.
- The joint is the shape's `joint` when it authors one, else the pivot of its
  first swing, else its own position. An effector bone uses the same rule
  today with the first two swapped. The rule moves to the compiled rig, so
  the clip and the solve read one copy.

**How a key is stored.** The document keeps the axis and the angle as
written. A swing is written the same way, so a turn crosses the author frame
as a swing does: its axis flips and its angle does not
(`CreationFrame.ToEngine`). A turn of zero stays zero. The converter for an
absolute orientation is the wrong tool here, because it puts a half turn in
front of whatever it is given. The compiler makes the composed turn's
quaternion, picks the one of its two signs whose scalar part is positive, and
refuses a composed turn of a half turn or more by name. Below a half turn
that sign is unique, so two ways of writing one key store the same thing.

**How keys interpolate.** A shape's track is the keys that pose it. Between
two keys the turn follows the arc between their stored quaternions, with no
change of sign on the way, and the slide follows a straight line. The key's
`ease` shapes the stretch that arrives at it: `linear`, `smooth` or `hold`.

That arc is the one that stays on the rest side. From a quarter turn one way
to a quarter turn the other, the joint passes through rest. A general
shortest-arc routine cannot be used: for those two keys the two arcs are
equally short, and `System.Numerics.Quaternion.Slerp` picks one by the sign
the second key happens to be written with. The rest-side arc has a property
the seam bound needs: along it the turn from rest never exceeds the larger of
its two ends. [Section 4](#4-one-compiled-rig-for-every-reader) gives the
argument and the arithmetic check that was run.

**How the driver's phase becomes a position.** A key's `at` is in the units
of its driver's phase. A clip is held, the default, or looping.

- A held clip clamps the phase to the span from its first key to its last. A
  track holds its first key before it and its last key after it.
- A looping clip (`wrap`) has a `period`, one turn unless authored. Its
  position is the phase modulo the period, counted so that a negative phase
  wraps like a positive one, and its keys lie inside one period. The stretch
  from a track's last key back to its first interpolates like any other.

A time or travel driver's phase is an angle the engine wraps every turn
(`WorldGaitDrivers.Advance`), and a driver that follows one wraps with it,
over one turn times its own cadence. A phase that wraps may play only a
looping clip whose period divides the span it wraps over. The compiler knows
that span for every driver and refuses a clip it cannot place, by name. A
held clip on a wrapped phase would snap back to its start each time the phase
wrapped, and a key placed past the span would never play. A held clip is
played by a phase that is a level: a state cell, a rate signal, or a driver
that follows one.

**How the weight applies.** The driver's share on the shape scales the
interpolated turn's angle about its own axis and the slide's length. A share
of one half gives half the turn.

**Where a clip's turn sits among a shape's motions.** A shape's swings
compose first, in the order written, as today. Its clip turns compose after
them, in the order the clips are listed, each about the joint. Its slides and
keyed slides are added last. This order is read from the shape and from the
`clips` list. The order in which drivers are declared plays no part in it
([section 3](#3-a-driver-is-a-layer-with-a-weight-a-mask-and-a-mode)). A turn
chains under the parent exactly as a swing does, so a keyed forearm carries
the hand.

**Why joint turns, and not whole poses.** A timeline frame stores where each
shape is. Interpolating two such poses moves a forearm in a straight line
through the elbow, and a child ignores its parent. A turn about a joint moves
along an arc, carries the children, layers on other motion, and has a size
that can be bounded from the document. It is also what an author can write by
hand: "bend the elbow this far".

**Why the driver stays the player.** A driver already yields a phase and an
eased weight from a signal, a gate, a curve or a second-order response
(`CreationDriverDocument`). A press shaped by a `dynamics` row, a move whose
position is a state cell the simulation advances, and a pose blended in by a
gate all exist as driver behaviour. A clip adds the poses and nothing else.

**What it costs or risks.**

- A key's values are literals. A state-bound key would have no bound a seam
  could be admitted under. An author who wants a pose scaled by state uses the
  driver's cadence or weight.
- A shape that carries a domain fold may not be posed by a clip, for the
  reason it may not carry a swing.
- One driver plays one position, so three dance moves are three drivers. A
  `.puck` template or `for` writes them, and the caps that made this a problem
  are removed in [section 9](#9-the-caps-come-off).
- A swing cannot sit outside a clip's turn. An author who wants an
  oscillation applied on top of a pose writes it as a looping clip listed
  after the pose's clip.
- A move that plays once when a cue fires has no driver signal yet
  ([what this design does not cover](#what-this-design-does-not-cover)).

**Still to find out.** Nothing. `.puck` has no trigonometry, so an author
could not compute a quaternion in a document. Storing the axis and the angle
means no document needs one.

### 3. A driver is a layer with a weight, a mask and a mode

**The problem.** A driver's weight reaches every shape it moves, and every
layer adds. A playing stance has to take the arms from the gait, the run, the
jump and the landing, without those four drivers knowing that playing exists.

**Options.**

| Option | For it | Against it |
|---|---|---|
| A second copy of each lower driver for the arms, weighted down by the upper one | Works today | Every lower layer is edited for every upper one. A new tool edits the gait |
| A weight curve on each lower driver, limited to a part | No second driver | The same edit, moved into a member. A tool still names other layers |
| The upper layer declares what it takes (recommended) | A new tool adds one driver and edits nothing. It matches the family's rule that a tool names no other tool | Order among drivers now matters for more than reads |

**What we propose.** A driver gains two members.

- `parts`: a list of part ids. The driver's swings, slides and clip poses play
  only on shapes those parts cover. Omitted, the mask is the shapes the driver
  itself poses, together with every shape an effector it owns writes
  ([section 6](#6-an-effector-is-a-final-constraint-and-a-layer-sets-its-influence)).
- `mode`: `add`, the default and today's behaviour, or `replace`.

**The share.** A replacing driver with weight `w` scales every driver
declared before it by `1 - w`, on the shapes in its mask and nowhere else. So
a driver's share on a shape is its own weight times `1 - w` for each replacing
driver that is declared after it and covers the shape. A driver declared
after a replacing one is not reduced by it, so an author places a breath sway
above or below a stance by where the driver's row sits. Wherever a swing, a
slide or a clip term used the driver's weight, it now uses the driver's share
on that shape.

**Two orders, kept apart.** Declaration order among drivers decides shares
and nothing else. The order in which motions compose on one shape is the
shape's own ([section 2](#2-a-pose-clip-is-key-poses-on-joints)). One order
cannot stand for both, because the two disagree whenever two turns on a shape
do not commute. The Moth's thigh shows why the shape's order must win: her
stride swing sits between two pairs of heading swings that turn the plane it
strides in. Sorting a shape's swings by their drivers would pull the stride
out from between them and break her sidestep. A replacing layer scales those
five swings by one factor, which keeps the arrangement intact at any weight.

Declaration order already carries meaning for drivers: a driver may read only
drivers declared before it.

**Why this also closes most of gap B.** A fingertip misses its vent today
because both arms carry gait, lean and breath on top of any pose. Under a
replacing stance at full weight both arms hold exactly their keyed turns from
the torso, so the hand sits where it was authored. What remains is drift an
author chooses to keep (a little sway on the instrument arm) and targets
outside the replaced parts (a hand on a hip the dance is turning). The
effector target of [section 5](#5-an-effector-reaches-its-own-creation-or-a-body-a-rule-chose)
closes those.

**What it costs or risks.** A mask narrower than a clip's keys leaves keys
that never play. The compiler warns about them and does not refuse.

**Still to find out.** Nothing that a prototype would settle. The
[research round](#what-the-research-round-could-change) may argue for a
different default mode.

### 4. One compiled rig for every reader

**The problem.** The readers and writers listed above. A clip added as one
more facet beside swings and slides needs one more branch in each reader, and
a forgotten branch is silent. The branches are already uneven for the
writers that exist.

**What we propose.** The authoring assembly compiles a creation's rig once.
Every reader takes the compiled rig and never the document's facets. It
holds:

- **Terms.** For each shape, its motions under drivers in the order they
  compose: each swing, then each clip turn, then each slide and keyed slide.
  A term names its driver and its pivot, and carries the largest turn and the
  largest slide it can produce at full share, and whether those are finite.
- **Layers.** Each driver's mode and its mask as a set of shapes.
- **Writers.** For each shape, everything that changes its own pose: a term,
  an effector's solve, an effector's `align` on its tip, a frame, a part
  follower. Each effector's writes are held once as a set: every bone of its
  chain, and its tip when it aligns.
- **Reads.** Everything outside the rig that the pose depends on
  ([section 7](#7-a-pose-is-packed-again-when-something-it-reads-changes)).
- **Reach.** How far from the root the rig can carry any shape (section 7).

From that one compiled form:

- a shape owns a slot when it has any writer;
- a creation needs a rig when any shape has a writer. The other conditions
  `WorldRigidAssets.IsRigid` tests (cameras, faces, erosion, morphing) are not
  motion and stay as they are;
- evaluation walks a shape's terms in order, and skips a term whose share is
  zero;
- the seam bound reads the terms' limits and refuses a skin where a writer
  has none.

**What keeps a reader from going around it.** Every renderer, collider and
anchor reads a creation through one converted document,
`WorldPrototype.EngineDocument`. Step 1 builds the compiled rig at that door
and removes swings, slides and clips from the document the door returns. A
reader there that asks the document for a shape's swings then finds none and
moves nothing, which the first run shows. The functions that classify motion
(`CreationRig.Owner`, `CreationMotion.Envelope`,
`CreationMotion.MovingBlends`) take the compiled rig as their argument, so
they cannot be called with a document that was never lowered.

This is also the form the rendering plan asks for and has not built: one
compiled motion program from which a CPU evaluator, a GPU kernel and a bound
all derive
([decision 12](../rendering-research.md#12-keep-motion-and-placement-as-authored-programs-at-zone-scale),
[authored motion on the GPU](../visibility.md#v2-instance-updates-proportional-to-change-and-authored-motion-on-the-gpu)).
Whether a slot's motion may run on the GPU is decided from the compiled reads
of that slot and of every ancestor, and never from a term's keys alone. A
shape is not eligible when an effector writes it or an ancestor, when a
follower moves it, or when any driver that poses or attenuates it reads the
seat's input or another body. Literal keys alone do not make a clip-posed
shape eligible: a vent whose driver follows a press is not.

**The seam bound with clips.** A skinned seam is admitted when two numbers
fit: the relative turn of its two sides is at most 60 degrees, and the
distance they can move apart at the seam is at most half the smooth radius
(`CreationBlendEnvelope.Admitted`). The bound walks the shapes on both sides
of the seam up to their common ancestor, as it does today. On each shape it
counts:

1. **Each swing,** in full, about its own pivot, as today.
2. **The shape's clip turns, as one turn about its joint.** Every clip turn
   of a shape is about the same joint, so however they interleave they
   compose to one rotation about that point. Its size is at most:
   - for each adding driver, the sum of the limits of that driver's tracks on
     the shape;
   - plus, across all replacing drivers, the largest such per-driver sum.

   A track's limit is its largest key turn.
3. **Slides,** in full, as today. A keyed slide's limit is its longest key,
   summed and maximized across drivers as the turns are.

The distance follows from the turns as it does today: a turn of angle θ about
a pivot moves the seam by at most `2 × reach × sin(θ / 2)`, where the reach
is the farthest corner of the seam's box from the pivot plus the distance
already counted. A clip's turn enters that sum at its joint. The angle alone
never admits a seam.

A seam is refused a skin, as today, when a shape on either side has a writer
with no bound: an effector's solve or its aligned tip, a follower, or a frame
pose bound to state.

**Why the bound holds.**

- *A track never exceeds its largest key.* Both ends of a stretch are stored
  with a positive scalar part. Along the arc between them the scalar part is
  a weighted sum of the two ends' scalar parts, in which neither weight is
  negative and the two add to at least one. So it never falls below the
  smaller end, and the turn never exceeds the larger. This holds for every key under a
  half turn. A seeded check of 10,000 random pairs of keys up to 179 degrees,
  33 positions each, found no excess, and gave the same pose for both
  spellings of every key. For the two opposite quarter turns it read zero
  degrees at the midpoint for either spelling, where the library routine read
  180 degrees for one of them.
- *Shares of replacing drivers add to at most one.* On one shape they
  telescope to one minus the product of what each leaves over. A turn scaled
  by its share is at most the share times its limit, so the replacing
  drivers' turns together are at most the largest limit.
- *The share belongs to a driver, so the limit must be summed for each
  driver.* Taking the largest single track would be wrong when one driver
  plays two clips on one joint: two 35 degree keys at full weight make 70
  degrees. A seeded check of 10,000 random stacks of up to ten replacing
  drivers with one to three tracks each found no pose past the per-driver
  rule, and 2,490 past the single-track rule.

Both checks are arithmetic on quaternions and run no engine code. Their
output is in the commit that lands this page.

**What it costs or risks.** The collapse in rule 2 is what lets a hand with
many poses keep a skinned seam at all, and it rests on every clip turn of a
shape sharing one joint. The collapse does not widen what one turn may do: the
seam's box is the overlap of the two parts' boxes widened by the smooth radius
on every side, so the reach from a joint inside it is at least √3 times the
radius, and `2 × √3 × radius × sin(θ / 2)` stays under half the radius only
while θ is under about 16.6 degrees, whatever the radius. A joint turned
further than that creases. (The first version's fixture, a seam box 18 mm from
the joint at a smooth radius of 14 mm, could not be built: the box alone
reaches at least 24 mm.) A swing keeps its own pivot, which the pelvis needs
(it leans about the ground and turns about the hips), so swings stay outside
it. Counting swings and adding drivers in full while a replacing layer is
reducing them is safe, and loose.

**Still to find out.** Nothing this page owns. The distance the bound
computes is the margin the
[bounds for skinned draws](../virtual-geometry.md#d2-bounds-for-skinned-and-multi-slot-draws)
need for a blended group. That plan reads it from the compiled rig, and
until it does a blended draw stays without a box, as today.

### 5. An effector reaches its own creation, or a body a rule chose

**The problem.** Gap B, and the family's wider need: a hand on a body that a
rule picked a moment ago. An effector's target is a probed surface, a body at
an index fixed in the document, or a world point in a state cell
(`CreationEffectorTargetDocument`).

**What we propose.**

- **A new kind, `shape`.** It names a shape of the same creation and an
  `offset`: a point in the creation at rest that rides that shape's motion.
  The solve reads the shape's pose in the same frame, after the drivers and
  after every effector declared earlier, so the target is where the shape is
  drawn and not where it was a frame ago. An optional `direction`, carried by
  the shape, is the way the tip approaches the point, and gives the existing
  `align` a normal to turn the tip onto.
- **The `body` kind takes a reference.** Its `index` may be a literal, as
  today, or a `reference` to a state cell holding a body index, read as stored
  truth each frame. It may also name a `part` of that body's look. The point
  is then that part's anchor as last drawn
  (`WorldCreationInstances.TryBodyPartAuthoredPose`), and the root when the
  look publishes no such part.

**Which cell, and what it may hold.** The cell is a numeric state cell that a
rule writes. It selects a body when it holds a whole number that names an
active body. A cell that holds no number, or any other number, selects none.
So zero selects body zero, and a rule clears the target by clearing the cell
or writing a value such as minus one. `WorldStateLease.TryNumber` already
tells an absent cell from a zero.

The register a designate effect fills (`WorldEffect.Designate`) is not such a
cell. It lives in the authority's population as a `WorldTargetDesignation`,
and no delivery carries it to a client's state mirror. A client can only
propose a designation. Reading that register on a client is a disclosure
decision of its own, for the plan of whichever expression needs it.

**The target has an identity, and the effector checks it first.** Each frame,
before anything else, the effector resolves who its target is. For a `shape`
target it is the shape. For a `body` target it is the body's
`WorldEntityAddress`, which names a slot and the generation of its occupant.
For a point in a state cell it is the cell holding a point. For a surface it
is the field the probe reads, which does not change when one probe misses.
When the identity differs from the one the effector last acted on, or there
is none, the effector releases: its plant latch, its step and its remembered
point are cleared through one function. Only then is a latch consulted. So a
hand never keeps reaching for a point whose owner has gone, and a slot that
is reused releases the old occupant's latch. A plant latched on a surface
still holds through a frame in which its probe misses, as today.

**Losing a target is a fade that reads nothing more.** After a release with
no new target, the effector's weight eases out over its blend time, and the
solve keeps running toward the last goal it drew, held as a point in the
reaching creation's own frame. The hand eases back to its driven pose from
where it was. Nothing about the lost target is read again, so a recipient who
loses the right to read the cell learns nothing more from the hand.
`body.rig` reads `target=none` in the frame of the loss. An unlatched
effector whose probe finds no surface fades the same way. Today that case
stops the solve at once while the weight number goes on easing, which shows
as a pop.

Three refusals keep the new kinds well defined, all by name at validation and
all stated over the effector's writes
([section 4](#4-one-compiled-rig-for-every-reader)):

- The target shape may not be a bone of the effector's own chain or its tip,
  nor hang from one. Moving the chain would move the target.
- No effector declared later may write the target shape or a shape it hangs
  from. An effector reads only poses that are final for it.
- A `plant` on a `shape` target is refused. A plant holds a point in the
  world, and a point on the creation's own body is not one.

**Why two kinds and not one.** A point on the creation's own shape can be read
in the same frame, exactly. Another body's part comes from that body's last
evaluation, which may be this frame's or an earlier one
([section 7](#7-a-pose-is-packed-again-when-something-it-reads-changes) says
how old). One kind would hide that difference.

**What it costs or risks.**

- A rule that leaves a stale index in the cell after the slot is reused aims
  the hand at the new occupant. The identity check releases the old latch and
  cannot know the rule meant someone else. Clearing the cell is the rule's
  job.
- A part follower (`partDynamics`) moves a shape after the solve. A follower
  on the target shape or on the chain breaks the contact. The world validator
  is to warn where a look does this; step 7 did not build that warning, and it
  stays open here.
- A remote client needs the cell. The presentation manifest registers an
  effector target's reference only for the `state` kind today
  (`WorldPresentationManifest`), so a body index used by nothing else would
  not be sent. Section 7 closes this for every read at once. A recipient who
  may not read the cell resolves no target, and the hand stays at rest.

**Still to find out.** Nothing.

### 6. An effector is a final constraint, and a layer sets its influence

**The problem.** Effectors run after every driver and know nothing of layers.
When a dance move replaces the legs, the foot effectors still pull the boots
to the ground and fight the clip. An author can gate them on a state token,
but a gate has no negation, so "not dancing" needs its own cell and rule.

**Two decisions.** When an effector runs and how strongly it pulls are
separate, and only the second is a layer's. An effector does not "belong to a
layer" in the sense that a later layer's pose survives its solve.

**When it runs.** Every driver's motion composes first. Then the effectors
solve, one after another in the order they are declared, each on the pose the
one before it left. That is today's order and it does not change. An effector
is a constraint on the final pose. A recoil added by a later layer moves the
arm, and the solve then puts a planted hand back on its point, so the recoil
shows in the elbow and the shoulder and not at the hand. An author who wants
the hand to give lowers the effector's weight for that moment.

**Which one wins.** Where two effectors move the same bone, the later one
solves last and holds its target, and the earlier one's tip can be carried
off its own. Declaration order is the priority. The compiler warns, naming
both, when a later effector writes a shape that an earlier effector's tip
hangs from. `body.rig` reports each effector's `error`: the distance from its
tip to its target on the final pose, after every effector and follower has
run.

**How strongly it pulls.** An effector takes the weight vocabulary a driver
has:

- `weightBy`: the record a driver's `weightBy` uses
  (`CreationDriverCurveDocument`). The driver it names owns the effector.
- `blendInSeconds` and `blendOutSeconds`, as on a driver. Today an effector
  eases over a fixed 0.15 seconds (`CreationDriverDocument.WeightSeconds`).

Three numbers multiply into the weight the solve and the `align` use.

1. **The eased level.** The effector's own state. It eases toward its gate
   times its contact weight times the curve's height at the owner's phase,
   over the blend times.
2. **The authored `weight`,** as today.
3. **The layer share,** read fresh every frame and never eased. For each
   shape the effector writes, take the owner's weight (one, with no owner)
   times `1 - w` for each replacing driver that is declared after the owner
   and covers that shape. The share is the smallest of those across its
   writes. With no owner, every replacing driver counts.

The share uses every shape the effector writes, not its tip. A mask that
takes the thigh and the shin and leaves the foot still removes the foot's
plant, because the plant turns the knee. And because the share is multiplied
in when the solve is applied, a layer that reaches full weight removes the
effector in that same frame, however long the effector's own blend-out is.
Easing toward the share would leave the effector pulling for its whole
blend-out.

**A suppressed effector lets go.** When the share is zero, the effector
releases its latch through the same function a lost target uses
([section 5](#5-an-effector-reaches-its-own-creation-or-a-body-a-rule-chose)).
When the dance ends, the foot plants where the body now stands and is not
pulled back to the footprint it left.

**An owner's mask must cover what its effector writes.** A fingertip effector
owned by a vent's driver turns the finger's segments. If the driver's mask
did not cover them, the solve would write outside its own layer. A driver
with no `parts` covers them by definition
([section 3](#3-a-driver-is-a-layer-with-a-weight-a-mask-and-a-mode)). A
driver with `parts` that leave out a shape its effector writes is refused,
naming the shape.

So the Moth's foot plants yield to any replacing layer that takes the legs,
with no edit to them. A move that wants planted feet declares its own two
effectors, weighted by its own driver and planted on that driver's phase
windows, which the existing `plant` already expresses. A fingertip effector
weighted by its vent's driver presses as the finger curls and lets go when a
move takes the hands.

**What it costs or risks.** `weightBy` needs a curve row, and a straight ramp
is awkward to draw as one (the rig reference says a slanted straight segment
does not compile). The standard package authors one ramp row and reuses it.

**Still to find out.** Nothing.

### 7. A pose is packed again when something it reads changes

**The problem.** The pool evaluates a registration's pose (it "packs" it)
only when its wake test says something changed. The test hashes the frame
cursor, the body's facts and the state cells the registration read
(`WorldStateLease.Changed`), and adds the root's motion and any driver still
in motion. Three things escape it or the steps after it.

- *Another body.* Let a hand settle on another body's part, then move only
  that part. Nothing the reaching body hashes has changed, so the hand stays
  where the part was. Reading the part marks the target as read and does
  nothing for the reader. The same is true today of the existing `body`
  target when the other body walks away from a body at rest.
- *The rate tiers.* A registration that wakes can still be held: a body small
  on screen is evaluated at a reduced rate, and a body nothing can see is not
  evaluated at all.
- *What has never been seen.* A body counts as unseen when no view or shadow
  reaches a sphere around it. The sphere's radius is the creation's reach at
  rest, widened by poses already drawn. A retracted hand that a state cell
  extends into view for the first time is outside that sphere, so the body is
  held, and held bodies do not widen their sphere.

**What we propose.** The compiled rig lists what the pose reads from outside,
and that list is the only way the pose pass can read.

- **The reads.** State references (each marked stored truth or eased), body
  facts, a surface probe, another body's root or part, the definition's curve
  rows, presentation time, and the seat's guess of
  [section 8](#8-the-owning-seat-sees-its-own-press-early-as-a-guess). The
  compiler derives the list from the drivers' signals and gates, the
  effectors' targets and gates, and `sizeBy`.
- **One view to read through.** The driver advance and the effector solve are
  handed a view of those reads, in place of the lease and the client they
  take today. A read that is not in the list has nothing to call.
- **The wake test hashes the view.** Every read has a revision. For another
  body it is that body's identity, together with a serial its registration
  moves each time its pose changes, or its root pose where it has no rig. So
  a target that moves, changes occupant or leaves wakes its reader.
- **Delivery uses the same list.** `WorldPresentationManifest` registers
  every state reference in the list, as stored truth where the read is of
  truth, through the recipient's disclosure as today. Today the manifest
  names each document type by hand.
- **A read pose is at least as fresh as its reader.** A body whose part an
  effector reads is rated no slower than the body that reads it. The one rate
  decision (`WorldCreationInstances`' `RateOf`) takes the faster of the two.
  Today a read only keeps the target from being held as unseen.
- **The reach is derived.** A turn keeps every point's distance to its pivot.
  So the farthest a point can get from the root is the length of the path
  from the root through each pivot on its chain, in order, to the point, plus
  the limits of the slides on the way. That is true of swings, clip turns,
  effector solves and an aligned tip alike, and it comes from the document
  alone. The sphere a body is rated and judged unseen by takes this reach, or
  the widest pose it has drawn where a follower has carried a part past it. A
  rig with a slide that has no finite limit has no such reach, and a wake
  from a changed read then forces a full evaluation.

**How old a read pose can be.** Another body's part is read from that body's
last evaluation. If the target is evaluated later in the same frame, the
reader sees the move one frame late. If both are far away and rated at a
reduced tier, the reader sees it at that tier's rate. The same-creation
`shape` target has neither delay.

**What it costs or risks.** The derived sphere is the fully stretched limb
and not the pose in use, so a body near the edge of a view counts as visible
a little more often. That costs evaluations and never hides a pose.

**Still to find out.** Nothing.

### 8. The owning seat sees its own press early, as a guess

**The problem.** Gap C. A driver reads state. A press is taken in by the next
tick, the snapshot is then presented between that tick and the one before it,
and only then does the finger start. With a remote authority a round trip is
added. For a play style judged on timing, the player's own hand answering late
is the feel the owner asked this feature to get right.

**Why "read the channel early" cannot be promised.** The simplest design
lets a driver follow an input channel in place of its state cell while the
seat is admitted and the channel is live. It would claim that this changes
only when the owner's screen shows a pose, never which pose, and never what
anyone else sees. Four counterexamples show otherwise.

- *Two seats on one screen.* A registration has one pose, and every view of
  the frame draws it (`WorldFramePresenter` hands one transform table to all
  its views). In split screen the second seat sees the first seat's early
  finger. Anything anchored to that finger reads it too: a sound emitter, a
  camera, another body's hand.
- *A press the authority refuses.* A rule rejects a press while the body is
  stunned. The player holds the button. The channel stays down and the cell
  stays zero, so the owner sees a closed vent for as long as the button is
  held, and nobody else ever does. That is a different pose, not an earlier
  one.
- *A body at rest.* A press changes no state cell until its tick has run, so
  the wake test does not fire, and a rate tier could hold the body anyway.
- *The press is not yet an action.* A device event waits in the router until
  the next tick takes it in (`InputRouter.SnapshotForTick`). Only then is it
  resolved through the binding pages and handed to the seat's producer
  (`WorldSeatInput.Observe`). Between the press and that tick there is no
  channel level to read.

**The decision: narrow the promise.** The mechanism stays, and it is named
for what it is: a guess. Dropping it would leave the remote topology a round
trip late, which is most of the delay there. The wider promise cannot be
kept.

**What we propose.** A driver whose signal is a state cell may carry `lead`,
the name of an input channel. The guess is drawn in a pose of its own, the
**guess layer**, and only the owning seat's own view draws it.

- **The shared pose never guesses.** Every reader of a body's pose other than
  the seat's own view reads the shared pose: the other views on the client, a
  camera screen or a session window that films the same scene, a part-anchored
  camera or a carried screen (and so the pointer ray a rule may read), another
  body's effector, the rate tiers and `body.rig`'s driver readings. The shared
  pose's drivers read their cells, as if no driver carried `lead`.
- **The layer is a second registration of the seat's body**
  (`WorldCreationInstances.Guess.cs`), held by the stamp pool of the world the
  seat's body is in and never entered in the pool, so no lookup that resolves
  a body reaches it. Only its view answers a lead (`WorldRigReads.ForGuess`);
  a pool registration's view answers none and refuses a guess outright.
- **Only the seat's own view reads the layer.** The layer packs into rows past
  every row the pool and its program name, and the seat's own view snapshot
  carries a pose overlay (`SdfViewSnapshot.PoseOverlay`) that reads the body's
  slots from those rows (`sdfViewPoseSlot` in the mesh kernels). No other view
  carries it, so no other picture can show the guess.

In the layer, the driver's phase follows the seat's level on the channel in
place of the cell only while all of the following hold.

1. **One audience.** The client presents exactly one seat, and the guess is
   offered only to the scene of the world whose authority that seat's route
   names, for the body the route drives there (`WorldClient.SeatGuessAt`). The
   one-seat test no longer protects anyone else's picture (the overlay does);
   it stands because a scene holds one guess layer.
2. **The seat is admitted** (`WorldSeatInput.Admitted`), the channel is live
   in the mapping the authority last published, and the producer has not lost
   a capture.
3. **An input on that channel is outstanding.** The seat's producer has
   recorded an edge on the channel in a batch it sent, and the tick in which
   the authority answered that edge has not yet been presented. Once it has,
   the producer forgets the edge, so a later delivery that no longer carries
   the answer (a restored checkpoint, a withdrawn span) revives nothing.
4. **The guess is young.** It is no older than the driver's `leadSeconds` of
   presented time: the frames' presentation deltas summed, which advance by
   the completed ticks under lockstep, never the wall clock.

Otherwise the layer's driver reads its cell, as the shared pose's does.

- **The level** is the producer's folded value for the channel
  (`WorldInputProducer`), which already obeys the mapping, the rule that a
  released source must return to neutral before it can act again, and the
  channel's quantization.
- **The channel must be a level**: a button or an axis. A relative channel
  (mouse look) and a text channel are refused by name, since neither has a
  held level.
- **The guess ends** when the answering tick is presented. If the rule took
  the press, the cell now says what the guess said and nothing visible
  happens. If the rule refused it, the finger returns to what the cell says,
  through the driver's `dynamics` response when it names one. A held button
  starts no new guess, because a hold is not an edge.
- **Every guess of a seat ends at once** when the authority publishes a new
  input epoch for it (a mode switch, a reset, a refused batch), when the seat
  stops being admitted or stops controlling the body, and when the producer
  loses a capture.
- **The answer needs one new fact on the wire.** The authority keeps the last
  input sequence it took from each owner (`WorldInputOwner.Sequence`) and
  sends it to no one. Each owner's delivery gains the last sequence the
  authority has answered for that owner, taken or refused, and the tick it
  answered in. It goes to that owner only, as the input publication does
  (`WorldOwnedRecords.ForOwner`); an observer with a body disclosure is no
  owner and is handed none (`WorldOutputHub.Observed`). Without it a remote
  client cannot tell a late answer from a refusal. Prediction will need the
  same fact. The answer carries the batch's input epoch, since event sequences
  restart each epoch, and it is delivery only: it is neither hashed nor
  checkpointed, so a resumed authority answers from its next batch and a guess
  left open across the resume ends at `leadSeconds`. The answered tick counts
  as presented once the mirror the rig reads has delivered a later tick, or
  that tick at a presentation fraction of one.
- **Every refusal is answered.** The submission door records the answer
  (`WorldInputRuntime.Answer`) before any check that can refuse the batch: a
  drive gate (a stun), a Drive denial, a reach, the admission itself. Only a
  batch of the owner's current epoch for the owner's own route writes it, so
  a stale or misdirected batch cannot overwrite the answer the current epoch
  holds.
- **The client reads the answer under the authority's owner.** A forwarded
  authority stamps a traveler as its peer, a principal the client never names,
  so the client reads its publication and answer under its own principal, else
  under whichever owner the route's authority publishes the routed body's
  input as (`WorldSessionMirror.TryInputOf`).
- **`leadSeconds` bounds a guess that gets no answer** (a stalled link). It
  is authored on the driver, because how long an unconfirmed press may show
  is a matter of feel. The engine's default is a quarter of a second, and no
  maximum is imposed: a long value changes only the owning seat's own picture.
- **It wakes and is not held.** The guess is one of the layer's reads
  ([section 7](#7-a-pose-is-packed-again-when-something-it-reads-changes)).
  The layer packs every frame it exists, so a guess shows in the frame after
  the tick that takes the press in whatever the shared pose's rate tier, and
  the shared pose holds exactly as it would with no `lead` authored.

The gate still reads stored state, because which controls are live is
simulation state
([modes are simulation state](../../decisions/input-channels.md#modes-are-simulation-state)).

**The promise, in one sentence.** While one of a seat's own inputs has been
taken in by a tick and not yet answered on screen, that seat's own view, and no
other picture, may draw the driver at the level the input asks for.

**What the player gets.** The time from the tick that takes the press in to
the frame that shows its result. With the authority in the same process that
is the presentation's one tick of interpolation, 33 ms at the debug world's
30 Hz. With a remote authority it is that and the round trip. The wait from
the press to the tick that takes it in, between nothing and one tick, stays.
The plan's stage 2 check is reworded to match.

**Why this is allowed.** Presentation may use its own values so long as
authoritative state does not change
([simulation and presentation](../../decisions/engine-design.md#simulation-and-presentation-have-different-contracts)).
The seat's camera already does this: its look is client state that the seat's
own input moves between ticks (`WorldSeatViewState.PresentedLook`).

**The attacks, and what closes each.**

- *A modified client lies about its guess.* It changes that client's own
  picture and nothing else.
- *Presentation feeds the simulation.* A guessed pose moved a part-anchored
  camera or a carried screen's camera when the guess was packed into the
  shared pose, and the pointer ray a rule reads is cast through that camera.
  Now every camera, anchor and carried screen reads the shared pose, which no
  lead reaches, and a guess's age is presented time, not the wall clock. The
  client cannot reference the server (`build/Architecture.props`).
- *Another viewer sees it.* Not on another client, because nothing about it
  is sent. Not on the same client: a second seat's view, a pending seat's, an
  operator or named camera, a camera screen and a session window that shares
  the scene all read the shared pose, and only the owning seat's own view
  carries the overlay that reads the layer.
- *The guess lands on the wrong body.* The body index belongs to the world
  the seat's route names, and only that world's scene is offered the guess.
- *The guess outlives the truth.* It ends at the answer, at a new epoch, or
  at `leadSeconds`, whichever comes first, and every refusal is answered.
- *Hidden information.* A seat's own input, shown to that seat.

**What it costs or risks.**

- For up to the outstanding window the owner can see a press the authority
  then refuses. That is what a guess is. An author who cannot accept it
  leaves `lead` off.
- Split-screen seats get no early feedback.
- The seat's own body costs a second pose evaluation every frame while it
  wears a creation with a led driver.
- In the seat's own view, a light anchored to a guessed part, a mirror or a
  portal view, and the culling box of a rigid part draw the shared pose: the
  overlay reaches the mesh vertex and shading reads only. A guessed part that
  leaves its shared box at the edge of the view can be culled for the frames
  the guess lasts.
- A recording of the seat's own screen contains the guess, since it is that
  seat's picture. A replay of the authority's tape does not.
- When prediction lands, the driver's state read goes through the predicted
  view and `lead` is deleted from the vocabulary and from the worlds in the
  same change. The acknowledgement stays.

**Still to find out.** Whether the wait that stays is felt. Step 8's order
log reads it per press. If the stage 2 sitting finds the finger's start
uneven, the next step is a preview from the moment of the press. That is a
change to the input router, so that it can resolve a press through the
binding pages for display without taking it from the queue the tick reads.
It is named under
[what this design does not cover](#what-this-design-does-not-cover) and is
opened only on that finding. Whether a seat may hear its own note early
belongs to the delivered-cue design.

### 9. The caps come off

**The problem.** What becomes of the rig's caps, given the reading above: the
swing cap is already full on three of the Moth's shapes.

**What we propose.**

| Cap | Decision | Why |
|---|---|---|
| Swings and slides a shape | Removed | Evaluation walks a list. Nothing is sized by the number |
| Drivers a creation | Removed | The cap is the width of tables each registration allocates. A registration is made for one document, so the tables take that document's count |
| Effectors a creation | Removed | The same |
| Bones a chain | Kept | It sizes solve scratch shared by every effector, and no rig is near it |
| Moving members of one mesh group | Kept, and reported earlier | A vertex names its member in a byte (`SdfMesh.MaxMembers`). It is a limit of the mesh format and not of the rig |

**Every table is sized from the compiled rig.** Removing the four refusals is
not enough, because several consumers clamp for themselves. The per-driver
followers that give a driver its `dynamics` response are six arrays of the
cap's width (`WorldGaitKinematics`). The effector solve, the slot resolve,
the "is my sibling stepping" test of a replant and the `body.rig` listing
each stop at the effector cap on their own. A thirty-third driver would be
listed and would not follow, and a ninth effector would be inert. Step 3
sizes each from the compiled rig and deletes the constants, so nothing is
left to clamp against.

**The mesh limit is reported when the document compiles.** A group with more
than 255 moving members fails today when its mesh is sealed, which is during
a bake. The compiled rig knows each group's members, so the count is checked
with the rest of the document, and the error names the group. A larger rig
makes this limit easier to reach. It moves an existing failure earlier and
refuses nothing new.

`puck creation stats` reports a creation's drivers, effectors and the most
terms on one shape, and the pose work counters (`WorldCreationWork`) gain the
terms evaluated and the effector solves. Cost is then read, where today it is
refused at a number chosen for one walker.

**Why.** A cap that makes an author split a driver or drop a pose is the
clumsier form
"[ergonomic is optimal](../../decisions/state-and-language.md#ergonomic-is-optimal)"
calls an engine defect. Clips remove most of the pressure, and the caps would
still bind a creation that wears several tools.

**What it costs or risks.** A creation's rig work is unbounded by admission,
as its shape count already is. For a world's own creations that is a counted
cost. For a look that arrives with a visiting character it is work another
player's client must do, and no budget for it exists. Admitting a visiting
look's rig waits on that budget, and
[the plan's packet](../open-items.md#instrument-and-dance-play) records it.

### 10. The hand gets joints

This is authoring on the Moth, for the plan's hands lane, and it is here to
show what changes on her. The engine side is already above: a shape a clip
poses needs a `joint` and no swing.

- The palm takes a `joint` at the wrist and parents to the forearm.
- Each finger becomes two or three armour segments, each parented to the one
  before with a `joint` at its pivot. The thumb the same.
- Each segment then moves on its own and takes a transform slot. Two hands add
  about thirty. The plan's stage 2 already counts transform rows for one Moth
  before and after.
- Segments meet at visible pivots and carry no smooth seam, so the small-joint
  seam gap (gap E) stays with the bare hand, as the plan says.

The only shapes in the tree that author a `joint` today are the two bones of
the gallery's reach exhibit (the `motion-ik` creation in
[the debug world's shapes](../../../worlds/debug/shapes.puck)), and neither
carries a swing. So putting the joint first in the rule of
[section 2](#2-a-pose-clip-is-key-poses-on-joints) changes no existing
effector.

## A worked sketch

This shows the proposed document shape. No compiler has read it, and the
angles are placeholders.

```
parts [
  { id: "arm-left",   shapeId: 148 }
  { id: "arm-right",  shapeId: 216 }
  { id: "hand-right", shapeId: 233 }
  { id: "arms",       shapeId: 148, shapes ["upperArm-left", "upperArm-right"] }
]

drivers [
  // The eighteen she has today come first.
  { name: "play", signal: "state.playPose.$body", cadence: 1,
    when ["state.playPose.$body"], blendInSeconds: 0.18, blendOutSeconds: 0.12,
    mode: "replace", parts ["arms"] }
  { name: "vent-0", signal: "state.vent0.$body", cadence: 1,
    lead: "vent0", dynamics: "finger-press", when ["always"] }
]

clips [
  { name: "play-stance", driver: "play", keys [
      { at: 0, poses [
          { shape: "upperArm-left",  turn [{ axis [1, 0, 0], angle: 40deg }] }
          { shape: "forearm-left",   turn [{ axis [1, 0, 0], angle: 55deg }] }
          { shape: "upperArm-right", turn [{ axis [1, 0, 0], angle: 35deg }
                                           { axis [0, 0, 1], angle: 12deg }] }
          { shape: "forearm-right",  turn [{ axis [1, 0, 0], angle: 50deg }] }
      ] }
  ] }
  { name: "stop-vent-0", driver: "vent-0", keys [
      { at: 0, poses [
          { shape: "finger-right-0-a" }
          { shape: "finger-right-0-b" }
      ] }
      { at: 1, ease: "smooth", poses [
          { shape: "finger-right-0-a", turn [{ axis [1, 0, 0], angle: 30deg }] }
          { shape: "finger-right-0-b", turn [{ axis [1, 0, 0], angle: 25deg }] }
      ] }
  ] }
]

effectors [
  { name: "fingertip-0",
    chain ["finger-right-0-a", "finger-right-0-b", "finger-right-0-c"],
    tip: "finger-right-0-c",
    weightBy { driver: "vent-0", curve: "unit-ramp" }, blendInSeconds: 0,
    target { kind: "shape", shape: "bracer-left", offset [...] } }
]
```

The stance is one key held by a gated weight: the arms blend to it while the
cell is set, and the gait, run and jump lose the arms by the same weight.

The vent is two keys on a phase that follows a state cell. The key at zero
names both segments with no turn, which is the rest pose. Without it each
track would begin at its only key and the finger would sit curled while the
cell read zero. The vent's driver has no `parts`, so its mask is the two
segments it poses and the three the fingertip effector writes. The fingertip's
solve follows the driver's phase through the ramp, so the tip finishes on its
vent as the finger curls.

A dance move that uses the arms is a later replacing driver whose parts cover
them. It fades the stance, the vent and the fingertip together.

## The invariants

Each row names one thing that enforces the invariant. Where nothing
structural does, the row says so.

| Invariant | What enforces it | Where the guarantee stops |
|---|---|---|
| Every reader of a creation's motion sees every writer of it. | One door: the converted document a client reads (`WorldPrototype.EngineDocument`) carries no swings, slides or clips, and the functions that classify motion take the compiled rig as their argument. | The authored document stays reachable in the author frame, for editing and saving. Code in the authoring assembly could still read its facets. Step 1's reference search lists who does. |
| A skinned seam never sees motion past the envelope it was admitted under. | The envelope is computed from the compiled rig when the document compiles. A seam outside it is split, and a writer with no bound refuses the skin. Key turns are literals under a half turn. | The rule that sums a driver's tracks is a formula in one function, and nothing structural forces a future kind of term into it. A test that samples weights and positions of random rigs against the bound holds that. |
| A key interpolates the same however it is written. | The compiler stores each key as the one quaternion with a positive scalar part, and the evaluator has one arc routine that never changes sign. | A key of a half turn or more has no such form and is refused. |
| The order of motions on a shape never depends on the order of drivers. | Evaluation walks the shape's own term list in the order stored. A share is looked up by driver and only scales. | None known. |
| A body part has one definition of what it covers. | Masks hold part ids only, and `CreationPartCompiler.Compile` is the one place that turns a part into shapes. | A part id means something inside one creation. Shared ids are a convention of the package that authors them. |
| An effector writes only where its layer reaches, and pulls nothing where its layer's share is zero. | Its writes are compiled once. The compiler refuses an owner whose mask leaves one out. One function forms the applied weight for the solve and the `align`, and multiplies in the share of the current frame. | A constraint is final: motion a later layer adds to a constrained chain shows above the tip and not at it. |
| A latch never outlives its target or its effector's influence. | The latch is read through one function, which checks the target's identity and the share first and releases through one door. | A rule that leaves a stale index in a cell names a new target, and the hand goes there. |
| An effector reads only poses that are final for it. | Validation over the compiled writes: a target on the effector's own chain is refused, and so is a later effector that writes the target or a shape above it. | A part follower moves a shape after the solve. Another body's part can be a frame old, or as old as its reader's rate tier. |
| A pose is evaluated again when a value it reads changes. | The pose pass can read only through the view of the rig's compiled reads, and the wake test hashes that view. | A body far away is still evaluated at its tier's rate. A body nothing can see is not evaluated. |
| A body judged unseen cannot be in view in any pose its rig can reach. | The sphere it is judged by takes the reach derived from the compiled rig. | A part follower can overshoot the derived reach. The widest drawn pose covers that only after it has happened once. |
| Every state cell a rig reads is sent to the recipients allowed to read it, and a scene is shared only between streams that read those cells alike. | The presentation manifest registers from the compiled reads; the scene's input rows and a presentation-tier peer's presented rows take every state read of every creation's compiled rig (`WorldSceneInputs.CreationRows`). | Disclosure decides who is allowed, unchanged. A look's lanes and poses are registered by the manifest and not yet among the scene's input rows. |
| A guess colours only the owning seat's own presented picture. | The guess layer is a registration no pool lookup reaches; only its view answers a lead (`WorldRigReads.ForGuess`), and a pool registration's view refuses a guess (`WorldRigReads.BindGuess`). The layer packs into rows no program, draw or lookup names, read only through a view's pose overlay, which the presenter sets on the guessing seat's own view alone. | A kernel that reads a slot's row without `sdfViewPoseSlot` (an anchored light, a mirror or portal view) draws the shared pose in that view. That seat can see a press the authority refuses, until the answer arrives, and a recording of its screen shows the guess. |
| A guess changes nothing the authority computes. | Cameras, anchors, carried screens and so the pointer ray read the shared pose, which no lead reaches; the guess's age is presented time; the client cannot reference the server (`build/Architecture.props`, enforced at build). | A player acts on what they see, which is why input is recorded. |
| Every input an owner sends is answered in the tick it arrives. | The submission door writes the answer through one function (`WorldInputRuntime.Answer`) before any check that can refuse the batch, for a batch of the owner's current epoch and its own route only. | A batch the door never reaches (no body at the index) is no authority outcome and is not answered; its guess ends at `leadSeconds`. |
| The engine learns no instrument, dance or finger. | Review. The names are a clip, a part, a mode, a lead. | A leaked name is a defect fixed when found. |

## The review, finding by finding

The findings are numbered as the review numbered them. "Step" is the build
step whose check runs the reviewer's counterexample.

| # | The finding | What changed | Step |
|---|---|---|---|
| 1 | One driver can spend its share twice: two clips on one full-weight driver each turn a finger 35 degrees, and the bound counted 35. | The bound sums a driver's tracks on a shape before it takes the largest across replacing drivers ([section 4](#4-one-compiled-rig-for-every-reader)). Nothing is refused. | 6 |
| 2 | Keys a quarter turn either side of rest have two shortest arcs, and the midpoint depended on the sign a key was written with. | Keys are stored with one sign and interpolate along the rest-side arc. The bound then holds for every key under a half turn, so the special case for large keys is gone ([section 2](#2-a-pose-clip-is-key-poses-on-joints)). | 5 |
| 3 | A mask that leaves out the tip does not protect the bones a solve writes. | An effector's share is taken over every shape it writes, and an owner's mask must cover them ([section 6](#6-an-effector-is-a-final-constraint-and-a-layer-sets-its-influence)). | 7 |
| 4 | Easing toward the layer share left a removed effector pulling, and its latch survived. | The share multiplies at application and is never eased. A zero share releases the latch (section 6). | 7 |
| 5 | In split screen one seat's early pose is drawn in both views, and anything anchored to it reads it. | The guess is off unless the client presents for one seat ([section 8](#8-the-owning-seat-sees-its-own-press-early-as-a-guess)). | 8 |
| 6 | A held button could override a cell for as long as it was held. | The promise is narrowed to a guess that lasts while an input is outstanding and ends at the authority's answer, a new epoch or its age limit. Level channels only (section 8). | 8 |
| 7 | The wake test misses another body's pose and the seat's input, and a woken body can still be held. | The rig's reads are compiled and are the only way to read. Another body's pose and the guess are reads with revisions. A guess in force is never held ([section 7](#7-a-pose-is-packed-again-when-something-it-reads-changes)). | 2, 8 |
| 8 | "Clips after swings" and "layers in declaration order" give different poses, and sorting swings by driver breaks the Moth's sidestep. | Order on a shape is the shape's own. Driver order decides shares only ([section 3](#3-a-driver-is-a-layer-with-a-weight-a-mask-and-a-mode)). | 1, 6 |
| 9 | An aligned tip that is not a bone moves on its own and is counted nowhere. A later solve can move an earlier tip. | The compiled rig lists every writer, the aligned tip among them, and every reader uses it. Later effectors win, the compiler warns, and `error` is read on the final pose (sections 4 and 6). | 1, 7 |
| 10 | The angle rule left out the distance that admits a skin, and keyed slides had no bound. | The distance is computed from the clip turn at its joint and from keyed slides. The fixtures state their reach (section 4). | 6 |
| 11 | A plant could keep a point after its target's cell was cleared, a miss stopped the solve at once, and clearing a cell could not be told from body zero. | The target's identity is checked before the latch, a loss is a fade that reads nothing more, and only a whole number naming an active body selects one ([section 5](#5-an-effector-reaches-its-own-creation-or-a-body-a-rule-chose)). The designation register is not a cell (section 5). | 7 |
| 12 | The sketch's finger started curled, and its effector asked for a share on a shape outside the driver's mask. | A pose with no turn is rest and the sketch writes it. A driver's unwritten mask includes what its effectors write ([the sketch](#a-worked-sketch)). | 5, 7 |
| 13 | A clip that does not loop restarts when a time driver's phase wraps. | A clip is held or looping, with a stated position rule for each. A phase that wraps may play only a looping clip whose period divides the span it wraps over (section 2). | 5 |
| 14 | Two "unchanged" contracts differed from the code: the hinge order, and what counts as rigid. A relative turn would gain a half turn crossing the frame. | The hinge rule is changed on purpose to joint first, and no shape in the tree has both a joint and a swing. Rigid means no writer, with the other conditions kept. Turns are stored as axis and angle (sections 2 and 4). | 1, 5 |
| 15 | An effector's weight and the time it runs are separate decisions. | Effectors are final constraints in declaration order. A layer sets influence only (section 6). | 7 |
| 16 | Whether motion may run on the GPU, and which cells a remote client is sent, both depend on everything a pose reads. | Both are decided from the compiled reads. The manifest registers a body target's reference as truth (sections 4 and 7). | 2, 7 |
| 17 | Listing a large rig does not show that it runs: several tables and loops clamp for themselves, and the mesh format has its own limit. | Every table is sized from the compiled rig, with a check that drives the entries past each old cap. The mesh limit is reported at compile time ([section 9](#9-the-caps-come-off)). | 3 |
| 18 | Frames cannot become clips by adding a cue clock. | The step is withdrawn. The three missing pieces are listed ([what this design does not cover](#what-this-design-does-not-cover)). | none |
| 19 | The channel's level does not exist until the next tick takes the press in. | The guess starts at that tick. The remaining wait is stated, measured, and given a named follow-up (section 8). | 8 |
| 20 | Three of the Moth's shapes are at the swing cap, not four. | Corrected. | none |
| 21 | A pose never drawn can be outside the sphere a body is judged unseen by. | The sphere takes a reach derived from the compiled rig (section 7). | 2 |

### The review of steps 7 and 8 as built

An adversarial review of the built steps 7 and 8 raised the findings below.
Each was opened in the code before it was answered.

| # | The finding | What the code showed | What changed |
|---|---|---|---|
| R1 | A guessed pose moves part-anchored and carried-screen cameras, so the pointer ray a rule reads can come from a guess. | Confirmed: the guess was packed into the shared transform table, which `WorldEntityPartResolver.TryPackedPose` and `TryCarriedScreen` read for the seat's camera. | The guess is a separate layer only the seat's own view reads (section 8); every camera reads the shared pose. |
| R2 | The cell that selects a reached body is not a scene input, so a restricted stream could share a scene that reveals it, and a presentation-tier peer is never sent it. | Confirmed, and wider: no state cell any rig reads was a scene input or a presented row; only bound values, fields, size sources and clocks were. | Both take every state read of every creation's rig (`WorldSceneInputs.CreationRows`). A restricted row still crosses only to a recipient its policy admits, so no binding check was added. |
| R3 | The guess is offered with an away world's body index to the boot scene; remote and portal scenes never draw it; and a forwarded authority answers under a principal the client never looks up. | Confirmed, all three. | The guess is offered only to the scene of the world the seat's route names, routed scenes included; the client reads its publication and answer under the owner the authority publishes for its body. |
| R4 | The guess reaches other views, session windows sharing the scene, camera screens and `body.rig`. | Confirmed for the views, windows and screens, which all drew the shared table. | The overlay reaches the seat's own view alone. `body.rig` prints the layer's drivers only to that seat or the operator. |
| R5 | A stun, a Drive denial or a reach refusal returns before the answer is written; the answer was written before the body and epoch checks; `leadSeconds` has no maximum. | Confirmed for the first two. | The answer is written first, for the current epoch's own-route batches only. `leadSeconds` stays uncapped: it changes only the owning seat's picture. |
| R6 | A restored checkpoint or a cut drops the answer and revives an answered guess; the guess's age reads the wall clock. | Confirmed. | The producer forgets an edge once its answer is presented, and the age is presented time. |
| R7 | A body-disclosure observer is handed every owner's answers. | Confirmed for the observer door; a colocated endpoint is the same process's own client. | The observer door strips owned records (`WorldOutputHub.Observed`), and a leak's excerpt names answers. |
| R8 | A parked attached reader keeps rating its last target, and a reader later in the pool lags one frame. | Confirmed for the first. | A parked row clears its packed root. The one-frame lag stands. |

## At the scale of an MMO

Every choice above is presentation, evaluated by each client for the bodies it
draws. A clip travels inside its creation's document, and what plays it is
state the authority already discloses. Two things are new on the wire. A body
index cell that an effector reads is registered for delivery like any other
cell a rig reads. Each owner receives the last input sequence the authority
answered for it, which only that owner is sent.

A world places its own authority, and a guess follows the seat: it is offered
only to the scene of the world the seat's route names, local or remote, and is
answered under whatever owner that authority knows the seat's body by. A
crossing rebuilds the seat's source registry, and the guess reads nothing until
the world the body now stands in has admitted the seat. So a body that crosses
a portal starts neutral under the new authority's mapping.

A crowd pays for terms evaluated on bodies that are posed. The existing rate
tiers already hold a far body's pose between evaluations, and a term at zero
share is skipped, so a performer who is not dancing does not pay for the
moves. A hand that reaches for another body raises that body's rate to its
own and no further. The count that grows fastest with this feature is
transform slots, from segmented hands, which the plan's crowd stage reads
against its count at 8 performers.

Three things do not scale by themselves and are named so that no one assumes
them. A clip names shapes, so it belongs to one creation. Playing one body's
motion on another body is retargeting, which this page does not design. A
visiting look brings rig work with no admission budget
([section 9](#9-the-caps-come-off)). And a guess is one seat's: a spectator
client, a second seat and a replay all draw the disclosed cell.

## Steps, each with its check

Each step lands alone. Every check is a headless reading unless it says
otherwise. `body.rig` gains three readings on the way: an effector's `error`
and its applied weight, and the source a led driver read.

| Step | What is built | The check that shows it worked |
|---|---|---|
| 1. The compiled rig (built) | Swings and slides lower to terms. The writers of each shape are compiled, the aligned tip among them. The readers take the compiled rig, and the converted document carries no motion facets. The joint rule moves to one home, joint first. No new capability. | `puck references` on a shape's swings and slides lists only the canonicalizer, the frame conversion, the sculpting builder, the lowering and the generated document table. `puck creation stats` on the Moth prints the same seam lines as before, and `puck motion --reproduce` on her walk and on a strafe gives the poses it gave before: the step claims to change nothing for her, so equality is the right tool. A fixture with a tip that is not a bone, has no swing, aligns, and joins its parent by a smooth blend: `creation stats` shows the tip with a slot of its own and its seam refused a skin. The gallery's reach exhibit, whose arm is moved only by an effector, and a fixture look with only a part follower both evaluate as rigs, and the exhibit still reaches its point. A fixture bone with a `joint` and a swing about another point solves about the joint. |
| 2. The reads (built) | The compiled list of reads, the one view the pose pass reads through, the wake test over it, the manifest registering from it, the rate a read body inherits, the derived reach. | With the rate tiers on, a hand on another body through the existing `body` target: move only the target, and the reaching body's count of evaluated poses rises in the next frame. A fixture whose hand a state cell extends by a slide, held out of every view at rest: setting the cell with the root and the camera still evaluates it and the hand appears. A remote recipient allowed a driver's cell reads the pose, and one denied it reads rest. |
| 3. The caps (built) | Every table sized from the compiled rig. The four refusals and the constants deleted. Counts in `creation stats`, two new work counters. The mesh member limit checked at compile time. | A fixture with 40 drivers, 12 swings and 5 slides on one shape, and 10 effectors passes `puck compile --validate`. Driver 40 names a `dynamics` row and `body.rig` shows its phase trailing its target on a step. Effectors 9 and 10 solve to an `error` under a tenth of a millimetre, and that pair, sharing a plant driver, step one at a time at rest. The shape's delta shows all five slides. A group with 256 moving members is refused by `compile --validate` with the group's name. The debug world boots. |
| 4. Parts as sets (built) | `shapes` and `except` on a part, compiled by the part compiler. | `puck creation stats` prints each part's shapes for a fixture with a nested part and an `except`. An unknown shape or part id is refused by name. A look's existing `partDynamics` on a one-shape part reads as before. |
| 5. Clips, adding (built) | The `clips` list, axis and angle keys, the stored sign, the arc routine in `Puck.Maths`, `ease`, held and looping positions. | On a fixture with a two-key clip on a state driver, `body.rig <body> <shape>` reads the key's turn when the cell is at the key, half of it midway under `linear`, and half again at half weight. Keys of plus and minus 90 degrees about one axis read zero at the midpoint, and the same with the second key written about the opposite axis. A key written in the author frame about +X reads the same angle about the engine's −X, and a pose with no turn reads a zero delta. The sketch's finger reads rest at a cell of zero, part way at one half and its key at one. A held clip on a `time` driver is refused by name, and so is a looping clip whose period does not divide one turn. A looping clip crosses its seam, and the point where its driver's phase wraps, with no jump, forward and at a negative cadence. A held clip on a state driver holds its last key when the cell passes it. Validation refuses a clip on a folded shape, an unknown shape, and a composed turn of a half turn or more. A test holds the track bound over random keys under a half turn. |
| 6. Masks and replacing (built) | `parts` and `mode` on a driver, the shares, the seam bound's clip rules. | On a fixture where a `time` swing and a replacing clip pose one shape: at full weight `body.rig` reads exactly the key's turn at every tick, at half weight the swing reads at half amplitude, and a shape outside the mask is unchanged. A stance clip about X and a sway swing about Y on one shoulder give the same pose whether the sway's driver is declared before or after the stance's, at equal shares. The Moth's strafe reads as in step 1 under a replacing layer at half weight on her arms. Seam fixtures, each read from `creation stats`, on a 1 mm part at its joint inside a static box, smooth 14 mm, so the seam's box reaches 26 mm from the joint: five replacing 15 degree poses on five drivers are admitted (6.78 mm against 7); the same at 17 degrees are split (7.68 mm); the same five 15 degree poses on adding drivers read 75 degrees and are split; a clip that only slides 8 mm is split; two 35 degree clips on one driver and joint read 70 degrees and are split; five 13 degree tracks on one driver read 65 degrees and are split. A test samples weights and positions of random rigs and finds no pose past the bound. |
| 7. Reach and influence (built) | The `shape` target and its refusals. `reference` and `part` on the `body` target, the identity check, the fade. `weightBy` and blend times on an effector, its share over its writes, the release. | Two arms, one swinging on `time`, the other's fingertip aimed at a point on it: `error` on the final pose stays under a tenth of a millimetre for 200 ticks. The three refusals fire by name. With a cell holding a second body's index, the hand reaches that body's part and follows a change of the cell in the next frame. Clearing the cell reads `target=none` in that frame, the weight eases, and moving the former target during the fade moves the hand by nothing. A cell of zero selects body zero. A slot that is reused releases the latch. A planted leg under a replacing layer at weight one that covers the thigh and shin and leaves out the foot: the applied weight reads zero in the first such frame, with a blend-out of half a second authored, and the knee reads exactly the clip's key. After that layer leaves and the body has walked a metre, the foot plants at the new ground point. An owner whose `parts` leave out a bone is refused, naming the bone. A planted hand under an adding recoil on a later layer keeps its `error`, and the elbow shows the recoil. Two effectors on one shoulder: the later holds its `error`, the earlier reports its own, and the compiler warns. A recipient allowed the index cell sees the reach, and one denied it sees rest. |
| 8. The guess (built) | `lead` and `leadSeconds` on a state driver, the outstanding test, the owner's acknowledgement written before every refusal, the guess layer and the view-local pose overlay that draws it in the seat's own view alone. | A log records, for each press, the times of capture, the tick that takes it in, the cell's change and each presented frame. On a one-seat client a scripted press at a stated offset reads `layer:` source `lead` and a phase above zero in the first frame after the tick that takes it in, and source `cell` once that tick is presented, while the shared pose's driver reads `cell` throughout. With two seats on the client no layer exists, both views draw one pose, and an anchor on the finger reads that pose. In a windowed batch, the seat's own view draws the press early and a camera screen, a session window and a named camera filming the same body draw it at the cell. A second client watching the same body reads `cell` throughout. A rule that refuses the press while the button is held for 60 ticks: the source returns to `cell` when the answer is presented and the phase reads zero for the rest of the hold. A cell a rule holds at one with the channel idle reads one. A mode switch during an outstanding press ends the guess in that frame. `lead` on a relative or a text channel is refused by name. A body at rest with the rate tiers on is evaluated in the frame after the tick that takes the press in. With answers delayed by a stated number of ticks, the guess holds until the answer is presented, with no return to the cell between, and ends at `leadSeconds` when no answer comes. `puck test --reproduce` on the scripted run gives the same authority hashes with `lead` authored and removed. Read so far: the debug fixture world `worlds/debug/lead-fixture.puck` (and `lead-plain-fixture.puck` with the `lead` removed) with `lead-tests.puck` give the authority half (the take, the refusal held for 60 ticks, the mode switch, equal hashes and snapshots with and without `lead`); `CreationLeadGuessLawTests` gives the pose half with a faked guess (the layer reads `lead` while the shared rows stay bit for bit as they were, the overlay names the body's slots and rows past the pool's, an unanswered edge ends at `leadSeconds` of presented time, a view of the shared pose refuses a guess, and a rate tier holds the shared pose while the layer poses the press); `InputAnswerLawTests` gives the answer (a gated refusal answered in its tick, and a stale or misdirected batch leaving the current answer). A headless World poses no creation (`body.rig` prints `creation-rig=none`), but `body.rig` reads the seat side there (`guess:<creation>.<driver>[<channel>]=`, `WorldSeatInput.ReadGuess`): `lead-fixture.puck` reads the answered state after a press (the answer's tick presented), the guess ending in the frame a mode switch publishes a new epoch (`NotLive`, outstanding sequence 0) and `lead-seats-fixture.puck` reading `off(...)` for both seats. A headless World presents every tick whole, so it never reads an edge outstanding; the in-force, delayed-answer and `leadSeconds` rows and the overlay's picture need a presentation fraction below one (a windowed World) or a remote authority. |
| 9. The Moth's hand | The plan's hands lane: joints, segments, the stance, the vents. | The plan's stage 2 row, in the lead's windowed batch. |

## What this design does not cover

- **Timeline frames stay as they are.** Two homes for authored poses is a
  duplicate, and the obvious step turns frames, a look's `poses` and its
  `cues` into clips. Three things stand in the way, and none is designed
  here. A frame moves a parent and leaves an unkeyed child where it was,
  where a joint turn carries the child, so each such child would need a turn
  that undoes its parent's. A frame's transform may be bound to state, and a
  key is a literal. When two of a look's pose cells are set the first row
  wins, and layers have no such rule. Frames are one of the compiled rig's
  writers, so every reader sees them. Whoever opens their retirement owns
  those three answers.
- **A clip that starts when a cue fires.** A blink or a flourish needs a
  driver signal that counts seconds since a named cue last fired for a body.
  The plan already asks that one delivered cue lane feed both the speakers and
  the pose (gap I). The delivered-cue design owns that signal. It is the
  level a held clip needs for a move that plays once.
- **A guess from the moment of the press.** The wait from a press to the tick
  that takes it in stays
  ([section 8](#8-the-owning-seat-sees-its-own-press-early-as-a-guess)).
  Closing it means the input router can resolve a press for display, through
  the same binding pages, toggles and focus rules, without taking it from the
  queue the tick reads. That is one resolution run twice and never a second
  copy of it. Step 8's log shows the wait, and the plan's stage 2 sitting
  decides whether it is opened.
- **A guess in split screen.** It needs a pose one view draws and another
  does not, for a body and for everything anchored to it. Prediction's design
  is the place for it.
- **Playing one body's clip on another body.** It belongs to the plan the
  owner's chosen concept opens
  ([the recorded self](../play-expressions.md#the-engine-gaps-the-memos-share)).
- **A limb that touches something in the simulation.** Everything here is
  presentation. A blade or a solid that the simulation must meet needs its
  own motion program on the fixed tick before contact can follow it, however
  it is drawn. Strikes at the limb are
  [readable interaction](../readable-interaction.md#build-order)'s steps 1
  and 2.
- **A moved body.** Turning a body and moving it along a path is the timed
  turn lane (gap F). A dance clip poses the body around a capsule that lane
  moves.
- **How closely a GPU evaluation of terms agrees with the CPU's.** The
  rendering plan's decision 12 lists it as open and owns it.

## What the research round could change

The plan's research round has no synthesis in the tree. Two of its topics bear
on this page: hand and finger animation, and procedural against authored
layering. The statements here about common practice (layers with masks that
replace or add, joint-space keys, inverse kinematics to finish a contact) are
general knowledge and were not checked against a source. The round's reviewer
should weigh four choices in particular:

1. **The default mode.** This page keeps `add` as the default because every
   driver in the tree adds today. If shipped practice shows authors reach for
   replacing first, the default changes and no mechanism does.
2. **The ease vocabulary.** Three eases and a response row on the driver may
   be too few for stylized motion. A richer set is a change to key data only.
3. **Fingers by solve or by pose.** This page lets a fingertip finish on its
   target by a solve. If the round finds that a curl parameter with authored
   poses reads better at chase distance, the vents use clips alone and the
   `shape` target stays for hands on hips and on other bodies.
4. **Constraints last, or solved within a layer.** This page keeps every
   solve after every layer. If the round finds that shipped rigs rely on a
   later layer's motion surviving a constraint, the alternative is to solve
   at each layer's boundary, which changes
   [section 6](#6-an-effector-is-a-final-constraint-and-a-layer-sets-its-influence)
   and the same-frame target rule with it.

## Systems considered

The capability searched for, what was read, and why nothing existing owns it.

- **Holding and blending a pose.** Read `FrameDocument`, `WorldLookMotion`,
  `CreationWave` and the pool's pose selection. The five candidates are the
  table in [how a pose can be held today](#how-a-pose-can-be-held-today).
- **A named set of shapes.** Read `CreationPartDocument`,
  `CreationPartCompiler`, a shape's `group`, and `ChainDocument`. A group is a
  bake partition and changes the mesh. A chain is a list of shape ids with a
  goal and a pole that nothing reads at run time except one bound that gives
  up when a chain exists, and no world authors one. A part is the published
  name a look and an anchor already use, so it is extended.
- **A layer limited to some shapes.** Read `CreationDriverDocument` and
  `WorldGaitDrivers.Advance`. A driver's weight is one number for every shape.
  `weightBy` reads an earlier driver through a curve and has no notion of a
  shape.
- **A bound on authored motion.** Read `CreationMotion.Envelope` and the
  plans that consume it. It is extended and not duplicated.
- **An arc between two rotations that keeps their signs.** Searched
  `Puck.Maths` and the sources for interpolation between rotations. The
  float helpers hold `VectorFunctions.ShortestArc`, which turns one direction
  onto another, and the fixed-point type has `FixedQuaternion.Slerp`. Neither
  is a float arc that leaves the signs alone, so step 5 added
  `VectorFunctions.Arc` with `TurnAngle`, `ScaleTurn` and `WithPositiveScalar`
  (two inline turn-angle formulas moved onto `TurnAngle`, and the three
  `Quaternion.Slerp` calls from the identity that scaled a turn, in the
  client's correction easing and the effector's align and replant, onto
  `ScaleTurn`).
- **A share of a layer.** Read `WorldGaitDrivers.ComposeDelta` and the
  effector weight. Nothing scaled a driver's weight per shape; the share is
  `CreationRig.Share`, and step 7's effector share is to call it.
- **One list of what moves a shape.** Read the owner rule then in
  `CreationMotion`, `CreationMotionPlan`, `WorldRigidAssets.IsRigid` and the
  effector code. `CreationMotionPlan` came closest: it already added a look's
  followers to the owners, but was built on that owner rule and inherited what
  it missed. The compiled rig (`CreationRig`) replaces the question both
  asked.
- **A record of what a pose reads.** Read `WorldStateLease`, the pool's wake
  test and `WorldPresentationManifest`. The lease records state cells as they
  are read. The pool records that a part was read, on the target's side
  only. The manifest walks the document types by hand. No one list serves the
  three.
- **A target on a body.** Read `CreationEffectorTargetDocument` and the
  resolve in the effector code. The pose read a `shape` target needs already
  exists for anchors (`WorldCreationInstances.TryShapePosition`), and the
  other body's part read exists too
  (`WorldCreationInstances.TryBodyPartAuthoredPose`). The designation
  register (`WorldTargetDesignation`) was traced and is not delivered to
  clients.
- **Presentation reading a seat's input.** Read `WorldSeatInput`,
  `WorldInputProducer`, `InputRouter.SnapshotForTick`, `WorldSeatViewState`,
  `IWorldStampSource`, the input decision and the presentation contract. The
  seat camera is the precedent. No driver signal reads input.
- **An answer to an input.** Read `WorldInputPublication`, `WorldInputBatch`
  and `WorldInputOwner`. The authority publishes to an owner at an epoch
  boundary and keeps its last taken sequence to itself. The remote link
  acknowledges that a submission arrived, which is a different fact.
- **Counting rig work.** Read `WorldCreationWork`. It counts drivers and
  slots, and no terms or solves.
- **Drawing one stamp differently in one view.** Read `SdfViewSnapshot`,
  `SdfStampFade` and `frame/sdf-stamp-fade.hlsli` (a view-local slot range,
  the only per-view stamp mechanism, which hides a stamp and moves none),
  the dynamic-transform table and its history copy (`SdfWorldTables.Motion`),
  the dresser's draw matrices (`SdfMeshDresser`) and the mesh vertex door
  (`sdfMeshWorldVertex`, `sdfMeshSkinSlotsOf`, `sdfMeshPreviousPosition`). The
  pose overlay is the fade's sibling: a view-local slot range read from other
  rows of the same table, so the history copy, the uploads and the bindings
  serve it unchanged. A second residency for the seat's view, or swapping rows
  between views on the queue, were rejected: the first doubles a scene, and
  the second would race every pass that reads the table.
- **Holding a second pose of a body.** Read the pool's registrations
  (`WorldCreationInstances.Registration`), the rigid copies (`WorldRigidAssets`)
  and the reseeding path. A registration is the one evaluator of a pose and
  carries every state the evaluation advances, so the guess layer is a second
  registration packed by the same `PackRegistration`, kept out of the pool's
  lookups.
