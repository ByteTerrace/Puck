namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>The register offsets, bit masks and video constants the forge's kernel and verify drivers reference; the
/// bus addresses are <see cref="AgbAddresses"/>'s.</summary>
public static class AgbHw {
    /// <summary>The byte offset of <see cref="AgbAddresses.DisplayStatus"/> from <see cref="AgbAddresses.DisplayControl"/>.</summary>
    public const int DisplayStatusOffset = 0x04;
    /// <summary>The byte offset of HALTCNT from <see cref="AgbAddresses.PowerControl"/>: a byte write of zero halts the processor
    /// until an enabled interrupt is requested (IE &amp; IF), whatever IME holds.</summary>
    public const int HaltControlOffset = 0x01;
    /// <summary>The byte offset of IF, the interrupt request register, from <see cref="AgbAddresses.InterruptEnable"/>; writing a
    /// set bit acknowledges that request.</summary>
    public const int InterruptFlagsOffset = 0x02;
    /// <summary>The mask of KEYINPUT's ten key bits.</summary>
    public const ushort KeyMask = 0x03FF;
    /// <summary>DISPCNT value for mode 3 with BG2 enabled — the bitmap framebuffer the kernel draws into.</summary>
    public const ushort Mode3WithBg2 = 0x0403;
    /// <summary>The screen width in pixels.</summary>
    public const int ScreenWidth = 240;
    /// <summary>The V-blank bit of IE and IF.</summary>
    public const byte VBlankInterrupt = 0x01;
    /// <summary>DISPSTAT's V-blank request enable bit.</summary>
    public const byte VBlankStatusEnable = 0x08;
}
