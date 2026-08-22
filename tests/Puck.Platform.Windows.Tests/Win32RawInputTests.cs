using System.Numerics;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class Win32RawInputTests {
    [Fact]
    public void Keyboard_break_clears_the_modifier_before_the_next_make() {
        const ushort shift = 0x10;
        const ushort a = 0x41;
        var keyState = new byte[256];

        Win32RawInput.ApplyKeyTransition(keyState: keyState, virtualKey: shift, isDown: true);
        Win32RawInput.ApplyKeyTransition(keyState: keyState, virtualKey: shift, isDown: false);
        Win32RawInput.ApplyKeyTransition(keyState: keyState, virtualKey: a, isDown: true);

        Assert.Equal(expected: 0, actual: keyState[shift]);
        Assert.Equal(expected: 0x80, actual: keyState[a]);
    }
    [Fact]
    public void Absolute_pointer_without_virtual_desktop_flag_uses_primary_bounds() {
        var translated = Win32RawInput.TranslateAbsolutePointer(
            clientOrigin: new Vector2(x: 100f, y: 50f),
            primaryDesktop: new Win32DesktopBounds(Left: 0, Top: 0, Width: 1920, Height: 1080),
            rawX: 0,
            rawY: 0,
            usesVirtualDesktop: false,
            virtualDesktop: new Win32DesktopBounds(Left: -1920, Top: -200, Width: 4480, Height: 1640)
        );

        Assert.Equal(expected: new Vector2(x: -100f, y: -50f), actual: translated);
    }
    [Fact]
    public void Absolute_pointer_with_virtual_desktop_flag_uses_virtual_bounds() {
        var translated = Win32RawInput.TranslateAbsolutePointer(
            clientOrigin: new Vector2(x: 100f, y: 50f),
            primaryDesktop: new Win32DesktopBounds(Left: 0, Top: 0, Width: 1920, Height: 1080),
            rawX: 0,
            rawY: 0,
            usesVirtualDesktop: true,
            virtualDesktop: new Win32DesktopBounds(Left: -1920, Top: -200, Width: 4480, Height: 1640)
        );

        Assert.Equal(expected: new Vector2(x: -2020f, y: -250f), actual: translated);
    }
}
