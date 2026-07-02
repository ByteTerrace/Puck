# The Ideal GamingBrick — GB / GBC / GBA build plan

Status: **planning complete, ready to build** · Date: 2026-07-02 · Scope now: **GB/GBC** (GBA deferred, seam kept ready)

This is the plan to evolve `experimental/Puck.HumbleGamingBrick` (the graduated LIVE
core) into the "ideal" deterministic Game Boy / Game Boy Color machine, using the
salvage harvested from the `_GBC Attempts` graveyard + the external C++ references.
It is derived from an 18-dossier forensic sweep of 8 code lineages (raw dossiers:
`tasks/wvwbb0svb.output`, `tasks/w06iwse9s.output`).

---

## 1. Mission — the three rules

1. **Deterministic.** Driven only by an abstract **integer** tick clock + inputs/config.
   No wall-clock, no RNG, no float in gameplay state.
2. **Carry-forward.** A GB game plays bit-perfectly on GBC and GBA; a GBC game on GBA.
   Architecturally: **ONE shared core parameterized by a capability profile**
   (`ConsoleModel` = a feature gate), never three emulators.
3. **Cross-generation determinism.** A DMG player and a GBA player stay bit-locked in
   the same GB link-cable multiplayer game. The **serial/link + timer/DIV** model is the
   crux — and it is barely covered by any reference test.

## 2. Settled decisions

| Decision | Choice |
|---|---|
| Tick base | **Unified integer 2²⁴.** DMG/CGB=2²², CGB-double=2²³, GBA=2²⁴ are exact powers of two → one integer tick expresses every generation with zero drift. |
| Tick execution model | **(B) CPU-driven per-tick lockstep** on the Q48.16 clock (no event queue → no equal-timestamp ambiguity), + **(C) idle-span fast-forward** layered in for speed. Reject catch-up + iterator-coroutine PPU (oracle only). |
| Boot | **Synthesize** the canonical per-profile post-boot state + own the DMG→CGB colorization; keep a selectable real-boot-ROM mode as a conformance oracle only. |
| Governance | **We author our own contract.** Reference suites are *evidence*, never a whole-suite CI gate. Tiers: Contract (gate) / Informative (canonicalize, track) / Reject. |
| Starting point | **Evolve the LIVE `Puck.HumbleGamingBrick`** in place — it already has the CPU, Q48.16 tick, docboy PPU, and fault-on-drift snapshot. |
| GBA | **Deferred.** Perfect GB/GBC + link determinism first; keep `ConsoleModel`/link seams `Agb`-ready. |
| Snapshot granularity | **Mid-frame full-instant** (arbitrary rewind/netplay in scope). |
| Link cable | First-class deterministic exchange object; guarded by a **net-new two-machine golden-replay** test, not by any reference suite. |

## 3. The contract (tiers)

**CONTRACT — bit-identical across generations, gates CI:**
1. Serial completion phase = a division of the shared 16-bit counter: tap **DIV bit-7
   (normal) / bit-2 (CGB-fast)**, one bit per **two** falling edges; SC-write *arms* but
   does **not** reset the counter. *(Evidence: gambatte `div_write_start_wait_read_if_*`,
   `start_late_div_write_*` — 46 ROMs; docboy/SameBoy/gambatte all agree on bit-7/two-edge.)*
2. Timer/DIV shared-counter edge model: TIMA on falling edge of (TAC-bit ∧ enable);
   DIV/TAC writes self-tick; 4-T reload-delay precedence (TIMA-write-ignored, TMA-write-lands).
   *(mooneye timer suite; gbmicrotest `timer_tima_phase`.)*
3. Mid-mode-3 PPU register-delay quirks: BGP-OR, CGB tile-select glitch, 8-bit window-line
   counter, CGB 2-T write delay, glitched-line-0 LCD-on. *(dmg/cgb-acid2 = 23040/23040; mealybug.)*
4. OAM-DMA-during-CPU bus conflict (SameBoy `is_addr_in_dma_use` model). *(mooneye `oam_dma`.)*
5. Component tie-break order at equal timestamp — **timer before serial is load-bearing**.
6. Deterministic power/uninit state; per-profile post-boot register + DIV seed as a pure
   function of the cartridge header.
7. APU **integer** channel digital output (PCM12/34) is contract; the float mix is not.

**INFORMATIVE — canonicalize per profile, tracked not gated:** post-boot DIV seed (see §6
crux 1) and register file; CGB boot-DIV prediction table; GB-on-GBC compat palette (title-hash
+ D-pad override); CGB color-correction gamma (output only). The gambatte `_dmgXX_outY_cgbXX_outZ`
files are the exhaustive list of where a GB game *legitimately* diverges per generation.

**REJECT — do not match:** dead ares scanline outputs; serialized ghost fields; cross-core
scorecard deltas taken under mismatched stop conditions; APU analog/revision-C glitches
(canonicalize to CGB-E); RTC/wall-clock; `oam_bug` on CGB/GBA; AGB `_gba.png` PPU washout.

## 4. Salvage assembly — subsystem → source

Lineage keys: **PL** = LIVE `Puck.HumbleGamingBrick`, **PN** = graveyard native Puck,
**BT** = ByteTerrace, **HAB** = HumbleAresBrick, **AGB** = Puck.AdvancedGamingBrick, **REF** = C++ refs.

| Subsystem | Seed | Grafts / corrections |
|---|---|---|
| Tick substrate | PL Q48.16 `Tick`/`MasterClock`/`ComponentClock` | + AGB `StepClocks` idle fast-forward |
| CPU / SM83 | PL hand-written (x/y/z/p/q, `ie_push`, halt-bug) | flat register file; devirtualize fan-out |
| Serial / Timer / DIV | shared-16-bit counter (PN architecture) | **bit-7/two-edge phase**, no-SC-reset, PL cached-TIMA mask, gambatte DIV-write re-phasing |
| PPU | docboy FIFO (dedup ×3→1; PL) | SameBoy OAM-DMA conflict + OAM-bug; `PpuTimingParameters` sweep→constants |
| APU | BT SameBoy-derived integer FSM | HAB DIV-driven frame-seq + dual-edge envelope; integer sampler |
| Cart / RTC / EEPROM | PN/PL mappers + tick-RTC | HAB `M93LCx6` + MBC7 driver if in scope |
| CGB compat | `ConsoleModel` gate + `CompatibilityPalette` (verified vs pandocs) | BT accessor-table mode-switch; **reject** Silo parallel-cores |
| Snapshot | PL fault-on-drift full-instant | port docboy's full PPU parcel set; serialize every live latch |
| Tooling | merge (see M0) | Sentinel pass-frame + bijection-compare + shootout grader + signed baseline + Scorecard |

**Traps to NOT carry forward:** per-tick interface-virtual `Tick()` fan-out (every lineage —
fix via BT sealed-field-cached `MachineCore`); delegate/`Func<>` indirection on the tick path;
DI-container in the hot provisioning path; float in APU live state; per-tick heap allocs.

## 5. Build milestones (GB/GBC)

**M0 — Baseline + scoreboard truth.** Build/run the LIVE core in a harness. Stand up the merged
conformance **scoreboard** (Sentinel pass-frame gate + structural-bijection image compare +
ShootoutRunner-over-our-framebuffer + SHA-256 signed baseline + Scorecard governance). CI gate =
"at least one decided result" + the (initially empty) contract set.
*Accept:* c-sp v7.0 corpus runs; signed baseline reproduces; one reused DI container, not per-ROM.

**M1 — Access-phase + DIV-seed (crux 1).** Pin the sub-machine-cycle access phase (PL's
`LeadingTCyclesBeforeRead=3` is an unverified hypothesis and is the root of the 0xABCC-vs-0xABCF
split). Prove it against blargg `mem_timing`(+`-2`) + mooneye `boot_sclk_align`/`boot_div-*`, then
**re-derive** the post-boot DIV seed against the chosen phase.
*Accept:* `mem_timing` green under the pinned phase; derived seed documented; ambiguity closed.

**M2 — Serial/timer to gold + Contract.** Apply bit-7/two-edge phase + no-SC-reset discipline +
cached TIMA mask + gambatte DIV-write serial-event re-phasing.
*Accept:* gambatte `serial`(46)+`tima`+mooneye timer green as evidence; timer/serial promoted to Contract.

**M3 — Devirtualize the tick path (perf).** Replace the interface `Tick()` fan-out with sealed
field-cached components (BT `MachineCore` pattern); add idle-span fast-forward. Establish the
first real fps measurement (none exists for any live engine on disk).
*Accept:* zero per-tick virtual dispatch/alloc in the hot loop (profiled); snapshot fault-on-drift
still clean; material fps gain recorded.

**M4 — Snapshot completeness (mid-frame).** Port docboy's full PPU parcel set; serialize every
live latch (incl. `haltBug`); mid-frame save/restore byte-identical.
*Accept:* save→restore at an arbitrary tick continues bit-identically over N frames; a
save/restore/replay differential fuzz passes.

**M5 — Two-machine link peer + cross-gen determinism (flagship rule-3 gate).** Build the
bidirectional `ISerialEndpoint` peer + a clock-owning link layer (incl. external-clock/slave path).
Canonicalize the start-of-link counter phase across profiles. Author the golden-replay **link-lock**
contract test (GB↔GB and GB↔GBC → bit-identical trace; replay-through-churn).
*Accept:* two linked machines produce bit-identical traces on a link game; the cross-gen test gates CI.

**M6 — APU integer-ize + graft.** Integer sampler (accumulator, not `double`); graft BT channel FSM
+ HAB frame-seq wiring; PCM12/34 = contract surface; sampler phase in the snapshot.
*Accept:* `dmg_sound`/`cgb_sound` as evidence; no APU float in live state; snapshot round-trips audio phase.

**Cart completeness (rolling):** core mappers (RomOnly, MBC1/2/3/5, MMM01, HuC1/3, PocketCamera)
first; MBC6/TAMA5 + MBC7 accelerometer + camera sensor as-needed (HAB `M93LCx6` ready to lift).

### Execution order (chosen) + ground-truth corrections

User reprioritized **M3 + M6 first** — front-load perf/float-hygiene before the accuracy/determinism
milestones. Reading the LIVE base corrected the synthesis on both (it generalized a ByteTerrace flaw
onto the core we actually chose):

- **M6 is nearly done — reclassify as verification, not a port.** LIVE `ApuComponent` **and**
  `AudioOutputComponent` are already fully integer: the channel FSM is SameBoy-quirk-accurate, the frame
  sequencer is already DIV-driven (bit 12 / bit 13), PCM12/34 is the integer contract output, and the
  mixer/resampler is an explicitly float-free integer rational accumulator whose `SaveState` writes
  nothing (host-output plumbing, snapshot-excluded → determinism already holds). The `double`
  sampler trap the synthesis flagged is **ByteTerrace's** APU, which we are not using. **M6 ⇒ run
  `dmg_sound`/`cgb_sound`, confirm pass.** No graft.
- **M3 must be profiled before it is touched.** `ComponentClock` already partitions components into two
  small typed `IClockedComponent[]` arrays iterated by `foreach` — not Silo's `List<>`+property-chain.
  The residual cost is only that `.Tick()` is a non-devirtualizable interface call (N/T-cycle). Whether
  that is the hot spot is unknown (CPU decode/bus routing may dominate). **Profile first**; flatten to a
  concrete sealed core only if justified, preserving the DI **registration order** (timer-before-serial
  is Contract §3.5) behind a frame-hash regression guard.

**Revised order:** thin harness → **profile** → M3 (if the fan-out is genuinely hot) → M6 (verify) →
then resume M0 (full scoreboard) → M1 → M2 → M4 → M5. The thin harness (boot ROM · step · frame-hash
baseline · snapshot round-trip · stopwatch · serial-result read) is the first artifact — the LIVE
project is library-only today, so no entry point exists yet.

## 6. Open empirical cruxes (resolved by measurement, tracked here)

1. **Access-phase + DIV-seed** (M1). The 0xABCC (docboy/PL) vs 0xABCF (PN, +3 phase-comp) seeds are
   *both correct for their own access phase*. Pin one phase, prove it, re-derive the seed — never copy
   a seed across lineages with different phases.
2. **Start-of-link counter-phase canonicalization** (M5). Serial/DIV/timer inherit a per-generation
   post-boot counter phase; **no ROM tests two consoles over a cable.** Decide: one canonical counter
   phase at power, or a resync at link-establishment. Pin the SC-write convention (gold no-phase-reset;
   reject PL's `m_edgeToggle=0` reset). Requires the net-new co-sim from M5 to verify.

## 7. Performance mandate

Repo targets `net10.0`; apply the `dotnet10-performance` skill on every hot path. Non-negotiables in
the per-tick loop: no virtual dispatch, no DI resolution, no delegate/`Func<>` indirection, no
allocation, no float in gameplay state. Devirtualize via sealed field-cached components; use
`delegate*<>` dispatch tables where a table is warranted; measure (no fps figure exists for any live
engine yet — M3 establishes the first).

## 8. Deferred — GBA carry-forward (future epic)

GBA-running-a-GB-game is a **mode of the SM83 core, not the ARM7TDMI**. When taken up: extend
`ConsoleModel` to `{Dmg, Cgb, Agb}`; route a DMG/CGB cart on GBA through the **same** shared SM83 +
shared-counter serial/timer + docboy PPU, gating only AGB deltas (KEY0 CPU-mode latch, initial
B=0x01, AGB OBJ quirk, AGB palette/color-correction). Keep the ARM7TDMI (`Puck.AdvancedGamingBrick`)
as a **separate** GBA-native core, cart-type-selected, sharing the one master clock + link layer.
**Design constraint now:** keep `ConsoleModel` and the M5 link layer `Agb`-ready so this is a
drop-in, not a rewrite.
