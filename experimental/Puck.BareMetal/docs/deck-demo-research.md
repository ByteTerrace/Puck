# Deck demo research pack — boot the Puck Demo on Steam Deck bare-metal

**Consolidated deep-research output, 2026-07-09.** Ten recon passes (five repo, five web) plus
five adversarial verification verdicts, synthesized for the next hardware session. Companion
ground truth: [`deck-bringup-handoff.md`](deck-bringup-handoff.md) (live state + the 12-boot
history table — not duplicated here), [`amd-vulkan-plan.md`](amd-vulkan-plan.md) (staged roadmap
a–h), [`gfx103-bringup-spec.md`](gfx103-bringup-spec.md) (register/protocol spec).

Every repo claim carries `file:line` against this worktree; every external claim carries its
source (URL or kernel/Mesa file+function). Claims that survived adversarial verification are
marked with their verdict (§3).

## Contents

1. [Executive summary](#1-executive-summary)
2. [Subsystem state map](#2-subsystem-state-map)
   - [2.1 Kernel substrate & syscall surface](#21-kernel-substrate--syscall-surface)
   - [2.2 GPU bring-up](#22-gpu-bring-up-stages--open-blockers)
   - [2.3 RADV closure & guest](#23-radv-closure--guest)
   - [2.4 DRM shim gap (the ioctl table)](#24-drm-shim-gap)
   - [2.5 Present / scanout path](#25-present--scanout-path)
   - [2.6 Engine/demo hosting strategy](#26-enginedemo-hosting-strategy)
   - [2.7 Deck periphery](#27-deck-periphery-input--net--audio--timers--thermals)
3. [Verified-claims register](#3-verified-claims-register)
4. [Open questions only a Deck boot can answer](#4-open-questions-only-a-deck-boot-can-answer)
5. [Source index](#5-source-index)

---

## 1. Executive summary

**Where we are.** Puck.BareMetal is a self-hosting UEFI kernel (`compat/native/puck-efi.c`, one
6243-line translation unit) that boots the Steam Deck OLED (Sephiroth, gfx10.3.1), exits boot
services, runs its own GDT/IDT/paging/PAT/scheduler, hosts a ring-3 Linux-ABI guest process, and
has brought the GPU up through PSP firmware load and **first silicon execution** — the gfx ring
test passes (`SET_UCONFIG_REG` + `WRITE_DATA`, `reg=Y mem=Y`). In QEMU, real unmodified Mesa 26.1
RADV runs as the ring-3 guest against a synthetic DRM device and prints
`COMPUTE PIPELINE COMPILED (ACO -> RDNA2 ISA)` — the entire userspace Vulkan stack works short of
command execution. The panel is driven today by the kernel's own console through the surviving
GOP framebuffer (verified present path, V2 §3).

**The milestone ladder to "demo on Deck"** (M-numbers; the session plan's *rungs* are work
units mapped onto these — see the numbering note in
[`deck-demo-session-plan.md`](deck-demo-session-plan.md) §3). Each milestone is independently
verifiable. Readiness, honestly stated: **M1 is already hardware-proven; M2 is ready to attempt
today (no prerequisites) but is itself an unproven hardware experiment; M3–M5 gate on the
hardware mysteries; M6–M7 are engine-side.**

- **M1 — Pixels via CPU blit into the GOP framebuffer** — already implemented and
   hardware-proven by the on-panel console (V2: CONFIRMED). Remaining work: capture
   `PixelFormat`, measure full-frame blit cost. Zero GPU dependency.
- **M2 — First MEC compute-queue execution** — clone the proven KIQ MQD-commit recipe to a user
   compute queue at me=1/pipe=0/queue=0, in the carveout, `WRITE_DATA` ring test then IB test
   with `INDIRECT_BUFFER_VALID`. Bypasses the wedged gfx PFP/CE front end entirely (V1: LIKELY).
- **M3 — Real DRM shim backing** — CS ioctl (chunk parse → MEC ring), carveout-backed GEM
   allocator, GPUVM page tables, fence-signalling syncobjs. The shim's *surface* is bounded and
   ~60% built; the *backing* is the new work (V5: CONFIRMED with that correction).
- **M4 — RADV on the real device** — flip the synthetic INFO answers to real values (family 144,
   GFX IP 10.3 with `available_rings≠0` even though only compute submits), leave the rest of the
   already-QEMU-proven VFS/fd plumbing unchanged (W2, V5).
- **M5 — NativeAOT guest substrate** — fix the `mremap` probe (today an EE-startup infinite
   loop), populate `sysinfo`, grow the 64 MiB guest arena ~10×, honor the GC's zero-on-recommit
   contract; defer real signal delivery at documented risk (W4, V4).
- **M6 — Minimal front-end ("option 1")** — a dynamic (Route A) NativeAOT `linux-musl-x64`
   guest: `Puck.SdfVm` + `Puck.Scene` + `Puck.Vulkan` compute seam + one new surfaceless device
   factory, rendering the demo's real run document via `SdfWorldEngine.RenderFrame` →
   `byte[]` → GOP blit (V4: CONFIRMED as the only realistic next-session vehicle).
- **M7 — Full Puck.Demo as guest ("option 2")** — strictly a superset of M6 plus full signal
   machinery plus a never-attempted AOT publish of the MEDI demo host plus a display seam that
   doesn't exist. Eventual destination, not a session vehicle (V4).

**Top risks, ranked:**

- **Open Blocker 1 — no `INDIRECT_BUFFER` has ever executed on this silicon.** Every real RADV
  submission is an IB. The MEC-bypass argument is architecturally sound and backed by verified
  kernel + tinygrad precedent, but zero MEC packets have ever executed here either (V1). If the
  true root cause lives in shared fetch fabric rather than PFP microcode, MEC inherits it.
- **Open Blocker 3 — the GART-block eraser** (3 of 5 cold boots zero the GART ring block).
  Mitigation is placement (run everything from the carveout first), but no clean first-attempt
  carveout run exists yet; the eraser's mechanism is unknown.
- **CP state contamination** — a stuck attempt poisons later attempts in the same boot; upstream's
  remedy is `GRBM_SOFT_RESET.SOFT_RESET_CP`, not halt/unhalt (W1 §6, verified against
  `gfx_v10_0_soft_reset()`). Until adopted, every multi-attempt boot's later data is suspect.
- **The boot-11 stall's root cause is still unknown.** The emission-bug hypothesis (handoff plan
  item d's leading suspect) is now REFUTED by dword-exact desk-check (V3); the stall is a CP
  front-end fetch/parse hang at the extent-2 header, leading suspect fetch coherency / the eraser
  on the stream's first large (274-dword) packet.
- **NativeAOT-on-fake-Linux is unproven** — nothing in `src/` has ever been published NativeAOT
  (V4 verified: zero `PublishAot` hits), and two current kernel stubs (`mremap`, `sysinfo`)
  hard-fail or hang the runtime at startup.

---

## 2. Subsystem state map

Shape for each: **Now** (settled, cited) / **Needed for demo-on-Deck** / **Risk & evidence**.

### 2.1 Kernel substrate & syscall surface

**Now.** `EfiEntry` (`puck-efi.c:5957`) runs the full boot chain: serial probe → GOP capture +
early framebuffer (`:2876`, `:2916`) → ACPI RSDP → RADV `.so` preload from ESP `\radv\` into the
guest VFS (`PuckEfiPreloadSos` `:3244`) → GPU microcode preload into kernel-only `g_gpuFw[]`
(`:3301`, also flushes the previous boot's log to `\PuckLog.txt` via `PuckLogFlush` `:3205`) →
`ExitBootServices` loop (`:5991-6013`) → bump heap over the largest conventional region
(`:2981`, `PuckAlloc` `:171` — no free, no OOM recovery) → GDT/IDT (`:2307`, `:2464`) →
identity paging with 2 MiB RWX leaves (`:2702`) + PAT WC/UC (`:2766`) + `PuckMapMmio` for
in-map retune and high-BAR 4 KiB chains (`:2795-2846`) → ECAM/GPU probe/LAPIC → GC statics →
SYSCALL MSRs (`:2330`) → clock (PIT-calibrated TSC, `:565`) → GPU bring-up (`:5905`) → 100 Hz
PIT preemption (`:2632`) → a kernel-side DHCP→DNS→TCP→TLS proof (`PuckNetTlsTest`,
`puck-netif.c:322`, real mbedTLS with cert validation — kernel-only, invisible to the guest) →
managed `Main` → ring-3 guest entry via `iretq` (`puck-efi-x64.asm:79`).

The syscall surface (`PuckSyscallDispatch` `:1819-2144`, scheduling subset `:2246-2276`) covers
~40 Linux x86-64 syscalls: full synthetic VFS (static table `:1192-1219` + 16 dynamic slots,
symlink-resolving path canonicalizer `:1315`, stable `st_dev/st_ino` because ld-musl dedups DSOs
by (dev,ino) `:1482-1485`), `mmap` with DRM-BO and file-backed flavors (`:2049-2097`), brk/arena
(64 MiB bump, `PUCK_GUEST_ARENA_BYTES` `:500`, `munmap` no-op `:2036`), threads
(`clone` thread-only `:2179`, `futex` WAIT/WAKE `:2205`, round-robin preemptive scheduler with
64 fixed slots `:410`, per-thread FS-base), `clock_gettime`, `getrandom` (RDRAND `:2110`),
`sched_getaffinity` reporting 1 CPU (`:1984` — sizes ACO to one worker). Unhandled syscalls log
and return `-ENOSYS` (`:2137-2143`).

**Needed for demo-on-Deck** (from W4 + V4, in priority order):

1. **Fix `mremap`** — today an unconditional `-ENOMEM` (`:2004-2005`). musl's
   `pthread_getattr_np` main-thread stack probe loops
   `while (mremap(...)==MAP_FAILED && errno==ENOMEM)` (VERIFIED musl 1.2.6
   `src/thread/pthread_getattr_np.c`), so any NativeAOT guest **hangs in an infinite loop at EE
   startup** before `Main`. Return a non-ENOMEM errno past the known stack extent, or model the
   stack VMA. (V4's finding; neither recon report caught the loop consequence.)
2. **Populate `sysinfo`** — zero-filled today (`:1992`); `sysconf(_SC_PHYS_PAGES)` →
   `sysinfo.totalram` feeds `GCToOSInterface::Initialize`, which returns `false` on failure
   (VERIFIED `gcenv.unix.cpp:246-249` + musl `sysconf.c`). Fill `totalram`/`mem_unit`.
3. **Grow the guest arena** 64 MiB → ≥512 MiB and audit the GC memory contract: decommit is
   `mmap(MAP_FIXED|PROT_NONE|ANON)` and **recommitted pages must read as zero** (VERIFIED
   `gcenv.unix.cpp` `VirtualDecommit` comment). Current anonymous-mmap zero-fill happens to
   satisfy this — pin with a test; violation is silent heap corruption.
4. **Signal machinery** (deferable at risk): `rt_sigaction`/`rt_sigprocmask` are lying no-ops
   (`:2001-2002`, `:2038-2039`); `tkill` (**200**), `tgkill` (**234**), `rt_sigreturn` (15)
   absent. NativeAOT GC suspension is cooperative-first, `pthread_kill(SIGRTMIN)` → musl
   `tkill` escalation only for threads missing safepoints (VERIFIED `threadstore.cpp`
   `SuspendAllThreads` + musl `pthread_kill.c`). Single hot managed thread + parked finalizer
   keeps the path cold, but it is always compiled in — required for general correctness.
   (W4's report said "tkill (231)"; wrong — 231 is `exit_group`. V4 corrected.)
5. **Thread-slot reclamation** — `ZOMBIE` slots never return to `FREE` (`:410`, so `clone`
   eventually fails `EAGAIN`); long-lived guests exhaust 64 slots. Also: `GS`-base is not per-thread
   (`:2024`), `exit_group` halts the machine with no restart path (`:2126-2135`).
6. **Guest framebuffer seam** — the guest has no path to the GOP aperture today; either map it
   user-accessible (mind the 2 MiB `PuckSetUserAccessible` grain, `:2741`) or add a blit
   syscall.

**Falsified fears — do not spend time on** (W4, all verified against dotnet/runtime + musl
source): `/proc/self/maps` is read nowhere in NativeAOT/GC/minipal; `sigaltstack` unused;
`rseq` unused by musl; `getrandom` absence would degrade gracefully to `/dev/urandom` anyway;
cgroup/`/proc` probes all degrade on `ENOENT`; musl skips `RLIMIT_NOFILE` entirely.

**Risk & evidence.** The substrate is the most mature subsystem — QEMU-proven end-to-end through
real RADV. The risk is concentrated in the NativeAOT delta: two startup-fatal stubs (items 1–2),
one silent-corruption contract (item 3), and the fact that no Puck engine assembly has ever been
AOT-published (V4). The kernel's own log-persistence loop (UEFI variable → next-boot
`\PuckLog.txt`, `:3188`, `:3205`) remains the only exact-text debug channel — one boot of lag,
both boots cold (handoff `:215-231`). **Caveat (review gap, code-verified):** `PuckLogPersist`
is called from the `exit_group` path only (`:2133`); every `PuckHang()` park (~15 call sites,
including most hard-failure paths) loses its exact log — the panel photo is the only record of
a parked boot. Wire persistence into `PuckHang` (session-plan build-13 item 0). A guest with a
loop-forever render loop likewise never persists — give it a render-N-frames-then-`exit_group`
mode. Also noted in passing: `PuckDetectIommu` (`:874-893`) only *logs* AMD-Vi presence — no
passthrough domain is implemented; moot on the Deck, which publishes no IVRS.

### 2.2 GPU bring-up (stages + open blockers)

**Now.** Stages (a) substrate, (b) probe, (c) GMC/GART, (d) PSP firmware load: **done and clean
on hardware** (handoff `:12-14`). Stage (e): the gfx ring test **fully passes** — first silicon
execution at boot 6, full pass (`reg=Y mem=Y`) at boot 7. The proven 12-step recipe (golden init
→ PSP LOAD_TOC/SETUP_TMR/LOAD_IP_FW with RLC srlc sub-blobs at fw-type IDs 20/21/22 →
AUTOLOAD_RLC → icaches untouched → live KIQ HQD via direct MMIO at me=2/pipe=1/queue=0 → gfx
HQD/MQD not legacy `CP_RB0_*` → halt/program/unhalt per attempt → CE un-halted → full 961-dword
CSB + `CLEAR_STATE` + `SET_BASE CE_PARTITION(3)` → `WRITE_DATA CACHE_POLICY=BYPASS` → 64-bit
doorbell 0x116 → nvd.h-verified PM4) is implemented in `PuckGpuRingTest`/`RingAttemptHqd`
(`puck-efi.c:5824`, `:4965-5302`) and mapped step-by-step in recon I2 §1. GART bring-up
(`PuckGpuGartBringUp` `:4285`) uses the physical-not-MC root-table address fix; the carveout
window layout is `VGH_PSPWIN_*` (`:3969-3983`).

**The carveout/GPU-memory map as the code defines it today** (carveout base = DRAM phys
`0x4_4000_0000`, 1024 MiB — the authoritative placement reference for "move everything to the
carveout" work and for eraser forensics; no such consolidated map existed before this pack):

- **PSP window** at carveout +64 MiB, 32 MiB (`VGH_PSPWIN_CARVEOUT_OFF`/`_SIZE` `:3969-3970`):
  TMR at window+0 (≤19 MiB cap, `:3971-3972`), ucode staging at +0x140_0000 (1 MiB, `:3973`),
  PSP KM ring / cmd / fence pages at +0x150_0000 / +0x150_1000 / +0x150_2000 (`:3974-3976`),
  **VMID0 GART page table** at +0x160_0000 (512 KiB, `:3977`), CE-icache staging at +0x1A0_0000
  (1 MiB, `:3982`).
- **Ring-test block** (`VGH_RT_*` — offsets are within whichever block hosts the attempt; today
  that is the GART test block at CPU `0x1_8040_0000`): gfx ring 8 KiB at +0x10_0000
  (`:4032-4033`), rptr/wptr/fence/scratch dwords at +0x10_2000/+0x10_2040/+0x10_2080/+0x10_20C0
  (`:4034-4037`), IB 4 KiB at +0x10_3000 (`:4038`), gfx MQD at +0x10_4000 (`:4039`), KIQ
  MQD/EOP/PQ-ring at +0x10_5000/+0x10_6000/+0x10_7000 with the KIQ rptr/wptr dwords at the ring
  page's tail (`:3678-3682`).
- **Unassigned**: everything below carveout +64 MiB and above +96 MiB. Builds 13/14 must pick
  the carveout-resident ring-test block's base from this space (avoid the PSP window), record it
  here, and the M3 GEM allocator pool later carves from what remains minus these reservations.

**Open blockers, with the boot-12 reframes** (handoff `:118-163`, updated by this research):

- **Blocker 1 — the IB+`RELEASE_MEM` fence has never signalled.** Boot-12 reframe: the CE is
  ALIVE (`CP_CE_INSTR_PNTR` moves); `CE_WAITING_ON_DE_COUNTER_UNDERFLOW` is plausibly its idle
  signature, not a wedge. The real signature is the **PFP** in a wait-loop at PC 0x2fd/0x2fe with
  `CP_CPF_STALLED_STAT1.RING_FETCHING_DATA` set. New from W1 (verified bit decode of
  `CP_STAT=0x94008200`): bits 31/28/26/15/9 set (CP_BUSY, ROQ_CE_RING_BUSY, CE_BUSY, PFP_BUSY,
  ROQ_RING_BUSY) with `ROQ_INDIRECT1_BUSY`, `ME_BUSY`, `MEQ_BUSY`, **`UTCL2IU_BUSY` all clear** —
  CPF was never commanded into indirect-buffer fetch mode; PFP is stuck before issuing the IB
  fetch, and the memory-translation fabric is idle (arguing the fault is CPG-local, not
  fabric-wide). Also new: amdgpu's own IB self-test (`gfx_v10_0_ring_test_ib`) submits with
  `job=NULL` — **no CONTEXT_CONTROL, no FRAME_CONTROL, no VM flush, vmid=0** — so the minimal IB
  shape needs nothing beyond `INDIRECT_BUFFER` + `RELEASE_MEM` (W1 §2c, verified
  `amdgpu_ib.c:258-270`).
- **Blocker 2 — the deterministic mid-CSB stall (boot 11, RPTR=0xde/dword 222).** **Reframed by
  V3 (REFUTED verdict on the emission-bug hypothesis):** Puck's CSB emission is byte-exact to
  upstream `gfx_v10_0_cp_gfx_start()` — vendored table verbatim (`clearstate_gfx10.h:961-970`,
  reg_counts {215,272,4,158,2,1,66,203}), `PACKET3` count placed with no −1 (`:4043`), framing
  and ordering identical (`:5128-5171`). Dword 222 is *exactly* the extent-2
  `SET_CONTEXT_REG(272)` header (`0xC1106900`); extent 1 (identical construction) was consumed
  cleanly, which logically rules out any generic emission off-by-one. Handoff plan item (d)'s
  leading suspect is dead. New leading suspects: (i) fetch-coherency/eraser interaction on the
  stream's first 274-dword packet — the first fetch forced past the CP's read-ahead window,
  predicting the stall moves or vanishes with the ring in the carveout; (ii) un-cleared CP state
  from a prior same-boot attempt (boot 11's "twice, identically" may be residue).
- **Blocker 3 — the GART-block eraser** (3 of 5 cold boots; only ever the GART block at CPU
  `0x1_8040_0000`, never the carveout). Unchanged; key gap remains that no clean *first-attempt*
  carveout run exists. Note the KIQ MQD/EOP/ring co-reside in the suspect block
  (`VGH_RT_KIQ_*` `:3678-3682`).

**Needed for demo-on-Deck.** The strategic pivot this research adds (V1: LIKELY): **stand up a
MEC compute queue instead of fighting the gfx front end.** Verified support: tinygrad's
production AM driver never touches PFP/ME/CE/clear-state at all on gfx10/11 — MEC MQD via direct
sequential MMIO, `CP_HQD_ACTIVE=1`, `INDIRECT_BUFFER|VALID` + `RELEASE_MEM`, doorbell (W1 §5,
source-read `tinygrad/runtime/support/am/ip.py`); amdgpu's compute IB path is the same
RELEASE_MEM encoding with `INDIRECT_BUFFER_VALID` (bit 23) added to the control dword — which
Puck's gfx-path `RingIbFence` (`:5339-5342`) correctly omits for gfx but must add for compute
(VERIFIED post-review against mainline `gfx_v10_0.c`: `gfx_v10_0_ring_emit_ib_compute` builds
`control = INDIRECT_BUFFER_VALID | ib->length_dw | (vmid << 24)` while
`gfx_v10_0_ring_emit_ib_gfx` builds `control` without the VALID bit; the KIQ ring funcs use the
compute emitter too).
Concrete delta (V1): widen `CP_MEC_DOORBELL_RANGE_LOWER/UPPER` (today pinned to the KIQ slot,
`:5705-5706`); clone `PuckGpuKiqBringUp` (`:5567-5734`) to me=1/pipe=0/queue=0 with non-KIQ
`PQ_CONTROL` bits, buffers **in the carveout**; ring test then IB test; add `CP_CPC_STATUS`/
`CP_CPC_STALLED_STAT1`/`CP_MEC_ME1_HEADER_DUMP`×8 to the dumps. **Correction V1 landed:** the
claim "the KIQ ring test already proves MAP_QUEUES executable" is FALSE — no PM4 packet has ever
gone through the KIQ ring (`:5558-5560`, `:3676`); the KIQ's liveness is proven only by its
scheduler side-effect. Direct-MMIO HQD commit is the proven mechanism; KIQ-as-executor is itself
an experiment (cheap one: one benign packet through the already-active KIQ PQ, doorbell 0).

Also adopt from W1 (all VERIFIED against mainline): **`GRBM_SOFT_RESET.SOFT_RESET_CP` as the
un-wedge**, gating the halt step on `CP_STAT==0` and escalating on failure (halt/unhalt does not
drain a stalled fetch — explains same-boot contamination); **`CP_PFP_HEADER_DUMP`×8 FIFO pops**
(same MMIO offset read 8×) to see the last 8 decoded PM4 headers — directly disambiguates
fetch-stall vs parse-hang at dword 222 (last header `0xC0D76900` = never latched extent 2;
`0xC1106900` = decoded it and hung applying).

**Risk & evidence.** Code-vs-doc drift flagged by I2: `PuckGpuCeIcacheFix` is still called
unconditionally (`:5851`) and still reprograms `CP_CE_IC_BASE` (`:5815-5817`) despite the
handoff marking it proven-ineffective/firmware-owned — remove per plan item (b); the stale
CE-microcode narrative comment (`:5736-5767`) should go with it. `RingAttempt` (legacy
`CP_RB0_*`, `:4750-4928`) is dead code kept as reference. The mec2→`CP_MEC` PSP fw-type mapping
remains UNVERIFIED in code (`:4480`). No Van-Gogh-specific CSB/CE erratum exists in upstream's
commit history or the amd-gfx list (W1 negative result).

### 2.3 RADV closure & guest

**Now.** Vanilla Mesa **26.1.1** release tarball, zero source patches — the lean build is pure
meson config (`radv/build-radv-musl.sh:23,43-47`: `-Dvulkan-drivers=amd -Dllvm=disabled
-Dplatforms= …`), built in a WSL Alpine chroot at `/root/aroot`, producing a 10-file / 24.6 MB
closure (9 `.so` + `ld-musl`) staged to gitignored `.qemu/radv` (`:52-58`;
`docs/amd-vulkan-plan.md:161-164`). The kernel preloads it from the ESP pre-ExitBootServices
into the guest VFS at `/lib/<name>` (`puck-efi.c:3069-3079`, `:3244-3269`). The guest `vktest.c`
links `libvulkan_radeon.so` as a plain `DT_NEEDED` and calls `vk_icdGetInstanceProcAddr`
directly — no Khronos loader, no dlopen (`samples/EfiLinux/guest/vktest.c:1-18`); it is a
dynamic PIE entered through ld-musl ("Route A", `samples/EfiLinux/Program.cs:239-257`), which
does real dynamic linking in ring 3 against the synthetic VFS. The guest walks: instance →
enumerate (finds the synthetic Van Gogh) → logical device → BO create/map/write → shader module
→ **`vkCreateComputePipelines` = ACO lowering SPIR-V to real RDNA2 ISA** → done
(`vktest.c:37-176`). It deliberately stops before any command buffer/submit — matching exactly
what the shim implements. "SYNTHETIC device" is not an env var or stub winsys: it is the
in-kernel fake amdgpu driver answering real ioctls (§2.4).

**Needed for demo-on-Deck.** The RADV *userspace* side needs nothing — the same closure runs
unmodified once the kernel answers with real values. The verified minimal kernel contract (W2,
source-read **Mesa 25.0.7** + libdrm 2.4.124 — note the staged closure is **26.1.1**: these
`ac_*/radv_*` paths are stable across that gap, but that is INFERENCE until the ★ classification
switch below is spot-checked in the staged 26.1.1 tarball; one grep in the chroot, on the
session plan's pre-session checklist):

- Enumeration is `drmGetDevices2` + render-node open + `drmGetVersion(name=="amdgpu")` + a
  **second** per-fd `drmGetDevice2` needing real PCI bus info; libdrm dups the fd
  (`F_DUPFD_CLOEXEC` — implemented, `puck-efi.c:1970`), classifies render nodes by fstat minor
  range, and hard-requires `AMDGPU_INFO_ACCEL_WORKING≠0` and `DRM_CAP_SYNCOBJ≠0`. (The Mesa
  source-read reported `GET_CLIENT` is never issued on a render node — but the shim already
  implements it answering `auth=1` with a comment saying an auth check was hit
  (`puck-efi.c:1646-1652`). Keep the handler; whether the current boot path still reaches it is
  UNVERIFIED, and "never issued" is downgraded accordingly.)
- **★ The classification trap:** `ac_gpu_info.c`'s gfx-level switch checks
  `info->ip[AMD_IP_GFX]` **exclusively** for GFX10_3 — no compute fallback (only GFX9 has one).
  An honest "compute-only" `HW_IP_INFO(GFX)={0 rings}` aborts device creation with
  `UNIMPLEMENTED_HW`. The GFX IP must *answer* as 10.3 with `available_rings≠0` even though its
  ring never functionally runs (real submissions target `AMDGPU_HW_IP_COMPUTE`).
- Not safely zeroable: `num_shader_engines`/`num_shader_arrays_per_engine`/`cu_bitmap`
  (divide-by-zero in `max_good_cu_per_sa`), family/external_rev (must resolve
  `identify_chip(VANGOGH)`, family 144, rev ∈ [1,0xFF)), ME/MEC/PFP `FW_VERSION` ioctls (must
  succeed), `READ_MMR_REG` of `gb_addr_cfg` 0x263e (must succeed; zero value tolerated in QEMU,
  serve from a real BAR5 read on Deck), `virtual_address_offset/max` (libdrm's userspace VA
  manager carves from these — currently 0, could hand out VA 0).
- Safely stubbed: `mall_size`, cache sizes, `pcie_*`, sensors, video caps (report zero video
  IPs → VCN fw queries never fire). `RADV_FORCE_FAMILY` is a null-winsys CI path, not a
  shortcut. `DRM_CAP_SYNCOBJ_TIMELINE=0` is a legitimate simplification lever: RADV falls back
  to binary syncobjs + CPU-emulated timelines, shrinking the syncobj surface to
  CREATE/DESTROY/WAIT/RESET + `SYNCOBJ_IN/OUT` chunks.
- WSI: fully offscreen-capable — the primary node is opened only behind `VK_KHR_display`;
  readback is `GEM_MMAP`-ioctl + `mmap(fd, offset)` + CPU loads, no read() path.

**Risk & evidence.** Machine-local gotchas that bite fresh boxes (I3 §5): CRLF corrupts the WSL
shell scripts (`tr -d '\r'`); scripts must run as root in the Alpine chroot; `mkguest.ps1`
hardcodes `-d Ubuntu` while the RADV scripts are distro-agnostic; vktest build must follow the
RADV build in the same chroot; the closure is gitignored and must be rebuilt per machine; the
NativeAOT stale-link trap forces `Remove-Item obj,bin` before every publish (`run-qemu.ps1:64`,
`stage-deck.ps1:27`); an intermittent #PF during ld-musl linking (~1 boot in N, RDRAND-seeded
layout nondeterminism) is a known flake (`docs/amd-vulkan-plan.md:176-177`). The
currently-committed `GuestElf.cs` is a static-pie guest with empty lib tables
(`GuestElf.cs:9-14`) — re-embed via `embed-dyn.ps1` for the dynamic path.

### 2.4 DRM shim gap

**Now.** The shim lives entirely in kernel C (`PuckDrmIoctl`, `puck-efi.c:1628-1816`; fd-kind
routed ioctl at `:1879-1886` — every non-DRM fd's ioctl returns `ENOTTY`; DRM mmap at
`:2060-2066`). Implemented and QEMU-proven: VERSION (`"amdgpu"` 3.59.0), GET_CLIENT (auth=1,
`:1646-1652`), GET_CAP, SET_CLIENT_CAP, GEM_CLOSE (no-op), the full syncobj family as
always-signalled fakes, GEM_CREATE (host RAM via `PuckAllocPages`, domain-blind), GEM_MMAP
(BO CPU address echoed as offset), GEM_VA (**hard no-op — no GPUVM**), CTX (monotonic fake ids),
INFO (DEV_INFO/MEMORY/HW_IP_INFO/ACCEL_WORKING; unknown queries return benign zeros `:1803-1805`).
Absent entirely: **CS, BO_LIST, WAIT_CS, GEM_OP, USERPTR, WAIT_FENCES, VM, FENCE_TO_HANDLE**
(zero grep hits) — the seam stops exactly where GPU work submission begins. The DRM seam and the
GPU-silicon bring-up code share a translation unit but **zero state**.

**The implementation table** (V5's verified merge — CONFIRMED verdict — of the shim state, Mesa
call-order, and Sephiroth-correct answers; compute-only path). **1st?** = needed for the first
real `vkCmdDispatch`+`vkWaitForFences`. Ioctl nrs corrected per uapi (the kernel recon's
0x4A–0x4E guesses were wrong; correct high-range nrs are 0x50–0x55):

| # | Surface | 1st? | What to return (Sephiroth-correct) | Backing object | Today | Diff |
|---|---|---|---|---|---|---|
| 1 | `open/openat /dev/dri/renderD128` | YES | fd ≥ 3 | VFS `PUCK_SF_DRM` node (`:1198-1199`) | DONE | — |
| 2 | `fstat` on the fd | YES | `S_IFCHR`, rdev 226:128 (`:1469`) | static | DONE | — |
| 3 | `/sys` stat/readlink/getdents walk | YES | PCI ids; optionally update `1002:163F@00:01.0` → real `1002:1435 rev 0xae @04:00.0` (cosmetic) | static VFS | DONE | — |
| 4 | `fcntl(F_DUPFD_CLOEXEC)` | YES | real dup | fd table | DONE (`:1970`) | — |
| 5 | `DRM_IOCTL_VERSION` (d,0x00) | YES | `"amdgpu"` 3.59.0 | constant | DONE (`:1638`) | — |
| 6 | `GET_CAP` (d,0x0c) | YES | `SYNCOBJ`=1 mandatory; **`SYNCOBJ_TIMELINE`=0** shrinks syncobj surface | constant | DONE (1; `DRM_CAP_PRIME`=3 import\|export `:1658`) | S |
| 7 | `SET_CLIENT_CAP`/`GEM_CLOSE` | no | accept/no-op (real close frees carveout eventually) | BO table | DONE | S |
| 8 | `INFO/ACCEL_WORKING` (0x00) | YES | 1 (libdrm init hard-fails on 0) | constant | DONE (`:1748`) | — |
| 9 | `INFO/DEV_INFO` (0x16) ×2 | YES | family 144, external_rev 1, 1 SE × 1 SA, `cu_bitmap[0][0]=0xFF`, wave32, FUSION; **add `virtual_address_offset`(+144)=0x200000** | constants | DONE minus VA fix | S |
| 10 | `INFO/MEMORY` (0x19) | YES | real: vram=1024 MiB carveout (minus kernel reservations), gtt=GART size | carveout/GART geometry | fabricated 256 MiB/3 GiB | S |
| 11 | `INFO/HW_IP_INFO` (0x02) | YES | **GFX must report 10.3 + rings≠0 despite never running** (★ classification); COMPUTE 10.3 ≥1 ring; gate by ip_type (others 0); set `ip_discovery_version`(+28) too | constant | DONE but ignores ip_type (`:1792`) | S |
| 12 | `HW_IP_COUNT` (0x03), `FW_VERSION` (0x0e ME/MEC/PFP), `READ_MMR_REG` (0x15, gb_addr_cfg) | YES | count=1; real fw versions from `g_gpuFw`; gb_addr_cfg via real BAR5 read | `g_gpuFw`, `g_gpuRegs` | zeros via `default:` | S |
| 13 | `GEM_CREATE` (0x40) | YES | honor VRAM\|GTT from **one carveout pool** (legit on UMA — RADV sets both bits on APUs by design); map CPU-access flags → attrs | **NEW: carveout/GART allocator** (today host RAM `:1701`) | works, wrong backing | M |
| 14 | `GEM_MMAP` (0x41) + `mmap(fd,offset)` | YES | fake offset → BO pages, `MAP_SHARED` | BO table + existing mmap path (`:2060`); carveout phys user-visible (2 MiB US grain + WC) | DONE (host-RAM) | S–M |
| 15 | `GEM_VA` (0x48) MAP/UNMAP | YES | 0; kernel receives RADV-chosen `va_address` (VA allocation is 100% userspace — libdrm `amdgpu_va_manager`; no per-alloc ioctl) | **NEW: GPUVM page tables** for the submitting VMID (gfx10 PTE format; analogous to the working GART/VMID0) | no-op (`:1724`) | **L** |
| 16 | `CTX` (0x42) | YES | monotonic ctx_id + per-(ctx,ip,ring) fence-seq bookkeeping | ctx table | DONE (fake) | S |
| 17 | **`CS` (0x44)** | **YES — the one true gap** | parse `chunk_array` (u64 ptrs): `CHUNK_ID_IB` (ip_type=COMPUTE(1), va_start→GPUVM), **`CHUNK_ID_BO_HANDLES` always present** (`operation=~0, list_handle=~0`, inline entries — the BO_LIST ioctl is never used), optional FENCE, `SYNCOBJ_IN/OUT`; return seq in `cs.out.handle`; never spuriously `-ENOMEM` (RADV retries 1 s) | **NEW: live MEC compute ring** — MQD/HQD via the proven KIQ commit recipe, doorbell, `INDIRECT_BUFFER\|VALID` + `RELEASE_MEM` EOP fence | absent → `-EINVAL` | **L** |
| 18 | `WAIT_CS` (0x49) | teardown | poll fence seq vs timeout (TSC-deadline, `GpuWaitReg` pattern) | fence memory | absent | S |
| 19 | Syncobj CREATE/DESTROY/WAIT/RESET/SIGNAL | YES | syncobj = {fence-seq ref}; WAIT polls real EOP value | fence BOs + seq table | fake always-signalled (`:1672-1693`) | S–M |
| 20 | Syncobj TRANSFER/QUERY/TIMELINE_WAIT; HANDLE_TO_FD/FD_TO_HANDLE | only if timeline cap=1 / fd export | — | — | fake | skip via #6 |
| 21 | `BO_LIST` (0x43), `FENCE_TO_HANDLE` (0x54), GEM_OP/USERPTR/WAIT_FENCES/VM/SCHED (0x50-0x53, 0x55), `/dev/dri/card0` | **NO** — zero RADV call sites / KHR_display-only | — | — | absent | skip |
| 22 | `GET_CLIENT` (d,0x05) | keep | auth=1 (already answered) | constant | DONE (`:1646-1652` — handler exists because an auth check was hit during bring-up; "never issued on render nodes" per Mesa source-read, so treat as UNVERIFIED-but-cheap and keep it) | — |
| 23 | Supporting syscalls: `clock_gettime`, `sched_getaffinity`, `getrandom`, `futex`/`clone`, anon `mmap` | YES | all exist | — | DONE | — |

**The synthetic→real flip, precisely** (V5's correction of the "env var off, node present"
framing): there is no env var and no mode switch. What changes is per-handler backing:
GEM_CREATE (host RAM → carveout allocator), GEM_VA (no-op → GPUVM PT writes), CS (`-EINVAL` →
chunk parse + MEC ring kick), syncobj-WAIT/WAIT_CS (instant → EOP-seq poll), INFO constants
(fabricated → real). Everything above the ioctl bodies — node, `/sys` walk, stat/rdev, fd dup,
mmap-on-fd, syscall table — is already real and QEMU-proven. **How both targets keep working
with one binary (review gap):** each handler selects its backing on hardware presence (register
BAR mapped / `g_gpuRegs` populated → real; else the synthetic constants), so QEMU-green stays
meaningful after the flip — there is no build-time or env-var mode.

**CS routing guard (review gap — silent-hang prevention).** Because the ★ classification trap
forces the GFX IP to advertise `available_rings≠0`, RADV will expose a graphics-capable queue
family whose submissions arrive as `ip_type=GFX` CS ioctls aimed at the wedged gfx front end.
The CS handler must reject any IB chunk with `ip_type != AMDGPU_HW_IP_COMPUTE` loudly (log +
`-EINVAL`), and the guest (vktest, later the option-1 front-end) must select RADV's compute-only
queue family — a misrouted submit is then a diagnosable error instead of a wedge.

**Risk & evidence.** V5's bottom line: the shim's *surface* is bounded (verified nothing hides
behind libdrm — RADV issues raw ioctls for CS/CTX/VA/INFO via `ac_linux_drm.c` and only uses
libdrm for BO lifecycle + userspace VA management) and ~60% built. The remaining 40% is exactly
three components: CS chunk parsing (bounded, spec'd), carveout BO allocator (bounded), and two
genuinely new hardware-facing pieces — **GPUVM page tables and a live fence-signalling MEC
ring** — which are *not* "existing kernel objects" and inherit Blockers 1/3. The shim is not the
risk; the MEC ring and GPUVM are.

### 2.5 Present / scanout path

**Now — implemented and hardware-proven, not a plan** (V2: CONFIRMED). `PuckGopCapture`
(`puck-efi.c:2876-2893`) captures `FrameBufferBase`/width/height/`PixelsPerScanLine` before
ExitBootServices; the fb is WC-mapped post-paging (`:6056-6063`, `PuckMapMmio` high-BAR 4 KiB
chain `:2814-2836` — on the Deck the fb lives inside BAR0 at ~995 GiB, above the identity map)
and the kernel console renders every boot's log to the panel through it
(`FbPutPixel`/`FbDrawGlyph` `:2934-2973`, `mfence` per byte). Every one of the 12+ real boots —
including minutes-long parked health screens after `PuckHang()`, under heavy concurrent
PSP/RLC/CP/GART perturbation — was read off a panel driven by exactly this mechanism. GOP
scanout survives ExitBootServices because the display controller keeps scanning the programmed
address; Linux's `efifb`/`simpledrm` are the production precedent (`efifb_probe` uses
`ioremap_wc`, zero mode-setting — VERIFIED `drivers/video/fbdev/efifb.c`).

**Panel facts** (W3): native **800×1280 portrait**, stride **3328 bytes** (832-px padded rows at
32bpp), per the upstream Deck sysfb/efifb quirk chain
([sysfb_efi fix](http://www.mail-archive.com/dri-devel@lists.freedesktop.org/msg579537.html),
[Galileo orientation quirk](https://lore.kernel.org/all/20240627203057.127034-1-mattschwartz@gwu.edu/T/)).
The code independently derived rotation 90° from a real boot (`PUCK_FB_ROTATION`,
`:2857-2860`) and handles pitch dynamically. **PSR risk is low**: Van Gogh is DCN 3.0.1; AMD
enables PSR by default only for DCN 3.1+
([Phoronix](https://www.phoronix.com/news/AMDGPU-Linux-5.16-PSR)); PSR requires driver-negotiated
DPCD + DMCUB arming a GOP driver never performs — and empirically the parked health screen stays
live indefinitely. **HDP flush**: the console works with plain `mfence`; the single-MMIO
`HDP_MEM_FLUSH` write (`VGH_HDP_MEM_FLUSH` `:3413`) is an available belt-and-suspenders, not a
requirement at human timescales (V2 downgraded W3's "important" framing).

**Needed for demo-on-Deck.** M1's remaining work is a format-aware full-frame blitter plus two
measurements:
capture `Info.PixelFormat` (offset +12 — currently unread; the console dodges it with
BGR/RGB-invariant colors `:2853`) and time a full 800×1280 blit (TSC) with/without the HDP poke.
The alternatives ladder above CPU blit: **(b) SDMA copy** (own microcode already PSP-loaded,
own ring/MQD, no PFP/CE triad — plausible but unstarted); **(c) GPU/compute write into the
scanout address** — the primitive is proven (`WRITE_DATA mem=Y CACHE_POLICY=BYPASS`), the
expected MC address math is `mc = 0xF4_0000_0000 + (fbPhys − 0xF8_E000_0000)` (untested);
**(d) minimal DCN flip** — a 5-register HUBP primary-surface address swap
(`hubp2_program_surface_flip_and_addr`, VERIFIED `dcn20_hubp.c`; DCN301 reuses dcn20 hubp),
double-buffered, worth it only once double-buffering against a non-GOP render target matters and
contingent on identifying GOP's live pipe/tiling.

**Risk & evidence.** Only two unknowns remain, both trivially measurable on the next boot: the
pixel-format channel order and the frame-rate blit cost. Neither can be contaminated by
Blockers 1–3 — the display path is entirely disjoint from the CP/CE/IB machinery.

### 2.6 Engine/demo hosting strategy

**Now.** The demo's render core is **pure compute** and already separable: `SdfWorldEngine`
dispatches 5 compute kernels (beam → instance-cull → cull-args → world-views → composite,
`SdfWorldKernels.cs:3-23`) through the neutral `IGpuComputeServices` seam, outputs an
`R8G8B8A8Unorm` storage image, and `RenderFrame` (`SdfWorldEngine.cs:688-709`) returns a
`byte[]` RGBA with no window/surface/swapchain — the exact path `Puck.Post`'s
`WorldStage.RenderWorldFrame` (`WorldStage.cs:141-152`) already drives. Sky/fog/text/diegetic
screens all live inside the views kernel. The Vulkan backend is loader-path-agnostic
(`VulkanNativeLibrary.LibraryPathOverride`, `VulkanNativeLibrary.cs:16-25`; all entry points via
`vkGetInstanceProcAddr`, zero static DllImports). The couplings a bare-metal port must cut:
the demo hard-wires validation on (`VulkanRenderer.cs:110`), enables `VK_KHR_swapchain`
unconditionally (`VulkanLogicalDeviceFactory.cs:182`), and `VulkanPhysicalDeviceSelector`
rejects devices without a present-capable queue family (`VulkanPhysicalDeviceSelector.cs:63-100`)
— all properties of the *presenting* device factory, not the compute engine. There is no truly
windowless device-creation path in the tree today (the `Headless` window stub carries no surface
payload and `VulkanRenderer.Initialize` throws on it).

**The option ladder** (V4: CONFIRMED, with ordering corrections):

- **Option 1 — minimal front-end (the next-session vehicle).** A **dynamic (Route A)** NativeAOT
  `linux-musl-x64` guest — *not* static-pie (`StaticExecutable=true` is incompatible with
  loading the RADV ICD: musl static-pie can't dlopen, `embed-dyn.ps1:3`; W4's static-pie
  recommendation is overridden here). Composition: `Puck.SdfVm` + `Puck.Scene` (source-gen STJ
  context exists — AOT-safe run-document loading) + `Puck.Vulkan`(+ the compute registrations,
  `VulkanComputeServiceRegistration.cs:18-58`, hand-wiring the descriptor-allocator/
  queue-submitter/shader-module/storage-buffer/surface-transfer factories too) + **one new
  piece**: a surfaceless `IGpuDeviceContext` factory (no WSI extensions, no validation,
  graphics/compute-queue-only selection). `LibraryPathOverride` → RADV ICD path. Kernels + run
  doc staged into the VFS via the existing `PuckVfsAddFile` preload pattern
  (`SdfWorldKernels.Load` takes an explicit directory, `SdfWorldKernels.cs:34-45`). **The
  staged file set is bounded and already exists as repo artifacts (review gap, verified):** the
  five compute kernels are checked-in `.spv` files — `sdf-beam`, `sdf-instance-cull`,
  `sdf-cull-args`, `sdf-world-views`, `sdf-world-composite` under
  `src/Puck.SdfVm/Assets/Shaders/Sdf/` (no compile step) — plus the run-document JSON; stage to
  the ESP and preload via the `PuckEfiPreloadSos` pattern. The frame renders with the run
  document's authored camera/viewport state — **zero input machinery is compiled in**; the
  first controller-driven frame is an option-2/M7 concern gated on xHCI (§2.7). Loop:
  `RenderFrame` → `byte[]` → GOP blit (needs the kernel framebuffer seam, §2.1 item 6), with a
  render-N-frames-then-`exit_group` mode so the boot's log persists (§2.1 caveat).
  Single hot managed thread; `ServerGC`/`ConcurrentGC` off.
- **Option 2 — full Puck.Demo as guest.** Strictly a superset of option 1's work **plus** full
  signal delivery **plus** an AOT publish of the MEDI/reflection-heaviest project in the tree
  (never attempted — zero `PublishAot` anywhere in `src/`) **plus** a display/window seam that
  doesn't exist (the launcher hard-requires an `INativeWindow` with a surface payload; WSI-free
  RADV can't create `VK_KHR_surface`). Real eventual destination; not co-equal with option 1.
- **Option 3 — port onto `Puck.Runtime` (no-GC zerolib).** Ruled out as anything but a rewrite:
  the engine is idiomatic GC'd C# (records, exceptions, interface-heavy seams); zerosharp's own
  framing calls the no-GC tier "so severely limited it's rather pointless" for real apps.
  `Puck.Runtime`'s place is kernel-side, where it already lives.

**Milestone honesty** (V4): option 1's provable ceiling *this side of the hardware blockers* is
"NativeAOT front-end boots under Puck.BareMetal, loads the real run document, compiles the real
SDF kernels through RADV/ACO, and reaches `vkQueueSubmit`" — QEMU exercises everything except
silicon execution, so the guest vehicle and the MEC/IB workstream proceed in parallel.
End-to-end pixels additionally require §2.4 rows 13–19 plus a working MEC IB.

**Risk & evidence.** The single genuinely new Puck-side artifact is the surfaceless device
factory (mirror `VulkanPhysicalDeviceSelector` minus the present requirement). The larger
unknowns are on the kernel side (§2.1) and the AOT-publish behavior of the chosen assembly set —
neither `Puck.SdfVm` nor `Puck.Vulkan` has been AOT-published either, though both are far less
reflection-dependent than the demo host. **A third gap surfaced by review: the publish
environment itself.** NativeAOT cannot cross-compile Windows→linux-musl; the publish must run
in a musl-capable Linux environment (the WSL Alpine chroot needs the .NET 10 SDK + clang/musl
toolchain — `mkguest.ps1` hardcodes Ubuntu/glibc today and covers only C guests). Standing this
up and proving it with a hello-GC guest is day-one work for the parallel track, not a detail.

### 2.7 Deck periphery (input / net / audio / timers / thermals)

**Input (the demo needs a controller).** The internal pad is a standard USB HID composite
device, VID `0x28de` PID `0x1205` (VERIFIED `drivers/hid/hid-ids.h`), on one of Van Gogh's xHCI
functions (`1022:162c`/`163b` at 04:00.3/.4 — which one hosts the pad is unresolved, needs an
on-hardware topology dump). Lizard mode (firmware kb/mouse emulation) is disabled with **two HID
feature reports** — `ID_CLEAR_DIGITAL_MAPPINGS` (0x81) + `ID_SET_SETTINGS_VALUES` (0x87,
trackpads→NONE, Deck also watchdog off) — VERIFIED from `hid-steam.c` (`steam_set_lizard_mode`).
The from-scratch cost is a minimal xHCI driver (command ring + event ring + one device slot +
one interrupt-IN endpoint) — structurally the same ring/doorbell shape as the GPU CP work
already built; INFERENCE: materially smaller than the GPU bring-up. This is the one periphery
item that is demo-critical.

**Network.** Wi-Fi (Qualcomm WCN6855, PCIe, `ath11k`-class firmware-heavy 802.11ax) is
confirmed undrivable-bare-metal — skip. Dock Ethernet: the common AX88179/RTL8153 chips are
**vendor-protocol, not pure class** (correction to the prior framing); the minimal-driver path
is a generic CDC-ECM/NCM *class* driver with a driverless-branded adapter (AX88179B) or an
Android phone in tether mode as the peer. PXE via the existing UEFI network work remains the
lower-effort dev-loop path. Network is dev-loop convenience, not demo-critical; the ring-3 guest
has zero socket syscall surface anyway (§2.1).

**Audio.** Van Gogh ACP runs under SOF — DSP firmware blob + topology file + NAU88L21/MAX98388
codec init (Phoronix, Collabora patch series). No PC-speaker or GPIO beep path exists. **Skip
entirely**; scope comparable to the PSP/RLC firmware work for a beep.

**Timers.** Invariant TSC is architecturally guaranteed on Zen 2 (Family ≥10h). **Do not build a
CPUID 0x15/0x16 calibration path** — it yields nothing usable on AMD (REPORTED: Zen 2 does not
populate those CPUID leaves usefully, so Linux's `native_calibrate_tsc` path falls through on
AMD; the exact vendor gating in `arch/x86/kernel/tsc.c` was not independently re-verified). AMD
uses the P-state MSR (`PStateDef`, exact index unconfirmed) or reference calibration. Puck's existing PIT-calibrated TSC
(`PuckInitClock`, `puck-efi.c:565`) is exactly the right fallback and already works. HPET is
chipset-class-typical at `0xFED00000` but unconfirmed on this board (probe once in ring 0).
The 50400 ticks/sec engine timebase rides on TSC with no new work.

**Power/thermal/EC/battery.** All autonomous: SMU firmware (already posted by GOP) self-manages
thermal/clocks without a host driver — bring-up never touches SMU/DPM registers, so the GPU
idles at the VBIOS boot P-state (same situation as sitting in BIOS setup); the fan is
EC-autonomous by default (SteamOS's `jupiter-fan-control` is an *override*); the EC (`VLV0100`)
exposes fan/battery/PD telemetry with **no OS-heartbeat requirement** (VERIFIED absence in the
platform driver source); battery cutoffs are BMS-level and unconditional. **Nothing forces the
OS to service anything to stay safe.**

---

## 3. Verified-claims register

Five adversarial verification passes ran against the recon output. Each verdict, one line, plus
the key reasoning. Then the falsified-hypotheses list — carried forward so nobody re-derives.

### V1 — "A MEC compute queue bypasses Open Blocker 1" → **LIKELY**

Architecturally sound and multiply-precedented: the wedge signature is entirely CPG-domain
(PFP/CE/ring-ROQ; `UTCL2IU_BUSY` clear — fabric idle); MEC is a separate block with its own
microcode (already PSP-loaded), own halt/status registers (already vendored), and **no
CSB/CE/CONTEXT_CONTROL machinery at all** — Blocker 2 is definitionally impossible on a compute
queue. tinygrad's production driver reaches working compute dispatch with zero PFP/ME/CE
interaction; amdgpu's compute IB self-test is the same 5-dword `WRITE_DATA` shape.
**Correction found:** the claim that MAP_QUEUES is "already proven executable" via the KIQ ring
test is FALSE — no PM4 packet has ever gone through the KIQ ring (`puck-efi.c:5558-5560`); KIQ
liveness is proven only by its scheduler side-effect. The proven mechanism is direct-MMIO HQD
commit; the path is a high-confidence plan, not a demonstrated capability. Residual risks:
zero MEC packets ever executed here; buffers must live in the carveout (Blocker 3); run the
compute-proof boot without a prior gfx attempt (contamination) and note mec2's fw-type mapping
is code-flagged UNVERIFIED.

### V2 — "Present = CPU copy into the surviving GOP framebuffer" → **CONFIRMED**

Not a plan — already implemented and hardware-proven by the kernel's own panel console
(`PuckGopCapture` `:2876`, WC map `:6059`, minutes-long live rendering across all documented
boots, handoff `:217-231`). PSR-blanking is empirically falsified (parked screen stays live);
HDP flush is optional (console works with `mfence` alone — W3's "required" framing downgraded).
Only remaining unknowns: the `PixelFormat` field (unread today) and frame-rate blit timing —
both one-boot measurements. "Plausibly GPU-writable" is correctly hedged: the `WRITE_DATA`
primitive is proven, the fb's MC-address mapping untested.

### V3 — "Boot-11 dword-222 stall is a CSB emission bug" → **REFUTED**

Independent desk-check: Puck's emission is byte-exact to upstream `gfx_v10_0_cp_gfx_start()` —
verbatim vendored table (`clearstate_gfx10.h:961-970`; `def_1`/`def_2` payload arrays full
length), correct PACKET3 count encoding (no −1, `puck-efi.c:4043`), identical framing/order
(`:5128-5171`), WPTR advanced to the full 961 dwords so the stop at 222 is a genuine mid-stream
stall. Dword 222 is exactly the extent-2 `SET_CONTEXT_REG(272)` header (`0xC1106900`); extent 1
(identical construction, 215 regs) was consumed cleanly — logically ruling out any generic
emission off-by-one, which would have desynced at extent 1. The stall is a CP front-end
fetch/parse hang at the packet boundary. Handoff plan item (d)'s leading suspect is dead;
new leading suspects: fetch coherency/the eraser on the first 274-dword packet (predicts the
stall moves with the ring in the carveout), and un-cleared CP state across same-boot attempts.
Cheapest disambiguator: `CP_PFP_HEADER_DUMP`×8 on the stuck boot.

### V4 — "Ring-3 hosting: the option ladder" → **CONFIRMED** (with re-ranking)

Option 1's separability verified seam-by-seam (`Puck.SdfVm` backend-neutral; explicit-directory
kernel loading; public `LibraryPathOverride`; `WorldStage.RenderWorldFrame` as the proven
template; the surfaceless device factory confirmed as the one new piece). Two corrections:
(i) option 1 requires a **dynamic** (Route A) guest — static-pie can't reach the RADV ICD, so
W4's `StaticExecutable=true` recommendation doesn't apply to this vehicle; (ii) option 2 is
**not** co-equal — it needs option 1's seams plus full signal delivery plus a never-attempted
AOT publish of the MEDI demo host plus a nonexistent display seam. New load-bearing kernel
findings: the current `mremap` stub is an **EE-startup infinite loop** (not a graceful failure),
`sysinfo` zeros are init-fatal for the GC, the 64 MiB arena cannot absorb a GC reserve, and
W4's "tkill (231)" was a wrong syscall number (tkill=200, tgkill=234, 231=exit_group).

### V5 — "The DRM shim is bounded and implementable" → **CONFIRMED** (with one correction)

The surface is genuinely closed (RADV's raw-ioctl usage fully enumerated from Mesa source;
nothing hidden behind libdrm) and *smaller* than planned: `BO_LIST`, `FENCE_TO_HANDLE`, and
`GET_CLIENT` all have zero call sites on this path and drop out. The VFS/fd/ioctl/mmap plumbing
exists and is QEMU-proven today; ~60% of the table is done. **Correction:** the claim's
"backed by the kernel's existing GPU objects (rings, fences)" is overstated — the GPUVM page
tables and the live fence-signalling MEC ring are *new*, hardware-gated work inheriting
Blockers 1/3 (no fence has ever signalled on this silicon). The shim is not the risk; the MEC
ring and GPUVM are. Also adjudicated: the kernel recon's ioctl nrs 0x4A–0x4E were wrong
(correct: 0x50–0x55); there is no synthetic/real env-var switch — the flip is per-handler
backend replacement; and the shim's legacy `hw_ip_version` fields worked in QEMU despite
`drm_minor=59` — populate `ip_discovery_version` too, belt-and-braces.

### Falsified hypotheses (carried from the handoff + this research — do not re-derive)

From the handoff's boot table (`deck-bringup-handoff.md:194-213`), plus two new entries:

1. **Preamble-only fix** (boot 1) — missing `PREAMBLE_CNTL`/`CONTEXT_CONTROL` was not why the
   legacy ring didn't fetch. DEAD.
2. **Legacy `CP_RB0_*` ring viability** (boots 2–3) — dead identically across interface, memory
   location, and wptr-delivery axes; gfx10.3 firmware serves only the HQD/MQD interface. DEAD.
3. **Scheduler-designation-bit-alone** (boot 5, "hqd-poke") — a designation bit without a
   genuinely live KIQ HQD unblocks nothing. DEAD.
4. **CE-halted as a workaround** (boot 6, "hqd-noce") — wedges the PFP worse; CE must run. DEAD.
5. **`CE_PARTITION_BASE` 2-vs-3 as the IB-wedge cause** (boot 8b) — 3 is correct per `nvd.h`,
   and the fix made no difference to the wedge. DEAD.
6. **Minimal/empty preamble sufficiency** (boot 10) — full CSB *content* is required, not just
   correct packet ordering. DEAD.
7. **"The CE is wedged" / `DE_COUNTER_UNDERFLOW` as the blocker** (boots 7–11) — the CE is
   alive (`CP_CE_INSTR_PNTR` moves); the underflow readout is plausibly idle signature. The real
   signature is the PFP wait-loop. DEAD (reframed, boot 12).
8. **"CE has no valid microcode"** (boots 11–12) — all three front-end icaches held valid TMR
   addresses before Puck touched them; the CE-icache reprogram never even stuck (register is
   fw/RLC-owned). DEAD. (Corollary: `PuckGpuCeIcacheFix` should be deleted — still called at
   `:5851` as of this worktree.)
9. **NEW — "the dword-222 stall is a CSB emission off-by-one / section-boundary miscount"**
   (handoff plan item d's leading suspect) — REFUTED by V3's byte-exact desk-check. Do not audit
   the emission again; instrument the fetch instead.
10. **NEW — "the KIQ ring has proven PM4 execution"** — it has not; no packet has ever gone
    through the KIQ PQ (V1). Its liveness evidence is the gfx-PFP-unblocking side-effect only.

---

## 4. Open questions only a Deck boot can answer

The measurements list for the next hardware session, cheapest first. Items 1–4 need no GPU
bring-up and cannot be contaminated by Blockers 1–3.

1. **GOP `PixelFormat`** — capture the u32 at `Info+12` in `PuckGopCapture` and log it
   (expected `PixelBlueGreenRedReserved8BitPerColor`). Closes the only unknown format parameter.
2. **BAR0 offset arithmetic** — log `g_fb.phys − 0xF8_E000_0000` to pin the aperture offset for
   future GPU-write/DCN-flip work.
3. **Full-frame blit timing** — fill 800×1280 through the WC mapping, `rdtsc` around it, with
   and without a trailing `HDP_MEM_FLUSH` poke. Quantifies CPU present cost and the flush's
   (ir)relevance.
4. **Rotation/extent sanity** — asymmetric corner-marker pattern; confirm the 90° mapping and
   visible 800 vs pitch 832 on the OLED unit.
5. **Carveout-first clean run** (handoff plan item a) — does the boot-11 stall move/vanish with
   the ring in the carveout, and does the eraser recur when GART is out of the fetch path
   entirely? Move the KIQ buffers to the carveout in the same build.
6. **`CP_PFP_HEADER_DUMP`×8 on the stuck boot** — last decoded header `0xC0D76900` vs
   `0xC1106900` separates fetch-stall from parse-hang at dword 222 (V3's disambiguator).
7. **First MEC packet** — cheapest probe: one benign packet through the already-active KIQ PQ
   (doorbell 0); then the full user-queue ring test + IB test (V1's work items). Does MEC's IB
   fetch inherit Blocker 1 or not? This is the single highest-leverage unknown.
8. **`GRBM_SOFT_RESET.SOFT_RESET_CP` efficacy** — does asserting it (~50 µs) between attempts
   clear the `CP_STAT=0x94008200` contamination that halt/unhalt does not?
9. **Eraser timing** (plan item c, if it still matters after 5) — re-read ring HEAD/RPTR during
   the SCRATCH poll to timestamp erasure relative to CP progress.
10. **GPU write to the scanout MC address** — one ring-resident `WRITE_DATA` targeting
    `0xF4_0000_0000 + fbOffset`; upgrades "plausibly GPU-writable" to proven, zero new
    infrastructure (rides on the existing ring test).
11. **Which xHCI hosts the internal pad** — `lsusb -t`-equivalent topology walk of
    `1022:162c`/`163b`/`163a`; prerequisite for controller input (an M7/option-2 concern).
12. **HPET presence** — probe `0xFED00000` for the HPET signature (or parse the ACPI `HPET`
    table); settles the timer-fallback question.
13. **NativeAOT guest smoke** (after §2.1 items 1–3 land) — does a trivial GC'd
    `linux-musl-x64` hello (dynamic, Route A) reach `Main` and survive a forced
    `GC.Collect()` on the fake kernel? Also empirically settles whether the eventpipe/diagnostics
    socket is attempted at startup (W4's unresolved flag — mitigate with
    `DOTNET_EnableDiagnostics=0` if so).

---

## 5. Source index

**Provenance note.** The ten recon reports and five verification reports this pack synthesizes
were ephemeral working artifacts of the research workflow — they are not committed anywhere;
this pack and the session plan are the surviving record, and their load-bearing content
(including the kernel recon's full 11-item gap list) has been integrated into §2 above.

### Repo files (all paths worktree-relative)

- `experimental/Puck.BareMetal/compat/native/puck-efi.c` — the kernel: boot chain, syscalls,
  VFS, DRM shim, GPU bring-up, GOP console (all `file:line` cites above).
- `experimental/Puck.BareMetal/compat/native/puck-efi-x64.asm` — syscall/timer/resume
  trampolines, ring-3 entry.
- `experimental/Puck.BareMetal/compat/native/puck-netif.c`, `puck-tls-mbedtls.c`,
  `puck-ca-bundle.c` — kernel-side lwIP/mbedTLS proof path.
- `experimental/Puck.BareMetal/docs/deck-bringup-handoff.md` — ground truth: recipe, blockers,
  boot table, falsified list, dev loop. **The boot-history table lives there, not here.**
- `experimental/Puck.BareMetal/docs/amd-vulkan-plan.md` — staged roadmap a–h (stage-e status
  superseded by the handoff); `docs/gfx103-bringup-spec.md` — register/protocol spec (current,
  authoritative); `docs/ring3-process-host-plan.md` — historical archive of the syscall/threads/
  networking substrate (its cited commit hashes no longer resolve; pre-squash history).
- `experimental/Puck.BareMetal/amdgpu/include/clearstate_gfx10.h`, `clearstate_defs.h`,
  `asic_reg/gc/gc_10_3_0_{offset,sh_mask,default}.h`, `nvd.h` — vendored MIT amdgpu headers.
- `experimental/Puck.BareMetal/radv/build-radv-musl.sh`, `build-vktest-musl.sh` — the closure
  build; `samples/EfiLinux/{Program.cs,GuestElf.cs,guest/vktest.c,guest/drm.c,mkguest.ps1,`
  `embed-dyn.ps1,run-qemu.ps1,stage-deck.ps1}` — guest loader + tooling.
- `experimental/Puck.BareMetal/build/Puck.BareMetal.Efi.{props,targets}` — the EFI link
  (no CRT, exports cleared, `/SUBSYSTEM:EFI_APPLICATION`); `NOTICE.md` — licensing inventory
  (verified consistent with the tree).
- Engine side: `src/Puck.SdfVm/{SdfWorldEngine,SdfWorldKernels}.cs`;
  `src/Puck.Post/Stages/WorldStage.cs`; `src/Puck.Vulkan/Interop/VulkanNativeLibrary.cs`;
  `src/Puck.Vulkan/Factories/{VulkanInstanceFactory,VulkanLogicalDeviceFactory}.cs`;
  `src/Puck.Vulkan/VulkanPhysicalDeviceSelector.cs`;
  `src/Puck.Vulkan.Presentation/{VulkanRenderer,VulkanComputeServiceRegistration}.cs`;
  `src/Puck.Launcher/LauncherWindowHostedService.cs`; `src/Puck.Demo/Program.cs`,
  `DemoHost.cs`, `GpuHostComposition.cs`; `src/Puck.Scene/PuckSceneJsonContext.cs`.

### Kernel / Mesa / libdrm / runtime source (read directly)

- `drivers/gpu/drm/amd/amdgpu/gfx_v10_0.c` — `cp_gfx_start`, `ring_test_ring/ib`,
  `emit_ib_gfx/compute`, `emit_fence` (RELEASE_MEM), `cp_gfx_enable`, `soft_reset`;
  `amdgpu_ib.c` — job-gated cntxcntl/frame_cntl; `nvd.h` — PM4 field encodings;
  `clearstate_gfx10.h`; `vega10_enum.h` (`CACHE_FLUSH_AND_INV_TS_EVENT=0x14`);
  commits `683308af030c` (CSIB fix), `867cf768cbe3` (HEADER_DUMP FIFOs).
- Mesa 25.0.7 (matches upstream): `src/amd/common/{ac_gpu_info.c,ac_linux_drm.c}`;
  `src/amd/vulkan/{radv_physical_device.c,radv_instance.c,winsys/amdgpu/{radv_amdgpu_winsys,`
  `radv_amdgpu_bo,radv_amdgpu_cs}.c}`; `src/vulkan/runtime/{vk_instance.c,vk_drm_syncobj.c}`;
  `include/drm-uapi/amdgpu_drm.h`. libdrm 2.4.124: `amdgpu/amdgpu_device.c`.
- dotnet/runtime (main): `src/coreclr/nativeaot/Runtime/threadstore.cpp`,
  `unix/{PalUnix.cpp,UnixSignals.{h,cpp}}`, `FinalizerHelpers.cpp`;
  `src/coreclr/gc/unix/{gcenv.unix.cpp,cgroup.cpp}`;
  `src/native/minipal/{random.c,cpucount.c,descriptorlimit.c}`;
  `src/libraries/.../PortableThreadPool.GateThread.cs`.
- musl 1.2.6 (git.musl-libc.org): `src/thread/{pthread_create,pthread_kill,`
  `pthread_getattr_np}.c`, `src/signal/sigaction.c`, `src/conf/sysconf.c`.
- tinygrad: `tinygrad/runtime/support/am/ip.py`, `runtime/ops_amd.py` — the kernel-free MEC
  driver precedent.
- Linux display: `drivers/video/fbdev/efifb.c` (`efifb_probe`, `ioremap_wc`);
  `drivers/gpu/drm/amd/display/dc/hubp/dcn20/dcn20_hubp.c`
  (`hubp2_program_surface_flip_and_addr`); `dcn301_resource.c` (DCN301 ⊃ dcn20 hubp);
  `drivers/hid/{hid-ids.h,hid-steam.c}`; `arch/x86/kernel/tsc.c` (`native_calibrate_tsc` —
  CPUID 0x15/0x16 calibration, not useful on AMD; gating not independently re-verified).

### Key web sources

- [dotnet/sdk#37643](https://github.com/dotnet/sdk/issues/37643) — `StaticExecutable` static-pie
  (maintainer-confirmed); [dotnet/runtime#93158](https://github.com/dotnet/runtime/issues/93158)
  — musl interpreter mismatch; [dotnet/runtime#121623](https://github.com/dotnet/runtime/issues/121623)
  — RT-signal-queue hijack hang; [dotnet/runtime#85961](https://github.com/dotnet/runtime/issues/85961)
  — GC config ignored under NativeAOT;
  [MichalStrehovsky/zerosharp](https://github.com/MichalStrehovsky/zerosharp) — bare-metal tiers.
- Deck display: [sysfb_efi Deck fix](http://www.mail-archive.com/dri-devel@lists.freedesktop.org/msg579537.html)
  (800×1280, stride 3328);
  [Galileo orientation quirk](https://lore.kernel.org/all/20240627203057.127034-1-mattschwartz@gwu.edu/T/);
  [Phoronix Linux 7.0 EFI fb quirk](https://www.phoronix.com/news/Linux-7.0-EFI);
  [Phoronix PSR default DCN3.1+](https://www.phoronix.com/news/AMDGPU-Linux-5.16-PSR);
  [kernel DCN overview](https://docs.kernel.org/6.0/gpu/amdgpu/display/dcn-overview.html).
- Periphery: [ath11k docs](https://wireless.docs.kernel.org/en/latest/en/users/drivers/ath11k.html)
  (WCN6855/PCIe); [ASIX AX88179B](https://www.asix.com.tw/en/product/USBEthernet/Super-Speed_USB_Ethernet/AX88179B)
  (driverless CDC-NCM); [Phoronix Linux 6.6 Van Gogh SOF](https://www.phoronix.com/news/Linux-6.6-Sound);
  [Collabora SOF Vangogh fixes](https://patchew.org/linux/20250207-sof-vangogh-fixes-v1-0-67824c1e4c9a@collabora.com/);
  [LKML AMD PStateDef TSC](https://lkml.org/lkml/2025/8/19/240);
  [libmanette Deck HID notes](https://blogs.gnome.org/alicem/2024/10/24/steam-deck-hid-and-libmanette-adventures/);
  [Steam Deck platform driver LKML](https://lkml.iu.edu/hypermail/linux/kernel/2202.0/05513.html);
  [kernel amdgpu thermal docs](https://docs.kernel.org/gpu/amdgpu/thermal.html);
  [RADV docs](https://docs.mesa3d.org/drivers/radv.html);
  [Chips and Cheese — Van Gogh](https://chipsandcheese.com/p/van-gogh-amds-steam-deck-apu).
