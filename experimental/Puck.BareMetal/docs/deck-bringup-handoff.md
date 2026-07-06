# Steam Deck Vulkan bring-up — continuation handoff

**Read this first if you're picking up the bare-metal-Vulkan-on-Steam-Deck work on a new
machine.** It captures the live state that isn't obvious from the code, plus the machine-local
setup another box needs. Companion docs: [`amd-vulkan-plan.md`](amd-vulkan-plan.md) (the staged
roadmap a–h + per-stage progress) and [`gfx103-bringup-spec.md`](gfx103-bringup-spec.md) (the
clean-room register/protocol spec). Vendored assets: [`../amdgpu/`](../amdgpu) (Van Gogh firmware +
MIT register headers).

## Where we are (2026-07-04)

Stages **(a) substrate, (b) probe, (c) GMC/GART, (d) PSP firmware load are DONE and clean** on real
hardware (boot 4 onward: the RLC restore-list fw-type fix took; all `rlc.srlc_*` loads return `ok`
every boot since).

Stage **(e): FIRST SILICON EXECUTION is achieved and the ring test fully passes.** Boot 6 landed
the first-ever executed packet (`SET_UCONFIG_REG` → `SCRATCH_REG0=0xDEADBEEF`); boot 7 landed a
full ring-test pass (`reg=Y mem=Y`, CACHE_POLICY=BYPASS for CPU-visible memory writes). The proven
recipe to get there is captured in full below.

**Still open, as of boot 12 (2026-07-04):**

1. **The IB+`RELEASE_MEM` fence has never signalled.** Every attempt beyond the plain ring test
   (i.e. anything that engages an `INDIRECT_BUFFER`) stalls the CE on `DE_COUNTER_UNDERFLOW`.
2. **A deterministic mid-CSB execution stall appeared once the full clear-state stream was
   emitted** (boot 11: stops at `RPTR=0xde`, dword 222, the start of the CSB's second
   `SET_CONTEXT_REG` block).
3. **An intermittent GART-block eraser**: 3 of the last 5 cold boots (8a, 10a, 12a) found the GART
   ring block read back all zeros after the CPU had written and verified it.

See "Open mystery #1," "Open mystery #2," and "Next session, in order" below for the current state
of each and the ordered plan. "Boot history" compresses all 12 boots into one table; several
theories that were presented as live hypotheses in earlier boots are now confirmed FALSIFIED — do
not re-derive them.

**The in-hand unit is a Steam Deck OLED (Sephiroth), NOT the LCD the plan originally targeted.** Same
gfx10.3.1 / PSP 11.5 silicon, so all vendored assets and register math apply unchanged. Hardware facts
established by real boots:

| Thing | Value |
|---|---|
| GPU | PCI `1002:1435` rev `0xae` at `04:00.0` (Sephiroth, "AMD Custom GPU 0932") |
| Register BAR | BAR5 `0x80500000`, 512 KiB, mapped UC (firmware assigns it; earlier "unassigned" was a probe bug) |
| Doorbell BAR | BAR2 `0xf8f0000000`, 2 MiB |
| Framebuffer BAR | BAR0 `0xf8e0000000`, 256 MiB (GOP scanout lives here) |
| FB_LOCATION | BASE `0xf400`, TOP `0xf43f` (MC, `<<24`); FB_OFFSET `0x440` ⇒ carveout at DRAM phys `0x4_4000_0000` |
| Carveout | 1024 MiB (`RCC_CONFIG_MEMSIZE`) |
| PSP | sOS alive warm (`C2PMSG_81` non-zero); SMU `C2PMSG_90 = 1` |
| IOMMU | none (no IVRS) → direct DMA; the `a.4-ii` IOMMU-domain work is moot on-target |
| x2APIC | absent (fine — everything polls) |

## The proven bring-up recipe

Everything in this section is SETTLED, hardware-verified fact — proven necessary by an actual
Deck boot, not a live hypothesis. This is the recipe stage (e) follows today, in order:

1. **Constants/golden init** (`PuckGpuConstantsInit`) — `GRBM_CNTL.READ_TIMEOUT=0xff`; per-VMID
   `SH_MEM_CONFIG`/`SH_MEM_BASES` (VMID 0..15, `SH_MEM_BASES` only for VMID != 0); `GDS_VMID1..15`
   BASE/SIZE zeroed (VMID0 left alone — HWS firmware needs it); 21 of 25 vangogh golden register
   entries applied (4 have no matching define in the vendored headers and are skipped). *Why:* this
   is amdgpu's `gfx_v10_0_constants_init` + golden-register block; without it the CP front ends are
   running against un-initialized VMID/GDS/scratch config that amdgpu never runs without.
2. **PSP `LOAD_TOC`/`SETUP_TMR`/`LOAD_IP_FW`** for sdma/ce/pfp/me/mec/mec2/RLC_G, **including the
   RLC save/restore-list sub-blobs at the correct firmware-type IDs (20/21/22, not 15/16/17)**. *Why:*
   the wrong IDs (15/16/17) are unrelated PSP fw-type slots — every load with them was silently
   rejected (`STATUS=0xffff0006`), so the RLC ran un-provisioned on every boot before the fix.
3. **`AUTOLOAD_RLC`** and poll `RLC_RLCS_BOOTLOAD_STATUS` bit31 + `CP_STAT==0`. *Why:* this is the
   stage-(d)-done condition; it's what actually loads PFP/ME/CE/MEC microcode into their
   instruction caches.
4. **PSP/RLC autoload wires all three gfx front-end icaches itself — do not touch them.** Boot 12's
   `GpuIcacheDump` found `CP_PFP_IC_BASE`, `CP_ME_IC_BASE`, and `CP_CE_IC_BASE` **all already valid
   TMR addresses** (`0x4_4447_0000` / `0x4_444b_4000` / `0x4_4442_c000`, `BASE_CNTL=0x10`) before
   Puck ever wrote to them. *Why this matters:* the boot-11 "CE has no microcode" hypothesis is
   FALSIFIED — the CE icache was fine all along, wired by PSP/RLC autoload exactly like PFP/ME. Our
   own `CP_CE_IC_BASE` write (`PuckGpuCeIcacheFix`) did not even stick — the register is
   firmware/RLC-owned on this path. Do not reprogram it.
5. **Live MEC with a designated, genuinely-active KIQ** (`PuckGpuKiqBringUp`) — `RLC_CP_SCHEDULERS`
   two-step write (designation byte, then OR in the valid bit `0x80`), a real `v10_compute_mqd`
   image committed via **direct MMIO** (no `MAP_QUEUES`) to the `CP_HQD_*` register file, at
   `me=2, pipe=1, queue=0` (Vangogh's MEC topology walked top-down per `amdgpu_gfx_kiq_acquire`).
   *Why:* the gfx PFP processes NO ring at all — any interface, any memory location — until a real,
   running KIQ HQD exists behind the scheduler designation. A designation bit alone
   (`PuckGpuKiqPoke` with no live queue) is NOT sufficient — proven insufficient at boot 5's "hqd-poke"
   attempt, isolated from that same boot's full KIQ bring-up, which worked.
6. **Gfx ring = HQD/MQD interface, not legacy `CP_RB0_*`.** Direct-MMIO `CP_GFX_HQD_*` registers +
   a `v10_gfx_mqd` image, committed in `gfx_v10_0_gfx_queue_init_register` (`BRING_UP_DEBUG`) order.
   *Why:* gfx10.3 firmware ships with `amdgpu_async_gfx_ring=1`; the legacy `CP_RB0_*` interface is
   unserved — two early boots (2, 3) proved the legacy ring dead identically regardless of doorbell
   vs. MMIO wptr delivery.
7. **Halt → program every register → un-halt → poll `CP_STAT` idle, once per attempt.** *Why:*
   mirrors `gfx_v10_0_cp_gfx_start`/`_cp_gfx_enable`; programming ring registers underneath an
   already-running (un-halted) CP produced a PFP that never recovered into a clean first fetch.
8. **CE must be un-halted.** *Why:* a CE-halted attempt (`hqd-noce`, boot 6) wedged the PFP at a
   new PC (`0xa7`) with zero consumption — worse than CE-enabled. The Constant Engine is vestigial
   on RDNA2 in the sense that drivers never send it real IBs, but gfx10.3 still requires it running.
9. **Full clear-state CSB stream** (`gfx10_cs_data`, 961 dwords total incl. the two ring-test
   packets) + `CLEAR_STATE` + `SET_BASE CE_PARTITION(3)`. *Why:* boots 6–9's minimal/empty preamble
   combination (`PREAMBLE_BEGIN/END` bracketing nothing) is a configuration no real amdgpu driver
   ever runs; boot 11 proved the full CSB gets the CP measurably deeper (376 dwords fetched, 222
   retired) before hitting the next blocker. `CE_PARTITION_BASE=3` (not 2) is confirmed correct
   against the vendored `nvd.h`.
10. **`WRITE_DATA` needs `CACHE_POLICY=BYPASS`** for CPU-visible memory writes (GL2 residency — the
    CPU-side HDP flush used by polls does not touch GL2; default `WRITE_DATA` policy is LRU-cached
    and invisible to a plain HDP-flushed CPU read). *Why:* boot 6 executed the memory write
    (`WR_CONFIRM` honored, CP idle) but read zeros until this fix landed in boot 7.
11. **64-bit doorbell** (index `0x116`, NBIO `BIF_DOORBELL_APER_EN` enabled). *Why:* MMIO
    `CP_RB0_WPTR` is inert on gfx10 — writes read back 0; the doorbell is the only path that
    actually advances the CP's view of the ring, confirmed by `DOORBELL_HIT` latching and the wptr
    showing up once the NBIO aperture-enable bit was set.
12. **All PM4 encodings are `nvd.h`-verified**, not guessed. The AMD amdgpu IP-block sources
    (`gfx_v10_0.c`, `nvd.h`, `soc15d.h`, `v10_structs.h`, `clearstate_*`, `asic_reg/*`) are
    **MIT-licensed** (X11 permission notice) — vendored verbatim under `../amdgpu/include/`. Only
    the DRM-framework glue (scheduler, TTM, `amdgpu_drv.c`-style plumbing) is GPL. This was
    discovered 2026-07-04 and immediately tightened every previously-"UNVERIFIED, behavior-only"
    PM4 field against the real header — all fields checked out correct except
    `CE_PARTITION_BASE` (we had 2, `nvd.h` says 3 — fixed).

## Open mystery #1: the deep-execution stall

**Reframed as of boot 12.** Five boots (7–11) treated `CP_STALLED_STAT3 =
CE_WAITING_ON_DE_COUNTER_UNDERFLOW` as "the CE is wedged" and chased CE-side fixes (partition
index, CE icache reprogram, halt/un-halt ordering). Boot 12 downgrades that whole line of
interpretation:

- **The CE is ALIVE.** `CP_CE_INSTR_PNTR` moves between `0x9b` and `0x9c` after un-halt (and reads
  `0` while halted, as expected). This is not a frozen PC.
- **`CE_WAITING_ON_DE_COUNTER_UNDERFLOW` is plausibly the CE's normal idle signature**, not a wedge
  — a CE with nothing queued from the DE naturally reads as "waiting on the DE counter." The five
  boots of "the CE is wedged" interpretation are hereby marked DEAD; treat them as "the CE was
  probably fine all along," not as live theories to re-test.

**The real blocker signature, per boot 12:** the **PFP** is in a wait-loop at PC `0x2fd`/`0x2fe`.
`CP_CPF_STALLED_STAT1` bit0 (`RING_FETCHING_DATA`, `gc_10_3_0_sh_mask.h` line 6520) is set.
`CP_STAT=0x94008200` persists **across a halt/un-halt cycle within the same boot** — meaning a
stuck first attempt contaminates every later attempt in that boot (consistent with the boot-10
finding that a CE/CP wedge does not clear on halt→un-halt).

**Best data point so far: boot 11's deterministic stall.** Two identical fail dumps in the same
boot both stopped at `RPTR=0xde` (dword 222) = the start of the CSB's *second*
`SET_CONTEXT_REG` block (`reg_count 272`), with `ME_PC` at `0xb65`. The ME stopped exactly at a
packet boundary in the CSB — not mid-packet, not at a random offset. This is the sharpest lead for
next session (see plan item d below): compare the actual emitted dwords around that boundary
against what `gfx10_cs_data` says should be there.

## Open mystery #2: the GART-block eraser

**3 of the last 5 cold boots (8a, 10a, 12a)** found the 8 KiB ring in the GART test block (CPU
`0x180400000`) read back **all zeros** from the CPU, after the CPU itself had written the packets
and a RING-pre readback had verified them intact. Boot 12a: pre intact, post zeros, CPU-probe=Y
throughout (rules out "the CPU's own mapping is broken"). It has **only ever hit the GART block**
— the carveout ("vram") ring has never once been erased.

**Suspects, still undecided:**
- MEC/KIQ activity — the KIQ MQD/EOP/ring buffers live in the **same** GART block as the gfx ring
  under test, so any KIQ-side scribble would land exactly here and nowhere else.
- HDP/write-combine aliasing between the CPU's view and the GPU's view of the block.
- Something in Puck's own kernel (a stray DMA-capable write, a stale mapping).

**Key gap: no clean FIRST-attempt carveout run has ever happened.** Every "vram"/carveout attempt
to date ran *after* a stuck first (GART) attempt in the same boot, so its result is contaminated by
whatever state the first attempt's CE/CP wedge left behind (per the boot-10 finding above). There is
currently zero clean data on whether the carveout block is immune to the eraser or has just never
been tested first.

## Next session, in order

a. **Swap attempt order: run the carveout ("vram") block FIRST.** One clean vram-first run
   simultaneously (i) tests the deep-execution/CSB-boundary stall without any contamination from a
   prior GART attempt, and (ii) removes GART entirely from the fetch path, so if the eraser doesn't
   recur, GART-specific mechanics (not just "ran second") become a live suspect again. Also **move
   the KIQ MQD/EOP/ring buffers into the carveout block**, isolating the MEC/KIQ-scribble suspect
   from the gfx block under test.
b. **Remove `PuckGpuCeIcacheFix`'s reprogram.** Proven ineffective (the register didn't stick) and
   now known to be firmware/RLC-owned — reprogramming it is pointless and risks regressing a
   register that already holds a valid TMR address. Keep `GpuIcacheDump` and the `CE_PC` dumps;
   they're diagnostic and still useful.
c. **Eraser instrumentation, if it still matters after (a).** Re-read the ring HEAD/RPTR during the
   existing SCRATCH poll loop (not just before/after the kick) to timestamp *when* erasure occurs
   relative to CP execution, rather than only knowing pre-kick-vs-post-fail state.
d. **Boot-11 stall analysis.** Dump the exact ring dwords around index 222 in the fail path
   (`RING[0xd8..0xe4]`) and diff against what was actually written for the CSB's second
   `SET_CONTEXT_REG` block. If the ME deterministically stops at that block's start, check its
   first operand/values against `gfx10_cs_data` for an emission bug — an off-by-one in a
   `reg_count`, or a section-boundary miscount, is the leading suspect given how exactly the stop
   lands on a packet boundary.
e. **Strategic: prefer porting over re-deriving.** The amdgpu IP-block sources are MIT
   (`gfx_v10_0.c`, `nvd.h`, `clearstate_*`, `v10_structs.h` — all vendored or vendorable) — favor
   porting that code directly over continuing to re-derive register sequences boot-by-boot.
   Genuinely-GPL artifacts (if ever needed) follow the RADV-closure model: pulled by script, staged
   to the ESP, never vendored. Note the Deck currently has no kernel-drivable NIC
   (`[net] no virtio-net device found`), so an on-target HTTP pull needs a USB/WiFi NIC driver
   first — until then, ESP staging from the dev box is the only path.

## Boot history

All 12 boots to date, compressed. Falsified theories are marked DEAD in the outcome column — they
are historical record, not live leads.

| Boot | Build change tested | Outcome |
|---|---|---|
| 1 | Initial halt→program→un-halt ordering fix (stage (d) no longer un-halts; stage (e) owns halt→program→un-halt) + legacy `CP_RB0` ring, doorbell kick | `CP_STAT` busy, never idles; `RPTR=0`, no fetch. Preamble-only hypothesis (missing `PREAMBLE_CNTL`/`CONTEXT_CONTROL`) tried same boot and **DEAD** — wptr advanced correctly, CP still didn't move `RPTR`. |
| 2 | HQD/MQD interface (A "hqd-gart"/B "hqd-vram") replacing legacy `CP_RB0_*`, direct-MMIO gfx HQD | A and B failed identically: `RPTR=0`, `CP_CPF_STATUS` wedged idle-but-busy, zero outstanding memory/translation traffic. `PFP_HDR` all-zero (FIFO fill pattern, never fetched). Exonerated GART specifically (vram attempt bypasses page tables, failed the same way). |
| 3 | Added C "rb0-mmio" (legacy ring, no doorbell); RLC srlc fw-type IDs still wrong (15/16/17) | All three (A/B/C) failed byte-identically across ring interface, memory location, and wptr-delivery axis. `CP_PFP_INSTR_PNTR` caught moving (`0x263..0x266`) — PFP alive, spinning a global wait loop. Root-caused: RLC restore-list PSP loads were rejected (`STATUS=0xffff0006`) due to wrong fw-type IDs — RLC ran un-provisioned every boot to date. |
| 4 | RLC fw-type IDs fixed (20/21/22); added `constants_init`+golden-register block; two graded experiments (A0 "hqd-poke" designation-only, A1 "hqd-kiq" full KIQ bring-up) replacing the old A/B/C matrix | RLC loads confirmed `ok`, all three — stage (d) fully clean from here on. Ring still stalled. `RLC_CP_SCHEDULERS` showed no valid KIQ designation bit set — scheduler-designation hypothesis raised. A0 (designation-poke only, no live KIQ) not yet tested this boot; queued for boot 5. |
| 5 | A0 "hqd-poke" (designation bit only) vs A1 "hqd-kiq" (full direct-MMIO KIQ bring-up) | **MAJOR MILESTONE:** A1 unblocked the gfx PFP for the first time ever — all 10 ring dwords consumed, PC moved to new loops. A0 (poke alone) failed identically to every prior boot — **DEAD: scheduler-designation-bit-alone is confirmed insufficient**; a genuinely live KIQ HQD is required. New stall found one layer deeper: CE wedges on `DE_COUNTER_UNDERFLOW`. Also found and fixed a `PuckLogFlush` truncation bug (stale bytes past new EOF on a shorter log). |
| 6 | B1 "hqd-noce" (CE held halted) vs B2 "hqd-ce" (CE running + `SET_BASE CE_PARTITION`) | **FIRST SILICON EXECUTION:** B2 executed `SET_UCONFIG_REG` — `SCRATCH_REG0` read back `0xDEADBEEF`, own packet headers appeared in the FIFOs, `CP_STAT` went fully idle. B1 (CE halted) made things **worse** — PFP wedged at a new PC, zero consumption — **DEAD: CE-halted is confirmed worse, not a workaround.** Remaining gap: `mem=N` (GL2 residency). |
| 7 | `CACHE_POLICY=BYPASS` on `WRITE_DATA`; first `RingIbFence` (IB + `RELEASE_MEM`) attempt | Ring test **FULL PASS** (`reg=Y mem=Y`) — GL2-residency theory confirmed. First IB/fence attempt stalled: `CP_STALLED_STAT3=CE_WAITING_ON_DE_COUNTER_UNDERFLOW`, engaging at the first `INDIRECT_BUFFER`. `CE_PARTITION_BASE` value (2 vs. spec) flagged as build-8 hypothesis. |
| 8a | `CE_PARTITION_BASE=3`; cold boot | New failure mode: ring read back all zeros from the CPU post-kick (first occurrence of the GART eraser). Suspects raised: low-RAM identity-block flake vs. a GPU-side scribble agent. |
| 8b | Same build as 8a, second cold boot | Ring intact this time (8a's zero-ring was transient/intermittent, not systematic). IB/fence failed identically to boot 7 — **DEAD: `CE_PARTITION_BASE` index 2-vs-3 is confirmed to make no difference to the IB wedge.** New detail: PFP never launches the DE IB fetch at all (`ROQ_INDIRECT1_BUSY` stays clear). |
| 9 | (Design boot, folded into 10/11 builds) RING-pre/post split, CPU-probe, carveout-fallback attempt added for eraser diagnosis | — |
| 10 | Full clear-state CSB (`gfx10_cs_data`, 961 dwords) replacing the minimal/empty preamble, first attempt | Attempt 1 (GART): RING-pre intact, RING-post all zeros, CPU-probe=Y — confirms a GPU-side eraser, not a CPU write-path bug (second occurrence). Attempt 2 (carveout, run after attempt 1's CE wedge): own ring stayed intact but nothing executed — **found that a CE stuck on `DE_COUNTER_UNDERFLOW` does NOT clear across a `CP_ME_CNTL` halt→un-halt**, so this result is contaminated/inconclusive, not a clean carveout data point. **DEAD: "CLEAR_STATE-alone/minimal-preamble is sufficient" era retired** — full CSB content, not just correct ordering, is required. |
| 11 | Full CSB stream, first real boot with content (build 11 was designed at boot 10) | Went deeper than ever: PFP fetched 376 dwords, ME retired 222 (`RPTR=0xde`, the second `SET_CONTEXT_REG` block's start), `ME_PC=0xb65`. Stalled on the same `CE_WAITING_ON_DE_COUNTER_UNDERFLOW` signature, but now mid-CSB, deterministically, twice, identically — not at a tail boundary. Boot-10's "ring erasure" reading corrected: re-examined, it was legitimate mostly-zero CSB register *content*, not an eraser (eraser theory not retired globally — see boot 12a — just not what boot 10's attempt-2 showed). Root-cause hypothesis raised: CE running without valid microcode since boot 1 (never directly tested — `CP_CE_INSTR_PNTR` had never been read). |
| 12 / 12a | `GpuIcacheDump` (all three front-end icache bases + `CP_CE_INSTR_PNTR`) + `PuckGpuCeIcacheFix` (unconditional CE icache reprogram) | **DEAD: "CE has no valid microcode" hypothesis falsified** — `CP_PFP_IC_BASE`/`CP_ME_IC_BASE`/`CP_CE_IC_BASE` all already held valid TMR addresses before Puck touched them; the CE icache reprogram write did not stick (register is fw/RLC-owned). **DEAD (five-boot span, 7–11): "the CE is wedged" reframed** — `CP_CE_INSTR_PNTR` moves (0x9b↔0x9c after un-halt), i.e. the CE is alive; `DE_COUNTER_UNDERFLOW` is plausibly its normal idle readout, not a stuck state. Real blocker re-identified: PFP wait-loop at PC 0x2fd/0x2fe, `CP_CPF_STALLED_STAT1` bit0 (`RING_FETCHING_DATA`) set, `CP_STAT` persisting across halt/un-halt within a boot. Boot 12a (cold): GART-block eraser recurred (third occurrence) — pre intact, post zeros, CPU-probe=Y. |

## Getting logs off the Deck (no serial port on the Deck)

The panel is the only live console. To get **exact text** instead of photographing the panel:

- The kernel tees all output into a RAM buffer and, at `exit_group`, stashes it to a UEFI variable
  (`PuckLog`) via Runtime Services (`PuckLogPersist`).
- On the **next** boot, while the ESP is still writable, it writes the previous boot's log to
  `\PuckLog.txt` on the USB and clears the variable (`PuckLogFlush`, called from
  `PuckEfiPreloadGpuFw`).
- So: boot (runs + stores log), boot again (flushes prior log to the stick), then read
  `\PuckLog.txt` on the dev box. **One boot of lag.** The panel still shows everything live as a
  fallback; the parked final screen ends with a `bring-up:` verdict line.
- **Both boots of the usual two-boot cadence are COLD** — the Deck is fully power-cycled between
  them, not warm-rebooted — and **every analyzed log is the FIRST boot of a build**, which gets
  flushed to the stick by the *next* boot. A given build's own log therefore needs one extra boot
  beyond whatever boot number you're reading; warm-boot verdicts (the second boot of a pair) have
  only ever been seen live on the panel, never in a flushed log file.

## Dev loop + machine-local setup (what a fresh box must rebuild)

These are gitignored / machine-local and must be regenerated on a new machine:

- **RADV musl closure** (`.qemu/radv/*.so`, ~25 MB): build in WSL with
  `radv/build-radv-musl.sh`. **CRLF gotcha:** the repo checkout is CRLF; strip `\r` before running
  under `sh` (`tr -d '\r' < build-radv-musl.sh > /tmp/x.sh && sh /tmp/x.sh`, or run via
  `wsl -d <distro> -u root`). It stands up an Alpine-musl rootfs and builds Mesa RADV LLVM-free/WSI-free.
- **Build the EFI image + boot in QEMU:** `samples/EfiLinux/run-qemu.ps1` (needs a VS Dev shell for
  `cl.exe`/`ml64.exe` and QEMU+OVMF; paths are near the top of the script — adjust per box). QEMU has
  no RDNA model, so it only exercises the substrate + the RADV userspace ceiling
  (`COMPUTE PIPELINE COMPILED`), and the GPU bring-up cleanly no-ops ("no register BAR mapped").
- **Stage a boot USB for the Deck:** `samples/EfiLinux/stage-deck.ps1 -Target E:\` copies
  `EFI\BOOT\BOOTX64.EFI` + the `radv\` closure + the `amdgpu\vangogh_*.bin` firmware to the stick.
- **Boot the Deck:** power off, insert the stick (USB-C hub), hold **Vol-Down + Power**, pick the USB
  entry. The firmware logo vanishing = our image took the machine.

## Verification

QEMU green = `run-qemu.ps1` ends with `vktest: COMPUTE PIPELINE COMPILED (ACO -> RDNA2 ISA)` +
`exit_group(0)`, shows the 9 `[esp] gpu fw` preload lines, and the GPU bring-up prints its skip line.
Real bring-up (stages b–e) only happens on the Deck. There is no unit-test story; the panel/`PuckLog`
output is the evidence.
