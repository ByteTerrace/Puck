using System.Runtime.Versioning;

namespace Puck.Platform.Windows;

/// <summary>
/// The Windows <see cref="ICameraControlSurface"/> both camera sessions share: maps the neutral
/// <see cref="CameraControl"/> vocabulary onto the media source's <see cref="IAMCameraControl"/>/
/// <see cref="IAMVideoProcAmp"/> implementations (Media Foundation capture sources answer both, and the controls live
/// on the SOURCE — independent of which tier reads frames, and live mid-stream). The process runs MTA throughout, so
/// the render-pump thread calls the grabber-thread-created source directly, same-apartment. A set clamps and
/// step-snaps into the device-reported range and switches the control to manual; a reset restores the driver default
/// (automatic where the device supports it). A device without the control — or a source without the interface —
/// reports <see langword="false"/>, never throws.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32CameraControlSurface(object mediaSource) : ICameraControlSurface {
    private const int FlagAuto = 1;
    private const int FlagManual = 2;

    private readonly IAMVideoProcAmp? m_amp = (mediaSource as IAMVideoProcAmp);
    private readonly IAMCameraControl? m_camera = (mediaSource as IAMCameraControl);

    /// <inheritdoc/>
    public bool TryGet(CameraControl control, out int value, out bool auto) {
        value = 0;
        auto = false;

        if (!TryMap(control: control, isCamera: out var isCamera, property: out var property)) {
            return false;
        }

        var flags = 0;
        var hr = -1;

        if (isCamera) {
            if (m_camera is not null) {
                hr = m_camera.Get(Property: property, lValue: out value, pFlags: out flags);
            }
        } else if (m_amp is not null) {
            hr = m_amp.Get(Property: property, lValue: out value, pFlags: out flags);
        }

        if (hr < 0) {
            return false;
        }

        auto = ((flags & FlagAuto) != 0);

        return true;
    }
    /// <inheritdoc/>
    public bool TryGetRange(CameraControl control, out CameraControlRange range) {
        range = default;

        if (!TryMap(control: control, isCamera: out var isCamera, property: out var property)) {
            return false;
        }

        var minimum = 0;
        var maximum = 0;
        var step = 0;
        var defaultValue = 0;
        var caps = 0;
        var hr = -1;

        if (isCamera) {
            if (m_camera is not null) {
                hr = m_camera.GetRange(Property: property, pMin: out minimum, pMax: out maximum, pSteppingDelta: out step, pDefault: out defaultValue, pCapsFlags: out caps);
            }
        } else if (m_amp is not null) {
            hr = m_amp.GetRange(Property: property, pMin: out minimum, pMax: out maximum, pSteppingDelta: out step, pDefault: out defaultValue, pCapsFlags: out caps);
        }

        if (hr < 0) {
            return false;
        }

        range = new CameraControlRange(
            Default: defaultValue,
            Maximum: maximum,
            Minimum: minimum,
            Step: step,
            SupportsAuto: ((caps & FlagAuto) != 0)
        );

        return true;
    }
    /// <inheritdoc/>
    public bool TryResetAuto(CameraControl control) {
        if (
            !TryGetRange(control: control, range: out var range) ||
            !TryMap(control: control, isCamera: out var isCamera, property: out var property)
        ) {
            return false;
        }

        var flags = (range.SupportsAuto ? FlagAuto : FlagManual);

        return ((isCamera
            ? m_camera!.Set(Property: property, lValue: range.Default, Flags: flags)
            : m_amp!.Set(Property: property, lValue: range.Default, Flags: flags)
        ) >= 0);
    }
    /// <inheritdoc/>
    public bool TrySet(CameraControl control, int value) {
        if (
            !TryGetRange(control: control, range: out var range) ||
            !TryMap(control: control, isCamera: out var isCamera, property: out var property)
        ) {
            return false;
        }

        var clamped = Math.Clamp(value: value, min: range.Minimum, max: range.Maximum);

        if (range.Step > 1) {
            clamped = Math.Clamp(
                value: (range.Minimum + ((((clamped - range.Minimum) + (range.Step / 2)) / range.Step) * range.Step)),
                min: range.Minimum,
                max: range.Maximum
            );
        }

        return ((isCamera
            ? m_camera!.Set(Property: property, lValue: clamped, Flags: FlagManual)
            : m_amp!.Set(Property: property, lValue: clamped, Flags: FlagManual)
        ) >= 0);
    }

    // The two interfaces' own KSPROPERTY ordinals per neutral control.
    private static bool TryMap(CameraControl control, out bool isCamera, out int property) {
        (isCamera, property) = (control switch {
            CameraControl.Pan => (true, 0),
            CameraControl.Tilt => (true, 1),
            CameraControl.Zoom => (true, 3),
            CameraControl.Exposure => (true, 4),
            CameraControl.Focus => (true, 6),
            CameraControl.Brightness => (false, 0),
            CameraControl.Contrast => (false, 1),
            CameraControl.Saturation => (false, 3),
            CameraControl.Sharpness => (false, 4),
            CameraControl.WhiteBalance => (false, 7),
            CameraControl.BacklightCompensation => (false, 8),
            CameraControl.Gain => (false, 9),
            _ => (false, -1),
        });

        return (property >= 0);
    }
}
