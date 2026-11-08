namespace Puck.AdvancedGamingBrick.Post;

// ARM probe emission and finishing; literal loads use AsmLiteralPool.
internal sealed class ArmProbeAssembler {
    private const uint RomBase = 0x08000000u;
    private const int RomSizeBytes = 0x8000; // pad so the cartridge/oracle see a sane game-pak

    public const uint CondAl = 0xEu;
    public const uint CondEq = 0x0u;
    public const uint CondNe = 0x1u;

    private readonly List<(int instr, string label, uint cond)> m_branches = new();
    private readonly List<uint> m_code = new();
    private readonly List<(int instr, int rd, uint value, string? label)> m_loads = new();
    private readonly Dictionary<string, int> m_labels = new();

    public void Add(int rd, int rn, uint imm8) =>
        m_code.Add(item: 0xE2800000u | (((uint)rn) << 16) | (((uint)rd) << 12) | (imm8 & 0xFFu));
    public void AddReg(int rd, int rn, int rm) =>
        m_code.Add(item: 0xE0800000u | (((uint)rn) << 16) | (((uint)rd) << 12) | ((uint)rm));
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

        m_code.Add(item: 0xEA000000u | (((uint)off) & 0xFFFFFFu));
    }
    public void Bx(int rn) => m_code.Add(item: 0xE12FFF10u | ((uint)rn));
    public void Cmp(int rn, uint imm8) => m_code.Add(item: 0xE3500000u | (((uint)rn) << 16) | (imm8 & 0xFFu));
    public void EorReg(int rd, int rn, int rm) =>
        m_code.Add(item: 0xE0200000u | (((uint)rn) << 16) | (((uint)rd) << 12) | ((uint)rm));
    public byte[] Finish() {
        foreach (var (instr, label, cond) in m_branches) {
            var off = (m_labels[label] - (instr + 2)); // ARM PC is 2 instructions (8 bytes) ahead

            m_code[instr] = (cond << 28) | 0x0A000000u | (((uint)off) & 0xFFFFFFu);
        }

        var poolBase = m_code.Count;
        var pool = AsmLiteralPool.Resolve(
            code: m_code,
            labels: m_labels,
            loads: m_loads,
            poolBase: poolBase,
            romBase: RomBase
        );

        var words = new List<uint>(collection: m_code);

        words.AddRange(collection: pool);

        var bytes = new byte[RomSizeBytes];

        for (var i = 0; (i < words.Count); ++i) {
            BitConverter.TryWriteBytes(
                destination: bytes.AsSpan(start: (i * 4)),
                value: words[i]
            );
        }

        return bytes;
    }
    public void Label(string name) => m_labels[name] = m_code.Count;
    public void Ldr(int rd, int rn, uint imm12) =>
        m_code.Add(item: 0xE5900000u | (((uint)rn) << 16) | (((uint)rd) << 12) | (imm12 & 0xFFFu));
    public void LdrConst(int rd, uint value) {
        m_loads.Add(item: (m_code.Count, rd, value, null));
        m_code.Add(item: 0);
    }
    public void LdrLabel(int rd, string label) {
        m_loads.Add(item: (m_code.Count, rd, 0, label));
        m_code.Add(item: 0);
    }
    // LDRH/STRH split their 8-bit offset into two nibbles (addressing mode 3).
    public void Ldrh(int rd, int rn, uint imm8) =>
        m_code.Add(item: 0xE1D000B0u | (((uint)rn) << 16) | (((uint)rd) << 12) | ((imm8 & 0xF0u) << 4) | (imm8 & 0x0Fu));
    // MOV rd, rm, LSL #shift / LSR #shift — the register-shift-by-immediate movs.
    public void LslImm(int rd, int rm, int shift) =>
        m_code.Add(item: 0xE1A00000u | (((uint)rd) << 12) | (((uint)shift) << 7) | ((uint)rm));
    public void LsrImm(int rd, int rm, int shift) =>
        m_code.Add(item: 0xE1A00020u | (((uint)rd) << 12) | (((uint)shift) << 7) | ((uint)rm));
    public void Nop() => m_code.Add(item: 0xE1A00000u);
    public void Spin() => m_code.Add(item: 0xEAFFFFFEu);
    public void Mov(int rd, uint imm8) => m_code.Add(item: 0xE3A00000u | (((uint)rd) << 12) | (imm8 & 0xFFu));
    public void Str(int rd, int rn, uint imm12) =>
        m_code.Add(item: 0xE5800000u | (((uint)rn) << 16) | (((uint)rd) << 12) | (imm12 & 0xFFFu));
    public void Strh(int rd, int rn, uint imm8) =>
        m_code.Add(item: 0xE1C000B0u | (((uint)rn) << 16) | (((uint)rd) << 12) | ((imm8 & 0xF0u) << 4) | (imm8 & 0x0Fu));
    public void Tst(int rn, uint imm8) => m_code.Add(item: 0xE3100000u | (((uint)rn) << 16) | (imm8 & 0xFFu));
}
