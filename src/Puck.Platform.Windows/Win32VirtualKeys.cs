using Puck.Input;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

/// <summary>The one table between Windows virtual-key codes and <see cref="KeyCode"/>, read in both directions: the
/// native window turns a key message into a key (<see cref="TryKeyOf"/>), and a passthrough window turns a key back into
/// the message it posts (<see cref="TryVirtualKeyOf"/>).</summary>
public static class Win32VirtualKeys {
    internal const int Vk0 = 0x30;
    internal const int Vk9 = 0x39;
    internal const int VkA = 0x41;
    internal const int VkAdd = 0x6B;
    internal const int VkBack = 0x08;
    internal const int VkC = 0x43;
    internal const int VkCapital = 0x14;
    internal const int VkControl = 0x11;
    internal const int VkDown = 0x28;
    internal const int VkEscape = 0x1B;
    internal const int VkF1 = 0x70;
    internal const int VkF10 = 0x79;
    internal const int VkF11 = 0x7A;
    internal const int VkF12 = 0x7B;
    internal const int VkF2 = 0x71;
    internal const int VkF3 = 0x72;
    internal const int VkF4 = 0x73;
    internal const int VkF5 = 0x74;
    internal const int VkF6 = 0x75;
    internal const int VkF7 = 0x76;
    internal const int VkF8 = 0x77;
    internal const int VkF9 = 0x78;
    internal const int VkLShift = 0xA0;
    internal const int VkLWin = 0x5B;
    internal const int VkLeft = 0x25;
    internal const int VkMenu = 0x12;
    internal const int VkNumLock = 0x90;
    internal const int VkNumpad0 = 0x60;
    internal const int VkNumpad9 = 0x69;
    internal const int VkOem3 = 0xC0;
    internal const int VkOemMinus = 0xBD;
    internal const int VkOemPlus = 0xBB;
    internal const int VkRShift = 0xA1;
    internal const int VkRWin = 0x5C;
    internal const int VkReturn = 0x0D;
    internal const int VkRight = 0x27;
    internal const int VkScroll = 0x91;
    internal const int VkShift = 0x10;
    internal const int VkSpace = 0x20;
    internal const int VkSubtract = 0x6D;
    internal const int VkTab = 0x09;
    internal const int VkUp = 0x26;
    internal const int VkV = 0x56;
    internal const int VkZ = 0x5A;

    // MapVirtualKey map types: a virtual key to its scan code, and a scan code to the LEFT/RIGHT-distinguishing
    // virtual key, the only way to tell VK_LSHIFT from VK_RSHIFT, since Shift's extended-key bit is never set for
    // either side.
    private const uint MapvkVkToVsc = 0x00;
    private const uint MapvkVscToVkEx = 0x03;

    /// <summary>Returns the letter a letter key's virtual-key code names; the code lies in <c>VK_A</c> to
    /// <c>VK_Z</c>.</summary>
    /// <param name="virtualKey">The virtual-key code.</param>
    /// <returns>The lowercase letter.</returns>
    public static char LetterOf(long virtualKey) => ((char)('a' + (virtualKey - VkA)));
    /// <summary>Returns whether pressing a key types text, which a window hears as a text event rather than only as a
    /// key: a letter, a digit, space, minus, equals, the backtick, a numpad digit, and numpad add and subtract.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> for a key whose press types text.</returns>
    public static bool Types(KeyCode key) => (
        (key is KeyCode.Letter or KeyCode.Space or KeyCode.Minus or KeyCode.Equals or KeyCode.Backtick or KeyCode.NumpadAdd or KeyCode.NumpadSubtract) ||
        (key is >= KeyCode.Digit0 and <= KeyCode.Digit9) ||
        (key is >= KeyCode.Numpad0 and <= KeyCode.Numpad9)
    );
    /// <summary>Reads a named key from a key message's virtual-key code, extended-key bit and scan code. A side-sensitive
    /// modifier takes its side from the extended-key bit (Control, Alt) or the scan code (Shift). Letters are not named
    /// keys; <see cref="LetterOf"/> reads them.</summary>
    /// <param name="virtualKey">The virtual-key code.</param>
    /// <param name="isExtended">Whether the message's extended-key bit is set.</param>
    /// <param name="scanCode">The message's scan code.</param>
    /// <param name="key">The key when this returns <see langword="true"/>; <see cref="KeyCode.None"/> otherwise.</param>
    /// <returns><see langword="true"/> when the code names a key.</returns>
    public static bool TryKeyOf(long virtualKey, bool isExtended, byte scanCode, out KeyCode key) {
        if (virtualKey is >= Vk0 and <= Vk9) {
            key = ((KeyCode)(((int)KeyCode.Digit0) + (virtualKey - Vk0)));
            return true;
        }

        if (virtualKey is >= VkNumpad0 and <= VkNumpad9) {
            key = ((KeyCode)(((int)KeyCode.Numpad0) + (virtualKey - VkNumpad0)));
            return true;
        }

        key = virtualKey switch {
            VkOem3 => KeyCode.Backtick,
            VkBack => KeyCode.Backspace,
            VkEscape => KeyCode.Escape,
            VkReturn => KeyCode.Enter,
            VkTab => KeyCode.Tab,
            VkUp => KeyCode.ArrowUp,
            VkDown => KeyCode.ArrowDown,
            VkLeft => KeyCode.ArrowLeft,
            VkRight => KeyCode.ArrowRight,
            VkSpace => KeyCode.Space,
            VkOemMinus => KeyCode.Minus,
            VkOemPlus => KeyCode.Equals,
            VkSubtract => KeyCode.NumpadSubtract,
            VkAdd => KeyCode.NumpadAdd,
            VkF1 => KeyCode.F1,
            VkF2 => KeyCode.F2,
            VkF3 => KeyCode.F3,
            VkF4 => KeyCode.F4,
            VkF5 => KeyCode.F5,
            VkF6 => KeyCode.F6,
            VkF7 => KeyCode.F7,
            VkF8 => KeyCode.F8,
            VkF9 => KeyCode.F9,
            VkF10 => KeyCode.F10,
            VkF11 => KeyCode.F11,
            VkF12 => KeyCode.F12,
            VkControl => ((isExtended)
            ? KeyCode.ControlRight
            : KeyCode.ControlLeft),
            VkMenu => ((isExtended)
            ? KeyCode.AltRight
            : KeyCode.AltLeft),
            VkShift => ((User32.MapVirtualKey(
                code: scanCode,
                mapType: MapvkVscToVkEx
            ) == VkRShift)
                ? KeyCode.ShiftRight
                : KeyCode.ShiftLeft),
            VkLWin => KeyCode.SuperLeft,
            VkRWin => KeyCode.SuperRight,
            _ => KeyCode.None,
        };

        return (key != KeyCode.None);
    }
    /// <summary>Finds the key message that carries a key: its virtual-key code, scan code and extended-key bit, the
    /// inverse of <see cref="TryKeyOf"/> and, for a letter, of <see cref="LetterOf"/>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="character">The letter of a <see cref="KeyCode.Letter"/> key, <c>a</c> to <c>z</c> in either case;
    /// ignored for any other key.</param>
    /// <param name="virtualKey">The virtual-key code when this returns <see langword="true"/>.</param>
    /// <param name="scanCode">The scan code when this returns <see langword="true"/>.</param>
    /// <param name="isExtended">Whether the message sets the extended-key bit, when this returns
    /// <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when some key message carries the key.</returns>
    public static bool TryVirtualKeyOf(KeyCode key, char character, out int virtualKey, out byte scanCode, out bool isExtended) {
        isExtended = (key is KeyCode.ControlRight or KeyCode.AltRight or KeyCode.ArrowUp or KeyCode.ArrowDown or KeyCode.ArrowLeft or KeyCode.ArrowRight or KeyCode.SuperLeft or KeyCode.SuperRight);
        virtualKey = key switch {
            KeyCode.Letter => (char.IsAsciiLetter(c: character)
                ? (VkA + (char.ToLowerInvariant(c: character) - 'a'))
                : 0),
            >= KeyCode.Digit0 and <= KeyCode.Digit9 => (Vk0 + (key - KeyCode.Digit0)),
            >= KeyCode.Numpad0 and <= KeyCode.Numpad9 => (VkNumpad0 + (key - KeyCode.Numpad0)),
            KeyCode.Backtick => VkOem3,
            KeyCode.Backspace => VkBack,
            KeyCode.Escape => VkEscape,
            KeyCode.Enter => VkReturn,
            KeyCode.Tab => VkTab,
            KeyCode.ArrowUp => VkUp,
            KeyCode.ArrowDown => VkDown,
            KeyCode.ArrowLeft => VkLeft,
            KeyCode.ArrowRight => VkRight,
            KeyCode.Space => VkSpace,
            KeyCode.Minus => VkOemMinus,
            KeyCode.Equals => VkOemPlus,
            KeyCode.NumpadSubtract => VkSubtract,
            KeyCode.NumpadAdd => VkAdd,
            >= KeyCode.F1 and <= KeyCode.F12 => (VkF1 + (key - KeyCode.F1)),
            KeyCode.ControlLeft or KeyCode.ControlRight => VkControl,
            KeyCode.AltLeft or KeyCode.AltRight => VkMenu,
            KeyCode.ShiftLeft or KeyCode.ShiftRight => VkShift,
            KeyCode.SuperLeft => VkLWin,
            KeyCode.SuperRight => VkRWin,
            _ => 0,
        };
        // Shift's side lives only in its scan code, which the side-specific virtual key maps to.
        scanCode = ((virtualKey == 0)
            ? ((byte)0)
            : unchecked((byte)User32.MapVirtualKey(
                code: ((uint)(key switch {
                    KeyCode.ShiftLeft => VkLShift,
                    KeyCode.ShiftRight => VkRShift,
                    _ => virtualKey,
                })),
                mapType: MapvkVkToVsc
            )));

        return (virtualKey != 0);
    }
}
