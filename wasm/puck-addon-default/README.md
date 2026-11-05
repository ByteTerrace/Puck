# puck-addon-default

Puck's default addon—the dead-reckoning clamp-walk ghost that ships with the
engine, and the worked example for authoring your own addon against
[`puck-stdlib`](../puck-stdlib). The addon ABI contract lives in
[deterministic WASM scripting](../../docs/reference/scripting.md);
[`../README.md`](../README.md) covers the build/test workflow and walks this
crate's behaviour end to end.

```text
crate-type  cdylib
target      wasm32-unknown-unknown
edition     2021
deps        puck-stdlib (path dependency) — nothing else
```

`src/lib.rs` is short by design: a `puck_stdlib::channels!` table declaring the
three channel names this ghost emits against (`forward`/`strafe` for its
movement, `jump` for the one latched press at its target), nine one-line
`#[no_mangle]` shims delegating into `puck_stdlib::abi` and that table, then
the tick behaviour itself (`on_tick`, plus the no-op `on_init`). The behaviour
demonstrates the whole authority flow deliberately: consume the host's
`GrantedBody` disclosure for a Drive handle, `ask` for Observe over the
learned body, dead-reckon off `query_pose` answers, and emit nothing at all
when no pose is held—a refused grant leaves the ghost standing, not
guessing. Copy this crate's `Cargo.toml` and `src/lib.rs` as your own addon's
starting point, then declare your own channel names and replace `on_tick`'s
body—using `puck_stdlib::Inputs` to drain the host's batch and
`puck_stdlib::Outputs`'s `act_bipolar`/`act_binary`/`act_unipolar` methods
against your declared channel handles, never a raw byte offset.

The loader derives the logical module path
`wasm/puck-addon-default/dist/puck-addon-default.wasm` from the variant in
`wasm/builds.json`, using its counted source cache. `puck wasm build` exports
an ignored copy for inspection; no module bytes are committed. See the
[workspace build workflow](../README.md#build-the-addons).

This guest is freestanding (`no_std`), with a trapping panic handler. Its shared
library needs only `core`; linking the hostless Rust standard library would add
tables and segment state the World's checkpoint serializer currently refuses.
## Documentation

📚 [WebAssembly addons](../README.md) · 🛠️ [Development](../../docs/development/README.md)
