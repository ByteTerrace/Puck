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
`WorldGrant.cs`, `WorldPrincipal.cs`, `PlayerIntent.cs`, `ChannelPolicy.cs`,
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
the `quilt-base` template (see "Document composition" below). Every one of
them layers over `standard.world.json`, the standard library document. The movement platform
every grounded kit rides is documented on its kit's `WorldMotionModel.Grounded`
row (`SprintMultiplier`/`SprintChannel`, `MoveFrame`/`FacingSnap`), the
frame its MoveAdvance/MoveStrafe channel rows are authored in
(`channels[].frame`, `ChannelFrame`: `World` raw, `Camera` camera-relative and
facing its travel, `Heading` body-relative with `Turn` steering — the stick's
`player.move` is camera-framed by its own definition, so keyboard-in-heading
beside stick-in-camera is one document), and the seat rig's own `dynamics` op
(a named `dynamics` row shaping the boom ease). Beside `holds`, the arm carries one
more optional row: `drive` (`WorldDrive`) — anisotropic body-frame drive
(longitudinal accel/brake/coast, lateral grip and a held `drift`,
speed-scaled steering, optional pitched flight) read by the
`ResolveDriveFrame`/`ShapeDriveVelocity` operations. A kart is the one arm plus a
drive row exactly as a swimmer is the one arm plus a `Medium` hold row; a
program selecting either drive operation against a kit authoring no row refuses
by the `Drive` tuning facet's name. The retired `arcade` world's
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
`WorldPlacementPolicy.MaxStampRegistrations` alongside animated rows; its
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
rather than silently falling back to kit gravity. A placement may appear in only one source
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
answer rather than falling back to kit gravity. In an areas-only world, a body
outside every area does not participate and retains the kit fallback. The cap is
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
`withReplacement` it only scales the weight; a set's cards total at most 64,
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
facet — the draw facet's single home. `WorldDrawBootResolver` (in `Puck.World`)
fills a row's slot only while it carries no cell yet (first fill: process boot,
or a fresh `world.instance.start`), keeps the facet and cursor, and resumes on
reload rather than re-rolling a value the player already saw; nothing settles
into a bare literal and nothing is cleared. `bodies.capacityRow`/
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
`"group": "state.bindingGroups.defaultActionGroup"`, which is what
`standard.world.json` authors. Changing that cell
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
`(Name, Gate, Effects, Mode)` over the same `ActionPredicate`, `ActionEffect` and
`ActionTriggerMode` types a kit's per-body actions use. `WorldRuleCompiler`
checks the world-scope subset at the document or mutation boundary. Gates admit
`all`, `any`, `not`, and `compareState`; they compile to a bounded postfix
Boolean program, so nested logic does not allocate or recurse during a tick.

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
reserved-channel reads, add, subtract, multiply, divide, minimum, maximum, and
clamp. The compiler proves stack shape. Runtime overflow, division by zero, or
an inverted clamp range refuses the effect instead of producing a wrapped or
undefined result. This is
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
`int` cell is refused outside `WorldStateCapacity.MinIntCellValue`..
`MaxIntCellValue` (`FixedQ4816`'s own integer band) at the document validator,
because every engine read of one lifts it to fixed point.

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
transaction preflight, and field-paint candidate visits. Validation refuses a
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
`Puck.World.Protocol`, since it operates on the wire snapshot shape.

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
