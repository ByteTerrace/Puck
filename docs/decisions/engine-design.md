# Engine design decisions

Puck's design centers on validated documents, an authoritative simulation, and
separate presentation. These choices guide new work; the
[overview](../overview.md) introduces the resulting engine, and
[plans](../plans/README.md) describe proposed extensions.

## Documents describe content

The authored document is the primary content artifact. Authoring tools and
console operations produce the same document model. Content
changes pass through composition and whole-candidate validation before becoming
active. Accepted mutations can be recorded in a journal and replayed through
the normal apply path. Saving and recovery must preserve those semantics.

The running engine is the visual editor. Puck has no separate scene editor or
content-creation tool: selecting, placing, sculpting and inspecting happen
inside a live World, through its console, views and HUD, and produce the same
document an author could write by hand. A missing editing affordance is a
feature of the World, not a reason for a second application; the
[editor plan](../plans/editor.md) lists the ones still missing.

Prefer existing data and operations when they express a new feature clearly.
For example, a body can be controlled through permissions without classifying
its geometry as a player or NPC. An anchor describes a camera's pose, while a
rig describes how it frames a view. These are examples of separating independent
choices; they are not a prohibition on adding a justified type or document field.

The engine provides general mechanisms; game-specific names and policies belong
in authored content. Authority and admission rules must stay explicit when a
new opcode, document shape or addon interface is introduced.

## A document owns the paths it names

A world document is self-contained and movable. Every relative path it authors
resolves beside the document that authored it: a basis, an import, a
neighbour reference, an asset row's source, an addon module, a pipeline or
graph source, a probe track, a machine's content, and the window icon. A
document and the files it names therefore move together, whether they are
copied into a build's output, staged under a state directory, or saved
somewhere new.

The engine's own content is different. Fonts, shaders and probe kinds ship beside the executable and resolve there; the engine ships
no world, and a boot names the asset package it runs with `--world`. The two kinds of
lookup never share a resolver, and neither falls back to the other: a
document-relative path that is missing beside its document is refused, rather
than found by accident in the executable's directory.

A document read from somewhere other than a file, such as standard input, an
in-memory build or a wire delivery, has no directory. It can still name
absolute paths, but a relative one is refused by name at validation. A hosted
document a silo reads from its store takes the directory its host lays the
hosted documents out in, the silo document's `documentDirectory`, when the
silo names one; the same resolver then reads its relative paths beside it.

## Simulation and presentation have different contracts

Replay depends on a fixed code version, a world definition, and recorded inputs.
Simulation uses defined numeric semantics and reproducible random streams.
External observations must enter at an ordered, recorded boundary. Presentation
may interpolate and use floating-point arithmetic without changing authoritative
state. See the [architecture](../architecture/README.md) and
[Maths guide](../reference/maths.md) for implementation details.

A correction can intentionally change results across code versions. Update any
affected fixtures and replay expectations with evidence that the corrected
behavior remains reproducible. Do not preserve an incorrect result merely to
keep an old hash. Runtime versions that affect execution budgets, such as addon
fuel accounting, are part of the replay environment and need explicit control.

This register decides what presentation may do with a value. What of a world's
document presentation code may observe at all, and the transports that carry it,
is decided in
[the presentation view](runtime-and-delivery.md#the-presentation-view).

Rendering comparisons use suitable image tolerances and content checks;
pixel-for-pixel equality is not the default requirement across GPU backends.
Passing a comparison does not establish correctness without an independently
meaningful expected result. Audio mixes in fixed point for reproducible results; that mixing contract is
separate from presentation devices and their availability.

## Each machine runs to its own strengths

Presentation uses what the machine it runs on is good at (the owner's ruling).
A Steam Deck and a desktop with a discrete GPU each get the choices that suit
them, not one compromise both put up with. The simulation does not adapt:
the same document and input give the same state on every machine (above).

The engine chooses from measured capabilities, never from a list of device
names, so a machine it has never seen gets the right choices too. The facts it
reads include:

- the GPU's memory: unified with the host or discrete, and how much the host
  can write directly (`GpuMemoryProfile`);
- the display's pixel count, refresh rate and variable-refresh range (the
  present pacing policy);
- the CPU's core count and instruction set;
- measured frame time, which dynamic resolution already follows.

For example, a unified-memory APU such as the Steam Deck's writes GPU tables in
place where a discrete GPU stages them, and spends its four CPU cores sparingly
on presentation work. A discrete GPU on a high-refresh variable-refresh
display spends its headroom on frame rate.

Until initial development is done, no adaptation lowers quality. Every machine
runs the highest quality settings, and the adaptations above are how it reaches
its frame-rate bar there (the owner's ruling).

What "lowering quality" means (the owner confirmed a consensus of Claude, Codex
and Antigravity):
- **Identical on every machine:** the simulation and document content, and every
  light, shadow, global illumination, ambient occlusion and glass pass. No
  machine drops a pass or an effect, and nothing pops in or out.
- **A machine may adapt** anything that changes no visible geometry by more
  than one display pixel and adds no visible shimmer, lag, or change to
  lighting or readability: culling, memory paths, lossless merges, level of
  detail under the one-pixel rule, and update rates within their floors.
- **Anything visible beyond that is a lower tier.** If a machine can't hold its
  bar at the highest tier under this rule, the shortfall is a finding to fix,
  not a reason to redefine the tier.

What each tier below the highest means waits until the highest is settled, and
is worked out on the Steam Deck and the RTX 2060 machine themselves (the
owner's ruling).

Every adaptation stays visible and switchable, so its effect can be measured
and compared on each machine. The rendering decisions say what each lever does
([rendering](rendering.md)).

## Producers compose through explicit contracts

A screen consumes an image supplied by a producer. The renderer need not know
whether it came from an emulator, a camera, another view, or a shader pipeline.
A surface that participates in shared visibility needs additional contracts;
an image alone does not imply geometry or depth.

Vulkan and Direct3D 12 implement neutral GPU interfaces. Select the backend
when building the render host; a document must not silently combine live GPU
resources from different APIs. Cross-backend composition is not an admitted
shortcut around resource ownership. Hardware ray tracing likewise does not
replace the primary SDF traversal merely because a device supports it.

The earlier SDF-only scope is a description of the established world-rendering
path, not a restriction on the programmable pipeline foundation. General mesh
import, shared mesh/SDF visibility, and broader material authoring remain
explicitly sequenced proposals in the
[rendering programme](../plans/rendering.md). That plan
must update affected contracts when those features become implemented.

The initial repeated-placement audio design also excluded a separate emitter
for every visual copy. A change to that choice needs a defined spatial-audio
and resource-budget contract rather than following the render instance count
implicitly.

## An actor is one type, and a grant names more than actors

Every surface that acts carries one identity type, `Principal` in
`Puck.Commands`: a console, a seat, an addon, a network peer, or the world's own
authored program. The command router stamps it, the submission wire and the
replay tape carry it, and a mutation admits against it, so no layer translates
one identity into another and no conversion can drift. Its default value names
no one: a dispatch that reaches a handler without an ingress door stamping it is
refused rather than treated as an anonymous caller. Only an ingress door mints a
principal; a handler reads the one its context carries.

A grant row can name more than an actor. A group holds rows its members reach,
and another world document holds rows the cross-document channel reads. Those
two never act, so they are not principals: a grant's holder is a separate type,
`Grantee` in the world schema, which is a principal, a group, or a document.
Because no acting surface accepts a grantee, a group or a document acting is a
type error rather than a run-time refusal.

## Configuration and operations remain discoverable

Durable configuration belongs in documents. Live operations belong in the
running application's command interface, available through the console and
process stdin/stdout. Launch flags select boot concerns such as the backend,
world and presentation mode; they do not replace a content authoring model.
No environment variable switches Puck; a run's diagnostics are flags, as the
[development guide](../development/contributing.md#configuration-and-diagnostics)
explains.

The intended authoring experience supports work within one session. A missing
in-session operation is a planned integration task, not evidence that an
underlying library is absent. The [reference-game design](../game/design.md)
and the [play programme](../plans/play.md) own that experience.

The owner's ruling: console commands are easy to author and compose for safe
operations, and a new one does not need C# work. A world document authors
them in its `commands` section from existing verbs: a namespaced name, help
text, typed parameters with defaults, and the console lines they expand to
([commands reference](../reference/commands.md#document-authored-commands)).
The console answers them beside the engine's verbs, in `help`, from a binding
row or wheel sector, and typed at any console, and `world.reload` changes them
with the document. Their safety is data rather than trust: an authored command
runs each line under its caller's own principal and session policy, calls only
what a binding may name (never an operator verb, never `quit`, never an
unbindable verb), never takes an engine verb's name, and runs on the
simulation tick its line applies on, so replay sees what its lines submitted.
A new C# verb is for an operation no composition of existing verbs can
express.

## One live world while the campaign runs

While the current engine campaign is in progress, the debug world
([`debug.puck`](../../worlds/debug/debug.puck)) is the only
live world, and the World boots it when no `--world` is given. Its own
dependencies stay live with it: the Moth avatar it takes as its basis, the
quality presets that avatar imports, the skies it imports, the garden and the
night field its painting wing opens onto ([`garden.puck`](../../worlds/debug/garden.puck),
[`night.puck`](../../worlds/debug/night.puck)), and the shipped
fonts, schemas and audio assets live code reads.

Every other world is quarantined in [`experimental/`](../../experimental/README.md),
each file at its repository path under that directory: the other shipped worlds
(the island, the courtyard, the games, the districts, the shards and the tools),
the asset packages that lived under the root `worlds/`, and every fixture world
under `tests/` (the parity world, the canary worlds, the counters workloads
other than the debug world's own, and the game, determinism and verdict
fixtures), with the laws, baselines and ledgers that existed only to boot or
test them. Engine code those worlds exercised stays live. The gates whose
fixtures moved (canaries, parity, determinism, the counters workloads other
than the debug world's, the qualification profile and the world baselines)
keep their verbs and wait, unrun, until the owner calls for qualification; the
[open items](../plans/open-items.md#quarantined-gates) record what each covered.
A quarantined world returns by being rewritten or re-admitted under the gate,
never by building it in place.

## A portal is a plane a body passes through

A door fires when the body's origin passes through its glass, from the side the
glass faces and within the opening. The test is swept, so no speed tunnels past
it. A door does not fire when the body enters a band in front of the glass.
While a body stands in a door's opening, contact with this world's geometry
behind the glass is ignored, so a door laid on solid ground lets a body walk
onto it and drop through, and a door on a wall lets a body walk into it. The
crossing then maps the body through the pair's isometry: it comes out just
through the counterpart's glass, moving as it went in. Every door follows this
rule, a door onto another world as much as a pair standing in one world.

A pair standing in one world carries the body in place rather than
transferring it. The body keeps its index and state. Its every point and
direction, its previous position included, moves through the isometry, and
presentation carries what it latched the same way, so the crossing reads as
continuous motion, never a teleport. A body straddling a door is drawn on both
sides of it, each copy clipped by its own glass, and a chase camera whose boom
reaches through a door's glass stands past the counterpart's glass, looking
back through it at the body.

Why: a door that fires early moves the body, and the camera that follows it,
to the far side before it has reached the glass. A floor door also needs the
ground under it to give way, or a falling body stops on it in the same step it
passes through.

## The engine has no reference body size

A world's length unit is the metre, and content is authored at the size the
real thing has: a handheld console is its real millimetres, a door its real
height. An avatar is content too, authored at whatever size its author chose.
The Moth stands 2.06 m because she wears a suit. The engine holds no scale
derived from an avatar's proportions and no notion of a "normal" body:
movement, reach, camera framing and engagement read each body's own authored
or current size. A world that wants an object to read well in a body's hands
authors that body at a matching size, for example a Moth out of her suit,
rather than rescaling the object.

Why: the owner's ruling. The debug world's sizing follows the owner's choices
for the Moth, which are an author's choices and not the engine's.

## The engine is generic

No one feature's, world's or game's look or vocabulary enters an engine
contract: core design tokens, enums, schema names and defaults, default shader
paths, or engine prose (the owner's ruling). A feature's look is that feature's
material, style or skin, opted into where it is used, and the engine's own
default is neutral and complete. [The UI decisions](ui.md#a-neutral-design-language)
hold how the overlay theme stays neutral and why a screen shows its picture
plainly; as built:

- **The overlay theme.** The core tokens of the `theme` section name purposes
  (surfaces, scrims, rules, text, accent, status; spacing, radii, type sizes,
  stroke, motion). Each component's geometry, opacities and recipe (the keycap's
  badge and its latched toggle's march, the glow, the console's echo color, the
  bar, cursor, wheel, toast and HUD chrome) is its style under `theme.styles`,
  which takes a core role or its own color. A document that authors no theme
  draws the engine's neutral `WorldThemeSection.Default`; the debug world's look
  is its own skin (`worlds/debug/skin.puck`). No document refuses a translucent
  scrim: a player's high-contrast preference (`world.theme contrast high`)
  raises every scrim to a near-opaque backplate.
- **Screens.** A screen shows its picture as its source made it, filling its
  authored face; a CRT's bezel, scanlines and grille are the `Crt` screen
  material a row opts into, and a pointer hit maps through that bezel only on
  such a screen. The owner approved the change of look this made to every
  screen that is not a tube.

Why: a default that carries one game's look is a cost every other game pays to
undo, and a token named after a feature cannot be retuned without that
feature's vocabulary.

## Combat is world content on generic queries, fields and input

Codex and Antigravity proposed the groundwork for action combat
independently, cross-reviewed, and converged; the lead settled what they still
held.

- **The engine stays generic.** Spatial queries, named physical regions, input
  history and state projections are engine mechanisms. Elements, vitality,
  statuses, abilities, recipes, art and the faerie's reactions are world
  content, authored as states and tags, never engine enums.
- **Pairs are found by a bounded spatial query.** `WorldInteraction.Distance`
  runs over `FixedSpatialNeighborhood` and replaces the Cartesian scan; targeting
  and area effects reuse the same compiled query. An author bounds the
  candidates inspected and the results kept separately, and may ask for the
  nearest K; omitting K keeps every result in range. A query is resolved
  completely before any effect fires. A source whose neighbourhood exceeds its
  bound is refused visibly and applies nothing, never a silent sample.
  Membership, poses and generations are frozen per interaction row; recipients
  are ordered by exact squared distance, then by stable handle; a body never
  pairs with itself. Every inspected candidate is charged, exclusions included.
- **A world holds several physical field regions.** Each named topology is its
  own lattice with its own origin, cells, cadence, fields and reactions;
  reactions stay local to their region. Region and field identity are carried
  through snapshots, primers, checkpoints, hashes and replay. Limits are budgets
  of cells, fields, bytes and work across all regions, not a fixed count of
  topologies tied to the render pool; a surface is never silently omitted.
- **Action input uses the landed wire.** The 32-axis, 64-button, 64-register
  intent is enough. What action combat lacks is ordered press and release
  events and input history, steps C3 to C6 of the
  [input-channels decision](input-channels.md). A short tap is an ordered event,
  never a stretched held bit. Pad and keyboard drive the same semantic actions.
- **Prediction is new work and is proved before scale.** Two clients on one
  authority come first: acknowledged inputs, and speculative local movement and
  action timing replayed on correction, while hits and statuses stay
  authoritative. Interest management extends the existing disclosure with
  indexed entity and field subscriptions.
- **Costs are measured, never ledgered.** Admission charges what the
  implementation bounds; frame-time claims come from quiet-machine readings.

## A collider follows the drawn solid

A placement collides as the solid it draws, not as a bound around it. A
circular cylinder collides as its surface. A Box, which every vocabulary draws
with a built-in fillet, collides as an inner box plus that fillet's radius, read
from the same source the renderer uses. A shape with no exact contact gets a
conservative one that lies inside the drawn solid, never outside it: a body may
sink a hair into a surface it can't resolve exactly, but it never stands on air.
How a walker climbs an edge is decided by its step height, not by the contact
normal a corner happens to produce. The colliders diagnostic therefore has no
allowance for a bound: a collider outside its drawn solid is a finding.

The rule is the same on every face: exact where the vocabulary can express the
solid, inside it otherwise, never outside. Walls and ceilings get no exception.
The chase camera clears against the drawn field, not the colliders, so an inset
wall collider can't make the camera clip. A conservative approximation reports
its computed bound. Within that bound it is a known approximation, not a red
finding; beyond it, it is red. The fix for an approximation is an exact contact,
such as an exact chamfered-box collider. The owner confirmed this as a
consensus of Claude, Codex and Antigravity.

Why: the owner's ruling. Bodies floating at the round dais and at box corners
were visible flaws. Removing the drawn fillet instead would make plain boxes look
machined and leave authored rounding needing the same contact anyway.

## A derived product is a cache, never the truth

A baked mesh, an irradiance volume, a compiled shader or any other product
derived from a document and the engine's code may be built ahead of time and
shipped with a package (`world prepare`, the deploy) or served by its identity,
so a fresh machine draws its first frame without deriving everything itself.
Every such product is only a shortcut to a result the engine can always derive
again:

- Its identity is computed from its inputs, the code that derives it included,
  at the moment it is needed. The code is the code the derivation runs, read
  from the build (for the mesh bake, the methods its call graph reaches and its
  kernels' bytecode), never a whole assembly's or a checkout's identity, so an
  edit elsewhere keeps the product. Nothing records an expected identity to compare
  against, so there is no step that accepts a new one: a change to the deriving
  code invalidates what it changes, and the cost is a derivation.
- A missing, stale or corrupt product is derived again and counted, never
  refused. No test, CI step, review or gate compares against a shipped product,
  and no change waits on one.
- Products are never committed. The pipeline builds them from the same commit
  as the code that reads them, into git-ignored caches or the deploy.
- Deleting every cache is always safe and yields the same picture, only
  slower. Counters show hits, misses and derivation time, as evidence for a
  performance decision, never as a gate.

Why: the owner's ruling. Shipped bake products are acceptable so long as Puck
never again becomes a slave to a recorded artifact, as it did with the deleted
ledgers and the bake derivation fingerprint.

The owner explicitly includes compiled cartridge JSON twins in this decision.
Only their authored sources are committed; consumers compile those sources,
and semantic compile/decompile round trips compare results produced in the run.
The owner also includes the Maths coverage ratchet, its numeric floor and all
recorded Maths registers in the no-recorded-ledgers ruling. The structural checks
hold declaration/binding parity, member-reference resolution and waiver reasons;
they read no recorded file and write no register. The retired ratchet
grandfathered uncovered public members, so whole-public-surface completeness
requires that remaining API to be classified in authored declarations first.

## Verification must match the claim

Validate physical limits and report useful failures rather than silently
truncating authored content. A measured capacity, an advisory performance
estimate, and a regression threshold serve different purposes and must be
identified as such. Device failures and unsupported features should have
explicit recovery or refusal behavior.

Use tests for the contracts they actually exercise, and run World for behavior
that depends on its composition and command processing. The quarantined
experimental engine Post harness is not an active verification gate. Current
emulator batteries and other live suites have their own scope; see
[development](../development/README.md).

Keep current limitations in the owning guide. Dated test results belong with
their candidate and environment, rather than in a hand-maintained table that
implies permanent certification of every feature. Readers should still be able
to learn what the engine does without inspecting source code first.

## Internal contracts can be corrected

Puck's internal names and document shapes can be changed with their callers,
examples, and verification. Do not accumulate migration aliases solely to retain
an accidental internal design. Package and release promises, when made, need
their own explicit compatibility policy rather than being inferred from an
internal schema's version label.

A format's own magic and version are what refuse data it cannot read. The codec
that owns a wire, checkpoint, tape or cache writes them in its header or
handshake and refuses any other by name before any state changes, and a change
to its layout raises the version in the same change. No ledger records a
layout. A store identified by its content already rejects by content and
needs no header.
