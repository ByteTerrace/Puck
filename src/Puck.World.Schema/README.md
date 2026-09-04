# Puck.World.Schema — what a world IS

This project holds the SHAPE of the world game: `puck.world.def.v1`, the
versioned JSON document family that describes a world and a player — plus the
two egress families, `puck.world.projection.v1` (`WorldProjection.cs`) and
`puck.world.counterpart.v1` (`WorldCounterpartAttestation.cs`), which are what
a world hands a peer instead of itself. It contains no rendering, no input
handling, and no server logic; it exists so the data the simulation runs on
cannot quietly grow a dependency on presentation. It also carries the
document-embedded vocabulary that a document's own rows type themselves
against — the admission section's entries/grants/disclosure tier, and the
channel/intent vector a document's motion and kit rows compile against — even
though those types keep the `Puck.World.Protocol` namespace their move here
did not rename (see "Namespace note" below).
[`Puck.World.Protocol`](../Puck.World.Protocol/README.md) is the wire
vocabulary layered on top of this document model; the runtime that folds
against both is [`Puck.World.Server`](../Puck.World.Server/README.md); the
process that composes everything is [`Puck.World`](../Puck.World/README.md).

## Discrete boards, cards, and turns

The discrete substrate shares `state.lattices` with physical fields. Declare
`kind: "Grid"`, `"Ring"`, or `"Hex"` and bind an integer/boolean row through
`board: { "topology": "map", "empty": 0 }`. Only `Field` declarations create
physical field storage. A world may declare at most 16 topologies, including
at most one physical field topology. Each discrete topology admits 4096 cells;
boards together admit 65536 cells, and all declared state storage admits
262144 cells. Ordinary keyed rows retain their 128-cell limit.

Grid keys are decimal `y * width + x` ordinals. Directions are `N`, `NE`, `E`,
`SE`, `S`, `SW`, `W`, `NW`; `wrap` is `None`, `X`, `Y`, or `Both`. Rings use
`width` cells, `depth: 1`, and `forward`/`backward`, with implicit wrapping.
Hexes use axial coordinates, `radius`, `width: 1`, and `depth: 1`; ordinals
follow ascending r then q. Hex directions are `E`, `NE`, `NW`, `W`, `SW`, `SE`.
All shapes still carry the registry's required `origin` and `cellSize` fields.
Missing board entries read as the declared `empty` value. Wrapped scans stop
before revisiting their origin.

Rule operands accept these bounded channels:

| Channel | Result |
|---|---|
| `$board:neighbour:<row>:<direction>` | Neighbour ordinal, or -1 at the edge |
| `$board:rayCell:<row>:<direction>` | First nonempty cell ordinal, or -1 |
| `$board:rayDistance:<row>:<direction>` | Distance to that cell, or -1 |
| `$board:line:<row>:<length>:<value>:<exact\|atLeast>` | 1 when a matching line exists, otherwise 0 |
| `$board:pathCost:<row>:<target>:<maxCost>:<maxVisits>` | Minimum terrain entry cost, -1 when unreachable/unaffordable, -2 when the visit budget is exhausted |
| `$board:mask:<row>:<min>:<max>` | The 64-bit mask of cells whose value lies in min..max (bit c is cell ordinal c); the topology holds at most 64 cells |
| `$board:image:<row>:<element>:<min>:<max>` | That mask carried through one point-group element of the topology |
| `$board:canonicalMask:<row>:<min>:<max>` | The least image mask over every element: one number for a position and all its rotations and mirrors |
| `$board:canonical:<row>` | The least 64-bit fingerprint of the whole board's values over every element, for boards of any size: pushed into a history ring, repetition up to symmetry is a pattern |
| `$board:cellOf:<row>:<bodyRef>` | The Grid cell a body's resolved world position falls in, or -1 |
| `$board:offset:<row>:<dx>:<dz>` | The cell reached by an arbitrary (dx, dz) grid step from the key cell, or -1 |
| `$phase:<row>:<current\|active\|ready\|sequence\|round\|deadline\|direction\|skipped>` | Persisted phase fact; active is -1 outside sequential phases |

Board operands use the ordinary predicate/source `key` for their origin,
including the existing `$cell:` dynamic-key form; `line`, `cellOf` accept no
key — `cellOf` takes a `bodyRef` in its place, the same `body:<n>`/
`argmax:<row>`/`argmin:<row>`/`cell:<row>:<key>` vocabulary `$distance:`/
`$los:`/`$nearest:` read. Both `cellOf` and `offset` require a `Grid`
topology — the only kind carrying a rectangular world-space frame
(`CompiledWorldTopology.Origin`/`CellSize`, the same origin/cellSize every
topology declares); a `Hex`/`Ring` row refuses them by name at compile.
A `Box` topology is `width` by `layers` by `depth` cells with the 26 space
directions: the grid's eight compass names in the layer, each prefixed `U`
or `D` for the layer above or below, and `U`/`D` alone; ordinals run
`(layer * depth + z) * width + x`, and `layerHeight` resolves a body's Y to
its layer the way `cellSize` resolves X and Z. Rays, lines, masks under 64
cells, patterns, path search, `combine`, and `mapBoard` apply to it
unchanged; a 4x4x4 tic-tac-toe is one 64-bit mask and `line:4`.
Every discrete topology carries its point group, derived from its shape and
never authored: a square grid the eight elements `identity`, `rot90`,
`rot180`, `rot270`, `mirrorX`, `mirrorZ`, `mirrorMain`, `mirrorAnti`; a
rectangle the four without quarter turns; a hex board `rot60`..`rot300` and
`mirror0`..`mirror5`; a box the signed axis permutations its equal extents
admit (48 for a cube, 16 for a square prism, 8 otherwise), named by where
`+x+y+z` land; a ring the identity alone. `world.topology <topology>
[<cell>]` lists them and a cell's image under each. `mapBoard` (`target`,
`source`, `element`) writes a board carried through an element, which is how
rules authored from one side's view read the other side's position through
`rot180`; the `boardImage` (`topology`, `element`) token does the same to a
mask inside an expression.
A grid's `band` is the vertical half-extent about its origin's Y a position
must lie within for `cellOf` to answer a cell; 0 (the default) answers any
height, so a table's board authors a band and a piece on the floor beneath
it reads as off the board.
`offset` is the arbitrary-`(dx, dz)` sibling of `neighbour`'s fixed eight
directions — what a leaper (a knight, or any piece whose reach is not a ray)
authors its geometry against.
Path search uses all topology neighbours, including grid diagonals, at the
destination cell's entry cost. Negative terrain is impassable. Equal-cost
nodes settle by ordinal; the visit bound counts settled nodes. A path budget
refusal is distinct from proof that no route exists.

`tokens: { "capacity": 256 }` declares stable token identities. `keysFrom`
restricts an attribute row to those identities; `valuesFrom` additionally
restricts integer positions to a named topology. A `zone: { "tokens": "cards",
"ordered": true }` row contains boolean membership cells. For any domain
with zones, every token belongs to exactly one zone. Cell order is pile order;
two cards with equal ranks still have different keys. Dealt-generator masks
are separate from these piles: each `drawDecks` mask is four 64-bit words,
serialized as exactly 64 hexadecimal digits, and supports 256 dealt entries.

The closed `transformState` effect and transaction step carry one of `transfer`,
`setRay`, `moveToken`, `completePhase`, `turnOrder`, `shuffle`, `sort`,
`setMask`, `combine`, `push`, `mapBoard`, or `observe`. `setMask` (`row`, `mask`, `maskKey`,
`value`) writes one value into every cell of a board (at most 64 cells) whose
bit is set in a mask read from an integer cell, so a mask built by
`$board:mask`, `boardShift`, and the bit operators lands back on the board;
`combine` (`target`, `left`, `operation` and/or/xor/andNot/not, `right`)
writes the cell-wise set operation of two boards over one topology into a
third as 1/0, the board algebra for topologies too large for one mask. The same operation travels
as the `TransformState` document mutation. Transfers preserve keys and accept
`Key`, `First`, `Last`, or `Random` selectors; random selection names a
redrawable integer `streamDraw` site, and `count` (1..256) moves that many
tokens in one mutation, each selected afresh from what remains, so a deal is
one journal entry. A cursor advances only with the committed transfer. `setRay` changes a nonempty run of `through` cells closed by `until`;
it excludes the origin and terminator and refuses a broken bracket.
`moveToken` checks all position rows over the topology for occupancy, searches
within `maxVisits`, and debits the token's allowance atomically with its move.
`shuffle` reorders an ordered zone in place by one Fisher-Yates pass over a
redrawable integer `streamDraw` site, consuming one sample per position after
the first (a 52-card deck advances the cursor by 51), so one transaction
shuffles a whole deck and a replay deals the same order.
Every written row requires edit authority. Transaction preflight leaves no
partial transfer, ray, cursor, allowance, or phase change after refusal.

A `phase` row declares up to 32 authenticated participants and 32 named phases.
`Sequential` activates participants in order; `Together` accepts actions until
a participant becomes ready; `Resolution` admits only world-program completion.
The order is state, not structure: the row carries `direction` (1 or -1) and a
`skipped` participant mask, both persisted across phases, and the world-only
`turnOrder` transform rewrites them (`direction`, `skip`, `unskip`, `active`)
without completing anything — a reverse card, a fold, an elimination, a
"play again". A sequential turn walks in `direction` over unskipped
participants and the phase ends when it passes either end; a `Together` phase
never waits on a skipped participant; skipping the active participant hands
the turn onward around the ring. A transition enters the phase's authored
`next` unless the world program's `completePhase` carries its own `next` — the
branch (one player left standing goes to `showdown`, otherwise `deal`). Rules
read `$phase:<row>:direction` and `:skipped` beside the other phase facts.
Readiness alone preserves `sequence`; changing activation or phase increments
it. Returning to phase zero increments `round`. A timeout expires at its exact
deadline tick and takes precedence over a player action at that tick; a world
rule explicitly performs timeout completion. Timeout completion advances the
phase, including when some participants remain unready. Rows with `phaseOf`
require the corresponding guard on external gameplay transforms. Grant those
players the `TransformState` mutation kind, leaving document authoring to the
authority. Phase eligibility does not grant access to another player's units.

`visibility: {}` opts a row into public literal observations. `readers` limits
that audience to canonical authenticated principals; `readers: []` retains it
at the authority; `readersFrom` names a keyed text row whose cell texts are
tokens admitted beside `readers`, so a rule widens the audience by writing a
token (a showdown reveals a hand) and narrows it by clearing one. Row and cell restrictions intersect. What a hidden entry
leaves behind is the row's `visibility.hidden` policy: `Omit` (the default)
drops it with its key and pile position; `Count` reports only `hiddenCount`
on the observed row; `Placeholder` keeps each hidden entry in pile order as an
anonymous `hidden: true` cell with no key, value, text, or stamp, the card
back an opponent's hand shows. Token attributes inherit their containing
zone's restrictions. An absent policy preserves the existing
absence of raw state from presentation documents. This is an observation
payload, not a partial executable authority document. `StateObservations(row)`
queries require `observe state:<row>` and apply the submission's stamped
principal. Public federation observers receive public observations;
`Replica` is an explicitly trusted authority tier and retains the full document.
Presentation bindings into restricted state fail closed before flattening.

For remembered fog, a board's `knowledge: { "source": "truth", "mask": "sight" }`
names a compatible source board and boolean visibility board. An authority-only
`observe` transform copies currently visible values, stamps their last-seen
tick, and marks previously known cells outside sight as no longer visible.
Unseen cells remain absent; remembered values never refresh from hidden truth.
Rules author the visibility mask and refresh cadence. This does not generate
line of sight or render a tactical UI automatically.

A `patterns` row is a regular language over cell values, compiled once at
validation into a deterministic table the `$match:<pattern>:<row>[:<direction>]`
operand runs allocation-free, one indexed step per token: `symbols` name value
ranges in the pattern's `kind` (overlaps refine into letters; at most 32
symbols, 64 letters) and `pattern` is the closed node vocabulary `symbol`,
`any`, `except`, `empty` (the empty word), `none` (the empty language),
`sequence`, `choice`, `all` (intersection), `not` (complement), `optional`,
`star`, `plus`, `repeat` (`min`..`max`, at most 64), matched against the
whole word. The machine's states are the pattern's
Brzozowski derivatives, kept canonical by similarity, so stars, complements,
and intersections are exact at any word length; `maxStates` (1..256, default
64) is the state budget the compile refuses past, by name, at validation. The
word is a board ray from the operand key's origin (exclusive) in the named
direction, an ordered zone's cells read through the pattern's `attribute` row
in pile order or through its `value` expression (evaluated once per token in
the pattern's kind, where a state token keyed `$token` reads that token's cell
of any row keyed over the zone's domain, so `suit * 16 + rank` makes a
straight flush one word over one alphabet), or a keyed numeric row's own
cells; acceptance is always 1 or
0, and a board origin that names no cell reads the empty word. A trailing
`prefix` facet answers the length of the longest accepted prefix instead
(-1 when none), which is Reversi's flip count on a ray; a board source may
name `any` in place of a direction, answering the mask of accepting
directions (bit d for direction ordinal d) or, with a trailing `count`, how
many. `world.match` walks one word at the console and narrates every step:
value, letter, state, verdict. A `history` row (`capacity` 1..128, `empty`)
is a ring of the last pushed values, the temporal twin of a ray: `push`
(`row`, `value`) and the `pushState` effect (`value`/`fromState`/
`expression`, world scope) append to it and advance its `historyCursor`;
`$history:<row>:<age>` reads the value `age` pushes ago (0 is the latest,
`empty` past what the ring holds); `$match:<pattern>:<row>` reads the ring
oldest first, so a combo, a rhythm window, or "three claims then silence" is
one pattern. `world.state <row>` echoes capacity, cursor, and how much of
the ring is held. `sort` puts a
zone in canonical order by `by`, 1..8 attribute keys (`row`, `descending`) in
precedence order, or a keyed row by its own values under `descending`, stably,
which is what turns a multiset question into a regular one: Reversi's
flank is `them+ me` on a ray, a straight is five consecutive rank symbols over
a sorted hand, Yahtzee's large straight is a `choice` of two sequences over a
sorted tray. `world.patterns` echoes each compiled table; `world.budget`
carries the states and the word cap.

Private random transfers can use `draw.secret`, a nonzero 256-bit key provisioned
by the authority before simulation. This selects cursor-addressed HMAC-SHA256
samples for integer `streamDraw` sites; ordinary generators keep their PCG
contract. Use a fresh unpredictable secret per game and keep authority saves,
replays, and replica access private. The simulation draws no system entropy.
No hidden keys, generator definitions, cursors, or dealt masks enter observation
payloads. Publicly authored seeds alone are unsuitable for hidden deals.

**The tabletop primitive.** A placement's `board` facet (`WorldPlacementBoard`)
anchors a discrete `Grid` topology's own world-space frame (its declared
`origin`/`cellSize` — no separate frame member) to a physical row/body-based
game. `cellSize` is the divisor `$board:cellOf` resolves world positions
against, so `WorldTopologyCompilation.TryValidate` refuses a `Grid` whose
`cellSize` does not quantize to a positive Q48.16 value, or whose `origin`
does not fit one, at document validation rather than at the per-tick rule
path. `topology` names the anchored Grid; `occupancy` names the board row the
engine reads back; `turn`/`verdict`/`move`/`plan` are author-named convenience
rows a `world.tabletop` read-back echoes together, never engine-interpreted —
any tabletop game names whichever it needs. A topology is carried by at most
one placement (validated). The shipped `body.carry` facet (`WorldCarry`) is a
separate primitive: it picks up a rigid body, never a placement or board. The
bridge from rigid bodies to this row is authored, not built
in: a world rule reads each piece's `$board:cellOf:<occupancy row>:body:<n>`
on `$physics:quiescent`'s rising edge (a settle, never every tick) and writes
its code into the occupancy row at that resolved cell — see the garden's own
`puck.world.json` tabletop rules for the worked pattern (snapshot the prior
board before clearing, derive fresh occupancy, detect which single piece
moved between two occupied board cells — a piece whose cell resolves to no
cell, before or after (captured, lifted off, knocked clear), never itself
qualifies as the mover, so its own disappearance is never ruled legal or
illegal by its own color — then a verdict any authored predicate — occupancy, turn order,
a `$board:rayCell`/`$board:offset` movement-geometry check — may set to 0
without touching the mover; a legal verdict alone advances turn and adopts the
new position into `lastLegal`). Illegal moves are recorded, never undone —
the world never rejects or repositions a physical piece. A rule's own
contiguous run of `setState`/`addState`/etc. effects preflights and applies
as ONE atomic candidate — deriving N independent pieces' occupancy therefore
needs N separately-authored derive rules, one per piece, never a single rule
spanning every piece: one piece's body leaving the frame (captured, knocked
clear) refuses only its own write when it owns its own rule, but rejects
every sibling piece's write too when they share one. A `body.pose` reposition
is a kinematic write: one that leaves the piece resting on its support (its
authored resting height) does not itself disturb the rigid census and leaves
the board unrevised, while one that drops the piece onto or above its
support un-rests it and crosses `$physics:quiescent`'s Edge on settle with no
impulse needed at all — pair a resting-height pose with a negligible
`body.impulse` (or drive the whole move by impulse) to trigger a derive
without relying on drop height. Wake a piece along its own up axis where
the kit's rigid friction is high — a horizontal wake couples into spin under
Coulomb/rolling friction and can drift the piece across a cell boundary
before the census re-quiesces. The quiescent census is population-wide: an
unrelated rigid body still settling elsewhere holds every board's derive at
bay too.

The [tabletop state fixture](../../tests/Puck.World.Canaries/tabletop-state/fixture.world.json)
and its [positive script](../../tests/Puck.World.Canaries/tabletop-state/positive.script.txt)
exercise movement, pile order, ray flips, phase progression, and replay through
the real headless application. The [control script](../../tests/Puck.World.Canaries/tabletop-state/discriminating.script.txt)
checks that an occupied destination spends nothing. Complex scoring remains
an addon concern; these operators introduce no scripts, recursion, or open loops.

## The dependency firewall

`Puck.World.Schema` references only `Puck.Abstractions`, `Puck.Assets`,
`Puck.Attestation`, `Puck.Commands`, `Puck.Hosting`, `Puck.Maths`,
`Puck.Physics`, `Puck.Text`, and `Puck.World.Authoring` (see
`Puck.World.Schema.csproj`). An architecture lane profile in
`build/Architecture.props` enforces the absences that matter: no GPU backend,
no presentation project, no `Puck.Overlays`, no `Puck.Input`, no
`Puck.World.Protocol`, and no `Puck.World.Server`. Adding a forbidden
reference fails the build with a `PUCKARCH` diagnostic naming the arrival
path.

Several validation/serialization paths genuinely need knowledge this project is
denied. Each crosses through a static injection point every composition root
wires with a module initializer before `Main` runs — one shared method,
`Puck.World.Client.WorldSchemaVocabularyHooks.Install`, called by
`WorldDataHookInstaller` (`Puck.World`), `WorldSiloDataHookInstaller`
(`Puck.World.Silo`), and `TestHookInstaller` (`tests/Puck.World.Tests`), so a
seam one process wires and another does not cannot exist:

- `BindingVocabularyHook.cs` lints a composed binding overlay against the live
  command/channel vocabulary (which needs `Puck.Input`).
- `InputSourceVocabularyHook.cs` answers whether a string names a declared
  physical control — the id a `bindingBar.slotSet` entry and an `icons.badges`
  row are keyed by, resolved against `Puck.Input.InputSourceVocabulary`.
- `GamepadFamilyVocabularyHook.cs` answers whether a badge override's family
  name is a declared `Puck.Input.Devices.GamepadType` member.
- `ContextFamilyVocabularyHook.cs` supplies the built-in context-family names an
  authored `seatModes` family must not collide with, derived from
  `Puck.World.Client.WorldContextFamilies.Families` rather than mirrored here.
- `MutationKindVocabularyHook.cs` round-trips a `MutationKindMask` field
  (`WorldGrant.KindMask`) by NAME (`verbs:UpsertStateCell,RemoveStateCell`)
  against `WorldMutationKindCatalog`, which lives in `Puck.World.Protocol`,
  downstream of this project.
- `WorldExtensionVocabularyHook.cs` checks a `screens[]` engine key and a
  `render.extensions[]` key against the catalogs in `Puck.World.Server`; those
  two arrive as parameters to the shared installer, since `Puck.World.Client`
  does not reference `Puck.World.Server` either.
- `WorldProbeVocabularyHook.cs` checks a `probes[].kind` key
  against the catalog in `Puck.World.Server`, the same required, arrives-as-a-
  parameter shape `WorldExtensionVocabularyHook.cs` uses.

Every validation path is covered without this project ever naming `Puck.Input`,
`Puck.World.Client`, or `Puck.World.Protocol`.

## Namespace note

A handful of files carry the `Puck.World.Protocol` namespace despite
physically living in this project: `WorldAdmission.cs`, `WorldAdmissionDoor.cs`,
`WorldGrant.cs`, `WorldPrincipal.cs`, `WorldEntityAddress.cs`, `PlayerIntent.cs`, `ChannelPolicy.cs`,
`MutationKindMask.cs`, `MutationKindVocabularyHook.cs`, and
`WorldDocumentWriteMask.cs`. These types are document-embedded (a grant row,
a principal, a channel value ride inside `WorldDefinition` itself) but were
authored under `Protocol/` before the project split existed; every caller
throughout the tree already spells them `Puck.World.Protocol.WorldGrant`
etc., so the split kept the namespace and moved only the file, rather than
renaming the type everywhere it is used.

## `puck.world.def.v1` — the world definition

`WorldDefinition.cs` is one aggregate record with a section record per concern;
the section list is the `WorldSection` enum in `WorldGrant.cs` (kits,
screens, cameras, spawns, motion, population, render, addons,
bindings, creations, placements, authoring, speakers, tunes, patches, audio,
collision, host, views, looks, grants, hud, state, input hold, rules,
groups, properties, interactions, player defaults, market, probes,
dynamics, curves). Worlds live as data
under `../Puck.World/Assets/worlds/`. Four are the four-world charter's whole
game roster: `nexus` (the overworld hub — a floating island above a field of
planetoids, and the shipped boot default; carries the `references` section
naming the other three plus `studio` by document path, and one portal-arch
placement per named world), `dive` (underwater), `kart` (racing), `jump`
(platformer). `studio` is a non-game dev canvas for character work (neutral
floor, no scenery or crowd, four anchored camera eyes and a `sheet` layout)
reached with `--world` or through the nexus's mapped archway. Five quilt
documents (`quilt-nw`, `quilt-ne`, `quilt-se`, `quilt-sw`, and `quilt-island`)
are non-game adjacency/federation stress content — each a `basis` delta over
the `quilt-base` template (see "Document composition" below). Reusable defaults
live in `standard.basis.json`. The movement platform
every kit rides is documented on its kit's `WorldMotion`
row (`Speed`/`Turn`, `MoveFrame`/`FacingSnap`), the
frame its MoveAdvance/MoveStrafe channel rows are authored in
(`channels[].frame`, `ChannelFrame`: `World` raw, `Camera` camera-relative and
facing its travel, `Heading` body-relative with `Turn` steering — the stick's
`player.move` is camera-framed by its own definition, so keyboard-in-heading
beside stick-in-camera is one document), and the seat rig's own `dynamics` op
(a named `dynamics` row shaping the boom ease). Beside `holds`, the motion row's
`shaping` table admits a row carrying an `across` facet — the anisotropic drive
decomposition (longitudinal accel/brake/coast via `along`, lateral grip via
`across`, a held drift row authored ahead of the ordinary one, `Turn`'s own
speed-scaled steering authority, optional pitched flight) `ShapeVelocity` reads
alongside `ResolveDriveFrame`. A kart is the same motion row plus an `across`
shaping row exactly as a swimmer is the same motion row plus a `Medium` hold
row; a program selecting `ShapeVelocity` against a kit authoring no shaping row
refuses by the `Shaping` tuning facet's name. An omitted engage/release/brake/
grip rate means exact convergence, while an explicit rate must be positive;
drive-only brake/reverse values are refused on a whole-vector row. The retired `arcade` world's
`gaming-brick`-cabinet + region-gated prompt/prize + `rules`-driven `state`
reaction ladder (originally a document-mounted addon, ported to a world rule
before the world itself was retired) survives only in git history; no shipped world exercises the `rules` section
today. The loader resolves an explicit `--world` file or the shipped default
document and refuses a missing, unreadable, or invalid file by name;
`WorldDefinitionLoader.cs` owns that boundary.

**Placement facets.** A `WorldPlacement` row (the `placements` section) is a
creation stamp plus optional facets — `Solid`, `Emission`, `Inhabit`,
`FaceSources`, `Region` (`WorldPlacementRegion`): a NAMED VOLUME, a
sphere centered on the placement's own position and addressed by the
placement's own `Id`, that `Server/WorldEventFeed.cs` watches for body
enter/exit edges (see the puck-world skill's `addons.md`, "World events"); and
`Attach` (`WorldPlacementAttach`, new): binds the row's resolved world pose to
a live population body's transform (0-based entity index, the SAME indexing
`WorldAnchor.Entity`/`body:<n>` use) plus an authored local offset rotated
into the body's own frame. Two derivations off the one facet: the
authoritative fixed-point resolve
(`Server/WorldPlacementAttachment.TryResolve`, what the `world.attachments`
read-back calls), and the rendered pose — presentation float over the client's
interpolated body pose, packed every frame by `Client/WorldStampPool.cs`, so
the row visibly RIDES its body. An attached row therefore draws through the
reserved stamp pool rather than as a static stamp
(`Client/WorldPlacementStamper.IsStaticStamp`) and charges
`WorldPlacementPolicy.MaxStampRegistrations` (= `WorldBodiesLimits.DetailedRenderBand`,
128) alongside animated rows; its
authored `Position`/`YawDegrees` become inert. `Region`, `Solid` (under the
analytic contact provider), and `Emission` all read the resolved DYNAMIC pose
instead of refusing outright (`Server/WorldEventFeed.CollectRegions`,
`Server/WorldColliderSet.RefreshAttached`,
`Client/WorldStampPool.TryShapePosition`/`RootPose`) — an equipped item's
aura, hitbox, or voice tracks the carrier, and an inactive carrier makes the
facet contribute/sense/sound nothing rather than at a stale point.
`Distribution`/`Mirror` (static-stamp-only) and `Inhabit` (a row cannot both
spawn its own bodies and ride another's) stay refused, and `Solid` stays
refused under the FIELD contact provider (it compiles every solid row's
geometry once into one SDF program, never rebuilt per tick).
`Contribution` (`WorldPlacementContribution`) makes the row a SLOT: the host
authors the frame (`Tenure` — `Presence`/`Endowed` — plus `SlotCreationId`, the
watched `Link` adjacency row name, and `GraceSeconds`), and a federation partner
fills it with a creation of their own through an ordinary `UpsertPlacement`.
`Contributor` and `RetractDeadlineTick` are SERVER-STAMPED: the compose arm
(`Server/WorldServer.Contributions.cs`) reads the contributor off the submitting
principal and refuses a payload that names either, and the per-tick presence
sweep owns the deadline. An unfilled slot shows its own `SlotCreationId`, so
there is no creationless placement to represent — the validator pins the pair
(`WorldDefinitionValidator.Contribution.cs`): unfilled requires
`creationId == slotCreationId`, filled requires them to differ. Read back with
`world.contributions`.
`Respond` (`WorldPlacementResponse` rows) is a STATE-DRIVEN prototype swap: an
ordered list of `{When, PrototypeId}` entries, each `When` the SAME
`WorldFieldCondition` grammar a `fields.reactions` Transform/Expose condition
uses, tested at the placement's own coupled lattice cell
(`Server/WorldFieldLattice.TryBodyCellOf`). The per-tick sweep
(`Server/WorldServer.Responses.cs`, run right after the field lattice steps)
tries entries in authored order and swaps to the first whose condition holds,
through an ordinary `UpsertPlacement`; nothing reverts a swap when no entry
holds. Refused alongside `Attach`/`Inhabit`/`FaceSources`, and every candidate
prototype (the row's own and every entry's) must be a declared, non-animated
creation. Read back with `world.responses`.
`Solid` compiles the same creation-shape transform chain the renderer emits.
The field provider evaluates that SDF geometry directly; the analytic provider
materializes exact isotropically scaled spheres and conservative world-axis
bounds for every other finite primitive copy, capped document-wide by
`WorldPlacementPolicy.MaxSolidPlacementColliders`. Authored body colliders
compile here into `Puck.Physics`' fixed collider vocabulary; contact geometry
does not live in the document assembly.
`WorldAddonRow` similarly gained `MemoryWatches` (`WorldAddonMemoryWatch`
rows: `Screen`/`Address`/`Length`) — the machine-memory-watch event family's
addon-scoped declaration.

**Document composition.** A world file may name a `basis` — another document it
layers over, resolved against its own directory. The file is then a delta:
authored members override, omitted members inherit, an authored `null` clears,
`$type`-changed union objects replace wholesale, and identity-keyed row lists
(the first of `id`/`name`/`index` on every row of both sides) merge by key with
`{"…", "$drop": true}` tombstones and a leading `{"$replace": true}` marker for
wholesale replacement. `WorldDocumentBasis.cs` owns the merge and its inverse
diff; `WorldDefinitionFileSource.cs` resolves the chain (depth-capped, cycles
refused by name) on every file load — boot, `world.load`/`world.reload`, replay
re-drives, and neighbour resolution — strips the consumed `basis` member, and
hands the composed tree to the one strict parse → migrate → validate gate, so a
live document never carries a basis and every wire egress is self-contained. A
derived document's content pin folds the whole chain's raw bytes
(`ComputeChainContentHash`), so a template edit moves every derived pin.
`world.save` preserves the derivation of the file it overwrites
(`SavePreservingBasis`): the written delta is proved by re-merging before
anything lands, degrading to a flat save with a named note when it cannot.
`world.status` echoes the source file's basis. A partial template cannot boot
(the validator names its missing sections). `IWorldDocumentSource` generalizes
the same chain walk onto any byte-level document source, so storage sync
(`Puck.World.Server`'s `WorldStorageDocumentSource`, over a flat
`puck/worlds/basis/` cloud namespace) composes a synced delta exactly like a
directory load does — it no longer refuses one by name. The wire still never
carries a basis-bearing document: a live document's `Basis` is always `null`
(stripped at load), so nothing reaches the wire un-flattened regardless.

**The validator is the one thick gate.** `WorldDefinitionValidator.cs` runs
over the entire composed candidate document — at boot, on every live mutation,
and on a whole-document swap — so builders and mutation appliers never repeat
semantic checks. The add-a-field procedure (model, validator, serializer
registration, verify by running) is in
[`docs/agent-guide.md`](../../docs/agent-guide.md).

**Serialization** (`WorldDefinitionSerialization.cs`): `WorldJsonContext` is a
System.Text.Json source-generated context — camelCase member names, enums by
name through strict converters (an unknown or numeric enum token is a parse
error, not a silent default), literal vectors/quaternions as
`[x, y]`/`[x, y, z]`/`[x, y, z, w]` arrays,
and `$type`-discriminated polymorphic hierarchies for screen sources, cameras,
scene rows, speakers, and anchors. Parsing is strict everywhere below the
document root: `UnmappedMemberHandling = Disallow` makes an unmapped member on
any nested row a hard load failure naming the member and the row type. Only the
root carries a `[JsonExtensionData]` `Extensions` bag, governed by
`Puck.World.DocumentExtensionsPolicy`: a reserved-prefix key
(`$` or `_`) round-trips untouched, and any other unknown root key is a hard
load failure.

**Identity conventions.** Every row is addressed by a stable string id, with
two exceptions: screens are position-addressed by index, and grant rows are
keyed by their `(principal, capability, subject)` triple, because a grant IS
that triple. `GrantSubject` and `WorldPrincipal` serialize as the same compact
tokens the console grammar uses (`body:1`, `addon:default`) through their own
JSON converters.

**Canonical write-back.** The `world.save` verb serializes the live definition
canonically — stable member order, invariant-culture numbers, LF line endings,
one trailing newline — so a load→save of an untouched world reproduces the
file byte-for-byte. That round-trip is a useful observation when editing the
serializer; it is not an acceptance gate (see the verification section of
[`Puck.World`'s README](../Puck.World/README.md)). The one honest exception is
a document carrying an advancing `state` row/cell (`Advance`): `world.save`
settles it to its live computed value with its projected epoch reset to 0 (see
"The `state` document" below), so an untouched world with one still reproduces
a slightly LARGER base than it loaded — never the identical bytes — because
some ticks always elapse before a save can be requested at all.

## Owned identity worlds

A person or character is an ordinary `puck.world.def.v1` document carrying an
`identity` row and durable `state` rows. Multiple identities are multiple owned
worlds; there is no player-document family or aggregate catalog. The runtime
stores them independently beneath the selected state root's `owned-worlds`
directory.

**Bindings compose in layers.** A seat's effective binding document is the
world's `bindingOverlays` rows in order (a `basis` chain supplies earlier rows
— the shipped `Assets/worlds/standard.world.json` template carries the standard
movement and action-wheel document; the engine itself ships none,
and a world authoring none binds nothing), then the seat's owned identity
world's `bindingOverlays`, then live session rebinds — merged by
`WorldBindingComposer.cs` with explicit keys (chord rows on the group plus
the ordered chord; a later layer's row for the same key overrides, wholesale
when the meaning differs and entry-by-source when both name the same page;
modifiers on id, a later layer's modifier under a new id that shares a source
with an earlier one absorbing it and rewriting every chord that held it;
`contexts` rows — `{family, state, group}`, deriving a seat's active group
from published engine state — on the `(family, state)` pair, a later layer
overriding in place and new keys appending). The engine names no group: it
publishes facts (`roster`, `engagement`, `layout`) and the document's `contexts` rows say which
group each selects; a seat with no matching row resolves in the first row's
group, and `player.bind` lands on the seat's active group's resting page.
The merged document compiles once per change through the binding stack in
`Puck.Commands` (`BindingProfile.Compile` — deliberately public and shared,
never copied here), and there is exactly one consumer of the compiled result,
so no second authoring grammar or dispatch path exists. The live `help` text
of `player.bind` and `player.bindings` documents the console surface.

## The HUD document

`WorldHud.cs` holds the `hud` section: panels of elements, each element bound
to a value through `HudBindingVocabulary` — a closed vocabulary (`world.tick`,
`world.fps`, `seat.<n>.position.*`, `population.active`, `state.<row>`,
`state.<row>.<key>`), refused by name outside it — and each panel carrying its
draw band (`WorldHudLayer`: under, over, or replace) as a document property.
`state.<row>` binds the row's own SLOT cell (unchanged); `state.<row>.<key>`
binds one named cell in ANY row shape, with a gauge's fraction still read from
the ROW's own declared `min`/`max` (cells carry no envelope of their own) —
the split on the FIRST dot after `state.` is unambiguous because a row/cell
name can never itself hold a dot (`WorldCellName`, below). `HudValidation.cs`
gates capacities, ids, and — for a `state.*` binding — that the named row (and,
for the cell form, the named cell) actually exists in the document's own
`state` section. An owned identity world may carry the same HUD and binding
primitives as any other world.

A `Text` element may carry a `Template` instead of a `Binding` — literal text
interleaved with `{token}` placeholders, each token the SAME closed
`HudBindingVocabulary` a `Binding` speaks (`HudTemplate.TryParse` in
`WorldHud.cs`; `{{`/`}}` escape a literal brace). `Binding` and `Template` are
refused together on one element (`hud.TemplateBindingConflict`); every
placeholder is validated the same way a plain `Binding` is (closed vocabulary,
plus — for `state.*` — the document's own row/cell existence), refusing by
name (`hud.UnknownTemplatePlaceholder`) at the first bad one, and a malformed
brace/escape sequence refuses as `hud.MalformedTemplate`. `HudTemplate.TryParse`
is the ONLY thing that reads this grammar: `Puck.Overlays` (the render-time
writer) cannot reference this project, so `Puck.World`'s `WorldHudFeed` parses
on the structure rebuild and hands the writer PRE-PARSED runs rather than a
string — the same direction `WorldHudCapacity`'s ceilings travel: the
composition root hands `Puck.Overlays` an `OverlayCapacity` built from them
(`Puck.World.Client.WorldOverlayCapacity.FromSchema()`), never a restated
number.

## Medium fields — a fluid free surface, as lattice content

A `state.world` row's `lattice` trait may carry a `medium` facet
(`WorldLatticeMedium`, `WorldFields.cs`): the row's value times its
`heightScale`, over the lattice origin, is a fluid free surface every active
body samples at its coupled cell each tick (the same coupling
`WorldFieldLattice.TryBodyCellOf` resolves for `emit`/`expose`). No document
authors a global waterline any more — a medium is lattice content like any
other field, so it can vary by region, rise and fall under a reaction, or be
absent entirely (a body outside every medium field's footprint floats
against nothing). `medium` refuses without a `heightScale` greater than
zero — a surface-less medium is meaningless. A kit's `bond: "Medium"` hold row
is the live consumer — its law reads the surface
`WorldPopulation.SampleMediumSurfaces` resamples onto each body every tick —
and a hover kit's float-over height remains a later, not-yet-built consumer.
`world.status` echoes the declared medium field names (`none` when there is
no medium row).

## The `gravity` section — acceleration, not geometry

`WorldGravity.cs` holds the optional `gravity` section. `uniform` is a constant
acceleration vector in world units per second squared. Static point sources may
be authored either as explicit `attractors` (`placementId` plus mass), or as
designer-facing `points` (`placementId`, positive `surfaceGravity`, positive
`referenceRadius`). A point row promises the named acceleration at the named
radius after the section's Plummer `softeningLength` is applied; the thick
validator lowers that promise through the fixed Q48.16 kernel and refuses an
underflow or overflow before the server boots. The compiled source order is
explicit attractors followed by points, each in authored order.

The `gravitationalConstant` may remain zero for a uniform-only field, but must
be positive when `points` is nonempty. A positive constant also activates the
global body-to-body solve when no static source is authored: bodies remain both
sources and targets, and a lone body participates with the solver's zero answer
rather than silently falling back to the held row's own gravity. A placement may appear in only one source
row across both spellings. Point authoring never reads a placement's solid or
SDF: its transform locates the source, while geometry and acceleration remain
separate decisions. `world.gravity` reads authored values, derived point masses,
and the last solver work counters back; `world.budget` includes the static
source count and last evaluation counts.

Optional `areas` add bounded local influences without introducing another
physics system. Each row rides a placement and declares `priority`, explicit
`Combine` or `Replace`, an analytic `$type` bound (`sphere` with `radius`, or a
yaw-local `box` with `halfExtents`), and an acceleration `$type`: `directional`
with a placement-local vector, or `radial` with a constant magnitude toward the
placement origin. Bounds include their boundary and scale with the placement;
directional vectors rotate with its yaw. When the placement carries `attach`,
the area follows the same authoritative fixed-point body pose as its region,
analytic collider, and emission. An inactive carrier contributes no area.

For each body, the runtime starts with uniform plus the existing global solve,
then folds matching areas in ascending `(priority, authored row index)` order.
`Combine` adds; `Replace` assigns, so a higher-priority or later equal-priority
Replace wins. A zero directional Replace deliberately authors a zero-G pocket,
and exact cancellation or the center of a radial area remains an authored zero
answer rather than falling back to the held row's own gravity. In an areas-only world, a body
outside every area does not participate and retains that same fallback. The cap is
64 areas; `world.gravity` echoes the compiled order and last area checks/matches,
and `world.budget` reports declared areas per target plus those live counters.
Every global/uniform/area addition saturates componentwise at the Q48.16 extrema
rather than wrapping direction; a later Replace still resets the accumulated value.

These bounds stay analytic by design. An arbitrary SDF-bounded influence needs
a deliberate cross-project asset/query contract; per-body masks belong at that
same future extension seam. Neither is inferred from placement geometry today.

## The `metadata` section — free-form author-facing facts

`WorldMetadata.cs` holds the optional `metadata` section (`WorldMetadataSection`):
`title`, `description`, `authors` (each an optional Entra `oid`, checked with
`WorldEntraObjectId.IsValid`), `tags`, and a `custom` bag typed
`IDictionary<string, JsonElement>` — an author scratch space nothing here reads
or dispatches on. This is deliberately NOT `Extensions` (below): `Extensions`
is a typo catcher — any unrecognized top-level key is a hard load failure —
while `metadata` is the ordinary, named, validated home for content an author
actually wants. `custom` follows `WorldDocumentBasis`'s generic nested-object
merge rule, with the same two carve-outs every nested object under a `basis`
delta observes: a key literally named `$drop`/`$replace` refuses at
validation, and an authored JSON `null` deletes the inherited key rather than
storing a literal null. `title`/`description` cross to a Presentation-tier
peer as `WorldProjectedMetadata`; `authors`/`tags`/`custom` never do (see "The
egress documents" below). The section carries no mutation dispatch axis and no
grant subject — boot-authored data, read back with `world.metadata`.

## The `state` document — genre-neutral game state, one CELL substrate

`WorldState.cs` holds the `state` section: named rows (`WorldStateRow`) over
one typed-value CELL substrate (`WorldStateCell`, `CellKind` —
`int`/`fixed`/`bool`/`text`, never float, the determinism contract), capped
at `WorldStateCapacity.MaxRows` rows and `MaxTextValueLength` characters per
text cell. A SLOT (the common one-value case) is a row with exactly one
cell keyed by the reserved `WorldStateRow.SlotKey`; a KEYED row carries
author-chosen keys — **a slot is a table with one key**, one mechanism under
ONE authored spelling (`WorldStateRowJsonConverter` in
`WorldDefinitionSerialization.cs`):

```json
{"name":.., "kind":"int"|"fixed"|"bool"|"text",
 "value":..              // sugar for one cell keyed "$value"
 "cells":[{"key":..,"value":..}],   // ... OR the keyed form; never both
 "min":.., "max":.., "capacity":.., "nonNegative":..}
```

`WorldStateCatalog.Compile` turns this authored inventory into immutable typed
descriptors for runtime processors. Each descriptor records its ownership lane
(`world`, `body`, or `identity`), storage shape (`slot`, `keyed`, or `lattice`),
value kind, one stable catalog ordinal, and its document-order ordinal within
the lane. A processor resolves `(lane, name)` once to a `WorldStateHandle`, then
indexes that same catalog by the catalog-bound handle instead of repeating
string lookup during execution. `WorldDefinition.StateCatalog` is the
non-serialized compiled view. Definitions sharing one `StateRaw` share that
view; value-only updates through `WithWorldState` retain it when the declaration
shape is unchanged, so processor handles remain live across ordinary state
writes. A name, lane, storage-shape, value-kind, or order change produces a new
catalog, and that catalog refuses handles minted by the previous instance. The
catalog changes no state value, document spelling, or storage implementation.

`kind` is required; `value` and `cells` are two optional fields, not two
`$type` discriminators — a row carrying both, or a `value` beside a
`capacity`, is refused by name, and the canonical writer emits `value` back
for a slot-shaped row so a load→save round-trip is byte-identical. There is
no `$type` and no `rows` member; the retired spellings refuse as unmapped
members like any other stale field, and the addon mutation decoder speaks
this identical grammar rather than forking one of its own. `name` and every
cell `key` are `WorldCellName` (`WorldSafeName.cs`) — a validated type that
CANNOT hold an empty, unsafe, or dotted value, refusing at JSON parse (naming
the offending character) rather than at whole-document validation; the
dot-free rule is what makes the `state.<row>.<key>` HUD binding grammar
unambiguous, since neither half of that token can itself contain the
separator. `nonNegative`
is what a "timer" meant before the table primitive's separate
`counter`/`timer` vocabulary reconciled into this same four-token `CellKind`
(a counter IS `fixed`; a timer IS `int` + `nonNegative`). A row-wide `Min`/
`Max` are BOTH-OR-NEITHER; a HUD gauge bound to `state.<row>` (the slot form)
or `state.<row>.<key>` (the cell form) reads that declared range off the ROW
either way — a plain `state.<row>` binding on a KEYED row (no single cell to
show) draws empty, the same "unbound gauge" precedent (see the HUD section
above and `HudBindingVocabulary`). Fixed-kind values are
DECIMAL text everywhere a human reads or writes them (document JSON,
console verb arguments, addon mutation payloads, validator refusal text,
read-backs) — never raw Q48.16 bits; only the per-cell mutation payload and
the addon ABI channel convention stay raw. A row's whole shape mutates
through `WorldMutation.UpsertStateRow`/`RemoveStateRow`; one cell mutates
through `WorldMutation.UpsertStateCell`/`RemoveStateCell` (works on ANY row,
slot or keyed — pass `SlotKey` to reach a one-value row's own cell). Every
mutation pair here is checked TWICE: the standard `Mutate`/`section:state`
hold, plus a second, row-scoped `Edit`/`state:<name>` hold
(`GrantSubjectKind.State` — the former separate `GrantSubjectKind.Table`
subject is retired, since one row now has one subject) — narrower authority
than any other section, deliberately, since a `state` row is genre-authored
game data an operator may want to hand out per-row (score to one addon,
inventory to another) rather than all-or-nothing per section. That `Edit`
row may additionally carry a `MutationKindMask` (`WorldGrant.KindMask`),
narrowing further to WHICH of the five kinds it admits — the difference
between bumping a row and redefining it, and (with `verbs:Generate`) between
REDRAWING a draw site and re-authoring it.

**One READ seam over the whole section.** `WorldDefinitionRows.FindStateRow` is
the one row-find (ordinal, allocation-free, beside `FindCreation`/`FindPlacement`
/`FindKit`/`FindSpawnPoint`), and `WorldStateReader.TryRead(definition, rowName,
key, tick, out row, out rawValue, out text)` is the one (row, key) → raw-value
read. Every live read routes through it: a rule's gate comparand and live copy
operand, a rule effect's read-modify-write, the `world.state` console read-backs
at every grain, the HUD `state.<row>`/`state.<row>.<key>` binding, and the
`UpsertStateCell` Add compose arm — so no two of them can drift in which cell a
pair names, or in what that cell currently holds. A null `key` means the row's
slot cell; an unknown row returns `false`, while a known row with no such cell
returns `true` with a null `rawValue`, because a rule effect treats an absent
cell as zero but an absent ROW as nothing to write. The `tick` parameter carries
the instant the read answers AS OF (the server's completed tick authoritatively,
the last delivered snapshot's tick on the client — which is itself a SERVER
tick, so it is comparable to an epoch) and it is what an ADVANCE row's value is
computed at (below). The reader hands back a RAW value rather than a
`WorldStateCell` for exactly that reason: a computed value has no stored cell to
hand back, and minting one per read would allocate on the per-frame HUD path.
The whole-document validator
deliberately stays off this seam: it builds a name-keyed map once per walk, and
a linear scan per lookup would make validation quadratic. So do the durable
identity-document reads (`WorldIdentity`, `Server/WorldOwnedWorlds`) — an
identity document has no server and no tick, so it has nothing honest to pass
and an advance row it carries reads FROZEN at its stored base; folding them on
is a design decision about what identity state means over time, not a
consolidation.

## `state.lattices` — fields folded into state

A lattice is not a separate section: `state.lattices` (`WorldStateLatticeTopology`
— name, origin, `cellSize`, `width`×`depth`×`layers`, `stepEveryTicks`,
`reactions`) plus a `lattice` trait on ordinary `fixed`-kind `state.world` rows
(`WorldStateLatticeTrait` — `topology`, `initial`/`min`/`max`, optional
`heightScale`/`color`, `paint`) is the whole spelling.
`WorldFieldsSection.Compile(state)` assembles the runtime composite
(`WorldDefinition.Fields`, cached, never an authored member); `ToStateSection`
is its inverse, for the projection reconstruction that must hand a client-side
definition the same lattice through the identical state section the compile
reads. A cell write against a lattice row (`world.state.cell.set`) refuses
through whole-document revalidation — a lattice row's cells are simulation
state, never authored cells.

**Paint** (`WorldLatticeFill`, `$type`-discriminated) seeds a row before its
first step: `rect` (a world-space box takes one value), `noise` (fixed-point
hash-lattice fBm over the cell index, thresholded into smooth patches),
`scatter` (one jittered disc per `spacing`-cell block, integer-hash offset),
and `draw` (every cell drawn from a numeric authored-randomness source — the
per-cell lattice draw). Every hash decision is integer-hash + `FixedQ4816`
arithmetic, so a fill is bit-identical on every machine and backend; every
hash fill's own `Seed` folds with `generation.worldSeed`, so the world's one
reroll lever moves the terrain.

A `draw` fill (`{ "$type": "draw", "source": … | "generator": … }`, at most one
per row, numeric sources only) is one whole-field pass of the row's own draw
stream, seeded through the same ladder as a state-row site under the row's
`state.<row>` descriptor: cell `k`, in cell-index order, takes the sample a site
at `drawCursor + k` would draw, with a weighted source's deck threaded cell to
cell — so a `weightedNumeric` bag in `reshuffleOnExhaustion` mode deals its
cards across the field and reshuffles as it goes, and outcome `count`s make a
field carry exactly N cells of a value per pass. The row's `drawCursor`/
`drawDecks` name the pass currently painted; `world.generate <row>` advances
them one whole pass (the cell count) and repaints. The draw occupies its authored
position in `paint`: it overwrites earlier fills and later fills overwrite it.
Boot, whole-document rebuild/load/reset, and an undo that rewinds the draw
position repaint the pass the document names, so restored state lands on the
field it last drew without resetting unrelated reaction-evolved rows.
Reactions then evolve the drawn cells like any other paint. `world.state`
echoes `draw source=… fill=lattice cursor=<n> decks=…` on the row line.

**Reactions** (`WorldReaction`, `$type`-discriminated, applied in document
order every `stepEveryTicks`): `diffuse` (each cell moves a fraction toward its
face-neighbour mean), `decay` (`v -= v·rate`), `transform` (where every `when`
condition holds at a cell, apply every `then` write — ignition, melting,
evaporation, and freezing are rows of this shape), `emit` (every active body
tagged nonzero in a keyed row deposits into the field cell it stands in),
`expose` (writes 1/0 into a keyed row per body by a field test at the body's
cell — the bridge to body-level chemistry), and `flow` (moves a field downhill,
mass-conserving, over the combined surface height of itself plus its `over`
terrain fields; each cell donates an equal share of its previous-step value to
each of the lattice's active-axis directions, and an optional `spillRow`
catches what an edge cell would otherwise send past the lattice boundary —
without one, edges are walls). A reaction scalar
(`WorldLatticeScalar`) is a JSON number or `{"row": "name"}` naming a scalar
`fixed`-kind state row's slot, read fresh every step — a season or
weather-intensity row modulates chemistry live with no new reaction kind; an
unwritten referenced row reads `0`, so a row-gated reaction is inert until
something writes it. A row-driven `diffuse`/`decay`/`flow` rate clamps to
`[0, 1]`.

`WorldFieldProgram.Compile` is the typed reaction-program view over that same
spelling, not a second graph language. It resolves lattice rows and
scalar/tag/output state dependencies once into
`WorldFieldHandle`/`WorldStateHandle` values, exposes its canonical
`StateCatalog`, quantizes literal scalars to `FixedQ4816` at the compiler
boundary, preserves reaction order as stable node order, and exposes immutable
canonical read/write sets plus the dependency DAG they imply and exact
cell-node/full-cell/body-pass cost classes. It is consumed alongside
`WorldDefinition.Fields`, which remains the complete topology, paint,
initialization, and display composite. `WorldDefinition.FieldProgram` is the
cached non-serialized door: unrelated definition edits and value-only state
updates preserve compatible program handles, while a field declaration or
reaction-program change creates a replacement. This is the shared reaction
boundary an editor, inspector, scheduler, and future runtime lowering consume;
nodes own no hidden state or random stream. The authoritative lattice executes
those nodes directly; it does not recompile reaction rows into a private
second form. A live reaction-only replacement installs a new compatible
program without reseeding cells, while topology, cadence, or field-envelope
changes refuse with restart guidance rather than attempting an implicit cell
migration. `world.fields` appends the installed node order, dependency edges,
and cell/body pass counts to its ordinary lattice statistics.

A row with `heightScale` IS geometry: its value raises a solid column above the
lattice origin (unioned with the authored solids for contact) that the
renderer shows as a CPU-baked distance brick, coloured by `color`. Capacity
(`WorldFieldCapacity`): `MaxCells` 262,144 (width × depth × layers, so a full
eight-row primer fits the federation wire's 32 MiB frame), `MaxFields` 8,
`MaxExtent` 1024 per axis, `MaxLayers` 128, `MaxSurfaceCells` 126 (a
height-bearing row's XZ footprint, and the cross-layer sum where several
layers raise), `MaxReactions` 64, `MaxTransformTerms` 64, `MaxPaint` 256. Read
back with `world.fields`; the exact structural cost (cell count × compiled
full-cell passes, plus body capacity × body passes, at the authored cadence)
folds into `world.budget`.

## Authored randomness: SOURCE x SITE x MOMENT

Everything random in a document is one primitive with three parts, and they are
deliberately separable: a **source** is a shape, a **site** is a place that
draws, and a **moment** is when.

**The SOURCE family** (`WorldGenerator`) is the document's whole randomness
vocabulary. `source` selects the shape: `markov` walks weighted alternatives per
context, each naming the context it moves INTO (that authored `next` is what
makes it a Markov process rather than independent draws — the context key IS the
process state) and is the only shape that writes TEXT and the only one that
DEALS per context; `uniformRange` draws one value over `[rangeMin, rangeMax]`;
`weightedNumeric` draws one value from an authored alias table and DEALS over
its outcome set under the same `mode` vocabulary (one `drawDecks` mask — the
numeric shuffle bag); `streamDraw` yields one raw 32-bit draw; `symmetryOrbit`
draws one node index uniformly over an orbit of the symmetry lattice — the
thirty nodes of `ring` (0..7), or the orbit of `node` (0..239) under `word`
(the same one-to-eight-mirror word a `cycle` trait authors, or omitted for the
lattice's own cycle, so the node's ring) — and deals that orbit under `mode`
through the same one mask, so `withoutReplacement` on a ring is "every node of
the ring once per pass". An alternative or
outcome may declare `count`: under a deck mode it is that many cards per pass
(an outcome that should come out twice per pass declares `2`), under
`withReplacement` it only scales the weight; a set's cards total at most 256,
one deck-mask bit each. Each shape reads a disjoint field set, and a foreign
field is refused BY NAME rather than parsed and ignored — including `bound` and
`mode`, which are non-nullable and so are refused against their declared
defaults. A markov emission is one walk from `start` to a TERMINAL context (one
declaring no alternatives), refusing BY NAME at `bound` rather than truncating;
`mode` is `withReplacement`, `withoutReplacement` (dealt out → refuse by name) or
`reshuffleOnExhaustion`; `uniformRange` and `streamDraw` refuse a `mode`, having no
entry set to deal from. Caps live in `WorldGeneratorCapacity`. The alias table
over a source's full entry set is built once per source instance and held weakly
beside it (`WorldGeneratorEngine`'s compiled cache), so a site drawn every tick
pays the build once; a dealt-down pool mid-pass is rebuilt in bounded stack
storage per emission, with no heap table allocation and the same exact alias
mapping.

**A source holds no position.** It may be declared once in the optional
`generators` section (`WorldGeneratorRow`: `name` + `generator`) and referenced
by any number of sites, or inlined at one site as sugar — the two spellings
compile to the identical record, so nothing is expressible one way and not the
other.

**The SITE facet** (`WorldDraw`) declares that a value is drawn. It carries
exactly one of `source` (naming a declared row) or `generator` (an inline
source), plus `timing`. **One site type exists: `WorldStateRow.Draw`** — the
draw facet's single home. `bodies.capacityRow`/`host.backendRow` are plain
strings naming a state row, not sites of their own: they READ that row's
already-resolved slot, after row first-fills, rather than drawing anything
directly (see "Two boot-time reads, one site rule" below). The
CURSOR and the dealt DECKS live on the SITE (`WorldStateRow.DrawCursor`/
`DrawDecks`), never on the source — which is exactly what lets two sites
reference one table and draw INDEPENDENT sequences. That independence is what
makes a reference safe: sharing a source shares its SHAPE and never its
position, so pointing a second site at an existing table cannot perturb the
first. Living in the DOCUMENT is the point: `WorldMutation.Generate` is a pure
function of (candidate, instance identity), so `world.undo` rewinds a draw
bit-identically with nothing to reconcile.

**The MOMENT** (`WorldDrawTiming`) is `boot` (drawn once at first fill; a later
`generate` refuses by name), `tickPeriod`, or `event`. The latter two both stay
redrawable through the SAME `Generate` mutation (ordinal 51); the actual cadence
or gate is spelled with the ordinary `rules` vocabulary (a `$tick`-scheduled Edge
rule, an event-flag-gated one), so timing costs NO mutation ordinal — the catalog
stays 64/64.

**The seed ladder is four rungs** (`WorldGeneratorEngine.ComputeSeedState`), each
LENGTH-DELIMITED before its bytes so no two rung sequences can fold to one
pre-image: the engine constant (so this system's streams cannot collide with any
other seeded system by accident), `generation.worldSeed` (the author's one
reroll-the-world lever), the running INSTANCE identity (so three instances of one
document draw differently, each reproducibly), and the SITE DESCRIPTOR (what
separates two sites). The descriptor is an IDENTITY, never a position: a
positional ordinal is read off the LIVE site set, which moves whenever a settled
facet clears, a `world.row.remove state` retires a draw row, or an `UpsertStateRow`
adds one — silently re-pointing a live site's stream while its cursor kept
counting. The `Pcg32XshRr` stream id derives from the descriptor alone, masked
small so derived ids stay far inside the `2^62` band that primitive warns about.

**The engine SEEKS; it never replays.** Every source costs a FIXED number of
generator advances per sample (`AdvancesPerSample`), which is why `uniformRange`
is a multiply-high map of a `UnitFraction32` rather than the rejection-sampled
`NextUInt32(min, max)`: resuming a site at cursor `n` is one `Advance(n * cost)`,
an O(1) jump. There is no per-tick cadence ceiling — a rule redrawing a site
every tick costs the same at cursor 1,000,000 as at cursor 0. The trade is
honest and stated: the uniform map is uniform to within `n/2^32`, exactly zero
when `n` divides `2^32`, rather than exactly uniform.

**One site type, never cleared.** Every draw site is a `WorldStateRow.Draw`
facet — the draw facet's single home. `WorldDrawBootResolver` fills a slot
row only while it carries no cell yet (first fill: process boot, or a fresh
`world.instance.start`), keeps the facet and cursor, and resumes on reload
rather than re-rolling a value the player already saw; nothing settles into a
bare literal and nothing is cleared. A KEYED row may be a site too — a dice
tray: its numeric source fills one sample per authored cell in cell order at
its first fill (cursor zero), and `world.generate <row> [key ...]` /
`WorldMutation.Generate(row, keys)` redraws every cell or only the named ones
with the rest held, advancing the cursor by the cells drawn. A text source
refuses a keyed site. `bodies.capacityRow`/
`host.backendRow` are boot-time READS layered over this, resolved AFTER every
row's first fill so a boot-drawn row is readable the same boot it draws:
`capacityRow` names a scalar `int` row, `backendRow` a scalar `text` row
(parsed through `WorldHostTokens.ParseBackend`, refusing a token naming no
backend — never an unnamed ordinal, which would silently re-point itself the
day an enum member is inserted). Each reads the row's slot into the ordinary
literal field (`bodies.capacity`/`host.backend`) and narrates the read on
stderr (`[world.draw: settled bodies.capacity instance=… -> …]`), but the ROW
stays the persisted evidence — its cursor and value survive in `world.state`,
and every fresh load re-reads it. `host.backendRow` is XOR-by-presence against
`host.backend` (`WorldHostDefaults` is a class, so a null `Backend` is
honestly distinguishable from an authored one, and declaring both refuses by
name); `bodies.capacityRow` beside a literal `capacity` is legitimate instead
— `WorldPopulationDefaults` is a struct, so the row is simply the source of
truth and overwrites the literal on every fresh load, nothing shadowed
silently.

**Draw domains are narrowed STATICALLY**, against the site's own admissible range
(a state row's `min`/`max`/`nonNegative`, the census coherence sum for
`bodies.capacity`, every reachable token for `host.backend`). Without that, a
draw the validator admits could produce a value the SAME validator refuses on the
resolved document — so whether the world boots would depend on what it rolled, a
refusal moving with the world seed and the instance identity. Refusing the
authoring mismatch makes the door the type rather than the outcome.

**A row may instead declare an ADVANCE** (`WorldStateAdvance`): `rateNumerator`/
`rateDenominator` (an exact per-tick rate, in the row's own DISPLAYED unit — for
a `fixed` row `1/1` is `1.0` per tick, and a rate far slower than one raw Q48.16
tick still accumulates exactly; may be negative for decay, which mirrors the
positive rate of equal magnitude rather than flooring the signed quantity) and
`epochTick` (the tick it starts from). The stored slot cell is a BASE value; the
READ value is `base + rate*(currentTick-epochTick)`, computed lazily on every
read via `Puck.Maths.DiscreteMeasure`'s exact rational allocation — nothing
per-tick materializes and nothing per-tick journals. Legitimate only on an
int/fixed SCALAR row (no `capacity`, no non-empty `cells`) and never beside
`draw` — a row is an authored-randomness draw site or a continuous accumulator,
never both. An explicit write (`UpsertStateRow`, or `UpsertStateCell` naming the row's
own `SlotKey`) RE-BASES: the written value becomes the new base and the epoch
becomes the tick the write applied at, `WorldServer.RebaseCellTraits`'s job,
run for both a live apply and `world.undo`'s per-entry replay (keyed off the
journal entry's own tick) so undo rewinds an advancing row exactly like it
rewinds a draw site's `drawCursor`. A declared `min`/`max`/`nonNegative` envelope
CLAMPS the computed value on every read without rewriting the stored base — the
read side of the envelope duality (a computed value clamps; an explicit write
refuses).

`WorldStateAdvance.ComputeCurrentValue` has one application site,
`WorldStateReader`'s known-cell computation, and that is the whole design: both
the name and compiled-handle read entrances, a rule's
`compareState`, a HUD gauge, `world.state`'s read-backs and the
`UpsertStateCell` **Add compose arm** all resolve through it, so a reader and a
writer can never disagree about what an advancing row holds. In particular an
`add` composes against the LIVE value and then re-bases — a regen row sitting at
a live 41 taking a `-10` lands on 31 and keeps advancing, where composing onto
the stored base would have landed on -10 and silently discarded everything
accumulated since the epoch. A row declared with no value carries no slot cell
at all, so a rule READING it refuses `StateCellUndeclared` until the first
write. `world.state`'s row line echoes the trait as
`advance=<num>/<den>@epoch<n>`.

**`world.save` settles an advancing row/cell, in the serialized PROJECTION
only.** `epochTick` is SESSION-relative (a tick
count from process start), so writing it verbatim to a saved file left a
reloaded document reading FROZEN at its stored base until the NEW session's
own tick counter climbed back past the OLD epoch. `Puck.World`'s
`WorldSessionCapture.Capture` (the `world.save` fold) writes every
advancing row's slot cell, and every advancing keyed cell's own base, as its
LIVE computed value at the save tick, and projects `epochTick: 0` — so tick 0
of the reloaded session already reads that value and keeps advancing
immediately. The LIVE in-memory document is never touched (a save is a
snapshot, not a mutation, exactly like every other session dimension this
fold folds) — only the bytes written to disk carry the settled base/epoch.

**A KEYED row's own cells advance INDEPENDENTLY** through `WorldStateCell.Advance`
— the same `WorldStateAdvance` shape, authored per cell instead of per row:

```json
{"name":"threat","kind":"fixed","capacity":8,
 "cells":[
   {"key":"body:0","value":"40.0","advance":{"rateNumerator":1,"rateDenominator":4,"epochTick":0}},
   {"key":"body:1","value":"10.0","advance":{"rateNumerator":-1,"rateDenominator":2,"epochTick":120}}
 ]}
```

Each cell's stored `value` is its own BASE, accumulating from its own
`epochTick` at its own rate — a body's HP, threat, or resource regenerates
(or drains) on its own clock, independent of every other cell in the same
row. Legitimate only on a NON-reserved cell key: the reserved slot key
(`WorldStateRow.SlotKey`) may carry only the row's OWN `advance` (above),
never a cell-level one — the two never both name the same cell, so "which
advance governs this cell" is never an open question. A DRAW SITE's own
bookkeeping is not reachable here at all: `drawCursor`/`drawDecks` are typed row
FIELDS, never cells, so nothing can name them as an accumulator. `WorldStateReader.TryRead` checks the row's own trait first
(only relevant for the slot cell) and falls back to the CELL's own trait
otherwise, so a scalar row's behavior is untouched. Because
`WorldStateReader.Reduce`/`ArgExtremum` resolve the row once and each candidate
cell once through that identical known-cell computation rather than repeating
row/key scans or reading `WorldStateCell.Value` directly. A
`$reduce:sum`/`$argmax:`/`$argmin:` rule operand over a table of independently
advancing cells therefore sees every cell's LIVE value in one linear pass. A per-cell VALUE write
(`world.state.cell.set`, `UpsertStateCell`) carries no advance payload of its
own, so it PRESERVES whatever the cell already declared and re-bases its
epoch to the write's tick — `WorldServer.RebaseCellTraits`'s widened job,
run for a whole-row `UpsertStateRow` (which re-bases the row's own slot trait
AND every keyed cell's own trait, since it re-declares the whole row) and for
a per-cell `UpsertStateCell` (which re-bases only the ONE cell it names) —
both for a live apply and for `world.undo`'s per-entry replay, exactly as it
already did for the scalar case. `world.state`'s cell line echoes a cell's
own trait the same way the row line echoes the row's:
`advance=<num>/<den>@epoch<n>`. A value that must wrap is a `cycle` row
(below), never an advance.

**A row or keyed cell may instead declare `dynamics`** (`WorldStateDynamics`
— `row`, `y0`, `v0`, `epochTick`), `advance`'s closed-form sibling: mutually
exclusive with `advance`/`draw`/a bare `value`, naming a `dynamics` section
row whose pole-matched second-order response `WorldStateReader.TryReadEased`
evaluates lazily from `(y0, v0)` at the elapsed tick — no per-tick write. `y0`
and `v0` are ALWAYS raw Q48.16 continuous-state bits, including on an `int`
row; only the stored target and the final presented sample use the carrying
row's encoding. That preserves sub-unit position and velocity across an integer
target's rebase instead of quantizing the follower at every write. The
stored cell value stays the TRUTH the target; a write rebases the trait
(`RebaseCellTraits`'s `RebaseDynamics` arm) the same way it rebases `advance`
— the live eased sample and a `Retarget` velocity kick become the new
`(y0, v0)` at the writing tick. `y0`/`v0` are authored and echoed in the fixed spelling on every row kind (they are the follower's
continuous state, not the row's unit). `world.state` echoes
`dynamics=<row> y0=<v> v0=<v>@epoch<n> eased=<v>` beside `value=`.

**A row or keyed cell may instead declare `cycle`** (`WorldStateCycle` —
`word`, `power`, `output`, `ticksPerStep`, `epochTick`, `substepTicks`): the
tick-indexed rotation, mutually exclusive with `advance`/`dynamics`/`draw`/
`lattice` and scalar-only at the row level the same way those are. The value
is a pure function of the server tick through a generator of the symmetry
lattice's reflection group (`Puck.Maths.SymmetryWord`): `word` is one to eight
mirror nodes applied first to last, or omitted for the lattice's own
thirty-step cycle (`Puck.Maths.CyclicRotation`), and `power` (nonzero, default
1) is how many applications one step is — with no word, powers 1, 7, 11 and 13
are the cycle's four rotation planes. The loop's period is the generator's
order, derived from the word rather than authored: a word of order twelve is a
twelve-position dial, and `world.symmetry.word <mirror>... [node:<n>]` prints a
word's order and a node's orbit before it is authored. A word that moves no
node, a power of zero, and a power at or past the order are refused. One step
lasts `ticksPerStep` ticks from `epochTick`. `output` names what the cell
reads: `Step` (0..order−1), `Node` or `Ring` (the node's ring, 0..7) on an
`int` row; `Turns` (`⌊step·2^16/order⌋` raw, so it wraps once per loop the way
`render.cycle` keys read a row), `Cos`, `Sin` (the order's root of unity at the
step), `ProjectionX` or `ProjectionY` on a `fixed` row. The lattice outputs
read through `Puck.Maths.SymmetryLattice`: the stored value is the node
(0..239) the orbit walk starts from, carried `power` generator applications
per step, and the projection outputs are that node's point on the plane of
eight concentric rings of thirty; the lattice's own cycle never leaves a ring,
so `Ring` is constant under it and moves only under a word whose orbits cross
rings. The stored cell value is the PHASE, in the
row's displayed unit (whole steps or a node index; a `fixed` row's phase is the
whole part of its value): nothing accumulates and nothing rebases, an explicit
write or a rule's `setState` sets the phase and `addState` turns it by whole
steps, and a declared envelope clamps the computed value on every read as it
does an advancing row's. A cycling row is refused as a `state:<row>` control
context the same way an advancing one is. `world.state` echoes
`cycle=<coxeter|[m,…]>^<power>:<output>/<ticksPerStep>@epoch<n> order=<order>`
beside the live `value=`, with `+<substepTicks>` appended after the epoch when
a settled document carries part of a step.
`world.save` settles a cycling cell in the serialized projection only: the
stored value becomes the current rotation index (or node), `epochTick` projects
to `0`, and `substepTicks` carries the elapsed portion of the current step. A
reload therefore preserves both the first value and the tick of the next
transition; `substepTicks` is refused outside `[0, ticksPerStep)`.

**A keyed `text`-kind row IS the text-table primitive** — an authored, named
collection of strings (flavor lines, names, phrases) a HUD `Binding` or
`Template` reads FROM (`state.<row>.<key>`, see "The HUD document" above). A
DRAWN string is a different shape — a text-kind DRAW SITE is scalar, redrawn in
place by `world.generate <row>` (above), never emitted into another row's cell.
No separate schema exists for this — deliberately: a second "table of
strings" concept beside the row/cell substrate would repeat the
`GrantSubjectKind.Table`/`state:<name>` duplication this project already
retired once (see the `Edit` hold paragraph above). Per-cell live text
writes ride `world.state.cell.set <row> <key> <text...>` (a raw-tail verb,
spaces included, no quoting needed, when `<row>` is ALREADY LIVE as a
text-kind row — one verb for either kind, dispatching on the row's own
declared kind) or `world.row.set state <row-json>`'s whole-row form, the
general document-row door every section shares. The numeric and text writes are TEXT-kind/
numeric-kind siblings riding the SAME `UpsertStateCell` mutation kind (its
optional `Text` payload beside the numeric `Value`), never a second
mutation kind for the same per-cell write.

**A color field is a `#RRGGBB` literal or a `state.<row>[.<key>]` binding to a
text cell holding one** — the same grammar, resolved by `WorldColor.Resolve`
against the hosting world: creation palette entries, `render.lighting`/`render.sky`
colors and every `render.cycle` key's, a screen text source's
`foreground`/`background`. `WorldDefinitionValidator` refuses a binding that
names no declared text cell, or one whose text is not a hex color; the
`CreationCanonicalizer` admits only the binding's syntax (a creation on its own
has no world to resolve against — the world validator resolves it at the
placement). Identity, profile, and `seatDefaults` neutral colors stay literal:
they persist per identity and travel between worlds. A bound color is live —
`world.state.cell.set colors sage #C0392B` re-registers palettes and lighting on
the delivery it composes.

**Spatial values may use the same state grammar.** World and embedded-creation
vector/quaternion fields accept their ordinary array or a `state.<row>[.<key>]`
reference to a text cell holding the matching JSON array. For example,
`"position": "state.spatial.zero3"` may read a cell whose value is
`"[0, 0, 0]"`, while an identity rotation reads `"[0, 0, 0, 1]"`. Resolution
happens after the complete world exists (in particular, an embedded creation
cannot resolve its containing world's state while parsing itself), and the reference remains
on the document value so canonical save writes the indirection back. A live
mutation touching a referenced row rehydrates and resolves a fresh candidate
before validation; a rejected value therefore cannot leak into the live
creation through record-sharing.

**Binding-group identifiers may be state-backed too.** A chord row, context
row, or wheel may name its group literally or with the same
`state.<row>[.<key>]` grammar. The referenced Text cell holds the group name
directly, so all linked rows can share one value—for example,
`"group": "state.bindingGroups.defaultActionGroup"`. Changing that cell
renames every consumer together, then recomposes and validates the complete
binding profile before the mutation is accepted.

**Every door that turns bytes into a live document resolves.**
`WorldStateDocumentValues.TryResolve` runs inside `WorldJsonPayload.TryParse`,
`WorldDefinitionSerialization.Deserialize`, and `WorldProjection.TryToDefinition`,
so a definition delivered across an authority transfer reads exactly like a
file-loaded one and an arriving seat's binding recompose sees resolved
identifiers. A projection carries no `state` section, so `WorldProjection.Compose`
flattens its egress — every reference answered from the composing authority's own
state and dropped (`WorldStateDocumentValues.TryFlatten`, run on a rehydrated copy
so the live document keeps its authored reference) — and a peer whose projection
still names a cell is refused by name at the decode door. Law suite:
`tests/Puck.World.Tests/DeliveredDocumentIdentifierLawTests.cs`.

**Reserved `$` names are ENGINE-MINTED ONLY.** A `$`-prefixed ROW name is refused
outright (nothing mints a row), and a `$`-prefixed CELL key is refused unless it
is exactly the key that row's shape mints (`$value` on a slot, and nothing
else). The rule lives in `WorldDefinitionValidator`, which
runs at boot, at every live mutation and on every undo-replay entry — so a
hand-authored file and a console verb refuse by the same code rather than by one
door the other walks around.

## The `rules` document — the per-body action primitive, one level up

`WorldRules.cs` holds the optional `rules` section. A `WorldRule` is
`(Name, Gate, Effects, Mode, ForEach, Decision)` over the same `ActionPredicate`, `ActionEffect` and
`ActionTriggerMode` types a kit's per-body actions use. `WorldRuleCompiler`
checks the world-scope subset at the document or mutation boundary. Gates admit
`all`, `any`, `not`, `compareState`, and `compareValue`; they compile to a bounded postfix
Boolean program, so nested logic does not allocate or recurse during a tick.

### Decision policies

A rule's optional `decision` chooses among named alternatives. It uses the same
predicates, numeric expressions, effects, and `$each` binding as other rules:
filter out ineligible options, evaluate their scores, then select a winner.
For example, this rule writes a declared integer `activity` row when its choice
changes:

```json
{
  "name": "choose-activity",
  "effects": [],
  "decision": {
    "periodSeconds": 0.1,
    "commitmentSeconds": 0.5,
    "mode": "Weighted",
    "scoreKind": "Int",
    "seed": 7,
    "options": [
      {
        "name": "rest",
        "score": { "tokens": [{ "$type": "constant", "value": 1 }] },
        "effects": [{ "$type": "setState", "state": "activity", "value": 0 }]
      },
      {
        "name": "explore",
        "score": { "tokens": [{ "$type": "constant", "value": 3 }] },
        "effects": [{ "$type": "setState", "state": "activity", "value": 1 }]
      }
    ]
  }
}
```

`HighestScore` picks the greatest eligible score, including negative scores;
ties use option order. `Weighted` uses positive scores as relative weights.
Zero or negative weights cannot win, and an all-ineligible decision chooses
nothing. An invalid arithmetic expression excludes that option for that
reconsideration. `scoreKind` is `Int` or `Fixed` (the default); expression
operands must match it, without implicit numeric casts.

The first evaluation is immediate. Later evaluations wait `periodSeconds` and
the current choice's `commitmentSeconds`, rounded to the next simulation
boundary; they do not catch up by executing several choices in one step.
Durations must be exact whole engine-tick counts. The optional `interrupt`
predicate bypasses both timers on a **rising edge**, not every tick it remains
true. The current option losing its eligibility gate also bypasses both.
`incumbentBonus` adds a bounded preference for staying; in weighted mode it
does not revive a non-positive weight. Keeping the same choice does not renew
commitment or repeat entry effects.

Decision rules require ordinary rule mode `Level`; the choice policy owns
their timing. Common rule effects run before option effects, only on a
selection transition. Either list may be empty. `onNoChoice` runs when an
enabled decision first finds no choice, loses its choice, or its enclosing
gate closes while a choice is held. Closing that gate clears the choice, not
its random history. Selection records intent: effects keep ordinary
transaction/refusal semantics, so a selected option does not imply every
effect succeeded.

Each rule binding owns its reproducible random state. Other decisions and
their evaluation order do not consume its draws. A weighted choice with
multiple positive options uses one 64-bit ticket (two PCG32 draws); this bounds
work without a rejection loop, with probability quantization below 2^-64 per
option. A deterministic choice, no choice, or one positive option consumes no
draws. Numeric aliases of the same `forEach` key evaluate once. A rule's
`forEach` iterates any keyed row: an integer key also binds the `each` body,
a non-integer key (a card or piece token) binds `$each` alone. Removing a
binding, replacing a body generation, or changing the source policy starts a
new decision episode; unrelated recompilation retains the episode and refreshes
its compiled state handles.

An option may expand into nearby individuals through `neighbors`. The enclosing
rule must name a numeric keyed `forEach` row whose keys identify observers.
Only this option's gate, score, and effects gain `left` (observer) and `right`
(candidate) body references, or `$left`/`$right` state keys; `$each` still names
the observer. Common effects, the enclosing gate, `interrupt`, `onNoChoice`,
and options without `neighbors` do not gain these bindings. Inactive or non-body
observer keys have no nearby candidates but can still choose a fixed option.

For example, inside an option, this declaration considers at most eight nearby
individuals from at most 32 inspected points per reconsideration:

```json
"neighbors": {
  "range": 20,
  "candidateBudget": 32,
  "maxCandidates": 8,
  "halfAngleDegrees": 180,
  "requiresLineOfSight": false,
  "retainCurrent": true
}
```

The option can score a social query from `left` to `right`, then enter with
`{"$type":"designateBody","key":"$each","register":"companion","kind":"Body","targetKey":"$right"}`.
The register must be declared, and a producer must consume it to cause movement.
A fixed "alone" option can clear the same register. This selects a movement
companion, not friendship or membership in a social group.

`maxCandidates` is 1..32, no greater than `candidateBudget`; the inspection
budget is 1..the body-capacity ceiling. Range is inclusive, from 1/65536 through
1,000,000 world units, and the forward half-angle is in (0,180]. Coincident
individuals remain perceptible. Each option uses a rotating sample of nearby
grid cells, then retains the nearest eligible sampled individuals. Self,
rejections, and repeated inspection of an incumbent all consume budget; this
is not a globally best-neighbor search. Decision sample phases reverse the low
bits within population-sized power-of-two blocks, exploring distant portions
first without consuming choice randomness. The spatial sampler separates cell
rotation from occupant rotation so a one-inspection budget cannot lock onto only
one residue class of occupants. Poses are frozen before ordinary rules
run. State gates and solid-field sight queries retain ordinary same-tick semantics.

By default, `retainCurrent` spends one inspection explicitly rechecking the
current individual. If perceptible and gate-eligible, it occupies one retained
slot; a budget of one may therefore spend all attention on that individual.
Disabling retention lets the rotating sample replace an unobserved incumbent.
Range, cone, and sight refresh on reconsideration, not every held tick. A lost
incarnation or closed candidate gate interrupts commitment immediately.
The selected incarnation, not just its body slot, is saved and hashed.

Within an option, stable body index breaks score ties and orders weighted
ticket intervals. Each individual is a separate weighted alternative: an option
with more eligible individuals can receive more total probability. The incumbent
bonus applies only to the exact option/individual pair. Switching individuals
repeats entry effects and renews commitment; retaining the same one does neither.
Perception scratch and range-level grids are reused; `world.decisions` exposes
image size, grid builds, inspections, score evaluations, sight tests, and limited
queries. `world.budget` charges candidate gates and expanded scores, even when
ordinary cadence would usually spread them across ticks. It also charges one
shared pose-image visit per population slot and two point visits per grid
rebuild (copying and grouping). The cost sheet separately reports the maximum
poses copied, distinct range-scale grids rebuilt, and total grid points sorted.
Those ceilings assume simultaneous reconsideration; sharing a range scale does
not charge a new grid per option or observer. Structural work units are not a
CPU-time or sort-comparison bound: whole-frame performance still needs measurement.

`world.decisions` reports choices, last evaluated raw scores, timers, and draw
counts. `world.budget` includes all option gates/scores, the current-option and
interrupt checks, and the greatest effect branch in its conservative per-tick
cost. A policy has at most 32 options. Decision state is checkpointed and hashed;
render-frame timing never drives it. These are world-authority rules: using
them for limited creature knowledge requires scoring the creature's observed
state, not unrestricted world facts.

### Social evidence and belief queries

`state.social` is an optional memory policy. It declares named dimensions such
as helpfulness or navigation competence, with independent bounds, baselines,
learning rates, uncertainty, and memory/work limits. It does not make creatures
omniscient or infer their personality. For example, add this member to `state`:

```json
"social": {
  "dimensions": [{ "name": "helpfulness", "maximumChange": 0.25 }],
  "impressionCapacity": 65536,
  "impressionsPerObserver": 256,
  "receiptCapacity": 65536,
  "evidenceAttemptsPerTick": 1024,
  "expiredReceiptsPerTick": 1024,
  "evidenceLifetimeSeconds": 60
}
```

Every relationship names an observer, a subject, and a dimension. An individual
reference contains exactly one of `body` (the ordinary rule grammar, including
`body:0`, `each`, `left`, `right`, `cell:row:key`, or `argmax:row`) and `identity`
(`authority`, `index`, `generation`). Live bodies resolve to their original
mobility incarnation; literal identities remain usable while absent. An
inactive body is unresolved, not the previous occupant.

An `observeSocial` effect carries `evidence`: a `relationship`, an event
`origin`, an `aspect` such as `help.attempt`, and numeric expressions for
`sequence`, `occurredAt`, `value`, and optional `quality`. Sequence and original
occurrence time are non-negative Int values; time uses engine ticks, with
`socialClock` supplying the current clock. Value and quality are Fixed;
quality defaults to one. Optional `source` makes the evidence a report rather
than direct observation. Relays preserve the original event identity and time.
Rules must explicitly gate perception; an effect is a delivery, not a sensor.
`forgetSocial` takes a relationship and removes its impression without erasing
unexpired duplicate receipts. These effects are world-only and cannot be state
transaction steps.

The expression token below reads one belief. Its `query.facet` defaults to
`Value`; `Value`, `Confidence`, `Uncertainty`, and `Weight` produce Fixed values.
`Known`, `EventCount`, and `Age` produce Int values (Age is engine ticks).
An unknown valid identity reads the authored baseline with zero confidence;
an unresolved body reads zero for every facet. Check `Known` when that distinction
matters. EventCount and Age saturate at Int64.MaxValue.

```json
{
  "$type": "social",
  "query": {
    "relationship": {
      "observer": { "body": "each" },
      "subject": { "body": "body:0" },
      "dimension": "helpfulness"
    },
    "facet": "Confidence"
  }
}
```

Social queries work in ordinary numeric effects and decision scores.
`compareValue` compares `left` and `right` expressions using a `comparison`
and one explicit `kind` (`Int` or `Fixed`, default Fixed). Kind mismatches
refuse compilation; failed arithmetic closes the predicate. `socialResult`
returns the last evidence/forgetting outcome as an Int ordinal, or -1 before
an attempt. Sequential effects can store it in an authored row for branching.
The outcome enum and memory semantics live in the
[server contract](../Puck.World.Server/README.md#social-memory-component).

`world.social [<query-json>]` is an operator read-back under `Observe/all`.
Its query uses the same shape as the token's `query`, but cannot use rule-local
bindings. No argument reports policy and work. Memory is runtime state, carried
by authority checkpoints and hashes rather than public document cells.

### World-rule state effects

The state effects are `setState`, `addState`, `countdownState`,
`removeStateCell`, `scheduleState`, and `transaction`. Rules may also generate a
text row, edit HUD panels or placements, save the session, pose or drive an
active body, set or clear one of its target registers, emit a gameplay cue, and
paint a bounded sphere into a live lattice field. Each effect keeps its native
runtime meaning: document and state changes use ordinary `WorldMutation`
admission, while save, pose, body, cue, and lattice effects use dedicated
deterministic paths. A body-only action such as `startTimer` still refuses at
world scope rather than being reinterpreted.

Predicates and effects address a (row, KEY) PAIR: an omitted `key` means the
row's slot cell, and `WorldStateRow.IsKeyed` is the discriminator, so rules reach
keyed rows and not just scalars. `IsKeyed` is deliberately NOT `!IsSlot` — a row
carrying no cells at all is not a slot yet IS slot-addressable, because the first
write mints its slot cell exactly as `world.state.cell.set` does; `IsSlot` asks
whether a single value exists to READ, `IsKeyed` whether an omitted key can
ADDRESS one. A `compareState` may instead name a reserved
channel — `$tick` (the completed-tick counter), `$population` (the live
active-entry count), `$region:<placementId>` (that region's live occupant count),
`$machine:<screen>:<address>` (one live byte off a screen's booted machine),
`$reduce:<max|min|sum|count>:<row>` (an aggregate over a row's cells; append
`:where:<filterRow>` to admit only keys whose numeric filter cell is nonzero),
`$symmetry:<function>[:<argument>]:<row>` (a cell holding a symmetry-lattice
node 0..239 read through `ring`, `antipode`, `canonicalRay`, `cycle:<steps>`,
`reflect:<node|cell:<row>[.<key>]>`, `orthogonal:<node|cell:…>` (1/0),
`innerProduct:<node|cell:…>` (the two roots' exact pairing, −2..2; 1 marks one
of a node's 56 neighbours at sixty degrees, so adjacency in the 240-node graph
is one comparison), or
`projectionX`/`projectionY`; the row is the last token and the operand's `key`
addresses the cell as usual; a cell holding no node reads −1, or 0 for
`orthogonal`, `innerProduct` and the projections — the door through which a
rule reaches the lattice's whole symmetry group; `world.symmetry <node> [other]`
reads the same maps back),
`$argmax:<row>`/`$argmin:<row>` (the active body naming a keyed row's extremal
cell — a 0-based entity index, or -1 for none; these also accept
`:where:<filterRow>`),
`$distance:<bodyRefA>:<bodyRefB>`/`$los:<bodyRefA>:<bodyRefB>` (live distance, or
1/0 line-of-sight, between two bodies named `body:<n>` or
`argmax:<row>`/`argmin:<row>`), `$parked:<bodyRef>` (one named body's
remaining reconnect-grace ticks, 0 when not parked or the reference resolves to
no live body — the SAME single-body-reference grammar as an argmax/argmin
token, so it composes with `$distance:`/`$argmax:` directly; see
`Server.WorldPopulation`'s park-with-grace remarks), and
`$link:<adjacencyName>` (simulation ticks since that `adjacencies` row last
received a delivered neighbour refresh, 0 when fresh and 0 forever when the row
authors no `livenessGraceSeconds`; the row name is proven at compile time, and
this is a federation seam — unrelated to machine cable linking, which is the
`Machine` source's own `cable` port), and `$channel:<seat>:<channelName>` (the
1-based LOCAL seat's own folded value for a declared `channels[]` row — the
exact per-tick value settled from that seat's drained `CommandSnapshot`,
riding the channel's own native fixed-point domain unchanged, so `1` already
means "fully pressed/1.0" with no rescale; 0 for a seat outside
`bodies.localSeats` or one no local seat currently occupies) —
folding time, population, occupancy, machine memory, aggregates,
reconnect-park state, federation liveness, and a local seat's own channel
value into the string channel `State` already carries rather than a fact enum
or a scheduler. `Mode` is `Level`
(fires every tick the gate holds) or `Edge` (fires once per crossing, re-arming
when the gate closes); a rule that writes a row almost always wants `Edge`.

`compareState`'s comparand is EITHER an authored `Value` OR a second
`(ComparandState, ComparandKey)` pair resolved through the SAME operand walk as
`(State, Key)` — including the reserved channels — never both, never neither, and
the two sides must resolve to the same cell kind (`int`/`fixed`/`bool`; a
mismatch refuses by name). One shared resolver (`ResolveOperand` at compile time,
`ReadWorldFact` at evaluation) reads both sides, so a name can never mean two
things across a comparison. This is the whole periodicity/cooldown/round-boundary
vocabulary — composition, not a new mechanism:

- **Every N ticks**: gate `$tick >= nextBeat` against an `int` schedule row the
  rule's own effect advances by N on fire (`addState nextBeat += N`), mode `Edge`.
  For N>=2 the advance lands synchronously and self-closes the gate the next tick;
  a period of exactly 1 wants `Level` (tick and schedule move in lockstep, so the
  gate never re-closes).
- **Cooldown**: `scheduleState` writes the current simulation tick plus a
  non-negative delay into an `int` row. Its seconds-to-ticks conversion uses the
  world's simulation rate and rounds up, so the deadline never opens early. Gate
  the ability on `$tick >= cooldownDue`; a companion effect can remove the keyed
  deadline after handling it. A relative countdown remains useful when pausing
  or explicitly spending engine-step time is the desired rule.
- **Round boundary**: compare a `round` row against a DECLARED `roundLength` row
  (both same kind) — the cross-row spelling, exercising the kind match.

`setState`/`addState` accept exactly one source: a literal `Value`, an exact
engine-tick `ValueSeconds`, a live copy `(FromState, FromKey)`, or a numeric
`Expression`. A copy or expression operand is read fresh every firing through
the same `ResolveOperand`/`ReadWorldFact` path the comparand uses, and every
operand must match the destination's `int` or `fixed` kind. Expressions are
postfix token lists with a 64-token ceiling; they provide constants, state or
reserved-channel reads, `add`, `subtract`, `multiply`, `divide`, `modulo`
(remainder toward zero; in `fixed` the raw remainder, so `2.5 modulo 1` is
`0.5`), `min`, `max`, `clamp`, the comparisons `equal`/`notEqual`/`less`/
`lessOrEqual`/`greater`/`greaterOrEqual` (two same-kind values in, `int` 1 or 0
out), `select` (condition, whenTrue, whenFalse — an `int` condition picks
between two same-kind branches, the inline conditional), and, in `int`
expressions only, `bitAnd`/`bitOr`/`bitXor`/`bitNot`/`shiftLeft`/`shiftRight`
(arithmetic)/`shiftRightLogical`/`rotateLeft`/`rotateRight` with counts 0..63,
the bit census `popCount`/`leadingZeroCount`/`trailingZeroCount` (64 for zero;
`63 - leadingZeroCount` is the integer log2, `trailingZeroCount` the lowest
occupied square), the piece walk `lowestSetBit`/`clearLowestSetBit`, and the
8x8 board symmetries `byteSwap` (rank mirror) and `bitReverse` (half turn);
`parallelBitExtract`/`parallelBitDeposit` (pext/pdep: occupancy along a set
of squares as a dense index and back); `bitField` (value, offset, width) and
`bitInsert` (value, field, offset, width) for packed fields, refusing a
field that leaves the 64-bit carrier; and `boardShift` (`topology`,
`direction`), which moves every set bit of a cell mask to its neighbour in
that direction and drops a bit at the edge instead of wrapping, so
`$board:mask` of one side shifted and masked against `$board:mask` of the
other is an attack map in one expression.
`negate`/`abs` keep their operand's kind and refuse the carrier's minimum;
`sign` reads either kind and pushes `int` -1/0/1. The compiler proves
the stack's shape AND each slot's kind, so a comparison's `int` result may feed
a `select` inside a `fixed` expression but never an arithmetic operator of the
wrong kind. Runtime overflow, division by zero, a shift count outside 0..63,
or an inverted clamp range refuses the effect instead of producing a wrapped
or undefined result. This is
what closes the round-reset gap the comparand alone cannot: a rule REACTING to a
counter someone else advances (`compareState round != roundReflect`) can reset a
whole set of other rows to authored literals AND resync `roundReflect` to
`round`'s CURRENT value in the same firing (`setState roundReflect
fromState=round`) — a standing `addState roundReflect += 1` only tracks a
disciplined `+1` counter and desyncs silently (gate stuck open, no refusal) the
moment the counter advances by anything else or is set outright. When the rule
that ADVANCES the round is itself authored as a rule, the resets can just be more
effects in that SAME rule instead — a rule's `Effects` list is not limited to one
row write; the copy operand is for the decoupled case, where the resetting rule
is not the thing that changed the counter.

A literal and a copy are both exact write spellings. `Value` is a JSON decimal
number carried as `decimal`, so an integer such as 16777217 reaches compilation
unchanged; `fixed` literals lower through the invariant decimal parser rather
than binary floating point. The copy path likewise moves the source cell's bits
unchanged — an exact shift for `int`/`bool`, verbatim raw bits for `fixed`, no
float anywhere. An out-of-carrier literal refuses by the rule's name. An
`int` cell spans the whole signed 64-bit carrier and is compared, copied, and
computed as a raw `long` (a bitboard's bit 63 is an ordinary value); the few
readers that need a continuous quantity from an `int` cell (a symmetry node, a
dynamics target, a body-reference key) lift it through
`WorldStateReader.LiftSaturating`, clamping at `FixedQ4816`'s integer band
rather than faulting.

Ordering is declaration order, on both sides: a later rule's copy operand reads
an earlier rule's same-tick write exactly as a later gate does. Within one rule,
each contiguous run of state effects is preflighted against one private
candidate; either the whole run installs in order or none of it does, so a later
range/capacity refusal cannot leave the earlier writes partially applied.

An explicit `transaction` makes that boundary visible in the document and adds
an optional `onFailure` branch. Both branches accept at most 64 steps and may
combine state cells, draw generation, HUD/placement mutations, poses, cues, body
effects, and field paints. The whole branch is preflighted against each preceding
candidate before anything escapes; a refusal runs `onFailure` with no leaked cue,
impulse, paint, or document write. Placement steps form the final suffix because
they may rebuild the active population. Nested transactions and `save` remain
structurally unavailable: persistence I/O cannot be rolled back.
`removeStateCell` lets the same transaction retire keyed membership cleanly.

`emitCue` publishes a stable dotted token of at most 64 ASCII characters, an
optional payload of at most 256 UTF-16 code units, an optional body association,
and the simulation tick. An `audio.cues` row may bind either a published engine
event or a token emitted by one of the same document's rules. The desktop
composition root forwards the name and body position to that table; other
consumers may observe the presentation-neutral token.
Body effects accept literal or dynamically bound body keys and use the same
fixed-point instruction path as authored body actions. `paintField` clips a
sphere to the lattice, clamps every result to the field envelope, and caps its
radius at eight cells, bounding one firing to at most 4,913 candidate visits.

Rule execution has three independent ceilings: 128 ordinary rules, 64 top-level
effects per rule/interaction, and 1,000,000 statically derived work units per
tick. The cost includes gate/expression operands, keyed scans, `forEach`, the
quadratic worst case of distance interactions, mutation rebuild weights, nested
transaction preflight, field-paint candidate visits, and flock-affinity expressions
for every body's worst-case simultaneous initial sample. Validation refuses a
document above the aggregate ceiling; `world.budget` prints the current rule,
interaction, evaluation-slot, and work-unit totals. Interaction `range` is an
exact JSON decimal lowered directly to fixed point, with no binary32 round trip.

Live effect failures are bounded diagnostics, not a Level-rule log flood.
`world.rule.failures` reports one fixed counter per refusal category plus its
latest tick, rule, effect, and concrete reason; stderr narrates only the first
occurrence of each category.

The high-frequency `UpsertStateCell` path validates only the addressed mutable
cell and installs only value-derived state. Declaration-shape mutations retain
whole-document validation and rebuild the compiled rule/input/field surfaces;
a value-only rule, countdown, field scalar, or console write does not pay those
unrelated rebuild costs.

See `WorldRule`'s own remarks in `WorldRules.cs` for the edge-with-a-moving-
threshold reasoning in full.

The section is optional deliberately: a new REQUIRED section would refuse every
existing document at boot for declaring nothing. Authoring a rule is an ordinary
mutation (`UpsertWorldRule`/`RemoveWorldRule`, `Mutate`/`section:rules`); a
rule's own EFFECTS act as `WorldPrincipal.World`, which the server's admission
predicate exempts STRUCTURALLY — the same standing a per-body `ActionEffect`
always had.

### Social flock affinities

A flock profile can prefer particular neighbors without assigning one universal
friendship score. `cohesionAffinity` weights neighbors in the attraction centroid;
`alignmentAffinity` independently weights their headings. For example, add these
optional fields to a kit producer's `flock` profile to stay near affectionate
companions while following demonstrated navigation competence:

```json
{
  "cohesionAffinity": {
    "tokens": [{ "$type": "social", "query": {
      "relationship": {
        "observer": { "body": "left" }, "subject": { "body": "right" },
        "dimension": "affection"
      }, "facet": "Value"
    }}]
  },
  "alignmentAffinity": {
    "tokens": [{ "$type": "social", "query": {
      "relationship": {
        "observer": { "body": "left" }, "subject": { "body": "right" },
        "dimension": "navigation-competence"
      }, "facet": "Value"
    }}]
  }
}
```

Declare those dimensions in `state.social`; the names have no engine-defined
meaning. Expressions may combine Fixed social facets, Fixed state cells and
state-backed reductions/symmetry with ordinary postfix arithmetic. `left` is
the observer and `right` the sampled neighbor; state keys use `$left`/`$right`.
`each` is unavailable. Movement-dependent world facts are refused so every body
reads the same state/social image during a movement pass.

Absent expressions give uniform weight one. Each result clamps to [0,1], with
arithmetic failure reading zero. Negative affection therefore means no cohesion
in this example, not repulsion. Authors can use arithmetic to remap a dimension,
or multiply by `Confidence` when unknown individuals should have no influence.
The expressions refresh with `updateSeconds` and keep the ordinary perception
limits: they neither grant omniscient sensing nor enlarge the inspected sample.
Zero affinity does not disable separation. These are relative mean weights;
the profile's outer `cohesion` and `alignment` still set each term's strength.
`world.flock` echoes the expressions and counters, and `world.budget` includes
their conservative cost in the shared 1,000,000-unit admission ceiling.
Runtime sampling and checkpoint semantics live in the
[server reference](../Puck.World.Server/README.md#local-flock-steering).

### Crowd scale policies

The engine admits at most 4096 bodies. A kit's optional `autonomy` row gives
locally simulated non-human bodies independent `motionSeconds` and
`steeringSeconds` cadences (0..1 seconds; zero means every authority tick).
Bodies are deterministically phased and integrate the complete elapsed
engine-tick duration when due. Human bodies, live sources, tapes, and pending
external input remain full-rate. `bodyContact: solid` requires full-rate motion;
large crowds that batch motion use the default `overlap` contact mode.

### Body scale

`bodies.scaleRow` names a keyed `state.world` row (`kind: fixed`, a declared
`capacity`, and both `min`/`max` authored, `min` strictly positive and `max`
no greater than `authoring.maxPlacementScale` — the row's own envelope is the
world's own declared scale envelope, and the render capacity probe's worst
case must cover the largest live body — refused by name otherwise) whose
cells, keyed by 0-based body index, carry each body's live scale multiplier:
absent (the default) leaves every body's `Server.WorldBody.Scale` at `1`
forever, at no per-tick cost. Every cell key must parse as an integer inside
`0..bodies.capacity-1` — the SAME parse `WorldPopulation.SyncBodyScale` runs at
every resync, which silently skips a key that fails it; refusing the mismatch
at validation instead of leaving an authored cell nothing ever reads. A cell
may not declare an `advance`/`cycle`/`dynamics` value-over-time trait — every
resync reads at tick 0, so such a cell would read its base value forever
instead of progressing. A `Region`
INTERACTION scoped to the one affected body (`left` a one-cell carrier
property naming it, `right` the placement) is the trigger shape for "this
specific body standing in this region": the `$region:<placementId>` reserved
channel is an AGGREGATE occupant count over the whole population and cannot
express "this body, not any other" — an ordinary world rule gated on it
shrinks whichever body happens to wander in, and restoring on "the aggregate
reads zero" stays blocked while an unrelated body lingers in the same region.
The interaction's own `Edge` mode (not `Level`) is what keeps the trigger a
single write per crossing rather than a per-tick one: `Level` re-fires the
effect every tick the co-occurrence holds, which for a `setState` effect is a
document mutation, a stderr journal line, and a client definition delivery
EVERY tick a body simply stands in the region. A shrink/restore pair is
therefore two `Edge` interactions over two regions — one that lowers `Scale`
on entering the shrinking region, a second, physically separate region that
restores it on entering THAT one — never a single region's `Level` write
paired with a self-resetting flag row, which is the same per-tick-write
anti-pattern `ActionTriggerMode.Edge`'s own remarks warn against.
`WorldPopulation.SyncBodyScale` resyncs every active body's `Scale` wholesale
from the row at the same `Install`/admission choke points `WorldGrants.SyncState`
resyncs its own drive gates at, PLUS `RestoreCheckpoint` (after
`WorldPopulation.Restore` rebuilds every body at the constructed default) and
a detached-seat/peer transfer restore, so a live write settles before the next
tick and a reused population slot — or a body a checkpoint restore or transfer
just minted fresh — never inherits a previous occupant's value nor sits at the
unscaled default the row itself disagrees with. `Scale` multiplies the body's
collider volumes (about its own root — contact resolution and hold probes/
standoff/reach alike), its resolved move speed and turn rate, a hold's own
gravity fall/rise/terminal magnitudes, a wall hold's travel speed, and a
grip's pull rate (`WorldBody.Hold.cs`) — so a shrunk body settles onto and
depenetrates from the ground at a proportionally gentler rate too, rather than
free-falling one tick of full-scale gravity into a collider whose own skin
margin it can no longer absorb — and, client-side, reading the same row live,
its composed render scale (`Client.WorldSceneEmitter.ResolveStampCreation`)
and the seat chase camera's orbit distance and look-at height
(`Client.WorldFramePresenter.ResolveCamera`, scaled about the body's own root
so a shrunk body stays framed instead of shrinking to a speck on screen).
`body.where`'s `scale=` echo is the read-back. `WorldLook.Scale` is a
different, presentation-only per-look constant layered on top (appearance
only; it never touches collision or motion tuning) — the two multiply
together, never one standing in for the other.

Body-vs-body contact resolution (`WorldPopulation.ResolveDynamicContacts`),
overlap/collision events (`WorldEventFeed`), the cross-boundary continuum
trajectory (`WorldBody.ApplyContinuumTrajectory`), the adjacency sweep's LOCAL
side (`WorldAdjacencyContactField`), and the self-collision sweep
(`WorldBody.Step.cs`'s `ResolveProgramContacts`) all read each body's own
live-scaled collider volumes now (`WorldBody.ScaledColliderVolumes`), so a
shrunk body's contact with another body agrees with its contact with the
world. Only the adjacency sweep's REMOTE side (a neighbour authority's own
entities) still reads an unscaled collider — no delivered snapshot carries a
remote entity's live Scale on the wire yet.

A rigid facet (`Rigid` below) scales with the body too: mass ∝ `Scale`³
against the authored mass at scale 1 (so a bigger body of the same material is
heavier by its volume ratio), inertia ∝ `Scale`⁵, and the derived bounding
radius/centre of mass ∝ `Scale` — `Server.WorldBody.ScaleRigid` derives this
once per read from the compiled facet rather than re-deriving mass properties
from the collider every tick. Restitution, friction, rolling friction, and
both damping rates are dimensionless coefficients, unaffected by `Scale`. The
grounded rest-hold window's LINEAR speed threshold scales with the body (a
spatial rate, like a hold's own travel speed); its ANGULAR threshold does not
(a rotational rate carries no length dimension). `body.where` echoes a rigid
body's centre of mass as `com=` — see `Rigid` below.

`collision.events` bounds overlap-event sensing without changing physical world
contact. `candidateBudget` limits inspected broadphase candidates per body,
`maxPairsPerBody` limits retained degree, and `beginBudget` limits new pairs per
tick. Established relationships are considered first. Setting
`maxPairsPerBody` to zero disables body-pair begin/end events.

`collision.bodyContacts` independently bounds physical depenetration between
two `solid` kits. Its `candidateBudget` caps inspected x-overlapping sweep
pairs per body (default 16, maximum 32); `maxPairsPerBody` caps corrections
incident to one body per tick (default 8, maximum 16). Saturation omits later
stable-index pairs, so even a fully coincident 4096-body stadium has linear,
authored work rather than an accidental all-pairs frame. Its
`rigidSubstepCeiling` (default 8, maximum 32) bounds a rigid body's own
per-tick continuous-collision substep count — the actual count is DERIVED
per body per tick from speed and collider size against `rigidSubstepTravelFraction`
(default 0.5, strictly positive — the fraction of the collider's own
bounding radius one substep may travel) floored by `rigidSubstepMinimumTravel`
(default 0.001 world units, strictly positive), never authored directly; the
ceiling only bounds worst-case cost. `rigidRestLinearSpeed`/`rigidRestAngularSpeed`
(default 0.05/0.1, non-negative) and `rigidRestHoldSeconds` (default 0.25,
non-negative) are the thresholds and hold window that decide when a grounded
rigid body's `Resting` fact latches — `rigidRestLinearSpeed` scales with each
body's own live `Scale` (see "Body scale" above); `rigidRestAngularSpeed` does
not. `rigidPairRestitutionSpeed` (default
0.05, non-negative) is the closing-speed floor below which a rigid-vs-rigid
contact restitutes at zero rather than the authored coefficient — a rigid
pair carries no rising-edge latch, so without this floor two touching bodies
would restitute a hair apart every tick they are found overlapping and never
fully settle. See the
[server reference](../Puck.World.Server/README.md#rigid-dynamics-worldbodyrigidcs-worldpopulationrigidcs).

### Rigid dynamics (`WorldRigid.cs`)

A kit's optional `rigid` facet (`WorldRigid`: `mass`, `restitution`,
`friction`, `rollingFriction`, `linearDamping`, `angularDamping`) hands its
bodies to the rigid solver instead of a locomotion motion program — a passive
physical entity such as a billiard ball, a bowling pin, or a chess piece.
`mass` is required and strictly positive; `restitution` lies in `[0, 1]`;
`friction` is a Coulomb coefficient at a contact point (non-negative, not
bounded by 1) — the SAME meaning against the static world and, as the pair's
average, against another rigid body; `rollingFriction`/`linearDamping`/`angularDamping`
are non-negative per-second decay RATES (applied as `1 - rate·dt`, never a
flat per-tick fraction, so the same authored value decays identically at any
simulation rate). A rigid kit REQUIRES `collider` (sphere, capsule, or box — never
`fromCreation`, whose compound shape has no single closed-form inertia) and
`bodyContact: solid` (a rigid body that never depenetrates is inert). Mass and
inertia derive from the collider's own shape and the authored mass through
`Puck.Maths.FixedMassProperties` — density and the inertia tensor are never
authored directly, matching the engine's derived-limits convention. The
validator also proves the authored coefficients and the collider-derived
room-scale mass/inertia fit the engine's fixed-point placements; values that
would quantize to zero, saturate, or overflow are refused before world boot.
See the
[server reference](../Puck.World.Server/README.md#rigid-dynamics-worldbodyrigidcs-worldpopulationrigidcs)
for integration, contact, and checkpoint/hash coverage.

A kit's optional `carry` facet (`WorldCarry.cs`: `offset`, `massEquivalent`,
`maxCarryFraction` default 1, `maxReach` default 1.5) is a distinct facet
from `rigid` — presence lets a body pick up another one via
`body.carry`/`body.release`. `offset` is the full-scale carry point in the
carrier's own body-local axes and scales with the carrier's live `Scale`;
`massEquivalent` stands in for a locomotion kit's own
inertial mass, which `WorldKit.Mass` (gravitational) does not carry;
`maxCarryFraction` scales `massEquivalent` into the carry-mass ceiling
`WorldBody.TryBeginCarry` compares a candidate's own live-scaled `RigidMass`
against; `maxReach` bounds the carrier-to-target distance the same call
admits. All four must fit the engine's fixed-point representation (including
the derived mass product); `massEquivalent`/`maxReach` are strictly positive
after fixed-point compilation, and `maxCarryFraction` is non-negative. See the
[server reference](../Puck.World.Server/README.md#carry-as-attachment-worldbodycarrycs-worldpopulationcarrycs)
for the attachment mechanics.

### Tether (`WorldTether.cs`)

A kit's optional `tether` facet (`WorldTether` → `FixedWorldTether`) is a
further distinct facet from `rigid`/`carry`, presence-is-the-switch on the
same terms: it admits the kit's bodies to `body.attach`/`body.detach`/
`body.reel`, an aimed distance-cap rope a body throws along its own facing
and reels (`Puck.Physics.FixedSurfaceQuery.TryNearestSurfaceAlongDirection`,
`Puck.Physics.FixedTetherConstraint`). Absent, those three channels refuse by
name for every body wearing the kit. `maxAnchorDistance` (non-negative) is
the aim ceiling — also the tether's rope length at attach, clamped to the
resolved anchor's actual distance; `aimHalfAngleDegrees` (`[0, 180]`) is the
aim-assist cone half-angle; `lengthRate` (non-negative) is the held reel
channel's world-units-per-second rate; `minLength` (non-negative) is the
reel-in floor; `releaseVelocityScale` (non-negative, default 1) scales the
body's velocity at the instant of detach. `attachChannel`/`detachChannel`/
`reelChannel` each name a declared composition channel (validated when
authored; a null lane is simply unreachable). `modeState` optionally names a
declared `state.body`/`state.identity` Counter slot the facet writes `1`
while attached and `0` otherwise — resolved to an ordinal at kit compile
time (never a runtime name scan), so the camera program's `select` op can key
off it like any other `state.<row>` value. A body's attach state is
`m_tether is not null` (`WorldBody.Tether.cs`) — there is no separate mode
enum. Read back with `body.tether` (per body: `[body.tether: body:<n>
attached=<yes|no> anchor=(x, y, z) rope=<length|n/a>]`) and per kit with
`world.kits` (`tether=none` for a kit that carries no rope). Surface holds
are not authored here — they are a kit's own `motion.holds` list.

## The `probes` section — probe and binding rows

`WorldProbesSection` (`WorldProbes.cs`) declares two lists: `probes`
(`WorldProbe` — an id, a registered kind, an input arm of
`WorldProbeInput` (`camera { sensor }` or a recorded `track { path }`), a
rate ceiling in Hz, and an opaque `config`) and `bindings` (`WorldProbeBinding`
— `axis`, `parameter`, or `control`, each naming a declared probe and
channel). A kind is checked against the registered vocabulary at load
(`WorldProbeVocabularyHook.IsRegisteredProbeKind`, a required hook installed
the same way `WorldExtensionVocabularyHook`'s post-render check is); a channel
name is not — the manifest behind that hook is not reachable here, so a
binding's `channel` is checked only for presence, and by name once a kind's own
manifest is consulted at boot. An `axis` binding's `source` mints the bindable
input source `probe.<source>` (`Puck.Input.InputSources.Probe.Axis`); a
`parameter` binding's `target` must name an entry the document's own
`render.extensions` composes; a `control` binding's `control` field must name a
`WorldCameraControls` member. Like `judges`/`music`, the section is
boot-authored only — no `WorldMutation` kind targets it and `world.row.set
probes` refuses by name enumerating siblings — though it does carry its own
`WorldSection.Probes` grant subject for a section-scoped hold.

## The `dynamics` section — the second-order personality table

`WorldDynamicsRow` (`WorldDynamics.cs`): named `{name, f, zeta, r}` rows — the
t3ssel8r-style pole-matched second-order response every follower consumer
(a look's root/part followers, a camera boom, a grounded kit's planar
shaping, a `state` cell's eased read) names by `name` rather than authoring
inline. `f` (Hz, positive), `zeta` (damping ratio, non-negative), and `r`
(initial response) are validated against `WorldDynamics`' ceilings
(`MaxFrequencyHz`, `MaxDamping`, `MinResponse`/`MaxResponse`). The section is
optional and every reference is nullable, so an unauthored world is
unchanged; every reference resolves through `WorldDefinitionRows.FindDynamics`
and refuses a dangling name, and removing a still-referenced row is refused
naming the referrer. `WorldDynamicsRow.Compiled` caches the row's
`Puck.Maths.SecondOrderDynamics` derivation per row instance
(`ConditionalWeakTable`) for the HUD's per-frame eased read; every other
consumer compiles its own follower from the same authored triple. Authored
with `world.row.set dynamics <row-json>` / `world.row.remove dynamics <name>`;
read back with `world.dynamics` (`Puck.World.Console`), which reports the
derived decay/oscillation/k3 constants through the identical fixed-point
derivation the simulation uses, plus a live reference count.

## The `curves` section — the curvature-first spline table

`WorldCurveRow` (`WorldCurves.cs`): named `{name, closed, knots}` rows. An
author declares intent per knot (position, tangent direction, signed
curvature); `WorldCurveRow.Compiled` derives the machinery — the cubic-Bézier
tangent lengths that reproduce it exactly, via `Puck.Maths.CurvatureSpline`
(Steven Wittens' curvature-continuous construction) — so no control-point
document shape ever ships. Knot `tangentYaw`/`curvature` are validated against
`CurvatureSpline`'s own ceilings (`MaxCurvature`, `MaxCoordinate`), the ONE
source both the document validator and the compiled primitive read; row/knot
counts are validated against `WorldCurves`' own (`MaxKnots`, `MaxRows`). The
section is optional and every reference is nullable, so an unauthored world is
unchanged; every reference resolves through `WorldDefinitionRows.FindCurve`
and refuses a dangling name, and removing a still-referenced row is refused
naming the referrer. `WorldCurveRow.Compiled` caches the row's
`Puck.Maths.CurvatureSpline.Compile` derivation per row instance
(`ConditionalWeakTable`, the `WorldDynamicsRow.Compiled` precedent) — the SAME
derivation `WorldDefinitionValidator` runs at the door as its own last
compile-refusal gate, so a validated row always compiles again for free.
Authored with `world.row.set curves <row-json>` /
`world.row.remove curves <name>`; read back with `world.curves`
(`Puck.World.Console`), which reports every row's authored shape, its
compiled segment count and total arc length, and a live reference count split
by camera/follow consumer. Consumers: a camera program's `path` op (dollies
the eye/pivot along the curve by arc-length fraction, `Puck.World.Client`'s
`WorldCameraRigCompiler` + `Puck.SdfVm.Views.SdfCurvePath`, the float twin
converted once from the compiled Q32 raws — never a second solver) and a
body-motion program's `curve` target source
(`Puck.Physics.Motion.BodyTargetSource.CurveFollow`, `Puck.World.Server`'s
per-tick arc advance) — the seed the kart-track charter inherits.

## The `navigation` section — bounded routes over world truth

`navigation.domains` declares finite named grids consumed by a body-motion
program whose target source is `{ "$type": "navigated", "domain": "…",
"register": "…" }`. The register remains the authority-checked destination;
navigation only replaces the direct bearing with deterministic intermediate
waypoints. `surface` domains sample ground from the world's SDF, enforce step
and slope limits, and bake both vertical capsule clearance and swept clearance
between adjacent cells. `volume` domains are collision-free 3D grids for
airborne or otherwise unconstrained actors. `medium` domains are 3D grids that
also name a `state.world` lattice row carrying `lattice.medium`; their solid
edges are baked, while whole-agent node and edge containment are re-read from
the live field so draining or moving fluid invalidates a cached route. A
medium agent's diameter may not exceed its field lattice cell size, keeping
that conservative neighboring-voxel clearance proof bounded.

All coordinates and tuning quantize once to `FixedQ4816`. A stable A* order
breaks equal costs by estimated cost and then node ordinal. `connectivity`
chooses 6, 18, or 26 neighbours for volume/medium domains; diagonal edges
cannot cut a blocked axis corner. `maxExpandedNodes` and `maxPathNodes` are
hard per-search/per-route limits, under global ceilings of 16 domains, 65,536
cells per domain, 262,144 cells per world, and 1,024 retained waypoints per
body. Route arrays allocate lazily only for bodies that actually navigate.
Validation also refuses a producer that stops outside `arrivalDistance`, has
no forward drive, or lacks the vertical channel/gain/consumer required by a
volume or medium route.
`world.navigation` reports compiled clear cells and budgets; `body.targets`
reports route status/waypoint/search work; `$nav:<bodyRef>:<hasPath|active|
arrived|unreachable|remaining>` exposes the same status to rules. Navigation
is withheld from presentation projections with the other authoritative AI and
motion-program declarations.

## The egress documents — what leaves an authority

`WorldDisclosureTier` (`WorldAdmission.cs`) has three members:
`frames` (no document), `presentation`, `replica` (the world document verbatim,
hash-identical). An `admission` row authors it as `disclosure`; absent resolves
to `presentation` through `WorldAdmissionEntry.Tier`, and the door carries the
decision on `WorldAdmissionVerdict.Tier`. A `frames` row that mints grants
refuses by name — a peer with nothing to address them against.

`WorldProjection.Compose` is the one egress composer, answering `null` outside
`presentation` so a replica caller serializes the definition itself.
`WorldProjectionDocument` is not a `WorldDefinition` with holes: it is its own
record, and its member list IS the disclosure decision. It has no member for
`rules`, `grants`, `state`, `market`, `admission`, `generation`, `generators`,
`groups`, `properties`, `addons`, `storage`, `host`, `authoring`, `identity`,
`inputHold`, `targetRegisters`, `bodyMotionPrograms`, or `portals`, and its
`WorldProjectedKit` has none for a kit's `producers`/`actions`. `metadata`
crosses in reduced form — `WorldProjectedMetadata` carries `title`/
`description` only; `authors`, `tags`, and `custom` never cross.
`TryToDefinition` hydrates one back into a `WorldDefinition` with neutral defaults
for what was withheld, so no receiving consumer changed type. Because `state` is
one of the withheld sections, `Compose` sends a FLAT document — every
`state.<row>[.<key>]` value answered from the composing authority's own state and
the reference dropped — and `TryToDefinition` refuses a peer that still names a
cell.

`WorldCounterpartAttestation` is a neighbour's statement of its seam edges plus
the five `WorldOverlapTerms` the overlap derivation reads from its side.
`WorldCounterpartAttestationProtocol.TryVerify` verifies a signed claim against
the reading world's own `admission` keys and returns what it attests; it does
not yet bind the verified subject to the document the attestation names.
`WorldNeighbourResolutionKind.VerifiedAttested` is the arm a resolver may only
construct once it has made that binding itself — nothing in this repository
produces it today. `WorldNeighbourResolution.Attested` is how a resolver hands
one over without that verification (today's `WorldStorageNeighbourResolver`
composes this arm locally, unsigned). A derived
corner (`WorldDefinitionValidator.ValidateDerivedAdjacencyCorners`) names a
third authority, so it accepts only `Resolved` or `VerifiedAttested` — never a
plain `Attested` outcome, which proves an ordinary two-document adjacency only.

`WorldIdentityProjection` (`WorldIdentity.cs`) is what an identity discloses
when it walks into another authority: id, name, colour, and the two motion
rates. `WorldObserverDisclosure` (`bodies.disclosure`) is the per-observer
snapshot policy — the record lives here (document data); the evaluation over a
live `EntitySnapshot` (`WorldObserverDisclosureEvaluation.Discloses`) lives in
`Puck.World.Protocol`, since it operates on the wire snapshot shape. Its
`updateSeconds` member controls remote QUIC projection cadence only (default
0.03 s, zero for every authority tick); skipped field writes and continuity
hints are coalesced by the server sampler before delivery.

## Verifying a change here

There is no engine gate over this project. Verify by building
(`dotnet build Puck.slnx -c Release` — the architecture profile and XML-doc
diagnostics run there) and by RUNNING `Puck.World` and round-tripping the
affected document over stdin (`world.status`, `world.save`, `world.load`;
see [`Puck.World`'s README](../Puck.World/README.md) for the console). The
strict-parse contract (an unmapped nested member refuses by name; a root
reserved-prefix key survives) is proven in-process by
`tests/Puck.World.Tests/StrictParseLawTests.cs`. No committed battery covers
the HUD document — validate HUD
document changes by running the app; see
[the puck-world skill's hud reference](../../.claude/skills/puck-world/references/hud.md)
for the recipes.
