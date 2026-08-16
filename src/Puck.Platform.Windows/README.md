# Puck.Platform.Windows

The Windows concrete backends behind `Puck.Platform`'s contracts.

## Contents

- **Windowing** — `Win32NativeWindow` (+ its `INativeWindowBackend` wrapper
  `Win32NativeWindowBackend`), `Win32ClipboardService`, VRR/EDID handling.
- **Camera/capture** — `Win32MediaFoundationCameraService` (the webcam,
  Media Foundation), `Win32NativeImageCaptureService` (Windows Graphics
  Capture desktop/window feeds).
- **Recording** — the Media Foundation hardware video-encoder ladder
  (AV1→H.264) and WASAPI loopback/microphone audio capture sources.
- **Audio render** — the WASAPI render-device factory.
- **Input** — HID device enumeration (`Win32HidDeviceSource`) and Xbox
  acquisition over XInput/GameInput (`Win32XboxAcquisitionSource`);
  `WindowsInputTransports.CreateGamepadManager` wires both into one
  `GamepadManager`.
- **`Win32PrecisionWaiter`** — a standalone high-resolution waitable timer
  for the headless tick host's pacing loop.
- Generated CsWin32 native interop (`NativeMethods.json`/`.txt`) and the
  WinRT/`Microsoft.Windows.SDK.NET` projection the Windows Graphics Capture
  path needs.

## `WindowsPlatformServiceRegistration`

One static class, one method per seam:
`AddWindowsPlatformWindowing`/`AddWindowsCameraCapture`/
`AddWindowsRecordingPlatform`/`AddWindowsAudioRender`/`AddWindowsPrecisionWaiter`.
Every method that constructs a `[SupportedOSPlatform("windows")]`-annotated
type carries the same attribute, so CA1416 requires the CALLER to guard with
`OperatingSystem.IsWindows()` — that guard is the composition-time platform
choice; it never lives inside this project.

## Build

Stays `net10.0` (not `net10.0-windows...`) — the same neutral TFM
`Puck.Platform` uses, so `[SupportedOSPlatform]` surface annotations keep
firing under CA1416 exactly as they do in `Puck.DirectX`. Referencing this
project on a non-Windows build is legal (it is plain net10.0 IL); running the
Windows-only code paths it contains is not.

## `WindowsCaptureProjection.targets`

Imported by the repository-root `Directory.Build.targets` for every project.
A raw `<Reference>` (this project's WinRT/CsWin32 block) is copied through
`ProjectReference` but never recorded in a downstream executable's
`.deps.json`; this file re-declares `Microsoft.Windows.SDK.NET.dll`/
`WinRT.Runtime.dll` at every entry point that references
`Puck.Platform.Windows`, on Windows hosts only, so the .NET host resolves the
projection before the first Windows Graphics Capture type loads.
