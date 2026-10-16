using System.Buffers.Binary;
using System.Numerics;
using Puck.Input.Hid;
using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// Parses the classic Valve Steam Controller (one analog stick, two clickable trackpads, analog triggers, two rear
/// grip buttons and a 6-axis IMU) and drives its trackpad haptics and Steam-button LED over HID, following SDL's
/// <c>SDL_hidapi_steam</c> and the Linux <c>hid-steam</c> driver. The controller speaks a vendor-defined collection:
/// on open it is taken out of its keyboard-and-mouse ("lizard") emulation through feature reports, after which it
/// streams <c>ValveInReport</c> messages: type 1 carries state, type 3 a receiver pairing event and type 4 the
/// wireless battery status.
/// </summary>
/// <remarks>
/// Wired (product <c>0x1102</c>) and each slot of the four-slot wireless receiver (product <c>0x1142</c>) share
/// this parser. The left trackpad and the stick share one field: the firmware reports whichever is in use, and
/// interleaves the two while both are, so each keeps its last value between the reports that carry the other.
/// The right trackpad doubles as the right stick, and the trackpads surface as <see cref="GamepadState.Touch0"/>
/// (right) and <see cref="GamepadState.Touch1"/> (left).
/// </remarks>
public sealed class SteamController : IGamepadParser, IRumbleParser, ILedParser, ITrackpadHapticParser, IWirelessSlotParser, IGamepadStreamReset, IDisposable {
    private const float AccelerometerGPerLsb = (1f / 16384f);
    private const int AccelerometerOffset = 28;
    private const byte AttributeConnectionInterval = 11;
    private const int AttributeSize = 5;                       // tag (u8) + value (u32 LE)
    private const int ButtonsOffset = 8;
    private const byte CommandClearDigitalMappings = 0x81;
    private const byte CommandGetAttributes = 0x83;
    private const byte CommandLoadDefaultSettings = 0x8E;
    private const byte CommandSetDefaultDigitalMappings = 0x85;
    private const byte CommandSetSettings = 0x87;
    private const byte CommandTriggerHapticPulse = 0x8F;
    private const uint DefaultConnectionIntervalMicroseconds = 9000;
    private const int FeatureReportBytes = 65;                 // report id 0 + 64-byte message
    private const int GyroOffset = 34;
    // The gyro spans ±2000 deg/s over int16 (SDL, Linux).
    private const float GyroRadiansPerSecondPerLsb = ((2000f / 32768f) * (MathF.PI / 180f));
    // The trackpad actuators are driven by a pulse train: each pulse is on for a share of the period that grows
    // with intensity, and a train spans a little more than one refresh, so refreshing sustains it seamlessly.
    private const float HapticTrainMicroseconds = 60_000f;
    private const ushort HapticLeftPeriodMicroseconds = 1300;  // low band, lower pitch
    private const ushort HapticRightPeriodMicroseconds = 900;  // high band, higher pitch
    private const byte HapticSideLeft = 1;                     // the pulse command swaps the pads for legacy reasons
    private const byte HapticSideRight = 0;
    private const ushort ImuModeRaw = 0x0018;                  // raw accelerometer (0x08) and raw gyro (0x10)
    private const int LeftPadOffset = 16;                      // shared with the stick, see the remarks
    private const int LeftTriggerOffset = 11;
    private const int MinimumStatePayload = 40;
    private const int PacketNumberOffset = 4;
    private const int RightPadOffset = 20;
    private const int RightTriggerOffset = 12;
    private const byte SettingImuMode = 48;
    private const byte SettingLeftTrackpadMode = 7;
    private const byte SettingLedBrightness = 45;
    private const byte SettingRightTrackpadMode = 8;
    private const byte SettingSmoothAbsoluteMouse = 24;
    private const byte SettingWirelessPacketVersion = 49;
    private const byte StateReportType = 0x01;
    private const int StatusBatteryPercentOffset = 14;
    private const byte StatusReportType = 0x04;
    private const float StickRange = 32768f;
    private const byte TrackpadModeAbsoluteMouse = 0;
    private const byte TrackpadModeNone = 7;
    private const float TrackpadHalf = 32768f;
    private const float TrackpadRange = 65535f;
    // The trigger byte reaches full pull near 201.55: SDL widens it to (n << 7) | n and saturates at 26000.
    private const float TriggerRange = (26000f / 129f);
    private const byte TriggerThreshold = 8;
    private const byte WirelessEventDisconnected = 1;
    private const byte WirelessEventReportType = 0x03;

    // ulButtons bits (SDL STEAM_*_MASK), as a 24-bit little-endian field at ButtonsOffset.
    private const uint MaskLeftPadAndStick = 0x00800000;
    private const uint MaskLeftPadClicked = 0x00020000;
    private const uint MaskLeftPadFingerDown = 0x00080000;
    private const uint MaskRightPadFingerDown = 0x00100000;
    private const uint MaskStickClicked = 0x00400000;

    private readonly IHidDevice m_device;
    private readonly byte[] m_featureBuffer;
    private readonly ImuOrientationTracker m_tracker = new();

    private bool m_hasPacketNumber;
    private bool m_hasPreviousPad;
    private uint m_lastPacketNumber;
    private GamepadState m_lastState = GamepadState.Neutral;
    private Vector2 m_previousLeftPad;
    private Vector2 m_previousLeftStick;
    private GamepadPower m_power;
    private float m_sampleSeconds = (DefaultConnectionIntervalMicroseconds / 1_000_000f);

    /// <summary>Initializes a new instance of the <see cref="SteamController"/> class.</summary>
    /// <param name="device">The opened HID device handle for the controller's vendor input collection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public SteamController(IHidDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        m_device = device;
        // Composition writes at protocol offsets, so the buffer is floored to the documented feature report.
        m_featureBuffer = new byte[Math.Max(
            val1: device.FeatureReportByteLength,
            val2: FeatureReportBytes
        )];
    }

    /// <inheritdoc />
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.Gyro | GamepadInputCapabilities.AnalogTriggers;
    /// <inheritdoc />
    public TimeSpan RumbleRefreshInterval => TimeSpan.FromMilliseconds(value: 50);
    /// <inheritdoc />
    public TimeSpan RumbleWriteInterval => TimeSpan.FromMilliseconds(value: 30);
    /// <inheritdoc />
    /// <remarks>The classic pad plays pulse trains only (feature command <c>0x8F</c>).</remarks>
    public TrackpadHapticKinds SupportedTrackpadHaptics => TrackpadHapticKinds.Pulse;
    /// <inheritdoc />
    public GamepadType Type => GamepadType.SteamController;

    void IGamepadStreamReset.ResetStreamState() {
        m_hasPacketNumber = false;
        m_hasPreviousPad = false;
        m_lastState = GamepadState.Neutral;
        m_power = GamepadPower.Unknown;
        m_previousLeftPad = Vector2.Zero;
        m_previousLeftStick = Vector2.Zero;
        m_tracker.Reset();
    }

    private bool FireHaptic(byte side, float intensity, ushort period) {
        var on = ((ushort)(Math.Clamp(
            max: 1f,
            min: 0f,
            value: intensity
        ) * period));
        var count = ((0 == on)
            ? ((ushort)0)
            : ((ushort)MathF.Ceiling(x: (HapticTrainMicroseconds / period)))
        );
        var off = ((ushort)(period - on));

        return FirePulse(
            count: count,
            gainDecibels: 0,
            off: off,
            on: on,
            side: side
        );
    }
    // MsgFireHapticPulse: pad, on-time and off-time in µs, pulse count, gain in dB (Linux steam_haptic_pulse).
    private bool FirePulse(byte side, ushort on, ushort off, ushort count, sbyte gainDecibels) {
        return SendFeature(
            command: CommandTriggerHapticPulse,
            payload: [
                side,
                ((byte)(on & 0xFF)), ((byte)(on >> 8)),
                ((byte)(off & 0xFF)), ((byte)(off >> 8)),
                ((byte)(count & 0xFF)), ((byte)(count >> 8)),
                unchecked((byte)gainDecibels),
            ]
        );
    }
    // Reads the controller's attributes (feature 0x83) for its connection interval, which is the spacing of its
    // state reports and so the IMU's sample period.
    private void LoadConnectionInterval() {
        if (!SendFeature(
            command: CommandGetAttributes,
            payload: []
        )) {
            return;
        }

        var reply = m_featureBuffer;

        reply.AsSpan().Clear();

        if (
            !m_device.TryGetFeatureReport(buffer: reply) ||
            (reply[1] != CommandGetAttributes)
        ) {
            return;
        }

        var length = Math.Min(
            val1: reply[2],
            val2: (reply.Length - 3)
        );

        for (var index = 0; ((index + AttributeSize) <= length); index += AttributeSize) {
            if (reply[(3 + index)] != AttributeConnectionInterval) {
                continue;
            }

            var microseconds = BinaryPrimitives.ReadUInt32LittleEndian(source: reply.AsSpan(start: (4 + index)));

            if (
                (microseconds > 0u) &&
                (microseconds < 100_000u)
            ) {
                m_sampleSeconds = (microseconds / 1_000_000f);
            }

            return;
        }
    }
    private static Vector2 ReadPad(ReadOnlySpan<byte> report, int offset) {
        return new Vector2(
            x: BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]),
            y: BinaryPrimitives.ReadInt16LittleEndian(source: report[(offset + 2)..])
        );
    }
    private static Vector2 PadAsStick(Vector2 pad) {
        // The absolute pad position as a stick vector, without a deadzone: a touch anywhere is a deliberate aim.
        return Vector2.Clamp(
            max: Vector2.One,
            min: -Vector2.One,
            value1: (pad / StickRange)
        );
    }
    private static GamepadTouchPoint PadAsTouch(Vector2 pad, byte id) {
        // int16 X grows right and Y grows up; the touch convention is 0..1 with the origin top-left.
        return new GamepadTouchPoint(
            Id: id,
            IsActive: true,
            Position: new Vector2(
                x: ((pad.X + TrackpadHalf) / TrackpadRange),
                y: (1f - ((pad.Y + TrackpadHalf) / TrackpadRange))
            )
        );
    }
    private bool SendFeature(byte command, ReadOnlySpan<byte> payload) {
        var buffer = m_featureBuffer;

        buffer.AsSpan().Clear();
        buffer[0] = 0;                       // the vendor collection is written as report id 0
        buffer[1] = command;
        buffer[2] = ((byte)payload.Length);
        payload.CopyTo(destination: buffer.AsSpan(start: 3));

        return m_device.TrySetFeatureReport(buffer: buffer);
    }
    private async ValueTask<bool> SendFeatureAsync(byte command, byte[] payload, CancellationToken cancellationToken) {
        var buffer = m_featureBuffer;

        buffer.AsSpan().Clear();
        buffer[0] = 0;
        buffer[1] = command;
        buffer[2] = ((byte)payload.Length);
        payload.CopyTo(destination: buffer.AsSpan(start: 3));

        return await HidFeatureWriteRetry.TryWriteAsync(
            buffer: buffer,
            cancellationToken: cancellationToken,
            device: m_device
        );
    }
    // The report begins with unReportVersion (0x0001) then ucType. Windows may or may not prepend a report-id
    // byte, so the version marker is accepted at offset 0 or 1.
    private static bool TryFindReportBase(ReadOnlySpan<byte> report, out int dataStart) {
        if (
            (report.Length >= 4) &&
            (report[0] == 0x01) &&
            (report[1] == 0x00)
        ) {
            dataStart = 0;

            return true;
        }

        if (
            (report.Length >= 5) &&
            (report[1] == 0x01) &&
            (report[2] == 0x00)
        ) {
            dataStart = 1;

            return true;
        }

        dataStart = 0;

        return false;
    }
    private bool TryParseState(ReadOnlySpan<byte> report, int dataStart, out GamepadState state) {
        var raw = (report[(dataStart + ButtonsOffset)] |
            ((uint)report[((dataStart + ButtonsOffset) + 1)] << 8) |
            ((uint)report[((dataStart + ButtonsOffset) + 2)] << 16));
        var buttons = GamepadButtons.None;

        if (0u != (raw & 0x000001)) { buttons |= GamepadButtons.RightTriggerClick; }
        if (0u != (raw & 0x000002)) { buttons |= GamepadButtons.LeftTriggerClick; }
        if (0u != (raw & 0x000080)) { buttons |= GamepadButtons.ButtonSouth; }    // A
        if (0u != (raw & 0x000020)) { buttons |= GamepadButtons.ButtonEast; }     // B
        if (0u != (raw & 0x000040)) { buttons |= GamepadButtons.ButtonWest; }     // X
        if (0u != (raw & 0x000010)) { buttons |= GamepadButtons.ButtonNorth; }    // Y
        if (0u != (raw & 0x000008)) { buttons |= GamepadButtons.LeftShoulder; }
        if (0u != (raw & 0x000004)) { buttons |= GamepadButtons.RightShoulder; }
        if (0u != (raw & 0x000100)) { buttons |= GamepadButtons.DpadUp; }
        if (0u != (raw & 0x000200)) { buttons |= GamepadButtons.DpadRight; }
        if (0u != (raw & 0x000400)) { buttons |= GamepadButtons.DpadLeft; }
        if (0u != (raw & 0x000800)) { buttons |= GamepadButtons.DpadDown; }
        if (0u != (raw & 0x001000)) { buttons |= GamepadButtons.Back; }           // left arrow
        if (0u != (raw & 0x002000)) { buttons |= GamepadButtons.Guide; }          // Steam
        if (0u != (raw & 0x004000)) { buttons |= GamepadButtons.Start; }          // right arrow
        if (0u != (raw & 0x008000)) { buttons |= GamepadButtons.LeftGrip; }
        if (0u != (raw & 0x010000)) { buttons |= GamepadButtons.RightGrip; }
        if (0u != (raw & 0x040000)) { buttons |= GamepadButtons.RightStickPress; } // right pad click
        if (0u != (raw & MaskStickClicked)) { buttons |= GamepadButtons.LeftStickPress; }

        var fingerDown = (0u != (raw & MaskLeftPadFingerDown));
        var interleaved = (0u != (raw & MaskLeftPadAndStick));
        var shared = ReadPad(
            offset: (dataStart + LeftPadOffset),
            report: report
        );
        Vector2 leftStickRaw;
        Vector2? leftPad;

        // A cached value survives only while the firmware interleaves the two; otherwise the channel the report
        // does not carry is at rest, and its cache is cleared so a later interleave cannot resurrect it (SDL).
        if (fingerDown) {
            leftPad = shared;
            m_previousLeftPad = shared;
            m_hasPreviousPad = true;

            if (!interleaved) {
                m_previousLeftStick = Vector2.Zero;
            }

            leftStickRaw = m_previousLeftStick;
        } else {
            leftStickRaw = shared;
            m_previousLeftStick = shared;

            if (!interleaved) {
                m_hasPreviousPad = false;
            }

            leftPad = (m_hasPreviousPad
                ? m_previousLeftPad
                : null
            );
        }

        if (0u != (raw & MaskLeftPadClicked)) {
            // Older firmware reports the stick click here when no finger is on the left pad.
            buttons |= ((fingerDown || interleaved)
                ? GamepadButtons.TouchpadLeft
                : GamepadButtons.LeftStickPress
            );
        }

        var rightPadTouched = (0u != (raw & MaskRightPadFingerDown));
        var rightPad = ReadPad(
            offset: (dataStart + RightPadOffset),
            report: report
        );
        var packetNumber = BinaryPrimitives.ReadUInt32LittleEndian(source: report[(dataStart + PacketNumberOffset)..]);
        var repeated = (m_hasPacketNumber && (packetNumber == m_lastPacketNumber));
        var accelerometer = ToFusionAccelerometer(sensor: GamepadNormalization.ReadVector3Int16(
            offset: (dataStart + AccelerometerOffset),
            scale: AccelerometerGPerLsb,
            source: report
        ));
        var gyro = ToFusionGyro(sensor: GamepadNormalization.ReadVector3Int16(
            offset: (dataStart + GyroOffset),
            scale: GyroRadiansPerSecondPerLsb,
            source: report
        ));

        m_hasPacketNumber = true;
        m_lastPacketNumber = packetNumber;

        state = new GamepadState(
            Accelerometer: accelerometer,
            Buttons: buttons,
            Gyro: gyro,
            IsMotionSample: !repeated,
            LeftStick: SteamControllerInput.Deadzone(stick: (leftStickRaw / StickRange)),
            LeftTrigger: GamepadNormalization.NormalizeTrigger(
                raw: report[(dataStart + LeftTriggerOffset)],
                threshold: TriggerThreshold,
                range: TriggerRange
            ),
            // A repeated packet carries no new sample, so it does not advance the fusion.
            Orientation: (repeated
                ? m_tracker.Orientation
                : m_tracker.Update(
                    accelerometerG: accelerometer,
                    deltaSeconds: m_sampleSeconds,
                    gyroRadiansPerSecond: gyro
                )
            ),
            Power: m_power,
            RightStick: (rightPadTouched
                ? PadAsStick(pad: rightPad)
                : Vector2.Zero
            ),
            RightTrigger: GamepadNormalization.NormalizeTrigger(
                raw: report[(dataStart + RightTriggerOffset)],
                threshold: TriggerThreshold,
                range: TriggerRange
            ),
            Touch0: (rightPadTouched
                ? PadAsTouch(
                    id: 0,
                    pad: rightPad
                )
                : default
            ),
            Touch1: ((leftPad is { } pad)
                ? PadAsTouch(
                    id: 1,
                    pad: pad
                )
                : default
            )
        );
        m_lastState = state;

        return true;
    }
    // The classic pad's IMU frame maps to the fusion frame (X right, Y up, Z toward the player) as gyro (x, z, y)
    // and accelerometer (x, z, -y): the two differ in the sign of Y on this pad alone (SDL, Linux).
    private static Vector3 ToFusionAccelerometer(Vector3 sensor) => new(
        x: sensor.X,
        y: sensor.Z,
        z: -sensor.Y
    );
    private static Vector3 ToFusionGyro(Vector3 sensor) => new(
        x: sensor.X,
        y: sensor.Z,
        z: sensor.Y
    );

    /// <inheritdoc />
    /// <remarks>Any event but disconnect (1) is a connection: established (2) or newly paired (3).</remarks>
    public WirelessSlotEvent ClassifySlotEvent(ReadOnlySpan<byte> report) {
        if (
            !TryFindReportBase(
            dataStart: out var dataStart,
            report: report
        ) ||
            (report.Length <= (dataStart + 4)) ||
            (report[(dataStart + 2)] != WirelessEventReportType) ||
            (report[(dataStart + 3)] < 1)
        ) {
            return WirelessSlotEvent.None;
        }

        return ((report[(dataStart + 4)] == WirelessEventDisconnected)
            ? WirelessSlotEvent.Disconnected
            : WirelessSlotEvent.Connected
        );
    }
    /// <summary>
    /// Restores the controller's keyboard-and-mouse emulation so it behaves as a desktop device again once the
    /// engine releases it: the default mappings, the default settings, and the right trackpad as a mouse.
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
        _ = SendFeature(
            command: CommandSetSettings,
            payload: [SettingRightTrackpadMode, TrackpadModeAbsoluteMouse, 0,]
        );
    }
    /// <inheritdoc />
    public async ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default) {
        // Leave lizard mode: drop the keyboard mappings, stop both trackpads emulating a mouse, use the current
        // wireless packet format, and stream raw IMU (SDL ResetSteamController). Each write is best-effort.
        _ = await SendFeatureAsync(
            cancellationToken: cancellationToken,
            command: CommandClearDigitalMappings,
            payload: []
        );
        _ = await SendFeatureAsync(
            cancellationToken: cancellationToken,
            command: CommandSetSettings,
            payload: [
                SettingWirelessPacketVersion, 2, 0,
                SettingLeftTrackpadMode, TrackpadModeNone, 0,
                SettingRightTrackpadMode, TrackpadModeNone, 0,
                SettingSmoothAbsoluteMouse, 0, 0,
                SettingImuMode, ((byte)(ImuModeRaw & 0xFF)), ((byte)(ImuModeRaw >> 8)),
            ]
        );
        LoadConnectionInterval();
    }
    /// <inheritdoc />
    public ValueTask PlayTrackpadHapticAsync(TrackpadHapticEffect effect, CancellationToken cancellationToken = default) {
        if (effect.Kind != TrackpadHapticKinds.Pulse) {
            return ValueTask.CompletedTask;
        }

        // The pulse command numbers the pads left 1, right 0 and both 2, swapped for legacy reasons (Linux).
        _ = FirePulse(
            count: effect.Repeat,
            gainDecibels: effect.GainDecibels,
            off: effect.OffMicroseconds,
            on: effect.OnMicroseconds,
            side: effect.Side switch {
                TrackpadSide.Left => HapticSideLeft,
                TrackpadSide.Right => HapticSideRight,
                _ => ((byte)2),
            }
        );

        return ValueTask.CompletedTask;
    }
    /// <inheritdoc />
    /// <remarks>The Steam-button LED has one channel, so its brightness follows the brightest of the three.</remarks>
    public ValueTask SetLedAsync(LedColor color, CancellationToken cancellationToken = default) {
        var brightness = ((byte)((Math.Max(
            val1: color.Red,
            val2: Math.Max(
                val1: color.Green,
                val2: color.Blue
            )
        ) * 100) / 255));

        _ = SendFeature(
            command: CommandSetSettings,
            payload: [SettingLedBrightness, brightness, 0,]
        );

        return ValueTask.CompletedTask;
    }
    /// <inheritdoc />
    /// <remarks>The low band drives the left trackpad's actuator and the high band the right's; intensity sets
    /// each pulse's share of its period.</remarks>
    public ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default) {
        _ = FireHaptic(
            intensity: lowFrequency,
            period: HapticLeftPeriodMicroseconds,
            side: HapticSideLeft
        );
        _ = FireHaptic(
            intensity: highFrequency,
            period: HapticRightPeriodMicroseconds,
            side: HapticSideRight
        );

        return ValueTask.CompletedTask;
    }
    /// <inheritdoc />
    /// <remarks>A battery status report (type 4) yields the last state with the new charge, once a state has
    /// arrived; a pairing event and anything else are not state.</remarks>
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        state = GamepadState.Neutral;

        if (!TryFindReportBase(
            dataStart: out var dataStart,
            report: report
        )) {
            return false;
        }

        var type = report[(dataStart + 2)];

        if (
            (StatusReportType == type) &&
            m_hasPacketNumber &&
            (report.Length > (dataStart + StatusBatteryPercentOffset))
        ) {
            m_power = new GamepadPower(
                Percent: Math.Min(
                    val1: report[(dataStart + StatusBatteryPercentOffset)],
                    val2: ((byte)100)
                ),
                State: GamepadPowerState.OnBattery
            );
            state = m_lastState with { IsMotionSample = false, Power = m_power, };
            m_lastState = state;

            return true;
        }

        return (
            (StateReportType == type) &&
            (report.Length >= ((dataStart + MinimumStatePayload) + 6)) &&
            TryParseState(
                dataStart: dataStart,
                report: report,
                state: out state
            )
        );
    }
}
