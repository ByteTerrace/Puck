namespace Puck.GameBoy;

/// <summary>
/// The OAM corruption erratum of the original monochrome hardware (DMG/MGB). Touching the OAM region
/// (<c>0xFE00</c>-<c>0xFEFF</c>) — or running the CPU's 16-bit increment/decrement unit over an address in that range —
/// while the PPU is scanning OAM (mode&#160;2) scrambles the row the PPU is currently reading. The color hardware (CGB)
/// is immune. The scrambles are model- and even instance-specific in silicon; these reproduce the common DMG-B
/// revision's behavior.
/// </summary>
public sealed partial class Ppu {
    private const int OamRowInvalid = 0xFF;
    // The highest OAM row (byte offset) the corruption can touch; rows past this would index beyond OAM.
    private const int OamRowLast = 0x98;
    // Dot offset mapping the current mode-2 dot to the OAM-scan row the corruption hits.
    private const int OamBugRowDotOffset = -4;

    // The OAM row (byte offset, a multiple of 8) the PPU is scanning this dot during mode 2 — the row the bug
    // corrupts — or 0xFF when the PPU is not scanning OAM.
    private int OamScanRow {
        get {
            if (!m_enabled || (m_mode != PpuMode.OamScan)) {
                return OamRowInvalid;
            }

            var index = ((m_dot + OamBugRowDotOffset) / 2);

            // Before the scan reaches its first object the accessed row is 0 (objects 0/1, which the bug never
            // touches), so the opening dots of mode 2 cannot corrupt; past the last object the scan is done.
            if ((index < 0) || (index > 39)) {
                return OamRowInvalid;
            }

            return (((index & ~0x01) * 4) + 8);
        }
    }

    /// <summary>Corrupts the scanned OAM row as a write (or the IDU write the bug ties to the address bus) would,
    /// if the PPU is mid OAM scan.</summary>
    public void OamBugWrite() {
        var row = OamScanRow;

        if ((row < 8) || (row > OamRowLast)) {
            return;
        }

        // The first word becomes glitch(this row's word 0, the preceding row's words 0 and 2); the rest of the row
        // is copied from the preceding row.
        SetOamWord(offset: row, value: GlitchWrite(a: OamWord(offset: row), b: OamWord(offset: (row - 8)), c: OamWord(offset: (row - 4))));
        CopyOamBytes(destination: (row + 2), source: (row - 6), count: 6);
    }

    /// <summary>Corrupts the scanned OAM row as a read would, if the PPU is mid OAM scan. Several rows have their own
    /// revision-specific scramble before the common copy from the preceding row.</summary>
    public void OamBugRead() {
        var row = OamScanRow;

        if ((row < 8) || (row > OamRowLast)) {
            return;
        }

        switch (row & 0x18) {
            case 0x10:
                ReadCorruptionSecondary(row: row);

                break;
            case 0x00:
                ReadCorruptionTertiary(row: row);

                break;
            default: {
                var word = GlitchRead(a: OamWord(offset: row), b: OamWord(offset: (row - 8)), c: OamWord(offset: (row - 4)));

                SetOamWord(offset: row, value: word);
                SetOamWord(offset: (row - 8), value: word);

                break;
            }
        }

        CopyOamBytes(destination: row, source: (row - 8), count: 8);

        if (row == 0x80) {
            CopyOamBytes(destination: 0, source: row, count: 8);
        }
    }

    private void ReadCorruptionSecondary(int row) {
        if (row >= 0x98) {
            return;
        }

        SetOamWord(
            offset: (row - 8),
            value: GlitchReadSecondary(a: OamWord(offset: (row - 16)), b: OamWord(offset: (row - 8)), c: OamWord(offset: row), d: OamWord(offset: (row - 4)))
        );
        CopyOamBytes(destination: (row - 0x10), source: (row - 0x08), count: 8);
    }

    private void ReadCorruptionTertiary(int row) {
        if (row >= 0x98) {
            return;
        }

        var a = OamWord(offset: row);
        var b = OamWord(offset: (row - 4));
        var c = OamWord(offset: (row - 8));
        var d = OamWord(offset: (row - 16));
        var e = OamWord(offset: (row - 32));

        var word = row switch {
            0x40 => GlitchQuaternaryRead(
                b: a,
                c: b,
                d: OamWord(offset: (row - 6)),
                e: c,
                f: OamWord(offset: (row - 14)),
                g: d,
                h: e
            ),
            0x20 => GlitchTertiaryRead2(a: a, b: b, c: c, d: d, e: e),
            0x60 => GlitchTertiaryRead3(a: a, b: b, c: c, d: d, e: e),
            _ => GlitchTertiaryRead1(a: a, b: b, c: c, d: d, e: e),
        };

        SetOamWord(offset: (row - 8), value: word);

        // Both the row two before and the currently scanned row take the (corrupted) preceding row's contents.
        for (var i = 0; i < 8; i += 1) {
            var value = m_objectAttributeMemory[(row - 0x08) + i];

            m_objectAttributeMemory[(row - 0x10) + i] = value;
            m_objectAttributeMemory[(row - 0x20) + i] = value;
        }
    }

    // The bitwise scrambles. Each operates on 16-bit OAM words. (a/b/c… name the operand rows; the quaternary form
    // ignores its leading operand on the DMG.)
    private static ushort GlitchWrite(ushort a, ushort b, ushort c) =>
        (ushort)(((a ^ c) & (b ^ c)) ^ c);
    private static ushort GlitchRead(ushort a, ushort b, ushort c) =>
        (ushort)(b | (a & c));
    private static ushort GlitchReadSecondary(ushort a, ushort b, ushort c, ushort d) =>
        (ushort)((b & (a | c | d)) | (a & c & d));
    private static ushort GlitchTertiaryRead1(ushort a, ushort b, ushort c, ushort d, ushort e) =>
        (ushort)(c | (a & b & d & e));
    private static ushort GlitchTertiaryRead2(ushort a, ushort b, ushort c, ushort d, ushort e) =>
        (ushort)((c & (a | b | d | e)) | (a & b & d & e));
    private static ushort GlitchTertiaryRead3(ushort a, ushort b, ushort c, ushort d, ushort e) =>
        (ushort)((c & (a | b | d | e)) | (b & d & e));
    private static ushort GlitchQuaternaryRead(ushort b, ushort c, ushort d, ushort e, ushort f, ushort g, ushort h) =>
        (ushort)((e & (h | g | ((ushort)~d & f) | c | b)) | (c & g & h));

    private ushort OamWord(int offset) =>
        (ushort)(m_objectAttributeMemory[offset] | (m_objectAttributeMemory[offset + 1] << 8));
    private void SetOamWord(int offset, ushort value) {
        m_objectAttributeMemory[offset] = (byte)value;
        m_objectAttributeMemory[offset + 1] = (byte)(value >> 8);
    }
    private void CopyOamBytes(int destination, int source, int count) {
        for (var i = 0; i < count; i += 1) {
            m_objectAttributeMemory[destination + i] = m_objectAttributeMemory[source + i];
        }
    }
}
