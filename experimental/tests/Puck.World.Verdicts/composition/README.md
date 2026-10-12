# A composed run

Two worlds in one process, written as one source. [`plot.puck`](plot.puck) is
the module — a plot of ground, a seat standing on it, and a `landing` region a
rule watches — and [`composition.puck`](composition.puck) stands two of them
side by side, joins their grounds with a `border`, and writes its tests at the
composition root. Each test lowers to one generated document per world: the
first declared world boots the run, and its `schedule.instances` arms the other
beside it, the way `world.instance.start` starts one.

```sh
puck test tests/Puck.World.Verdicts/composition --reproduce
```

What the pair proves:

- **A test at a composition root drives the composed set.** Every line inside
  `given`, `when` and `expect` stands in a `north { … }` or `south { … }` block,
  so it says which world it is about; the tick grid outside those blocks carries
  the whole run.
- **A step lands in the world it addressed.** `north { seat1: world.state.cell.set
  charge … }` lights north and leaves south dark.
- **A verdict is read from each world's own export.** The run writes
  `state-export.json` for the booted world and `state-export.south.json` beside
  it at the one host step boot's export tick falls on, lists both in
  `schedule.json`, and the report prefixes a far world's verdicts with its name
  (`south/…`).
- **A body crossing the border is seen on the far side.** The seat standing in
  north is posed beside the seam and flown across it; the transfer admits it into
  south's population, south's own `landing` region reads as occupied, and south's
  own rule writes the row the test expects. Nothing about that verdict is read
  from north.
- **Determinism covers both.** `--reproduce` runs the pair twice and compares
  every world's export bytes, not just the booted one's.

A far world's verdict rule fires one tick before the booted world's export tick.
An instance armed at boot advances beside the booted world but counts from its
own zero, so the two exports of one host step carry ticks one apart; the
lowering derives that tick, so an author writes the expectation and not the
arithmetic.

The seat is embodied by the module's own `bodies.seatSpawns` at boot, so the
test poses and flies that body rather than joining one: `player.join` names an
identity, and a test world has no profile catalog to name one from.

[`../proofs/uncrossed-border.puck`](../proofs/uncrossed-border.puck) is the red
half: the same two worlds and the same body, never sent across the seam, so the
far world's verdict fails and names the occupancy it read.
