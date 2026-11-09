# Port the experimental worlds' features into the debug world

The quilt, the island (the One World) and their games, districts and tools are
quarantined under [`experimental/`](../../experimental/README.md). They are not
re-admitted: what they have that the debug world lacks is ported into
`worlds/debug/` (or `worlds/standard/` where it is a reusable design), because
the debug world is the proving ground that shows the engine's systems compose
and run well, and the inspiration an author draws on. This page is the
inventory and the lane list behind the
[port packet](open-items.md#port-the-experimental-worlds-missing-features).

## How to read it

Each feature names its quarantined source, then its support, found with
`puck search`, `puck references` and the owning skill's references:

- **wire:** the engine and the documents already support it; the port is `.puck`
  content in `worlds/debug/`.
- **standard:** a reusable design (a kit-row template, an ability, a deck) that
  belongs in `worlds/standard/`, with the debug world importing it.
- **gap:** engine code is needed, and the missing mechanism is named.

Sizes are small (hours), medium (a day or two) or large (several days, or more
than one sitting at the windowed World). Paths without a prefix are under
`experimental/src/Puck.World/Assets/worlds/`; `tests/` means
`experimental/tests/Puck.World.Canaries/` (one directory per canary) and `games
tests` means `experimental/tests/Puck.World.Games.Tests/`.

Two facts bind every lane. The debug document's rule sheet already takes most of
the work-unit ceiling, `RuleCapacity.MaxWorkUnitsPerTick` (`world.budget` reads
both; [card tables](open-items.md#card-tables-in-the-board-game-room)),
so a lane that adds a whole game lands it as a destination document behind a
painting, as the garden and the night field do, unless the game is priced to fit.
And the debug world runs one local seat (`bodies { localSeats: 1 }`,
`worlds/debug/avatars/moth.puck:197`), so a lane that needs a second seat waits
for lane 5.

## What the debug world already has in another form

Not ported again:

| Quarantined | Debug world |
|---|---|
| `modules/granaries.puck` (the storage court) | `worlds/debug/granaries.puck` |
| Klondike, FreeCell, the solitaire collection (`games/freecell.puck`, `games/README.md`) | `klondike.puck`, `freecell.puck`, `solitaire.puck` behind `cardtable.puck` |
| Physical chess, Go, Reversi, Alchemy (the parlor and market) | `chess.puck`, `go.puck`, `reversi.puck`, `alchemy.puck` |
| `modules/arcade.puck` (cabinets and a handheld), `tools/hgb-mirror.puck` | `devices.puck`, `desktop.puck`, `machine-debugger.puck`, `cartridges/` (the tetromino, light-gun and hgb-mirror cartridges) |
| `modules/studio.puck` (a stage, a turntable) | `studio.puck` (the avatar studio) |
| The island's picture-frame doors to dungeons | `paintings.puck`, `garden.puck`, `night.puck` |
| `drinkMe`/`eatMe`, the wren's shrink on contact | `size.puck`, `wonderland.puck` |
| The `wander` producer, the companion body | `range.puck`, `companion.puck`, `worlds/standard/companions.puck` |
| `Assets/probes/tracks/brio-head.probe-track.json` | `probe-beacon.puck`, `probes/beacon.probe-track.json` (the brio-head track is lane 14) |
| The `captures` stations and the `parity` layout | `sweep.json` and `puck sweep` |
| Sky, atmosphere and indirect-light canaries | `skies.puck`, the `gi-corner` room, `quality.puck` |
| The island's garden district: flocks, a stalker, wall crawling, rumour | the garden's pond plot (`pond.puck`, [garden.md](../../worlds/debug/garden.md)), `garden-crowd.puck` at 128 creatures, the standard's `crawlHold` |
| `moth-courtyard.puck`'s meadow and foliage | [the meadow](../../worlds/debug/meadow.md) (`meadow.puck`), the standard's `foliage.puck` |

## Lanes, in order

**The ports that force the fundamentals are the job system's forcing
workloads (the owner's ruling).** Each lands just before the step it proves,
so every optimization is measured on the load it exists for:
- the second seat (lane 5) and authored passes (lane 1) ahead of parallel
  view recording (R1);
- the quilt (lane 2) with residency step 2;
- water and fields (lane 3), the rules arcade (lane 7) and the combat yard's
  pair primitive (lane 13) as the field, rule-VM and pair-rule workloads.

The rest (lanes 6, 8, 10, 11, 12 and 14) is content, taken after the
fundamentals ([job system](engine-job-system.md#6-build-order)).

Where each port stands in the valley, which domain it forces and which seams it
composes with its neighbours is the
[composition plan](debug-world-composition.md); where that plan moves a port
from the place named below (the traversal course east, the pool to the stream's
end west of the spawn), the plan's placement governs.

Within that, order is value to the proving ground: lanes that exercise engine
systems the debug world does not touch come first. Beyond the garden's pond
plot (`interactions`, `navigation`, flocks, a `Pull` hold), a search of
`worlds/` finds no `adjacencies`, `curves` track, `region(` trigger, medium,
`field` lattice, authored HLSL package, `embeds` row or identity fact.

### 1. Authored render passes

The debug world mounts one graph instance, the engine's `source.capture`
package (`worlds/debug/desktop.puck:30`), and ships no HLSL. The pipeline
subsystem (`views.graphs`, shader packages, `world.shaders.reload`) is the one
a 30-second edit-to-running ruling most depends on.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| A state lattice row bound into an authored pass that draws one tile a cell | `experimental/worlds/rulepush/board.hlsl`, `board.graph.json`, `level.puck` (the `graph "board"` row) | wire: `views.graphs` ([documents-render](../../.claude/skills/puck-world/references/documents-render.md)), canaries `pipeline-*`, `post-pass` | An exhibit room in the gallery hall, a screen whose pane draws a live state grid; the first test of state-to-pass binding and shader hot reload in the debug world, and the drawn surface lane 7 needs | medium |
| A compare studio: a pipeline shader and a native SDF model in two slots under one paired camera | `tools/shader-compare.puck`, `tools/shader-compare.md`, `pipeline.puck` (its `pipelines/*.hlsl` sources are not in the tree; author a fresh pair) | wire | The same room; shows a procedural pass beside the creation system and exercises paired cameras | small |
| A procedural raymarched playing card (a Didone Q and a heart, pop-up and camera presets) | `experimental/worlds/genesis/card.hlsl` | wire | The room's sample shader only; the card look itself is the standard deck's, `worlds/standard/cards.puck` | small |
| Machine against machine, side by side | `tools/hgb-compare.puck` | wire | The Wonderland deck (`devices.puck`); low priority | small |

### 2. Seamless topology and carried identity

Debug crossings are all portals. Adjacency (a seam you walk across, with
authority handoff) and identity facts carried across a crossing are built
([adjacency and federation](../../.claude/skills/puck-world/references/adjacency-and-federation.md),
`src/Puck.World.Schema/WorldAdjacencies.cs`,
`src/Puck.World.Client/WorldAdjacencySceneEmitter.cs`) and `worlds/` has no
`adjacencies` row and no identity fact.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| The quilt: ground shards stitched by invisible seams, walked across with no portal | `shards/quilt-{nw,ne,se,sw}.world.json`; canaries `seamless-adjacency`, `seamless-four-corners-circuit`, `four-corners-sharded` | wire; the island once outgrew the instance ceiling ([design](../game/design.md)), so keep each shard a ground-only document and measure | A terrace past the hall's yard; the benchmark for streaming a neighbour authority | medium |
| A shard with two seams dropped, so the border closes (`unavailable: Closed`) and a body past the edge is carried back | `shards/quilt-nw-gap.puck`; canary `quilt-nw-gap-edge-carry` | wire | The terrace's far edge; the failure half of the seam | small |
| A pitched seam that hands a falling body to the world below | canaries `rim-drop`, `rim-refused`, `rim-round-trip` | wire | Beside the endless fall (`paintings.md`), which is the portal form of the same drop | small |
| Authored composition: `entry world`, `world`, `border` and a module that carries its own tests | `experimental/worlds/beacons/` (`beacon.puck`, `beacons.puck`) | wire ([Transpiler README](../../src/Puck.World.Transpiler/README.md)) | The terrace is authored this way, so the package doubles as the worked example | small |
| A win written onto the visitor's record that opens a gate on return | `experimental/worlds/rulepush/hub.puck`, `rulepush.puck`; island rules `seat1-returning-identity-wakes-at-plaza`; the studio gate that opens once awakened | wire (`WorldIdentityProjection`, `src/Puck.World.Schema/WorldIdentity.Records.cs`) | The night field awards a fact; a hall door opens on it | medium |

### 3. Water and fields

Both rows are built in composition lane C3 ([as built](debug-world-composition.md#c3-as-built)):
the pool is a medium lattice a body swims in, and the chain from glacier to aquifer is a
conserved flow on a lattice, with the pool fed by its river.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| A pool whose `field` lattice is a medium: swimming, buoyancy, depth rows read from regions | `modules/dive.puck` (`lattices field(name: "pool"...)`, `bond: Medium` holds, `dive-depth-*` rules); canaries `dive-medium`, `medium-submersion` | wire ([documents-motion](../../.claude/skills/puck-world/references/documents-motion.md)); standard: a swim hold row the Moth refines, as `abilities.puck` holds `dashAbility` | A courtyard pool; the first `field` lattice and `Medium` hold in the debug world, and the base the climate wing's wetness and puddles stand on | medium |
| Flow across basins: a source topped up each step, boundary spill, a drawn whole-field pass | canaries `flow-conservation-live`, `lattice-draw-fill` | wire | The pool's inflow; proves reactions run live | small |

### 5. Second seat and hidden information

Every private-information game needs a second reader that the first cannot read
through. Row `visibility` audiences stand in the debug world (the granaries,
`disclosure-probe.puck` and the four hidden-information games,
`worlds/games/auction.puck`, `shedding.puck`, `deduction.puck` and
`skirmish.puck`, whose test and disclosure worlds run four local seats); the
debug world itself still boots one seat and has no `embeds` row.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| Two local seats and a `split` layout | `puck.world.json` `bodies.localSeats: 2`, `views` layout `split`; canary `split-seats`; `worlds/debug/suit-tests.puck:29` already runs two | wire; cost in the entity table and the per-seat rules (`worlds/debug/avatars/moth.puck`'s `fly-1` rule, one a local seat) | `debug.puck`'s capacity; the prerequisite for the rest of this lane and for poker and Hearts in lane 12 | medium |
| Lineup: hidden busts, yes-or-no questions, a computer that asks the best splitting question | `experimental/worlds/parlor/lineup.puck`, `parlor.basis.puck`; canary `lineup`; games tests `LineupLawTests`, `LineupDealLawTests` | wire (seat-scoped `visibility`, a `deal` facet) | A board game room table | medium |
| Hidden ranks: concealed armies, per-side knowledge boards, a reveal by widening the readers | `games/hiddenranks.puck`; `experimental/tests/Puck.World.Fixtures/minimal-hiddenranks-host.puck` | wire (`visibility`, `readersFrom`, `knowledge` boards, `removeStateCell`) | A board game room table | medium |
| Word spy: a key card only the spymasters read, an aim computed in a vector space | `games/wordspy.puck`, `games/wordspy.embeddings.json` | wire (`Puck.Embeddings`, `embeds(...)`, `puck embed`; the fixture generator has no semantics, so clues are reproducible rather than apt) | The only embeddings consumer; four roles, so addon or computer seats | large |

Poker (seats, blinds, a bit-mask hand-strength judge, `games/poker.puck`,
games tests `PokerHandStrengthLawTests`) is step 2 of the
[card tables](open-items.md#card-tables-in-the-board-game-room) packet (lane 12)
and waits on this lane's seats.

### 6. Traversal course

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| A kart on a closed curve: a drive program, gates taken in order, a lap time | `modules/kart.puck` (`kartTrack` curve, `kartDrive`, `kart-gate-*`); canary `kart-lap` | wire; a vehicle kit swapped on the seat, as `noclip.puck` swaps one | The north ground; the first `curves` track and `region(` trigger | medium |
| A ledge course: steps, a ledge-grab hold, a trophy region, a fall counter | `modules/jump.puck` (`vaulter`, `jumpTrophyReached`, `jumpFallsCounter`); canary `jump-trophy` | wire | The same ground; standard: the ledge-grab hold row | small |
| Planetoids: radial gravity areas a body walks round | `puck.world.json` `gravity.areas`, placements `planetoid-*` | wire (`src/Puck.World.Schema/WorldGravity.cs`) | The same ground, above the course | small |
| A grapple: attach, detach and reel channels to posts | the `walker` kit's `tether` facet, `tetherPost1` | wire; standard: a grapple ability | The same ground | small |

### 7. Rules arcade: puzzles on a drawn board

The tetromino cartridge in `worlds/debug/cartridges/` is an emulated machine;
this is the same game as state rules, which exercises different systems. Each
needs a drawn surface, which is lane 1's pass or a placement grid; the island
left the well and the snake field undrawn and read through the console.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| Tetromino as rules: a `piece` workflow, a seven-piece bag draw site, `fullRow` pattern clears, lock delay by `schedule`, saturating score, wall kicks | `games/tetromino.puck`; games tests `TetrominoTickLawTests`; canary `tetromino-clear` | wire | An arcade room; priced at about 109,000 work units a tick, against `RuleCapacity.MaxWorkUnitsPerTick` | medium |
| Snake: a `ring`-domain body, a tick-indexed beat from two `cycle` cells, an `evicts` meal table | `games/snake.puck`, `experimental/tests/Puck.World.Fixtures/minimal-snake-host.puck` | wire | The same room | small |
| Rulepush: sentences of word tiles rewrite what objects do; one `stabilize` turn, a 32-turn undo ring, push chains | `experimental/worlds/rulepush/` (`rules.puck`, `room.puck`, `level.puck`); canaries `rulepush-undo`, `rulepush-board`, `push-ray`; games tests `RulePushTurnLawTests` | wire; needs lane 1's pass for the board | The same room; the `stabilize` and undo proof | large |

### 8. Physical judges at the table

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| Billiards: a held `attack` charges a cue, the release strikes the ball with `applyRigidImpulse` | `games/billiards.puck` (`cue-charge`, `cue-release`), `experimental/tests/Puck.World.Fixtures/minimal-billiards-host.puck` | wire (`applyRigidImpulse`, `$channel:1:attack`) | A table in the board game room; the first charged gesture on a table | medium |
| Bowling: a ball and pins judged by settling | `games/bowling.puck` | wire | A lane beside it | small |
| Dominoes: a run of tiles toppling | `games/dominoes.puck` | wire | The same room | small |
| Paddleball: a physical court judged by three `interactions` latches (Distance and Region, Edge and Level), a spin follower | `games/paddleball.puck`; games tests `PaddleballContactLawTests` | wire; first user of `interactions` in the debug world | The same room | medium |

### 10. Packages as worked examples

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| A rules module with no setting: twelve seasonal elements, an effectiveness chart, a real-time fight, orders by console, stamped as `use rules(...)` | `experimental/worlds/familiars/` (`rules.puck`, `seasons.puck`, `familiars.puck`) | wire | A destination behind a painting; the parametrised-module showcase, and the example an author copies | large |

### 11. Board tables II

New topologies and one new search node on the `board(` primitive chess, Go and
Reversi already use.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| Chinese checkers: 121 holes, jump chains, a bounded one-ply opponent | `experimental/worlds/parlor/chinese-checkers.puck` | wire | A board game room table | medium |
| Hex lines on a hex topology | `games/hexlines.puck`, `experimental/tests/Puck.World.Fixtures/minimal-hexlines-host.puck`; games tests `HexLinesModuleImportLawTests` | wire | The same room | small |
| Mancala: sowing and captures | `games/mancala.puck`; games tests `MancalaModuleLawTests` | wire | The same room | small |
| Tic-tac-toe, a two-table twin | `games/tictactoe.puck`, `experimental/tests/Puck.World.Fixtures/twin-tictactoe-host.puck` | wire | The smallest worked table, an author's first | small |
| Backgammon: a dice row, a `chance` node in the search; hitting and bearing off are authored here | `games/backgammon.puck`; games tests `BackgammonLawTests` | wire (`src/Puck.State.Search/ArenaSearch.Chance.cs`) | The same room; the first `chance` node | medium |

### 12. Card tables continued

The [card tables packet](open-items.md#card-tables-in-the-board-game-room) owns
the order: Spider (`games/spider.puck`, 104 cards, medium), then poker (lane 5's
seats), then Hearts (`experimental/worlds/parlor/hearts.puck`, four seats with AI, large). All wire
over `worlds/standard/cards.puck`.

### 13. Combat yard

`modules/arena.puck` (nearest and target-of-target targeting, auto-attack,
fire and frost, burn and thaw, death and respawn, a fire pit and an ice pool)
and `games/arena.puck` (frags, health pickups, respawn, a contended momentum
cell) are the prior art the
[debug world content packet](open-items.md#debug-world-content) names. Support:
wire, except one **gap**: the island dropped the body-pair `heat-ignites` and
`cold-spreads` interactions because a pair rule costs capacity squared against
the work budget, so spread between bodies needs a region-scoped pair primitive
(the packet's `FixedSpatialNeighborhood` item). Large; it waits on the job-system
memo.

### 14. Small items

- `Assets/probes/tracks/brio-head.probe-track.json`: a recorded four-channel head
  track, the input for the camera-as-controller stretch goal (wire).
- The studio gate and the mirror wall (`modules/studio.puck`): folds into lane 2's
  identity gate and the avatar studio (small).

## Not ported here

The quarantined gates, canary runner, parity and counters fixtures
(`experimental/tests/Puck.Parity`, `experimental/tests/Puck.Counters`, `experimental/tests/Puck.Determinism`, the CLI
test projects) test tools and engine seams, not world features; the
[quarantined gates](open-items.md#quarantined-gates) packet owns them. The
canaries listed above are prior art for each lane's own `test` blocks, which
run in the debug package with `puck test`. The `experimental/Puck.Post` and
`experimental/Puck.Bench` projects hold no world.
