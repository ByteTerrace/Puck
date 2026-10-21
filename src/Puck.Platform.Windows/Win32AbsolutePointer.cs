namespace Puck.Platform.Windows;

/// <summary>
/// <para>One absolute-mode pointer (a Remote Desktop session's mouse, a virtual machine's, a tablet's) read as relative
/// motion, in the device's normalized 0..65535 coordinates: a report's motion is its difference from the report before
/// it.</para>
/// <para>Such a device reports where its own cursor is, and nothing the session does moves that cursor: a Remote
/// Desktop client answers a warp (<c>SetCursorPos</c>) by reporting from where its cursor was, so the session's cursor
/// jumps back. Its motion therefore ends where its cursor meets the edge of the client's screen, and a pointer lock
/// never warps the cursor while such a device drives it.</para>
/// </summary>
public sealed class Win32AbsolutePointer {
    private int? m_lastX;
    private int? m_lastY;

    /// <summary>Gets whether the device has reported since it was last forgotten.</summary>
    public bool HasReported => (m_lastX is not null);

    /// <summary>Forgets every report, so the next report starts afresh.</summary>
    public void Forget() {
        m_lastX = null;
        m_lastY = null;
    }
    /// <summary>Takes one absolute report and returns the motion since the last one.</summary>
    /// <param name="x">The report's normalized x.</param>
    /// <param name="y">The report's normalized y.</param>
    /// <returns>The motion, in normalized units; zero for the first report.</returns>
    public (int X, int Y) Report(int x, int y) {
        var motion = (((m_lastX is { } previousX) && (m_lastY is { } previousY))
            ? ((x - previousX), (y - previousY))
            : (0, 0));

        m_lastX = x;
        m_lastY = y;

        return motion;
    }
}
