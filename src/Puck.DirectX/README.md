# Puck.DirectX

Puck.DirectX provides the low-level Direct3D 12 graphics backend substrate:
CsWin32 COM bindings, resource-barrier tracking, and WARP software fallback.

`DirectXGpuTimestampFactory` provides optional pass timestamp heaps. Queue
frequency and heap creation use `DirectXCommandCalls` so device removal follows
the same neutral recovery path as other resources. Pools are named and released
through their owning frame slots.

## Documentation

- [Direct3D 12 backend substrate](https://github.com/ByteTerrace/Puck/blob/main/docs/rendering/directx.md) — CsWin32 interop, device APIs, and neutral GPU contract implementations.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.Parity/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
