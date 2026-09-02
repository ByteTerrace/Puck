using System.Numerics;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class Win32RawInputTests {
    [Fact]
    public void Keyboard_break_clears_the_modifier_before_the_next_make() {
        const ushort shift = 0x10;
        const ushort a = 0x41;
        var keyState = new byte[256];

        Win32RawInput.ApplyKeyTransition(isDown: true, keyState: keyState, virtualKey: shift);
        Win32RawInput.ApplyKeyTransition(isDown: false, keyState: keyState, virtualKey: shift);
        Win32RawInput.ApplyKeyTransition(isDown: true, keyState: keyState, virtualKey: a);

        Assert.Equal(expected: 0, actual: keyState[shift]);
        Assert.Equal(expected: 0x80, actual: keyState[a]);
    }
    [Fact]
    public void Absolute_pointer_without_virtual_desktop_flag_uses_primary_bounds() {
        var translated = Win32RawInput.TranslateAbsolutePointer(
            clientOrigin: new Vector2(x: 100f, y: 50f),
            primaryDesktop: new Win32DesktopBounds(Height: 1080, Left: 0, Top: 0, Width: 1920),
            rawX: 0,
            rawY: 0,
            usesVirtualDesktop: false,
            virtualDesktop: new Win32DesktopBounds(Height: 1640, Left: -1920, Top: -200, Width: 4480)
        );

        Assert.Equal(expected: new Vector2(x: -100f, y: -50f), actual: translated);
    }
    [Fact]
    public void Absolute_pointer_with_virtual_desktop_flag_uses_virtual_bounds() {
        var translated = Win32RawInput.TranslateAbsolutePointer(
            clientOrigin: new Vector2(x: 100f, y: 50f),
            primaryDesktop: new Win32DesktopBounds(Height: 1080, Left: 0, Top: 0, Width: 1920),
            rawX: 0,
            rawY: 0,
            usesVirtualDesktop: true,
            virtualDesktop: new Win32DesktopBounds(Height: 1640, Left: -1920, Top: -200, Width: 4480)
        );

        Assert.Equal(expected: new Vector2(x: -2020f, y: -250f), actual: translated);
    }
}
