namespace Puck.HumbleGamingBrick;

/// <summary>
/// The coupled mode-3 pixel-pipeline timing knobs, isolated behind one injectable so they can be co-swept against the
/// SameBoy oracle without a rebuild (see the <c>Mode3Sweep</c> verification tool). The <see cref="Default"/> values
/// reproduce the shipped, oracle-tuned behavior bit-for-bit, so the standard machine composition is unchanged; only a
/// sweep harness registers a non-default instance. Like <see cref="MachineConfiguration"/> and the tick resolution, this
/// is immutable startup configuration — the PPU copies the knobs into its own fields, so the parameters are never
/// serialized.
/// </summary>
/// <remarks>
/// A joint grid sweep over the earlier knob set (delay 4–8 × flat/variable sprite stall × live/latched SCX sampling)
/// confirmed the pre-2026-07 defaults were that model's aggregate optimum; the sprite-stall knobs are gone now that the
/// object fetch is the oracle's real wait-then-six-dot machine (the penalty is emergent, not a constant). The knobs
/// below remain the turnkey seam for re-sweeping jointly whenever a coupled knob is added or the pipeline changes.
/// </remarks>
public sealed class PpuTimingParameters {
    /// <summary>The shipped default knobs — the mode-3 values remain that thread's joint-sweep optimum (entry latency 4
    /// dots, coarse-column phase 0, mode-0 edge at the hardware's 172 + SCX%8 dots), and the LY/LYC/STAT schedule values
    /// (line-event phase −1, OAM pulse −3, mode-0 IRQ/polled lags 2/2, polled mode-3 lag 4) are the <c>StatSweep</c>
    /// joint-grid optimum against the gambatte hardware verdicts (574/726 over the STAT-coupled families).</summary>
    public static PpuTimingParameters Default { get; } = new();

    /// <summary>The offset added to the pipeline's output position when the background fetcher derives its
    /// pixel-position-coupled coarse tile column, aligning our fetch dot to the oracle's
    /// <c>position_in_line</c> sample.</summary>
    public int CoarseColumnPhase { get; init; }
    /// <summary>The shift, in dots, applied to the whole per-line LY/LYC/STAT event schedule (the LY register write,
    /// the comparison gap and its close, the OAM interrupt pulse, the polled mode-2 edge, the vertical-blank entry
    /// group, and the line-153 handover) relative to the line boundary — the knob that aligns the corroborated event
    /// structure to our own access phase.</summary>
    public int LineEventPhase { get; init; } = -1;
    /// <summary>The additional shift, in dots, applied to the LY-comparison events only (the gap opening and its
    /// close, on every line kind) relative to the rest of the line event schedule — the LYC comparison's own clock
    /// runs ahead of the LY register's on hardware.</summary>
    public int LycEventPhase { get; init; }
    /// <summary>Dots the mode-0 STAT interrupt condition trails the internal mode-3→0 edge (the true edge still drives
    /// HDMA and the bus gates on time).</summary>
    public int Mode0IrqLag { get; init; } = 2;
    /// <summary>Dots the pixel pipeline idles between the mode-3 flip and the render loop engaging — the entry latency
    /// ahead of the structural lead-in (the junk-pixel pops that run while the first tile fetch completes). Shifts every
    /// fetch, emit, and the mode 3 / mode 0 boundary together within the fixed 456-dot line.</summary>
    public int Mode3PixelPipelineDelay { get; init; } = 4;
    /// <summary>The shift, in dots, applied to the OAM STAT interrupt pulse relative to its nominal slot in the line
    /// event schedule — the one interrupt condition the hardware runs ahead of the rest of the line-start group. A
    /// negative shift may push the pulse onto the tail of the previous line.</summary>
    public int OamPulseOffset { get; init; } = -3;
    /// <summary>Dots the POLLED mode-3→0 STAT edge trails the internal transition at single speed; double speed adds
    /// one more dot on top (the kevtris 173.5 half-cycle made observable at half-dot resolution).</summary>
    public int PolledMode0Lag { get; init; } = 2;
    /// <summary>Dots the POLLED mode-2→3 STAT edge trails the internal transition at the end of the OAM scan (the
    /// interrupt-side conditions are unaffected). Also moves the color-palette-RAM lock, which follows the polled
    /// mode.</summary>
    public int PolledMode3Lag { get; init; } = 4;
    /// <summary>The DOUBLE-speed length, in dots, of the window activation phase — the pipeline freeze between the WX
    /// match and the fetcher restart (the hardware window penalty beyond the restart's six fetch dots). Co-swept with
    /// the STAT mode-0 lags because the freeze lengthens mode 3 and shifts the mode-3→0 boundary on window lines.</summary>
    public int WindowActivationDotsDouble { get; init; } = 4;
    /// <summary>The SINGLE-speed base length, in dots, of the window activation phase; the commit is additionally
    /// stretched up to two dots to land in the second half of the machine's 4-dot grid. The same shared mode-3 boundary
    /// couples this to the STAT mode-0 lags, so it is co-swept with them rather than in isolation.</summary>
    public int WindowActivationDotsSingle { get; init; } = 6;
    /// <summary>The Color single-speed dot-in-line phase (mod 4) of the WY = LY comparator's sample grid (double speed
    /// adds one, DMG adds three). The window's per-frame WY latch arms only on a dot at this phase.</summary>
    public int WyCheckGridPhase { get; init; } = 3;
}
