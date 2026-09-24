# Testing a world

Behaviour is written in the world's own language, beside the rules it is about.
A `test` block names a claim, sets the world up, drives it for a few ticks, and
says what must be true at the end. `puck test` runs it.

```puck
test "all marbles settle into the star" {
  when {
    ticks 500
  }
  expect {
    missingCount == 0
    boardCollisions == 0
    movedCount == 0
    verdict == 1
    turn == 0
    winner == -1
  }
}
```

Run it:

```bash
puck test worlds/parlor/chinese-checkers.puck
```

Independent test worlds run concurrently and their reports remain in authored
order. Use `--jobs 1` when debugging a single shared resource, or `--jobs <n>`
to choose the worker limit explicitly. The headless host advances on the
world's fixed tick grid without waiting for wall clock.

```text
test: chinese-checkers~all-marbles-settle-into-the-star <- .../chinese-checkers.puck test "all marbles settle into the star"
  expect$1: pass gate="missingCount == 0" saw=[missingCount=0] firedTick=501
  ...
  6/6 verdict(s) passed at export tick 501.
PASS: every verdict in 2 test world(s) passed.
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
  same bytes, same hash, same rule cost. That holds for the tests a module it
  uses brings with it, too.

## Testing a module

A module has no world of its own, so a test of one says which module, with which
arguments:

```puck
test "a beacon at threshold three lights on the third charge" with beacon(centre: [0m, 0m, 0m], threshold: 3) {
  when {
    seat1: world.state.cell.set charge $value 3
    ticks 1
  }
  expect {
    lit == 1
  }
}
```

The generated world is that module expanded with those arguments and nothing
else — not the document the test is written in. A module's parameters are
therefore its test seam: the way to state what a value means is to choose it.

A `test` written **inside** a module body belongs to the module, and rides along
to every place the module is used:

```puck
module beacon(centre: Point, threshold) {
  // rows, rules, ground, spawn …

  test "a beacon boots dark" {
    when {
      ticks 2
    }
    expect {
      lit == 0
    }
  }
}
```

Every `use beacon(…)` and every `world north = beacon(…)` runs that test once,
against the arguments that use supplies, and reports it under the instance's
name (`north a beacon boots dark`). Two uses that read identically run it once;
two that differ run it twice. Because the arguments differ between instances, a
module's own tests have to be claims that hold for every argument the module
admits — a claim about one particular threshold is written beside the instance
that chooses it, with `with`.

`puck test` is what runs them. `puck compile` lowers and does not boot, so a
module whose tests fail still compiles: run the source, or the directory holding
it, through `puck test` to get a verdict. A module source on its own — one
declaring no `schema` — is skipped by a directory sweep unless it writes a `with`
test of its own, since nothing there says what arguments to stand it up with.

## Testing a composition

A source that emits several worlds — `world name = module(arguments)` joined by
a `border` or a `door` — has no default world, so a test written at its root
says which world each line is about:

```puck
test "a body that walks across the border is seen by the far world" {
  when {
    north {
      seat1: body.pose 0 1 -2 0 0 0 0
      seat1: body.fly -1 0 0 0 0 0 2 0
    }
    ticks 90
  }
  expect {
    north {
      visited == 1
    }
    south {
      visited == 1
    }
  }
}
```

The block lowers to one generated world per world of the composition. The
world the source declares `entry world` is the one the run boots with, as a boot
of the source starts there, and a composition with such a test and no entry is
refused (PUCK104); the entry's `schedule` arms the rest beside it, they all advance in the same host step, and each world's
verdicts are read from its own export and reported under its own name
(`south/…`).

- **`ticks` stands outside every world block.** One grid carries the whole
  composed run, so a `ticks` step inside a block is refused by name.
- **A line that names no world is refused**, as is one naming a world the source
  does not declare. There is nothing to default to.
- **A step reaching a world other than the booted one needs a verb whose grammar
  carries a world token** — `body.fly`, `body.pose`, `body.stop`, `player.join`,
  `player.leave`. Every other admitted verb reaches the booted world whatever a
  step asks for, so addressing another world with one is refused when the source
  compiles, and the refusal lists the five that work. Driving a far world's own
  state means driving what crosses into it.
- **A far world's expectation is decided one tick earlier than the booted
  world's.** A world armed at boot counts from its own zero, so the exports of
  one host step carry ticks one apart; the lowering derives that, and the
  expectation is written as though both worlds were read at the same moment.

## The three blocks

A test's body is `given`, `when` and `expect`, in that order. `given` and `when`
are optional; `expect` is not, because a test with no expectation answers
nothing. A `with module(arguments)` clause sits between the name and the brace
and says what the body is about; without one, the body is about the world it
stands in.

### `given` — where the world starts

One cell per line, written the way an effect writes one:

```puck
given {
  hp = 4
  aiSide = 0
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

A step is submitted when its tick completes and takes effect in the next one,
on every run and on any machine: a scheduled step does not wait on the wall
clock the way a line typed at a console does.

A step's command must open with a verb a schedule admits — the state, transform,
body and session verbs a seat can submit at an exact tick, and the reads that
answer through the seat's own visibility (`world.state`, `world.row`,
`world.observe`, `world.state.observe`, `world.state.similar`, `world.match`,
`world.tabletop`, `world.hud.template`). Every verb that touches the process, the
clock, the file system or the grant table is refused when the source compiles,
and the refusal lists what is admitted.

A step says what it expects the world to answer. Written plainly, the step must
be taken; written `seat<n> refused: <command line>`, the world must refuse it,
and with a quoted text, `seat<n> refused "text": <command line>`, the recorded
refusal must contain that text. Either way, a different answer fails the test:

```puck
when {
  seat1: world.state secret $value
  seat2 refused "is not disclosed to seat2": world.state secret $value
  ticks 1
}
```

The two lines are a pair: the seat the row's visibility admits reads the cell,
and the other seat is refused it.

### `expect` — what the test claims

One gate expression per line, in the grammar a rule's `when` already uses:

```puck
expect {
  home[0] == 10
  winner == 0
}
```

Each line becomes its own verdict, so a failure names the line that failed
rather than the whole test. Each is decided **once**, on the last tick the grid
reached — so an expectation that is false early and true at the end passes, and
a test never reports a failure the world had not finished producing yet.

The report names the gate as written and the values it read:

```text
  expect$1: fail gate="home[0] == 10" saw=[home$0=9] firedTick=8
```

Each expectation line lowers to a verdict row named `expect$<line>` beside a rule
of the same name, and a gate's values are recorded under the name of the row it
read (`missingCount`), or the row and the key joined by `$` (`home$0`). The `$`
marks a [generated name](../reference/dsl.md#generated-names), so none of them can
collide with a row the world declares. A value read from a `Fixed` or `Bool` row
is recorded in a witness row of that kind, `expect$<line>$fixed` or
`expect$<line>$bool`, and listed the same way; a value read from a `Text` row is
not listed, because no verdict row holds text. The generated world is named
`<source stem>~<test slug>`.

## What a test cannot say yet

- **A gate compares a `Bool` row to `0` or `1`.** The gate grammar reads `true`
  and `false` as row names, in a rule's `when` as much as here, so
  `open == false` is refused and `open == 0` is how it is said.
- **A far world's own state, driven directly.** A step reaches a world other
  than the booted one only through the verbs whose grammar carries a world
  token, so a claim about a far world is written as a claim about what crossed
  into it.
- **A module-local `let` inside a test written with a module.** The subject is
  expanded on its own, so the test's `given` and `expect` reach the module's own
  names and the arguments it was given, not the constants of the source the test
  sits in.
- **A body, a pose, a position, a camera.** `expect` reads state rows, which is
  what a rule reads. A claim about a body's position needs an operand a rule can
  read it through.
- **A comparison of two expressions** folds no values into its report: a verdict
  row holds cells, so only the rows a gate compares are recorded beside it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | every verdict passed; with `--reproduce`, both runs also exported identical bytes |
| 1 | a verdict failed, or a step's recorded outcome was not the one it declared |
| 2 | usage: a source that does not compile, a source with no test, a `--reproduce` mismatch, a build or boot refusal |

Ordinary authoring runs each isolated test world once. `--reproduce` is the
determinism qualification tier: it runs every world twice into sibling
directories and refuses a world whose state export or schedule manifest differs
byte for byte.

---

[Authoring](README.md) · [World vocabulary](../reference/world-vocabulary.md) ·
[CLI reference](../reference/cli.md)
