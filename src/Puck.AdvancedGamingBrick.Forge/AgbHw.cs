namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>The hardware bus addresses and video constants the forge's kernel and verify drivers reference.</summary>
public static class AgbHw {
    /// <summary>DISPCNT — the display control register (halfword).</summary>
    public const uint DisplayControl = 0x04000000u;
    /// <summary>DISPSTAT — the display status register (halfword; bit 0 is the V-blank flag).</summary>
    public const uint DisplayStatus = 0x04000004u;
    /// <summary>The byte offset of <see cref="DisplayStatus"/> from <see cref="DisplayControl"/>.</summary>
    public const int DisplayStatusOffset = 0x04;
    /// <summary>The byte offset of HALTCNT from <see cref="PowerControl"/>: a byte write of zero halts the processor
    /// until an enabled interrupt is requested (IE &amp; IF), whatever IME holds.</summary>
    public const int HaltControlOffset = 0x01;
    /// <summary>IE — the interrupt enable register (halfword).</summary>
    public const uint InterruptEnable = 0x04000200u;
    /// <summary>The byte offset of IF, the interrupt request register, from <see cref="InterruptEnable"/>; writing a
    /// set bit acknowledges that request.</summary>
    public const int InterruptFlagsOffset = 0x02;
    /// <summary>KEYINPUT — the keypad register (halfword, active-low: a clear bit is a pressed key).</summary>
    public const uint KeyInput = 0x04000130u;
    /// <summary>The mask of KEYINPUT's ten key bits.</summary>
    public const ushort KeyMask = 0x03FF;
    /// <summary>DISPCNT value for mode 3 with BG2 enabled — the bitmap framebuffer the kernel draws into.</summary>
    public const ushort Mode3WithBg2 = 0x0403;
    /// <summary>POSTFLG — the power-control byte pair whose upper byte is HALTCNT.</summary>
    public const uint PowerControl = 0x04000300u;
    /// <summary>The screen width in pixels.</summary>
    public const int ScreenWidth = 240;
    /// <summary>The V-blank bit of IE and IF.</summary>
    public const byte VBlankInterrupt = 0x01;
    /// <summary>DISPSTAT's V-blank request enable bit.</summary>
    public const byte VBlankStatusEnable = 0x08;
    /// <summary>The video RAM base (in mode 3 the 240×160 BGR555 framebuffer starts here).</summary>
    public const uint VideoRam = 0x06000000u;
}
