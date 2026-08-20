using System.Buffers.Binary;
using System.Runtime.Versioning;
using WinRT;

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
internal sealed unsafe class Win32CameraControlSurface(object mediaSource) : ICameraControlSurface {
    private const int FlagAuto = 1;
    private const int FlagManual = 2;
    // The Logitech video XU's discrete field-of-view selector: byte 0/1/2 = 90/78/65 degrees, hardware-verified on
    // the BRIO (a mid-stream set crops the live sensor readout; a set on an idle filter is accepted and ignored).
    private const uint FovSelector = 2;
    private const uint KsPropertyTypeBasicSupport = 0x00000200;
    private const uint KsPropertyTypeGet = 0x00000001;
    private const uint KsPropertyTypeSet = 0x00000002;
    private const uint KsPropertyTypeTopology = 0x10000000;

    private readonly IAMVideoProcAmp? m_amp = QueryInterface<IAMVideoProcAmp>(source: mediaSource);
    private readonly IAMCameraControl? m_camera = QueryInterface<IAMCameraControl>(source: mediaSource);
    private readonly IKsControl? m_ksControl = QueryInterface<IKsControl>(source: mediaSource);

    // The vendor XU's topology node id, discovered once by sweeping BASICSUPPORT on the FOV selector (node 6 on the
    // BRIO; devices differ): null = not yet probed, -1 = no Logitech video XU on this device.
    private int? m_vendorNode;

    /// <inheritdoc/>
    public bool TryGet(CameraControl control, out int value, out bool auto) {
        value = 0;
        auto = false;

        if (CameraControl.FieldOfView == control) {
            if (!TryVendorRead(selector: FovSelector, value: out var step)) {
                return false;
            }

            value = FovDegrees(step: step);

            return true;
        }

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

        if (CameraControl.FieldOfView == control) {
            if (VendorNode() < 0) {
                return false;
            }

            // The envelope in DEGREES; the device snaps a set to its discrete supported values, so Step reports the
            // envelope shape rather than a real arithmetic stride.
            range = new CameraControlRange(
                Default: 90,
                Maximum: 90,
                Minimum: 65,
                Step: 1,
                SupportsAuto: false
            );

            return true;
        }

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
        if (CameraControl.FieldOfView == control) {
            return TryVendorWrite(selector: FovSelector, value: 0);
        }

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
        if (CameraControl.FieldOfView == control) {
            return TryVendorWrite(selector: FovSelector, value: FovStep(degrees: value));
        }

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

    /// <inheritdoc/>
    public bool TryVendorRead(uint selector, out int value) {
        value = 0;

        var node = VendorNode();

        if (node < 0) {
            return false;
        }

        Span<byte> data = stackalloc byte[1];

        if (!TryKs(data: data, node: ((uint)node), selector: selector, type: (KsPropertyTypeGet | KsPropertyTypeTopology))) {
            return false;
        }

        value = data[0];

        return true;
    }
    /// <inheritdoc/>
    public bool TryVendorWrite(uint selector, int value) {
        var node = VendorNode();

        if (node < 0) {
            return false;
        }

        Span<byte> data = [((byte)Math.Clamp(value: value, min: 0, max: 255))];

        return TryKs(data: data, node: ((uint)node), selector: selector, type: (KsPropertyTypeSet | KsPropertyTypeTopology));
    }

    // Byte step <-> degrees for the discrete FOV selector: 0/1/2 = 90/78/65. A degrees set snaps to the nearest step
    // (midpoints 84 and 71.5).
    private static int FovDegrees(int step) => (step switch {
        0 => 90,
        1 => 78,
        _ => 65,
    });
    private static int FovStep(int degrees) => ((degrees >= 84)
        ? 0
        : ((degrees >= 72) ? 1 : 2)
    );
    // One KSP_NODE-shaped property call against the Logitech video XU: 16-byte set GUID + selector + flags + node id
    // + reserved, all little-endian, exactly the wire shape ksproxy forwards to the UVC extension unit.
    private bool TryKs(uint selector, uint type, uint node, Span<byte> data) {
        if (m_ksControl is not { } ksControl) {
            return false;
        }

        Span<byte> property = stackalloc byte[32];

        _ = MfInterop.LOGITECH_VIDEO_XU.TryWriteBytes(destination: property);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property[16..], value: selector);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property[20..], value: type);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property[24..], value: node);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property[28..], value: 0u);

        fixed (byte* propertyPointer = property)
        fixed (byte* dataPointer = data) {
            var succeeded = (ksControl.KsProperty(
                Property: ((nint)propertyPointer),
                PropertyLength: 32,
                PropertyData: ((nint)dataPointer),
                DataLength: ((uint)data.Length),
                BytesReturned: out var bytesReturned
            ) >= 0);

            return (succeeded && (((type & KsPropertyTypeGet) == 0) || (bytesReturned >= data.Length)));
        }
    }
    // Discovers (once) which topology node carries the Logitech video XU by asking each candidate node whether it
    // supports the FOV selector at all — BASICSUPPORT is a read that no device misinterprets as a change.
    private int VendorNode() {
        if (m_vendorNode is { } known) {
            return known;
        }

        Span<byte> support = stackalloc byte[4];

        for (var node = 0u; (node < 16u); node++) {
            if (TryKs(data: support, node: node, selector: FovSelector, type: (KsPropertyTypeBasicSupport | KsPropertyTypeTopology))) {
                m_vendorNode = ((int)node);

                return ((int)node);
            }
        }

        m_vendorNode = -1;

        return -1;
    }
    // A raw Media Foundation media source is an ordinary COM RCW and casts directly. A WinRT projection such as
    // VideoDeviceController must be queried through CsWinRT's As<T>; treating it as an ordinary C# cast silently
    // removes the entire control surface from dual-reader sessions.
    private static T? QueryInterface<T>(object source) where T : class {
        if (source is T direct) {
            return direct;
        }

        try {
            return source.As<T>();
        } catch (Exception) {
            return null;
        }
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
