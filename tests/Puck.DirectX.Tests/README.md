# Puck.DirectX.Tests

This xUnit v3 suite checks the Direct3D 12 backend's managed decisions that do
not need a device. These include which barrier a buffer transition records,
and how the lazily created device context treats a creation the host cannot
satisfy: it reports `GpuDeviceUnavailableException`, and draining a device
that was never created neither creates it nor retries the failure. The
pipeline library's file is named from the device identity, so a driver version
the adapter will not report still keeps the library on disk. The memory
profile is filled from fixture architecture, adapter and options 16 structures,
and each fixture adapter selects the residency policy it should.

The debug-layer liveness tests are the only ones that create a Direct3D 12
device, with the debug layer on, and skip when the host has no device or no
debug layer. One deliberate violation must reach the debug drain as a
`[d3d12-debug]` line. A storage buffer left alive when the context is disposed
must be reported as a `[d3d12-debug] live` line naming `ID3D12Resource`, and a
teardown that leaks nothing must print no `[d3d12-debug]` line at all. Beyond
that the suite says nothing about driver behavior; cross-backend rendering is
checked by `puck parity`.

## Verification

```powershell
dotnet test tests/Puck.DirectX.Tests/Puck.DirectX.Tests.csproj -c Release
```

## Documentation

📚 [Direct3D 12](../../docs/rendering/directx.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
