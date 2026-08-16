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

The `DirectXGpu*` types implement the neutral GPU contracts from
`Puck.Abstractions`: compute pipelines, descriptor allocation, storage
buffers and images, shared-surface export, DXR acceleration structures, queue
submission, and timestamp pools. Swapchains and the frame loop live in
`Puck.DirectX.Presentation`, mirroring the split between `Puck.Vulkan` and
`Puck.Vulkan.Presentation`.

## ✨ Key features

- *Source-generated bindings, not hand-written P/Invoke:* CsWin32 emits the
  raw COM surface from `NativeMethods.txt`, `unsafe` and pointer/vtable-shaped
  in the same style as the rest of the codebase's interop.
- *No runtime COM marshaling:* `NativeMethods.json` sets `allowMarshaling:
  false`, so COM interfaces come through as `unsafe` structs with
  function-pointer vtables — no marshaling ceremony, no GC pressure.
- *A WARP fallback for headless verification:* software rendering is always
  available through `IDirectXDeviceApi.CreateWarpDevice`, so CI and
  no-GPU machines can still exercise the device path.
- *One failure shape:* every native call funnels through
  `HResultExtensions.ThrowIfFailed`, so a failing `HRESULT` always becomes a
  `DirectXException` carrying the operation name and the code.

---

## 📐 Structure

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

## 🚀 Quick start

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

## 🎛️ Capabilities

| Concern | Interface | Native call(s) | Result |
|---------|-----------|----------------|--------|
| Adapter enumeration | `IDirectXAdapterApi` | `CreateDXGIFactory2`, `IDXGIFactory4::EnumAdapters1` | `IReadOnlyList<DirectXAdapterDescription>` |
| Feature-level probe | `IDirectXDeviceApi` | `D3D12CreateDevice` (null device) | `DirectXFeatureLevel?` |
| Device creation | `IDirectXDeviceApi` | `D3D12CreateDevice` | `DirectXDevice` (owns `ID3D12Device`) |
| Software fallback | `IDirectXDeviceApi` | `IDXGIFactory4::EnumWarpAdapter` + `D3D12CreateDevice` | `DirectXDevice` (WARP) |

---

## ⚠️ Result handling

Native calls return `HRESULT`. The internal `HResultExtensions.ThrowIfFailed(operation)`
turns a failing code into a `DirectXException` carrying the operation name and the
`HRESULT`, matching `Puck.Vulkan`'s `VulkanException` pattern. (`EnumWarpAdapter` has no
non-throwing overload and surfaces the framework's COM exception directly — it effectively
never fails.)

---

## Constraints and invariants

- **Windows-only by construction.** Every type that touches Win32 is
  `[SupportedOSPlatform("windows8.1")]`; consumers on a platform-neutral target framework
  will (correctly) get `CA1416` until they guard or annotate.
- **Don't hand-write P/Invoke here.** To reach a new API, add its name to
  `NativeMethods.txt` and let CsWin32 generate it; then wrap it behind an `IDirectX*Api`.
- **COM lifetime is manual.** Every `IDXGIxxx`/`ID3D12xxx` pointer obtained must be
  `Release`d. The APIs use `try/finally` around transient factories and adapters; persistent
  objects are owned by an `IDisposable` `Interop` wrapper.

## 📋 Core types

| Type | Role |
|------|------|
| `IDirectXAdapterApi` / `DirectXNativeAdapterApi` | Adapter enumeration. |
| `IDirectXDeviceApi` / `DirectXNativeDeviceApi` | Feature-level probing, device creation, and the WARP software fallback. |
| `DirectXDevice` | The `IDisposable` handle owner for a created `ID3D12Device`. |
| `DirectXAdapterDescription` | A `readonly record struct` projection of one enumerated adapter's native data. |
| `DirectXFeatureLevel` | A managed mirror of `D3D_FEATURE_LEVEL`. |
| `DirectXException` / `HResultExtensions` | The failing-`HRESULT`-to-exception seam every native call funnels through. |
| `DirectXGpu*` (`Apis`/`Factories`/`Interop`) | The `Puck.Abstractions` GPU-contract implementations: compute pipelines, descriptor allocation, storage buffers/images, shared-surface export, DXR acceleration structures, queue submission, timestamp pools. |

## 🧪 Verification

There is no dedicated `Puck.DirectX.Tests` project; the backend is verified by
running the engine on Direct3D 12 and by `puck parity`, which boots the real
windowed `Puck.World` on both backends and compares the same fenced composed
frame under the relaxed envelope:

```powershell
dotnet build Puck.slnx -c Release
dotnet src/Puck.Cli/publish/Puck.Cli.dll parity
```

## 📦 Packaging

`ByteTerrace.Puck.DirectX` depends on `Puck.Abstractions` (the neutral GPU
contracts it implements) and, build-only, `Microsoft.Windows.CsWin32`
(`PrivateAssets="all"` — a source generator, never a runtime dependency of a
consumer). `Puck.DirectX.Presentation` and `Puck.World`/`Puck.Launcher.Windows`
depend on it for the Direct3D 12 backend; presentation, windowing, and shader
compilation live upstream in `Puck.DirectX.Presentation`.
