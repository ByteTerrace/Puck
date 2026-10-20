using Xunit;

namespace Puck.Platform.Windows.Tests;

// A Remote Desktop client's mouse is absolute: each report is where the client's own cursor is, and a pointer lock's
// warp reaches that cursor only when the server's pointer-position update arrives, some reports later. Nothing on a
// test machine sends such reports, so the client is modelled here and the window's lock policy reads its reports.
public sealed class Win32AbsolutePointerTests {
    private const int ClientHeight = 1080;
    private const int ClientLeft = 320;
    private const int ClientTop = 180;
    private const int ClientWidth = 1920;
    private const int ScreenHeight = 1440;
    private const int ScreenWidth = 2560;

    private static int Normalize(int pixels, int extent) => ((int)((((long)pixels) * 65535L) / extent));

    // Drives a drag of steady motion through the window's lock policy: each pump the client sends its reports, the
    // window sums their motion, and then recentres the cursor when the policy says so. The client follows a warp
    // after latencyReports further reports. Returns the motion the window read, in pixels.
    private static (double X, double Y) Drag(int pumps, int reportsPerPump, int stepX, int stepY, int latencyReports) {
        var pointer = new Win32AbsolutePointer();
        var centreX = (ClientLeft + (ClientWidth / 2));
        var centreY = (ClientTop + (ClientHeight / 2));
        var (clientX, clientY) = (centreX, centreY);
        var (serverX, serverY) = (centreX, centreY);
        var (readX, readY) = (0.0, 0.0);
        var pendingWarps = new Queue<(int Due, int X, int Y)>();
        var reports = 0;

        _ = pointer.Report(
            x: Normalize(extent: ScreenWidth, pixels: clientX),
            y: Normalize(extent: ScreenHeight, pixels: clientY)
        );

        for (var pump = 0; (pump < pumps); pump++) {
            for (var report = 0; (report < reportsPerPump); report++) {
                if (pendingWarps.TryPeek(result: out var warp) && (warp.Due <= reports)) {
                    _ = pendingWarps.Dequeue();
                    (clientX, clientY) = (warp.X, warp.Y);
                }

                clientX = Math.Clamp(value: (clientX + stepX), min: 0, max: (ScreenWidth - 1));
                clientY = Math.Clamp(value: (clientY + stepY), min: 0, max: (ScreenHeight - 1));
                reports++;

                var (motionX, motionY) = pointer.Report(
                    x: Normalize(extent: ScreenWidth, pixels: clientX),
                    y: Normalize(extent: ScreenHeight, pixels: clientY)
                );

                readX += ((motionX / 65535.0) * ScreenWidth);
                readY += ((motionY / 65535.0) * ScreenHeight);
                // The lock's ClipCursor holds the server's cursor inside the client area.
                serverX = Math.Clamp(value: clientX, min: ClientLeft, max: (ClientLeft + ClientWidth - 1));
                serverY = Math.Clamp(value: clientY, min: ClientTop, max: (ClientTop + ClientHeight - 1));
            }

            if (
                !pointer.WarpPending &&
                Win32AbsolutePointer.ShouldRecentre(
                    clientHeight: ClientHeight,
                    clientWidth: ClientWidth,
                    offsetX: (serverX - centreX),
                    offsetY: (serverY - centreY)
                )
            ) {
                (serverX, serverY) = (centreX, centreY);
                pointer.Warp(
                    x: Normalize(extent: ScreenWidth, pixels: centreX),
                    y: Normalize(extent: ScreenHeight, pixels: centreY)
                );
                pendingWarps.Enqueue(item: ((reports + latencyReports), centreX, centreY));
            }
        }

        return (readX, readY);
    }

    // A drag reads the client's whole motion, many screens of it, whether the client follows a warp within the pump or
    // several pumps later, and however fast it moves.
    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 4)]
    [InlineData(6, 4)]
    [InlineData(6, 40)]
    [InlineData(2, 120)]
    [InlineData(25, 20)]
    public void A_locked_drag_reads_every_pixel_the_remote_cursor_moves(int latencyReports, int step) {
        const int Pumps = 2000;
        const int ReportsPerPump = 2;
        var moved = ((double)(Pumps * ReportsPerPump * step));
        var (readX, readY) = Drag(
            latencyReports: latencyReports,
            pumps: Pumps,
            reportsPerPump: ReportsPerPump,
            stepX: step,
            stepY: -(step / 4)
        );

        Assert.True(condition: (moved > (ScreenWidth * 3)));
        Assert.InRange(actual: readX, high: (moved * 1.01), low: (moved * 0.99));
        Assert.InRange(actual: readY, high: (-(moved / 4) * 0.99), low: (-(moved / 4) * 1.01));
    }
    // A device that has not reported has no baseline for a warp to move, and a forgotten one starts afresh.
    [Fact]
    public void A_warp_needs_a_report_and_forgetting_starts_afresh() {
        var pointer = new Win32AbsolutePointer();

        pointer.Warp(x: 100, y: 100);
        Assert.False(condition: pointer.WarpPending);
        Assert.Equal(expected: (0, 0), actual: pointer.Report(x: 10, y: 10));
        Assert.Equal(expected: (5, -3), actual: pointer.Report(x: 15, y: 7));
        pointer.Warp(x: 1000, y: 1000);
        Assert.True(condition: pointer.WarpPending);
        Assert.Equal(expected: (2, 2), actual: pointer.Report(x: 17, y: 9));
        Assert.Equal(expected: (4, -1), actual: pointer.Report(x: 1004, y: 999));
        Assert.False(condition: pointer.WarpPending);
        pointer.Forget();
        Assert.False(condition: pointer.HasReported);
        Assert.Equal(expected: (0, 0), actual: pointer.Report(x: 50, y: 50));
    }
}
