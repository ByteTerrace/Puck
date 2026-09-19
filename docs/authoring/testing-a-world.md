# Testing a world

A world's behaviour is written in the world's own language, beside the rules it
is about. A `test` block names a claim, sets the world up, drives it for a few
ticks, and says what must be true at the end. `puck test` runs it.

```puck
test "one piece home is not a win" {
  when {
    seat1: world.state.cell.set pieceCell s0p1 14
    ticks 1
  }
  expect {
    home0 == 1
    winner == -1
  }
}
```

Run it:

```bash
puck test src/Puck.World/Assets/worlds/games/chinese-checkers.puck
```

```text
test: chinese-checkers--one-piece-home-is-not-a-win <- .../chinese-checkers.puck test "one piece home is not a win"
  one-piece-home-is-not-a-win-1: pass gate="home0 == 1" saw=[home0=1] firedTick=7
  one-piece-home-is-not-a-win-2: pass gate="winner == -1" saw=[winner=-1] firedTick=7
  2/2 verdict(s) passed at export tick 7.
PASS: every verdict in 2 test world(s) passed, and each world's two runs exported identical bytes.
```

## A test is a world

Nothing about a test is a test framework. Each `test` block becomes a whole
generated world: the world it is written in, unchanged, plus the cells the test
set, plus the commands it scheduled, plus one rule per expectation. `puck test` boots that
world through the real `Puck.World` executable, headless, and reads the answers
out of the world's own exported state.

That has three consequences worth knowing up front.

- **A failing test is a world you can open.** `puck test --keep <dir>` writes
  every generated world into `<dir>/generated`, so a failure is a
  `*.world.json` you can boot, inspect with `world.state`, and step by hand.
- **A test can only ask for things the engine can do.** A `given` line writes a
  cell; a `when` step submits a command a seat could type. Anything else is
  refused when the source compiles, by name, on the line that wrote it.
- **The world you ship is unchanged.** Compiling a world without `puck test`
  produces exactly the document it produced before the tests were written —
  same bytes, same hash, same rule cost.

## The three blocks

A test's body is `given`, `when` and `expect`, in that order. `given` and `when`
are optional; `expect` is not, because a test with no expectation answers
nothing.

### `given` — where the world starts

One cell per line, written the way an effect writes one:

```puck
given {
  hp = 4
  pieceCell[s0p1] = 14
}
```

The value is a literal of the row's own kind: a whole number for an `Int` row,
a decimal for a `Fixed` one, `true` or `false` for a `Bool` one, a quoted string
for a `Text` one. The row must be one the world declares, and the value must be
one the row's kind and envelope admit — the same refusals an authored cell gets.

### `when` — the tick grid

Two kinds of step, in any order:

```puck
when {
  ticks 2
  seat1: world.state.cell.set armour $value 2
  ticks 3
}
```

`ticks n` carries the tick cursor forward. A `seat<n>:` step submits one command
line at the tick the cursor has reached. The cursor opens at tick 1 — the first
tick a command can be submitted at — so the example above submits at tick 3 and
the world runs to tick 6.

A step's command is *submitted* at its tick: the host's command pump drains it,
the simulation lane applies it, and the run records the answer — each on a later
tick. The grid therefore always runs a fixed margin past the last step whatever
you wrote after it. Write the `ticks` you mean; the margin is a floor beneath
them, not a replacement for them.

A step acts as a **seat**, never as the console: the console is trusted at every
gate, so a step under it would prove nothing about who is allowed to do what.
The authority a step needs is the world's own, authored in its `grants`:

```puck
grants [
  {
    capability: "Mutate"
    principal: "seat1"
    subject: "section:state"
  }
  {
    capability: "Edit"
    principal: "seat1"
    subject: "state:pieceCell"
  }
]
```

A step whose seat lacks a grant is not granted one: the run records the refusal
and the test fails on it.

A step's command must open with a verb a schedule admits — the state, transform,
body and session verbs a seat can submit at an exact tick. Every verb that
touches the process, the clock, the file system or the grant table is refused
when the source compiles, and the refusal lists what is admitted.

### `expect` — what the test claims

One gate expression per line, in the grammar a rule's `when` already uses:

```puck
expect {
  home0 == 2
  winner == 0
}
```

Each line becomes its own verdict, so a failure names the line that failed
rather than the whole test. Each is decided **once**, on the last tick the grid
reached — so an expectation that is false early and true at the end passes, and
a test never reports a failure the world had not finished producing yet.

The report names the gate as written and the values it read:

```text
  the-win-condition-fires-when-the-last-piece-lands-1: fail gate="home0 == 2" saw=[home0=1] firedTick=8
```

The values listed are the ones read from `Int` rows. A gate may read a row of
any kind, and the report still names the gate as written; a value read from a
`Fixed`, `Bool` or `Text` row is not listed, because the row a verdict is
recorded in holds whole numbers.

## What a test cannot say yet

- **A gate compares a `Bool` row to `0` or `1`.** The gate grammar reads `true`
  and `false` as row names, in a rule's `when` as much as here, so
  `open == false` is refused and `open == 0` is how it is said.
- **`test "name" with module(arguments)`** is refused by name. A test of a
  module arrives with modules, and the module construct and its `use` do not
  exist yet. A test today states a whole world's behaviour, at that world's
  root.
- **A body, a pose, a position, a camera.** `expect` reads state rows, which is
  what a rule reads. A claim about a body's position needs an operand a rule can
  read it through.
- **A comparison of two expressions** folds no values into its report: a verdict
  row holds cells, so only the rows a gate compares are recorded beside it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | every verdict passed, and each world's two runs exported identical bytes |
| 1 | a verdict failed, or a step's recorded outcome was not the one it declared |
| 2 | usage: a source that does not compile, a source with no test, a world that did not reproduce, a build or boot refusal |

Every world runs **twice**, into sibling directories, and a world whose two runs
export different bytes is refused rather than reported: a verdict read off a
world that does not reproduce says nothing.

---

[Authoring](README.md) · [World vocabulary](../reference/world-vocabulary.md) ·
[CLI reference](../reference/cli.md)
