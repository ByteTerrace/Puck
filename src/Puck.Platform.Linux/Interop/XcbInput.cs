using System.Runtime.InteropServices;

namespace Puck.Platform.Linux.Interop;

/// <summary>The few XInput 2 entry points the X11 window needs for touch: the core
/// <c>xcb_query_extension</c> (libxcb) finds the extension's major opcode, and libxcb-xinput announces
/// XI 2.2 and selects the touch events. A system without libxcb-xinput reads no touch; the window
/// catches the load failure.</summary>
internal static partial class XcbInput {
    [StructLayout(LayoutKind.Sequential)]
    public struct Cookie {
        public uint Sequence;
    }

    [LibraryImport("libxcb.so.1")]
    public static partial Cookie xcb_query_extension(nint connection, ushort nameLength, [In] byte[] name);
    [LibraryImport("libxcb.so.1")]
    public static partial nint xcb_query_extension_reply(nint connection, Cookie cookie, nint error);
    [LibraryImport("libxcb-xinput.so.0")]
    public static partial Cookie xcb_input_xi_query_version(nint connection, ushort majorVersion, ushort minorVersion);
    [LibraryImport("libxcb-xinput.so.0")]
    public static partial nint xcb_input_xi_query_version_reply(nint connection, Cookie cookie, nint error);
    // masks points at num_mask xcb_input_event_mask_t headers ({ uint16 deviceid, uint16 mask_len }), each followed by
    // mask_len 32-bit mask words.
    [LibraryImport("libxcb-xinput.so.0")]
    public static partial Cookie xcb_input_xi_select_events(nint connection, uint window, ushort maskCount, [In] uint[] masks);
}
