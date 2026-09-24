# Shipped-world state baselines

One canonical state export per shipped world, taken after a fixed scripted input
sequence, plus the sequence itself and the tick cost of running it. These are
evidence for a diff: re-record after a change and read what moved.

## The two commands

Re-record every baseline:

```sh
puck baselines state
```

Check every baseline:

```sh
puck baselines state --check
```

`ShippedWorldStateBaselineTests` compares each export and cost record against
this directory and writes the fresh copy beside its assembly; the verb runs the
class and promotes those copies. It runs the class twice, in two processes, and
refuses to write when the two runs differ, so a nondeterministic world fails
the recording rather than pinning a coin flip. Re-recording an unchanged tree
rewrites nothing: every file above reproduces byte for byte.

## The files

| File | Holds |
|---|---|
| `<world>.sequence.json` | The scripted input sequence and how the world is composed and booted. |
| `<world>.state.json` | The canonical state export after the sequence (`puck.world.state-export.v1`). |
| `<world>.cost.json` | The exact per-tick rule work-unit budget, at boot and after the sequence. |

A sequence step is one of `advance` (that many simulation ticks), `set` or `add`
(one `WorldMutation.UpsertStateCell` under `Principal.Console`), `pose` (one
body, addressed by its placement id, moved to a literal world position or to a
named topology's computed `cell` centre), `join` (one `SessionRequest.Join` for a
1-based seat, which is what makes that seat human-occupied and so what makes
`$channel:<seat>:` read anything but zero), or `press` (one
`WorldBody.PressChannel` timed hold on a named channel, the `body.press` door:
the channel reads held for `holdSeconds` of simulation time and releases itself).
A slot row's cell key is `$value`.

## What the export covers

The export is `Puck.World.Server.WorldStateExport.ToCanonicalJson`. It exists
because nothing else rendered the live state substrate as canonical JSON: the
checkpoint codec (`WorldAuthorityCheckpointCodec`) writes an opaque binary wire
image of the whole server, `WorldReplaySnapshot`/`WorldReplayTape` carry poses
and submissions, and `WorldStateCommandModule` answers one addressed cell at a
time. The export walks nothing of its own for the row shape: its `state` member
is the live `WorldDefinition.StateRaw` section serialized through
`WorldDefinitionSerialization`, so it cannot drift from the document.

`WorldStateHashComposition` names, in one declared order, every component the
authoritative hash folds, so its list is the definition the export is measured
against. Where each component lands:

| `WorldStateHashComponent` | Export member |
|---|---|
| `Arena` (every stored cell, presence, member key, cursor, drawn mask, phase sequence and runtime-state column, and both slot lanes) | `state.world[i]`, `state.world[i].cells[j]`, `resolved[i].cells[j]` |
| `Declaration` (row kind, envelope, capacity, overflow, `gatesDrive`, `evicts`, domain, `valuesFrom`, `phaseOf`, `inverse`, `knowledge`, visibility, the advance/draw/dynamics/field/cycle records, and each cell's own traits) | `state.world[i]`, `state.world[i].cells[j]` |
| `Topologies` (`StateRaw.Lattices`) | `state.lattices` |
| `HostOwnedRows` (`FieldLattice.AppendStateHash`, field-major then cell-major) | `fields` |
| `Tick` | `tick` |
| `PopulationPose` (`WorldReplaySnapshot.HashState`: body poses, rigid residue, carry) | `hashes.pose` only |
| `Seed`, `RuleLatch`, `InteractionLatch`, `RuleGroups`, `Decisions`, `BoardEnforcement`, `BodyActionState`, `Navigation`, `Flock`, `Search` | `hashes.authoritative` only |

The mechanical evidence for the first three rows is that the same converter both
reads and writes each member — `StateRowJsonConverter` parses `drawCursor`,
`historyCursor`, `phase`, `drawnMasks`, `observation`, `provenance`, `behavior`,
`clock` and the base64url `vector`, and writes every one of them back — so the
serialized `state` section round-trips the exact `WorldStateRow` graph the hash
reads its fields off.

The last two rows are the gap. `WorldPersistence.AppendStateHashComponent` is declared
internal on `WorldPersistence` because the latches remain private implementation
details, and `WorldReplaySnapshot.HashState` folds the population rather than
exposing it. The export carries those two lanes as their digests, not as values:
a change inside them moves `hashes.pose` or `hashes.authoritative` and shows up
as a one-line diff with no detail. All four `WorldStateHashScope` digests are
recorded, so any change anywhere in the hashed boundary is visible even when the
values are not.

## How each world is booted

Most of the shipped games are document fragments, not bootable worlds: they
declare `state`, `rules` and placements for an importing host and nothing else.
Each sequence declares which of four compositions it uses. `document` loads the
world document as it ships. `source` compiles a `.puck` world outside the asset
tree, such as a Parlor package entry point, with its basis chain, the way
`--world` boots it. `fixture` loads an existing minimal host under
`tests/Puck.World.Tests/Fixtures` that imports the module. `spliced` merges the
named sections of the module onto `Fixtures.BuildDocument()` — arrays
concatenate, objects merge member-wise, the same shape a module import composes
with — optionally adding rows a sibling district would have supplied
(`host.stateRows`, `host.append`).

| World | Document | Composition | Ticks | Moves state |
|---|---|---|---|---|
| arena | `games/arena.puck` | fixture `minimal-arena-host.world.json` | 224 | yes |
| backgammon | `games/backgammon.puck` | document | 630 | yes |
| billiards | `games/billiards.puck` | fixture `minimal-billiards-host.world.json` | 510 | yes |
| bowling | `games/bowling.puck` | spliced | 600 | no |
| dominoes | `games/dominoes.puck` | spliced | 600 | no |
| freecell | `games/freecell.puck` | spliced | 610 | yes |
| go | `games/go.puck` | document | 168 | yes |
| hexlines | `games/hexlines.puck` | fixture `minimal-hexlines-host.world.json` | 600 | yes |
| hiddenranks | `games/hiddenranks.puck` | fixture `minimal-hiddenranks-host.world.json` | 190 | yes |
| klondike | `games/klondike.puck` | spliced | 680 | yes |
| lineup | `worlds/parlor/lineup.puck` | source | 135 | yes |
| mancala | `games/mancala.puck` | spliced | 590 | yes |
| moth-courtyard | `moth-courtyard.puck` | document | 600 | yes |
| paddleball | `games/paddleball.puck` | fixture `minimal-paddleball-host.world.json` | 2941 | yes |
| pipeline | `pipeline.world.json` | document | 600 | no |
| poker | `games/poker.puck` | spliced | 610 | yes |
| reversi | `games/reversi.puck` | document | 600 | yes |
| snake | `games/snake.puck` | fixture `minimal-snake-host.world.json` | 635 | yes |
| solitaire | `games/solitaire.puck` | spliced | 600 | no |
| spider | `games/spider.puck` | spliced | 810 | yes |
| tetromino | `games/tetromino.puck` | document | 130 | yes |
| tictactoe | `games/tictactoe.puck` | fixture `twin-tictactoe-host.world.json` | 600 | yes |
| wordspy | `games/wordspy.puck` | fixture `minimal-wordspy-host.world.json` | 162 | yes |

"Moves state" is asserted both ways: a sequence declared to move the world-scope
state hash must move it, and one declared not to must leave it exactly as it
booted. A sequence that exercised nothing therefore cannot pass silently.

The four worlds that move nothing do so for stated reasons. `bowling`,
`dominoes` and `pipeline` declare no state rows at all. `solitaire` declares one
`table` cell and no rules.

`billiards` and `hexlines` each need a host that completes them. `billiards`
declares one `cue` row whose two rules read seat 1's `attack` channel and strike
`placement:cueBall`; its minimal host supplies the non-rigid seat kit those rules
need a seat body for, plus the `walk` program, the `billiardBall` look, the
`billiardsColors` palette row and the population capacity its twelve balls want.
Its sequence joins the seat and presses `attack` twice: the first hold charges
`cue` and its auto-release fires `cue-release`'s impulse into the rack, and the
second hold leaves `cue` charged where the sequence ends. `hexlines` declares no
rules at all; its per-stone rows are derived by whatever host anchors the board,
so the minimal host authors the one derive rule (`hexStoneCell[$each]` from
`$board:cellOf`) the module exports `hexStoneCell` for, and the sequence poses two
stones onto computed hex-cell centres. `hiddenranks` needs a host for the same
reason billiards does and for one more: its ready handshake reads
`$channel:1:attack` and `$channel:2:attack`, so the host declares the `attack`
channel, a seat kit, and `localSeats: 2` — a seat a `$channel:<seat>:` operand
names must exist at compile time and be human-occupied at read time, so the
sequence joins both seats and presses both before any move is admitted.

`wordspy` needs a host only for its seats: its key card, the four masks
derived from it and every row the spymaster computes from them declare
`visibility` admitting `seat1` and `seat3`, so the minimal host is four local seats on the shared
`walk` kit and nothing else. The sequence joins all four, then plays through
`wordSpyAct`: three clues, four accepted touches ending on the trap, and
five refusals. Its export is the corpus's only one carrying `Vector` cells —
the base64url `vector` member of each word, clue and aim.
`arena` needs a host for both: its shot rules read seat 1's and seat 2's
`attack` channels, and its distance interaction measures real body positions, so
the host declares the `attack` channel, two local seats, a four-body population
(the two seats plus the two inhabited item placements) and the `arenaItem` look.
Its seat kit and the module's item kit both hold `Free`/`None`, and the host
authors zero uniform gravity, so every body stands where it was spawned or posed
and the pickup radius measures the arena's authored layout rather than a fall.
Its sequence joins both seats, enters the second into the live match through the
`arenaEnter` submission, plays a 2–1 match with two same-tick exchanges, two
deaths a side's respawn deadline covers, two shots the rules refuse outright,
and one medkit carried into the plaza by a `pose` step and taken.

`snake` needs a host for its input rather than its geometry: its four turn rules
read seat 1's `strafe`/`forward` channels, so its minimal host supplies the seat
kit, the `walk` program and those two bipolar channels. Its sequence joins the
seat, presses a reversal the game refuses, then plays two full runs — four meals,
two deaths on the snake's own body, and the respawn deadline between them — with
one turn driven through the console submission door instead of a press. It stops
before the second deadline passes and off the beat, so the export pins
`snakeRespawn`'s armed deadline tick beside two `snakeTempo` cells resolving to 5
and 10 from a stored phase of zero, which is what the two `cycle` powers compute.

`paddleball` needs a host for its input and its population: its serve rule reads seat
1's `attack` channel, and its five bodies — the ball, two paddles and two serve
posts — are inhabited placements whose body indices its property rows are keyed
by, so the host's `bodies.capacity` of six is part of the document's meaning. Its
sequence plays a whole game: one rally the physics plays by itself, a let, a dead
ball, three refused serves, and twenty-one points, each a real `applyRigidImpulse`
serve down a lane one paddle has been placed out of. A placed body's yaw is reset
to zero by the placing, which is why the serve aims along the two fixed posts
rather than along the serving paddle.

`tetromino` boots as it ships; its input is seat 1's own play channels, so the
sequence joins the seat before pressing. It turns, moves and hard-drops the first
piece, holds the second, soft-drops and hard-drops the piece the hold brought out,
sets the bag so the next piece is an I, turns and drops it, and leaves the next
piece falling. The export pins three locked pieces in the well, the held piece, the bag's
cursor and drawn mask, and the lock deadline of the piece in play.

`moth-courtyard`'s document is a basis delta over the `avatars/moth` document,
which the composer reads from its `avatars/moth.puck` source.

`lineup` boots as it ships from its Parlor source. Its sequence leaves the deal
to draw both busts, then plays seat 1 through `lineupAct`: three questions, each
answered by the computer on seat 2's own turn, and a wrong name that ends the
game. The export pins both standing sets and both galleries' rows.

Most of the documents these sequences name are `.puck` sources. The source tree
holds no compiled `.world.json` for them: a document compiles in memory when it
loads, and a basis or import that names a document resolves to its source where
one exists. Neither command needs a build step beyond the test project itself.

## Tick cost

`<world>.cost.json` records `WorldRuleWorkBudget` through
`WorldRuleWorkBudget.Measure`, at boot and after the sequence. These units are
exact, deterministic, and machine-independent: they are the worst-case work the
document's rules and interactions can spend in one tick, not a count of units
actually spent. The engine exposes no runtime spent-unit counter — `RuleWorkBudget`
is a static work sheet over the compiled rules — so this is the exact number
available.
