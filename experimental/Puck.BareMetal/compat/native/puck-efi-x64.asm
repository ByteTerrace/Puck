; UEFI kernel assembly. Part of Puck.BareMetal.
;
; The low-level kernel pieces that cannot be expressed in C: loading our own GDT/TSS/IDT, the SYSCALL
; entry trampoline that turns a Linux-style `syscall` into a call to our C dispatcher, and the
; iretq/sysretq ring transitions.
;
; The guest runs in ring 3: we drop into it with `iretq` (PuckEnterUserMode) and the trampoline
; returns to it with `sysretq` (CS/SS come from STAR[63:48]; RIP from rcx, RFLAGS from r11). All
; syscalls therefore originate from ring 3.
;
; Assembled with ml64 (see build/Puck.BareMetal.Efi.targets).

EXTERN g_puckSyscallStackTop : QWORD
EXTERN g_puckIncomingCtx : QWORD
EXTERN PuckHandleSyscall : PROC
EXTERN PuckHandleTimer : PROC
EXTERN PuckTrapHandler : PROC
EXTERN PuckNetMsixHandler : PROC

; Byte offsets into PuckCtx (must match struct PuckCtx in puck-efi.c). The asm saves and
; restores the full GPR set + RIP/RFLAGS; FSBASE (offset 144) is managed entirely on the C side.
; rcx/r11 are real fields because a timer interrupt can preempt mid-instruction (unlike a syscall,
; where they are architecturally clobbered) - so every thread is resumed with `iretq`.
CTX_RAX    equ 0
CTX_RBX    equ 8
CTX_RCX    equ 16
CTX_RDX    equ 24
CTX_RSI    equ 32
CTX_RDI    equ 40
CTX_RBP    equ 48
CTX_RSP    equ 56
CTX_R8     equ 64
CTX_R9     equ 72
CTX_R10    equ 80
CTX_R11    equ 88
CTX_R12    equ 96
CTX_R13    equ 104
CTX_R14    equ 112
CTX_R15    equ 120
CTX_RIP    equ 128
CTX_RFLAGS equ 136

_TEXT SEGMENT

; void PuckLoadGdt(void* gdtr)   rcx = pointer to { uint16 limit; uint64 base }
; Loads the GDT and reloads all segment registers to our flat ring-0 selectors (CS=0x08,
; everything else 0x10). CS cannot be set with mov, so it is reloaded via a far return.
PUBLIC PuckLoadGdt
PuckLoadGdt PROC
        lgdt    fword ptr [rcx]
        mov     ax, 10h
        mov     ds, ax
        mov     es, ax
        mov     fs, ax
        mov     gs, ax
        mov     ss, ax
        lea     rax, reloadCS
        push    8
        push    rax
        retfq
reloadCS:
        ret
PuckLoadGdt ENDP

; void PuckLoadTr(unsigned long long selector)   rcx = TSS selector (0x28)
; Loads the task register so ring-3 -> ring-0 transitions (interrupts/exceptions) fetch rsp0 from
; our TSS. Must run after the GDT (which holds the TSS descriptor) is loaded.
PUBLIC PuckLoadTr
PuckLoadTr PROC
        ltr     cx
        ret
PuckLoadTr ENDP

; void PuckEnterUserMode(void* entry, void* userStackTop)   rcx = entry RIP, rdx = user RSP
; Transition from ring 0 into ring 3 by faking an interrupt return: push the ring-3 SS, RSP, RFLAGS,
; CS and entry RIP, clear the GPRs (so no kernel pointers leak into the guest), and iretq. The user
; selectors are 0x23 (code, 0x20|RPL3) and 0x1B (data, 0x18|RPL3) from our GDT.
PUBLIC PuckEnterUserMode
PuckEnterUserMode PROC
        mov     rax, 1Bh                ; user SS = 0x18 | RPL 3
        push    rax
        push    rdx                     ; user RSP
        push    202h                    ; RFLAGS: IF=1, reserved bit 1
        mov     rax, 23h                ; user CS = 0x20 | RPL 3
        push    rax
        push    rcx                     ; entry RIP
        xor     eax, eax
        xor     ebx, ebx
        xor     ecx, ecx
        xor     edx, edx
        xor     esi, esi
        xor     edi, edi
        xor     ebp, ebp
        xor     r8, r8
        xor     r9, r9
        xor     r10, r10
        xor     r11, r11
        xor     r12, r12
        xor     r13, r13
        xor     r14, r14
        xor     r15, r15
        iretq                           ; -> ring 3 at entry RIP on the user stack
PuckEnterUserMode ENDP

; SYSCALL entry point (set in LSTAR). On entry: rax = number; rdi,rsi,rdx,r10,r8,r9 = args;
; rcx = return RIP; r11 = saved RFLAGS; rsp = the guest's ring-3 stack (syscall does not switch it).
;
; Multithreaded model: snapshot the calling thread's full register file into g_puckIncomingCtx,
; switch to the kernel scratch stack, and call PuckHandleSyscall, which services the syscall AND
; performs any cooperative context switch - returning a pointer to the PuckThread whose context
; we must resume (ctx is its first field). We then load that context and sysretq into it, which may
; be a DIFFERENT thread than the caller. The FS base of the resumed thread is set by the C side.
PUBLIC PuckSyscallEntry
PuckSyscallEntry PROC
        ; Snapshot the caller's full register file (RIP-relative stores need no scratch register).
        ; rax = syscall number; rdi/rsi/rdx/r10/r8/r9 = args; rcx = return RIP; r11 = saved RFLAGS.
        mov     [g_puckIncomingCtx + CTX_RAX], rax
        mov     [g_puckIncomingCtx + CTX_RBX], rbx
        mov     [g_puckIncomingCtx + CTX_RCX], rcx      ; (= return RIP; not reused on resume)
        mov     [g_puckIncomingCtx + CTX_RDX], rdx
        mov     [g_puckIncomingCtx + CTX_RSI], rsi
        mov     [g_puckIncomingCtx + CTX_RDI], rdi
        mov     [g_puckIncomingCtx + CTX_RBP], rbp
        mov     [g_puckIncomingCtx + CTX_RSP], rsp      ; guest ring-3 stack
        mov     [g_puckIncomingCtx + CTX_R8],  r8
        mov     [g_puckIncomingCtx + CTX_R9],  r9
        mov     [g_puckIncomingCtx + CTX_R10], r10
        mov     [g_puckIncomingCtx + CTX_R11], r11      ; (= saved RFLAGS; not reused on resume)
        mov     [g_puckIncomingCtx + CTX_R12], r12
        mov     [g_puckIncomingCtx + CTX_R13], r13
        mov     [g_puckIncomingCtx + CTX_R14], r14
        mov     [g_puckIncomingCtx + CTX_R15], r15
        mov     [g_puckIncomingCtx + CTX_RIP], rcx      ; return RIP
        mov     [g_puckIncomingCtx + CTX_RFLAGS], r11   ; saved RFLAGS
        mov     rsp, g_puckSyscallStackTop
        sub     rsp, 20h                                   ; Win64 shadow space (rsp stays 16-aligned)
        call    PuckHandleSyscall                       ; -> rax = PuckThread* to resume
        jmp     PuckResumeThread
PuckSyscallEntry ENDP

; Shared resume tail. rax = PuckThread* (ctx is its first field); the C side already set the FS
; base. Build a ring-3 iretq frame from the ctx, restore every GPR, and iretq into the thread. iretq
; (not sysret) so we can resume a thread parked at EITHER a syscall boundary or a timer interrupt.
PuckResumeThread:
        mov     rcx, [rax + CTX_RSP]
        push    1Bh                              ; SS  = user data (0x18 | RPL3)
        push    rcx                              ; RSP
        mov     rcx, [rax + CTX_RFLAGS]
        or      rcx, 200h                        ; IF = 1 (the thread is preemptible)
        push    rcx                              ; RFLAGS
        push    23h                              ; CS  = user code (0x20 | RPL3)
        push    qword ptr [rax + CTX_RIP]        ; RIP
        mov     rbx, [rax + CTX_RBX]
        mov     rcx, [rax + CTX_RCX]
        mov     rdx, [rax + CTX_RDX]
        mov     rsi, [rax + CTX_RSI]
        mov     rdi, [rax + CTX_RDI]
        mov     rbp, [rax + CTX_RBP]
        mov     r8,  [rax + CTX_R8]
        mov     r9,  [rax + CTX_R9]
        mov     r10, [rax + CTX_R10]
        mov     r11, [rax + CTX_R11]
        mov     r12, [rax + CTX_R12]
        mov     r13, [rax + CTX_R13]
        mov     r14, [rax + CTX_R14]
        mov     r15, [rax + CTX_R15]
        mov     rax, [rax + CTX_RAX]             ; rax last (it was the ctx pointer)
        iretq

; Timer IRQ (vector 0x20). The PIT fires this while a thread runs at ring 3 (IF=1); the interrupt
; gate clears IF so it never nests with a syscall (syscalls run with IF=0). Snapshot the interrupted
; thread's full register file plus the CPU-pushed RIP/RFLAGS/RSP, then PuckHandleTimer
; round-robins and EOIs. The shared resume tail iretqs into whatever thread runs next.
PUBLIC PuckTimerIsr
PuckTimerIsr PROC
        mov     [g_puckIncomingCtx + CTX_RAX], rax
        mov     [g_puckIncomingCtx + CTX_RBX], rbx
        mov     [g_puckIncomingCtx + CTX_RCX], rcx
        mov     [g_puckIncomingCtx + CTX_RDX], rdx
        mov     [g_puckIncomingCtx + CTX_RSI], rsi
        mov     [g_puckIncomingCtx + CTX_RDI], rdi
        mov     [g_puckIncomingCtx + CTX_RBP], rbp
        mov     [g_puckIncomingCtx + CTX_R8],  r8
        mov     [g_puckIncomingCtx + CTX_R9],  r9
        mov     [g_puckIncomingCtx + CTX_R10], r10
        mov     [g_puckIncomingCtx + CTX_R11], r11
        mov     [g_puckIncomingCtx + CTX_R12], r12
        mov     [g_puckIncomingCtx + CTX_R13], r13
        mov     [g_puckIncomingCtx + CTX_R14], r14
        mov     [g_puckIncomingCtx + CTX_R15], r15
        ; Interrupt frame at [rsp]: RIP(+0), CS(+8), RFLAGS(+16), RSP(+24), SS(+32) on the rsp0 stack.
        mov     rax, [rsp + 0]
        mov     [g_puckIncomingCtx + CTX_RIP], rax
        mov     rax, [rsp + 16]
        mov     [g_puckIncomingCtx + CTX_RFLAGS], rax
        mov     rax, [rsp + 24]
        mov     [g_puckIncomingCtx + CTX_RSP], rax
        mov     rsp, g_puckSyscallStackTop
        sub     rsp, 20h
        call    PuckHandleTimer               ; -> rax = PuckThread* to resume
        jmp     PuckResumeThread
PuckTimerIsr ENDP

; Spurious-interrupt vector handler: the LAPIC does not expect an EOI for a spurious vector, so just
; return. Installed at vector 0xFF (by PuckInitLapic) so a stray spurious interrupt is harmless.
PUBLIC PuckSpuriousIsr
PuckSpuriousIsr PROC
        iretq
PuckSpuriousIsr ENDP

; Generic kernel-context IRQ stub (does NOT switch threads, unlike the timer): save the Win64
; volatile registers the C handler may clobber, align the stack, call the handler (which services the
; device and EOIs the LAPIC), restore, and iretq. This is the pattern every MSI/MSI-X vector reuses;
; here for the virtio-net MSI-X proof (vector 0x42).
PUBLIC PuckNetMsixIsr
PuckNetMsixIsr PROC
        push    rax
        push    rcx
        push    rdx
        push    r8
        push    r9
        push    r10
        push    r11
        push    rbp
        mov     rbp, rsp
        and     rsp, 0FFFFFFFFFFFFFFF0h
        sub     rsp, 32
        call    PuckNetMsixHandler
        mov     rsp, rbp
        pop     rbp
        pop     r11
        pop     r10
        pop     r9
        pop     r8
        pop     rdx
        pop     rcx
        pop     rax
        iretq
PuckNetMsixIsr ENDP

; ---------------------------------------------------------------------------------------------------
; Control-register helpers. CR2 holds the faulting linear address after a page fault (read in the
; panic dump); CR3 is the page-table root (read for the dump, written to install our map).
PUBLIC PuckReadCr2
PuckReadCr2 PROC
        mov     rax, cr2
        ret
PuckReadCr2 ENDP

PUBLIC PuckReadCr3
PuckReadCr3 PROC
        mov     rax, cr3
        ret
PuckReadCr3 ENDP

PUBLIC PuckWriteCr3
PuckWriteCr3 PROC                       ; rcx = PML4 physical address
        mov     cr3, rcx
        ret
PuckWriteCr3 ENDP

; Cache-control helpers for retyping MMIO regions (UC/WC via PAT). Single-CPU at boot, so a global
; wbinvd is cheap and correct; clflush/mfence are kept for the eventual framebuffer/ring writers.
PUBLIC PuckWbinvd
PuckWbinvd PROC
        wbinvd
        ret
PuckWbinvd ENDP

PUBLIC PuckClflush
PuckClflush PROC                        ; rcx = linear address
        clflush byte ptr [rcx]
        ret
PuckClflush ENDP

PUBLIC PuckMfence
PuckMfence PROC
        mfence
        ret
PuckMfence ENDP

; unsigned long long PuckRdRand64(void)
; Hardware entropy from the CPU's RDRAND instruction (QEMU/modern x86 expose it). Retries a few
; times per the Intel guidance; only if the silicon RNG is starved do we fall back to a TSC mix.
PUBLIC PuckRdRand64
PuckRdRand64 PROC
        mov     ecx, 10
rdrand_retry:
        rdrand  rax
        jc      rdrand_ok                  ; CF=1 => a valid random value was loaded
        dec     ecx
        jnz     rdrand_retry
        rdtsc                              ; fallback (RNG starved): edx:eax = TSC
        shl     rdx, 32
        or      rax, rdx
rdrand_ok:
        ret
PuckRdRand64 ENDP

; Deliberately raise #UD (invalid opcode). Used once during bring-up to prove the IDT/panic path
; actually fires; not called in normal boot.
PUBLIC PuckTriggerUd
PuckTriggerUd PROC
        ud2
        ret
PuckTriggerUd ENDP

; ---------------------------------------------------------------------------------------------------
; IDT plumbing: an lidt loader plus 32 exception ISR stubs that funnel into a common trap handler
; which dumps the CPU state to serial. Without this, any fault (a guest bug, a bad page table, a
; wrong ring transition) triple-faults and QEMU silently reboots; with it we get a diagnosable line.

; void PuckLoadIdt(void* idtr)   rcx = pointer to { uint16 limit; uint64 base }
PUBLIC PuckLoadIdt
PuckLoadIdt PROC
        lidt    fword ptr [rcx]
        ret
PuckLoadIdt ENDP

; Common tail: finish the trap frame by pushing the GPRs on top of what the CPU and the stub already
; pushed, hand its address to the C handler, and halt (the handler does not return). The resulting
; frame, from low address up, is: r15..r8, rbp, rdi, rsi, rdx, rcx, rbx, rax, vector, errorCode,
; rip, cs, rflags, rsp, ss - matching struct PuckTrapFrame.
isr_common:
        push    rax
        push    rbx
        push    rcx
        push    rdx
        push    rsi
        push    rdi
        push    rbp
        push    r8
        push    r9
        push    r10
        push    r11
        push    r12
        push    r13
        push    r14
        push    r15
        mov     rcx, rsp                ; arg1 = &PuckTrapFrame (lowest field = r15)
        sub     rsp, 32                 ; Win64 shadow space (rsp stays 16-byte aligned here)
        call    PuckTrapHandler      ; does not return
isr_halt:
        hlt
        jmp     isr_halt

; Stub generators. NOERR pushes a dummy error code so every vector reaches isr_common with the same
; frame shape; ERR relies on the error code the CPU already pushed for that vector.
ISR_NOERR macro n
isr_stub_&n&:
        push    0
        push    n
        jmp     isr_common
endm
ISR_ERR macro n
isr_stub_&n&:
        push    n
        jmp     isr_common
endm

        ISR_NOERR 0           ; #DE divide error
        ISR_NOERR 1           ; #DB debug
        ISR_NOERR 2           ; NMI
        ISR_NOERR 3           ; #BP breakpoint
        ISR_NOERR 4           ; #OF overflow
        ISR_NOERR 5           ; #BR bound range
        ISR_NOERR 6           ; #UD invalid opcode
        ISR_NOERR 7           ; #NM device not available
        ISR_ERR   8           ; #DF double fault (error code = 0)
        ISR_NOERR 9           ; coprocessor segment overrun (legacy)
        ISR_ERR   10          ; #TS invalid TSS
        ISR_ERR   11          ; #NP segment not present
        ISR_ERR   12          ; #SS stack fault
        ISR_ERR   13          ; #GP general protection
        ISR_ERR   14          ; #PF page fault
        ISR_NOERR 15          ; reserved
        ISR_NOERR 16          ; #MF x87 FP
        ISR_ERR   17          ; #AC alignment check
        ISR_NOERR 18          ; #MC machine check
        ISR_NOERR 19          ; #XM SIMD FP
        ISR_NOERR 20          ; #VE virtualization
        ISR_ERR   21          ; #CP control protection
        ISR_NOERR 22
        ISR_NOERR 23
        ISR_NOERR 24
        ISR_NOERR 25
        ISR_NOERR 26
        ISR_NOERR 27
        ISR_NOERR 28
        ISR_NOERR 29
        ISR_NOERR 30
        ISR_NOERR 31

_TEXT ENDS

_DATA SEGMENT
; The 32 stub entry points, consumed by PuckInitIdt (C) to fill the IDT gate descriptors.
PUBLIC g_puckIsrStubs
g_puckIsrStubs LABEL QWORD
        DQ      isr_stub_0,  isr_stub_1,  isr_stub_2,  isr_stub_3
        DQ      isr_stub_4,  isr_stub_5,  isr_stub_6,  isr_stub_7
        DQ      isr_stub_8,  isr_stub_9,  isr_stub_10, isr_stub_11
        DQ      isr_stub_12, isr_stub_13, isr_stub_14, isr_stub_15
        DQ      isr_stub_16, isr_stub_17, isr_stub_18, isr_stub_19
        DQ      isr_stub_20, isr_stub_21, isr_stub_22, isr_stub_23
        DQ      isr_stub_24, isr_stub_25, isr_stub_26, isr_stub_27
        DQ      isr_stub_28, isr_stub_29, isr_stub_30, isr_stub_31
_DATA ENDS
END
