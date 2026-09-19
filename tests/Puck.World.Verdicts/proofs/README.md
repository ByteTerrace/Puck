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

Both are basis deltas over the shipped `games/hearts.world.json`, like the
curated worlds, and act as `seat1` under the same two authored grants.

`wrong-expectation.puck` is the `test` construct's red half: the same world and
the same step as `../sources/seat-writes-a-cell.puck`, claiming a value the step
never writes. `puck test` exits 1 and the line names the gate as the author
wrote it beside the value it read.
