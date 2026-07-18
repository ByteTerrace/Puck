using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Puck.Input.Hid;

namespace Puck.Input.Devices;

/// <summary>
/// Parses the 2026 Valve Steam Controller (codename <em>Triton</em>, VID <c>0x28DE</c> PID <c>0x1304</c>) and
/// drives its dual-motor rumble over HID. Unlike the classic Steam Controller (<see cref="SteamController"/>) this
/// is a conventional twin-stick pad: two clickable analog sticks, two clickable capacitive trackpads, analog
/// triggers, four rear paddles, and a 6-axis IMU, connected through a wireless "puck" receiver. Its state arrives
/// on a vendor-defined HID collection (usage page <c>0xFF00</c>, usage <c>0x01</c>) as a fixed 54-byte input
/// report (id <c>0x42</c>); on open it must be taken out of its built-in "lizard" keyboard/mouse emulation and
/// switched into raw IMU streaming via feature reports.
/// </summary>
/// <remarks>
/// The Triton's control channel differs from the classic pad in framing, and that difference is load-bearing: its
/// feature reports are written under <strong>report id 1</strong> (<see cref="FeatureReportId"/>) with a
/// <c>[type][length][payload]</c> message at offset 1 — the classic pad's id-0 framing is rejected by the Triton
/// firmware (Windows <c>ERROR_INVALID_PARAMETER</c>, 87). Settings are applied through
/// <see cref="SetSettingsValuesCommand"/> as 3-byte <c>{settingNum, value}</c> entries. Rumble is an
/// <em>output</em> report (id <see cref="OutputRumbleReportId"/>, written not fed back as a feature), mapping the
/// low/high rumble bands onto the left/right motor speeds. The feature and rumble framing and the
/// at-rest IMU are hardware-proven on this receiver, while the IMU axis mapping is nominal (gravity anchors
/// pitch/roll regardless — only gyro-only yaw depends on the exact mapping).
/// </remarks>
internal sealed class SteamControllerTriton : IGamepadParser, IRumbleParser, IDisposable {
    // Report ids. The vendor collection carries a fixed 54-byte state report (0x42); rumble is written as an
    // output report (0x80); every control write is a feature report under report id 1 (the classic pad's id-0
    // framing is rejected by the Triton firmware with Win32 error 87).
    private const byte InputReportId = 0x42;
    private const byte OutputRumbleReportId = 0x80;     // ID_OUT_REPORT_HAPTIC_RUMBLE
    private const byte FeatureReportId = 0x01;          // buffer[0] for every feature write (SET_SETTINGS etc.)

    // Feature-report message header (at buffer offset 1, after the report id): type then payload length.
    private const byte SetSettingsValuesCommand = 0x87; // ID_SET_SETTINGS_VALUES
    private const byte ControllerSettingSize = 3;       // sizeof(ControllerSetting): settingNum (u8) + value (u16)

    // Settings written via SetSettingsValuesCommand.
    private const byte SettingLizardMode = 9;           // SETTING_LIZARD_MODE
    private const byte SettingImuMode = 48;             // SETTING_IMU_MODE
    private const ushort LizardModeOff = 0;             // disable the keyboard/mouse emulation
    private const ushort LizardModeOn = 1;             // restore it on release
    // IMU mode bit flags. Raw accel + raw gyro feed the shared complementary fusion (matching the other pads).
    // SETTING_GYRO_MODE_SEND_ORIENTATION (0x0004) is deliberately left off — the firmware quaternion at
    // OrientationQuatOffset is available as an alternative to running the fusion, but we consume raw so a Triton
    // fuses identically to a DualSense/Switch.
    private const ushort ImuModeSendRawAccelerometer = 0x0008; // SETTING_GYRO_MODE_SEND_RAW_ACCEL
    private const ushort ImuModeSendRawGyro = 0x0010;         // SETTING_GYRO_MODE_SEND_RAW_GYRO
    private const ushort ImuModeRaw = ImuModeSendRawAccelerometer | ImuModeSendRawGyro; // 0x0018 (hardware-proven accepted)

    // A freshly opened receiver slot may not accept a feature write immediately, so each setup command is retried
    // briefly before giving up. Missing a write degrades a feature, never crashes.
    private const int FeatureWriteAttempts = 50;
    private const int FeatureWriteRetryDelayMilliseconds = 2;
    private const int HidFeatureReportBytes = 64;       // HID_FEATURE_REPORT_BYTES (the declared feature length)
    private const int HidRumbleOutputReportBytes = 10;  // HID_RUMBLE_OUTPUT_REPORT_BYTES (report id + 9-byte payload)

    // Lizard-off is re-asserted on a ~1 s watchdog so a firmware re-arm can't restore the keyboard/mouse emulation
    // mid-session; the pad streams at ~274 Hz, so this rides the per-report parse hook (serviced on the I/O thread).
    private static readonly long LizardWatchdogIntervalTicks = (3L * Stopwatch.Frequency);

    // Input report field offsets (absolute, report id at [0]). Layout is the packed TritonMTUNoQuat report: a
    // sequence byte, a 32-bit button field, the analog axes, then the IMU block (accel BEFORE gyro).
    private const int ButtonsOffset = 2;                // u32 LE
    private const int TriggerLeftOffset = 6;            // u16, 0..32768
    private const int TriggerRightOffset = 8;           // u16
    private const int LeftStickOffset = 10;             // i16 X then i16 Y
    private const int RightStickOffset = 14;
    private const int LeftPadOffset = 18;               // i16 X then i16 Y (capacitive trackpad absolute position)
    private const int RightPadOffset = 24;
    private const int ImuTimestampOffset = 30;          // u32 LE, microseconds (the report clock the fusion dt derives from)
    private const int AccelerometerOffset = 34;         // three i16 axes
    private const int GyroOffset = 40;                  // three i16 axes
    // An optional firmware orientation quaternion (four i16 Q15 W/X/Y/Z at offset 46) follows the gyro block, but
    // only when SEND_ORIENTATION is enabled; we stream raw and fuse instead, so it is neither requested nor read.
    private const int MinimumReportLength = (GyroOffset + 6); // through the gyro block (46)

    // Triton button bits (the 32-bit field at ButtonsOffset).
    private const uint ButtonA = (1u << 0);
    private const uint ButtonB = (1u << 1);
    private const uint ButtonX = (1u << 2);
    private const uint ButtonY = (1u << 3);
    // Hardware-verified button mapping from the press-driven calibration wizard: L4=17 left-top,
    // L5=18 left-bottom, R4=7 right-top, R5=8 right-bottom, and RB=9.
    private const uint ButtonQuickAccess = (1u << 4);     // QAM ("…")
    private const uint ButtonRightStickClick = (1u << 5); // R3 (wizard-verified)
    private const uint ButtonMenu = (1u << 6);            // MENU, the hamburger glyph (wizard-verified: this bit is swapped relative to the upstream RE record)
    private const uint ButtonR4 = (1u << 7);              // rear paddle, RIGHT-TOP (wizard-verified)
    private const uint ButtonR5 = (1u << 8);              // rear paddle, RIGHT-BOTTOM (wizard-verified)
    private const uint ButtonRightBumper = (1u << 9);     // RB (wizard-verified)
    private const uint ButtonDpadDown = (1u << 10);
    private const uint ButtonDpadLeft = (1u << 12);
    private const uint ButtonDpadRight = (1u << 11);
    private const uint ButtonDpadUp = (1u << 13);
    private const uint ButtonView = (1u << 14);           // VIEW, the overlapping-squares glyph (wizard-verified: this bit is swapped relative to the upstream RE record)
    private const uint ButtonLeftStickClick = (1u << 15); // L3
    private const uint ButtonSteam = (1u << 16);
    private const uint ButtonL4 = (1u << 17);             // rear paddle, LEFT-TOP (wizard-verified)
    private const uint ButtonL5 = (1u << 18);             // rear paddle, LEFT-BOTTOM (wizard-verified)
    private const uint ButtonLeftBumper = (1u << 19);     // LB
    private const uint ButtonRightPadTouch = (1u << 21);  // capacitive: a finger on the right trackpad
    private const uint ButtonRightPadClick = (1u << 22);  // the right trackpad pressed
    private const uint ButtonLeftPadTouch = (1u << 25);   // capacitive: a finger on the left trackpad
    private const uint ButtonLeftPadClick = (1u << 26);   // the left trackpad pressed
    // Deliberately NOT mapped (no honest carrier in GamepadState / would only bloat the button vocabulary):
    //   RightJoystickTouch (1u<<20), LeftJoystickTouch (1u<<24), RightGripTouch (1u<<28), LeftGripTouch (1u<<29)
    //   capacitive proximity sensors; and RightTriggerClick (1u<<23), LeftTriggerClick (1u<<27) — the analog
    //   trigger crossing full scale already carries the full-pull click.

    private const float StickRange = 32768f;
    private const float StickDeadzone = 0.12f;
    private const float TriggerRange = 32768f;          // full pull is ~32768 over the u16 range
    private const int TriggerThreshold = 256;           // ~0.8% of full scale, to reject a resting jitter floor
    private const float PadRange = 65536f;              // normalized pad position = raw / 65536 + 0.5
    private const float PadHalf = 0.5f;
    // Nominal (uncalibrated) IMU scales. Accel: ±2 g over int16 (16384 LSB/g). Gyro: 2000 deg/s full scale over
    // int16 (so 2000/32768 deg/s per LSB). The fusion's gravity term anchors pitch/roll regardless of the exact
    // axis mapping; only gyro-only yaw depends on the gyro scale being roughly right.
    private const float AccelerometerGPerLsb = (1f / 16384f);
    private const float GyroRadiansPerSecondPerLsb = (((2000f / 32768f)) * (MathF.PI / 180f));
    // The IMU timestamp is a free-running microsecond counter; a raw delta converts to seconds by dividing by 1e6.
    private const float SensorTimestampSecondsPerUnit = (1f / 1_000_000f);

    // Rumble: the low/high bands map onto the left/right motor speeds (u16). The IRumbleParser 0..1 intensity
    // scales across the full 16-bit speed range. Coalesce equal-or-weaker writes to a >=30 ms cadence so a
    // per-tick streamer can't flood the link; stops, the first write, and any intensity increase always go through.
    private const float RumbleSpeedMax = 65535f;
    private const long RumbleWriteIntervalMilliseconds = 30L;
    // Rumble output-report field offsets (packed MsgHapticRumble: type u8, intensity u16, then {speed u16, gain
    // i8} per side). intensity is 16-bit — this shifts the speeds one byte past a naive u8 reading.
    private const int RumbleTypeOffset = 1;
    private const int RumbleIntensityOffset = 2;        // u16
    private const int RumbleLeftSpeedOffset = 4;        // u16 (low band)
    private const int RumbleLeftGainOffset = 6;         // i8
    private const int RumbleRightSpeedOffset = 7;       // u16 (high band)
    private const int RumbleRightGainOffset = 9;        // i8

    private readonly IHidDevice m_device;
    private readonly byte[] m_featureBuffer;
    private readonly byte[] m_outputBuffer;
    private readonly ImuOrientationTracker m_tracker = new();
    private bool m_hasSensorTimestamp;
    private uint m_lastSensorTimestamp;
    private long m_lastRumbleSendTicks;
    private float m_lastRumbleIntensity;
    private long m_nextLizardWatchdogTicks;

    /// <summary>Initializes a new instance of the <see cref="SteamControllerTriton"/> class.</summary>
    /// <param name="device">The opened HID device handle for the Triton's vendor input interface.</param>
    public SteamControllerTriton(IHidDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        var featureLength = device.FeatureReportByteLength;
        var outputLength = device.OutputReportByteLength;

        m_device = device;
        // Feature and output reports must be written at the device's declared length (the HID stack rejects a
        // mismatched length); fall back to the documented sizes if the device declares none.
        m_featureBuffer = new byte[((featureLength > 0) ? featureLength : HidFeatureReportBytes)];
        m_outputBuffer = new byte[((outputLength > 0) ? outputLength : HidRumbleOutputReportBytes)];
    }

    /// <inheritdoc />
    public GamepadType Type => GamepadType.SteamControllerTriton;
    /// <inheritdoc />
    // A 6-axis IMU and pressure-sensitive analog triggers.
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.Gyro | GamepadInputCapabilities.AnalogTriggers;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default) {
        // Take the Triton out of lizard mode and enable raw IMU streaming. Both are best-effort feature writes
        // under report id 1; a failed write simply leaves that feature at its default rather than faulting.
        _ = await SendSettingAsync(settingNum: SettingLizardMode, value: LizardModeOff, cancellationToken: cancellationToken);
        _ = await SendSettingAsync(settingNum: SettingImuMode, value: ImuModeRaw, cancellationToken: cancellationToken);
        m_nextLizardWatchdogTicks = (Stopwatch.GetTimestamp() + LizardWatchdogIntervalTicks);
    }

    // Builds a single-setting SET_SETTINGS_VALUES feature report into the reusable feature buffer: report id 1,
    // the message type + length, then one {settingNum, value} entry.
    private void BuildSingleSetting(byte settingNum, ushort value) {
        var buffer = m_featureBuffer;

        buffer.AsSpan().Clear();
        buffer[0] = FeatureReportId;                        // report id 1 (the Triton framing)
        buffer[1] = SetSettingsValuesCommand;               // 0x87
        buffer[2] = ControllerSettingSize;                  // payload length = 1 * sizeof(ControllerSetting)
        buffer[3] = settingNum;
        buffer[4] = ((byte)(value & 0xFF));
        buffer[5] = ((byte)(value >> 8));
    }
    // Writes one setting, retrying briefly because a freshly connected receiver slot may not accept the write
    // immediately. Returns whether the write was ultimately accepted.
    private async ValueTask<bool> SendSettingAsync(byte settingNum, ushort value, CancellationToken cancellationToken) {
        BuildSingleSetting(settingNum: settingNum, value: value);

        for (var attempt = 0; (attempt < FeatureWriteAttempts); ++attempt) {
            if (m_device.TrySetFeatureReport(buffer: m_featureBuffer)) {
                return true;
            }

            await Task.Delay(millisecondsDelay: FeatureWriteRetryDelayMilliseconds, cancellationToken: cancellationToken);
        }

        return false;
    }
    // The synchronous variant used by the watchdog (on the I/O thread, between reads) and by disposal (the async
    // loop has stopped, the handle is still open) to best-effort re-assert / restore a setting without awaiting.
    private bool SendSettingSynchronously(byte settingNum, ushort value) {
        BuildSingleSetting(settingNum: settingNum, value: value);

        return m_device.TrySetFeatureReport(buffer: m_featureBuffer);
    }
    // Lizard-off is re-asserted periodically so a firmware re-arm can't quietly restore the keyboard/mouse
    // emulation while we stream. TryParse runs on the device I/O thread once per report, so this synchronous
    // feature write is serialized with the reads and is the parser's natural per-report service seam.
    private void ServiceLizardWatchdog() {
        var now = Stopwatch.GetTimestamp();

        if (now >= m_nextLizardWatchdogTicks) {
            m_nextLizardWatchdogTicks = (now + LizardWatchdogIntervalTicks);
            _ = SendSettingSynchronously(settingNum: SettingLizardMode, value: LizardModeOff);
        }
    }

    /// <inheritdoc />
    public ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default) {
        var high = Math.Clamp(value: highFrequency, max: 1f, min: 0f);
        var low = Math.Clamp(value: lowFrequency, max: 1f, min: 0f);
        var intensity = MathF.Max(x: low, y: high);
        var now = Stopwatch.GetTimestamp();

        // Throttle equal-or-weaker updates inside the 30 ms window; stops, the first write, and any intensity
        // increase always go through (so rumble-off and stronger effects stay instant). The demo's rumble streamer
        // re-issues each tick, so this <=30 ms cadence also sustains a held effect if the firmware auto-decays.
        if ((0f < intensity) && (intensity <= m_lastRumbleIntensity) && (0L != m_lastRumbleSendTicks)) {
            var elapsedMilliseconds = (((now - m_lastRumbleSendTicks) * 1000L) / Stopwatch.Frequency);

            if (elapsedMilliseconds < RumbleWriteIntervalMilliseconds) {
                return ValueTask.CompletedTask;
            }
        }

        m_lastRumbleIntensity = intensity;
        m_lastRumbleSendTicks = now;

        // Map the low band to the left motor speed and the high band to the right — an all-zero write stops rumble.
        return WriteRumbleAsync(
            cancellationToken: cancellationToken,
            leftSpeed: ((ushort)(low * RumbleSpeedMax)),
            rightSpeed: ((ushort)(high * RumbleSpeedMax))
        );
    }

    private ValueTask WriteRumbleAsync(ushort leftSpeed, ushort rightSpeed, CancellationToken cancellationToken) {
        var buffer = m_outputBuffer;

        buffer.AsSpan().Clear();
        buffer[0] = OutputRumbleReportId;   // 0x80
        buffer[RumbleTypeOffset] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: RumbleIntensityOffset), value: 0);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: RumbleLeftSpeedOffset), value: leftSpeed);
        buffer[RumbleLeftGainOffset] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: RumbleRightSpeedOffset), value: rightSpeed);
        buffer[RumbleRightGainOffset] = 0;

        return m_device.WriteAsync(buffer: buffer, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        state = GamepadState.Neutral;

        if ((report.Length < MinimumReportLength) || (report[0] != InputReportId)) {
            return false;
        }

        // Re-assert lizard-off on its watchdog cadence (serviced here, the parser's per-report I/O-thread hook).
        ServiceLizardWatchdog();

        var raw = BinaryPrimitives.ReadUInt32LittleEndian(source: report[ButtonsOffset..]);
        var buttons = GamepadButtons.None;

        // Face buttons by physical position: A (bottom) → South, B (right) → East, X (left) → West, Y (top) → North.
        if (0u != (raw & ButtonA)) { buttons |= GamepadButtons.ButtonSouth; }
        if (0u != (raw & ButtonB)) { buttons |= GamepadButtons.ButtonEast; }
        if (0u != (raw & ButtonX)) { buttons |= GamepadButtons.ButtonWest; }
        if (0u != (raw & ButtonY)) { buttons |= GamepadButtons.ButtonNorth; }

        if (0u != (raw & ButtonDpadUp)) { buttons |= GamepadButtons.DpadUp; }
        if (0u != (raw & ButtonDpadDown)) { buttons |= GamepadButtons.DpadDown; }
        if (0u != (raw & ButtonDpadLeft)) { buttons |= GamepadButtons.DpadLeft; }
        if (0u != (raw & ButtonDpadRight)) { buttons |= GamepadButtons.DpadRight; }

        if (0u != (raw & ButtonLeftBumper)) { buttons |= GamepadButtons.LeftShoulder; }
        if (0u != (raw & ButtonRightBumper)) { buttons |= GamepadButtons.RightShoulder; }
        if (0u != (raw & ButtonLeftStickClick)) { buttons |= GamepadButtons.LeftStickPress; }   // L3
        if (0u != (raw & ButtonRightStickClick)) { buttons |= GamepadButtons.RightStickPress; } // R3

        if (0u != (raw & ButtonView)) { buttons |= GamepadButtons.Back; }    // VIEW (squares) → Back/Select, the Xbox convention
        if (0u != (raw & ButtonMenu)) { buttons |= GamepadButtons.Start; }   // MENU (hamburger) → Start
        if (0u != (raw & ButtonSteam)) { buttons |= GamepadButtons.Guide; }  // STEAM → Guide/Home
        if (0u != (raw & ButtonQuickAccess)) { buttons |= GamepadButtons.QuickAccess; }

        // Four rear paddles, physical correspondence verified by the calibration wizard: L4/R4 are the TOP pair
        // (trigger end) so they ride the Upper flags; L5/R5 are the BOTTOM pair on the base grip flags the
        // classic pad established.
        if (0u != (raw & ButtonL4)) { buttons |= GamepadButtons.LeftUpperGrip; }
        if (0u != (raw & ButtonL5)) { buttons |= GamepadButtons.LeftGrip; }
        if (0u != (raw & ButtonR4)) { buttons |= GamepadButtons.RightUpperGrip; }
        if (0u != (raw & ButtonR5)) { buttons |= GamepadButtons.RightGrip; }

        // Trackpad clicks: the right pad reuses the neutral touchpad-click flag (as the DualSense's single pad
        // does), the left pad rides the additive companion.
        if (0u != (raw & ButtonRightPadClick)) { buttons |= GamepadButtons.Touchpad; }
        if (0u != (raw & ButtonLeftPadClick)) { buttons |= GamepadButtons.TouchpadLeft; }

        var accelerometer = ReadVector3Int16(report: report, offset: AccelerometerOffset, scale: AccelerometerGPerLsb);
        var gyro = ReadVector3Int16(report: report, offset: GyroOffset, scale: GyroRadiansPerSecondPerLsb);
        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(source: report[ImuTimestampOffset..]);
        var rightPadTouched = (0u != (raw & ButtonRightPadTouch));
        var leftPadTouched = (0u != (raw & ButtonLeftPadTouch));

        state = new GamepadState(
            Accelerometer: accelerometer,
            Buttons: buttons,
            Gyro: gyro,
            LeftStick: ReadStick(report: report, offset: LeftStickOffset),
            LeftTrigger: NormalizeTrigger(raw: BinaryPrimitives.ReadUInt16LittleEndian(source: report[TriggerLeftOffset..])),
            Orientation: m_tracker.Update(gyroRadiansPerSecond: ToFusionFrame(sensor: gyro), accelerometerG: ToFusionFrame(sensor: accelerometer), deltaSeconds: SensorDeltaSeconds(sensorTimestamp: timestamp)),
            RightStick: ReadStick(report: report, offset: RightStickOffset),
            RightTrigger: NormalizeTrigger(raw: BinaryPrimitives.ReadUInt16LittleEndian(source: report[TriggerRightOffset..])),
            SensorTimestamp: timestamp,
            // The two trackpads surface as the shared touch points (right → Touch0, left → Touch1), mirroring the
            // classic pad; pad pressure (unPressureLeft/Right) has no carrier in GamepadState and is not exposed.
            Touch0: (rightPadTouched ? ReadTouch(report: report, offset: RightPadOffset, id: 0) : default),
            Touch1: (leftPadTouched ? ReadTouch(report: report, offset: LeftPadOffset, id: 1) : default)
        );

        return true;
    }

    // Converts the free-running microsecond IMU counter into a seconds delta since the previous report. The
    // counter is 32-bit and wraps; the unsigned subtraction yields the correct forward delta across a single wrap,
    // and the tracker clamps the result so a wrap or the first sample degrades safely.
    private float SensorDeltaSeconds(uint sensorTimestamp) {
        var delta = (m_hasSensorTimestamp ? unchecked((sensorTimestamp - m_lastSensorTimestamp)) : 0u);

        m_hasSensorTimestamp = true;
        m_lastSensorTimestamp = sensorTimestamp;

        return (delta * SensorTimestampSecondsPerUnit);
    }
    // Maps the IMU axes into the fusion frame (X=right, Y=up, Z=back). Nominal: the device rests with +Z up
    // (accelZ ≈ +1 g at rest, hardware-observed), so this proper rotation routes sensor +Z to the fusion up-axis
    // (+Y); the accelerometer gravity term anchors pitch/roll regardless of the remaining sign choices.
    private static Vector3 ToFusionFrame(Vector3 sensor) {
        return new Vector3(x: sensor.X, y: sensor.Z, z: -sensor.Y);
    }
    private static Vector3 ReadVector3Int16(ReadOnlySpan<byte> report, int offset, float scale) {
        return new Vector3(
            x: (BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]) * scale),
            y: (BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..]) * scale),
            z: (BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 4)..]) * scale)
        );
    }
    private static Vector2 ReadStick(ReadOnlySpan<byte> report, int offset) {
        // Signed int16 axes centered at 0; Y grows up already (the Steam family convention), so no flip. Apply a
        // radial deadzone then rescale the remainder to the full range.
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
    private static GamepadTouchPoint ReadTouch(ReadOnlySpan<byte> report, int offset, byte id) {
        // Signed int16 X (left→right) and Y (bottom→top); normalized position = raw / 65536 + 0.5. Y is flipped to
        // the shared GamepadTouchPoint top-left origin convention (X right, Y down).
        var x = BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]);
        var y = BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..]);

        return new GamepadTouchPoint(
            Id: id,
            IsActive: true,
            Position: new Vector2(x: ((x / PadRange) + PadHalf), y: (1f - ((y / PadRange) + PadHalf)))
        );
    }
    private static float NormalizeTrigger(ushort raw) {
        if (raw <= TriggerThreshold) {
            return 0f;
        }

        return MathF.Min(x: ((raw - TriggerThreshold) / (TriggerRange - TriggerThreshold)), y: 1f);
    }

    /// <summary>
    /// Restores the controller's built-in keyboard/mouse (lizard) emulation, so it behaves as a normal desktop
    /// device again once the engine releases it. Best-effort and synchronous: the I/O loop has already stopped but
    /// the handle is still open at this point.
    /// </summary>
    public void Dispose() {
        _ = SendSettingSynchronously(settingNum: SettingLizardMode, value: LizardModeOn);
    }
}
