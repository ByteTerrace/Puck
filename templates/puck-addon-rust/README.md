# puck-addon-rust

**A Rust starting point for a `puck.addon.v1` WASM addon.** Puck's scripting host
(`Puck.Scripting`) loads a `.wasm`/`.wat` module named in a run document's `addons` list,
instantiates it once, and drives it once per sim tick: it writes a 40-byte fixed-point
snapshot into the module's linear memory, calls the module's nullary `puck_on_tick`, and
reads back whatever virtual-pad command records the module wrote. This template gives you
the plumbing (`abi.rs`, `fixed.rs`) so you only ever write the body of `on_tick` in `lib.rs`,
using typed accessors — never a raw byte offset.

```text
crate-type  cdylib
target      wasm32-unknown-unknown
edition     2021
deps        none — core/std only
```

---

## At a glance

| File | Role |
|------|------|
| `Cargo.toml` | `cdylib`; release profile `opt-level = "z"`, `lto = true`, `panic = "abort"`. |
| `.cargo/config.toml` | Pins the default build target to `wasm32-unknown-unknown`. |
| `src/abi.rs` | The frozen `#[no_mangle]` exports over two static byte buffers. Do not edit for a normal addon. |
| `src/fixed.rs` | Bit-exact `FixedQ4816` mirror — `add`/`sub`/`neg`/`cmp`/`clamp`/`mul` implemented; `div`/`sqrt`/`atan2` are documented opt-in stubs. |
| `src/fixed_tests.rs` | Known-answer `cargo test` vectors for `fixed.rs`, including the round-half-to-even tie cases. |
| `src/lib.rs` | The typed `Snapshot`/`Commands` surface, the `pad` ID table, and the example addon body (the ghost's clamp-walk). |
| `build.ps1` / `build.sh` | One-line release build wrappers. |

---

## Build it

```powershell
# Windows
.\build.ps1
```

```sh
# macOS / Linux
./build.sh
```

Both scripts are a thin wrapper over:

```sh
cargo build --release
```

(`.cargo/config.toml` already pins the default target to `wasm32-unknown-unknown`, so no
`--target` flag is needed.) The compiled module lands at:

```text
target/wasm32-unknown-unknown/release/puck_addon.wasm
```

Rename the crate (the `[package].name` in `Cargo.toml`) to your addon's own name before
shipping — the output file name follows the crate name.

### Running the unit tests

`cargo test` alone will try to run the test binary *as wasm* (because of the pinned default
target above) and fail with something like `%1 is not a valid Win32 application` / `cannot
execute binary file` — there is no default runner for `wasm32-unknown-unknown`. Override the
target explicitly to run tests on your own machine instead:

```sh
cargo test --target <your-host-triple>
```

Find your host triple with `rustc -vV` (look for the `host:` line — e.g.
`x86_64-pc-windows-msvc`, `x86_64-unknown-linux-gnu`, `aarch64-apple-darwin`).

---

## Drop it into a run document

Point a document's `addons` entry at the built `.wasm` file:

```json
{
  "addons": [
    { "name": "ghost", "modulePath": "Assets/Addons/my-addon.wasm", "slot": 1, "fuelPerTick": 250000 }
  ]
}
```

See [`docs/examples/overworld-autopilot.json`](../../docs/examples/overworld-autopilot.json)
for a complete run document that boots the built-in autopilot ghost the same way (that one
loads a hand-authored `.wat` fixture rather than this template's `.wasm` output, but the
`addons` entry shape is identical). `modulePath` is resolved the same way a cartridge's
`romPath` is — a plain path, read through the host's asset source — and an optional
`moduleHash` field (`sha256-64/{16 hex}`) pins the module's content hash as an integrity
check.

A malformed or missing module never crashes the run: the host logs a loud, attributed
message and the addon loads in a **faulted** state (skipped every tick) until an operator
runs the `addon enable <name>` console verb, which re-instantiates the module fresh.

---

## The `puck.addon.v1` ABI, summarized

Everything on the wire is a **raw `FixedQ4816` `i64`** (Q48.16, scale `2^16`) or a plain
integer — **no `f32`/`f64` ever crosses the boundary**, in either direction. All multi-byte
values are little-endian.

**Guest exports** (bound by name at instantiation; `Snapshot`/`Commands` in `lib.rs` are the
typed view over the last two):

| Export | Signature | Meaning |
|---|---|---|
| `memory` | `(memory)` | This module's linear memory — the host reads/writes the two regions below directly. |
| `puck_abi_version` | `() -> i32` | Must return `1`, exactly — an exact-match handshake. |
| `puck_snapshot_ptr` | `() -> i32` | Byte offset of the 40-byte snapshot region. |
| `puck_commands_ptr` | `() -> i32` | Byte offset of the command output region. |
| `puck_commands_cap` | `() -> i32` | Count of 24-byte command slots reserved at `puck_commands_ptr` (`<= 64`). |
| `puck_on_tick` | `() -> i32` | Reads the snapshot, writes `<= cap` command records, returns the record count. |
| `puck_init` | `() -> ()` | Optional; called once after instantiation, before the first tick. |

**Snapshot region** (40 bytes, host writes every tick — `Snapshot` in `lib.rs`):

| Offset | Field | Type |
|---|---|---|
| 0 | `tick` | `i64` (engine tick, 50400 Hz) |
| 8 | `posLocalX` | `i64` raw `FixedQ4816` (strafe axis) |
| 16 | `posLocalY` | `i64` raw `FixedQ4816` (up axis) |
| 24 | `posLocalZ` | `i64` raw `FixedQ4816` (forward axis; cabinets sit at negative Z) |
| 32 | `buttons` | `u32` digital bitfield of the addon's own roster slot |
| 36 | `reserved0` | `u32`, always `0` — ignore |

**Command record** (24 bytes each, guest writes, fixed stride — `Commands` in `lib.rs`):

| Offset | Field | Type |
|---|---|---|
| 0 | `padId` | `u16` — see the pad table below |
| 2 | `phase` | `u8` — `0`=Started `1`=Active `2`=Completed `3`=Canceled |
| 3 | `reserved0` | `u8`, **must be `0`** |
| 4 | `reserved1` | `u32`, **must be `0`** |
| 8 | `valueX` | `i64` raw `FixedQ4816` |
| 16 | `valueY` | `i64` raw `FixedQ4816`, **must be `0`** for Digital/Axis1D pads |

**The frozen pad vocabulary** (`pad` module in `lib.rs`) — a closed, ABI-versioned set; there
is no way to invent a new pad ID without an ABI version bump:

| id | `pad` const | Kind |
|---|---|---|
| 0 | `MOVE` | Axis2D |
| 1 | `SOUTH` | Digital |
| 2 | `EAST` | Digital |
| 3 | `WEST` | Digital |
| 4 | `NORTH` | Digital |
| 5 | `SHOULDER_LEFT` | Digital |
| 6 | `SHOULDER_RIGHT` | Digital |
| 7 | `TRIGGER_LEFT` | Axis1D |
| 8 | `TRIGGER_RIGHT` | Axis1D |
| 9 | `RIGHT_STICK` | Axis2D |

`PadMove`'s X/Y are **already camera-relative floor-plane**, matching the host's own
movement-intent frame exactly — **do not negate Y**. Whichever game consumes your addon's
commands owns the binding from these abstract pad IDs to its own verbs (jump, interact,
whatever); an unbound pad ID is simply ignored, never a fault.

Use `commands.push_move(x, y)` / `commands.push_digital(pad::NORTH, CommandPhase::Started,
true)` / `commands.push_axis1d(pad::TRIGGER_LEFT, value)` — never write a record's bytes by
hand. Those helpers are the only things in this template that touch the reserved-must-be-zero
fields, and they always zero them correctly.

Fuel (not a wall-clock timer) bounds every tick's execution; a trap or a protocol violation
(bad pad ID, nonzero reserved field, a `valueY` on a Digital/Axis1D record) puts the addon
into a **sticky faulted state** — skipped every subsequent tick until `addon enable <name>`
re-instantiates it from a fresh `Store`. Faults are loud: the host logs the exact reason
(`AbiMismatch`, `BadExport`, `DecodeError`, `OutOfFuel`, …) with your addon's name.

---

## The golden rule

**Your `mul` must match the host's `FixedQ4816` multiplication bit-for-bit, tie for tie, sign
for sign** — full-width product, round to nearest with ties to even inspecting the
*truncated* result's low bit (never `+ 0.5`). `fixed.rs`'s `mul` already implements this
correctly; `fixed_tests.rs` pins it down with known-answer vectors, including tie cases with
both parities and both signs. If you ever rewrite `mul` (or implement the `div`/`sqrt`/
`atan2` stubs), re-run `cargo test --target <host-triple>` and cross-check against
`Puck.Post`'s `scripting-determinism` stage (the `echo` and `fuel-boundary` fixtures are the
reference oracle) before shipping — any bit-level disagreement between your addon's
arithmetic and the host's makes the *host's* replay/determinism guarantees, not just your
addon's, unreliable for anyone reasoning about what your addon will do next.

`div`, `sqrt`, and `atan2` are **not implemented** — they ship as documented `unimplemented!()`
stubs in `fixed.rs` carrying the exact constants and algorithm description needed to write
them (the CORDIC table and `HALF_PI_RAW` for `atan2`, the widening/rounding recipe for `div`,
the bit-by-bit restoring square root for `sqrt`). The example addon (a dead-reckoning
clamp-walk) needs none of them: sub + clamp is enough to walk toward a fixed target and stay
within `[-1, 1]` per axis.

---

## Notes for agents

- **Never hand-index the ABI's byte offsets.** Use `Snapshot`'s accessors and `Commands`'s
  `push_*` methods; they are the single source of truth for the layout in `abi.rs`, and
  keeping all offset math in one place is what makes the ABI safe to evolve.
- **No floats, ever.** Every value that crosses `puck_on_tick`'s static buffers is an integer
  (`FixedQ4816` raw bits, or a plain `u16`/`u8`/`u32`). If you find yourself reaching for
  `f32`/`f64` anywhere near `abi.rs`, stop — that is exactly the boundary Puck's determinism
  tenet forbids floats from crossing.
- **`cargo test` needs an explicit `--target`** because of the pinned build target — see
  "Running the unit tests" above. Don't add a workaround that changes the default build
  target back to the host; that would silently switch `cargo build --release` (no flags) away
  from producing a `.wasm` file, breaking the one-line build story.
- **Static mutable buffers in `abi.rs` are the plumbing, not a pattern to imitate elsewhere.**
  They exist because the ABI requires stable, guest-exported memory offsets the host can cache
  once at load time; every access is guarded by a `SAFETY` comment establishing the
  single-sim-tick-thread, no-reentrancy invariant the host's call contract guarantees
  (plan-of-record A.7). Don't add more `static mut` state beyond what a genuinely stateful
  addon needs (like the one-shot interact latch in `lib.rs`'s example).
