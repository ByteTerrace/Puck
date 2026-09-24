# puck-addon-channelwalk

A battery-only guest. It is never shipped and no shipped world pins it. It exercises
the capability-channel battery for `Puck.World`'s addon, grant, and co-driving
model, and is templated on `wasm/puck-addon-queryspam`.

`channelwalk` is a document-mounted addon, so its channel contributions are world
logic: trusted in the fold, added outside the pool, and gated by its own declared
reach alone. A seat's authored ceiling never bounds it; a ceiling bounds only a
genuinely untrusted (pooled) contributor such as a peer. `StageContribution` in
`src/Puck.World.Server/WorldTick.Channels.cs` is where that split is made, and
[authority](../../.claude/skills/puck-world/references/authority.md) states the
policy.

Three build-time variants (Cargo features—see `Cargo.toml`; exactly one selected per build):
- `main` (default): the functional guest—`forward`, `walkonly`, `trigger`, `strafe`. See `src/lib.rs`'s module
  doc for the full per-channel behavior and timing.
- `bound64`: declares exactly 64 (`AddonAbi.MaxChannelNames`) channel names—must mount.
- `bound65`: declares 65—must fault `BadExport` at handshake, naming the bound.

Build: `cargo build --release --target wasm32-unknown-unknown -p puck-addon-channelwalk [--no-default-features
--features bound64|bound65]`, then copy `target/wasm32-unknown-unknown/release/puck_addon_channelwalk.wasm` to
`dist/<name>.wasm`. There is no build script (unlike `puck-addon-default`'s `puck wasm build` command)—this crate is
never shipped, so nothing refreshes a committed copy automatically; re-run the three builds by hand after any
source change and re-learn each hash (see below).

## Learning a module's content hash

Same recipe every guest crate here uses: mount with a deliberately wrong `hash` in the world document, boot,
and read the printed computed value off the `HashMismatch` line
(`content sha256-64/{hex} does not match the declared moduleHash pin sha256-64/{pin}`), then paste the printed
`content` value into the row's `hash` field. `worlds/channel-walk-world.json`'s three addon rows are pinned to
the hashes of the committed `dist/` builds; re-learn all three after touching `src/lib.rs` (the `main`
feature)—`bound64`/`bound65` are unaffected by `main`-only changes.

## Test world

`worlds/channel-walk-world.json` is self-contained and needs no shipped world. It carries no document-authored
grants (`grants: []`; the scripts grant everything live), this crate's three addon rows, and two channel rows
beside the usual eight:
- `trigger`—**Binary**, `composition: true`, `threshold: 0.75`. A threshold is legal only on a Binary channel
  (see finding 2).
- `level`—**Unipolar**, `composition: true`, no threshold. No shipped world declares a Unipolar channel row, so
  this is the only Unipolar row the battery reaches.

The three `addons[].modulePath` entries point at this crate's own `dist/*.wasm`, so nothing under
`src/Puck.World/Assets` is touched. Like every relative path a world document authors, they resolve beside the
world file, so `../dist/…` names the crate's build output from `worlds/`. That keeps the world portable to any
checkout; do not turn these into absolute paths.

## Scripts (stdin batteries)—what each proves

Every script leads with `replay.status` (a harmless Immediate read-back) before any `world.wait`, per the
documented stdin-driving trap: a leading `world.wait` silently swallows every line behind it. `world.wait` takes
TICKS (240/sec), never seconds.

Run every script from the repo root—`--world` resolves through `Path.GetFullPath`, i.e. against the current
directory, so a repo-relative path works there and only there:

```bash
dotnet build src/Puck.World -c Release
dotnet src/Puck.World/bin/Release/net10.0/Puck.World.dll \
  --world wasm/puck-addon-channelwalk/worlds/channel-walk-world.json \
  --exit-after-seconds 30 < wasm/puck-addon-channelwalk/scripts/<name>.txt > <name>.log 2>&1
```

A window opens; that is expected. Never pass `--help`—it is not wired as a special case and boots the full
windowed app instead.

| Script | Proves |
|---|---|
| `i-three-window-expiry.txt` | `forward`'s per-tick-declarative contribution—active for a finite window, then a permanent stop with no drift or decay. Its reading is tagged `trusted=[addon:channelwalk]`. |
| `h-below-threshold.txt` / `h-at-threshold.txt` | A seat pool ceiling one raw unit below and exactly at the pinned `trigger` threshold. Both read identically, because the seat's ceiling does not reach a trusted addon's contribution. |
| `j-floor-pool-consent.txt` | Reach alone lands `strafe`'s contribution in full; a seat ceiling on the same channel still grants but bounds nothing. The script's header records the four identical readings. |
| `k-declared-name-bound.txt` | The `bound64`/`bound65` mount/fault pair, the unresolved `walkonly` name's report-and-inert disclosure, and continuous per-act attenuation never faulting the guest. |
| `l-ceiling-refusal-boundary.txt` | `ceiling:0` and a negative ceiling both refuse by name at the console door; a small positive ceiling is accepted (see finding 4). |
| `h2-level-unipolar-joins.txt` | Join-by-join coverage of the Unipolar `level` channel—press-ingress domain refusal and acceptance, base and read-back visibility. The script's header says which stages it does not exercise. |
| `m-role-shape-composition-not-overreached.txt` | The role-must-be-Bipolar validator rule does not over-reach: a Unipolar/Binary composition world still boots and echoes both channels. The negative half (a role-bound non-Bipolar channel must refuse) is not exercised; this battery ships no such world. |

## Findings

These are the battery's observations about current engine behavior. The engine is not changed here.

**1. A Binary-shaped channel admits only `{0, ONE}` from any writer, and the host enforces it.** An addon's
`channels!` declaration carries a local kind hint (Bipolar/Binary/Unipolar) the crate cannot check against the
host. The host decodes an `Act` against the world's declared shape, not the hint: a `Bipolar`-hinted `trigger`
act carrying a raw value in `[-ONE, ONE]` faults the whole batch when the world declares `trigger` Binary:
```text
[world.addon: addon channelwalk: DecodeError — cell 2 binary 'trigger' requires A in {0, one} — the literal
fixed-point values, never a boolean 0/1 (A=49151)]
```
This is why the guest presses `trigger` as a held press and why the threshold pair walks a pool ceiling rather
than a raw contributed value.

**2. A threshold is legal only on a Binary channel.** `WorldDefinitionValidator` refuses a `threshold` on any
other shape (`"{path}.threshold is only meaningful on a binary channel."`), matching `WorldChannel.Threshold`'s
own doc, and `WorldChannelTable.Compile` compiles a zero threshold for any non-Binary shape.

**3. A composition channel's held press never enters the numeric pool.** `StageContribution` accumulates an act's
analog `Intent` delta and its `HeldChannels` press separately. The held press joins through
`WorldChannelTable.ComposeHeld` (a max for Unipolar/Binary, a clamped sum for Bipolar) in
`FoldChannelContributions`, and never through `Puck.Maths.FixedContributionFold`, whose pool and threshold only an
analog delta reaches.

**4. `ceiling:0` and negative ceilings are refused at parse time.** `ChannelPolicy.WithCeiling` and
`FixedContributionFold` ("zero is a valid zero-width pool") encode two different zero-radius semantics.
`WorldGrantCommandModule` refuses `ceiling:0` and any negative ceiling before either fold sees a value, so the
two layers are never asked to agree on a live-authored zero.

## What the ABI made impossible as specified

- `trigger` is `Binary`, not `Unipolar`—see finding 2.
- `walkonly` (guest-declared, unresolved) cannot appear in any `channels:` reach/ceiling token—the grant parser
  resolves every name against the world's channel table and refuses an unrecognized one
  (`"channels:<> names 'walkonly', which names no declared channel"`). `channelwalk`'s
  Drive grant in `k-declared-name-bound.txt` therefore carries no `channels:` token at all; `walkonly`'s act
  still reaches the host and still only attenuates, because the name never resolves to a `PlayerIntent`
  ordinal, a property of the act checked before any reach or ceiling gate runs.
- `forward`'s window and `trigger`'s press are about one second each (`FORWARD_ACTIVE_TICKS = 240`,
  `TRIGGER_PRESS_TICKS = 240`). A window of a few ticks is not a boundary the stdin console driver can reliably
  straddle: identical input does not land on matching absolute ticks across processes, and one queued line's
  dispatch overhead can exceed the whole window. The mechanism under test (a finite window, then a permanent
  per-tick-declarative stop) does not depend on the length.

## Documentation

📚 [WebAssembly addons](../README.md) · 🛠️ [Development](../../docs/development/README.md)
