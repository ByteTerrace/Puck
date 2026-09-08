using System.Buffers.Binary;
using System.Runtime.Versioning;
using WinRT;
using Windows.Media.Devices;

namespace Puck.Platform.Windows;

/// <summary>
/// The Windows <see cref="ICameraControlSurface"/> both camera sessions share: maps the neutral
/// <see cref="CameraControl"/> vocabulary onto a WinRT <see cref="VideoDeviceController"/> when MediaCapture owns the
/// graph, or the media source's legacy <see cref="IAMCameraControl"/>/<see cref="IAMVideoProcAmp"/> implementations
/// when a Media Foundation source reader owns it. Controls live on the physical source — independent of which tier
/// reads frames, and live mid-stream. A set clamps and step-snaps into the device-reported range and switches the
/// control to manual; a reset restores the driver default (automatic where the device supports it). Every projection
/// and driver call is contained here: a device without the control — including a COM proxy whose interface query is
/// deferred until first use — reports <see langword="false"/>, never throws.
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
    private readonly IModernCameraControlSurface? m_modern = CreateModernControlSurface(source: mediaSource);

    // The vendor XU's topology node id, discovered once by sweeping BASICSUPPORT on the FOV selector (node 6 on the
    // BRIO; devices differ): null = not yet probed, -1 = no Logitech video XU on this device.
    private int? m_vendorNode;

    /// <inheritdoc/>
    public bool TryGet(CameraControl control, out int value, out bool auto) {
        value = 0;
        auto = false;

        try {
            return TryGetCore(auto: out auto, control: control, value: out value);
        } catch (Exception) {
            value = 0;
            auto = false;

            return false;
        }
    }

    private bool TryGetCore(CameraControl control, out int value, out bool auto) {
        value = 0;
        auto = false;

        if (CameraControl.FieldOfView == control) {
            if (!TryVendorRead(selector: FovSelector, value: out var step)) {
                return false;
            }

            value = FovDegrees(step: step);

            return true;
        }

        if ((m_modern?.TryGet(auto: out auto, control: control, value: out value) ?? false)) {
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

        try {
            return TryGetRangeCore(control: control, range: out range);
        } catch (Exception) {
            range = default;

            return false;
        }
    }

    private bool TryGetRangeCore(CameraControl control, out CameraControlRange range) {
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

        return ((m_modern?.TryGetRange(control: control, range: out range) ?? false) || TryLegacyRange(control: control, range: out range));
    }
    // The classic IAMCameraControl/IAMVideoProcAmp envelope; the WinRT projection answers first where it carries the
    // control, so this is the fallback for members it lacks and for source-reader sessions.
    private bool TryLegacyRange(CameraControl control, out CameraControlRange range) {
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
                hr = m_camera.GetRange(Property: property, pCapsFlags: out caps, pDefault: out defaultValue, pMax: out maximum, pMin: out minimum, pSteppingDelta: out step);
            }
        } else if (m_amp is not null) {
            hr = m_amp.GetRange(Property: property, pCapsFlags: out caps, pDefault: out defaultValue, pMax: out maximum, pMin: out minimum, pSteppingDelta: out step);
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
        try {
            return TryResetAutoCore(control: control);
        } catch (Exception) {
            return false;
        }
    }

    private bool TryResetAutoCore(CameraControl control) {
        if (CameraControl.FieldOfView == control) {
            return TryVendorWriteCore(selector: FovSelector, value: 0);
        }

        if ((m_modern?.TryResetAuto(control: control) ?? false)) {
            return true;
        }

        if (
            !TryLegacyRange(control: control, range: out var range) ||
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
        try {
            return TrySetCore(control: control, value: value);
        } catch (Exception) {
            return false;
        }
    }

    private bool TrySetCore(CameraControl control, int value) {
        if (CameraControl.FieldOfView == control) {
            return TryVendorWriteCore(selector: FovSelector, value: FovStep(degrees: value));
        }

        if ((m_modern?.TrySet(control: control, value: value) ?? false)) {
            return true;
        }

        if (
            !TryLegacyRange(control: control, range: out var range) ||
            !TryMap(control: control, isCamera: out var isCamera, property: out var property)
        ) {
            return false;
        }

        var clamped = SnapToRange(range: range, value: value);

        return ((isCamera
            ? m_camera!.Set(Flags: FlagManual, Property: property, lValue: clamped)
            : m_amp!.Set(Flags: FlagManual, Property: property, lValue: clamped)
        ) >= 0);
    }

    /// <inheritdoc/>
    public bool TryVendorRead(uint selector, out int value) {
        value = 0;

        try {
            return TryVendorReadCore(selector: selector, value: out value);
        } catch (Exception) {
            value = 0;

            return false;
        }
    }

    private bool TryVendorReadCore(uint selector, out int value) {
        value = 0;

        var node = VendorNode();

        if (node < 0) {
            return false;
        }

        Span<byte> data = stackalloc byte[1];

        if (!TryKs(data: data, node: ((uint)node), selector: selector, type: KsPropertyTypeGet | KsPropertyTypeTopology)) {
            return false;
        }

        value = data[0];

        return true;
    }

    /// <inheritdoc/>
    public bool TryVendorWrite(uint selector, int value) {
        try {
            return TryVendorWriteCore(selector: selector, value: value);
        } catch (Exception) {
            return false;
        }
    }

    private bool TryVendorWriteCore(uint selector, int value) {
        var node = VendorNode();

        if (node < 0) {
            return false;
        }

        Span<byte> data = [((byte)Math.Clamp(max: 255, min: 0, value: value))];

        return TryKs(data: data, node: ((uint)node), selector: selector, type: KsPropertyTypeSet | KsPropertyTypeTopology);
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
        Span<byte> property = stackalloc byte[32];

        _ = MfInterop.LOGITECH_VIDEO_XU.TryWriteBytes(destination: property);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property[16..], value: selector);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property[20..], value: type);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property[24..], value: node);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property[28..], value: 0u);

        if (m_ksControl is { } ksControl) {
            fixed (byte* propertyPointer = property)
            fixed (byte* dataPointer = data) {
                var succeeded = (ksControl.KsProperty(
                    Property: ((nint)propertyPointer),
                    PropertyLength: 32,
                    PropertyData: ((nint)dataPointer),
                    DataLength: ((uint)data.Length),
                    BytesReturned: out var bytesReturned
                ) >= 0);

                var returnsData = ((type & (KsPropertyTypeGet | KsPropertyTypeBasicSupport)) != 0);

                if (succeeded && (!returnsData || (bytesReturned >= data.Length))) {
                    return true;
                }
            }
        }

        // The WinRT extended-property door issues a plain get or set, so it cannot answer a BASICSUPPORT probe; a node
        // sweep routed through it would latch whichever node tolerates a value read. Failed direct value traffic falls
        // back to it, while a failed BASICSUPPORT probe continues the topology-node sweep.
        if ((type & KsPropertyTypeBasicSupport) != 0) {
            return false;
        }

        return (m_modern?.TryKs(data: data, property: property, set: ((type & KsPropertyTypeSet) != 0)) ?? false);
    }
    // Discovers (once) which topology node carries the Logitech video XU by asking each candidate node whether it
    // supports the FOV selector at all — BASICSUPPORT is a read that no device misinterprets as a change.
    private int VendorNode() {
        if (m_vendorNode is { } known) {
            return known;
        }

        Span<byte> support = stackalloc byte[4];

        for (var node = 0u; (node < 16u); node++) {
            if (TryKs(data: support, node: node, selector: FovSelector, type: KsPropertyTypeBasicSupport | KsPropertyTypeTopology)) {
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
    private static IModernCameraControlSurface? CreateModernControlSurface(object source) {
        if (
            !OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 15063) ||
            (source is not VideoDeviceController controller)
        ) {
            return null;
        }

        return new ModernCameraControlSurface(controller: controller);
    }
    private static int SnapToRange(int value, CameraControlRange range) {
        var clamped = Math.Clamp(value: value, min: range.Minimum, max: range.Maximum);

        if (range.Step <= 1) {
            return clamped;
        }

        var offset = (((long)clamped) - range.Minimum);
        var snapped = (((long)range.Minimum) + (((offset + (range.Step / 2L)) / range.Step) * range.Step));

        return ((int)Math.Clamp(value: snapped, min: range.Minimum, max: range.Maximum));
    }
    private static bool TryInt(double value, out int result) {
        if (!double.IsFinite(d: value) || (value < int.MinValue) || (value > int.MaxValue)) {
            result = 0;

            return false;
        }

        result = ((int)Math.Round(mode: MidpointRounding.AwayFromZero, value: value));

        return true;
    }

    // Keeps WinRT's Windows-10 versioned surface behind an unannotated internal seam. Construction is guarded above,
    // while the owning camera session can continue to support legacy Media Foundation sources on older Windows.
    private interface IModernCameraControlSurface {
        bool TryGet(CameraControl control, out int value, out bool auto);
        bool TryGetRange(CameraControl control, out CameraControlRange range);
        bool TryKs(ReadOnlySpan<byte> property, Span<byte> data, bool set);
        bool TryResetAuto(CameraControl control);
        bool TrySet(CameraControl control, int value);
    }
    [SupportedOSPlatform("windows10.0.15063")]
    private sealed class ModernCameraControlSurface(VideoDeviceController controller) : IModernCameraControlSurface {
        public bool TryGet(CameraControl control, out int value, out bool auto) {
            value = 0;
            auto = false;

            var deviceControl = Resolve(control: control);

            if (
                (deviceControl is null) ||
                !deviceControl.TryGetValue(value: out var current) ||
                !TryInt(result: out value, value: current)
            ) {
                return false;
            }

            _ = deviceControl.TryGetAuto(value: out auto);

            return true;
        }
        public bool TryGetRange(CameraControl control, out CameraControlRange range) {
            range = default;

            var deviceControl = Resolve(control: control);

            if (deviceControl is null) {
                return false;
            }

            var capabilities = deviceControl.Capabilities;

            if (
                !capabilities.Supported ||
                !TryInt(value: capabilities.Min, result: out var modernMinimum) ||
                !TryInt(value: capabilities.Max, result: out var modernMaximum) ||
                !TryInt(value: capabilities.Step, result: out var modernStep) ||
                !TryInt(value: capabilities.Default, result: out var modernDefault)
            ) {
                return false;
            }

            range = new CameraControlRange(
                Default: modernDefault,
                Maximum: modernMaximum,
                Minimum: modernMinimum,
                Step: Math.Max(val1: 1, val2: modernStep),
                SupportsAuto: capabilities.AutoModeSupported
            );

            return true;
        }
        public bool TryKs(ReadOnlySpan<byte> property, Span<byte> data, bool set) {
            if (set) {
                return (VideoDeviceControllerSetDevicePropertyStatus.Success == controller.SetDevicePropertyByExtendedId(
                    extendedPropertyId: property.ToArray(),
                    propertyValue: data.ToArray()
                ));
            }

            var result = controller.GetDevicePropertyByExtendedId(
                extendedPropertyId: property.ToArray(),
                maxPropertyValueSize: 128u
            );

            if (
                (VideoDeviceControllerGetDevicePropertyStatus.Success != result.Status) ||
                (result.Value is not byte[] bytes) ||
                (bytes.Length < data.Length)
            ) {
                return false;
            }

            bytes.AsSpan(start: 0, length: data.Length).CopyTo(destination: data);

            return true;
        }
        public bool TryResetAuto(CameraControl control) {
            var deviceControl = Resolve(control: control);

            if ((deviceControl is null) || !TryGetRange(control: control, range: out var range)) {
                return false;
            }

            return (range.SupportsAuto
                ? deviceControl.TrySetAuto(value: true)
                : deviceControl.TrySetValue(value: range.Default)
            );
        }
        public bool TrySet(CameraControl control, int value) {
            var deviceControl = Resolve(control: control);

            if ((deviceControl is null) || !TryGetRange(control: control, range: out var range)) {
                return false;
            }

            if (range.SupportsAuto && !deviceControl.TrySetAuto(value: false)) {
                return false;
            }

            return deviceControl.TrySetValue(value: SnapToRange(range: range, value: value));
        }

        // Not every classic VideoProcAmp member has a WinRT projection. Those members return null and deliberately
        // fall through to the legacy COM map when the source supplies it.
        private MediaDeviceControl? Resolve(CameraControl control) => (control switch {
            CameraControl.Pan => controller.Pan,
            CameraControl.Tilt => controller.Tilt,
            CameraControl.Zoom => controller.Zoom,
            CameraControl.Exposure => controller.Exposure,
            CameraControl.Focus => controller.Focus,
            CameraControl.Brightness => controller.Brightness,
            CameraControl.Contrast => controller.Contrast,
            CameraControl.WhiteBalance => controller.WhiteBalance,
            CameraControl.BacklightCompensation => controller.BacklightCompensation,
            _ => null,
        });
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
