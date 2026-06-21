using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Puck.Input.Hid;
using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// Parses the Sony DualSense (PS5) controller and drives its rumble + lightbar over HID. The USB input report
/// 0x01 (and the Bluetooth full report 0x31) carry sticks, analog triggers, buttons, and the gyro/accel IMU; the
/// USB output report 0x02 drives the two "compatible vibration" motors and the lightbar / player LEDs. USB needs
/// no handshake — it streams the full report immediately. Bluetooth output (report 0x31 with a trailing CRC32)
/// and the Bluetooth full-mode request are not implemented, so rumble is USB-only.
/// </summary>
internal sealed class DualSenseController : IGamepadParser, IRumbleParser, ILedParser
{
    private const byte UsbInputReportId = 0x01;
    private const byte BluetoothInputReportId = 0x31;
    private const byte UsbOutputReportId = 0x02;
    // Feature report 0x05 carries the factory IMU calibration (per-axis gyro bias + sensitivity). Without it the
    // raw gyro counts have no correct per-device scale; the real sensitivity is ~64x the bare 1024 LSB/deg/s
    // resolution, so falling back to that bare figure reads ~64x too weak (see GyroRadiansPerSecondPerLsbFallback).
    private const byte CalibrationFeatureReportId = 0x05;
    private const int CalibrationFeatureReportLength = 41;
    // A freshly connected DualSense may not answer the calibration feature report immediately, so the read is
    // retried briefly before falling back to the uncalibrated scale.
    private const int CalibrationReadAttempts = 12;
    private const int CalibrationRetryDelayMilliseconds = 30;
    private const byte FlagCompatibleVibration = 0x01;  // valid_flag0: emulate the classic dual motors
    private const byte FlagHapticsSelect = 0x02;        // valid_flag0
    private const byte FlagLightbarControl = 0x04;      // valid_flag1: apply the lightbar RGB
    private const byte FlagPlayerIndicatorControl = 0x10; // valid_flag1: apply the 5 player LEDs

    // The two touchpad contacts are 4 bytes each, at common offsets 32 and 36 (after the IMU + timestamp block).
    private const int Touch0Offset = 32;
    private const int Touch1Offset = 36;
    // The touchpad reports 12-bit coordinates over a nominal 1920×1080 surface (DS_TOUCHPAD_WIDTH/HEIGHT).
    private const float TouchpadWidth = 1920f;
    private const float TouchpadHeight = 1080f;
    private const int StickCenter = 128;
    private const float StickRange = 127f;
    private const float StickDeadzone = 0.12f;
    private const byte TriggerThreshold = 8;
    private const float TriggerRange = 255f;
    // DualSense gyro resolution is 1024 LSB per deg/s (DS_GYRO_RES_PER_DEG_S), but the per-device factory
    // calibration sensitivity is ~64x that figure, so the bare 1024 value is NOT a usable uncalibrated scale —
    // it reads ~64x too weak and yaw (which has no gravity reference) goes effectively dead. The uncalibrated
    // fallback therefore uses 64x the bare nominal, replaced per-axis once feature report 0x05 is read.
    private const float GyroRadiansPerSecondPerLsbFallback = ((64f * (MathF.PI / 180f)) / 1024f);
    // DualSense accelerometer is 8192 LSB per g (DS_ACC_RES_PER_G); convert to g. The accel block follows the
    // gyro block (gyro at common 15..20, accel at 21..26), then a 32-bit sensor timestamp at common 27..30.
    private const int AccelerometerOffset = 21;
    private const float AccelerometerGPerLsb = (1f / 8192f);
    // Rate-limit equal-or-weaker rumble writes to a >=30 ms coalescing cadence so a per-tick streamer can't flood
    // the link. USB tolerates faster writes, but the deferred Bluetooth path needs this cadence to avoid dropping
    // the link.
    private const long RumbleWriteIntervalMilliseconds = 30L;

    // The 5-LED player-indicator bit patterns (the lightbar RGB comes from the shared GamepadPlayerColors).
    private static readonly byte[] PlayerLeds = [0x04, 0x0A, 0x15, 0x1B,];
    private readonly IHidDevice m_device;
    private readonly byte[] m_outputBuffer;
    private byte m_lastHigh;
    private byte m_lastLow;
    private byte m_lightbarBlue;
    private byte m_lightbarGreen;
    private byte m_lightbarRed;
    private byte m_playerLeds;
    private long m_lastRumbleSendTicks;
    private float m_lastRumbleIntensity;
    private readonly ImuOrientationTracker m_tracker = new();
    private Vector3 m_gyroCalibrationBias;          // factory zero-rate offset (raw LSB), from feature report 0x05
    private Vector3 m_gyroScale;                    // per-axis rad/s per (raw - bias) LSB

    /// <summary>Initializes a new instance of the <see cref="DualSenseController"/> class.</summary>
    /// <param name="device">The opened HID device handle for the DualSense.</param>
    public DualSenseController(IHidDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        var outputLength = device.OutputReportByteLength;

        m_device = device;
        m_outputBuffer = new byte[(outputLength > 0) ? outputLength : 64];
        // Uncalibrated fallback scale until the factory calibration is read in InitializeAsync, and the scale that
        // stays in effect if the calibration report cannot be read.
        m_gyroScale = new Vector3(value: GyroRadiansPerSecondPerLsbFallback);
    }

    /// <inheritdoc />
    public GamepadType Type => GamepadType.Ps5;
    /// <inheritdoc />
    // The DualSense has a 6-axis IMU and pressure-sensitive L2/R2 triggers.
    public GamepadInputCapabilities InputCapabilities => (GamepadInputCapabilities.Gyro | GamepadInputCapabilities.AnalogTriggers);

    /// <inheritdoc />
    public async ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default) {
        var color = GamepadPlayerColors.ForPlayer(playerIndex: playerIndex);

        m_lightbarBlue = color.Blue;
        m_lightbarGreen = color.Green;
        m_lightbarRed = color.Red;
        m_playerLeds = PlayerLeds[playerIndex & 3];

        // Read the factory IMU calibration so the gyro is correctly scaled (the orientation fusion depends on it).
        await LoadGyroCalibrationAsync(cancellationToken: cancellationToken);

        // USB streams the full 0x01 report with no handshake; send one output report to light the player's
        // lightbar/LED indicator (motors at rest), which also confirms the output-report layout end to end.
        await WriteOutputAsync(cancellationToken: cancellationToken);
    }

    // Reads the factory IMU calibration, retrying the feature-report read because a freshly connected pad may not
    // answer it immediately; falls back to the uncalibrated scale only after exhausting the retries.
    private async ValueTask LoadGyroCalibrationAsync(CancellationToken cancellationToken) {
        for (var attempt = 0; (attempt < CalibrationReadAttempts); ++attempt) {
            if (TryLoadGyroCalibration()) {
                return;
            }

            await Task.Delay(millisecondsDelay: CalibrationRetryDelayMilliseconds, cancellationToken: cancellationToken);
        }

        Console.Error.WriteLine(value: $"[gamepad] DualSense gyro calibration unavailable after {CalibrationReadAttempts} attempts; using nominal scale");
    }

    // Reads feature report 0x05 and derives each gyro axis's zero-rate bias and rad/s-per-LSB scale from the
    // factory calibration points: scale = (gyro_speed_plus + gyro_speed_minus) * (pi/180) / (|plus-bias| +
    // |minus-bias|). Returns false (leaving the nominal scale) if the report is unavailable or implausible.
    private bool TryLoadGyroCalibration() {
        Span<byte> report = stackalloc byte[CalibrationFeatureReportLength];

        report[0] = CalibrationFeatureReportId;

        if (!m_device.TryGetFeatureReport(buffer: report)) {
            return false;
        }

        var pitchBias = ReadInt16(report: report, offset: 1);
        var yawBias = ReadInt16(report: report, offset: 3);
        var rollBias = ReadInt16(report: report, offset: 5);
        var speed2x = (ReadInt16(report: report, offset: 19) + ReadInt16(report: report, offset: 21));
        var pitchDenominator = (Math.Abs(value: (ReadInt16(report: report, offset: 7) - pitchBias)) + Math.Abs(value: (ReadInt16(report: report, offset: 9) - pitchBias)));
        var yawDenominator = (Math.Abs(value: (ReadInt16(report: report, offset: 11) - yawBias)) + Math.Abs(value: (ReadInt16(report: report, offset: 13) - yawBias)));
        var rollDenominator = (Math.Abs(value: (ReadInt16(report: report, offset: 15) - rollBias)) + Math.Abs(value: (ReadInt16(report: report, offset: 17) - rollBias)));

        // Implausible calibration (would divide by zero, or the device hasn't filled the report yet) — report
        // failure so the caller retries rather than locking in a bad/zero scale.
        if ((0 == speed2x) || (0 == pitchDenominator) || (0 == yawDenominator) || (0 == rollDenominator)) {
            return false;
        }

        var radiansPerDegree = (MathF.PI / 180f);

        m_gyroCalibrationBias = new Vector3(x: pitchBias, y: yawBias, z: rollBias);
        m_gyroScale = new Vector3(
            x: ((speed2x * radiansPerDegree) / pitchDenominator),
            y: ((speed2x * radiansPerDegree) / yawDenominator),
            z: ((speed2x * radiansPerDegree) / rollDenominator)
        );

        return true;
    }
    private static short ReadInt16(ReadOnlySpan<byte> report, int offset) {
        return BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]);
    }

    /// <inheritdoc />
    public ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default) {
        var high = Math.Clamp(value: highFrequency, max: 1f, min: 0f);
        var low = Math.Clamp(value: lowFrequency, max: 1f, min: 0f);
        var intensity = MathF.Max(x: low, y: high);
        var now = Stopwatch.GetTimestamp();

        // Throttle equal-or-weaker updates inside the 30ms window; stops, the first write, and any intensity
        // increase always go through (so rumble-off and stronger effects stay instant). A dropped intermediate
        // weaker value persists until the next LED/light-bar write flushes the combined report, which is
        // imperceptible for continuous rumble. See RumbleWriteIntervalMilliseconds.
        if ((0f < intensity) && (intensity <= m_lastRumbleIntensity) && (0L != m_lastRumbleSendTicks)) {
            var elapsedMilliseconds = (((now - m_lastRumbleSendTicks) * 1000L) / Stopwatch.Frequency);

            if (elapsedMilliseconds < RumbleWriteIntervalMilliseconds) {
                return ValueTask.CompletedTask;
            }
        }

        m_lastRumbleIntensity = intensity;
        m_lastRumbleSendTicks = now;
        m_lastHigh = ((byte)(high * 255f));
        m_lastLow = ((byte)(low * 255f));

        return WriteOutputAsync(cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetLedAsync(LedColor color, CancellationToken cancellationToken = default) {
        m_lightbarRed = color.Red;
        m_lightbarGreen = color.Green;
        m_lightbarBlue = color.Blue;

        return WriteOutputAsync(cancellationToken: cancellationToken);
    }

    // Writes the USB output report 0x02 from the last-applied motor + lightbar + player-LED state. The DualSense
    // multiplexes motors and the light bar into one report, so always emitting the full state keeps a rumble
    // write from clearing the light bar and a LED write from clearing the motors. (Bluetooth would be report
    // 0x31 with the common block shifted +1 and a trailing CRC32 — not yet implemented.)
    private ValueTask WriteOutputAsync(CancellationToken cancellationToken) {
        var buffer = m_outputBuffer;

        buffer.AsSpan().Clear();

        buffer[0] = UsbOutputReportId;
        buffer[1] = (FlagCompatibleVibration | FlagHapticsSelect);      // valid_flag0
        buffer[2] = (FlagLightbarControl | FlagPlayerIndicatorControl); // valid_flag1
        buffer[3] = m_lastHigh;                                         // motor_right (weak / high band)
        buffer[4] = m_lastLow;                                          // motor_left (strong / low band)
        buffer[44] = m_playerLeds;                                      // player LED bitmask
        buffer[45] = m_lightbarRed;
        buffer[46] = m_lightbarGreen;
        buffer[47] = m_lightbarBlue;

        return m_device.WriteAsync(buffer: buffer, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        state = GamepadState.Neutral;

        // USB carries the full report as 0x01 (common block at offset 1); the Bluetooth full report is 0x31
        // (block at offset 2, after a sequence tag). The block layout is identical past that point.
        var dataStart = report[0] switch {
            UsbInputReportId => 1,
            BluetoothInputReportId => 2,
            _ => -1,
        };

        if ((dataStart < 0) || (report.Length < (dataStart + 21))) {
            return false;
        }

        var buttons0 = report[dataStart + 7];
        var buttons1 = report[dataStart + 8];
        var buttons2 = report[dataStart + 9];
        var buttons = GamepadButtons.None;

        // Face buttons by physical position: Cross (bottom) → South, Circle (right) → East, Square (left) →
        // West, Triangle (top) → North.
        if (0 != (buttons0 & 0x20)) { buttons |= GamepadButtons.ButtonSouth; }   // Cross
        if (0 != (buttons0 & 0x40)) { buttons |= GamepadButtons.ButtonEast; }    // Circle
        if (0 != (buttons0 & 0x10)) { buttons |= GamepadButtons.ButtonWest; }    // Square
        if (0 != (buttons0 & 0x80)) { buttons |= GamepadButtons.ButtonNorth; }   // Triangle

        ApplyDpad(hat: (buttons0 & 0x0F), buttons: ref buttons);

        if (0 != (buttons1 & 0x01)) { buttons |= GamepadButtons.LeftShoulder; }  // L1
        if (0 != (buttons1 & 0x02)) { buttons |= GamepadButtons.RightShoulder; } // R1
        if (0 != (buttons1 & 0x10)) { buttons |= GamepadButtons.Back; }          // Create
        if (0 != (buttons1 & 0x20)) { buttons |= GamepadButtons.Start; }         // Options
        if (0 != (buttons1 & 0x40)) { buttons |= GamepadButtons.LeftStickPress; }
        if (0 != (buttons1 & 0x80)) { buttons |= GamepadButtons.RightStickPress; }

        if (0 != (buttons2 & 0x01)) { buttons |= GamepadButtons.Guide; }         // PS button
        if (0 != (buttons2 & 0x02)) { buttons |= GamepadButtons.Touchpad; }      // touchpad click
        if (0 != (buttons2 & 0x04)) { buttons |= GamepadButtons.Mute; }          // mic mute

        // The accel and touchpad blocks sit past the gyro; only read each when the report is long enough (the full
        // USB/BT reports are, but guard so a short report never reads out of bounds).
        var hasAccelerometer = (report.Length >= (dataStart + AccelerometerOffset + 6));
        var hasTouch = (report.Length >= (dataStart + Touch1Offset + 4));
        var gyro = ReadGyro(report: report, offset: (dataStart + 15));
        var accelerometer = (hasAccelerometer ? ReadVector3Int16(report: report, offset: (dataStart + AccelerometerOffset), scale: AccelerometerGPerLsb) : default);

        state = new GamepadState(
            Accelerometer: accelerometer,
            Buttons: buttons,
            Gyro: gyro,
            LeftStick: ReadStick(rawX: report[dataStart], rawY: report[dataStart + 1]),
            LeftTrigger: NormalizeTrigger(raw: report[dataStart + 4]),
            Orientation: UpdateOrientation(gyro: gyro, accelerometer: accelerometer, hasAccelerometer: hasAccelerometer),
            RightStick: ReadStick(rawX: report[dataStart + 2], rawY: report[dataStart + 3]),
            RightTrigger: NormalizeTrigger(raw: report[dataStart + 5]),
            Touch0: (hasTouch ? ReadTouch(report: report, offset: (dataStart + Touch0Offset)) : default),
            Touch1: (hasTouch ? ReadTouch(report: report, offset: (dataStart + Touch1Offset)) : default)
        );

        return true;
    }

    // Fuses gyro + accel into an absolute orientation via the shared tracker. The DualSense IMU frame is
    // left-handed (X=right, Y=up, Z=forward); the fusion frame is right-handed, so the Z axis is negated
    // (yielding X=right, Y=up, Z=back). The raw vectors stay as-is in GamepadState.
    private Quaternion UpdateOrientation(Vector3 gyro, Vector3 accelerometer, bool hasAccelerometer) {
        return (hasAccelerometer
            ? m_tracker.Update(gyroRadiansPerSecond: ToRightHanded(sensor: gyro), accelerometerG: ToRightHanded(sensor: accelerometer))
            : m_tracker.Orientation);
    }
    private static Vector3 ToRightHanded(Vector3 sensor) {
        return new Vector3(x: sensor.X, y: sensor.Y, z: -sensor.Z);
    }
    private static GamepadTouchPoint ReadTouch(ReadOnlySpan<byte> report, int offset) {
        // contact: bit 7 set ⇒ no finger; low 7 bits are the incrementing contact id. The 12-bit X/Y are packed
        // across the next three bytes: x = byte1 | (byte2 low nibble << 8); y = (byte2 high nibble) | (byte3 << 4).
        var contact = report[offset];
        var x = (report[offset + 1] | ((report[offset + 2] & 0x0F) << 8));
        var y = ((report[offset + 2] >> 4) | (report[offset + 3] << 4));

        return new GamepadTouchPoint(
            Id: ((byte)(contact & 0x7F)),
            IsActive: (0 == (contact & 0x80)),
            Position: new Vector2(x: (x / TouchpadWidth), y: (y / TouchpadHeight))
        );
    }
    private static void ApplyDpad(int hat, ref GamepadButtons buttons) {
        // 8-direction hat: 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW, 8=neutral.
        buttons |= hat switch {
            0 => GamepadButtons.DpadUp,
            1 => (GamepadButtons.DpadUp | GamepadButtons.DpadRight),
            2 => GamepadButtons.DpadRight,
            3 => (GamepadButtons.DpadDown | GamepadButtons.DpadRight),
            4 => GamepadButtons.DpadDown,
            5 => (GamepadButtons.DpadDown | GamepadButtons.DpadLeft),
            6 => GamepadButtons.DpadLeft,
            7 => (GamepadButtons.DpadUp | GamepadButtons.DpadLeft),
            _ => GamepadButtons.None,
        };
    }
    private static Vector2 ReadStick(byte rawX, byte rawY) {
        // 0..255 axes centered at 128, with Y growing downward; flip Y so up is +1, then apply a radial deadzone.
        var stick = new Vector2(
            x: ((rawX - StickCenter) / StickRange),
            y: (-(rawY - StickCenter) / StickRange)
        );
        var magnitude = stick.Length();

        if (magnitude <= StickDeadzone) {
            return Vector2.Zero;
        }

        var scaled = ((MathF.Min(x: magnitude, y: 1f) - StickDeadzone) / (1f - StickDeadzone));

        return ((stick / magnitude) * scaled);
    }
    private static float NormalizeTrigger(byte raw) {
        if (raw <= TriggerThreshold) {
            return 0f;
        }

        return ((raw - TriggerThreshold) / (TriggerRange - TriggerThreshold));
    }
    // Three consecutive int16 LE axes (gyro angular velocity / accel specific force), scaled to physical units.
    private static Vector3 ReadVector3Int16(ReadOnlySpan<byte> report, int offset, float scale) {
        return new Vector3(
            x: (BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]) * scale),
            y: (BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..]) * scale),
            z: (BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 4)..]) * scale)
        );
    }
    private Vector3 ReadGyro(ReadOnlySpan<byte> report, int offset) {
        // Three int16 LE angular-velocity axes (pitch, yaw, roll), factory-calibrated to rad/s: subtract the
        // per-axis factory bias, then apply the per-axis scale from the calibration report. The factory bias is
        // tiny; ImuOrientationTracker additionally learns and removes the residual zero-rate bias at runtime, so
        // the absolute value here need not be exact.
        return new Vector3(
            x: ((ReadInt16(report: report, offset: offset) - m_gyroCalibrationBias.X) * m_gyroScale.X),
            y: ((ReadInt16(report: report, offset: (offset + 2)) - m_gyroCalibrationBias.Y) * m_gyroScale.Y),
            z: ((ReadInt16(report: report, offset: (offset + 4)) - m_gyroCalibrationBias.Z) * m_gyroScale.Z)
        );
    }
}
