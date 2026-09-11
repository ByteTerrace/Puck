namespace Puck.GamingBricks.Forge;

/// <summary>
/// The capacities every target compiler must satisfy. Validation refuses a document against these numbers alone, so a
/// source that checks on one target compiles on the other; each backend carves its own memory map to fit them and
/// reports a capacity failure rather than truncating a ROM.
/// </summary>
/// <remarks>
/// KEEP IN SYNC with both backend state layouts: <c>FrameworkMemoryMap.GameRam</c> upward on CGB and
/// <c>AgbForgeMemoryMap.GameRam</c> upward on AGB. CGB is the binding constraint — its game-owned work RAM runs
/// 0xC200..0xCFFF, and the compiler's own held-input byte and index scratch come off the top of it.
/// </remarks>
public static class CartridgeLimits {
    /// <summary>The maximum number of named byte state slots.</summary>
    public const int VariableCount = 64;
    /// <summary>The maximum number of named arrays.</summary>
    public const int ArrayCount = 32;
    /// <summary>The maximum number of elements in one array; a byte index cannot address more.</summary>
    public const int ArrayLength = 256;
    /// <summary>The maximum total bytes across every array.</summary>
    public const int ArrayByteCount = 7168;
    /// <summary>The maximum number of rules.</summary>
    public const int RuleCount = 64;
    /// <summary>The maximum number of conditions in one rule.</summary>
    public const int ConditionCount = 8;
    /// <summary>The maximum number of steps anywhere in one rule's body tree.</summary>
    public const int StatementCount = 64;
    /// <summary>The maximum nesting depth of if and repeat steps within one rule body.</summary>
    public const int StatementDepth = 8;
    /// <summary>The maximum iteration count of one repeat; the index variable is a byte and must hold the terminal value.</summary>
    public const int RepeatCount = 255;
    /// <summary>The maximum number of named screens.</summary>
    public const int ScreenCount = 16;
    /// <summary>
    /// The maximum number of map writes one frame may execute. The background write queue drains in the vertical blank
    /// and holds exactly this many, so a document is refused rather than allowed to overflow it and lose a write.
    /// </summary>
    /// <remarks>KEEP IN SYNC with <c>FrameworkMemoryMap.VramQueueCapacity</c>.</remarks>
    public const int MapWriteCount = 24;
    /// <summary>
    /// The largest rectangle a blit may paint, in tiles. A blit repaints with the display off, and with the display off
    /// no vertical blank arrives, so the frame counter does not advance while it runs. Measured on the Color machine,
    /// a rectangle this size still holds frame cadence; larger ones degrade steeply and stop the document ticking
    /// entirely near 168 cells.
    /// </summary>
    public const int BlitCellCount = 120;
    /// <summary>
    /// The largest battery-backed payload, in bytes. The framework mirrors the block in work RAM before writing it
    /// through to the cartridge's save window, and that mirror is what bounds it.
    /// </summary>
    /// <remarks>KEEP IN SYNC with <c>FrameworkMemoryMap.SaveMirrorCapacity</c>.</remarks>
    public const int SaveByteCount = 72;
    /// <summary>The maximum number of named music tracks.</summary>
    public const int SoundCount = 8;
    /// <summary>The maximum number of background or object palettes on the humble machine.</summary>
    public const int HumblePaletteCount = 8;
    /// <summary>The maximum number of background or object palettes on the advanced machine.</summary>
    public const int AdvancedPaletteCount = 16;
    /// <summary>The maximum number of samples in one recorded one-shot.</summary>
    public const int SampleLength = 16384;
    /// <summary>The maximum number of recorded one-shots sounding at once.</summary>
    public const int SampleVoiceCount = 4;
    /// <summary>The fade amount that reaches the target colour completely.</summary>
    public const int FadeSteps = 16;
    /// <summary>The surfaces a blend step can make translucent, in the order the hardware numbers them.</summary>
    /// <remarks>
    /// KEEP IN SYNC with the target bits each backend writes; the index here is the bit position. "middle" and "far"
    /// are the two surfaces behind the document's own background — a turning background occupies "middle", and the
    /// declared layers fill them nearest first.
    /// </remarks>
    public static readonly string[] BlendSurfaces = ["background", "panel", "middle", "far", "sprites", "backdrop"];
    /// <summary>The maximum number of scrolling backgrounds beside the document's own.</summary>
    /// <remarks>A turning background occupies the nearer of the two, so declaring one leaves room for a single layer.</remarks>
    public const int LayerCount = 2;
    /// <summary>The blend weight that leaves the translucent surface fully opaque.</summary>
    public const int BlendWeights = 16;
    /// <summary>The maximum number of mid-picture scroll changes.</summary>
    /// <remarks>Each one interrupts the processor part way down the picture, so the count is deliberately small.</remarks>
    public const int RasterRowCount = 8;
    /// <summary>The maximum number of hardware sprites.</summary>
    public const int SpriteCount = 40;
    /// <summary>The maximum number of 8 by 8 tiles.</summary>
    public const int TileCount = 256;
}
