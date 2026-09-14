# Build and run Puck

This guide uses the Windows desktop application. Run commands from the
repository root in PowerShell. You will build World, inspect a running session,
and find a small content-editing workflow.

## Prerequisites

Install the .NET SDK version selected by [global.json](../global.json). The
repository disables automatic SDK roll-forward, so having a different .NET 10
SDK installed is not sufficient. Package restore requires access to the feeds
listed in [NuGet.Config](../NuGet.Config).

The desktop render path needs a compatible Direct3D 12 or Vulkan GPU and the
DirectX Shader Compiler (`dxc`) on the process search path. Check the
[World launch guide](../src/Puck.World/README.md#usage) and the selected
backend's requirements before choosing a device. Live GLSL/Shadertoy editing
also needs glslang and SPIRV-Cross; the
[shader compiler guide](../src/Puck.Shaders/README.md#one-off-shaders) explains
how tools are resolved.

Check the SDK and compiler from the same terminal that will run the build:

```powershell
dotnet --version
Get-Command dxc
```

## Build the application

```powershell
dotnet build src/Puck.World/Puck.World.csproj -c Release
```

The first build restores dependencies and prepares shader assets. Resolve a
restore or shader-compiler error before proceeding; the relevant diagnostic
names the failed dependency or tool.

## Run a world

```powershell
dotnet run --project src/Puck.World -c Release --no-build -- --exit-after-seconds 10 --state-dir artifacts/getting-started/state
```

World reports the document it loaded and opens a window. This command closes
the application after ten seconds and keeps session files in an explicit
scratch directory. Omit `--exit-after-seconds 10` when you want to explore
until closing the window yourself. The backend can be selected at launch with
`--backend directx` or `--backend vulkan`.

## Inspect the same host without a window

Headless mode runs the World host without GPU presentation. It is useful for
checking documents and simulation; it does not test rendering.

```powershell
'world.status' | dotnet run --project src/Puck.World -c Release --no-build -- --headless --exit-after-seconds 3 --state-dir artifacts/getting-started/headless
```

Look for the loaded-world information and the status response. If startup fails,
read the preceding error before interpreting missing command output. The
[World guide](../src/Puck.World/README.md) explains the console, saved state,
recording and more detailed launch options.

## Make a first content change

The [three-pass ink example](../src/Puck.World/README.md#shader-pipelines)
provides a focused editing task: launch its world, draw with the pointer, then
change the visualization's exposure through `pipeline.set`. The guide includes
exact commands for inspecting the result, pausing, stepping and capturing it.
Its pipeline document and shader sources are linked beside the instructions.

For document authoring, start with the
[World DSL examples](../src/Puck.World.Transpiler/README.md). For native handheld
content, use the [cartridge workflow](../src/Puck.GamingBricks.Forge/README.md).
These workflows have different compiler requirements; choose one first.

## Continue

Read the [overview](overview.md) for the engine's runtime model,
[authoring](authoring/README.md) for content tasks, or
[development](development/README.md) for investigation and verification tools.
