namespace Puck.AdvancedGamingBrick.Post;

// Generates tiny hand-assembled ARM .gba ROMs: the timer/IRQ probes that isolate IRQ-recognition timing for
// differential lockstep against the cosim oracle (`--lockstep <rom> N direct`), plus the link-parent/link-child
// SIO multiplayer exchange pair the Tier-C link-replay stage boots on a shared cable. Unlike a commercial game,
// a tight loop has near-zero cumulative bus-cost drift, so any Puck-vs-oracle functional divergence is purely the
// IRQ pipeline depth / timer latch — exactly the residual this session is tuning. Code begins at ROM offset 0
// (= 0x08000000), which both cores jump to under direct boot; the timer/IRQ probes rely on the real BIOS to
// dispatch the IRQ vector to the user handler installed at 0x03007FFC (the link pair instead polls IF with IME
// off, so it needs no BIOS at all).
internal static class MicroRoms {
    /// <summary>The micro-ROM kinds this generator knows, for the diagnostics that sweep them in memory. The
    /// <c>link-parent</c>/<c>link-child</c> pair is deliberately NOT here: those two are one two-console protocol
    /// (the Tier-C link-replay stage boots them on a shared cable), not a standalone timing probe, so the
    /// single-machine sweeps skip them.</summary>
    public static readonly string[] Kinds = ["timer-irq", "timer-irq-iwram", "cascade-irq", "ime-delay"];

    /// <summary>The number of multiplayer rounds the link-parent/link-child protocol runs.</summary>
    public const int LinkRounds = 4;

    /// <summary>The base of the parent's per-round send words (round k sends <c>LinkParentSendBase + k</c>).</summary>
    public const ushort LinkParentSendBase = 0x1000;

    /// <summary>The child's seeded first send word (its round-0 reply, latched before any exchange).</summary>
    public const ushort LinkChildSeedWord = 0xA000;

    /// <summary>The child's transform: each round it re-arms <c>SIOMLT_SEND</c> with the parent word it just
    /// received XOR this mask — so every reply after the first PROVES data crossed the cable.</summary>
    public const ushort LinkChildTransformMask = 0xFF00;

    /// <summary>IWRAM address of the observed serial-IRQ (IF bit 7) count, written by both link ROMs at the end.</summary>
    public const uint LinkIrqCountAddress = 0x03000000u;

    /// <summary>IWRAM address of the completion marker (<see cref="LinkCompletionMarker"/>).</summary>
    public const uint LinkMarkerAddress = 0x03000004u;

    /// <summary>IWRAM address of the final SIOCNT read-back (id bits, cleared start) both link ROMs record.</summary>
    public const uint LinkControlAddress = 0x03000008u;

    /// <summary>IWRAM base of the per-round records: 8 bytes per round — the SIOMULTI0/1 word then the SIOMULTI2/3
    /// word, exactly as read back after each round's IRQ.</summary>
    public const uint LinkRecordAddress = 0x03000010u;

    /// <summary>The completion-marker value both link ROMs store once all rounds are done.</summary>
    public const uint LinkCompletionMarker = 0x600DF00Du;

    // Builds a micro-ROM image in memory (no disk), so the savestate round-trip diagnostic can boot every kind
    // without a temp file.
    public static byte[] GenerateBytes(string kind) => kind switch {
        "timer-irq" => TimerIrq(reload: 0xFF00, control: 0x00C0),
        "timer-irq-iwram" => TimerIrqIwram(reload: 0xFF00, control: 0x00C0),
        "cascade-irq" => CascadeIrq(),
        "ime-delay" => ImeDelay(),
        "link-parent" => LinkParent(),
        "link-child" => LinkChild(),
        _ => throw new ArgumentException(message: $"unknown micro-rom kind '{kind}' (timer-irq | timer-irq-iwram | cascade-irq | ime-delay | link-parent | link-child)"),
    };
    public static void Generate(string kind, string outPath) {
        var rom = GenerateBytes(kind: kind);

        File.WriteAllBytes(path: outPath, bytes: rom);
        Console.WriteLine(value: $"== wrote '{kind}' micro-rom ({rom.Length} bytes) to {outPath} ==");
    }

    // Timer0 overflows and raises an IRQ; the handler acks it and returns. The main loop counts in r4, so the
    // exact instruction at which the IRQ is recognised is observable. (reload, control) parameterise the cadence.
    private static byte[] TimerIrq(uint reload, uint control) {
        var a = new Asm();

        a.LdrConst(rd: 0, value: 0x04000000u);          // r0 = I/O base
        a.LdrLabel(rd: 1, label: "handler");
        a.LdrConst(rd: 2, value: 0x03007FFCu);          // BIOS user-IRQ-handler pointer
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Mov(rd: 1, imm8: 1);
        a.Str(rd: 1, rn: 0, imm12: 0x208);              // IME = 1
        a.Mov(rd: 1, imm8: 8);
        a.Str(rd: 1, rn: 0, imm12: 0x200);              // IE = timer0 (bit 3); IF high half = 0
        a.LdrConst(rd: 1, value: (control << 16) | (reload & 0xFFFFu));
        a.Str(rd: 1, rn: 0, imm12: 0x100);              // TM0CNT_L reload + TM0CNT_H control, one word
        a.Mov(rd: 4, imm8: 0);
        a.Label(name: "loop");
        a.Add(rd: 4, rn: 4, imm8: 1);
        a.BBack(instructions: 1);                        // b loop

        a.Label(name: "handler");
        a.LdrConst(rd: 0, value: 0x04000000u);
        a.LdrConst(rd: 1, value: 0x00080008u);          // IE = timer0; IF write-one-to-clear bit 3
        a.Str(rd: 1, rn: 0, imm12: 0x200);
        a.Bx(rn: 14);                                    // return to the BIOS dispatcher

        return a.Finish();
    }

    // As TimerIrq, but the counting loop runs from IWRAM (0-wait, no prefetch), so the per-instruction cycle
    // accounting is deterministic and identical between cores — any remaining divergence is purely IRQ-recognition
    // timing, with the slow-ROM bus-cost attribution noise removed. The loop body is two opcodes written straight
    // into IWRAM (position-independent: `add r4,r4,#1` then `b .-4`), then we jump there with `ldr pc, =0x03000000`.
    private static byte[] TimerIrqIwram(uint reload, uint control) {
        var a = new Asm();

        a.LdrConst(rd: 0, value: 0x04000000u);          // r0 = I/O base
        a.LdrConst(rd: 2, value: 0x03000000u);          // r2 = IWRAM loop address
        a.LdrConst(rd: 1, value: 0xE2844001u);          // add r4,r4,#1
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.LdrConst(rd: 1, value: 0xEAFFFFFDu);          // b .-4  (back to 0x03000000)
        a.Str(rd: 1, rn: 2, imm12: 4);
        a.LdrLabel(rd: 1, label: "handler");
        a.LdrConst(rd: 3, value: 0x03007FFCu);
        a.Str(rd: 1, rn: 3, imm12: 0);
        a.Mov(rd: 1, imm8: 1);
        a.Str(rd: 1, rn: 0, imm12: 0x208);              // IME = 1
        a.Mov(rd: 1, imm8: 8);
        a.Str(rd: 1, rn: 0, imm12: 0x200);              // IE = timer0
        a.LdrConst(rd: 1, value: (control << 16) | (reload & 0xFFFFu));
        a.Str(rd: 1, rn: 0, imm12: 0x100);              // TM0CNT
        a.Mov(rd: 4, imm8: 0);
        a.LdrConst(rd: 15, value: 0x03000000u);         // ldr pc, =0x03000000 — jump to the IWRAM loop

        a.Label(name: "handler");
        a.LdrConst(rd: 0, value: 0x04000000u);
        a.LdrConst(rd: 1, value: 0x00080008u);
        a.Str(rd: 1, rn: 0, imm12: 0x200);
        a.Bx(rn: 14);

        return a.Finish();
    }

    // Timer0 (prescaler ÷1) cascades into Timer1 (count-up); Timer1 raises the IRQ. Probes the synchronous
    // in-cycle cascade + the cascade-timer overflow→IRQ path.
    private static byte[] CascadeIrq() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: 0x04000000u);
        a.LdrLabel(rd: 1, label: "handler");
        a.LdrConst(rd: 2, value: 0x03007FFCu);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Mov(rd: 1, imm8: 1);
        a.Str(rd: 1, rn: 0, imm12: 0x208);              // IME = 1
        a.Mov(rd: 1, imm8: 0x10);
        a.Str(rd: 1, rn: 0, imm12: 0x200);              // IE = timer1 (bit 4)
        // Timer1: reload 0xFFFE (cascade overflows after 2 cascade ticks), control 0xC4 = enable+irq+cascade.
        a.LdrConst(rd: 1, value: (0x00C4u << 16) | 0xFFFEu);
        a.Str(rd: 1, rn: 0, imm12: 0x104);              // TM1CNT
        // Timer0: reload 0xFFF0 (overflows every 16 cycles), control 0x80 = enable, prescale ÷1.
        a.LdrConst(rd: 1, value: (0x0080u << 16) | 0xFFF0u);
        a.Str(rd: 1, rn: 0, imm12: 0x100);              // TM0CNT
        a.Mov(rd: 4, imm8: 0);
        a.Label(name: "loop");
        a.Add(rd: 4, rn: 4, imm8: 1);
        a.BBack(instructions: 1);

        a.Label(name: "handler");
        a.LdrConst(rd: 0, value: 0x04000000u);
        a.LdrConst(rd: 1, value: 0x00100010u);          // IE = timer1; IF clear bit 4
        a.Str(rd: 1, rn: 0, imm12: 0x200);
        a.Bx(rn: 14);

        return a.Finish();
    }

    // IF is pre-pended with a timer0 request while IME is left 0, then IME is enabled mid-stream: the IRQ must be
    // recognised one instruction after the IME store (the ime[1]→ime[0] pipeline), not on the store itself.
    private static byte[] ImeDelay() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: 0x04000000u);
        a.LdrLabel(rd: 1, label: "handler");
        a.LdrConst(rd: 2, value: 0x03007FFCu);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Mov(rd: 1, imm8: 8);
        a.Str(rd: 1, rn: 0, imm12: 0x200);              // IE = timer0
        // Timer0 reload 0xFFFF (overflows next cycle), enable+irq prescale ÷1 — arms a pending IF quickly.
        a.LdrConst(rd: 1, value: (0x00C0u << 16) | 0xFFFFu);
        a.Str(rd: 1, rn: 0, imm12: 0x100);
        a.Mov(rd: 4, imm8: 0);                           // marker before IME
        a.Mov(rd: 5, imm8: 0);
        a.Mov(rd: 1, imm8: 1);
        a.Str(rd: 1, rn: 0, imm12: 0x208);              // IME = 1  (IRQ must NOT fire on this instruction)
        a.Mov(rd: 4, imm8: 1);                           // r4 = 1: executed iff IRQ deferred past the IME store
        a.Label(name: "loop");
        a.Add(rd: 5, rn: 5, imm8: 1);
        a.BBack(instructions: 1);

        a.Label(name: "handler");
        a.LdrConst(rd: 0, value: 0x04000000u);
        a.LdrConst(rd: 1, value: 0x00080008u);
        a.Str(rd: 1, rn: 0, imm12: 0x200);
        a.Bx(rn: 14);

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
        var a = new Asm();

        a.LdrConst(rd: 0, value: 0x04000000u);           // r0  = I/O base (word reads: SIOMULTI0..3)
        a.LdrConst(rd: 10, value: 0x04000100u);          // r10 = halfword base for SIOCNT/SIOMLT_SEND/RCNT
        a.LdrConst(rd: 11, value: 0x04000200u);          // r11 = halfword base for IF
        a.Mov(rd: 1, imm8: 0);
        a.Strh(rd: 1, rn: 10, imm8: 0x34);               // RCNT = 0: pins owned by SIO
        a.LdrConst(rd: 7, value: LinkRecordAddress);
        a.LdrConst(rd: 8, value: LinkParentSendBase);
        a.Mov(rd: 4, imm8: 0);                           // r4 = observed serial-IRQ (IF) count
        a.Mov(rd: 6, imm8: 0);                           // r6 = round index

        a.Label(name: "round");
        a.AddReg(rd: 1, rn: 8, rm: 6);
        a.Strh(rd: 1, rn: 10, imm8: 0x2A);               // SIOMLT_SEND = base + round
        a.LdrConst(rd: 1, value: 0x00006083u);           // multiplayer | 115200 bps | IRQ-enable | start
        a.Strh(rd: 1, rn: 10, imm8: 0x28);               // SIOCNT — the parent clocks the round

        a.Label(name: "wait");
        a.Ldrh(rd: 1, rn: 11, imm8: 0x02);               // IF
        a.Tst(rn: 1, imm8: 0x80);                        // serial request?
        a.B(label: "wait", cond: Asm.CondEq);
        a.Mov(rd: 1, imm8: 0x80);
        a.Strh(rd: 1, rn: 11, imm8: 0x02);               // acknowledge (write-one-to-clear)
        a.Add(rd: 4, rn: 4, imm8: 1);

        a.LslImm(rd: 2, rm: 6, shift: 3);
        a.AddReg(rd: 2, rn: 7, rm: 2);
        a.Ldr(rd: 1, rn: 0, imm12: 0x120);               // SIOMULTI0/1
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Ldr(rd: 1, rn: 0, imm12: 0x124);               // SIOMULTI2/3
        a.Str(rd: 1, rn: 2, imm12: 4);

        a.Add(rd: 6, rn: 6, imm8: 1);
        a.Cmp(rn: 6, imm8: LinkRounds);
        a.B(label: "round", cond: Asm.CondNe);

        EmitLinkEpilogue(a: a);

        return a.Finish();
    }
    private static byte[] LinkChild() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: 0x04000000u);           // r0  = I/O base (word reads: SIOMULTI0..3)
        a.LdrConst(rd: 10, value: 0x04000100u);          // r10 = halfword base for SIOCNT/SIOMLT_SEND/RCNT
        a.LdrConst(rd: 11, value: 0x04000200u);          // r11 = halfword base for IF
        a.Mov(rd: 1, imm8: 0);
        a.Strh(rd: 1, rn: 10, imm8: 0x34);               // RCNT = 0: pins owned by SIO
        a.LdrConst(rd: 7, value: LinkRecordAddress);
        a.LdrConst(rd: 8, value: LinkChildTransformMask);
        a.LdrConst(rd: 1, value: LinkChildSeedWord);
        a.Strh(rd: 1, rn: 10, imm8: 0x2A);               // seed SIOMLT_SEND — the round-0 reply
        a.LdrConst(rd: 1, value: 0x00006003u);           // multiplayer | 115200 bps | IRQ-enable (no start: parent clocks)
        a.Strh(rd: 1, rn: 10, imm8: 0x28);
        a.Mov(rd: 4, imm8: 0);                           // r4 = observed serial-IRQ (IF) count
        a.Mov(rd: 6, imm8: 0);                           // r6 = round index

        a.Label(name: "round");
        a.Ldrh(rd: 1, rn: 11, imm8: 0x02);               // IF
        a.Tst(rn: 1, imm8: 0x80);                        // the parent's round landed?
        a.B(label: "round", cond: Asm.CondEq);
        a.Mov(rd: 1, imm8: 0x80);
        a.Strh(rd: 1, rn: 11, imm8: 0x02);               // acknowledge (write-one-to-clear)
        a.Add(rd: 4, rn: 4, imm8: 1);

        a.LslImm(rd: 2, rm: 6, shift: 3);
        a.AddReg(rd: 2, rn: 7, rm: 2);
        a.Ldr(rd: 1, rn: 0, imm12: 0x120);               // SIOMULTI0/1
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Ldr(rd: 3, rn: 0, imm12: 0x124);               // SIOMULTI2/3
        a.Str(rd: 3, rn: 2, imm12: 4);

        a.LslImm(rd: 3, rm: 1, shift: 16);               // isolate SIOMULTI0 — the parent's word this round
        a.LsrImm(rd: 3, rm: 3, shift: 16);
        a.EorReg(rd: 3, rn: 3, rm: 8);                   // the transform
        a.Strh(rd: 3, rn: 10, imm8: 0x2A);               // re-arm SIOMLT_SEND for the next round

        a.Add(rd: 6, rn: 6, imm8: 1);
        a.Cmp(rn: 6, imm8: LinkRounds);
        a.B(label: "round", cond: Asm.CondNe);

        EmitLinkEpilogue(a: a);

        return a.Finish();
    }

    // Shared tail of the two link ROMs: record the final SIOCNT (id bits + cleared start), the IF-observation count,
    // and the completion marker, then hang.
    private static void EmitLinkEpilogue(Asm a) {
        a.Ldrh(rd: 1, rn: 10, imm8: 0x28);               // final SIOCNT
        a.LdrConst(rd: 2, value: LinkControlAddress);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.LdrConst(rd: 2, value: LinkIrqCountAddress);
        a.Str(rd: 4, rn: 2, imm12: 0);
        a.LdrConst(rd: 1, value: LinkCompletionMarker);
        a.LdrConst(rd: 2, value: LinkMarkerAddress);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Label(name: "hang");
        a.B(label: "hang");
    }

    // A minimal ARM assembler: emits little-endian words, resolves PC-relative literal loads through a dedup pool
    // appended after the code, and resolves backward branch targets and labels. Enough for these micro-ROMs.
    private sealed class Asm {
        private const uint RomBase = 0x08000000u;
        private const int RomSizeBytes = 0x8000; // pad so the cartridge/oracle see a sane game-pak

        public const uint CondAl = 0xEu;
        public const uint CondEq = 0x0u;
        public const uint CondNe = 0x1u;

        private readonly List<(int instr, string label, uint cond)> m_branches = new();
        private readonly List<uint> m_code = new();
        private readonly List<(int instr, int rd, uint value, string? label)> m_loads = new();
        private readonly Dictionary<string, int> m_labels = new();

        public void Label(string name) => m_labels[name] = m_code.Count;
        public void Mov(int rd, uint imm8) => m_code.Add(item: 0xE3A00000u | ((uint)rd << 12) | (imm8 & 0xFFu));
        public void Add(int rd, int rn, uint imm8) =>
            m_code.Add(item: 0xE2800000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm8 & 0xFFu));
        public void AddReg(int rd, int rn, int rm) =>
            m_code.Add(item: 0xE0800000u | ((uint)rn << 16) | ((uint)rd << 12) | (uint)rm);
        public void EorReg(int rd, int rn, int rm) =>
            m_code.Add(item: 0xE0200000u | ((uint)rn << 16) | ((uint)rd << 12) | (uint)rm);

        // MOV rd, rm, LSL #shift / LSR #shift — the register-shift-by-immediate movs.
        public void LslImm(int rd, int rm, int shift) =>
            m_code.Add(item: 0xE1A00000u | ((uint)rd << 12) | ((uint)shift << 7) | (uint)rm);
        public void LsrImm(int rd, int rm, int shift) =>
            m_code.Add(item: 0xE1A00020u | ((uint)rd << 12) | ((uint)shift << 7) | (uint)rm);
        public void Cmp(int rn, uint imm8) => m_code.Add(item: 0xE3500000u | ((uint)rn << 16) | (imm8 & 0xFFu));
        public void Tst(int rn, uint imm8) => m_code.Add(item: 0xE3100000u | ((uint)rn << 16) | (imm8 & 0xFFu));
        public void Ldr(int rd, int rn, uint imm12) =>
            m_code.Add(item: 0xE5900000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm12 & 0xFFFu));
        public void Str(int rd, int rn, uint imm12) =>
            m_code.Add(item: 0xE5800000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm12 & 0xFFFu));

        // LDRH/STRH split their 8-bit offset into two nibbles (addressing mode 3).
        public void Ldrh(int rd, int rn, uint imm8) =>
            m_code.Add(item: 0xE1D000B0u | ((uint)rn << 16) | ((uint)rd << 12) | ((imm8 & 0xF0u) << 4) | (imm8 & 0x0Fu));
        public void Strh(int rd, int rn, uint imm8) =>
            m_code.Add(item: 0xE1C000B0u | ((uint)rn << 16) | ((uint)rd << 12) | ((imm8 & 0xF0u) << 4) | (imm8 & 0x0Fu));
        public void Bx(int rn) => m_code.Add(item: 0xE12FFF10u | (uint)rn);

        // A (conditional) branch to a label, forward or backward; the offset is fixed up in Finish once every label
        // is known.
        public void B(string label, uint cond = CondAl) {
            m_branches.Add(item: (m_code.Count, label, cond));
            m_code.Add(item: 0);
        }

        // Branch back to the instruction `instructions` slots before this one (1 = the immediately preceding one).
        public void BBack(int instructions) {
            var idx = m_code.Count;
            var off = ((idx - instructions) - (idx + 2)); // ARM PC is 2 instructions (8 bytes) ahead

            m_code.Add(item: 0xEA000000u | ((uint)off & 0xFFFFFFu));
        }
        public void LdrConst(int rd, uint value) {
            m_loads.Add(item: (m_code.Count, rd, value, null));
            m_code.Add(item: 0);
        }
        public void LdrLabel(int rd, string label) {
            m_loads.Add(item: (m_code.Count, rd, 0, label));
            m_code.Add(item: 0);
        }
        public byte[] Finish() {
            foreach (var (instr, label, cond) in m_branches) {
                var off = (m_labels[label] - (instr + 2)); // ARM PC is 2 instructions (8 bytes) ahead

                m_code[instr] = (cond << 28) | 0x0A000000u | ((uint)off & 0xFFFFFFu);
            }

            var poolBase = m_code.Count;
            var pool = new List<uint>();

            foreach (var (instr, rd, value, label) in m_loads) {
                var resolved = ((label is null) ? value : (RomBase + ((uint)m_labels[label] * 4u)));
                var poolIndex = pool.IndexOf(item: resolved);

                if (poolIndex < 0) {
                    poolIndex = pool.Count;
                    pool.Add(item: resolved);
                }

                var literalWord = (poolBase + poolIndex);
                var offsetBytes = ((literalWord - (instr + 2)) * 4); // pc = instr*4 + 8

                m_code[instr] = 0xE59F0000u | ((uint)rd << 12) | ((uint)offsetBytes & 0xFFFu);
            }

            var words = new List<uint>(collection: m_code);

            words.AddRange(collection: pool);

            var bytes = new byte[RomSizeBytes];

            for (var i = 0; (i < words.Count); ++i) {
                BitConverter.TryWriteBytes(destination: bytes.AsSpan(start: (i * 4)), value: words[i]);
            }

            return bytes;
        }
    }
}
