using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using Puck.Input;
using Puck.Platform.Linux.Interop;

namespace Puck.Platform.Linux;

/// <summary>An X11 (XCB) native window backed by libxcb. Drives the window lifecycle and
/// the events the engine loop needs (resize, close, first paint, pointer motion, and a
/// fixed set of navigation/function keys on the standard Linux evdev keymap). Full keysym
/// text input and clipboard integration are out of scope; on the Steam Deck this path runs
/// under XWayland, while the native Gamescope path is <see cref="WaylandNativeWindow"/>.</summary>
internal sealed class XcbNativeWindow : INativeWindow, IWindowInputSource {
    private const uint XcbAtomAtom = 4;
    private const uint XcbAtomString = 31;
    private const uint XcbAtomWmName = 39;
    private const byte XcbClientMessage = 33;
    private const byte XcbConfigureNotify = 22;
    private const byte XcbCopyFromParent = 0;
    private const uint XcbCwBackPixel = 2;
    private const uint XcbCwEventMask = 2048;
    private const byte XcbDestroyNotify = 17;
    private const uint XcbEventMaskExposure = 32768;
    private const uint XcbEventMaskKeyPress = 1;
    private const uint XcbEventMaskKeyRelease = 2;
    private const uint XcbEventMaskPointerMotion = 64;
    private const uint XcbEventMaskStructureNotify = 131072;
    private const byte XcbExpose = 12;
    private const byte XcbKeyPress = 2;
    private const byte XcbKeyRelease = 3;
    private const byte XcbMotionNotify = 6;
    private const byte XcbPropModeReplace = 0;
    private const byte XcbResponseTypeMask = 0x7F;
    private const ushort XcbWindowClassInputOutput = 1;

    // Standard Linux evdev keycodes (X11 keycode = evdev scancode + 8); stable on the
    // Steam Deck and mainstream desktops, so a small fixed table covers the navigation and
    // function keys the terminal app binds without an xkb keysym dependency.
    private const byte KeycodeEscape = 9;
    private const byte KeycodeBackspace = 22;
    private const byte KeycodeDown = 116;
    private const byte KeycodeF1 = 67;
    private const byte KeycodeF8 = 74;
    private const byte KeycodeGrave = 49;
    private const byte KeycodeLeft = 113;
    private const byte KeycodeReturn = 36;
    private const byte KeycodeRight = 114;
    private const byte KeycodeUp = 111;

    private readonly nint m_connection;
    private readonly uint m_deleteWindowAtom;
    private readonly NativeWindowOptions m_options;
    private readonly Queue<WindowInputEvent> m_pendingInput = [];
    private readonly uint m_window;
    private bool m_disposed;
    private bool m_hasPainted;
    private bool m_isOpen = true;
    private bool m_isVisible;
    private int? m_lastPointerX;
    private int? m_lastPointerY;
    private byte? m_pendingReleaseKeycode;
    private uint m_pendingReleaseTime;

    public XcbNativeWindow(IOptions<NativeWindowOptions> options) {
        ArgumentNullException.ThrowIfNull(options);

        m_options = options.Value;
        Width = m_options.Width;
        Height = m_options.Height;

        m_connection = Xcb.xcb_connect(
            displayName: 0,
            screenNumber: out _
        );

        if (
            (m_connection == 0) ||
            (Xcb.xcb_connection_has_error(connection: m_connection) != 0)
        ) {
            throw new InvalidOperationException(message: "xcb_connect failed to open a connection to the X server.");
        }

        var setup = Xcb.xcb_get_setup(connection: m_connection);
        var screen = Xcb.xcb_setup_roots_iterator(setup: setup).Data;

        if (screen == 0) {
            Xcb.xcb_disconnect(connection: m_connection);
            throw new InvalidOperationException(message: "The X server reported no root screen.");
        }

        var rootWindow = (uint)Marshal.ReadInt32(
            ofs: 0,
            ptr: screen
        );
        var rootVisual = (uint)Marshal.ReadInt32(
            ofs: 32,
            ptr: screen
        );

        m_window = Xcb.xcb_generate_id(connection: m_connection);
        _ = Xcb.xcb_create_window(
            borderWidth: 0,
            connection: m_connection,
            depth: XcbCopyFromParent,
            height: (ushort)m_options.Height,
            parent: rootWindow,
            valueList: [0u, (XcbEventMaskExposure | XcbEventMaskKeyPress | XcbEventMaskKeyRelease | XcbEventMaskPointerMotion | XcbEventMaskStructureNotify)],
            valueMask: XcbCwBackPixel | XcbCwEventMask,
            visual: rootVisual,
            width: (ushort)m_options.Width,
            windowClass: XcbWindowClassInputOutput,
            windowId: m_window,
            x: 0,
            y: 0
        );

        ApplyTitle();
        m_deleteWindowAtom = RegisterDeleteWindowProtocol();
        _ = Xcb.xcb_flush(connection: m_connection);
    }

    public NativeDisplayKind DisplayKind => NativeDisplayKind.Xcb;
    public bool HasPainted => m_hasPainted;
    public uint Height { get; private set; }
    public bool IsOpen => (!m_disposed && m_isOpen);
    public bool IsVisible => m_isVisible;
    public ulong ResizeCount { get; private set; }
    public string Title => m_options.Title;
    public uint Width { get; private set; }

    public void Close() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (!m_isOpen) {
            return;
        }

        _ = Xcb.xcb_destroy_window(
            connection: m_connection,
            windowId: m_window
        );
        _ = Xcb.xcb_flush(connection: m_connection);
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
            Xcb: new XcbNativeSurfaceBinding(
                Connection: m_connection,
                Window: m_window
            )
        );
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (m_connection != 0) {
            if (m_isOpen) {
                _ = Xcb.xcb_destroy_window(
                    connection: m_connection,
                    windowId: m_window
                );
                _ = Xcb.xcb_flush(connection: m_connection);
            }

            Xcb.xcb_disconnect(connection: m_connection);
        }

        m_isOpen = false;
        m_isVisible = false;
        m_disposed = true;
    }
    public void PollEvents() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        while (m_isOpen) {
            var eventPointer = Xcb.xcb_poll_for_event(connection: m_connection);

            if (eventPointer == 0) {
                break;
            }

            try {
                HandleEvent(eventPointer: eventPointer);
            } finally {
                Libc.free(pointer: eventPointer);
            }
        }

        // A deferred release with no following repeat-press this batch was a real release; flush it. Auto-repeat
        // release/press pairs are delivered together, so a genuine release never lingers past its own batch.
        if (m_pendingReleaseKeycode is { } leftoverKeycode) {
            m_pendingReleaseKeycode = null;
            EmitKeyRelease(keycode: leftoverKeycode);
        }
    }
    public void Show() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        _ = Xcb.xcb_map_window(
            connection: m_connection,
            windowId: m_window
        );
        _ = Xcb.xcb_flush(connection: m_connection);
        m_isVisible = true;
    }
    public bool TryDequeueInput(out WindowInputEvent inputEvent) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_pendingInput.Count == 0) {
            inputEvent = default;
            return false;
        }

        inputEvent = m_pendingInput.Dequeue();
        return true;
    }

    private void ApplyTitle() {
        var titleBytes = Encoding.UTF8.GetBytes(s: m_options.Title);

        _ = Xcb.xcb_change_property_bytes(
            connection: m_connection,
            data: titleBytes,
            dataLength: (uint)titleBytes.Length,
            format: 8,
            mode: XcbPropModeReplace,
            property: XcbAtomWmName,
            type: XcbAtomString,
            windowId: m_window
        );
    }
    private uint RegisterDeleteWindowProtocol() {
        var protocolsAtom = InternAtom(name: "WM_PROTOCOLS");
        var deleteWindowAtom = InternAtom(name: "WM_DELETE_WINDOW");

        if (
            (protocolsAtom == 0) ||
            (deleteWindowAtom == 0)
        ) {
            return 0;
        }

        _ = Xcb.xcb_change_property_atom(
            connection: m_connection,
            data: [deleteWindowAtom],
            dataLength: 1,
            format: 32,
            mode: XcbPropModeReplace,
            property: protocolsAtom,
            type: XcbAtomAtom,
            windowId: m_window
        );
        return deleteWindowAtom;
    }
    private uint InternAtom(string name) {
        var nameBytes = Encoding.ASCII.GetBytes(s: name);
        var cookie = Xcb.xcb_intern_atom(
            connection: m_connection,
            name: nameBytes,
            nameLength: (ushort)nameBytes.Length,
            onlyIfExists: 0
        );
        var reply = Xcb.xcb_intern_atom_reply(
            connection: m_connection,
            cookie: cookie,
            error: 0
        );

        if (reply == 0) {
            return 0;
        }

        try {
            return (uint)Marshal.ReadInt32(
                ofs: 8,
                ptr: reply
            );
        } finally {
            Libc.free(pointer: reply);
        }
    }
    private void HandleEvent(nint eventPointer) {
        var responseType = (byte)(Marshal.ReadByte(
            ofs: 0,
            ptr: eventPointer
        ) & XcbResponseTypeMask);

        ReconcilePendingRelease(eventPointer: eventPointer, responseType: responseType);

        switch (responseType) {
            case XcbExpose:
                m_hasPainted = true;
                return;
            case XcbConfigureNotify:
                HandleConfigureNotify(eventPointer: eventPointer);
                return;
            case XcbClientMessage:
                HandleClientMessage(eventPointer: eventPointer);
                return;
            case XcbMotionNotify:
                HandleMotionNotify(eventPointer: eventPointer);
                return;
            case XcbKeyPress:
                HandleKeyPress(eventPointer: eventPointer);
                return;
            case XcbKeyRelease:
                // Defer the release by one event so an auto-repeat KeyPress can cancel it (see ReconcilePendingRelease).
                m_pendingReleaseKeycode = Marshal.ReadByte(ofs: 1, ptr: eventPointer);
                m_pendingReleaseTime = (uint)Marshal.ReadInt32(ofs: 4, ptr: eventPointer);
                return;
            case XcbDestroyNotify:
                m_isOpen = false;
                m_isVisible = false;
                return;
            default:
                return;
        }
    }
    private void HandleConfigureNotify(nint eventPointer) {
        var width = (uint)(ushort)Marshal.ReadInt16(
            ofs: 20,
            ptr: eventPointer
        );
        var height = (uint)(ushort)Marshal.ReadInt16(
            ofs: 22,
            ptr: eventPointer
        );

        if (
            (width == 0) ||
            (height == 0)
        ) {
            return;
        }

        if (
            (width != Width) ||
            (height != Height)
        ) {
            Width = width;
            Height = height;
            ResizeCount++;
        }
    }
    private void HandleClientMessage(nint eventPointer) {
        if (m_deleteWindowAtom == 0) {
            return;
        }

        var messageData = (uint)Marshal.ReadInt32(
            ofs: 12,
            ptr: eventPointer
        );

        if (messageData == m_deleteWindowAtom) {
            m_isOpen = false;
            m_isVisible = false;
        }
    }
    private void HandleMotionNotify(nint eventPointer) {
        var pointerX = Marshal.ReadInt16(
            ofs: 24,
            ptr: eventPointer
        );
        var pointerY = Marshal.ReadInt16(
            ofs: 26,
            ptr: eventPointer
        );

        if (
            (m_lastPointerX is { } lastPointerX) &&
            (m_lastPointerY is { } lastPointerY)
        ) {
            var deltaX = (pointerX - lastPointerX);
            var deltaY = (pointerY - lastPointerY);

            if (
                (deltaX != 0) ||
                (deltaY != 0)
            ) {
                m_pendingInput.Enqueue(item: WindowInputEvent.PointerDelta(delta: new Vector2(
                    x: deltaX,
                    y: deltaY
                )));
            }
        }

        m_lastPointerX = pointerX;
        m_lastPointerY = pointerY;
    }
    private void ReconcilePendingRelease(nint eventPointer, byte responseType) {
        if (m_pendingReleaseKeycode is not { } pendingKeycode) {
            return;
        }

        var keycode = Marshal.ReadByte(ofs: 1, ptr: eventPointer);
        var time = (uint)Marshal.ReadInt32(ofs: 4, ptr: eventPointer);

        m_pendingReleaseKeycode = null;

        // X11 auto-repeat: a held key arrives as KeyRelease immediately followed by a KeyPress with the same
        // keycode and timestamp. Such a press cancels the deferred release (no up/down churn); the repeat
        // KeyPress is then handled normally and emits another KeyDown, matching the Win32 repeat behavior.
        if (
            (responseType == XcbKeyPress) &&
            (keycode == pendingKeycode) &&
            (time == m_pendingReleaseTime)
        ) {
            return;
        }

        EmitKeyRelease(keycode: pendingKeycode);
    }
    private void HandleKeyPress(nint eventPointer) {
        var keycode = Marshal.ReadByte(
            ofs: 1,
            ptr: eventPointer
        );

        if (TryMapKeycode(keycode: keycode, key: out var key)) {
            m_pendingInput.Enqueue(item: WindowInputEvent.KeyDown(key: key));
        }
    }
    private void EmitKeyRelease(byte keycode) {
        if (TryMapKeycode(keycode: keycode, key: out var key)) {
            m_pendingInput.Enqueue(item: WindowInputEvent.KeyUp(key: key));
        }
    }
    private static bool TryMapKeycode(byte keycode, out KeyCode key) {
        if (
            (keycode >= KeycodeF1) &&
            (keycode <= KeycodeF8)
        ) {
            key = (KeyCode)((int)KeyCode.F1 + (keycode - KeycodeF1));
            return true;
        }

        key = keycode switch {
            KeycodeEscape => KeyCode.Escape,
            KeycodeBackspace => KeyCode.Backspace,
            KeycodeReturn => KeyCode.Enter,
            KeycodeGrave => KeyCode.Backtick,
            KeycodeUp => KeyCode.ArrowUp,
            KeycodeDown => KeyCode.ArrowDown,
            KeycodeLeft => KeyCode.ArrowLeft,
            KeycodeRight => KeyCode.ArrowRight,
            _ => KeyCode.None,
        };

        return (key != KeyCode.None);
    }
}
