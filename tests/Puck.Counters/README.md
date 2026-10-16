# Render work counters

While the debug world is the one live world, only its two workloads stay here:
`debug.world.json` with `shadow.script.txt` and `avatar.world.json` with
`avatar.script.txt`, and the report schema; the debug workload is
`puck counters`' default. Every other
workload, script, batch and reference is quarantined at the
same path under
[`experimental/tests/Puck.Counters`](../../experimental/tests/Puck.Counters),
and none is run until the owner calls for qualification
([open items](../../docs/plans/open-items.md#quarantined-gates)).

The [indirect comparison batch](../../experimental/tests/Puck.Counters/indirect-comparison/README.md) collects named
method/input observations in four serial boots, retaining ordinary paired
reports and their exact shared-session provenance.
The [sky default matrix](../../experimental/tests/Puck.Counters/sky-defaults/README.md) declares separate sky-form,
independent field-scale and binary-shadow observations through engine readiness;
its measured reports and chosen Full defaults hold on the RTX 4070; the selected
images and RTX 2060 device qualification remain open.

These workloads feed [`puck counters`](../../docs/reference/cli.md#puck-counterswork-counter-collector),
which runs the same offscreen world on Vulkan and Direct3D 12 and reports the
work each render pass performs. The measurements count work, not elapsed time.
Nothing records them in the repository: a change is judged by a report from
before it and one from after it on the same device, held to each other with
`puck counters compare`.

| Workload | Scene | Script |
|---|---|---|
| `debug.world.json` | The debug world's wide key light over its ground, cubes and Moth, inherited from `debug.puck`, seen from the fixed counters camera | `shadow.script.txt` |
| `avatar.world.json` | The debug world as played: its Moth avatar seated before the default third-person camera, inherited from `debug.puck` | `avatar.script.txt` |

Every view renders from the meshes its program is baked into
([frame rendering](../../docs/rendering/sdf/handbook/frame-rendering.md#the-bake)),
so a frame's field evaluations are the bake's alone: they fall to zero once
every group has baked, and the counts read after readiness are the mesh
passes' raster and lighting work.

The debug workload fixes one camera at `(0, 2.5, 7)`, looking at
`(0, 0.8, 0)`, with a vertical field of view of 0.9 radians, over a 1920×1080
output at the medium preset, with temporal reconstruction and indirect lighting
off so every frame rasterizes its shadow cascades and lights every pixel
afresh. The script disables cadence, pauses simulation until the renderer is
ready, and then advances 120 ticks before its one reading.

The seated-avatar workload is the debug world as played: its 1280×720 output
shows the Moth seated before the default third-person camera, at the shipped
high preset with temporal reconstruction and indirect lighting off, read 30
ticks after readiness.

Run these commands from the repository root using a private copy of the
candidate's built CLI. Each command runs both GPU backends; run them serially,
one GPU at a time:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --output <reports>/debug.json
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/avatar.world.json --script tests/Puck.Counters/avatar.script.txt --output <reports>/avatar.json
```

Keep each report beside the corresponding comparison report:
`counters compare` names every changed comparable count, including
intentional savings.

The `sdf.transforms.*` totals count actual packing work over produced frames,
including interpolated poses between ticks. They have class `pacing`, so
different frame counts may produce different totals at the same simulation
tick. Paused frames with unchanged owner inputs pack nothing; resuming still
advances any pending followers. The source compiler's basis reads and merges
are also cache-dependent: `world.boot.compile-documents-read`,
`world.boot.compile-compositions`, and
`world.boot.compile-compositions-shared` have class `pacing`. Ordinary boot
document reads and compositions remain deterministic and must agree between
two runs of one build.
