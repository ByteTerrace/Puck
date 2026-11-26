using System.Numerics;
using Puck.Commands;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

// Touch and pen contacts over WM_POINTER (Windows 8 and later). Sources, each consulted for what it settles:
// - WM_POINTERDOWN (learn.microsoft.com/windows/win32/inputmsg/wm-pointerdown): the message values, the pointer id in
//   the low word of wParam and the pointer flags in its high word, lParam in physical screen coordinates, "If an
//   application processes this message, it should return zero", and "If a gesture is not recognized, DefWindowProc
//   may generate mouse input", which is why every touch and pen message is answered here and never passed on.
// - POINTER_INFO, POINTER_TOUCH_INFO, POINTER_PEN_INFO (learn.microsoft.com/windows/win32/api/winuser): the layouts,
//   sourceDevice as a raw-input device handle, and pressure on 0..1024 behind TOUCH_MASK_PRESSURE / PEN_MASK_PRESSURE.
// - System Events and Mouse Messages (learn.microsoft.com/windows/win32/tablet/system-events-and-mouse-messages): a
//   mouse message the system synthesized from pen or touch carries MI_WP_SIGNATURE (0xFF515700 under the mask
//   0xFFFFFF00) in GetMessageExtraInfo, and bit 0x80 tells touch from pen.
// - SDL (src/video/windows/SDL_windowsevents.c), a second implementation: it drops mouse messages and raw mouse
//   packets whose extra information carries that signature (reading RAWMOUSE.ulExtraInformation for raw input, which
//   the Microsoft page does not mention), reads pen over WM_POINTER with pressure divided by 1024, and reads touch over
//   WM_TOUCH instead. Puck reads touch over WM_POINTER too: one message family for both digitizers, a pointer id
//   stable for the contact's life, and no RegisterTouchWindow, which would trade WM_POINTER for WM_TOUCH.
// Mouse and touchpad pointers (PT_MOUSE, PT_TOUCHPAD) arrive as WM_POINTER only after EnableMouseInPointer, which this
// window never calls, so they stay on the mouse path; one that does arrive is passed to DefWindowProc untouched.
internal sealed partial class Win32NativeWindow {
    private const uint WmPointerUpdate = 0x0245;
    private const uint WmPointerDown = 0x0246;
    private const uint WmPointerUp = 0x0247;
    private const uint WmPointerCaptureChanged = 0x024C;
    private const uint PointerTypeTouch = 2;
    private const uint PointerTypePen = 3;
    private const uint PointerFlagInContact = 0x0004;
    private const uint PointerFlagCanceled = 0x8000;
    private const uint TouchMaskPressure = 0x0004;
    private const uint PenMaskPressure = 0x0001;
    private const float PointerPressureScale = 1024f;
    private const uint ContactSignature = 0xFF515700;
    private const uint ContactSignatureMask = 0xFFFFFF00;

    // Whether a mouse message's or raw mouse packet's extra information marks it as synthesized from a pen or touch
    // contact, which this window already reads as the contact itself.
    private static bool IsFromContact(uint extraInformation) => ((extraInformation & ContactSignatureMask) == ContactSignature);
    // Whether the mouse message being dispatched was synthesized from a contact.
    private static bool IsMouseFromContact(uint message) => (
        (message is WmMouseMove or WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or WmMButtonDown or WmMButtonUp or WmXButtonDown or WmXButtonUp) &&
        IsFromContact(extraInformation: unchecked((uint)User32.GetMessageExtraInfo().ToInt64()))
    );
    private nint HandleContact(nint windowHandle, uint message, nint wParam, nint lParam) {
        var pointerId = unchecked((uint)(wParam.ToInt64() & 0xFFFF));

        if (
            !User32.GetPointerType(pointerId: pointerId, pointerType: out var pointerType) ||
            (pointerType is not (PointerTypeTouch or PointerTypePen))
        ) {
            return User32.DefWindowProc(
                lParam: lParam,
                message: message,
                wParam: wParam,
                windowHandle: windowHandle
            );
        }

        var device = ((pointerType == PointerTypePen) ? ContactDevice.Pen : ContactDevice.Touch);
        var flags = unchecked((uint)((wParam.ToInt64() >> 16) & 0xFFFF));
        var phase = message switch {
            WmPointerDown => CommandPhase.Started,
            WmPointerUp => CommandPhase.Completed,
            WmPointerCaptureChanged => CommandPhase.Canceled,
            _ => CommandPhase.Active,
        };

        if ((flags & PointerFlagCanceled) != 0) {
            phase = CommandPhase.Canceled;
        }
        // A hovering pen updates without touching; only contact with the glass is a contact.
        if ((phase == CommandPhase.Active) && ((flags & PointerFlagInContact) == 0)) {
            return 0;
        }

        var (sourceDevice, pressure) = ReadContactDetail(device: device, pointerId: pointerId);
        Vector2? position = null;

        // WM_POINTERCAPTURECHANGED's lParam names the window taking capture, not a point; the queue cancels at the
        // contact's last position.
        if (message != WmPointerCaptureChanged) {
            var point = new Point {
                X = GetSignedLowWord(value: lParam),
                Y = GetSignedHighWord(value: lParam),
            };

            _ = User32.ScreenToClient(point: ref point, windowHandle: windowHandle);
            position = new Vector2(x: point.X, y: point.Y);
        }

        _ = m_pendingInput.EnqueueContact(
            device: device,
            deviceId: ResolveRawDeviceId(deviceHandle: sourceDevice),
            osId: pointerId,
            phase: phase,
            position: position,
            pressure: pressure
        );

        return 0;
    }
    // The digitizer's raw-input handle and the contact's pressure, 0 to 1 (NaN when the device reports none).
    private static (nint SourceDevice, float Pressure) ReadContactDetail(ContactDevice device, uint pointerId) {
        if (device == ContactDevice.Pen) {
            return (User32.GetPointerPenInfo(penInfo: out var pen, pointerId: pointerId)
                ? (pen.PointerInfo.SourceDevice, (((pen.PenMask & PenMaskPressure) != 0) ? (pen.Pressure / PointerPressureScale) : float.NaN))
                : (0, float.NaN));
        }

        return (User32.GetPointerTouchInfo(pointerId: pointerId, touchInfo: out var touch)
            ? (touch.PointerInfo.SourceDevice, (((touch.TouchMask & TouchMaskPressure) != 0) ? (touch.Pressure / PointerPressureScale) : float.NaN))
            : (0, float.NaN));
    }
}
