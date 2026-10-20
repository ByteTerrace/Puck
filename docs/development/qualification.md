# Qualifying a package

`puck qualify` checks a producer-built `Puck.World` package against the
release profile, a checked-in document that says how the package is published
and what it must survive. It runs the package's own World, never a build of the
checkout, so what it proves is true of the bytes that ship. It needs a GPU on
each backend it runs, so it runs on a GPU machine with no build or other GPU
work beside it, like `puck parity` and the GPU canaries.

While the debug world is the one live world, the release profile qualifies it
alone: its stability matrix boots the debug world, and its functional list is
empty, because every canary is quarantined under `experimental/`
([quarantined gates](../plans/open-items.md#quarantined-gates)).

## Running it

Publish the World the way CI's artifact producer does, then qualify the
output directory:

```text
dotnet build src/Puck.World -c Release -p:PuckShaderSpirvEnabled=true
dotnet publish src/Puck.World -c Release --no-build --no-restore -p:PuckShaderSpirvEnabled=true -p:AppendRuntimeIdentifierToOutputPath=false -o artifacts/world
puck compile --tree worlds/debug --output artifacts/world/worlds/debug
puck qualify artifacts/world --profile tests/Puck.Qualification/release.profile.json
```

On Windows a build or publish of one project ships Direct3D 12 bytecode alone,
and every Vulkan cell of its package is refused; `PuckShaderSpirvEnabled`
adds SPIR-V, as CI and a solution build do.

A CI download of the published World is the same directory and qualifies the
same way. `puck qualify <package> --profile <file> --list` checks the package
and prints the matrix and every cell's script without booting anything.
`--profile` is required, and `--output` says where the report goes.

A run first checks that the package's entry assembly exists and is in the
profile's publish mode, then installs a clean copy of the package in the run's
run directory and qualifies that copy. It runs in two halves:

1. **The functional canaries** the profile names, run on the copy's World
   through `puck canary --world-artifact`, each checked on both backends. The
   profile names none while every canary is quarantined; the quarantined
   profile named every `pipeline-*` canary, `no-device-compile` and the other
   offscreen GPU proofs.
2. **The stability matrix**: every workload at every resolution on every
   backend, offscreen. Each cell boots from a fresh state root, so its
   pipeline cache starts cold. It boots an overlay document written beside the
   workload's world, which takes the world as its basis and sets only the
   offscreen presentation and the cell's extent.

Each cell's script, transcripts, state root and captures stay in the run's
run directory beside the report, a `puck.qualification.report.v1`
document. The report records the package, the profile, and for each cell the
backend, extent, workload lengths, device and driver, memory profile, peak
pipeline bytes against the threshold, validation-message count, and verdict.
A run that passes with `--output` deletes the run directory; any other run
keeps it and prints `run directory kept: <path>`, so the report and its
evidence survive (see [the CLI conventions](../reference/cli.md#conventions)).

## The release profile

The profile is `tests/Puck.Qualification/release.profile.json`, a
`puck.release.profile.v1` document; `puck qualify` has no default profile. `puck schema` generates its schema at
`tests/Puck.Qualification/puck.release.profile.v1.schema.json`, and `puck qualify` refuses a profile that names a member the schema does
not have, leaves out a required one, or describes a matrix it cannot run.

**Publish mode.** The package is framework-dependent ReadyToRun, the form CI
publishes. `Puck.World` declares itself not AOT-compatible
(`IsAotCompatible` is false), so no Native AOT package exists to qualify.
ReadyToRun moves first-use compilation out of startup while the machine
supplies the .NET runtime. Qualification checks the mode rather than
trusting the directory: the entry assembly must carry a ReadyToRun header,
which a plain build's output never does.

**Compiler discovery.** The profile sets `None`: a World run in the package
needs no shader compiler, because a shipped world's pipelines load from the
[package store](../reference/shaders.md#the-builds-package-store) the build
writes beside the worlds. Every matrix leg runs with each directory holding
`dxc` removed from its search path, so a world that asks for a compile fails
its cell. `Path` lets the World find the compiler on the search path the run
inherits, as on a developer machine. The functional canaries always run with
the caller's search path, because some of them build shader packages in the
CLI before the World starts. The `no-device-compile` canary still proves a
World with no compiler: it removes every `dxc` directory from its own World's
search path, whatever the caller's holds.

**Validation layers.** The profile turns on the validation layer for both
backends under `debugLayers`. Every World the run starts on a listed backend
boots with `--debug-layers`, the functional canaries' included. A cell on a listed backend fails on any
`[vulkan-debug] validation` message or any `[d3d12-debug]` message. This is
how qualification answers the Direct3D 12 buffer-transition question in the
[rendering plan](../plans/rendering.md#p1b--foundation-qualification): the
first transition of a buffer in a command list relies on implicit promotion
from `COMMON`, and a debug-layer run with no message is the evidence that it
is valid. Both layers also judge teardown. The Vulkan validation layer reports
every object still alive when the device is destroyed. On Direct3D 12, the
device context releases its own objects, asks the debug layer for every
object the device still holds, and prints each as a `[d3d12-debug] live`
line, which fails the cell like any other debug message. A device created with
the layer says whether it is live on standard error: a Direct3D 12 device prints
`[d3d12] debug layer live`, or `[d3d12] debug layer requested but not loaded`
when it has no info queue, and a Vulkan instance prints
`[vulkan] validation layer live`, or `[vulkan] validation layer requested but
not live` when it has no debug messenger. A cell on a listed backend that never
prints its backend's live line fails, so a run with no validation message can be
told from one the layer never watched. The Direct3D 12 recorder corrects a stated old layout from its tracked
resource state, so a wrongly declared layout shows only on Vulkan. On a machine where
the Direct3D 12 debug layer stops the device from being created, the
Direct3D 12 cells are blocked and name the reason.

**The matrix.** Two backends (Vulkan, then Direct3D 12), two resolutions
(1280×800, the Steam Deck's panel, and 1920×1080), and one workload. Every cell's script first waits, for up to 180
seconds, for the engine to be ready (`world.wait ready`): a clean install
builds the engine's pipelines on a cold driver cache, so the warm-up counts
from readiness rather than from boot.

| Workload | World | What a cell does |
|---|---|---|
| `debug` | `Assets/worlds/debug.world.json` | Warms up 900 ticks, long enough for the boot's mesh bake, soaks 1800, reloads the world twice with a 900-tick warm-up after each, and soaks 1800 again. |

The `debug` workload boots the debug world, the one live world. A workload may
also churn a pipeline (`pipeline`: reloads, resizes, loads and unloads of a
`views.graphs` row, each settled over counted submissions); the quarantined
`ink-pipeline` workload did, and the debug world has none. The
[forcing world](../plans/state-and-language.md#the-forcing-world) is not
authored yet, so no workload boots it. Every length is ticks of the world's
simulation clock or frames the pipeline submits, never time. A workload's `timeoutSeconds` is only the
ceiling a hung World is killed at.

## What a cell checks

A cell reads its evidence from the World's own console:

- **No object created while soaking.** `world.counters --json` is read before
  and after each soak window. Every `gpu.created.*` count must be the same at
  both ends, whichever node or source reports it.
- **Exactly the installed graph.** After each pipeline step the script resets
  the instance, waits for its settle frames to be counted, and reads
  `pipeline.inspect`. The instance must own exactly its installed graph's
  steady-state bytes (`owned=` equals `steady=`), so nothing a replacement or
  a resize retired is still held.
- **Every unload releases.** After each unload, `pipeline.inspect` must be
  refused because the instance is no longer rendered.
- **The memory threshold.** The largest `owned=` or `peak=` any inspection
  reads must not exceed the cell's `peakOwnedPipelineBytes`. `peak=` is the
  bytes a replacement of the installed graph would reach, so it bounds the
  moment a reload or resize holds two graphs at once. When the cell sets
  `peakDeviceLocalBytes`, the largest `gpu.memory.device-local.peak` any
  `world.counters --json` reading reports (the `memory.vulkan` or
  `memory.directx` source) must not exceed it, and a cell with the threshold
  but no such reading fails.
- **Every world reload applies.** Each `world.reload` the script sends must
  answer that it applied. A reload refused by the World, or a submission the
  wire codec refuses before the verb can answer, fails the cell and is quoted
  in its findings.
- **Every wait reached**, no pipeline candidate refused, and, with the
  validation layer on, no validation message.

### Verdicts

| Verdict | Meaning |
|---|---|
| Pass | Every check the cell makes held. |
| Fail | A check observed the package misbehave, or the World exited, timed out, or answered a scripted command other than the script expects. A world that asks for a shader compiler under compiler discovery `None` fails. |
| Blocked | The cell could not run here: the machine has no usable device for the backend, a shader tool that compiler discovery `Path` lets the World find is absent, or the run's own infrastructure refused. |

The functional canaries get one verdict: pass when the canary run exits 0,
fail when it exits 1, and blocked otherwise. Any failure fails the run and
exits 1. Otherwise any blocked check exits 2, and everything passing exits 0.
A refused profile, package or plan also exits 2.

## Thresholds

Each cell sets a peak device-local bytes threshold, the most device-local memory
the World process may hold at once: the largest `gpu.memory.device-local.peak`
any of the cell's `world.counters --json` readings reports. Every cell's report
records that peak whether or not its cell sets a threshold, which is how a
threshold is set. Each is the published package's reading on the RTX 4070 with
a tenth added and rounded up to a whole MiB, since the peak is a pacing count
(when the bake's staging overlaps its meshes moves it), not a deterministic one:

| Cell | Reading | Threshold |
|---|---|---|
| Vulkan, 1280×800 | 1,006,632,960 | 1,107,296,256 |
| Vulkan, 1920×1080 | 1,645,248,512 | 1,809,842,176 |
| Direct3D 12, 1280×800 | 1,031,751,680 | 1,135,607,808 |
| Direct3D 12, 1920×1080 | 1,660,241,920 | 1,826,619,392 |

The AMD devices have no reading yet. A pipeline workload also sets peak owned
pipeline bytes, a planned count that is the same on every device: the
quarantined `ink-pipeline` workload's was the `ink` graph's planned replacement
peak, 96 bytes a pixel. The `debug` workload churns no pipeline, so its cells
carry none.

The profile defers two checks, and a run prints each with its reason:

- **Frame-time median and tail**, and **reload stalls**. Both need wall-clock
  and GPU timing, which is evidence beside counts and never a gate
  ([the rule](../decisions/rendering.md#the-pipeline-foundation)).
  Qualification sets no frame-time threshold and counts that every reload
  installs, never how long it took.
