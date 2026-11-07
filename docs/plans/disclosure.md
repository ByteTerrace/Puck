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
truthfully whether hidden rows or public ones filled it. The engine does not
promise these carry nothing; it keeps the diagnostics that read them for the
operator (`host.frames`, `host.memory`, `world.counters`, `world.gpu-timing`,
`world.collision.probe`, registered today for anyone,
`src/Puck.Launcher/Commands/HostFrameCommandModule.cs:17-46`,
`src/Puck.World/WorldCollisionCommandModule.cs:251-255`), since they carry
nothing a player needs, and it keeps hidden-dependent work proportional to
change so the channel stays narrow ([decision 5](#5-a-delta-is-a-difference-of-views-owned-by-the-stream)).

**Who is trusted.** The engine's own C# is trusted code: the authority's
program, its sinks and codecs, the composition roots. The structural guards
below make it impossible for trusted code to reach the authority by omission
(a verb, a module or a sink that never knew it owed a check cannot compile), and
they make the compliant path the shortest one to write. They do not defend
against trusted code that deliberately captures a definition in a lambda or
reads a property by reflection; that is review, and the guards are shaped so
such code has nowhere ordinary to stand ([decision 6](#6-the-compiler-holds-the-line)).
Rules are the authority's program: a rule that copies a hidden value into a
public cell, or a `search` whose public outputs are judged over private rows,
is an authored disclosure, like a binding to a restricted row on the shared
screen.

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
- The policy predicate exists several times. Beside `ReadsWhole`, the write and
  admission paths scan current members: `WorldDocument.DisclosesStateRead`
  (`src/Puck.World.Server/WorldDocument.StateCommands.cs:97-116`) walks every
  present cell, `IsWithheldFromWriter` (`src/Puck.World.Server/WorldDocument.Compose.Cells.cs:576-585`)
  asks `CanReadCell` (`WorldStateDisclosure.cs:181-187`), which reads the cell's
  current visibility and so passes an absent key through a public row policy,
  `WorldServer.WriteStateCell` asks it again for an addon's bound write
  (`WorldServer.Arena.cs:98-99`), and the batch guard `ExpectedCells` is checked
  twice, both times from the authority arena (`WorldDocument.StateCommands.cs:135-142`,
  `WorldDocument.Compose.Batch.cs:82-106`), after an admission that checks the
  Observe grant alone (`WorldDocument.StateTransformAdmission.cs:127-147`) and
  checks nothing for `ExpectedSpatialReads`, whose fingerprint is computed over
  the raw placements (`src/Puck.World.Schema/WorldDefinitionFingerprint.cs:109`).
  `ReadsThroughDisclosure` (`Compose.Cells.cs:574-575`) names addons, peers and
  sessions; a seat writes under its grants alone.
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
- Delivery metadata is the authority's. Every install bumps one sequence
  (`src/Puck.World.Server/WorldDocument.cs:28-30`) that sinks pass through as
  `WorldDocumentVersion` and projections carry as `Population.Revision`; a
  traveller stream's validity compares the raw definition by reference
  (`src/Puck.World.Server/WorldTravelerProjection.cs:47-50`) and ends the stream
  with `world.observation.invalidated` when it moved
  (`WorldFederationProjectionSink.cs:52-55`), so an install that changed
  nothing the recipient may read still changes what the recipient sees.
- The delta cache is a comparator, not a seed. `WorldStateChangeDisclosure.Filter`
  (`src/Puck.World.Server/WorldStateChangeDisclosure.cs:22-93`) marks whole rows
  changed from row headers (`:41`), because the change carrier sends every cell
  of a changed row (`WorldServer.Arena.cs:45-56`, `CopyPublishedCells`),
  recomposes each restricted row and suppresses the replacement only when
  `m_sent[ordinal]` holds an equal image (`:85`); `Forget` (`:97`) clears it, so
  the first hidden-only change after a definition delivery sends an unchanged
  replacement, and the stored image's vector payloads alias arena memory
  (`:90`, `ReadCellImage`). Each sink holds its own cache and its own
  `WorldProjectionFeed`, which serializes and diffs the whole projection
  (`src/Puck.World.Schema/WorldProjectionFeed.cs:56-57,75-103`).
- The deal's slots are authority history. A child takes the lowest free slot
  (`src/Puck.World.Server/WorldTick.Deals.cs:426`, `FirstFreeSlot`), is built at
  `Position = offsets[slot]` with the template's `Solid`, `Grip`, `Region`,
  `Emission` and `Spatial` facets (`:558-571`), and `preserve.transform` keeps
  that position across reconciliation (`:544-552`); by design a departing cell
  moves no sibling (`src/Puck.World.Schema/WorldPlacementDeal.cs:22-28`).
  Disclosure removes a hidden child but keeps the visible one's `DealSlot` and
  transform (`WorldStateDisclosure.cs:479-482`, `:562-622`), and
  `world.placements` prints the slot (`WorldTick.Deals.cs:102`).
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
| [high] Replica resume delivers the raw definition and neither seeds nor forgets the delta cache (`WorldSessionSink.cs:188`) | A sink discloses by hand on one path and forgets on another | 1, 2 |
| [high] Batch `ExpectedCells` is a read oracle over undisclosed cells, implemented twice (`StateCommands.cs:135`, `Compose.Batch.cs:82`); admission checks the Observe grant only (`StateTransformAdmission.cs:127`); `ExpectedSpatialReads` is never checked | A guard is a read that never passed the policy; two implementations | 3 |
| [high] Transform and Generate admission test current hidden membership (`StateCommands.cs:97-116`) | A second whole-row predicate beside `ReadsWhole` | 3 |
| [high] Per-cell upsert and remove receipts distinguish hidden from absent for peers, addons and sessions (`Compose.Cells.cs:292,576,636`; `WorldServer.Arena.cs:98`) | `CanReadCell` reads current visibility; an absent key passes a public row policy | 3 |
| [medium] A seat writes under its grants alone and learns which hidden keys exist (`Compose.Cells.cs:574`) | Seats are not recipients at the write door | 3 |
| [high] Dealt children keep hidden-dependent slots and positions (`WorldStateDisclosure.cs:481,621`, `WorldTick.Deals.cs:426`) | The layout is authority history, shown as is | 4 |
| [high] `world.budget` publishes raw placement counts (`WorldPopulationCommandModule.cs:225`) | A diagnostic reads `server.Definition` with no audience | 2 |
| [high] Forgetting the delta cache reopens a timing oracle after re-disclosure (`WorldStateChangeDisclosure.cs:85,97`) | A comparator cache seeded by nothing | 1 |
| [medium] The last-sent image aliases live vector memory (`WorldStateChangeDisclosure.cs:90`) | The image is a view of the arena, not a copy | 1 |
| [low] `world.identify` hashes authored hidden seeds for a seat with Control over a screen (`WorldIdentifyCommandModule.cs:72`) | Reads `server.Definition` | 2 |
| [low] `world.timeline` withholds through a predicate of its own, provable only windowed (`WorldTimelineCommandModule.cs:137-140`) | A door with its own check | 2 |
| [unconfirmed] A search over an arena holding private keyed sites may compute private outcomes (`src/Puck.State.Search/ArenaSearchEffectHost.cs:30-32`) | Search runs as the authority; its outputs are rows with their own policy | 2 (confirm), see [what serves authors](#what-serves-authors) |

## The decisions

### 1. One capability: the recipient's view

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

### 3. A writer that cannot read a row whole cannot write it

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
mutation that touches the row is refused by policy, before anything else is
looked at and with one text whatever the key: `'<row>' is not disclosed to
<principal>`. That covers `UpsertStateCell` (Set and Add), `RemoveStateCell`,
`UpsertStateRow`, `TransformState` and `Generate` naming the row, a batch
holding any of them or an `ExpectedCells` or `ExpectedSpatialReads` guard that
reads it (the whole batch refuses, as one member's refusal refuses a batch
today), and an addon's bind or write of a cell in it, which answers one
verdict, `NotDisclosed`, for a hidden key and an absent one. The refusal is the
actual outcome, so the typed completion (`Refused`, code
`world.mutation.undisclosed`, the one text), the replay tape's recorded
outcome and the echo all say the same thing and nothing else, and the recipient
is shown its own stream's version ([decision 5](#5-a-delta-is-a-difference-of-views-owned-by-the-stream)),
never the authority's. The view's `Admits(mutation)` is the one
predicate, used by `TryAdmitCompleteMutation` ahead of grants, by the compose
arms, by `WorldServer.WriteStateCell` and the addon runtime's bind, and by the
forwarded submission door, replacing `ReadsThroughDisclosure`,
`IsWithheldFromWriter`, `IsRowWithheldFromWriter`, `DisclosesStateRead`,
`TryAdmitGuardObservation`'s Observe-only arm and `WriteStateCell`'s
`CanReadCell`.

**Sealed bids are authored.** The writer writes a row it reads whole and
nobody else does (a per-writer row with `visibility { readers [<writer>] }`),
and a rule moves the value into the authority-only row and clears the inbox.
The writer's receipt is an ordinary receipt on its own row; the moment of the
move, the count of bids and their values are the authority's. The standard
package carries the pattern as a template (`sealedInbox`, one row per seat
with the collecting rule), so an author imports it rather than rebuilding it.

**Why:** the owner's ruling to prefer the fix at the cause and the lead's
direction to prefer the simpler sound design. A write a writer cannot read the
result of is a read-modify-write whose public effect depends on the secret;
no receipt wording changes the effect. Refusing by policy has one outcome,
which is why it survives every consumer of outcomes (echo, typed completion,
addon reply, replay) without a split between what the recipient is told and
what happened. The authored inbox gives the game what blind writes promised,
on rows the engine can reason about.

### 4. The physical layout follows the hidden policy

A dealt child stands at `offsets[slot]`, and the authority's slot is history
(the lowest slot free when the cell arrived). A reader shown a visible card at
slot 1 learns a hidden card holds slot 0; a child built after a hidden one
keeps that offset as its position for as long as `preserve.transform` holds it
(`WorldTick.Deals.cs:544-552,561`).

**Puck builds:**

- **Re-dealing in the view, transforms included.** For a template whose row
  the view does not read whole, the kept cells are dealt in the disclosed row's
  cell order into slots `0..n-1`. A child's base transform is the deal's,
  `offsets[i]`; its displacement is what the authority's child carries beyond
  its own base, `Position - offsets[authoritySlot]` (and the yaw and scale
  `preserve.transform` kept), so the recipient sees `offsets[i] + displacement`.
  Nothing of the authority's slot survives into the recipient's transform. Zero,
  one and many hidden cells give one layout. The view's placements are the ones
  `world.placements`, the wire projection, `world.reflow.preview` and every
  spatial fingerprint read for that recipient.
- **A restricted template's children act on nothing.** A template whose row
  can withhold from any principal (its row declares a visibility policy) deals
  children that carry no `Solid`, `Grip`, `Spatial`, `Region` or `Emission`
  facet, whatever its `hidden` policy; the validator refuses such a facet on
  such a template by name. A hidden child then has no collider, no clearance,
  no region and no light, so no body, reflow, probe or spatial guard can meet
  it, and a visible child's physical absence tells nothing because no child of
  the template is physical. The table, the felt and the public piles stay
  solid as ordinary placements; the cards are posed, which is how the card
  tables already work ([card tables](open-items.md#card-tables-in-the-board-game-room)).
- **`Placeholder` is a look, not a thing.** Under `hidden: Placeholder` the
  view deals an anonymous child for each hidden cell at the authority's slot,
  with the template's own prototype and no variant: positions and count are
  what the policy discloses. Its id is `<template>/$back:<slot>`, a
  `GeneratedName`-minted id with no key in it, stable while the hidden cell
  holds the slot. It is not a target: a pick on it answers the template's id
  with no key, and a submission naming it is refused by name as a placeholder,
  because the authority has no such row. Under `Count` placements are as under
  `Omit`; the count travels in the observation row's `HiddenCount`, where it
  already does.

**Why:** renumbering on the authority is rejected for the reason
`WorldPlacementDeal` records: it would shuffle every later instance when a cell
leaves. Renumbering in the view costs nothing on the authority and is a pure
function of the disclosed row, once the transform is split into the deal's base
and the instance's displacement; keeping the authority's position as
"instance-owned" would keep the leak. Physical facets on hidden children are
an oracle no view can close (a collider the authority solves against exists for
every body in the room), so the template may not author them; that is a
validator refusal rather than a per-recipient collider, because the authority
has one collider set. An interactive back would need recipient-scoped ids with
generations and a reverse translation into authority targets; a back the
policy only shows is a look, and a visible card's own id already works, so the
plan drops the physical form rather than building that translation.

### 5. A delta is a difference of views, owned by the stream

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

**Rules are the authority.** A rule reads every cell (it is the world's own
program), and a rule that copies a hidden value into a public cell, or a
`search` job whose outputs are public rows judged over private sites
(`ArenaSearchEffectHost.cs:30-32`), is an authored disclosure, exactly like a
binding on the shared screen. Step 2 confirms that a search's outputs carry the
policy the author gave them, and a search output whose audience is wider than
its restricted inputs is refused (the owner's ruling, [open items](#open-items)).

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
| Keep a child's authority position as instance-owned under `preserve.transform` | Reject | The position is `offsets[authoritySlot]` plus a displacement; keeping it keeps the slot | Nothing |
| Interactive, solid card backs with recipient-scoped ids | Reject for now | Needs opaque ids with generations and a reverse translation into authority targets; the policy only shows a back, and a visible card's own id already works | An authored game that needs an opponent to touch a face-down card |
| Per-recipient colliders for hidden children | Reject | The authority has one collider set; a hidden collider is met by every body in the room | Nothing |
| A per-recipient arena copy | Reject | Stale against a moving table; the live store is the reason `Compose` reads the arena (`WorldStateDisclosure.cs:58-60`); cost per recipient per tick | Nothing |
| One last-sent image per principal, seeded by composition | Reject | Two streams of one principal, and a read composing the view, would advance a cursor no delivery moved | Nothing |
| A member-level analyzer as the guard | Reject | Misses `ArenaHost.Arena`, `IStateReader` through `RuleHost`, generic constraints and a bare `ViewFor(Principal.Console)`; cannot follow a captured delegate | Nothing; `internal` governs acquisition and the analyzer keeps the rule it can decide |
| A separate authority project as the guard | Reject as the sole guard | SDK references flow transitively and Server's public API would still carry the members | Nothing |
| The assembly-level denied-API gate | Reject as the guard | Sees assembly references, not members | Nothing; it stays for `System.Console` and Azure |
| Making `world.identify` operator-only | Watch | Hashing the recipient's view gives a per-recipient identity, which is what the recipient was handed; operator-only is simpler | The owner wanting one QR for every seat |
| A separate body-disclosure view (`WorldOutputHub.Redact`, `WorldSinkDisclosure`) folded into the recipient view | Watch | Body redaction is the same shape (a per-sink policy applied at the hub) and the natural second occupant of the view; out of scope here so the state fix lands alone | The view landed and a body-disclosure finding |
| Refusing `search` jobs whose public outputs are judged over restricted sites | Watch | An authored disclosure like a shared-screen binding; a lint is the owner's product call | The owner's ruling on what authors may hide and from whom |
| Isolating the arena byte ceiling per row or per audience | Watch | A resource channel outside the guarantee; the per-row `capacity` already bounds cells | An authored world where the shared ceiling is reached with hidden rows |

## The build order

Each step is one lane and lands alone. The check is the **noninterference
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
  at the same tick, recorded at the session sink's withheld span and resume: the
  replica resume path delivers `server.Definition` directly after withholding
  (`src/Puck.World.Server/WorldSessionSink.cs:180-193`), which an always-observing
  session never reaches.
- **A federated traveller**, with transfer credentials and forwarding state:
  traveller stream validity depends on the authority definition object's
  identity (`src/Puck.World.Server/WorldTravelerProjection.cs:47-50`), so a
  hidden-only install can invalidate that stream.
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
| `world.budget` | seat2 | any hidden member | the raw placement count and rows, dealt children included | 2 |
| `world.placements` | seat2 | any hidden member dealt before `pub` | `pub`'s `dealSlot` and position | 4 |
| `world.projection` | seat2 | any hidden member | the presentation projection's byte count | 2 |
| `echo` | seat2 | any hidden member | `world.state.cell.remove hand secret0` answers `ran` with no text in both, and its echo says `applied` against `has no cell keyed 'secret0'`: a membership oracle a fire-and-forget verb carries only on the echo | 3 |
| `deliver.state`, `deliver.definition` | every session | any hidden member | the re-deal after seat2's blind removal moves `pub`'s dealt position (`$.placements.rows[2].position[0]`) | 3, 4 |
| `world.state.transform`, `world.state.act` | seat2 | two hidden members | after seat2 overwrites the hidden `secret1`, a transfer out of `hand` is refused as `state operation is not disclosed` against `not an ordered token zone` | 3 |
| `outcome.transform`, `outcome.act` | writer | two hidden members | the same refusal texts on the typed outcome | 3 |
| `deliver.snapshot`, `query.state` | every session | two hidden members | the field deltas and state observations after the blind overwrite | 1, 2, 3 |
| `world.state.observe`, `world.observe`, `world.state` | seat2 | two hidden members | the observations of `hand` after the blind overwrite | 2, 3 |
| `deliver.state`, `deliver.definition`, `deliver.snapshot` | every session | a hidden-only install mid-run (`late`) | a delivery in one world and none in the other at tick 40, the replica's moved-row list, and a field delta for the installed row | 1 |
| `outcome.*`, `query.state` | every session | `late` | the typed outcome's document version, and the observations after the install | 1, 3 |
| `echo`, `world.state.transform` | seat2 | `late` | a drawn transfer out of `hand` is refused as `state operation is not disclosed` against `not an ordered token zone` (a drawn `Generate 'hand'` likewise as `draw site is not disclosed` against `requires a redrawable draw site`) | 3 |
| `world.state`, `world.observe`, `world.budget`, `world.placements` | seat2 | `late` | the install's cells, counts and deal | 2, 4 |

The drawn doors move with the registry, since the fuzzer draws over every verb
anyone may run: a verb added or removed elsewhere changes which lines a seed
draws. The authored rows (the schedule's and the script's) stay where they are.

**No step opens a door before its protection lands.** The order puts the
delivery protections (the cursor, recipient-scoped versions, the delta over
images) first, since every later step changes what recipients are shown and
would otherwise show its own change through the old cache; the write door only
refuses more, never admits more, so it opens nothing; the layout lands with its
facet refusal in one step; and the old predicates stay in force beside the new
types until step 6 deletes them, so between steps both answer and the stricter
one wins.

| Step | Lands | Check |
|---|---|---|
| 0. The harness and the games | The noninterference harness (above): paired boots differing only in hidden state, one principal of each kind driven identically in both, byte-for-byte comparison of everything it receives, leaks named by door; the input fuzzer over the principal's grants; `DisclosureHarnessLawTests` and `puck disclosure <world>`. The four fixtures as authored Puck games in `worlds/debug/`, each at a table in the tables room and each with its pair script and its hidden difference: **the auction** (sealed bids into an authority-only row, the reveal, the winner; its pair differs by one bid present or absent), **the card game** (a draw pile, a discard pile and dealt hands under `Count` and `Placeholder`, a displaced visible card; its pair differs by one hidden card), **social deduction** (roles in a restricted row, private chat channels through `chat.whisper` inboxes with `readersFrom` audiences, a vote; its pair differs by one role and one whisper), **the skirmish** (units with hidden positions under fog, a `search` job over what a side can see, physics on a small board; its pair differs by one unit in the fog). The existing probe is the fifth, smallest fixture | The harness runs over the five fixtures and the fuzzer and is red on today's code at exactly the findings' doors, naming each. The games settle what the plan left open: the auction shows whether an authored private inbox gives a sealed bid everything it needs (if it does, blind writes stay rejected; if it cannot, decision 3 is revisited with the auction's requirement in hand); the skirmish shows whether a `search` with public outputs over hidden rows leaks and so decides the lint; the card game decides the `$back` contract and the layout; social deduction decides whether `readersFrom` audiences and inbox eviction (`chat.whisper`'s bounded inbox) need a door of their own |
| 1. The view and the cursor, deltas first | `WorldRecipientView` in Schema, stateless, with its ceiling, beside the old types (nothing deleted); `WorldDeliveryCursor` with the shared image table and the per-key change bitset in the carrier; `Advance` over images; recipient-scoped sequence shown on deliveries, outcomes and provenance; traveller validity by projection identity; both sinks deliver through a cursor, `Resume` included; `WorldStateChangeDisclosure` and `Forget` deleted; `host.frames`, `host.memory`, `world.counters`, `world.gpu-timing` and `world.collision.probe` registered Operator; the spectator measurement in `world.budget` | Harness: the replica, presentation and peer principals' deliveries, counts, sizes, versions and stream lifetimes compare equal across every pair, on the hidden-only tick and across a read between deliveries; `StateChangeDisclosureLawTests` rewritten over `Advance` with the in-place vector write; the measurement law reports N spectators × M vectors with one composition per audience class per changed row |
| 2. Reads through the view | Every non-operator read door reads the view at its ceiling: `world.budget`, `world.placements`, `world.reflow.preview` (over the view's placements), `world.topologies`, `world.timeline`, `world.projection` composed for the caller, `world.identify`, `world.hud.template`, `world.state.similar`, `AnswerStateObservations`, `WorldFederationCodec.Observation`, the prototype fetch, `WorldTravelerProjection`; sessions read through the view's ceiling in place of `ReadSessionDefinition`; the skirmish's `search` doors confirmed | Harness: every read door the fuzzer reaches compares equal except the layout doors step 4 owns; `ConsoleDisclosureLawTests` green; `puck references` shows no `server.Definition` in `Puck.World` or `Puck.World.Console` outside operator verbs and boot |
| 3. The write door refuses by policy | `Admits` and `Refusal` on the view; every mutation touching a restricted row refused by policy for every recipient, seats included, batches and guards (`ExpectedCells`, `ExpectedSpatialReads` through `Fingerprint`) included; the typed completion's code and text; the addon's `NotDisclosed` verdict on bind and write; the forwarded submission door; the `sealedInbox` template in `worlds/standard/` the auction imports; the seven old predicates deleted | Harness: every write the fuzzer makes (set, add, remove, transform, generate, guarded batches, addon binds and writes) compares equal in reply, typed outcome and later deliveries; the auction plays to its reveal with each bidder's receipts on its own row; `TransformDisclosureLawTests` updated to the one text |
| 4. The layout | The view re-deals restricted templates with base and displacement split; `$back` children under `Placeholder` with their pick and submission contract; the validator's refusal of `Solid`, `Grip`, `Spatial`, `Region` and `Emission` on a restricted template; `world.placements`, the projection and reflow from the re-deal | Harness: the card game's and the skirmish's `world.placements`, `world.reflow.preview`, picks and projections compare equal across their pairs, the displaced card included; `puck compile --validate` refuses the card game's template with `solid` by name; `PlacementResponseDisclosureLawTests` and `WorldDeckAndDisclosureLawTests` updated to the renumbered slots |
| 5. The compiler | The authority `internal`; `WorldOperatorAuthority` and the two doors; an operator factory on `CommandDefinition` and `OperatorContext`; `TryResolveServer` deleted; every module's constructor rewired (the 47 files); DISC001 with `Puck.Analyzers.Tests` fixtures for a module taking a `WorldServer`, one taking the view, and an operator handler; the public-surface law | The analyzer test is red on a planted `WorldServer` constructor parameter and green on the view; the surface law green; `dotnet build Puck.slnx` zero warnings; the harness still green |
| 6. Nothing else decides | `WorldStateDisclosure` the class and `WorldStateReadView` deleted; `view.Withheld` where `ReadsWhole` serves and the timeline's lambda gone; the documents updated: [console](../../.claude/skills/puck-world/references/console.md) "Disclosure chokepoint", [traits](../reference/state/traits.md#control-what-observers-learn), [documents-state](../../.claude/skills/puck-world/references/documents-state.md), the Schema and Server READMEs, [worlds](../reference/state/worlds.md) | `puck references WorldStateDisclosure` answers nothing; the harness green over the five fixtures and the fuzzer: done |
| 7. Red team | The extension that seats AI agents as players over the harness (an `IPuckExtension`, [hosting](../../.claude/skills/puck-world/references/hosting-and-release.md)), each agent driving one principal in both worlds of a pair with inputs it invents, `world.state.similar` among its doors; a difference it finds becomes an authored script in the fixture | A red-team run over the five fixtures adds no new script, or every script it adds is green after its fix |

Steps 0 to 2 are sequential; 3 and 4 depend on 2 and not on each other; 5
depends on 2 and lands before 6; 7 runs once 6 is green and then on every
later change to a door. Where a step and the
[records and pools](records-and-pools.md) work meet (pool storage carries no
visibility today), the view treats a pool row as read whole, and the policy a
pool later declares enters through `ReadsWhole` alone.

## Open items

- The owner's ruling: a `search` job whose outputs reach a wider audience than
  the restricted rows its scoring reads is **refused**, at compile time for
  declared audiences and at run time where an audience changes live (fog that
  follows line of sight), exactly as a transform is refused with
  `TransformWidensAudience`. A refused job lands nothing, as a faulted search
  does. The author confines the output's audience (the authority, the side
  that may read the input, an omniscient spectator tier) or discloses
  deliberately through a rule's own write, which makes every reveal mechanic
  (a sonar ping, hot-and-cold hints, an end-of-game reveal) an explicit line of
  the document. A search over the side's own view or memory compiles as before.
  Step 2 builds the check beside the transform's. The evidence:
  `worlds/debug/skirmish-tests.puck`, the two `search evidence` scenarios.
  Blue's `skirmishSharp` scores its move by
  red's real `skirmishPos`, which only `seat1` reads, and lands its best move in
  the public `skirmishSharpBest`, so the destination says which side of the
  board the hidden unit is on and the score gives its exact distance; its twin
  `skirmishFair`, scored over blue's own `knowledge` row, answers the same every
  time. A transform that reads a restricted row is refused when its
  destination's audience is wider (`TransformWidensAudience`,
  `src/Puck.State.Rules/Transforms/Disclosure.cs:120-143`); a search's outputs
  go through no such check. The fair job is honest only because its score reads
  nothing but blue's memory: its candidates still step onto cells where red's
  hidden units stand (`relocate(displace: true)` takes them), so a score reading
  what the step changed would leak the same way.
- Whether blind writes return: rejected here (decision 3). The auction fixture
  (`worlds/debug/auction.puck`, `worlds/standard/sealed.puck`'s `sealedInbox`)
  needs nothing more: each bid lands in the bidder's own inbox, a rule moves
  it into the authority's row, the bidder's receipt is its own row, and the
  close's timing is a fixed deadline, so nothing public moves with a bid.
  Until step 3 lands, a seat still writes another seat's inbox under the
  seeded `Edit/all` grant (unread, but written), the seat write oracle that
  step closes.
- What the other fixtures settled. Social deduction: a `readersFrom` row must
  itself be public (`WorldDefinitionValidator.Disclosure.cs:169-177`), so a
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
  variant's setup steps apply, and every control agrees. The skirmish is red
  at `world.state` to `seat2` (tick 40) and at `deliver.snapshot` to the
  replica and editor sessions (tick 15) in each of its three fog pairs: the
  public `skirmishSharpBest` row (`to`/`score` 25/-4 against 26/-7, 24/-4 and
  24/-5), which is the search door; its late in-fog move is green. The card
  game is red at the step-1 doors (`deliver.snapshot`, `deliver.state` and
  `deliver.definition` on the hidden-only installs, and every session
  `outcome.*` carrying the install-counting version), at `world.budget` and
  `world.projection` (step 2), at the sessions' `query.state` in the two pairs
  that swap a hand's card with the deck's (the `Placeholder` row a session
  reads moves with the hidden swap), and at `world.placements` in the pair
  where hidden cards leave seat 1's hand and return before a card is shown
  (the shown card's `dealSlot` and position, step 4). The auction is red only when a bid is sealed mid-run
  (`deliver.snapshot` to the replica and editor sessions at the bid's tick);
  a bid present or absent, of another amount or over the purse, sealed before
  the observed window, is green. Social deduction is green over another pair
  of moles, a whisper between two other seats, a line in the den and two
  other seats' votes: its hidden changes are rule writes into per-cell and
  per-seat rows that move nothing a crew seat or a session is delivered.
- Whether `world.identify` hashes the recipient's view or becomes operator-only
  (decided here: the view; the owner may prefer one QR per world).
- Body disclosure (`WorldOutputHub.Redact`) joins the recipient view when its
  next finding arrives; the view is designed to hold it.
- The resource channels named as best effort are measured, not assumed: step
  1's spectator check records the per-tick disclosure work a hidden write
  costs, so the width of the timing channel is a number in `world.budget`.

---

[Plans](README.md) · [State and language](state-and-language.md) · [Open items](open-items.md#security-review) · [Decisions](../decisions/state-and-language.md)
