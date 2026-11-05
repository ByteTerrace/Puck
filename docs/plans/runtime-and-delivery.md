# Runtime and delivery

A world is authored once and then has to reach a player. That path runs from
the content root a product declares, through the derivation that turns a
document into a running world, out to a deployed server that can be rolled
forward, rolled back, and restored without losing what the players earned.
This programme makes the whole path one unit: a product tree that declares what
it holds, ships its authored sources, derives its data on demand as a cache,
and is deployed, retained and returned to as one thing. It also settles the
last leg of that path: what a client may observe of the document it draws. The
reasoning behind every decision is in
[the decisions register](../decisions/runtime-and-delivery.md); the
[owned-memory design](memory-pooling.md) is a separate runtime foundation.

## The forcing artifact

The debug world, [the forcing world](state-and-language.md#the-forcing-world),
is built as an asset package, shipped from its authored sources, deployed to a
hosted authority, rolled back, and restored. The package supplies the unit
(`worlds/debug/manifest.json` names its entry, its folder carries its worlds,
cartridges and addons, and the engine ships none of it); compiled worlds are a
cache derived from the sources on demand, never the shipped truth; the ledger
supplies the boundary between house content and someone else's; release pairs
supply the operation (release B replaces A inside a maintenance window, players
earn items and cross place boundaries under B, an operator rolls back to A, and
the items and completed transfers survive); and the evidence package proves one
candidate carrying all four.

**Check:** `puck_world_start` boots the debug package from its folder with
`world`, and the same start without one refuses and names what it needs; every
chunk derived fresh equals the chunk the cache holds, byte for byte, and a
debug-world boot from its sources and from its compiled world agree on
`stateHash` at a fixed tick; once the first release has shipped, the operator
exercise (prepare B, deploy B, inspect status, play, roll back, reconnect,
verify progress) completes through the supported CLI with no manual blob
edits, and repeats with an interrupted deployment and an interrupted rollback
([release pairs](#release-pairs) are frozen until then).

## Packages

### Asset packages

**Owns:** `worlds/<id>/` packages and their `manifest.json`
(`puck.asset-package.v1`), package resolution in `Puck.World` (`--world
<folder>`), `puck compile --tree`, `puck world prepare`, `puck world release
prepare`, `puck official build`, `puck azure build` and the silo image
([decision](../decisions/runtime-and-delivery.md#asset-packages)).

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

**Check:** `puck official build --worlds worlds/debug` then `puck official
verify` passes; `puck azure build` produces a bundle with no hard-coded package
name or channel.

### The ledger

**Owns:** `roms/manifest.json` (`puck.roms.v1`) and its README; `puck roms`
(`--check`, `fetch`, `locate <id>`); `CorpusManifest`; one public resolver for
verification images.

The ledger records where the images Puck runs come from. It is provenance, not
a gate: no test, battery stage or CI job depends on an image outside git
(AGENTS.md), and it records no hash of a file git already holds.

**Delivers, in this order:**

1. Every entry has an `id` and a `kind`. `firmware` entries are split by origin:
   a hand-authored replacement image is source, and its bytes stay in git beside
   their license; an image generated from a program (`puck firmware`) is a
   derived product, built on demand and removed from git, and the generator's
   `--verify` is deleted ([derived products](open-items.md#derived-products-are-caches)).
   `corpus` entries name open conformance suites fetched into the local cache
   (`version`, `archive` and its `sha256`, `root`, `license`, `homepage`).
   `external` entries describe images whose bytes never enter git. `CorpusManifest`
   reads the ledger, and all three `corpora.json` files are deleted.
2. `--check` scans the tree: a tracked file with a ROM extension or a valid Game
   Boy or Game Boy Advance header that neither the ledger nor a product manifest
   names fails. The corpus cache key hashes the manifest.
3. A by-hand run resolves an external image from one local store beside the
   corpus cache, keyed by id: `--bios`, `--accuracy-suite`, both `--boot`
   spellings, the engine's `bios=` option and `firmware.path` accept a ledger id
   beside a path, and a run missing the image skips naming the id. Nothing
   automated reads that store.

**Check:** a case-insensitive `puck search -M 0` for the commercial game's name
(outside git history and `experimental/`) finds nothing; no test returns early
on an environment variable; `puck roms --check` passes and fails on an
unregistered ROM; both batteries fetch their corpora through the verb and run
on committed and fetched sources alone.

### Compiled worlds

A compiled world (`<name>.puckb`) is a chunk container in the shape
`PbakBundle` reads ([the worlds manual](../architecture/worlds.md#compiled-worlds)
owns the container, header and landed chunks). Each chunk records its
derivation and the hashes of the inputs it read beyond the definition; a
mismatched chunk is re-derived and the rest kept, a mismatched header is
ignored whole, and GPU objects, live runtimes, adjacency projections and the
live scene program are never stored.

| Code | Holds | Depends on |
|---|---|---|
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

Every chunk that lands carries one check: for every shipped world, each product
derived fresh equals the product loaded from the compiled world byte for byte.

**The container's remaining gaps:**

- A hit still validates and compiles rules like a miss. Admitting a compiled
  world's definition without validating again needs the validation receipt to
  travel in the compiled world, which the trust decision allows for a local
  boot and nothing stores yet.
- A hit still compiles, composes, parses and serializes the authored document
  to compute the header's definition hash, so `DEFN` saves the draw alone; a
  larger boot-time win needs a simulation chunk shown to save measured time.
- A chunk that reads a neighbour document or the CPU identity records it as an
  input through `ReadInput`, which no chunk does yet.
- The hosted asynchronous load and the replay drive draw without a compiled
  world, and checkpoints, replay tapes and instance starts do not reference one
  by its header.
- The per-user compiled-world cache keeps one compiled world per document path
  and never evicts one whose document was deleted.

**Simulation chunks** (`PLCE`, `SOLD`, `NAVB`, `TOPO`, `POPL`, `TBLS`) are
paused: none is built until one is shown, as a counted derivation against the
cold and warm boot profile, to save boot or edit-to-running time. The caches
those derivations sit behind are `ConditionalWeakTable` and `ReferenceEquals`
checks on section instances today and must be seeded by the loader or move to
content keys. The duplicated work beside them (channels compiled per consumer,
distribution offsets computed three times, solids built twice per mutation) is
removed whether or not a chunk is built.

**Everything else** waits, as the simulation chunks do, for a measured saving:
`RULE` after ordinal operands and closed fact tags; `ROMS` and `WASM` after
confirming the Wasmtime binding exposes module serialization; `FONT`, `SCNE`,
`AUDI` after the static scene is its own stream and stamps on the fixed-point
path so `SCNE` is bit-portable; and the checkpoint, replay and instance
references, after which restore, rewind and the replay drive load products from
the compiled world their checkpoint names.

#### Derivation keys

A derived product is keyed by the code that makes it, computed by the running
build and never recorded. The key is narrowed to the deriving call graph: the
methods the derivation reaches, hashed from the running build, so an edit
elsewhere in the same assembly does not move it. `CompiledWorldChunks` roots each
registered concrete producer, including its codec and input reader, in the shared
`CodeClosure` walker. Its `EngineBuild` keys the header, so adding a custom producer
includes that producer automatically. Applied metadata updates are filtered to the
types the closure reads; an unrelated edit in the same assembly preserves the key.

- **Shader packages.** `ShaderPackager.KeyOf` hashes the document's logical
  path, the pipeline's name, every closure file's pin, and each pass's
  interface and declarations pins, and nothing of the code that compiles and
  writes the package, so a change to that code under an unchanged source serves
  the stored package. The key gains the packaging code's call-graph
  fingerprint. Check: a packaging-code change misses the package stored under
  the earlier key and loads the rebuilt one.

### The presentation view

Presentation code receives a view and never the authority's document, as
[the decision](../decisions/runtime-and-delivery.md#the-presentation-view)
states. The primary client holds the authority's own definition by reference,
and presentation reads document members the projection omits. Presentation
`state.*` reads already go through `WorldStateMirror` and `IWorldStateView`,
whose one implementation, `WorldDocumentStateView`, answers from the delivered
definition; `WorldProjection.Compose` sets `FieldRegions` and `TryToDefinition`
hydrates them, rebuilding disclosed observations as plain `state.world` rows.
The five cuts are in dependency order, each independently landable.

**The conformance law and the disclosure round trip.** Owns `WorldProjection`'s
`Compose` and `TryToDefinition`, and the round-trip law: for every shipped
world, tick and recipient, the colocated view and a view rebuilt from an
encode-then-decode round trip answer every query identically. `TryToDefinition`
copies no authored row beyond the observed ones today. Check: a composed
projection carries the document's field regions and a hydrated one rebuilds the
lattice; a disclosed observation of each cell kind survives the round trip; the
conformance law green over every shipped world and red when one member is
dropped from `Compose`, shown once.

**The static sections become disclosure decisions.** Owns
`WorldProjectionDocument`'s member list and the compose and hydrate arms for
`Curves`, `Markers`, `SeatModes`, `Text`, `Theme`, `Icons`, and the world seed
in `Generation`. Delivers a decision per member, each either added to the
member list or its reader corrected: `WorldCameraRigCompiler` and
`WorldGaitDrivers` read `Curves`, `WorldFramePresenter` reads `Markers`,
`WorldSeatBindings` reads `SeatModes`, `WorldTextCatalog` reads `Text`,
`WorldThemeResolve` reads `Theme`, and `WorldPlacementStamper`,
`WorldSceneEmitter` and `WorldSessionRenderEnvelope` read the world seed. None
carries a rule or logic surface. `WorldIconTable` reads `Icons` from the boot
document only, so whether icons cross at all is decided rather than assumed.
Check: the conformance law extended per member; a presentation-tier peer draws
marker chips, follows a camera path, binds a seat mode, lays out screen text
and resolves theme tokens on the real executable.

**Target registers and the authoring envelope by design.** Owns `WorldClient`'s
designation encoding and its target-register table, and the envelope reads in
`WorldSceneEmitter`, `WorldFramePresenter`, `WorldSessionRenderEnvelope` and
`WorldBootComposition`. `WorldClient` compiles `TargetRegisters` to encode
channel and reach and resolves a target lock inside a register's range and
cone, so either the register table is disclosed as static document geometry or
cone resolution moves to the authority and the client submits an aim ray. The
envelope (`Authoring.MaxPlacementScale`, `AuthoringHeadroomScreens`,
`AuthoringHeadroomInstances`, `DerivedFaceScreens`) is derived from the
placements when unauthored, so the presenters most likely recompute it from the
disclosed placements and screens; an explicitly authored policy row decides it.
Check: a designation from a view-only client reaches the same subject the
by-reference client picks, on the real executable; a peer sizes the same screen
and placement reservations as the authority, for both the derived and the
authored policy.

**Bound state crosses as per-recipient observations.** Owns the
observation-backed `IWorldStateView` implementation, the observation channel's
regions, and `WorldStateDisclosure.ValidateBindings`' reach. The cut adds the
second implementation: the presentation manifest's rows crossing as
observations filtered per recipient, and a view that answers from them instead
of from the delivered document. Public rows share one region, restricted rows
get one region per recipient in use, and an audience is re-evaluated when a row
it reads moves or a slot's seat changes. `ValidateBindings` walks for
`IDocumentStateValue`, which `BindableScalar` and `BindableColor` do not carry,
so a bindable naming a restricted row is invisible to the compose-time gate;
they become document state values or the walk learns their shape. A binding its
recipient may not read draws its literal fallback and narrates once, and a row
whose audience comes from whichever ordered zone holds a token refuses at
validation rather than being half-enforced. Check: on the debug world, a HUD
gauge bound to the opposing rank row draws its fallback, the owning seat's draws
the value, and a rule writing the reveal row mid-run flips the first;
`ValidateBindings` refuses a `BindableScalar` naming a restricted row; a
`state.<row>` gauge resolves over a presentation-tier link on the real
executable.

**The primary client onto the view, and a produce path that reads no server
state.** Owns `WorldOutputHub`'s fan-out, `WorldClient.DeliverDefinition` and
`DeliverState`, the colocated view, and every server read on the produce path.
Delivers the delivery seam handing out a view rather than the live definition
`WorldDocument.DeliverPending` passes today, with the colocated view a
zero-copy filtered view over the authority's export, and the tools that
legitimately read everything (the console modules, `WorldCaptureScheduler`,
`world.save`, host boot) taking a named whole-document capability instead. The
federated-neighbour mirror, which already observes through `WorldProjection`, is
the working precedent. The same cut makes produce read no server state (the job
system does not need it; the silo and this view do):

- `IWorldAdjacencySource`'s tick-side state, written in
  `WorldAdjacencyFields.BeginTick` and read in `Pin`, becomes one immutable
  record published by a reference write.
- `WorldScreenBinder` resolves machine video outputs and instance servers once
  per tick (`MachinesOf(world)?.VideoOutput`, `WorldMachineHost.Instances`
  read live per frame today) and holds the table; machine and instance
  disposals (`WorldMachineHost.CommitInstances`, an instance stop's `Dispose`)
  run after the binder re-resolves.
- Every other `server.Definition`, `Grants`, `Population` and `Machines` read
  during produce moves out of produce or copies once a tick. A guard finds the
  reads an audit misses: `WorldServer` and `WorldInstanceHost` take a
  `[Conditional("DEBUG")]` assertion on their frame-facing seams, armed around
  produce with no thread running.

Check: `puck references WorldDefinition` over `src/Puck.World.Client` names the
view's own surface and the tool capability, nothing else; a debug build of the
windowed debug World under the guard runs the sweep's tour, a cabinet engaged
and reset, a `world.instance.stop` of a visible instance and a crossing into the
garden and back with no throw; `puck sweep` exits 0 over the debug world; frame
time and steady-state allocation at the colocated seam unregressed over one
authored workload.

### Release pairs

Frozen until the first shipped release. A qualified pair protects a deployed
predecessor, and nothing has shipped; compatibility is bounded to deployed
official artifacts and their retained predecessor
([the releases decision](../decisions/runtime-and-delivery.md#releases)). The
maintenance and recovery sequence already landed
([deployment guide](../development/ci.md#world-maintenance-and-recovery)).

**Owns:** the release manifest, the durable maintenance operation, the
coordinator, rollback and restore, the deployment guide's runbook.

**Delivers:** group-scoped restore that refuses when later external transfers
or durable effects cannot be reconciled (inspection on an isolated copy stays
possible); the preservation rules for definition edits, written to the boundary
that an edit beyond metadata is a new release; and release-pair qualification
with packaged images replacing the deployment fallback (`puck world release
qualify` passes metadata-only definition deltas today and refuses other
sections; Azure acceptance is open). A release is an immutable manifest naming
the engine image digest, composed definitions, required assets and extensions,
and the persistence and peer-protocol contracts; a qualified pair proves both
binaries restore and continue representative states written by the other,
including B-only reachable states and journal encodings. The authored A-to-B
delta is computed apart from live state, applied only as admitted changes
through validation and authority, retained by stable row and entity identity,
refusing removed or retyped state and conflicting live edits; rollback reverses
the admitted delta with the same checks and never installs the old definition
wholesale. A pair carrying machines qualifies through checkpoints that restore
only against the same firmware, cartridge and core format identity
([the server README](../../src/Puck.World.Server/README.md#hosted-release-records)).
Pumped addon guest state, applied screen operations, live coupled machine links
and enabled machine rewind history refuse capture by name; release pairs decide,
state by state, whether each gains capture or stays refused.

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
above, through the CLI.

### The evidence package

Last, against one named candidate, because every row is evidence about the
same candidate.

**Owns:** `CartridgeStateLayout`, `CartridgeEffects`, both forge suites, and
the language tool suites.

**Delivers:** an audit of metadata still reconstructed in several consumers,
extending the two homes rather than building a general IR, so every admitted
operation has one interpretation and unsupported target combinations refuse at
a located span; regression combinations across both native targets and the
language tools (mixed byte and wide declaration order, every comparison branch,
saved state partial and repeated; literals, zero divisors, shift bounds,
overflow, integers above float exactness; scene changes with nested indices,
implicit writes under exclusive guards, nested queue loops; template defaults,
shadowing, capture, units in several orders; raw and interpolated strings;
compile, lint, and language-server agreement at the same span); a measurement
of lowering and cancellation under bounded large inputs (indexed reads scaling
with output work, structural deduplication without a linear scan, no partial
output on limit or cancellation); one packaged release candidate verified
through the real `Puck.World` executable for save, open, build, export, play,
named machine insertion and reset, persistence across restart, refusals, and
clean shutdown, with cartridge admission, firmware, registries, and extension
composition; and the pre-first-tick cartridge insertion trigger re-run. CGB and
AGB compare at normalized authored state on matched game-frame boundaries;
independent expected results show both targets did not repeat one mistake.

**Check:** both forge suites and the language tools run against the candidate;
every shipped source and document pair agrees; the landing commit message
carries the commands and results against the candidate.

## Sequencing

| Step | Packages, in parallel | Why here |
|---|---|---|
| 1 | The rest of the asset packages; the ledger; the view's conformance law | Neither of the first two reads `Puck.State`; the law is `Puck.World.Schema` alone, and every later cut is measured by it. |
| 2 | The view's static sections; the call-graph derivation key for shader packages | A disclosed static section is one compose and hydrate arm each; the derivation-key slice touches no simulation state. |
| 3 | Target registers and the authoring envelope; a simulation chunk only once it is measured to save time | Both by-design members change what a client submits or recomputes, so they are decided before state crosses. |
| 4 | Everything else in the compiled world, each after a measured saving; bound state as observations | `RULE` needs ordinal operands; the observation channel needs the rebuilt substrate's delivery seam. |
| 5 | The primary client onto the view, with produce reading no server state | Last of the view's cuts: until the four above settle it would darken presentation or keep a raw-document escape hatch. |
| 6 | The evidence package; the forcing artifact end to end | One candidate carrying all four. |
| Frozen | Release pairs | Until the first shipped release. |

## Open items

What the programme still owes. A line is deleted when its work lands, never
ticked; the [open items](open-items.md#next) rank the work packets drawn from it.

- The rest of the [asset packages](#asset-packages): branding from the manifest,
  per-package official trees, the release manifest pinning package content.
- [The ledger](#the-ledger), including the split of firmware by origin.
- [Compiled worlds](#compiled-worlds): the container's remaining gaps; the
  call-graph derivation key; simulation chunks and the rest only after a
  measured saving.
- [The presentation view](#the-presentation-view): its five cuts in order.
- [Release pairs](#release-pairs), frozen until the first shipped release.
  `world release deploy` and `rollback` take the qualification exit codes:
  `world release qualify` exits 1 for a failed check and 2 for a refusal, but
  `AzureCommand.DeployWorldReleaseAsync` still throws on a refused start, so
  both verbs exit 2 for a failed check.
- [The evidence package](#the-evidence-package), against one named candidate.

---

[Plans](README.md) · [Decisions](../decisions/runtime-and-delivery.md)
