---
type: llm
weight: 1
---

The response keeps the neutral project free of OS code and puts the OS choice in the composition root.

PASS if it says `Puck.Platform` holds only OS-neutral contracts and pieces, the V4L2 backend belongs in `Puck.Platform.Linux` behind the camera contract and its Linux registration method (replacing the null camera service it registers today), and the one OS branch that picks a platform lives in the composition root that calls the registration methods. Example: "Implement `ICameraCaptureService` in `Puck.Platform.Linux`, register it from `AddLinuxCameraCapture` instead of `NullCameraCaptureService`; `WorldBootComposition` already branches on the OS. Leave `CameraDeviceScanner` OS-neutral."

FAIL if it keeps the OS branch inside `Puck.Platform` or the scanner, or puts the backend in the neutral project. Example: "The `IsLinux` check in `CameraDeviceScanner` is fine; add the V4L2 calls next to it."
