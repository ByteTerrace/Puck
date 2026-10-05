# Render work counters

The [indirect comparison batch](indirect-comparison/README.md) collects named
method/input observations in four serial boots, retaining ordinary paired
reports and their exact shared-session provenance. Gate discovery routes its
recorded workload/script associations through the batch once.
The [sky default matrix](sky-defaults/README.md) declares separate sky-form,
independent field-scale and binary-shadow observations through engine readiness;
its measured reports and final defaults remain owed.

These workloads feed [`puck counters`](../../docs/reference/cli.md#puck-counterswork-counter-collector),
which runs the same offscreen world on Vulkan and Direct3D 12 and records the
work each render pass performs. The measurements count work, not elapsed time.

| Workload | Scene | Script | Ceilings |
|---|---|---|---|
| `counters.puck` | Two blocks and their caps | `counters.script.txt` | `counters.ceilings.json` |
| `nexus.world.json` | The Nexus overworld hub, inherited from `puck.world.json` | `counters.script.txt` | `nexus.ceilings.json` |
| `courtyard.world.json` | The Moth courtyard, inherited from `moth-courtyard.puck` | `counters.script.txt` | `courtyard.ceilings.json` |
| `sky-still.puck` | An unchanging sky | `sky.script.txt` | `sky-still.ceilings.json` |
| `sky-drift.puck` | Moving clouds | `sky.script.txt` | `sky-drift.ceilings.json` |
| `sky-twinkle.puck` | Twinkling stars | `sky.script.txt` | `sky-twinkle.ceilings.json` |
| `sky-cycle.puck` | A changing sky cycle | `sky.script.txt` | `sky-cycle.ceilings.json` |

The dense workloads inherit their scenes' geometry and behavior. Each fixes
one camera at `(0, 2.5, 7)`, looking at `(0, 0.8, 0)`, with a vertical field of
view of 0.9 radians. Its 1920×1080 output uses the floor preset's 1440×810 render
grid. Temporal reconstruction and dynamic resolution are off, so the hit
samples share one camera and extent across comparisons. The script disables
cadence, pauses simulation until the renderer is ready, and then advances 120
ticks before its one reading.

Run these commands from the repository root using a private copy of the
candidate's built CLI. Each command runs both GPU backends; run them serially
on each GPU whose counts the ledger holds. A first recording creates the
Nexus or courtyard ceilings file at the path shown, using measured counts:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/nexus.world.json --script tests/Puck.Counters/counters.script.txt --ceilings tests/Puck.Counters/nexus.ceilings.json --record --output <reports>/nexus.json
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/courtyard.world.json --script tests/Puck.Counters/counters.script.txt --ceilings tests/Puck.Counters/courtyard.ceilings.json --record --output <reports>/courtyard.json
```

Each backend's ledger holds shared deterministic ceilings and required zeros,
plus a `devices` array for counts that depend on the GPU. These include march
steps, shape evaluations and analytic gradient evaluations. A device record
is identified by backend, PCI vendor and device identifiers, and driver
implementation. Driver versions remain recorded evidence; updating a driver
keeps its record and reports a note. Recording replaces the shared ceilings
and that device's record while preserving the other device records. A check
on an unrecorded device fails by name; record that device on its own GPU.
The [counted-cost ceilings reference](../../docs/reference/cli.md#counted-cost-ceilings)
owns the complete recording and refusal rules.

An existing report from the same production workload can supply the record
without another GPU run. It must contain both backends and their device
identities; a primary-reference report has the wrong script identity:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --report <reports>/nexus.json --ceilings tests/Puck.Counters/nexus.ceilings.json --record
dotnet <cli-copy>/Puck.Cli.dll counters --report <reports>/courtyard.json --ceilings tests/Puck.Counters/courtyard.ceilings.json --record
```

The ledgers contain measured counts rather than placeholders. Once a world's
matching `.ceilings.json` exists, `puck gate --gpu` discovers that workload
and uses its recorded `counters.script.txt`. Each world contributes one gate
step, regardless of how many devices its ledger holds.

Use `--check` in place of `--record` to judge the recorded ceilings. Keep each
report beside the corresponding comparison report: `counters compare` names
every changed comparable count, including intentional savings. Comparisons of
renderer optimizations use law-level or compiled variants and retain the same
world, camera, extent and hit samples.

`primary-reference.script.txt` applies the `primary-reference` shader tree
through `world.shaders.reload` while simulation is paused. Its primary wrapper
defines `SDF_TAPE_REFERENCE` and includes the production kernel, so the
reference compiles out only primary's tape reads. The tape pass still runs and
counts its own cost. The script then advances the same 120 ticks as
`counters.script.txt`. Run both worlds with this reference before the ordinary
recording commands above:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/nexus.world.json --script tests/Puck.Counters/primary-reference.script.txt --output <reports>/nexus-primary-reference.json
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/courtyard.world.json --script tests/Puck.Counters/primary-reference.script.txt --output <reports>/courtyard-primary-reference.json
dotnet <cli-copy>/Puck.Cli.dll counters compare <reports>/nexus-primary-reference.json <reports>/nexus.json
dotnet <cli-copy>/Puck.Cli.dll counters compare <reports>/courtyard-primary-reference.json <reports>/courtyard.json
```

The comparison reports intentional count differences and therefore exits 1
when work falls. Read each backend's `world` node: let `full` and `pruned` be
`sdf.world$primary`'s `gpu.shapes.evaluated` in its two reports, and `tape` be
the optimized report's `sdf.world$tape` shape count. The Nexus requires
`pruned <= 0.60 × full`, a reduction of at least 40%. Both dense workloads
require `tape < 0.25 × (full - pruned)`, equal primary `gpu.march.steps`, and
passing parity. Shader compiler and pipeline lifetime counts differ because
the reference reloads one kernel. The two scripts have different identities;
record production ceilings only with `counters.script.txt`. The compare verb
compares their measured rows without applying a ceilings file.

The `sdf.transforms.*` totals count actual packing work over produced frames,
including interpolated poses between ticks. They have class `pacing`, so
different frame counts may produce different totals at the same simulation
tick. Paused frames with unchanged owner inputs pack nothing; resuming still
advances any pending followers. The source compiler's basis reads and merges
are also cache-dependent: `world.boot.compile-documents-read`,
`world.boot.compile-compositions`, and
`world.boot.compile-compositions-shared` have class `pacing`. Ordinary boot
document reads and compositions remain deterministic and must agree between
the production and reference runs. Shader reload reads no world documents.

The floor-tier RTX 2060 measurements meet these conditions. Primary shape
evaluations fall by 42.7% on the Nexus and 2.2% on the courtyard; tape work
costs 1.5% and 8.6% of the respective savings. March steps remain equal on both
backends, and parity passes. The courtyard has no independent percentage
reduction threshold.

For winner gradients, the Nexus reference full walk supplies the number of
shapes with nonzero blend weight. The device laws hold `gpu.shapes.gradients`
to that count and require total shape evaluations, including winner selection,
to be lower than the full-gradient walk. These laws pass on both backends at
the same hit samples; CPU document loading alone does not establish them.

The CPU inventory and camera study run without a device:

```text
dotnet test tests/Puck.World.Tests -c Release --no-build --no-restore --filter-class Puck.World.Tests.SdfTapeInventoryLawTests Puck.World.Tests.SdfTapePredictionLawTests --output Detailed
```

The study counts every tile and slab of the initial composed frame using the
production certificates and Puck.Maths primitive enclosures. It reports finite
coverage, rejected models, and exact counts of certified deletion decisions.
It uses a conservative camera-cone mask, near-plane entry and authored far
bound because it has no GPU beam readback. Its interval centre values enclose
the admitted float results; they do not predict a backend's centre-value bits.
These counts therefore describe the CPU enclosures, not the live workload's
120-tick, march-weighted shape reduction. Slab rows separate the near-camera
decisions from overlap in larger far balls; zero-radius and direct interval
diagnostics distinguish radius limits from primitive-enclosure limits.

The tape retains unknown or uncertified candidates. It applies only to the
camera mask, inside its covered balls and under the same detail selection;
secondary queries keep their own full masks. Each slab has its own instruction
mask, so losing shapes inside retained segments can disappear. Compiled-part
fast paths read the mask through each placement's original instruction indices;
independently marched parts retain their own field even when their root union
loses. Root queries that omit those parts use the full root walk. These limits
preserve the full field's result and remain part of the
measured workload cost.

Run the field device laws and parity serially before accepting the counts:

```text
dotnet test tests/Puck.World.Tests -c Release --no-build --no-restore --filter-class Puck.World.Tests.SdfFieldDeviceLawTests
dotnet <cli-copy>/Puck.Cli.dll parity --debug-layers
dotnet <cli-copy>/Puck.Cli.dll canary pipeline-counters world-counters --keep-transcripts
```

The device-law filter includes both Vulkan and DirectX versions of
`GradientsEvaluateExactlyTheFinalWeightedShapes`,
`NexusHitGradientsEvaluateExactlyTheFinalWeightedShapes`,
`TileTapesPreserveEverySampleAndCountLessShapeWork`, and
`NexusTileTapesPreserveTheFullMarchSamples`,
`SparseTileTapesPreserveMaskedInstancesAndWorldSegments`,
`PowerGaugeEnforcesItsCertifiedNormEnvelope`, and
`SmoothGradientEndpointsReturnTheDecidingGradientExactly`, alongside the existing field and
gradient checks. Each pair must execute without skips. The Nexus laws capture
the presenter's composed frame and actual dynamic transforms on the CPU, then
compare both walks at the same GPU hit samples. The counters workloads run the
live world through their fixed simulation script.

The `world-counters` canary's CPU upload law holds the tape dispatch and its
pass-block upload. The tape clears its enable words and evaluates no shapes in
this empty field. The GPU transcript checks the complete exact rows. The
`pipeline-counters` fixture also evaluates no SDF shapes and pins zero in both
shape columns.

When counted passes or shape work change, record each affected device in the
existing workload ledgers:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/counters.puck --script tests/Puck.Counters/counters.script.txt --ceilings tests/Puck.Counters/counters.ceilings.json --record
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/sky-still.puck --script tests/Puck.Counters/sky.script.txt --ceilings tests/Puck.Counters/sky-still.ceilings.json --record
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/sky-drift.puck --script tests/Puck.Counters/sky.script.txt --ceilings tests/Puck.Counters/sky-drift.ceilings.json --record
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/sky-twinkle.puck --script tests/Puck.Counters/sky.script.txt --ceilings tests/Puck.Counters/sky-twinkle.ceilings.json --record
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/sky-cycle.puck --script tests/Puck.Counters/sky.script.txt --ceilings tests/Puck.Counters/sky-cycle.ceilings.json --record
```
