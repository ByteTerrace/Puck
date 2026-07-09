# Determinism, Savestate, and Replay

This is the partition where Puck's contract pays the biggest dividend — no-float /
no-RNG means a savestate is complete by construction — and it was the core's
biggest architectural outlier: the GB/GBC core has snapshot/restore/Fork, and the
GBA core had none. That gap **closed this arc**: whole-machine savestate landed
(a ~551 KB flat image, `--state-roundtrip` covering frame-boundary *and*
mid-frame, `agb.snap`/`agb.restore` demo verbs), and with it the keystone that
rewind, runahead, and rollback all reduce to.

Provenance: `digest-8` (gatherer), `review-c` §A (deep review), with credit and
post-wave facts from `digest-0` and the implementation.

---

### What a deterministic-core contract looks like

- **Source:** Libretro netplay docs,
  https://docs.libretro.com/development/retroarch/netplay/ ; RetroArch `core.h`,
  https://github.com/libretro/RetroArch/blob/master/core.h ; BizHawk (TASVideos),
  https://tasvideos.org/Bizhawk ; GGPO, https://www.ggpo.net/ ; *What actually
  breaks determinism?*, https://tasvideos.org/Forum/Topics/15811.
- **Finding:** the field states one equivalence rule — "same game, same firmware,
  same core, same sync settings, same input → same result" (BizHawk); GGPO assumes
  it as a *precondition*, not something it provides. libretro distinguishes "can
  serialize" from "serializes deterministically" (`CORE_INFO_SAVESTATE_DETERMINISTIC`
  gates netplay + runahead) and bakes in a monotonicity rule: `retro_serialize_size()`
  may only *decrease*, never increase. Concretely-named determinism bugs:
  uninitialized RAM differing per host ("one of the most common desync causes,"
  some emulators scramble it at boot to catch TAS authors relying on it),
  RTC/wall-clock reads, float order-of-operations across platforms, thread-scheduling
  nondeterminism, heap-address leakage into state, unordered hash-map iteration.
  melonDS's netplay bring-up hit a textbook unsigned-integer-underflow (`clientframes
  < NumFrames-16`) — a reminder these are often mundane arithmetic bugs surfacing
  under rollback stress.
- **Determinism fit:** this *is* the fit criterion.
- **Puck status: already at SOTA — the hardest precondition is met.** Entirely
  integer state, no wall-clock in the traced core; input (controller and RTC/sensor
  reads) is per-tick `CommandSnapshot`s. This is what makes the savestate complete
  by construction. **Verdict (review-c B5): already at SOTA.**
- **See also:** savestate below, hash divergence below.

### Flat whole-machine savestate

- **Source:** mGBA savestate discussion,
  https://forums.mgba.io/showthread.php?tid=5624 ; Carter Yagemann, *Save-state
  hacking for beginners* (GBAState offsets),
  https://carteryagemann.com/save-state-hacking-for-beginners.html ; GGPO,
  https://www.ggpo.net/ ; BizHawk Waterbox issue #3247,
  https://github.com/TASEmulators/BizHawk/issues/3247.
- **Finding:** two schools. mGBA uses a fixed-layout, memcpy-able `GBAState` struct
  — version magic + BIOS checksum + ROM CRC32 at offset 0, then CPU/APU/DMA/timer/
  interrupt/video blocks at fixed offsets; fast because it's essentially a memcpy,
  versioned by the magic field. BizHawk's Waterbox instead snapshots *all memory
  the core touched* (à la Emacs `unexec`) — easy correctness, but state can balloon
  to hundreds of MB, and it *cannot snapshot GPU state*, forcing ported cores to
  software rendering. For GBA the size is dominated by 32 KB IWRAM + 256 KB EWRAM +
  96 KB VRAM + save-RAM, not the register block. GGPO reduces its entire contract
  to "save state, load state, advance one frame headlessly."
- **Determinism fit:** ideal — the no-float rule guarantees no hidden state to
  serialize. The one care item: the scheduler event queue holds `Callback`
  references that must serialize as stable enum/ID tags, not raw heap addresses
  (address leakage is a named desync cause).
- **Puck status: implemented (this arc).** Whole-machine `Snapshot`/`Restore`
  landed — a ~551 KB flat image of IWRAM/EWRAM/VRAM/OAM/PRAM/save-RAM + CPU banked
  regs/CPSR/SPSR + PPU/APU/DMA/timer/IRQ/scheduler/prefetch/cart state + master
  `Now` (`AgbMachineSnapshot`, `AgbStateReader`/`AgbStateWriter`, the per-subsystem
  `*.State.cs` partials, `IAgbSnapshotable`). The `--state-roundtrip` Post
  diagnostic (`StateRoundTripProbe`) proves record/restore/re-run identity at a
  frame boundary *and* mid-frame (mid-scanline, between two instructions);
  `agb.snap`/`agb.restore` demo verbs rewind the machine in-place with no reboot,
  echoing frame/PC/cycle/framebuffer-hash. The `DirectSoundFifo` learned
  `SaveState`/`LoadState` so the ring-FIFO model round-trips too. **Verdict
  (review-c A1 / survey #5): adopt now** — done, modeled on the GB/GBC core's Fork
  API. It reinstates the snapshot-round-trip Post capability we previously worked
  around and unblocks the rest of this partition.
- **Calibration (review-c D8):** "Waterbox can't snapshot GPU state" is *not* our
  constraint — our PPU is a CPU rasterizer producing a plain framebuffer in
  emulated memory, so the savestate has no GPU-state problem; don't import the
  Waterbox limitation as a phantom blocker.
- **See also:** rewind / runahead / rollback below (all now unblocked), hash
  divergence below.

### Rewind (delta-against-base ring buffer)

- **Source:** binjgb rewind, https://binji.github.io/posts/binjgb-rewind/ ;
  mgba `rewind.c`, https://github.com/mgba-emu/mgba/blob/master/src/core/rewind.c ,
  issue #36, https://github.com/mgba-emu/mgba/issues/36 ; BizHawk rewinder issues
  #2866/#2870, https://github.com/TASEmulators/BizHawk/issues/2866.
- **Finding:** full "base" snapshot every ~120 frames; intermediate frames as
  RLE/XOR deltas vs the nearest base in a fixed-capacity ring; input captured
  edge-triggered (changes far less than once per frame). binjgb (Donkey Kong):
  delta+RLE2/LEB128 ≈ 1.39% ratio vs 1.17% whole-frame zlib but ~10× smaller than
  compressing independent snapshots; ~70 KiB/s steady state, ~712 B/frame. mGBA
  runs the diff on a background thread — safe *only* because it never feeds back
  into emulated state. BizHawk's rewinder had "stuck once a frame is not stored" /
  "crash rewinding a full buffer" bugs — fuzz the wraparound.
- **Determinism fit:** fully compatible — deltas are pure `Span<byte>` comparisons
  over the deterministic image; keep any diff threading strictly read-only over a
  frozen snapshot.
- **Puck status: not built — now unblocked by savestate.** **Verdict (review-c A2 /
  survey #15): adopt later** (L, M once savestate exists — which it now does) —
  right after savestate. Pre-size and reuse the ring's `byte[]`s to avoid GC churn.
- **See also:** savestate above.

### Runahead (two-instance preferred)

- **Source:** RetroArch Run Ahead docs, https://docs.libretro.com/guides/runahead/.
- **Finding:** re-simulate N frames per displayed frame to cut input latency (cost
  ≈ `1+k` passes). Two-instance mode runs the lookahead on a *second* independent
  core instead of save/load-thrashing one — and is *faster*: Super Castlevania IV
  @5-frame runahead went 68→140 fps because it skips the save+load round-trip. A
  fast all-integer core running many× realtime makes the extra passes cheap.
- **Determinism fit:** fully compatible — requires only a correct serialize/
  deserialize round-trip. Two-instance is a natural fit for our fleet (independent
  machines already the model).
- **Puck status: not built — unblocked by savestate (single-instance) and cheap
  machine construction (two-instance).** **Verdict (review-c A3 / survey #16):
  adopt later** (M once savestate lands) — the two-instance variant is the better
  target; it doubles down on the fleet architecture we already want.
- **See also:** savestate above,
  [performance-techniques.md](performance-techniques.md) (the fleet pool).

### Rollback / lockstep cross-instance sync

- **Source:** melonDS *netplay saga ep 2*,
  https://melonds.kuribo64.net/comments.php?id=179 ; GGPO, https://www.ggpo.net/ ;
  Libretro netplay, https://docs.libretro.com/development/retroarch/netplay/ ;
  melonDS multiplayer (DeepWiki),
  https://deepwiki.com/melonDS-emu/melonDS/5.2-multiplayer-support.
- **Finding:** lockstep applies a fixed input-delay so all instances apply the same
  input on the same frame (melonDS delays 4); rollback applies local input
  immediately, reloads a saved state and re-simulates forward on a misprediction.
  Both reduce to save/load/advance-one-frame — exactly GGPO's three primitives.
  Rollback is strictly more demanding on savestate *speed* (every predicted-wrong
  frame is a full save+load+resimulate), which is why fast flat memcpy savestates
  are the enabling technology. mGBA's link approach instead streams the SIO bus
  traffic, leaning on the console's hardware determinism — a lighter alternative if
  link is the only use case. Watch the mundane traps (melonDS unsigned-underflow,
  save-while-polling races).
- **Determinism fit:** this is exactly the workload the contract is built for.
- **Puck status: not built — Tier-C reserved, `NullAgbLink` only.** More relevant
  to us than netplay: replay validation across fleet machines and a substrate for
  future cross-machine SIO/link determinism. **Verdict (review-c A4 / survey #22):
  test-first** (M lockstep, L–XL rollback; both need savestate + hash-divergence
  first) — prove round-trip identity, then decide lockstep vs rollback based on
  whether real link latency is ever in scope.
- **See also:** savestate above, hash divergence below.

### Hash-based divergence detection + BIOS/ROM pre-flight gate

- **Source:** Dolphin desync troubleshooting,
  https://www.smashladder.com/guides/view/26pv/desync-troubleshooting-guide ;
  Libretro netplay, https://docs.libretro.com/development/retroarch/netplay/.
- **Finding:** serialize+hash state at each tick, exchange/compare hashes (not full
  state); on mismatch, pull full snapshots at that tick and field-diff — hashing is
  the coarse detector, full-state diff the fine localizer, used in sequence. A
  pre-flight ROM/BIOS hash gate is a separate, cheaper safeguard — Dolphin calls
  mismatched ROM/BIOS hash "the top cause of divergences."
- **Determinism fit:** native fit.
- **Puck status: BIOS pre-flight gate implemented (this arc); per-tick fine-diff
  not yet.** The core previously compared two independently-built machines' full
  observable state after 200 frames. The BIOS pre-flight gate landed: `AgbBiosProfile`
  classifies a loaded BIOS by SHA-1 (retail vs replacement stub vs unknown/wrong-size),
  surfaced through the machine to `agb.status`, and the co-sim diagnostics
  (`--lockstep`/`--statetrace`/`--trace-cycles`) *refuse* a non-retail BIOS unless
  `--allow-replacement-bios` is passed — which would have prevented the documented
  "phantom cycle drift" session where the replacement BIOS was accidentally used
  for parity work. **Verdict (review-c A5 / survey #2): adopt now (gate) — done;**
  per-tick hash logging + the savestate-dependent fine-diff localizer remain
  **adopt later**.
- **See also:** savestate above, the BIOS profile in
  [emulator-landscape.md](emulator-landscape.md).

### RTC and wall-clock under determinism

- **Source:** libTAS options, https://clementgallet.github.io/libTAS/guides/options/ ;
  BizHawk RTC sync-setting issue #1870,
  https://github.com/TASEmulators/BizHawk/issues/1870.
- **Finding:** libTAS feeds a *virtualized* system time whose seed
  (`initial_time_sec`/`nsec`) is itself a recorded, replayable movie parameter,
  because games seed RNG from wall-clock time. BizHawk treats RTC as a sync setting
  (per-game `rtcEnabled` overrides), and a documented off-by-one-tick regression
  (pre- vs post-increment) shows how a virtualized clock threaded wrong through
  frame-boundary semantics becomes its own desync source. The unifying pattern: RTC
  is not *disabled*, it is *virtualized and made a recorded/replayable input*.
- **Determinism fit:** the exact posture the contract requires.
- **Puck status: already at SOTA.** RTC derives from `cycles / 16_780_000` off a
  fixed epoch, never `DateTime.Now` — the thing the field converged on. **Verdict
  (review-c B4): already at SOTA.**
- **See also:** [cartridge-saves-rtc-peripherals.md](cartridge-saves-rtc-peripherals.md)
  (the S-3511A protocol).

### TAS / movie formats and console verification

- **Source:** BizHawk rerecording, https://tasvideos.org/Bizhawk/Rerecording ;
  libTAS, https://tasvideos.org/EmulatorResources/LibTAS ; console verification,
  https://tasvideos.org/ConsoleVerification/Guide.
- **Finding:** BizHawk `.bk2` movies embed an input log + sync settings +
  firmware/game hash + a GUID; a mid-movie savestate embeds the *entire* movie and
  its frame count, and loading it triggers a frame-by-frame timeline check
  (GUID-mismatch / timeline-divergence / future-event errors) — continuous
  verification on every branch load. libTAS `.ltm` stores the whole virtualized
  time environment, not just input. Console verification (TASBot) replays a movie
  onto *real hardware* — the strongest end-to-end determinism proof; Extrems's
  GameBoyInterface keys GB/GBC/GBA inputs to *audio PWM cycle counts since
  power-on* rather than frame numbers, an alternate tick-base forced by
  real-hardware timing.
- **Determinism fit:** informational — a target if console-verifiable AGB output
  ever enters scope.
- **Puck status: surveyed, not deep-reviewed.** No movie format is in scope today;
  the `CommandSnapshot` per-tick input model is already the right substrate if one
  is ever wanted.
- **See also:** the deterministic-core contract above.

---

**Already at SOTA in this partition (credit, per review-c §B):** no-float/no-RNG
discipline (the single hardest determinism precondition, already met); deterministic
RTC from tick; dual live co-simulation vs mGBA + ares with a shared trace format,
`--lockstep`, and `statediff.py`; and — now — whole-machine savestate, which
restores the snapshot-round-trip Post capability and unblocks rewind, runahead, and
rollback. The open items are the delta-ring rewind, two-instance runahead, the
per-tick hash localizer, and the Tier-C rollback/lockstep substrate — all now
keystone-unblocked, none yet built.
