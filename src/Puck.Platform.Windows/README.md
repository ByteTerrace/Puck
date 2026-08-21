# Puck.Platform.Windows

The Windows concrete backends behind `Puck.Platform`'s contracts.

## Contents

- **Windowing** — `Win32NativeWindow` (+ its `INativeWindowBackend` wrapper
  `Win32NativeWindowBackend`), `Win32ClipboardService`, VRR/EDID handling.
- **Camera/capture** — `Win32MediaFoundationCameraService` (the webcam,
  Media Foundation plus the camera frame server), `Win32NativeImageCaptureService`
  (Windows Graphics Capture desktop/window feeds).
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

## Camera capture

Camera capture has two independent axes. A single sensor uses a Media
Foundation source reader; coordinated color and infrared use one camera frame
server graph selected from the device's public Windows Face Authentication
Profile V2. Each graph can publish CPU pixels or, when the device and render
adapter admit it, shared GPU textures. GPU refusal is atomic for a dual graph,
so both sensors fall back together to the CPU-pixel graph.

`Win32FaceAuthenticationCapture` is the single owner of dual-device discovery,
profile selection, native format metadata, reader startup, and shutdown. The
CPU and GPU dual cores differ only after native acquisition: one publishes
host buffers and the other converts the native Direct3D surfaces into shared
RGBA rings. Capture workers retain their native objects when a bounded join
times out; the worker releases them after the driver call returns, avoiding a
cross-thread dispose race during device loss.

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
