# Debug world Alchemy table

The debug world stands an Alchemy table in the gallery hall's board game room,
the east end of level 2 ([`shapes.md`](shapes.md#the-board-game-room)), at the
north-east spot east of the Go table ([`go.md`](go.md)) and north of the Reversi
table ([`reversi.md`](reversi.md)). The workbench is an 8×5 grid of 0.14 m cells,
its centre at (65, 9.905, 30.6) on a table 3.5 m by 1.7 m whose top is 0.8 m
over the floor. You start with four elements—Air, Earth, Fire and Water—and
combine two at a time in the cauldron to discover the remaining 27, completing
the book at 31. [`alchemy.puck`](alchemy.puck) holds the table and rules,
[`elements.puck`](elements.puck) holds the shared elemental colours and
two-letter glyph codes, and `debug.puck` imports `alchemy` whole
(`import "alchemy"`).

The game has two layers. The rules are the single-player discovery game over the
32×32 `pairs` lattice (`recipe`) and the `known` bitset: each element is its
ordinal in the `Element` enum (`Nothing` is 0, `Air` is 1, and `House` is 31),
and each unordered recipe pair `a`, `b` is stored once at cell
`minimum(a, b) * 32 + maximum(a, b)`. Bit `e` of `known` is 1 when element `e`
has been discovered (`known` starts at 30 for bits 1..4). When both hands
(`left` and `right`) hold elements:

- If both are known and their pair is a recipe (`mix`), the cauldron makes the
  result (`made`). On first sight (`fresh == 1`), its bit is set in `known`,
  `newest` records it, `discovered` advances, and `alchemyBoard[result]` becomes
  1; once `discovered == 31`, `complete` becomes 1.
- If both are known and their pair has no recipe, `made` is `Nothing` and
  `fizzles` advances.
- If either hand holds an undiscovered element (`refuse-unknown`), the attempt is
  refused (`refused` advances and `made` is `Nothing`).
- Whenever `known` changes (`count-hints`), `hints` counts how many recipes have
  both ingredients known and an undiscovered result. The `final` row marks the
  elements no recipe uses as an ingredient.

Over the rules lies the physical table (`alchemyGrid`, 8 columns by 5 rows).
Cells 1 to 31 hold the 31 element tokens (`alchemyToken1` to `alchemyToken31`)
on inlaid shelf tiles tinted with each element's colour from
[`elements.puck`](elements.puck), cell 0 is a brass reset tile at the south-west
corner, and row 4 holds the cauldron (cells 35 and 36) between the left hand
well (cell 34) and right hand well (cell 37). All 31 tokens share one rigid
two-faced medallion geometry (`alchemyToken`): bright alchemical brass and ivory
on the discovered (`+Y`) face and dark sealed iron on the undiscovered (`-Y`)
face. Behind the cauldron on the north (`+Z`) edge of the table stands a wooden
lectern holding screen 7, a native text decal screen (`WorldScreenSource.Text`)
that displays `discovered`, `hints`, `left`, `right`, `made`, `newest` and
`complete` live from world state. Whenever Alchemy's 31 tokens have rested for 0.3 s
(`alchemySettleTicks`), the rules read each token's cell (`alchemyCell`) and up
face (`alchemyFace`): a token resting in the left or right hand well loads
`left` or `right`, newly discovered tokens turn face up on their shelf cells,
and displaced tokens return to their shelf cells. A token overturned at home is
also corrected to its book face, whether discovered or still sealed.
Motion at another table does not delay this reading. Carrying an Alchemy token
keeps Alchemy unsettled until it is released and rests.

## Playing at the table

Walk within 2.5 m of the workbench and press the context button (E, or the pad's
RB): the Moth sits down and the camera frames the workbench and lectern from her
side. `debug.go alchemy` stands her at the south (`-Z`) edge of the table,
facing the cauldron and lectern and close enough to sit. While she sits, the
controls switch to the shared tabletop board page (`chess.puck`).

| Device | Move the cursor | Pick an element into a hand | Clear a partial hand | Stand up |
|---|---|---|---|---|
| Mouse | Point at a cell | Click | Right-click (or click cell 0) | E |
| Keyboard | W A S D or the arrows | Space or Enter | Escape or Backspace | E |
| Pad | Left stick or d-pad | South (A) | East (B) | RB |

A blue outline marks the cursor's cell (`alchemyCursor`); it starts on cell 1
(`Air`). Picking an element (`1..31`) when `left` is empty loads `left`; picking
a second element loads `right`, which immediately mixes the pair in the cauldron
and empties both hands for the next mix. Picking the same element twice mixes it
with itself (`Water` + `Water` → `Sea`).

## Cells, tokens and the console

A cell's ordinal is `8 × row + column`, the column along `+X` and the row along
`+Z`: cell 0 is the `-X, -Z` reset tile, cells 1 to 31 are the 31 elements in
enum order (`Air` = 1 .. `House` = 31), cell 34 is the left hand well, and cell
37 is the right hand well. Cell `(column, row)` is centred at
`x = 64.51 + 0.14 × column`, `z = 30.32 + 0.14 × row` (so cell 34 is
`(64.79, 9.905, 30.88)` and cell 37 is `(65.21, 9.905, 30.88)`). Element token
`e` (`alchemyToken{e}`, `1..31`) is body `503 - e`, below `alchemyCursor` (body
503), and seat 1 drives all 31 tokens, so posing a token over a hand well drops
it in: `body.pose 64.79 9.97 30.88 0 0 0 500` drops `Fire` (`alchemyToken3`)
into the left hand well.

| To | Console line |
|---|---|
| Go to the Alchemy table | `debug.go alchemy` |
| Mix two elements by name | `alchemy.mix <first> <second>` |
| Set the left or right hand directly | `world.state.cell.set left $value <Element>`, `world.state.cell.set right $value <Element>` |
| Reset the book and table to the four starters | `alchemy.reset` |
| Read the last mix, newest discovery and fresh flag | `world.state made`, `world.state newest`, `world.state fresh` |
| Read discoveries, known bitset, hints and completion | `world.state discovered`, `world.state known`, `world.state hints`, `world.state complete` |
| Read mix, fizzle, refusal and illegal tallies | `world.state mixes`, `world.state fizzles`, `world.state refused`, `world.state alchemyIllegal` |
| Read the shelf board and token faces | `world.state alchemyBoard`, `world.state alchemyFace`, `world.state alchemyCell` |
| Frame the table and lectern from the south side | `view.override camera look 65 11.2 28.6 65 9.95 30.7`, then `view.override camera auto` |

## Tests

[`alchemy-tests.puck`](alchemy-tests.puck) holds the table's scenarios, each
booted in the whole debug world: the 31 tokens settling on their shelf cells
with the four starters face up (settled hold, actual board cells and upright
reads), correcting both discovered and sealed tokens overturned on their own
shelves, `Fire` + `Water` making `Steam` and physically turning its token face
up on the table, hand-order independence, mixing an element with
itself (`Water` + `Water` → `Sea`), a non-recipe pair fizzling, an undiscovered
ingredient being refused, the starting hint count, an alternate recipe for an
already-known element leaving `discovered` unchanged, seated cursor play and
physical token drops into the hand wells, settling and resolving a physical mix
while a Go stone is still falling, and completing all 31 elements in 27 mixes.
Run them with `puck test worlds/debug/alchemy-tests.puck --reproduce --jobs 1`.
