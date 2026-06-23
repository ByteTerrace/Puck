namespace Puck.GameBoy;

/// <summary>Specifies which model of the SM83 system a <see cref="SystemBus"/> emulates. The model parameterizes the
/// hardware seams that differ between revisions — work-RAM and video-RAM bank counts, the palette
/// block, and the availability of double-speed and the CGB-only DMA/registers — so a single component
/// implementation serves every model and branches only where the silicon does.</summary>
public enum ConsoleModel {
    /// <summary>The original monochrome model (DMG): one 8&#160;KiB video-RAM bank, two 4&#160;KiB
    /// work-RAM banks, fixed monochrome palette registers, and no double-speed mode.</summary>
    Dmg = 0,
    /// <summary>The color model (CGB): two switchable video-RAM banks, eight switchable work-RAM banks,
    /// palette RAM, the general-purpose and HBlank VRAM DMA, and switchable double-speed.</summary>
    Cgb = 1,
}
