# puck-addon-channelwalk

BATTERY-ONLY guest. Never shipped, pinned by no shipped world. Exercises the capability-channel verification
battery for `Puck.World`'s addon/grant/co-driving-pool model. Templated on `wasm/puck-addon-queryspam`.

**SUPERSEDED IN PART by owner ruling 2026-08-02 (headless P6b, trusted-by-authorship re-cut)** — see
`.claude/skills/puck-world/references/authority.md`.
`channelwalk` is a document-mounted (Simulation-lane) addon, so its contribution class in the fold is now TRUSTED
(added outside the pool, gated by its OWN declared Reach only — consent does not apply to world logic). Two
scripts' comments were corrected in the same change: `scripts/j-floor-pool-consent.txt` (its whole premise — Reach
without a seat-authored ceiling folds nothing — is inverted: Reach ALONE now folds in full, and a seat's ceiling
grant is a no-op for this addon regardless of value) and `scripts/i-three-window-expiry.txt` (its `forward`
reading is now tagged `trusted=[addon:channelwalk]`, not `untrusted=[...]`). `scripts/h-at-threshold.txt` and
`scripts/h-below-threshold.txt`'s asserted NUMBERS are unaffected (both always granted a ceiling, so the pooled
and trusted paths read identically there), but Finding 2 below now has a sharper cause on the trusted branch: the
ceiling is not merely un-consulted for its VALUE, it is not consulted AT ALL.

**Verified at:** `f9d5d15b` — the full re-run executed across `b11e0b16..f9d5d15b`, and
`l-ceiling-refusal-boundary.txt` was re-confirmed at `f9d5d15b` itself after the last of those commits
landed. The intervening commits are comment-only plus the `IWorldGrantsView` split, none of which
changes a fold value. (Re-run in full, minus the unexercised negative half of `m-role-shape-composition-not-overreached.txt`, after two
landings that touched what this battery exercises: `cf38a86e` deleted `Server/WorldChannelFold.cs` and pointed both
the client and server fold sites at `Puck.Maths.FixedContributionFold`; `216fe466` made a ROLE channel require
Bipolar shape). Every script's asserted claim still holds bit-for-bit against the original run at `6c19a959`. `cf38a86e`
is a mechanical rename — `FixedContributionFold.Evaluate`'s arithmetic is line-for-line the deleted
`WorldChannelFold.Fold` — so nothing about the fold's OUTPUT could have moved; Findings 2 and 4 below are
re-confirmed, and Finding 2 now names its root cause directly instead of reporting two untested candidates.

Three build-time variants (Cargo features — see `Cargo.toml`; exactly one selected per build):
- `main` (default): the functional guest — `forward`, `walkonly`, `trigger`, `strafe`. See `src/lib.rs`'s module
  doc for the full per-channel behavior and timing.
- `bound64`: declares exactly 64 (`AddonAbi.MaxChannelNames`) channel names — must mount.
- `bound65`: declares 65 — must fault `BadExport` at handshake, naming the bound.

Build: `cargo build --release --target wasm32-unknown-unknown -p puck-addon-channelwalk [--no-default-features
--features bound64|bound65]`, then copy `target/wasm32-unknown-unknown/release/puck_addon_channelwalk.wasm` to
`dist/<name>.wasm`. There is no build script (unlike `puck-addon-default`'s `wasm/build.cs`) — this crate is
never shipped, so nothing refreshes a committed copy automatically; re-run the three builds by hand after any
source change and re-learn each hash (see below).

## Learning a module's content hash

Same recipe every guest crate here uses: mount with a deliberately wrong `hash` in the world document, boot,
and read the printed computed value off the `HashMismatch` line
(`content sha256-64/{hex} does not match the declared moduleHash pin sha256-64/{pin}`), then paste the printed
`content` value into the row's `hash` field. `worlds/channel-walk-world.json`'s three addon rows are pinned to
the hashes actually built in this repo state; re-learn all three after touching `src/lib.rs` (the `main`
feature) — `bound64`/`bound65` are unaffected by `main`-only changes.

## Test world

`worlds/channel-walk-world.json` — derived from the `default` world (retired under the 2026-08-06 four-world
charter; `src/Puck.World/Assets/worlds` now ships only `play`/`dive`/`kart`/`jump` — this fixture is
self-contained and needs no shipped world to exist): same motion/scene/kits/etc., the shipped `default` addon
row REMOVED (replaced by this crate's three rows), `grants: []`
(everything is granted LIVE via scripts, never document-authored), and two channel rows added to the default
eight:
- `trigger` — **Binary**, `composition: true`, `threshold: 0.75`. See "Finding: unipolar + threshold" below for
  why this is Binary rather than the Unipolar the task originally specified.
- `level` — **Unipolar**, `composition: true`, no threshold. The first Unipolar channel row in any world this
  repo boots (see "Finding: no shipped world exercises Unipolar" below).

The three `addons[].modulePath` entries point at this crate's own `dist/*.wasm`, so nothing under
`src/Puck.World/Assets` is touched. They are written relative to the BUILD OUTPUT, not the repo root and not the
world file: `WorldAddonRuntime` (`AddonModulePath`) combines an unrooted `modulePath` with
`AppContext.BaseDirectory`, so `../../../../../wasm/…` climbs from `src/Puck.World/bin/<config>/net10.0/` back to
the repo root. That makes the world portable to any checkout on any machine — do NOT "fix" these into absolute
paths, which is what pinned an earlier revision of this file to one disk.

## Scripts (stdin batteries) — what each proves

Every script leads with `replay.status` (a harmless Immediate read-back) before any `world.wait`, per the
documented stdin-driving trap: a leading `world.wait` silently swallows every line behind it. `world.wait` takes
TICKS (240/sec), never seconds.

Run every script FROM THE REPO ROOT — `--world` resolves through `Path.GetFullPath`, i.e. against the current
directory, so a repo-relative path works there and only there:

```
dotnet build src/Puck.World -c Release
dotnet src/Puck.World/bin/Release/net10.0/Puck.World.dll \
  --world wasm/puck-addon-channelwalk/worlds/channel-walk-world.json \
  --exit-after-seconds 30 < wasm/puck-addon-channelwalk/scripts/<name>.txt > <name>.log 2>&1
```

A window opens; that is expected. Never pass `--help` — it is not wired as a special case and boots the full
windowed app instead.

| Script | Proves |
|---|---|
| `i-three-window-expiry.txt` | Case I: `forward`'s per-tick-declarative contribution — active for a finite window, then a PERMANENT stop with no drift or decay. |
| `h-below-threshold.txt` | Case H, RED half: a pool ceiling one raw unit BELOW the pinned `trigger` threshold. |
| `h-at-threshold.txt` | Case H, GREEN half: a pool ceiling exactly AT the pinned threshold. See "Finding: ceiling has no observed effect on a Binary composition channel" — both halves in fact read identically; this pair is what demonstrates that. |
| `j-floor-pool-consent.txt` | Case J-floor: no reach+ceiling → attenuated; ceiling granted → same acts land unclamped; a ceiling boundary pair (`c` vs `c+1` raw) on `strafe`, landing at exact 1-raw-unit precision. Fully passes as designed. |
| `k-declared-name-bound.txt` | Case K: the `bound64`/`bound65` mount/fault pair, the unresolved `walkonly` name's report-and-inert disclosure, and continuous per-act attenuation never faulting the guest. |
| `l-ceiling-refusal-boundary.txt` | Owner addendum: `ceiling:0` and a negative ceiling both REFUSE by name at the console door; a small positive ceiling is accepted. The seam that keeps `Puck.World.Protocol.ChannelPolicy.WithCeiling` (throws on `ceiling <= 0`) and `Puck.Maths.FixedContributionFold` (whose own doc: "zero is a valid zero-width pool") from ever disagreeing live — `ceiling:0` can never reach either fold through the console. |
| `h2-level-unipolar-joins.txt` | Owner addendum: join-by-join coverage of the genuinely-Unipolar `level` channel — press-ingress domain refusal/acceptance, base/read-back visibility. See the script's own header for which stages (fold-with-a-contribution, quantize/threshold) are N/A or unverified, and why. |
| `m-role-shape-composition-not-overreached.txt` | Owner addendum: the role-must-be-Bipolar validator rule (`216fe466`, an ancestor of the SHA above) does NOT over-reach — a Unipolar/Binary COMPOSITION world still boots and echoes both channels. Asserted and passing. The negative half (a role-bound non-Bipolar channel must refuse) is NOT exercised here: this battery ships no such world. |

## Findings (claims for the orchestrator — not fixed here; engine code is read-only for this task)

**1. A Binary-shaped channel admits only `{0, ONE}` from ANY writer — addon or human — and the host, not the
guest's local kind hint, enforces it.** An addon's `channels!` declaration carries a LOCAL kind hint
(Bipolar/Binary/Unipolar) that `puck_stdlib::channels`'s own module doc says is "purely a LOCAL, compile-time
hint this crate cannot check against the host" and that a mismatch "compiles cleanly and faults the instance at
the first out-of-domain act." The FIRST reading of that sentence — that the LOCAL hint's domain governs, so a
`Bipolar`-hinted `trigger` could carry any raw value in `[-ONE, ONE]` even though the world declares `trigger`
Binary — is WRONG. Running it produced an immediate whole-batch fault:
```
[world.addon: addon channelwalk: DecodeError — cell 2 binary 'trigger' requires A in {0, one} — the literal
fixed-point values, never a boolean 0/1 (A=49151)]
```
The host decodes an `Act` against the WORLD's declared shape. This forced the guest redesign documented in
`src/lib.rs`'s module doc, and forced case H off a literal "walk a raw value across the threshold" test (not
constructible for Binary) onto a pool-ceiling boundary instead.

**2. Ceiling has no observed effect on a Binary composition channel's untrusted contribution — RE-VERIFIED at
`b11e0b16`, root cause now DETERMINED, not merely reported.** Case H's pool-ceiling boundary pair
(`h-below-threshold.txt` ceiling `49151` vs `h-at-threshold.txt` ceiling `49152`, both straddling the pinned `0.75`
threshold) still produces the IDENTICAL result at the current tip:
```
trigger:binary folded=0(0) h=0(0) held=1(65536) composed=1(65536) ... ceiling=<either value> clamped=no
```
`composed` reads `ONE`, unclamped (`clamped=no`), regardless of which ceiling is authored, and `folded` never
leaves `0` in either run. Contrast `strafe` (a Bipolar ROLE channel), where the identical
`world.grant ... channels:strafe ceiling:<f>` mechanism clamps to the EXACT raw ceiling every time
(`j-floor-pool-consent.txt`'s clean pass: ceiling `16384` -> `composed=0.25(16384)`, ceiling `16385` ->
`composed=0.2500152587890625(16385)`, both `clamped=yes`).

The prior pass left two candidate explanations open; this pass isolates the answer to (a), confirmed by reading
both call sites and the accumulation that feeds them (`src/Puck.World.Server/WorldServer.cs`):

- `StageContribution` (~line 849) reads the addon's act as two SEPARATE halves per ordinal:
  `submission.Intent[ordinal]` (an analog delta) and `submission.HeldChannels[ordinal]` (a composition
  button-press image). `channelwalk`'s Binary `trigger` act is a HELD PRESS, not an analog delta — it never writes
  a nonzero `Intent[trigger]`. Only `Intent` deltas accumulate into `m_untrustedSum`, the term
  `FoldChannelContributions` (~line 1020) passes to `FixedContributionFold.Evaluate` as `poolDeltaRaw`. So
  `poolDeltaRaw` is `0` for `trigger` on every tick, in every script — the pool-clamp path this Case H pair was
  built to walk is never fed a nonzero value to clamp, independent of the ceiling. `folded` reading `0` is what
  `Evaluate` correctly returns for `baseline=0, poolDeltaRaw=0` at any threshold; the fold ARITHMETIC is not at
  fault (`cf38a86e` confirms this — `FixedContributionFold.Evaluate` is a line-for-line rename of the deleted
  `WorldChannelFold.Fold`, so this was equally true before the unification).
- `held`/`composed` instead come from a second, independent join that never touches
  `FixedContributionFold`: `StageContribution`'s `eligible` gate admits an ordinal into `m_contributedHeld` once
  `reach.Meet(consent: ceilings.Support)` contains it — `ceilings.Support` is the SET of channels the seat
  authored ANY positive ceiling for, not a magnitude check — so any positive `ceiling:<f>` is enough to admit the
  addon's full `held=ONE` press into the MAX join `WorldChannelTable.ComposeHeld` performs, both in
  `FoldChannelContributions` (seat `held` ⊕ addon `held`) and again in `WorldBody.NextIntent` (~line 1158, folded
  movement value ⊕ the combined held image) for the value `player.channels` prints as `composed`. Neither join
  reads the ceiling's VALUE, only its presence, and `NextIntent`'s overlay applies unconditionally to every
  composition ordinal (>= `ChannelLimits.RoleCount`) — which is also why `strafe` (a role ordinal, < `RoleCount`)
  never takes this overlay and reads `composed == folded` exactly.

So explanation (a) is confirmed and (b) is refuted: `WorldChannelTable.CompileFoldShape`/`.Threshold` are compiled
once from the WORLD's channel table, never gated by a kit's `Actions` map, so `trigger` being unbound in this test
world's kits was never the cause. The live defect is real and unification-surviving: a seat's authored `ceiling`
on a Binary/Unipolar composition channel governs only WHETHER an addon's held press is admitted at all, never HOW
FAR it may pull the value — the numeric pool/threshold machinery `FixedContributionFold` implements for exactly
this purpose is simply never reached by a composition channel's button-style act.

**3. `WorldDefinitionValidator` refuses a `Threshold` on any non-Binary channel row, and `WorldChannelTable.Compile`
would have discarded it anyway.** The task's original brief asked for `trigger` to be declared Unipolar with a
pinned threshold. `WorldDefinitionValidator.ValidateChannels` refuses this outright:
`"{path}.threshold is only meaningful on a binary channel."` — confirmed by the doc comment on
`WorldChannel.Threshold` itself ("binary channels only"). Independently, even absent that refusal,
`WorldChannelTable.Compile` hardcodes the compiled threshold to `FixedQ4816.Zero` for any non-Binary shape,
regardless of what a document declares — so a "pinned Unipolar threshold" would have been silently inert even if
the validator admitted it. `trigger` was built Binary instead, as documented above and in `src/lib.rs`.

**4. No shipped world exercises a Unipolar channel row at all — RE-VERIFIED unchanged at `b11e0b16`.** `level`
(this crate's addition) is still the first Unipolar row any world document this repo boots. It validates and boots
cleanly (`m-role-shape-composition-not-overreached.txt`'s verified half; `world.affordances`' echoed channel table lists
`{"name":"level","shape":"unipolar","consumer":"composition"}`, and `trigger` lists `"consumer":"composition"`
too — both composition-only, confirming neither is touched by `216fe466`'s new role-shape rule). `player.press
level -0.1 ...` still refuses by name (`"level" is unipolar — value must be in [0, 1]"`); a legal press still
lands with `composed` reflecting the pressed value quantized to raw `FixedQ4816` bits while `h`/`held` stay `0`.
Verbatim re-run (`h2-level-unipolar-joins.txt`, right after `player.press level 0.4 2 1`):
```
level:unipolar folded=0(0) h=0(0) held=0(0) composed=0.399993896484375(26214) trusted=[] untrusted=[] ceiling=none clamped=no
```
identical in shape to the original run. `player.press`'s value for a Unipolar composition channel still reaches
`composed` through a third path this pass did not chase (plausibly the SAME lane-timer overlay in
`WorldBody.NextIntent` that Finding 2 identifies for `held`/`composed` — a `player.press` is a wire timer, not a
`HeldChannels` act, so it takes the unconditional `m_laneTimers[ordinal] > 0` overwrite a few lines above the
`ComposeHeld` overlay, bypassing both `h`/`held` and the fold entirely — but this pass did not verify that lane
directly, so it stays reported, not asserted).

**5. `ceiling:0`/negative-ceiling refusal is real and load-bearing (owner addendum item 1, verified).**
`ChannelPolicy.WithCeiling` (`ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ceiling, 0)`) and
`FixedContributionFold` (whose own doc: "A null radius means no pool; zero is a valid zero-width pool") encode
TWO DIFFERENT zero-radius semantics. `WorldGrantCommandModule.TryParseGrant` refuses `ceiling:0` and any negative
ceiling at PARSE TIME, before either fold ever sees a value — confirmed verbatim in `l-ceiling-refusal-boundary.txt`'s
run. This is the seam that keeps the two fold layers from ever being asked to agree on what a live-authored zero
means.

## What the ABI made impossible as specified

- `trigger` is `Binary`, not `Unipolar` — see Finding 3.
- `walkonly` (guest-declared, unresolved) cannot appear in any `channels:` reach/ceiling token — the grant parser
  resolves every name against the WORLD's channel table and refuses an unrecognized one
  (`"channels:<> names 'walkonly', which names no declared channel"`) — confirmed by running it. `channelwalk`'s
  Drive grant in `k-declared-name-bound.txt` therefore carries no `channels:` token at all; `walkonly`'s act
  still reaches the host and still only attenuates, with no reach needed to prove that (attenuation there comes
  from the name never resolving to a `PlayerIntent` ordinal at all, a property of the ACT, checked before any
  reach/ceiling gate runs).
- `forward`'s "exactly 3 ticks" and `trigger`'s narrow raw-value walk were both widened to ~1-real-second windows
  (`FORWARD_ACTIVE_TICKS = 240`, `TRIGGER_PRESS_TICKS = 240`) — a few-tick (millisecond-scale) window is not a
  boundary the stdin console driver can reliably straddle; the documented cross-process pacing caveat (identical
  input does NOT land on matching absolute ticks) applies doubly here, since even one queued line's dispatch
  overhead can exceed the whole window. The MECHANISM under test (finite window, then permanent per-tick-declarative
  stop) is unchanged; only the magnitude moved for observability.
