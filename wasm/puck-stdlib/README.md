# puck-stdlib

Puck's addon WASM standard library — an `rlib`, not a `cdylib`. The addon ABI
contract itself lives in
[`../../src/Puck.Scripting/README.md`](../../src/Puck.Scripting/README.md),
beside the constants that define it; [`../README.md`](../README.md) covers the
guest-authoring workflow (build/test commands, generated sources, the hash-pin
refresh) and the spec-pinned vs algorithm-pinned split this crate's `fixed`
module follows. This file is a short map of what lives where.

| File | Role |
|------|------|
| `src/lib.rs` | The typed `Inputs`/`Outputs` surface over the ABI's two 32-byte cell rings: `Inputs` iterates the host-written batch (`Tick`/`Answer`/`Observation` cells), `Outputs`'s `act_bipolar`/`act_binary`/`act_unipolar`/`ask`/`query_pose` methods emit through a host-minted `Handle` and a `channels!`-declared `ChannelHandle`, each returning the cell's ordinal for next-tick answer correlation. |
| `src/channels.rs` | The `channels!` macro and its `ChannelHandle`/`Bipolar`/`Binary`/`Unipolar` types — declares the `Input` channel's verb table (the packed, length-prefixed channel-name table) and hands back one typed, compile-time-checked handle per declared channel. |
| `src/abi.rs` | The static output/input rings and channel descriptor table, the raw pointer/capacity/version accessors, and `dispatch_tick` — the callable entry helper a consuming `cdylib`'s `puck_on_tick` shim delegates into. Declares no `#[no_mangle]` exports of its own; see its module doc for why. |
| `src/abi_generated.rs` | GENERATED — the wire enums (`OutCellKind`, `InCellKind`, `ChannelKind`, `SubjectKind`, `Verdict` with `is_allowed`), the `CAP_*` capability mask values, and every `AddonAbi` layout constant (cell sizes, field offsets, caps, verb ordinals), read from the live `Puck.Scripting` types by name. `lib.rs`/`abi.rs`/`channels.rs` index and re-export through this module rather than hard-coding any of it. Same regenerate verb as `fixed_generated.rs` below; never hand-edit. |
| `src/fixed.rs` | Bit-exact `FixedQ4816` mirror. `add`/`sub`/`neg`/`cmp`/`clamp`/`mul`/`div`/`sqrt` are implemented directly; `atan2`/`sin`/`cos`/`exp2`/`log2`/`pow` are re-exports of `fixed_generated.rs`. |
| `src/fixed_generated.rs` | GENERATED — the Rust port of `atan2`/`sin`/`cos`/`exp2`/`log2`/`pow` plus their tables and polynomial coefficients, read from the live `FixedQ4816` type. Regenerate with `dotnet run --project src/Puck.Cli -c Release -- wasm-stdlib`; never hand-edit. |
| `src/fixed_vectors.rs` | GENERATED — 12,000 known-answer `cargo test` vectors for `fixed_generated.rs`'s six functions, computed by calling the real `FixedQ4816` at generation time. Same regenerate verb as above; never hand-edit. |
| `src/fixed_tests.rs` | Hand-written known-answer `cargo test` vectors for `fixed.rs`'s hand-written functions, including the round-half-to-even tie cases for `mul`/`div` and the floor-boundary cases for `sqrt`. |

Depend on this crate from your own addon with a path dependency:

```toml
[dependencies]
puck-stdlib = { path = "../puck-stdlib" }
```

then write your addon's `#[no_mangle]` export shims and tick body the way
[`../puck-addon-default`](../puck-addon-default) does.
