# Deck demo session plan — next hardware session

**Goal: maximum verified progress toward "Puck Demo pixels on the Deck panel."** Written
2026-07-09 from [`deck-demo-research.md`](deck-demo-research.md) (the consolidated research pack,
verdicts V1–V5) and [`deck-bringup-handoff.md`](deck-bringup-handoff.md) (ground truth, boot
table, plan items a–e). Read both before executing this. All `file:line` cites are against
`compat/native/puck-efi.c` unless another file is named.

## 1. Goal & non-goals

**Session goal.** Close rungs 0–2 of the ladder below on hardware, with rung 1 — **the first
fence ever signalled on this silicon, via a MEC compute queue** — as the headline objective.
Rung 1 is the single highest-leverage unknown in the whole program (research §4 item 7): if a
compute IB executes and `RELEASE_MEM` signals, the entire RADV/DRM-shim path (rung 3) unblocks
and the remaining work is bounded software. If it wedges like gfx, the failure data reshapes
everything downstream, and getting that data early is worth the whole session by itself.

**Non-goals for this session.** Rung 3's kernel backing (CS parse, GPUVM, carveout allocator) is
*dev-box* work — write it before/after, don't debug it on the Deck. Rung 4 (NativeAOT guest,
surfaceless device factory) and rung 5 (demo-as-guest) touch the Deck only after rung 3 is
QEMU-green. Input/xHCI, audio, networking: out of scope entirely (research §2.7 — input is the
only demo-critical one and it gates nothing at this rung level). Do not chase the gfx ring past
the budgeted opportunistic probes — **the demo path is compute + GOP blit; gfx is now a
diagnostic sideshow** (V1/V3).

**Standing rules.**
- **Port, don't re-derive** (handoff item e): every new register sequence this session — MEC MQD
  init, PQ_CONTROL bits, compute-IB control dword, SOFT_RESET_CP — is ported from the vendored
  MIT sources (`gfx_v10_0.c`, `nvd.h`, `v10_structs.h`) or from tinygrad's
  `runtime/support/am/ip.py` recipe, then desk-checked, never guessed.
- **Contamination discipline**: a stuck CP attempt poisons the rest of the boot
  (`CP_STAT=0x94008200` persists across halt/unhalt). Therefore **run the MEC compute proof as
  attempt 1 of its boot, before any gfx attempt**, and order every boot's attempts
  cheapest-conclusion-first.
- **Everything lives in the carveout** until the eraser is understood (Blocker 3 mitigation).
  Placement decisions come from the carveout map (research §2.2) — do not overlap the PSP
  window at carveout +64..96 MiB; record any new placement back into that map.
- **Power discipline**: start ≥80% charged; the USB-C hub must be PD-passthrough and verified
  charging with the stick inserted (photograph the battery indicator at session start and end).
  A dead Deck ends the session; each boot runs panel + GPU at the VBIOS P-state with
  minutes-long parked screens.
- **Recovery posture**: worst case is a wedged boot, not a brick — our image never touches
  internal storage and the boot picker (Vol-Down+Power) is firmware-owned. A hung machine:
  hold Power ~10 s to force off. Keep the SteamOS recovery USB in the session kit.

## 2. Pre-session checklist (all dev-box, before touching the Deck)

Land these as code changes, build with `samples/EfiLinux/run-qemu.ps1` (VS Dev shell), and get
QEMU green (`COMPUTE PIPELINE COMPILED` + `exit_group(0)` + 9 `[esp] gpu fw` lines + GPU
bring-up skip line) on every build. QEMU can't execute RDNA silicon, but it proves the builds
don't regress the substrate — a wasted Deck boot costs a full restage cycle.

**Two builds, staged as two USB images (or one stick restaged between).** Build 13 = rung 0.
Build 14 = rung 1 (+ rung 2 riders). Prepare both *before* the session so the Deck loop is pure
boot-photograph-restage.

### Build 13 — "instrument + isolate" (rung 0)

0. **Wire `PuckLogPersist` into `PuckHang`** (do this first — one-line fix, protects every
   other item). Today persistence fires only on the `exit_group` path (`:2133`); every
   `PuckHang()` park — which is how most GPU-attempt failure modes end — loses its exact log,
   leaving only the panel photo. While there, confirm the persist path's **capacity and
   truncation direction** against the enlarged instrumentation below (UEFI variable stores have
   firmware-specific limits, and a flush truncation bug already bit once at boot 5): the TAIL
   must survive — verdicts land late in the log.
1. **Carveout-first attempt order** (handoff item a). Make the carveout ("vram") ring attempt 1
   and the GART attempt 2 (or drop GART to a flag). **Move the KIQ MQD/EOP/ring buffers into the
   carveout too** (`VGH_RT_KIQ_*` `:3678-3682`; carveout window layout `VGH_PSPWIN_*`
   `:3969-3983`) — this isolates the MEC/KIQ-scribble eraser suspect in the same boot.
2. **Delete `PuckGpuCeIcacheFix`'s reprogram** (handoff item b): the call at `:5851`, the
   `CP_CE_IC_BASE` writes `:5815-5817`, and the stale CE-microcode narrative comment
   `:5736-5767`. Keep `GpuIcacheDump` and the `CE_PC` reads — diagnostic, still useful.
3. **`CP_PFP_HEADER_DUMP` ×8 in the fail dump** (V3's disambiguator): read the same MMIO offset
   8 times (FIFO pops) and log all 8. Last header `0xC0D76900` = extent 2 never latched
   (fetch stall); `0xC1106900` = decoded and hung applying (parse hang). Port the access
   pattern from upstream commit `867cf768cbe3`. Do NOT re-audit the CSB emission — V3's
   byte-exact desk-check refuted the emission-bug hypothesis (handoff item d as originally
   written is dead); the instrumentation replaces it.
4. **`GRBM_SOFT_RESET.SOFT_RESET_CP` un-wedge between attempts** (port from
   `gfx_v10_0_soft_reset()`): assert ~50 µs, deassert, then gate the next attempt's halt step on
   `CP_STAT==0`, logging whether the reset actually cleared `0x94008200`. This is the fix for
   same-boot contamination; halt/unhalt does not drain a stalled fetch.
5. **Eraser timing instrumentation** (handoff item c): inside the existing SCRATCH poll loop,
   re-read ring HEAD/RPTR and one sentinel ring dword each iteration and log transitions —
   timestamps erasure relative to CP progress instead of pre/post-only.
6. **GOP measurements rider** (research §4 items 1–4, zero GPU dependency):
   - Read and log `Info.PixelFormat` (u32 at `Info+12`) in `PuckGopCapture` (`:2876-2893`).
   - Log `g_fb.phys − 0xF8_E000_0000` (BAR0 aperture offset, for future GPU-write/DCN work).
   - Draw an asymmetric corner-marker pattern once (confirms the 90° rotation mapping and the
     visible-800 vs pitch-832 padding on the OLED).
   - Timed full-frame fill: 800×1280 through the WC mapping, `rdtsc` around it, once with and
     once without a trailing `HDP_MEM_FLUSH` write (`VGH_HDP_MEM_FLUSH` `:3413`). Log cycles
     and the derived ms/frame.
7. **Dump-block extension**: add `CP_CPC_STATUS`, `CP_CPC_STALLED_STAT1`, and
   `CP_MEC_ME1_HEADER_DUMP` ×8 to the standard fail dump now, so build 14's MEC attempts have
   them from boot one.

### Build 14 — "MEC compute proof" (rung 1, + rung 2 riders)

1. **User compute queue at me=1/pipe=0/queue=0**: clone `PuckGpuKiqBringUp` (`:5567-5734`) into
   a `PuckGpuMecBringUp` — same direct-MMIO `v10_compute_mqd` HQD-commit recipe (the proven
   mechanism; do NOT route through KIQ `MAP_QUEUES`, which has never executed a packet, V1),
   with non-KIQ `PQ_CONTROL` bits ported from `gfx_v10_0.c`/tinygrad. All buffers (MQD, ring,
   EOP, fence, IB) **in the carveout**.
2. **Widen `CP_MEC_DOORBELL_RANGE_LOWER/UPPER`** — today pinned to the KIQ slot (`:5705-5706`);
   the user queue needs its doorbell index inside the range. **Name the index by porting, not
   choosing** (review gap): take the compute-ring doorbell assignment from amdgpu's navi10
   doorbell layout (`AMDGPU_NAVI10_DOORBELL_MEC_RING0` in `amdgpu_doorbell.h` — desk-check the
   value when porting) and the `CP_HQD_PQ_DOORBELL_CONTROL` OFFSET/EN encoding from
   `gfx_v10_0_compute_mqd_init`. For the KIQ-PQ probe (item 4), desk-check the KIQ's own
   doorbell index by reading it back from the existing KIQ MQD — do not assume 0.
3. **Staged self-test, in-boot ordered**: (i) `WRITE_DATA` ring test with `CACHE_POLICY=BYPASS`
   (the amdgpu compute ring-test shape); (ii) IB test — `INDIRECT_BUFFER` control dword **with
   `INDIRECT_BUFFER_VALID` (bit 23) set** (VERIFIED against mainline `gfx_v10_0.c`:
   `gfx_v10_0_ring_emit_ib_compute` builds `control = INDIRECT_BUFFER_VALID | ib->length_dw |
   (vmid << 24)`, the gfx emitter omits the VALID bit — parameterize `RingIbFence`
   `:5339-5342`) + `RELEASE_MEM` EOP fence, minimal shape, no CONTEXT_CONTROL/
   FRAME_CONTROL/VM flush, vmid=0 (verified upstream `amdgpu_ib.c:258-270` shape).
4. **KIQ-PQ probe rider** (cheap, V1's suggested experiment): one benign packet through the
   already-active KIQ PQ at doorbell 0, result logged. First-ever PM4 through the KIQ ring;
   settles whether KIQ-as-executor works independent of the user queue.
5. **GPU-write-to-scanout rider** (rung 2 stretch, research §4 item 10): one ring-resident
   `WRITE_DATA CACHE_POLICY=BYPASS` targeting `mc = 0xF4_0000_0000 + (fbPhys − 0xF8_E000_0000)`,
   writing a visible sentinel block of pixels. Rides whichever ring test passes; zero new
   infrastructure. Gate it to run only after a passing ring test.
6. **Note in code**: the mec2→`CP_MEC` PSP fw-type mapping is flagged UNVERIFIED (`:4480`) —
   have the build log which MEC fw the PSP actually loaded, so a mec-microcode failure is
   attributable.
7. **Gfx attempt demoted to last**: keep the gfx CSB attempt in build 14 but as the final
   attempt of the boot, after SOFT_RESET_CP, purely to collect HEADER_DUMP data. It must not
   run before the MEC proof.

### Parallel dev-box track (not gating the Deck session; do whenever blocked)

**Day-one blocker first (review gap): stand up the NativeAOT `linux-musl-x64` publish
environment.** NativeAOT cannot cross-compile Windows→Linux, and `mkguest.ps1` hardcodes
Ubuntu/glibc (C guests only) — the AOT publish must run in a musl-capable Linux environment
(the WSL Alpine chroot + .NET 10 SDK + clang/musl toolchain). Prove it end-to-end with the
hello-GC guest before scheduling any rung-4 work.

Then the rung 3/4 pre-work, all QEMU-testable: fix `mremap` (`:2004` — currently an EE-startup
infinite loop for any NativeAOT guest), populate `sysinfo.totalram/mem_unit` (`:1992`), grow the
guest arena to ≥512 MiB (`PUCK_GUEST_ARENA_BYTES` `:500`), then a NativeAOT dynamic (Route A)
hello-with-`GC.Collect()` guest smoke in QEMU (research §4 item 13; set
`DOTNET_EnableDiagnostics=0` if eventpipe startup shows up). Engine side: the surfaceless
`IGpuDeviceContext` factory (mirror `VulkanPhysicalDeviceSelector` minus the present-queue
requirement; no WSI extensions, no validation). Cheap closure of a version-gap inference:
grep the staged **Mesa 26.1.1** tarball in the chroot for the ★ classification switch
(`ac_gpu_info.c` gfx-level selection) — the contract was source-read at 25.0.7 (research §2.3).
None of this needs the Deck.

### Staging mechanics reminder

`stage-deck.ps1 -Target E:\` per build; `Remove-Item obj,bin` before any NativeAOT republish
(stale-link trap); CRLF-strip any WSL script edits; the committed `GuestElf.cs` is the
static-pie guest — irrelevant this session, but don't let a rebuild swap the embedded guest
unintentionally.

## 3. The rung ladder

**Numbering note (do not conflate).** The research pack's ladder uses **milestone numbers
M1–M7**; this plan's **rungs are session work units** mapped onto them: rung 0 = instrumentation
+ M1's remaining measurements; rung 1 = M2; rung 2 = the rest of M1 plus the GPU-write stretch;
rung 3 = M3+M4; rung 4 = M5+M6; rung 5 = M7. When a number appears bare, "M" always means the
research pack, "rung" always means this plan.

Boot vocabulary: one "boot pair" = two cold boots of the same build (boot N runs and stores its
log; boot Na — or the next build's boot — flushes `\PuckLog.txt` to the stick). **Pipeline the
cadence**: boot build 13 → photograph panel → restage build 14 → boot it (this flushes 13's log
AND runs 14) → pull the stick, read 13's exact log on the dev box while 14's verdict is on the
panel. Steady-state cost: one cold boot per build iteration + panel photos for live triage; only
the *final* build of the session needs a dedicated extra flush boot. Session budget: **~8 cold
boots** (comfortable afternoon; each cycle ≈ restage + Vol-Down+Power + photograph).

### Rung 0 — instrumentation + eraser/stall isolation (build 13)

**Work**: boot build 13 once (attempt order: carveout gfx CSB → SOFT_RESET_CP → GART gfx CSB).
**Exit criteria** (all data, no fixes required):
- Carveout-first CSB result recorded: stall at dword 222 moved / vanished / identical.
- `CP_PFP_HEADER_DUMP`×8 captured on the stuck attempt → fetch-stall vs parse-hang verdict.
- Eraser recurrence on carveout-resident ring recorded (with the timing instrumentation's
  first erase timestamp if it fires).
- SOFT_RESET_CP efficacy recorded (did `CP_STAT` clear between attempts).
- GOP `PixelFormat`, BAR0 offset, rotation markers, and blit timing (±HDP poke) logged.
**Boot budget**: 1 boot (log flushed by rung 1's first boot). +1 only if the panel photo is
ambiguous and the exact log is needed before choosing rung 1 parameters — unlikely; build 14
is parameter-free with respect to build 13's outcomes.

### Rung 1 — MEC compute queue: ring test → IB → first fence (build 14)

**Work**: boot build 14 (attempt order: MEC ring test → MEC IB+fence → KIQ-PQ probe →
SOFT_RESET_CP → gfx CSB data collection). **Exit criteria**:
- **PASS = `RELEASE_MEM` fence value lands in carveout memory and the CPU poll sees it.** First
  fence ever on this silicon; Blocker 1 bypassed; rung 3 unblocked.
- Partial pass = MEC `WRITE_DATA` ring test `reg=Y mem=Y` but IB wedges → capture
  `CP_CPC_STALLED_STAT1`/`CP_MEC_ME1_HEADER_DUMP`, proceed per decision tree.
- Fail = MEC ring test dead → MQD/doorbell debugging per decision tree.
**Boot budget**: 2–4 boots (initial + one fix iteration; each fix iteration is a dev-box code
change + restage; budget assumes at most two fix builds 15/16).

### Rung 2 — present proof beyond the console (riders, ~0 extra boots)

**Work**: rides builds 13/14 — the timed CPU blit (13) and the GPU `WRITE_DATA`-to-scanout
sentinel (14, gated on a passing ring test). **Exit criteria**: measured ms/frame for a full
800×1280 CPU blit through WC (target: know whether 30+ fps CPU present is real); visible
GPU-written pixels on the panel = the scanout MC-address math `0xF4_0000_0000 + fbOffset`
proven. **Boot budget**: 0 extra (riders); +1 if the GPU-write sentinel needs an address-math
fix iteration.

### Rung 3 — RADV real-device compute dispatch through the DRM shim

**Work** (dev-box first, Deck last): the shim table's rows 13–19 (research §2.4) — carveout GEM
allocator, GPUVM page tables (gfx10 PTE format, analogous to the working VMID0 GART), CS ioctl
chunk parse (`CHUNK_ID_IB` ip_type=COMPUTE, inline `CHUNK_ID_BO_HANDLES`, FENCE, SYNCOBJ_IN/OUT)
feeding the rung-1-proven MEC ring, real fence-polling syncobjs/WAIT_CS, INFO flip to real
values (family 144, GFX IP *answering* 10.3 with `available_rings≠0` — the ★ classification
trap — real fw versions from `g_gpuFw`, real MEMORY/gb_addr_cfg from BAR5, `SYNCOBJ_TIMELINE=0`,
`virtual_address_offset=0x200000`). Extend `vktest.c` past pipeline creation:
`vkAllocateCommandBuffers → vkCmdDispatch → vkQueueSubmit → vkWaitForFences → mapped-readback
verify`; then a **second vktest step** — storage-image write from a compute shader +
`vkCmdCopyImageToBuffer` + readback — the exact output shape rung 4's `SdfWorldEngine` needs
(image BOs bring addrlib tiling and larger GPUVM mappings; prove them before the engine does).
**Routing guard (review gap)**: the ★ trap forces a graphics-capable queue family to be
advertised, so the CS handler must reject `ip_type != AMDGPU_HW_IP_COMPUTE` with a loud log +
`-EINVAL`, and vktest/the option-1 front-end must select the compute-only queue family — a
misrouted GFX submit is then a diagnosable error, not a silent wedge. **Rung-3 instrumentation**
(the first GPUVM-translated IB cannot be QEMU-proven): on any fence timeout dump
`GCVM_L2_PROTECTION_FAULT_STATUS/_ADDR` (the stage-c assert registers — a VM fault presents as
a silent timeout) plus the MEC dump set, and echo every CS chunk parse (ip_type, IB va/size, BO
count) to the log. QEMU proves everything except silicon execution. **Exit criterion (Deck)**:
vktest prints a correct dispatch readback on hardware. **Boot budget**: 2 boots if rung 1 passed
clean (one proof + one flush); realistically a *following* session's headline.

### Rung 4 — SDF world compute render → panel

**Work**: the parallel-track substrate fixes (mremap/sysinfo/arena) + guest framebuffer seam
(§2.1 item 6: user-map the GOP aperture at the 2 MiB `PuckSetUserAccessible` grain, or a blit
syscall) + option-1 NativeAOT guest (`Puck.SdfVm` + `Puck.Scene` + `Puck.Vulkan` compute seam +
surfaceless factory, `LibraryPathOverride` → RADV, kernels/run-doc via `PuckVfsAddFile`):
`SdfWorldEngine.RenderFrame` → `byte[]` → GOP blit. **Staged file set** (all existing repo
artifacts, no compile step): the five checked-in `.comp.spv` kernels under
`src/Puck.SdfVm/Assets/Shaders/Sdf/` (`sdf-beam`, `sdf-instance-cull`, `sdf-cull-args`,
`sdf-world-views`, `sdf-world-composite`) + the run-document JSON, staged to the ESP and
preloaded via the `PuckEfiPreloadSos` pattern. The frame uses the run document's authored
camera — **zero input machinery compiled in** (controller = rung 5, gated on xHCI). Give the
guest a **render-N-frames-then-`exit_group` mode** so its boot flushes the log (a loop-forever
guest never persists — §2.1 caveat / build-13 item 0). **Exit criterion**: one real SDF world
frame (the demo's run document) visible on the Deck panel. **Not this session** unless rungs 1–3 all
fall on the first try; the QEMU-provable ceiling ("boots, loads run doc, compiles kernels
through ACO, reaches vkQueueSubmit") is the pre-session target for the *next* session.

### Rung 5 — demo-as-guest

Full `Puck.Demo` (option 2): superset of rung 4 + signal machinery + first-ever MEDI AOT publish
+ a display seam that doesn't exist. **Explicit non-goal**; direction only.

## 4. Decision tree

Ordered by boot. "→" = next action *within the session*.

**Boot 13 (rung 0) outcomes:**

- **CSB stall vanishes carveout-first** → fetch-coherency/eraser interaction was the mid-CSB
  culprit. Gfx is likely one `SOFT_RESET_CP`-plus-placement fix from a working IB path — but do
  NOT divert: proceed to rung 1 as planned; note gfx-IB retry as a build-15 rider only if MEC
  wedges (a second, independent IB data point).
- **Stall persists at dword 222 in the carveout** → read HEADER_DUMP verdict:
  - `0xC0D76900` (fetch stall) → CPG-local fetch fault, placement-independent. Gfx front end is
    parked indefinitely; MEC is the only path. Proceed rung 1, remove the gfx attempt from
    build 15+ entirely (saves boot time).
  - `0xC1106900` (parse hang) → the CP decoded the extent-2 header and hung applying it. This is
    the one branch where an opportunistic one-line fix is licensed: desk-check the *first
    registers of extent 2* against upstream for a fw-owned/privileged register (the emission
    itself is verified byte-exact — V3 — so suspect the *target register*, not the stream). If a
    plausible skip/mask is found, add it to build 15 as a rider behind the MEC changes; if not,
    park gfx.
- **Eraser recurs on the carveout ring** → major reframe: the eraser is not GART-mechanics; live
  suspects become Puck-kernel stray write / HDP-WC aliasing / TMR-adjacent scribble. Actions:
  check the erase timestamp from the poll-loop instrumentation (pre-kick erase = CPU/kernel-side
  suspect; mid-execution = GPU agent); verify the ring block doesn't overlap the PSP TMR or KIQ
  buffers' new carveout homes; move the ring to a different carveout offset in build 15. The
  eraser now gates rung 1's buffer placement — pick the offset that survived.
- **Eraser does not recur (carveout clean)** → GART-specific mechanics confirmed as live suspect;
  policy for the rest of the program: *all* GPU-visible buffers stay carveout-resident (the DRM
  shim's GEM allocator was already spec'd carveout-backed — no change). GART debugging is
  deferred indefinitely; it gates nothing.
- **SOFT_RESET_CP fails to clear `CP_STAT`** → same-boot multi-attempt data stays suspect; drop
  to one attempt per boot for anything that must be clean (costs boots — re-budget: cut the
  KIQ-PQ probe first, then the gfx data-collection attempt).

**Boot 14+ (rung 1) outcomes:**

- **MEC ring test + IB + fence all pass** → breakthrough. Immediately: (i) confirm the rung-2
  GPU-write sentinel fired (visible pixels); (ii) re-run the IB test a few times in-loop to check
  stability; (iii) if boots remain, restage a build-15 with a *larger* IB (a few hundred dwords
  of `WRITE_DATA`s) to probe the fetch path at CS-like sizes — the boot-11 stall happened at the
  first *large* packet, and rung 3's IBs will be large. Session then ends on doc updates; rung 3
  becomes pure dev-box work.
- **MEC ring test passes, IB wedges** → Blocker 1 is not PFP-local after all; shared-fabric or
  IB-fetch-mode suspects. Capture `CP_CPC_STALLED_STAT1` + `CP_MEC_ME1_HEADER_DUMP`. Build-15
  matrix (one boot, SOFT_RESET_CP between attempts if boot 13 proved it works): IB in carveout
  vs (deliberately) GART; `INDIRECT_BUFFER_VALID` bit toggled (the bit is now source-verified
  as required for compute — the toggle stays in the matrix purely as a falsifier, and the
  control dword must match `gfx_v10_0_ring_emit_ib_compute` exactly); KIQ-PQ probe result consulted (if the
  KIQ executed its benign packet, queue-level execution works and the fault is IB-fetch-specific).
  If IB still wedges after one fix boot: stop, collect maximal dumps, end session — the finding
  ("compute IB inherits the fetch fault") redirects the program toward SDMA-based experiments
  and deeper fabric analysis, which need dev-box research, not more boots.
- **MEC ring test fails (no `WRITE_DATA` execution)** → MQD/doorbell recipe fault, very likely
  ours. Checks in order: doorbell range actually widened (read back
  `CP_MEC_DOORBELL_RANGE_*`); `CP_HQD_ACTIVE=1` stuck; MQD field diff against tinygrad's
  `ip.py` values field-by-field (port, don't reason); mec2 fw-type mapping (`:4480`) — if the
  wrong MEC fw got loaded, me=1 may be running nothing. One fix build; if still dead, fall back
  to the KIQ-PQ probe result — if the KIQ PQ executed, clone *its* exact MQD contents for the
  user queue.
- **Whole-boot anomaly (hang before bring-up, log machinery broken)** → restage the previous
  known-good build to verify the stick/unit, then re-restage. Never debug two variables at once.

**Cross-cutting**: any boot whose panel shows a verdict contradicting this tree's assumptions →
photograph everything, do the extra flush boot to get the exact log, and re-plan from the log,
not the photo.

## 5. Data to capture every boot (instrumentation list)

The build must log all of this unconditionally; the panel shows it live, and `PuckLog` persists
it **provided the boot reaches `exit_group` or a persistence-wired `PuckHang`** (build-13
item 0 — without it, parked boots lose their log). Photograph the parked health screen *every*
boot before power-off — the photo is the only same-boot record.

1. Boot/build id line (bake the build number into the banner — with one-boot log lag, log-to-
   build attribution must be self-identifying).
2. Stage (a)–(d) status lines (regression canary: RLC srlc loads `ok` ×3, PSP fw loads).
3. Per-attempt, pre-kick: ring location (carveout/GART, phys), ring-pre readback checksum,
   CPU-probe result, WPTR programmed.
4. Per-attempt, post: `RPTR`, `CP_STAT`, `CP_CPF_STATUS`, `CP_CPF_STALLED_STAT1`,
   `CP_STALLED_STAT3`, `CP_PFP_INSTR_PNTR`/`CP_ME_INSTR_PNTR`/`CP_CE_INSTR_PNTR`,
   `CP_PFP_HEADER_DUMP`×8, ring-post checksum, SCRATCH value, fence memory value.
5. MEC set (build 14+): `CP_CPC_STATUS`, `CP_CPC_STALLED_STAT1`, `CP_MEC_ME1_HEADER_DUMP`×8,
   `CP_HQD_ACTIVE`, `CP_HQD_PQ_RPTR`/`WPTR`, doorbell-range readbacks, EOP fence value.
6. Eraser telemetry: poll-loop HEAD/RPTR/sentinel-dword transitions with TSC timestamps.
7. SOFT_RESET_CP telemetry: `CP_STAT` before/after each assert.
8. GOP block (build 13): `PixelFormat` raw u32, fb phys, BAR0 offset arithmetic, blit `rdtsc`
   deltas ±HDP poke.
9. Icache dump (kept from build 12): `CP_PFP/ME/CE_IC_BASE` + `BASE_CNTL`.
10. Final `bring-up:` verdict line (the panel-photo one-liner).
11. **Parked-screen latching** (review gap): the boot log scrolls past one Deck panel, and the
    HEADER_DUMP/eraser additions triple it — so latch the decision-tree-critical values into
    the parked health screen's re-print (last HEADER_DUMP header + verdict, `CP_STAT`
    before/after SOFT_RESET, MEC fence value, eraser first-erase timestamp, the GOP block).
    The scrolling log is not photographable; the parked screen is.
12. **Log-volume discipline**: eraser telemetry logs transitions only (item 6); if `PuckLog`
    capacity forces truncation, the TAIL survives (build-13 item 0's check).

## 6. Post-session doc updates (same day, while the photos are fresh)

1. **`deck-bringup-handoff.md`**: append boots 13..N to the boot-history table (build change
   tested / outcome, DEAD markers for anything falsified); rewrite "Still open" and both Open
   mystery sections against the new data; replace "Next session, in order" with the new ordered
   list; update the hardware-facts table if anything new (PixelFormat, HPET if probed).
2. **`deck-demo-research.md`**: strike or annotate any §2/§3 claim the boots contradicted; move
   newly falsified hypotheses into the falsified register; update §4 (open questions) removing
   answered items.
3. **`amd-vulkan-plan.md`**: stage (e) status update; if rung 1 passed, mark the compute-queue
   path as the primary stage-(f) vehicle and **re-word stages f–h around the compute pivot**
   (f = compute CS ioctls, g unchanged, h = compute-dispatch readback checksum / SDF frame; the
   gfx offscreen-triangle deliverable is deferred — it depends on the front end this program
   just demoted to a diagnostic sideshow).
4. **This file**: mark each rung's exit criteria met/not-met with boot numbers; write the next
   session's goal line at the top.
5. **Memory** (`deck-devloop-on-laptop.md` / bringup note): only if the dev-loop mechanics
   changed (new staging step, new gotcha).
6. **Do not** create new doc files for results; these four are the corpus.
