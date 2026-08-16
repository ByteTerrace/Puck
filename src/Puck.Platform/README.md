# Puck.Platform

The OS-neutral half of native windowing and platform capture: contracts only,
plus the pieces that need no OS-specific code at all.

## What lives here

- **Windowing contracts** — `INativeWindow`, `INativeWindowFactory`,
  `INativeWindowBackend` (the seam `Puck.Platform.Windows`/`.Linux` register
  their concrete window backends against), `IClipboardService`,
  `NativeDisplayEnvironment`/`NativeDisplayKindSelector` (pure environment-variable
  detection), `ConfiguredNativeWindow` (the headless stand-in),
  `NativeWindowOptionsValidator`.
- **Capture contracts** — `ICameraCaptureService`, `INativeImageCaptureService`,
  `IAudioRenderDeviceFactory`/`IAudioRenderDevice`.
- **`Puck.Memory`** — the unmanaged allocator (mimalloc-backed, with a
  tracking wrapper and a plain native fallback), registered via
  `AddPuckAllocator`.
- **Null fallbacks** — `NullClipboardService`, `NullCameraCaptureService`,
  `NullNativeImageCaptureService`, used by `Puck.Platform.Linux`'s
  registration (and by anything that never calls a platform-specific
  registration method at all).

## What does not live here

Every concrete platform backend. `PlatformWindowingServiceRegistration.AddPlatformWindowing`
registers only the neutral pieces above (display probe, platform support, the
window factory); it registers no `IClipboardService` and no window backend.
A composition root pairs it with exactly one of:

- `Puck.Platform.Windows.WindowsPlatformServiceRegistration.AddWindowsPlatformWindowing`
- `Puck.Platform.Linux.LinuxPlatformServiceRegistration.AddLinuxPlatformWindowing`

Camera capture, recording, and audio-render registration follow the same
shape (`AddWindowsCameraCapture`/`AddLinuxCameraCapture`, etc.) — this
project holds no `AddCameraCapture`/`AddRecordingPlatform` of its own.

`Puck.World`'s `WorldBootComposition`/`Program.cs` show the composition-root
side of this: one `OperatingSystem.IsWindows()` branch per seam, picking
which platform package's method to call.
