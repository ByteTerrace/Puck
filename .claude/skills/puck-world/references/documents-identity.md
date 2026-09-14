# Identity conventions and owned-world identity documents

Part of [`puck.world.def.v1`](documents.md). See
[documents-state.md](documents-state.md) for `state.identity` row mechanics.

## Identity conventions

- Screens are POSITION-ADDRESSED by `WorldScreen.Index` (an engine
  screen-surface index); the derived `WorldMachineCableGroup.Screens` and
  `WorldSpeakerSource.Machine` key off the same int — screen index IS machine
  identity for screen-hosted machines. Cable linking itself is authored
  per-machine: a `Machine` source's `cable` port (`WorldMachineCable` — name +
  position), never a row of its own; `WorldDefinition.MachineCableGroups()`
  derives the groups.
- Everything else is string-addressed: stable ids (`WorldSceneRow`,
  `WorldCreation`, `WorldPlacement`, `WorldSpawnPoint`,
  `WorldBindingOverlay`, HUD panels/elements, profiles) or names
  (`WorldCamera`, `WorldKit`, `WorldLook`, `WorldChannel`, `WorldSpeaker`,
  `WorldAddonRow`, `WorldViewLayout`, `WorldStateRow`).
- Spawn points carry both modes deliberately: `Id` is the mutation address,
  but LIST ORDER is seat identity (seat n spawns at `SpawnPoints[n]`).
- Grant rows are keyed by their `(principal, capability, subject)` triple —
  a grant IS that triple (`Exclusive` and the co-drive fields are row data,
  not key). `GrantSubject` and `WorldPrincipal` serialize as the console
  grammar tokens through their own converters (`all`, `body:<n>`,
  `screen:<n>`, `section:<name>`, `state:<name>`,
  `region:<name>`, `seat:<n>`, `creation:<id>`, `placement:<id>`;
  `seat1..seat4`, `console`, `addon:<name>`,
  `peer:<index>:<generation>`, `document:<id>`). A member-wise serialization would permit denormalized
  "phantom grant" keys no table lookup could match. Two asymmetries to know:
  `composition` is a write-only subject token (echoed by `world.grants`,
  rejected on read — only the boot seed constructs it). A peer identity is
  always generation-bearing; reusing an index after disconnect mints a new
  token and cannot inherit the previous generation's grant key. `region:`/
  `seat:` are the world-events feed's subjects (Observe-only, untrusted
  principals only — see references/addons.md's "World events" section);
  `WorldPlacement.Region` (`WorldPlacementRegion`) is the document-side
  facet a region name addresses — a sphere on the placement's own position,
  keyed by the placement's own `Id`. `WorldPlacement.Attach`
  (`WorldPlacementAttach`, new) is a placement's BODY-ATTACHMENT facet — a
  0-based `body:<n>`-indexed target plus a local offset rotated into the
  body's own frame. It derives TWICE off the one authored facet: the
  authoritative fixed-point resolve
  (`Server/WorldPlacementAttachment.TryResolve`, called on demand by
  `world.attachments` and every active tick for attached gravity areas), and
  the rendered pose —
  presentation float over the client's INTERPOLATED body pose, packed every
  frame by `Client/WorldStampPool.cs`. Riding the interpolated pose is what
  keeps an attached row as smooth as its body; reading the authoritative
  resolve in the renderer would judder at the tick rate. An attached row
  draws through the reserved stamp pool, never as a static stamp
  (`Client/WorldPlacementStamper.IsStaticStamp` is the one fork), so it
  charges `WorldPlacementPolicy.MaxStampRegistrations` alongside animated
  rows and its authored `Position`/`YawDegrees` are inert. `Region`, `Solid`
  (under the analytic contact provider), and `Emission` no longer refuse
  alongside `Attach` — each now reads the resolved DYNAMIC pose instead of
  the row's static transform (`Server/WorldEventFeed.CollectRegions`,
  `Server/WorldColliderSet.RefreshAttached`,
  `Client/WorldStampPool.TryShapePosition`/`RootPose`), so an equipped item's
  aura/hitbox/voice tracks its carrier; an inactive carrier makes the facet
  contribute/sense/sound nothing, the same verdict the render stamp already
  had. `Distribution`/`Mirror` (static-stamp-only) and `Inhabit` (a row
  cannot both spawn its own bodies and ride another's) still refuse by name
  rather than blend, and `Solid` still refuses under the FIELD contact
  provider (it compiles every solid row's geometry once into one SDF
  program, never rebuilt per tick — `collision.requirements` non-empty). An
  out-of-range `BodyIndex` refuses at author time; a valid but
  inactive/despawned target body makes the row contribute nothing at
  RUNTIME — no refusal, `world.attachments` names the reason and the stamp
  parks below the floor. `WorldPlacement.Contribution`
  (`WorldPlacementContribution`) is the CONTRIBUTION SLOT facet — a host world
  authors the frame and a federation partner fills it. Its two halves never
  mix: `tenure` (`Presence`/`Endowed`), `slotCreationId`, `link` (an
  `adjacencies` row name, required for `Presence` and refused for `Endowed`)
  and `graceSeconds` are AUTHORED; `contributor` and `retractDeadlineTick` are
  SERVER-STAMPED and a submission naming either is refused BY NAME
  (`Server/WorldServer.Contributions.cs`'s `TryComposeUpsertPlacement` reads the
  contributor off the acting principal — accepting an authored one would be the
  laundering the acting-principal rule forbids). An UNFILLED slot shows its own
  `slotCreationId`, so no creationless placement has to be representable, and
  the validator pins the pair. Retraction — the per-tick
  `SweepContributionTenure` pass, once the watched link has read dropped past
  `graceSeconds` — re-points `creationId` back and clears the stamp through
  ordinary journalled mutations, so the host's FRAME stands, only the piece
  goes, and `world.undo` puts it back. It defers (never orphans) while the
  slot's inhabitant is drive-possessed. Read back with `world.contributions`.
  `WorldPlacement.Respond` (`WorldPlacementResponse` rows) is the RESPONSE
  facet — a state-driven prototype swap: an ordered `{when, prototypeId}`
  list, each `when` the SAME `WorldFieldCondition` grammar a
  `fields.reactions` Transform/Expose condition uses, tested at the
  placement's own coupled lattice cell
  (`Puck.Physics.Fields.FieldLattice.TryBodyCellOf`) by the per-tick
  `SweepPlacementResponses` pass — run right after the field lattice steps,
  so it reads THIS tick's own writes. Entries try in authored order; the
  FIRST whose condition holds wins, through an ordinary `UpsertPlacement`
  under `WorldPrincipal.World`; when none holds the row is left exactly as
  it reads — the facet only ever SELECTS on a match, it never reverts a
  prior swap. Refused alongside `Attach`/`Inhabit`/`FaceSources`; every
  candidate prototype (the row's own base and every entry's) must resolve
  to a declared, non-animated creation, and the analytic solid-collider
  ceiling counts the WORST CASE across every variant the row could show.
  Read back with `world.responses`.
  `WorldPlacement.Deal` (`WorldPlacementDeal`) is the DEAL facet — the row is
  a template whose children are dealt from a keyed `state.world` row of any
  cell kind, one child placement per cell named `<template>/<cellKey>`,
  parented to the template at an offset of its own `distribution` region
  (Lattice/Noise/Scatter only; the region must materialize at least the
  row's capacity), carrying the template's prototype and `solid`/`grip`/
  `region`/`emission`; `deal.variants` maps a second keyed row's same-keyed
  cell text to a prototype. The per-tick `SweepPlacementDeals` pass
  (`WorldServer.Deals.cs`, right after the response sweep) lands children as
  ordinary placement mutations in one `Batch` under `WorldPrincipal.World`:
  a child keeps its offset while its cell is present, a departing cell frees
  its offset and moves no sibling, an arriving cell takes the lowest free
  offset, and the sweep only re-deals when the row, the variant row, or the
  template changes — so `world.undo` of a deal stays undone until the row
  moves. The template renders and collides as nothing; an authored id
  spelling `/` refuses by name. Read back with `world.placements` (`dealt
  from <row> (<n> of <capacity>)` / `dealt by <template>`); `world.budget`
  counts every template's offsets. The granaries module
  (`modules/granaries.world.json`) is the worked example.

## Owned-world identities

World/owned-world ids (`Server/WorldOwnedWorlds.cs`) and `world.instance.start`
names are `SafeName` (`Puck.State/SafeName.cs`) — the reserved-character
kernel `CellName` shares, plus a bare `"."`/`".."` refusal instead of the
dot-free rule; `WorldOwnedWorldFileName.For` takes a `SafeName` and escapes
nothing, so the id→file-name mapping is injective into file-name STRINGS — but
not into storage LOCATIONS, since the catalog directory resolves names
case-insensitively. One id names one location only under the separate
**case-insensitive uniqueness** rule the two admitting doors hold (the seed-list
validator and `WorldOwnedWorlds`).

An identity is an ordinary owned `WorldDefinition` document, not a catalog
row: `WorldOwnedWorlds` (`Server/WorldOwnedWorlds.cs`) is the CATALOG (seats
select identities from it; a seat's profile IS a `WorldIdentity` wrapping one
owned document), one file per identity under the local state directory,
named `WorldOwnedWorldFileName.For(id)` (`"<id>.world.json"`). Every id is a
`SafeName`, so the mapping escapes nothing and is injective into file-name
STRINGS — but a string is not a storage location, and the catalog directory
resolves names case-insensitively, so **ids are unique IGNORING CASE**. That is
the rule the seed-list validator holds (a case-variant pair refuses at
validation) and the rule every id comparison in the catalog holds
(`FindById`, `Create`'s collision guard, `ReplaceFromSync`'s match, and the
file-name check). A loaded file whose name does not match
`WorldOwnedWorldFileName.For` of its OWN declared identity `id` — ignoring case,
so a case-only rename of a catalog file is ADMITTED and keeps the name it
carries — is refused by name (`[identity] owned world refused: …`,
distinguishing "the name another file in this directory carries" from "a name
no file in this directory carries") rather than silently renamed or merged —
that document parses, so it stays where it is and the refusal names the remedy.

A document the loader refuses is handled by the CLASS of the refusal, and no
refusal is ever a hard boot failure. Only a verdict on the BYTES — the
`{path} is not a valid puck.world.def.v1 document: …` and `cannot decode …`
classes, which include a document with no `identity` section — is DISCARDED:
the file moves into the `unloadable/` subdirectory (outside the catalog's
`*.world.json` top-directory glob, like `basis/`), once, so the next boot has
nothing left to refuse. Every other class can answer differently on the next
boot — `cannot read …` (locked or half-written), `no file at …`, `… basis
composition refused: …` (a chain link not placed yet), `… document validation
refused: …` (which may rest on an adjacency neighbour the sweep is itself
moving) — so those files STAY where they are and are only named. Quarantining
them would cascade: the neighbour resolver reads the same directory the sweep
empties, and the seeding pass would write defaults over every freed name.

Each half narrates as ONE stderr line — `[identity] discarded N unloadable
owned world(s) into '…'` and `[identity] refused N owned world(s) this boot
could not read …` — grouping file names by their shared reason, with the path
itself stripped out of the reason so one fault across a directory reads as one
group and a lone corrupt file stands in a group of its own. There is no
migration and no read-side tolerance for a retired shape: an emptied catalog
re-seeds from `playerDefaults.identities`, and a catalog that still holds
documents simply lacks the discarded ids.

Retention is exact on both sides. A quarantine destination that is already
taken (deterministic file names, and the catalog re-seeds a freed name) takes
an ordinal suffix rather than overwriting the earlier copy, and the seeding
pass SKIPS any seed id whose catalog path still holds a file or directory — so
a disposal whose move failed, or a document left in place, keeps its authored
bytes for the next boot instead of being replaced by a fresh default, and a
directory occupying a deterministic file name cannot crash startup.
`identity.create` refuses an id whose catalog path is occupied for the same
reason, reading the DIRECTORY rather than the identity list — a boot that
admitted nothing leaves that list empty while the bytes are still on disk. Read
back with `WorldOwnedWorlds.Discarded` + `identity.list`'s `discarded=` column
(disposals — the moved bytes stay readable under `unloadable/`) and
`WorldOwnedWorlds.Refused` + `identity.list`'s `refused=` column (everything
left in place, whatever the class).

**Seeding.** When the identity directory holds zero admitted documents,
`WorldOwnedWorlds` seeds one owned world per `playerDefaults.identities` row
(`WorldIdentitySeed(Id, Name, Color)`, validated non-empty, ids and names both
unique ignoring case, hex color — `ValidatePlayerDefaults` in
`WorldDefinitionValidator.cs`) and persists each immediately.

**`WorldIdentity`** (`Puck.World.Schema/WorldIdentity.cs`) is the runtime
handle over one owned document's `identity` section
(`WorldIdentityDefinition(Id, Name, Color, MoveSpeedState, TurnSpeedState,
Controllers, Voice, Facts)`): `MoveSpeed`/`TurnSpeed` read
and write the owned document's OWN `state` rows named by those state-row
references; `Bindings` is the owned document's own first `bindingOverlays`
row's document (the seat's profile binding layer — see
[documents-binding.md](documents-binding.md)); `Hud` is the owned document's first `Hud` panel (the identity's
PRIVATE seat-scope HUD panel, see `hud.md`). `Voice` (`WorldVoiceProfile?` —
a `PatchId` resolving against declared `patches` rows, plus a positive
`CadenceTicks`) selects the identity's synthesized voice-babble pitch/timbre
and cadence. `WorldAudioDirector.TriggerBabble` reads it, drives
`Puck.Audio.Simulation.VoiceBabbler` for the cadence-jittered per-syllable
trigger schedule, resolves the patch, and fires one seeded trigger per
syllable under the `voice.babble` cue token (`voice.state`/`voice.babble` are
its read-back/debug-trigger verbs). Two things stay open: no producer yet
estimates an utterance's syllable count from dialogue/caption text, and a
babbling identity has no live-body correlation, so every syllable voices
listener-placed rather than at a resolved world position.

**Facts.** `Facts` (`WorldIdentityFacts {state, capacity}`, defaulting to
`identity-facts` holding 64) names the keyed `int` row of facts the identity
carries on its own document — minted on the first write, capacity-bounded,
validated as that one number. A world receives them only through a reserved
keyed `int` row named `identity` it declares in `state.world`
(`WorldIdentityFactLane`; cells keyed `<bodyIndex>-<fact>`), loaded into a
body's cells when a seat binds the identity and zeroed when it unbinds. A rule
writes one with the `setIdentityFact` effect (`{key: <body>, fact, value |
expression}` — lane and identity row together, the identity persisted through
`WorldOwnedWorlds.TrySetFact`) and reads one through `$identity:<bodyRef>:<fact>`
(0 when never written or no identity drives the body); a world declaring no
lane refuses both by name at compile (`IdentityLaneUndeclared`), and a body
driving under no owned identity refuses the write at fire time
(`IdentityUnbound`). `identity.facts [player]` echoes the identity's row;
`identity.fact.set <key> <value> [player]` writes one from the console (a seat
principal only its own seat's); `world.state identity` echoes the lane.

**Seating.** `SessionRequest.SetIdentity` (gated on `WorldCapability.Drive`
over the targeted slot's body — the same grant `Join`/`Leave` use) sets a
slot's participant to a named owned identity.

**Cross-document durable state.** A body's authored durable-state writes
(`WorldPopulation.DurableStateOutputs`, drained every tick in
`WorldServer.Step`) submit against the OWNER identity's own `state` rows
through `WorldOwnedWorlds.Submit`/`Decide` — gated by a `Mutate` grant the
owner's OWN document declares for the writing document's principal
(`WorldPrincipal.Document(sourceDocumentId)`) over `state:<slot>`; refusals
name a missing source id, an unknown owner, a missing/unknown slot, the
absent grant, the wrong storage kind, an out-of-envelope or negative value,
or overflow. `Save()` re-serializes the owner through
`WorldDefinitionSerialization.Save` to its own file on every accepted write;
`ReplaceFromSync` adopts a pulled cloud copy the same way.
