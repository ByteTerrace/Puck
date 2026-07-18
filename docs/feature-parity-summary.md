# Backend parity at a glance

Puck implements its GPU contracts on Vulkan and Direct3D 12 through the
backend-neutral interfaces in `Puck.Abstractions`. The engine's supported
rendering path is functionally equivalent on both backends; API-specific
differences are documented as design constraints rather than portability gaps.

See [feature-parity-table.md](feature-parity-table.md) for implementation and
verification details.

## Supported on both backends

| Capability | Vulkan | Direct3D 12 |
|---|:---:|:---:|
| Device, adapter, swapchain, and windowed presentation | ✅ | ✅ |
| Runtime backend switching | ✅ | ✅ |
| Graphics and compute pipelines | ✅ | ✅ |
| Explicit synchronization and image-layout transitions | ✅ | ✅ |
| Direct and indirect compute dispatch | ✅ | ✅ |
| Sampled and storage image bindings | ✅ | ✅ |
| Inline ray queries and per-frame acceleration structures | ✅ | ✅ |
| Per-pass GPU timestamp measurements | ✅ | ✅ |
| Same-device SDF world rendering | ✅ | ✅ |
| Compositor window/monitor capture (Vulkan CPU upload; Direct3D 12 shared-texture zero-copy) | ✅ | ✅ |
| Cross-API shared-resource primitives | ✅ | ✅ |

Cross-API resources are Direct3D 12-owned because a Direct3D 12 shared NT
handle can be opened by both APIs. The `camera-share` Post stage verifies
Direct3D 12-produced content imported by Vulkan; `reverse-share` verifies
Vulkan writing into a Direct3D 12-owned resource that Direct3D 12 reads back.
The run-document `world` graph currently renders on its host backend and
rejects a mismatched `graph.produce` value during preflight.

Window and whole-monitor capture are platform-specific at acquisition (Windows
Graphics Capture, Windows 10 2004 / build 19041 or newer; non-Windows hosts
report it unsupported), but the transport to the host is backend-specific. The
Vulkan host uploads the fixed-size CPU BGRA frames through the neutral surface
source; the Direct3D 12 host instead provisions round-robin shared
simultaneous-access textures WGC copies each frame into (same-adapter
Direct3D 11→Direct3D 12) and samples them directly with no CPU round-trip,
keeping the CPU readback at a divided cadence for the glow tap. A monitor target
uses the same feed through `CreateForMonitor`, addressed by a primary-first
0-based index. The `capture-share` Post stage verifies the Direct3D 12
shared-texture transport end to end.

## API-specific design constraints

- WARP is a Direct3D 12 software-adapter facility; Vulkan has no equivalent.
- Direct3D 12 uses static samplers in root signatures rather than dynamic
  sampler objects.
- Direct3D 12 presentation is Win32-only. Vulkan also supports the engine's
  Wayland, XCB, and Nintendo VI surface bindings.
- Vulkan `OPAQUE_WIN32` exports are not Direct3D 12-openable, so shared
  resources used across the APIs are Direct3D 12-owned.

## Shared limitations

Neither backend currently provides these engine features:

- Indirect draw. Indirect compute dispatch is supported.
- Async compute, multiple queues, or timeline synchronization.
- Depth or stencil attachments. The SDF renderer does not require them.
- A pooled device-memory allocator; resources use individual allocations.
