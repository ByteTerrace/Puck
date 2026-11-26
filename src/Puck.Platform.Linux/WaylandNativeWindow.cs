using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Puck.Commands;
using Puck.Input;
using Puck.Platform.Linux.Interop;

namespace Puck.Platform.Linux;

/// <summary>A Wayland toplevel window (the native Steam Deck / Gamescope path) backed by
/// libwayland-client + xdg-shell. Handles the lifecycle, the xdg-shell configure/close
/// handshake, surface resize and touch: the first <c>wl_seat</c> advertising the touch
/// capability gives its <c>wl_touch</c>, whose contacts enter the shared
/// <see cref="WindowInputQueue"/>. Keyboard and pointer input would require <c>wl_keyboard</c>
/// + xkb and <c>wl_pointer</c> and are out of scope; Vulkan WSI owns the buffers, so this
/// backend otherwise only drives the surface role and event pump.</summary>
/// <remarks>Unverified off-device — the protocol glue (hand-built xdg-shell tables, the
/// non-blocking dispatch in <see cref="PollEvents"/>, the seat and touch listeners) is expected
/// to need on-device iteration. The touch events follow the core protocol's <c>wl_touch</c>:
/// <c>down</c>, <c>up</c>, <c>motion</c>, <c>frame</c>, <c>cancel</c>, then <c>shape</c> and
/// <c>orientation</c> since version 6, with surface-local <c>wl_fixed</c> (24.8) coordinates in
/// the same logical space the toplevel's configured size is in; a <c>cancel</c> ends every live
/// contact.</remarks>
internal sealed unsafe class WaylandNativeWindow : INativeWindow, IWindowInputSource {
    private const uint WlCompositorMaxVersion = 4;
    private const uint WlSeatMaxVersion = 7;
    private const uint WlSeatCapabilityTouch = 4;
    private const uint XdgWmBaseMaxVersion = 1;

    private static readonly Lock ListenerLock = new();

    private static bool ListenersBuilt;
    private static nint RegistryListener;
    private static nint SeatListener;
    private static nint ToplevelListener;
    private static nint TouchListener;
    private static nint WmBaseListener;
    private static nint XdgSurfaceListener;

    private readonly NativeWindowOptions m_options;
    private readonly WindowInputQueue m_pendingInput = new();

    private nint m_seat;
    private uint m_seatName;
    private nint m_touch;
    private InputDeviceId m_touchDevice;

    private nint m_compositor;
    private nint m_display;
    private bool m_disposed;
    private bool m_hasPainted;
    private bool m_isOpen = true;
    private bool m_isVisible;
    private nint m_registry;
    private GCHandle m_selfHandle;
    private nint m_surface;
    private nint m_toplevel;
    private nint m_wmBase;
    private nint m_xdgSurface;

    public WaylandNativeWindow(IOptions<NativeWindowOptions> options) {
        ArgumentNullException.ThrowIfNull(options);

        m_options = options.Value;
        Width = m_options.Width;
        Height = m_options.Height;
        m_selfHandle = GCHandle.Alloc(value: this);

        EnsureListenersBuilt();

        m_display = WaylandClient.wl_display_connect(name: null);

        if (m_display == 0) {
            FreeSelfHandle();
            throw new InvalidOperationException(message: "wl_display_connect failed to connect to a Wayland compositor.");
        }

        var dataPointer = GCHandle.ToIntPtr(value: m_selfHandle);

        m_registry = WaylandClient.GetRegistry(display: m_display);
        _ = WaylandClient.wl_proxy_add_listener(
            data: dataPointer,
            implementation: RegistryListener,
            proxy: m_registry
        );

        // Round-trips run the registry callbacks, which bind the compositor + xdg_wm_base.
        _ = WaylandClient.wl_display_roundtrip(display: m_display);

        if (
            (m_compositor == 0) ||
            (m_wmBase == 0)
        ) {
            DisposeInternal();
            throw new InvalidOperationException(message: "The Wayland compositor does not expose wl_compositor and xdg_wm_base.");
        }

        _ = WaylandClient.wl_proxy_add_listener(
            data: dataPointer,
            implementation: WmBaseListener,
            proxy: m_wmBase
        );

        m_surface = WaylandClient.CreateSurface(compositor: m_compositor);
        m_xdgSurface = WaylandClient.GetXdgSurface(
            surface: m_surface,
            wmBase: m_wmBase
        );
        _ = WaylandClient.wl_proxy_add_listener(
            data: dataPointer,
            implementation: XdgSurfaceListener,
            proxy: m_xdgSurface
        );

        m_toplevel = WaylandClient.GetToplevel(xdgSurface: m_xdgSurface);
        _ = WaylandClient.wl_proxy_add_listener(
            data: dataPointer,
            implementation: ToplevelListener,
            proxy: m_toplevel
        );

        ApplyTitleAndAppId();

        WaylandClient.Commit(surface: m_surface);
        _ = WaylandClient.wl_display_roundtrip(display: m_display);
    }

    public NativeDisplayKind DisplayKind => NativeDisplayKind.Wayland;
    public bool HasPainted => m_hasPainted;
    public uint Height { get; private set; }
    public bool IsOpen => (!m_disposed && m_isOpen);
    public bool IsVisible => m_isVisible;
    public ulong ResizeCount { get; private set; }
    public string Title => m_options.Title;
    public uint Width { get; private set; }

    private void ApplyTitleAndAppId() {
        var titlePointer = Marshal.StringToCoTaskMemUTF8(s: m_options.Title);
        var appIdPointer = Marshal.StringToCoTaskMemUTF8(s: m_options.Title);

        try {
            WaylandClient.SetToplevelTitle(
                titleUtf8: titlePointer,
                toplevel: m_toplevel
            );
            WaylandClient.SetToplevelAppId(
                appIdUtf8: appIdPointer,
                toplevel: m_toplevel
            );
        } finally {
            Marshal.FreeCoTaskMem(ptr: titlePointer);
            Marshal.FreeCoTaskMem(ptr: appIdPointer);
        }
    }
    private static nint BuildListener(ReadOnlySpan<nint> functionPointers) {
        var block = Marshal.AllocHGlobal(cb: (IntPtr.Size * functionPointers.Length));

        for (var index = 0; (index < functionPointers.Length); index++) {
            Marshal.WriteIntPtr(
                ofs: (index * IntPtr.Size),
                ptr: block,
                val: functionPointers[index]
            );
        }

        return block;
    }
    private void DisposeInternal() {
        if (m_touch != 0) {
            WaylandClient.ReleaseTouch(touch: m_touch);
            m_touch = 0;
        }

        if (m_seat != 0) {
            WaylandClient.ReleaseSeat(seat: m_seat);
            m_seat = 0;
        }

        if (m_toplevel != 0) {
            WaylandClient.DestroyToplevel(toplevel: m_toplevel);
            m_toplevel = 0;
        }

        if (m_xdgSurface != 0) {
            WaylandClient.DestroyXdgSurface(xdgSurface: m_xdgSurface);
            m_xdgSurface = 0;
        }

        if (m_surface != 0) {
            WaylandClient.DestroySurface(surface: m_surface);
            m_surface = 0;
        }

        if (m_wmBase != 0) {
            WaylandClient.DestroyWmBase(wmBase: m_wmBase);
            m_wmBase = 0;
        }

        if (m_registry != 0) {
            WaylandClient.wl_proxy_destroy(proxy: m_registry);
            m_registry = 0;
        }

        if (m_display != 0) {
            WaylandClient.wl_display_disconnect(display: m_display);
            m_display = 0;
        }

        FreeSelfHandle();
        m_isOpen = false;
        m_isVisible = false;
    }
    private static void EnsureListenersBuilt() {
        lock (ListenerLock) {
            if (ListenersBuilt) {
                return;
            }

            RegistryListener = BuildListener(functionPointers: [
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, void>)&RegistryGlobal)),
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, uint, void>)&RegistryGlobalRemove))
            ]);
            WmBaseListener = BuildListener(functionPointers: [
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, uint, void>)&WmBasePing))
            ]);
            XdgSurfaceListener = BuildListener(functionPointers: [
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, uint, void>)&XdgSurfaceConfigure))
            ]);
            ToplevelListener = BuildListener(functionPointers: [
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, int, int, nint, void>)&ToplevelConfigure)),
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, void>)&ToplevelClose))
            ]);
            SeatListener = BuildListener(functionPointers: [
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, uint, void>)&SeatCapabilities)),
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&SeatName))
            ]);
            TouchListener = BuildListener(functionPointers: [
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nint, int, int, int, void>)&TouchDown)),
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, uint, uint, int, void>)&TouchUp)),
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, uint, int, int, int, void>)&TouchMotion)),
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, void>)&TouchFrame)),
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, void>)&TouchCancel)),
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, int, int, int, void>)&TouchShape)),
                ((nint)((delegate* unmanaged[Cdecl]<nint, nint, int, int, void>)&TouchOrientation))
            ]);
            ListenersBuilt = true;
        }
    }
    private void FreeSelfHandle() {
        if (m_selfHandle.IsAllocated) {
            m_selfHandle.Free();
        }
    }
    private static WaylandNativeWindow? FromData(nint data) {
        if (data == 0) {
            return null;
        }

        return (GCHandle.FromIntPtr(value: data).Target as WaylandNativeWindow);
    }
    private void OnRegistryGlobal(uint name, nint interfaceName, uint version) {
        var interfaceText = Marshal.PtrToStringUTF8(ptr: interfaceName);

        if (interfaceText == "wl_compositor") {
            m_compositor = WaylandClient.Bind(
                interfacePointer: WaylandClient.CompositorInterface,
                name: name,
                registry: m_registry,
                version: Math.Min(
                    val1: version,
                    val2: WlCompositorMaxVersion
                )
            );
            return;
        }

        if (interfaceText == "xdg_wm_base") {
            m_wmBase = WaylandClient.Bind(
                interfacePointer: WaylandClient.XdgWmBaseInterface,
                name: name,
                registry: m_registry,
                version: Math.Min(
                    val1: version,
                    val2: XdgWmBaseMaxVersion
                )
            );
            return;
        }

        // The first seat is the one whose touch the window reads; a second seat's touchscreen is not this window's.
        if ((interfaceText == "wl_seat") && (m_seat == 0)) {
            m_seat = WaylandClient.Bind(
                interfacePointer: WaylandClient.SeatInterface,
                name: name,
                registry: m_registry,
                version: Math.Min(
                    val1: version,
                    val2: WlSeatMaxVersion
                )
            );
            m_seatName = name;
            _ = WaylandClient.wl_proxy_add_listener(
                data: GCHandle.ToIntPtr(value: m_selfHandle),
                implementation: SeatListener,
                proxy: m_seat
            );
        }
    }
    private void OnSeatCapabilities(uint capabilities) {
        var hasTouch = ((capabilities & WlSeatCapabilityTouch) != 0);

        if (hasTouch && (m_touch == 0)) {
            m_touch = WaylandClient.GetTouch(seat: m_seat);
            m_touchDevice = InputDeviceId.FromConnectionKey(key: $"wayland-touch-{m_seatName}");
            _ = WaylandClient.wl_proxy_add_listener(
                data: GCHandle.ToIntPtr(value: m_selfHandle),
                implementation: TouchListener,
                proxy: m_touch
            );
        } else if (!hasTouch && (m_touch != 0)) {
            // The touchscreen went away: every contact it held ends.
            _ = m_pendingInput.CancelContacts();
            WaylandClient.ReleaseTouch(touch: m_touch);
            m_touch = 0;
        }
    }
    private void OnTouchUp(int id) => _ = m_pendingInput.EnqueueContact(
        device: ContactDevice.Touch,
        deviceId: m_touchDevice,
        osId: unchecked((uint)id),
        phase: CommandPhase.Completed,
        position: null,
        pressure: float.NaN
    );
    private void OnTouch(int id, CommandPhase phase, int x, int y) => _ = m_pendingInput.EnqueueContact(
        device: ContactDevice.Touch,
        deviceId: m_touchDevice,
        osId: unchecked((uint)id),
        phase: phase,
        position: new System.Numerics.Vector2(x: (x / WlFixedScale), y: (y / WlFixedScale)),
        pressure: float.NaN
    );
    // wl_fixed_t is a signed 24.8 fixed-point number.
    private const float WlFixedScale = 256f;
    private void OnToplevelClose() {
        m_isOpen = false;
        m_isVisible = false;
    }
    private void OnToplevelConfigure(int width, int height) {
        if (
            (width <= 0) ||
            (height <= 0)
        ) {
            return;
        }

        if (
            (((uint)width) != Width) ||
            (((uint)height) != Height)
        ) {
            Width = ((uint)width);
            Height = ((uint)height);
            ResizeCount++;
        }
    }
    private void OnWmBasePing(uint serial) {
        WaylandClient.Pong(
            serial: serial,
            wmBase: m_wmBase
        );
    }
    private void OnXdgSurfaceConfigure(uint serial) {
        WaylandClient.AckConfigure(
            serial: serial,
            xdgSurface: m_xdgSurface
        );
        m_hasPainted = true;
        m_isVisible = true;
        WaylandClient.Commit(surface: m_surface);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RegistryGlobal(nint data, nint registry, uint name, nint interfaceName, uint version) {
        FromData(data: data)?.OnRegistryGlobal(
            interfaceName: interfaceName,
            name: name,
            version: version
        );
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RegistryGlobalRemove(nint data, nint registry, uint name) {
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SeatCapabilities(nint data, nint seat, uint capabilities) {
        FromData(data: data)?.OnSeatCapabilities(capabilities: capabilities);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SeatName(nint data, nint seat, nint name) {
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TouchDown(nint data, nint touch, uint serial, uint time, nint surface, int id, int x, int y) {
        FromData(data: data)?.OnTouch(id: id, phase: CommandPhase.Started, x: x, y: y);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TouchUp(nint data, nint touch, uint serial, uint time, int id) {
        // A lift carries no point; the contact lifts where it last moved to.
        FromData(data: data)?.OnTouchUp(id: id);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TouchMotion(nint data, nint touch, uint time, int id, int x, int y) {
        FromData(data: data)?.OnTouch(id: id, phase: CommandPhase.Active, x: x, y: y);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TouchFrame(nint data, nint touch) {
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TouchCancel(nint data, nint touch) {
        _ = FromData(data: data)?.m_pendingInput.CancelContacts();
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TouchShape(nint data, nint touch, int id, int major, int minor) {
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TouchOrientation(nint data, nint touch, int id, int orientation) {
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ToplevelClose(nint data, nint toplevel) {
        FromData(data: data)?.OnToplevelClose();
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ToplevelConfigure(nint data, nint toplevel, int width, int height, nint states) {
        FromData(data: data)?.OnToplevelConfigure(
            height: height,
            width: width
        );
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WmBasePing(nint data, nint wmBase, uint serial) {
        FromData(data: data)?.OnWmBasePing(serial: serial);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void XdgSurfaceConfigure(nint data, nint xdgSurface, uint serial) {
        FromData(data: data)?.OnXdgSurfaceConfigure(serial: serial);
    }

    public void Close() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_isOpen = false;
        m_isVisible = false;
    }
    public NativeSurfaceBinding CreateSurfaceBinding() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        return new NativeSurfaceBinding(
            DisplayKind: DisplayKind,
            Wayland: new WaylandNativeSurfaceBinding(
                DisplayHandle: m_display,
                SurfaceHandle: m_surface
            )
        );
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        DisposeInternal();
        m_disposed = true;
    }
    public void PollEvents() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_display == 0) {
            return;
        }

        _ = WaylandClient.wl_display_dispatch_pending(display: m_display);
        _ = WaylandClient.wl_display_flush(display: m_display);
    }
    public void Show() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_isVisible = true;
        WaylandClient.Commit(surface: m_surface);
        _ = WaylandClient.wl_display_flush(display: m_display);
    }
    public bool TryDequeueInput(out WindowInputEvent inputEvent) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        return m_pendingInput.TryDequeue(inputEvent: out inputEvent);
    }
}
