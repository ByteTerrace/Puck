# puck-addon-channelwalk

A Rust verification guest for capability-channel composition, declared-name
bounds, unresolved-name attenuation and finite contribution windows. The source
is retained; build outputs are counted caches.

The variants are declared in `wasm/builds.json`, with their Cargo features in
`Cargo.toml`:

- `main` contributes `forward` for a finite window, holds `strafe`, presses the
  Binary `trigger`, and exercises an unresolved `walkonly` channel.
- `bound64` declares `AddonAbi.MaxChannelNames` names.
- `bound65` exceeds that bound and is a `BadExport` handshake fixture.

The addon loader derives these logical modules from their sources through
`WasmGuestBuild` and `DerivedBytes`. `puck wasm build` exports ignored copies
for inspection. Authored `.puck` addon rows use `hash: auto`; no verification
world records a pin for generated repository bytes.

These fixtures still link Rust's standard library. They compile and load in
`Puck.Scripting`, but their tables and segment state are outside the current
World checkpoint contract. The [refactoring packet](../../docs/plans/open-items.md#refactoring-campaign)
owns generic checkpoint support and capability-boundary scenarios in the debug
world. The default guest is a freestanding worked example that mounts there.

The [channel authority contract](../../.claude/skills/puck-world/references/authority.md)
owns trusted contributions and pooled ceilings. The
[scripting reference](../../docs/reference/scripting.md) owns channel shapes,
name resolution, attenuation and the ABI. `src/lib.rs` owns this fixture's
channels and timing.

## Documentation

📚 [WebAssembly addons](../README.md) · 🛠️ [Development](../../docs/development/README.md)