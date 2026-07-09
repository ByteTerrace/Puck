namespace Puck.AdvancedGamingBrick.Post;

// Generates tiny hand-assembled ARM .gba ROMs that isolate timer/IRQ-recognition timing for differential
// lockstep against the ARES oracle (`--lockstep <rom> N direct`). Unlike a commercial game, a tight loop has
// near-zero cumulative bus-cost drift, so any Puck-vs-ARES functional divergence is purely the IRQ pipeline
// depth / timer latch — exactly the residual this session is tuning. Code begins at ROM offset 0 (= 0x08000000),
// which both cores jump to under direct boot; the real BIOS handles the IRQ vector and dispatches to the user
// handler installed at 0x03007FFC.
internal static class MicroRoms {
    /// <summary>The micro-ROM kinds this generator knows, for the diagnostics that sweep them in memory.</summary>
    public static readonly string[] Kinds = ["timer-irq", "timer-irq-iwram", "cascade-irq", "ime-delay"];

    // Builds a micro-ROM image in memory (no disk), so the savestate round-trip diagnostic can boot every kind
    // without a temp file.
    public static byte[] GenerateBytes(string kind) => kind switch {
        "timer-irq" => TimerIrq(reload: 0xFF00, control: 0x00C0),
        "timer-irq-iwram" => TimerIrqIwram(reload: 0xFF00, control: 0x00C0),
        "cascade-irq" => CascadeIrq(),
        "ime-delay" => ImeDelay(),
        _ => throw new ArgumentException(message: $"unknown micro-rom kind '{kind}' (timer-irq | timer-irq-iwram | cascade-irq | ime-delay)"),
    };

    public static void Generate(string kind, string outPath) {
        var rom = GenerateBytes(kind: kind);

        File.WriteAllBytes(path: outPath, bytes: rom);
        Console.WriteLine($"== wrote '{kind}' micro-rom ({rom.Length} bytes) to {outPath} ==");
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

    // A minimal ARM assembler: emits little-endian words, resolves PC-relative literal loads through a dedup pool
    // appended after the code, and resolves backward branch targets and labels. Enough for these micro-ROMs.
    private sealed class Asm {
        private const uint RomBase = 0x08000000u;
        private const int RomSizeBytes = 0x8000; // pad so the cartridge/oracle see a sane game-pak

        private readonly List<uint> m_code = new();
        private readonly List<(int instr, int rd, uint value, string? label)> m_loads = new();
        private readonly Dictionary<string, int> m_labels = new();

        public void Label(string name) => m_labels[name] = m_code.Count;

        public void Mov(int rd, uint imm8) => m_code.Add(item: 0xE3A00000u | ((uint)rd << 12) | (imm8 & 0xFFu));

        public void Add(int rd, int rn, uint imm8) =>
            m_code.Add(item: 0xE2800000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm8 & 0xFFu));

        public void Str(int rd, int rn, uint imm12) =>
            m_code.Add(item: 0xE5800000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm12 & 0xFFFu));

        public void Bx(int rn) => m_code.Add(item: 0xE12FFF10u | (uint)rn);

        // Branch back to the instruction `instructions` slots before this one (1 = the immediately preceding one).
        public void BBack(int instructions) {
            var idx = m_code.Count;
            var off = (idx - instructions) - (idx + 2); // ARM PC is 2 instructions (8 bytes) ahead

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
            var poolBase = m_code.Count;
            var pool = new List<uint>();

            foreach (var (instr, rd, value, label) in m_loads) {
                var resolved = label is null ? value : RomBase + ((uint)m_labels[label] * 4u);
                var poolIndex = pool.IndexOf(item: resolved);

                if (poolIndex < 0) {
                    poolIndex = pool.Count;
                    pool.Add(item: resolved);
                }

                var literalWord = poolBase + poolIndex;
                var offsetBytes = (literalWord - (instr + 2)) * 4; // pc = instr*4 + 8

                m_code[instr] = 0xE59F0000u | ((uint)rd << 12) | ((uint)offsetBytes & 0xFFFu);
            }

            var words = new List<uint>(collection: m_code);

            words.AddRange(collection: pool);

            var bytes = new byte[RomSizeBytes];

            for (var i = 0; i < words.Count; ++i) {
                BitConverter.TryWriteBytes(destination: bytes.AsSpan(start: i * 4), value: words[i]);
            }

            return bytes;
        }
    }
}
