# The mutation substrate

One pipeline for ALL durable change. A `WorldMutation`
(`src/Puck.World.Data/Protocol/WorldMutation.cs`) is a closed union of nested
sealed records — one coarse record per `WorldDefinition` section, addressed by
stable id, whole-row upsert, never a field poke. A genre world arrives as
different DATA through these same messages, never a new message shape.

## Contents

- The tick (`WorldServer.Step`)
- Applying one mutation (`TryApplyMutation`)
- Whole-document rebuild-and-swap and undo
- The kind catalog
- Adding a mutation kind, end to end

## The tick (`WorldServer.Step`, `src/Puck.World.Server/WorldServer.cs`)

The exact per-tick order, transcribed from `Step`:

1. `WorldAddonRuntime.TickAddons` — run every mounted guest FIRST; decodes and
   validates, applies nothing (a guest's effect never depends on where in the
   tick it was pumped).
2. `DrainPendingOps` — drain the buffered live edits FIFO (mutations,
   whole-document swaps, undo, addon lifecycle), each applying at this tick boundary; deliver
   the new definition to the client sink ONCE if at least one applied.
3. Drain the tick's buffered intents (`m_intents` → `ApplyIntentSubmission`,
   under the per-tick Drive check).
4. `WorldAddonRuntime.ApplyContributions` — the guests' staged contributions
   enter the same `ApplyIntentSubmission` path.
5. `FoldChannelContributions` — fold each human-occupied body's owning-seat
   base with its tick's pooled/unpooled contributions (see
   [authority.md](authority.md)).
6. Settle per-body contention for the tick as a whole (the `m_contended`
   write-back — dequeue order proves nothing about contention).
7. `ResolveEngageProbes`: resolve context-button candidates against pre-move
   positions.
8. `WorldPopulation.AdvanceSimulated`, then `AdvanceSeats`: advance every
   body (stand-ins/peers first, seats second), then apply fired auto-engages.
9. `WorldEventFeed.Collect`: collect settled collision, region, seat, and
   route edges.
10. `WorldAddonRuntime.ResolveReads`: guests' reads resolve against the
   stepped state, so a verdict, a minted handle, and a pose describe the same
   settled instant.
11. `WorldEngagement.FoldTick`: fold routed intents into per-screen pads and
   body-route contributions.
12. `WorldMachineHost.Advance`: step every booted machine directly from
   `WorldEngagement.BuildPadSnapshot()`, with no client or wire round trip.
13. Enqueue body-route contributions through the ordinary intent path for
   the target body's next tick.
14. `EmitSnapshot`: deliver the tick's `WorldSnapshot`.

The shared shell is `src/Puck.World/WorldServerStepShell.cs`: drain pending
TCP work → `WorldServer.Step` → `WorldConsoleWaitGate.PublishTick` (the
`world.wait` clock counts completed simulation ticks) → replay `NoteTick`
when armed. `WorldSimulation` wraps it with seat-intent submission before the
shell and seat-context sync plus analog/editor latching after it. The launcher
owns time; `WorldReplaySnapshot.SimulationRate = 240` is the fixed rate.

## Applying one mutation (`TryApplyMutation`)

Compose → validate → capacity → solids → install (swap + derived rebuild) →
journal → echo. Precisely:

1. **Authority.** The principal must hold `WorldCapability.Mutate` over
   `GrantSubject.Section(SectionOf(mutation))`. A denial prints a
   `[world.grant denied: …]` stderr line, fires a `WorldEditEcho`
   (`Denied: true`), and drops.
2. **Compose.** `TryCompose(current, mutation, out candidate)` builds the
   candidate document as a with-expression. A compose failure (unknown id,
   dangling reference, foreign asset hash) rejects loudly, definition
   unchanged.
3. **Validate.** `WorldDefinitionValidator.TryValidate` over the WHOLE
   candidate — builders and appliers never repeat semantic checks.
4. **Capacity.** If the kind is render-envelope-affecting
   (`AffectsRenderEnvelope`), `m_envelope.TryFit(candidate)` checks the probed
   render envelope — a loud apply-time rejection, never a later GPU
   allocation failure.
5. **Solids.** If the kind is solid-affecting (`AffectsSolidField`), rebuild
   the SDF contact field (`WorldSolidField.TryBuild`) — a solid naming an op
   the warp-free evaluator cannot interpret is a loud apply-time rejection.
   `SetCollision` alone re-wraps the live field with new tuning instead of
   recompiling (`WorldSolidField.WithTuning`).
6. **Install.** Swap the live definition; `Install` rebuilds only the changed
   section's derived state, with a population rebuild when
   `AffectsPopulation(mutation)` (or a field change under the field provider).
7. **Journal.** Append a `JournalEntry(Tick, Mutation)`. The `dirty` count in
   `world.status` IS the journal length.
8. **Echo.** One `[world.mutation: … applied]` stderr line plus a
   `WorldEditEcho` carrying the submitting envelope's
   connection/correlation identity, so a deferred echo routes to its
   submitter.

A mutation's visual effect is a side effect of the delivered definition —
rendering derives from it on revision moves, never from a draw call.

**Timing classes.** Most kinds apply LIVE on delivery. `IsDocumentDefaults`
(`SetRenderDefaults`, `SetPopulationDefaults`, `SetHostDefaults`) edit what
the NEXT boot wakes on while live session levers keep their values
(`world.save` folds levers back into the fields). Two rows split honestly and
the accept echo narrates the split: `SetAuthoringDefaults` (headroom/repeat
caps boot-consumed, candidate/layout/preview live) and `SetPopulationDefaults`
(census figures next boot, spawn policy live for future activations).
`UpsertGrant`/`RemoveGrant` are document-only in a third sense: they edit what
the next boot seeds through `WorldServer.Grant` and never touch the LIVE grant
table — a row added there grants nothing until relaunch.

## Whole-document rebuild-and-swap and undo

- **Rebuild** (`ApplyRebuild`, the `world.reset`/`world.load`/`world.reload`
  path — one `WorldRebuildRequest` closed over `WorldRebuildKind`): resolve
  the candidate and its CAS `sha256-64` content hash FIRST (Reset: the
  server's own `m_base`, hashed fresh via `WorldDefinitionSerialization.
  Serialize`; Load/Reload: the console-resolved document, whose hash the
  console already computed from the exact bytes it read — or, on a REPLAY
  drive, a fresh re-read of the path hint, since the tape carries no
  document) → on replay, refuse BY NAME on a content-hash mismatch before
  anything else runs → `RebuildTap` fires (the replay tape's apply-time
  capture point — see [replay.md](replay.md)) → the principal must hold
  Mutate over EVERY section (`WorldGrants.AllowsAllSections`) → (Load-only,
  unless `force`) refuse while the journal is dirty → whole-document validate
  → envelope `TryFit` → wholesale solid rebuild → `Install` with a full
  population rebuild → journal CLEARS → `WorldGrants.Reset` wipes and
  re-seeds the RUNTIME grant table, the candidate's own `Grants` section
  replays under Console (the identical `WithoutAuthoredConsent`-filtered path
  the constructor and `world.grant` use) → every currently-admitted peer
  connection's admission grant re-mints (see [authority.md](authority.md)).
  Reset targets `m_base` WITHOUT moving it; Load/Reload REPLACE it. Fully
  replay-compatible: the trio rides the tape, CAS-pinned, and no
  longer refuses while a `replay.record` is armed — see
  [replay.md](replay.md).
- **Undo** (`ApplyUndo`, the `world.undo [count]` path): Mutate over every
  section; `count` clamps to `1..journal.Count`. Restores the base and
  deterministically replays journal-minus-tail through the SAME per-entry
  gates a live mutation passes (compose, whole-document validate, envelope,
  solid buildability — everything but the per-entry authority check, which the
  every-section hold already re-proves). ALL-OR-NOTHING: any entry failing any
  gate refuses the whole undo, names the failing entry's index and reason on
  stderr, and installs NOTHING. There is no per-mutation inverse. Battery:
  `docs/verification/undo-all-or-nothing/run.ps1`.
- **Save** (`world.save`): writes the canonical session snapshot (live levers,
  census, runtime screen inserts fold into their document homes) and compacts
  the journal.

## The kind catalog

Every nested record carries `[MutationKind(ordinal, section)]` — the ordinal
is DECLARED, unique, `0..WorldMutationKindCatalog.MaxOrdinal` (= 127, one bit of
the `MutationKindMask` lane). An ordinal past the lane is refused at boot rather
than left to wrap: .NET masks a shift count by the operand's width, so an
out-of-lane bit aliases a REAL kind and would admit the wrong door silently.
`WorldMutationKindCatalog` (`src/Puck.World.Data/Protocol/WorldMutationKindCatalog.cs`)
discovers the set by reflection and `Validate()` fails BOOT loudly on a
missing attribute, an out-of-range ordinal, or a collision.

The lane is `UInt128`. It was `ulong`, and ordinals 0–63 filled it exactly —
that ceiling is what forced the widen, because a 65th kind on a 64-bit lane does
not overflow loudly: `1UL << 64` becomes `1UL << 0` and silently admits
`UpsertKit`. Ordinals 0–63 kept their exact meanings and their exact wire
positions through the widen; nothing was renumbered. Free ordinals exist again,
but a new kind is STILL a substrate decision that must survive consolidation
review first (see "Adding a mutation kind" below) — the ceiling was healthy
pressure against kind-proliferation, and widening it removed an arithmetic wall,
not the design discipline behind it. Regenerate rather than trust this
table — it is a copy of the `[MutationKind]` attributes on `WorldMutation`'s
nested records, which are the authority:

| Section | Kinds (ordinal) |
|---|---|
| Kits | UpsertKit 0, RemoveKit 1, SetDefaultSeatKit 2, SetKitAssignment 3 |
| Screens | UpsertScreen 4, RemoveScreen 5 |
| Cameras | UpsertCamera 6, RemoveCamera 7 |
| Spawns | SetSpawns 8 |
| Motion | SetMotion 9 |
| Properties | SetProperty 10 |
| Population | SetPopulationDefaults 11 |
| Render | SetRenderDefaults 12 |
| Addons | UpsertAddon 13, RemoveAddon 14 |
| Bindings | UpsertBindingOverlay 15, RemoveBindingOverlay 16 |
| Creations | UpsertCreation 17, RemoveCreation 18 |
| Placements | UpsertPlacement 19, RemovePlacement 20 |
| Speakers | UpsertSpeaker 21, RemoveSpeaker 22 |
| Tunes | UpsertTune 23, RemoveTune 24 |
| Patches | UpsertPatch 25, RemovePatch 26 |
| Audio | SetAudioDefaults 27 |
| Authoring | SetAuthoringDefaults 28 |
| Collision | SetCollision 29 |
| Host | SetHostDefaults 30 |
| Views | SetViewDefaults 31, UpsertViewLayout 32, RemoveViewLayout 33 |
| Looks | UpsertLook 34, RemoveLook 35, SetLookAssignment 36 |
| Links | UpsertScreenLink 37, RemoveScreenLink 38 |
| Grants | UpsertGrant 39, RemoveGrant 40 |
| Hud | UpsertHudPanel 41, RemoveHudPanel 42, UpsertHudElement 43, RemoveHudElement 44, SetHudDefaults 45 |
| State | UpsertStateRow 46, RemoveStateRow 47 (whole row), UpsertStateCell 49, RemoveStateCell 50 (one cell), Generate 51 (one draw at a draw SITE) |
| InputHold | SetInputHold 48 |
| Rules | UpsertWorldRule 52, RemoveWorldRule 53 |
| Interactions | UpsertInteraction 54, RemoveInteraction 55 |
| Groups | UpsertGroupKind 56, RemoveGroupKind 57, FormGroup 58, JoinGroup 59, LeaveGroup 60, KickMember 61, OfferOwnership 62, SettleOwnership 63 |
| PlayerDefaults | SetPlayerDefaults 64 |

Rules the catalog encodes:

- **Asset hash pinning.** `UpsertCreation`/`UpsertTune`/`UpsertPatch`
  re-canonicalize the embedded document at the compose boundary and REJECT a
  carried hash the pipeline did not itself compute.
- **No cascades.** `RemoveCreation`/`RemoveTune`/`RemovePatch` refuse while
  dependents reference them, naming the dependents — remove or retarget the
  dependents first.
- **Cross-row transaction.** `UpsertHudPanel` carries its child elements — the
  one whole-panel commit boundary; `UpsertHudElement` is a single-element
  read-modify-write on an already-declared panel.
- **Double authority check.** The five `State` kinds — `UpsertStateRow`/
  `RemoveStateRow` (whole row, 46/47), `UpsertStateCell`/`RemoveStateCell`
  (one cell, 49/50) and `Generate` (51, which names the row it WRITES) — are the
  ONE set checked TWICE by the admission predicate
  (`WorldServer.TryAdmitMutation`): the standard `Mutate`/`section:state` hold
  every kind requires, PLUS a second, row-scoped `Edit` hold over the CONCRETE
  `state:<name>` subject, the SAME subject at both grains (a slot is a table
  with one key, so there is one row and one subject). Narrower authority than
  any other section (the "concrete rows" ruling; see
  [authority.md](authority.md)'s `GrantSubjectKind.State` entry). The
  domain-seeded `Edit/all` reaches every row and cell until narrowed, so this
  is inert by default. Both holds may additionally carry a
  `MutationKindMask` — the Edit one is what separates bumping a row from
  redefining it (`verbs:UpsertStateCell,RemoveStateCell`); `verbs:Generate` is the
  fire-without-redefine hold: it redraws the site but cannot re-author it.

- **The one structural exemption.** `WorldPrincipal.World` — the world's own
  authored program (a `rules` effect, a kit's `generate` effect) — is admitted by
  `TryAdmitMutation` BEFORE the table is consulted, keyed on the principal kind.
  It is not an actor: it holds no grant rows (the grant door refuses one by
  name), the wire refuses it as a submitter, and `world.why world …` reports
  `allowed (structural)` rather than a verdict about a table that was never
  asked. Every non-authority gate still runs. See [authority.md](authority.md).

- **A draw site's own bookkeeping.** `Generate` names ONE row — the SITE — and
  writes its drawn slot cell together with the site's own `drawCursor` (plus
  `drawDecks` under a deck mode) in one candidate. The row is the authority
  subject; the cursor advance is engine bookkeeping intrinsic to drawing, while
  re-authoring the site's facet, or the `generators` row it references, is an
  `UpsertStateRow` against that row, gated there. Sampling itself lives in
  `Puck.World.Data/WorldGeneratorEngine.cs` because the BOOT resolver — which
  runs before any server exists — must reach the identical code.

## Adding a mutation kind, end to end

**FIRST — the catalog declares 65 kinds (ordinals 0–64) on a 128-bit lane.**
Ordinals 65–127 are free; a colliding ordinal is still a boot failure, not an
option. A genuinely new kind is
a SUBSTRATE decision, not a lane's, and must SURVIVE CONSOLIDATION REVIEW first:
is this an existing kind's payload? Most proposals are — a new section reuses
`UpsertStateCell`, a rule effect reuses an existing kind. That review is the
gate that matters and it did not go away when the lane widened.

The lane has already been widened once (`ulong` → `UInt128`), so a new kind no
longer needs one. Should it ever fill again, the widen is ONE dedicated substrate
commit — mask type + grant wire codec + document serialization +
`world.grants`/`world.why` echoes — never inside a feature lane, and the proof
has two halves that must BOTH be present: dual-run byte-identity over the
existing ordinals (necessary, but it passes on a codec that silently truncates
the new range), AND a control exercising a bit past the old ceiling across the
wire. The width is an implementation detail, not protocol: authored grants name
verbs BY NAME and the ordinal is an internal dense index, so widening (or
re-packing to reclaim a retired ordinal) is mechanical under supergreen. Only
past the consolidation gate do the steps below apply.

1. **Data:** the nested sealed record on `WorldMutation` with
   `[MutationKind(ordinal, section)]` (the ordinal claimed by the widening
   above). XML-doc the row semantics (rejection conditions, timing class) in the
   same style as its neighbors.
2. **Server:** an arm in each of these `WorldServer` switches — `TryCompose`
   (compose the candidate) and `SectionOf` (which THROWS on a missing arm
   rather than mis-authorizing) — plus membership in the classification
   predicates that apply: `AffectsPopulation`, `AffectsSolidField`,
   `AffectsRenderEnvelope`, `IsDocumentDefaults`, and a `Describe` arm.
3. **Validator:** whatever whole-document invariant the new row needs lives in
   `WorldDefinitionValidator`, never in the apply arm.
4. **Console:** a verb in the owning command module submitting
   `WorldSubmissionPayload.Mutation`. Row-valued verbs take one inline-JSON
   argument in the exact wire shape of the section row.
5. **Read-back, same change:** no new decision surface lands without a verb
   that echoes it — a decision nothing can echo can only be asserted through
   downstream inference.
6. **Sweep shipped worlds** if the schema changed (strict parse turns
   stragglers into boot refusals — see [documents.md](documents.md)).
7. **Verify by running** (see the SKILL.md recipes): drive the verb over
   stdin, read the read-back, exercise one rejection path, and check
   `world.undo` restores.

A kind's CONSOLE reachability (steps above) is independent of its ADDON
reachability: a guest submits a mutation through a Mutate handle's own
hand-walked JSON door, `Server.WorldAddonMutationDecoder`, which wires only a
NAMED SUBSET of the 64 declared kinds today (10, as of this writing — the 5 HUD
kinds plus the 2 placement kinds, the 2 state kinds, and `SetInputHold`; the
Properties/Interactions/Groups kinds are console-only, not addon-reachable; see
[addons.md](addons.md#requests-queries-verdicts) for the exact list and the
decoder's own division of labor against the validator). Adding a kind here
does not make it addon-reachable — that is a separate, optional `case` arm in
the decoder, additive by the same discipline this section already follows.
