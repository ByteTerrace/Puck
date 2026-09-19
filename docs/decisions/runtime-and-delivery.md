# Runtime and delivery: decisions

The choices behind [the runtime and delivery programme](../plans/runtime-and-delivery.md),
each with the problem it answers and what follows from it. Current behavior
lives in [the server README](../../src/Puck.World.Server/README.md) and
[the deployment guide](../development/ci.md); a decision here is why the
contract has the shape it has.

## Products

**The engine ships no content.** `Puck.World` and its siblings ship engine
data only: shaders, fonts, overlays, probe kinds, document schemas, each owned
by the project that uses it. Worlds, cartridges, music, addons, and art belong
to a product. No second product exists yet, so nothing but review proves the
build is product-neutral, and a Puck-specific name in a build verb is a defect.

**The unit is a product, rooted at `content/<id>/`.** "Client",
"distribution", "channel", "title", and "identity" were all taken by existing
meanings. The official world is `content/puck/`. A product's internal folders
are its own choice; the official layout is not a requirement.

**The manifest is the contract.** The build reads `product.json` to learn what
a product contains and never infers content from folder names, because
products are often authored with outside tooling.

**No product, no boot.** Launched without a product or world, `Puck.World`
refuses and names what it needs. There is no hidden default world.

**Existing pipelines are extended, not joined by a new one.** Eight pipelines
package, bundle, or release something; four carry content and select it three
different ways under two manifest schemas. The manifest replaces the
selection; the pipelines stay.

**The packaged unit is the product tree; a compiled world is a file inside
it.** `product.json` declares identity, branding, channels, engines, and every
document and asset, which is what a release manifest must pin and an operator
deploys. A compiled world holds one world's derived products keyed by the
build that derived them: a cache with a strong key, not an identity. Making
the cache the unit would force the release manifest to name a file whose
contents change with every engine build and leave branding, cartridges, and
addons with no home. Shipped worlds carry compiled worlds as build output
named in the manifest under their own family, and the runtime cache still
writes one on a miss.

**The manifest supplies the window title and icon**, and a world may not
override them; branding is a property of the product a player installed, never
of the document it happens to be showing. `content/puck/branding` holds copies
the build makes from `branding/`, which authors the assets once. Document
schemas ship in the CLI package, because only CLI tooling reads them. Signing
and upload stay outside the programme; neither exists.

## ROMs

**The Tetris cartridge keeps its game and becomes `tetromino`.** Tetris is a
trademark with no generic name the way chess has one; a tetromino is the
mathematical name for its pieces. Every tie to the commercial reference image
is cut, and the rename is accompanied by a re-theme, because renaming removes
the trademark but not the likeness.

**Every house cartridge has a `.puck` source.** `pip.agb` gets one by
decompiling its document, reproduced byte for byte through `puck compile`.

**Licensed images stay usable as evidence, recorded by hash only.** Their bytes
never enter the repository; the ledger records id and SHA-256, and the battery
refuses an image whose hash matches no accepted value.

**`roms/` holds the engine's verification ROMs; cartridges live in the product
that ships them.** House cartridges are product content, not evidence.

**The ledger governs `bios=` and `firmware.path` too.** They are player-facing
inputs for a player's own images, but one home for every path that reaches a
ROM is worth more than the exemption. `tetromino` is an arcade cabinet in the
product, not a test fixture: a cabinet is what gives it a player.

## Compiled worlds

**The file is a chunk container in `PbakBundle`'s shape.** The codec exists;
it moves to a shared home rather than gaining a second implementation.

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

**The facade split rides the state rebuild's WP11b**, which rewrites the same
partials; splitting them twice would mean rewriting them twice.

## The presentation view

**Presentation code receives a presentation view, never the authority's
document.** The primary client is handed the authority's own document by
reference: `WorldDocument.Apply` passes its live definition to
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
