# Puck.Platform.Linux

The Linux concrete backends behind `Puck.Platform`'s contracts.

## Contents

- **Windowing**—`WaylandNativeWindow`/`XcbNativeWindow` (+ their
  `INativeWindowBackend` wrappers), over `Interop/Libc.cs`,
  `Interop/WaylandClient.cs`, `Interop/Xcb.cs`. `AddLinuxPlatformWindowing`
  also registers `Puck.Platform.NullClipboardService`—there is no Linux
  clipboard backend. The only input either window reads besides XCB's keyboard
  and pointer is touch: the first `wl_seat`'s `wl_touch` on Wayland, and XInput
  2.2 touch events on X11 (`XcbNativeWindow.Touch`, through `libxcb-xinput`,
  which a system without it simply lacks), both into
  `WindowInputQueue.EnqueueContact`. Neither has met a touchscreen yet.
- **Precise waiting**—`LinuxPrecisionWaiter`, a `clock_nanosleep` on
  `CLOCK_MONOTONIC` behind `Puck.Abstractions.Pacing.IPrecisionWaiter`
  (`AddLinuxPrecisionWaiter`), which the host loop's pacer sleeps on.
- **Declining recording**—`AddLinuxRecordingPlatform` registers
  `Puck.Platform.Recording`'s `DecliningVideoEncoderFactory`/
  `DecliningAudioCaptureSourceFactory`: no Linux video-encoder or
  audio-capture backend exists yet, so the recording graph resolves an
  honest decline reason instead of a missing service.

## What is absent

No camera-capture, desktop-capture, or audio-render backend.
`AddLinuxCameraCapture` registers `Puck.Platform.NullCameraCaptureService`/
`NullNativeImageCaptureService` and the probes' `NullProbeKernelHostService`; no `IAudioRenderDeviceFactory` is registered
at all (`WorldAudioRenderService` already treats an unresolved factory as
"no render backend" and parks as `unsupported`).

## Verification

Compiles and is exercised by `dotnet build src/Puck.Platform.Linux -c Release`
(no Windows dependency in its closure—enforced by the
`Puck.Platform.Linux` lane profile in `build/Architecture.props`) and by a
WSL2 build of the whole solution—see `src/Puck.Launcher.Linux/README.md`
for the exact commands and what a WSL2 build does and does not prove.
Windowed Wayland/Xcb behavior is verified only on real Linux hardware
(Steam Deck/Steam Machine); nothing here has been run there.

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
