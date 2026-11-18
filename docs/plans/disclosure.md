# Disclosure

A world hides state from some of its readers: a dealt hand only its seat reads,
a sealed bid only the authority reads, a token whose zone decides who sees it
(`StateVisibility`, [traits](../reference/state/traits.md#control-what-observers-learn)).
The contract is that a reader the policy excludes cannot tell a hidden cell from
an absent one: not whether it exists, not how many there are, not where they
sit, not when they change. Two lanes closed doors one at a time
(`state-correctness`, then `disclosure-2`), and each cross-family review found
more: the latest found eight highs after the second lane landed. Door-by-door
enforcement does not converge, because every verb, sink, codec and admission
path that reaches `WorldServer.Definition` or `WorldServer.Arena` is a door, and
a new one leaks by forgetting a check it never knew it owed.

This plan is the structural fix. It states what is guaranteed and to whom, names
the one chokepoint, decides the semantics the chokepoint enforces (writes by
readers who cannot read, physical layout, change timing and delivery metadata),
says how the compiler holds the line, and orders the build as landable steps,
each checked by a noninterference harness over four authored games and none
opening a door before its protection lands; the design is done when that
harness and its fuzzer are green, which is a mechanical verdict rather than a
review running out of findings. The criterion that decided every verdict: a reader learns
exactly what the author's policy says it may, and nothing about the mechanism
that keeps the rest (the owner's ruling that seed data is often a public game
feature, so disclosure is the author's policy,
[documents-state](../../.claude/skills/puck-world/references/documents-state.md)).
An adversarial review of the first draft shaped the decisions below.

## What is guaranteed, and to whom

**The guarantee.** To a recipient the policy excludes from a cell, the world is
indistinguishable from the same world without that cell, in everything the
engine answers at tick granularity: content, structure (slots, offsets, order,
reservations), counts, identity (ids, keys), and the timing of change, including
every document version, revision and sequence the recipient is shown, the size
and number of deliveries it receives per tick, and the lifetime of its streams.
The three hidden policies set what an admitted reader of a row learns about the
cells it may not read (`HiddenCells.Omit`, `Count`, `Placeholder`,
`src/Puck.State/StateVisibility.cs:8-16`); the guarantee is stated against what
the policy permits, so under `Count` the count is disclosed by the author's
choice, and nothing else is.

**Outside the guarantee, best effort.** Wall-clock and machine-resource
channels: how long a tick, a delivery or a composition takes, garbage
collection, CPU and memory, and the arena's shared 64 MiB byte ceiling
(`src/Puck.State/Arena/ArenaCapacity.cs:17`), which a growth refusal reads
truthfully whether hidden rows or public ones filled it, and the in-session
history's memory ceiling, which evicts early only when the window does not fit
it. The engine does not
promise these carry nothing; it keeps the diagnostics that read them for the
operator (`host.frames`, `host.memory`, `world.counters`, `world.gpu-timing`,
`world.collision.probe`, `world.rate`, all `CommandAudience.Operator`), since
they carry nothing a player needs, and it keeps hidden-dependent work
proportional to change so the channel stays narrow ([decision 5](#5-a-delta-is-a-difference-of-views-owned-by-the-stream)).

**Who is trusted.** The engine's own C# is trusted code: the authority's
program, its sinks and codecs, the composition roots. The structural guards
below make it impossible for trusted code to reach the authority by omission
(a verb, a module or a sink that never knew it owed a check cannot compile), and
they make the compliant path the shortest one to write. They do not defend
against trusted code that deliberately captures a definition in a lambda or
reads a property by reflection; that is review, and the guards are shaped so
such code has nowhere ordinary to stand ([decision 6](#6-the-compiler-holds-the-line)).
Rules are the authority's program: a rule that copies a hidden value into a
public cell is an authored disclosure, like a binding on the shared screen.
Search cannot widen its inputs' audiences; a reveal is an explicit rule write.

## Where the boundary stands

The plan rests on these facts, read from the code; recheck them before
scheduling a step:

- The policy question is already one question. `WorldStateDisclosure.Observer.ReadsWhole`
  (`src/Puck.World.Schema/WorldStateDisclosure.cs:758-768`) decides from
  declarations alone whether a row can withhold anything from a recipient: its
  own policy, a cell policy its storage admits (`StateArena.DeclaresCellVisibility`,
  `src/Puck.State/Arena/StateArena.Runtime.cs:98`), or a containing ordered zone.
  `Discloses(row, key)` (`:771-778`) answers a withheld and an absent cell alike.
- The chokepoint exists for console verbs and is partial. `WorldStateReadView`
  (`src/Puck.World.Server/WorldStateReadView.cs`) is minted per acting principal
  by `IWorldConsoleAuthority.ReadView` (`src/Puck.World.Console/IWorldConsoleAuthority.cs:24`),
  but the same interface's `TryResolve`, `TryResolveInstance` and
  `TryResolveServer` (`:18,39,59`) hand any handler a `WorldInstance` with its
  `WorldServer`, and the server's public surface carries the authority:
  `Definition`, `Configuration`, `ExportDefinition`, `MaterializeArena`
  (`src/Puck.World.Server/WorldServer.cs:275-282`), `Arena`
  (`WorldServer.Arena.cs:144`), `ArenaHost` and `RuleHost`
  (`WorldServer.RuleHostSeam.cs:8,23`), each of the last two an `IStateReader`
  with a public `Arena` (`src/Puck.State/IStateReader.cs:16`,
  `src/Puck.State.Rules/Evaluation/ArenaEffectHost.cs:76`). Command modules in
  `Puck.World` take the server as a constructor parameter (47 files outside the
  server name a `WorldServer` parameter or field). `puck references Definition
  --containing WorldServer.Definition --no-doc` lists 638 references: 122 in
  `Puck.World.Server`, 98 in `Puck.World`, 28 in `Puck.World.Console`, 11 in
  `Puck.World.Silo`, 1 in `Puck.World.Addons`, 14 in `Puck.Cli.Bench` and the
  rest in tests; the same query for `WorldServer.Arena` lists 271, of which 130
  are the server's own and 9 are in `Puck.World`, `Puck.World.Console` and
  `Puck.World.Addons`. Among them, verbs any seat may run: `world.budget`
  (`src/Puck.World/WorldPopulationCommandModule.cs:208-234`, registered at
  `:460` without an audience), `world.placements` and its siblings
  (`src/Puck.World/WorldPlacementCommandModule.cs:60,203,262,345,405`),
  `world.reflow.preview` (`src/Puck.World/WorldPlacementCommandModule.Reflow.cs:24`,
  served from `m_document.Definition` after admission,
  `src/Puck.World.Server/WorldServer.Reflow.cs:98-131`, naming a raw spatial
  participant in a refusal at `:358`), `world.topologies`
  (`src/Puck.World.Console/WorldStateCommandModule.Discrete.cs:78-80`),
  `world.timeline` with a predicate of its own (`src/Puck.World.Console/WorldTimelineCommandModule.cs:137-140`),
  `world.projection`, which composes for the public observer whoever asks
  (`src/Puck.World.Console/WorldNetworkCommandModule.cs:213-231`), and
  `world.identify`, which hashes the authority document for a seat holding
  Control over a screen (`src/Puck.World/WorldIdentifyCommandModule.cs:41-46,72`).
- A session's read has restrictions beyond visibility. A session reads through
  `ReadSessionDefinition`, which returns the sink's own disclosure at the
  session's admission tier and nothing without a live Observe grant
  (`WorldStateReadView.cs:52-69`, `WorldSessionSink.cs:38-58`); a Presentation
  session is handed a hydrated projection, never the definition, so that
  unobserved rows and authoritative sections never reach it at that fidelity.
- Step 3 unified the former write predicates under `WorldRecipientView.Admits`.
  Complete admission checks it before grants; detached composition and live state members
  recheck it against earlier batch members. `WorldRecipientMutations.CheckGuards` is the one
  guard implementation, over the view's fingerprints. Addon reads use `Discloses`, while binds
  and writes require whole-row admission. A spatial guard's admission takes no region: it checks
  every placement that can enter a census (`WorldDefinitionFingerprint.EntersSpatialCensus`,
  spatial deal templates included), since which placements a region holds can depend on bound
  state. A row admits only with its declared write footprint: its token domain, every row keyed
  over it (`StateCatalog.DependentRows`, the relation the arena's commit check and the touched-row
  validator read), and for an incoming declaration every row the declaration names
  (`WorldRecipientView.AdmitsDeclaration`); a transform only while every row it resolves to
  does (`RuleCompiler.TryCollectTransformRows`); a placement only with its parent chain's inputs
  and the template every id on that chain spells as a dealt child, present or absent, for the
  incoming placement and the installed one it replaces or removes; a machine or kit with the rows
  its bindings or redraws name, incoming and installed. A recipient's mutation of rules or
  interactions is refused by kind (`WorldSectionFacts.IsProgram`). A live
  audience (`readersFrom`) admits only while every recipient reads it whole. Reflow and
  fingerprints share the placement state-dependency collector. Whole-document validation and
  rule compilation judge declarations alone (`RuleCompiler.RequireEnclosedAudience` reads row
  policies, never seeded cells); the arena judges current cell policies at the firing.
  This is the implemented collector's intended coverage, not a completeness
  guarantee: decision 3a's W1–W7 reopen it and replace the mechanism.
- A write's outcome reaches its submitter typed, not only as an echo:
  `WorldTick.CompletePendingOp` builds a `WorldMutationOutcome` from the real
  verdict with its decision, detail and the live document version
  (`src/Puck.World.Server/WorldTick.Step.cs:187-211`,
  `src/Puck.World.Protocol/Protocol/WorldMutationOutcome.cs:75-110`), and the
  same site fires the replay tape's `OutcomeObserved(ok)`.
- Every delivery sink discloses by hand. `WorldSessionSink` discloses a
  replica's definition at `:218-225` and forgets its delta cache, but `Resume`
  delivers `server.Definition` raw (`WorldSessionSink.cs:188-193`);
  `WorldFederationProjectionSink` forgets the same cache at `:156`;
  `WorldFederationCodec.Observation` composes from `server.Definition` for a
  recipient (`src/Puck.World.Server/WorldFederationCodec.Observation.cs:104-105`);
  `WorldRuleHost.AnswerStateObservations` composes from the host's arena and
  `Host.Definition` (`src/Puck.World.Server/WorldRuleHost.Queries.cs:114-119`).
  Every composer takes `(definition, arena, recipient)` as three separate
  arguments, so nothing stops a caller passing the authority with a recipient
  it never checked.
- Step 1 replaced authority-derived delivery metadata and the comparator-only
  delta cache with `WorldDeliveryCursor`, `WorldRecipientView` and
  `WorldDisclosureImages`. Definitions seed delivery history; reads do not.
  `WorldTravelerProjection` compares the projection the stream last delivered.
  The shared image table copies vector bytes and caches by audience; the
  presentation feed retains shared observation rows rather than a state JSON
  tree per recipient. [Decision 5](#5-a-delta-is-a-difference-of-views-owned-by-the-stream)
  records the carrier, dependency and sequence choices.
- The deal's slots are authority history: a child takes the lowest free slot
  (`WorldTick.FirstFreeSlot`), is stamped by `WorldPlacementDeal.Child` on that
  slot, and stores only its displacement from it, which `preserve.transform`
  keeps across reconciliation; by design a departing cell moves no sibling. Step 4 keeps that
  on the authority and re-deals in the view: `WorldStateDisclosure.DisclosedPlacements`
  is the one placement reading every recipient door shares (the replica
  document, the presentation projection, `world.placements`, fingerprints), and
  `world.placements` prints the view's slot.
- An eviction removes position zero (`src/Puck.State/Arena/StateArena.Members.cs:148-163`),
  so a write into a full row with hidden members has a public effect that
  depends on them.
- Local presentation is unfiltered by design: the boot client reads the
  definition directly (`src/Puck.World.Schema/WorldProjection.cs:171`) and the
  colocated snapshot sink takes every body (`src/Puck.World.Server/WorldOutputHub.cs:11-20`);
  a binding to a restricted row on the shared screen shows it to the room
  ([console](../../.claude/skills/puck-world/references/console.md), "Local
  presentation is not filtered"). This plan keeps that: the shared screen is the
  author's, and the operator's.
- The enforcement tools in the repository are an analyzer family with named
  allowlists (`src/Puck.Analyzers/`: ENV001, NET001, GPU001, INTEROP001, IVT001),
  an assembly-level denied-API gate (`build/PuckBuildTasks.cs:678-724`,
  `PuckArchitectureDeniedApi` in `build/Architecture.props`) that sees assembly
  references, not members, and `internal`, which IVT001 makes a real wall by
  forbidding every grant of it to another assembly.

## The findings this plan answers

Every finding of the two code reviews with a disclosure cause, where it is
today, and the step that closes it. The rule-evaluation findings the same
reviews made (a faulting remove key committing a partial firing, read operands
that erase key faults) are not disclosure and stay in the
[security review packet](open-items.md#security-review).

| Finding | Cause today | Step |
|---|---|---|
| [high, RD-03] A missing dealt child reappears only after a hidden-only dealt or variant-row edit | `WorldStateDisclosure.Redeal` (`src/Puck.World.Schema/WorldStateDisclosure.cs:682`) emits existing children; the skip in `WorldTick.SweepPlacementDeals` (`src/Puck.World.Server/WorldTick.Deals.cs:236`) omitted child-only placement edits. Closed by retaining the installed placement projection as a sweep input | 4, landed: the probe removes children from restricted-membership and restricted-variant templates, then changes hidden state in only its late variant; the same fixture leaks before the fix and compares equal after it |
| [high] Batch guards read undisclosed cells or spatial dependencies | Closed by `WorldRecipientMutations.CheckGuards` over the view, after whole-row policy admission | 3, landed |
| [high] Transform and Generate admission tests current hidden membership | Closed by declaration-only `WorldRecipientView.Admits` | 3, landed |
| [high] Per-cell upsert and remove receipts distinguish hidden from absent for peers, addons and sessions | Closed by the same policy refusal on every key; typed and addon completions carry that actual verdict | 3, landed |
| [medium] A seat writes under its grants alone and learns which hidden keys exist | Seats use the same recipient admission as peers, sessions and addons | 3, landed |
| [high] Removing a public domain key is refused only when a hidden row keyed over the domain holds it (`StateArena.TryValidateRelations` at commit) | Closed: the domain's dependents are in its write footprint (`StateCatalog.DependentRows`) | 3, landed |
| [high] A new row keyed over a hidden domain is accepted or refused by the domain's membership (`WorldDefinitionValidator.Discrete.cs`) | Closed: `WorldRecipientView.AdmitsDeclaration` admits every row an incoming declaration names | 3, landed |
| [high] A placement under a dealt child id answers "not disclosed" when the child exists and "not a declared placement" when it does not | Closed: the parent-chain walk admits the template every id spells, present or absent | 3, landed |
| [high] An unrelated edit recompiles every rule and `RequireEnclosedAudience` refuses by a hidden cell's current policy | Closed: compile-time audience enclosure reads declared row policies; cells are the arena's at the firing | 3, landed |
| [high] A seat's rule or interaction with a literal read of a hidden key compiles or refuses `StateCellUndeclared` by the hidden row's membership | Closed: a recipient's mutation of a program section is refused by kind (`WorldSectionFacts.IsProgram`) | 3, landed |
| [high] A seat's rule edit that eats work allowance makes search re-pricing print a minimum counting a hidden score row's cells (`WorldSearchCompilation.TryPrice`) | Closed: rule edits by the same refusal, and pricing reads declarations only (`RuleCompileContext.RowCapacity`; `RecipientOracleLawTests`); growth and memory admission still read reservations (open items) | 3, landed |
| [high] Replacing a dealt template without its deal keeps its children, so the distributed-parent refusal answers whether the hidden deal has any | Closed: an upsert or removal admits the installed placement's footprint too (`WorldRecipientView.AdmitsPlacement`) | 3, landed |
| [high] Dealt children keep hidden-dependent slots and positions (Argon's `deal` finding) | Closed: a template over a row that can withhold is re-dealt in the recipient's row order, each child at the deal's own offset for its re-dealt slot (`WorldStateDisclosure.DisclosedPlacements`) | 4, landed |
| [high] A deal install's delivery waited for the next unrelated delivery, a hidden-only edit included: the sweep ran after the tick's last `DeliverPending` (the card game's tick-40 presentation and replica deliveries) | Closed: the tick delivers right after the response and deal sweeps (`WorldTick` step) | 4, landed |
| [high] A replica or federation peer was handed its document on a value-only tick only when the authority installed placements, so a dealt cell turning readable by a zone move reached it at the next install, hidden or not | Closed: a value-only delivery owes the document when the recipient's frame moved (`WorldDeliveryCursor.FrameMoved`); the authority's install flag is gone from the stamp | 4, landed |
| [high] `replay.inspect`, `replay.stop` and `replay.verify` answer any principal with the authoritative tail hash, unprojected authority entries and every body pose | Closed: every `replay.*` verb is `CommandAudience.Operator` | console audit, landed |
| [medium] `world.history` keyframe ticks, window start and a seek's restored keyframe and re-simulated count follow checkpoint byte counts, which fold withheld rows | Closed: the window's shape is a function of ticks alone (`WorldHistory.Interval`, `WorldHistory.WindowTicks`) | console audit, landed |

## The console surface

Every registered console verb declares who it answers (`CommandAudience`, no
default: the factories require it and `CommandRegistry` refuses
`CommandAudience.Unspecified` by name at construction), and the registry's one
predicate (`CommandAudienceExtensions.Admits`) refuses the rest before a handler
runs. The audit below covers every verb whose audience admits a seat or anyone
(`Anyone`, or `Local` for a verb that touches the host machine) and asks one
question of each: can any part of its answer (text, value, count, tick or byte
length) derive from a row the caller cannot read whole? The evidence is
`worlds/debug/verbs-disclosure.puck`, a harness fixture whose variants differ
only in `vault`, a row only seat1 reads (empty, one small cell, or forty large
ones rewritten every twenty ticks): seat2 runs the history and replay verbs and
the read-backs in order, and the fuzzer draws 1600 lines over every verb anyone
may run. The five game fixtures draw over the same verbs against placements,
rules, search and sessions.
Verbs a headless World does not register (the renderer, audio and capture
levers) are judged from their source.
This records the earlier audit, not proof of closure: decision 3a's W6–W8
reopen the console writes, HUD templates and parked-body handler. Its D0a
adds their missing argument and principal combinations to this same harness.

D1 separates `Host` from `Operator`: resources, file-backed replay,
pipeline loading, instance lifecycle and wire diagnostics use Host. Operator
dispatch resolves the addressed world and asks its live Operate predicate;
peer reporting exposes endpoints and verified identities only to the host.
The table below records the earlier audit.

| Verbs | Audience | Verdict | Fix |
|---|---|---|---|
| `replay.record`, `replay.stop`, `replay.cancel`, `replay.drive`, `replay.fork`, `replay.verify`, `replay.inspect`, `replay.list`, `replay.status` | Operator | A tape is the whole authority: its tail hash, authority entries and poses differ with withheld rows (baseline: `replay.stop`, `replay.inspect` and `replay.verify` differed between every pair), and `drive` and `fork` replace the live document through the load door | Operator verbs |
| `pipeline.load` | Operator | Authors a `views.graphs` row naming a graph, shader source or package on the host's disk: the shared screen's pipeline and host files, which a seat never changes (decision 2: the shared presentation is the operator's) | Operator verb |
| `world.transfer` | Operator | Moves any body of any instance to any destination path, which a seat's own `player.travel` (hub plan, gap 8) does for its own body under Drive and the destination's admission | Operator verb |
| `player.claim`, `player.cycle` | Local | Move the pressing device between player slots. A bound press carries the seat's own device and is ordinary play (the roster still asks Drive over the bodies it affects); typed text names the host's keyboard, so a seat's typed line is refused and only the console moves it (the fuzzer had moved a device to player 1). A peer, session, addon or world program has no device here | `Local` audience, and the handlers refuse typed text from a seat |
| `world.history`, `world.history.drag` | Anyone (seat forms checked against `control history`) | The keyframe cadence and window eviction read checkpoint and input byte counts (baseline: `row`'s keyframe ticks and a step's restored keyframe and re-simulated count differed between `empty` and `full`); whether a restore went through the load door follows the journal, which an operator's edit of a withheld row moves | Keyframes every simulated second and eviction by the window's length in ticks; the memory ceiling evicts earlier only when the window does not fit it (a resource limit outside the guarantee); a seat's seek omits the restore route; the `history.window` HUD gauge reads the window's fill, not bytes |
| `wire.errors`, `wire.ack` | Operator | The registry's one refusal counter counts every principal's refused lines, the operator's included, and the acknowledgement mode is the operator pipe's | Operator verbs |
| `world.rate` | Operator | `shed` reads a host-lifetime wall-time counter beside `host.frames`, and pause and resume schedule every seat's world | Operator verb |
| `world.instance.start`, `world.instance.stop`, `world.instance.status` | Operator | `status` prints an instance's journal length (`dirty`), which an operator's edit of a withheld row moves, beside host paths; start and stop run documents from host paths | Operator verbs |
| `world.view` | Anyone | A seat's claim is public; the `<seat>` form's clock line reads wall-time snapshot arrivals (the fixed fixture's control pair differed there) | The clock line answers the operator alone |
| `world.state`, `world.state.observe`, `world.observe`, `world.row`, `world.match`, `world.symmetry`, `world.symmetry.word`, `world.tabletop`, `world.topologies`, `world.timeline`, `world.projection`, `world.identify`, `world.hud.template`, `world.state.similar`, `world.status`, `world.budget`, `world.text`, `world.metadata`, `world.references`, `world.imports`, `world.admission`, `world.lighting`, `world.looks`, `world.curves`, `world.dynamics`, `world.contributions`, `world.kits`, `world.properties`, `world.addons` | Anyone | Read through the caller's `WorldStateReadView` (step 2); equal across every pair | None |
| `world.placements`, `world.placement.get`, `world.inhabitants`, `world.attachments`, `world.faces`, `world.portals`, `world.destinations`, `world.adjacencies`, `world.reflow.preview`, `world.reflow.status`, `world.reflow.commit`, `world.reflow.cancel` | Anyone | Read the view's placements, re-dealt for a restricted template; a restricted template carries no spatial footprint, so it never reflows | None (step 4) |
| `world.state.cell.set`, `world.state.cell.remove`, `world.state.transform`, `world.state.act`, `world.generate`, `world.row.set`, `world.row.add`, `world.row.remove`, `world.row.step`, `world.assign`, `world.kit.default`, `world.population.defaults`, `world.population.spawn`, `world.place`, `world.snap`, `world.nudge`, `world.turn`, `world.grid`, `player.row.set`, `player.state.cell.set`, `player.state.cell.toggle`, `creation.sculpt`, `creation.shape`, `pipeline.load`, `pipeline.commit` | Anyone | Answer the write door's verdict (step 3). An absent key answers one text whatever any row interns (`StateArena.MissingCellReason`) | Built |
| `world.grant`, `world.grants`, `world.revoke`, `world.why`, `world.grant.set`, `world.grant.remove`, `world.group.*`, `world.groups`, `world.ownership.*`, `world.instance.seats`, `world.transfer`, `world.links` | Anyone | Grant and instance tables, no state rows; equal across every pair | None |
| `world.peers` | Operator | Live population membership (which bodies arrived, from which authority, and when they leave), other connections' endpoints and identities, and the door's refusal counts: the host's, with no recipient view to derive them from | Operator verb, off the silo's remote surface |
| `body.*`, `player.*` controls and read-backs (`body.where`, `body.state`, `body.targets`), `world.players`, `world.population`, `world.parked`, `world.gravity` | Anyone | Bodies, not rows: body disclosure is the open item below (`WorldOutputHub.Redact`, A16); no state row reaches them. Their server queries ask as the caller: `IServerLink.Query` takes the principal and the trusted composer (`WorldServer.Answer`) is internal, so a session the query door refuses is refused here too | Body disclosure step |
| `identity.*`, `chat.*` | Anyone | The caller's own identity documents, authorized per seat (`TryAuthorizedIdentity`); a whisper's inbox rows are what the deduction fixture exercises | None |
| `help`, `console`, `run`, `world.affordances`, `world.refusals`, `world.extensions`, `world.extensions.catalog`, `world.devices`, `world.device-profiles`, `world.input-holds`, `world.watch`, `world.flag`, `forge.*`, `machine.default`, `update.*`, `storage.*`, `probe.*`, `backend` | Anyone or Local | Registry, catalogue, host configuration or the caller's own session; no state rows. `run` submits an authored command's lines under the caller's principal, so each line meets its own verb's audience | None |
| `world.fps`, `world.target`, `world.cadence`, `world.residency`, `world.host`, `world.shaders.status`, `world.shaders.reload` | Anyone | Wall time or machine resources, outside the guarantee | None |
| Render, view and audio levers and read-backs (`world.ao` … `world.upscale-sharpness`, `view.*`, `world.view.*`, `world.compare`, `world.screenshot`, `world.debug-view`, `world.screens`, `world.machines`, `world.nesting`, `world.view-refresh`, `screen.*`, `machine.state`, `machine.content`, `machine.operation`, `machine.swap`, `world.cameras`, `world.sdf.*`, `world.slice`, `world.slice.save`, `world.speakers`, `audio.*`, `speaker.state`, `voice.*`, `music.state`, `instrument.state`, `world.volume`, `world.theme`, `world.hud`, `world.binding-bar`, `world.captures`, `world.control`, `world.wait`) | Anyone | The shared screen and speakers are the operator's presentation, which shows the room what the author binds there (decision 2); a non-local ingress reaching them is the same open question as the local mirror | None in this audit |

`CommandAudience.Anyone` carries the obligation in its documentation:
an answer to a recipient derives only from that recipient's view. What enforces
it for a new verb is the audit's fixture and the fuzzer drawing every verb the
registry lets anyone run, not the compiler; D0b1's `OperatorContext` and
DISC001 are the structural check that makes the authority unreachable from a
non-operator handler, and required audiences are its first piece.

## The decisions

### 1. One capability: the recipient's view

Step 2's implementation invariant: every recipient read derives from its live
`WorldRecipientView` at the server-minted ceiling; reading never advances a
`WorldDeliveryCursor`. The existing `WorldStateReadView` remains a console
adapter until step 6, with its definition and value reads delegated to that
capability. Session capabilities retain their admission tier and recheck live
Observe. Host resource/work measurements in `world.budget` remain Host reads;
recipient budgets count only the disclosed document. Search output audiences
must be enclosed by every dependency of candidate generation, judging and
scoring, checked before execution and before landing against live policy.
`SymmetryOperand.CollectReads` includes both the primary row and the other-node
cell, so search and transform control checks cover either input. Replica field
declarations pass through `WorldStateDisclosure.FieldProjection` in `Disclose`,
which the definition image cache also uses. Presentation observations carry an
explicit slot/keyed storage domain and authored capacity, independent of which
cells are disclosed.
The compiler wall against new authority-reading callers belongs to D0b1.

Authority diagnostics (`world.budget.rules`, `world.inspect`, `world.cost`,
`world.contacts`, `world.collision.status`, `world.rigid`, `world.explain`,
`world.interactions`, `world.fields`, `world.rule.hazards`, `world.patterns`,
`world.tables`, `world.topology`, `world.flock`, `world.navigation`,
`world.debug-view report` and `goto`, and diagnostic finding picks) are
operator reads. Their counts, work and revisions cannot be reconstructed from
a recipient's view. A `world.debug-view` mode stays bindable for anyone: it
selects what the shared screen draws, the operator's own presentation, and a
seat's `colliders` or `contacts` selection answers the mode without the
report's summary (the debug world's `gallery.view` and `range.tiers` commands
call it). Standard seat controls no longer bind the operator inspector or the
debug view.
The journal and the save snapshot are the authority's edit history and its
whole document: `world.save` is an operator verb (it writes the authority
document, withheld cells included, to a host path and answers its byte
count), and a recipient's `world.status` omits `session-drift` and `dirty`,
whose journal length moves with an operator's hidden-only edit.
`WorldGrants.IsOperator` is the live operator predicate.

**Puck builds:** `WorldRecipientView`, the one object every per-recipient
answer is derived from: reads, write admission and refusal, placements and
their layout, projections, deltas and diagnostics. It replaces
`WorldStateReadView` and absorbs `WorldStateDisclosure`'s `Observer` and the
dealt-placement half of `Disclose`. It lives in `Puck.World.Schema` beside
`WorldProjection`, is constructed over a definition, an arena, a principal and
a **ceiling** (the recipient's admission tier and whether it observes right
now), and is minted for the live world only by the server, from an
ingress-stamped carrier (a `CommandContext`, a `SubmissionEnvelope`, an attached
session or addon), never from a bare `Principal`. The view is stateless: it
reads the live store at every call and holds no delivery state (that is the
stream's, [decision 5](#5-a-delta-is-a-difference-of-views-owned-by-the-stream)).
It offers:

- `Definition`: what the recipient may hold of the document at its ceiling:
  at `Replica`, the document with every withheld cell removed, storage metadata
  stripped whether or not anything is withheld now, and every dealt and
  responsive placement re-read from the disclosed rows
  ([decision 4](#4-the-physical-layout-follows-the-hidden-policy)); at
  `Presentation`, the hydrated projection and nothing more; at `Frames`, or
  while the recipient's Observe grant is revoked, `WorldProjection.Undisclosed`.
  That is what a delivery stream holds; a session's read door refuses before it
  mints a view whenever its query door would (`WorldServer.ReadView`), so a read
  never answers a session the query door refuses.
  The ceiling is the view's; no member takes a tier, so a caller can lower
  nothing and raise nothing.
- `ReadsWhole(row)` and `Discloses(row, key)`: the policy questions.
  `TryRead(row, key)` answers a withheld cell and an absent cell identically,
  as `Absent`.
- `Observations(presented)`, `Placements`, `FieldRegions`, `Project(time,
  anchors)`: today's `WorldStateDisclosure.Compose`, `PlacementProjection`,
  `FieldProjection` and `WorldProjection.Compose`, with no `recipient`,
  `unrestricted` or `tier` argument because the view is the recipient at its
  ceiling.
- `Image(row)`: the recipient's complete observable image of one row (its
  readable cells, and under `Count` the hidden count, under `Placeholder` the
  backs in order), the unit deltas are computed over.
- `Admits(mutation)` and `Refusal(verb, row, key)`: the write-door decision
  ([decision 3](#3-a-writer-that-cannot-read-a-row-whole-cannot-write-it)) and
  the one refusal text, worded alike for withheld and absent.
- `Fingerprint(cells | spatial region | inputs)`: the batch guards' basis,
  computed over the view's rows and placements, so a guard can only ever name
  what its proposer may read.
- `Budget`: the counts a diagnostic may print, derived from `Definition`.

The operator's view (`Principal.Console`) and the world's own (`Principal.World`)
are the authority: `Definition` is the live export, `ReadsWhole` is always true.
A measurement that sizes for every possible recipient asks for the widest view,
today's `unrestricted: true`; both are minted only through the operator door
([decision 6](#6-the-compiler-holds-the-line)).

**The rule the view makes structural:** `Principal? recipient`, `unrestricted`
and `tier` disappear from every composer's and codec's signature. A method that
takes a `WorldDefinition` and a `StateArena` is the authority's own and
composes for nobody; a method that composes for someone takes a
`WorldRecipientView`, and the only way to name a recipient is to hold its view.

**Why:** the reviews' doors have one shape: a path reaches `server.Definition`
or `server.Arena`, composes something for a recipient, and omits or
re-implements the policy question. `WorldStateReadView` already proved the
shape of the fix for console verbs (`ConsoleDisclosureLawTests` sweeps every
console module verb as a non-reader), and the doors it did not cover are the
doors that leaked. One object that every per-recipient answer derives from is
the only arrangement where a new door inherits the check instead of owing it.
It is also the one-implementation ruling applied: `ReadsWhole`,
`DisclosesStateRead`, `IsWithheldFromWriter`, `CanReadCell`, `WriteStateCell`'s
check, the timeline's predicate and the two batch guards are seven readings of
one question. The ceiling is in the view because the session's tier and its
live Observe grant are restrictions on the recipient, not on the row, and a
view that did not carry them would hand a Presentation session the definition
(the case `WorldStateReadView.cs:57-58` guards against today).

### 2. A recipient is anyone the authority is not

**Puck builds:** every principal but `Console` and `World` is a recipient: a
local seat, a peer, a session, an addon, an agent participant, a federated
traveller. A seat's console session, its writes and its refusals go through
its view exactly as a peer's do. The shared screen stays the operator's
presentation (the boot client renders the operator's view), and the author who
binds a restricted row to it has chosen to show it to the room, as today.

**Why:** `ReadsThroughDisclosure` exempts seats at the write door
(`Compose.Cells.cs:574`), which is where the recorded seat write oracle comes
from, while `WorldStateReadView` already treats a seat as a non-reader at the
read door. A principal that is a recipient for reads and the authority for
writes is a contradiction a reviewer finds every time. Local trust is a property
of the shared screen, not of the seat principal: the same seat principal
arrives over MCP, a schedule row or a session, and those are not the room.
Which principals are the authority is decided by kind here;
[decision 3a](#3a-a-declaration-is-judged-inside-a-scope-its-declarer-reads-whole)
designs it as a grant the world gives, so a couch player's own world or a
hosted world's remote owner can be its operator.

### 3. A writer that cannot read a row whole cannot write it

Step 3's invariant: a recipient's mutation or guard cannot inspect or change a row its declared policy
does not disclose whole. `WorldRecipientView.Admits` decides before grants and composition at
`WorldDocument.TryAdmitCompleteMutation`; detached composition rechecks the same capability against
each preceding batch member. Addon binds and typed writes use that same row policy. Whole-document
rebuild and undo are authority-only at their apply doors, as well as operator-only console verbs.

Step 3 also closes the whole-document write doors. `world.load`, `world.reload`,
`world.reset` and `world.undo` are operator verbs, and `WorldDocument.ApplyRebuild`
and `WorldPersistence.ApplyUndo` refuse a recipient before inspecting the source or journal.
The every-section Mutate hold alone did not protect hidden state: undo drops the tail
of the authority's journal and replays what remains, including mutations hidden from a seat.

A sealed bid is a write into a row the writer may not read. The first draft of
this plan admitted such writes blind (a pure `Set`, one `accepted` receipt, the
resolution private). The review showed no receipt discipline survives the
write's public effects: in a row at capacity, a blind `Set` evicts position
zero (`StateArena.Members.cs:148-163`) or refuses at capacity, and whether the
writer's own readable cell survives tells it whether a hidden cell was there;
the typed completion carries the real decision and version
(`WorldTick.Step.cs:187-211`) to forwarded links and peers; an atomic batch
mixing the blind write with a public marker reveals the private failure
through its rollback; and the addon's bound write resolves an existing key
before writing (`WorldAddonRuntime.Cells.cs:120-129`), so an absent key and a
hidden one answer apart or the bid is silently lost.

**Puck builds:** for a recipient and a row `ReadsWhole` denies it, every
mutation whose declared write footprint holds the row is refused by policy,
before anything else is looked at and with one text whatever the key: `'<row>'
is not disclosed to <principal>`. That covers `UpsertStateCell` (Set and Add),
`RemoveStateCell`, `UpsertStateRow`, `TransformState` and `Generate` naming the
row, a write into a row keyed over it as a token domain, a write into the
domain a row keyed over it reads, an incoming declaration naming it as its
domain, audience, board source or phase protocol, a transform resolving to it by a
live key, a zone's domain or a pool field, a placement whose own inputs, parent
chain or dealt-child ids depend on it, a batch holding any of them or an
`ExpectedCells` guard that reads it or an `ExpectedSpatialReads` guard while any
placement a spatial read could observe depends on it, whatever the region (the
whole batch refuses, as one member's refusal refuses a batch today), and an
addon's bind or write of a cell in it, which answers one
verdict, `NotDisclosed`, for a hidden key and an absent one. The refusal is the
actual outcome, so the typed completion (`Refused`, code
`world.mutation.undisclosed`, the one text), the replay tape's recorded
outcome and the echo all say the same thing and nothing else, and the recipient
is shown its own stream's version ([decision 5](#5-a-delta-is-a-difference-of-views-owned-by-the-stream)),
never the authority's.

**The write door neither depends on observing nor names a row the writer did
not.** Policy is the writer's declared readership, while a delivery ceiling and a
live Observe grant restrict what its stream carries; so the door
(`WorldServer.WriteView`) mints every principal's capability at the policy's own
tier, and the view's write questions read policy alone (`WritesWhole`, the read
question without the ceiling). A session admitted at `Frames` writes a public row
the operator grants it Edit over, and nothing it is refused reads its observation.
A name no row holds is refused exactly as a withheld row is (the key table's rule,
below, applied to row names), so a recipient replaces a row it reads whole and adds
none; and the refusal names only what the submission spelled: a placement, machine
or kit by its own name, a transform by its first subject, a spatial guard as
`placements`, never a row the footprint derived. A guard over the whole document is
refused by its shape, as `state`, since whether any declaration withholds from its
proposer would otherwise decide it. Enforcement: `WorldRecipientMutations.Admits`
alone assigns the refusal name, and the view's derived-footprint checks
(`AdmitsPlacement`, `AdmitsSpatial`, `AdmitsMachine`, `AdmitsKit`, `AdmitsTransform`)
return no name to assign. Where it stops: the footprint relation is declared
structure, so a write into a public row is still refused while some row keyed over
it is withheld, which tells a recipient below `Replica` that such a declaration
exists, never its name (filed under the open items below).
`SessionWriteDoorLawTests` and the harness law
`AWriteIntoAWithheldRowIsEchoedAsAWriteIntoAnAbsentOne` hold it.

The view's `Admits(mutation)` is the one
predicate, used by `TryAdmitCompleteMutation` ahead of grants, by the compose
arms, by `WorldServer.WriteStateCell` and the addon runtime's bind, and by the
forwarded submission door, replacing `ReadsThroughDisclosure`,
`IsWithheldFromWriter`, `IsRowWithheldFromWriter`, `DisclosesStateRead`,
`TryAdmitGuardObservation`'s Observe-only arm and `WriteStateCell`'s
`CanReadCell`.

**The first write footprint did not close the class.** Two rounds of point fixes each
added a row to a forward list the admission checked, and each review found
more of one shape: after admission the mutation flows through composition,
whole-document validation, rule recompilation and the arena's commit checks,
which read whatever they need, and their refusals reach the recipient through
the ordinary completion. The reverse domain (removing a public key is refused
only while a hidden row keyed over the domain holds it), the incoming
declaration (a new row keyed over a hidden domain is judged by the domain's
members), the absent parent (a placement under `tmpl/x` answers "not
disclosed" when the dealt child exists and "not a declared placement" when it
does not) and whole-document recompilation (an unrelated placement edit runs
`RuleCompiler.RequireEnclosedAudience` over a hidden cell's current policy)
were the fourth round's instances. The invariant, restated so a check can hold
it: **a recipient's verdict, text, tick and byte count are a function of the
mutation and the rows the recipient reads whole.** A post-admission check
keeps that invariant in one of two ways, and every check is one or the other:

- *It reads current members only inside the footprint.* The footprint of a
  row is declared, never current: the row, its token domain, every row keyed
  over it (`StateCatalog.DependentRows`), the ordered zones over its domain
  and its live audience row (already inside `ReadsWhole`); for an incoming
  declaration, every row it names (`WorldRecipientView.AdmitsDeclaration`); for a
  transform, every row it resolves to; for a placement, its own inputs, its
  parent chain's and the template every id on the chain spells as a dealt
  child whether or not that placement exists. The arena's relation check at
  commit and the touched-row validator read that same `DependentRows` table, so
  the admission and the checks it covers share one relation rather than a list
  one side maintains for the other. Admission requires the whole footprint to
  read whole, so the members those checks read are members the recipient may
  read.
- *It reads declarations only.* Whole-document validation and rule
  compilation run over the authority's document on every full install, and
  they may read any row's declaration (declarations are delivered at the
  replica tier) but no row's current members. The one compile-time check that
  read members, `RequireEnclosedAudience` over seeded cell policies, now
  judges row policies alone; the arena judges cell and containing-zone
  policies at the firing (`StateArena.ReadAudienceEncloses`, through
  `TryEncloseAudience` for transforms and `ArenaSearch` before landing). The
  work sheet and every search price read a row's declared room
  (`RuleCompileContext.RowCapacity`), never its cells or reservation. The
  property that makes this sufficient is that the installed document
  validates: every write that changes members is checked at its own door (the
  relation check at commit, binding revalidation when a bound row is written),
  so an edit that changes no member revalidates a document whose validity no
  hidden member decides.

One store is neither a row's members nor a declaration: the arena's key table
(`StateArena.Keys`), which every row shares and which grows whenever any row,
hidden ones included, mints a name. A door that answers one way for a name the
table resolves and another for a name it does not is an oracle over every row's
history. The remove door answered "has no cell keyed" for an unminted name and
the arena's "holds no cell" for a minted one the row lacked; both arms now answer
the arena's text (`StateArena.MissingCellReason`). The rule for a door: a name
the table does not resolve is answered exactly as a resolved key the row does
not hold, so a read answers absent, a write refuses as absent, and nothing
mints a key on a recipient's behalf.

The current predicate, `WorldRecipientView.Admits(mutation)`, does not enforce
that invariant structurally. The later review found existing reads outside its
list, not merely a risk from future validators. The declared-footprint account
above describes the attempted fix, superseded by
[the structural write footprint](#the-structural-write-footprint) below. The
current checks cover their examples, not completeness: `puck disclosure`
marks a run INVALID when a variant's steps leave the authority's live document
failing `WorldDefinitionValidator.TryValidateLocally` (the precondition for the
fourth instance, which no probe write needs to hit), and `RecipientOracleLawTests`
holds each sequence this class has produced, each run over both hidden
variants and compared by code and text.

**A replacement admits what it replaces.** An upsert of an existing row and a
removal both carry the installed row's footprint beside the incoming one's,
through one admission path: composition keeps what the installed row's state
decides (a deal template's dealt children, present or absent) and validation
judges it against the replacement, so a footprint over the incoming
declaration alone let that verdict read the hidden deal. A placement write
admits the installed placement's inputs (its deal and variants rows), its
parent chain and the template every id on that chain spells
(`WorldRecipientView.AdmitsPlacement`); a machine write admits the rows every
memory binding of the incoming and the installed machine reads or writes
(`AdmitsMachine`), since a binding copies a cell into hardware a screen may
show; a kit write admits the rows its actions redraw (`AdmitsKit`).

**Authoring the program is the authority's.** Rules and interactions are the
world's program: they compile against the whole document, read any row through
expressions and fire as `Principal.World`, which the grant table admits
structurally. A recipient who may author one can copy a hidden cell into a
public row outright, and even a refused authoring was answered by the hidden
state: the rule compiler refuses a literal key the row neither holds nor declares
(`StateCellUndeclared`), so a seat's rule reading `vault[x]` asked whether the
hidden row held `x`; and a rule that ate work allowance made the search
re-pricing print a minimum that counts a hidden score row's cells. A footprint
cannot be declared for a program short of compiling it, which is the step that
leaks. So `WorldRecipientMutations.Admits` refuses every recipient mutation of a
program section (`WorldSectionFacts.IsProgram`) by its kind, before anything
compiles, with the one refusal text naming the section; the operator and the
authority keep it. The same predicate refuses an untrusted Mutate grant over a
program section, and the seeded seat grants leave program sections out. Search
jobs have no mutation kind: they change only through a whole-document rebuild,
an operator verb. A section whose content touches state through rows it names
(machine bindings, a kit's redraws) stays open to recipients and admits those
rows as its footprint. The rule refusal closes the re-pricing instance found
through a rule edit, and pricing itself reads declarations only: every price
reads a row's room through `RuleCompileContext.RowCapacity`, which answers an
uncapped row's ceiling (its token domain's, or the engine's), never its cells,
so no door that moves a hidden row's members moves the work sheet or a search's
allowance ([the costing decision](../decisions/state-and-language.md#costing)).
An arena growth's work and memory admission still read reservations
([open items](#open-items)).

**The options weighed.** (a) Admission computes the full read closure of every
later stage: this is the footprint above, and it holds only because the closure
is declared (the catalog's relations, a declaration's names, a placement's ids)
and shared with the checks; a closure enumerated by hand per stage is the
forward list that did not converge. (b) Post-admission stages run over the
recipient's projection: a verdict computed over the projection is the same in
both hidden variants by construction, but it is not the authority's verdict.
In the reverse-domain instance the projection accepts the removal in both
variants while the authority must refuse the one that would leave a hidden hand
holding a key outside its domain, so either the authority installs an invalid
state or the recipient is told "accepted" for a write that did not land, which
its next public read exposes. The projection decides nothing the footprint does
not already decide, and for state commands it would need the per-recipient
arena copy decision 1 rejected; at the scale of a world per zone with thousands
of writers it is a second whole-document validation per recipient edit. (c) A
recipient's mutation takes only paths whose checks are functions of disclosed
rows: this is what the two kinds above make true, without forbidding a
recipient the document edits that need whole-document validation, which the
engine cannot localise today. (d) One fixed outcome for every post-admission
refusal: it narrows the text channel to one bit but cannot close the first
instance, where the bit itself is the oracle, and it would take a builder's
reasons away. That earlier choice of (c) built from (a) is superseded by the
restricted transaction and static declaration security closure in decision 3a.
In particular, a restricted transaction refuses an unavailable read; it never
substitutes the projection's missing cells for the authority's cells as (b) did.

**What the footprint costs.** Nothing per recipient per tick: `DependentRows`
is computed once per catalog beside `ContainingZones`, and an admission reads
a handful of flags. A legitimate write pays one new refusal: a recipient may
not write a public domain while some row keyed over it is withheld from it,
because the arena's commit check would otherwise read that row's members to
decide the write. The games' seat writes (per-seat inboxes, request slots) sit
in footprints of their own. The debug world's granaries board declared
`cellVisibility: true` on its public feed rows, a mechanical conversion of an
earlier `visibility {}` that reserved per-cell reader storage the feed never
uses and made every seat's write to them undisclosed; the rows are plain public
rows again. `cellVisibility: true` is a restriction a recipient's writes meet,
never a way to publish a row.

**Sealed bids are authored.** The writer writes a row it reads whole and
nobody else does (a per-writer row with `visibility { readers [<writer>] }`),
and a rule moves the value into the authority-only row and clears the inbox.
The writer's receipt is an ordinary receipt on its own row; the moment of the
move, the count of bids and their values are the authority's. The standard
package carries the pattern as a template (`sealedInbox`, one row per seat
with `collectSealed` and `receiveSealed` rule templates), so an author imports it rather than rebuilding it.
The auction supplies its deadline and purse rule; the standard templates move the offer,
clear it and advance the private receipt in one firing. Row policy and cell-policy storage
are distinct: a row's `visibility` admits its readers whole unless a seeded cell declares a
policy or `cellVisibility: true` reserves those policies even while empty. The probe's empty
variants and the games' mixed-audience rows declare that capability explicitly.

**Why:** the owner's ruling to prefer the fix at the cause and the lead's
direction to prefer the simpler sound design. A write a writer cannot read the
result of is a read-modify-write whose public effect depends on the secret;
no receipt wording changes the effect. Refusing by policy has one outcome,
which is why it survives every consumer of outcomes (echo, typed completion,
addon reply, replay) without a split between what the recipient is told and
what happened. The authored inbox gives the game what blind writes promised,
on rows the engine can reason about.

### 3a. A declaration is judged inside a scope its declarer reads whole

This decision is a design; its implementation remains open. The rd2-review
adversarial review is answered below (H1–H6, M1–M8 and L1–L7). The revised
security closure is static; executed reads never remove a security check. It answers the
question decision 3 closed without settling. The write door refuses every
recipient's declaration of a new state row (`WorldRecipientView.Admits` refuses
a name no row holds, and `WorldRecipientView.AdmitsDeclaration` says "a
recipient replaces a row it reads whole and adds none"), because a door that
admits a free name and refuses a name a withheld row holds tells the submitter
whether a hidden row exists. The refusal costs a capability: a principal who is
not the operator cannot add state to a world. Players and guest authors
extending a shared world, an addon keeping rows of its own, a couch player
building in their own world and
[in-world editing](open-items.md#in-world-editing) all need it in a form that
discloses nothing. A review of the write door raised four findings and one
question about who the operator is; they are answered here because they share
its cause. The subsequent eight-finding review is answered under
[the structural write footprint](#the-structural-write-footprint). That design
replaces the enumerated admission footprint for existing writes too; a
writable scope is one bounded use of the same transaction, not a second
write implementation.

**What must be decided.** How a principal who is not the operator declares
state rows so that no verdict, refusal text, outcome code, version, tick or
byte count it is shown differs between a world that holds a row withheld from
it and the same world without that row; how the rows it declares are disclosed
to others, bounded, persisted and carried across an author's edit; and who the
operator is.

**What exists**, read from the code:

- **A declaration is a full install.** A row that changes the catalog is
  `WorldInstallKind.Full` (`WorldDocument.InstallKindOf`): the whole document is
  validated and every rule compiled (`WorldDocument.TryValidateMutationCandidate`
  calls `WorldDefinitionValidator.TryValidateLocally`), and the arena is
  installed whole (`WorldServer.SyncArena`: "Any other document is installed
  whole"). Every one of those stages reads the whole document, and its refusal
  reaches the submitter through the ordinary completion.
- **The section's ceilings are shared.** `StateCapacity.MaxRows`,
  `StateCapacity.MaxCellKeys` (the one key table, `StateArena.Keys`) and
  `ArenaCapacity.MaxBytes` are counted over every row, hidden ones included. A
  recipient's view carries no reservation for a row that can withhold
  (`WorldStateDisclosure.WithoutStorageMetadata`), so the room left under a
  ceiling is not something any recipient is delivered.
- **The replica tier is delivered every row's declaration.**
  `WorldStateDisclosure.DiscloseRow` keeps a withheld row and removes its cells.
  Below `WorldDisclosureTier.Replica` a recipient is delivered no declaration,
  and the write door is the same for every tier (`WorldServer.WriteView`), so
  the door may not answer from declarations either.
- **A namespace no author can spell exists, at compose time.** An import's
  alias composes a fragment as `<alias>$<name>` (`WorldModuleNamespace`,
  `GeneratedName.Qualify`), every name site the schema holds is listed once
  (`WorldNameRegistry`), and a fragment's names are private unless exported
  (`WorldModuleExports`). `GeneratedName.Joiner` appears in no name an author
  writes.
- **A host-authored slot a guest fills exists, for placements.** A placement's
  `WorldPlacementContribution` is a slot whose frame and lifecycle the host
  authors and whose creation a partner supplies through ordinary mutations; the
  server stamps `WorldPlacementContribution.Contributor` from the acting
  principal and refuses a submission that names it.
- **A grant that confers a row not yet declared exists.**
  `GrantSubjectKind.Creation` and `GrantSubjectKind.Placement` admit one row
  apiece, as an alternative to the section hold, and are shape-checked rather
  than bound-checked "because authoring a row that does not exist yet is what
  the grant confers". `GrantSubjectKind.State` names one existing row.
- **A document its owner declares rows in exists.** A character is an owned
  world (`WorldOwnedWorlds`); `ChatCommandModule` declares rows on the acting
  player's own identity document, validated alone, under generated names
  (`WorldIdentityRows`).
- **The operator was a principal kind in this audit.** D1 replaces that
  console-or-world shortcut with `WorldGrants.IsOperator`, shared by command
  admission and recipient views. On a silo the console remains the machine's
  standard input; an admitted peer can separately hold Operate.
- **Saved rows reach a changed source by declaration.** A durable image's rows
  load through `StateArena.TryValidateLoad`, which drops a row the current
  catalog does not declare
  ([the restore ladder](hub-and-saves.md#the-restore-ladder)); a row declared at
  run time is in no source, so it would not survive the author's next edit.

**The channels.** Each is a way a declaration's outcome reads something its
declarer cannot, or a way a declared row harms the world that took it. An
option is weighed by which it closes by construction.

| | Channel | Where it reads outside the declarer's view |
|---|---|---|
| R1 | Name collision | The catalog refuses a duplicate row, pool or family name, so a door that admits free names answers "free" or "taken" for a hidden one |
| R2 | A name the declaration spells | A token domain, a live audience, a derived board's rows, a phase protocol, a vector space, an enum or a generator resolves or fails against declarations the declarer may not hold |
| R3 | Shared ceilings | Rows, keys, arena bytes and vector bytes left are a function of every row's reservation |
| R4 | Whole-document checks | Validation and rule compilation run over everything on a full install, so any check a new row can fail or move (a price against `RuleCapacity.MaxWorkUnitsPerTick`, a relation, a binding) answers from the whole document |
| R5 | Entanglement | A guest row keyed over an author's row joins that row's write footprint (`StateCatalog.DependentRows`): the author's rule can no longer remove a key the guest row holds, and another recipient's write to the author's row is refused while the guest row is withheld from it |
| R6 | Capture and squatting | A guest takes a name the author's next edit declares, and the image's saved cells then load into the author's new row (an inference from the ladder's rule that a declared row keeps its saved cells); a guest row with `WorldStateRow.GatesDrive` can gate a body's input |
| R7 | Work | Each declaration costs a full install of the host world, whoever submits it |
| R8 | Persistence | A declared row is dropped at the first source change, or kept by a path that reads the old document |

**Options.**

| Option | Evidence | Closes | Leaves open, or fails at |
|---|---|---|---|
| a. Flat names, admitted when free | Simple; what a seat's seeded `Edit all` would give | Nothing | Every channel; R1 is the leak the write door refuses declarations to close |
| b. Flat names, admitted only for a principal delivered declarations (a replica reader learns nothing from a collision) | Cheapest way back; collisions are a function of what the replica tier holds | R1 and R2 at the replica tier | The door reads the delivery ceiling again, which the write door was just made not to do; R3 leaks at every tier (reservations and the key table are never delivered); R4 to R8 untouched |
| c. Server-minted row identities (the guest never names a row) | Closes naming outright | R1, R6's squatting | A shared counter counts other principals' declarations, so it must be per principal, which is a namespace with unreadable names; an addon cannot find its rows after a restart without a name map; R2 to R5, R7, R8 untouched |
| d. A writable scope: an author-declared namespace in the host's state section, closed under its own names, with a reserved quota, shared or private writer partitions carrying stamped row policies | Reuses `WorldNameRegistry` and contributor stamping; structural membership avoids import-name collisions, and the validator keeps each writable partition readable whole to its writer | R1, R2, R5, R6 by closure; R3 by reservation; R4 and R7 once a scope row installs without a full install; R8 through the image ladder | Programs: a scope holds rows, never rules (decision 3 stands). Scale: scopes share one catalog and one arena, so they are few. A scope's rows are declared to every replica reader, as every row is |
| e. An overlay document per principal, composed into the host under an alias | Reuses the import machinery whole; the overlay file is the persistence | As d, with R8 by the file | Composition runs at load, so a live edit is a recomposition and a full install of the host (R7 stays); every principal's overlay enters the host's catalog, so it does not scale past d; a second persistence beside the image |
| f. A world the declarer operates: the guest's state lives in a document it owns, mounted in the shared world through a portal, a screen or the cross-document write-back door | [The world model](../architecture/worlds.md#the-idea-in-plain-terms): "A character is a world"; [the hub plan](hub-and-saves.md#durable-images) gives a `user` or `group` scoped place an image of its own; the only form in which a guest authors rules | Every channel, since the declaration is validated against the guest's own document | Nothing of the guest's is in the host's arena: the host's rules and bindings reach it only across a seam (a claim, a write-back grant, a face) |
| g. An operator grant: the world names who operates it | The reviewed finding that a hosted world's owner and a couch seat in its own world are refused as strangers | Every channel for its holder, who reads the document whole, so nothing is withheld from it | It is not a way for a stranger to declare; it decides who is not one |

**Favoured: g, d and f together, as one rule.** A declaration is validated
against a universe its declarer reads whole, and there are exactly three:

1. **The document, for the world's operator** (g). Nothing is withheld from the
   operator, so its declarations stay as they are. What changes is who it is.
2. **A writable partition of a scope, for its admitted recipient** (d). The
   in-world form: rows that live in the host's tick, are delivered by its sinks
   and are bound by its HUD and placements.
3. **A world of its own, for everything else** (f). A house, a guild hall or a
   table with house rules has a program and an owner, and at an MMO's
   population no one zone's catalog holds every visitor's rows; the model
   already makes each of them a world, and nothing in this design is needed to
   declare a row in a world one operates.

Options a, b, c and e are rejected for the reasons in the table. **The
strongest case against** is that d adds a mechanism where f alone would do:
every channel is closed by construction in a world the guest owns, and the
writable scope must close each one by rule. The answer is that f cannot
put a row in the host's own arena, which is what an addon's rows, a co-built
exhibit's counters and a visitor's note on a shared board are, and that
without d the capability the fix removed stays removed. d is kept small so
that its closure is checkable: rows only, plain storage only, author-selected policy templates and
reserved storage per independently writable partition.

**The rulings it rests on** ([AGENTS.md](../../AGENTS.md)): "Authors rule" (who
may add to a world is the author's declaration, with a neutral default of
nobody); "Unreached is not unwanted" (the capability returns rather than being
deleted); "The proper fix is always preferred" (the scope closes the class
instead of gating the old door by tier); the purpose statement's weighing rule
(at the scale of a world per zone, a per-player row belongs to the player's
world); and the generic-engine ruling (a scope is a mechanism, and housing or a
guestbook is `.puck` data that uses it).

**The scope invariant.** A recipient's declaration is decided by its submission,
the scope's author-declared contract and the partition it is entitled to read
whole. A shared scope is one partition; a private-input scope has one reserved
partition per admitted writer. No other partition's occupancy, keys or cells
participate in this decision. **The one door:** the restricted preparation
transaction below, with scope-local handles and a declarations-only type;
only its successful prepared result can install. **Where it stops:** replicas
receive row declarations, so this hides cell contents and membership, not row
declarations from replicas. Below Replica, a scope grant delivers only the
contract and the writer's entitled partition. It does not upgrade the general
read tier. Host resource failure and dishonest trusted primitives remain
outside the guarantee; authored quotas and key exhaustion are inside it.

**Puck builds:**

- **The operator is whoever holds `Operate` over the world.** Add
  an `Operate` member to `WorldCapability`, valid only over `all`, nonexclusive, without a
  budget, consent, ceiling or kind mask, and without an addon ABI bit. Console
  and the world's own program hold it structurally. All other holders use
  the live grant table, including group membership and peer/session generation.
  Addons cannot hold it, directly or through a group. One `WorldGrants`
  operator predicate replaces the retired kind-based authority shortcuts, the
  kind-based arm of `HoldsForAdministration`, and the program-section refusal.
  A Peer operator can administer grants and author programs; an ordinary seat
  retains its own-body consent operation but gains no administration power.
  Operator authority is checked at application, never cached in a handle.
  `Operate` supplies full world administration/read/write authority; it never
  supplies Host authority. The predicate reads explicit Operate holds and
  role-qualified group holds directly, without calling a generic `Allows`
  method that calls the predicate again. Boot and admission install Operate rows
  first, then the remaining rows, refusing only each bad row loudly. Rebuilds
  validate the complete transition before changing live state. Ordinary seat seed grants must gain
  the explicit dispatch masks/budgets required of recipients, using the
  existing grant/budget machinery; no kind-based unmetered fallback survives.
  Explicit rows of revocable operators and groups carry those payloads too;
  their validity cannot depend on who temporarily holds Operate. Camera-body
  Drive uses the local-seat seed budget. Exclusive reservations bind every actor,
  and explicit operator payloads resolve against their deciding rows.
  D1 keeps ordinary seat controls and own-body consent working and exercises
  admission-template ordering and operator revocation with bound handles.
  A couch world's author may grant `seat1 operate all` explicitly.
  An admission template may grant it only at Replica on a single-subject
  `SignsDirectly` or exact-subject `OAuth` row. `Vouches`, all
  `FederatedAuthority` rows (including a named namespace), wildcard subjects
  and wildcard domains cannot mint it. A namespace is not one subject.
  Grant validation and live admission share this restriction. Live Operate
  grants and role-qualified group membership also require a remote holder's
  Replica entitlement; reject an ineligible effective holder rather than
  storing an inert operator grant or silently raising its admitted tier.
- **Every route to operator authority requires an operator (H6, M1).** Direct
  grant/revoke of `Operate`, every mutation of the grants section, and every
  group operation that can change an effective operator set require the
  predicate at the start of the transaction. This includes forming, joining,
  renaming, replacing or deleting an operator-holding group, changing its
  roles or membership source, and changing a group-kind definition that governs
  it. Resolve the before and proposed group/grant graphs together; dependencies
  of operator membership are operator-owned, including any state row feeding
  membership. Refuse a mutable non-operator membership source when installing
  an operator grant. This closes escalation through default seat Mutate/groups.
  Check the final candidate against the actor's pre-batch authority, so creating
  a group and granting it in either order cannot evade the check. A grant acquired in the batch
  cannot authorize that batch. Narrow delegated non-Operate grant/revoke still
  uses the existing administered-capability ceiling; grants-section replacement
  is never that delegation path. `Operate` alone authorizes program editing;
  possession of Mutate/rules without it does not. Revocation cuts all these
  routes, including a previously bound peer session, on its next check.
  The enforcing home is `WorldGrants`' effective-authority transition admission,
  used by direct administration, group operations and document composition,
  not three copies of the predicate. Only that admission can mint the prepared
  authority transition consumed by grant/group installation; raw table writes
  are private to it. This makes a new caller unable to omit the check. D1
  lands all of these together.
  Operator grant/revocation changes the disclosure image at the ordered tick.
  The existing delivery cursor reseeds there; a revoked operator's knowledge
  cannot be erased, so the paired guarantee starts from the revocation tick
  with equal prior disclosed history (L3).
- **The host's verbs are a separate audience.** `CommandAudience.Operator`
  admits whoever operates the addressed world. A new `Host` audience admits
  `PrincipalKind.Console` alone, for a verb that touches the machine: a host
  path, another connection's endpoint or verified identity, a wall-clock or
  resource reading, a process or an instance's lifetime. Each verb the audit
  table above lists as an operator's is assigned one of the two by that test.
  `world.peers` is the operator's and prints arrivals, the population and the
  door's refusal counts; its endpoint and verified-identity fields print for the
  host alone, as a recipient's `world.status` omits the journal. The silo's
  remote door (`SiloControlSessionHost`) admits its explicit list as now, and
  every operator verb for a session whose principal holds `Operate`; it never
  admits a host verb. [Decision 6](#6-the-compiler-holds-the-line)'s operator
  context is minted for the operator predicate, not for the console kind.
- **A writable scope is declared in the state section (M2, L7).** Call the
  authored list `writeScopes`, its grant subject `scope:<id>` and its row
  membership a structural `scope` field. Do not overload placement
  contributions or infer membership from a dollar-prefixed name. Identity is
  `(qualified scope id, writer partition, leaf)`; an ordinary row belongs to
  neither a scope nor a partition. Module qualification resolves the scope
  id once using `WorldModuleNamespace`; it does not parse a composite row name.
  Scope ids may not be import aliases, generated heads, or existing row, pool,
  family or set names. The proposed `.puck` spelling is a `scope plaza { ... }`
  block under state, containing its contract and leaf rows; an imported scope
  is selected with the ordinary qualified module reference. No author writes
  `$`. D4 adds parser, serializer and import round-trip cases before opening
  this spelling to recipients.
- **Rows are plain storage with an author-selected policy template (H5).**
  The generated-model allowlist admits name, scope membership, kind (`Int`,
  `Fixed`, `Bool`, `Text`), value/cells, capacity, bounds, overflow, eviction
  and a token domain in the same writable partition. No per-cell policy,
  program, draw, lattice, board, vector, drive gate or value-over-time trait.
  The server stamps membership, contributor identity and visibility; the
  submitted payload cannot supply any of them. The scope chooses shared
  readers or a row template such as `readers [$contributor]` plus an authored
  collector. That template is resolved into the ordinary row policy, so
  `WorldStateDisclosure` remains the one disclosure predicate. The collector
  reads a total export, never constrains a writer's next edit.
  A private scope's leaf is namespaced by a durable verified identity, not
  `Principal.Peer`'s connection generation. Remote identities use the verified
  issuer/domain and exact subject; local seats use world identity and seat id;
  addons use their installed module identity. A session without such a binding
  gets no durable private partition. The ingress stamps this identity, as
  `WorldPlacementContribution.Contributor` is stamped, and reconnect proves the
  same binding. An alias, payload principal or claimed subject cannot select
  another writer's partition.
  The author reserves a bounded number of writer slots, each with its row,
  cell and key budget. Slot assignment is an explicit admission operation;
  slot availability is public contract metadata, independent of private row
  occupancy. A writer's reserved slot cannot be consumed by another writer.
  Removing a private row does not free a writer slot; retiring an identity is
  an operator operation with the authored retention policy. R1's sealed bid
  is now one leaf in the bidder's partition, readable by that bidder and the
  authority collector, not by every bidder. A trade offer and private request
  slot use the same mechanism. No per-player scope catalog is required.
- **A scope is closed in both directions.** Every name site
  `WorldNameRegistry` lists in a submitted row names a row of the same scope or
  the submission is refused, as a function of the submission alone. No rule,
  interaction, search job, set, family or zone outside the scope may name a row
  inside it (a validator refusal the author sees), so no program's price or
  footprint moves when a guest declares, reshapes or removes. Outside consumers
  may use a scope export only through an author-declared, total binding: the
  binding supplies its missing/incompatible-value behavior and cannot constrain
  the scope's next edit. A strict binding or relation across the boundary is
  refused when the operator installs it. Otherwise a hidden outside consumer
  could veto a scope removal, defeating R4 and R5. The static declaration security closure below enforces this boundary;
  an executed read trace can add checks but cannot prove an omitted edge safe.
  A rule may consume a total scope export, priced against the scope's reserved
  quota, through a fixed interface that returns absence for missing or
  incompatible rows. It may not bind directly to a guest row's changing
  declaration. This is how an authority's collecting rule serves a lower-tier
  inbox without gaining a veto over its sender's writes; compilation reads the
  export contract, not current scope members. D4 builds that generic export
  seam and D6 wires the authored collecting program.
- **The quota reserves actual runtime storage (H4, L4).** Authors state rows,
  capacity per row, maximum text/key length and distinct keys (and writer slots
  for private input); the engine derives bytes and reports them in
  `world.budget`. Reserve the sum before boot against `StateCapacity.MaxRows`,
  `StateCapacity.MaxCellKeys` and `ArenaCapacity.MaxBytes`. Every writable
  partition has its own key intern table and byte account. The same spelling
  in a hidden outside row never saves a partition a key or changes its cost.
  Key identity includes the partition; a bare index cannot address another
  partition's table. Total exports translate names at the export boundary
  without interning them into a recipient's reservation.
  Non-scope rules, draws and authored growth are capped at the world ceiling
  minus all reservations on every runtime allocation, not only at boot.
  Rows allocate at declared capacity, including text's maximum length.
  Retired names and tombstones remain charged until deterministic compaction;
  declare/remove churn cannot reuse a quota while retaining its storage.
- **The door's order (L1).** Resolve the requested scope contract and the
  caller's grant/policy as one admission operation, with the same neutral
  refusal for absent, withheld and ungranted scopes. Only then return the
  contract and partition-local handles. A `scope:<id>` grant authorizes the
  allowed state mutation kinds within that partition, under the existing
  `WorldMutationBudgetMeter`. It is neither a whole-section Mutate hold nor
  general Observe. Shape, closure, quotas and collisions use only these
  handles, so precise errors may name only the admitted partition's remainder.
  Reshape and remove use the same door as declare and value writes.
- **The install preserves identities (M4).** Reserve a fixed ordinal region
  per writable partition after the authored catalog. Empty slots and removed
  rows are tombstones, charged to the reservation; live authored ordinals
  never shift. A replacement increments the slot generation, invalidating
  bound addon `StateHandle`s and queued reads before reuse. Compaction is
  deterministic at a transaction boundary and local to a partition: reclaim
  unreachable key names, retain live row ordinals, remap keys in that
  partition's cells, and invalidate its bound handles by generation. A full
  rebuild may remap rows only while recompiling all ordinal-bearing programs
  and reseeding replica cursors. The reservation counts against MaxRows even
  when empty. The prepared successor catalog and arena install are proportional
  to the changed partition; no outside rule compilation or validation occurs.
- **Disclosure to others (L5).** Replica readers receive declarations; row
  cells follow the stamped policy. `Omit` hides membership; `Count` and
  `Placeholder` deliberately change the excluded reader's image when counts
  change. Do not claim every withheld image is constant. Below Replica the
  scoped read door exposes only the entitled partition. Widening a scope's
  policy publishes existing rows and cells on the ordinary cursor transition;
  narrowing it cannot erase past knowledge.
- **Every rebuild preserves eligible scope state (M3).** Restart, reload,
  reset, load and source replacement all re-offer installed or saved scope
  declarations to the same typed checker against the new source contract,
  then restore cells in stable saved order. `WorldReloadDelta` cannot discard
  these rows merely because they are absent from compiled source. Shrinking a
  quota, removing a scope or changing its policy/allowlist drops incompatible
  rows deterministically and reports names only to the operator. Reset resets
  values according to the current contract while preserving eligible scope
  declarations; an operator's explicit scope purge removes them. Restored
  contributor stamps are trusted only from authenticated authority storage,
  never from recipient mutation payloads. Namespace isolation prevents R6
  squatting over an ordinary authored row.
- **One refusal that does not mislead.** The recipient's text becomes
  `'<name>' is not writable by <principal>` and the code
  `world.mutation.unwritable`, the same for a withheld row, an absent one, a
  name outside every scope and a program section. The precise cause is printed
  where it leaks nothing: to the operator's log for each refused recipient
  write, by `puck lint` and `puck compile --validate` over a document its
  author holds, and at an addon's mount, when its manifest requests a row the
  document does not declare.
- **A whole-document guard is a read of the document the proposer holds.** A
  batch's expected-definition guard, with a null, empty or nonempty state-row
  selection, is answered through the proposer's read
  door, not the policy-tier write view: a proposer the read door hands a replica
  document is compared against that document's fingerprint
  (`WorldDefinitionFingerprint.Compute`), and one it hands no document is refused
  by shape. A selection limits only state rows; it does not turn this into a
  named-input guard. The refusal then depends on the
  proposer's own tier and Observe grant, which are equal in both worlds of a
  pair, and never on what the document declares.
- **An addon binds by name.** `WorldAddonRuntime`'s cell bind records names and
  answers from the payload alone; the write answers per cell. A hidden or absent
  subject answers `AddonVerdict.NoSuchSubject` on a read and on a write, and
  `AddonVerdict.NotDisclosed` is deleted.

**The fresh review's findings, one by one.** Each was checked against the
source; none was reproduced by running.

- **F1, nobody could moderate a hosted world remotely.** Closed by D1:
  `SiloControlAdmission` admits a live remote operator to `world.peers`, with
  endpoints and verified identities withheld. `world.peer.disconnect` uses
  the server's ordered disconnect, which closes transports and command admission.
- **F2, a whole-document guard is refused for every recipient.** Confirmed in
  `WorldRecipientView.AdmitsDefinition`. The premise the finding left unconfirmed
  is half true: the policy-tier view's definition omits every withheld cell and
  the storage metadata of every row that can withhold, and keeps every row's
  declaration and every other section. That is what a replica reader is
  delivered, and the harness already compares that delivery byte for byte across
  every pair, so the fingerprint, a function of those bytes, is equal across
  them (an inference; D3's law is the proof). For a proposer below the replica
  tier the same fingerprint would confirm guesses about a document it is not
  delivered, so the guard is answered at the read door. The mutation's
  documentation, which says an absent selection hashes every row, is corrected
  in D3.
- **F3, one absent row fails an addon's whole bind.** Confirmed in
  `WorldAddonRuntime`'s cell bind, which asks `WorldRecipientView.Admits` per
  entry and answers once. The bind resolves nothing (resolution is at the
  write, which asks the view again), so the check at the bind protects nothing
  and is removed; step D2.
- **F4, the refusal text misleads on a typo.** Confirmed:
  `WorldRecipientView.Refusal` answers "is not disclosed" for a name no row
  holds. Step D2.
- **The rule keyed on the principal's kind.** D1 replaces it with
  `WorldGrants.IsOperator`.

#### The structural write footprint

**Evidence and status.** The eight findings below were checked by reopening
their cited source, including the bodies of the collectors, compiler,
validators and handlers. They are source findings, not executed reproductions.
The read-door change's policy-only `WorldServer.WriteView` and caller-stamped
`IServerLink.Query` remain useful; neither restricts what a later composer or
validator reads. `WorldDocument.TryPrepareMutation` already separates refusable
preparation from `InstallPrepared`, and `WorldPreparedMutation` already carries
prepared resources. Extend those mechanisms; do not add a parallel composer,
transform interpreter, journal or commit protocol.

**Options and ruling.** This is the design's choice, not a new owner ruling.
It follows the owner's proper-fix, one-implementation and change-proportional
work rulings, and the hazards page's preference for a type over a convention.

| Option | Evidence and cost | Attacks closed; failure |
|---|---|---|
| Add the missing fields to the collector | `WorldStateDocumentValues` deliberately walks `IDocumentStateValue`; other references have other representations | Closes named examples only. A new string, AST operand or reverse relation repeats findings 1–4 |
| Generate a reference list from the schema | `WorldNameRegistry` and `WorldDocumentWalker` already describe syntax | Useful for names and fuzz inputs, not proof of semantic reads: negative lookup, enumeration, computed addresses, implicit property names and validator branches need no reference-valued field |
| Validate a redacted copy, then commit against the authority | Existing view supplies a projection; costs a copy and a second validation | Hides reads but can accept an invalid domain removal. The later true refusal or missing public effect is still an oracle |
| Run the real preparation against a restricted access interface, with dependencies recorded by those accesses | Reuses preparation, arena transactions and the compiler; requires their read interfaces to change | Closes unlisted reads, failed resolution and reverse validation reads. Needs a complete invalidation relation and a non-refusing install; chosen |
| Give every writer its own world | The existing world model gives a complete independent validation universe | Best for a character, house or guest program. Does not provide rows in the host's tick; retained alongside writable scopes |

**Invariant.** Fix the acting principal, its grants and permitted declaration
universe, the submission sequence and ticks, and everything it may read under
the author's policy. Changing withheld cells, including their presence, must
change neither a recipient write's full transcript nor its permitted effects.
Every value that preparation uses to choose a branch, destination, diagnostic,
guard or result comes through the restricted transaction. A read it cannot
serve stops preparation before returning a value. Successful preparation proves
the *authority's* candidate valid; it does not prove a projected substitute valid.
Only that successful preparation can produce an installable result.

**Declaration existence is a separate boundary.** Recording a forbidden read
is insufficient when the *existence of the check* is secret: a hidden inverse
board can make a public token edit fail even if no hidden cell is returned.
General document writes therefore require an entitlement to the replica
declaration universe, checked from the ingress's tier and current observation
grant before any document lookup. Every lower-tier or non-observing caller is
refused that form alike, whether a hidden dependent exists or not. Its granted
scope writes remain available: its granted partition is readable whole, its
contract and reservation are explicitly disclosed, and no outside check may veto
it. An addon uses such a scope for its rows; it is not silently promoted to
replica. This revises the earlier policy-only door for arbitrary lower-tier
writes. It preserves useful low-tier input through authored inbox scopes and
closes the open declaration-existence oracle rather than describing it as
cell secrecy. D6 migrates the debug world's affected inputs and addon bindings before
D0c removes their former general-write routes. A full replica's withheld row *declarations* already differ
when one is absent; that pair is not indistinguishable at its read door.
Direct probes of an unavailable row still use the same refusal as an absent
row. A row whose declaration must be secret belongs to a separate world.

**Ingress tiers (H5).** Local seat consoles and schedules acting as a seat use
that seat's Replica entitlement while Observe holds; a schedule never becomes
the operator merely because it runs locally. A local MCP control session is
host Console; remote MCP/control is its verified peer at the admission row's
tier (Presentation by default), with no Console impersonation. Joined player
sessions inherit their admitted tier. Mounted addons have no Replica
entitlement by default and write through granted scopes. D6 migrates the
granaries feed and public-scoreboard addon bindings to shared/public writable
partitions and the auction's sealed inbox to private per-writer rows. Authored
collectors consume total exports. D6 lands those input migrations through the completed restricted substrate
before D0c switches the remaining general-write routes and refuses lower-tier
general writes by form. The migration and its protected scoped door are one
change; no input loses its only route between steps.

**The one enforcing door.** Make `WorldDocument.TryPrepareMutation` the sole
recipient preparation entry, including the present state-command fast path,
typed addon writes and detached batch composition. Its ingress is stamped by
the existing ordered domain. It opens a bounded transaction (working name
`WorldWriteTransaction`), whose private completion is the only mint for a
recipient `WorldPreparedMutation`. An installer accepts that prepared result,
not a raw mutation, definition or caller-supplied success flag. Authority
preparation uses the same algorithms with an authority access implementation.
The scope checker is a schema concern invoked inside this door, not another
admission door that can install independently.

**Declarations have a different type (H3).** Preparation receives a new
declarations-only carrier, not a redacted `WorldDefinition`. Its row descriptors
contain shape, policy and symbolic bindings, never cells, seeds, key tables,
resolved values or lazy getters. Declaration lookups themselves require the
caller's declaration entitlement. Proposed payload values are transaction
inputs, not authority seeds. `TryCompose`, candidate validation, input,
envelope, population, solids, addon and machine preparers all consume this
carrier plus the restricted operations they need. Remove `MaterializeArena`
from the write path, including retry and detached composition. Full exports
stay at explicit authority snapshot/persistence boundaries. The prepared
result may retain admitted products, never a callback into a raw authority.
Build-negative fixtures must show that a preparer's parameters, reachable
fields, constructors, callbacks and generic constraints cannot carry a
`WorldDefinition` with state cells or seeds. Merely clearing cells in a
mutable `WorldDefinition` does not meet this requirement.

The transaction exposes document and arena **operations**, not the backing
objects. Looking up a row by name or ordinal, reading its kind, policy, members,
order, count, key membership, clocks or resolved values, looking up an absent
name, and enumerating a section all go through it. Write acquisition does too.
Its restricted row handles cannot reveal a raw `WorldStateRow.Cells`, catalog,
key table, mutable span or `StateArena`; a borrowed cell span exists only after
whole-row admission and cannot outlive preparation. `IStateReader.Arena` is
therefore not the interface for recipient evaluation. Narrow the common reader
used by the existing kernels and compiler so both authority and recipient
access implement it; do not write recipient-only kernels. A bound
`IDocumentStateValue` must resolve through the transaction, never retain the
live binder's authority closure. Serializers and fingerprints receive admitted
materialized values or a read-view image, not a lazy authority-bound object.

Before returning a state row, the access implementation applies the existing
whole-row policy, including declared cell-policy capacity and containing zones.
An absent or withheld row returns the same **unavailable** failure. It is not
an empty row, zero, a successful `Try` result or an ordinary parse error.
Failure is sticky in the transaction: catching a compiler exception, clearing
a diagnostic list or returning `true` cannot mint a prepared result. Only the
door formats that failure, using a stable submission target or section name;
no dependency name, internal ordinal or diagnostic from a blocked read escapes.
Details for a completely admitted computation remain available to its writer.

**Static security closure; derived reads only add checks (H1).** Every
validation unit has an owning declaration and a declared input universe.
Derive that universe from declarations and compiled address/effect contracts;
computed addresses conservatively name the entire declared row/section they
can resolve. If a validator cannot bound a read, its universe is the whole
section, not its last observed inputs. A unit owning `hand` keyed over `deck`
depends on both declarations whether `hand` is empty or full. This extends
`StateCatalog.DependentRows`' static approach to bindings, boards, search,
rule pricing, placement derivation and host preparation. The compiler requires
each validator to supply its universe; there is no empty default.

Before inspecting derived read sets or cached results, a recipient write takes
the static transitive reverse closure of its changed inputs. Acquire each
unit's owner and declared inputs through the restricted transaction. An
unavailable input refuses identically whether a hidden cell exists, the last
run took a branch, or a hidden unit previously read the public input. This
mandatory set never shrinks using actual reads. Recorded reads can add checks
and maintain cache correctness; they cannot skip or narrow a security check.
A newly discovered read outside a unit's declared universe is an engine fault,
refusing the transaction before any effect, and requires widening the declared
universe. A deliberately new state-reading field therefore fails closed.
Partitioning declarations can improve this conservative cost and admission;
observing hidden values cannot. Keep `RecipientOracleLawTests` through D0c.

The validity cache separately records successful results and actual reads,
including negative lookups, enumeration, order and membership. Invalidate on
any changed input, settle derived changes to a bounded fixed point, and
replace edges only with the successful commit. New units always run. Removed
units retire their edges. No persisted fingerprint ledger is introduced.

**Every writer maintains validity at its own commit (H2).** Authority rule
firings (`RuleEvaluator.Commit`), clocks, draws, value installs, operator edits
and recipient writes share the dirty-unit maintenance boundary. Authority
writes run their dirty units with authority access before committing arena,
host participants and index together. Failure rolls the proposed transaction
back: a rule firing reports a named validation fault and does not fire; a
clock/draw advance faults its owning tick transaction; an operator install
refuses before adoption. This deliberately strengthens rule/tick semantics:
no committed invalid intermediate world is left for the next player to repair.
The fault detail is operator-only. There is no deferred authority validation
charged to a recipient and no hidden dirty-bit-based refusal at its next edit.
At each successful commit the index agrees with a full authority validation.
D0b4 exercises interleaved rule firings and player edits, as well as plain
document installs. Ordinary authored consequences of a rule remain within the
policy's permitted observations; this rule promises no hidden scheduler channel.

**Whole-world preparation reads (M5, M8).** Preparation has no raw host stores.
Each dependency below becomes a transaction operation or a declaration-only
reservation before its recipient route is enabled. This is part of the write
guarantee, not an exemption postponed to A16 (body observation itself remains
that separate work).

- the envelope's `TryFit` and `ExceedsBootDerivedFaceReservation` count restricted
  deals at declared capacity, including their maximum face demand, regardless
  of current hidden members. Public placement edits use that reservation.
- `Population.CanInstallFields` and `CanReplaceKit` use declared capacities
  for shared totals and restricted population/body reads for actual compatibility.
  A hidden-dependent compatibility check conservatively refuses by its static
  universe, never by its last read set. Author-declared public population
  facts can be read normally; the engine never silently declares them public.
- `TryBuildSolids` uses declared, admitted geometry. Restricted dealt geometry
  retains decision 4's facet prohibitions; hidden occupancy cannot enter an
  otherwise public geometry budget. Adjacency checks use admitted declaration
  contracts; `AdmitsSpatial` reads only entitled geometry and reserved bounds.
- Machine state and memory bindings, addon plans and input selection
  (`Input.TryPrepareAll`) read through transaction handles. External device
  availability has a separate host failure, but a hidden memory row or private
  input selection is a semantic dependency, never disguised as host failure.
  Staged plans contain admitted values and reserved resources, not raw stores.

A restricted-deal world must still accept a public placement edit within its
declared reservation. A full-world conservative unit is an intermediate closed
substrate, not the final admission contract for that case. D0b2 partitions
binding/relation/input checks and D0b3 covers these host preparers before D0c.

**Prepare once and land what was prepared.** Parse payload-only syntax,
placeholder ids and program-kind prohibitions without a world read. Any
schema-dependent parsing (`text` versus `vector`, for example), defaulting,
classification such as `IsStateCommand`, grant-subject resolution, composition,
transform compilation, current and incoming bindings, guards, settling,
validation and all refusable resource preparation follow inside the transaction.
Batch members share one candidate and one journal; a later member sees earlier
members' changes, and each handle rechecks candidate policy. A refusal rolls
back arena and enlisted host state and publishes nothing from preparation.
Grant changes cannot authorize their own batch before installation. Future
state readers installed by an edit (bindings, knowledge, witnesses, triggers)
must acquire their sources through the restricted compile context too; checking
only values evaluated immediately would leave a delayed disclosure door.

Prepare and install occur without another mutation or tick between them in the
ordered domain. A reusable preview is only input plus read-view guards; it is
prepared anew at application. No global authority revision is returned as a
staleness oracle. Grants and policy are checked at application, including
revocation since preview. Install consumes the exact staged writes, products
and reserved resources; it neither runs a hidden validator nor calls an
unrestricted kernel again. Unexpected install failure is an engine fault, not
a hidden-dependent ordinary recipient verdict. External effects are staged as
the existing prepared addon/machine plans are, and delivered after commit.
Receipt and echo formatting is shared by state and document writes. Versions
come from the recipient's delivery cursor as before.

**Guards have an explicit read universe.** All expected-definition guards,
including `ExpectedStateRows: []`, hash the proposer's *read* view at its actual
ceiling, or refuse by shape when no replica document is available. Named input,
cell and spatial guards must likewise describe a read the proposer is entitled
to receive; a write grant never upgrades their ceiling. In a scope, a guard may
hash only the explicitly granted partition image. Engine-built batches,
including `WorldServer.Reflow` and its `ExpectedInputs`, use the same admitted
read universe; their current reference collector is not exempt (L6). State-row selections do not certify
the rest of a definition. Do not compose the whole policy-tier definition to
hash a small selection: named-input hashes traverse only selected inputs and
their admitted resolution, while whole-document hashes consume the shared
disclosed image. This preserves D3's useful whole-document guard without its
empty-selection loophole.

**Where the construction stops.** The access implementation, successful-result
mint and install primitive are trusted; a deliberate raw capture, reflection
escape or incorrect primitive remains a trust-boundary review obligation.
Move preparation algorithms behind interfaces that cannot acquire the
authority, and apply decision 6's acquisition wall to their constructors,
fields, generic constraints and callbacks. A facade whose `Arena` property
still returns the store proves nothing. Build-negative fixtures must reject
these escapes, including an `IStateReader` alias and a live-value binder.
The runtime fault marker additionally prevents an accidental swallowed failure.
Host resource exhaustion and wall time remain outside the stated guarantee;
semantic quota, rule-work or relation failures do not become resource exceptions
merely because hidden rows influence them. Scopes reserve those budgets up
front. General arena-growth work must be localized or admitted against declared
reservations before the relevant write path is enabled. Authored authority
rules can still reveal, and body/spatial observation needs the read door; a
write transaction does not fix an unrelated read handler.

**The eight findings and their witnesses.** Symbols identify the reopened
source; the candidate transcripts remain to be executed in D0a. Finding
numbers here are W1–W8 to distinguish them from rd2's F1–F4 above.

| Finding | Reopened evidence and correction | Structural closure | Harness and step-0 fuzzer witness |
|---|---|---|---|
| W1, high: references outside document-value holders | `WorldStateDocumentValues.Finder.Enter` handles `IDocumentStateValue`; `WorldDefinitionFingerprint.CollectPlacementStateInputs` adds only deal/variant rows. `WorldDefinitionValidator.ValidateProperties` looks up the property's implicit same-name row. Board, response, HUD and overlay validators also resolve names outside that collector | Every semantic lookup in the real validator or compiler goes through transaction access, regardless of the field's spelling. New delayed bindings acquire sources there too | Typed `SetProperty` and nested placement/kit/creation/HUD/camera/overlay/host edits naming public, absent and withheld rows; malformed and valid values. Compare decisions and subsequent public bindings, not only text |
| W2, high: transform indirections and failed resolution | `WorldRecipientView.AdmitsTransform` returns true on failed `RuleCompiler.TryCollectTransformRows`; `RuleCompiler.ResolveCellRef` checks kind and `DeclaresCell` before that collection succeeds. `StateTransform.Transfer.Subjects` omits its key | Resolution itself is restricted; unavailable is sticky before kind or key lookup. The compiler and kernel share that context; no preliminary collector grants permission | `$cell:`, `$zone:`, `$expr:` and computed-key forms with present/absent keys, wrong kinds and nested expressions; vary only withheld membership with declarations fixed. Generate both direct and batched forms |
| W3, high: declaration omissions | `AdmitsDeclaration` checks domain, row readers, inverse and phase only; `ValidateDisclosureRow` reads knowledge source/mask/positions and cell readers; `ValidateWitnessRow` resolves the witness | Incoming declaration compilation, including its deferred source bindings, reads through access. Scope allowlist rejects these features from payload alone | Replace an admitted row with each knowledge, witness or per-cell-reader form. Include a public-source success control and inspect later delivery for copied values |
| W4, high: reverse dependencies | `StateCatalog` builds `DependentRows` only from `KeysOf`; `ValidateDerivedBoard` compares token/code membership and order. Its shown error names the board and codes, **not the hidden keys themselves**. The packet overstates that text claim | Reverse invalidation comes from check reads, including the owner and negative lookups. All checks that could veto the change run restricted. Lower-tier general writes refuse by shape; scope checks cannot have outside dependents | Change tokens while a fixed hidden inverse's code membership varies; remove/reshape public sources of knowledge, phase, readers and witness checks. Add absent-dependent pairs below replica, and assert public state unchanged on refusal |
| W5, high as a lower-tier oracle: empty selection | `WorldDefinitionFingerprint.Compute` filters `StateRaw.World` only; other sections remain. `DisclosedPlacements` deliberately emits one back per hidden cell under `Placeholder`. **One versus two backs is permitted disclosure to the seat in the packet, not a valid hidden-equivalent seat pair** | Every expected-definition form uses the actual read universe, never the write view. Null, empty and nonempty selection have identical ceiling admission | Recast the example as a Frames/Presentation writer not delivered those bytes; all forms refuse identically. Replica controls use their actual delivered fingerprints: equal for `Omit` hidden-only changes, different for a permitted back-count change |
| W6, medium: console pre-reads | `WorldStateCommandModule.HandleCellSet` branches on `FindRow(server.ReadView(context))`; `WorldRowCommandModule.TryReadMutationRow` distinguishes `Withheld` from missing before admission; `WorldServer.ReadView` throws for unavailable sessions | Console submits raw edit intent; schema-dependent parsing/read-modify-write occurs inside preparation. Handler needs no write-side `ReadView` call | Multiword text, invalid vector and row set/remove/step/add through the real console for seats and session tiers, with Observe revoked too; compare synchronous reply, queued echo, outcome and stream survival |
| W7, medium: route before admission | The former dispatch path inspected the installed row before admission and emitted denials separately on its two routes | `WorldPreparedMutation.TryPrepare` now admits before classification. `WorldDocument.DenyMutation` emits the admission refusal on both routes. Classification still reads installed rows directly | Generate/upsert inside a batch also naming a placeholder, both state-only and mixed edits; same input, hidden row present/absent; compare the complete ordered transcript |
| W8, medium: adjacent read doors | `WorldPopulationCommandModule.DescribeParked` reads population/body poses and its Anyone handler ignores context; `WorldHudCommandModule.ResolveDynamicTemplate` looks up the row before checking withholding | Parked read becomes a caller-stamped query over the existing body-observation policy, counts and deadlines included. HUD validation and substitution share a restricted read operation; unavailable/absent answer alike. Decision 6 prevents a handler obtaining population/authority state directly | Parked bodies with denied Observe and differing poses/membership; HUD placeholders naming withheld/absent rows and cells. Drive actual registered handlers for all ingresses; no GPU needed |

The other W1 locations are leads, not proof that every submitted payload passes
all intervening checks. In particular, a later audience check may already
refuse a proposed knowledge copy. The design does not rely on the review's
claimed final success or exact text: D0a runs each smallest case and records
the actual first differing event, or marks the premise refuted. No quotation
from that packet is used as source truth here.

**What step 0 lacked.** Enumerating verbs is not enumerating their semantic
arguments. The current fixture contract accepts whole transform JSON strings
and a short session-action list; it does not generate every nested mutation,
declaration and guard form. D0a extends that existing harness, not a second
oracle: use the mutation/model registry and grammar to construct typed payloads,
substitute fixture-owned public/absent/withheld names at nested sites, and
compose batches of conflicting members. Name metadata guides coverage but is
not the enforcement mechanism. Cover negative lookups, enumeration, delayed
consumers and every early-error path; include a deliberately new field that
reads state without adding any collector entry. The restricted door must
catch it and the paired transcript must catch a deliberately bypassed door.
Seeds and shrinking operate on payloads chosen independently of either world.

Validate both variants before each probe and check their permitted images agree
at every compared tick; do not silently normalize allowed differences away.
Declaration-absence probes use a principal not delivered those declarations;
member probes keep declarations fixed. An authorized success control prevents
an all-refuse implementation from passing. Compare failed writes' subsequent
public reads and deliveries too. Preserve minimized witnesses as authored
fixture scripts or typed harness actions. Add socket-peer, mounted-addon and
traveller capture through step 0b before claiming those ingresses proved.

**Cost, counted rather than timed.** No per-recipient arena or full document
copy is retained. The world retains successful validation results and their
dependency edges; one in-flight edit retains its changed paths, journal entries,
admitted handles and staged products. A warmed cell write pays for the resolved
handle, live policy/grant checks, the kernel's reads/writes and invalidated
validation units. Admission is cached only within that candidate generation;
bound addon handles never cache permission. Reuse scratch and interned source
ids; do not allocate a set or closure on each scalar read.

Count accesses, denied accesses, static and derived dependency edges visited/replaced/retained, validation
units executed/reused, rows/cells copied, journal bytes, compiled rules and
allocated scratch bytes separately, in operator diagnostics only. Target work
is proportional to touched inputs and their affected checks, not total hidden
cells or recipient count. A real global invariant may cost the section; the
counter must say so. The baseline includes `MaterializeArena`'s per-edit full copy,
`ValidateStateTransaction`'s whole validation for a HUD-bound value,
`AdmitsSpatial`'s global traversal and `WorldContributionState.Declare`'s
placement scan (M6). Each is counted explicitly, then routed to partitioned
validation or declared reservations; no timing-only claim hides its work.
Static security closure visits are counted separately from executed validators;
hidden-dependent closure cannot be optimized away to meet a work target.
Cold installation builds the index once; adding a new
validator invalidates its units. D0b2–D0b4 measure sparse cell writes, HUD-bound writes, document edits,
rule firings, transforms, mixed batches and dependency fan-out at 1 and 400 unrelated rows and 1 and
1000 submitting recipients. D5 measures scoped declaration/removal against the
same growth: zero outside rows validated, zero rules compiled and zero full
arena copies. These are acceptance measurements to run during implementation,
not measurements this documentation lane claims to have made. If a kernel
cannot use the restricted reader or a validator needs a full scan, the named
step fixes that engine limit; a second evaluator or field whitelist is not its
fallback.

**Rejected and watched.**

| Approach | Verdict | Reason | What would change it |
|---|---|---|---|
| Hiding a scope's declarations from readers its policy excludes | Reject for now | A replica's row ordinals follow the authority's catalog (`WorldStateDisclosure.DisclosedFields` keeps an undisclosed field's row for that reason); omitting rows needs recipient-scoped ordinals in every delta | An authored world that needs private row names in a shared arena, rather than in a world of the declarer's own |
| A scope per principal, minted at admission | Reject | A principal token is not durable (a peer's generation changes at reconnect), and one catalog cannot hold a scope per visitor | Nothing; a per-player scope is a world the player owns |
| Rules inside a scope | Reject | Decision 3: a rule reads any row and fires as the world | Nothing; a guest's program runs in a world the guest operates |
| Per-writer private partitions inside one scope | Adopt (H5) | Default-tier players need sealed bids and private requests in the host tick; per-writer policies and reservations prevent cross-writer reads and quota oracles | D4–D6 prove private-policy stamping, reconnect ownership and isolated storage |
| Vector rows and value-over-time traits in a scope | Watch | Each needs a reserved share of a section-wide budget (vector bytes, per-tick work) before it can be allowlisted | An addon that needs them |
| Guards over a policy-tier image the proposer cannot receive | Reject | D3 uses actual read entitlement or the explicitly granted partition image; a write view never substitutes for a read view | An explicit authored disclosure, not another collector |

**Review disposition.** H1–H6 and M1–M8 are accepted and settled above;
their implementation checks are in the single [build order](#the-build-order).
L1 uses joint grant/policy admission; L2 fixes observer tiers in D6; L3 reseeds
the cursor on operator transitions; L4 derives bytes from author-facing units;
L5 states Count/Placeholder and policy-widening disclosure; L6 includes engine
batches; L7 renames the proposed mechanism writable scopes. These are design
closures, not claims that any new protection has landed. The options analysis,
unavailable-read failure, prepared-once install, explicit guard universe,
operator/host separation, field-split peer reporting, neutral refusal and
max-length text reservation remain. Reopened source confirms the cited risks;
the paired witnesses are owned by D0a, not claimed as executed by this memo.

### 4. The physical layout follows the hidden policy

**RD-03 proposed fix, implemented at the sweep:** source-row membership owns the presence of dealt children.
A placement-only removal or undo must be reconciled on that tick's deal sweep,
independently of any later hidden-only edit. The single enforcing place is
`WorldTick.SweepPlacementDeals`: its skip decision must include the installed
placement projection, and retain only the projection its own install leaves.
This makes every template recheck child-only edits through the existing `Deal`
allocator and `WorldPlacementDeal.Reconcile`, including public membership with
a restricted variant row. It adds no recipient-door repairs or second allocator.
The probe removes a readable child from each kind of template in every variant;
only its `late` variant then changes hidden state. `puck disclosure` must first
distinguish the missing/recreated children, then compare equal with this change.

A dealt child stands on its slot, and the authority's slot is history (the
lowest slot free when the cell arrived). A reader shown a visible card at slot 1
learns a hidden card holds slot 0, and a card lifted out of its slot stands
wherever that slot puts it.

**Puck builds:**

- **A child stores its displacement; one resolver composes its slot.** A dealt
  child's `position` is its displacement from its slot (zero as the deal stamps
  it, a lift or a turn once a mover writes one), and its local frame is the
  slot's offset, computed in fixed point from the authored distribution and the
  world seed, plus that displacement
  (`WorldPlacementFrameCompilation.LocalFrame`). The frame table, the spatial
  index and canonical surface frames all resolve a parent chain through that one
  composition. The row's C# `position` carrier is `WorldPlacementTranslation`,
  an opaque value with no vector getter or outward conversion: reading its
  components or using it as a position does not compile. Audio emission anchors,
  slice framing, animated roots and editor nudges read resolved frames too;
  an animated registration requires the delivery's frame table. The slot's
  offset never passes through a float and no reader
  recovers a displacement by subtracting one. A mover writes displacement:
  reflow's candidates go through the inverse (`StoredPosition`) directly into
  exact Q48.16 storage, whose JSON numbers round trip without binary32 narrowing;
  world-space editor edits use `WithWorldPosition`, which undoes the parent and
  slot through the same inverse. A position
  upsert writes the row's `position` member, which is the displacement.
- **Re-dealing in the view, transforms included.** For a template whose dealt
  row the view does not read whole, the kept cells are dealt in the disclosed
  row's cell order into slots `0..n-1`, and each kept child changes only its
  slot (`WorldStateDisclosure.DisclosedPlacements`): it keeps its displacement,
  yaw and scale, which are its own, and its frame is the re-dealt slot's exact
  offset plus that displacement. Nothing of the authority's slot reaches it, not
  even the rounding a float would give that slot's offset (a step of
  `128 + 2^-16` metres rounds at slot 3 and not at slot 0). Zero, one and many
  hidden cells give one layout, and a lifted card is shown lifted from the slot
  the reader is dealt. A placement authored under a child the view is not shown
  is absent with it. The view's placements are the ones `world.placements`, the
  wire projection, `world.reflow.preview` and every spatial fingerprint read for
  that recipient.
- **A hidden face is masked whichever row hides it.** A template whose variant
  row the view does not read whole is in the reading even when its dealt row is
  read whole: its children keep their slots (a row the view reads whole shaped
  them) and each child whose variant cell the view may not read shows the
  template's own prototype. Both the dealt and the variant row are recorded as
  rows the placements read, by declaration.
- **A restricted template's subtree carries only what the re-deal discloses.**
  A template whose dealt or variant row can withhold from any principal (a
  non-public row policy, a cell policy its storage admits, or a containing
  ordered zone's), its dealt children and every placement authored under them
  carry only the members the re-deal already discloses: the identity, parent
  and slot, the prototype the disclosed face selects, the transform the deal
  stamps, and presentation composed over it (`distribution`, `mirror`,
  `surface`). Every other member, `solid`, `grip`, `spatial`, `region`,
  `emission`, `inhabit`, `attach`, `faceSources`, `respond`, `board`,
  `contribution` and any member the placement model gains later, is refused by
  name (`WorldPlacementDeal.UndisclosingMembers`, an allowlist read from the
  generated model shape, applied by `WorldDefinitionValidator` in whole-document
  validation over each placement's parent chain and in the sweep's
  `TryValidateDealtChildren`). Its children's transforms are their
  displacements, so `preserve.transform` is admitted on it. A hidden child then
  has no collider, no clearance, no
  region, no body and no light, so no body, reflow, probe or spatial guard can
  meet it, and a visible child's physical absence tells nothing because no
  placement in the subtree is physical. The table, the felt and the public
  piles stay solid as ordinary placements; the cards are posed, which is how
  the card tables already work ([card tables](open-items.md#card-tables-in-the-board-game-room)).
- **`Placeholder` is a look, not a thing.** Under `hidden: Placeholder` the
  view deals an anonymous child for each hidden cell at its place in the
  re-deal (the row's order, which the policy discloses, never the authority's
  slot, which hidden arrivals shape), with the template's own prototype and no
  variant: positions and count are what the policy discloses, and a reader the
  row's own policy excludes is shown no backs, as its observation shows none.
  Its id is `<template>/$back$<slot>` (`WorldPlacementDeal.PlaceholderId`,
  minted through `GeneratedName`, with no key in it; a placement id may not
  carry `:`), stable while the hidden cell holds that place. It is not a
  target: a pick on it answers the template's id (`WorldPickTarget` holds
  `WorldPlacementDeal.PickedId`), and a submission naming it is refused by name
  as a placeholder before policy (`WorldRecipientMutations.NamesPlaceholder`,
  code `world.mutation.placeholder`), because the authority has no such row.
  Under `Count` placements are as under `Omit`; the count travels in the
  observation row's `HiddenCount`, where it already does.

**Why:** renumbering on the authority is rejected for the reason
`WorldPlacementDeal` records: it would shuffle every later instance when a cell
leaves. Renumbering in the view costs nothing on the authority and is a pure
function of the disclosed row because a child stores only what is its own: a
position that held `offsets[slot]` plus a displacement in one float would keep
the leak, and so would reconstructing the displacement by subtracting the
slot's offset from it, since the float's rounding depends on the slot. Storing
the displacement and composing the slot's fixed-point offset in the one frame
resolver costs one offset lookup per dealt child per frame compilation (the
region's literal offsets are derived once per distribution and seed, while
state-bound lattice steps bypass that cache and the caches retaining their frames,
`WorldPlacementDeal.Offsets`), and it is the same composition every other
parent-chain reader uses, so the view, the spatial index and the renderer
cannot disagree about where a card stands. A separate displacement member beside
`position` remains rejected: `WorldPlacementTranslation` replaces the carrier
of that one member, preserving state references and exact runtime writes while
making raw-vector reads unavailable. A spatial guard includes the world seed,
which positions both Noise/Scatter distributions and dealt slots even when its
census ids stay equal (`WorldDefinitionFingerprint.ComputeSpatial`). A member that acts or shows at the
authority's slot is an oracle no view can close (a collider the authority
solves against exists for every body in the room), so nothing under the
template may author one; that is a validator refusal rather than a
per-recipient collider, because the authority has one collider set. The
refusal is an allowlist rather than a list of physical facets because every
earlier gap was a member the list did not name (an inhabit on a preserved
child, a solid on a placement authored under a child): a member the placement
model gains is refused until someone decides it discloses nothing. An interactive back would need recipient-scoped ids with
generations and a reverse translation into authority targets; a back the
policy only shows is a look, and a visible card's own id already works, so the
plan drops the physical form rather than building that translation.

### 5. A delta is a difference of views, owned by the stream

Step 1 implementation invariant: `WorldDeliveryCursor` alone owns the hashes
and sequence of a stream's delivered images. `WorldRecipientView` reads live
sources at its fixed ceiling; neither a read nor the world-owned
`WorldDisclosureImages` table changes a cursor. Images copy vector bytes before
hashing. Committed value and clock writes carry interned key bits; membership,
order and audience changes conservatively invalidate a whole row. The shared
table indexes `readersFrom` and containing-zone dependencies at installation
and after structural changes. Resource measurements are visible only in the
operator's `world.budget` answer.

Implementation choices for step 1:

- Audience classes are the current policy-admission bit vector, tier and
  authority bypass. An authority reads storage metadata that a recipient with
  the same current admission bits cannot read on a declaration that can
  withhold cells, so their definition caches must remain separate;
  policy dependencies include containing ordered zones and their `readersFrom`
  rows. Structural and audience writes rebuild that index. The world owns it;
  a cursor holds only hashes, sequence and any pending observation headers.
- Replica definitions carry readable cells. Their immediate snapshot primers
  carry every restricted row's observation header in `FieldCellDelta.Observation`,
  including a zero hidden count under `Omit`, `Count` or `Placeholder`. A session
  pairs later document and placement-state replacements with the same
  non-consuming primer, so replacing its arena cannot erase those headers or
  defer count-dependent sequence work to a quiet tick. A Resume already inside
  a snapshot uses that snapshot rather than rebuilding its borrowed carrier.
  Later count, order and deletion changes compare the complete image.
- A key-skip proof covers only its current change window. A cached image still
  current at a window boundary may use the next window's keys; an image whose
  version already differs must recompose. Before any change carrier is bound,
  no key skip is available. Presentation physical fields use the same admitted
  rows as their field projection, and an unchanged disclosed definition still
  checks the feed's carried state-clock anchors.
- Typed receipts use the actor's sole attached delivery cursor. With no stream,
  or several streams at different ceilings and no stream identity on the
  submission, the sequence is zero. They never borrow an authority counter or
  choose a higher-fidelity stream. The activation identity remains the world's.
- A traveller compares against the projection its sink last delivered, with
  the feed's held clock anchors and sequence. The raw definition reference is
  only an invalidation hint for when to compare content. Route, live principal,
  admission tier and Observe grant checks still apply on every delivery.
- Presentation feeds retain shared typed observation rows, removing state from
  their retained JSON diff tree. A read may warm the shared image cache but
  changes neither a cursor nor a feed. A changed typed reference is only an
  invalidation hint: the transient state diff must contain a semantic change
  before the cursor advances, even after an obsolete image is reclaimed.
  The earlier composer overloads remain
  for existing callers until the read and retirement steps migrate them.
- The shared table's work/byte counters are visible only in the operator's
  budget and `world.counters world.disclosure`; a recipient receives a fixed
  message in their place. Snapshot cadence alone does not advance a document
  sequence; delivered document, state-image and anchor changes do.
- A stream's document identity is a digest of the audience's frame (the
  disclosed document less its state rows) and every row's image and cell-free
  skeleton, all functions of the recipient's view. Between two moves of
  `StateArena.DisclosureRevision` a row changes only by a keyed cell write
  (`StateArena.MarkVersion` names the keyed columns; every other write moves the
  revision), so a cell-only tick refreshes the image digests of the rows it
  wrote and a revision move also their skeletons. The frame is composed from the
  whole document again only for a new source, a changed policy index, or a
  change to a withheld row the disclosed placements read
  (`WorldStateDisclosed.PlacementReads`, recorded as `DisclosePlacements` reads).

`DeliveryReviewLawTests` checks physical deltas at both tiers, administrative
and missed-window readable writes, clock corrections without source
observations, whole-row storage metadata, and policy-shaped replica primers.
Its restore cases check a document replacement outside the tick, where no
ordinary snapshot follows to repair missing observation headers.

**Puck builds:** two objects where the first draft had one. The view
([decision 1](#1-one-capability-the-recipients-view)) composes; a
`WorldDeliveryCursor`, owned by each delivery stream (a session sink, a
federation projection sink, each traveller stream of one principal), remembers
what that stream delivered. The cursor holds, per row the recipient does not
read whole, the content hash of the row's last delivered `Image`; images are
immutable and shared by content in one table, so two streams or two recipients
whose images agree hold one copy, and a stream's own memory is a hash per
restricted row. The cursor also holds the recipient-scoped **sequence**: the
version the recipient is shown.

A tick's delivery for a stream is `view.Advance(cursor, changes)`:

- For a row the recipient reads whole, the snapshot's own replacement passes
  through, as today.
- For a restricted row the tick touched, the view composes the recipient's
  `Image` of the row (readable cells with their values; under `Count` the
  hidden count; under `Placeholder` the backs in order), hashes it, and sends a
  replacement only when the hash differs from the cursor's; a removed readable
  key, a changed count and a moved back all differ, and a hidden-only change
  under `Omit` does not. The image is the whole observable row, so no changed
  key needs classifying after it is gone.
- A policy move (a `readersFrom` row written, an ordered zone moved) marks the
  rows that depend on it through an index built at each install, not a scan of
  every row per tick.
- A delivery is the only thing that advances a cursor: a definition delivery
  stores the hashes of every image it carried; a snapshot delivery stores the
  hashes it replaced. Composing a view for a read, a query, a prototype
  authorization or a diagnostic touches no cursor. A `Resume` after a withheld
  span is a definition delivery of `view.Definition` through the cursor, like
  any other.
- **Metadata is the recipient's.** The version a recipient is shown, on
  deliveries, on typed mutation outcomes and in projection provenance, is its
  cursor's sequence, which advances only when the stream delivers; the
  authority's install sequence and `Population.Revision` never cross to a
  recipient. A definition install whose projection for a recipient is equal
  to what its cursor last delivered delivers nothing and invalidates nothing;
  a traveller stream's validity compares the recipient's projection identity,
  its admission and its route, never the authority definition by reference.
  Delivery count and size per tick are then functions of the recipient's view
  alone.

**Cost.** The change carrier gains the set of keys each tick changed (a
bitset beside `m_snapshotRows`), so a restricted row is recomposed only when
one of its readable keys, its count or its placeholder order can have moved,
and otherwise skipped without a compare. Composition of one row's image for a
tick is cached by (row, audience class), where the audience class is the set
of policies that admit the recipient, so a thousand spectators with the same
rights compose once. The retained state per stream is a hash per restricted
row; the shared image table holds one copy per distinct image. The measurement
in step 1's check sizes this: N spectator sessions on a restricted row of M
readable vectors with one hidden cell written every tick, read in `world.budget`
as per-tick disclosure work and bytes, with no per-recipient arena, no
per-recipient JSON tree for state, and the presentation feed's whole-projection
diff bounded by the same shared images.

**Why:** the comparator-with-forget design cannot be seeded by its callers
without each caller remembering to, which is the door-by-door pattern again,
and one image per principal cannot serve two streams of one principal or
survive a read composing the same view: the cursor is the stream's because the
delivery is the stream's. Defining the delta over the recipient's complete
image rather than over readable keys is what keeps `Count` and `Placeholder`
live and makes deletion a difference like any other. Recipient-scoped sequence
numbers are the only way a version can be shown to a recipient at all under
the guarantee; the authority's counter counts hidden installs. The vector
aliasing finding disappears with the private, shared, immutable images.

### 6. The compiler holds the line

The first draft proposed a member-level analyzer over seven terminal members.
The review showed it unsound: `server.ArenaHost.Arena`, `server.RuleHost`
through `IStateReader`, a generic constrained to `IStateReader`, and a bare
`server.ViewFor(Principal.Console)` all reach the authority without naming a
listed member, and an allowlisted member can hand a lambda to anyone. The
guard has to govern **acquisition**, with the type system, and leave the
analyzer only what it can decide soundly.

**Puck builds:**

- **The authority is `internal` to `Puck.World.Server`.** `WorldServer.Definition`,
  `Configuration`, `ExportDefinition`, `MaterializeArena`, `Arena`, `ArenaHost`,
  `RuleHost` and `Document`, and the types `WorldArenaHost` and `WorldRuleHost`
  themselves, become `internal`. IVT001 forbids every `InternalsVisibleTo`, so
  this is a wall `Puck.World`, `Puck.World.Console`, `Puck.World.Addons`,
  `Puck.World.Machines`, `Puck.World.Silo`, the extensions, the benches and the
  tests cannot cross by any spelling: not a property, not an interface
  implementation, not a captured delegate, since the capturing code would have
  to compile outside the server. A law in `Puck.World.Server.Tests` enumerates
  the server assembly's public surface by reflection and asserts that no public
  member returns or exposes `StateArena`, `IStateReader`, `IEffectHost`,
  `WorldArenaHost`, `WorldRuleHost` or `WorldDocument`, and that the only public
  members returning a `WorldDefinition` are `WorldRecipientView.Definition` and
  the operator authority below; reflection sees every public member, so this
  check is complete for the surface it describes.
- **Two public doors.** `WorldRecipientView`, minted by the server from an
  ingress-stamped carrier (`server.ViewFor(CommandContext)`,
  `ViewFor(SubmissionEnvelope)`, the session's and the addon's own) at that
  carrier's ceiling. And `WorldOperatorAuthority`, the authority's public face
  (the live definition, the export, the arena for the save, the history, the
  replay and the benches), handed out exactly twice: to whoever constructs the
  server (the composition roots, the silo grain, a test) by the factory that
  constructs it, and to an Operator-audience handler through `OperatorContext`.
- **The operator shape.** `IWorldConsoleAuthority`'s `TryResolveServer` is deleted.
  A handler registered with `CommandAudience.Operator` is built through
  an operator factory on `CommandDefinition`, whose handler delegate receives an
  `OperatorContext` carrying the `WorldOperatorAuthority`; `OperatorContext`'s
  constructor is `internal` to `Puck.Commands`, so only the registry mints one,
  and only for a verb it refuses to every principal but the console before the
  handler runs. Every other handler receives the `CommandContext` it does
  today, from which `TryResolveReadView` yields the view and `TryResolveLink`
  the submission link.
- **DISC001, what the analyzer can decide.** A type implementing
  `ICommandModule` declares no constructor parameter, field or property of type
  `WorldServer`, `WorldInstance`, `WorldOperatorAuthority`, `StateArena`,
  `IStateReader` or `WorldDefinition`; it reaches a world through
  `IWorldConsoleAuthority` and, for an operator verb, `OperatorContext` at
  dispatch. This is a syntactic rule over type declarations, decidable and
  complete for what it states; it is the rule the 47 files that take a
  `WorldServer` today violate, and it is what stops a composition root from
  wiring the authority into a module by construction, which is the lambda
  capture the review named (`WorldCaptureScheduler.cs:196`,
  `WorldSiloApplication.cs:45`). The capture scheduler and the silo's authored
  commands are operator paths and take the `WorldOperatorAuthority` instead.
- **No second predicate.** `WorldStateDisclosure`'s static `CanRead`,
  `CanReadCell`, `CanReadRow`, `WithheldFrom`, `Compose`, `Disclose`,
  `PlacementProjection`, `FieldProjection` and `ValidateBindings` are deleted
  with the class, and their bodies become the view's members. A door cannot
  re-implement the policy because nothing exports the pieces.

**What this does not do, by design.** Code inside `Puck.World.Server` is the
authority and can read anything; its sinks and codecs are kept honest by the
signature rule (a composer for a recipient takes a view) and by review. A
composition root that holds `WorldOperatorAuthority` and chooses to pass its
`Definition` into a closure for a non-operator module is writing trusted code
against the plan; DISC001 refuses the ordinary shapes of that (a module field
or parameter of the authority's types), and the rest is the review every
engine change gets.

**Why not an analyzer alone:** an analyzer rule over member references is an
invocation-symbol rule, not information flow (ENV001 is the same shape,
`src/Puck.Analyzers/EnvironmentReadAnalyzer.cs`); it cannot follow an
interface property, an inherited alias or a delegate. **Why not a separate
project alone:** a project boundary hides types, but Server's public API would
still carry the authority members, and SDK project references flow
transitively; `internal` inside the one assembly that is the authority is the
boundary the language gives, and IVT001 is what makes it hold.

## What serves authors

| Decision | What an author gets | What an author owes |
|---|---|---|
| 1, 2. One view, everyone a recipient | One declaration (`visibility`) decides every door: console, HUD template, wire, replica, federation, addons, agents, diagnostics, batch guards, reflow | Nothing new; the shared screen remains the room's |
| 3. No write without reading | A write into a restricted row refuses by name, so a misauthored grant cannot leak by writing; sealed bids, hidden votes and private inboxes are the `sealedInbox` pattern in the standard: a private row per writer and a rule that moves it | A game that collects private input declares the inbox rows (or imports the template) and the collecting rule |
| 4. Layout follows `hidden` | `Omit` hides a hand's shape, `Placeholder` shows card backs in place, `Count` says how many; one declaration, observations and placements agree | A restricted template's children carry no physical facet; the table and the public piles do |
| 5. Deltas from the view | A replica or federation peer sees a restricted row move only when what it may read moves, and sees versions of its own | Nothing |
| 6. The compiler | A verb, a mod host or a codec added later cannot reach the authority by omission | A new operator verb is registered as one |

**Rules are the authority.** A rule reads every cell and can deliberately
reveal through its own write, like a binding on a shared screen. The owner's
ruling for search is different: a search output whose audience is wider than
any of its inputs is refused with `SearchWidensAudience`. Compilation shares
`RuleCompiler.RequireEnclosedAudience` with transforms; execution shares
`StateArena.ReadAudienceEncloses`, before advancing and before landing. A
refused job lands nothing. Candidate generation, judge rules, scores, chance,
enabled and revision rows all belong to that dependency set, maintained by
`ArenaSearchPlan.Dependencies`. Their audience-check work is priced by
`RuleCompiler.DisclosureCost`.

`WorldSearchCompilation.JudgeRules` excludes a rule only when its gate is
provably false at a positive search ply (besides the existing host-fact
exclusions). Thus a live-only reveal into a side's memory does not make the
memory's original authority inputs speculative dependencies. Unknown gates
remain in the judge. The skirmish's fair job uses blue's memory for candidates
and judging as well as scoring; its sharp job remains authority-only. Both
`search evidence` scenarios refuse an attempt to publish the sharp output.
The runtime law narrows a cell's audience during an open search, verifies no
landing, restores the audience and observes a fresh completed search.

**The games decide, and the harness is the definition of done.** Four authored
games in `worlds/debug/`, played at the tables room's tables, are the fixtures
the plan is proved on (the owner's direction): a sealed-bid auction (what a
private write needs), a card game with a draw pile, a discard pile and hands
(`Count`, `Placeholder`, the dealt layout), a social-deduction game with
private chat channels (roles, who may talk to whom), and a small fog-of-war
skirmish (hidden positions, physics, a `search` over what a side can see). Each
is a game an author could have written, so what the engine must offer them is
what it must offer any author, and where this plan's decisions leave a product
question open, the game that needs the answer settles it ([step 0](#the-build-order)).
The noninterference harness that step 0 builds boots two copies of a game that
differ only in hidden state, drives the same non-operator principal with the
same inputs in both, and compares everything that principal receives byte for
byte; an author who adds a hidden row to a table runs the same harness over
their own world. The design is done when the harness is green over the four
games and its fuzzer, not when a review runs out of findings.

## Rejected and watched

| Approach | Verdict | Reason | What would change it |
|---|---|---|---|
| Door-by-door checks (`view.Withheld`, `ReadsWholeRow` at each verb) | Reject | Two lanes, two reviews, sixteen findings; a new door owes a check it never knew about | Nothing; this plan replaces it |
| Blind writes (a pure `Set` admitted and answered `accepted`, resolved privately) | Reject | The write's public effects depend on the secret: eviction of position zero or a capacity refusal in a full row, a mixed batch's rollback, the typed completion's decision, the addon's bound write resolving the key first | Nothing; the authored inbox gives the same capability on rows the engine can reason about |
| Admit `Add`/`Remove` blind and report the true outcome | Reject | The outcome is the oracle (`(evicted 'k')`, "has no cell keyed") | Nothing |
| Renumber slots on the authority | Reject | Shuffles every later instance when a cell leaves (`WorldPlacementDeal.cs:22-28`) | Nothing |
| Store a child's position as `offsets[slot]` plus a displacement in one float | Reject | Keeping it, or subtracting the slot's offset from it, keeps the slot through the float's rounding | Nothing |
| Store a child's displacement from its slot and compose the slot's fixed-point offset in the one frame resolver | Adopt | A re-deal changes the slot alone, so a preserved transform or a lifted card discloses nothing of the authority's slot (`WorldPlacementFrameCompilation.LocalFrame`) | Nothing |
| Interactive, solid card backs with recipient-scoped ids | Reject for now | Needs opaque ids with generations and a reverse translation into authority targets; the policy only shows a back, and a visible card's own id already works | An authored game that needs an opponent to touch a face-down card |
| Per-recipient colliders for hidden children | Reject | The authority has one collider set; a hidden collider is met by every body in the room | Nothing |
| A per-recipient arena copy | Reject | Stale against a moving table; the live store is the reason `Compose` reads the arena (`WorldStateDisclosure.cs:58-60`); cost per recipient per tick | Nothing |
| One last-sent image per principal, seeded by composition | Reject | Two streams of one principal, and a read composing the view, would advance a cursor no delivery moved | Nothing |
| A member-level analyzer as the guard | Reject | Misses `ArenaHost.Arena`, `IStateReader` through `RuleHost`, generic constraints and a bare `ViewFor(Principal.Console)`; cannot follow a captured delegate | Nothing; `internal` governs acquisition and the analyzer keeps the rule it can decide |
| A separate authority project as the guard | Reject as the sole guard | SDK references flow transitively and Server's public API would still carry the members | Nothing |
| The assembly-level denied-API gate | Reject as the guard | Sees assembly references, not members | Nothing; it stays for `System.Console` and Azure |
| Making `world.identify` operator-only | Watch | Hashing the recipient's view gives a per-recipient identity, which is what the recipient was handed; operator-only is simpler | The owner wanting one QR for every seat |
| Refusing `search` jobs whose outputs are wider than their inputs | Adopt | The owner requires the transform boundary for search too; explicit rule writes remain authored reveals | Nothing |
| A separate body-disclosure view (`WorldOutputHub.Redact`, `WorldSinkDisclosure`) folded into the recipient view | Adopt as its own step | Body redaction is the same shape (a per-sink policy applied at the hub) and the natural second occupant of the view; out of scope here so the state fix lands alone. Both conditions have arrived: the view landed with step 1, and the MMO round's A16 is the body-disclosure finding ([open items](#open-items)) | Nothing |
| Isolating the arena byte ceiling per row or per audience | Watch | A resource channel outside the guarantee; the per-row `capacity` already bounds cells | An authored world where the shared ceiling is reached with hidden rows |

## The build order

Each row below is one independently landable change, with its own check.
There is only one implementation table (M7); D0b1–D0b4 own the entire acquisition
wall formerly split with step 5. The check is the **noninterference
harness** step 0 builds, and the definition of done is mechanical: the harness
is green over the four games and the fuzzer. The harness (`puck test`'s worker
hosts it, as a `DisclosureHarnessLawTests` class in `tests/Puck.World.Server.Tests`
and a `puck disclosure` verb for an author's own world) does one thing: it
boots a **pair** of worlds identical except for hidden state (the fixture
authors the difference: an extra hidden card, a bid that is present or absent,
a unit in the fog or not, a different secret value), drives the same
non-operator principal with the same inputs in both (a seat's console session,
a presentation session, a replica session, a socket peer, a mounted addon, and
later a federated traveller), and compares everything that principal receives,
byte for byte: console replies, typed mutation outcomes with their decision,
code, detail and version, query answers, every delivery with its count and
size per tick, deltas, provenance, observations, `world.placements` slots and
positions, the HUD template's answers, refusals, and the stream's lifetime
(attach, invalidation, detach). Any difference is a leak, reported by the door
that answered it (the verb, the sink, the query kind, the outcome field) with
the tick and the two bytes. Timing and resource readings are outside the
comparison, as [the guarantee](#what-is-guaranteed-and-to-whom) says.

Beside the authored scripts, an **input fuzzer** drives the same principal in
both worlds with the same seeded random sequence over everything it is granted:
every console verb and argument shape `world.affordances` publishes to it,
every mutation kind over every row and key name it can spell (keys that exist,
keys that do not, keys of other players), transforms and batches with guards,
`search` requests, session actions (join, leave, observe, revoke and regrant
through the operator in both worlds at the same tick), and addon binds and
writes. A difference the fuzzer finds is recorded as an authored script in the
fixture, so the suite only grows. Later, an extension seats AI red-team agents
as players over the same harness to generate attack inputs an enumerator would
not; the similarity search (`world.state.similar`) is one of its doors.

Every later step adds its doors to the harness's comparison and is checked by
the harness staying green. A headless console run opens no session
(`StateChangeDisclosureLawTests.cs:7-8`), so the session, peer and addon
principals are driven in-process. No GPU leg: the one door a headless run
cannot reach is the local HUD mirror, which is unfiltered by design.
`worlds/debug/disclosure-probe.puck` becomes the harness's smallest fixture.

The oracle is the recipient's own stream. A transcript (`WorldDisclosureTranscript`)
is one ordered stream of raw events per recipient, each recorded at the boundary
where it leaves the authority for that recipient (a session's sink, a
submission's completion, a console line's answer as its handler returned it, the
edit echo, a stream's attach and end with its cause) and stamped with the tick
the run's clock reads as it arrives, never reconstructed from the input that
caused it. Every field is encoded whole: a snapshot as the federation wire
encodes it (`WorldFederationCodec.EncodeSnapshot`, so vector components, body
colours and input epochs are bytes), a typed outcome as the peer wire's
Completion lane does (`WorldPeerWireFormat.EncodeResult`, operation id, actor,
digest, watermark and version included), a document delivery as its version,
stamp and canonical JSON, console text with its line breaks. The one
normalization is the named run-local nonce, a server's activation id
(`WorldDocumentVersion.Activation`), replaced by its order of first appearance.
The edit echo's connection and correlation ids are left out, not normalized:
the in-process transport mints them before the world sees the submission, from
one counter the observed seat shares with the variant's operator steps, so they
count the run's inputs (a variant with an extra step shifts every later one)
and carry nothing of its state.
The comparison (`WorldDisclosureComparison`) compares each recipient's whole
stream, so an event at another tick, in another order across doors, or
missing, differs as another byte does; grouping by door is the report's aid.
Operation ids are seeded per principal (`WorldOperationIds`, through the
console's `LoopbackTransport` and the sessions' envelopes alike), so the paired
worlds receive identical envelopes on every ingress.

Step 0 drives the seat console, presentation and replica sessions and a
session holding Edit over the rows under test. **Step 0b** adds the remaining
principals, each as another writer into the same transcript through
`RecordText` and `RecordBytes` under doors of its own, so existing fixtures and
the comparison stand unchanged. Each needs capture at its own boundary, because
its path is separate code the in-process session never runs:

- **A socket peer over QUIC**, recorded as the frames its connection writes:
  the peer wire's Completion lane (`WorldPeerWireFormat.EncodeResult`, already
  shared with the sessions' outcome door) and the projection stream
  (`WorldFederationProjectionSink`, `src/Puck.World.Server/WorldFederationProjectionSink.cs:65-108`),
  whose encoded projection deltas, definition leaves and named invalidation
  records (`ProjectionInvalidated` with its reason) are not the hydrated
  definitions a session sink is handed.
- **Observe revoked and regranted mid-run**, through the operator in both worlds
  at the same tick, recorded at the session sink's withheld span and resume.
  Step 1 routes `WorldSessionSink.Resume` through the cursor and tests both
  document tiers; an always-observing fixture does not exercise that path.
- **A federated traveller**, with transfer credentials and forwarding state:
  step 1's `WorldTravelerProjection` compares delivered projection identity,
  admission and route, with an authenticated-stream law for a hidden install.
  Step 0b still must drive those lifetimes in every paired game.
- **A mounted addon** (a WASM guest that records what it receives), at the ABI
  boundary: binds and writes use their own cached handles and verdicts
  (`src/Puck.World.Addons/WorldAddonRuntime.Cells.cs:76-128`).

The harness is not "green over the four games" until 0b has run; a step may
land before 0b, but its doors on those principals are unproved until then.

### The harness's fixture contract

The harness is `puck disclosure <world>` ([CLI reference](../reference/cli.md#puck-disclosurethe-noninterference-harness)).
A fixture is a world plus a `<stem>.disclosure.json` beside it
(`puck.disclosure.fixture.v1`, read by `WorldDisclosureFixture` in
`src/Puck.World.Server/WorldDisclosureFixture.cs`); the probe's
`disclosure-probe.disclosure.json` is the worked example. A game fixture authors:

- **A `schedule` in the world.** Its export tick ends every run of the pair; its
  rows are the authored script for any seat (in the schedule's admitted verbs).
  The observed seat's rows are compared; another seat's rows are inputs only,
  identical in every world.
- **`admission` rows for the sessions it observes.** One row per tier, its
  `domain` the authority's name, `mode: FederatedAuthority` and an `Observe
  all` grant; the probe names them `replica` and `presentation`. A session meant
  to reach the write path also holds Mutate over the state section and Edit
  over the rows under test (the probe's `writer`: `Observe all`, `Mutate
  section:State` with an explicit `kindMask`, which an untrusted principal's
  Mutate row requires, `Edit state:hand` and `Edit state:pile`), or its
  writes stop at a capability denial before the policy under review. A session
  the world does not admit leaves the run INVALID: a fixture that names it
  claims its doors.
- **`observer`**: the seat (`seat1`..`seat4`) whose console session is compared.
- **`variants`**, at least two: each a `name` and operator `steps`
  (`{ "at": <tick>, "run": "<console line>" }`, run as the operator after the
  completed tick, as `world.at` runs a line) that author that world's hidden
  state: another value, a hidden cell present or absent, zero, one or two hidden
  members, or a hidden-only change later in the run. The first variant is the
  base the others are compared with, and the verb boots it twice: the second
  boot is the control, which must compare equal, or the run is refused as noise.
  Variants run the same number of installs when the fixture means to isolate
  content; a variant with one extra hidden-only install (the probe's `late`)
  puts the install's own timing (versions, delivery count) under test. A
  variant's setup must succeed and settle, or the run is INVALID rather than
  judged: every step and script line is pinned before the schedule's export
  tick; every step answers without a refusal or fault, and no operator act is
  refused on the edit echo; and a step armed before the fuzz window's `from`
  settles before `from`, so the probes that depend on it see the world the
  fixture describes. A step inside the window (the probe's `late`) need only
  settle before the export.
- **`script`**: the observer's own lines (`{at, run}`, any verb, operator-only
  verbs included so their refusal is compared too).
- **`sessions`**: the authorities in-process sessions are admitted from.
- **`fuzz`**: `seed`; `lines`, console lines the observer submits, drawn over
  every verb the registry lets anyone run less the fixture's `exclude` and the
  wall-time and resource verbs outside the guarantee
  (`WorldDisclosureFixture.OutsideTheGuarantee`), each in one of the argument
  forms its `help` text spells (`CommandRegistry.Usage`); `actions`, per
  session, drawn over `query.state [<row>]`, `query.where <index>`,
  `query.properties`, `set|add <row> <key> <value>`, `remove <row> <key>`,
  `generate <row> [<key>]`, `transform <transform-json>`,
  `act <phase-row> <sequence> <transform-json>`, `guarded <row> <key> <value>
  <row> <key> <value>` (a write guarded by a cell expectation) and
  `spatial <row> <key> <value> <x> <z> <x> <z>` (a write guarded by a spatial
  read); the window `from`..`until`; `slots`, each placeholder's vocabulary by
  the name the usage gives it (`row`, `key`, `value`, `transform-json`,
  `phase-row`, `sequence`, `index`, `x`, `z`, `text`, `principal`, `placement`,
  …), aimed at hidden, absent, public and other players' targets, with
  structured payloads (transform JSON) written whole; and `words`, the loose
  words and the vocabulary of any placeholder `slots` does not name. One draw in
  eight of a verb with forms takes loose words instead, which reaches its parse
  refusals. The vocabulary is the fixture's own, never read from either world.
  `slots` is optional: a fixture without it draws every placeholder from
  `words`, as before.

A game's pair script is its schedule plus `script`: a sealed bid placed, a card
dealt and discarded, a whisper sent, a unit moved under fog. What a game cannot
spell as a console line or a session action is a door the harness does not yet
drive. Today that is a socket peer, a mounted addon, a federated traveller and
an Observe revoked and regranted mid-run (step 0b); each is added as a principal
of the same transcript, so a fixture written now gains those doors without
changing.

On the probe the harness is red at these doors, each the cause a later step
removes; the control pair agrees for every recipient. Seat2's script removes
`hand.secret0` and sets `hand.secret1` at tick 23, cells it cannot read, so
from that tick the variants' worlds differ by what those blind writes did:

| Door | Recipient | Pair | What differs | Step |
|---|---|---|---|---|
| `world.placements` | seat2 | any hidden member dealt before `pub` | `pub`'s `dealSlot` and position (closed by step 4) | 4 |
| `world.projection` | seat2 | a hidden member dealt before `pub` | the disclosed layout still carries the authority deal slot and position, changing the composed projection size (closed by step 4) | 4 |
| `echo` | seat2 | any hidden member | `world.state.cell.remove hand secret0` answers `ran` with no text in both, and its echo says `applied` against `has no cell keyed 'secret0'`: a membership oracle a fire-and-forget verb carries only on the echo | 3 |
| `deliver.state`, `deliver.definition` | every session | any hidden member | the re-deal after seat2's blind removal moves `pub`'s dealt position (`$.placements.rows[2].position[0]`) | 3, 4 |
| `world.state.transform`, `world.state.act` | seat2 | two hidden members | after seat2 overwrites the hidden `secret1`, a transfer out of `hand` is refused as `state operation is not disclosed` against `not an ordered token zone` | 3 |
| `outcome.transform`, `outcome.act` | writer | two hidden members | the same refusal texts on the typed outcome | 3 |
| `deliver.snapshot`, `query.state` | every session | two hidden members | the field deltas and state observations after the blind overwrite | 3 |
| `world.state.observe`, `world.observe`, `world.state` | seat2 | two hidden members | the observations of `hand` after the blind overwrite | 3 |
| `deliver.definition`, `deliver.snapshot`, `outcome.*`, `query.state` | every session | `late`, after the blind writes and fuzz | the re-authored row restores `pub` and removes keys the earlier writes made readable; that visible change advances the recipient's sequence | 3, 4 |
| `echo`, `world.state.transform` | seat2 | `late` | a drawn transfer out of `hand` is refused as `state operation is not disclosed` against `not an ordered token zone` (a drawn `Generate 'hand'` likewise as `draw site is not disclosed` against `requires a redrawable draw site`) | 3 |
| `world.state`, `world.observe`, `world.budget`, `world.placements` | seat2 | `late` | the install's cells, counts and deal | 3, 4 |

Step 2's remaining layout doors were `world.placements`, `world.reflow.preview`,
`world.projection` (including its byte count), and placements in `deliver.state`
and `deliver.definition`. They consume the view, and step 4 re-deals inside it;
the probe now compares equal at every door.
Read differences after a blind write are step 3's public-state consequences,
including `query.state`, `world.state`, observation replies and later budgets;
changing the read path cannot undo a public write. `wire.errors` also retains
write refusal text. Neither these nor the card fixture's intermediate disclosed
count changes are claimed green by step 2.
The probe's blind upsert can create a public `secret1` where none existed while
preserving another variant's existing cell policy. `world.status` then counts
the view's differing disclosed placements, and `world.state.observe` reads its
differing public cells. A drawn `world.state.act` exposes the same policy-specific
refusal as `world.state.transform`; all three follow step 3's write-policy cause.
The drawn doors move with the registry, since the fuzzer draws over every verb
anyone may run: a verb added or removed elsewhere changes which lines a seed
draws. The authored rows (the schedule's and the script's) stay where they are.

**No step opens a door before its protection lands.** The order puts the
delivery protections (the cursor, recipient-scoped versions, the delta over
images) first, since every later step changes what recipients are shown and
would otherwise show its own change through the old cache; the write door only
refuses more, never admits more, so it opens nothing; the layout lands with its
facet refusal in one step. Step 3 removes the duplicate write predicates; the remaining
read adapter and disclosure implementation stay until step 6 replaces their callers.

| Step | Lands | Check |
|---|---|---|
| 0. The harness and the games | The noninterference harness (above): paired boots differing only in hidden state, one principal of each kind driven identically in both, byte-for-byte comparison of everything it receives, leaks named by door; the input fuzzer over the principal's grants; `DisclosureHarnessLawTests` and `puck disclosure <world>`. The four fixtures as authored Puck games in `worlds/debug/`, each at a table in the tables room and each with its pair script and its hidden difference: **the auction** (sealed bids into an authority-only row, the reveal, the winner; its pair differs by one bid present or absent), **the card game** (a draw pile, a discard pile and dealt hands under `Count` and `Placeholder`, a displaced visible card; its pair differs by one hidden card), **social deduction** (roles in a restricted row, private chat channels through `chat.whisper` inboxes with `readersFrom` audiences, a vote; its pair differs by one role and one whisper), **the skirmish** (units with hidden positions under fog, a `search` job over what a side can see, physics on a small board; its pair differs by one unit in the fog). The existing probe is the fifth, smallest fixture | The harness runs over the five fixtures and the fuzzer and is red on today's code at exactly the findings' doors, naming each. The games settle what the plan left open: the auction shows whether an authored private inbox gives a sealed bid everything it needs (if it does, blind writes stay rejected; if it cannot, decision 3 is revisited with the auction's requirement in hand); the skirmish shows whether a `search` with public outputs over hidden rows leaks and so decides the lint; the card game decides the `$back` contract and the layout; social deduction decides whether `readersFrom` audiences and inbox eviction (`chat.whisper`'s bounded inbox) need a door of their own |
| 1. The view and the cursor, deltas first — landed | `WorldRecipientView`, `WorldDeliveryCursor`, `WorldDisclosureImages` and the carrier's key bitsets; both sinks use `Advance` and seed on definition/Resume; recipient sequences on deliveries, outcomes and provenance; traveller validity by delivered projection identity; the old change-disclosure cache and its forget path removed. `host.frames`, `host.memory`, `world.counters`, `world.gpu-timing` and `world.collision.probe` are Operator; the operator's budget reports image work/storage. [Decision 5](#5-a-delta-is-a-difference-of-views-owned-by-the-stream) records the choices | `DisclosureHarnessLawTests` compares hidden-only installs with intervening reads; `StateChangeDisclosureLawTests` covers independent cursors, in-place vectors, count primers, seat receipts and shared spectator composition; `WorldProjectionBackpressureLawTests` compares both peer tiers' wire bytes using the harness nonce normalization. Session Resume, authenticated traveller lifetime and observation/anchor restore laws cover boundaries step 0b has not yet added to every fixture. The five fixtures still expose the read/write/layout causes below; equality is required while the actual disclosed image remains equal |
| 2. Reads through the view — landed | Every non-operator read door reads the view at its ceiling: `world.budget`, `world.placements`, `world.reflow.preview` (over the view's placements), `world.topologies`, `world.timeline`, `world.projection` composed for the caller, `world.identify`, `world.hud.template`, `world.state.similar`, `AnswerStateObservations`, `WorldFederationCodec.Observation`, the prototype fetch, `WorldTravelerProjection`; sessions read through the view's ceiling in place of `ReadSessionDefinition`; search audience widening refused at compile time and live execution; the skirmish's fair search uses blue's memory throughout | Harness: every read door the fuzzer reaches compares equal except the layout doors step 4 owns; `ConsoleDisclosureLawTests` green; `puck references` shows no `server.Definition` in `Puck.World` or `Puck.World.Console` outside operator verbs and boot |
| 3. The write door refuses by policy — landed | `Admits` and `Refusal` on the view; every mutation touching a restricted row refused by policy for every recipient, seats included, batches and guards (`ExpectedCells`, `ExpectedSpatialReads` through `Fingerprint`) included; the typed completion's code and text; the addon's `NotDisclosed` verdict on bind and write; the forwarded submission door; the `sealedInbox` template in `worlds/standard/` the auction imports; the seven old predicates deleted | Every write door compares equal across all 46 fixture variant pairs, including full typed outcomes; actual guest bind/write and forwarded-traveller laws pass; the auction plays through reveal and reproduces; `TransformDisclosureLawTests` uses the one refusal text. Remaining read/layout delivery differences are recorded below |
| 4. The layout — landed | `WorldStateDisclosure.DisclosedPlacements`, the one placement reading for every recipient door, re-deals a template whose dealt row can withhold: readable cells in the row's order take slots `0..n-1`, each child moved to its re-dealt slot with its own displacement (a child stores its displacement from its slot, and every frame is the slot's fixed-point offset plus it, `WorldPlacementFrameCompilation.LocalFrame`), re-dealt children following their template in slot order, and a placement authored under an unshown child absent with it; a template whose variant row alone withholds keeps its slots and masks each unreadable face, both rows recorded as placement reads; `$back$<slot>` children under `Placeholder`, picked as the template and refused by name on submission; the validator's allowlist (`WorldPlacementDeal.UndisclosingMembers`) over a restricted template, its children and every placement under them, which admits `preserve.transform` since a transform is a displacement; the sweep's child stamp shared as `WorldPlacementDeal.Child`; the tick delivers right after its response and deal sweeps, so an install is delivered on its own tick; a value-only delivery owes a replica or federation peer its document when the recipient's frame moved (`WorldDeliveryCursor.FrameMoved`), replacing the authority's placement-install flag | Harness: the probe and the card game compare equal at every door across every pair; `puck compile --validate` refuses the card game's rack with `solid` by name; `WorldDeckAndDisclosureLawTests` holds the re-dealt layout under all three policies, the facet refusals, the hidden variant under a public deal, the slot-independent rounding of a re-dealt position and a lifted card in a hidden hand shown at bit-identical frames whatever hides ahead of it; the card game's variants all lift its shown card; `PlacementDealLawTests` refuses a solid placement authored under a restricted child |
| 0b. Remaining harness ingresses | Socket peer, mounted addon, traveller and Observe revoke/resume feed the existing transcript | Paired wire/ABI events and session lifetimes compare equal; no claim of full ingress coverage before this row |
| D0a. Actual witnesses | Extend the existing typed payload/fuzzer vocabulary for W1–W8, H1–H3 and M5/M8; preserve positive controls | Record first difference or refutation for each. H1 varies only hidden `hand` membership while removing public `deck[x]`; include board/search conditional iteration. H2 interleaves an authority rule edit before an unrelated recipient edit. Vary private host/input/machine dependencies too. Reject unequal permitted images and all-refuse controls; retain RecipientOracleLawTests |
| D1. Operator grant and every escalation closure — landed | WorldGrants admits the complete before/proposed authority graph and mints the transition consumed by private grant/group installation. Live Operate replaces kind-keyed administration; ordinary seats retain explicit budgets/masks and consent. Admission templates are single-subject Replica only. Host separation, peer report field split, disconnect, silo admission, cursor reseeding and observer invalidation are implemented | Seat and Peer operators read hidden rows, declare, administer and author programs; revoke restores recipient restrictions and reseeds from that tick. Ordinary seats cannot join/form/edit an Operate group, change its membership source, self-grant via grants-section mutation, or escalate via either batch order. Own-body consent still works. Refuse addon/group laundering, Vouches, both FederatedAuthority forms, wildcard and below-Replica templates; accept pinned SignsDirectly and exact OAuth subjects. A silo operator sees peer/population/refusal data but no endpoint or verified identity and cannot invoke Host verbs. Disclosure observers holding Operate are INVALID. Build touched projects, focused laws, disclosure probe, verbs --headless-only and headless debug boot |
| D0b1. Declarations and acquisition wall | After D0a and D1, introduce the declarations-only carrier and restricted reader shared by existing kernels; remove full materialization from preparation. Move command and preparation consumers behind the same wall (former step 5), including operator-context minting | Build-negative fixtures reject raw definition/arena, seeds, lazy binders, IStateReader.Arena, constructor/callback/generic escapes and dropped faults. DISC001 and the public-surface law cover the moved consumers; touched projects build and existing doors retain conservative admission |
| D0b2. Static closure and validation units | After D0b1, declaration-derived security universes, transitive reverse closure, partitioned binding/relation/input validators; actual reads may add checks only | H1 empty/nonempty hidden member and branch variants have equal verdicts. A new unlisted read faults. Negative lookup, new declaration and enumeration invalidate correctly. Count sparse/HUD-bound writes, document edits and 1/400-row, 1/1000-recipient workloads, including retained edges; existing recipient admission is not relaxed |
| D0b3. Host preparation and reservations | After D0b2, envelope, face, population, solids, adjacency, spatial, machines and addons use declared reservations or transaction reads; no raw host store or late refusable install | Public placement edit succeeds in both restricted-deal variants at declared capacity. Private population, memory binding and input-selection pairs compare equal; prepared results install without revalidation. Count copies, scans and staged bytes |
| D0b4. Authority maintenance | After D0b3, rule, clock, draw, value and operator commit paths settle dirty units under authority access and atomically commit index and state; validation fault rolls back | Interleave rule firings and recipient document edits; no pending authority work reaches the recipient. Full authority validator agrees after every accepted commit, failed firings rewind host and arena state, reproduce affected authored scenarios; count rule work and retained edges |
| D2. One refusal and addon verdicts | After D1, neutral text/code at existing doors, operator-only detail, bind-by-name and per-cell verdicts; delete NotDisclosed | SessionWriteDoorLawTests and RecipientOracleLawTests; mixed absent/present addon bind succeeds, the present write lands and absent/withheld writes both answer NoSuchSubject; lint identifies authored typos. No added write capability |
| D3. Guard read universe | After D0b4 and D2, all user and engine-built guards use entitled images, including reflow ExpectedInputs; remove empty-selection and policy-tier shortcuts | Replica fresh/stale guards, hidden-only Omit changes, all lower-tier selections, mixed cell/input/spatial guards and permitted Placeholder count changes; engine-created batches obey the same rule |
| D0d. Adjacent read handlers | After D0b1, parked-body query and HUD template reads consume caller-stamped restricted operations; same acquisition wall | W8 paired handler transcripts include counts, deadlines and lifetime, with observed-body and readable-template controls |
| D4. Authored writable scopes, closed to recipients | After D0b4 and D2, writeScopes schema/DSL and structural membership, policy templates, durable contributor stamps, static closure and total exports; row/key/byte reservations and runtime outside-growth caps | Import/qualification/serialization round trip; refuse forged stamps, cross-partition domains, strict outside bindings and rules, unsupported fields and overcommit. A collector sees total absence without vetoing writes; private rows differ only for their authorized readers; schema and registry checks |
| D5. Proportional scope installation | After D4, operator-only scoped append/reshape/remove in reserved ordinal regions; generation-safe handles, tombstones and deterministic partition-local key compaction | Churn through more lifetime names than quota with bounded retained storage; stale addon handles fail, existing compiled rules and replica deltas retain identities. Zero outside rows validated/rules compiled/full arena copies with 1/400 outside rows; focused declare/write/remove scenario reproduces |
| D7. Every rebuild and policy transition | After D5, restart/reload/reset/load/source replacement re-offer scope rows through the same typed check; widening publishes existing data, narrowing reseeds | Restart with changed source, reload from source lacking guest rows, reset and load preserve eligible declarations; shrunken quotas and removed scopes drop deterministically with operator-only detail. Count/Placeholder changes are allowed observations; widening delivers old cells to newly entitled readers |
| D6. Open scoped input and migrate consumers | After D7 and D3, scope grants and partition read/write doors open with every protection installed; migrate default-tier auction bids, granaries feed and public-scoreboard addon bindings in this change | At Presentation/Frames, compare hidden flat-name row and absent/withheld second-scope variants; at Replica keep declarations fixed and vary hidden members only (L2). Hidden outside key-minting rule and declare/remove churn cannot change the admitted partition's quotas or key cost. Two bidders cannot read or overwrite each other's rows; verified reconnect recovers only its own partition. Declaring, reshaping, removing and reading own rows succeeds. Harness across actual addon/peer/local ingresses and authored input scenarios reproduces |
| D0c. Switch general writes | After D3 and D6, route Replica general writes, console raw edits, typed addon writes, detached composition, transforms and batches through the prepared-result door; refuse low-tier general forms after D6 has supplied their scoped replacements | W1–W7 and H1/H2 transcripts equal with successful controls; revocation, policy changes and final-member failure leave no partial effect. No raw recipient prepare/install caller or authority rerun remains. RecipientOracleLawTests stays; a new model field requires no hand-maintained security list |
| 6. Nothing else decides | `WorldStateDisclosure` the class and `WorldStateReadView` deleted; `view.Withheld` where `ReadsWhole` serves and the timeline's lambda gone; the documents updated: [console](../../.claude/skills/puck-world/references/console.md) "Disclosure chokepoint", [traits](../reference/state/traits.md#control-what-observers-learn), [documents-state](../../.claude/skills/puck-world/references/documents-state.md), the Schema and Server READMEs, [worlds](../reference/state/worlds.md) | `puck references WorldStateDisclosure` answers nothing; the harness green over the five fixtures and the fuzzer: done |
| 7. Red team | The extension that seats AI agents as players over the harness (an `IPuckExtension`, [hosting](../../.claude/skills/puck-world/references/hosting-and-release.md)), each agent driving one principal in both worlds of a pair with inputs it invents, `world.state.similar` among its doors; a difference it finds becomes an authored script in the fixture | A red-team run over the five fixtures adds no new script, or every script it adds is green after its fix |

Steps 0–4 describe the existing foundation. New rows declare their dependencies
in the table. Step 6 follows D0c, D0d and D6; it removes remaining duplicate
read adapters only after their callers move. Step 7 follows step 6. There is
no D0c/D2/D3/D6 integration unit: D2 narrows output, D3 narrows guards,
D4–D5 keep scopes operator-only, D7 makes them durable, and D6 alone opens
recipient access and migrates low-tier consumers. D0c then switches general
preparation and retires the former low-tier routes. The old conservative
admission remains until a
route's replacement is complete; no intermediate row widens it. D1 is atomic
with its escalation closures, so it needs no later protection step. The
acquisition wall has one owner, D0b1, extended by D0b2–D0b3 for their consumers.
Where [records and pools](records-and-pools.md) meet this work, future policy
enters through the one disclosure predicate; scopes do not admit pools yet.

### D0b1 implementation checkpoint

D0b1 remains open. The sec1 change introduces a sealed `StateRowDeclaration`
beside the seeded `StateRow`; both use `StateRowShape` for domain and value
rules. A declaration carries neither cells nor storage cursors, a phase
sequence, a draw secret, an arena or a live value binder. `StateReadAccess`
checks whole-row admission before each operation, gives restricted callers
access-bound row handles and local key identities, copies vector payloads, and retains an unavailable
fault for its lifetime. Missing and withheld acquisitions of the **named row** throw the same
failure. This does not close the dependent-row existence channel R5: the
[structural write footprint](#the-structural-write-footprint) still owns that closure.
`StateReadRow` binds a row to the access that acquired it, with no public ordinal;
authority catalog ordinals enter the shared kernels through explicit overloads.
`StateReader.ReduceRaw` and `DomainOrdinals` use these operations for
authority and restricted access alike.
Vector detachment uses `CellValue.DetachedCopy`, shared with pool seed records
and disclosure images.

`world.state.cell.set` now acquires only the addressed submission link. It
sends the raw operand for every row kind, and compose interprets it after
admission against the candidate produced by earlier writes. A console handler
cannot branch on a private row's kind through this command. This also removes
the declaration preflight for rows whose kind changes during the same drain.
Restricted declaration acquisition admits its row bindings and omits a draw's
source and program. Bindings into other sections still require wider entitlement.

`CommandDefinition.OperatorWithWireArgs` receives a registry-minted
`OperatorContext`. `WorldServer.AcquireOperatorAuthority` and the resulting
`WorldOperatorAuthority` check the addressed world's live Operate grant.
That live grant, not the registry's mint alone, is the authorization boundary.
The capability exposes only hash operations, checked under `AuthorityGate`;
it exposes no retained arena or definition.
`world.state.hash` now uses this route, including its component digests. An
ordinary command context cannot acquire that capability. Other operator
commands and composition roots have not yet migrated.

Replica observation rows never carry `Draw.Secret`, including unrestricted
operator replicas. `WorldStateDisclosure.DiscloseRow` removes it for initial
documents and refreshed image skeletons; authority persistence keeps it.
`DisclosureReviewLawTests.ReplicaDrawsNeverCarryTheAuthoritySecret` failed for
both public and restricted rows before this correction and covers both plus
operator observations and the persistence control.

`WorldDocument.TryPrepareMutation` now admits before choosing the state or
document route. Successful transaction completion is the sole constructor of
`WorldPreparedMutation`. It checks the transaction's sticky read fault before
transferring ownership of the journal mark or staged document resources. A
refusal rewinds the mark or disposes the plans. Exact-change guards take both
samples through the transaction's read access.

Rule row lookup returns sealed declarations for authored and generated rows.
Transform prices read the declared sampling algorithm and bound cell metadata
without examining stored cells. A private stream keeps the same price when
its observation omits the key. Execution requires a matching provisioned key.

The whole-preparation invariant remains open. Document staging still exports
the arena, and classification, validators and transform kernels still have raw
access. The world-wide declarations carrier, compiler and evaluator migration
(including `IStateReader.Arena` and the seeded rows in `RuleCompileContext`),
removal of lazy authority binders, raw server surface closure, DISC001 and the
negative fixtures remain D0b1 work. D0b2–D0b4 depend on that wall.

`StateReadAccessLawTests` covers access-bound handles, sticky failure, detached
vectors and declaration acquisition. The console acquisition laws observe
settled compose results and denied echoes, including numeric, add and multiword
text writes to a row declared earlier in the same drain. Operator acquisition
laws cover revocation of retained hash capabilities. These checks cover the
checkpoint's stated invariants; the complete D0a witness packet and D0b1's
build-negative/public-surface fixtures remain open.

Step 3's completed run compares every pair of variants, not only each with its base:
all 46 pairs over the five fixtures have identical write-door transcripts, including echoes
and complete typed outcomes. The probe's baseline differed at `echo`, `world.state.act`,
`world.state.transform` and typed act, add, generate, guarded, remove, set, spatial and
transform outcomes; the card baseline differed at typed add, generate, guarded, remove,
set and spatial outcomes. The other three fixtures already agreed. Card fixture corrections
preserve both the disclosed count at every tick and the placeholder order around its shown
card; hidden swaps rename members in place, and the storage-slot variant restores logical
order before delivery. All controls agree. Actual guest bind and previously bound write
laws cover `NotDisclosed`, and the committed-traveller law covers forwarded completion.
The auction's highest bid, over-purse bid and tie/replacement scenarios play through reveal
and reproduce their authoritative hashes. The full solution builds in Release and the
debug world boots headlessly. Allocation readings and their revisions are in the completion
commit, as machine readings are not a repository baseline.

## Open items

- Row declaration, the operator grant and the structural write footprint are designed in
  [decision 3a](#3a-a-declaration-is-judged-inside-a-scope-its-declarer-reads-whole)
  with rd2-review answered. The single build table owns the implementation;
  none of its new protections is implemented by the memo. D0a verifies the
  W1–W8 witnesses and H1/H2 interleavings, including the corrected W5 premise.
- More seat-reachable control verbs: `player.confirm` acts on the host
  keyboard from a seat's typed line (`PlayerCommandModule.Admission.cs`,
  `ConfirmHandler`); `player.assign` (`PlayerAssignmentCommand.cs`) can name
  any device; `pipeline.commit` changes the shared view. All take
  `CommandAudience.Anyone`. Decide them as `player.claim`, `player.cycle` and
  `pipeline.load` were decided.
- Lower-tier write verdicts still reveal dependency existence today. Decision
  3a's D6 followed by D0c closes this by making general writes require the declaration
  entitlement and keeping lower-tier writes within wholly readable, closed
  partitions. D6 migrates the debug world's inputs and addon state before
  D0c retires their former routes; a
  restricted reader alone does not close this channel.
- Growth and memory admission read reservations. Work prices read declarations
  only ([the costing decision](../decisions/state-and-language.md#costing)), but
  an arena growth is still charged `StateArena.ReservationWork` over the whole
  proposed layout (every row's reserved room, hidden rows included) plus the
  growth already spent this tick (`WorldRuleHost.Growth`), and judged against
  the arena byte and retained-journal ceilings, so a recipient's mint into a
  public uncapped row can be refused or admitted by a hidden row's reservation.
  The validator's vector-byte and undo-byte checks
  (`WorldDefinitionValidator.State`, `RuleGroup`) read seed reservations, which
  are live members at an install. Candidates: admit memory against declared
  ceilings once at load, and make a growth's copy proportional to the row that
  grows.
- Search plan validation reads members. `WorldSearchCompilation` requires a
  relocate's or promote's `codes` row to hold as many cells as the tokens row,
  and resolves a tandem's companion by its position among the tokens' current
  cells, both on every install. A rule that removes a token, or reorders the
  row, changes what an unrelated recipient edit's validation answers. Declare
  the relation (codes keyed over the tokens, a companion named by key) instead.
- Whether blind writes return: rejected here (decision 3). The auction fixture
  (`worlds/debug/auction.puck`, `worlds/standard/sealed.puck`'s `sealedInbox`)
  needs nothing more: each bid lands in the bidder's own inbox, a rule moves
  it into the authority's row, the bidder's receipt is its own row, and the
  close's timing is a fixed deadline, so nothing public moves with a bid.
  Step 3 refuses a seat writing another seat's inbox under the seeded `Edit/all`
  grant before it can inspect any key. The standard collection and receipt templates
  supply the auction's move into authority storage.
- What the other fixtures settled. Social deduction: a `readersFrom` row must
  itself read whole by every recipient (`WorldDefinitionValidator.ValidateReadersFrom`,
  and `WorldStateDisclosure.Allows` admits no one through any other), so a
  secret team's channel cannot be one row whose audience lists the team
  without publishing who is on it; the game keeps a den row per mole instead,
  and seeds every chat slot, so no eviction ever runs. The card game: a pile
  that shows `Count` or `Placeholder` to readers who may not read its cards
  needs a second, authority-only or owner-only pile holding the same cards
  (`sheddingSealed`, `sheddingHeld<s>`), since no rule effect sets a cell's own
  visibility and a transfer or shuffle out of a restricted pile into a wider
  one is refused as widening; its deals are rule writes keyed by
  `zone(pile, first)`.
- The four games' harness fixtures (`worlds/debug/<game>-disclosure.puck` and
  its `.disclosure.json`; each admits a replica, a presentation and an
  `editor` session, the last with Mutate of the state section and Edit of the
  game's restricted rows, so its drawn writes reach the mutation path). Every
  variant's setup steps apply, and every control agrees. The skirmish is green after its output-audience refusal and memory-only fair job.
  The card game's Presentation session received its definition at the hidden swap
  (tick 40) rather than at the next public change: the deal sweep ran after the
  tick's last delivery, so the turned-up card's install rode the next delivery any
  later change triggered, and the swap's hidden install triggered it early. Step 4
  closed it (the tick delivers after its sweeps), with the frame trigger for
  value-only reveals and backs that stand for every withheld cell whether or not
  the sweep has dealt it, so an operator's hidden key rename between sweeps moves
  nothing a recipient is shown; the probe's dealt positions closed with the
  re-deal. All six fixtures compare equal at every door. The probe's baseline late
  whole-row install also overwrote public changes caused by blind writes; step 3
  refuses those writes.
  The focused step-1 laws hold
  the public image fixed to isolate the delivery invariant. The auction's
  pairs, including the mid-run sealed bid, are green. Social deduction is green over another pair
  of moles, a whisper between two other seats, a line in the den and two
  other seats' votes: its hidden changes are rule writes into per-cell and
  per-seat rows that move nothing a crew seat or a session is delivered.
- The world seed is two things at once. A presentation recipient rebuilds its
  document without it (`WorldProjection.TryToDefinition` builds no
  `Generation`), so every noise or scatter placement, and now a noise or
  scatter deal template's children, lays out at seed 0 there; the replica tier
  carries it, and it is a rung of every draw's seed ladder
  (`GeneratorEngine.ComputeSeedState`) beside a fixed boot instance name, so a
  replica peer may predict hidden draws (unverified). No shipped deal uses a
  seeded region (every deal in `worlds/debug` is a lattice). Decided: separate
  a layout seed, disclosed to every tier, from the draw seed, which stays the
  authority's; check first whether a replica can predict a draw today.
- Whether `world.identify` hashes the recipient's view or becomes operator-only
  (decided here: the view; the owner may prefer one QR per world).
- Body disclosure (`WorldOutputHub.Redact`) joins the recipient view: the MMO
  round's I1 (its step 16; [the seams decision](../decisions/worlds-and-federation.md#seams)).
  Its finding is the attack catalogue's
  [A16](mmo-foundations/attacks.md#a16-watching-without-a-body): under the
  default `All` an unembodied spectator receives every body, and under `Radius`
  an observer anchored to a window or a camera receives none, because `Redact`
  anchors only to the observer's own body (`WorldObserverDisclosure`'s
  `Radius` mode: "An observer with no body of its own is delivered nothing").
  Fold a spatial grid, hysteresis, an unembodied-observer policy with a window
  or camera anchor, and distance-tiered cadence into the view and its cursor,
  replacing `Redact`. Check 5 (I1) in
  [the memo's checks](mmo-foundations/synthesis.md#5-how-the-debug-world-proves-each-favoured-option):
  at `Radius 15` with 64 autonomous bodies crossing the radius, a spectator
  anchored to a window grant receives the bodies within 15 m of its window, an
  unanchored one receives none under `All` as well as `Radius`, a body
  oscillating 0.1 m at 15.0 m does not flicker, and `world.budget` shows
  redaction cost scaling with nearby bodies. Needs step 1 (landed) alone.
- The resource channels named as best effort are measured, not assumed: step
  1's spectator check records the per-tick disclosure work a hidden write
  costs, so the width of the timing channel is a number in `world.budget`.
- A keyframe a boundary defers (`WorldPersistence.TryCaptureCheckpointCore`: a
  buffered edit, an open scope, a non-empty ordered domain) lands a tick later,
  so an operator edit pending at a keyframe tick would shift the keyframe ticks
  a seat reads. The fixtures' operator steps run between ticks and never meet
  it; a schedule that keeps the planned keyframe tick (the next capture fills
  the slot it was due in) closes it.

---

[Plans](README.md) · [State and language](state-and-language.md) · [Open items](open-items.md#security-review) · [Decisions](../decisions/state-and-language.md)
