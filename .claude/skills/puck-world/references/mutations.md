# The mutation substrate

One pipeline for ALL durable change. A `WorldMutation`
(`src/Puck.World.Protocol/Protocol/WorldMutation.cs`) is a closed union of nested
sealed records — one coarse record per `WorldDefinition` section, addressed by
stable id, whole-row upsert, never a field poke. A genre world arrives as
different DATA through these same messages, never a new message shape.

`Batch` can carry a full `expectedDefinition` fingerprint, named placement/state `expectedInputs`, bounded
three-dimensional `expectedSpatialReads`, and numeric `expectedCells` comparisons evaluated at commit time.
Spatial reads include referenced state values and catch new or moved blockers entering the region.
Each cell can also require an exact post-composition `change` and `kind`; failure discards the whole candidate.
Observation grants cover guarded rows. Members must carry the enclosing principal, checked at codec and server
admission. Reflow uses this ordinary batch and ordered submission path; Immediate preview starts bounded background work,
status reviews detached positions and price, and Simulation commit submits the resulting payload. A preview is
not a simulation write. Do not replace that submission with direct server enqueue in the console: the link is
what records the accepted payload for replay.
Successful base rebuilds clear deal sweep memos so a reset or live replay materializes children even when
the replacement inventory equals the prior session's. Ordinary edits and undo retain their reconciliation semantics.

Rule-driven state, placement, and HUD mutations share one firing-order queue. Document effects validate a
speculative candidate including earlier queued writes; scope rollback discards its entire tail. The end-of-rule
fold installs through the ordinary mutation door once. Keep this boundary intact when adding an effect; see
the [rule-effects-land-on-the-arena contract](../../../../src/Puck.World.Server/README.md#rule-effects-land-on-the-arena-worldrulehostcs-worldserverarenacs-worldrulehostarmscs).

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
   whole-document swaps, undo), each applying at this tick boundary; deliver
   the new definition to the client sink ONCE if at least one applied. An
   `UpsertAddon`/`RemoveAddon` mutation carries its own addon-prepare gate —
   the LAST fallible step before install — see [addons.md](addons.md).
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

The shared shell is `src/Puck.World.Server/WorldServerStepShell.cs`: drain pending
QUIC work → replay `InjectDriveTick` (a no-op unless a live drive is in
progress — see [replay.md](replay.md)) → `WorldServer.Step` →
`WorldConsoleWaitGate.PublishTick` (the `world.wait` clock counts completed
simulation ticks) → replay `NoteTick` when armed, looping for a
fast-forwarding drive's burst. `WorldSimulation` wraps it with seat-intent submission before the
shell and seat-context sync plus the per-tick analog clear after it. The launcher
owns time, pacing off `IFixedStepSimulation.RatePerSecond` (authored per world
via the document's `simulation.rateHz` field — the shipped worlds author 30 Hz
themselves, and a world authoring no section is rate-0 resident; see
[documents.md](documents.md)). A `.puckreplay` tape carries its OWN
`SimulationRate`, stamped at record time from the live world's own rate, and
`Drive` refuses a disagreement with the embedded definition's own
`SimulationRateHz` by name, right after deserializing it, rather than
re-driving at the wrong step size (see [replay.md](replay.md)).

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
7. **Journal.** Append a `WorldJournalEntry(Tick, EngineTick, Mutation)` to `WorldDocument`'s journal. The `dirty` count in
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
  stderr, and installs NOTHING. Either outcome echoes through `EchoTap`
  (an acceptance as `dropped <n>, <m> remaining`), so `world.undo` answers
  its own line like every registered verb. There is no per-mutation inverse. Proven
  in-process by `tests/Puck.World.Tests/MutationAllOrNothingLawTests.cs`
  against the shared apply gate; the replay loop's own early-return on a
  genuine mid-replay failure is unproven (see that law's own remarks).
- **Save** (`world.save`): writes the authored document and compacts the
  journal. `WorldSaveSnapshot.Compose` runs the authority fold
  (`WorldSessionCapture.Capture`: peer-source default, machine declarations,
  magazine selectors, moving `state` cells) and then the lever fold
  (`WorldSessionLevers.Fold`: render levers, master volume, present target,
  binding bar). Each fold reads the `*Raw` member and hands it back as the same
  instance when the session agrees, so an omitted section stays omitted and a
  lever folds only into an authored section. `world.status`'s drift hint names
  each section the snapshot replaced (`WorldSessionCapture.DescribeDrift`).
  `WorldSaveAuthoredDocumentLawTests` (`tests/Puck.World.Tests`) saves every
  shipped, fixture (`tests/Puck.World.Tests/Fixtures`), and canary world and
  reloads it.

Named machine rows use the same mutation and undo pipeline. UpsertMachine and
RemoveMachine affect machine preparation independently of screen, population,
solid, and render capacity. Preparation resolves configuration assets and hardware
bindings before installing a replacement; rejected or abandoned candidates retain
the live runtime. Changing only running state or bindings preserves its generation.
The shared world.row verbs author these rows and machine.state reports execution
and binding availability. Undo prepares the restored machine declarations too.

## The kind catalog

Every nested record carries `[MutationKind(ordinal, section)]` — the ordinal
is DECLARED, unique, `0..WorldMutationKindCatalog.MaxOrdinal` (= 127, one bit of
the `MutationKindMask` lane). An ordinal past the lane is refused at boot rather
than left to wrap: .NET masks a shift count by the operand's width, so an
out-of-lane bit aliases a REAL kind and would admit the wrong door silently.
`WorldMutationKindCatalog` (`src/Puck.World.Protocol/Protocol/WorldMutationKindCatalog.cs`)
discovers the set by reflection and `Validate()` fails BOOT loudly on a
missing attribute, an out-of-range ordinal, or a collision.

The lane is `UInt128`, with room past the last ordinal, but a new kind is a substrate
decision that must survive consolidation review first (see "Adding a mutation
kind" below). Regenerate rather than trust this
table — it is a copy of the `[MutationKind]` attributes on `WorldMutation`'s
nested records, which are the authority:

| Section | Kinds (ordinal) |
|---|---|
| Kits | UpsertKit 0, RemoveKit 1, SetDefaultSeatKit 2, SetKitAssignment 3 |
| Screens | UpsertScreen 4, RemoveScreen 5 |
| Machines | UpsertMachine 76, RemoveMachine 77 |
| Cameras | UpsertCamera 6, RemoveCamera 7 |
| Spawns | SetSpawns 8 |
| Motion | SetMotion 9 |
| Properties | SetProperty 10 |
| Population | SetPopulationDefaults 11, SetPopulationDistribution 68, SetPopulationCensus 69 |
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
| Views | SetViewDefaults 31, UpsertViewLayout 32, RemoveViewLayout 33, SetViewSeatRig 70, SetViewSeatControl 71, UpsertViewPipeline 74, RemoveViewPipeline 75, CommitViewPipeline 78 |
| Looks | UpsertLook 34, RemoveLook 35, SetLookAssignment 36 |
| Grants | UpsertGrant 37, RemoveGrant 38 |
| Hud | UpsertHudPanel 39, RemoveHudPanel 40, UpsertHudElement 41, RemoveHudElement 42, SetHudDefaults 43 |
| State | UpsertStateRow 44, RemoveStateRow 45 (whole row), UpsertStateCell 47, RemoveStateCell 48 (one cell), Generate 49 (one draw at a draw SITE), TransformState 67, Batch 73 |
| InputHold | SetInputHold 46 |
| Rules | UpsertWorldRule 50, RemoveWorldRule 51 |
| Interactions | UpsertInteraction 52, RemoveInteraction 53 |
| Groups | UpsertGroupKind 54, RemoveGroupKind 55, FormGroup 56, JoinGroup 57, LeaveGroup 58, KickMember 59, OfferOwnership 60, SettleOwnership 61 |
| PlayerDefaults | SetPlayerDefaults 62, SetPlayerSeatLook 72 |
| Dynamics | UpsertDynamics 63, RemoveDynamics 64 |
| Curves | UpsertCurve 65, RemoveCurve 66 |

The ordinals are dense from zero (`MutationKindMaskLawTests`); deleting a kind
renumbers the kinds after it. Machine cable linking is authored on
the `Machine` source itself (`WorldMachineCable`), so cable edits ride
`UpsertScreen`; an auction is an escrowed conditional transfer over ordinary
keyed rows, authored as rules — see [documents.md](documents.md)'s `state`
section.

Rules the catalog encodes:

- **Asset hash pinning.** `UpsertCreation` re-canonicalizes its embedded
  document at the compose boundary; `UpsertTune`/`UpsertPatch` load their
  referenced document off disk and canonicalize it there. Both REJECT a
  carried hash the pipeline did not itself compute.
- **Pipeline overrides bind at the mutation door.** `TryPrepareMutation` runs
  `TryAdmitPipelineOverrides` after whole-document validation, for every kind:
  a `views.pipelines` row whose source, `overrides` or `output` changed, and
  that names overrides or an output, has its source read through
  `WorldServer.PipelineSources` (`WorldPipelineSources`, attached by
  `WorldPostBuildWiring` in every boot shape) and its values bound through the
  pass config schema (`ShaderPipelineSource.TryBindOverrides`). A
  `CommitViewPipeline` composes against the row's fingerprint
  (`WorldDefinitionFingerprint.ComputePipeline`) and also requires the source's
  content and config-schema identities to equal the installed graph's.
  Refusals read `pipeline.overrides/<WorldPipelineOverrideRefusal>: …`. Undo
  replays compose only and never re-reads a source. A whole document binds
  through the same row check (`WorldDocument.TryBindPipelineRows`): `ApplyRebuild`
  runs it for `world.load` and `world.reload` (not `world.reset`, which
  reinstalls an admitted base), and `WorldPostBuildWiring` runs it at boot
  through `WorldServer.TryBindPipelineRows`, refusing the boot by name.
- **No cascades.** `RemoveCreation`/`RemoveTune`/`RemovePatch` refuse while
  dependents reference them, naming the dependents — remove or retarget the
  dependents first. "Who names a creation" is one walk,
  `WorldDefinitionRows.EnumerateCreationReferences` (a placement's authored,
  `respond`, contribution-slot, and deal-variant creations, a look's source, a
  kit's `fromCreation` collider); `RemoveCreation`, the contribution retraction,
  and the composition decompiler's door-prototype strip all ask it.
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

- **The one structural exemption.** `Principal.World` — the world's own
  authored program (a `rules` effect, a kit's `generate` effect) — is admitted by
  `TryAdmitMutation` BEFORE the table is consulted, keyed on the principal kind.
  It is not an actor: it holds no grant rows (the grant door refuses one by
  name), the wire refuses it as a submitter, and `world.why world …` reports
  `allowed (structural)` rather than a verdict about a table that was never
  asked. Every non-authority gate still runs. See [authority.md](authority.md).

- **A draw site's own bookkeeping.** `Generate` names ONE row — the SITE — and
  writes its drawn slot cell together with the site's own `drawCursor` (plus
  `drawnMasks` under an exhausting mode) in one candidate. The row is the authority
  subject; the cursor advance is engine bookkeeping intrinsic to drawing, while
  re-authoring the site's facet, or the `generators` row it references, is an
  `UpsertStateRow` against that row, gated there. Sampling itself lives in
  `src/Puck.State.Generators/GeneratorEngine.cs` because the BOOT resolver — which
  runs before any server exists — must reach the identical code.

## Rule-effect sugar (`.puck`) → state mutation kind

A world rule's body is `.puck` sugar over `Puck.State`'s own effect union
(`ActionEffect`, `src/Puck.State/ActionEffect.cs`) — `Puck.World.Transpiler`
invents no rule-effect shape of its own. Every effect but `transformState`
fires as a `Mutation` (`MutationKind`, `src/Puck.State/IEffectHost.cs`)
applied inside the firing's journal scope by `WorldArenaHost`
(`src/Puck.World.Server/WorldArenaHost.cs`); `transformState` instead calls
`IArenaTransformHost.TryTransform` directly, since a transform addresses a
selection of cells rather than one. Either way the arm folds into one of the
SAME `WorldMutation` state kinds the catalog above already names — never a
shape unique to rules:

| `.puck` effect statement | `ActionEffect` discriminant | `Mutation` | Folds through |
|---|---|---|---|
| `row[key] = rhs` | `setState` | `Write` (`StateWriteKind.Set`), or `WriteText` for a `Text` row | `UpsertStateCell` (49) |
| `row[key] += rhs` | `addState` | `Write` (`StateWriteKind.Add`) | `UpsertStateCell` (49) |
| `schedule row[key] in Ns` | `scheduleState` | `Write` (writes the due tick) | `UpsertStateCell` (49) |
| `remove row[key]` | `removeStateCell` | `Remove` | `RemoveStateCell` (50) |
| `push row = rhs` | `pushState` | `Push` | `TransformState` (75) |
| `transform call(...)` | `transformState` | none — `IArenaTransformHost.TryTransform` | `TransformState` (75) |
| `generate(row: ...)` | `generate` | `Generate` | `Generate` (51) |
| `transaction { } [onFailure { }]` | groups the statements above atomically (`RuleEvaluator.Effects.FireSavepoint`) | — | each grouped effect folds as its own row above |
| `if Gate { } [else if Gate { }]* [else { }]` | `if` | branches to `Then`/`Else`; each fired effect folds as its own row above | — |

Every one of these still lands on the arena first and installs once per
tick — the cross-cutting "rule writes land on the arena" contract in
`SKILL.md` — so the table names the kind a write eventually composes as,
never a second apply path.

**A world rule body is mostly straight-line, `if` aside.** The core `.puck`
language parses `if`/`else if`/`else`, `repeat`/`break`, call-form gates, and
compound assignment (`+= -= *= /= %= &= |= ^= <<= >>=`) for every vocabulary,
but the WORLD vocabulary's rule shape still refuses `repeat`, `break`, a
call-form gate, and a compound assignment by name: control flow with nothing
to lower onto is PUCK037, a call-form gate where only comparisons are legal is
PUCK038, and a compound assignment none of the effects above carries an
operator for is PUCK039
(`src/Puck.Transpiler/Diagnostics/PuckDiagnosticCodes.cs`). `if` is the one
exception: it lowers to `ActionEffect.If` (`$type: "if"`), branching on a
`condition` compiled the same way a `when` gate is, firing `then` or the
optional `else` — an `else if` chain is one nested `if` node per level, so a
chain of any length lowers, formats, and decompiles the same way a single
branch does. A cartridge rule (`puck.cartridge.v1`, see `rom-forge`) is a
DIFFERENT vocabulary that additionally admits `repeat`/`break` — the refusal
is per-vocabulary, not language-wide. Both branches read the frame at the
effect's own position, so an earlier same-firing write is visible to the
condition exactly as a later effect's own operand would see it; a condition
that fails to evaluate (an arithmetic fault, a missing table key) runs neither
branch and is reported through `world.rule.failures` the same way a failing
top-level effect is — a false condition with no fault is not a failure. Each
branch effect is its own boundary, on the same terms as a top-level effect
(or, inside a transaction, any other step) — a branch is not itself a
transaction. An `if` may sit inside a `transaction`; a `transaction` may sit
inside an `if` only when that `if` is not itself inside one, since
transactions never nest either way. `save` is refused by name inside any `if`
branch, at any nesting depth. Kit actions and body-scope effects refuse `if`
by name — a per-body action compiles to a flat instruction stream with no
branch of its own. `world.rule.trace` shows which branch a captured
evaluation took.

## Adding a mutation kind, end to end

**First, `puck search "\[MutationKind\(" src -M 0` against `WorldMutation.cs`
gives the current kind count and declared ordinals.** A colliding ordinal is a
boot failure. A genuinely new kind is a substrate decision and must survive
consolidation review first: is this an existing kind's payload? Most proposals
are — a new section reuses `UpsertStateCell`, a rule effect reuses an existing
kind.

Should the 128-bit lane ever fill, the widen is one dedicated substrate commit
— mask type + grant wire codec + document serialization +
`world.grants`/`world.why` echoes — never inside a feature change, and its proof
has two halves that must both be present: dual-run byte-identity over the
existing ordinals (necessary, but it passes on a codec that silently truncates
the new range), and a control exercising a bit past the old ceiling across the
wire. The width is an implementation detail, not protocol: authored grants name
verbs by name and the ordinal is an internal dense index, so widening or
re-packing ordinals is mechanical under supergreen. Only past the consolidation
gate do the steps below apply.

1. **Data:** the nested sealed record on `WorldMutation` with
   `[MutationKind(ordinal, section)]` (the next ordinal past the last, keeping the set dense). XML-doc the row
   semantics (rejection conditions, timing class) in the
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
hand-walked JSON door, `Addons.WorldAddonMutationDecoder`, which wires only a
NAMED SUBSET of the declared kinds (10 — the 5 HUD
kinds plus the 2 placement kinds, the 2 state kinds, and `SetInputHold`; the
Properties/Interactions/Groups kinds are console-only, not addon-reachable; see
[addons.md](addons.md#requests-queries-verdicts) for the exact list and the
decoder's own division of labor against the validator). Adding a kind here
does not make it addon-reachable — that is a separate, optional `case` arm in
the decoder, additive by the same discipline this section already follows.
