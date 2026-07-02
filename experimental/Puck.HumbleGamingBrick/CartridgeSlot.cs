using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The concrete cartridge slot. It holds the inserted cartridge behind one indirection and delegates snapshot save and
/// load to it, so swapping the cartridge is a single field change and the snapshot always captures whatever is
/// currently inserted. Swapping to a cartridge whose RAM differs in size invalidates snapshots taken against the
/// previous one, just as a save state from one game cannot be restored into another.
/// </summary>
public sealed class CartridgeSlot : ICartridgeSlot, ISnapshotable {
    private ICartridge m_cartridge;

    /// <summary>Creates a slot with an initial cartridge inserted.</summary>
    /// <param name="cartridge">The cartridge present at power-on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridge"/> is <see langword="null"/>.</exception>
    public CartridgeSlot(ICartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);

        m_cartridge = cartridge;
    }

    /// <inheritdoc/>
    public ICartridge Cartridge =>
        m_cartridge;

    /// <inheritdoc/>
    public void Insert(ICartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);

        m_cartridge = cartridge;
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        m_cartridge.SaveState(writer: writer);
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        m_cartridge.LoadState(reader: reader);
}
