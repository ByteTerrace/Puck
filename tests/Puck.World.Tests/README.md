# Puck.World.Tests

These laws check what needs the composition root, `Puck.World`, or a GPU: the
boot's service collection in each presentation shape, full hosts driven through
`WorldBootHarness`, routed presentation and editor flows over a booted world, the
in-session history over a host, and every device law that opens a Vulkan or
Direct3D device. Rendering and complete game interaction still need verification
by running Puck.World.

The rest of the World's laws live in suites aligned with what they reach, so a
change selects only the suites that can observe it:

| Suite | Holds |
|---|---|
| [`Puck.World.Server.Tests`](../Puck.World.Server.Tests/README.md) | The authoritative simulation, console, addons, storage and release coordinator |
| [`Puck.World.Games.Tests`](../Puck.World.Games.Tests/README.md) | Authored `.puck` worlds run on the server: shipped games, modules, compiled worlds, state baselines |
| [`Puck.World.Machines.Tests`](../Puck.World.Machines.Tests/README.md) | The machine host driven by the Gaming Brick cores |
| [`Puck.World.Client.Tests`](../Puck.World.Client.Tests/README.md) | The client seam with no server |
| [`Puck.World.Presentation.Tests`](../Puck.World.Presentation.Tests/README.md) | A client and its frame producers over a live server or compiled world |
| [`Puck.World.Silo.Tests`](../Puck.World.Silo.Tests/README.md) | The silo and the extension model |

Document-model laws also sit in `Puck.World.Schema.Tests`, and a few protocol,
transpiler and Azure laws in their projects' suites. The fixtures every World
suite shares, and how to write a fast law, are in
[`tests/Shared/World`](../Shared/World/README.md).

## Device laws

`GpuWorkDetailDeviceLawTests` runs the generated counting functions on Vulkan
and Direct3D 12, crossing the low-word boundary in both a plain row and a named
row. It submits two frames before waiting, grows the detail labels between
them, and holds every detail sum to its pass total and each frame to its own
labels. `SdfSkyEvaluationDeviceLawTests` binds the sky's named rows and checks
that covered pixels evaluate no layer.

`BakeSamplingDeviceLawTests` uploads the bake sampling fixture's BC7, BC5 and
BC6H textures with every mip level through the image upload and samples each
probe texel at its level with `Assets/Shaders/bake-sampling.comp.hlsl`, on the
first Vulkan device with a graphics queue, the first Direct3D 12 adapter and
WARP, skipping by name on a host without one. `StagedRegionDeviceLawTests`
runs an uploaded source's conversion on the same three devices with its region
staged, chosen by handing the runtime the device's own memory profile with no
host-visible device-local bytes, and holds a capture of each tick's image to the
CPU reference byte for byte; its Direct3D 12 hardware leg turns the debug layer
on and fails on any `[d3d12-debug]` line, which is safe because device laws run
one at a time with no other device alive. `SurfaceEncoderUploadDeviceLawTests` hands
`SurfaceEncoder.ReadSurface` CPU pixels in both float working formats on the
same three devices, uploaded and drawn through the display encode in SDR, and
holds each RGBA8 channel within one code of the value's own code, headroom
saturating to 255; its debug-layer leg runs alone the same way.
The device laws share `tests/Shared`'s
`HeadlessVulkanDevice` and `DirectXTestDevices`. Every class that opens a
hardware device carries `[Trait("Category", "Gpu")]`, which the build holds
(GPU001). The trait places the class in the suite's one serial collection of
[device laws](../../docs/development/contributing.md#device-laws), which runs after
the parallel ones, and `--filter-not-trait Category=Gpu` runs the rest of the suite
beside a GPU leg. A Vulkan device law's
instance runs under `VK_LAYER_KHRONOS_validation` as the one switch
`HeadlessVulkanDevice.Validation` says (on), unless the law passes
`validation` itself; a host without the layer skips the law by name. An instance
created under validation without a reporting messenger fails the law. The layer
writes what it finds to the device's own writer, and disposing the device
destroys it and its instance, then fails the law that owns it when the writer
holds any `[vulkan-debug] validation` line, naming the first message's
identifier. Creation failures check the same writer after teardown, so a validation
message fails even when the device would otherwise be skipped. Snapshots share the
callback writer's lock. `HeadlessVulkanDeviceValidationLawTests` reads the switch back from the
device and holds the check: a deliberate violation fails the law that owns the
device, and a device asked for no validation reports no layer.
`HeadlessVulkanLifecycleLawTests` holds creation-failure cleanup, missing-messenger
failures and synchronized snapshots over recording APIs without opening a device. The Direct3D 12
debug layer stays off for `DirectXTestDevices.Hardware` and `Warp`: the layer
is enabled for the whole process and removes every device the process already
holds, and on some configurations it stops the next device from being created,
so only a device law, which runs alone, takes
`DirectXTestDevices.Debug`. `SharedFenceLawTests` orders a
Direct3D 11 writer and a Direct3D 12 or Vulkan reader by a shared fence alone.

## Composition root

`WorldInspectorLawTests` checks the shared panel/command formatter, captured placement
and material identities, explicit text-cap refusal, and allocation-free steady formatting.
`CompiledCatalogLawTests` boots every shipped world the World's build compiled
into its Release catalog from its compiled world. `WorldStateRootIsolationLawTests`
reads the IL of every assembly in this suite's output, the desktop assembly
included, and proves the shared scan the other World suites run finds the one
site per per-user root that exists, in the desktop entry point.

This suite holds full hosts and scene probes, so `puck affected` and the gate
treat its full run as heavy: one at a time on the machine.

## Verification

Measure execution separately from restore and build:

```powershell
dotnet test --project tests/Puck.World.Tests/Puck.World.Tests.csproj -c Release --no-build --filter-not-trait Category=Gpu
```

Review slow cases before reducing workloads. Do not make the default run
fast by silently excluding functional coverage.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
