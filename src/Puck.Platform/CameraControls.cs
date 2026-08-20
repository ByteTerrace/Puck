namespace Puck.Platform;

/// <summary>A standard UVC camera/image control a live camera session can read and drive on its open device — the
/// classic camera-control and video-proc-amp vocabulary, platform-neutral. The device is authoritative for which
/// controls exist and for their ranges (<see cref="ICameraControlSurface.TryGetRange"/>); a control the device does not
/// implement simply reports <see langword="false"/> everywhere.</summary>
public enum CameraControl {
    /// <summary>Horizontal framing offset (digital pan on webcams).</summary>
    Pan,
    /// <summary>Vertical framing offset (digital tilt on webcams).</summary>
    Tilt,
    /// <summary>Magnification — on sensor-cropping webcams a region-of-interest zoom (e.g. 100..500 = 1x..5x).</summary>
    Zoom,
    /// <summary>Exposure time, typically log2 seconds (e.g. -5 = 1/32 s). Usually auto-capable.</summary>
    Exposure,
    /// <summary>Focus distance. Usually auto-capable.</summary>
    Focus,
    /// <summary>Image brightness offset.</summary>
    Brightness,
    /// <summary>Image contrast.</summary>
    Contrast,
    /// <summary>Color saturation (0 is grayscale on most devices).</summary>
    Saturation,
    /// <summary>Edge sharpening strength.</summary>
    Sharpness,
    /// <summary>Sensor gain (ISO-like amplification).</summary>
    Gain,
    /// <summary>White-balance color temperature in kelvin. Usually auto-capable.</summary>
    WhiteBalance,
    /// <summary>Backlight compensation (devices commonly report a 0..1 toggle range).</summary>
    BacklightCompensation,
}

/// <summary>One control's device-reported envelope: the value range, its stepping, the driver default, and whether the
/// device can run the control automatically.</summary>
/// <param name="Minimum">The smallest accepted value.</param>
/// <param name="Maximum">The largest accepted value.</param>
/// <param name="Step">The stepping delta between accepted values (values are snapped onto it).</param>
/// <param name="Default">The driver's default value.</param>
/// <param name="SupportsAuto">Whether the device can drive this control automatically.</param>
public readonly record struct CameraControlRange(int Minimum, int Maximum, int Step, int Default, bool SupportsAuto);

/// <summary>The live control surface a camera session exposes over its open device, shared by both capture tiers (the
/// controls live on the capture source, independent of how frames are read). Every member is best-effort and
/// non-throwing: a control the device or platform does not implement reports <see langword="false"/>.</summary>
public interface ICameraControlSurface {
    /// <summary>Reads a control's device-reported range and capabilities.</summary>
    /// <param name="control">The control to describe.</param>
    /// <param name="range">The device's envelope for the control when supported.</param>
    /// <returns>Whether the device implements the control.</returns>
    bool TryGetRange(CameraControl control, out CameraControlRange range);
    /// <summary>Reads a control's current value and whether the device is currently driving it automatically.</summary>
    /// <param name="control">The control to read.</param>
    /// <param name="value">The current value when supported.</param>
    /// <param name="auto">Whether the device is running the control automatically.</param>
    /// <returns>Whether the device implements the control.</returns>
    bool TryGet(CameraControl control, out int value, out bool auto);
    /// <summary>Sets a control to a manual value, clamped and step-snapped into the device's reported range.</summary>
    /// <param name="control">The control to set.</param>
    /// <param name="value">The desired value (device-clamped).</param>
    /// <returns>Whether the device accepted the set.</returns>
    bool TrySet(CameraControl control, int value);
    /// <summary>Restores a control to its driver default — automatic mode where the device supports it, else the
    /// default value manually.</summary>
    /// <param name="control">The control to reset.</param>
    /// <returns>Whether the device accepted the reset.</returns>
    bool TryResetAuto(CameraControl control);
}
