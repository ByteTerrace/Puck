# Pose clips, body parts and reach

This is the design for three rig gaps the
[instrument and dance plan](../instrument-play.md#the-rig) names: poses that
do not blend and layers that cannot be masked (gap A), an effector that cannot
aim at its own body (gap B), and a press that reaches the rig a tick late
(gap C). It also names the shape mask once, as a body part, for every later
[play expression](../play-expressions.md#what-the-bards-first-wave-should-take-from-this),
and decides what happens to the rig's caps.

Nothing here is built. The page is a design for review, and its
[steps](#steps-each-with-its-check) are the build lane's order. It was written
from the code and before the plan's research round, so
[one section](#what-the-research-round-could-change) says which choices that
round could overturn.

## Summary

- **The swing cap already binds.** The Moth's thighs and pelvis use all 8 of
  the swings a shape may carry, and her upper arms and torso use 7. A dance
  move cannot add one swing to a leg today.
- **A pose clip is key poses on joints.** Each key gives a shape a turn about
  its joint, measured from rest. Keys interpolate, and a driver plays the clip:
  the driver's phase is the position in the clip and its weight is the blend.
- **A driver becomes a layer.** It gains a mask of body parts and a mode. A
  replacing layer fades out the layers declared before it, on its parts only,
  by its own weight. "The gait keeps the legs and gives up the arms" is then
  one line on the layer that takes the arms, and no earlier layer is edited.
- **A body part is a named set of shapes,** defined once in the creation and
  used by every mask. It extends the `parts` list a creation already has.
- **Swings, slides and clips lower to one list of motion terms.** Seven places
  read a creation's swings today, among them the test that gives a shape its
  transform slot and the bound that keeps a skinned seam from tearing. A third
  kind of motion added beside swings would have to be remembered in each. The
  list removes that.
- **An effector gains two targets:** a point on a shape of its own creation,
  and a body whose index is read from a state cell, optionally at one of that
  body's parts. An effector also joins a layer, so a foot plant yields to a
  dance move without a rule.
- **The seat's own presentation may read its own input early.** A driver whose
  signal is a state cell may name the input channel that leads it. Only the
  screen of the seat that owns the body shows the lead. The authority, other
  players and replays never see it. This does not wait for prediction.
- **The caps on swings, slides, drivers and effectors are removed.** None
  protects a representation. Counts are reported, and work is counted.
- **Fingers and wrists get joints.** A clip needs a joint on a shape and no
  swing, so the Moth's hand can become segments on pivots.

## Where the rig stands

### The caps, and what uses them

| Limit | Declared by | In use on the Moth |
|---|---|---|
| 8 swings a shape | `ShapeDocument.MaxSwings` | Thighs 8, pelvis 8, upper arms 7, torso 7, forearms, shins and boots 5 |
| 4 slides a shape | `ShapeDocument.MaxSlides` | A few shapes carry one |
| 32 drivers a creation | `CreationDocument.MaxDrivers` | 18 |
| 8 effectors a creation | `CreationDocument.MaxEffectors` | 2, her feet |
| 8 bones a chain | `CreationEffectorDocument.MaxChainBones` | 2 |

The swing counts are a count of `swing(` calls under each shape of
[the Moth's document](../../../worlds/debug/avatars/moth.puck), and the others
are the lengths of its `drivers` and `effectors` lists.

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

### Who reads authored motion

A creation's swings and slides are read in seven places, and each decides
something different:

- `CreationMotion.Owner` decides whether a shape moves on its own and so gets
  a transform slot. `CreationMotionPlan`, `CreationMotion.HasPartMotion` and
  the tight bound of a blend group all rest on it.
- `CreationMotion.Envelope` bounds how far two sides of a smooth seam can
  move apart, which decides whether the seam is skinned or split
  ([the admitted envelope](../moving-blends.md#admitted-motion-envelope-f1)).
- `CreationMotion.MovingBlends` decides which seams join shapes that move
  apart.
- `WorldGaitDrivers.ComposeDelta` evaluates the motion each frame.
- `WorldRigidAssets.IsRigid` decides whether a creation needs a rig at all.
- The effector code takes a bone's hinge from its first swing's pivot
  (`WorldCreationInstances.Effectors`).
- The world validator checks that a wave's curve row exists.

This is the hazard the design is built around. A new kind of motion that the
slot test misses never moves. One that the seam bound misses lets a seam tear
with no warning
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
  that hangs from it through `parent`. Omitted, it is the anchor shape.
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
- A pose names a shape and gives it a `turn`, a rotation about the shape's
  joint measured from its rest pose, and optionally a `slide`. Both are in the
  author frame and convert as a swing's axis does.
- The joint is the one an effector bone already uses: the shape's `joint`,
  else the pivot of its first swing, else its own position. That rule moves
  from the effector code to the shared motion home, so both read one copy.
- `wrap` makes a clip loop.

The driver's phase is the position in the clip, and each shape reads the keys
that name it. Between two keys the turn follows the shortest arc and the slide
a straight line, shaped by `ease`: `linear`, `smooth` or `hold`. Before the
first key and after the last, the pose holds. The driver's weight then scales
the result from rest: a weight of one half gives half the turn.

A clip's turn composes after the shape's own swings, and where several clips
pose one shape they compose in the order their drivers are declared. A turn
chains under the parent exactly as a swing does, so a keyed forearm carries the
hand.

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
  are removed in [section 8](#8-the-caps-come-off).

**Still to find out.** Whether `.puck` has a spelling for a rotation from an
axis and an angle. The document stores a quaternion, as every rotation in a
creation does, and an author should not have to compute one. The build lane
reads the `puck-dsl` skill at step 4 and adds the spelling if it is missing.

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
  itself poses.
- `mode`: `add`, the default and today's behaviour, or `replace`.

A replacing driver with weight `w` scales the contribution of every driver
declared before it by `1 - w`, on the shapes in its mask and nowhere else. So
on any one shape, layers composite in declaration order: a driver's share is
its own weight times `1 - w` for each replacing driver declared after it that
covers the shape. A driver declared after a replacing one is not reduced by
it, so an author places a breath sway above or below a stance by where the row
sits.

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

### 4. One list of motion terms for every reader

**The problem.** The seven readers above. A clip added as a third facet beside
swings and slides needs a third branch in each, and a forgotten branch is
silent.

**What we propose.** The authoring assembly compiles a creation's motion once
into a list of terms for each shape, and every reader takes that list and
never the document's facets. A term is one driver's contribution to one shape:
a turn about a pivot or a slide along an axis, with the largest turn and the
largest slide it can produce at full weight, and whether those are finite. A
swing lowers to one term, a slide to one, and a clip to one term for each
shape it poses. The list also holds each driver's mode and mask as a set of
shapes.

From that one list:

- a shape owns a slot when it has a term, lies on an effector's chain, or a
  frame moves it;
- a creation is rigid when no shape has a term;
- evaluation walks the terms, and skips a term whose share is zero;
- the seam bound reads each term's limits.

This is also the form the rendering plan asks for and has not built: one
compiled motion program from which a CPU evaluator, a GPU kernel and a bound
all derive
([decision 12](../rendering-research.md#12-keep-motion-and-placement-as-authored-programs-at-zone-scale),
[authored motion on the GPU](../visibility.md#v2-instance-updates-proportional-to-change-and-authored-motion-on-the-gpu)).
A clip term reads presentation time, state cells and constants, so a shape it
poses stays eligible for that kernel.

**The seam bound with clips.** The bound today adds every swing's amplitude
along the two chains to a seam. Three rules extend it. They are this page's
derivation and have not been run; step 5 holds them with a test.

1. A clip term's turn is at most its largest key turn, when no key of that
   shape turns more than a quarter turn. Two keys that small lie in one
   hemisphere of rotations, and along the shortest arc between them the turn
   from rest never exceeds the larger end. A term with a larger key is counted
   as a half turn, which no skinned seam admits.
2. A term's turn scales with its share, so the turn of a shape is at most the
   sum of its terms' limits times their shares.
3. On one shape, the shares of clip terms played by replacing drivers sum to
   at most one, and all of them turn about the same joint. Together they
   count as one turn the size of the largest. Ten fingering poses on a
   segment cost the bound one pose and not ten.

Swings and terms of adding drivers count in full, as swings do today. Counting
them in full while a replacing layer is reducing them is safe, and loose.

**What it costs or risks.** Rule 3 is what lets a hand with many poses keep a
skinned seam at all, and it rests on every clip term of a shape sharing one
joint. A swing keeps its own pivot, which the pelvis needs (it leans about the
ground and turns about the hips), so swings stay outside rule 3.

**Still to find out.** How the published form of the envelope, which the
[moving blends plan](../moving-blends.md) still owes to the bounds for skinned
draws
([virtual geometry](../virtual-geometry.md#d2-bounds-for-skinned-and-multi-slot-draws)),
reads the list. That plan's owner takes it once step 1 lands.

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

Two refusals keep the new kind well defined, both by name at validation:

- The target shape may not be a bone or the tip of the effector's own chain,
  nor hang from one. Moving the chain would move the target.
- No effector declared later may have a bone at or above the target shape. An
  effector reads only poses that earlier effectors have finished, the rule
  parents and drivers already follow.

**Why two kinds and not one.** A point on the creation's own shape can be read
in the same frame, exactly. Another body's part comes from that body's last
pack, which may be this frame's or the one before, so it can be a frame old.
One kind would hide that difference.

**What it costs or risks.**

- A cell holds an index and not a generation. If a rule leaves a stale index
  in the cell and the slot is reused, the hand reaches the new occupant. The
  effector already requires the body to be active; clearing the cell is the
  rule's job.
- A part follower (`partDynamics`) moves a shape after the solve. A follower
  on the target shape or on the chain breaks the contact. The world validator
  warns where a look does this.
- A recipient who may not read the cell resolves no target, and the effector
  eases out, as it does today for a probe that finds nothing.

**Still to find out.** Whether the register that the designate effect fills
(`WorldEffect.Designate`) reaches the client's state mirror as a cell a
reference can name. This lane did not trace it. The build lane reads it at
step 6. If it does not, the first build uses a numeric cell a rule writes, and
the register read becomes a named follow-up in the plan of whichever
expression needs it.

### 6. An effector belongs to a layer

**The problem.** Effectors run after every driver and know nothing of layers.
When a dance move replaces the legs, the foot effectors still pull the boots
to the ground and fight the clip. An author can gate them on a state token,
but a gate has no negation, so "not dancing" needs its own cell and rule.

**What we propose.** An effector takes the weight vocabulary a driver has.

- `weightBy`: the record a driver's `weightBy` uses (`CreationDriverCurveDocument`).
  The level the effector's weight eases toward is the curve's height at that
  driver's phase, times that driver's share on the tip shape.
- `blendInSeconds` and `blendOutSeconds`, as on a driver. Today an effector
  eases over a fixed 0.15 seconds (`CreationDriverDocument.WeightSeconds`).
- An effector without `weightBy` belongs to the bottom of the stack: its
  weight is multiplied by `1 - w` for every replacing driver that covers its
  tip.

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

### 7. The owning seat sees its own press early

**The problem.** Gap C. A driver reads state. A press is processed on the next
tick, the snapshot is then presented between that tick and the one before it,
and only then does the finger start. With a remote authority a round trip is
added. For a play style judged on timing, the player's own hand answering late
is the feel the owner asked this feature to get right.

**Options.**

| Option | What it gives | What it risks |
|---|---|---|
| Wait for prediction | One general answer: the client runs the authored rules ahead of the authority | Prediction has no design and is the last step of [readable interaction](../readable-interaction.md#build-order). Stage 2's check ("on its vent in the frame after its press") cannot pass until then |
| A driver may name a channel that leads its state cell (recommended) | The owner's own screen answers in the next frame, in both topologies. Nothing is speculated in the simulation, so nothing is rolled back | The channel and the cell can disagree. Prediction later makes it redundant |
| A free-standing `input.<channel>` signal | The same speed | A pose only the owner can ever see. A look would differ by viewer in content, not only in timing |
| Run chosen rules on the client | Closer to prediction | It is prediction, built for one case first |

**What we propose.** A driver whose signal is a state cell may carry `lead`,
the name of an input channel. While three things hold, the driver's phase
follows the channel's current level in place of the cell:

- the body is controlled by a seat on this client;
- the authority has admitted that seat's input (`WorldSeatInput`);
- the channel is live in the mapping the authority last published.

Otherwise the driver reads its cell, exactly as today. The lead moves the
phase only. A gate still reads stored state, because which controls are live
is simulation state
([modes are simulation state](../../decisions/input-channels.md#modes-are-simulation-state)).

The rule in one sentence: a lead changes when the owning seat's screen shows a
pose, never which pose is shown, and never anything another viewer or the
authority sees.

**Why this is allowed.** Presentation may use its own values so long as
authoritative state does not change
([simulation and presentation](../../decisions/engine-design.md#simulation-and-presentation-have-different-contracts)).
The seat's camera already does this: its look is client state that the seat's
own input moves between ticks (`WorldSeatViewState.PresentedLook` says a
pointer nudge between ticks "shows at once"). The client already holds the
seat's source levels and the published mapping, and answers questions about
them (`WorldSeatInput.ActionStatus`).

**The attacks, and what closes each.**

- *A modified client lies about its lead.* It changes that client's own
  picture and nothing else.
- *Presentation feeds the simulation.* The server cannot reference the client:
  the profiles in `build/Architecture.props` forbid it in both directions, and
  a build that tries fails. The lead is read in one place, the driver advance.
- *Two viewers disagree.* Only about when, and only on the owner's screen.
- *Hidden information.* A seat's own input, shown to that seat.

**What it costs or risks.**

- The lead is right only where the cell mirrors the channel. If a rule holds
  the cell longer than the press, the owner's finger lifts earlier than others
  see. That is the author's to keep true, and `body.rig` reports which source
  a driver read.
- The lead must be read through the registration's state lease
  (`WorldStateLease`). A registration at rest repacks only when something its
  lease reads has changed. A lead read any other way would leave a resting
  body's finger frozen until something else woke it.
- When prediction lands, the driver's state read goes through the predicted
  view and `lead` is deleted from the vocabulary and from the worlds in the
  same change. It is small enough to remove.

**Still to find out.** Whether a scripted seat's presses (`player.signal`)
pass through the client's producer, so that a motion scenario shows the lead.
Step 7's check answers it. This design covers pose only. Whether a seat may
hear its own note early belongs to the delivered-cue design.

### 8. The caps come off

**The problem.** The brief's question, with the reading above: the swing cap
is already full on four of the Moth's shapes.

**What we propose.**

| Cap | Decision | Why |
|---|---|---|
| Swings and slides a shape | Removed | Evaluation walks a list. Nothing is sized by the number |
| Drivers a creation | Removed | The cap is the width of tables each registration allocates. A registration is made for one document, so the tables take that document's count |
| Effectors a creation | Removed | The same |
| Bones a chain | Kept | It sizes solve scratch shared by every effector, and no rig is near it |

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
player's client must do, and no budget for it exists. That is a question for
the plans that own admission, and it is recorded in the hand-back that
delivered this page.

### 9. The hand gets joints

This is authoring on the Moth, for the plan's hands lane, and it is here
because the brief asks what changes. The engine side is already above: a
shape a clip poses needs a `joint` and no swing.

- The palm takes a `joint` at the wrist and parents to the forearm.
- Each finger becomes two or three armour segments, each parented to the one
  before with a `joint` at its pivot. The thumb the same.
- Each segment then moves on its own and takes a transform slot. Two hands add
  about thirty. The plan's stage 2 already counts transform rows for one Moth
  before and after.
- Segments meet at visible pivots and carry no smooth seam, so the small-joint
  seam gap (gap E) stays with the bare hand, as the plan says.

## A worked sketch

This shows the proposed document shape. No compiler has read it, and the
turns are left out.

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
          { shape: "upperArm-left",  turn [...] }
          { shape: "forearm-left",   turn [...] }
          { shape: "upperArm-right", turn [...] }
          { shape: "forearm-right",  turn [...] }
      ] }
  ] }
  { name: "stop-vent-0", driver: "vent-0", keys [
      { at: 0 }
      { at: 1, ease: "smooth", poses [
          { shape: "finger-right-0-a", turn [...] }
          { shape: "finger-right-0-b", turn [...] }
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
cell is set, and the gait, run and jump lose the arms by the same weight. The
vent is two keys on a phase the seat's own press leads. The fingertip's solve
follows that phase, so the tip finishes on its vent as the finger curls. A
dance move that uses the arms is a later replacing driver whose parts cover
them. It fades the stance, the vent and the fingertip together.

## The invariants

| Invariant | What enforces it | Where the guarantee stops |
|---|---|---|
| Every reader of a creation's authored motion sees every kind of it. | The term list of [section 4](#4-one-list-of-motion-terms-for-every-reader) is the only view the seven readers take, and step 1 moves all seven. A test poses random documents and fails when evaluation moves a shape the slot test calls still. | The type system does not stop a new reader from going to the document's swings. Review and step 1's search hold that. |
| A skinned seam never sees motion past the envelope it was admitted under. | The envelope is computed from the term list when the document compiles, and a seam outside it is split, as today. Key turns are literals under a half turn, which the canonicalizer refuses otherwise. A test samples driver weights and phases against the bound. | Effector chains have no bound and are refused a skin, as today. A lead and a response row change a phase, and the bound does not depend on phase. |
| A body part has one definition of what it covers. | Masks hold part ids only, and `CreationPartCompiler.Compile` is the one place that turns a part into shapes. | A part id means something inside one creation. Shared ids are a convention of the package that authors them. |
| An effector reads only poses that are final for it. | Validation refuses a target on the effector's own chain, and a later effector with a bone at or above the target. | A part follower moves a shape after the solve. Another body's part is read from its last pack and can be a frame old. |
| A lead changes when the owning seat sees a pose, and nothing else. | The server cannot reference the client (`build/Architecture.props`, enforced at build). The lead is read only in the driver advance, through the state lease. | A player acts on what they see, which is why input is recorded. A cell that does not mirror its channel shows the owner a different timing than others see. |
| A pose is evaluated again when a value it reads changes. | Every read a registration makes, the lead included, goes through its `WorldStateLease`, and the one wake test reads that lease. | Nothing further is known to escape it. |
| The engine learns no instrument, dance or finger. | Review. The names are a clip, a part, a mode, a lead. | A leaked name is a defect fixed when found. |

## At the scale of an MMO

Every choice above is presentation, evaluated by each client for the bodies it
draws. Nothing new crosses the wire: a clip travels inside its creation's
document, and what plays it is state the authority already discloses. A world
places its own authority, and a lead follows the seat: it reads nothing until
the world the body stands in has admitted that seat's input, so a body that
crosses a portal starts neutral under the new authority's mapping.

A crowd pays for terms evaluated on bodies that are posed. The existing rate
tiers already hold a far body's pose between packs, and a term at zero share
is skipped, so a performer who is not dancing does not pay for the moves. The
count that grows fastest with this feature is transform slots, from segmented
hands, which the plan's crowd stage reads against its count at 8 performers.

Two things do not scale by themselves and are named so that no one assumes
them. A clip names shapes, so it belongs to one creation. Playing one body's
motion on another body is retargeting, which this page does not design. And a
visiting look brings rig work with no admission budget
([section 8](#8-the-caps-come-off)).

## Steps, each with its check

Each step lands alone. Every check is a headless reading unless it says
otherwise. `body.rig` gains two readings on the way: an effector's `error`,
the distance from its tip to its target after the solve, and the source a led
driver read.

| Step | What is built | The check that shows it worked |
|---|---|---|
| 1. The term list | Swings and slides lower to terms. The seven readers take the list. The hinge rule moves to the shared motion home. No new capability. | A search of the sources for readers of a shape's swings and slides finds only the canonicalizer, the frame conversion, the sculpting builder and the lowering. `puck creation stats` on the Moth prints the same seam lines as before, with the same angles and separations. `puck motion --reproduce` on the walk gives the poses it gave before. This step claims to change nothing, so equality is the right tool for it. |
| 2. The caps | Tables sized from the document. The four refusals deleted. Counts in `creation stats`, two new work counters. | A fixture creation with 40 drivers, 12 swings on one shape and 10 effectors passes `puck compile --validate`, and `body.rig` lists all 40 drivers and 10 effectors. The debug world boots. |
| 3. Parts as sets | `shapes` and `except` on a part, compiled by the part compiler. | `puck creation stats` prints each part's shapes for a fixture with a nested part and an `except`. An unknown shape or part id is refused by name. A look's existing `partDynamics` on a one-shape part reads as before. |
| 4. Clips, adding | The `clips` list, its lowering, key interpolation, `ease`, `wrap`. | On a fixture with a two-key clip on a state driver, `body.rig <body> <shape>` reads the key's turn when the cell is at the key, half of it midway under `linear`, and half again at half weight. Validation refuses a clip that poses a folded shape, an unknown shape, or a key of a half turn or more. A test holds rule 1 of the seam bound over random keys and positions. |
| 5. Masks and replacing | `parts` and `mode` on a driver, the shares, the seam bound's rules 2 and 3. | On a fixture where a `time` swing and a replacing clip pose one shape: at full weight `body.rig` reads exactly the key's turn at every tick, at half weight the swing reads at half amplitude, and a shape outside the mask is unchanged. A creation with five replacing 20-degree poses across a 14 mm seam is admitted as a skin, where their sum would split it. A test samples weights and phases and finds no pose past the bound. |
| 6. Reach | The `shape` target and its two refusals. `reference` and `part` on the `body` target. `weightBy` and blend times on an effector, and its place in the stack. | On a fixture with two arms, one swinging on `time` and the other's fingertip aimed at a point on it, `error` stays under a tenth of a millimetre for 200 ticks. Both refusals fire by name. With a cell holding a second body's index, the hand reaches that body's part, follows a change of the cell in the next frame, and reads `target=none` and eases out when the cell is cleared. A base effector's weight reads zero under a full replacing layer that covers its tip. |
| 7. The lead | `lead` on a state driver, read through the lease. | A scripted press at a stated offset inside a tick: on the owning seat, `body.rig` shows the driver's phase above zero and its source as the lead in a frame presented before the tick that carries the press. The same tape replayed with no live seat shows the phase at zero until the cell changes. `puck test --reproduce` on the scripted run agrees with itself. With the body otherwise at rest, the count of posed registrations rises in the press frame. |
| 8. The Moth's hand | The plan's hands lane: joints, segments, the stance, the vents. | The plan's stage 2 row, in the lead's windowed batch. |
| 9. Frames onto clips | A timeline frame, a look's `poses` and its `cues` become clips and drivers, and the frame code is deleted. | Every creation in the tree that authors `frames` compiles and reads as it did in a capture from each side. It waits on a signal that counts time from a cue, below. |

Step 9 is here because two homes for authored poses is a duplicate. It is not
part of the first build.

## What this design does not cover

- **A clip that starts when a cue fires.** A blink or a flourish needs a
  driver signal that counts seconds since a named cue last fired for a body.
  The plan already asks that one delivered cue lane feed both the speakers and
  the pose (gap I). The delivered-cue design owns that reading, and step 9
  consumes it.
- **Playing one body's clip on another body.** It belongs to the plan the
  owner's chosen concept opens
  ([the recorded self](../play-expressions.md#the-engine-gaps-the-memos-share)).
- **A limb that touches something in the simulation.** Everything here is
  presentation. Strikes at the limb are
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
should weigh three choices in particular:

1. **The default mode.** This page keeps `add` as the default because every
   driver in the tree adds today. If shipped practice shows authors reach for
   replacing first, the default changes and no mechanism does.
2. **The ease vocabulary.** Three eases and a response row on the driver may
   be too few for stylized motion. A richer set is a change to key data only.
3. **Fingers by solve or by pose.** This page lets a fingertip finish on its
   target by a solve. If the round finds that a curl parameter with authored
   poses reads better at chase distance, the vents use clips alone and the
   `shape` target stays for hands on hips and on other bodies.

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
- **A target on a body.** Read `CreationEffectorTargetDocument` and the
  resolve in `WorldCreationInstances.Effectors`. The pose read a `shape`
  target needs already exists for anchors
  (`WorldCreationInstances.TryShapePosition`), and the other body's part read
  exists too (`WorldCreationInstances.TryBodyPartAuthoredPose`).
- **Presentation reading a seat's input.** Read `WorldSeatInput`,
  `WorldSeatViewState`, `IWorldStampSource`, the input decision and the
  presentation contract. The seat camera is the precedent. No driver signal
  reads input.
- **Counting rig work.** Read `WorldCreationWork`. It counts drivers and
  slots, and no terms or solves.
