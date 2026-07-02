namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The physical cartridge slot: the one place that holds whichever cartridge is currently inserted. The bus reads the
/// cartridge through the slot rather than binding a fixed reference, so the host can pull one cartridge and push
/// another between steps — the emulated equivalent of swapping carts on a paused console. The slot is the snapshot
/// owner for the cartridge, so a snapshot captures the inserted cartridge's RAM and registers.
/// </summary>
public interface ICartridgeSlot {
    /// <summary>Gets the currently inserted cartridge.</summary>
    ICartridge Cartridge { get; }

    /// <summary>Inserts a cartridge, replacing whatever was present.</summary>
    /// <param name="cartridge">The cartridge to insert.</param>
    void Insert(ICartridge cartridge);
}
