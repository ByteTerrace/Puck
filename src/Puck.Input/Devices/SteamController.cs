using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Puck.Input.Hid;

namespace Puck.Input.Devices;

/// <summary>
/// Parses a Valve Steam Controller — a dual-trackpad controller with one analog stick, analog triggers, rear
/// grip paddles, and a 6-axis IMU — and drives its dual trackpad haptics over HID. The device speaks a
/// vendor-defined HID collection rather than a standard gamepad one: on open it must be taken out of its built-in
/// "lizard" keyboard/mouse emulation and switched into raw input via feature reports, after which it streams a
/// 64-byte controller-state report (report type <c>0x01</c>). Its two capacitive trackpads surface through the
/// shared <see cref="GamepadState.Touch0"/>/<see cref="GamepadState.Touch1"/> touch points (right pad → Touch0,
/// left pad → Touch1); the right pad additionally drives the right stick, and the left analog stick drives the
/// left stick. Feature-report control commands follow the community-documented vendor protocol for this
/// device's feature-report command set.
/// </summary>
/// <remarks>
/// Wired (product <c>0x1102</c>) and the four-slot wireless receiver (product <c>0x1142</c>) share this parser;
/// each receiver slot is opened as its own connection and only streams state while a controller is paired to it.
/// The receiver announces pairing changes out of band (report type <c>0x03</c>), surfaced through
/// <see cref="IWirelessSlotParser"/> so an empty slot parks dormant instead of being mistaken for a dead device.
/// The IMU axis mapping and sensor scales are nominal (uncalibrated); gravity anchors pitch and roll regardless.
/// </remarks>
internal sealed class SteamController : IGamepadParser, IRumbleParser, IWirelessSlotParser, IDisposable {
    // Feature-report command ids (FeatureReportMessageIDs). A feature report is [reportId=0, command, payloadLen,
    // payload...] written at the device's declared feature-report length.
    private const byte ClearDigitalMappingsCommand = 0x81;      // drop the keyboard emulation mappings
    private const byte SetSettingsValuesCommand = 0x87;         // apply (register, uint16 value) triples
    private const byte TriggerHapticPulseCommand = 0x8F;        // fire a haptic pulse train on one trackpad
    private const byte SetDefaultDigitalMappingsCommand = 0x85; // restore the default keyboard mappings (on close)
    private const byte LoadDefaultSettingsCommand = 0x8E;       // restore factory settings (on close)

    // Settings registers written via SetSettingsValuesCommand.
    private const byte SettingLeftTrackpadMode = 7;
    private const byte SettingImuMode = 49;
    private const byte SettingRightTrackpadMode = 8;
    private const byte TrackpadModeNone = 7;                    // disables a trackpad's built-in mouse emulation
    // IMU mode bitmask: stream raw accelerometer + raw gyro (the fusion path consumes the raw vectors).
    private const ushort ImuModeSendRawAccelerometer = 0x08;
    private const ushort ImuModeRaw = ImuModeSendRawAccelerometer | ImuModeSendRawGyro;
    private const ushort ImuModeSendRawGyro = 0x10;

    // A freshly opened controller (especially a wireless slot) may not accept a feature write immediately, so each
    // setup command is retried briefly before giving up. Missing a write degrades a feature, never crashes.
    private const int FeatureWriteAttempts = 50;
    private const int FeatureWriteRetryDelayMilliseconds = 2;
    private const byte StateReportType = 0x01;                  // ValveInReport ucType for a controller-state report
    // ValveInReport ucType for a receiver pairing event; its single payload byte is the transition below.
    private const byte WirelessEventReportType = 0x03;
    private const byte WirelessEventConnected = 2;
    private const byte WirelessEventDisconnected = 1;
    // ValveInReport header: unReportVersion (2 bytes, 0x0001) then ucType, ucLength; the state payload follows.
    // Windows may or may not prepend a report-id byte, so the base is detected per report (see TryParse).
    private const int ButtonsOffset = 8;                       // three button bytes at payload+4..6 (base+8..10)
    private const int LeftTriggerOffset = 11;                  // analog left trigger, 0..255
    private const int RightTriggerOffset = 12;                 // analog right trigger, 0..255
    private const int LeftPadOffset = 16;                      // int16 X/Y (shared with the analog stick)
    private const int RightPadOffset = 20;                     // int16 X/Y
    private const int AccelerometerOffset = 28;               // three int16 axes
    private const int GyroOffset = 34;                        // three int16 axes
    private const int MinimumStatePayload = 40;               // bytes required past the base to decode gyro
    private const float StickDeadzone = 0.12f;
    private const float StickRange = 32768f;
    private const float TrackpadHalf = 32768f;
    private const float TrackpadRange = 65535f;
    private const float TriggerRange = 255f;
    private const byte TriggerThreshold = 8;
    // Nominal (uncalibrated) IMU scales. Gyro: ~2000 deg/s full scale over int16 (≈16.4 LSB per deg/s). Accel:
    // ±2 g over int16 (16384 LSB per g). The complementary filter's gravity term keeps pitch/roll correct even if
    // these are imperfect; only gyro-only yaw depends on the gyro scale being roughly right.
    private const float GyroRadiansPerSecondPerLsb = ((1f / 16.4f) * (MathF.PI / 180f));
    private const float AccelerometerGPerLsb = (1f / 16384f);

    // Haptic pulse-train shape. The trackpad LRAs are driven by a square wave: amplitude is the per-pulse drive,
    // period the pulse spacing (µs), count the number of pulses. This is a coarse buzz, not true amplitude control.
    private const ushort HapticMaxAmplitude = 0x3F00;
    private const ushort HapticLeftPeriodMicroseconds = 1300;  // low band → lower pitch
    private const ushort HapticRightPeriodMicroseconds = 900;  // high band → higher pitch
    private const ushort HapticPulseCount = 40;                // spans ~one rumble-update cadence, re-fired to sustain
    private const byte HapticSideRight = 0;                    // pad indices are swapped for legacy reasons
    private const byte HapticSideLeft = 1;
    // Coalesce equal-or-weaker rumble writes to a >=30 ms cadence so a per-tick streamer can't flood the link;
    // stops, the first write, and any intensity increase always go through.
    private const long RumbleWriteIntervalMilliseconds = 30L;

    private readonly IHidDevice m_device;
    private readonly byte[] m_featureBuffer;
    private readonly ImuOrientationTracker m_tracker = new();
    private long m_lastRumbleSendTicks;
    private float m_lastRumbleIntensity;

    /// <summary>Initializes a new instance of the <see cref="SteamController"/> class.</summary>
    /// <param name="device">The opened HID device handle for the controller's vendor input interface.</param>
    public SteamController(IHidDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        var featureLength = device.FeatureReportByteLength;

        m_device = device;
        // Feature reports must be written at the device's declared feature length (the HID stack rejects a
        // mismatched length); fall back to the documented 65-byte report if the device declares none.
        m_featureBuffer = new byte[((featureLength > 0) ? featureLength : 65)];
    }

    /// <inheritdoc />
    public GamepadType Type => GamepadType.SteamController;
    /// <inheritdoc />
    // A 6-axis IMU and pressure-sensitive analog triggers.
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.Gyro | GamepadInputCapabilities.AnalogTriggers;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default) {
        // Take the controller out of lizard mode: clear the keyboard-emulation mappings and switch both trackpads
        // out of their built-in mouse mode, then enable raw IMU streaming. Each is best-effort; a failed write
        // simply leaves that feature at its default rather than faulting the connection.
        _ = await SendFeatureAsync(command: ClearDigitalMappingsCommand, payload: [], cancellationToken: cancellationToken);
        _ = await SendFeatureAsync(
            command: SetSettingsValuesCommand,
            payload: [SettingLeftTrackpadMode, TrackpadModeNone, 0, SettingRightTrackpadMode, TrackpadModeNone, 0],
            cancellationToken: cancellationToken
        );
        _ = await SendFeatureAsync(
            command: SetSettingsValuesCommand,
            payload: [SettingImuMode, ((byte)(ImuModeRaw & 0xFF)), ((byte)(ImuModeRaw >> 8))],
            cancellationToken: cancellationToken
        );
    }

    // Writes one feature-report command, retrying briefly because a freshly connected (notably wireless) device
    // may not accept the write immediately. Returns whether the write was ultimately accepted.
    private async ValueTask<bool> SendFeatureAsync(byte command, byte[] payload, CancellationToken cancellationToken) {
        var buffer = m_featureBuffer;

        buffer.AsSpan().Clear();
        buffer[0] = 0;                       // report id (the vendor collection is written with id 0)
        buffer[1] = command;
        buffer[2] = ((byte)payload.Length);
        Array.Copy(sourceArray: payload, sourceIndex: 0, destinationArray: buffer, destinationIndex: 3, length: payload.Length);

        for (var attempt = 0; (attempt < FeatureWriteAttempts); ++attempt) {
            if (m_device.TrySetFeatureReport(buffer: buffer)) {
                return true;
            }

            await Task.Delay(millisecondsDelay: FeatureWriteRetryDelayMilliseconds, cancellationToken: cancellationToken);
        }

        return false;
    }
    // The synchronous variant used during disposal (the async loop has already stopped, but the handle is still
    // open) to best-effort restore lizard mode without awaiting.
    private bool SendFeatureSynchronously(byte command, ReadOnlySpan<byte> payload) {
        var buffer = m_featureBuffer;

        buffer.AsSpan().Clear();
        buffer[0] = 0;
        buffer[1] = command;
        buffer[2] = ((byte)payload.Length);
        payload.CopyTo(destination: buffer.AsSpan(start: 3));

        return m_device.TrySetFeatureReport(buffer: buffer);
    }

    /// <inheritdoc />
    public ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default) {
        var high = Math.Clamp(value: highFrequency, max: 1f, min: 0f);
        var low = Math.Clamp(value: lowFrequency, max: 1f, min: 0f);
        var intensity = MathF.Max(x: low, y: high);
        var now = Stopwatch.GetTimestamp();

        // Throttle equal-or-weaker updates inside the 30 ms window; stops, the first write, and any intensity
        // increase always go through (so rumble-off and stronger effects stay instant).
        if ((0f < intensity) && (intensity <= m_lastRumbleIntensity) && (0L != m_lastRumbleSendTicks)) {
            var elapsedMilliseconds = (((now - m_lastRumbleSendTicks) * 1000L) / Stopwatch.Frequency);

            if (elapsedMilliseconds < RumbleWriteIntervalMilliseconds) {
                return ValueTask.CompletedTask;
            }
        }

        m_lastRumbleIntensity = intensity;
        m_lastRumbleSendTicks = now;

        // Map the low band to the left pad and the high band to the right pad. A pad with no intensity is left
        // untouched (the device has no "stop"; a finite pulse train simply lapses).
        if (0f < low) {
            _ = FireHaptic(side: HapticSideLeft, intensity: low, period: HapticLeftPeriodMicroseconds);
        }

        if (0f < high) {
            _ = FireHaptic(side: HapticSideRight, intensity: high, period: HapticRightPeriodMicroseconds);
        }

        return ValueTask.CompletedTask;
    }

    private bool FireHaptic(byte side, float intensity, ushort period) {
        var amplitude = ((ushort)(Math.Clamp(value: intensity, max: 1f, min: 0f) * HapticMaxAmplitude));

        return SendFeatureSynchronously(
            command: TriggerHapticPulseCommand,
            payload: [
                side,
                ((byte)(amplitude & 0xFF)), ((byte)(amplitude >> 8)),
                ((byte)(period & 0xFF)), ((byte)(period >> 8)),
                ((byte)(HapticPulseCount & 0xFF)), ((byte)(HapticPulseCount >> 8)),
            ]
        );
    }

    // The report begins with unReportVersion (0x0001) then ucType. Windows may or may not prepend a report-id
    // byte, so accept the version marker at offset 0 or 1 and treat the following byte as the type.
    private static bool TryFindReportBase(ReadOnlySpan<byte> report, out int dataStart) {
        if ((report.Length >= 4) && (report[0] == 0x01) && (report[1] == 0x00)) {
            dataStart = 0;

            return true;
        }

        if ((report.Length >= 5) && (report[1] == 0x01) && (report[2] == 0x00)) {
            dataStart = 1;

            return true;
        }

        dataStart = 0;

        return false;
    }

    /// <inheritdoc />
    public WirelessSlotEvent ClassifySlotEvent(ReadOnlySpan<byte> report) {
        // The pairing event shares the state report's ValveInReport framing (version, type, length); type 0x03
        // carries a single payload byte announcing the transition.
        if (!TryFindReportBase(report: report, dataStart: out var dataStart)
            || (report.Length <= (dataStart + 4))
            || (report[(dataStart + 2)] != WirelessEventReportType)) {
            return WirelessSlotEvent.None;
        }

        return report[(dataStart + 4)] switch {
            WirelessEventConnected => WirelessSlotEvent.Connected,
            WirelessEventDisconnected => WirelessSlotEvent.Disconnected,
            _ => WirelessSlotEvent.None,
        };
    }

    /// <inheritdoc />
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        state = GamepadState.Neutral;

        // Only the controller-state report (type 0x01) carries input; pairing/other reports are ignored here
        // (pairing events are classified separately via ClassifySlotEvent).
        if (!TryFindReportBase(report: report, dataStart: out var dataStart)) {
            return false;
        }

        if ((report[(dataStart + 2)] != StateReportType) || (report.Length < ((dataStart + MinimumStatePayload) + 6))) {
            return false;
        }

        var b0 = report[(dataStart + ButtonsOffset)];
        var b1 = report[((dataStart + ButtonsOffset) + 1)];
        var b2 = report[((dataStart + ButtonsOffset) + 2)];
        var buttons = GamepadButtons.None;

        // Face buttons by physical position: A (bottom) → South, B (right) → East, X (left) → West, Y (top) → North.
        if (0 != (b0 & 0x80)) { buttons |= GamepadButtons.ButtonSouth; }   // A
        if (0 != (b0 & 0x20)) { buttons |= GamepadButtons.ButtonEast; }    // B
        if (0 != (b0 & 0x40)) { buttons |= GamepadButtons.ButtonWest; }    // X
        if (0 != (b0 & 0x10)) { buttons |= GamepadButtons.ButtonNorth; }   // Y
        if (0 != (b0 & 0x08)) { buttons |= GamepadButtons.LeftShoulder; }  // left bumper
        if (0 != (b0 & 0x04)) { buttons |= GamepadButtons.RightShoulder; } // right bumper

        if (0 != (b1 & 0x01)) { buttons |= GamepadButtons.DpadUp; }
        if (0 != (b1 & 0x02)) { buttons |= GamepadButtons.DpadRight; }
        if (0 != (b1 & 0x04)) { buttons |= GamepadButtons.DpadLeft; }
        if (0 != (b1 & 0x08)) { buttons |= GamepadButtons.DpadDown; }
        if (0 != (b1 & 0x10)) { buttons |= GamepadButtons.Back; }          // left arrow (select)
        if (0 != (b1 & 0x20)) { buttons |= GamepadButtons.Guide; }         // Steam / mode
        if (0 != (b1 & 0x40)) { buttons |= GamepadButtons.Start; }         // right arrow
        if (0 != (b1 & 0x80)) { buttons |= GamepadButtons.LeftGrip; }      // lower-left grip paddle

        if (0 != (b2 & 0x01)) { buttons |= GamepadButtons.RightGrip; }     // lower-right grip paddle
        if (0 != (b2 & 0x04)) { buttons |= GamepadButtons.RightStickPress; } // right pad click
        if (0 != (b2 & 0x40)) { buttons |= GamepadButtons.LeftStickPress; }  // stick click

        var leftPadTouched = (0 != (b2 & 0x08));   // when set, the shared bytes hold the left pad, not the stick
        var leftPadAndStick = (0 != (b2 & 0x80));  // both are reported this frame
        var rightPadTouched = (0 != (b2 & 0x10));

        var accelerometer = ReadVector3Int16(report: report, offset: (dataStart + AccelerometerOffset), scale: AccelerometerGPerLsb);
        var gyro = ReadVector3Int16(report: report, offset: (dataStart + GyroOffset), scale: GyroRadiansPerSecondPerLsb);
        // The stick reads the shared bytes only when the left pad is not the sole occupant of them.
        var leftStick = ((!leftPadTouched || leftPadAndStick) ? ReadStick(report: report, offset: (dataStart + LeftPadOffset)) : Vector2.Zero);
        // The right pad doubles as the right stick (absolute position while touched, recentring when released).
        var rightStick = (rightPadTouched ? ReadPadAsStick(report: report, offset: (dataStart + RightPadOffset)) : Vector2.Zero);

        state = new GamepadState(
            Accelerometer: accelerometer,
            Buttons: buttons,
            Gyro: gyro,
            LeftStick: leftStick,
            LeftTrigger: NormalizeTrigger(raw: report[(dataStart + LeftTriggerOffset)]),
            Orientation: m_tracker.Update(gyroRadiansPerSecond: ToFusionFrame(sensor: gyro), accelerometerG: ToFusionFrame(sensor: accelerometer), deltaSeconds: ImuSampleSeconds),
            RightStick: rightStick,
            RightTrigger: NormalizeTrigger(raw: report[(dataStart + RightTriggerOffset)]),
            Touch0: (rightPadTouched ? ReadTouch(report: report, offset: (dataStart + RightPadOffset), id: 0) : default),
            Touch1: (leftPadTouched ? ReadTouch(report: report, offset: (dataStart + LeftPadOffset), id: 1) : default)
        );

        return true;
    }

    // The controller streams state at a fixed cadence; the fusion integrates one fixed step per report. The pad's
    // own free-running motion timestamp isn't decoded here (nominal fusion), so a constant per-report dt is used —
    // matching the Switch's fixed-sub-sample cadence approach.
    private const float ImuSampleSeconds = 0.004f;

    // Maps the IMU axes into the fusion frame (X=right, Y=up, Z=back). Nominal/unverified: the accelerometer
    // gravity term anchors pitch/roll regardless of the exact mapping.
    private static Vector3 ToFusionFrame(Vector3 sensor) {
        return new Vector3(x: sensor.X, y: sensor.Y, z: -sensor.Z);
    }
    private static Vector3 ReadVector3Int16(ReadOnlySpan<byte> report, int offset, float scale) {
        return new Vector3(
            x: (BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]) * scale),
            y: (BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..]) * scale),
            z: (BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 4)..]) * scale)
        );
    }
    private static Vector2 ReadStick(ReadOnlySpan<byte> report, int offset) {
        // int16 axes centered at 0; Y grows up already, so no flip. Apply a radial deadzone then rescale.
        var stick = new Vector2(
            x: (BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]) / StickRange),
            y: (BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..]) / StickRange)
        );
        var magnitude = stick.Length();

        if (magnitude <= StickDeadzone) {
            return Vector2.Zero;
        }

        var scaled = ((MathF.Min(x: magnitude, y: 1f) - StickDeadzone) / (1f - StickDeadzone));

        return ((stick / magnitude) * scaled);
    }
    private static Vector2 ReadPadAsStick(ReadOnlySpan<byte> report, int offset) {
        // The absolute pad position mapped to a stick vector (no deadzone: a touch anywhere is an intentional aim).
        return new Vector2(
            x: Math.Clamp(value: (BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]) / StickRange), max: 1f, min: -1f),
            y: Math.Clamp(value: (BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..]) / StickRange), max: 1f, min: -1f)
        );
    }
    private static GamepadTouchPoint ReadTouch(ReadOnlySpan<byte> report, int offset, byte id) {
        // int16 X (left→right) and Y (bottom→top), normalized to 0..1 with the touch-surface origin at top-left
        // (X right, Y down) — so Y is flipped to match the shared GamepadTouchPoint convention.
        var x = BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]);
        var y = BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..]);

        return new GamepadTouchPoint(
            Id: id,
            IsActive: true,
            Position: new Vector2(x: ((x + TrackpadHalf) / TrackpadRange), y: (1f - ((y + TrackpadHalf) / TrackpadRange)))
        );
    }
    private static float NormalizeTrigger(byte raw) {
        if (raw <= TriggerThreshold) {
            return 0f;
        }

        return ((raw - TriggerThreshold) / (TriggerRange - TriggerThreshold));
    }

    /// <summary>
    /// Restores the controller's built-in keyboard/mouse (lizard) emulation, so it behaves as a normal desktop
    /// device again once the engine releases it. Best-effort and synchronous: the I/O loop has already stopped but
    /// the handle is still open at this point.
    /// </summary>
    public void Dispose() {
        _ = SendFeatureSynchronously(command: SetDefaultDigitalMappingsCommand, payload: []);
        _ = SendFeatureSynchronously(command: LoadDefaultSettingsCommand, payload: [0]);
    }
}
