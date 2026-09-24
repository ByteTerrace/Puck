# Blocker-reproduction worlds

One pair per repaired way a test world could pass when it should fail: a world
that reproduces the hole, and a control that differs only in the thing the
repair made authorable. They are not curated test worlds — `puck test` over
[the parent directory](../README.md) does not glob this one, and a law runs each
of these by path instead.

| World | Reproduces | After the repair |
|---|---|---|
| `unexpected-outcome.world.json` | a scheduled row the world refused, with the verdict still passing because the gate never depended on the row | exit 1, naming the row's index, its command and the recorded outcome |
| `expected-outcome.world.json` | the same row declaring `expect: "Refused"` and the text the refusal must carry | exit 0 |

Both are basis deltas over the parent directory's self-contained
`phase-fixture.world.json` and act as `seat1` under the same two authored grants.

`wrong-expectation.puck` is the `test` construct's red half: the same world and
the same step as `../sources/seat-writes-a-cell.puck`, claiming a value the step
never writes. `puck test` exits 1 and the line names the gate as the author
wrote it beside the value it read.

`wrong-module-expectation.puck` is the module subject's red half: the same
module stood up with the same arguments as `../sources/a-module-and-its-use.puck`
uses, claiming a plating it was not given. The generation line names the module
and the arguments, so the run says which instantiation failed.

`uncrossed-border.puck` is the distributed test's red half: the same two plots
and the same border as `../composition/composition.puck`, with the body posed
beside the seam and never sent across it. The near world's verdict passes and the
far world's fails, so the failure is about the crossing rather than about the
body existing; the line names the world, the gate and the occupancy the far
world's rule read.
