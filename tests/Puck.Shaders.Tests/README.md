# Puck.Shaders.Tests

This xUnit v3 suite verifies shader manifests, pipeline graphs and liveness,
versioned resources and the planned barriers, binding layouts and the generated frame block, compiler adapters, probe kinds, and
compiled-bytecode freshness. It tests the document and build contracts without
claiming correctness for every GPU driver.

`ShaderPipelineRenderNodeLawTests` run the pipeline render node against
`FakePipelineGpu`, which implements the node's factory seams with no device.
The fake counts every object it creates, the thread that created it, and each
disposal, and it counts the bytes of every image, buffer, render target and
vertex buffer independently of the node. It can fail the Nth creation, hold every shader module and pipeline
creation behind a gate the way a cold driver cache does, hold the queue
unfinished, and records fence waits, device drains and barriers in order. The
laws cover refusing a candidate whose build or allocation fails partway,
a planned memory account equal to the bytes the fake holds for every graph
shape, refusing a candidate over the memory budget before anything is built
while the installed graph keeps running, zero managed
allocation in steady-state frames, building every candidate's modules and
pipelines off the frame thread while the installed graph keeps presenting, and
retiring the old graph only once the queue has finished with it, without a
device drain.
`ShaderPipelineVersionLawTests` cover forwarding: readers ordered before an
overwrite, each refusal by name, one storage per chain, and a node that records
exactly the barriers the plan gives each access.
`ShaderInterfaceLawTests` pin the pass interface's layout, document and
generated include. `ShaderInterfaceSpikeTests` build the variant passes under
`Assets/Interfaces/` with DXC. They hold the SPIR-V and DXIL readers to the
interface's layout and require byte-identical output from two builds.
`-showLiveOutput` prints each build's content pins for comparison with another
host running the same DXC. `ShaderFrameBlockLawTests` compile every shipped
pipeline pass and hold the offsets DXC gave each frame member in both bytecodes
to the bytes the host writer puts there, and the film grain set's checked-in
declarations to the generator. `EchoCanaryFixtureTests` hold the `pipeline-echo`
canary's generated fixtures to the generator and show reflection catching its
hand-perturbed offset.

`ShaderPackageLawTests` cover the source closure and `puck.shader.package.v1`
packages over the fixtures in `Assets/ShaderPackages`: transitive, missing and
outside includes, an image-only one-off shader, and malformed manifests, and a
package that loads from its binaries with no compiler. A fake tool runner writes
bytecode that hashes its input, wrapped as a SPIR-V module with no bindings, so
equal bytecode means an equal compile input.

## Verification

```powershell
dotnet test tests/Puck.Shaders.Tests/Puck.Shaders.Tests.csproj -c Release
```

The native compiler checks require `dxc` when the corresponding test is
available; the test discovery and managed contract checks remain useful
without a GPU.

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
