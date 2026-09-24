# Test worlds

Authored worlds whose own documents carry the whole test: a `schedule` section
says what to submit and when, and `verdict`-marked `state` rows say what the
rules must conclude. `puck test` boots each one through the real `Puck.World`
executable, headless, and reads the verdicts back out of the state export the
world writes at its declared export tick. The host runs that fixed tick grid
without wall-clock pacing; `--reproduce` adds a second, byte-compared run.

Beside them, `sources/` holds the `.puck` half: a world whose behaviour is
written as a `test` block rather than as a hand-authored schedule and verdict
row, which is how a world's own behaviour is normally stated. `puck test`
compiles the source and boots the world its test generated. `composition/` is
the same thing over a set: two worlds from one source, joined by a `border`, with
the tests at the source's root driving one of them and reading the other.

```sh
puck test tests/Puck.World.Verdicts/phase-advance.world.json --keep out
puck test tests/Puck.World.Verdicts/sources/seat-writes-a-cell.puck
```

Every `.world.json` world here is a basis delta over the local `phase-fixture.world.json`. The
fixture contains only the ordered zones and phase rows needed to exercise
command scheduling, so this CLI suite has no dependency on authored game
content.

Each scheduled row acts as `seat1`, never as `console`: the console principal is
trusted at every gate, so a step acting as it would prove nothing about
authority. What `seat1` may do is the two authored grants — `Mutate` over
`section:state` and `Edit` over `all` — and removing either turns both worlds'
scheduled commands into recorded refusals.

| World | Proves |
|---|---|
| `phase-advance.world.json` | A scheduled command submits a guarded transform (`world.state.act`) that advances `passPhase`'s generation. Its second verdict is deliberately unproven, so the failure path is exercised on every run: nothing is scheduled against `idlePhase`, so no firing ever writes its status and the verdict reads "never evaluated" against that gate while still reporting the generation it saw. |
| `phase-advance-stopped.world.json` | `phase-advance.world.json` with `rateHz` 0. The simulation never steps, so a piped `quit` ends the run at tick 0 and every scheduled row records itself as unreached, independent of machine load. `puck test` cannot judge it, because it never reaches its export tick; the CLI suite's truncation law boots it directly. |
| `no-test-block.puck` | A world source that authors no `test` block. `puck test` refuses it as a usage error (exit 2) rather than reporting an empty pass; the CLI suite's law for that refusal runs it. |
| `sources/seat-writes-a-cell.puck` | A `test` block generating its own world: a `given` cell, a seat step, and an expectation the run passes. The green half of the construct's pair; `proofs/wrong-expectation.puck` is the red half. |
| `sources/every-kind-is-given.puck` | A `given` line writes a Fixed, a Bool and a Text row as an authored cell of that kind would, and an expectation over a Fixed or a Bool row records what its gate read in a witness row of that kind, which the report lists beside the ints (`saw=[speed=2.25]`, `saw=[open=false]`). |
| `sources/a-declared-set-is-read.puck` | A rule's `boardCombine` and `writeSet` read a declared `set` by its name: the first paints the set's four members onto an empty board, the second writes into those members of a board that already holds values and leaves the other cells as they were. |
| `sources/a-seat-reads-only-what-it-is-shown.puck` | A read step answered through the reading seat's own disclosure: seat 1 reads a slot only it may read, directly and through a HUD template, and seat 2's read of the same cell, its template naming it, and its attempt to observe seat 1 are refused, each step declaring the refusal text it expects (`seat2 refused "…": …`). |
| `sources/a-module-and-its-use.puck` | The two ways a module's behaviour is stated: a `test` inside the module body, which runs at the `use` that brought it and under that use's arguments, and a `test "…" with armoury(plating: 5)`, whose world is the module standing alone. `proofs/wrong-module-expectation.puck` is the red half. |
| `composition/composition.puck` | A test at a composition root: one generated document per world, the first arming the rest, a step landing in the world it addressed, and a body flown across the `border` and judged by the far world's own rule. [Its own README](composition/README.md) says what the pair proves; `proofs/uncrossed-border.puck` is the red half. |
| `refused-command.world.json` | A guarded transform naming the wrong generation is refused, and a transform naming a row that does not exist is refused, and the phase never advances. The verdict asserts the refusal's consequence; the refusals themselves are recorded in the run's `schedule.json` manifest under `echoes`, which `puck test` prints. |

A verdict row's status cell is authored at `0` and only a rule effect writes `1`
or `2`. The effect door stamps the firing's tick into the row's reserved
`$firedTick` cell, and `puck test` judges a status with no stamp as "never
evaluated" whatever it reads — so a status a cell trait accrued into, or a
scheduled command wrote, cannot pass. Every other door refuses the write by
name.

A failing verdict is **sticky**: once a rule writes `2`, later firings are
absorbed, so the export carries the first failing tick and the values the gate
saw then. That decides the authoring shape — write the expectation at the tick
it becomes decidable, never a pre-emptive `else` that fails before the schedule
has run, or the verdict settles on that first tick and can never pass. A gate
that never becomes decidable leaves its verdict at "never evaluated", which
fails.

`puck test` over this whole directory exits **1**, by construction:
`phase-advance.world.json` carries a verdict that is meant to fail so the
failure path is exercised on every run. The green form is
`puck test tests/Puck.World.Verdicts/refused-command.world.json`, which exits 0.
`TestCommandLawTests` pins both, which is what makes the failing half an
assertion rather than a nuisance.

`phase-advance.world.json` is what a `.puck` `test { given when expect }`
block lowers to, member for member: `when` becomes `schedule.rows`, and each
`expect` becomes one verdict row plus the rule that decides it.
