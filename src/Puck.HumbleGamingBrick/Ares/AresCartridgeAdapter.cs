using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Ares;

/// <summary>
/// Adapts an existing <see cref="ICartridge"/> (its MBC banking and RAM logic) to the cycle-addressed bus. The
/// cartridge owns the ROM range (0x0000-0x7FFF, where writes are MBC register writes) and cartridge RAM
/// (0xA000-0xBFFF); both land at bus sub-cycle 2.
/// </summary>
public sealed class AresCartridgeAdapter : IAresIo {
    private readonly ICartridge m_cartridge;

    /// <summary>Wraps the given cartridge.</summary>
    /// <param name="cartridge">The mapper-backed cartridge to expose on the bus.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridge"/> is <see langword="null"/>.</exception>
    public AresCartridgeAdapter(ICartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);

        m_cartridge = cartridge;
    }

    /// <inheritdoc/>
    public byte ReadIo(int cycle, ushort address, byte data) {
        if (cycle != 2) {
            return data;
        }

        if (address <= 0x7FFF) {
            return m_cartridge.ReadRom(address: address);
        }

        if ((address >= 0xA000) && (address <= 0xBFFF)) {
            return m_cartridge.ReadRam(address: address);
        }

        return data;
    }

    /// <inheritdoc/>
    public void WriteIo(int cycle, ushort address, byte data) {
        if (cycle != 2) {
            return;
        }

        if (address <= 0x7FFF) {
            m_cartridge.WriteRom(address: address, value: data);

            return;
        }

        if ((address >= 0xA000) && (address <= 0xBFFF)) {
            m_cartridge.WriteRam(address: address, value: data);
        }
    }
}
