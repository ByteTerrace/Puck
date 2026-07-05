# Puck.DirectX

The low-level Direct3D 12 (DXGI + D3D12) backend for the Puck engine. Where
`Puck.Vulkan` hand-binds a flat C loader, DirectX is COM-based, so this project leans on
**[Microsoft.Windows.CsWin32](https://github.com/microsoft/CsWin32)** — Microsoft's actively
maintained, AOT-friendly P/Invoke + COM source generator — to emit the raw bindings. That
fits Puck's existing spirit: the engine already uses source-generated interop
(`[LibraryImport]` in `Puck.Platform`), and CsWin32 generates `unsafe`, pointer-and-vtable,
zero-marshaling code in the same shape as the rest of the codebase.

```text
namespaces  Puck.DirectX (+ .Interfaces, .Apis, .Interop, .Messages)
target      net10.0  (Windows-only at runtime; surface annotated [SupportedOSPlatform("windows8.1")])
deps        Microsoft.Windows.CsWin32 (build-only, PrivateAssets=all — no runtime dependency)
```

> What began as a binding-strategy proof (enumerate GPUs, probe Direct3D 12 capability,
> create a device) has since grown into the full low-level D3D12 backend: the `DirectXGpu*`
> types implement the neutral GPU seams from `Puck.Abstractions` (compute pipelines,
> descriptor allocation, storage buffers/images, shared-surface export, acceleration
> structures for DXR, queue submission, timestamp pools). Swap chains and the frame loop
> live one level up, in `Puck.DirectX.Presentation` — mirroring how `Puck.Vulkan` /
> `Puck.Vulkan.Presentation` split.

---

## How it's organized

The layering mirrors `Puck.Vulkan` so the two backends read the same way:

| Folder | Prefix | What lives here |
|--------|--------|-----------------|
| `Messages/` | `DirectX*` | `readonly record struct` projections of native data (`DirectXAdapterDescription`). |
| `Interop/` | `DirectX*`, `Dxgi*` | The `IDisposable` handle owner (`DirectXDevice`) and shared low-level helpers (`DxgiInterop`, `HResultExtensions`). |
| `Interfaces/` | `IDirectX*Api` | The contracts — the dependency-injection / mocking seam. |
| `Apis/` | `DirectXNative*Api` | Thin implementations that marshal to the generated DXGI / D3D12 entry points. |

Top-level helpers: `DirectXException` (carries the failing operation + `HRESULT`) and
`DirectXFeatureLevel` (a managed mirror of `D3D_FEATURE_LEVEL`).

### The CsWin32 surface

`NativeMethods.txt` lists exactly the APIs to generate; `NativeMethods.json` sets
`allowMarshaling: false` so COM interfaces come through as `unsafe` structs with
function-pointer vtables (no runtime COM marshaling, no GC ceremony). The generated
`Windows.Win32.*` types are `internal` to this assembly — only Puck types cross the public
boundary.

---

## Capabilities

| Concern | Interface | Native call(s) | Result |
|---------|-----------|----------------|--------|
| Adapter enumeration | `IDirectXAdapterApi` | `CreateDXGIFactory2`, `IDXGIFactory4::EnumAdapters1` | `IReadOnlyList<DirectXAdapterDescription>` |
| Feature-level probe | `IDirectXDeviceApi` | `D3D12CreateDevice` (null device) | `DirectXFeatureLevel?` |
| Device creation | `IDirectXDeviceApi` | `D3D12CreateDevice` | `DirectXDevice` (owns `ID3D12Device`) |
| Software fallback | `IDirectXDeviceApi` | `IDXGIFactory4::EnumWarpAdapter` + `D3D12CreateDevice` | `DirectXDevice` (WARP) |

```csharp
using Puck.DirectX;
using Puck.DirectX.Apis;

var adapterApi = new DirectXNativeAdapterApi();
var deviceApi = new DirectXNativeDeviceApi();

foreach (var adapter in adapterApi.EnumerateAdapters()) {
    var maxLevel = deviceApi.ProbeMaxFeatureLevel(adapterLuid: adapter.AdapterLuid);
    // adapter.Description, adapter.DedicatedVideoMemory, adapter.IsSoftware, maxLevel ...
}

// WARP is always available — handy for headless/CI verification with no GPU.
using var device = deviceApi.CreateWarpDevice(minimumFeatureLevel: DirectXFeatureLevel.Level110);
```

`DirectXDevice` owns its `ID3D12Device` and releases it exactly once on `Dispose` — dispose
it like any other Puck handle owner.

---

## Result handling

Native calls return `HRESULT`. The internal `HResultExtensions.ThrowIfFailed(operation)`
turns a failing code into a `DirectXException` carrying the operation name and the
`HRESULT`, matching `Puck.Vulkan`'s `VulkanException` pattern. (`EnumWarpAdapter` has no
non-throwing overload and surfaces the framework's COM exception directly — it effectively
never fails.)

---

## Notes for agents

- **Windows-only by construction.** Every type that touches Win32 is
  `[SupportedOSPlatform("windows8.1")]`; consumers on a platform-neutral target framework
  will (correctly) get `CA1416` until they guard or annotate.
- **Don't hand-write P/Invoke here.** To reach a new API, add its name to
  `NativeMethods.txt` and let CsWin32 generate it; then wrap it behind an `IDirectX*Api`.
- **COM lifetime is manual.** Every `IDXGIxxx`/`ID3D12xxx` pointer obtained must be
  `Release`d. The APIs use `try/finally` around transient factories and adapters; persistent
  objects are owned by an `IDisposable` `Interop` wrapper.
- **Dependencies.** This project references only `Puck.Abstractions` (the neutral GPU
  seams it implements). Presentation, windowing, and shader compilation live upstream in
  `Puck.DirectX.Presentation`.
