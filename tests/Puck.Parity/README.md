# Puck.Parity

`parity.puck` is the cross-backend parity check, authored as a world:
`host.presentation: offscreen`, zero seats and zero input, a `station` state
row advanced by rules on `$tick` thresholds, a `select` camera program
dispatching one authored pose per station, and tick-scheduled `captures` rows
that land the frames and write the `puck.parity.manifest.v1` the comparator
consumes. `parity.sdf.json` is its companion `puck.sdf.v1` document
(`world.sdf.load`), carrying the SDF-program stations. `parity.puck`'s
own `prototypes`/`placements` sections carry the `vocabulary` station's
creation content directly (a `puck.creation.v1` document, not a raw SDF op
stream). Its static creations draw their bakes, and the world ships them:
`puck parity` compiles this directory into its run with the World artifact's
own CLI and boots the compiled world, whose bake pack holds every bake, so no
capture depends on a bake made on the device.

The shared sky's second-order harmonic irradiance supplies ambient light, and
its map supplies reflections beneath two lighting-only rectangular panels.
The panels exercise analytic reflections across the stations' roughness values.
The lit stations (`materials`, `lattice`,
`noise`, `vocabulary` and `converge`) therefore change with sky lighting;
`sky` also covers the keyed environment. Their pixel comparisons and census
floors need device qualification when this lighting changes. The `binding`
and `bound` integer reference images remain independent of SDF lighting.

The world boots with soft shadows at `High` and ambient occlusion on
(`render.shadows`, `render.ambientOcclusion`), so every SDF station passes
through the shadow and ambient stages under the cross-backend pixel gate. It
pins temporal reconstruction off (`render.temporal`), and every station but
`converge` holds with it off: `puck parity`'s script turns it on
(`world.temporal on`) sixty ticks before the first station that converges, after
every other station's last capture, and refuses a world that captures a station
without converging once it is on.

The SDF stations' `captures` rows name the `world` instance, the SDF world as the
station camera sees it. The layout also shows two instances of the binding graph
(`binding.graph.json`, each a `views.graphs` row), `binding` and `bound`, in
quarter-size panes along the bottom edge, so the scheduler renders both every
frame, and the `binding` and `bound` rows capture each instance's own output.

| Station | Stresses |
|---|---|
| `sky` | An authored gradient, distance fog, sun disc, stars with twinkle, and clouds with drift, shear and spin. Four captures cross the two-key cycle and its intermediate blends. |
| `materials` | Two SDF primitives with distinct materials—silhouette edges and specular. |
| `lattice` | A `state.lattices` height-field—the fields-to-pixels path. Its census palette uses the field's observed lit top and shaded face and a separate background entry, so the sky behind the field counts toward neither floor. |
| `noise` | `noiseDisplace` + `cellJitter`—`sdfPcg3d` agreement on SPIR-V and DXIL. |
| `vocabulary` | The `vocabRig` creation (`prototypes`/`placements`): a chamfered Box, a Prism with a `ChamferedRectangle` profile and a recessed panel, a Cylinder with a chamfer and a raised panel, a `symmetry`-folded pair riding a `parent`'s swing, and a `repeat` (with an `origin`) plate—placed twice, at placement scale 1 and 2. The swing reads `state.station` with immediate weight, so both backends render the same nonzero pose at the scheduled ticks. A wall-time driver would integrate different phases as the backends render at different rates. Separate static prototypes add a recessed `GrooveUnion` seam, a `PipeUnion` joint, and `cells` relief in both `F1` and `F2MinusF1` modes at their supported randomness limits. |
| `binding` | The binding groups on both backends: every pass reads a frame group at set 0 and a pass group at set 3 through its generated interface. A compute pass seeds an integer pattern, a compute pass pixelates it (the pass group's config `cellSize` and a `uint3` of `levels`, a formatted load, a storage image), and a fullscreen pass samples it through the pass group's image and sampler and adds an integer grain of up to `amplitude` 255ths, hashed from the pixel, the seed and the frame group's tick. Every step works in whole 255ths, so no half-way unorm value is left to a backend to round, and the 96x96 output must equal a CPU reference exactly. |
| `bound` | A bound row reaching a pass. The same graph at the reference tier, `high`, which the graph declares and the row names (`tier`), with the grain pass's `seed` bound to the `grainSeed` state row (`parameters`). The row starts at 0 and the world's `toGrainSeed` rule sets it to 13 at tick 1210. The captures sit on both sides of the move: tick 1195 must equal the reference drawn with seed 0, and tick 1215 the reference drawn with seed 13. Any literal in the binding's place fails at least one of them, a binding that does not resolve (which draws the graph's default seed, 7) fails both, and so does a row that never moved at tick 1215. |
| `converge` | Temporal reconstruction at the `vocabulary` pose: each capture resets the world view's history at its armed tick and serves the eighth frame composed at it (`converge: 8`), over one frozen presentation snapshot, so both backends resolve the same eight jittered samples, reprojected, rectified and accumulated, through the temporal resolve. |

`parity.contract.json` is the per-station comparison contract (tile size,
per-tile mean/max delta ceilings, census floors). It is versioned beside the
world on purpose: thresholds are content facts, re-calibrated in the same
change that changes a station, echoable in review. Census floors come from
observed coverage at roughly half its value—a frame whose declared content
collapses fails the gate before any pixel is compared.

A station whose frames are exact names a `reference`: the comparator computes
the frame each capture must be and fails a side that differs by one byte
(`REFERENCE-FAILED`, naming the side, the count and the first pixel), so a pass
that reads a wrong config value or a wrong binding fails even when both
backends make the same mistake. The census stays as the floor under it. The one
reference kind, `binding`, is `ParityBindingReference`: it reads the config
defaults from `binding.graph.json` and the step rate from `parity.puck`,
and repeats the three passes' integer steps at the capture tick. A station whose
row binds a scalar field states the steps of the bound row in the reference's
`parameters`, keyed by pass and field as the row keys them, each step mapping
the simulation tick a value starts at to the value (`bound` states `grain.seed`
as `{ "0": 0, "1210": 13 }`). At a capture tick the field reads the last step at
or before it, and the graph default before the first; a field the reference
does not read, or a key that is not a tick, is refused by name. The captures sit
mid-way through a grain frame (ticks 1195, 1205, 1215 and 1225 show grain frames
119, 120, 121 and 122 of the 24 Hz flicker), so a capture a few steps early or
late still shows the same grain. An edit to those passes changes
`ParityBindingReference` in the same change.

`puck parity` runs at one pinned reference tier, `high`: the `bound` row names
it, and the binding graph declares it, so that station renders the graph's
`high` variant. A second leg at the floor tier, `low`, on floor hardware is a
deferred hardware check.

Every armed capture is exactly one manifest entry. Either it carries the
`frame` and `census` of the frame that showed its armed tick, with the
`regionTick` that frame refreshed its bound regions at (and, for a capture of a
source instance whose source states its image, the `sourceVerdict` that holds
the frame exactly to that image), or it carries a `refusal` and a `detail`
naming the ticks involved:

| `refusal` | Meaning |
|---|---|
| `cameraInside` | The camera sat inside geometry (`map(cameraPos) <= 0`) at the armed tick. |
| `busy` | Another capture still held the render chain when this one was armed. |
| `stale` | The frame that served it showed a later tick; the detail names both ticks. |
| `failed` | The readback, PNG write, or PNG decode failed. |
| `unserved` | No frame served it: the run ended first, or the offscreen host's hold ran out — 180 seconds while the engine's pipeline set builds, 60 seconds once the engine is ready. The detail names why, such as "the engine's pipeline set is building (12 of 14 pipelines created; waiting on sdf-world-surface, sdf-world-views)". |
| `deviceLost` | The graphics device was lost while it was armed or being read back; the host rebuilt the device and ran on, and the detail carries the loss's reason. |

A landed frame is named `<station>~<tick>.png` (`WorldCaptureRow.CaptureName`,
a generated file name), so a station may not carry `~`. The comparator writes a
failed capture's evidence into a directory with the same `<station>~<tick>`
name.

The comparator's content gate fails a capture that either side refused, and
prints the refusal and its detail. A capture absent from a manifest is a
producer defect, never a refusal. When a capture is armed, the fixed-step pump
ends its catch-up burst there. The host then composes the frame for that tick
before the next step, so a slow machine does not step past a capture. The
offscreen host composes at most one frame per step, and composes the owed frame
again only while a capture waits for it. The comparator's tick verdict holds
each side's `regionTick` to the armed tick, so a frame that shows another
tick's regions fails there whatever its pixels.
`WorldCaptureSchedulerLawTests` (`tests/Puck.World.Tests`) drives this without
a GPU.

`parity-inside.puck` is the negative-path proof: its camera is authored
inside solid geometry, so every scheduled capture must refuse with
`refusal: "cameraInside"` and no frame written. If it ever produces a frame,
the camera-validity gate is broken.

Editing a station: edit `parity.puck`/`parity.sdf.json` directly (they
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
`puck parity` command continues to run `parity.puck`. Its census palette
uses observed shaded colors and a separate background entry: the red, gold,
and cyan coverage was 11,993, 5,930, and 10,225 pixels on the verification frame.
The floors retain roughly half that coverage; background cannot satisfy them.

## Documentation

📚 [Engine manual](../../docs/README.md) · 🛠️ [Development](../../docs/development/README.md)
