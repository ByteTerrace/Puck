# Qualifying a package

`puck qualify` checks a producer-built `Puck.World` package against the
release profile, a checked-in document that says how the package is published
and what it must survive. It runs the package's own World, never a build of the
checkout, so what it proves is true of the bytes that ship. It needs a GPU on
each backend it runs, so it runs on a GPU machine with no build or other GPU
work beside it, like `puck parity` and the GPU canaries.

## Running it

Publish the World the way CI's artifact producer does, then qualify the
output directory:

```text
dotnet build src/Puck.World -c Release
dotnet publish src/Puck.World -c Release --no-build --no-restore -p:AppendRuntimeIdentifierToOutputPath=false -o artifacts/world
puck qualify artifacts/world
```

A CI download of the published World is the same directory and qualifies the
same way. `puck qualify <package> --list` checks the package and prints the
matrix and every cell's script without booting anything. `--profile` names
another profile, and `--output` says where the report goes.

A run first checks that the package's entry assembly exists and is in the
profile's publish mode, then installs a clean copy of the package in the run's
scratch directory and qualifies that copy. It runs in two halves:

1. **The functional canaries** the profile names, run on the copy's World
   through `puck canary --world-artifact`. These are every `pipeline-*`
   canary, `no-device-compile`, and the other offscreen GPU proofs, each checked
   on both backends. `ReleaseProfileLawTests` holds the profile to naming every
   `pipeline-*` canary and only offscreen ones.
2. **The stability matrix**: every workload at every resolution on every
   backend, offscreen. Each cell boots from a fresh state root, so its
   pipeline cache starts cold. It boots an overlay document written beside the
   workload's world, which takes the world as its basis and sets only the
   offscreen presentation and the cell's extent.

Each cell's script, transcripts, state root and captures stay in the run's
scratch directory beside the report, a `puck.qualification.report.v1`
document. The report records the package, the profile, and for each cell the
backend, extent, workload lengths, device and driver, memory profile, peak
pipeline bytes against the threshold, validation-message count, and verdict.

## The release profile

The profile is `tests/Puck.Qualification/release.profile.json`, a
`puck.release.profile.v1` document. `puck schema` generates its schema beside
it, and `puck qualify` refuses a profile that names a member the schema does
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
line, which fails the cell like any other debug message. On a machine where
the Direct3D 12 debug layer stops the device from being created, the
Direct3D 12 cells are blocked and name the reason.

**The matrix.** Two backends (Vulkan, then Direct3D 12), two resolutions
(1280×800, the Steam Deck's panel and the flagship world's authored size, and
1920×1080), and two workloads:

| Workload | World | What a cell does |
|---|---|---|
| `flagship` | `Assets/worlds/puck.world.json` | Warms up 300 ticks, soaks 1800, reloads the world twice with a 300-tick warm-up after each, and soaks 1800 again. |
| `ink-pipeline` | `Assets/worlds/pipeline.world.json` | Waits for the `ink` pipeline to install, warms up 120 ticks, soaks 900, then reloads the pipeline three times, resizes its slot to half and back three times, and unloads and loads it three times, settling four counted submissions after each step. |

The `flagship` workload boots the flagship world the package ships. The
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

The profile sets one threshold for the pipeline workload, peak owned pipeline
bytes, on every cell. It is the `ink` graph's planned replacement peak at the
cell's extent. The installed graph's steady state holds three storages (a
16-byte float simulation image and two 4-byte color images) in each of three
frame slots, 72 bytes a pixel. A replacement allocates a second graph beside
it, but the float simulation history moves into the candidate rather than
being allocated again, so the peak is 72 + 72 − 3 × 16 = 96 bytes a pixel:
98,304,000 bytes at 1280×800 and 199,065,600 at 1920×1080. The node counts
these bytes from its plan rather than asking the driver, so they are the same on
every device and backend, and the threshold is the planned peak itself: any
byte over it fails. The `flagship` workload has no pipeline, so its cells
carry no threshold.

The profile defers three checks, and a run prints each with its reason:

- **Peak device-local bytes.** `memory.vulkan` and `memory.directx` count the
  device-local bytes a World process allocates and releases at their actual
  allocation sizes, and the peak held at once; swapchain images are never
  counted. Every cell's `peakDeviceLocalBytes` is null, because each is set
  from a qualification reading of the published package on the reference
  devices and none has been taken.
- **Frame-time median and tail**, and **reload stalls**. Both need wall-clock
  and GPU timing, which are deferred with no date. Qualification sets no
  frame-time threshold and counts that every reload installs, never how long it
  took.
