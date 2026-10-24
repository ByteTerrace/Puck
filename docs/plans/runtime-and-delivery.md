# Runtime and delivery

A world is authored once and then has to reach a player. That path runs from
the content root a product declares, through the derivation that turns a
document into a running world, out to a deployed server that can be rolled
forward, rolled back, and restored without losing what the players earned.
Today each leg was designed on its own: content is welded into the engine,
every boot re-derives the same products from JSON, verification ROMs are
reached through nine unrelated mechanisms, and release identity has no single
packaged unit to name. This programme makes the whole path one unit, a product
tree that declares what it holds, carries its derived data beside its sources,
and is deployed, retained, and returned to as one thing. It also settles the
last leg of that path: what a client may observe of the document it draws. The
reasoning behind every decision is in
[the decisions register](../decisions/runtime-and-delivery.md).

## Implementation status

- **Canonical world sources:** `standard`, `pipeline` and `shards/quilt-nw-gap`
  are authored as `.puck`. Five migrations remain unstarted:
  `puck.world.json` and `shards/quilt-ne.world.json`, `quilt-nw.world.json`,
  `quilt-se.world.json` and `quilt-sw.world.json`, all under
  `experimental/src/Puck.World/Assets/worlds`. The JSON resolver prerequisite is implemented.
  Each migration must preserve the composed definition and its state baseline,
  replace source-facing references, and keep executable-relative references
  naming the generated `.world.json` output. The build ships those generated
  documents, not the authored sources.
- **Landed:** the baked solid query, `WorldSolidField`, `WorldOutputHub`
  narration, and `WorldDeadlineTable`; the `WorldServer` facade split and its
  constraint that every record the checkpoint codec reads sits at the top level
  of its namespace; the
  release manifest, group store,
  coordinator, CLI verbs, and the maintenance-and-recovery sequence (owned by
  [the server README](../../src/Puck.World.Server/README.md#hosted-release-records)
  and [the deployment guide](../development/ci.md#world-maintenance-and-recovery));
  `CartridgeStateLayout`, `CartridgeEffects`, and `DocumentEvaluationBudget`;
  the ledger's first and fourth steps (the cartridge's image tooling deleted,
  `puck cartridge-cost`, and the `tetromino` cartridge renamed and re-themed);
  and, of compiled worlds, the one validated load, the container with its
  header, and the `DEFN`, `ASST` and `CURV` chunks (owned by
  [the worlds manual](../architecture/worlds.md#compiled-worlds)).
- **Not started:** the rest of the asset packages, the rest of the ledger, the rest of the
  compiled-world chunks, and the evidence package. Release-pair qualification
  (`puck world release qualify`) exists; only metadata-only definition deltas
  pass it, other sections refuse, and Azure acceptance is open.
- **Partly landed, and partly defective where it exists:** the presentation
  view. `IWorldStateView` exists, with one implementation,
  `WorldDocumentStateView`, which answers from the definition the client was
  delivered; `WorldStateMirror` reads every presentation `state.*` row through
  it. `WorldProjection` composes and hydrates a document for a federated
  neighbour, but `Compose` never sets `Fields`, so a presentation-tier peer sees
  no field lattice, and the primary client is fed the authority's own definition
  by reference. `TryToDefinition` rebuilds the disclosed observations as plain
  `state.world` rows and copies no other authored row.

## The forcing artifact

The quilt, [the forcing world](state-and-language.md#the-forcing-world), is
built as an asset package, shipped as compiled worlds inside it, deployed to a
hosted authority, rolled back, and restored. The package supplies the unit
(`worlds/<id>/manifest.json` names its entry, its folder carries its worlds,
cartridges and addons, and the engine ships none of it); compiled worlds supply the delivery
format; the ledger supplies the boundary between house content and someone
else's; release pairs supply the operation (release B replaces A inside a
maintenance window, players earn items and cross corner boundaries under B, an
operator rolls back to A, and the items and completed transfers survive); and
the evidence package proves one candidate carrying all four.

**Check:** the quilt boots from its package folder with `--world`, and the
same command without one refuses and names what it needs; a law derives every
chunk fresh and compares it byte for byte with the chunk loaded from the
compiled world, and a `puck landing` canary boots the quilt from JSON and from
its compiled world and compares `stateHash` at a fixed tick; `puck roms --check`
passes on the checkout and fails on each seeded defect; the operator exercise
(prepare B, deploy B, inspect status, play, roll back, reconnect, verify
progress) completes through the supported CLI with no manual blob edits, and
repeats with an interrupted deployment and an interrupted rollback.

## Packages

### Asset packages

**Owns:** `worlds/<id>/` packages and their `manifest.json`
(`puck.asset-package.v1`), package resolution in `Puck.World` (`--world
<folder>`), `puck compile --tree`, `puck world prepare`, `puck world release
prepare`, `puck official build`, `puck azure build`, the silo image, and the
eviction of everything under `src/Puck.World/Assets` but the default recording.

**Delivered:** the debug world is the package `worlds/debug/`; `Puck.World`
boots a package folder (its manifest's entry) and refuses to boot without
`--world`; the shader package store is `packages/` beside the booted document;
`puck compile --tree <package> --output <out>` writes a bootable copy of a
package, and the release publish compiles the debug package beside the
published engine; the silo image, `world prepare`, `official build` and the
Azure release take the package folder; test fixtures moved to the tests that
read them, the orphans were deleted, and the generated schemas moved to
`src/Puck.World.Schema/Schemas/` ([decision](../decisions/runtime-and-delivery.md#asset-packages)).

**Delivers, in this order:**

1. The manifest supplies the window title and icon, and a world may not
   override them; `branding/` copies land in the package.
2. `puck official` scoped per package (`<out>/<id>/<channel>/manifest.json`)
   over a shared `objects/sha256` store; the manifest supplies the identity,
   channels, and publication base that are hard-coded today.
3. The release manifest pins the package's content and required extensions;
   the desktop artifact carries no content.
4. The procedure for authoring a package outside this repository is written
   and followed once from an empty folder to a booting world.

**Check:** `dotnet run --project src/Puck.World -c Release -- --world
worlds/debug --exit-after-seconds 2` boots the debug world and the same command
without `--world` refuses naming it; `puck official build --worlds worlds/debug`
then `puck official verify` passes; `puck azure build` produces a bundle with no
hard-coded package name or channel; `puck landing` passes.

### The ledger

**Owns:** `roms/manifest.json` (`puck.roms.v1`) and its README; `puck roms`
(`--check`, `fetch`, `locate <id>`); `CorpusManifest`; one public resolver for
verification ROMs; the cartridges' move into the product.

**Delivers, in this order:**

1. Landed: the cartridge's image-export, text-look, and screenshot harnesses
   are deleted, with the stale `verification/authority` rule in `.gitignore`,
   the unread `PUCK_AGB_*` documentation, and a made-up ROM path in a
   transpiler test; `CartridgeCostMeasurement` is public in
   `Puck.GamingBricks.Forge` and `puck cartridge-cost` runs its bisection on
   both machines.
2. The ledger: every entry has `id`, `kind`, `sha256`; `firmware` entries
   (bytes in git beside their source and license, with the `puck firmware`
   generator), `corpus` entries (fetched into the local cache; `version`,
   `archive`, `root`, `license`, `homepage`), `external` entries (bytes never in
   git; description, accepted hashes, the battery stages that use them).
   `CorpusManifest` reads the ledger and both `corpora.json` files are deleted.
   `--check` verifies the manifest and every tracked hash and scans in reverse:
   any tracked file with a ROM extension or a valid Game Boy or Game Boy
   Advance header that neither the ledger nor a product manifest names fails.
   `verify.yml`'s `ledgers` job runs it and the corpus cache key hashes the
   manifest.
3. Landed: the cartridges live in the debug package's `cartridges/`, each
   `.puck` source beside its document.
4. Landed: the CGB cartridge is `tetromino.cgb` in source, document, tests,
   and prose, re-themed so its presentation no longer resembles the commercial
   game while its rules, piece shapes, scoring, and speeds stay. The re-theme
   is a chalkboard: a slate backdrop and seven pastel chalks in place of the
   familiar piece colours; a rounded chalk cell and a ruler-ticked well side;
   the panel moved left of the well; the modes named `ENDLESS` and `RUBBLE`;
   a `TETROMINO` title; original endless-mode and title music; and a balloon
   in place of the rocket that ends the second mode. Nothing outside the
   cartridge's own tests pinned its hash.
5. External images resolve from one local store beside the corpus cache, keyed
   by id; `--bios`, `--accuracy-suite`, both `--boot` spellings, the
   engine's `bios=` option, and `firmware.path` become ledger ids; a stage given
   an image with an unaccepted hash refuses it by entry id and a stage missing
   one skips naming the id.

**Check:** a case-insensitive `puck search -M 0` for the commercial game's name
(outside git history and `experimental/`) finds nothing; no test returns early on an environment variable; `puck roms --check`
passes and fails on each seeded defect (hash drift, missing file, unregistered
ROM); both batteries fetch through the verb and run their asset-free lanes
unchanged; the round-trip gate covers all three cartridges from the manifest;
the official world boots with every arcade cabinet inserted.

### Compiled worlds

A compiled world (`<name>.puckb`) is a chunk container in the shape `PbakBundle`
reads: a header, then chunks of a four-character code, length, content hash, and
8-byte-aligned payload, with the canonical variable-length integers
`AutomaticIntegerSequenceCodec` writes. The writer and reader
(`CanonicalBinaryWriterExtensions`, `CanonicalBinaryReader`) and the container
(`ChunkContainer`) are public in `Puck.Assets`, and `PbakBundle` reads the same
container. The header records the format version,
the engine build, the machine catalog fingerprint, the definition hash over the
canonical JSON bytes (so the existing `sha256-64` pins keep their meaning), and
the instance identity. Each chunk records its derivation, the derivation's
version, and the hashes of the inputs it read beyond the definition (asset
files, neighbour documents, the CPU identity for float products). A mismatched
chunk is re-derived and the rest kept; a mismatched header is ignored whole;
neither repairs or adapts data. GPU objects, live runtimes, adjacency
projections and neighbour solids, and the live scene program are never stored.

| Code | Holds | Depends on |
|---|---|---|
| `DEFN` | the drawn, resolved definition, as compact canonical JSON | header only |
| `ASST` | content hashes of every asset file read | asset files |
| `PLCE` | placement frames, spatial index, distribution offsets | `DEFN` |
| `SOLD` | the server solid program, evaluator, and a fully baked distance grid | `DEFN`, kits |
| `NAVB` | navigation bake arrays per domain | `SOLD` |
| `TOPO` | compiled topologies | `DEFN` |
| `POPL` | kits, motion, curves, gravity, colliders, channels | `DEFN` |
| `TBLS` | compiled tables | table files |
| `RULE` | compiled rules, patterns, search plans | `DEFN`, `TBLS` |
| `ROMS` | cartridge ROMs, source hashes, symbols | cartridge sources |
| `WASM` | serialized Wasmtime modules | module bytes, Wasmtime build, CPU |
| `FONT` | the packed font atlas catalog | font files |
| `SCNE` | capacity probe, static scene streams, client static field | `DEFN`, `FONT` |
| `AUDI` | voice patches, tune ROMs | patch and tune files |

`puck compile` writes one beside a document's JSON, `puck compile --tree`
produces them for a package's worlds, the runtime writes one into the per-user
compiled-world cache on a miss. Checkpoints, replay tapes, and instance starts do
not yet reference one by its header hash. Every package that adds a chunk
carries the same law: for every shipped world, each product derived fresh equals
the product loaded from the compiled world byte for byte. A chunk carries no
version: the header's engine build moves with any change to code a derivation
runs ([derivation keys](#derivation-keys)).

**One validated load (landed).** Owns `WorldDefinitionValidator.ValidateCore`'s
callers and `WorldServer.RecompileRules`'s. One validation receipt reaches the
server constructor, the machine host, and post-build wiring, across mutation,
reload, boot, checkpoint restore, and separate hazard and budget requests with
explicit ownership; catalog shape alone is not a safe cache key, so nothing is
keyed on it.

Installed hazard and budget requests reuse one compilation and its analysis.
Every loader prepares first-fill draws, retained document bindings, host
overrides, and state-row settlement (`WorldStateSettlement`) before its one full
admission, so the document the receipt names is the document that installs and
construction neither validates nor compiles again; draw inputs use the existing
source and row validators, and a section no arena can hold is handed on
unchanged for the admission to refuse by name. A constructor handed no receipt
settles before admitting for itself. Construction exports clocks and inverse
boards from the initial arena and installs it directly, without seeding a second
arena, and hands the same operation's local admission proof to machine
preparation. Checkpoint restore retains its deserialized admission through
construction, retained-turn checks and installation; a distinct journal base is
separately validated. File/DSL boot, local instance preparation, and the hosted
asynchronous read each carry their receipt into construction, which accepts it
when it names that document and the catalog the host validates against;
post-build wiring rechecks environment-dependent sections without recompiling
rules. Neighbour resolution reduces a neighbour to seam facts and admits
nothing, so it carries no receipt.

Two doors validate a document twice by design. A hosted reload reads and
validates the published definition to refuse it early, then submits it to the
rebuild door, which admits it again at its own tick: a rebuild settles clocks at
the tick it applies, so a receipt earned at boot time does not name the document
it installs. A replay drive deserializes its embedded definition with provider
checks deferred, and construction admits it against the drive's machine catalog;
a receipt is bound to the catalog it was earned against, so the first does not
stand in for the second.

Check: a desktop boot, an instance start, and a checkpoint restore each run
`ValidateCore` once and compile rules once, with state hashes unchanged —
`WorldValidatedLoadLawTests` in `tests/Puck.World.Server.Tests` and
`tests/Puck.World.Schema.Tests`, counting through the `world.boot` work source's
`world.boot.validations` and `world.boot.rule-compilations` kinds (`WorldBootWork`).

**Container, header, `DEFN`, `ASST` (landed).** Owns the container codec's
shared home, `puck compile`, the runtime cache. The
codec, the header, the chunk registry (`CompiledWorldChunks`), both chunks,
the three producers, and the boot's use of them are in place, and
[the worlds manual](../architecture/worlds.md#compiled-worlds) owns their
contract. Check: `CompiledCatalogLawTests` (quarantined in `experimental/tests/Puck.World.Tests`) boots
every compiled world the build shipped and counts a hit through the
`world.boot` work source's `world.boot.compiled-hits`; `CompiledWorldLawTests`
in `tests/Puck.World.Games.Tests` ignores a mismatched
header, re-derives a chunk whose asset input moved while keeping the
rest, and admits byte for byte the definition a fresh draw admits;
`WorldDocumentOutputLawTests` in
`tests/Puck.Cli.Worlds.Tests` holds every compiled world a tree compile writes to
what its document derives fresh, byte for byte. Still open:

- A hit still validates and compiles rules like a miss. Admitting a compiled
  world's definition without validating again needs the validation receipt to
  travel in the compiled world, which the trust decision allows for a local
  boot and nothing stores yet.
- A hit still compiles, composes, parses and serializes the authored document
  to compute the header's definition hash, so `DEFN` saves the draw alone; the
  boot-time win arrives with the simulation chunks.
- A derivation that reads an earlier chunk's product declares it in
  `DependsOn`, and a boot re-derives it when that chunk is re-derived; a chunk
  that reads a neighbour document or the CPU identity records it as an input
  through `ReadInput`, which no chunk does yet.
- The hosted asynchronous load and the replay drive draw without a compiled
  world, and checkpoints, replay tapes, and instance starts do not yet
  reference one by its header.
- The per-user compiled-world cache keeps one compiled world per document
  path and never evicts one whose document was deleted.

**Simulation chunks** (`PLCE`, `SOLD`, `NAVB`, `TOPO`, `POPL`, `TBLS`). Owns
the caches those derivations sit behind, which are `ConditionalWeakTable` and
`ReferenceEquals` checks on section instances today and must be seeded by the
loader or move to content keys; and the duplicated work beside them (channels compiled per consumer,
distribution offsets computed three times, solids built twice per mutation).
Constructor tables now reuse validation's compiled bundle.
Check: the byte-for-byte law over the six chunks; the duplicated work gone.

**Everything else.** `RULE` after ordinal operands and closed fact tags (the
rebuilt state vocabulary decides them); `ROMS` and `WASM` after confirming the
Wasmtime binding exposes module serialization; `FONT`, `SCNE`, `AUDI` after the
static scene is its own stream and stamps on the fixed-point path so `SCNE` is
bit-portable; and the checkpoint, replay, and instance references. Check: the
byte-for-byte law over every chunk; restore, rewind, and the replay drive load
products from the compiled world their checkpoint names; the `puck landing`
canary and `puck parity` from compiled worlds on both backends.

#### Derivation keys

A derived product is keyed by the code that makes it, so a deliberate change to
that code moves the key and nothing is served stale. The running build computes
the key, so nothing records it and nothing goes stale: the producer's assembly
roots the reach (`AssemblyReach.Of`, every Puck assembly it reaches), and the
content fingerprint of that reach is the key. The compiled world's chunks need
none of their own: its header keys the engine build, the module version id of
every `Puck.*` assembly reachable from `Puck.World.Schema`
(`CompiledWorld.EngineBuild`), and `DEFN`, `ASST` and `CURV` derive wholly
inside that reach. A chunk whose derivation lies outside the reach adds the
fingerprint of its producer's assembly as its key. No other product is keyed
this way yet; the first slice that keys one adds the fingerprint over the reach.

**Owns:** `AssemblyReach` and the product below.

**Delivers:**

1. **Shader packages.** `ShaderPackager.KeyOf` hashes the document's logical
   path, the pipeline's name, every closure file's pin, and each pass's interface
   and declarations pins. It reads nothing of the code that compiles and writes
   the package, so a change to that code under an unchanged source keeps the key
   and a stored package is served. The key gains the packaging code's
   fingerprint. Check: a law that a packaging-code change misses a package stored
   under the earlier key and loads the rebuilt one.

### The presentation view

Presentation code receives a view and never the authority's document, as
[the decision](../decisions/runtime-and-delivery.md#the-presentation-view)
states. The gap between that and the code is a programme rather than a package:
the primary client holds the authority's own definition by reference, and
presentation reads document members the projection omits. Presentation `state.*`
reads already go through `WorldStateMirror` and `IWorldStateView`
(`src/Puck.World.Protocol`), whose one implementation, `WorldDocumentStateView`,
answers from that delivered definition; the cuts below add the view that does
not need the document. The five cuts are in dependency order, each independently
landable.

**The mechanism defects, and the conformance law.** Owns `WorldProjection`'s
`Compose` and `TryToDefinition`, and the round-trip law. Delivers a `Compose`
that supplies `Fields`, which it declares and never sets, so a presentation-tier
peer stops seeing no field lattice where the client's state view
(`WorldDocumentStateView`) and `WorldFieldEmitter` read `Definition.Fields`. The
hydration already reads `projection.Observations` back as plain `state.world`
rows, so the channel `WorldStateDisclosure.Compose` fills is not inert; it
rebuilds the rest of `StateRaw` only through `WorldFieldsSection.ToStateSection`,
which manufactures lattice-shaped rows from `Fields`, and copies no authored row
beyond the observed ones. The conformance law is written here: for every shipped
world, tick, and recipient, the colocated view and a view rebuilt from an
encode-then-decode round trip answer every query identically. Check: a law that
a composed projection carries the document's field lattice; a law that a
disclosed observation of each cell kind survives the round trip
(`VectorStateLawTests` holds the vector row's); the conformance law green over
every shipped world and red when one member is dropped from `Compose`, shown
once.

**The static sections become disclosure decisions.** Owns
`WorldProjectionDocument`'s member list and the compose and hydrate arms for
`Curves`, `Markers`, `SeatModes`, `Text`, `Theme`, `Icons`, and the world seed
in `Generation`. Delivers a decision per member, each either added to the
member list or its reader corrected: `WorldCameraRigCompiler` and
`WorldGaitDrivers` read `Curves` for camera paths and curve-follow drivers,
`WorldFramePresenter` reads `Markers` for marker chips, `WorldSeatBindings`
reads `SeatModes`, `WorldTextCatalog` reads `Text` for screen and placement
text, `WorldThemeResolve` reads `Theme` for chrome tokens, and
`WorldPlacementStamper`, `WorldSceneEmitter`, and `WorldSessionRenderEnvelope`
read the world seed for per-placement visual variety. None carries a rule or
logic surface. `WorldIconTable` reads `Icons` from the boot document only, so
whether icons cross at all is decided rather than assumed. Check: the
conformance law extended per member; a presentation-tier peer draws marker
chips, follows a camera path, binds a seat mode, lays out screen text, and
resolves theme tokens on the real executable; the shipped-world baselines
unmoved.

**Target registers and the authoring envelope by design.** Owns `WorldClient`'s
designation encoding and its target-register table, and the envelope reads in
`WorldSceneEmitter`, `WorldFramePresenter`, `WorldSessionRenderEnvelope`, and
`WorldBootComposition`. Delivers a design decision for each rather than
plumbing. `WorldClient` compiles `TargetRegisters` to encode channel and reach
and resolves a target lock inside a register's range and cone, so either the
register table is disclosed as static document geometry or cone resolution
moves to the authority and the client submits an aim ray. The envelope is
`Authoring.MaxPlacementScale`, `AuthoringHeadroomScreens`,
`AuthoringHeadroomPlacements`, and `DerivedFaceScreens`, which are derived from
the placements when unauthored, so the presenters most likely recompute it from
the disclosed placements and screens; an explicitly authored policy row is the
case that decides it. Check: a designation from a view-only client reaches the
same subject the by-reference client picks, on the real executable; a peer
sizes the same screen and placement reservations as the authority for the same
document, with a law over both the derived and the authored policy.

**Bound state crosses as per-recipient observations.** Owns the
observation-backed `IWorldStateView` implementation, the observation channel's
regions, and `WorldStateDisclosure.ValidateBindings`' reach. The interface and
`WorldDocumentStateView` exist: `WorldStateMirror` reads every presentation read
of state through the interface, from the HUD binding resolver, the binding bar
and the radial wheel to the overlay predicates, camera operands, markers, render
colors, theme, seat-binding contexts, and a body's look lanes, gait drivers,
poses, effectors and scale. What the cut adds is the second implementation: the
presentation manifest's rows crossing as observations filtered per recipient, and
a view that answers from them instead of from the delivered document. Public rows
share one region, restricted rows get one region per recipient in use, and an
audience is re-evaluated when a row it reads moves or a slot's seat changes.
`ValidateBindings` walks for `IDocumentStateValue`, and `BindableScalar` and
`BindableColor` do not carry it, so a bindable naming a restricted row is
invisible to the compose-time gate; they become document state values or the
walk learns their shape. A binding its recipient may not read draws its literal
fallback and narrates once, and a row whose audience comes from whichever
ordered zone holds a token is not reachable from a document, so a binding to one
refuses at validation rather than being half-enforced. Check: a `hiddenranks` canary
— a HUD gauge bound to the opposing rank row draws its fallback, the owning
seat's draws the value, and a rule writing the reveal row mid-run flips the
first; a law that `ValidateBindings` refuses a `BindableScalar` naming a
restricted row; the conformance law over a world with restricted rows; a
`state.<row>` gauge resolving over a presentation-tier link on the real
executable.

**The primary client onto the view.** Owns `WorldOutputHub`'s fan-out,
`WorldClient.DeliverDefinition` and `DeliverState`, and the colocated view.
Delivers the delivery seam handing out a view rather than the live definition
`WorldDocument.DeliverPending` passes today, with the colocated view a zero-copy filtered
view over the authority's export so the floor device serialises nothing, and the
tools that legitimately read everything — the console modules,
`WorldCaptureScheduler`, `world.save`, and host boot — taking the named
whole-document capability instead. The federated-neighbour mirror, which already
observes through `WorldProjection`, is the working precedent. Check:
`puck references WorldDefinition` over `src/Puck.World.Client` names the view's
own surface and the tool capability, nothing else; the conformance law green;
`puck landing` and `puck parity` exit 0 with the `rim-drop` and `traveller-kit`
canaries green; frame time and steady-state allocation at the colocated seam
unregressed over one authored workload.

### Release pairs

**Owns:** the release manifest, the durable maintenance operation, the
coordinator, rollback and restore, `WorldSiloLifecycleLawTests`,
`WorldAuthorityBlobStoreTests`, the deployment guide's runbook.

**Delivers:** group-scoped restore that refuses when later external transfers
or durable effects cannot be reconciled (inspection on an isolated copy stays
possible); the preservation rules for definition edits, written to the boundary
that an edit beyond metadata is a new release; and release-pair qualification
with packaged images replacing the deployment fallback. A release is an
immutable manifest naming the engine image digest, composed definitions,
required assets and extensions, and the persistence and peer-protocol
contracts; a qualified pair proves both binaries restore and continue
representative states written by the other, including B-only reachable states
and journal encodings. The authored A-to-B delta is computed apart from live
state, applied only as admitted changes through validation and authority,
retained by stable row and entity identity, refusing removed or retyped state
and conflicting live edits; rollback reverses the admitted delta with the same
checks and never installs the old definition wholesale. A pair carrying
machines qualifies through checkpoints: they preserve named handheld machine
cores, held input, pacing, instance generations, and each binding's on-change
memo, and restore only against the same firmware, cartridge, and core format
identity ([the server README](../../src/Puck.World.Server/README.md#hosted-release-records)).
Pumped addon guest state, applied screen operations, live coupled machine links,
and enabled machine rewind history refuse capture by name, so a world
exercising one cannot pass qualification yet. Release pairs own
deciding, state by state, whether each gains capture or stays excluded by that
refusal.

**Check:** A-to-B deployment, play under B, B-to-A rollback, and continuation
under A preserve inventory, identity, population, clocks, random state,
supported machine state, live edits, and completed transfers, compared before
the next tick; unsupported state, a conflicting authored row, a missing
predecessor image, an unresolved external transfer, or a stale fence produces a
named refusal and no partial installation; failure at each durable phase, a
delayed old writer, and a retry after runner loss preserve exclusive ownership
and follow the recorded commit boundary; a post-commit failure never
auto-recovers to an older save; an acknowledged rollback survives another
restart; finalization closes only the rollback window; the operator exercise
above, through the CLI, with the release identifiers and results archived as
qualification evidence.

### The evidence package

Last, against one named candidate, because every row is evidence about the
same candidate.

**Owns:** `CartridgeStateLayout`, `CartridgeEffects`, both forge suites, and
the language tool suites.

**Delivers:** an audit of metadata still reconstructed in several consumers,
extending the two homes rather than building a general IR, so every admitted
operation has one interpretation and unsupported target combinations refuse at
a located span; durable regression combinations across both native targets and
the language tools (mixed byte and wide declaration order, every comparison
branch, saved state partial and repeated; literals, zero divisors, shift
bounds, overflow, integers above float exactness; scene changes with nested
indices, implicit writes under exclusive guards, nested queue loops; template
defaults, shadowing, capture, units in several orders; raw and interpolated
strings; compile, lint, and language-server agreement at the same span); a
measurement of lowering and cancellation under bounded large inputs (indexed
reads scaling with output work, structural deduplication without a linear
scan, no partial output on limit or cancellation); one packaged release
candidate verified through the real `Puck.World` executable for save, open,
build, export, play, named machine insertion and reset, persistence across
restart, refusals, and clean shutdown, with cartridge admission, firmware,
registries, and extension composition; and the pre-first-tick cartridge
insertion trigger re-run, closed without a current reproducer. CGB and AGB
compare at normalized authored state on matched game-frame boundaries;
repeated execution proves repeatability; independent expected results prove
both targets did not repeat one mistake.

**Check:** both forge suites and the language tools pass against the candidate;
every shipped source and document pair agrees; the landing commit message
carries the commands and results against the candidate.

## Sequencing

| Step | Packages, in parallel | Why here |
|---|---|---|
| 1 — today | The rest of the asset packages; the ledger; the view's mechanism defects and conformance law | Neither of the first two reads `Puck.State`. The root must exist before anything is named relative to it, and the ledger before cartridges leave the engine tree. The two projection defects are `Puck.World.Schema` alone, and the law they are written with is what every later cut is measured by. |
| 2 | One validated load; the container; the view's static sections; the derivation keys for shader packages, pipeline caches and kernel sets | Validating once and the container are independent of the arena; a disclosed static section is one compose and hydrate arm each and parallelizable; the derivation-key slices share the verb's machinery and touch no simulation state, and a new chunk outside the engine build's reach adds its key with the chunk. |
| 3 | Simulation chunks; target registers and the authoring envelope | Content-keyed caches first; the widest slice of the boot profile. The two by-design members need a decision before state crosses, because both change what a client submits or recomputes. |
| 4 | Everything else in the compiled world; release pairs; bound state as observations | `RULE` needs the rebuilt vocabulary's ordinal operands; a qualified pair needs a packaged world; the observation channel needs the rebuilt substrate's delivery seam. |
| 5 | The primary client onto the view | Last of the view's cuts: until the four above are settled it would either darken presentation or keep a raw-document escape hatch, which is what it exists to remove. |
| 6 | The evidence package; the forcing artifact end to end | One candidate carrying all four. |

## Verification summary

```bash
dotnet run --project src/Puck.World -c Release -- --world worlds/debug --exit-after-seconds 2
```

```bash
puck official build --tree artifacts/official --channel dev --engine src/Puck.World.Browser/bin/Release/net11.0/publish/wwwroot --worlds worlds/debug
```

```bash
puck official verify --tree artifacts/official --channel dev
```

```bash
puck roms --check
```

```bash
puck landing --against origin/main --base <the commit the branch was authored from>
```

```bash
puck parity
```

```bash
dotnet test tests/Puck.World.Server.Tests -c Release
dotnet test tests/Puck.World.Games.Tests -c Release
dotnet test tests/Puck.World.Machines.Tests -c Release
dotnet test tests/Puck.World.Client.Tests -c Release
dotnet test tests/Puck.World.Presentation.Tests -c Release
dotnet test tests/Puck.World.Silo.Tests -c Release
dotnet test tests/Puck.World.Tests -c Release
```

```bash
puck docs links
```

---

[Plans](README.md) · [Decisions](../decisions/runtime-and-delivery.md)
