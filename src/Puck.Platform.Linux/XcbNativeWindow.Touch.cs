using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Commands;
using Puck.Platform.Linux.Interop;

namespace Puck.Platform.Linux;

// Touch on X11 through XInput 2.2. The XI2 protocol specification (XI2proto.txt, xorg.freedesktop.org) settles the
// rules followed here: touch events reach only a client that announced XI 2.2 with XIQueryVersion; TouchBegin,
// TouchUpdate and TouchEnd must be selected together or the server answers BadValue; and an emulated event goes "as a
// touch event to the window if a client has selected for touch events on this window", so no emulated pointer event
// doubles a touch. Qt's XCB backend (qxcbconnection_xi2.cpp) is a second implementation that queries 2.2 first for the
// same reason. The event numbers (18, 19, 20) are XI2.h's enumeration, and the byte offsets are the
// xcb_input_touch_begin_event_t layout xcb generates from xinput.xml (event_type at 8, detail, the touch id, at 16,
// event_x and event_y as 16.16 fixed point at 44 and 48, sourceid at 56, with xcb's full_sequence at 32); neither was
// checked against the header here. Unverified on hardware: a Linux build proves it compiles, not that a touchscreen
// reaches it.
internal sealed partial class XcbNativeWindow {
    private const byte XcbGenericEvent = 35;
    private const ushort XiTouchBegin = 18;
    private const ushort XiTouchUpdate = 19;
    private const ushort XiTouchEnd = 20;
    private const uint XiAllMasterDevices = 1;
    private const float Fixed1616Scale = 65536f;

    // XInput's major opcode once XI 2.2 touch is selected on this window, or 0 when touch is unavailable.
    private byte m_xinputOpcode;

    // Announces XI 2.2 and selects touch begin, update and end from every master device on the window; leaves touch off
    // where the server or the client library lacks it.
    private void SelectTouch() {
        try {
            var name = "XInputExtension"u8.ToArray();
            var extension = XcbInput.xcb_query_extension_reply(
                connection: m_connection,
                cookie: XcbInput.xcb_query_extension(connection: m_connection, name: name, nameLength: ((ushort)name.Length)),
                error: 0
            );

            if (extension == 0) {
                return;
            }

            var present = Marshal.ReadByte(ofs: 8, ptr: extension);
            var opcode = Marshal.ReadByte(ofs: 9, ptr: extension);

            Libc.free(pointer: extension);

            if (present == 0) {
                return;
            }

            var version = XcbInput.xcb_input_xi_query_version_reply(
                connection: m_connection,
                cookie: XcbInput.xcb_input_xi_query_version(connection: m_connection, majorVersion: 2, minorVersion: 2),
                error: 0
            );

            if (version == 0) {
                return;
            }

            var major = ((ushort)Marshal.ReadInt16(ofs: 8, ptr: version));
            var minor = ((ushort)Marshal.ReadInt16(ofs: 10, ptr: version));

            Libc.free(pointer: version);

            if ((major < 2) || ((major == 2) && (minor < 2))) {
                return;
            }

            // One xcb_input_event_mask_t: deviceid in the low half-word and mask_len (one word) in the high, as a
            // little-endian target lays the two uint16 fields out, then the mask word.
            _ = XcbInput.xcb_input_xi_select_events(
                connection: m_connection,
                maskCount: 1,
                masks: [(XiAllMasterDevices | (1u << 16)), ((1u << XiTouchBegin) | (1u << XiTouchUpdate) | (1u << XiTouchEnd))],
                window: m_window
            );
            m_xinputOpcode = opcode;
        } catch (DllNotFoundException) {
        } catch (EntryPointNotFoundException) {
        }
    }
    // Whether an event is one of this window's XI2 touch events, which it then enqueues.
    private bool TryHandleTouch(nint eventPointer, byte responseType) {
        if (
            (responseType != XcbGenericEvent) ||
            (m_xinputOpcode == 0) ||
            (Marshal.ReadByte(ofs: 1, ptr: eventPointer) != m_xinputOpcode)
        ) {
            return false;
        }

        var eventType = ((ushort)Marshal.ReadInt16(ofs: 8, ptr: eventPointer));
        var phase = eventType switch {
            XiTouchBegin => CommandPhase.Started,
            XiTouchUpdate => CommandPhase.Active,
            XiTouchEnd => CommandPhase.Completed,
            _ => ((CommandPhase?)null),
        };

        if (phase is not { } touchPhase) {
            return false;
        }

        var touchId = ((uint)Marshal.ReadInt32(ofs: 16, ptr: eventPointer));
        var x = (Marshal.ReadInt32(ofs: 44, ptr: eventPointer) / Fixed1616Scale);
        var y = (Marshal.ReadInt32(ofs: 48, ptr: eventPointer) / Fixed1616Scale);
        var source = ((ushort)Marshal.ReadInt16(ofs: 56, ptr: eventPointer));

        _ = m_pendingInput.EnqueueContact(
            device: ContactDevice.Touch,
            deviceId: InputDeviceId.FromConnectionKey(key: $"xi2-touch-{source}"),
            osId: touchId,
            phase: touchPhase,
            position: new Vector2(x: x, y: y),
            pressure: float.NaN
        );

        return true;
    }
}
