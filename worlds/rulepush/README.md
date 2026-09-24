# Puck Rulepush

A rule-pushing puzzle, written entirely in Puck DSL: an overworld and three
levels bordering it. The rules are written once, in [rules.puck](rules.puck),
and each level supplies only its map. The package is this folder and its
[manifest](manifest.json).

The player is an imp. Word tiles stand on the board beside the objects, and
pushing them into a new line rewrites what the objects do.

- [rules.puck](rules.puck) is the rules module. Words stand on the board as
  tokens. Three of them in a row or a column, noun, `IS` and a property or
  noun, form a sentence. The module derives a property table and a
  noun-rewrite table from the sentences standing at the start of each turn,
  then moves, rewrites, sinks, defeats and wins from those tables. The
  properties are YOU, PUSH, STOP, SINK, DEFEAT and WIN. Words are always
  pushable. The nouns are IMP, HEDGE, BOULDER, FLOWER, MIRE and THORN. The
  whole turn is one `stabilize` group with a 32-turn undo ring.
- [room.puck](room.puck) is the physical room every world stands in: a floor,
  the arrival spot, the walking controls and a camera.
- [level.puck](level.puck) turns a map into a level. A win writes an identity
  fact named for the level onto the visitor's own record.
- [hub.puck](hub.puck) is the overworld. Its west gate opens once the visitor's
  record shows that Hedges and Rewrite are cleared.
- [rulepush.puck](rulepush.puck) declares the four worlds, their borders, the
  three maps and the package's tests.

## Levels

| World | Tests |
|---|---|
| `hedges` | HEDGE IS STOP, read down a column, pens the flower in a corner. A push chain into the hedge either moves whole or not at all. Pushing STOP out of the column opens the pen. |
| `rewrite` | FLOWER IS WIN, but the board has no flower. Pushing a loose FLOWER word under BOULDER IS turns every boulder into a flower, and the imp wins by walking onto one. |
| `sink` | MIRE IS SINK, and a mire crosses the whole board. A boulder pushed into the mire sinks with it and opens a path. If the imp walks into the mire, no YOU remains until an undo. THORN IS DEFEAT removes the imp and leaves the thorn. |

## Controls

Walk with WASD or the left stick. In a level, the arrow keys or the d-pad move
the imp one cell per press, and Z or the west face button undoes one turn.
Walking off the overworld's east, north or west edge crosses into a level.

## Run and verify

`rulepush.puck` declares four worlds, `hub`, `hedges`, `rewrite` and `sink`, and
marks the overworld `entry world hub`. The game boots the source directly: it
stages all four documents under its state directory and starts in the entry,
so every border crossing finds its neighbour. The canaries boot it the same way.

```text
puck compile rulepush.puck --validate -o <directory>
Puck.World --world rulepush.puck
```

`puck compile` writes one `<world>.world.json` per declaration into the
directory it is given; write them outside this folder, which holds sources
only.

```text
puck format --check .
puck lint rulepush.puck --strict
puck test . --reproduce
```

`puck test` runs the composition test and the level tests. The composition
test starts on the overworld, crosses into Hedges and plays a turn there. The
level tests drive winning solutions, failed pushes, sinking, defeat and undo
with the same presses a player makes.

Two canaries boot the real `Puck.World` executable headless:

- [`rulepush-undo`](../../tests/Puck.World.Canaries/rulepush-undo/canary.json)
  plays eight turns, then eight more and undoes them. It checks that the world
  state hash matches the hash from eight turns earlier, and that 32 undos
  return to the start.
- [`rulepush-overworld`](../../tests/Puck.World.Canaries/rulepush-overworld/canary.json)
  walks from the overworld into Hedges and Rewrite, clears both, returns, and
  finds the west gate open.

[RulePushTurnLawTests](../../tests/Puck.World.Tests/RulePushTurnLawTests.cs)
states the cost of a turn: one working pass and one confirming pass, one undo
slot, and bounded allocation per turn and per idle tick. It also boots a 16x16
test level and holds its per-tick rule work under the ceiling.

## Limitations

The board is simulated but not drawn. A level shows its room and floor, and
the board's state is visible only through the console and the tests. When the
board gets a look, the art, palette and tile shapes must be original to Puck:
the package keeps a known puzzle mechanic, never the look of a commercial game
that uses it.
