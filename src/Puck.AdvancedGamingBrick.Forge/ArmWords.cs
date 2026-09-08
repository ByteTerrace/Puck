namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The few raw ARM (32-bit) instruction words the forge needs: the CPU wakes at the cartridge entry in ARM state,
/// so the header's entry branch and the two-instruction handoff into Thumb are ARM words; everything after them is
/// emitted through <see cref="ThumbEmitter"/>. Encodings are the standard ARM7TDMI ARM instruction set (see the
/// emulator's decoder, <c>src/Puck.AdvancedGamingBrick/Arm7Tdmi.Arm.cs</c>).
/// </summary>
public static class ArmWords {
    /// <summary><c>add r0, pc, #1</c> — the first half of the ARM→Thumb handoff: r0 receives the address of the
    /// instruction two words ahead (PC reads as the instruction address + 8) with bit 0 set, so the following
    /// <see cref="BranchExchangeR0"/> enters Thumb exactly where the ARM words end.</summary>
    public const uint AddR0PcOne = 0xE28F0001u;
    /// <summary><c>bx r0</c> — the second half of the handoff: branch to r0, switching to Thumb via its bit 0.</summary>
    public const uint BranchExchangeR0 = 0xE12FFF10u;

    /// <summary>Builds <c>b target</c> — an unconditional ARM branch from one word-aligned address to another
    /// (the header's jump over the logo/title block to the entry stub).</summary>
    /// <param name="fromAddress">The address of the branch instruction itself.</param>
    /// <param name="toAddress">The branch target.</param>
    /// <returns>The encoded instruction word.</returns>
    public static uint Branch(uint fromAddress, uint toAddress) {
        if ((((fromAddress | toAddress) & 3u) != 0u)) {
            throw new ArgumentException(message: "ARM branch source and target must be word-aligned.", paramName: nameof(toAddress));
        }

        // The 24-bit field is the signed word delta from PC (the branch address + 8).
        var delta = ((int)((toAddress - (fromAddress + 8u)) >> 2));

        if ((delta < -0x800000) || (delta > 0x7FFFFF)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(toAddress), message: "An ARM branch reaches ±32 MiB.");
        }

        return 0xEA000000u | (((uint)delta) & 0x00FFFFFFu);
    }
}
