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

Every armed capture is exactly one manifest entry. Either it carries the
`frame` and `census` of the frame that showed its armed tick, or it carries a
`refusal` and a `detail` naming the ticks involved:

| `refusal` | Meaning |
|---|---|
| `cameraInside` | The camera sat inside geometry (`map(cameraPos) <= 0`) at the armed tick. |
| `busy` | Another capture still held the render chain when this one was armed. |
| `stale` | The frame that served it showed a later tick; the detail names both ticks. |
| `failed` | The readback, PNG write, or PNG decode failed. |
| `unserved` | No frame served it: the run ended first, or the offscreen host's hold ran out — 180 seconds while the engine's pipeline set builds, 60 seconds once the engine is ready. The detail names why, such as "the engine's pipeline set is building (5 of 14 pipelines created)". |
| `deviceLost` | The graphics device was lost while it was armed or being read back; the host rebuilt the device and ran on, and the detail carries the loss's reason. |

A landed frame is named `<station>~<tick>.png` (`WorldCaptureRow.CaptureName`,
a generated file name), so a station may not carry `~`. The comparator writes a
failed capture's evidence into a directory with the same `<station>~<tick>`
name.

The comparator's content gate fails a capture that either side refused, and
prints the refusal and its detail. A capture absent from a manifest is a
producer defect, never a refusal. When a capture is armed, the fixed-step pump
ends its catch-up burst there. The host then composes the frame for that tick
before the next step, so a slow machine does not step past a capture.
`WorldCaptureSchedulerLawTests` (`tests/Puck.World.Tests`) drives this without
a GPU.

`parity-inside.world.json` is the negative-path proof: its camera is authored
inside solid geometry, so every scheduled capture must refuse with
`refusal: "cameraInside"` and no frame written. If it ever produces a frame,
the camera-validity gate is broken.

Editing a station: edit `parity.world.json`/`parity.sdf.json` directly (they
are authored documents, not generated), re-run `puck parity`, and re-calibrate
the station's contract entry from the run's observed deltas in the same
change. Growing the shader interpreter also moves the deltas, because it
changes each backend's generated code: benign ±1-LSB differences move around,
and a boundary material-winner flip appears as an isolated multi-LSB tile
delta. Examine such a delta before recalibrating a station.

`paths.puck` is a separate offscreen fixture for bounded Path profiles:
two circular lobes with a clamped polynomial shear, a smooth tapered quadratic
stroke, an outline with a hole, and an anisotropically scaled cubic stroke.
Boot the source itself (`--world tests/Puck.Parity/paths.puck`) once with
`--backend vulkan` and once with `--backend directx`, using
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
