namespace Puck.Commands;

/// <summary>
/// The selection-grace window of one open radial gesture: the sector a highlighted reading leaves behind, and how
/// long that sector survives a dead-centre dwell before the radial goes back to highlighting nothing.
/// </summary>
/// <remarks>
/// <para>This is the decision, separated from the presenter that feeds it. It matters because the sector it holds is
/// the sector a commit dispatches — a command, into the seat's deterministic lane — so the window has to be counted
/// on the engine's one monotonic tick base rather than a private wall clock the host cannot substitute. The type
/// therefore owns no clock at all: every call that needs the time is handed it, so a caller can drive a whole window
/// (and the tick counter's wrap) from a test without waiting for one.</para>
/// <para>The window length comes from <see cref="BindingWheelGeometry.SelectionGraceTicks"/>, converted once per
/// gesture. Zero disables the window entirely: a dead-centre reading drops the held sector on the very frame it
/// arrives, which is also the reading a presenter must use for its "is there a window at all" tests so the drawn
/// highlight and the armed commit never disagree.</para>
/// </remarks>
public sealed class BindingWheelGrace {
    // Valid only while m_dwelling — tick 0 is a real reading, so there is no in-band "no dwell yet" tick value.
    private ulong m_dwellSinceTick;
    private bool m_dwelling;
    private int m_sector = -1;
    private ulong m_ticks;

    /// <summary>Gets whether a dead-centre dwell is currently being counted — <see langword="true"/> from the first
    /// dead-centre reading that finds a held sector until the next reading that highlights one (or drops it).</summary>
    public bool IsDwelling => m_dwelling;
    /// <summary>Gets the sector the window is holding, or <c>-1</c> when it holds none.</summary>
    public int Sector => m_sector;
    /// <summary>Gets the window's length in engine ticks; <c>0</c> is no window at all.</summary>
    public ulong Ticks => m_ticks;

    /// <summary>Begins a fresh gesture: no held sector, no dwell in progress, and the authored window converted to
    /// whole engine ticks once, here, so the conversion never runs inside a per-frame decision.</summary>
    /// <param name="graceTicks">The window's length in engine ticks — see
    /// <see cref="BindingWheelGeometry.SelectionGraceTicks"/>. Zero disables the window.</param>
    public void BeginGesture(ulong graceTicks) {
        m_dwellSinceTick = 0UL;
        m_dwelling = false;
        m_sector = -1;
        m_ticks = graceTicks;
    }
    /// <summary>Folds one frame's reading into the window and answers the sector the frame should actually present
    /// and arm.</summary>
    /// <param name="hoverSector">The sector this frame's live reading highlights, or a negative value when it
    /// highlights none.</param>
    /// <param name="deadCentre">Whether a negative <paramref name="hoverSector"/> is the selector sitting in the
    /// authored hub — the one non-selecting reading the window exists to survive. Any other reason for no selection
    /// (cancelled, outside a bounded selector, no selector sample at all) drops the held sector immediately.</param>
    /// <param name="nowTick">The engine tick this frame is being decided on, from the host's monotonic clock.</param>
    /// <returns>The sector to present, or <c>-1</c> for none. A held sector answers here for the whole window and
    /// stops answering on the first frame past it.</returns>
    public int Observe(int hoverSector, bool deadCentre, ulong nowTick) {
        if (hoverSector >= 0) {
            m_dwelling = false;
            m_sector = hoverSector;

            return hoverSector;
        }

        if (
            !deadCentre ||
            (m_sector < 0) ||
            (m_ticks == 0UL)
        ) {
            m_sector = -1;

            return -1;
        }

        if (!m_dwelling) {
            m_dwellSinceTick = nowTick;
            m_dwelling = true;
        }

        // Monotonic clock, so the difference never underflows — and because it is taken in unsigned arithmetic, a
        // dwell that spans the tick counter's wrap measures its true length rather than an enormous one.
        if ((nowTick - m_dwellSinceTick) <= m_ticks) {
            return m_sector;
        }

        m_sector = -1;

        return -1;
    }
    /// <summary>Seeds the held sector from a reading taken while the selector is already at dead centre — the
    /// gesture that opens on a flick which has returned to neutral before the first presentation frame. Does
    /// nothing once a sector is held, once a dwell is under way, or when there is no window to hold it for.</summary>
    /// <param name="sector">The sector the neutral reading still resolves to; a negative value seeds nothing.</param>
    /// <returns><see langword="true"/> if <paramref name="sector"/> became the held sector.</returns>
    public bool TrySeed(int sector) {
        if (
            (sector < 0) ||
            (m_sector >= 0) ||
            m_dwelling ||
            (m_ticks == 0UL)
        ) {
            return false;
        }

        m_sector = sector;

        return true;
    }
}
