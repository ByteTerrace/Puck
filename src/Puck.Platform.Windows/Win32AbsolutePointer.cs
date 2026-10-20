namespace Puck.Platform.Windows;

/// <summary>
/// <para>One absolute-mode pointer (a Remote Desktop session's mouse, a virtual machine's, a tablet's) read as relative
/// motion, in the device's normalized 0..65535 coordinates, across the warps a pointer lock makes. Between warps a
/// report's motion is its difference from the report before it.</para>
/// <para>A warp moves the cursor such a device reports from, but late: a Remote Desktop client moves its own cursor to
/// the warp's target when the server's pointer-position update reaches it, so reports sent before that still come from
/// where its cursor was. The first report it sends from the target is the first measured from the target, and it is
/// told apart by distance: it lies nearer the target than the report before it does. That test holds only when the
/// warp jumped much further than the device moves between two reports: a warp of one pump's motion sits about as far
/// from the next report as the report before it does, and steady motion measured against the wrong one sums to almost
/// nothing. <see cref="ShouldRecentre"/> therefore recentres only once the cursor has wandered a quarter of the client
/// away from the centre, and the window never recentres while a warp is still pending.</para>
/// </summary>
public sealed class Win32AbsolutePointer {
    private int? m_lastX;
    private int? m_lastY;
    private int? m_warpX;
    private int? m_warpY;

    /// <summary>Gets whether the device has reported since it was last forgotten.</summary>
    public bool HasReported => (m_lastX is not null);
    /// <summary>Gets whether a warp waits for the device's first report from its target.</summary>
    public bool WarpPending => (m_warpX is not null);

    private static long DistanceSquared(long x, long y) => ((x * x) + (y * y));

    /// <summary>Forgets every report and any pending warp, so the next report starts afresh.</summary>
    public void Forget() {
        m_lastX = null;
        m_lastY = null;
        m_warpX = null;
        m_warpY = null;
    }
    /// <summary>Takes one absolute report and returns the motion since the last one, measured from a pending warp's
    /// target when this is the first report the device sent from there.</summary>
    /// <param name="x">The report's normalized x.</param>
    /// <param name="y">The report's normalized y.</param>
    /// <returns>The motion, in normalized units; zero for the first report.</returns>
    public (int X, int Y) Report(int x, int y) {
        if (
            (m_lastX is not { } previousX) ||
            (m_lastY is not { } previousY)
        ) {
            m_lastX = x;
            m_lastY = y;

            return (0, 0);
        }

        if (
            (m_warpX is { } warpX) &&
            (m_warpY is { } warpY) &&
            (DistanceSquared(x: (x - warpX), y: (y - warpY)) < DistanceSquared(x: (x - previousX), y: (y - previousY)))
        ) {
            previousX = warpX;
            previousY = warpY;
            m_warpX = null;
            m_warpY = null;
        }

        m_lastX = x;
        m_lastY = y;

        return ((x - previousX), (y - previousY));
    }
    /// <summary>Records a warp of the cursor this device reports from, in its normalized coordinates. A device that has
    /// not reported yet has no baseline to move and takes none.</summary>
    /// <param name="x">The target's normalized x.</param>
    /// <param name="y">The target's normalized y.</param>
    public void Warp(int x, int y) {
        if (!HasReported) {
            return;
        }

        m_warpX = x;
        m_warpY = y;
    }
    /// <summary>Answers whether a locked pointer this far from the client area's centre is moved back to it: once it is
    /// a quarter of the client's width or height away, so each warp jumps far further than the device moves between
    /// two reports.</summary>
    /// <param name="offsetX">The cursor's offset from the centre, pixels.</param>
    /// <param name="offsetY">The cursor's offset from the centre, pixels.</param>
    /// <param name="clientWidth">The client area's width, pixels.</param>
    /// <param name="clientHeight">The client area's height, pixels.</param>
    /// <returns><see langword="true"/> when the cursor goes back to the centre.</returns>
    public static bool ShouldRecentre(int offsetX, int offsetY, int clientWidth, int clientHeight) => (
        ((((long)Math.Abs(value: offsetX)) * 4) >= clientWidth) ||
        ((((long)Math.Abs(value: offsetY)) * 4) >= clientHeight)
    );
}
