namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The 16&#160;KiB system BIOS image, behind a swappable seam. The emulator ships with an open-source
/// replacement BIOS as the default; a real dumped BIOS can be supplied later — needed for full accuracy, since
/// some software and test ROMs depend on exact BIOS behaviour and on open-bus reads of BIOS memory — by
/// registering an alternate implementation, with no other code change.
/// </summary>
public interface IBios {
    /// <summary>Gets the 16&#160;KiB BIOS image, mapped at the start of the address space.</summary>
    ReadOnlyMemory<byte> Image { get; }
}
