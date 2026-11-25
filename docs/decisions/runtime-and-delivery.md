# Runtime and delivery: decisions

The choices behind [the runtime and delivery programme](../plans/runtime-and-delivery.md),
each with the problem it answers and what follows from it. Current behavior
lives in [the server README](../../src/Puck.World.Server/README.md) and
[the deployment guide](../development/ci.md); a decision here is why the
contract has the shape it has.

## Asset packages

**Status: the debug world is the first package; the rest of [the package
programme](../plans/runtime-and-delivery.md#asset-packages) is planned.**

**A world is an asset package the engine is handed (owner ruling).** The debug
world is the package `worlds/debug/` at the repository root, independent of the
engine: its documents and every file they name (fonts, tunes, cartridges,
pipeline sources) live in the folder, and every path a document authors
resolves beside the document. `Puck.World --world <folder>` boots it; the same
flag still takes a `.puck` source or a `.world.json` document.

**The manifest names the package and its entry (owner ruling).** A package's
`manifest.json` declares `schema` (`puck.asset-package.v1`), `id`, `name` and
`entry`. The entry is a document name, never a file, so one manifest serves the
source tree (the entry's `.puck` source) and a compiled package (its
`.world.json`). The manifest lists no files: the folder is the payload, and a
world's basis and imports are its source's own business.
`Puck.World.WorldPackageManifest` is the one reader, strict about every member.

**No world, no boot (owner ruling).** `Puck.World` launched without `--world`
refuses and names what it needs. The engine names no repository path and has
no default world; the debug launch (`.claude/launch.json`) and every documented
run command pass `--world worlds/debug`.

**The shader package store is `packages/` beside the booted document (owner
ruling).** `puck compile --tree <package> --output <out>` writes a bootable copy
of a package: its compiled documents and compiled worlds, the store at the
output's root, and every other file of the package. A source boot has no store
and compiles a pipeline on first use.

**The engine ships no content.** `Puck.World` and its siblings ship engine data
only: shaders, fonts, overlays, probe kinds and the default recording, each
owned by the project that uses it. Document schemas live with the document
model (`src/Puck.World.Schema/Schemas/`) and ship in the CLI package, because
only CLI tooling reads them. Worlds, cartridges, music, addons and art belong
to a package. Test fixtures live with the tests that read them.

**Existing pipelines take a package; none is added.** `puck compile --tree`,
`puck world prepare`, `puck official build --worlds` and the host release each
take the package folder, and the release publish compiles the package beside
the published engine. `puck world player` composes those same steps into the
folder a consumer machine runs (the owner's ruling: the RTX 2060 machine runs a
prepared release build with its caches prewarmed and never builds, compiles a
cartridge or cold-bakes). The published engine fills that folder's caches
itself, so each identity it ships is the one the consumer's boot derives; a
consumer device whose window size or memory bars demand other mesh tiers is a
baking target of its own.

**Planned, not implemented.** A package's manifest supplying the window title
and icon, which a world may not override; channels; the release manifest
pinning package content; and the desktop artifact carrying no content, which
replaces the content-bearing player folder once content arrives through the
release manifest. A
compiled world stays a cache with a strong key inside a package, never the
packaged unit, because its bytes change with every engine build.

## Replay and tick scheduling

**One replay system and one scheduler (lead decision).** `replay.*` records,
persists, verifies and drives debug and player sessions, including addon guests.
`world.at` arms commands at exact ticks. Test worlds lower their `when` blocks
to the `schedule` document section; `WorldTestRun` adapts those rows to the same
`WorldTickScript` dispatcher and `WorldTickSchedule` queue. The test adapter
retains seat authority, expected refusals, edit echoes, truncated-run detection
and exports from sibling worlds, because `puck test --reproduce` measures those
results. Its manifest is `puck.world.test-run.v1`, written as `test-run.json`.

**Native capture is a product capability (owner ruling).** Gameplay video and
audio clips, including loopback and microphone input, stay even without a live
world caller. `capture.start/status/stop`, `--recording`, the recording document
format and loader, the Windows encoder and audio capture, and the Linux
factories and registration methods remain. Capture is composed in windowed
Worlds and uses `Puck.Recording`; deterministic session playback uses `replay.*`.
`WorldProbes`, `probe.*` and probe tracks remain for the stretch goal.

## ROMs

**The falling-block cartridge keeps its game and is `tetromino`.** The
commercial game's name is a trademark with no generic equivalent the way chess
has one; a tetromino is the mathematical name for its pieces. Every tie to the
commercial reference image is cut, and the name comes with a re-theme, because
a generic name removes the trademark but not the likeness.

**Puck ships no trademarked game names or likenesses, so every such game gets
the same treatment.** A game keeps its mechanics, takes a generic name, and is
re-themed wherever it still resembles the product. Five games ship under
generic names: `tetromino`, `rulepush` (its player is an imp, and its levels
and word tiles are Puck's own), `hiddenranks`, `wordspy` (with Puck's own
board words), and `paddleball`. A game with a generic name, such as chess, Go,
Reversi, Hearts or Snake, keeps it. A citation of a real product as prior art,
such as an emulator reference or a game named for its feel, is not a shipped
name.

**Every house cartridge has a `.puck` source.** Each source sits under the
package's `cartridges/<target>`, one folder per forge target. The compiled
document is a cache that `puck compile` derives from it when needed and is never
committed ([a derived product is a cache](engine-design.md#a-derived-product-is-a-cache-never-the-truth)).

**Licensed images are run by hand, never by an automated stage.** Their bytes
never enter the repository, and no test, battery stage or CI job depends on one
(AGENTS.md). The ledger describes each by id so a by-hand run can find a
developer's own copy; it records no accepted hash and refuses nothing.

**`roms/` (planned) holds the engine's verification ROMs; cartridges live in the
package that ships them.** The directory does not exist yet; the
[ledger](../plans/runtime-and-delivery.md#the-ledger) creates it. House
cartridges are package content, not evidence, and `roms/` is never copied into a
build, publish or image. The ledger splits firmware by origin: a hand-authored
image is source and its bytes stay committed; a generated image is a derived
product, built on demand and never committed or compared by bytes.

**`bios=` and `firmware.path` stay player inputs.** A player names their own
image by path; a ledger id is one more way to name an image the ledger
describes, so every path that reaches a ROM resolves through one resolver. The
engine never requires an id or a recorded hash to run a player's image.
`tetromino` is an arcade cabinet in the package, not a test fixture: a cabinet
is what gives it a player.

## Compiled worlds

**The file is a chunk container in `PbakBundle`'s shape.** One codec serves
both rather than a second implementation. Its home is `Puck.Assets`, the
content-addressing leaf every producer and consumer
already references, so the container and the content hash each chunk carries
live together. The file is `<name>.puckb` beside its document: the binary
sibling of `.puck`, a suffix no document or source spelling shares.

**The header keys the authored definition; `DEFN` holds the drawn one.** The
definition hash is taken over the composed, undrawn definition's canonical
bytes, so a `.puck` source and the document it compiles to key one compiled
world however each was spelled. `DEFN` stores that definition drawn for the
header's instance, before host overrides, which every boot applies afresh.

**A boot never writes beside its document.** A compiled world beside a document
is build or `puck compile` output; a boot that derives anything writes the whole
compiled world into the per-user compiled-world cache. That cache, like the bake
cache, holds a pure function of the source, the build and the catalog, never a
run's state, so every boot on the device shares it whatever its state root.

**The header records the build; a mismatch is ignored, never adapted.** A
derivation's code is part of its input, so the engine build, catalog
fingerprint, definition hash, and instance identity key the whole file, and
each chunk keys its own inputs. A mismatched chunk is re-derived and the rest
kept; a mismatched header means the file is not a compiled world for this
build. No repair, no adaptation.

**`DEFN` stays canonical JSON in the first version.** The 61 hand-written
converters and the runtime-extended schema make a binary definition encoding a
rewrite of its own, and parsing is the smallest cost in the inventory.

**Live and per-device products are never stored:** GPU objects, machine
instances, mounted addons after `puck_init`, the command registry, adjacency
projections and neighbour solids (a documented nondeterministic boundary), and
the live scene program.

**Trust.** A world received from a peer or from storage validates anyway. A
compiled world lets a local boot skip validation because the compiler
validated under the same build and catalog, but that receipt is a cache key,
not trust.

**Draws.** A compiled world is specific to one instance identity, and spawned
instances share every chunk that does not read drawn cells, so a chunk
declares that dependency.

**`puck.cartridge.v1` gets no compiled form outside a world.** It compiles to a
ROM already; a second form would be a container with no consumer.

## Releases

**These decisions apply after the first shipped release.** Nothing has shipped,
so there is no deployed predecessor to preserve; the release-pair work is frozen
until one exists, and the rules below are scoped to it.

**One active release and at most one retained previous release.** Deploying C
while A is the rollback target for B refuses until B is finalized. All
participating worlds enter a maintenance window together. Rollback takes no
arguments and selects the retained release. Commit is the conservative
recovery boundary: once admission may have opened, recovery never
automatically reverts, and a later rollback is a new maintenance transaction.

**Only pairs qualified to preserve progress in both directions enter the
workflow.** A breaking persistence change is blocked in this version and
needs a separately designed forward transition. Compatibility is bounded to
deployed official artifacts and their retained predecessor, never to
development formats or historically incorrect behavior, and no general shims
enter the engine.

**Release identity is separate from authority identity and mutable state.**
The manifest describes published starting definitions; inventory, identities,
population, clocks, random state, grants, machine state, and transfer receipts
belong to the continuing world.

**A definition edit beyond metadata is a new release**, never a maintenance
deployment, and the preservation rules are written to that boundary. The
authored delta is computed apart from live state, applied only as admitted
changes, and reversed by rollback with the same conflict checks; rollback never
installs the old definition wholesale. Only a metadata change has a preservation
rule today. Under a durable declaration, a deployment that changes a definition
outside its metadata resets that group's hosted state over the drained roots
rather than refusing, until the preservation rules for other edits exist; a reset
root carries the target's definition, no gameplay and no rewind boundary, since
the boundary pins the source's closed inventory, which a reset may change.

**While hosted data is scratch, a deployment replaces the hosted world.** A
deployment group declares whether its hosted data (every world authority under its
owner's private hosted namespace, and its release record) outlives a deployment:
`scratch` or `durable`, in its deployment's configuration. Azure's is
`worldSiloHostedData` in `main.bicepparam`, a required parameter with no default,
set to `scratch` until the owner declares hosted data durable (AGENTS.md rule 5);
the deploy reads it from the live deployment under the group's controller lease
(the latest successful world-platform deployment's output, never the runner's local
outputs file, which a later deployment may have superseded), and an absent output
is durable, so data is discarded only on a positive declaration. Under `scratch` a deployment
qualifies its candidate alone from a fresh bootstrap fixture while the serving world
still runs, then stops the group's compute, discards its hosted data and starts the
release as a first deployment's bootstrap, the path CI's qualification and the
release rehearsal (`puck world release rehearse`) exercise. The drain, protected
roots, in-place reset, pre-commit recovery and rollback run only under `durable`.

The preservation transaction had never run where its defects could be seen before
a deployment found them: four consecutive releases failed in it, each after about
two hours, the last on the reset root's stale rewind boundary. Replacing the world
costs nothing that scratch data is owed and runs the path that is exercised on
every push. It trades availability on failure: a deployment that fails after its
discard leaves the group without a world until a later one succeeds, where the
preservation path would recover the source. A qualification failure still leaves
the serving world untouched, because qualification runs before the discard.

The invariant is that reachable hosted data is deleted only through one door,
`WorldHostedData.DiscardAsync`. Checkpoint retirement deletes journal segments and
checkpoints a landed checkpoint superseded, which no root reaches any longer; that is
garbage collection, not a discard. The door:
- reads the live policy itself, through the caller's provider read, and refuses
  unless it is `Scratch`;
- refuses when the owner's container holds another group's record or discard marker,
  and `WorldReleaseGroupStore.CreateAsync` refuses a second group's record the same
  way, so one owner container holds one group, as it must while world authority keys
  carry no group;
- writes a discard marker before it changes anything and clears it last; while it
  stands `WorldReleaseGroupStore.LoadAsync` refuses the group, so a resume, rollback,
  finalize, durable deploy or silo activation cannot follow a half-discarded record,
  and only another scratch deploy, which repeats the discard, proceeds;
- runs the caller's writer stop before deleting anything; Azure's stop refuses when
  any scale set or VM of another name in the resource group runs under the silo
  identity, the only runtime identity granted the container, and otherwise deletes
  the group's own compute;
- deletes each world's root before the objects the root names, so an interrupted
  discard leaves unreferenced objects and never a root pointing at deleted ones, then
  the group record, then the marker.

The key layouts it deletes by are internal to `Puck.World.Server`. One condition is
the caller's: it holds the group's release controller lease (the deploy acquires it
before reading the policy). A scratch deploy that finds its release already serving
skips the discard only when the record is readable, settled and open and its one
worker exists, so an interrupted discard is never stranded behind that shortcut.
Release content, fixtures, the TLS state and the controller lease are outside the
discarded keys. The storage account versions blobs, so a delete keeps a previous
version: a lifecycle rule (`expire-superseded-hosted-world-versions` in `main.bicep`)
deletes previous versions under the owner's hosted namespace a day after they are
made, which also bounds the versions every root write leaves. The discard does not
delete versions itself, because that needs a version-delete permission the publishing
identity does not hold. When the owner declares hosted data durable, the declaration
changes to `durable` and nothing else does.

**The durable path is rehearsed locally while no deploy runs it.** Four durable
releases failed in the preservation transaction because it had never run where its
defects could be seen, and a scratch declaration means no deploy runs it now. The
rehearsal's durable mode (`puck world release rehearse --mode durable`) runs it on
every use: a reset deployment through the source silo's own `/release/drain` route,
then a deployment whose candidate fails, from which pre-commit recovery must restore
and readmit the source. Its candidate fails by refusing activation (it is started
naming the source release, which is not the pending candidate), because that failure
comes after Activate has replaced the drained roots, so recovery must put the
protected roots back rather than find them untouched. The storage steps over drained
roots have one home (`WorldReleaseRoots`) that the Azure runtime and the rehearsal
share, so the rehearsal proves the code a deploy runs, not a copy of it. What it does
not prove is the hosting half: draining a worker through Run Command, the VM's
replacement and the extension's waits.

**A deploy spends its time only where something changed.** A push that changes no
infrastructure reuses the last platform deployment instead of planning and applying
it again (about nine minutes on the first replace deploy): the input identity is a
hash of the compiled template and the parameters, recorded on Azure's deployment
record and nowhere in the tree, so a hand deployment, a failed one or any input
change applies in full. The skip trusts that nothing outside the pipeline changed
the resources since; an explicit full reconcile (the workflow's `full-reconcile`
input) is the repair when something did, rather than a periodic apply that would
cost every push. Each check of the worker is one Run Command, since a Run Command
round trip costs about half a minute whatever it runs and the first replace deploy
spent ten minutes in about thirty of them; one probe now reads the engine the silo runs, the
release status and private health together. The two legs of a qualification pair
import the same seed into separate copies and processes, so they run side by side.

**A worker's evidence outlives the worker.** A deploy deletes a failed worker, its
journal and its console with it, so the silo writes its logs and narration to a
telemetry sink the silo document selects (`lifecycle.telemetry`), through the same
extension seam as its observer, so the silo and its schema name no provider. The
Azure deployment selects `azure.monitor`, contributed by the provider's world
extension and authenticated as the silo identity; the built-in `file` sink serves
local runs and the rehearsal, which fails unless its sink received the activation
narration. The export holds the bounds the hosted services already use (one
transmission at a time, each bounded, a bounded flush at shutdown) and keeps nothing
on disk, since the disk goes with the worker. Orleans and ASP.NET Core reach the sink
as logs through the silo's logging pipeline. Spans are not exported: nothing in Puck
emits activities, Orleans emits grain-call activities only with propagation turned on
for the whole host, and the request spans the silo would emit are mostly the load
balancer's health probes.

**Uncapturable machine state is not captured.** A pair carrying machines
qualifies by boot-anchored reproduction, so addon guest state, applied screen
operations, live coupled links, and rewind history stay live features rather
than being cut from a qualification fixture.

**Restore refuses what it cannot reconcile.** An in-place restore within a
closed group is supported; later external transfers or durable effects outside
the recovery point's scope refuse it by default. Finalization removes rollback
eligibility and never deletes backup recovery points.

**Pruning defends against cooperating processes and crashes, not against the
install root's own user.** The launcher removes past versions and unreferenced
objects under an install root that only its user and the launcher write. A
process with that user's write access can already replace the binaries the
launcher runs, so an interleaving that needs a hostile process renaming entries
inside the root mid-pass is outside the threat model, and a review finding that
requires one is not a defect. Inside it, pruning must hold four things:
- it never follows a link out of the root (a planted or mistaken link deletes
  nothing outside it);
- it never deletes a version a registered Puck process runs from, and every
  Puck entry point that can run from `versions/<v>/` registers before loading,
  or refuses to start;
- it is crash-safe: an interrupted pass leaves every kept version whole and is
  finished or undone by the next one;
- it fails closed: unreadable or malformed metadata, manifests or records keep
  what they might protect.

Three pre-landing reviews of earlier designs each found the same class of high,
all of it adversarial same-user interleavings; this rule settles the class.

## The player channel

**The consumer build is distributed through Puck's own release chain and
hosting (the owner's ruling), never by copying folders.** What the RTX 2060
test machine and, later, every player runs is a `puck world player` folder
published as a signed `puck.release.manifest.v1` release on the `player`
channel. The installer (`Puck.Launcher.Setup`) installs and updates it through
the ordinary verifier, stager, applier and release record, and the stub starts
it. Content addressing means a release uploads, and an install downloads, only
the files it does not already hold.

**An install pins its channel's root key on first install.** The installer
writes the channel's address, name and root anchor into the install root
(`ChannelSubscription`), and every later run, and every World running from
that install, verifies releases against that pin. The first download is trust
on first use over HTTPS; `--trust <fingerprint>` makes it an explicit pin.
The key's custody is the channel host's (`ReleaseChannelHost`): a deployment
provider keeps it in its own key store, created on first use, and no provider
type reaches the verb that signs (`puck channel push`). A reset of that store
mints a new root, and installs pinned to the old one need a fresh install.

**The prewarmed mesh store comes from a GPU machine, and the release pipeline
releases what it pushes (the owner's ruling).** A hosted runner has no GPU, and
WARP or the CPU audit bake is not the GPU product a consumer would otherwise
derive. Until CI has a GPU runner, the lead machine (RTX 4070) is the interim
producer: it bakes a player folder and runs `puck world caches produce --host
azure`, which writes each backend's meshes into the deployment's own release
store as a cache set named by the build's mesh bake identity (below). The
release job (`player` in the Azure workflow) assembles the folder without
baking boots, and `puck world caches fetch` reads the set whose identity equals
its own build's, checks its plan, and takes every mesh, each verified against
its content pin; the deployment's own identity then publishes the release. When
no set matches, or a matching set was produced for other groups than the
release's world plans, the verb holds the publish (below). A derived product is
never committed
([decision](engine-design.md#a-derived-product-is-a-cache-never-the-truth)),
so the deployment's storage carries it, never a branch. A GPU runner later runs
the same `produce`, under the publishing identity that already writes the
channel, and nothing else changes.

**Cache sets live in the channel's own store, as content-addressed objects (the
owner's ruling: they are hosted in the deployment's storage, not on GitHub).** The store is the
release channel tree that `puck channel push` writes and an install reads: a
channel host (`ReleaseChannelHost`) writes it and a release source
(`IReleaseSource`) reads it, so the verbs name no provider. Each mesh is one
object named by its content pin, in the same `objects/sha256/…` space the
channel's releases use, and each set is one manifest,
`caches/<backend>-<identity>/manifest.json`, listing every mesh's file name, pin
and size with the world plan it was produced for. A mesh is therefore stored
once by its bytes: a new release uploads only the meshes the store lacks, a new
identity whose bake writes the same bytes reuses every object, identical meshes
across backends are stored once, and the release that ships a set finds every
mesh already in place, so its push copies none of them again. An archive per
set would instead upload every mesh again for each release and keep each set
apart from the release that ships it.

The granularity is one object per mesh rather than packs of meshes. The
deciding fact is the channel itself: an install already fetches a release one
file per object, so a mesh stored as its own object is the object the release
names, and a pack would be stored beside it a second time. The cost of many
small objects is round trips, which the fetch bounds by taking many at once
(`ReleaseObjectFetch`), and the edge serves the channel's objects outside its
per-client rate limit. The first complete set measured 11,612 directx meshes
(2.88 GB) and 11,533 vulkan meshes (2.87 GB), about 250 KB each, as 23,145
distinct objects: no mesh's bytes were shared across the two backends, so the
saving comes from releases and identities, not backends. A set's manifest was
2.5 MB, inside the 8 MiB bound a source reads a channel document under
(`ReleaseSourceReader`); a world past roughly three times the debug world's
meshes needs that bound raised or the manifest split, which nothing does yet.

Reads are anonymous. The channel tree is public content behind the
deployment's edge, as it must be for an install, so `status` and `fetch` need no
identity and no role. Writes need the channel host's: the workflow's publishing
identity holds it, and the GPU machine's producer signs in as the developer's
own Azure CLI account, which needs write access to the official-content
container (scoped to `public/puck/official/releases`). The workflow's identity
cannot grant that to a user account, so it is the owner's one setup step
([CI](../development/ci.md#the-player-channel)). The invariant a reader rests on
is that a set's manifest appears only after every object it names, and one door
enforces it: a channel host writes every object before any document
(`ReleaseChannelHost.UploadAsync`), so `status` reads a manifest alone and never
checks the objects. Nothing prunes the store yet; objects only accumulate, as the
channel's release objects already do.

**A cache set fits by identity, never by commit.** The mesh bake identity
hashes the bake's code and kernel bytecode, so every machine that built the
same bake from the same sources computes it. A player can bake (the owner's
ruling, correcting an agent's overstated "a consumer build never derives a
mesh"): a set is a warm start, never a precondition. When no set matches a
build's identity, or a set misses groups the release's world needs, the
`player` job warns and the channel still ships; the player's machine derives
what the channel did not carry, on its play bar, and the lead produces the
set for the next release when it pays. Whether the
documents moved is read without a GPU: a set's record carries the hashes of the
groups its world planned, and the release compares its own world's plan
(`WorldCachePlan`, from the rest scene `puck mesh audit` also plans). The plan
is compared, not looked up in the store, because a live frame keys a posed
character's parts by poses a rest scene does not stand.

**The pipeline says early when a set is owed, and waits for one.** The identity
already follows only what the bake runs (`SdfMeshBakeClosure` walks the code the
bake reaches, and the kernels' bytecode), so a set is owed only when the bake's
code, its kernels or a world document change. Producing it stays a GPU machine's
job, and the time it takes is the bake's. What made it manual was learning of the
need from a held channel after the deploy: the first replace deploy shipped with
its channel held. Every Azure run now checks, with no device, whether whole sets
exist for its own World build and world (`puck world caches status`, which reads
only each set's manifest) and warns on the run's page
without holding anything, so the lead produces the sets beside the validation run.
The player job then waits for a set (`fetch --wait`) instead of failing at once, so
a set that lands after the push ships with that release. A GPU runner that produces
the sets when the check finds none would remove the hand step; it needs a
self-hosted runner on a GPU machine, which only the owner can register.

**A set is whole by construction, and a player's build draws only what is
stored.** A mesh has one store entry for each detail tier, and which tier a
view asks depends on its window size, its distance and its device's memory
budget, so a set baked from one view misses on every other. The alternatives
were a producing boot for each target (it covers one window and one spawn view,
and the first consumer test baked 598 lattices on its first boot and kept
baking on every route), a scripted route through the world (a hand list that
goes stale as places move), and enumerating the tiers offline (a live frame
keys posed parts by poses no rest scene stands). The producer instead runs the
engine's own bake under a ladder policy (`SdfMeshCachePolicy.Ladder`): it bakes
every tier between each drawn mesh's finest and coarsest, at each spawn point
the world's documents author, so no target, distance or budget can ask a tier
the set lacks, and the set no longer depends on which targets it was made for.
A dynamic group's mesh is stored under its form as well as its posed geometry,
so a body at another pose loads it. A player's build then runs the stored
policy: where a tier is missing it draws the nearest stored one rather than
waiting on a bake, and an entry its memory has no room to read is held (the tier it draws
stays) or read again, never baked. Only two things bake. A shape the store holds no tier of does, because nothing
else can draw it: a world whose rules make new static geometry in play (dealt
cards, set type) bakes that geometry on any machine. So does live geometry, a
mesh that reads an operand's pose or a sampled field that moved, which is a
feature of the engine and is counted apart from the cache's misses. The guarantee stops at what the authored spawn points
draw; a residency no such place shows is filled only as far as its frames came.
The whole ladder ships, though most of its bytes are fine tiers of far
geometry no player reaches: bounding a ladder's finest tier needs the nearest
a body can stand to a mesh, and the set is cut to that bound only once
something computes it.

**The fill stands at places rather than enumerating the plan offline.** What a
player's build needs is every tier between finest and coarsest of every
geometry a check boot draws, since window, distance and memory budget can ask
any of them. Enumerating that from the rest scene would need no camera and
would fit a CPU runner, but the debug world shows three things a rest scene
does not stand: its program grows as it runs (dealt cards add instances, and
each growth renumbers the groups after them), a world field's reference takes a
new shape as its water settles, and other worlds' residencies open only where a
place's portal or screen shows them. A boot of the published engine stands in
all three as a player's does, so the fill stays a boot. Its cost is the bake's,
not the visits': in a Vulkan fill of the debug world the spawn's whole-world
ladder took nearly all of the boot and the places after it a few minutes,
so a smaller fill comes from fewer tiers or fewer sets, not fewer places
([open items](../plans/open-items.md#the-player-build-never-bakes)).

**Complete means checked as a player runs it.** `puck world player` boots each
backend and target under the stored policy on the target's device profile,
stands at every authored spawn point, and marks the folder complete only when
every boot settled at every place, missed no tier and baked nothing but live
geometry (`world.counters sdf.mesh-cache`). The invariant is that a check asks
only for tiers the fill produced. The engine names every tier a check asks
that the store lacks (`[sdf.mesh-bake] rung-missed`), and the verb records each
in `player.json` with the place it stood at, the residency, the geometry and
the tiers asked and drawn, so a miss names what the fill did not produce. A
sampled field's reader asked at a field state the world then moves past is live
geometry, like a reader planned again as its field moves: its misses are counted
apart (`SdfMeshCache.RungsMissedLive`), because no cache made beforehand holds
the ladder of a state the world passes through on its way to rest. The folder's
store holds only the build's own sets, one a backend, since the store keeps the
writer's set whole and evicts others past its bound; a set a later fill evicted
fails the folder. An incomplete folder says why in `player.json` and produces
no cache set, so the release job holds the channel as it does for a missing
set.

## Hosting machines

**A hosting machine installs and updates its World exactly as a player's machine
does (the owner's ruling: "there is basically no difference between a client and
a server in Puck ... our own updater/launcher tech is probably the best
choice").** No container, registry, Python or shell bootstrap stands between a
release and the machine that serves it. A server is a World configured to host
plus the silo's pieces, so its release is an install payload like a player
build: `puck world host build` assembles the CLI (which carries the silo)
published with its own runtime, its launcher, the composed hosted worlds and
the deployment's world extensions, and the TLS front when the deployment
exposes the MCP front. `puck publish --channel server` and `puck channel push`
turn it into a signed `puck.release.manifest.v1` release on the deployment's
`server` channel, beside the `player` channel and under the same root key
(`WorldHostLayout` holds every name).

**The engine is the host release's payload identity.** A world release's
manifest names its engine as `engine`, the `ReleasePayloadIdentity` of the
host release it runs: the pin over the payload's files that the install record
already carries and nothing else computes, so re-signing or relabelling a
release keeps it and any changed file changes it. Qualification, the worker
probe and the production check all compare that identity, read from the
install, never a path's spelling.

**A machine installs the exact release its deployment names.** Every signed
manifest a push publishes is also an object in the channel's object space, so a
release stays installable by its pin after the channel moves on
(`PinnedReleaseSource`, the installer's `--release`). That is what lets Azure
replace a machine long after its deployment and still boot the admitted release,
and what lets a controller install a retained predecessor for rollback
qualification: the channel never prunes, so there is no image retention step.
The release's signature still verifies against the install's pinned root, and
each file against its pin. The guarantee stops at the signature's validity
window (`puck channel push --validity`): a replacement after it has lapsed
refuses until a later release is deployed.

**The bootstrap is the installed CLI, reached through the platform's own
primitives.** The VM extension's protected settings carry the installer's
object URL as a file to fetch (the platform downloads it, so the machine needs
no download tool) and one command: check the installer against its pin, install
the release's signed manifest by pin into `/opt/puck/world`, and hand the
protected bootstrap (the retained inputs' pin and the signing keys) to the
installed CLI's `puck world host install` on standard input, so keys never
appear in a process's arguments or environment. The install, as root, reads the
retained inputs through the owner's store, re-checks the boot guard under the
machine's release lock, writes configuration and keys as root-only files,
writes the units and starts them, then waits for private health and the TLS
front and, on failure, ends with a verdict that names its cause. QUIC's
`libmsquic` comes from the host's own package manager (Azure Linux's Microsoft
repository), as any other OS library would.

**systemd runs the server directly, sandboxed.** The units run the installed
version's `puck` (`world host serve`, `world host tls`) and the pinned Caddy as
the system account `puck`, with no capabilities, `ProtectSystem=strict`, a
service system-call filter (failing with `EPERM`), private devices and
temporary files, and the memory and CPU the deployment's `service` sizes.
Secrets reach a unit only as systemd credentials (`LoadCredential`), which only
that unit's processes can read, so the silo never sees the TLS state and the
TLS front never sees a signing key; each unit's state directory is
inaccessible to the others. Write-execute memory stays allowed because the
runtime's JIT needs it. The units do not go through the stub: systemd is the
supervisor, and two supervisors (the stub's launch-attempt ceiling and
`Restart=`) would fight. The account owns the install root, because a process
running from an install registers as a holder of its version, which takes the
root's update lease; that is the pruning decision's threat model, a process
with the install's own write access.

Options weighed:
- Keep the container, minus Python. The registry, the image build and push, the
  Docker daemon's OpenSSL hazard on Azure Linux and a second packaging of the
  same engine would all stay, and none of them is Puck's.
- `DynamicUser`. A dynamic UID cannot own the install's mutable state across
  restarts, and the keeper and the front must share theirs; a static system
  account with credentials and inaccessible paths gives the same separation.
- Run the units through the stub. The stub's selection and launch counting
  serve a player who starts the World by hand; on a server systemd already
  restarts, backs off and stops, so the stub would only add a second policy.

**One implementation of the provider's identity and storage.** The machine's
verbs (`world host install`, `guard`, `probe`, `request`, `tls`) open the
owner's store from the same `type` and `settings` a silo document names,
through the installed world extensions (`WorldHostStore`), so the managed
identity, token and blob access live once in the provider's extension and no
host code names a provider. The guest guard reads the group record through
`WorldReleaseGroupStore.LoadAsync`, the record's one reader, and checks it in
`WorldReleaseGuestGuard`, so no second copy of the record's wire format exists.
The TLS keeper's issuance lease is a store document replaced only by
compare-and-swap on its version token, holding its holder and expiry, so it
works over any store; its expiry is judged on each machine's clock, which
bounds the guarantee by the hosts' clock skew, far below the lease's minute.

**What the pipeline proves without a deployment.** CI builds the host release
on the runtime machines run, signs a copy with a throwaway root, installs it
through `puck world host fetch` exactly as a machine does, and runs that install:
`puck azure test-world-host` (two boots against a fixture store, the second
through the MCP entry point) and `puck world release rehearse --install`. It
does not prove the systemd units, the firewall rules or the host package
install; the first deploy after a change to `WorldHostInstall` does.

## Vendor binaries

A closed component from another company enters a Puck build only where its
licence obliges nobody beyond whoever chose it (the owner's ruling, from the
rendering plan's GPU capabilities round; the licence files were read, not
summarized):

- **Microsoft's Agility SDK** may ship in Windows builds whenever a feature
  needs a newer Direct3D 12 runtime than Windows provides. Its licence makes
  the runtime DLL distributable code inside an application, on condition
  that the application adds significant functionality, its end users accept
  terms that protect Microsoft at least as much as the SDK's own licence (an
  end-user licence agreement), and the distributor indemnifies Microsoft. It
  is restored from NuGet at build time and never committed.
- **AMD's FSR 3.1** is the engine's default optional upscaler: its source is
  open under the MIT licence and it runs on every backend and vendor.
- **AMD's FSR 4 and Intel's XeSS** may ship as engine-optional upscalers,
  each enabled on the hardware that runs it. Both are binary-only, and their
  licences ask only that redistributions reproduce the copyright notice and
  terms and that nobody reverse engineers the binary (the FSR SDK's
  [licence](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/main/docs/license.md); the Intel Simplified Software License in intel/xess),
  so they carry MIT's notice duty plus that ban on reverse engineering the
  binary. FSR 4.1 runs only on AMD
  GPUs (RDNA 2 and later, and listed Ryzen integrated GPUs), its signed
  binaries are Windows DLLs for Direct3D 12 and Vulkan, and the Steam Deck's
  chip is not on its list; XeSS's binaries are Windows-only.
- **NVIDIA's DLSS** is an opt-in vendor package that an author or distributor
  chooses, never an engine part. Its licence requires notifying NVIDIA before
  a commercial release and showing NVIDIA's marks on splash screens, in the
  about box and in a game's credits. Built into the engine, those duties
  would fall on every author who ships a world, which the engine never
  imposes (AGENTS.md, "Authors rule").

Every such component is an authored or distributor's choice with a working
fallback, like any per-target capability. Before a commercial release, each
licence is read in full again.

## The hosted MCP front's certificate

**The certificate is a cache in the owner's store, never world state.** Caddy's
certificate, key and ACME account live under `private/puck/hosted/tls/<hostname>/`,
beside the worlds' authority prefixes and outside them, so a hosted state reset,
which replaces only each world's authority root
(`WorldAuthorityBlobStore.PrepareReleaseResetAsync`), never touches it. A fresh
worker restores it; one hostname issues once (the TLS state keeper, `puck world host tls`,
`WorldHostTlsKeeper`).

**First issuance uses ACME TLS-ALPN-01 through the load balancer's own rule.** A
release boots its one worker on fresh compute, whose NIC joins the load
balancer's pool when the scale set creates it, so the worker that needs a
certificate is the one the 443 rule reaches. The VM extension waits for the
certificate before the release commits, so the commit means the MCP front
answers. This needs only two things: the rule's probe judges that Caddy listens
(not `/healthz`), and every network resource on that path has one owner. The
platform (`main.bicepparam`'s world subnet) owns the subnet and its security
group, the MCP rule included, and the world compute template only references
them. A second owner undoes the first without an error: a platform reconcile
writes the group's whole rule set, so a rule another template adds is gone
until that template is applied again, and a worker that needs a certificate in
between cannot be validated.

Options weighed:
- DNS-01. Caddy would need a DNS-provider module, which means a custom build
  in place of the pinned official release. The worker would also need a write
  grant on the zone, and the provider's name would sit in Caddy's
  configuration, behind no Puck abstraction.
- Issuing from the deploying runner and seeding the store. That needs an ACME
  client in the CLI and a DNS write grant for the runner. It duplicates what
  Caddy already does, and every renewal still has to happen on the worker.
- Committing without the certificate. This would keep a release from
  depending on the CA's availability. It was not needed: the stored
  certificate makes issuance rare, and the front is reachable before the commit
  once one template owns the rule. A CA outage during a first issuance still
  rolls the release back, and the extension's tail names it.

## The deployment's issuer

**The deployment's players sign in at its issuer, and its worlds trust the
issuer's root.** The trust model is
[the federation decision](worlds-and-federation.md#players-are-admitted-through-their-hosts-issuer);
this entry is how a release carries it. The issuer's root key is a deployment
vault secret, `puck-world-issuer-root`. A release generates it the first time it
finds the secret absent, exactly as it generates each world's federation key
(`AzureCommand.LoadSigningKeyAsync`). It reaches the worker the same way the
worlds' keys do, through the protected bootstrap, and the silo unit loads it as
the systemd credential `issuer.pk8`. The release writes one `issuer` member into the
silo document: that key file, plus the deployment's configured sign-in
(`worldSiloConfiguration.authentication`, today `azure.api-users` over the
existing API application, its user group and `user_impersonation`). The world
documents are not touched. The public `world-authentication.json` the release
publishes names the same sign-in and the primary world's transport key
(`remoteKeyHash`), which a player passes to
`--authentication-config-file` with `--connect`.

**Rotation and custody.** Deleting the secret rotates the root at the next
release. Every player is vouched for again at their next sign-in, and no world
definition changes, so no state resets. The root signs only one issuing-key
binding per process start and per week. Its custody matches the worlds' own
signing keys, a file on the worker. Moving it behind a signing service (a vault
key that signs without leaving the vault) changes only how
`WorldAttestationIssuer` signs the root binding, so it is a step for whoever
next tightens key custody, and nothing else depends on where the root lives.

**Rehearsal.** `puck world release rehearse` gives every silo it starts the
same `issuer` member, with the engine's `puck.accounts` sign-in standing in for
the deployment's provider, so a rehearsal proves that the silo starts its issuer
and that its rows trust the root. `puck benchmark --remote issued` proves a
client signing in and joining through an issuer, end to end, on one machine.

## The runtime

**Bare-metal Puck runs on Puck's own host (the owner's ruling).** The
freestanding runtime and platform host supply the engine's required contracts;
hosting stock .NET is not a goal or a fallback. The archived
[BareMetal experiment](../../experimental/Puck.BareMetal/README.md) is prior art
for this work. Missing runtime, memory-management, device, or presentation
contracts are gaps to implement in Puck's host or resolve in the engine, rather
than reasons to introduce a stock .NET host. This direction does not establish
that the current experiment can run the current engine.

**On a hosted OS, Puck plays nice with the host (the owner's ruling).** On
Windows and Linux, Puck builds on the host's primitives (threads and their
priorities, waits and timers, virtual memory, I/O, drivers) to meet its
correctness and performance goals. It does not replace those primitives or
compete with them: it never spins in place of a wait, and never takes cores or
global timer resolution from other programs. The engine's scheduler,
memory account and job system are layers over those primitives. Only the bare-metal host
supplies its own.

**The bare-metal host has no garbage collector, by design (the owner's
ruling).** Porting Puck does not add one. Engine allocations need explicit
owners and lifetimes, with storage reclaimed or reused when those lifetimes
end. GC-dependent cache lifetime and implicit reclamation are engine porting
work; adding allocation alone to the host does not resolve them.

**No new packages; every fold has an existing home.** No rewrite of
`WorldBody`; no change to the name grammar (a namespace is a property of an
import, never a character inside a name); no preservation of retired journal
or tape shapes; no emitted evaluation or bit-packed cells until a measurement
names them; the simulation evaluator is single-homed and the browser hosts the
same engine through WebAssembly.

**Static geometry is baked once into a queryable grid**, fixed-point and part
of the collider census, with the exact program as the narrow-band refinement;
narration is a subscriber outside the core.

**`WorldServer` is split into facades over the partials the state rebuild
rewrote, in the same rewrite**; splitting them separately would have rewritten
them twice. The facades are `WorldDocument`, `WorldTick`, `WorldRuleHost`,
`WorldExtensions`, `WorldGrants` and `WorldPersistence`, and `WorldServer`
keeps its public API, constructor and phase order.

## The presentation view

**Presentation code receives a presentation view, never the authority's
document.** The primary client is handed the authority's own document by
reference: `WorldDocument.DeliverPending` passes its live definition to
`Host.Output.DeliverDefinition`, `WorldOutputHub` fans it out, and
`WorldClient.DeliverDefinition` stores it, so any presentation reader can reach
anything a document holds. A federated neighbour observes the same world through
`WorldProjection.Compose` and `WorldProjection.TryToDefinition` instead. That is
two paths for one question, and the remote one is the path nobody looks at. The
view is one contract with two transports. A remote client's view is materialised
from wire deltas — the presentation manifest's rows, by row version, filtered
per recipient by the existing disclosure door and packed by the conversions
[the state mirror](rendering.md#how-worlds-reach-the-gpu) uses. A colocated
client's view is a zero-copy filtered view over the authority's export, with the
door inside the view, so a reader cannot forget it and the floor device
serialises nothing. A conformance law holds the two together: for every shipped
world, tick, and recipient, the colocated view and a view rebuilt from an
encode-then-decode round trip answer every query identically, so a feature that
works on one transport and not the other fails a test rather than a player. A
client-side door over the raw document is rejected because it keeps both paths,
lets the remote one rot unobserved, and asks every new reader to remember the
door. Real serialisation for the colocated client is rejected because it is one
path paid for every tick on the floor device, and tools need the whole document
anyway. The view's content is the projection's member list, which is the
disclosure decision already written down in the code, plus the manifest's rows
as the recipient may observe them, plus the tick stamp.

**A tool's whole-document read is a separately named capability.** The console,
the inspector, the studio, and a capture legitimately read everything.
"Presentation reads state" and "a tool reads everything" are different types
rather than one reader used with more trust, so a tool takes the capability by
name, the view stays the only presentation surface, and a tool's reach is
visible where it is taken.

**Disclosure is by construction, and row visibility is declared.** What a
recipient may not observe is not given to the code that draws for them, because
a filter a reader has to remember to call is not a boundary. Declared visibility
is also what lets the layout compiler place rows: public rows share one region
and restricted rows get a region per recipient in use. An audience changes only
when a row it reads moves or when the seat filling a slot changes, so permission
is re-evaluated on those two events and a frame reads a settled flag rather than
repeating the check.

## Release evidence

**The language grammar stays separate from the World and cartridge
vocabularies.** Compile, lint, and the language server dispatch from one
schema and report the same refusal at the same span; formatting preserves
literal meaning; decompilation preserves admitted semantics; source macros stay
distinct from runtime cartridge procedures.

**Mandatory physical capacity and advisory performance costing stay
separate**, and the two metadata homes (`CartridgeStateLayout`,
`CartridgeEffects`) are extended where they remove duplication rather than
paralleled by a general-purpose IR.

**Native verification asks three separate questions:** repeated execution
proves repeatability; CGB and AGB compare at normalized authored state on
matched frame boundaries, never raw hardware snapshots; independent expected
results prove both targets did not repeat one mistake. Hashes alone do not
establish authored behavior.

**The pre-first-tick cartridge insertion stall needs a current reproducer;**
without one it is closed.

---

[Decisions](README.md) · [The programme](../plans/runtime-and-delivery.md)
