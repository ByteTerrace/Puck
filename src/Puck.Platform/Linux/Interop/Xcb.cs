using System.Runtime.InteropServices;

namespace Puck.Platform.Linux.Interop;

/// <summary>Minimal libxcb bindings for creating and pumping an X11 window. Entry points
/// resolve only on Linux at call time, so the declarations compile on every platform.</summary>
internal static partial class Xcb {
    [StructLayout(LayoutKind.Sequential)]
    public struct ScreenIterator {
        public nint Data;
        public int Remaining;
        public int Index;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct VoidCookie {
        public uint Sequence;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct InternAtomCookie {
        public uint Sequence;
    }

    [LibraryImport("libxcb.so.1")]
    public static partial nint xcb_connect(nint displayName, out int screenNumber);
    [LibraryImport("libxcb.so.1")]
    public static partial int xcb_connection_has_error(nint connection);
    [LibraryImport("libxcb.so.1")]
    public static partial void xcb_disconnect(nint connection);
    [LibraryImport("libxcb.so.1")]
    public static partial nint xcb_get_setup(nint connection);
    [LibraryImport("libxcb.so.1")]
    public static partial ScreenIterator xcb_setup_roots_iterator(nint setup);
    [LibraryImport("libxcb.so.1")]
    public static partial uint xcb_generate_id(nint connection);
    [LibraryImport("libxcb.so.1")]
    public static partial VoidCookie xcb_create_window(
        nint connection,
        byte depth,
        uint windowId,
        uint parent,
        short x,
        short y,
        ushort width,
        ushort height,
        ushort borderWidth,
        ushort windowClass,
        uint visual,
        uint valueMask,
        [In] uint[] valueList
    );
    [LibraryImport("libxcb.so.1")]
    public static partial VoidCookie xcb_map_window(nint connection, uint windowId);
    [LibraryImport("libxcb.so.1")]
    public static partial VoidCookie xcb_destroy_window(nint connection, uint windowId);
    [LibraryImport("libxcb.so.1")]
    public static partial int xcb_flush(nint connection);
    [LibraryImport("libxcb.so.1")]
    public static partial nint xcb_poll_for_event(nint connection);
    [LibraryImport("libxcb.so.1")]
    public static partial InternAtomCookie xcb_intern_atom(
        nint connection,
        byte onlyIfExists,
        ushort nameLength,
        [In] byte[] name
    );
    [LibraryImport("libxcb.so.1")]
    public static partial nint xcb_intern_atom_reply(nint connection, InternAtomCookie cookie, nint error);

    // Same native entry point (xcb_change_property), two managed shapes for the two data
    // payloads we set: UTF-8 title bytes and a single 32-bit atom (WM_PROTOCOLS).
    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_change_property")]
    public static partial VoidCookie xcb_change_property_bytes(
        nint connection,
        byte mode,
        uint windowId,
        uint property,
        uint type,
        byte format,
        uint dataLength,
        [In] byte[] data
    );
    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_change_property")]
    public static partial VoidCookie xcb_change_property_atom(
        nint connection,
        byte mode,
        uint windowId,
        uint property,
        uint type,
        byte format,
        uint dataLength,
        [In] uint[] data
    );
}
