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
only: shaders, fonts, overlays and probe kinds, each
owned by the project that uses it. Document schemas live with the document
model (`src/Puck.World.Schema/Schemas/`) and ship in the CLI package, because
only CLI tooling reads them. Worlds, cartridges, music, addons and art belong
to a package. Test fixtures live with the tests that read them.

**Existing pipelines take a package; none is added.** `puck compile --tree`,
`puck world prepare`, `puck official build --worlds` and the silo image each
take the package folder, and the release publish compiles the package beside
the published engine.

**Planned, not implemented.** A package's manifest supplying the window title
and icon, which a world may not override; channels; the release manifest
pinning package content; and the desktop artifact carrying no content. A
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

The removed World recording integration encoded native video and audio; it was
not a replay system and had no debug-world caller. Its boot flag, default asset
and capture verbs are deleted, together with their orphaned native A/V backends
and registration helpers. Audio output keeps its shared WASAPI interop under
`src/Puck.Platform.Windows/Audio/`. The reusable `Puck.Recording` library remains in
the build with typed `RecordingConfiguration`; its orphaned JSON schema, reader,
serialization context and extension-data bag are deleted. `WorldProbes`,
`probe.*` and probe tracks remain for the stretch goal.

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

**Every house cartridge has a `.puck` source.** Each sits beside its compiled
document under the package's `cartridges/<target>`, one folder per forge target,
and `puck compile` reproduces the document byte for byte.

**Licensed images stay usable as evidence, recorded by hash only.** Their bytes
never enter the repository; the ledger records id and SHA-256, and the battery
refuses an image whose hash matches no accepted value.

**`roms/` holds the engine's verification ROMs; cartridges live in the package
that ships them.** House cartridges are package content, not evidence, and
`roms/` is never copied into a build, publish or image.

**The ledger governs `bios=` and `firmware.path` too.** They are player-facing
inputs for a player's own images, but one home for every path that reaches a
ROM is worth more than the exemption. `tetromino` is an arcade cabinet in the
package, not a test fixture: a cabinet is what gives it a player.

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

## The runtime

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
