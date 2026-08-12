# Puck.World.Data — the document model and the wire protocol

This project holds the SHAPE of the world game: the two versioned JSON document
families that describe a world and a player, and the protocol vocabulary
(`Protocol/`) that every submission into — and every delivery out of — the
authoritative server travels as. It contains no rendering, no input handling,
and no server logic; it exists so the data the simulation runs on cannot
quietly grow a dependency on presentation. The runtime that consumes these
shapes is [`Puck.World.Server`](../Puck.World.Server/README.md); the process
that composes both is [`Puck.World`](../Puck.World/README.md).

## The dependency firewall

`Puck.World.Data` references only `Puck.Abstractions`, `Puck.Commands`,
`Puck.Forge`, and `Puck.Maths` (see
`Puck.World.Data.csproj`). An architecture lane profile in
`build/Architecture.props` enforces the absences that matter: no GPU backend,
no presentation project, no `Puck.Overlays`, no `Puck.Input`, and no
`Puck.World.Server`. Adding a forbidden reference fails the build with a
`PUCKARCH` diagnostic naming the arrival path.

Two validation paths genuinely need input-vocabulary knowledge this project is
denied (composing a candidate binding overlay against the engine default, and
linting it against the live command/channel vocabulary). They cross through
`BindingVocabularyHook.cs` — a static injection point the composition root
wires with a module initializer (`WorldDataHookInstaller` in `Puck.World`)
before `Main` runs, so every validation path is covered without this project
ever naming `Puck.Input`.

## `puck.world.def.v1` — the world definition

`WorldDefinition.cs` is one aggregate record with a section record per concern;
the section list is the `WorldSection` enum in `Protocol/WorldGrant.cs` (kits,
screens, cameras, spawns, motion, population, render, addons,
bindings, creations, placements, authoring, speakers, tunes, patches, audio,
collision, host, views, looks, links, grants, hud, state, input hold, rules,
groups, properties, interactions, player defaults, market). Worlds live as data
under `../Puck.World/Assets/worlds/` — ten checked-in documents. Four are the
four-world charter's whole game roster (2026-08-06): `play` (the overworld hub —
the game's first main city, and the shipped boot default; carries the
optional `references` section naming the other three by document path, and a
wall-mounted picture-frame portal placement per named world), `dive`
(underwater), `kart` (racing), `jump` (platformer). The fifth, `studio`, is a
non-game dev canvas for character work (neutral floor, no scenery or crowd,
four anchored camera eyes and a `sheet` layout) reached only with `--world`.
Five quilt documents (`quilt-nw`, `quilt-ne`, `quilt-se`, `quilt-sw`, and
`quilt-island`) are non-game adjacency/federation stress content. The movement platform
every grounded kit rides is documented on its kit's `WorldMotionModel.Grounded`
row (`SprintMultiplier`/`SprintChannel`, `MoveFrame`/`FacingSnap`) and
`WorldCameraRig.SmoothRate`. The motion-model union's second arm is
`WorldMotionModel.Vehicle` — anisotropic body-frame drive (longitudinal
accel/brake/coast, lateral grip/drift, speed-scaled steering, optional pitched
flight) read by the `ResolveVehicleFrame`/`ShapeVehicleVelocity` operations;
`kart.world.json` is its worked example. The retired `arcade` world's
`gaming-brick`-cabinet + region-gated prompt/prize + `rules`-driven `state`
reaction ladder (originally a document-mounted addon, ported to a world rule
before the world itself was retired) survives only in git history; no shipped world exercises the `rules` section
today. The loader resolves an explicit `--world` file or the shipped default
document and refuses a missing, unreadable, or invalid file by name;
`WorldDefinitionLoader.cs` in `Puck.World` owns that boundary.

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
`Solid` compiles the same creation-shape transform chain the renderer emits.
The field provider evaluates that SDF geometry directly; the analytic provider
materializes exact isotropically scaled spheres and conservative world-axis
bounds for every other finite primitive copy, capped document-wide by
`WorldPlacementPolicy.MaxSolidPlacementColliders`.
`WorldAddonRow` similarly gained `MemoryWatches` (`WorldAddonMemoryWatch`
rows: `Screen`/`Address`/`Length`) — the machine-memory-watch event family's
addon-scoped declaration.

**The validator is the one thick gate.** `WorldDefinitionValidator.cs` runs
over the entire composed candidate document — at boot, on every live mutation,
and on a whole-document swap — so builders and mutation appliers never repeat
semantic checks. The add-a-field procedure (model, validator, serializer
registration, verify by running) is in
[`docs/agent-guide.md`](../../docs/agent-guide.md).

**Serialization** (`WorldDefinitionSerialization.cs`): `WorldJsonContext` is a
System.Text.Json source-generated context — camelCase member names, enums by
name through strict converters (an unknown or numeric enum token is a parse
error, not a silent default), `Vector3` as a three-element `[x, y, z]` array,
and `$type`-discriminated polymorphic hierarchies for screen sources, cameras,
scene rows, speakers, and anchors. Parsing is strict everywhere below the
document root: `UnmappedMemberHandling = Disallow` makes an unmapped member on
any nested row a hard load failure naming the member and the row type. Only the
root carries a `[JsonExtensionData]` `Extensions` bag, governed by
`Puck.Abstractions.Documents.DocumentExtensionsPolicy`: a reserved-prefix key
(`$` or `_`) round-trips untouched, and any other unknown root key is a hard
load failure.

**Identity conventions.** Every row is addressed by a stable string id, with
two exceptions: screens are position-addressed by index, and grant rows are
keyed by their `(principal, capability, subject)` triple, because a grant IS
that triple. `GrantSubject` and `WorldPrincipal` serialize as the same compact
tokens the console grammar uses (`body:1`, `addon:default`) through their own
JSON converters.

**`Qr/` — the QR encoder a document field needs to validate.** A
`WorldScreenSource.Qr` row names a payload string; whether that payload FITS is
not arithmetic a validator can fake, so the whole ISO/IEC 18004 byte-mode
encoder lives here rather than beside its renderer: `QrReedSolomon` (the
standard's field and its prebuilt generator polynomials), `QrCapacityTable`
(versions 1..10 plus alignment centers), `QrBitWriter`, `QrEncoder` (auto
version, format/version BCH info), `QrMatrix` (placement, all eight masks scored
by the spec's four penalty rules, plus a nearest-neighbor B8G8R8A8 raster), and
`QrErrorCorrection` (the one spelling of the `L`/`M`/`Q`/`H` token). It is pure
integer math with no wall clock or RNG, so the validator refuses an
over-capacity payload BY NAME — its byte count against the level's capacity —
by asking the encoder the same question `screen.source <index> qr` asks it at
runtime.

**The coding half graduated; the QR half did not.** The owner ruled on
2026-08-05 that the field arithmetic and the Reed–Solomon coding belong in
`Puck.Maths`. `QrGaloisField` — a hand-rolled GF(256) log/exp table — was a
DUPLICATE of `BinaryField<T>`, which already shipped, and is deleted;
`QrReedSolomon`'s generator construction and synthetic division graduated to
`Puck.Maths.ReedSolomon`, generic over every carrier `BinaryField<T>` admits and
carrying no QR vocabulary. What stays here is what is genuinely the standard's:
the field it chose (`GF(256)` under `0x11D`, which is deliberately NOT the
catalog's `BinaryFields.Degree8` at `0x11B` — see `QrReedSolomon`'s remarks), the
generator element and root convention, the block-count table, the capacity and
alignment tables, the format/version BCH codes, masking, and matrix placement.
The graduated primitive answers to `tests/Puck.Maths.Tests`' `reed-solomon.*`
laws, one of which pins ISO/IEC 18004 Annex I's published remainder.

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
engine default, then any world `bindingOverlays` rows, then the seat's
owned identity world's `bindingOverlays`, then live session rebinds — merged by
`WorldBindingComposer.cs` with explicit keys (chord rows on the group plus
the ordered chord; a later layer's row for the same key overrides, wholesale
when the meaning differs and entry-by-source when both name the same page;
`contexts` rows — `{family, state, group}`, deriving a seat's active group
from published engine state — on the `(family, state)` pair, a later layer
overriding in place and new keys appending).
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
string. The `OverlayChannelLeases` hand-mirror precedent covers
`WorldHudCapacity`'s CONSTANTS and was deliberately not extended here — a
mirrored constant can be eyeballed for drift; a mirrored grammar cannot.

## The `water` section — the world's standing-water medium

`WorldWater.cs` holds the optional `water` section (`WorldWaterSection`): one
waterline `level` (world-space Y). Null IS the dry world (the same
optional-section rationale as `rules`), so no shipped world changed meaning
when the section landed; `world.status` echoes `water <level|none>`. The swim
motion model is the live consumer — its buoyancy/surface stage reads the
waterline compiled onto each body by the population — and a hover kit's
float-over height remains a later, not-yet-built consumer. Bounded water
VOLUMES are the destination shape and arrive as a future optional member
beside `level`, never a reshape of it. The section carries no mutation
dispatch axis and no grant subject — it is boot-authored data.

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

## Authored randomness: SOURCE x SITE x MOMENT

Everything random in a document is one primitive with three parts, and they are
deliberately separable: a **source** is a shape, a **site** is a place that
draws, and a **moment** is when.

**The SOURCE family** (`WorldGenerator`) is the document's whole randomness
vocabulary. `source` selects the shape: `markov` walks weighted alternatives per
context, each naming the context it moves INTO (that authored `next` is what
makes it a Markov process rather than independent draws — the context key IS the
process state) and is the only shape that writes TEXT and the only one that
DEALS; `uniformRange` draws one value over `[rangeMin, rangeMax]`;
`weightedNumeric` draws one value from an authored alias table; `streamDraw`
yields one raw 32-bit draw. Each shape reads a disjoint field set, and a foreign
field is refused BY NAME rather than parsed and ignored — including `bound` and
`mode`, which are non-nullable and so are refused against their declared
defaults. A markov emission is one walk from `start` to a TERMINAL context (one
declaring no alternatives), refusing BY NAME at `bound` rather than truncating;
`mode` is `withReplacement`, `withoutReplacement` (dealt out → refuse by name) or
`reshuffleOnExhaustion`. Caps live in `WorldGeneratorCapacity`.

**A source holds no position.** It may be declared once in the optional
`generators` section (`WorldGeneratorRow`: `name` + `generator`) and referenced
by any number of sites, or inlined at one site as sugar — the two spellings
compile to the identical record, so nothing is expressible one way and not the
other.

**The SITE facet** (`WorldDraw`) declares that a value is drawn. It carries
exactly one of `source` (naming a declared row) or `generator` (an inline
source), plus `timing`. Three sites exist today: a `WorldStateRow.Draw`,
`WorldPopulationDefaults.CapacityDraw`, and `WorldHostDefaults.BackendDraw`. The
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

**Two site classes, two settle rules.** A BOOT-ONLY site is a document field read
once at composition: `WorldDrawBootResolver` (in `Puck.World`) draws it, writes
the settled value into the ordinary literal field, CLEARS the facet, and NARRATES
the settlement on stderr — the narration is load-bearing, because settling erases
the only evidence the value was random. A STATE site keeps its facet and cursor,
fills ONLY while the row carries no cell yet (an authored `value` is therefore a
deliberate override), and resumes on reload rather than re-rolling a value the
player already saw. `host.backendDraw` draws its backend BY NAME from a weighted
TEXT source over the backend tokens, parsed through `WorldHostTokens.ParseBackend`
at settle — never an unnamed ordinal, which would silently re-point itself the
day an enum member is inserted. It is XOR-by-presence against `host.backend`
(`WorldHostDefaults` is a class, so presence is honestly observable);
`population.capacityDraw` cannot be, because `WorldPopulationDefaults` is a
STRUCT and an authored `capacity: 128` is indistinguishable from the C# default —
there the draw simply wins, a stated limitation rather than a silent guess.

**Draw domains are narrowed STATICALLY**, against the site's own admissible range
(a state row's `min`/`max`/`nonNegative`, the census coherence sum for
`population.capacity`, every reachable token for `host.backend`). Without that, a
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
becomes the tick the write applied at, `WorldServer.RebaseAdvanceEpoch`'s job,
run for both a live apply and `world.undo`'s per-entry replay (keyed off the
journal entry's own tick) so undo rewinds an advancing row exactly like it
rewinds a draw site's `drawCursor`. A declared `min`/`max`/`nonNegative` envelope
CLAMPS the computed value on every read without rewriting the stored base — the
read side of the envelope duality (a computed value clamps; an explicit write
refuses).

`WorldStateAdvance.ComputeCurrentValue` has exactly ONE caller,
`WorldStateReader.TryRead` above, and that is the whole design: a rule's
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
only (owner ruling, 2026-08-06).** `epochTick` is SESSION-relative (a tick
count from process start), so writing it verbatim to a saved file left a
reloaded document reading FROZEN at its stored base until the NEW session's
own tick counter climbed back past the OLD epoch. `Puck.World`'s
`WorldSessionCapture.Capture` (the `world.save` fold) now writes every
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
`WorldStateReader.Reduce`/`ArgExtremum` already resolve each candidate cell
through this identical seam rather than reading `WorldStateCell.Value` off
the row directly, a `$reduce:sum`/`$argmax:`/`$argmin:` rule operand over a
table of independently advancing cells sees every cell's LIVE value for
free — no special case in either method. A per-cell VALUE write
(`world.state.cell.set`, `UpsertStateCell`) carries no advance payload of its
own, so it PRESERVES whatever the cell already declared and re-bases its
epoch to the write's tick — `WorldServer.RebaseAdvanceEpoch`'s widened job,
run for a whole-row `UpsertStateRow` (which re-bases the row's own slot trait
AND every keyed cell's own trait, since it re-declares the whole row) and for
a per-cell `UpsertStateCell` (which re-bases only the ONE cell it names) —
both for a live apply and for `world.undo`'s per-entry replay, exactly as it
already did for the scalar case. `world.state`'s cell line echoes a cell's
own trait the same way the row line echoes the row's:
`advance=<num>/<den>@epoch<n>`. There is no WRAP/modulo mode — an open
question, not built.

**A keyed `text`-kind row IS the text-table primitive** — an authored, named
collection of strings (flavor lines, names, phrases) a HUD `Binding` or
`Template` reads FROM (`state.<row>.<key>`, see "The HUD document" above). A
DRAWN string is a different shape — a text-kind DRAW SITE is scalar, redrawn in
place by `world.generate <row>` (above), never emitted into another row's cell.
No separate schema exists for this — deliberately: a second "table of
strings" concept beside the row/cell substrate would repeat exactly the
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

**Reserved `$` names are ENGINE-MINTED ONLY.** A `$`-prefixed ROW name is refused
outright (nothing mints a row), and a `$`-prefixed CELL key is refused unless it
is exactly the key that row's shape mints (`$value` on a slot, and nothing
else). The rule lives in `WorldDefinitionValidator`, which
runs at boot, at every live mutation and on every undo-replay entry — so a
hand-authored file and a console verb refuse by the same code rather than by one
door the other walks around.

## The `rules` document — the per-body action primitive, one level up

`WorldRules.cs` holds the OPTIONAL `rules` section. A `WorldRule` is
`(Name, Gate, Effects, Mode)` over the SAME `ActionPredicate`, `ActionEffect` and
`ActionTriggerMode` types a kit's per-body actions use — there is no second
predicate or effect vocabulary, only a narrower admissible subset, refused BY
NAME by `WorldRuleCompiler` at the document/mutation boundary: `all` and
`compareState` gates; `setState`/`addState`/`generate` effects (each an ordinary
document write); `upsertHudPanel`/`removeHudPanel`/`upsertPlacement`/
`removePlacement` (each admitting an EXISTING `WorldMutation` kind into the rule
effect set, riding the same seam `generate` proved); and `save` (a session
snapshot to the world's own loaded file — the ONE effect with no `WorldMutation`
of its own: it composes no candidate and journals nothing, so it rides
`WorldServer.FireWorldRuleEffect` directly instead of `TryApplyMutation`, calling
out through `WorldServer.SaveEffectTap` to the composition root, which alone can
reach the render-lever/screen-binder/audio/pacing state the `world.save` fold
needs — see `ActionEffect.Save`'s own remarks). The rest read or write a body's
own state (`now`/`recently` read an engine fact, `timerElapsed` a per-body timer
slot, the velocity/impulse/designate effects a body's kinematics) and are refused
rather than reinterpreted.

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
`$reduce:<max|min|sum|count>:<row>` (an aggregate over a keyed row's cells),
`$argmax:<row>`/`$argmin:<row>` (the body naming a keyed row's extremal cell — a
0-based entity index, or -1 for none — the entity-addressable primitive),
`$distance:<bodyRefA>:<bodyRefB>`/`$los:<bodyRefA>:<bodyRefB>` (live distance, or
1/0 line-of-sight, between two bodies named `body:<n>` or
`argmax:<row>`/`argmin:<row>`), and `$parked:<bodyRef>` (one named body's
remaining reconnect-grace ticks, 0 when not parked or the reference resolves to
no live body — the SAME single-body-reference grammar as an argmax/argmin
token, so it composes with `$distance:`/`$argmax:` directly; see
`Server.WorldPopulation`'s park-with-grace remarks) —
folding time, population, occupancy, machine memory, aggregates, and
reconnect-park state into the string channel `State` already carries rather
than a fact enum or a scheduler. `Mode` is `Level`
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
- **Cooldown (NOT a `$tick` threshold)**: a `$tick`+N "next allowed" deadline is a
  FOOTGUN for a request-gated ability — once background ticks accrue, `$tick`
  already sits past any freshly set deadline and the gate never spends the
  cooldown. Use a relative COUNTDOWN: a `NonNegative` `int` `cooldownRemaining`, a
  `Level` rule gated `cooldownRemaining > 0` decrementing it each tick, the ability
  gated on `cooldownRemaining <= 0`, and use re-arms it (`setState … = N`). The
  decrement's `> 0` gate is load-bearing: `NonNegative` REFUSES a negative
  candidate (it does not silently clamp), so an ungated decrement would refuse
  loudly every tick at 0 — the gate floors it cleanly. A countdown is immune to
  absolute-tick drift by construction.
- **Round boundary**: compare a `round` row against a DECLARED `roundLength` row
  (both same kind) — the cross-row spelling, exercising the kind match.

`setState`/`addState` carry the SAME duality on the WRITE side: EITHER a literal
`Value` OR a live copy `(FromState, FromKey)` — another row or reserved channel,
read fresh every firing through the identical `ResolveOperand`/`ReadWorldFact`
path the comparand uses — never both, never neither, kinds must match. This is
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

A copy is also the only EXACT write spelling. `Value` is a `float`, so a literal
above 2^24 is already rounded before the compiler sees it (16777217 compiles to
16777216, and `world.rules` reads the rounded number back); the copy path moves
the source cell's bits unchanged — an exact shift for `int`/`bool`, verbatim raw
bits for `fixed`, no float anywhere (`WorldServer.ConvertWorldFactToRaw`). An
`int` cell is refused outside `WorldStateCapacity.MinIntCellValue`..
`MaxIntCellValue` (`FixedQ4816`'s own integer band) at the document validator,
because every engine read of one lifts it to fixed point.

Ordering is declaration order, on both sides: a later rule's copy operand reads
an earlier rule's same-tick write exactly as a later gate does.

See `WorldRule`'s own remarks in `WorldRules.cs` for the edge-with-a-moving-
threshold reasoning in full.

The section is optional deliberately: a new REQUIRED section would refuse every
existing document at boot for declaring nothing. Authoring a rule is an ordinary
mutation (`UpsertWorldRule`/`RemoveWorldRule`, `Mutate`/`section:rules`); a
rule's own EFFECTS act as `WorldPrincipal.World`, which the server's admission
predicate exempts STRUCTURALLY — the same standing a per-body `ActionEffect`
always had.

## `Protocol/` — the wire vocabulary

Two currencies cross the client/server boundary, and they are deliberately
distinct:

- **Intents.** `PlayerIntent.cs` is a fixed 16-slot vector of fixed-point
  channel values (`ChannelLimits.MaxChannels`). Ordinals 0–5 are the engine
  `ChannelRole`s (the movement axes); the rest are composition channels a
  world's channel table declares as data. There is no separate button bitmask:
  a button is a binary channel. Intents buffer per tick and drain at the
  server step; they are not envelope payloads.
- **Everything else.** Every non-intent submission — command, grant, revoke,
  session, definition swap, mutation, undo, composition, lever, query,
  addon-lifecycle (`world.addon.mount`/`.unmount`, P5), screen-op
  (`screen.insert`/`.eject`/`.select`/`.options`/`.link`/`.unlink`),
  designation (`player.designate`) —
  travels as one `SubmissionEnvelope` (`SubmissionEnvelope.cs`) carrying the
  closed `WorldSubmissionPayload` union (13 kinds) and the acting
  `WorldPrincipal`, and resolves to a typed `WorldSubmissionResult` through
  an inline completion callback. The server drains envelopes through one
  ordered domain in submission order; definition swaps, mutations, undo, and
  addon-lifecycle buffer to the tick boundary, every other kind — including
  screen-op — applies at submit. The ordering contract lives with
  `WorldServer` (see [`Puck.World.Server`](../Puck.World.Server/README.md)).

The named vocabularies: `WorldCommand.cs` (the closed drive-a-body command
hierarchy), `WorldMutation.cs` with `WorldMutationKindCatalog.cs` (every
mutation kind carries a declared ordinal, validated for uniqueness and range
at boot — never inferred from file order), `WorldGrant.cs` and
`WorldPrincipal.cs` (capabilities, subjects, principals, and their token
grammar — `TryParse` lives on the protocol types so the console and the JSON
converters share one parser; `GrantSubjectKind.Region`/`Seat` and
`WorldGrant.EventBudget` are the world-events feed's grant vocabulary, both
untrusted-principal-only), `ChannelPolicy.cs` (the reach and consent masks
the co-driving fold consumes), `SessionRequest.cs` (join/leave/profile/
population, with a `WireProtocolKey` checked against `WorldProtocol.WireProtocolKey` —
a mismatched client is rejected in the reply, never silently admitted),
`WorldSnapshot.cs` (the per-tick pose delivery — poses flow OUT only),
`WorldComposition.cs` and `WorldSessionLever.cs` (the composition and session-
lever deliveries), `WorldScreenOp.cs` (the screen-machine lifecycle vocabulary
the authoritative-machines campaign added — insert/eject/select/options/link/
unlink, each CAS-pinned where it names on-disk content), and
`WorldSubmissionResult.cs`.

`WorldSubmissionCodec.cs` is the single encoder/decoder owner for each of the
thirteen payload leaves. `WorldFrameCodec.cs` wraps a leaf as little-endian
`[u32 following-length][u8 kind][payload]` with a hard per-kind cap; malformed
caller state and bytes return a `WorldCodecRefusal` name. Loopback always
round-trips through that frame. The opaque `WorldProtocol.WireProtocolKey` is
checked by `WorldHelloDoor` and echoed by `Join`; it is deliberately unrelated
to replay-tape magic and guest-ABI pins. `WorldServerEvent.cs` declares the
server-authored peer admission/disconnect records; they are ordered events,
never client submission payloads.

`IServerLink.cs` and `IClientSink.cs` are the two sides of the link;
`LoopbackTransport.cs` is the in-process implementation (with the tap points
the replay tape records through), and `IWorldServerHost.cs` names exactly the
server surface the transport calls, so the transport never holds a concrete
`WorldServer`.

**The admission test for this surface.** A new game genre must be expressible
as different DATA through the existing messages, never as a new message kind.
If a proposed feature needs a genre-specific message, the surface is wrong:
generalize it or move the specificity into a document row.

## Verifying a change here

There is no engine gate over this project. Verify by building
(`dotnet build Puck.slnx -c Release` — the architecture profile and XML-doc
diagnostics run there) and by RUNNING `Puck.World` and round-tripping the
affected document over stdin (`world.status`, `world.save`, `world.load`;
see [`Puck.World`'s README](../Puck.World/README.md) for the console). One
committed runner exercises this project's load-bearing behavior end to
end: `docs/verification/strict-definition-parse/run.ps1` (the strict-parse
contract). `docs/verification/hud-document/` is QUARANTINED
(2026-08-06) and is not a gate — validate HUD document changes by running
the app; see that directory's README for the recipes.
