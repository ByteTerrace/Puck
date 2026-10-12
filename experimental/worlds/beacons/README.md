# Puck Beacons

The smallest package that is about modules rather than about a game: one beacon
module, and a source that stands two of them side by side across a walkable
seam. It exists so the two ways a module's behaviour is stated both have a
worked example under the tracked worlds.

- [beacon.puck](beacon.puck) is the module. It takes the centre of its plot and
  the charge threshold its light waits for, and it carries its own tests. Those
  tests hold for every threshold the module admits, which is what lets them run
  as they stand wherever the module is used.
- [beacons.puck](beacons.puck) uses it twice — `north` at threshold 3, `south`
  at threshold 7 — and joins their grounds with a `border`. `north` is its
  `entry world`, the one `Puck.World --world beacons.puck` starts in. Compiling
  it writes `north.world.json` and `south.world.json` side by side, with the
  reciprocal adjacency rows the border derives, into the directory `-o` names.

## What `puck test` runs here

```text
puck format --check .
puck lint beacons.puck --strict
puck compile beacons.puck --validate
puck test .
```

Six test worlds, from two places:

- **The module's own two tests, once per world.** Each `world` statement is a
  use, so `north` and `south` each run `a beacon boots dark` and `a charge past
  every admitted threshold lights the beacon` against their own threshold. The
  report names the world the test ran for.
- **Two tests written with the module.** `test "…" with beacon(centre: …,
  threshold: 3)` chooses its own arguments, so it can claim what one particular
  threshold means. Its world is the module standing alone, not either declared
  world.

A `puck lint` of `beacon.puck` on its own reports the module as never
instantiated. That is what a module fragment looks like to the linter: the
source that uses it is `beacons.puck`.

## What it does not prove

The `border` is authored and compiled, and the two documents carry each other's
adjacency rows, but no test here crosses it: these beacons carry no kit, no
collision and no seat rig, so no body exists to walk the seam. The worked
crossing is
[`tests/Puck.World.Verdicts/composition`](../../tests/Puck.World.Verdicts/composition/README.md),
whose module supplies those.
