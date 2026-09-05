namespace Puck.HumbleGamingBrick;

/// <summary>
/// The coupled mode-3 pixel-pipeline timing knobs, isolated behind one injectable so they can be co-swept against the
/// hardware-accurate reference without a rebuild. The <see cref="Default"/> values
/// reproduce the oracle-tuned behavior bit-for-bit; only a
/// sweep harness registers a non-default instance. Like <see cref="MachineConfiguration"/> and the tick resolution, this
/// is immutable startup configuration — the PPU copies the knobs into its own fields, so the parameters are never
/// serialized.
/// </summary>
/// <remarks>The object-fetch penalty and the window hand-over penalty both emerge from the pipeline — the
/// wait-then-six-dot object state machine, and the background FIFO drop and fetcher rewind on a WX match — rather than
/// from a constant. Re-sweep the remaining parameters jointly whenever the pipeline or a coupled parameter
/// changes.</remarks>
public sealed class PpuTimingParameters {
    /// <summary>The defaults place a visible line's schedule at these dots from the line boundary: LY register and OAM
    /// interrupt pulse 2, polled mode 2 at 3, polled mode 3 and the memory locks at 83, the pixel loop at 88, screen
    /// column 0 at 96 + SCX%8, the mode-3→0 edge with the polled STAT flip and the video-RAM unlocks at 255 + SCX%8,
    /// and the mode-0 interrupt at 256 + SCX%8. The PPU-interrupt acceptance battery pins the spacings between them
    /// (its 51/50/49-cycle SCX pattern selects the mode-0 interrupt dot) and its LCD-on cases pin the first line after
    /// an enable. See the Post README for the corpus.</summary>
    public static PpuTimingParameters Default { get; } = new();
    /// <summary>The offset added to the pipeline's output position when the background fetcher derives its
    /// pixel-position-coupled coarse tile column, aligning our fetch dot to the hardware-accurate reference's
    /// per-dot line-position sample.</summary>
    public int CoarseColumnPhase { get; init; }
    /// <summary>The shift, in dots, applied to the whole per-line LY/LYC/STAT event schedule (the LY register write,
    /// the comparison gap and its close, the OAM interrupt pulse, the polled mode-2 edge, the vertical-blank entry
    /// group, and the line-153 handover) relative to the line boundary — the knob that aligns the corroborated event
    /// structure to our own access phase.</summary>
    public int LineEventPhase { get; init; } = -1;
    /// <summary>The additional shift, in dots, applied to the register file's own view only — the LY register, the
    /// polled STAT mode bits, and the CPU-facing memory locks — relative to the interrupt logic, which samples the same
    /// edges a dot sooner. The gap is where the CPU latches an I/O read inside its access: the bus settles on the
    /// access's third T-cycle (<c>Sm83</c>'s read dot-phase), while the interrupt line is sampled at the instruction
    /// boundary, so a poll and an interrupt taken at the same edge disagree by one dot.</summary>
    public int PolledEventPhase { get; init; }
    /// <summary>The additional shift, in dots, applied to the LY-comparison events only (the gap opening and its
    /// close, on every line kind) relative to the rest of the line event schedule — the LYC comparison's own clock
    /// runs ahead of the LY register's on hardware.</summary>
    public int LycEventPhase { get; init; }
    /// <summary>Dots the mode-0 STAT interrupt condition trails the internal mode-3→0 edge (the true edge still drives
    /// HDMA on time). The internal edge fires on the pop of the 160th pixel — dot 256 + SCX%8 on an unobstructed line —
    /// and the interrupt lands one dot behind it, the dot the hardware re-evaluates the STAT line on after clearing the
    /// mode bits.</summary>
    public int Mode0IrqLag { get; init; } = 1;
    /// <summary>Dots the pixel pipeline idles between the internal mode-3 flip at dot 80 and the render loop engaging,
    /// so the first pop lands at dot 88 and the first visible pixel at dot 96 + SCX%8. Splits into the four dots before
    /// the memory locks and the polled mode-3 flip (see <see cref="PolledMode3Lag"/>) and the five after them. Shifts
    /// every fetch, emit, and the mode 3 / mode 0 boundary together within the fixed 456-dot line.</summary>
    public int Mode3PixelPipelineDelay { get; init; } = 8;
    /// <summary>The shift, in dots, applied to the OAM STAT interrupt pulse relative to its nominal slot in the line
    /// event schedule. Zero fires the pulse on the LY register write, one dot before STAT shows mode 2 — the hardware's
    /// one-T-cycle-before-STAT-shows-mode-2 quirk — and lets the pulse's tail overlap the dot the LY comparison becomes
    /// valid, so a coincidence that holds across the line boundary never sees the interrupt line dip — hardware's
    /// STAT-interrupt-blocking guarantee. A negative shift may push the pulse onto the tail of the previous line.</summary>
    public int OamPulseOffset { get; init; }
    /// <summary>Dots the polled mode-3→0 STAT edge trails the internal transition at single speed; double speed adds
    /// one more dot on top (a documented 173.5-dot half-cycle made observable at half-dot resolution). The mode bits
    /// clear on the pop of the 160th pixel itself, so the single-speed lag is zero and the interrupt trails it by
    /// <see cref="Mode0IrqLag"/>.</summary>
    public int PolledMode0Lag { get; init; }
    /// <summary>Dots the polled mode-2→3 STAT edge trails the internal transition at the end of the OAM scan (the
    /// interrupt-side conditions are unaffected). Also moves the color-palette-RAM lock, which follows the polled
    /// mode.</summary>
    public int PolledMode3Lag { get; init; } = 3;
    /// <summary>The Color single-speed dot-in-line phase (mod 4) of the WY = LY comparator's sample grid (double speed
    /// adds one, DMG adds three). The window's per-frame WY latch arms only on a dot at this phase.</summary>
    public int WyCheckGridPhase { get; init; } = 3;
}
