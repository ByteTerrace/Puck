namespace Puck.AdvancedGamingBrick.Post;

// Generates tiny hand-assembled ARM .gba ROMs: the timer/IRQ probes that isolate IRQ-recognition timing for
// differential lockstep against the cosim oracle (`--lockstep <rom> N direct`), plus the link-parent/link-child
// SIO multiplayer exchange pair the Tier-C link-replay stage boots on a shared cable. Unlike a retail game,
// a tight loop has near-zero cumulative bus-cost drift, so any Puck-vs-oracle functional divergence is purely
// the IRQ pipeline depth / timer latch. Code begins at ROM offset 0 (= 0x08000000), which both cores jump to
// under direct boot; the timer/IRQ probes rely on the real BIOS to dispatch the IRQ vector to the user handler
// installed at 0x03007FFC (the link pair instead polls IF with IME off, so it needs no BIOS at all).
internal static class MicroRoms {
    /// <summary>The child's seeded first send word (its round-0 reply, latched before any exchange).</summary>
    public const ushort LinkChildSeedWord = 0xA000;
    /// <summary>The child's transform: each round it re-arms <c>SIOMLT_SEND</c> with the parent word it just
    /// received XOR this mask — so every reply after the first PROVES data crossed the cable.</summary>
    public const ushort LinkChildTransformMask = 0xFF00;
    /// <summary>The completion-marker value both link ROMs store once all rounds are done.</summary>
    public const uint LinkCompletionMarker = 0x600DF00Du;
    /// <summary>IWRAM address of the final SIOCNT read-back (id bits, cleared start) both link ROMs record.</summary>
    public const uint LinkControlAddress = 0x03000008u;
    /// <summary>IWRAM address of the observed serial-IRQ (IF bit 7) count, written by both link ROMs at the end.</summary>
    public const uint LinkIrqCountAddress = 0x03000000u;
    /// <summary>IWRAM address of the completion marker (<see cref="LinkCompletionMarker"/>).</summary>
    public const uint LinkMarkerAddress = 0x03000004u;
    /// <summary>The base of the parent's per-round send words (round k sends <c>LinkParentSendBase + k</c>).</summary>
    public const ushort LinkParentSendBase = 0x1000;
    /// <summary>IWRAM base of the per-round records: 8 bytes per round — the SIOMULTI0/1 word then the SIOMULTI2/3
    /// word, exactly as read back after each round's IRQ.</summary>
    public const uint LinkRecordAddress = 0x03000010u;
    /// <summary>The number of multiplayer rounds the link-parent/link-child protocol runs.</summary>
    public const int LinkRounds = 4;

    /// <summary>The micro-ROM kinds this generator knows, for the diagnostics that sweep them in memory. The
    /// <c>link-parent</c>/<c>link-child</c> pair is deliberately NOT here: those two are one two-console protocol
    /// (the Tier-C link-replay stage boots them on a shared cable), not a standalone timing probe, so the
    /// single-machine sweeps skip them.</summary>
    public static readonly string[] Kinds = ["timer-irq", "timer-irq-iwram", "cascade-irq", "ime-delay"];

    // Timer0 (prescaler ÷1) cascades into Timer1 (count-up); Timer1 raises the IRQ. Probes the synchronous
    // in-cycle cascade + the cascade-timer overflow→IRQ path.
    private static byte[] CascadeIrq() {
        var a = new ArmProbeAssembler();

        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );
        a.LdrLabel(
            label: "handler",
            rd: 1
        );
        a.LdrConst(
            rd: 2,
            value: 0x03007FFCu
        );
        a.Str(
            imm12: 0,
            rd: 1,
            rn: 2
        );
        a.Mov(
            imm8: 1,
            rd: 1
        );
        a.Str(
            imm12: 0x208,
            rd: 1,
            rn: 0
        );              // IME = 1
        a.Mov(
            imm8: 0x10,
            rd: 1
        );
        a.Str(
            imm12: 0x200,
            rd: 1,
            rn: 0
        );              // IE = timer1 (bit 4)
        // Timer1: reload 0xFFFE (cascade overflows after 2 cascade ticks), control 0xC4 = enable+irq+cascade.
        a.LdrConst(
            rd: 1,
            value: (0x00C4u << 16) | 0xFFFEu
        );
        a.Str(
            imm12: 0x104,
            rd: 1,
            rn: 0
        );              // TM1CNT
        // Timer0: reload 0xFFF0 (overflows every 16 cycles), control 0x80 = enable, prescale ÷1.
        a.LdrConst(
            rd: 1,
            value: (0x0080u << 16) | 0xFFF0u
        );
        a.Str(
            imm12: 0x100,
            rd: 1,
            rn: 0
        );              // TM0CNT
        a.Mov(
            imm8: 0,
            rd: 4
        );
        a.Label(name: "loop");
        a.Add(
            imm8: 1,
            rd: 4,
            rn: 4
        );
        a.BBack(instructions: 1);

        a.Label(name: "handler");
        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );
        a.LdrConst(
            rd: 1,
            value: 0x00100010u
        );          // IE = timer1; IF clear bit 4
        a.Str(
            imm12: 0x200,
            rd: 1,
            rn: 0
        );
        a.Bx(rn: 14);

        return a.Finish();
    }
    // Shared tail of the two link ROMs: record the final SIOCNT (id bits + cleared start), the IF-observation count,
    // and the completion marker, then hang.
    private static void EmitLinkEpilogue(ArmProbeAssembler a) {
        a.Ldrh(
            imm8: 0x28,
            rd: 1,
            rn: 10
        );               // final SIOCNT
        a.LdrConst(
            rd: 2,
            value: LinkControlAddress
        );
        a.Str(
            imm12: 0,
            rd: 1,
            rn: 2
        );
        a.LdrConst(
            rd: 2,
            value: LinkIrqCountAddress
        );
        a.Str(
            imm12: 0,
            rd: 4,
            rn: 2
        );
        a.LdrConst(
            rd: 1,
            value: LinkCompletionMarker
        );
        a.LdrConst(
            rd: 2,
            value: LinkMarkerAddress
        );
        a.Str(
            imm12: 0,
            rd: 1,
            rn: 2
        );
        a.Label(name: "hang");
        a.B(label: "hang");
    }
    // IF is pre-pended with a timer0 request while IME is left 0, then IME is enabled mid-stream: the IRQ must be
    // recognised one instruction after the IME store (the ime[1]→ime[0] pipeline), not on the store itself.
    private static byte[] ImeDelay() {
        var a = new ArmProbeAssembler();

        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );
        a.LdrLabel(
            label: "handler",
            rd: 1
        );
        a.LdrConst(
            rd: 2,
            value: 0x03007FFCu
        );
        a.Str(
            imm12: 0,
            rd: 1,
            rn: 2
        );
        a.Mov(
            imm8: 8,
            rd: 1
        );
        a.Str(
            imm12: 0x200,
            rd: 1,
            rn: 0
        );              // IE = timer0
        // Timer0 reload 0xFFFF (overflows next cycle), enable+irq prescale ÷1 — arms a pending IF quickly.
        a.LdrConst(
            rd: 1,
            value: (0x00C0u << 16) | 0xFFFFu
        );
        a.Str(
            imm12: 0x100,
            rd: 1,
            rn: 0
        );
        a.Mov(
            imm8: 0,
            rd: 4
        );                           // marker before IME
        a.Mov(
            imm8: 0,
            rd: 5
        );
        a.Mov(
            imm8: 1,
            rd: 1
        );
        a.Str(
            imm12: 0x208,
            rd: 1,
            rn: 0
        );              // IME = 1  (IRQ must NOT fire on this instruction)
        a.Mov(
            imm8: 1,
            rd: 4
        );                           // r4 = 1: executed iff IRQ deferred past the IME store
        a.Label(name: "loop");
        a.Add(
            imm8: 1,
            rd: 5,
            rn: 5
        );
        a.BBack(instructions: 1);

        a.Label(name: "handler");
        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );
        a.LdrConst(
            rd: 1,
            value: 0x00080008u
        );
        a.Str(
            imm12: 0x200,
            rd: 1,
            rn: 0
        );
        a.Bx(rn: 14);

        return a.Finish();
    }
    private static byte[] LinkChild() {
        var a = new ArmProbeAssembler();

        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );           // r0  = I/O base (word reads: SIOMULTI0..3)
        a.LdrConst(
            rd: 10,
            value: 0x04000100u
        );          // r10 = halfword base for SIOCNT/SIOMLT_SEND/RCNT
        a.LdrConst(
            rd: 11,
            value: 0x04000200u
        );          // r11 = halfword base for IF
        a.Mov(
            imm8: 0,
            rd: 1
        );
        a.Strh(
            imm8: 0x34,
            rd: 1,
            rn: 10
        );               // RCNT = 0: pins owned by SIO
        a.LdrConst(
            rd: 7,
            value: LinkRecordAddress
        );
        a.LdrConst(
            rd: 8,
            value: LinkChildTransformMask
        );
        a.LdrConst(
            rd: 1,
            value: LinkChildSeedWord
        );
        a.Strh(
            imm8: 0x2A,
            rd: 1,
            rn: 10
        );               // seed SIOMLT_SEND — the round-0 reply
        a.LdrConst(
            rd: 1,
            value: 0x00006003u
        );           // multiplayer | 115200 bps | IRQ-enable (no start: parent clocks)
        a.Strh(
            imm8: 0x28,
            rd: 1,
            rn: 10
        );
        a.Mov(
            imm8: 0,
            rd: 4
        );                           // r4 = observed serial-IRQ (IF) count
        a.Mov(
            imm8: 0,
            rd: 6
        );                           // r6 = round index

        a.Label(name: "round");
        a.Ldrh(
            imm8: 0x02,
            rd: 1,
            rn: 11
        );               // IF
        a.Tst(
            imm8: 0x80,
            rn: 1
        );                        // the parent's round landed?
        a.B(
            cond: ArmProbeAssembler.CondEq,
            label: "round"
        );
        a.Mov(
            imm8: 0x80,
            rd: 1
        );
        a.Strh(
            imm8: 0x02,
            rd: 1,
            rn: 11
        );               // acknowledge (write-one-to-clear)
        a.Add(
            imm8: 1,
            rd: 4,
            rn: 4
        );

        a.LslImm(
            rd: 2,
            rm: 6,
            shift: 3
        );
        a.AddReg(
            rd: 2,
            rm: 2,
            rn: 7
        );
        a.Ldr(
            imm12: 0x120,
            rd: 1,
            rn: 0
        );               // SIOMULTI0/1
        a.Str(
            imm12: 0,
            rd: 1,
            rn: 2
        );
        a.Ldr(
            imm12: 0x124,
            rd: 3,
            rn: 0
        );               // SIOMULTI2/3
        a.Str(
            imm12: 4,
            rd: 3,
            rn: 2
        );

        a.LslImm(
            rd: 3,
            rm: 1,
            shift: 16
        );               // isolate SIOMULTI0 — the parent's word this round
        a.LsrImm(
            rd: 3,
            rm: 3,
            shift: 16
        );
        a.EorReg(
            rd: 3,
            rm: 8,
            rn: 3
        );                   // the transform
        a.Strh(
            imm8: 0x2A,
            rd: 3,
            rn: 10
        );               // re-arm SIOMLT_SEND for the next round

        a.Add(
            imm8: 1,
            rd: 6,
            rn: 6
        );
        a.Cmp(
            imm8: LinkRounds,
            rn: 6
        );
        a.B(
            cond: ArmProbeAssembler.CondNe,
            label: "round"
        );

        EmitLinkEpilogue(a: a);

        return a.Finish();
    }
    // The multiplayer link-exchange pair: the parent drives LinkRounds SIO multiplayer rounds, the child answers.
    // No BIOS is involved — IME stays 0 and both sides observe completion by polling IF bit 7 (the serial request,
    // which SIOCNT bit 14 latches regardless of IE/IME) and acknowledging it write-one-to-clear, so the pair runs on
    // the zeroed stub BIOS. Both sides record every round's SIOMULTI0..3 read-back, their IF-observation count, and
    // their final SIOCNT (whose id bits prove daisy-chain position) to the Link* IWRAM addresses above.
    //
    // Round k: the parent sends LinkParentSendBase+k; the child's reply is LinkChildSeedWord for k=0 and
    // (parent word k-1) XOR LinkChildTransformMask after that — each reply after the first is derived from data that
    // crossed the cable the round before, so the recorded slots prove a real two-way exchange, not idle lines.
    private static byte[] LinkParent() {
        var a = new ArmProbeAssembler();

        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );           // r0  = I/O base (word reads: SIOMULTI0..3)
        a.LdrConst(
            rd: 10,
            value: 0x04000100u
        );          // r10 = halfword base for SIOCNT/SIOMLT_SEND/RCNT
        a.LdrConst(
            rd: 11,
            value: 0x04000200u
        );          // r11 = halfword base for IF
        a.Mov(
            imm8: 0,
            rd: 1
        );
        a.Strh(
            imm8: 0x34,
            rd: 1,
            rn: 10
        );               // RCNT = 0: pins owned by SIO
        a.LdrConst(
            rd: 7,
            value: LinkRecordAddress
        );
        a.LdrConst(
            rd: 8,
            value: LinkParentSendBase
        );
        a.Mov(
            imm8: 0,
            rd: 4
        );                           // r4 = observed serial-IRQ (IF) count
        a.Mov(
            imm8: 0,
            rd: 6
        );                           // r6 = round index

        a.Label(name: "round");
        a.AddReg(
            rd: 1,
            rm: 6,
            rn: 8
        );
        a.Strh(
            imm8: 0x2A,
            rd: 1,
            rn: 10
        );               // SIOMLT_SEND = base + round
        a.LdrConst(
            rd: 1,
            value: 0x00006083u
        );           // multiplayer | 115200 bps | IRQ-enable | start
        a.Strh(
            imm8: 0x28,
            rd: 1,
            rn: 10
        );               // SIOCNT — the parent clocks the round

        a.Label(name: "wait");
        a.Ldrh(
            imm8: 0x02,
            rd: 1,
            rn: 11
        );               // IF
        a.Tst(
            imm8: 0x80,
            rn: 1
        );                        // serial request?
        a.B(
            cond: ArmProbeAssembler.CondEq,
            label: "wait"
        );
        a.Mov(
            imm8: 0x80,
            rd: 1
        );
        a.Strh(
            imm8: 0x02,
            rd: 1,
            rn: 11
        );               // acknowledge (write-one-to-clear)
        a.Add(
            imm8: 1,
            rd: 4,
            rn: 4
        );

        a.LslImm(
            rd: 2,
            rm: 6,
            shift: 3
        );
        a.AddReg(
            rd: 2,
            rm: 2,
            rn: 7
        );
        a.Ldr(
            imm12: 0x120,
            rd: 1,
            rn: 0
        );               // SIOMULTI0/1
        a.Str(
            imm12: 0,
            rd: 1,
            rn: 2
        );
        a.Ldr(
            imm12: 0x124,
            rd: 1,
            rn: 0
        );               // SIOMULTI2/3
        a.Str(
            imm12: 4,
            rd: 1,
            rn: 2
        );

        a.Add(
            imm8: 1,
            rd: 6,
            rn: 6
        );
        a.Cmp(
            imm8: LinkRounds,
            rn: 6
        );
        a.B(
            cond: ArmProbeAssembler.CondNe,
            label: "round"
        );

        EmitLinkEpilogue(a: a);

        return a.Finish();
    }
    // Timer0 overflows and raises an IRQ; the handler acks it and returns. The main loop counts in r4, so the
    // exact instruction at which the IRQ is recognised is observable. (reload, control) parameterise the cadence.
    private static byte[] TimerIrq(uint reload, uint control) {
        var a = new ArmProbeAssembler();

        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );          // r0 = I/O base
        a.LdrLabel(
            label: "handler",
            rd: 1
        );
        a.LdrConst(
            rd: 2,
            value: 0x03007FFCu
        );          // BIOS user-IRQ-handler pointer
        a.Str(
            imm12: 0,
            rd: 1,
            rn: 2
        );
        a.Mov(
            imm8: 1,
            rd: 1
        );
        a.Str(
            imm12: 0x208,
            rd: 1,
            rn: 0
        );              // IME = 1
        a.Mov(
            imm8: 8,
            rd: 1
        );
        a.Str(
            imm12: 0x200,
            rd: 1,
            rn: 0
        );              // IE = timer0 (bit 3); IF high half = 0
        a.LdrConst(
            rd: 1,
            value: (control << 16) | (reload & 0xFFFFu)
        );
        a.Str(
            imm12: 0x100,
            rd: 1,
            rn: 0
        );              // TM0CNT_L reload + TM0CNT_H control, one word
        a.Mov(
            imm8: 0,
            rd: 4
        );
        a.Label(name: "loop");
        a.Add(
            imm8: 1,
            rd: 4,
            rn: 4
        );
        a.BBack(instructions: 1);                        // b loop

        a.Label(name: "handler");
        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );
        a.LdrConst(
            rd: 1,
            value: 0x00080008u
        );          // IE = timer0; IF write-one-to-clear bit 3
        a.Str(
            imm12: 0x200,
            rd: 1,
            rn: 0
        );
        a.Bx(rn: 14);                                    // return to the BIOS dispatcher

        return a.Finish();
    }
    // As TimerIrq, but the counting loop runs from IWRAM (0-wait, no prefetch), so the per-instruction cycle
    // accounting is deterministic and identical between cores — any remaining divergence is purely IRQ-recognition
    // timing, with the slow-ROM bus-cost attribution noise removed. The loop body is two opcodes written straight
    // into IWRAM (position-independent: `add r4,r4,#1` then `b .-4`), then we jump there with `ldr pc, =0x03000000`.
    private static byte[] TimerIrqIwram(uint reload, uint control) {
        var a = new ArmProbeAssembler();

        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );          // r0 = I/O base
        a.LdrConst(
            rd: 2,
            value: 0x03000000u
        );          // r2 = IWRAM loop address
        a.LdrConst(
            rd: 1,
            value: 0xE2844001u
        );          // add r4,r4,#1
        a.Str(
            imm12: 0,
            rd: 1,
            rn: 2
        );
        a.LdrConst(
            rd: 1,
            value: 0xEAFFFFFDu
        );          // b .-4  (back to 0x03000000)
        a.Str(
            imm12: 4,
            rd: 1,
            rn: 2
        );
        a.LdrLabel(
            label: "handler",
            rd: 1
        );
        a.LdrConst(
            rd: 3,
            value: 0x03007FFCu
        );
        a.Str(
            imm12: 0,
            rd: 1,
            rn: 3
        );
        a.Mov(
            imm8: 1,
            rd: 1
        );
        a.Str(
            imm12: 0x208,
            rd: 1,
            rn: 0
        );              // IME = 1
        a.Mov(
            imm8: 8,
            rd: 1
        );
        a.Str(
            imm12: 0x200,
            rd: 1,
            rn: 0
        );              // IE = timer0
        a.LdrConst(
            rd: 1,
            value: (control << 16) | (reload & 0xFFFFu)
        );
        a.Str(
            imm12: 0x100,
            rd: 1,
            rn: 0
        );              // TM0CNT
        a.Mov(
            imm8: 0,
            rd: 4
        );
        a.LdrConst(
            rd: 15,
            value: 0x03000000u
        );         // ldr pc, =0x03000000 — jump to the IWRAM loop

        a.Label(name: "handler");
        a.LdrConst(
            rd: 0,
            value: 0x04000000u
        );
        a.LdrConst(
            rd: 1,
            value: 0x00080008u
        );
        a.Str(
            imm12: 0x200,
            rd: 1,
            rn: 0
        );
        a.Bx(rn: 14);

        return a.Finish();
    }

    public static void Generate(string kind, string outPath) {
        var rom = GenerateBytes(kind: kind);

        File.WriteAllBytes(
            bytes: rom,
            path: outPath
        );
        Console.WriteLine(value: $"== wrote '{kind}' micro-rom ({rom.Length} bytes) to {outPath} ==");
    }
    // Builds a micro-ROM image in memory (no disk), so the savestate round-trip diagnostic can boot every kind
    // without a temp file.
    public static byte[] GenerateBytes(string kind) => kind switch {
        "timer-irq" => TimerIrq(
        control: 0x00C0,
        reload: 0xFF00
    ),
        "timer-irq-iwram" => TimerIrqIwram(
        control: 0x00C0,
        reload: 0xFF00
    ),
        "cascade-irq" => CascadeIrq(),
        "ime-delay" => ImeDelay(),
        "link-parent" => LinkParent(),
        "link-child" => LinkChild(),
        _ => throw new ArgumentException(message: $"unknown micro-rom kind '{kind}' (timer-irq | timer-irq-iwram | cascade-irq | ime-delay | link-parent | link-child)"),
    };

}
