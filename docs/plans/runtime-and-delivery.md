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

Checked against `state/rebuild` at `1d0d910c9`.

- **Landed:** the baked solid query, `WorldSolidField`, `WorldOutputHub`
  narration, and `WorldDeadlineTable`; the release manifest, group store,
  coordinator, CLI verbs, and the maintenance-and-recovery sequence (owned by
  [the server README](../../src/Puck.World.Server/README.md#hosted-release-records)
  and [the deployment guide](../development/ci.md#world-maintenance-and-recovery));
  `CartridgeStateLayout`, `CartridgeEffects`, and `DocumentEvaluationBudget`.
- **Not started:** the product tree, the ledger, compiled worlds, release-pair
  qualification, and the evidence package. The `WorldServer` facade split rides
  the state rebuild's [WP11b](state-rebuild.md#wp11b--the-remainder).
- **Not started, and partly defective where it exists:** the presentation view.
  `WorldProjection` composes and hydrates a document for a federated neighbour,
  but `Compose` never sets `Fields` and `TryToDefinition` never reads
  `Observations`, and the primary client is fed the authority's own definition
  by reference.

## The forcing artifact

The quilt, [the forcing world](state-and-language.md#the-forcing-world), is
built as a product, shipped as compiled worlds inside it, deployed to a hosted
authority, rolled back, and restored. The product tree supplies the unit
(`content/puck/product.json` names its worlds, cartridges, addons, and
branding, and the engine ships none of it); compiled worlds supply the delivery
format; the ledger supplies the boundary between house content and someone
else's; release pairs supply the operation (release B replaces A inside a
maintenance window, players earn items and cross corner boundaries under B, an
operator rolls back to A, and the items and completed transfers survive); and
the evidence package proves one candidate carrying all four.

**Check:** the quilt boots from its product root with `--product`, and the
same command without one refuses and names what it needs; a law derives every
chunk fresh and compares it byte for byte with the chunk loaded from the
compiled world, and a `puck landing` canary boots the quilt from JSON and from
its compiled world and compares `stateHash` at a fixed tick; `puck roms --check`
passes on the checkout and fails on each seeded defect; the operator exercise
(prepare B, deploy B, inspect status, play, roll back, reconnect, verify
progress) completes through the supported CLI with no manual blob edits, and
repeats with an interrupted deployment and an interrupted rollback.

## Packages

### The product tree

**Owns:** `content/<id>/`, `product.json` (`puck.product.v1`), content
resolution in `Puck.World`, `puck product` (renamed from `puck official`),
`puck world prepare`, `puck world release prepare`, `puck azure build`, the
silo image, and the eviction of everything under `src/Puck.World/Assets`.

**Delivers, in this order, as one landing:**

1. Test fixtures move to the tests that use them (the canary music, tunes, and
   patch, the `quilt-nw-gap` shard, the shader-pipeline demo world), the orphans
   are deleted (the hud-builder addon binary, `sdf/example.sdf.json`, the
   transition stinger patch), and the generated schemas and default recording
   move to the projects that own them. The stale `standard.world.json` claims
   leave source comments and regenerated schemas.
2. `content/puck` and its manifest exist and the official content moves there:
   `branding/` (copies the build makes from the repository's `branding/`),
   `worlds/`, `cartridges/`, `music/`, `tunes/`, `patches/`, `addons/`,
   `pipelines/`, `hosting/`. The manifest declares `id` (the self-update
   application id), `name`, branding, root worlds, every document and asset
   with a family and a manifest-relative path, `channels`, `engines`, and the
   runtime extensions its hosted worlds require; the asset families are the
   official manifest's (music, table, tune, patch; audio, synth, font reserved)
   plus `cartridge`, `addon`, `pipeline`, and `compiled`. `Puck.World --product
   <root>` resolves every path against the root and nothing against the
   executable's directory or the repository layout; `--world <path>` stays for
   one document outside any product. The default boot, the shipped-worlds
   fallback, and the `Assets\**` content item are deleted. The build refuses a
   file under the root the manifest does not name and a manifest entry whose
   file is missing or whose hash disagrees.
3. `puck product build <root>` keeps the content-addressed output, verification,
   and serve verbs, scoped per product (`<out>/<id>/<channel>/manifest.json`)
   over a shared `objects/sha256` store; the manifest supplies the identity,
   channels, and publication base that are hard-coded today.
4. The hosted and desktop engines take a product root; the release manifest
   pins the product's content and required extensions; the silo image and the
   desktop artifact carry no content.
5. The manual, project map, package READMEs, and skills cite `content/puck` by
   document name, `puck.product.v1` is documented with the schemas, and the
   procedure for authoring a product outside this repository is written and
   followed once from an empty folder to a booting world.

**Check:** `dotnet run --project src/Puck.World -c Release -- --product
content/puck --exit-after-seconds 2` boots the official world and the same
command without `--product` refuses naming it; `puck product build content/puck`
then `puck product verify` passes; the dashboard boots the official world from
the built tree; `puck azure build` produces a bundle with no hard-coded product
name or channel; the silo image contains no content tree; `puck landing`
passes; `src/Puck.World` has no `Assets` directory; no documentation or skill
cites `src/Puck.World/Assets`.

### The ledger

**Owns:** `roms/manifest.json` (`puck.roms.v1`) and its README; `puck roms`
(`--check`, `fetch`, `locate <id>`); `CorpusManifest`; one public resolver for
verification ROMs; the Tetris tooling's deletion; the cartridges' move into the
product; `tetromino`.

**Delivers, in this order, as one landing:**

1. `TetrisExport`, `TetrisLook`, `TetrisShots`, `TetrisSideBySide`,
   `hgb-compare.puck` and its generated document are deleted, with the stale
   `verification/authority` rule in `.gitignore`, the unread `PUCK_AGB_*`
   documentation, and the made-up `roms/tetris.agb` path;
   `CartridgeCostMeasurement` becomes a `puck` verb keeping its bisection.
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
3. The cartridges move into `content/puck/cartridges` and its manifest;
   `pip.agb.puck` is committed beside its document; `CartridgeRoundTripTests`
   enumerates cartridges from product manifests.
4. `tetris.cgb` becomes `tetromino.cgb` in source, document, tests, and prose,
   re-themed so its presentation no longer resembles the commercial game;
   everything pinning its hash is re-recorded.
5. External images resolve from one local store beside the corpus cache, keyed
   by id; `--bios`, `--games`, `--ags`, `--accuracy-suite`, `--link-game`,
   `--solar-rom`, `--link-rom`, `--trade-rom`, both `--boot` spellings, the
   engine's `bios=` option, and `firmware.path` become ledger ids; a stage given
   an image with an unaccepted hash refuses it by entry id and a stage missing
   one skips naming the id.

**Check:** `puck search -M 0 PUCK_TETRIS`, `puck search -M 0 tetris-world`, and
`puck search -M 0 -i tetris` (outside git history and `experimental/`) return
nothing; no test returns early on an environment variable; `puck roms --check`
passes and fails on each seeded defect (hash drift, missing file, unregistered
ROM); both batteries fetch through the verb and run their asset-free lanes
unchanged; the round-trip gate covers all three cartridges from the manifest;
the official world boots with every arcade cabinet inserted.

### Compiled worlds

A compiled world (`.pwc`, a working name) is a chunk container in the shape
`PbakBundle` uses: a header, then chunks of a four-character code, length,
content hash, and 8-byte-aligned payload, with the canonical variable-length
integers of `AutomaticSequenceCodec`, whose writer and reader move from
`Puck.Assets` to a shared public home. The header records the format version,
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

`puck compile` writes one beside a document's JSON, `build/WorldAssets.targets`
produces them for shipped worlds, the runtime writes one into the state root on
a miss, and checkpoints, replay tapes, and instance starts reference one by its
header hash. Four packages, each with the same law: for every shipped world,
every product derived fresh equals the product loaded from the compiled world
byte for byte; a deliberate change to a derivation moves the chunk version and
re-records the compiled worlds in the same change.

**One validated load.** Owns `WorldDefinitionValidator.ValidateCore`'s callers
and `WorldServer.RecompileRules`'s. Delivers one validation receipt that the
server constructor, the machine host, and post-build wiring accept, extended
from mutation and reload to boot, checkpoint restore, and separate hazard and
budget requests with explicit ownership, since catalog shape alone is not a
safe cache key. Check: a desktop boot, instance start, and checkpoint restore
each run `ValidateCore` once and compile rules once, with state hashes
unchanged.

Installed hazard and budget requests now reuse one compilation and its analysis.
Server construction carries its validation's programs and tables into
installation when row settlement keeps the definition unchanged, and hands the
same operation's local admission proof to machine preparation.
Construction settles clocks and inverse boards from the
initial arena and installs it directly, without seeding a second arena. Checkpoint restore
retains its deserialized admission through construction, retained-turn checks and
installation; a distinct journal base is separately validated. File/DSL loaders now
retain their final receipt through boot and local instance preparation; post-build
wiring rechecks environment-dependent sections without recompiling rules. Bytes, file,
and asynchronous loaders now prepare first-fill draws and retained document bindings
before their one full admission; draw inputs use the existing source and row validators.
Row settlement, receipt propagation beyond hosted asynchronous loaders, and boot
overrides still need the once-per-load acceptance check above; the item remains open.

**Container, header, `DEFN`, `ASST`.** Owns the container codec's shared home,
`puck compile`, `WorldAssets.targets`, the runtime cache. Check: `Puck.World`
boots every shipped world from a compiled world; a mismatched header is
ignored and a mismatched chunk re-derived, each with a law.

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

### The presentation view

Presentation code receives a view and never the authority's document, as
[the decision](../decisions/runtime-and-delivery.md#the-presentation-view)
states. The gap between that and the code is a programme rather than a package:
the primary client holds the authority's own definition by reference,
presentation reads document members the projection omits, and every
presentation `state.*` read goes through `WorldStateReader`, which takes a
document. The five cuts are in dependency order, each independently landable.

**The mechanism defects, and the conformance law.** Owns `WorldProjection`'s
`Compose` and `TryToDefinition`, and the round-trip law. Delivers a `Compose`
that supplies `Fields`, which it declares and never sets, so a presentation-tier
peer stops seeing no field lattice where `WorldClient` and `WorldFieldEmitter`
read `Definition.Fields`; and a `TryToDefinition` that reads
`projection.Observations` back, so the channel `WorldStateDisclosure.Compose`
fills stops being inert — the hydration rebuilds `StateRaw` only through
`WorldFieldsSection.ToStateSection`, which manufactures lattice-shaped rows and
copies no authored row. The conformance law is written here: for every shipped
world, tick, and recipient, the colocated view and a view rebuilt from an
encode-then-decode round trip answer every query identically. Check: a law that
a composed projection carries the document's field lattice; a law that a
disclosed observation survives the round trip; the conformance law green over
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

**Bound state crosses as per-recipient observations.** Owns `WorldStateReader`'s
reads, the observation channel's regions, and
`WorldStateDisclosure.ValidateBindings`' reach. Delivers the presentation
manifest's rows crossing as observations filtered per recipient, and
`WorldStateReader` — the one surface every presentation `state.*` consumer reads
through, from the HUD binding resolver and the binding bar to the overlay,
wheel, gait, render-cycle, and seat-binding paths — reading the view instead of
a document, which is one file's methods rather than each consumer. Public rows
share one region, restricted rows get one region per recipient in use, and an
audience is re-evaluated when a row it reads moves or a slot's seat changes.
`ValidateBindings` walks for `IDocumentStateValue`, and `BindableScalar` and
`BindableColor` do not carry it, so a bindable naming a restricted row is
invisible to the compose-time gate; they become document state values or the
walk learns their shape. A binding its recipient may not read draws its literal
fallback and narrates once, and a row whose audience comes from whichever
ordered zone holds a token is not reachable from a document, so a binding to one
refuses at validation rather than being half-enforced. Check: a Stratego canary
— a HUD gauge bound to the opposing rank row draws its fallback, the owning
seat's draws the value, and a rule writing the reveal row mid-run flips the
first; a law that `ValidateBindings` refuses a `BindableScalar` naming a
restricted row; the conformance law over a world with restricted rows; a
`state.<row>` gauge resolving over a presentation-tier link on the real
executable.

**The primary client onto the view.** Owns `WorldOutputHub`'s fan-out,
`WorldClient.DeliverDefinition` and `DeliverState`, and the colocated view.
Delivers the delivery seam handing out a view rather than the live definition
`WorldDocument.Apply` passes today, with the colocated view a zero-copy filtered
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
machines qualifies by boot-anchored reproduction; addon guest state, applied
screen operations, live coupled links, and rewind history stay live features.

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

**Owns:** `CartridgeStateLayout`, `CartridgeEffects`, both forge suites, the
language tool suites, the milestone record in
[game development milestones](../development/game-milestones.md).

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
every shipped source and document pair agrees; the commands and results are
recorded against the candidate in the milestone record.

### Constraints that ride the rebuild

The `WorldServer` facade split and its maintenance constraints are the state
rebuild's [WP11b](state-rebuild.md#wp11b--the-remainder), which rewrites the
same partials. The clustering it executes:

| Object | Absorbs | Owns |
|---|---|---|
| `WorldDocument` | mutation compose and apply, generate, state-transform admission, admission | definition, base, journal, pending ops, preflight scopes, budget meter, solids, the delivery decision |
| `WorldTick` | step, contributions, channels, engagement, transfers, fields, music, responses, board enforcement, lattice draws | intents, channel fold scratch, federated intents, the music clock |
| `WorldRuleHost` | rule host, queries, patterns, decisions, trace, diagnostics, flock affinities | evaluator, compiled rules, tables, latches |
| `WorldExtensions` | extensions, the recorded-extension epoch, the external-operation dispatcher and journal | epoch, suppression; the addon and machine hosts hang off it |
| `WorldGrants` | grants, ownership, the admission half of admission | grants, owner base, the drive-denied latch |
| `WorldPersistence` | checkpoint, state hash, replay | nothing; it walks the others |

Bodies stay behind `IWorldGrantsView` and handles, so nothing under `Bodies/`
names `WorldServer`; the federation and checkpoint codecs' records nested in
server classes are un-nested by the facade. With it: one deadline primitive
(escrows, transfer escrows, contribution tenure, placement deals and
responses, reflow reviews, and parks swept once per tick from
`WorldDeadlineTable`, without LINQ), a comment ledger for
`puck scan -Only comment-smells` whose counts per file may only shrink, and a
lower file-length ceiling once the largest files are split.

## Sequencing

| Step | Packages, in parallel | Why here |
|---|---|---|
| 1 — today | The product tree; the ledger; the view's mechanism defects and conformance law | Neither of the first two reads `Puck.State`. The root must exist before anything is named relative to it, and the ledger before cartridges leave the engine tree. The two projection defects are `Puck.World.Schema` alone, and the law they are written with is what every later cut is measured by. |
| 2 — after the rebuild lands | The facade in WP11b; one validated load; the container; the view's static sections | The facade rewrites the partials the rebuild is rewriting; validating once and the container are independent of the arena; a disclosed static section is one compose and hydrate arm each and parallelizable. |
| 3 | Simulation chunks; target registers and the authoring envelope | Content-keyed caches first; the widest slice of the boot profile. The two by-design members need a decision before state crosses, because both change what a client submits or recomputes. |
| 4 | Everything else in the compiled world; release pairs; bound state as observations | `RULE` needs the rebuilt vocabulary's ordinal operands; a qualified pair needs a packaged product; the observation channel needs the rebuilt substrate's delivery seam. |
| 5 | The primary client onto the view | Last of the view's cuts: until the four above are settled it would either darken presentation or keep a raw-document escape hatch, which is what it exists to remove. |
| 6 | The evidence package; the forcing artifact end to end | One candidate carrying all four. |

## Verification summary

```bash
dotnet run --project src/Puck.World -c Release -- --product content/puck --exit-after-seconds 2
```

```bash
puck product build content/puck
```

```bash
puck product verify
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
dotnet test tests/Puck.World.Tests -c Release
```

```bash
puck doc-links
```

---

[Plans](README.md) · [Decisions](../decisions/runtime-and-delivery.md)
