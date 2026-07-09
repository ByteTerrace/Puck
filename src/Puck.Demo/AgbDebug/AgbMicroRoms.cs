namespace Puck.Demo.AgbDebug;

/// <summary>
/// Generates tiny hand-assembled ARM <c>.gba</c> ROMs the AGB debug scene falls back to when no cartridge is
/// reachable — a deterministic, self-contained subject so <c>agb.debug</c> always has something to boot, step, and
/// hash. The pattern (a minimal in-process ARM assembler emitting a direct-boot image at 0x08000000) mirrors the
/// AdvancedGamingBrick POST battery's micro-ROMs; this copy keeps the two decoupled (the demo never references the
/// POST project) and adds a VISIBLE mode-3 bitmap ROM so the fullscreen scene shows a real picture, not a black pane.
/// </summary>
internal static class AgbMicroRoms {
    /// <summary>The default built-in ROM kind — a visible mode-3 gradient, so the fullscreen scene lights up.</summary>
    public const string DefaultKind = "mode3-fill";

    /// <summary>Builds a built-in micro-ROM image by name.</summary>
    /// <param name="kind"><c>mode3-fill</c> (a visible BG-mode-3 gradient) or <c>timer-irq</c> (a register-exercising
    /// timer-IRQ loop, handy for <c>agb.step</c>/<c>agb.trace</c>/<c>agb.regs</c>).</param>
    /// <returns>The assembled cartridge ROM image (0x8000 bytes, padded).</returns>
    public static byte[] Generate(string kind) =>
        kind.ToLowerInvariant() switch {
            "timer-irq" => TimerIrq(reload: 0xFF00, control: 0x00C0),
            _ => Mode3Fill(),
        };

    // BG mode 3 (a 240x160 15-bit direct bitmap at 0x06000000): enable it via DISPCNT, then fill VRAM by writing each
    // pixel's own framebuffer pointer as its colour — a clean diagonal BGR gradient across the whole screen — and spin.
    // The picture is a pure function of the emulated PPU, so its framebuffer hash is a deterministic render floor.
    private static byte[] Mode3Fill() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: 0x04000000u);          // r0 = I/O base
        a.LdrConst(rd: 1, value: 0x0403u);              // DISPCNT = mode 3 + BG2 on
        a.Str(rd: 1, rn: 0, imm12: 0);
        a.LdrConst(rd: 1, value: 0x06000000u);          // r1 = VRAM (BG mode 3 framebuffer)
        a.LdrConst(rd: 3, value: (240u * 160u) / 2u);   // r3 = word count (two 16-bit pixels per word)
        a.Label(name: "fill");
        a.Str(rd: 1, rn: 1, imm12: 0);                  // write the pointer value as the pixel colour
        a.Add(rd: 1, rn: 1, imm8: 4);                   // advance one word
        a.Subs(rd: 3, rn: 3, imm8: 1);
        a.BneBack(instructions: 3);                      // bne fill (back to the Str)
        a.BBack(instructions: 1);                        // b .  (spin forever)

        return a.Finish();
    }

    // Timer0 overflows and raises an IRQ; the handler acks it and returns, while the main loop counts in r4 — so an
    // agent stepping this ROM sees r4 advance and the IRQ recognised at an observable instruction. (reload, control)
    // parameterise the cadence. Mirrors the POST battery's timer-irq micro-ROM.
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

    // A minimal ARM assembler: emits little-endian words, resolves PC-relative literal loads through a dedup pool
    // appended after the code, and resolves backward branch targets and labels. Enough for these micro-ROMs.
    private sealed class Asm {
        private const uint RomBase = 0x08000000u;
        private const int RomSizeBytes = 0x8000; // pad so the cartridge sees a sane game-pak

        private readonly List<uint> m_code = new();
        private readonly List<(int instr, int rd, uint value, string? label)> m_loads = new();
        private readonly Dictionary<string, int> m_labels = new();

        public void Label(string name) => m_labels[name] = m_code.Count;

        public void Mov(int rd, uint imm8) => m_code.Add(item: 0xE3A00000u | ((uint)rd << 12) | (imm8 & 0xFFu));

        public void Add(int rd, int rn, uint imm8) =>
            m_code.Add(item: 0xE2800000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm8 & 0xFFu));

        public void Subs(int rd, int rn, uint imm8) =>
            m_code.Add(item: 0xE2500000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm8 & 0xFFu));

        public void Str(int rd, int rn, uint imm12) =>
            m_code.Add(item: 0xE5800000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm12 & 0xFFFu));

        public void Bx(int rn) => m_code.Add(item: 0xE12FFF10u | (uint)rn);

        // Unconditional branch back to the instruction `instructions` slots before this one (1 = the immediately
        // preceding one). ARM PC is 2 instructions (8 bytes) ahead when the branch offset is applied.
        public void BBack(int instructions) => m_code.Add(item: 0xEA000000u | BackOffset(instructions: instructions));

        // Conditional (NE) branch back — the loop primitive paired with Subs.
        public void BneBack(int instructions) => m_code.Add(item: 0x1A000000u | BackOffset(instructions: instructions));

        private uint BackOffset(int instructions) {
            var idx = m_code.Count;
            var off = (idx - instructions) - (idx + 2);

            return ((uint)off & 0xFFFFFFu);
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
