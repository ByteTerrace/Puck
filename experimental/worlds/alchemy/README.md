# Puck Alchemy

A single-player discovery game, written in one Puck DSL source,
[alchemy.puck](alchemy.puck). You start with four elements and combine two at a
time to discover the rest. The book is complete when you have discovered all 31
elements. The package is this folder and its [manifest](manifest.json), which
lists the one world and every file to distribute.

The world holds rules and state only. It has no scene, controls or HUD yet, so
you play it by writing console commands.

## How the game plays

The cauldron has two hands, `left` and `right`. Put a known element in each:

- If the pair is a recipe, the cauldron makes its result. The first time it
  makes an element, you have discovered it.
- If the pair is not a recipe, the mix fizzles.
- If either hand holds an element you have not discovered, the mix is refused.

Either way, both hands empty and the cauldron is ready for the next pair. The
order of the hands doesn't matter, and an element can be mixed with itself:
Water and Water make Sea.

You start with Air, Earth, Fire and Water. Some elements have more than one
recipe, so a stuck player has more than one way forward. Seven elements are
*final*: no recipe uses them as an ingredient, so discovering one opens nothing
new. The recipe list at the top of the source is the whole book.

## How the rules work

Each element is a number, its position in the `Element` enum: Nothing is 0,
Air is 1, and House, the last, is 31.

**The recipe book is a grid.** A recipe is an unordered pair, so the book stores
each pair once, smaller element first, in one cell of a 32 × 32 grid. The cell
for a pair `a`, `b` is `minimum(a, b) * 32 + maximum(a, b)`. Fire (3) and Water
(4) share cell 100, which holds Steam (5). A cell no recipe fills reads 0,
Nothing, so looking up any pair is a single read. The compiler fills the grid
from the `recipes` list, so the running game never sees the list itself.

**What you know is one number.** Bit `e` of `known` is set when element `e` is
discovered. The four starters are bits 1 to 4, so `known` starts at 30. Testing
whether you know an element is a shift and a mask, and `discovered` is the
count of set bits.

**Three rules run the game:**

| Rule | Fires when | Does |
|---|---|---|
| `mix` | Both hands hold known elements | Looks up the pair, discovers the result if it's new, counts a fizzle if it's Nothing, and empties both hands. |
| `refuse-unknown` | Both hands are full and either element is unknown | Counts the refusal and empties both hands. |
| `count-hints` | `known` has changed | Counts the recipes you could make now that would discover something new. |

The player may write `left` and `right` and nothing else. Every other row
belongs to the rules:

| Row | Holds |
|---|---|
| `made` | What the last mix produced, or Nothing. |
| `newest` | The most recent discovery. |
| `fresh` | 1 when the last mix discovered what it made. |
| `discovered` | How many elements you know. |
| `hints` | How many recipes are open that would discover something new. |
| `mixes`, `fizzles`, `refused` | Running tallies. |
| `complete` | 1 once every element is discovered. |
| `final` | 1 for each element no recipe uses as an ingredient. |

## Usage

Run these from the repository root. Boot the world headless and type commands
on its console. Each hand takes an element by name:

```text
Puck.World --world worlds/alchemy/alchemy.puck --headless
world.state.cell.set left $value Fire
world.state.cell.set right $value Water
world.wait 1
world.state made
```

Fire and Water make Steam, so the console prints:

```text
[world.state.row 'made' kind=Int value=Steam domain=slot]
  [world.state.cell 'made'.'$value' value=Steam]
```

`discovered` then reads 5 and `hints` reads 6.

## Verification

```text
puck format --check worlds/alchemy
puck lint worlds/alchemy/alchemy.puck --strict
puck test worlds/alchemy --reproduce
```

`puck test` boots the real `Puck.World` executable once per test block, twice
with `--reproduce`, and checks that both runs export identical state. The tests
cover a first discovery, hand order, mixing an element with itself, a fizzle, a
refusal, the starting hint count, a second recipe for an element you already
know, and one route through the whole book in 27 mixes.

## Limitations

- The world has no scene, controls or HUD.

## Documentation

- [The `.puck` language](../../docs/reference/dsl.md)
- [Rows, cells, and values](../../docs/reference/state/data-model.md)
- [Testing a world](../../docs/authoring/testing-a-world.md)
