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
`puck world prepare`, `puck official build --worlds` and the silo image each
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
installs the old definition wholesale.

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
producer: it bakes a player folder and runs `puck world caches produce --push`,
which packs each backend's mesh set into a cache set named by the build's mesh
bake identity and uploads it as the asset of a GitHub prerelease tagged
`caches/<backend>-<identity>`. The lead creates those tags and prereleases
(the owner's ruling); a lane never does, since a lane never pushes. The
producer needs no deployment access. The
release job (`player` in the Azure workflow) assembles the folder without
baking boots, and `puck world caches fetch` takes the cache set whose identity
equals its own build's, verifies every file's content pin and unpacks it; the
deployment's own identity then publishes the release. When no set matches, or a
matching set was produced for other groups than the release's world plans, the
verb holds the publish (below). A derived product is never committed
([decision](engine-design.md#a-derived-product-is-a-cache-never-the-truth)),
so a release asset carries it, never a branch. A GPU runner later runs the same
`produce --push`, and nothing else changes.

**A cache set fits by identity, never by commit.** The mesh bake identity
hashes the bake's code and kernel bytecode, so every machine that built the
same bake from the same sources computes it. A consumer build never derives a
mesh (the owner's ruling: the consumer machine runs what a player runs and
never cold-bakes), so a set that misses any group the release's world needs,
whether its identity moved or its world documents changed, does not ship: the
`player` job holds the channel's publish for that build and names the miss,
and the lead produces the set before the release is retried. The job fails and
the deploy does not; the channel keeps its last complete release. Whether the
documents moved is read without a GPU: a set's record carries the hashes of the
groups its world planned, and the release compares its own world's plan
(`WorldCachePlan`, from the rest scene `puck mesh audit` also plans). The plan
is compared, not looked up in the store, because a live frame keys a posed
character's parts by poses a rest scene does not stand.

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
policy: where a tier is missing it draws the nearest stored one and never bakes
it, and an entry its memory has no room to read is held (the tier it draws
stays) or read again, never baked. Only a shape the
store holds no tier of bakes, because nothing else can draw it: a world whose
rules make new static geometry in play (dealt cards, set type) bakes that
geometry on any machine. The guarantee stops at what the authored spawn points
draw; a residency no such place shows is filled only as far as its frames came.

**Complete means checked as a player runs it.** `puck world player` boots each
backend and target under the stored policy on the target's device profile,
stands at every authored spawn point, and marks the folder complete only when
every boot settled at every place and dispatched no lattice, baked no group and
missed no tier (`world.counters sdf.mesh-cache`). An incomplete folder says why
in `player.json` and produces no cache set, so the release job holds the
channel as it does for a missing set.

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
