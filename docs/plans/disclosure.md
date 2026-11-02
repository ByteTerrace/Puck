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

This plan is the structural fix. It names the one chokepoint, decides the
semantics the chokepoint enforces (writes by readers who cannot read, physical
layout, change timing), says how the compiler holds the line, and orders the
build as landable steps each with a check on the disclosure probe world. The
criterion that decided every verdict: a reader learns exactly what the author's
policy says it may, and nothing about the mechanism that keeps the rest (the
owner's ruling that seed data is often a public game feature, so disclosure is
the author's policy, [documents-state](../../.claude/skills/puck-world/references/documents-state.md)).

## Where the boundary stands

The plan rests on these facts, read from the code; recheck them before
scheduling a step:

- The policy question is already one question. `WorldStateDisclosure.Observer.ReadsWhole`
  (`src/Puck.World.Schema/WorldStateDisclosure.cs:758-768`) decides from
  declarations alone whether a row can withhold anything from a recipient: its
  own policy, a cell policy its storage admits (`StateArena.DeclaresCellVisibility`,
  `src/Puck.State/Arena/StateArena.Runtime.cs:98`), or a containing ordered zone.
  `Discloses(row, key)` (`:771-778`) answers a withheld and an absent cell alike.
  The three hidden policies (`HiddenCells.Omit`, `Count`, `Placeholder`,
  `src/Puck.State/StateVisibility.cs:8-16`) say what an admitted reader of the
  row learns about the cells it may not read.
- The chokepoint exists for console verbs and is partial. `WorldStateReadView`
  (`src/Puck.World.Server/WorldStateReadView.cs`) is minted per acting principal
  by `IWorldConsoleAuthority.ReadView` (`src/Puck.World.Console/IWorldConsoleAuthority.cs:24`),
  but the same interface's `TryResolveServer` (`:39`) hands any handler the whole
  server. `puck references Definition --containing WorldServer.Definition
  --no-doc` lists 638 references: 122 in `Puck.World.Server`, 98 in
  `Puck.World`, 28 in `Puck.World.Console`, 11 in `Puck.World.Silo`, 1 in
  `Puck.World.Addons`, 14 in `Puck.Cli.Bench` and the rest in tests; the same
  query for `WorldServer.Arena` lists 271, of which 130 are the server's own
  and 9 are in `Puck.World`, `Puck.World.Console` and `Puck.World.Addons`.
  Among them, verbs any seat may run: `world.budget` (`src/Puck.World/WorldPopulationCommandModule.cs:208-234`,
  registered at `:460` without an audience), `world.placements` and its
  siblings (`src/Puck.World/WorldPlacementCommandModule.cs:60,203,262,345,405`),
  `world.topologies` (`src/Puck.World.Console/WorldStateCommandModule.Discrete.cs:78-80`),
  `world.timeline` with a predicate of its own (`src/Puck.World.Console/WorldTimelineCommandModule.cs:137-140`),
  `world.projection`, which composes for the public observer whoever asks
  (`src/Puck.World.Console/WorldNetworkCommandModule.cs:213-231`), and
  `world.identify`, which hashes the authority document for a seat holding
  Control over a screen (`src/Puck.World/WorldIdentifyCommandModule.cs:41-46,72`).
- The policy predicate exists several times. Beside `ReadsWhole`, the write and
  admission paths scan current members: `WorldDocument.DisclosesStateRead`
  (`src/Puck.World.Server/WorldDocument.StateCommands.cs:97-116`) walks every
  present cell, `IsWithheldFromWriter` (`src/Puck.World.Server/WorldDocument.Compose.Cells.cs:576-585`)
  asks `CanReadCell` (`WorldStateDisclosure.cs:181-187`), which reads the cell's
  current visibility and so passes an absent key through a public row policy,
  and the batch guard `ExpectedCells` is checked twice, both times from the
  authority arena (`WorldDocument.StateCommands.cs:135-142`,
  `WorldDocument.Compose.Batch.cs:82-106`), after an admission that checks the
  Observe grant alone (`WorldDocument.StateTransformAdmission.cs:127-147`).
  `ReadsThroughDisclosure` (`Compose.Cells.cs:574-575`) names addons, peers and
  sessions; a seat writes under its grants alone.
- Every delivery sink discloses by hand. `WorldSessionSink` discloses a
  replica's definition at `:218-225` and forgets its delta cache, but `Resume`
  delivers `server.Definition` raw (`src/Puck.World.Server/WorldSessionSink.cs:188-193`);
  `WorldFederationProjectionSink` forgets the same cache at `:156`;
  `WorldFederationCodec.Observation` composes from `server.Definition` for a
  recipient (`src/Puck.World.Server/WorldFederationCodec.Observation.cs:104-105`);
  `WorldRuleHost.AnswerStateObservations` composes from `Host.Arena` and
  `Host.Definition` (`src/Puck.World.Server/WorldRuleHost.Queries.cs:114-119`).
  Every composer takes `(definition, arena, recipient)` as three separate
  arguments, so nothing stops a caller passing the authority with a recipient
  it never checked.
- The delta cache is a comparator, not a seed. `WorldStateChangeDisclosure.Filter`
  (`src/Puck.World.Server/WorldStateChangeDisclosure.cs:22-93`) marks whole rows
  changed from row headers (`:41`), recomposes each restricted row and
  suppresses the replacement only when `m_sent[ordinal]` holds an equal image
  (`:85`); `Forget` (`:97`) clears it, so the first hidden-only change after a
  definition delivery sends an unchanged replacement, and the stored image's
  vector payloads alias arena memory (`:90`, `ReadCellImage`).
- The deal's slots are authority history. A child takes the lowest free slot
  (`src/Puck.World.Server/WorldTick.Deals.cs:426`, `FirstFreeSlot`) and stands at
  `offsets[slot]` (`:392,455,465`), by design so a departing cell moves no
  sibling (`src/Puck.World.Schema/WorldPlacementDeal.cs:22-28`). Disclosure
  removes a hidden child but keeps the visible one's `DealSlot` and transform
  (`WorldStateDisclosure.cs:479-482`, `:562-622`), and `world.placements` prints
  the slot (`WorldTick.Deals.cs:102`).
- Local presentation is unfiltered by design: the boot client reads the
  definition directly (`src/Puck.World.Schema/WorldProjection.cs:171`) and the
  colocated snapshot sink takes every body (`src/Puck.World.Server/WorldOutputHub.cs:11-20`);
  a binding to a restricted row on the shared screen shows it to the room
  ([console](../../.claude/skills/puck-world/references/console.md), "Local
  presentation is not filtered"). This plan keeps that: the shared screen is the
  author's, and the operator's.
- The enforcement tools in the repository are an analyzer family with named
  allowlists (`src/Puck.Analyzers/`: ENV001, NET001, GPU001, INTEROP001, IVT001)
  and an assembly-level denied-API gate (`build/PuckBuildTasks.cs:678-724`,
  `PuckArchitectureDeniedApi` in `build/Architecture.props`) that sees assembly
  references, not members. `InternalsVisibleTo` is forbidden (IVT001).

## The findings this plan answers

Every finding of the two reviews with a disclosure cause, where it is today, and
the step that closes it. The rule-evaluation findings the same reviews made (a
faulting remove key committing a partial firing, read operands that erase key
faults) are not disclosure and stay in the
[security review packet](open-items.md#security-review).

| Finding | Cause today | Step |
|---|---|---|
| [high] Replica resume delivers the raw definition and neither seeds nor forgets the delta cache (`WorldSessionSink.cs:188`) | A sink discloses by hand on one path and forgets on another | 1, 4 |
| [high] Batch `ExpectedCells` is a read oracle over undisclosed cells, implemented twice (`StateCommands.cs:135`, `Compose.Batch.cs:82`); admission checks the Observe grant only (`StateTransformAdmission.cs:127`) | A guard is a read that never passed the policy; two implementations | 2 |
| [high] Transform and Generate admission test current hidden membership (`StateCommands.cs:97-116`) | A second whole-row predicate beside `ReadsWhole` | 2 |
| [high] Per-cell upsert and remove receipts distinguish hidden from absent for peers, addons and sessions (`Compose.Cells.cs:292,576,636`) | `CanReadCell` reads current visibility; an absent key passes a public row policy | 2 |
| [medium] A seat writes under its grants alone and learns which hidden keys exist (`Compose.Cells.cs:574`) | Seats are not recipients at the write door | 2 |
| [high] Dealt children keep hidden-dependent slots and positions (`WorldStateDisclosure.cs:481,621`, `WorldTick.Deals.cs:426`) | The layout is authority history, shown as is | 3 |
| [high] `world.budget` publishes raw placement counts (`WorldPopulationCommandModule.cs:225`) | A diagnostic reads `server.Definition` with no audience | 1 |
| [high] Forgetting the delta cache reopens a timing oracle after re-disclosure (`WorldStateChangeDisclosure.cs:85,97`) | A comparator cache seeded by nothing | 4 |
| [medium] The last-sent image aliases live vector memory (`WorldStateChangeDisclosure.cs:90`) | The image is a view of the arena, not a copy | 4 |
| [low] `world.identify` hashes authored hidden seeds for a seat with Control over a screen (`WorldIdentifyCommandModule.cs:72`) | Reads `server.Definition` | 1 |
| [low] `world.timeline` withholds through a predicate of its own, provable only windowed (`WorldTimelineCommandModule.cs:137-140`) | A door with its own check | 1 |
| [unconfirmed] A search over an arena holding private keyed sites may compute private outcomes (`src/Puck.State.Search/ArenaSearchEffectHost.cs:30-32`) | Search runs as the authority; its outputs are rows with their own policy | 1 (confirm), see [what serves authors](#what-serves-authors) |
| The earlier review's D1 to D8 (`cellSlots`, HUD template, observation inclusion, whole-row edits, binding validation, replica deltas, `Withheld` cost) | Closed by `disclosure-2` on `ReadsWhole`; kept closed by step 6's deletion of every other predicate | 6 |

## The decisions

### 1. One capability: the recipient's view

**Puck builds:** `WorldRecipientView`, the one object a recipient's reads,
writes, receipts, deliveries and diagnostics are derived from. It replaces
`WorldStateReadView` and absorbs `WorldStateDisclosure`'s `Observer`,
`WorldStateChangeDisclosure` and the dealt-placement half of `Disclose`. It
lives in `Puck.World.Schema` beside `WorldProjection`, is constructed over a
definition, an arena and a principal, and is minted for the live world only by
`WorldServer.ViewFor(Principal)`, which stamps it with the completed tick. The
server holds one per attached session, peer, addon and seat for as long as that
recipient is attached, so the view can carry the last image it sent (decision 5).
It offers:

- `Definition`: the document as the recipient may read it, with every withheld
  cell removed, storage metadata stripped whether or not anything is withheld
  now, and every dealt and responsive placement re-read from the disclosed rows
  (decision 4).
- `ReadsWhole(row)` and `Discloses(row, key)`: the policy questions, read from
  the live store at the call. `TryRead(row, key)` answers a withheld cell and an
  absent cell identically, as `Absent`.
- `Observations(presented)`, `Placements`, `FieldRegions`: what today's
  `WorldStateDisclosure.Compose`, `PlacementProjection` and `FieldProjection`
  answer, with no `recipient` argument because the view is the recipient.
- `Project(tier, time, anchors)`: the one egress composer, today's
  `WorldProjection.Compose`, taking the view in place of `(definition, arena,
  recipient, unrestricted)`.
- `Advance(changes)`: the recipient's deltas for a tick (decision 5).
- `Admits(write)` and `Receipt(mutation, outcome)`: the write-door decision and
  the one echo a recipient is answered with (decision 3).
- `Refusal(verb, row, key)`: the one refusal text, worded alike for withheld and
  absent.
- `Budget`: the counts a diagnostic may print, derived from `Definition`.

The operator's view (`Principal.Console`) and the world's own (`Principal.World`)
are the authority: `Definition` is the live export, `ReadsWhole` is always true,
and `Advance` passes the snapshot through. A measurement that sizes for every
possible recipient asks for `WorldRecipientView.Widest(server)`, today's
`unrestricted: true`.

**The rule the view makes structural:** `Principal? recipient` disappears from
every composer's and codec's signature. A method that takes a `WorldDefinition`
and a `StateArena` is the authority's own and composes for nobody; a method that
composes for someone takes a `WorldRecipientView`, and the only way to name a
recipient is to hold its view. A sink, a codec or a verb cannot pass the
authority with an unchecked recipient because no such parameter exists.

**Why:** the reviews' doors have one shape: a path reaches `server.Definition`
or `server.Arena`, composes something for a recipient, and omits or
re-implements the policy question. `WorldStateReadView` already proved the
shape of the fix for console verbs (`ConsoleDisclosureLawTests` sweeps every
console module verb as a non-reader), and the doors it did not cover are the
doors that leaked. One object that every per-recipient answer derives from is
the only arrangement where a new door inherits the check instead of owing it.
It is also the one-implementation ruling applied: `ReadsWhole`,
`DisclosesStateRead`, `IsWithheldFromWriter`, `CanReadCell`, the timeline's
predicate and the two batch guards are six readings of one question.

### 2. A recipient is anyone the authority is not

**Puck builds:** every principal but `Console` and `World` is a recipient: a
local seat, a peer, a session, an addon, an agent participant, a federated
traveller. A seat's console session, its writes and its receipts go through
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

### 3. Writes by readers who cannot read are blind writes

A sealed bid is a write into a row the writer may not read, and it must keep
working. The reviews' oracles come from a receipt whose text or verdict depends
on whether the key exists: `(evicted 'key')` names the victim
(`src/Puck.World.Server/WorldDocument.Mutation.cs:469`), a remove of an absent
key answers "has no cell keyed" where a hidden one is removed
(`Compose.Cells.cs:648`), an `Add` to an absent key fails where a hidden one
succeeds, and a batch guard compares a value the proposer cannot read.

**Puck builds:** for a write by a recipient to a row `ReadsWhole` denies it:

- Only `Set` is admitted (an `UpsertStateCell` with a value, and a text or
  vector write of the same shape). `Add`, `RemoveStateCell`, `TransformState`
  on the row, `Generate` of the site, and a batch `ExpectedCells` guard on the
  row are refused *by policy* with one text whatever the key:
  `'<row>' is not disclosed to <principal>: a writer that cannot read it may
  only set a value`. A read-modify-write by a writer that cannot read is a read.
- A `Set` is admitted on the public facts alone: the grant (`Edit` over the row,
  `Mutate` over the section), the row's declared kind, key shape and envelope.
  A refusal on any of those is named, because the declaration is public.
- An admitted `Set` is answered with one receipt, `accepted`, and the authority
  resolves it privately: minted, overwritten, dropped at capacity, or evicting
  an unnamed victim. The resolution is narrated on the operator's transcript
  alone (`[world.mutation: ... applied (evicted 'k')]` stays, for the operator)
  and is never in the recipient's echo, toast or `wire.errors` count. An author
  who wants the bidder to know the bid landed writes a public acknowledgement
  cell from a rule, which is game state the author owns.
- For a row the writer reads whole, nothing changes: `applied`, the named
  refusal and the eviction note are facts the writer may read anyway.

`WorldRecipientView.Admits(mutation)` is the one predicate, used by the
compose arms in `WorldDocument.Compose.Cells`, by `TryStateMembers` for
transforms, generate and batches, by `WorldAddonRuntime`'s bound cell write
(`src/Puck.World.Addons/WorldAddonRuntime.Cells.cs:121`) and by the forwarded
submission door, replacing `ReadsThroughDisclosure`, `IsWithheldFromWriter`,
`IsRowWithheldFromWriter`, `DisclosesStateRead` and
`TryAdmitGuardObservation`'s Observe-only check. `Receipt(mutation, outcome)`
is the one echo composer for a recipient; `WorldDeferredVerbAnswers` prints
what the view composed, never the operator's message.

**Why:** the alternatives each fail a case. Refusing every write to an
unreadable row kills sealed bids. Admitting `Add` and resolving it blindly
either lies (an `Add` to nothing "applied") or tells (it refused). Answering
the true outcome is the oracle. A uniform `accepted` for a pure write, with the
resolution private and acknowledgement authored, is the only shape where the
receipt carries no bit about the row's membership, and it is Puck code over
engine code: the game, not the engine, decides what a bidder is told.

### 4. The physical layout follows the hidden policy

A dealt child stands at `offsets[slot]`, and the authority's slot is history
(the lowest slot free when the cell arrived). A reader shown a visible card at
slot 1 learns a hidden card holds slot 0.

**Puck builds:** the view re-deals every template whose row it does not read
whole, from the disclosed row alone, deterministically, by the row's `hidden`
policy:

- `Omit`: the kept cells are dealt in the disclosed row's cell order into slots
  `0..n-1`; each child's `DealSlot` and local position are `i` and
  `offsets[i]`. A child whose transform the deal preserves (`preserve.transform`)
  keeps its instance-owned transform, which is the visible card's own. Zero,
  one and many hidden cells give one layout.
- `Placeholder`: a hidden cell is dealt as an anonymous child at the authority's
  slot, with the template's own prototype and no variant, no key in its id (a
  `GeneratedName`-minted `$back` id, never the key), so the reader sees card
  backs where the author said it may. Positions and count are the policy's
  choice.
- `Count`: as `Omit` for placements; the count travels in the observation row's
  `HiddenCount`, where it already does.

The validator refuses, by name, a `solid` or `grip` facet on a template whose
row can withhold from any principal under `Omit` or `Count`: a collider the
authority keeps for a card a reader cannot see is a physical oracle no view can
close, and the authored alternative is `Placeholder`, which deals a back the
reader may touch. `world.placements` prints the view's slots; the wire projection
and the console answer from the same re-deal.

**Why:** renumbering on the authority is rejected for the reason
`WorldPlacementDeal` records: it would shuffle every later instance when a cell
leaves. Renumbering in the view costs nothing on the authority, is a pure
function of the disclosed row, and is the same function the wire and the console
already claim to apply (`WorldStateDisclosure.Disclose`'s remarks promise
"re-dealt from what remains" and the code keeps the slot). Mapping the three
hidden policies onto layout makes the author's one declaration decide
observations and placements together, instead of a fourth knob.

### 5. A delta is a difference of views

**Puck builds:** `WorldRecipientView.Advance(changes)` composes the recipient's
row replacements for a tick. For a row the recipient reads whole, the snapshot's
own replacement passes through, as today. For a restricted row, the view keeps
the last image it sent (a private copy; vector payloads copied, never
arena-backed) and the tick's changed positions are filtered to the keys the
recipient may read: a replacement is composed and sent only when a readable
key changed or the readable membership changed, and the image is replaced by
what was sent. A tick on which only hidden cells changed sends nothing, no
header included. A policy move (a `readersFrom` row written, an ordered zone
moved) recomposes every restricted row against its image, so a cell that became
readable is sent once and a cell that became hidden is sent as a removal.

A definition delivery seeds the image: delivering `view.Definition` to a
replica, or `view.Project(...)` to a presentation session or a federation peer,
stores every restricted row's sent image in the same call. There is no
`Forget`; a view's image is never empty after its first delivery, and a
`Resume` after a withheld span delivers `view.Definition` and seeds, like any
other definition delivery.

**Why:** the comparator-with-forget design cannot be seeded by its callers
without each caller remembering to, which is the door-by-door pattern again;
the view is the one object that both delivers and compares, so seeding is a
side effect of delivering. Filtering the changed positions to readable keys
removes most recompositions and makes "hidden-only change" a case the code
never reaches rather than one it compares away. The vector aliasing finding
disappears with the private copy.

### 6. The compiler holds the line

**Puck builds:** two structural guards and one API shape.

- **DISC001, the authority stays home.** A Roslyn analyzer in `Puck.Analyzers`,
  the shape of ENV001 and NET001: the authority members (`WorldServer.Definition`,
  `WorldServer.Configuration`, `WorldServer.Arena`, `WorldServer.ExportDefinition`,
  `WorldServer.MaterializeArena`, `WorldRuleHost.Arena` and the
  `WorldRecipientView` constructor) may be
  referenced only from members named in `DisclosureAuthorityAllowlist`: the
  server's own tick, mutation, journal, checkpoint, rule host, deal sweep and
  view-minting types; the boot composition roots and the silo host; the
  operator-only modules (`world.verdicts`, `world.state.hash`, save and load,
  replay); and the tests. Each entry names its reason. A console module, a
  sink, a codec or an addon runtime outside the list fails the build on the
  first reference. The analyzer runs in every project, so a new project that
  references `Puck.World.Server` inherits the rule.
- **The operator shape.** `IWorldConsoleAuthority.TryResolveServer` is deleted.
  A handler registered with `CommandAudience.Operator` is built through
  `CommandDefinition.Operator(...)`, whose handler delegate receives an
  `OperatorContext` carrying the `WorldServer`; every other handler receives
  the `CommandContext` it does today, from which `TryResolveReadView` yields
  the view and `TryResolveLink` the submission link, and nothing yields the
  server. The registry already refuses an Operator verb for every principal but
  the console before the handler runs, so the delegate shape and the audience
  are one declaration, and a verb that reads the authority cannot be registered
  for anyone else by mistake.
- **No second predicate.** `WorldStateDisclosure`'s static `CanRead`,
  `CanReadCell`, `CanReadRow`, `WithheldFrom`, `Compose`, `Disclose`,
  `PlacementProjection`, `FieldProjection` and `ValidateBindings` are deleted
  with the class, and their bodies become the view's members. A door cannot
  re-implement the policy because nothing exports the pieces.

**Why not a project boundary alone:** a boundary hides a type, not a member.
`Puck.World.Console` must hold a `WorldServer` for the operator's verbs and the
submission link, and `IVT001` forbids the internal escape hatch. The existing
denied-API gate sees assembly references (`PuckBuildTasks.cs:678-724`), so it
cannot distinguish `server.Definition` from `server.SubmissionLink`. An analyzer
with a named allowlist is the repository's established answer to exactly this
shape (ENV001 and NET001), and the operator delegate shape makes the compliant
path the shortest one to write.

## What serves authors

| Decision | What an author gets | What an author owes |
|---|---|---|
| 1, 2. One view, everyone a recipient | One declaration (`visibility`) decides every door: console, HUD template, wire, replica, federation, addons, agents, diagnostics | Nothing new; the shared screen remains the room's |
| 3. Blind writes | Sealed bids, hidden votes and private inboxes work as pure writes under ordinary grants; the engine never tells a writer what it could not read | An acknowledgement the player should see is a rule writing a public cell (`bidReceived.seat2 = 1`), which is the game's to decide |
| 4. Layout follows `hidden` | `Omit` hides a hand's shape, `Placeholder` deals card backs the room can see and touch, `Count` says how many; one declaration, observations and placements agree | A hidden hand's template carries no `solid`/`grip` under `Omit`/`Count`; `Placeholder` is the way to a touchable back |
| 5. Deltas from the view | A replica or federation peer sees a restricted row move only when what it may read moves | Nothing |
| 6. The compiler | A verb, a mod host or a codec added later cannot leak by omission | Nothing |

**Rules are the authority.** A rule reads every cell (it is the world's own
program), and a rule that copies a hidden value into a public cell, or a
`search` job whose outputs are public rows judged over private sites
(`ArenaSearchEffectHost.cs:30-32`), is an authored disclosure, exactly like a
binding on the shared screen. The engine cannot and should not decide that a
rule's output is a leak; step 1 confirms whether a search's *outputs* carry the
policy the author gave them and refuses a job whose output row is public while
its token row is restricted only if the owner wants that lint (see
[open items](#open-items)).

**The probe is the author's test.** `worlds/debug/disclosure-probe.puck` boots
a second seat through every door with zero, one or two hidden cells and shows
byte-identical answers. Step 0 extends it to every door this plan names and
makes the comparison a standard check, so an author who adds a hidden row to a
table can run it against their own world.

## Rejected and watched

| Approach | Verdict | Reason | What would change it |
|---|---|---|---|
| Door-by-door checks (`view.Withheld`, `ReadsWholeRow` at each verb) | Reject | Two lanes, two reviews, sixteen findings; a new door owes a check it never knew about | Nothing; this plan replaces it |
| Refuse every write to an unreadable row | Reject | Kills sealed bids, the stated purpose of authority-only rows | Nothing |
| Admit `Add`/`Remove` blind and report the true outcome | Reject | The outcome is the oracle (`(evicted 'k')`, "has no cell keyed") | Nothing |
| Admit `Add`/`Remove` blind and report `accepted` | Reject | A silent no-op `Add` is a lie the author cannot see; a pure `Set` has no such case | Nothing |
| Renumber slots on the authority | Reject | Shuffles every later instance when a cell leaves (`WorldPlacementDeal.cs:22-28`) | Nothing |
| A per-recipient arena copy | Reject | Stale against a moving table; the live store is the reason `Compose` reads the arena (`WorldStateDisclosure.cs:58-60`); cost per recipient per tick | Nothing |
| Hide the authority behind a project boundary alone | Reject | Members, not types, are the door; IVT001 forbids the escape hatch; the console needs the server for operator verbs | Nothing |
| The assembly-level denied-API gate | Reject as the guard | Sees assembly references, not members | Nothing; it stays for `System.Console` and Azure |
| Making `world.identify` operator-only | Watch | Hashing the recipient's view gives a per-recipient identity, which is what the recipient was handed; operator-only is simpler | The owner wanting one QR for every seat |
| A separate body-disclosure view (`WorldOutputHub.Redact`, `WorldSinkDisclosure`) folded into the recipient view | Watch | Body redaction is the same shape (a per-sink policy applied at the hub) and the natural second occupant of the view; out of scope here so the state fix lands alone | The view landed and a body-disclosure finding |
| Refusing `search` jobs whose public outputs are judged over restricted sites | Watch | An authored disclosure like a shared-screen binding; a lint is the owner's product call | The owner's ruling on what authors may hide and from whom |

## The build order

Each step is one lane, lands alone, and is checked on the disclosure probe:
`worlds/debug/disclosure-probe.puck` booted headless three times with preludes
minting zero, one and two cells only seat1 reads, seat2's lines in the three
`test-run.json` files byte-identical. Step 0 makes that comparison a check the
later steps run; every later step adds its doors to the probe's schedule. A
headless console run opens no session (`StateChangeDisclosureLawTests.cs:7-8`),
so the session doors are checked in-process over the same world and preludes.
No GPU leg: the one door a headless run cannot reach is the local HUD mirror,
which is unfiltered by design.

| Step | Lands | Check |
|---|---|---|
| 0. The probe is the check | The probe schedule gains every console door this plan names, as seat2: `world.budget`, `world.placements` over a dealt hand `[secret, public]` with distinct offsets, `world.projection`, `world.timeline`, `world.identify`, `world.state.cell.set`/`.remove` and `world.state.transform` on a hidden, an absent and a public key, and a batch with an `ExpectedCells` guard on `hand.secret0`. `DisclosureProbeLawTests` (`tests/Puck.World.Server.Tests`) boots the probe world three times with the three preludes, drives the schedule, and attaches a replica and a presentation session (the way `SessionObservationLawTests.Observe` does), then diffs seat2's lines and each session's deliveries across the three runs, including a tick on which only `secret0` changes after an unrelated row edit (the delta oracle) and a revoke-and-regrant of the session's Observe grant (the resume oracle) | The law is red on today's code at exactly the findings' doors, and names each differing line |
| 1. The view | `WorldRecipientView` in Schema replacing `WorldStateReadView` and `WorldStateDisclosure`'s public surface; `WorldServer.ViewFor`; every console verb that reads state, placements, counts or the definition for a non-operator reads the view (`world.budget`, `world.placements`, `world.topologies`, `world.timeline`, `world.projection` composed for the caller, `world.identify`, `world.hud.template`); `WorldRuleHost.AnswerStateObservations`, `WorldFederationCodec.Observation`, `WorldTravelerProjection` and both sinks take the view; `Resume` delivers `view.Definition`; the search-outputs question confirmed | Probe lines identical at every read door; `ConsoleDisclosureLawTests` green; `rg "server\.Definition"` in `src/Puck.World` and `src/Puck.World.Console` leaves only operator verbs and boot |
| 2. The write door | `Admits` and `Receipt` on the view; blind-write semantics for every recipient, seats included; one batch guard check through the view, the `Compose.Batch` copy deleted; `DisclosesStateRead`, `ReadsThroughDisclosure`, `IsWithheldFromWriter`, `IsRowWithheldFromWriter` and `TryAdmitGuardObservation`'s Observe-only arm deleted; `WorldAddonRuntime`'s cell write and the forwarded submission door on `Admits`; `WorldDeferredVerbAnswers` prints the view's receipt | Probe write lines identical for hidden, absent and public keys, with `accepted` for the restricted row and the operator's transcript naming the true outcome; `TransformDisclosureLawTests` updated to the policy wording |
| 3. The layout | The view re-deals restricted templates by `hidden`; `$back` placeholder children; `world.placements` and the projection from the re-deal; the validator's `solid`/`grip` refusal on restricted `Omit`/`Count` templates; the probe's hand template | Probe `world.placements` identical for `[secret, public]` and `[public]`; `PlacementResponseDisclosureLawTests` and `WorldDeckAndDisclosureLawTests` updated to the renumbered slots; `puck compile --validate` refuses the probe's template with `solid` by name |
| 4. Deltas | `Advance` on the view with the private image and readable-key filter; `WorldStateChangeDisclosure` and `Forget` deleted; both sinks deliver and seed through the view; replica `Resume` seeds | Probe's hidden-only tick delivers nothing to the replica session after an unrelated edit; `StateChangeDisclosureLawTests` rewritten over `Advance`, with the in-place vector write case |
| 5. The compiler | DISC001 with `DisclosureAuthorityAllowlist`; `CommandDefinition.Operator` and `OperatorContext`; `TryResolveServer` deleted; `Puck.Analyzers.Tests` fixtures for a module outside the list, a sink, and an allowlisted authority type | The analyzer test is red on a planted `server.Definition` in a console module and green on the allowlist; `dotnet build Puck.slnx` zero warnings |
| 6. Nothing else decides | Every remaining door predicate deleted (`WorldStateDisclosure` the class, `view.Withheld` where `ReadsWhole` serves, the timeline's lambda); the documents updated: [console](../../.claude/skills/puck-world/references/console.md) "Disclosure chokepoint", [traits](../reference/state/traits.md#control-what-observers-learn), [documents-state](../../.claude/skills/puck-world/references/documents-state.md), the Schema and Server READMEs, [worlds](../reference/state/worlds.md) | `puck references WorldStateDisclosure` answers nothing; `puck references WorldServer.Definition` lists only allowlisted members; the probe law green |

Steps 0 and 1 are sequential; 2, 3 and 4 depend on 1 and not on each other;
5 depends on 1 (the view it guards) and lands before 6. Where a step and the
[records and pools](records-and-pools.md) work meet (pool storage carries no
visibility today), the view treats a pool row as read whole, and the policy a
pool later declares enters through `ReadsWhole` alone.

## Open items

- Whether a `search` job whose outputs are public while its token or code rows
  are restricted is refused, linted or left to the author: a product call on
  what authors may hide and from whom (step 1 confirms the mechanism; the
  owner decides the lint).
- Whether `world.identify` hashes the recipient's view or becomes operator-only
  (decided here: the view; the owner may prefer one QR per world).
- Body disclosure (`WorldOutputHub.Redact`) joins the recipient view when its
  next finding arrives; the view is designed to hold it.

---

[Plans](README.md) · [State and language](state-and-language.md) · [Open items](open-items.md#security-review) · [Decisions](../decisions/state-and-language.md)
