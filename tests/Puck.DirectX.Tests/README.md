# Puck.DirectX.Tests

This xUnit v3 suite checks the Direct3D 12 backend's managed decisions that do
not need a device. These include which barrier a buffer transition records,
and how the lazily created device context treats a creation the host cannot
satisfy: it reports `GpuDeviceUnavailableException`, and draining a device
that was never created neither creates it nor retries the failure. The
pipeline library's file is named from the device identity, so a driver version
the adapter will not report still keeps the library on disk. The memory
profile is filled from fixture architecture, adapter and options 16 structures,
and each fixture adapter selects the residency policy it should. The root
layout planner turns the gate spike's two-group layouts into dense root
parameters: a view table per group, a second table for a group's samplers,
and the pushed index at `b0` in space 4. It refuses a description no backend
may plan. The root signature serialized from that plan, read back through the
runtime's deserializer, holds the same tables and no static sampler. Every
neutral pixel format, the sRGB and 10-bit formats a Vulkan swapchain may take
included, maps to its own `DXGI_FORMAT`.

The device laws run on a software (WARP) device without the debug layer and
skip when the host has none: the shader-visible heap pair, pools as ranges of
it, and the spike's root signatures created from their plans, with a group's
sampler table taken from its pool's range of the sampler heap. The debug-layer
tests create a device on the default adapter with the debug layer on and skip
when the host has no device or no debug layer. One deliberate violation must
reach the debug drain as a `[d3d12-debug]` line. A storage buffer left alive
when the context is disposed must be reported as a `[d3d12-debug] live` line
naming `ID3D12Resource`, and a teardown that leaks nothing must print no
`[d3d12-debug]` line at all. The spike's plan-built root signatures and a film
grain set create with no `[d3d12-debug]` line. These run alone, because
turning the debug layer on removes every device the process already holds.
Beyond that the suite says nothing about driver behavior; cross-backend
rendering is checked by `puck parity`.

## Verification

```powershell
dotnet test tests/Puck.DirectX.Tests/Puck.DirectX.Tests.csproj -c Release
```

## Documentation

📚 [Direct3D 12](../../docs/rendering/directx.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
