namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>The hardware bus addresses and video constants the forge's kernel and verify drivers reference.</summary>
public static class AgbHw {
    /// <summary>The I/O register base (DISPCNT lives at offset 0).</summary>
    public const uint IoBase = 0x04000000u;
    /// <summary>DISPCNT — the display control register (halfword).</summary>
    public const uint DisplayControl = 0x04000000u;
    /// <summary>DISPSTAT — the display status register (halfword; bit 0 is the V-blank flag).</summary>
    public const uint DisplayStatus = 0x04000004u;
    /// <summary>VCOUNT — the current scanline (halfword; 0..227, visible lines are 0..159).</summary>
    public const uint VerticalCounter = 0x04000006u;
    /// <summary>KEYINPUT — the keypad register (halfword, active-low: a clear bit is a pressed key).</summary>
    public const uint KeyInput = 0x04000130u;
    /// <summary>The byte offset of <see cref="VerticalCounter"/> from <see cref="IoBase"/>.</summary>
    public const int VerticalCounterOffset = 0x06;
    /// <summary>The video RAM base (in mode 3 the 240×160 BGR555 framebuffer starts here).</summary>
    public const uint VideoRam = 0x06000000u;
    /// <summary>The screen width in pixels.</summary>
    public const int ScreenWidth = 240;
    /// <summary>The screen height in pixels.</summary>
    public const int ScreenHeight = 160;
    /// <summary>DISPCNT value for mode 3 with BG2 enabled — the bitmap framebuffer the kernel draws into.</summary>
    public const ushort Mode3WithBg2 = 0x0403;
    /// <summary>The mask of KEYINPUT's ten key bits.</summary>
    public const ushort KeyMask = 0x03FF;
}
