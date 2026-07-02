using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The memory decode a DMA unit uses to read its source, on the DMA's own bus path rather than through the system bus —
/// so it is never subject to the access gating the CPU sees, and no dependency cycle forms. Shared by OAM DMA and Color
/// VRAM DMA. Echo and the high pages fold back into work RAM, as the hardware does for DMA sources.
/// </summary>
internal static class DmaSource {
    /// <summary>Reads one source byte for a DMA transfer.</summary>
    /// <param name="cartridgeSlot">The cartridge slot, a source when the address selects ROM or external RAM.</param>
    /// <param name="memory">The internal RAM, a source for VRAM, work RAM, and echo.</param>
    /// <param name="address">The source address.</param>
    /// <returns>The byte at that source address.</returns>
    public static byte Read(ICartridgeSlot cartridgeSlot, SystemMemory memory, ushort address) {
        if (address <= MemoryMap.RomBankNEnd) {
            return cartridgeSlot.Cartridge.ReadRom(address: address);
        }

        if (address <= MemoryMap.VideoRamEnd) {
            return memory.ReadVideoRam(address: address);
        }

        if (address <= MemoryMap.ExternalRamEnd) {
            return cartridgeSlot.Cartridge.ReadRam(address: address);
        }

        if (address <= MemoryMap.WorkRamBankNEnd) {
            return memory.ReadWorkRam(address: address);
        }

        return memory.ReadWorkRam(address: (ushort)(address - MemoryMap.EchoRamMirrorOffset));
    }
}
