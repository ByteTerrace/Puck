using System.Numerics;

namespace Puck.Platform.Windows;

/// <summary>The pixel bounds of one Windows desktop coordinate space.</summary>
/// <param name="Left">The screen-space x coordinate of the desktop's left edge.</param>
/// <param name="Top">The screen-space y coordinate of the desktop's top edge.</param>
/// <param name="Width">The desktop width in pixels.</param>
/// <param name="Height">The desktop height in pixels.</param>
public readonly record struct Win32DesktopBounds(int Left, int Top, int Width, int Height);

/// <summary>Pure state and coordinate operations shared by the Win32 Raw Input adapter and its tests.</summary>
public static class Win32RawInput {
    /// <summary>Applies one raw keyboard make or break transition to a <c>BYTE[256]</c>-compatible key-state table.</summary>
    /// <param name="keyState">The per-device virtual-key state table.</param>
    /// <param name="virtualKey">The Windows virtual-key index.</param>
    /// <param name="isDown"><see langword="true"/> for a make; <see langword="false"/> for a break.</param>
    public static void ApplyKeyTransition(Span<byte> keyState, ushort virtualKey, bool isDown) {
        if (virtualKey < keyState.Length) {
            keyState[virtualKey] = (isDown ? (byte)0x80 : (byte)0);
        }
    }
    /// <summary>Maps an absolute raw pointer report into client-relative pixels using its declared desktop space.</summary>
    /// <param name="rawX">The normalized x coordinate in the inclusive 0..65535 Raw Input range.</param>
    /// <param name="rawY">The normalized y coordinate in the inclusive 0..65535 Raw Input range.</param>
    /// <param name="usesVirtualDesktop">Whether the packet carries <c>MOUSE_VIRTUAL_DESKTOP</c>.</param>
    /// <param name="primaryDesktop">The primary desktop bounds used when the flag is absent.</param>
    /// <param name="virtualDesktop">The full virtual-desktop bounds used when the flag is present.</param>
    /// <param name="clientOrigin">The client area's origin in screen coordinates.</param>
    /// <returns>The client-relative pointer position.</returns>
    public static Vector2 TranslateAbsolutePointer(
        int rawX,
        int rawY,
        bool usesVirtualDesktop,
        Win32DesktopBounds primaryDesktop,
        Win32DesktopBounds virtualDesktop,
        Vector2 clientOrigin
    ) {
        var desktop = (usesVirtualDesktop ? virtualDesktop : primaryDesktop);
        var screenX = (desktop.Left + ((rawX / 65535f) * desktop.Width));
        var screenY = (desktop.Top + ((rawY / 65535f) * desktop.Height));

        return new Vector2(
            x: (screenX - clientOrigin.X),
            y: (screenY - clientOrigin.Y)
        );
    }
}
