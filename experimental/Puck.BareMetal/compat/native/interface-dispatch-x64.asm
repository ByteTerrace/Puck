; Polymorphic interface-dispatch entry stub. Part of Puck.BareMetal.
;
; RhpInitialDynamicInterfaceDispatch - the entry stub the stock .NET 10 ILC points every
; polymorphic interface-dispatch cell at (see compat/native/puck-rt.c for the C resolver
; and the long-form explanation of how NativeAOT interface dispatch works here).
;
; Why this MUST be hand-written assembly: the stub is entered in the middle of a normal
; managed call (`mov rcx,<this>; lea r11,[cell]; call qword ptr [r11]`), with the interface
; method's own arguments still live in the volatile argument registers and the original return
; address on the stack. It has to run the C resolver (which clobbers those registers) and then
; transfer to the resolved target with EVERY original argument intact, so the target returns
; straight to the original caller. C cannot express "preserve N unknown argument registers
; across a call, then tail-jump to a computed address" - hence MASM.
;
; ABI on entry (Windows x64, matching what the ILC emits - verified from the generated .obj):
;   rcx = `this` (the object reference; its MethodTable* is at offset 0)
;   r11 = address of the InterfaceDispatchCell for this call site
;   [rsp] = return address back into the caller
;
; We are NOT [SuppressGCTransition]-style restricted here, but there is no GC and no stack
; unwinding, so the stub needs no reverse-P/Invoke frame and no .pdata/.xdata unwind info.

EXTERN PuckResolveInterfaceDispatch : PROC

_TEXT SEGMENT

; void* PuckResolveInterfaceDispatch(void* pThis /*rcx*/, void* pCell /*rdx*/) -> rax = target
PUBLIC RhpInitialDynamicInterfaceDispatch
RhpInitialDynamicInterfaceDispatch PROC
        ; On entry rsp is 8 (mod 16) because the `call` pushed an 8-byte return address onto a
        ; 16-aligned stack. Reserve 0x88 so rsp becomes 16-aligned for our own call, with room
        ; for 32 bytes of callee shadow space, the four integer + four xmm argument registers,
        ; and 8 bytes of tail padding.
        sub     rsp, 088h

        mov     [rsp+20h], rcx          ; preserve the interface method's integer arguments
        mov     [rsp+28h], rdx          ; (each Win64 arg slot 0..3 uses the int OR the xmm reg,
        mov     [rsp+30h], r8           ;  and we don't know which the target consumes, so we
        mov     [rsp+38h], r9           ;  save both banks)
        movaps  [rsp+40h], xmm0
        movaps  [rsp+50h], xmm1
        movaps  [rsp+60h], xmm2
        movaps  [rsp+70h], xmm3

        ; rcx already holds `this` (arg1). Pass the cell pointer as arg2.
        mov     rdx, r11
        call    PuckResolveInterfaceDispatch
        mov     r11, rax                ; r11 = resolved target code address

        mov     rcx, [rsp+20h]          ; restore the original arguments
        mov     rdx, [rsp+28h]
        mov     r8,  [rsp+30h]
        mov     r9,  [rsp+38h]
        movaps  xmm0, [rsp+40h]
        movaps  xmm1, [rsp+50h]
        movaps  xmm2, [rsp+60h]
        movaps  xmm3, [rsp+70h]

        add     rsp, 088h               ; restore rsp so [rsp] is again the original return addr
        jmp     r11                      ; tail-transfer: the target returns straight to the caller
RhpInitialDynamicInterfaceDispatch ENDP

_TEXT ENDS

END
