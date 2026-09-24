# Canary coverage

`canary-coverage.json` records which `Puck.World` source files each
[real-World canary](../../docs/reference/cli.md#puck-canaryreal-world-behavioral-proofs)
executes. [`puck affected`](../../docs/reference/cli.md#puck-affectedthe-checks-a-change-needs)
reads it to choose the canaries a change needs, so a change runs the proofs
that exercise it instead of the whole set.

The file is generated. `puck affected --record` rewrites it by running the full
canary set on a `Puck.World` build that records every method it compiles, then
mapping those methods to source files through the build's PDBs. Never edit it
by hand.

- `sources` lists every C# source of the projects `Puck.World` is built from,
  as they stood at the recording. A source listed here that no canary executed
  chooses no canary when it changes.
- `runs` lists, per canary id, the sources its legs executed, as positions in
  `sources`.

A source missing from `sources`, such as a new file, is reported by
`puck affected` as `unmapped` until the next recording.
