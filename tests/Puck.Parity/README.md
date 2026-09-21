# Puck.Parity

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
| `sky` | The procedural sky gradient and stars—smooth broad-band shading. |
| `materials` | Two SDF primitives with distinct materials—silhouette edges and specular. |
| `lattice` | A `state.lattices` height-field—the fields-to-pixels path. |
| `noise` | `noiseDisplace` + `cellJitter`—`sdfPcg3d` agreement on SPIR-V and DXIL. |
| `vocabulary` | The `vocabRig` creation (`prototypes`/`placements`): a chamfered Box, a Prism with a `ChamferedRectangle` profile and a recessed panel, a Cylinder with a chamfer and a raised panel, a `symmetry`-folded pair riding a `parent`'s swing, and a `repeat` (with an `origin`) plate—placed twice, at placement scale 1 and 2. The swing reads `state.station` with immediate weight, so both backends render the same nonzero pose at the scheduled ticks. A wall-time driver would integrate different phases as the backends render at different rates. Separate static prototypes add a recessed `GrooveUnion` seam, a `PipeUnion` joint, and `cells` relief in both `F1` and `F2MinusF1` modes at their supported randomness limits. |

`parity.contract.json` is the per-station comparison contract (tile size,
per-tile mean/max delta ceilings, census floors). It is versioned beside the
world on purpose: thresholds are content facts, re-calibrated in the same
change that changes a station, echoable in review. Census floors come from
observed coverage at roughly half its value—a frame whose declared content
collapses fails the gate before any pixel is compared.

`parity-inside.world.json` is the negative-path proof: its camera is authored
inside solid geometry, so every scheduled capture must refuse with
`cameraInside: true` and no frame written. If it ever produces a frame, the
camera-validity gate is broken.

Editing a station: edit `parity.world.json`/`parity.sdf.json` directly (they
are authored documents, not generated), re-run `puck parity`, and re-calibrate
the station's contract entry from the run's observed deltas in the same
change.

`paths.puck` (compiled to `paths.world.json`) is a separate offscreen fixture for bounded Path profiles:
two circular lobes with a clamped polynomial shear, a smooth tapered quadratic
stroke, an outline with a hole, and an anisotropically scaled cubic stroke.
Boot it once with `--backend vulkan` and once with `--backend directx`, using
separate `--state-dir` and `--capture-dir` directories. Feed `world.wait 100`,
`wire.errors`, and `quit` on separate stdin lines. Its captures occur at ticks
60 and 90. Compare the capture directories with `puck parity compare <vulkan>
<directx> --contract tests/Puck.Parity/paths.contract.json`. The default
`puck parity` command continues to run `parity.world.json`. Its census palette
uses observed shaded colors and a separate background entry: the red, gold,
and cyan coverage was 11,993, 5,930, and 10,225 pixels on the verification frame.
The floors retain roughly half that coverage; background cannot satisfy them.

## Documentation

📚 [Engine manual](../../docs/README.md) · 🛠️ [Development](../../docs/development/README.md)
