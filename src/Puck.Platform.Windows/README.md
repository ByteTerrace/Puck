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

`Win32MediaFoundationCameraService.EnumerateDevices` reports every attached
physical camera as a `CameraDeviceInfo`; `Win32CameraDeviceGroups` is the one
`MediaFrameSourceGroup` scan every camera path resolves a device through — a
group is one physical camera, and its color/infrared source infos carry both
the display name and the `DeviceInformation.Id` symbolic links a specific
device is selected by. `TryOpenPixels`/`TryOpenShared` open one named
device's `ICameraGraph`. A single sensor opens through a Media Foundation
source reader (`Win32SourceReaderCameraGraph`), which resolves the requested
device id to its color/infrared symbolic link and passes it to
`MfInterop.ActivateDefaultVideoSource` so the right physical camera is
activated among several attached ones; the color + infrared pair opens
through the camera frame server's public Windows Face Authentication Profile
V2 graph for that same device
(`Win32FaceAuthenticationCapture` discovers and starts it,
`Win32FaceAuthenticationCameraGraph` polls it). Each shape has a CPU-pixel leaf
and a shared-texture leaf; the leaves differ only in reader configuration and
in where a frame goes (`Win32PixelStream`'s latest-frame buffer, or a
consumer-provisioned shared ring published through `Win32SharedStream`). CPU
frames are normalized from the negotiated signed stride into tightly packed,
top-down BGRA before publication. A shared-ring consumer explicitly acquires a
completed slot until its GPU submission retires; the camera drops an incoming
frame when every other slot remains acquired instead of overwriting a texture
still being sampled.

The attached-camera pixel delivery test is intentionally opt-in: set
`PUCK_WINDOWS_CAMERA_FRAME_TESTS=1` before running
`Win32MediaFoundationCameraServiceTests`. Its frame assertion remains a real
hardware gate; the default test lane skips it rather than depending on an
attached device and driver.

Every graph's native objects belong to one MTA worker thread
(`Win32CameraGraph`): construction blocks until the worker reports ready or
fails, disposal requests a stop and joins for a bounded interval, and a worker
that outlives the join keeps its objects until the driver call returns.
`Win32D3D11CameraFrameConverter` is the shared tier's compute kernel for the
native YUY2/NV12/L8 surfaces. Color conversion preserves the format's Media
Foundation transfer-matrix, nominal-range, and chroma-siting attributes;
unsupported metadata or a failed Direct3D resource/view creation refuses the
GPU tier so the coordinated pair reopens on CPU pixels.
`Win32D3D11VideoDevice` is the source reader's DXVA
device. Both shared-tier graphs are `ICameraKernelHost`s: `Win32ProbeKernelBench`
holds the attached `Win32D3D11ProbeKernel`s and runs them on the graph's worker,
on the graph's own device, right after the trigger frame converts. Each
attached kernel's declared `Sensor`/`StrobePair` sockets resolve through
`IProbeInputResolver` (the FaceAuth graph's converter output/previous view, or
the source reader's latest published slot); a `Ring` socket's shared targets
are opened once on the graph's device (`ID3D11Device1::OpenSharedResource1`,
the same pattern the converter uses for its own output ring) and its latest
slot is acquired and released around each run, running unbound (a null SRV,
its `boundMask` bit clear) on a cycle with nothing published yet. A kernel
compiles once every `Sensor`/`StrobePair` socket resolves; a `Ring`/`Unbound`
socket never blocks that. `Win32CameraControlSurface` maps the neutral control
vocabulary onto either a WinRT `VideoDeviceController` or the legacy
`IAMCameraControl`/`IAMVideoProcAmp` pair.

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
