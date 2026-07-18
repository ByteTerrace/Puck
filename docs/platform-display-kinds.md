# Platform display kinds

Puck separates **surfaces** (the Vulkan `VkSurfaceKHR` and its `NativeSurfaceBinding`) from
**windows** (`INativeWindow` and its native backends). A window produces a
`NativeSurfaceBinding` via `INativeWindow.CreateSurfaceBinding()`, and
`VulkanSurfaceFactory` turns that binding into a `VkSurfaceKHR`. Both layers dispatch on
`NativeDisplayKind`.

## Supported kinds

| `NativeDisplayKind` | Instance extension        | Surface call             | Window backend            | Notes |
|---------------------|---------------------------|--------------------------|---------------------------|-------|
| `Win32`             | `VK_KHR_win32_surface`    | `vkCreateWin32SurfaceKHR`| `Win32NativeWindow`       | Windows. |
| `Wayland`           | `VK_KHR_wayland_surface`  | `vkCreateWaylandSurfaceKHR` | `WaylandNativeWindow`  | Native Steam Deck / Gamescope path. |
| `Xcb`               | `VK_KHR_xcb_surface`      | `vkCreateXcbSurfaceKHR`  | `XcbNativeWindow`         | X11 desktop sessions; runs under XWayland on the Deck. |
| `Vi`                | `VK_NN_vi_surface`        | `vkCreateViSurfaceNN`    | `ViNativeWindow` (seam)   | Nintendo Switch. |
| `Headless`          | —                         | —                        | `ConfiguredNativeWindow`  | Offscreen; no surface. |

`Xlib` is intentionally not implemented — `Xcb` is the modern X11 choice.

## Selecting a kind

`NativeDisplayKindSelector` auto-detects the kind from the environment (`Win32` on Windows;
`Wayland` when `WAYLAND_DISPLAY`/`XDG_SESSION_TYPE=wayland`, otherwise `Xcb`, on Unix).
Override it with `NativeWindowOptions.DisplayKind` (default `Auto`):

- Linux: pin `Xcb` or `Wayland` regardless of the session.
- Switch: set `Vi` explicitly — there is no environment-based detection for it.

`INativeWindowPlatformSupport.ResolveDisplayKind`/`SupportsWindowFor` apply this override;
`NativeWindowFactory` instantiates the matching backend.

## Nintendo Switch (VI) seam

`nn::vi`/NVN live behind Nintendo's NDA SDK, so the Switch **window** is a seam:
`ISwitchViWindowBackend` (open source) is implemented by a P/Invoke shim over the licensed
SDK that ships only in the licensed Switch build and registers itself in DI. With no backend
registered, `NativeWindowFactory` throws a clear `PlatformNotSupportedException`. The Switch
**surface** path (`vkCreateViSurfaceNN`, `ViNativeSurfaceBinding`) is fully implemented and
compiles everywhere.

## Verification

- **All platforms:** `dotnet build Puck.slnx -c Release` validates the managed
  contracts and platform-specific compilation under warnings-as-errors.
- **Windows:** run the engine or demo with the default `Win32` display kind.
  The Linux and Switch paths are not exercised by this run.
- **Linux / Steam Deck:** run with `NativeWindowOptions.DisplayKind = Xcb`, then `Wayland`,
  through the `WindowProbeRunner` smoke loop (show → poll → first-paint autoclose). The
  Wayland backend's hand-built xdg-shell glue and event dispatch are expected to need
  on-device iteration.
- **Switch:** confirmed only in the licensed build; the open-source build reports the missing
  shim for `Vi`.
