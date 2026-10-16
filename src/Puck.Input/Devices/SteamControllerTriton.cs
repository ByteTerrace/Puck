using System.Buffers.Binary;
using System.Numerics;
using Puck.Input.Hid;
using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// Parses the 2026 Valve Steam Controller (codename <em>Triton</em>; Linux calls it Ibex) and drives its two rumble
/// motors over HID, following SDL's <c>SDL_hidapi_steam_triton</c> and the Linux <c>hid-steam</c> driver. It is a
/// twin-stick pad with two clickable pressure-sensing trackpads, analog triggers, four rear paddles and a 6-axis
/// IMU, reached through its wireless puck receiver (product <c>0x1304</c>, one collection per slot), its Nereid
/// dongle (<c>0x1305</c>), a cable (<c>0x1302</c>) or Bluetooth LE (<c>0x1303</c>). State arrives on a
/// vendor-defined collection as report <c>0x42</c>, or <c>0x45</c> without the firmware quaternion, or
/// <c>0x47</c> with a 16-bit IMU clock; report <c>0x43</c> carries the battery, and <c>0x79</c> or <c>0x46</c> a
/// receiver pairing event.
/// </summary>
/// <remarks>
/// Control messages are feature reports under report id 1 (<c>[type][length][payload]</c>); the classic pad's
/// id-0 framing is rejected (Windows error 87). Rumble is output report <c>0x80</c>, which the firmware drops after
/// about 50 ms without a fresh write, so the device refreshes a running rumble every 40 ms. Initialization turns
/// off the keyboard-and-mouse ("lizard") emulation together with the firmware watchdog that would otherwise re-arm
/// it when no Steam client answers (Linux <c>steam_set_lizard_mode</c>). The trackpad actuators take typed haptics
/// as output reports <c>0x81</c> (pulse), <c>0x82</c> (built-in effect), <c>0x83</c> (tone), <c>0x84</c> (sweep)
/// and <c>0x85</c> (stored script), laid out as Valve's <c>controller_structs.h</c> declares them.
/// </remarks>
public sealed class SteamControllerTriton : IGamepadParser, IRumbleParser, ITrackpadHapticParser, IWirelessSlotParser, IGamepadStreamReset, IDisposable {
    private const float AccelerometerGPerLsb = (1f / 16384f);   // ±2 g over int16 (SDL)
    private const int AccelerometerOffset = 34;
    private const byte BatteryReportId = 0x43;
    private const int BatteryReportLength = 3;
    // Button bits of the 32-bit field at ButtonsOffset; the paddle, bumper, stick-click, View and Menu positions
    // are hardware-verified, and every bit agrees with SDL and Linux.
    private const uint ButtonA = (1u << 0);
    private const uint ButtonB = (1u << 1);
    private const uint ButtonDpadDown = (1u << 10);
    private const uint ButtonDpadLeft = (1u << 12);
    private const uint ButtonDpadRight = (1u << 11);
    private const uint ButtonDpadUp = (1u << 13);
    private const uint ButtonL4 = (1u << 17);             // rear paddle, left top
    private const uint ButtonL5 = (1u << 18);             // rear paddle, left bottom
    private const uint ButtonLeftBumper = (1u << 19);
    private const uint ButtonLeftGripTouch = (1u << 29);
    private const uint ButtonLeftStickTouch = (1u << 24);
    private const uint ButtonLeftTriggerClick = (1u << 27);
    private const uint ButtonLeftPadClick = (1u << 26);
    private const uint ButtonLeftPadTouch = (1u << 25);
    private const uint ButtonLeftStickClick = (1u << 15);
    private const uint ButtonMenu = (1u << 6);            // the hamburger glyph
    private const uint ButtonQuickAccess = (1u << 4);
    private const uint ButtonR4 = (1u << 7);              // rear paddle, right top
    private const uint ButtonR5 = (1u << 8);              // rear paddle, right bottom
    private const uint ButtonRightBumper = (1u << 9);
    private const uint ButtonRightGripTouch = (1u << 28);
    private const uint ButtonRightStickTouch = (1u << 20);
    private const uint ButtonRightTriggerClick = (1u << 23);
    private const uint ButtonRightPadClick = (1u << 22);
    private const uint ButtonRightPadTouch = (1u << 21);
    private const uint ButtonRightStickClick = (1u << 5);
    private const uint ButtonSteam = (1u << 16);
    private const uint ButtonView = (1u << 14);           // the overlapping-squares glyph
    private const uint ButtonX = (1u << 2);
    private const uint ButtonY = (1u << 3);
    private const int ButtonsOffset = 2;
    private const byte CommandClearDigitalMappings = 0x81;
    private const byte CommandLoadDefaultSettings = 0x8E;
    private const byte CommandSetDefaultDigitalMappings = 0x85;
    private const byte ControllerSettingSize = 3;         // settingNum (u8) + value (u16)
    private const byte FeatureReportId = 0x01;
    private const int GyroOffset = 40;
    private const float GyroRadiansPerSecondPerLsb = ((2000f / 32768f) * (MathF.PI / 180f));   // ±2000 deg/s (SDL)
    private const int HidFeatureReportBytes = 64;
    private const int HidRumbleOutputReportBytes = 10;
    private const ushort ImuModeRaw = 0x0018;             // raw accelerometer (0x08) and raw gyro (0x10)
    private const int ImuTimestampOffset = 30;            // u32 LE, microseconds (reports 0x42, 0x45)
    private const byte InputReportId = 0x42;
    private const byte InputReportNoQuaternionId = 0x45;
    private const byte InputReportShortTimestampId = 0x47;
    private const int LeftStickOffset = 10;
    private const ushort LizardModeOff = 0;
    private const int MinimumReportLength = (GyroOffset + 6);
    private const byte OutputHapticCommandReportId = 0x82;
    private const byte OutputHapticLogSweepReportId = 0x84;
    private const byte OutputHapticLfoToneReportId = 0x83;
    private const byte OutputHapticPulseReportId = 0x81;
    private const byte OutputHapticScriptReportId = 0x85;
    private const byte OutputRumbleReportId = 0x80;
    private const float PadHalf = 0.5f;
    private const float PadRange = 65536f;
    private const float PressureRange = 32768f;
    private const int RightStickOffset = 14;
    private const int RumbleLeftSpeedOffset = 4;
    private const int RumbleRightSpeedOffset = 7;
    private const float RumbleSpeedMax = 65535f;
    private const byte SetSettingsValuesCommand = 0x87;
    private const byte SettingImuMode = 48;
    private const byte SettingLizardMode = 9;
    private const byte SettingSteamWatchdog = 71;
    private const int ShortTimestampOffset = 32;          // u16 LE in 32 µs units (report 0x47)
    private const float ShortTimestampSecondsPerUnit = (32f / 1_000_000f);
    private const float TimestampSecondsPerUnit = (1f / 1_000_000f);
    private const int TriggerLeftOffset = 6;
    private const float TriggerRange = 32768f;
    private const int TriggerRightOffset = 8;
    private const int TriggerThreshold = 256;
    private const byte WirelessEventDisconnected = 1;
    private const byte WirelessStatusReportId = 0x79;
    private const byte WirelessStatusReportIdAlternate = 0x46;

    private readonly IHidDevice m_device;
    private readonly byte[] m_featureBuffer;
    private readonly byte[] m_outputBuffer;
    private readonly ImuOrientationTracker m_tracker = new();

    private bool m_hasSensorTimestamp;
    private bool m_hasState;
    private uint m_lastSensorTimestamp;
    private GamepadState m_lastState = GamepadState.Neutral;
    private GamepadPower m_power;

    /// <summary>Initializes a new instance of the <see cref="SteamControllerTriton"/> class.</summary>
    /// <param name="device">The opened HID device handle for the Triton's vendor input collection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public SteamControllerTriton(IHidDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        m_device = device;
        // Composition writes at protocol offsets, so both buffers are floored to their documented reports.
        m_featureBuffer = new byte[Math.Max(
            val1: device.FeatureReportByteLength,
            val2: HidFeatureReportBytes
        )];
        m_outputBuffer = new byte[Math.Max(
            val1: device.OutputReportByteLength,
            val2: HidRumbleOutputReportBytes
        )];
    }

    /// <inheritdoc />
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.Gyro | GamepadInputCapabilities.AnalogTriggers;
    /// <inheritdoc />
    public TimeSpan RumbleRefreshInterval => TimeSpan.FromMilliseconds(value: 40);
    /// <inheritdoc />
    public TimeSpan RumbleWriteInterval => TimeSpan.FromMilliseconds(value: 30);
    /// <inheritdoc />
    public TrackpadHapticKinds SupportedTrackpadHaptics => TrackpadHapticKinds.Pulse | TrackpadHapticKinds.Command | TrackpadHapticKinds.Tone | TrackpadHapticKinds.Sweep | TrackpadHapticKinds.Script;
    /// <inheritdoc />
    public GamepadType Type => GamepadType.SteamControllerTriton;

    void IGamepadStreamReset.ResetStreamState() {
        m_hasSensorTimestamp = false;
        m_hasState = false;
        m_lastSensorTimestamp = 0u;
        m_lastState = GamepadState.Neutral;
        m_power = GamepadPower.Unknown;
        m_tracker.Reset();
    }

    // Builds a feature message under report id 1: [1][command][payload length][payload].
    private void BuildFeature(byte command, ReadOnlySpan<byte> payload) {
        var buffer = m_featureBuffer;

        buffer.AsSpan().Clear();
        buffer[0] = FeatureReportId;
        buffer[1] = command;
        buffer[2] = ((byte)payload.Length);
        payload.CopyTo(destination: buffer.AsSpan(start: 3));
    }
    // The pulse report numbers the pads as the classic pulse command does, left 1 and right 0 (Linux
    // steam_haptic_pulse); the other haptic reports take Valve's MsgTriggerHaptic side codes, left 1, right 2 and
    // both 3.
    private static byte EncodeSide(TrackpadSide side, bool pulse) => (pulse
        ? side switch {
            TrackpadSide.Left => ((byte)1),
            TrackpadSide.Right => ((byte)0),
            _ => ((byte)2),
        }
        : side switch {
            TrackpadSide.Left => ((byte)1),
            TrackpadSide.Right => ((byte)2),
            _ => ((byte)3),
        }
    );
    // Report 0x43: [1] the charger state (0 reset, 1 discharging, 2 charging, 3 validating the source, 4 done),
    // [2] the charge in percent. A reset or a source still being validated says nothing yet (SDL).
    private static GamepadPower DecodePower(ReadOnlySpan<byte> report) {
        var percent = Math.Min(
            val1: report[2],
            val2: ((byte)100)
        );

        return report[1] switch {
            1 => new GamepadPower(
                Percent: percent,
                State: GamepadPowerState.OnBattery
            ),
            2 => new GamepadPower(
                Percent: percent,
                State: GamepadPowerState.Charging
            ),
            4 => new GamepadPower(
                Percent: 100,
                State: GamepadPowerState.Charged
            ),
            _ => GamepadPower.Unknown,
        };
    }
    private static GamepadTouchPoint ReadTouch(ReadOnlySpan<byte> report, int offset, int pressureOffset, byte id) {
        // Signed int16 X (left to right) and Y (bottom to top), normalized as raw / 65536 + 0.5 with Y flipped to
        // the top-left touch origin; pressure is u16 over 0..32768.
        var x = BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]);
        var y = BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..]);

        return new GamepadTouchPoint(
            Id: id,
            IsActive: true,
            Position: new Vector2(
                x: ((x / PadRange) + PadHalf),
                y: (1f - ((y / PadRange) + PadHalf))
            ),
            Pressure: Math.Min(
                val1: (BinaryPrimitives.ReadUInt16LittleEndian(source: report[pressureOffset..]) / PressureRange),
                val2: 1f
            )
        );
    }
    private async ValueTask<bool> SendFeatureAsync(byte command, byte[] payload, CancellationToken cancellationToken) {
        BuildFeature(
            command: command,
            payload: payload
        );

        return await HidFeatureWriteRetry.TryWriteAsync(
            buffer: m_featureBuffer,
            cancellationToken: cancellationToken,
            device: m_device
        );
    }
    private bool SendFeature(byte command, ReadOnlySpan<byte> payload) {
        BuildFeature(
            command: command,
            payload: payload
        );

        return m_device.TrySetFeatureReport(buffer: m_featureBuffer);
    }
    // Converts the IMU clock into a seconds delta since the previous report; the unsigned subtraction carries the
    // delta across a wrap of the clock's width, and the tracker clamps a first or implausible sample.
    private float SensorDeltaSeconds(uint sensorTimestamp, uint mask, float secondsPerUnit) {
        var delta = (m_hasSensorTimestamp
            ? (unchecked((sensorTimestamp - m_lastSensorTimestamp)) & mask)
            : 0u
        );

        m_hasSensorTimestamp = true;
        m_lastSensorTimestamp = sensorTimestamp;

        return (delta * secondsPerUnit);
    }
    // Maps the IMU into the fusion frame (X right, Y up, Z toward the player) as (x, z, -y), for the gyro and the
    // accelerometer alike (SDL, Linux); the pad rests with +Z up.
    private static Vector3 ToFusionFrame(Vector3 sensor) {
        return new Vector3(
            x: sensor.X,
            y: sensor.Z,
            z: -sensor.Y
        );
    }
    private ValueTask WriteRumbleAsync(ushort leftSpeed, ushort rightSpeed, CancellationToken cancellationToken) {
        var buffer = m_outputBuffer;

        // MsgHapticRumble: type (u8), intensity (u16), then per side a speed (u16) and a gain (i8).
        buffer.AsSpan().Clear();
        buffer[0] = OutputRumbleReportId;
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination: buffer.AsSpan(start: RumbleLeftSpeedOffset),
            value: leftSpeed
        );
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination: buffer.AsSpan(start: RumbleRightSpeedOffset),
            value: rightSpeed
        );

        return m_device.WriteAsync(
            buffer: buffer,
            cancellationToken: cancellationToken
        );
    }

    /// <inheritdoc />
    /// <remarks>Report <c>0x79</c> (or <c>0x46</c>) carries the event at byte 1: 1 disconnected, 2 connected,
    /// 3 newly paired.</remarks>
    public WirelessSlotEvent ClassifySlotEvent(ReadOnlySpan<byte> report) {
        if (
            (report.Length < 2) ||
            ((report[0] != WirelessStatusReportId) && (report[0] != WirelessStatusReportIdAlternate))
        ) {
            return WirelessSlotEvent.None;
        }

        return report[1] switch {
            WirelessEventDisconnected => WirelessSlotEvent.Disconnected,
            2 or 3 => WirelessSlotEvent.Connected,
            _ => WirelessSlotEvent.None,
        };
    }
    /// <summary>
    /// Restores the controller's keyboard-and-mouse emulation and default settings, its firmware watchdog included,
    /// so it behaves as a desktop device again once the engine releases it (Linux <c>steam_set_lizard_mode</c>).
    /// Best-effort and synchronous, while the handle is still open after the I/O loop stopped.
    /// </summary>
    public void Dispose() {
        _ = SendFeature(
            command: CommandSetDefaultDigitalMappings,
            payload: []
        );
        _ = SendFeature(
            command: CommandLoadDefaultSettings,
            payload: []
        );
    }
    /// <inheritdoc />
    public async ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default) {
        _ = await SendFeatureAsync(
            cancellationToken: cancellationToken,
            command: CommandClearDigitalMappings,
            payload: []
        );
        _ = await SendFeatureAsync(
            cancellationToken: cancellationToken,
            command: SetSettingsValuesCommand,
            payload: [
                SettingLizardMode, ((byte)(LizardModeOff & 0xFF)), ((byte)(LizardModeOff >> 8)),
                SettingSteamWatchdog, 0, 0,
                SettingImuMode, ((byte)(ImuModeRaw & 0xFF)), ((byte)(ImuModeRaw >> 8)),
            ]
        );
    }
    /// <inheritdoc />
    public ValueTask PlayTrackpadHapticAsync(TrackpadHapticEffect effect, CancellationToken cancellationToken = default) {
        var buffer = m_outputBuffer;
        var pulse = (effect.Kind == TrackpadHapticKinds.Pulse);

        buffer.AsSpan().Clear();
        buffer[1] = EncodeSide(
            pulse: pulse,
            side: effect.Side
        );

        switch (effect.Kind) {
            case TrackpadHapticKinds.Pulse:
                buffer[0] = OutputHapticPulseReportId;
                BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: 2), value: effect.OnMicroseconds);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: 4), value: effect.OffMicroseconds);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: 6), value: effect.Repeat);

                break;
            case TrackpadHapticKinds.Command:
                buffer[0] = OutputHapticCommandReportId;
                buffer[2] = ((byte)effect.Command);
                buffer[3] = unchecked((byte)effect.GainDecibels);

                break;
            case TrackpadHapticKinds.Tone:
                buffer[0] = OutputHapticLfoToneReportId;
                buffer[2] = unchecked((byte)effect.GainDecibels);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: 3), value: effect.FrequencyHertz);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: 5), value: effect.DurationMilliseconds);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: 7), value: effect.LfoFrequencyHertz);
                buffer[9] = Math.Min(
                    val1: effect.LfoDepthPercent,
                    val2: ((byte)100)
                );

                break;
            case TrackpadHapticKinds.Sweep:
                buffer[0] = OutputHapticLogSweepReportId;
                buffer[2] = unchecked((byte)effect.GainDecibels);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: 3), value: effect.DurationMilliseconds);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: 5), value: effect.FrequencyHertz);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: buffer.AsSpan(start: 7), value: effect.EndFrequencyHertz);

                break;
            case TrackpadHapticKinds.Script:
                buffer[0] = OutputHapticScriptReportId;
                buffer[2] = effect.ScriptId;
                buffer[3] = unchecked((byte)effect.GainDecibels);

                break;
            default:
                return ValueTask.CompletedTask;
        }

        return m_device.WriteAsync(
            buffer: buffer,
            cancellationToken: cancellationToken
        );
    }
    /// <inheritdoc />
    /// <remarks>The low band drives the left motor and the high band the right; zero on both stops them.</remarks>
    public ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default) {
        return WriteRumbleAsync(
            cancellationToken: cancellationToken,
            leftSpeed: ((ushort)(lowFrequency * RumbleSpeedMax)),
            rightSpeed: ((ushort)(highFrequency * RumbleSpeedMax))
        );
    }
    /// <inheritdoc />
    /// <remarks>A battery report yields the last state with the new charge, once a state has arrived.</remarks>
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        state = GamepadState.Neutral;

        if (report.IsEmpty) {
            return false;
        }

        if (BatteryReportId == report[0]) {
            if (
                !m_hasState ||
                (report.Length < BatteryReportLength)
            ) {
                return false;
            }

            m_power = DecodePower(report: report);
            state = m_lastState with { IsMotionSample = false, Power = m_power, };
            m_lastState = state;

            return true;
        }

        var shortTimestamp = (InputReportShortTimestampId == report[0]);

        if (
            (report.Length < MinimumReportLength) ||
            ((InputReportId != report[0]) && (InputReportNoQuaternionId != report[0]) && !shortTimestamp)
        ) {
            return false;
        }

        var raw = BinaryPrimitives.ReadUInt32LittleEndian(source: report[ButtonsOffset..]);
        var buttons = GamepadButtons.None;

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
        if (0u != (raw & ButtonLeftStickClick)) { buttons |= GamepadButtons.LeftStickPress; }
        if (0u != (raw & ButtonRightStickClick)) { buttons |= GamepadButtons.RightStickPress; }
        if (0u != (raw & ButtonView)) { buttons |= GamepadButtons.Back; }
        if (0u != (raw & ButtonMenu)) { buttons |= GamepadButtons.Start; }
        if (0u != (raw & ButtonSteam)) { buttons |= GamepadButtons.Guide; }
        if (0u != (raw & ButtonQuickAccess)) { buttons |= GamepadButtons.QuickAccess; }
        if (0u != (raw & ButtonL4)) { buttons |= GamepadButtons.LeftUpperGrip; }
        if (0u != (raw & ButtonL5)) { buttons |= GamepadButtons.LeftGrip; }
        if (0u != (raw & ButtonR4)) { buttons |= GamepadButtons.RightUpperGrip; }
        if (0u != (raw & ButtonR5)) { buttons |= GamepadButtons.RightGrip; }
        if (0u != (raw & ButtonRightPadClick)) { buttons |= GamepadButtons.Touchpad; }
        if (0u != (raw & ButtonLeftPadClick)) { buttons |= GamepadButtons.TouchpadLeft; }
        if (0u != (raw & ButtonLeftTriggerClick)) { buttons |= GamepadButtons.LeftTriggerClick; }
        if (0u != (raw & ButtonRightTriggerClick)) { buttons |= GamepadButtons.RightTriggerClick; }
        if (0u != (raw & ButtonLeftStickTouch)) { buttons |= GamepadButtons.LeftStickTouch; }
        if (0u != (raw & ButtonRightStickTouch)) { buttons |= GamepadButtons.RightStickTouch; }
        if (0u != (raw & ButtonLeftGripTouch)) { buttons |= GamepadButtons.LeftGripTouch; }
        if (0u != (raw & ButtonRightGripTouch)) { buttons |= GamepadButtons.RightGripTouch; }

        // Report 0x47 inserts a 16-bit trackpad clock before the pads, shifting them two bytes.
        var padShift = (shortTimestamp
            ? 2
            : 0
        );
        var accelerometer = ToFusionFrame(sensor: GamepadNormalization.ReadVector3Int16(
            offset: AccelerometerOffset,
            scale: AccelerometerGPerLsb,
            source: report
        ));
        var gyro = ToFusionFrame(sensor: GamepadNormalization.ReadVector3Int16(
            offset: GyroOffset,
            scale: GyroRadiansPerSecondPerLsb,
            source: report
        ));
        var timestamp = (shortTimestamp
            ? BinaryPrimitives.ReadUInt16LittleEndian(source: report[ShortTimestampOffset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(source: report[ImuTimestampOffset..])
        );
        var deltaSeconds = (shortTimestamp
            ? SensorDeltaSeconds(
                mask: 0xFFFFu,
                secondsPerUnit: ShortTimestampSecondsPerUnit,
                sensorTimestamp: timestamp
            )
            : SensorDeltaSeconds(
                mask: uint.MaxValue,
                secondsPerUnit: TimestampSecondsPerUnit,
                sensorTimestamp: timestamp
            )
        );

        state = new GamepadState(
            Accelerometer: accelerometer,
            Buttons: buttons,
            Gyro: gyro,
            LeftStick: SteamControllerInput.ReadStick(
                offset: LeftStickOffset,
                report: report
            ),
            LeftTrigger: GamepadNormalization.NormalizeTrigger(
                raw: BinaryPrimitives.ReadUInt16LittleEndian(source: report[TriggerLeftOffset..]),
                threshold: TriggerThreshold,
                range: TriggerRange
            ),
            Orientation: m_tracker.Update(
                accelerometerG: accelerometer,
                deltaSeconds: deltaSeconds,
                gyroRadiansPerSecond: gyro
            ),
            Power: m_power,
            RightStick: SteamControllerInput.ReadStick(
                offset: RightStickOffset,
                report: report
            ),
            RightTrigger: GamepadNormalization.NormalizeTrigger(
                raw: BinaryPrimitives.ReadUInt16LittleEndian(source: report[TriggerRightOffset..]),
                threshold: TriggerThreshold,
                range: TriggerRange
            ),
            SensorTimestamp: timestamp,
            // Right pad on Touch0 and left on Touch1, as on the classic pad.
            Touch0: ((0u != (raw & ButtonRightPadTouch))
                ? ReadTouch(
                    id: 0,
                    offset: (24 + padShift),
                    pressureOffset: (28 + padShift),
                    report: report
                )
                : default
            ),
            Touch1: ((0u != (raw & ButtonLeftPadTouch))
                ? ReadTouch(
                    id: 1,
                    offset: (18 + padShift),
                    pressureOffset: (22 + padShift),
                    report: report
                )
                : default
            )
        );
        m_hasState = true;
        m_lastState = state;

        return true;
    }
}
