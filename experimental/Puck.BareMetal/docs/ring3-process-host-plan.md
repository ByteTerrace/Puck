# Puck.BareMetal — Ring-3 Linux Process Host Plan

This document lets a fresh agent (on another machine) continue the Puck.BareMetal
UEFI-OS work without prior conversation context. **Work only in
`experimental/Puck.BareMetal/`. Never touch `src/Puck*`.** Licensing is in flux —
ignore license headers / do not gate work on them.

## ✅ STATUS: all gates landed — a real Linux binary runs (2026-06-15)

Gates 1–4 plus the pre-emptive IDT are implemented and **verified end-to-end in
QEMU+OVMF**. A genuine `musl -static-pie` Linux x86-64 binary
(`samples/EfiLinux/guest/hello.c`, embedded base64 in `GuestElf.cs`) loads, maps
its PT_LOAD segments, gets a SysV entry stack, drops into musl's `_start` **at
ring 3 on our own page tables**, and `printf`s/`write`s/`exit_group`s over COM1 —
byte-for-byte identical to the same binary under WSL.

- `c56cb93` Gate 1 — register-preserving syscall trampoline (save/restore guest GPRs).
- `d7b874e` IDT — 256-entry IDT + serial panic handler (trap-frame dump).
- `57e1d30` Gate 2 — our own identity-mapped page tables (2 MiB RWX) + CR3.
- `c008113` Gate 3 — ring 3 (user GDT+TSS, `iretq` entry, `sysretq` return).
- `55602f5` Gate 4 — broadened syscalls + SysV stack + the real musl binary.

**Beyond the gates (growing the ABI toward the engine guest):**

- `89bcc39` memory — `mmap`/`brk`/`munmap`/`mprotect` from a 64 MiB US guest arena, plus
  `clock_gettime` (TSC). Verified with `guest/mem.c`.
- `6e81c71` **threads** — the trampoline now saves/restores a per-thread context (`PuckCtx`,
  offsets mirrored by the asm `CTX_*` equ's) so a syscall can resume a *different* thread.
  `clone`/`futex`(WAIT/WAKE)/`sched_yield`/thread-`exit` + a round-robin scheduler. Verified with
  `guest/threads.c` (musl pthreads): 4 workers + a mutex + `pthread_join` compute
  `total=79999600000` with no lost updates.
- `5bbe7eb` **preemption** — `PuckCtx` widened to the full GPR set (incl. rcx/r11) so an
  arbitrary-instruction timer interrupt can be saved/resumed; both the syscall trampoline and a new
  PIT timer ISR resume via one shared `iretq` tail. Remap the 8259 PIC (vectors 0x20-0x2F), PIT
  ch0 ~100 Hz, IRQ0 unmasked; threads run IF=1, the kernel IF=0. Verified with `guest/preempt.c`:
  two threads that do pure compute with NO syscalls interleave (impossible under cooperative).
- `be0fb4a` **file/device I/O** — a synthetic read-only VFS (`/proc/cpuinfo`, `/proc/meminfo`,
  `/dev/zero|null|urandom`) + fd table behind `open`/`openat`/`read`/`readv`/`lseek`/`close`.
  Verified with `guest/io.c`: musl `fopen`+`fread` of `/proc/cpuinfo` returns our content. The
  on-ramp to `/dev/dri/cardN` (DRM) and the Mesa/Vulkan driver-piggyback endgame.

**Build + boot loop on the dev box (no human paste needed):** this machine has VS2026
(`cl`/`ml64`), QEMU+OVMF (`C:\Program Files\qemu`), and WSL Ubuntu with `musl-gcc`.
`samples/EfiLinux/run-qemu.ps1` enters the VS Dev shell, does a **clean** publish
(the NativeAOT `LinkNative` step does NOT track our `cl`/`ml64` objs, so an
incremental publish silently relinks a STALE `.efi` — the script wipes `obj`/`bin`
and asserts the `.efi` is newer than the objs), stages an ESP, and boots headless
with `-serial file:` so the agent reads COM1 directly.

**Scope:** this is a single-purpose host — the *one* guest is the engine, not arbitrary
Linux processes. The syscall layer is the ABI for running that single real binary
bare-metal, nothing more; there is no scheduler, no fork/exec, no second process.

**Known follow-ups (not blockers):** `exit_group` halts the kernel; for the engine guest
it should instead hand control back to `__managed__Main` for an orderly shutdown/reboot
(this is one-process teardown, not multitasking). User pages are marked US at 2 MiB
granularity (leaks neighbouring heap to ring 3); `SFMASK` clears only IF, not DF; no
`swapgs` (single-CPU uses globals); paging covers a fixed low 4 GiB. The real trajectory
is growing the syscall surface toward what the engine itself calls (mmap, futex, clock,
GPU ioctls, …), driven the same strace-first way the musl hello was.

The original gate-by-gate plan below is kept for provenance / future hardening.

After ANY code generation, format in two steps from the repo root:

```powershell
dotnet run tools/Tools.cs -- format   # 1. repo formatter
dotnet format                         # 2. then dotnet format
```

---

## 0. Re-orient first (read these before writing anything)

- Auto-memory: `baremetal-uefi-os-state` (the canonical state + roadmap). Also
  `baremetal-no-gc-hard-requirement`, `baremetal-companion-agpl`.
- `samples/EfiLinux/Program.cs` — the managed ELF64 loader + guest launch (Gate A/C).
- `compat/native/puck-efi-x64.asm` — GDT load + SYSCALL entry trampoline + a
  kernel-side `PuckDoSyscall` self-test helper.
- `compat/native/puck-efi.c` — kernel bring-up: `EfiEntry` (GetMemoryMap +
  ExitBootServices + bump heap), serial, GC-static init, lazy cctors,
  `PuckInitSyscalls` (GDT + SYSCALL MSRs), `PuckSyscallDispatch`.
- `build/Puck.BareMetal.Efi.targets` / `.props` — how the native `.c`/`.asm` are
  compiled (`cl.exe`, `ml64.exe`) and linked (`/SUBSYSTEM:EFI_APPLICATION`,
  `/NODEFAULTLIB`, no CRT/import libs).

## Current state (what already works)

The kernel self-hosts: native `EfiEntry` does `GetMemoryMap` + `ExitBootServices`,
arms a bump allocator over the largest `EfiConventionalMemory` region, owns a COM1
16550 UART, runs GC-static init + lazy cctors, then calls `__managed__Main`. The
full managed runtime works bare-metal (heap, arrays, reference statics, interface
dispatch, generics, `ArrayPool<T>`).

`samples/EfiLinux` loads a **hand-crafted static Linux x86-64 ELF** (built in
`BuildTestElf`, no Linux toolchain needed), copies its PT_LOAD segments, and jumps
to its entry. The guest's `write`/`exit` go: `syscall` instr → LSTAR asm trampoline
(`PuckSyscallEntry`) → C dispatcher (`PuckSyscallDispatch`) → serial.

**Limitations to remove (this plan):**
- Guest runs in **ring 0** on the **firmware identity map** (no own page tables).
- Trampoline **clobbers rdx/r10/r8/r9** (Win64 callee-clobbered; Linux preserves
  them) → breaks any real binary that expects syscalls to preserve them.
- `exit` just halts (no process model).
- Tiny syscall surface (`write`, `exit`, `exit_group` only).
- No SysV entry stack (argc/argv/envp/auxv).

## Build / test workflow

Build from a **VS Developer shell** (`Launch-VsDevShell.ps1 -Arch amd64`) so
`cl.exe`/`ml64.exe` are on PATH, then:

```powershell
dotnet publish experimental/Puck.BareMetal/samples/EfiLinux -r win-x64 -c Release
```

The published `Puck.BareMetal.EfiLinux.efi` is copied to an ESP dir as
`EFI\BOOT\BOOTX64.EFI` and booted headless in QEMU+OVMF with `-serial stdio`.
**The human operator runs QEMU and pastes back the serial log** — do not install VM
tooling. OVMF firmware on the operator's box:
`C:\Program Files\qemu\share\edk2-x86_64-code.fd` (CODE) + `edk2-i386-vars.fd`
(VARS). Each gate below must be **verified over serial before moving to the next.**

---

## Gate 1 — Harden the syscall trampoline (cheap, do first)

**File:** `compat/native/puck-efi-x64.asm`, `PuckSyscallEntry`.

Problem: the Win64 ABI lets `PuckSyscallDispatch` clobber rdx, r8, r9, r10 (and
r11). Linux guests assume the kernel preserves all registers except rax (result),
rcx and r11 (architecturally destroyed by `syscall`). After the dispatch `call`,
the trampoline currently just restores rsp/rip and `jmp rcx`, so a guest doing two
syscalls in a row with live data in rdx/r8/r9/r10 corrupts.

Fix: on entry (after switching to the kernel scratch stack) **save the guest's
rbx, rbp, rdi, rsi, rdx, r8–r15** to the kernel stack (or push them), marshal args,
`call PuckSyscallDispatch`, then **restore all of them** before returning. rax =
result; do not restore rax. rcx/r11 are allowed to be destroyed. Keep 16-byte stack
alignment for the `call` (currently `sub rsp,40h`). Still single-threaded
(`g_puckUserRsp/Rip` globals) — fine for now.

**Verify:** the existing hand-crafted ELF still prints its message and exits; add a
second `write` of a different register-dependent buffer to prove rsi/rdx survive a
prior syscall. Operator boots, pastes serial.

## Gate 2 — Our own page tables (identity-mapped PML4, RWX)

Stop depending on firmware mappings (and pre-empt firmware that NX-maps conventional
memory). **File:** `compat/native/puck-efi.c`, called from `EfiEntry` after the
bump heap is armed (so page-table pages come from our RAM) and **before**
`PuckInitSyscalls`.

- Allocate page-table pages from a dedicated 4 KiB-aligned bump (carve from the
  conventional region; must be page-aligned — current heap is only 16-byte aligned,
  so add a `PuckAllocPages(n)` helper that rounds `g_heapPtr` up to 4096).
- Build a 4-level identity map covering all physical RAM seen in the memory map
  (use 2 MiB PD large pages = `PS` bit, RWX = Present|RW|US-cleared-for-now). Cover
  at least the conventional region + MMIO holes you touch (COM1 port I/O needs no
  mapping; it's `in`/`out`). Map enough to cover the kernel image, heap, and guest.
- Load CR3 with the PML4 physical address. Keep paging RWX initially (set US later
  in Gate 3 for user pages).

**Verify:** boot still reaches `__managed__Main` and the guest still runs — i.e. we
survive the CR3 switch onto our own tables. Serial must show the existing
`[kernel] …` lines and the guest message.

## Gate 3 — Ring 3 (user GDT + TSS, SYSRET, swapgs)

**Files:** `puck-efi.c` (GDT/TSS/MSR setup), `puck-efi-x64.asm` (trampoline).

- Extend the GDT (`g_puckGdt`) with **user code (ring 3, 0x1B = idx 3|RPL3)** and
  **user data (0x23 = idx 4|RPL3)** descriptors, plus a **TSS descriptor** (16-byte
  system descriptor) with `rsp0` = kernel stack top for the ring-0 transition. `ltr`
  the TSS selector.
- Program `STAR` so SYSRET loads the right user selectors (SYSRET CS = STAR[63:48]+16
  in 64-bit mode; lay out the GDT so user code/data are at the SYSRET-implied indices:
  the conventional order is kernel code, kernel data, **user data (32-bit/compat
  slot), user code** — verify selector math against the Intel SDM SYSRET pseudocode).
- Mark the **guest's pages user-accessible** (set `US` on the guest image + user
  stack PTEs from Gate 2). Keep kernel pages supervisor-only.
- Set up **`swapgs`**: `IA32_KERNEL_GS_BASE` → a per-CPU kernel struct holding the
  kernel `rsp`. Trampoline entry: `swapgs`; load kernel rsp from gs; do the dispatch;
  restore; `swapgs`; `sysretq`. (For single-CPU you can also stash kernel rsp in a
  global, but wire swapgs now to match real kernels.)
- Change the guest launch in `Program.cs` (or a new asm helper) from a plain
  `jmp/call entry` to an **`iretq` or `sysretq` into ring 3**: push user SS, user
  RSP, RFLAGS (IF set), user CS, entry RIP and `iretq`. Allocate a user stack
  (user-accessible page from Gate 2/3).

**Verify:** add a syscall that reports CPL (or just confirm the guest runs and a
`write` works) — the guest now executes at ring 3 and faults into ring 0 only via
`syscall`. If a fault occurs you'll need an IDT (see Risks) to see it instead of a
triple-fault reboot loop on serial.

## Gate 4 — Broaden syscalls + SysV entry stack (run a real toolchain binary)

**File:** `puck-efi.c`, `PuckSyscallDispatch` + a new ELF loader path.

Add syscalls (return sane values; many can be stubbed):
- `brk` (12) — back with a bump region; `arch_prctl` (158) — `ARCH_SET_FS`/`SET_GS`
  set FS/GS base MSRs (real libc start needs FS for TLS).
- `set_tid_address` (218) → return a fake tid (1). `set_robust_list` (273) → 0.
- `mmap` (9)/`munmap` (11) — anonymous private mappings from a page bump; honor
  PROT bits in PTEs (Gate 2 helpers). `mprotect` (10) → 0 (or real).
- `writev` (20) — iterate iovec → serial. `exit_group` (231)/`exit` (60) — real
  process teardown (return to kernel `__managed__Main`, don't hang) so multiple
  guests can run.
- `readlinkat`/`getrandom`/`rt_sigprocmask`/`rt_sigaction` etc. — stub as needed
  when a real binary demands them; add incrementally driven by serial ENOSYS logs
  (have the default case **log the unknown syscall number** instead of silently
  returning ENOSYS so the operator can report what's missing).

Build a proper **SysV initial process stack** before entering the guest: from the
top of the user stack push (high→low) the auxv terminator, `AT_NULL`, key auxv
entries (`AT_PAGESZ`=4096, `AT_PHDR/AT_PHENT/AT_PHNUM/AT_ENTRY` from the ELF, optional
`AT_RANDOM` → 16 bytes), envp (NUL-terminated list + null), argv (+ null), then
`argc` at the lowest address (rsp points at argc on entry). rsp must be 16-byte
aligned per the SysV AMD64 ABI at `_start`.

Replace the hand-crafted ELF with a **real statically-linked Linux x86-64 binary**
(operator supplies one, e.g. `musl-gcc -static` hello world; embed its bytes or load
from the ESP via a pre-ExitBootServices file read — embedding is simplest). The loader
already parses PT_LOAD; make sure it honors p_flags for PTE permissions and zeroes
`.bss` (memsz > filesz).

**Verify:** a real `_start` (musl static) runs to `write`+`exit_group` over serial.
This is the milestone — a genuine Linux toolchain binary on Puck.

---

## Risks / likely side-quests

- **No IDT yet:** any guest fault (or kernel bug) triple-faults → QEMU reboots. The
  moment ring-3 or paging is wrong you'll see a boot loop, not a message. Strongly
  consider adding a **minimal IDT** with a panic handler that dumps the trap frame to
  serial *before* Gate 3 — it turns silent triple-faults into diagnosable output.
- **SYSRET selector math** is the classic footgun; verify GDT layout against the
  Intel SDM SYSRET pseudocode, not by guessing.
- **Page-table allocator must be 4 KiB aligned** and come from RAM you've mapped.
- Keep everything **single-threaded**; no SMP, no preemption.
- `g_puckSyscallStack` is 16 KiB — fine, but the user stack and any mmap arena
  need their own carve-outs from the conventional region; track the watermark.

## Suggested commit cadence

One gate per commit (or small sub-steps), each verified over serial first. Update
the `baremetal-uefi-os-state` memory after each gate lands so state stays canonical.

---

## Post-Gate-4 milestone log (all verified over serial)

Gates 1–4 landed the ring-3 Linux process host. Everything below builds on it toward
the real objective: the engine's `AzureBlobObjectBlobStoreBackend` running bare-metal.

- **Threads** — cooperative scheduler + `clone`/`futex`/`sched_yield`; per-thread
  `PuckCtx`; real pthreads run at ring 3.
- **Preemption** — PIT timer IRQ (~100 Hz) + 8259 PIC + `iretq` context switch;
  unified resume path shared with the syscall return.
- **File/device I/O** — synthetic VFS (`open`/`openat`/`read`/`readv`/`lseek`/`close`).
- **Clock + entropy** — CMOS RTC boot epoch + TSC calibration → `CLOCK_REALTIME`
  (UTC) and `CLOCK_MONOTONIC`; `getrandom`/entropy from `RDRAND` (needs `-cpu max`).
- **Networking (vendored, not invented)** — virtio-net PIO driver + **vendored lwIP
  2.2.1** (`NO_SYS=1`, official `cc.h`/`ethernetif.c` port). DHCP → DNS → TCP → HTTP
  GET over the real (slirp-NAT'd) internet.
- **TLS (vendored, not invented)** — **vendored mbedTLS 3.6** behind an
  engine-agnostic `PuckTls` interface (`compat/native/puck-tls.h`) so a future
  rustls swap is a one-file change. Freestanding port (`puck-tls-mbedtls.c`):
  `RDRAND` entropy (`mbedtls_hardware_poll`), `CLOCK_REALTIME` + epoch→UTC `gmtime`,
  uptime `ms_time`, a static `memory_buffer_alloc` heap, an `snprintf`/`printf` alt,
  and `byteswap`/`exit` shims (`puck-tls-shims.c`). Config selects a TLS-1.2 client
  only (`puck-mbedtls-config.h`, named uniquely so `build_info.h` can't shadow it with
  the vendored default). **Real CA roots** embedded from the system store
  (`puck-ca-bundle.c`, regen via `mbedtls-port/regen-ca-bundle.ps1`): ISRG Root X1,
  DigiCert Global Root G2, Baltimore CyberTrust. The TLS byte transport is a
  non-blocking lwIP TCP pcb (`puck-netif.c`).
  **Verified:** HTTPS GET of `valid-isrgrootx1.letsencrypt.org` — full handshake with
  X.509 chain + hostname verification (`MBEDTLS_SSL_VERIFY_REQUIRED`) against the
  embedded ISRG Root X1, `200 OK` returned. This is the TLS milestone.

- **Network boot (PXE)** — the EFI image can boot over the network instead of off the FAT
  ESP. `run-qemu.ps1 -Pxe`: QEMU slirp serves the same `BOOTX64.EFI` over its built-in TFTP;
  iPXE's UEFI option ROM (`efi-virtio.rom`) supplies the virtio-net UEFI driver the bundled
  OVMF lacks, and the firmware's PXE Base Code DHCPs + TFTPs the image. **Verified:**
  `>>Start PXE over IPv4 … NBP file downloaded successfully` → our `[boot]` banner → full
  stack (incl. the HTTPS test) still runs. The NIC must be transitional (drop
  `disable-modern=on`) for iPXE; our legacy PIO driver still binds it after forcing **MSI-X
  off** in `PuckVirtioNetInit` (a virtio reset doesn't clear the PCI MSI-X enable bit, so a
  firmware/iPXE-left MSI-X would shift the legacy register window and corrupt the MAC read).

### Next toward Azure blobs

- Point `PuckNetTlsTest` at an Azure Blob **SAS URL** (HTTPS GET of a blob) — the
  DigiCert/Baltimore roots are already embedded for it. SAS-first means no
  `DefaultAzureCredential`/IMDS probing.
- Expose BSD socket syscalls so the **engine itself** (not kernel-side test code) drives
  the stack, then run `AzureBlobObjectBlobStoreBackend` as the ring-3 guest.
- Eventually: swap mbedTLS → rustls by adding `puck-tls-rustls.c` behind the same
  `PuckTls` interface; nothing above the interface changes.
