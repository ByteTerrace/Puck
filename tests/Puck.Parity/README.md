# The parity world

`parity.world.json` is the cross-backend parity check, authored as a world:
`host.presentation: offscreen`, zero seats and zero input, a `station` state
row advanced by rules on `$tick` thresholds, a `select` camera program
dispatching one authored pose per station, and tick-scheduled `captures` rows
that land the frames and write the `puck.parity.manifest.v1` the comparator
consumes. `parity.sdf.json` is its companion `puck.sdf.v1` document
(`world.sdf.load`), carrying the SDF-program stations. `parity.world.json`'s
own `prototypes`/`placements` sections carry the `vocabulary` station's
creation content directly (a `puck.creation.v1` document, not a raw SDF op
stream).

| Station | Stresses |
|---|---|
| `sky` | The procedural sky gradient and stars — smooth broad-band shading. |
| `materials` | Two SDF primitives with distinct materials — silhouette edges and specular. |
| `lattice` | A `state.lattices` height-field — the fields-to-pixels path. |
| `noise` | `noiseDisplace` + `cellJitter` — `sdfPcg3d` agreement on SPIR-V and DXIL. |
| `vocabulary` | The `vocabRig` creation (`prototypes`/`placements`): a chamfered Box, a Prism with a `ChamferedRectangle` profile and a recessed panel, a Cylinder with a chamfer and a raised panel, a `symmetry`-folded pair riding a `parent`'s swing, and a `repeat` (with an `origin`) plate — placed twice, at placement scale 1 and 2. The panels and the fold's swing are driver-fed presentation facets; `WorldStampPool` only advances a driver's phase for a body-rooted registration (`live.BodyIndex`), so this row-rooted placement's swing composes the identity every tick — the two scheduled ticks render byte-identical frames. Separate static prototypes add a recessed `GrooveUnion` seam, a `PipeUnion` joint, and `cells` relief in both `F1` and `F2MinusF1` modes at their supported randomness limits. |

`parity.contract.json` is the per-station comparison contract (tile size,
per-tile mean/max delta ceilings, census floors). It is versioned beside the
world on purpose: thresholds are content facts, re-calibrated in the same
change that changes a station, echoable in review. Census floors come from
observed coverage at roughly half its value — a frame whose declared content
collapses fails the gate before any pixel is compared.

`parity-inside.world.json` is the negative-path proof: its camera is authored
inside solid geometry, so every scheduled capture must refuse with
`cameraInside: true` and no frame written. If it ever produces a frame, the
camera-validity gate is broken.

Editing a station: edit `parity.world.json`/`parity.sdf.json` directly (they
are authored documents, not generated), re-run `puck parity`, and re-calibrate
the station's contract entry from the run's observed deltas in the same
change.
