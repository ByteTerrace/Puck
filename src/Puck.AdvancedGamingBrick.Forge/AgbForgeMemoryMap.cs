namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The framework's fixed EWRAM (0x02000000) state layout. Everything below <see cref="GameRam"/> belongs to the
/// polling kernel (the frame counter, the keypad edge pipeline, and the PRNG state); a game owns
/// <see cref="GameRam"/> upward and the framework never touches it. A fresh machine's EWRAM is zeroed, and the
/// kernel's boot prologue re-clears the framework region, so a game must initialize its own fields before use.
/// </summary>
public static class AgbForgeMemoryMap {
    /// <summary>The framework state base (equals <see cref="FrameCounter"/>; kernel routines address the other
    /// fields as halfword offsets from here).</summary>
    public const uint StateBase = 0x02000000u;
    /// <summary>The free-running 32-bit frame counter, incremented once per V-blank sync.</summary>
    public const uint FrameCounter = 0x02000000u;
    /// <summary>The active-high held-key halfword the game reads (KEYINPUT bit order).</summary>
    public const uint InputHeld = 0x02000004u;
    /// <summary>The newly-pressed edges this frame (<c>held &amp; ~previous</c>).</summary>
    public const uint InputPressed = 0x02000006u;
    /// <summary>Last frame's held halfword (the edge detector's memory).</summary>
    public const uint InputPrevious = 0x02000008u;
    /// <summary>The 16-bit LCG PRNG state.</summary>
    public const uint PrngState = 0x0200000Au;
    /// <summary>The current state id of the game state machine.</summary>
    public const uint GameState = 0x0200000Cu;
    /// <summary>The first game-owned EWRAM byte; the framework never touches this address or above.</summary>
    public const uint GameRam = 0x02000040u;
    /// <summary>The byte offset of <see cref="InputHeld"/> from <see cref="StateBase"/>.</summary>
    public const int InputHeldOffset = 0x04;
    /// <summary>The byte offset of <see cref="InputPressed"/> from <see cref="StateBase"/>.</summary>
    public const int InputPressedOffset = 0x06;
    /// <summary>The byte offset of <see cref="InputPrevious"/> from <see cref="StateBase"/>.</summary>
    public const int InputPreviousOffset = 0x08;
    /// <summary>The byte offset of <see cref="PrngState"/> from <see cref="StateBase"/>.</summary>
    public const int PrngStateOffset = 0x0A;
    /// <summary>The byte offset of <see cref="GameState"/> from <see cref="StateBase"/>.</summary>
    public const int GameStateOffset = 0x0C;
    /// <summary>The framework region's size in bytes (the boot prologue zero-fills exactly this much).</summary>
    public const int FrameworkByteCount = 0x40;
}
