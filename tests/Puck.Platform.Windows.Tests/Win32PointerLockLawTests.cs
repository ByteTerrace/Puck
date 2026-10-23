using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>Laws for the pointer lock's decisions and arithmetic: only a foreground window with a client area locks, the
/// cursor is held at the client area's centre, a screen point lands where an absolute-mode device reports it, and after
/// a warp each report is measured from the point the device last stood at.</summary>
public sealed class Win32PointerLockLawTests {
    [Theory]
    [InlineData(true, 800, 600, true)]
    [InlineData(false, 800, 600, false)]
    [InlineData(true, 0, 0, false)]
    [InlineData(true, 800, 0, false)]
    public void OnlyAForegroundWindowWithAClientAreaLocks(bool foreground, int width, int height, bool locks) =>
        Assert.Equal(
            actual: Win32PointerLock.CanLock(
                clientHeight: height,
                clientWidth: width,
                foreground: foreground
            ),
            expected: locks
        );
    [Fact]
    public void TheCursorIsHeldAtTheClientAreasCentre() =>
        Assert.Equal(
            actual: Win32PointerLock.Centre(
                clientHeight: 1369,
                clientWidth: 2560,
                originX: -1920,
                originY: 31
            ),
            expected: (-640, 715)
        );
    [Fact]
    public void AScreenPointLandsWhereAnAbsoluteDeviceReportsIt() {
        // The primary monitor's corners and centre, and a point on a virtual desktop that starts left of it.
        Assert.Equal(
            actual: Win32PointerLock.ToAbsolute(height: 1440, left: 0, screenX: 0, screenY: 0, top: 0, width: 2560),
            expected: (0, 0)
        );
        Assert.Equal(
            actual: Win32PointerLock.ToAbsolute(height: 1440, left: 0, screenX: 1280, screenY: 720, top: 0, width: 2560),
            expected: (32767, 32767)
        );
        Assert.Equal(
            actual: Win32PointerLock.ToAbsolute(height: 1440, left: -1920, screenX: 0, screenY: 1440, top: 0, width: 4480),
            expected: (28086, Win32PointerLock.AbsoluteExtent)
        );
        Assert.Null(@object: Win32PointerLock.ToAbsolute(height: 0, left: 0, screenX: 5, screenY: 5, top: 0, width: 2560));
    }
    [Fact]
    public void AfterAWarpEachReportIsMeasuredFromWhereTheDeviceLastStood() {
        var centre = (X: 32767, Y: 32767);
        var last = (X: 60000, Y: 32767);

        // A report from the device's old position, sent before it followed the warp, continues from its last report.
        Assert.Equal(
            actual: Win32PointerLock.Baseline(last: last, report: (60100, 32767), reportsSinceWarp: 0, warp: centre),
            expected: (last, false)
        );
        // The first report after the device followed the warp is measured from the warp's target, and the wait ends.
        Assert.Equal(
            actual: Win32PointerLock.Baseline(last: last, report: (32900, 32767), reportsSinceWarp: 1, warp: centre),
            expected: (centre, true)
        );
        // A device that never follows is taken not to once the limit's worth of reports has arrived.
        Assert.Equal(
            actual: Win32PointerLock.Baseline(last: last, report: (60100, 32767), reportsSinceWarp: (Win32PointerLock.WarpReportLimit - 1), warp: centre),
            expected: (last, true)
        );
    }
}
