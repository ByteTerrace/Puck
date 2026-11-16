# Puck.Abstractions

Puck.Abstractions is the engine's neutral contract layer: backend- and
platform-agnostic interfaces and small value types that every subsystem
implements and depends on.

`ChildProcess` and `OperationDeadline` provide the shared bounded host-process
boundary used by native compilers, shader tools, CLI commands and hosted services.

The optional `IGpuTimestampFactory` creates named observational query pools.
`GpuDeviceServices` carries it through the ordinary counting and creation-fault
wrappers; unsupported queues return no pool. Consumers fence readback and never
use durations as deterministic work or simulation input.

Uniform regions and descriptor writes use the current device's
`IGpuBindings.MaxConstantBufferBytes`. `RequireConstantBufferSize` is their
shared admission check: a nonzero multiple of 256 bytes within that limit,
before allocating a uniform region, growing it or writing its descriptor.
Direct3D 12 exposes 65,536 bytes: 4,096 float4 elements in
[Microsoft's constants](https://learn.microsoft.com/en-us/windows/win32/direct3d12/constants).
Vulkan exposes the physical device's
[`maxUniformBufferRange`](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceLimits.html),
which limits the descriptor range rather than the allocation's size. The
open-source [Dawn Direct3D 12 backend](https://dawn.googlesource.com/dawn/+/f9f446fa58c8941760ea4932b53f5f23a394f493/src/dawn/native/d3d12/PhysicalDeviceD3D12.cpp)
independently uses `D3D12_REQ_CONSTANT_BUFFER_ELEMENT_COUNT * 16` for its
uniform binding limit; these sources agree. Puck binds each uniform region
from its first byte over its whole size, so its allocation must fit one view.
Storage regions retain their separate capacity contract. The reported
`GpuDeviceCapabilities` includes the same limit as `constant-buffer-bytes`.

## Documentation

- [Seam abstractions](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/abstractions.md) — presentation, windowing, machine, platform, and work-counting contracts.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.Abstractions.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
