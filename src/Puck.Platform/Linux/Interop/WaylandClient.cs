using System.Runtime.InteropServices;

namespace Puck.Platform.Linux.Interop;

/// <summary>libwayland-client bindings plus the minimal xdg-shell protocol glue needed to
/// open a toplevel window for a Vulkan surface.</summary>
/// <remarks>The core <c>wl_*</c> protocol marshalling tables (<c>wl_registry_interface</c>,
/// <c>wl_compositor_interface</c>, <c>wl_surface_interface</c>) are exported data symbols of
/// libwayland-client and are resolved with <see cref="NativeLibrary"/> — so they are correct
/// by construction. Only the xdg-shell tables (not part of libwayland) are hand-built here,
/// limited to the version-1 messages this backend actually sends and receives. Requests are
/// issued through the variadic <c>wl_proxy_marshal_flags</c>; the parent proxy's own
/// interface drives argument serialization. This code only runs on Linux; the declarations
/// compile everywhere. It is unverified off-device — expect on-device iteration.</remarks>
internal static unsafe partial class WaylandClient {
    public const uint WlMarshalFlagDestroy = 1;

    // wl_display / wl_registry core opcodes.
    private const uint WlDisplayGetRegistryOpcode = 1;
    private const uint WlCompositorCreateSurfaceOpcode = 0;
    private const uint WlRegistryBindOpcode = 0;
    private const uint WlSurfaceCommitOpcode = 6;
    private const uint WlSurfaceDestroyOpcode = 0;

    // xdg-shell opcodes (stable v1).
    private const uint XdgWmBaseDestroyOpcode = 0;
    private const uint XdgSurfaceAckConfigureOpcode = 4;
    private const uint XdgSurfaceDestroyOpcode = 0;
    private const uint XdgSurfaceGetToplevelOpcode = 1;
    private const uint XdgToplevelDestroyOpcode = 0;
    private const uint XdgToplevelSetAppIdOpcode = 3;
    private const uint XdgToplevelSetTitleOpcode = 2;
    private const uint XdgWmBaseGetXdgSurfaceOpcode = 2;
    private const uint XdgWmBasePongOpcode = 3;

    private static readonly Lock SyncRoot = new();
    private static nint LibraryHandle;
    private static nint RegistryInterfaceHandle;
    private static nint CompositorInterfaceHandle;
    private static nint SurfaceInterfaceHandle;
    private static nint XdgWmBaseInterfaceHandle;
    private static nint XdgSurfaceInterfaceHandle;
    private static nint XdgToplevelInterfaceHandle;
    private static bool Initialized;

    [LibraryImport("libwayland-client.so.0", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint wl_display_connect(string? name);
    [LibraryImport("libwayland-client.so.0")]
    public static partial void wl_display_disconnect(nint display);
    [LibraryImport("libwayland-client.so.0")]
    public static partial int wl_display_dispatch_pending(nint display);
    [LibraryImport("libwayland-client.so.0")]
    public static partial int wl_display_roundtrip(nint display);
    [LibraryImport("libwayland-client.so.0")]
    public static partial int wl_display_flush(nint display);
    [LibraryImport("libwayland-client.so.0")]
    public static partial uint wl_proxy_get_version(nint proxy);
    [LibraryImport("libwayland-client.so.0")]
    public static partial void wl_proxy_destroy(nint proxy);
    [LibraryImport("libwayland-client.so.0")]
    public static partial int wl_proxy_add_listener(nint proxy, nint implementation, nint data);

    // Variadic constructor/request marshaller (libwayland >= 1.19.91). Declared once with
    // __arglist; each wrapper passes the request's arguments in protocol order.
    [DllImport("libwayland-client.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint wl_proxy_marshal_flags(
        nint proxy,
        uint opcode,
        nint @interface,
        uint version,
        uint flags,
        __arglist
    );

    public static nint GetRegistry(nint display) {
        return wl_proxy_marshal_flags(
            display,
            WlDisplayGetRegistryOpcode,
            RegistryInterface,
            wl_proxy_get_version(proxy: display),
            0,
            __arglist((nint)0)
        );
    }
    public static nint Bind(nint registry, uint name, nint interfacePointer, uint version) {
        var interfaceName = Marshal.ReadIntPtr(
            ofs: 0,
            ptr: interfacePointer
        );

        return wl_proxy_marshal_flags(
            registry,
            WlRegistryBindOpcode,
            interfacePointer,
            version,
            0,
            __arglist(
                name,
                interfaceName,
                version,
                (nint)0
            )
        );
    }
    public static nint CreateSurface(nint compositor) {
        return wl_proxy_marshal_flags(
            compositor,
            WlCompositorCreateSurfaceOpcode,
            SurfaceInterface,
            wl_proxy_get_version(proxy: compositor),
            0,
            __arglist((nint)0)
        );
    }
    public static nint GetXdgSurface(nint wmBase, nint surface) {
        return wl_proxy_marshal_flags(
            wmBase,
            XdgWmBaseGetXdgSurfaceOpcode,
            XdgSurfaceInterface,
            wl_proxy_get_version(proxy: wmBase),
            0,
            __arglist(
                (nint)0,
                surface
            )
        );
    }
    public static nint GetToplevel(nint xdgSurface) {
        return wl_proxy_marshal_flags(
            xdgSurface,
            XdgSurfaceGetToplevelOpcode,
            XdgToplevelInterface,
            wl_proxy_get_version(proxy: xdgSurface),
            0,
            __arglist((nint)0)
        );
    }
    public static void SetToplevelTitle(nint toplevel, nint titleUtf8) {
        _ = wl_proxy_marshal_flags(
            toplevel,
            XdgToplevelSetTitleOpcode,
            0,
            wl_proxy_get_version(proxy: toplevel),
            0,
            __arglist(titleUtf8)
        );
    }
    public static void SetToplevelAppId(nint toplevel, nint appIdUtf8) {
        _ = wl_proxy_marshal_flags(
            toplevel,
            XdgToplevelSetAppIdOpcode,
            0,
            wl_proxy_get_version(proxy: toplevel),
            0,
            __arglist(appIdUtf8)
        );
    }
    public static void AckConfigure(nint xdgSurface, uint serial) {
        _ = wl_proxy_marshal_flags(
            xdgSurface,
            XdgSurfaceAckConfigureOpcode,
            0,
            wl_proxy_get_version(proxy: xdgSurface),
            0,
            __arglist(serial)
        );
    }
    public static void Pong(nint wmBase, uint serial) {
        _ = wl_proxy_marshal_flags(
            wmBase,
            XdgWmBasePongOpcode,
            0,
            wl_proxy_get_version(proxy: wmBase),
            0,
            __arglist(serial)
        );
    }
    public static void Commit(nint surface) {
        _ = wl_proxy_marshal_flags(
            surface,
            WlSurfaceCommitOpcode,
            0,
            wl_proxy_get_version(proxy: surface),
            0,
            __arglist()
        );
    }
    public static void DestroyToplevel(nint toplevel) {
        Destroy(
            opcode: XdgToplevelDestroyOpcode,
            proxy: toplevel
        );
    }
    public static void DestroyXdgSurface(nint xdgSurface) {
        Destroy(
            opcode: XdgSurfaceDestroyOpcode,
            proxy: xdgSurface
        );
    }
    public static void DestroyWmBase(nint wmBase) {
        Destroy(
            opcode: XdgWmBaseDestroyOpcode,
            proxy: wmBase
        );
    }
    public static void DestroySurface(nint surface) {
        Destroy(
            opcode: WlSurfaceDestroyOpcode,
            proxy: surface
        );
    }

    private static void Destroy(nint proxy, uint opcode) {
        _ = wl_proxy_marshal_flags(
            proxy,
            opcode,
            0,
            wl_proxy_get_version(proxy: proxy),
            WlMarshalFlagDestroy,
            __arglist()
        );
    }

    public static nint CompositorInterface {
        get {
            EnsureInitialized();
            return CompositorInterfaceHandle;
        }
    }
    public static nint RegistryInterface {
        get {
            EnsureInitialized();
            return RegistryInterfaceHandle;
        }
    }
    public static nint SurfaceInterface {
        get {
            EnsureInitialized();
            return SurfaceInterfaceHandle;
        }
    }
    public static nint XdgSurfaceInterface {
        get {
            EnsureInitialized();
            return XdgSurfaceInterfaceHandle;
        }
    }
    public static nint XdgToplevelInterface {
        get {
            EnsureInitialized();
            return XdgToplevelInterfaceHandle;
        }
    }
    public static nint XdgWmBaseInterface {
        get {
            EnsureInitialized();
            return XdgWmBaseInterfaceHandle;
        }
    }

    private static void EnsureInitialized() {
        lock (SyncRoot) {
            if (Initialized) {
                return;
            }

            LibraryHandle = NativeLibrary.Load(libraryPath: "libwayland-client.so.0");
            RegistryInterfaceHandle = NativeLibrary.GetExport(
                handle: LibraryHandle,
                name: "wl_registry_interface"
            );
            CompositorInterfaceHandle = NativeLibrary.GetExport(
                handle: LibraryHandle,
                name: "wl_compositor_interface"
            );
            SurfaceInterfaceHandle = NativeLibrary.GetExport(
                handle: LibraryHandle,
                name: "wl_surface_interface"
            );

            // Build order respects cross-references: toplevel <- surface <- wm_base.
            XdgToplevelInterfaceHandle = BuildInterface(
                eventCount: 2,
                events: BuildMessages(messages: [
                    ("configure", "iia", null),
                    ("close", "", null)
                ]),
                methodCount: 4,
                methods: BuildMessages(messages: [
                    ("destroy", "", null),
                    ("set_parent", "?o", null),
                    ("set_title", "s", null),
                    ("set_app_id", "s", null)
                ]),
                name: "xdg_toplevel",
                version: 1
            );
            XdgSurfaceInterfaceHandle = BuildInterface(
                eventCount: 1,
                events: BuildMessages(messages: [
                    ("configure", "u", null)
                ]),
                methodCount: 5,
                methods: BuildMessages(messages: [
                    ("destroy", "", null),
                    ("get_toplevel", "n", [XdgToplevelInterfaceHandle]),
                    ("get_popup", "n?oo", null),
                    ("set_window_geometry", "iiii", null),
                    ("ack_configure", "u", null)
                ]),
                name: "xdg_surface",
                version: 1
            );
            XdgWmBaseInterfaceHandle = BuildInterface(
                eventCount: 1,
                events: BuildMessages(messages: [
                    ("ping", "u", null)
                ]),
                methodCount: 4,
                methods: BuildMessages(messages: [
                    ("destroy", "", null),
                    ("create_positioner", "n", null),
                    ("get_xdg_surface", "no", [XdgSurfaceInterfaceHandle, SurfaceInterfaceHandle]),
                    ("pong", "u", null)
                ]),
                name: "xdg_wm_base",
                version: 1
            );
            Initialized = true;
        }
    }
    private static nint BuildMessages((string Name, string Signature, nint[]? Types)[] messages) {
        const int MessageSize = 24;
        var block = Marshal.AllocHGlobal(cb: (MessageSize * messages.Length));

        for (var index = 0; (index < messages.Length); index++) {
            var (name, signature, types) = messages[index];
            var record = (block + (index * MessageSize));

            Marshal.WriteIntPtr(
                ofs: 0,
                ptr: record,
                val: Marshal.StringToCoTaskMemUTF8(s: name)
            );
            Marshal.WriteIntPtr(
                ofs: 8,
                ptr: record,
                val: Marshal.StringToCoTaskMemUTF8(s: signature)
            );
            Marshal.WriteIntPtr(
                ofs: 16,
                ptr: record,
                val: BuildTypes(types: types)
            );
        }

        return block;
    }
    private static nint BuildTypes(nint[]? types) {
        if (
            (types is null) ||
            (types.Length == 0)
        ) {
            return 0;
        }

        var block = Marshal.AllocHGlobal(cb: (IntPtr.Size * types.Length));

        for (var index = 0; (index < types.Length); index++) {
            Marshal.WriteIntPtr(
                ofs: (index * IntPtr.Size),
                ptr: block,
                val: types[index]
            );
        }

        return block;
    }
    private static nint BuildInterface(
        string name,
        int version,
        nint methods,
        int methodCount,
        nint events,
        int eventCount
    ) {
        // struct wl_interface { const char* name; int version; int method_count;
        //   const wl_message* methods; int event_count; const wl_message* events; }
        var block = Marshal.AllocHGlobal(cb: 40);

        Marshal.WriteIntPtr(
            ofs: 0,
            ptr: block,
            val: Marshal.StringToCoTaskMemUTF8(s: name)
        );
        Marshal.WriteInt32(
            ofs: 8,
            ptr: block,
            val: version
        );
        Marshal.WriteInt32(
            ofs: 12,
            ptr: block,
            val: methodCount
        );
        Marshal.WriteIntPtr(
            ofs: 16,
            ptr: block,
            val: methods
        );
        Marshal.WriteInt32(
            ofs: 24,
            ptr: block,
            val: eventCount
        );
        Marshal.WriteIntPtr(
            ofs: 32,
            ptr: block,
            val: events
        );
        return block;
    }
}
