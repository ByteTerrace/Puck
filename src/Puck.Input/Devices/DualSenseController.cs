using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using Puck.Input.Hid;
using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// Parses the Sony DualSense (PlayStation 5) and DualSense Edge controllers and drives their rumble, adaptive
/// triggers, light bar, player LEDs and microphone-mute LED over HID, following the Linux <c>hid-playstation</c>
/// driver and SDL's <c>SDL_hidapi_ps5</c>. USB streams the full input report <c>0x01</c> (64 bytes) at once.
/// Bluetooth starts on the simple report <c>0x01</c> (sticks, buttons and triggers only) and switches to the full
/// report <c>0x31</c> (78 bytes with a trailing CRC) once feature report <c>0x05</c> is read during initialization.
/// Output is one report carrying every section: <c>0x02</c> on USB, <c>0x31</c> with a seeded CRC on Bluetooth.
/// </summary>
/// <remarks>
/// The IMU frame is X right, Y up, Z toward the player, which is the fusion frame, so the sensor vectors pass
/// through unchanged. The firmware does not light the microphone-mute LED or mute the microphone itself; as on the
/// console, each press of the mute button toggles both.
/// </remarks>
public sealed class DualSenseController : IGamepadParser, IRumbleParser, ILedParser, ITriggerEffectParser, IServicedGamepadParser {
    // Offsets relative to the shared input block (byte 1 of the USB report, byte 2 of Bluetooth 0x31).
    private const int AccelerometerOffset = 21;
    private const float AccelerometerGPerLsb = (1f / 8192f);
    private const int BluetoothFullReportLength = 78;
    private const byte BluetoothInputCrcSeed = 0xA1;
    private const byte BluetoothInputReportId = 0x31;
    private const byte BluetoothOutputReportId = 0x31;
    // The logical Bluetooth 0x31 output report is 78 bytes with its CRC32 in the last four. Windows requires a
    // write of the declared OutputReportByteLength, so the buffer may be longer, but the CRC still sits at byte 74.
    private const int BluetoothOutputReportLength = 78;
    private const byte BluetoothOutputTag = 0x10;
    private const int ButtonsOffset = 7;
    private const byte CalibrationFeatureReportId = 0x05;
    private const int CalibrationFeatureReportLength = 41;
    // A freshly connected DualSense may not answer a feature read at once, so the calibration read is retried.
    private const int CalibrationReadAttempts = 12;
    private const int CalibrationRetryDelayMilliseconds = 30;
    private const ushort EdgeProductId = 0x0DF2;
    private const byte FirmwareFeatureReportId = 0x20;
    private const int FirmwareFeatureReportLength = 64;
    private const int FirmwareUpdateVersionOffset = 44;
    private const byte Flag0CompatibleVibration = 0x01;
    private const byte Flag0HapticsSelect = 0x02;
    private const byte Flag0LeftTriggerFeedback = 0x08;
    private const byte Flag0RightTriggerFeedback = 0x04;
    private const byte Flag1LightbarControl = 0x04;
    private const byte Flag1MicMuteLedControl = 0x01;
    private const byte Flag1PlayerIndicatorControl = 0x10;
    private const byte Flag1PowerSaveControl = 0x02;
    private const byte Flag1ReleaseLeds = 0x08;
    private const byte Flag2CompatibleVibration2 = 0x04;
    private const byte Flag2LightbarSetup = 0x02;
    // The nominal gyro scale is 1/16 deg/s per LSB: the 1024 LSB per deg/s resolution times the ~64 sensitivity
    // a factory calibration carries. A calibration whose scale strays more than half from it is rejected (SDL).
    private const float GyroRadiansPerSecondPerLsbNominal = ((64f * (MathF.PI / 180f)) / 1024f);
    // Firmware 2.24 and newer emulates rumble more faithfully through the second compatible-vibration flag; older
    // firmware needs the first flag and drives the motors about twice as hard for the same byte (SDL).
    private const ushort ImprovedRumbleFirmwareVersion = 0x0224;
    private const int LeftTriggerOffset = 4;
    // Bluetooth connection plays a light-bar animation that swallows colour writes until it ends, about 3.4 s
    // into the sensor clock (SDL); the colour is reasserted once the clock passes it.
    private const uint LightbarAnimationEndTimestamp = 10_200_000u;
    private const byte LightbarSetupLightOut = 0x02;
    private const int MaximumCalibrationBias = 1024;
    private const float MaximumCalibrationScaleError = 0.5f;
    private const byte MicMuteLedOn = 0x01;
    private const byte OutputCrcSeed = 0xA2;
    // Output offsets relative to the shared output block (byte 1 of USB 0x02, byte 3 of Bluetooth 0x31).
    private const int OutputLeftTriggerOffset = 21;
    private const int OutputLightbarRedOffset = 44;
    private const int OutputLightbarSetupOffset = 41;
    private const int OutputMicMuteLedOffset = 8;
    private const int OutputMotorLeftOffset = 3;
    private const int OutputMotorRightOffset = 2;
    private const int OutputPlayerLedsOffset = 43;
    private const int OutputPowerSaveOffset = 9;
    private const int OutputRightTriggerOffset = 10;
    private const int OutputValidFlag2Offset = 38;
    private const byte PlayerLedsInstant = 0x20;
    private const byte PowerSaveMicMute = 0x10;
    private const int RightTriggerOffset = 5;
    private const int SensorTimestampOffset = 27;
    private const float SensorTimestampSecondsPerUnit = (1f / 3_000_000f); // the counter ticks in thirds of a µs
    private const int SimpleButtonsOffset = 4;
    private const int SimpleLeftTriggerOffset = 7;
    private const int SimpleReportLength = 10;
    private const int SimpleRightTriggerOffset = 8;
    private const int StatusOffset = 52;
    private const int StickCenter = 128;
    private const float StickDeadzone = 0.12f;
    private const float StickRange = 127f;
    private const int Touch0Offset = 32;
    private const int Touch1Offset = 36;
    // The touchpad reports 12-bit coordinates over a 1920 × 1080 surface.
    private const float TouchpadMaximumX = 1919f;
    private const float TouchpadMaximumY = 1079f;
    private const float TriggerRange = 255f;
    private const byte TriggerThreshold = 8;
    private const int UsbFullReportLength = 64;
    private const byte UsbInputReportId = 0x01;
    private const byte UsbOutputReportId = 0x02;
    private const int UsbOutputReportLength = 48;

    // The touchpad player-indicator patterns, indexed by player slot (SDL).
    private static ReadOnlySpan<byte> PlayerLeds => [0x04, 0x0A, 0x15, 0x1B, 0x1F, 0x11, 0x0E,];

    private readonly IHidDevice m_device;
    private readonly bool m_isBluetooth;
    private readonly bool m_isEdge;
    private readonly byte[] m_outputBuffer;
    private readonly ImuOrientationTracker m_tracker = new();

    private Vector3 m_accelerometerBias;
    private Vector3 m_accelerometerScale = new(value: AccelerometerGPerLsb);
    private Vector3 m_gyroBias;
    private Vector3 m_gyroScale = new(value: GyroRadiansPerSecondPerLsbNominal);
    private bool m_hasSensorTimestamp;
    private bool m_improvedRumble;
    private uint m_lastSensorTimestamp;
    private TriggerEffectSpec m_leftTriggerEffect;
    private bool m_lightbarReassertPending;
    private LedColor m_lightbar;
    private bool m_micMuted;
    private bool m_muteHeld;
    private byte m_motorHigh;
    private byte m_motorLow;
    private byte m_outputSequence;
    private bool m_outputStale;
    private byte m_playerLeds;
    private TriggerEffectSpec m_rightTriggerEffect;

    /// <summary>Initializes a new instance of the <see cref="DualSenseController"/> class.</summary>
    /// <param name="device">The opened HID device handle for the DualSense or DualSense Edge.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public DualSenseController(IHidDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        m_device = device;
        m_isBluetooth = (device.Transport == HidTransport.Bluetooth);
        m_isEdge = (device.ProductId == EdgeProductId);
        // Composition writes at protocol offsets, so the buffer is floored to the transport's documented report; a
        // smaller declared length is nonconforming and its write is rejected by the HID stack instead.
        m_outputBuffer = new byte[Math.Max(
            val1: device.OutputReportByteLength,
            val2: (m_isBluetooth
                ? BluetoothOutputReportLength
                : UsbOutputReportLength
            )
        )];
    }

    /// <inheritdoc />
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.Gyro | GamepadInputCapabilities.AnalogTriggers;
    /// <inheritdoc />
    public TimeSpan RumbleRefreshInterval => TimeSpan.Zero;
    /// <inheritdoc />
    public TimeSpan RumbleWriteInterval => TimeSpan.FromMilliseconds(value: 30);
    /// <inheritdoc />
    public GamepadType Type => GamepadType.PlayStation5;

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
    // CRC-32/ISO-HDLC over a single seed byte followed by the data: the DualSense's per-direction seeded CRC
    // (0xA1 input, 0xA2 output).
    private static uint ComputeCrc32(byte seed, ReadOnlySpan<byte> data) {
        var crc = new Crc32();

        crc.Append(source: [seed]);
        crc.Append(source: data);

        return crc.GetCurrentHashAsUInt32();
    }
    private GamepadButtons DecodeButtons(ReadOnlySpan<byte> buttons) {
        var buttons0 = buttons[0];
        var buttons1 = buttons[1];
        var buttons2 = buttons[2];
        var result = GamepadButtons.None;

        // Face buttons by physical position: Cross (bottom) → South, Circle (right) → East, Square (left) →
        // West, Triangle (top) → North.
        if (0 != (buttons0 & 0x20)) { result |= GamepadButtons.ButtonSouth; }
        if (0 != (buttons0 & 0x40)) { result |= GamepadButtons.ButtonEast; }
        if (0 != (buttons0 & 0x10)) { result |= GamepadButtons.ButtonWest; }
        if (0 != (buttons0 & 0x80)) { result |= GamepadButtons.ButtonNorth; }

        ApplyDpad(
            buttons: ref result,
            hat: (buttons0 & 0x0F)
        );

        if (0 != (buttons1 & 0x01)) { result |= GamepadButtons.LeftShoulder; }  // L1
        if (0 != (buttons1 & 0x02)) { result |= GamepadButtons.RightShoulder; } // R1
        if (0 != (buttons1 & 0x10)) { result |= GamepadButtons.Back; }          // Create
        if (0 != (buttons1 & 0x20)) { result |= GamepadButtons.Start; }         // Options
        if (0 != (buttons1 & 0x40)) { result |= GamepadButtons.LeftStickPress; }
        if (0 != (buttons1 & 0x80)) { result |= GamepadButtons.RightStickPress; }

        if (0 != (buttons2 & 0x01)) { result |= GamepadButtons.Guide; }         // PS
        if (0 != (buttons2 & 0x02)) { result |= GamepadButtons.Touchpad; }
        if (0 != (buttons2 & 0x04)) { result |= GamepadButtons.Mute; }

        if (m_isEdge) {
            if (0 != (buttons2 & 0x10)) { result |= GamepadButtons.LeftFunction; }
            if (0 != (buttons2 & 0x20)) { result |= GamepadButtons.RightFunction; }
            if (0 != (buttons2 & 0x40)) { result |= GamepadButtons.LeftGrip; }   // left back paddle
            if (0 != (buttons2 & 0x80)) { result |= GamepadButtons.RightGrip; }  // right back paddle
        }

        return result;
    }
    // The status byte: the low nibble is the charge in tenths, the high nibble the charger state.
    private static GamepadPower DecodePower(byte status) {
        var percent = ((byte)Math.Min(
            val1: (((status & 0x0F) * 10) + 5),
            val2: 100
        ));

        return (status >> 4) switch {
            0x0 => new GamepadPower(
                Percent: percent,
                State: GamepadPowerState.OnBattery
            ),
            0x1 => new GamepadPower(
                Percent: percent,
                State: GamepadPowerState.Charging
            ),
            0x2 => new GamepadPower(
                Percent: 100,
                State: GamepadPowerState.Charged
            ),
            // 0xA and 0xB report a charger that is not charging (voltage or temperature out of range).
            0xA or 0xB => new GamepadPower(
                Percent: percent,
                State: GamepadPowerState.OnBattery
            ),
            _ => GamepadPower.Unknown,
        };
    }
    private async ValueTask LoadCalibrationAsync(CancellationToken cancellationToken) {
        for (var attempt = 0; (attempt < CalibrationReadAttempts); ++attempt) {
            if (TryLoadCalibration()) {
                return;
            }

            await Task.Delay(
                cancellationToken: cancellationToken,
                millisecondsDelay: CalibrationRetryDelayMilliseconds
            );
        }

        Console.Error.WriteLine(value: $"[gamepad] DualSense IMU calibration unavailable after {CalibrationReadAttempts} attempts; using nominal scale");
    }
    private void LoadFirmwareVersion() {
        Span<byte> report = stackalloc byte[Math.Clamp(
            max: 128,
            min: FirmwareFeatureReportLength,
            value: m_device.FeatureReportByteLength
        )];

        report[0] = FirmwareFeatureReportId;

        // An unreadable version is treated as current firmware, as SDL does over Bluetooth.
        var version = (m_device.TryGetFeatureReport(buffer: report)
            ? BinaryPrimitives.ReadUInt16LittleEndian(source: report[FirmwareUpdateVersionOffset..])
            : ((ushort)0)
        );

        m_improvedRumble = (
            m_isEdge ||
            (0 == version) ||
            (version >= ImprovedRumbleFirmwareVersion)
        );
    }
    private static short ReadInt16(ReadOnlySpan<byte> report, int offset) {
        return BinaryPrimitives.ReadInt16LittleEndian(source: report[offset..]);
    }
    private static Vector2 ReadStick(byte rawX, byte rawY) {
        // 0..255 axes centered at 128, with Y growing downward; flip Y so up is +1, then apply a radial deadzone.
        var stick = new Vector2(
            x: ((rawX - StickCenter) / StickRange),
            y: (-(rawY - StickCenter) / StickRange)
        );

        return GamepadNormalization.ApplyRadialDeadzone(
            deadzone: StickDeadzone,
            stick: stick
        );
    }
    private static GamepadTouchPoint ReadTouch(ReadOnlySpan<byte> report, int offset) {
        // contact: bit 7 set means no finger; the low 7 bits are the incrementing contact id. The 12-bit X/Y are
        // packed across the next three bytes: x = byte1 | (byte2 low nibble << 8); y = (byte2 high nibble) | (byte3 << 4).
        var contact = report[offset];
        var x = report[(offset + 1)] | ((report[(offset + 2)] & 0x0F) << 8);
        var y = (report[(offset + 2)] >> 4) | (report[(offset + 3)] << 4);

        return new GamepadTouchPoint(
            Id: ((byte)(contact & 0x7F)),
            IsActive: (0 == (contact & 0x80)),
            Position: new Vector2(
                x: Math.Min(
                    val1: (x / TouchpadMaximumX),
                    val2: 1f
                ),
                y: Math.Min(
                    val1: (y / TouchpadMaximumY),
                    val2: 1f
                )
            )
        );
    }
    // Converts the free-running sensor counter into a seconds delta since the previous report; the unsigned
    // subtraction carries the delta across a 32-bit wrap, and the tracker clamps a first or implausible sample.
    private float SensorDeltaSeconds(uint sensorTimestamp) {
        var delta = (m_hasSensorTimestamp
            ? unchecked((sensorTimestamp - m_lastSensorTimestamp))
            : 0u
        );

        m_hasSensorTimestamp = true;
        m_lastSensorTimestamp = sensorTimestamp;

        return (delta * SensorTimestampSecondsPerUnit);
    }
    // Reads feature report 0x05, the factory IMU calibration (also what switches Bluetooth to the full report).
    // Gyro: scale = (speed+ + speed-) / (plus - minus) in deg/s per LSB about the factory bias. Accelerometer:
    // the plus/minus readings span 2 g, so bias = plus - span/2 and scale = 2 g / span. A calibration with a bias
    // past 1024 LSB or a scale more than half away from nominal is rejected whole, as SDL does.
    private bool TryLoadCalibration() {
        Span<byte> report = stackalloc byte[Math.Clamp(
            max: 128,
            min: CalibrationFeatureReportLength,
            value: m_device.FeatureReportByteLength
        )];

        report[0] = CalibrationFeatureReportId;

        if (!m_device.TryGetFeatureReport(buffer: report)) {
            return false;
        }

        var gyroBias = new Vector3(
            x: ReadInt16(
                offset: 1,
                report: report
            ),
            y: ReadInt16(
                offset: 3,
                report: report
            ),
            z: ReadInt16(
                offset: 5,
                report: report
            )
        );
        var gyroSpan = new Vector3(
            x: (ReadInt16(
                offset: 7,
                report: report
            ) - ReadInt16(
                offset: 9,
                report: report
            )),
            y: (ReadInt16(
                offset: 11,
                report: report
            ) - ReadInt16(
                offset: 13,
                report: report
            )),
            z: (ReadInt16(
                offset: 15,
                report: report
            ) - ReadInt16(
                offset: 17,
                report: report
            ))
        );
        var speed = (ReadInt16(
            offset: 19,
            report: report
        ) + ReadInt16(
            offset: 21,
            report: report
        ));
        var accelerometerPlus = new Vector3(
            x: ReadInt16(
                offset: 23,
                report: report
            ),
            y: ReadInt16(
                offset: 27,
                report: report
            ),
            z: ReadInt16(
                offset: 31,
                report: report
            )
        );
        var accelerometerSpan = (accelerometerPlus - new Vector3(
            x: ReadInt16(
                offset: 25,
                report: report
            ),
            y: ReadInt16(
                offset: 29,
                report: report
            ),
            z: ReadInt16(
                offset: 33,
                report: report
            )
        ));

        if (
            (0f == gyroSpan.X) || (0f == gyroSpan.Y) || (0f == gyroSpan.Z) ||
            (0f == accelerometerSpan.X) || (0f == accelerometerSpan.Y) || (0f == accelerometerSpan.Z)
        ) {
            return false;
        }

        var gyroScale = (new Vector3(value: (speed * (MathF.PI / 180f))) / gyroSpan);
        var accelerometerBias = (accelerometerPlus - (accelerometerSpan / 2f));
        var accelerometerScale = (new Vector3(value: 2f) / accelerometerSpan);

        if (
            !IsPlausible(
            bias: gyroBias,
            nominal: GyroRadiansPerSecondPerLsbNominal,
            scale: gyroScale
        ) ||
            !IsPlausible(
            bias: accelerometerBias,
            nominal: AccelerometerGPerLsb,
            scale: accelerometerScale
        )
        ) {
            // A readable but implausible calibration is a property of this pad, so retrying cannot fix it.
            Console.Error.WriteLine(value: "[gamepad] DualSense IMU calibration implausible; using nominal scale");

            return true;
        }

        m_accelerometerBias = accelerometerBias;
        m_accelerometerScale = accelerometerScale;
        m_gyroBias = gyroBias;
        m_gyroScale = gyroScale;

        return true;

        static bool IsPlausible(Vector3 bias, Vector3 scale, float nominal) {
            var maximum = Vector3.Max(
                value1: Vector3.Abs(value: bias),
                value2: Vector3.Zero
            );
            var error = Vector3.Abs(value: (Vector3.One - (scale / nominal)));

            return (
                (maximum.X <= MaximumCalibrationBias) && (maximum.Y <= MaximumCalibrationBias) && (maximum.Z <= MaximumCalibrationBias) &&
                (error.X <= MaximumCalibrationScaleError) && (error.Y <= MaximumCalibrationScaleError) && (error.Z <= MaximumCalibrationScaleError)
            );
        }
    }
    private bool TryParseFull(ReadOnlySpan<byte> report, int dataStart, out GamepadState state) {
        var block = report[dataStart..];
        var buttons = DecodeButtons(buttons: block[ButtonsOffset..]);

        // Each press of the mute button toggles the microphone mute and its LED; the firmware does neither.
        var muteHeld = (0 != (buttons & GamepadButtons.Mute));

        if (
            muteHeld &&
            !m_muteHeld
        ) {
            m_micMuted = !m_micMuted;
            m_outputStale = true;
        }

        m_muteHeld = muteHeld;

        var gyro = ((GamepadNormalization.ReadVector3Int16(
            offset: 15,
            scale: 1f,
            source: block
        ) - m_gyroBias) * m_gyroScale);
        var accelerometer = ((GamepadNormalization.ReadVector3Int16(
            offset: AccelerometerOffset,
            scale: 1f,
            source: block
        ) - m_accelerometerBias) * m_accelerometerScale);
        var sensorTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(source: block[SensorTimestampOffset..]);

        if (
            m_lightbarReassertPending &&
            (sensorTimestamp >= LightbarAnimationEndTimestamp)
        ) {
            m_lightbarReassertPending = false;
            m_outputStale = true;
        }

        state = new GamepadState(
            Accelerometer: accelerometer,
            Buttons: buttons,
            Gyro: gyro,
            LeftStick: ReadStick(
                rawX: block[0],
                rawY: block[1]
            ),
            LeftTrigger: GamepadNormalization.NormalizeTrigger(
                raw: block[LeftTriggerOffset],
                threshold: TriggerThreshold,
                range: TriggerRange
            ),
            Orientation: m_tracker.Update(
                accelerometerG: accelerometer,
                deltaSeconds: SensorDeltaSeconds(sensorTimestamp: sensorTimestamp),
                gyroRadiansPerSecond: gyro
            ),
            Power: DecodePower(status: block[StatusOffset]),
            RightStick: ReadStick(
                rawX: block[2],
                rawY: block[3]
            ),
            RightTrigger: GamepadNormalization.NormalizeTrigger(
                raw: block[RightTriggerOffset],
                threshold: TriggerThreshold,
                range: TriggerRange
            ),
            SensorTimestamp: sensorTimestamp,
            Touch0: ReadTouch(
                offset: Touch0Offset,
                report: block
            ),
            Touch1: ReadTouch(
                offset: Touch1Offset,
                report: block
            )
        );

        return true;
    }
    // The Bluetooth simple report a pad sends before feature report 0x05 switches it to the full one.
    private bool TryParseSimple(ReadOnlySpan<byte> report, out GamepadState state) {
        var block = report[1..];

        state = GamepadState.Neutral with {
            Buttons = DecodeButtons(buttons: block[SimpleButtonsOffset..]),
            LeftStick = ReadStick(
                rawX: block[0],
                rawY: block[1]
            ),
            LeftTrigger = GamepadNormalization.NormalizeTrigger(
                raw: block[SimpleLeftTriggerOffset],
                threshold: TriggerThreshold,
                range: TriggerRange
            ),
            RightStick = ReadStick(
                rawX: block[2],
                rawY: block[3]
            ),
            RightTrigger = GamepadNormalization.NormalizeTrigger(
                raw: block[SimpleRightTriggerOffset],
                threshold: TriggerThreshold,
                range: TriggerRange
            ),
        };

        return true;
    }
    // Writes the output report from the persistent motor, trigger, light-bar, player-LED and mute state. The
    // firmware applies only the sections whose valid-flag bits are set, so emitting every section from its
    // current state keeps any one update from clearing the others. releaseLeds instead sends the reset that ends
    // the connection light-bar animation (both reset mechanisms: Linux's light-out setup and SDL's release flag).
    private ValueTask WriteOutputAsync(bool releaseLeds, CancellationToken cancellationToken) {
        var buffer = m_outputBuffer;
        var common = (m_isBluetooth
            ? 3
            : 1
        );

        buffer.AsSpan().Clear();

        if (m_isBluetooth) {
            buffer[0] = BluetoothOutputReportId;
            buffer[1] = ((byte)((m_outputSequence++ & 0x0F) << 4));    // rolling sequence tag (upper nibble)
            buffer[2] = BluetoothOutputTag;
        } else {
            buffer[0] = UsbOutputReportId;
        }

        if (releaseLeds) {
            buffer[(common + 1)] = Flag1ReleaseLeds;
            buffer[(common + OutputValidFlag2Offset)] = Flag2LightbarSetup;
            buffer[(common + OutputLightbarSetupOffset)] = LightbarSetupLightOut;
        } else {
            buffer[common] = (Flag0HapticsSelect | Flag0RightTriggerFeedback | Flag0LeftTriggerFeedback);
            buffer[(common + 1)] = (Flag1LightbarControl | Flag1PlayerIndicatorControl | Flag1MicMuteLedControl | Flag1PowerSaveControl);

            if (m_improvedRumble) {
                buffer[(common + OutputValidFlag2Offset)] = Flag2CompatibleVibration2;
                buffer[(common + OutputMotorRightOffset)] = m_motorHigh;
                buffer[(common + OutputMotorLeftOffset)] = m_motorLow;
            } else {
                buffer[common] |= Flag0CompatibleVibration;
                buffer[(common + OutputMotorRightOffset)] = ((byte)(m_motorHigh >> 1));
                buffer[(common + OutputMotorLeftOffset)] = ((byte)(m_motorLow >> 1));
            }

            buffer[(common + OutputMicMuteLedOffset)] = (m_micMuted
                ? MicMuteLedOn
                : ((byte)0)
            );
            buffer[(common + OutputPowerSaveOffset)] = (m_micMuted
                ? PowerSaveMicMute
                : ((byte)0)
            );
            DualSenseAdaptiveTrigger.WriteTriggerBlock(
                block: buffer.AsSpan(
                    length: DualSenseAdaptiveTrigger.TriggerBlockLength,
                    start: (common + OutputRightTriggerOffset)
                ),
                spec: in m_rightTriggerEffect
            );
            DualSenseAdaptiveTrigger.WriteTriggerBlock(
                block: buffer.AsSpan(
                    length: DualSenseAdaptiveTrigger.TriggerBlockLength,
                    start: (common + OutputLeftTriggerOffset)
                ),
                spec: in m_leftTriggerEffect
            );
            buffer[(common + OutputPlayerLedsOffset)] = m_playerLeds;
            buffer[(common + OutputLightbarRedOffset)] = m_lightbar.Red;
            buffer[((common + OutputLightbarRedOffset) + 1)] = m_lightbar.Green;
            buffer[((common + OutputLightbarRedOffset) + 2)] = m_lightbar.Blue;
        }

        if (m_isBluetooth) {
            // The CRC covers the logical 78-byte report bar its last four bytes, not the padded write.
            var crcOffset = (BluetoothOutputReportLength - 4);

            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: buffer.AsSpan(
                    length: 4,
                    start: crcOffset
                ),
                value: ComputeCrc32(
                    data: buffer.AsSpan(
                        length: crcOffset,
                        start: 0
                    ),
                    seed: OutputCrcSeed
                )
            );
        }

        return m_device.WriteAsync(
            buffer: buffer,
            cancellationToken: cancellationToken
        );
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default) {
        m_lightbar = GamepadPlayerColors.ForPlayer(playerIndex: playerIndex);
        m_playerLeds = ((playerIndex >= 0)
            ? ((byte)(PlayerLeds[(playerIndex % PlayerLeds.Length)] | PlayerLedsInstant))
            : ((byte)0)
        );
        m_lightbarReassertPending = m_isBluetooth;

        await LoadCalibrationAsync(cancellationToken: cancellationToken);
        LoadFirmwareVersion();
        await WriteOutputAsync(
            cancellationToken: cancellationToken,
            releaseLeds: true
        );
        await WriteOutputAsync(
            cancellationToken: cancellationToken,
            releaseLeds: false
        );
    }
    /// <inheritdoc />
    public ValueTask ServiceAsync(long timestamp, long timestampFrequency, CancellationToken cancellationToken) {
        if (!m_outputStale) {
            return ValueTask.CompletedTask;
        }

        m_outputStale = false;

        return WriteOutputAsync(
            cancellationToken: cancellationToken,
            releaseLeds: false
        );
    }
    /// <inheritdoc />
    public ValueTask SetLedAsync(LedColor color, CancellationToken cancellationToken = default) {
        m_lightbar = color;

        return WriteOutputAsync(
            cancellationToken: cancellationToken,
            releaseLeds: false
        );
    }
    /// <inheritdoc />
    public ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default) {
        m_motorHigh = ((byte)(highFrequency * 255f));
        m_motorLow = ((byte)(lowFrequency * 255f));

        return WriteOutputAsync(
            cancellationToken: cancellationToken,
            releaseLeds: false
        );
    }
    /// <inheritdoc />
    public ValueTask SetTriggerEffectAsync(TriggerEffectSpec left, TriggerEffectSpec right, CancellationToken cancellationToken = default) {
        m_leftTriggerEffect = left;
        m_rightTriggerEffect = right;

        return WriteOutputAsync(
            cancellationToken: cancellationToken,
            releaseLeds: false
        );
    }
    /// <inheritdoc />
    /// <remarks>USB accepts only the 64-byte full report <c>0x01</c>. Bluetooth accepts the full report
    /// <c>0x31</c> whose CRC checks, and the simple report <c>0x01</c> (no motion, touch or battery) a pad sends
    /// before it switches.</remarks>
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        state = GamepadState.Neutral;

        if (report.IsEmpty) {
            return false;
        }

        if (!m_isBluetooth) {
            return (
                (UsbInputReportId == report[0]) &&
                (report.Length >= UsbFullReportLength) &&
                TryParseFull(
                    dataStart: 1,
                    report: report,
                    state: out state
                )
            );
        }

        if (BluetoothInputReportId == report[0]) {
            return (
                (report.Length >= BluetoothFullReportLength) &&
                (ComputeCrc32(
                    data: report[..(BluetoothFullReportLength - 4)],
                    seed: BluetoothInputCrcSeed
                ) == BinaryPrimitives.ReadUInt32LittleEndian(source: report[(BluetoothFullReportLength - 4)..])) &&
                TryParseFull(
                    dataStart: 2,
                    report: report,
                    state: out state
                )
            );
        }

        return (
            (UsbInputReportId == report[0]) &&
            (report.Length >= SimpleReportLength) &&
            TryParseSimple(
                report: report,
                state: out state
            )
        );
    }
}
