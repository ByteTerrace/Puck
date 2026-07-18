# Puck.Scripting

**Deterministic WASM addon host.** Puck.Scripting loads sandboxed WebAssembly modules and drives
each one once per sim tick, on the sim thread, with a fixed-point snapshot in and a stream of
neutral virtual-pad commands out. It is the shared substrate for scripting the engine from a
`.wasm`/`.wat` module without letting a script touch anything but its own slot's intent.

Everything is built for **bit-identical replay**: the Wasmtime engine is pinned and configured with
every determinism knob explicit (fuel on, threads/SIMD off, NaN canonicalization on, Cranelift at a
fixed optimization level), no floating point ever crosses the boundary, and a runaway module halts
at a fuel-deterministic point rather than a wall-clock one.

```text
namespace Puck.Scripting
target     net10.0
deps       Puck.Assets, Puck.Commands, Puck.Maths + Wasmtime [44.0.0] (exact pin)
```

The `Wasmtime` package version **is** the native engine version, and fuel accounting is
Cranelift-codegen-dependent (basic-block granularity, upstream #4109). The pin is exact on purpose:
a silent bump can shift the fuel-exhaustion tick and break stored replays. Bumping it is a
deliberate, re-gated change — never an incidental restore. This is the repo's first
native-runtime-bearing NuGet dependency; the verified path is framework-dependent `dotnet run -c
Release` (no self-contained/AOT story in v1).

---

## At a glance

| Type | Kind | Role |
|------|------|------|
| `AddonAbi` | `static class` | The frozen `puck.addon.v1` contract: byte layouts, export names, pinned budgets. |
| `ScriptingEngineOptions` | `readonly record struct` | The pinned deterministic config values (`Deterministic` preset). |
| `ScriptingEngine` | `sealed class` | Owns the one configured `Wasmtime.Engine`; asserts the pinned version. |
| `WasmModuleLoader` | `sealed class` | Path → bytes (`IAssetSource`) → `\0asm`/WAT → hash → LRU of compiled `Module`. |
| `ScriptingModuleInfo` | `sealed record` | The immutable load result: path, content hash, byte length, compiled module. |
| `AddonModuleValidator` | `static class` | Static export-shape check against the ABI before instantiation. |
| `AddonDescriptor` | `readonly record struct` | A neutral load request (keeps `Puck.Scene` out of the deps). |
| `AddonSnapshot` / `AddonSnapshotWriter` | `readonly record struct` / `static` | The 40-byte fixed-point input; the little-endian serializer. |
| `AddonCommand` / `AddonCommandReader` | `readonly record struct` / `static` | One decoded pad record; the desync-proof fixed-stride reader. |
| `PadCommandId` / `AddonValueKind` | `static` / `enum` | The frozen 10-entry pad vocabulary and its value-shape lookup. |
| `AddonButtons` | `[Flags] enum` | The snapshot's digital-button bitfield (A.4). |
| `AddonInstance` | `sealed class` | One `Store`+`Instance` per addon; sticky fault; single-threaded; owns the decode buffer. |
| `AddonHost` | `sealed class` | Composes engine+loader; owns the instance set keyed by name; the object consumers pump. |
| `AddonTickResult` / `AddonTickStatus` / `AddonFault` / `AddonFaultKind` / `AddonState` | records/enums | The per-tick outcome and lifecycle/fault vocabulary. |

---

## The frozen ABI (`puck.addon.v1`)

A module agrees on a byte contract, not a function-call ABI. The host writes a fixed **40-byte
snapshot** into a guest-exported region, calls the nullary `puck_on_tick`, and reads back a
fixed-stride buffer of **24-byte command records**. Every field is little-endian; every fixed-point
value is `FixedQ4816` raw `i64` bits (`One = 0x1_0000`). No `f32`/`f64` appears anywhere.

### Guest exports (bound by name at instantiation)

| Export | Signature | Required | Meaning |
|--------|-----------|----------|---------|
| `memory` | `(memory)` | yes | Guest linear memory; the host reads/writes its two regions. |
| `puck_abi_version` | `() -> i32` | yes | Returns `1`. Exact-match handshake. |
| `puck_snapshot_ptr` | `() -> i32` | yes | Byte offset of the 40-byte snapshot region. |
| `puck_commands_ptr` | `() -> i32` | yes | Byte offset of the command output region. |
| `puck_commands_cap` | `() -> i32` | yes | Count of 24-byte slots reserved (`0..64`). |
| `puck_on_tick` | `() -> i32` | yes | Reads snapshot, writes ≤cap records, returns the record count. |
| `puck_init` | `() -> ()` | optional | Called once after instantiation, before the first tick. |

The four pointer/cap/version getters are **pure constants** — called once at instantiation and
cached. `puck_on_tick`/`puck_init` take no parameters, so a WAT body is a clean `i64.load`/`i64.store`
sequence with zero argument plumbing.

### Snapshot input region — 40 bytes (host writes each tick)

| Offset | Type | Field |
|--------|------|-------|
| 0 | i64 | `tick` (engine `ulong` bit pattern, 50400 Hz) |
| 8 | i64 | `posLocalX` (`FixedQ4816` raw — strafe) |
| 16 | i64 | `posLocalY` (`FixedQ4816` raw — up) |
| 24 | i64 | `posLocalZ` (`FixedQ4816` raw — forward, cabinets at −Z) |
| 32 | u32 | `buttons` (digital bitfield, see `AddonButtons`) |
| 36 | u32 | `reserved0` (host writes 0; v1 guests MUST ignore) |

### Command output record — 24 bytes each (guest writes; host reads the returned count)

| Offset | Type | Field |
|--------|------|-------|
| 0 | u16 | `padId` (frozen vocabulary; host DecodeErrors on unknown ids) |
| 2 | u8 | `phase` (`0=Started 1=Active 2=Completed 3=Canceled`) |
| 3 | u8 | `reserved0` — **MUST be 0** |
| 4 | u32 | `reserved1` — **MUST be 0** |
| 8 | i64 | `valueX` (`FixedQ4816` raw; Digital: `One`/`0`) |
| 16 | i64 | `valueY` (Axis2D Y; **MUST be 0** for Digital and Axis1D) |

There is **no `kind` byte** — the host derives the value shape from `padId` via
`PadCommandId.KindOf`. Records are packed contiguously; the host reads exactly `N*24` bytes as a
fixed-stride loop that cannot desync.

### The frozen pad vocabulary

| id | name | kind |
|----|------|------|
| 0 | `Move` | Axis2D |
| 1 | `South` | Digital |
| 2 | `East` | Digital |
| 3 | `West` | Digital |
| 4 | `North` | Digital |
| 5 | `ShoulderLeft` | Digital |
| 6 | `ShoulderRight` | Digital |
| 7 | `TriggerLeft` | Axis1D |
| 8 | `TriggerRight` | Axis1D |
| 9 | `RightStick` | Axis2D |

The ABI freezes the **abstract** pad names/kinds — a closed set; adding an eleventh is an
ABI-version event, not configuration. The pad→game binding lives in the **consumer** (the demo maps
pad→`overworld.*`; Puck.Post maps pad→hash), never here — that is what keeps Puck.Scripting a shared
substrate that never references `Puck.Scene`/`Puck.Demo`.

---

## Loading, ticking, faulting

```csharp
using var engine = new ScriptingEngine(ScriptingEngineOptions.Deterministic);
var loader = new WasmModuleLoader(engine: engine, assetSource: assetSource);

using var host = new AddonHost(engine: engine, loader: loader);   // host owns the engine
host.Add(new AddonDescriptor(Name: "ghost", ModulePath: "Assets/Addons/autopilot-ghost.wat",
    ModuleHash: null, Slot: 1, FuelPerTick: null, Enabled: true));

// Once per sim tick, on the sim thread:
foreach (var addon in host.Instances) {
    var result = addon.Tick(in snapshot);
    if (result.Status == AddonTickStatus.Ok) {
        foreach (var command in addon.Commands) {
            // map command.PadId -> your game's intent (consumer-owned binding)
        }
    }
}
```

`Load` mirrors `ShaderModuleLoader`: read bytes through the `IAssetSource`, treat a leading `\0asm`
as binary wasm else compile the WAT text via `Module.FromText`, compute the `AssetContentHash`, and
cache the compiled `Module` in a content-addressed LRU so two documents naming the same bytes
compile once.

`AddonInstance.Tick` never allocates and never desyncs. It writes the 40 snapshot bytes, sets the
per-tick fuel budget, calls `puck_on_tick` once, derives `FuelConsumed` as `budget − remaining`, and
decodes exactly the returned count of records into a **reusable buffer** you read back through
`instance.Commands` (a `ReadOnlySpan<AddonCommand>`) synchronously, before the next tick.
`AddonTickResult` deliberately carries **no span**.

### Fuel and fault contract

- **Fuel, not epochs.** The halt point is a pure function of the instruction stream. Each tick runs
  under `FuelPerTick` (default `1_000_000`); exhaustion traps `OutOfFuel` at a codegen-deterministic
  point.
- **Faults are sticky, terminal, deterministic state.** A trap or a protocol violation drives the
  addon into `Faulted` and it is **skipped every subsequent tick** — no mid-tick retry — until an
  explicit `Enable()` **disposes and re-instantiates a fresh `Store`** from the cached module, a
  clean reset to the module's defined initial state.
- **Trap classification:** `OutOfFuel`, `StackOverflow`, `MemoryOutOfBounds`, `Unreachable`, else
  `Trap`. Load-time problems (missing file, bad bytes, bad export shape, ABI mismatch, out-of-range
  region) fault as `AbiMismatch`/`BadExport` and the addon **never instantiates or ticks**.
- **One `Store`/`Instance` per addon, single sim-tick thread only** (Wasmtime store thread affinity,
  issue #331). `GC.KeepAlive(store)` follows every guest invoke (wasmtime-dotnet finalizer-hazard
  discipline).
- **Memory cap:** each store gets a hard `SetLimits(memorySize: …)` ceiling (256 pages) plus a
  load-time region-bounds pre-flight; `memory.grow` is fuel-charged.

Fault detail lines are formatted for the console, keyed by the addon's **name**:

```text
addon ghost: BadExport — export 'puck_on_tick' missing or not ()->i32
addon ghost: AbiMismatch — guest ABI 2, host speaks puck.addon.v1
addon ghost: OutOfFuel at tick 3140 — disabled; 'addon enable ghost' to retry
addon ghost: DecodeError — record 0 padId 42 unknown
```

`AddonHost.Describe()` narrates each addon with a `ContentPetname` (`Willow-Lantern-Nine  sha256-64/…
slot 1  fuel 1000000  ENABLED`).

**Hot reload** (`AddonHost.Reload(name)`): re-reads the declared module path, recompiles (a changed
content hash misses the module cache; an unchanged one reuses it), and swaps in a fresh store — the
in-session edit loop the demo's `addon reload <name>` verb drives. The status line names the change by
petname (`Moss-Pouch-Two became Cinder-Locket-Five`) because the petname IS the content hash; an
unchanged module reports `unchanged (fresh store)`. A declared `moduleHash` pin **refuses** a content
change (remove the pin to hot-reload); a broken edit swaps in a sticky faulted instance naming the
reason — fix and reload again. The reloaded addon runs regardless of its prior enabled/disabled state.

---

## Notes for agents

- **No floats cross the boundary, ever.** The host pre-quantizes analog input via the exact
  `FixedQ4816.FromDouble` path the sim already uses and ships raw `i64` bits; the guest returns raw
  `i64` bits. Do not hand an `f32` across and re-derive fixed-point guest-side — that reintroduces
  the one non-determinism this ABI exists to remove.
- **The pad vocabulary is frozen; the binding is not.** Puck.Scripting stops at decoded
  `AddonCommand`s. Each consumer owns the last-mile mapping (`PlayerIntent`, an `Fnv1aHash` fold, …).
  Never reach for a demo verb name in here.
- **The reader cannot desync.** `puck_on_tick`'s returned count drives a fixed-stride read; every
  reserved-must-be-zero and kind/value guard is checked in order, and any failure is a deterministic
  `DecodeError` naming the record index — a stale guest can smuggle no meaning into the reserved
  fields.
- **Never float the Wasmtime version.** Fuel timing is codegen-locked to `[44.0.0]`. The Post
  `scripting-determinism` stage asserts the loaded assembly's major version; a drift is a `Fail`.
- **Single-threaded, one store per addon.** Do not share a `Store` across threads or reuse one
  across addons; hot-swap a script by `Enable()` (dispose + re-instantiate), not by mutation.
