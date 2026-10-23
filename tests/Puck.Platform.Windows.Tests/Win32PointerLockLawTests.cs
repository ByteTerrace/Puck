using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>Laws for the pointer lock's decisions and arithmetic: only a foreground window with a client area locks, the
/// relative cursor is held at the client area's centre.</summary>
public sealed class Win32PointerLockLawTests {
    [InlineData(true, 800, 600, true)]
    [InlineData(false, 800, 600, false)]
    [InlineData(true, 0, 0, false)]
    [InlineData(true, 800, 0, false)]
    [Theory]
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
}
