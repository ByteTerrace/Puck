namespace Puck.HumbleGamingBrick;

/// <summary>
/// The coupled mode-3 pixel-pipeline timing knobs, isolated behind one injectable so they can be co-swept against the
/// hardware-accurate reference without a rebuild (see the <c>Mode3Sweep</c> verification tool). The <see cref="Default"/> values
/// reproduce the oracle-tuned behavior bit-for-bit; only a
/// sweep harness registers a non-default instance. Like <see cref="MachineConfiguration"/> and the tick resolution, this
/// is immutable startup configuration — the PPU copies the knobs into its own fields, so the parameters are never
/// serialized.
/// </summary>
/// <remarks>The object-fetch penalty emerges from the wait-then-six-dot state machine rather than a constant.
/// Re-sweep the remaining parameters jointly whenever the pipeline or a coupled parameter changes.</remarks>
public sealed class PpuTimingParameters {
    /// <summary>The default knobs use entry latency 4
    /// dots, coarse-column phase 0, mode-0 edge at the hardware's 172 + SCX%8 dots), and the LY/LYC/STAT schedule values
    /// (line-event phase −1, OAM pulse +1, mode-0 IRQ/polled lags 4/4, polled mode-3 lag 4) are constrained by the
    /// PPU-interrupt acceptance battery: its interrupt-timing and line/SCX-timing cases jointly pin the pulse and
    /// mode-0 lags (the 51/50/49-cycle SCX pattern uniquely selects the mode-0 interrupt firing at dot
    /// 256 + SCX%8), and its LCD-on cases pin the entry latency. See the Post README for the corpus.</summary>
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
    /// <summary>The additional shift, in dots, applied to the LY-comparison events only (the gap opening and its
    /// close, on every line kind) relative to the rest of the line event schedule — the LYC comparison's own clock
    /// runs ahead of the LY register's on hardware.</summary>
    public int LycEventPhase { get; init; }
    /// <summary>Dots the mode-0 STAT interrupt condition trails the internal mode-3→0 edge (the true edge still drives
    /// HDMA on time). The internal edge fires on the pop of the 160th pixel — dot 251 + SCX%8 on an unobstructed line,
    /// one dot ahead of the hardware's mode-3 latch — and the value 5 puts the interrupt at dot 256 + SCX%8, the only
    /// alignment that reproduces the hardware's 51/50/49-cycle interrupt-to-LY spacing across the SCX fine-scroll
    /// phases.</summary>
    public int Mode0IrqLag { get; init; } = 5;
    /// <summary>Dots the pixel pipeline idles between the mode-3 flip and the render loop engaging — the entry latency
    /// ahead of the structural lead-in (the junk-pixel pops that run while the first tile fetch completes). Shifts every
    /// fetch, emit, and the mode 3 / mode 0 boundary together within the fixed 456-dot line.</summary>
    public int Mode3PixelPipelineDelay { get; init; } = 4;
    /// <summary>The shift, in dots, applied to the OAM STAT interrupt pulse relative to its nominal slot in the line
    /// event schedule. The value +1 fires the pulse one dot after the LY register write (the hardware's
    /// one-T-cycle-before-STAT-shows-mode-2 quirk) and lets the pulse's tail overlap the dot the LY comparison becomes
    /// valid, so a coincidence that holds across the line boundary never sees the interrupt line dip — hardware's
    /// STAT-interrupt-blocking guarantee. A negative shift may push the pulse onto the tail of the previous line.</summary>
    public int OamPulseOffset { get; init; } = 1;
    /// <summary>Dots the POLLED mode-3→0 STAT edge trails the internal transition at single speed; double speed adds
    /// one more dot on top (a documented 173.5-dot half-cycle made observable at half-dot resolution). One dot tighter
    /// than the interrupt lag — the acceptance battery's sprite-timing case pins the polled edge while its
    /// line/SCX-timing case pins the interrupt, and they disagree by exactly one dot.</summary>
    public int PolledMode0Lag { get; init; } = 4;
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
