namespace Puck.Demo.Forge;

/// <summary>
/// A deliberately tiny SM83 (the brick's CPU) assembler: just the instructions the forge's display routine needs, emitted
/// as readable calls instead of raw hex, with a one-pass label fixup for the relative <c>jr</c> loops. It is the
/// "quickest to first-pixel" seed of a real assembler — private to the forge, expected to be lifted into a reusable
/// <c>Puck.HumbleGamingBrickRom</c> toolkit later. Opcodes are the standard SM83 encoding (see the emulator's decoder,
/// <c>experimental/Puck.HumbleGamingBrick/Sm83.Decode.cs</c>).
/// </summary>
internal sealed class Sm83Emitter {
    private readonly List<byte> m_code = [];
    private readonly Dictionary<int, int> m_labelOffsets = [];
    private readonly List<(int PatchOffset, int Label)> m_relativeFixups = [];
    private readonly List<(int PatchOffset, int Label)> m_absoluteFixups = [];
    private int m_nextLabel;

    /// <summary>Allocates an unbound label id; bind it with <see cref="MarkLabel"/> at the target instruction.</summary>
    public int NewLabel() => m_nextLabel++;

    /// <summary>Binds <paramref name="label"/> to the current position in the stream.</summary>
    public void MarkLabel(int label) => m_labelOffsets[label] = m_code.Count;

    public void Nop() => m_code.Add(item: 0x00);
    public void DisableInterrupts() => m_code.Add(item: 0xF3);
    public void Halt() => m_code.Add(item: 0x76);
    public void XorA() => m_code.Add(item: 0xAF); // A = 0, the cheapest zero.
    public void OrA() => m_code.Add(item: 0xB7);  // A |= A — sets the zero flag when A == 0 (a cheap "test A").
    public void IncrementA() => m_code.Add(item: 0x3C);
    public void DecrementA() => m_code.Add(item: 0x3D);
    /// <summary>ld b, a</summary>
    public void LoadBFromA() => m_code.Add(item: 0x47);
    /// <summary>bit n, b — sets the zero flag to the COMPLEMENT of bit n of B (Z = 1 when the bit is 0, i.e. when an
    /// active-low joypad line reads as PRESSED).</summary>
    public void TestBitOfB(int bit) {
        if ((bit < 0) || (bit > 7)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(bit));
        }

        m_code.Add(item: 0xCB);
        m_code.Add(item: (byte)(0x40 + (bit * 8)));
    }

    /// <summary>ld sp, nn</summary>
    public void LoadStackPointer(ushort value) => EmitImmediate16(opcode: 0x31, value: value);
    /// <summary>ld a, n</summary>
    public void LoadAImmediate(byte value) { m_code.Add(item: 0x3E); m_code.Add(item: value); }
    /// <summary>ld b, n</summary>
    public void LoadBImmediate(byte value) { m_code.Add(item: 0x06); m_code.Add(item: value); }
    /// <summary>ld hl, nn</summary>
    public void LoadHlImmediate(ushort value) => EmitImmediate16(opcode: 0x21, value: value);
    /// <summary>ld de, nn</summary>
    public void LoadDeImmediate(ushort value) => EmitImmediate16(opcode: 0x11, value: value);
    /// <summary>ld bc, nn</summary>
    public void LoadBcImmediate(ushort value) => EmitImmediate16(opcode: 0x01, value: value);

    /// <summary>ld a, (hl+) — read from HL, then increment HL.</summary>
    public void LoadAFromHlIncrement() => m_code.Add(item: 0x2A);
    /// <summary>ld (hl+), a — write A to HL, then increment HL.</summary>
    public void StoreAToHlIncrement() => m_code.Add(item: 0x22);
    /// <summary>ld (de), a</summary>
    public void StoreAToDe() => m_code.Add(item: 0x12);
    /// <summary>inc de</summary>
    public void IncrementDe() => m_code.Add(item: 0x13);
    /// <summary>dec b</summary>
    public void DecrementB() => m_code.Add(item: 0x05);
    /// <summary>dec bc</summary>
    public void DecrementBc() => m_code.Add(item: 0x0B);
    /// <summary>ld a, b</summary>
    public void LoadAFromB() => m_code.Add(item: 0x78);
    /// <summary>or c — used with <see cref="LoadAFromB"/> to test whether the 16-bit BC counter has reached zero.</summary>
    public void OrC() => m_code.Add(item: 0xB1);

    /// <summary>ldh (n), a — write A to the high page (0xFF00 + <paramref name="port"/>).</summary>
    public void StoreAToHighPage(byte port) { m_code.Add(item: 0xE0); m_code.Add(item: port); }
    /// <summary>ldh a, (n) — read A from the high page (0xFF00 + <paramref name="port"/>).</summary>
    public void LoadAFromHighPage(byte port) { m_code.Add(item: 0xF0); m_code.Add(item: port); }

    /// <summary>ld a, (nn) — read A from an absolute address (e.g. a work-RAM sensor byte).</summary>
    public void LoadAFromAddress(ushort address) => EmitImmediate16(opcode: 0xFA, value: address);
    /// <summary>ld (nn), a — write A to an absolute address (e.g. an OAM byte).</summary>
    public void StoreAToAddress(ushort address) => EmitImmediate16(opcode: 0xEA, value: address);
    /// <summary>add a, a — A += A (a left shift by one; three of them multiply a tile coordinate to pixels).</summary>
    public void AddAToA() => m_code.Add(item: 0x87);
    /// <summary>add a, n</summary>
    public void AddImmediate(byte value) { m_code.Add(item: 0xC6); m_code.Add(item: value); }
    /// <summary>cp n — compare A with an immediate, setting flags (carry when A &lt; n).</summary>
    public void CompareImmediate(byte value) { m_code.Add(item: 0xFE); m_code.Add(item: value); }
    /// <summary>and n — A &amp;= n (used to isolate a status bit, e.g. the camera's busy flag).</summary>
    public void AndImmediate(byte value) { m_code.Add(item: 0xE6); m_code.Add(item: value); }

    /// <summary>jr nz, label — relative branch taken when the zero flag is clear.</summary>
    public void JumpRelativeIfNotZero(int label) { m_code.Add(item: 0x20); EmitRelativeFixup(label: label); }
    /// <summary>jr c, label — relative branch taken when the carry flag is set (e.g. while LY &lt; 144).</summary>
    public void JumpRelativeIfCarry(int label) { m_code.Add(item: 0x38); EmitRelativeFixup(label: label); }
    /// <summary>jr nc, label — relative branch taken when the carry flag is clear (e.g. while LY &gt;= 144).</summary>
    public void JumpRelativeIfNoCarry(int label) { m_code.Add(item: 0x30); EmitRelativeFixup(label: label); }
    /// <summary>jr label — unconditional relative branch.</summary>
    public void JumpRelative(int label) { m_code.Add(item: 0x18); EmitRelativeFixup(label: label); }
    /// <summary>jp label — unconditional ABSOLUTE jump (3 bytes). Use for a long back-edge a relative <c>jr</c> cannot
    /// reach (over ±127 bytes); requires the routine's load address passed to <see cref="ToArray"/> so the label
    /// resolves to a real 16-bit address rather than a signed offset.</summary>
    public void JumpAbsolute(int label) {
        m_code.Add(item: 0xC3);
        m_absoluteFixups.Add(item: (m_code.Count, label));
        m_code.Add(item: 0x00); // low byte placeholder, patched in ToArray
        m_code.Add(item: 0x00); // high byte placeholder
    }

    /// <summary>Resolves the fixups and returns the finished machine code. <paramref name="baseAddress"/> is the address
    /// the routine will be LOADED at (0 for a position-independent routine); absolute jumps add it to the label offset.</summary>
    public byte[] ToArray(ushort baseAddress = 0) {
        foreach (var (patchOffset, label) in m_relativeFixups) {
            if (!m_labelOffsets.TryGetValue(key: label, value: out var target)) {
                throw new InvalidOperationException(message: $"jr targets an unbound label {label}.");
            }

            // A relative jump is measured from the address of the instruction AFTER the offset byte.
            var delta = (target - (patchOffset + 1));

            if ((delta < -128) || (delta > 127)) {
                throw new InvalidOperationException(message: $"jr delta {delta} is out of the signed-byte range; use JumpAbsolute.");
            }

            m_code[patchOffset] = (byte)(sbyte)delta;
        }

        foreach (var (patchOffset, label) in m_absoluteFixups) {
            if (!m_labelOffsets.TryGetValue(key: label, value: out var target)) {
                throw new InvalidOperationException(message: $"jp targets an unbound label {label}.");
            }

            var address = (baseAddress + target);

            m_code[patchOffset] = (byte)(address & 0xFF);
            m_code[patchOffset + 1] = (byte)((address >> 8) & 0xFF);
        }

        return m_code.ToArray();
    }

    private void EmitImmediate16(byte opcode, ushort value) {
        m_code.Add(item: opcode);
        m_code.Add(item: (byte)(value & 0xFF));
        m_code.Add(item: (byte)((value >> 8) & 0xFF));
    }

    private void EmitRelativeFixup(int label) {
        m_relativeFixups.Add(item: (m_code.Count, label));
        m_code.Add(item: 0x00); // Placeholder; patched in ToArray.
    }
}
