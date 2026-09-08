using System.Buffers.Binary;
using System.Numerics;
using Puck.Input.Hid;

namespace Puck.Input.Devices;

internal sealed class NintendoSwitchController : IGamepadParser, IRumbleParser {
    // The accelerometer reports ≈ 0.000244 g per LSB (≈ ±8g full scale). Nominal/uncalibrated.
    private const float AccelerometerGPerLsb = 0.000244f;
    private const int ButtonsLeftOffset = 5;    // Down, Up, Right, Left, SR, SL, L, ZL
    private const int ButtonsRightOffset = 3;   // Y, X, B, A, SR, SL, R, ZR
    private const int ButtonsSharedOffset = 4;   // -, +, RStick, LStick, Home, Capture
    private const byte DisableUsbTimeoutCommand = 0x04;
    private const byte EnableFastBaudRateCommand = 0x03;   // 0x80 0x03: switch to the 3 Mbit baud rate
    // Factory stick calibration lives in SPI flash: 9 bytes per stick, three 12-bit-packed (x, y) triples for the
    // center, the below-center span, and the above-center span. The LEFT and RIGHT sticks pack those three triples
    // in DIFFERENT orders (LEFT: above, center, below — RIGHT: center, below, above; see TryDecodeStickCalibration).
    // User calibration, when present (magic 0xB2 0xA1), OVERRIDES the factory values.
    private const uint FactoryLeftStickCalibrationAddress = 0x603DU;
    private const uint FactoryRightStickCalibrationAddress = 0x6046U;
    // The IMU gyro reports ~0.070 deg/s per LSB (≈ ±2294 dps full scale), then degrees → radians.
    // Nominal/uncalibrated — per-device calibration would refine it.
    private const float GyroRadiansPerSecondPerLsb = (0.070f * (MathF.PI / 180f));
    private const byte HandshakeCommand = 0x02;            // 0x80 0x02: USB handshake
    private const int ImuOffset = 13;   // three 12-byte samples (accel xyz, gyro xyz; int16 LE)
    private const int ImuSampleCount = 3;
    private const float ImuSampleSeconds = 0.005f; // each of the three IMU sub-samples spans a fixed 5 ms
    private const int ImuSampleSize = 12;
    private const byte InertialMeasurementUnitCommand = 0x40;
    private const byte InitCommandInputReportId = 0x81;    // device -> host: reply to a 0x80 init command
    private const byte InitCommandOutputReportId = 0x80;   // host -> device: USB init command
    private const int LeftStickOffset = 6;    // 3 bytes, two 12-bit axes
    private const byte PlayerLightsCommand = 0x30;
    private const int ReportSizeInBytes = 64;
    private const byte RequestStatusCommand = 0x01;        // 0x80 0x01: request controller status (MAC + type), NOT "enable UART"
    private const int RightStickOffset = 9;    // 3 bytes, two 12-bit axes
    private const byte RumbleOutputReportId = 0x10;
    private const byte RumbleSubcommandOutputReportId = 0x01; // host -> device: rumble + optional subcommand
    private const byte SetInputReportModeCommand = 0x03;   // subcommand 0x03: select the input report mode (arg 0x30 = standard full)
    private const byte SpiFlashReadCommand = 0x10;         // subcommand 0x10: read SPI flash (payload: u32 LE address + u8 length)
    private const int SpiReadAttempts = 5;              // resend the read request this many times before giving up
    // A 0x21 subcommand reply to an SPI read echoes: [15..18] address (u32 LE), [19] length, [20..] the flash data.
    private const int SpiReplyAddressOffset = 15;
    private const int SpiReplyDataOffset = 20;
    private const int SpiReplyLengthOffset = 19;
    private const byte StandardFullModeArgument = 0x30;    // arg to subcommand 0x03
    // Standard full input report layout (report id 0x30); byte offsets per the Switch Pro report format.
    private const byte StandardInputReportId = 0x30;
    private const int StickCalibrationDataLength = 9;
    private const int StickCalibrationMaxCenter = 3583;
    private const int StickCalibrationMaxSpan = 2400;
    // Plausibility bounds that reject unprogrammed/garbage flash (all-0x000 or all-0xFFF, absurd spans) so a bad
    // read degrades to the nominal scale rather than poisoning an axis. A 12-bit axis is 0..4095, nominal center ~2048.
    private const int StickCalibrationMinCenter = 512;
    private const int StickCalibrationMinSpan = 128;
    private const int StickCenter = 2048;  // nominal center of a 12-bit axis before calibration
    private const float StickDeadzone = 0.12f;
    private const float StickRange = 1800f; // nominal half-travel; clamped to ±1 after scaling
    private const byte SubcommandReplyAckOffset = 14;         // echoed subcommand id within a 0x21 reply
    private const byte SubcommandReplyInputReportId = 0x21;   // device -> host: subcommand acknowledgement/reply
    private const byte UserCalibrationMagic0 = 0xB2;
    private const byte UserCalibrationMagic1 = 0xA1;
    private const uint UserLeftStickCalibrationAddress = 0x8010U;   // 2 magic bytes at 0x8010, then 9 data bytes at 0x8012
    private const uint UserRightStickCalibrationAddress = 0x801BU;  // 2 magic bytes at 0x801B, then 9 data bytes at 0x801D

    private readonly IHidDevice m_device;
    private readonly byte[] m_requestBuffer;
    private readonly byte[] m_responseBuffer;
    private readonly byte[] m_rumbleBuffer;
    private readonly ImuOrientationTracker m_tracker = new();

    private StickCalibration m_leftStickCalibration;   // default: IsValid = false → the nominal scale is used
    private byte m_packetId;
    private StickCalibration m_rightStickCalibration;
    private byte m_rumblePacketId;
    private RumbleThrottle m_rumbleThrottle;

    private static ReadOnlySpan<byte> DefaultRumblePacket => [0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40,];

    /// <inheritdoc />
    // The Pro Controller has a 6-axis IMU; its ZL/ZR shoulder triggers are digital, not analog.
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.Gyro;
    /// <inheritdoc />
    public GamepadType Type => GamepadType.SwitchPro;

    public NintendoSwitchController(IHidDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        // Every buffer is floored to the documented report size: composition and response parsing index at
        // protocol offsets, so a smaller declared length must never shrink the allocation. A nonconforming
        // declared length makes the oversized write a stack-rejected no-op instead of a buffer overrun.
        var inputLength = device.InputReportByteLength;
        var outputLength = device.OutputReportByteLength;

        m_device = device;
        m_packetId = byte.MinValue;
        m_requestBuffer = new byte[Math.Max(
            val1: outputLength,
            val2: ReportSizeInBytes
        )];
        m_responseBuffer = new byte[Math.Max(
            val1: inputLength,
            val2: ReportSizeInBytes
        )];
        m_rumbleBuffer = new byte[Math.Max(
            val1: outputLength,
            val2: ReportSizeInBytes
        )];
    }

    /// <inheritdoc />
    ValueTask IGamepadParser.InitializeAsync(int playerIndex, CancellationToken cancellationToken) {
        return InitializeAsync(
            playerLedState: PlayerLedPattern(playerIndex: playerIndex),
            cancellationToken: cancellationToken
        );
    }

    private static void EncodeRumbleSide(Span<byte> destination, float lowFrequency, float highFrequency) {
        var high = Math.Clamp(
            max: 1f,
            min: 0f,
            value: highFrequency
        );
        var low = Math.Clamp(
            max: 1f,
            min: 0f,
            value: lowFrequency
        );

        if (
            (0f >= high) &&
            (0f >= low)
        ) {
            // Neutral (no rumble) packet.
            destination[0] = 0x00;
            destination[1] = 0x01;
            destination[2] = 0x40;
            destination[3] = 0x40;

            return;
        }

        // Default carrier frequencies (encoded), with amplitude scaled from the requested intensities.
        // Layout per the Switch HD-rumble format: high-band freq/amp in [0..1], low-band freq/amp in [2..3].
        // Amplitude bytes must stay within the safe ceilings — HA <= 0xC8, LA <= 0x72; higher values are
        // unsafe for the linear resonant actuators. High band peaks at 0x9C
        // (< 0xC8); the low band maps 0..1 onto baseline 0x40 .. ceiling 0x72 so full intensity stays safe.
        const ushort HighFrequencyEncoded = 0x0098;             // ~320 Hz
        const byte LowFrequencyEncoded = 0x46;                  // ~160 Hz
        const float LowAmplitudeSpan = (0x72 - 0x40);           // 0x40 baseline (rest) up to the 0x72 safe ceiling

        var highAmplitude = ((byte)(high * 0x9C));               // high-band amplitude byte (peak 0x9C < 0xC8)
        var lowAmplitude = ((ushort)(0x40 + (low * LowAmplitudeSpan))); // low-band amplitude (0x40 rest .. 0x72 max)

        destination[0] = ((byte)(HighFrequencyEncoded & 0xFF));
        destination[1] = ((byte)(highAmplitude + ((HighFrequencyEncoded >> 8) & 0xFF)));
        destination[2] = ((byte)(LowFrequencyEncoded + ((lowAmplitude >> 8) & 0xFF)));
        destination[3] = ((byte)(lowAmplitude & 0xFF));
    }
    private async ValueTask<StickCalibration> LoadOneStickCalibrationAsync(
        uint factoryAddress,
        uint userAddress,
        bool isLeftStick,
        string stickName,
        CancellationToken cancellationToken
    ) {
        // Read the 2 magic bytes + 9 data bytes at the user-calibration address; a valid magic means the user
        // calibration is present and OVERRIDES the factory calibration.
        var userBuffer = new byte[(StickCalibrationDataLength + 2)];

        if (
            await TryReadSpiFlash(
            address: userAddress,
            cancellationToken: cancellationToken,
            destination: userBuffer,
            length: (StickCalibrationDataLength + 2)
        ) &&
            (UserCalibrationMagic0 == userBuffer[0]) &&
            (UserCalibrationMagic1 == userBuffer[1]) &&
            TryDecodeStickCalibration(
            data: userBuffer.AsSpan(
                length: StickCalibrationDataLength,
                start: 2
            ),
            isLeftStick: isLeftStick,
            calibration: out var userCalibration
        )
        ) {
            LogCalibration(
                calibration: in userCalibration,
                source: "user",
                stickName: stickName
            );

            return userCalibration;
        }

        // Factory calibration (9 data bytes).
        var factoryBuffer = new byte[StickCalibrationDataLength];

        if (
            await TryReadSpiFlash(
            address: factoryAddress,
            cancellationToken: cancellationToken,
            destination: factoryBuffer,
            length: StickCalibrationDataLength
        ) &&
            TryDecodeStickCalibration(
            calibration: out var factoryCalibration,
            data: factoryBuffer,
            isLeftStick: isLeftStick
        )
        ) {
            LogCalibration(
                calibration: in factoryCalibration,
                source: "factory",
                stickName: stickName
            );

            return factoryCalibration;
        }

        // A pad must never get WORSE because calibration failed: keep the nominal scale and say so exactly once.
        Console.Error.WriteLine(value: $"[gamepad] Switch Pro {stickName} stick calibration unavailable (SPI read failed or implausible); using nominal scale");

        return default;
    }
    // Loads both sticks' calibration from SPI flash. Each stick prefers its user calibration (magic 0xB2 0xA1
    // present) over the factory calibration, and falls back to the nominal scale on any failure.
    private async ValueTask LoadStickCalibrationAsync(CancellationToken cancellationToken) {
        m_leftStickCalibration = await LoadOneStickCalibrationAsync(
            cancellationToken: cancellationToken,
            factoryAddress: FactoryLeftStickCalibrationAddress,
            isLeftStick: true,
            stickName: "left",
            userAddress: UserLeftStickCalibrationAddress
        );
        m_rightStickCalibration = await LoadOneStickCalibrationAsync(
            cancellationToken: cancellationToken,
            factoryAddress: FactoryRightStickCalibrationAddress,
            isLeftStick: false,
            stickName: "right",
            userAddress: UserRightStickCalibrationAddress
        );
    }
    private static void LogCalibration(string source, string stickName, in StickCalibration calibration) {
        Console.Error.WriteLine(value: $"[gamepad] Switch Pro {stickName} stick calibration source={source} center=({calibration.CenterX:F0},{calibration.CenterY:F0}) x[-{calibration.MinBelowX:F0},+{calibration.MaxAboveX:F0}] y[-{calibration.MinBelowY:F0},+{calibration.MaxAboveY:F0}]");
    }
    private static byte PlayerLedPattern(int playerIndex) {
        // Switch convention: player N lights N solid LEDs (P1 = ●○○○, P2 = ●●○○, …). Bits 0-3 are the four
        // solid LEDs; cap at four slots and wrap beyond.
        var slot = playerIndex & 3;

        return ((byte)((1 << (slot + 1)) - 1));
    }
    // Decodes the three 5 ms IMU sub-samples this report carries in one pass (each leads with accel xyz, then
    // gyro xyz; int16 LE). The averaged outputs are the frame-rate-independent readings a consumer gets; the
    // fused orientation advances one tracker step per sub-sample at the sensor's own fixed cadence — never a
    // wall clock — so fusion stays correct regardless of how fast reports arrive. (Bias learning therefore runs
    // once per sub-sample, harmless since it is a slow still-only adaptation.) Sub-samples are read raw (scale
    // one) so the average is taken before the nominal scale is applied.
    private Quaternion ReadImu(ReadOnlySpan<byte> report, out Vector3 accelerometer, out Vector3 gyro) {
        var accelerometerSum = Vector3.Zero;
        var gyroSum = Vector3.Zero;

        for (var sample = 0; (sample < ImuSampleCount); ++sample) {
            var baseOffset = (ImuOffset + (sample * ImuSampleSize));
            var accelerometerSample = GamepadNormalization.ReadVector3Int16(
                offset: baseOffset,
                scale: 1f,
                source: report
            );
            var gyroSample = GamepadNormalization.ReadVector3Int16(
                offset: (baseOffset + 6),
                scale: 1f,
                source: report
            );

            accelerometerSum += accelerometerSample;
            gyroSum += gyroSample;

            _ = m_tracker.Update(
                accelerometerG: ToFusionFrame(sensor: (accelerometerSample * AccelerometerGPerLsb)),
                deltaSeconds: ImuSampleSeconds,
                gyroRadiansPerSecond: ToFusionFrame(sensor: (gyroSample * GyroRadiansPerSecondPerLsb))
            );
        }

        accelerometer = ((accelerometerSum / ImuSampleCount) * AccelerometerGPerLsb);
        gyro = ((gyroSum / ImuSampleCount) * GyroRadiansPerSecondPerLsb);

        return m_tracker.Orientation;
    }
    private static Vector2 ReadStick(ReadOnlySpan<byte> report, int offset, in StickCalibration calibration) {
        var rawX = report[offset] | ((report[(offset + 1)] & 0x0F) << 8);
        var rawY = (report[(offset + 1)] >> 4) | (report[(offset + 2)] << 4);
        float x, y;

        if (calibration.IsValid) {
            // Per-axis normalization from the pad's own calibration: (raw - center) / span, where span is the
            // above-center travel for a positive push and the below-center travel for a negative one. Because the
            // span comes from the pad itself, a full physical deflection maps to |axis| ≈ 1 even on an asymmetric
            // stick — the fix for a pad that could not reach ±1 under the single nominal scale.
            var deltaX = (rawX - calibration.CenterX);
            var deltaY = (rawY - calibration.CenterY);

            x = (deltaX / ((deltaX >= 0f)
                ? calibration.MaxAboveX
                : calibration.MinBelowX));
            y = (deltaY / ((deltaY >= 0f)
                ? calibration.MaxAboveY
                : calibration.MinBelowY));
        } else {
            // Nominal fallback (no valid calibration was read) — the original single-scale path, unchanged.
            x = ((rawX - StickCenter) / StickRange);
            y = ((rawY - StickCenter) / StickRange);
        }

        // Clamp each axis into the unit range (a calibrated axis can nudge just past 1 at its extreme point) before
        // the radial deadzone/rescale.
        var stick = new Vector2(
            x: Math.Clamp(
                max: 1f,
                min: -1f,
                value: x
            ),
            y: Math.Clamp(
                max: 1f,
                min: -1f,
                value: y
            )
        );

        return GamepadNormalization.ApplyRadialDeadzone(
            deadzone: StickDeadzone,
            stick: stick
        );
    }
    private async ValueTask<bool> SendInitCommand(
        byte command,
        bool waitForAck = true,
        CancellationToken cancellationToken = default
    ) {
        const uint MaximumNumberOfAttempts = 64U;
        const uint WriteAttemptInterval = 7U;

        var device = m_device;
        var requestBuffer = m_requestBuffer;
        var response = m_responseBuffer;
        var result = false;

        requestBuffer[0] = InitCommandOutputReportId;
        requestBuffer[1] = command;

        response.AsSpan().Clear();

        for (var attempts = uint.MinValue; (MaximumNumberOfAttempts > attempts); ++attempts) {
            if (uint.MinValue == (attempts % WriteAttemptInterval)) {
                await device.WriteAsync(
                    buffer: requestBuffer,
                    cancellationToken: cancellationToken
                );
                await Task.Delay(
                    cancellationToken: cancellationToken,
                    millisecondsDelay: 12
                );

                if (!waitForAck) {
                    result = true;

                    break;
                }
            }

            var numberOfBytesRead = await device.ReadAsync(
                buffer: response,
                cancellationToken: cancellationToken,
                timeoutInMilliseconds: 60
            );

            if (
                (1 < numberOfBytesRead) &&
                (InitCommandInputReportId == response[0]) &&
                (command == response[1])
            ) {
                result = true;

                break;
            }
        }

        requestBuffer.AsSpan().Clear();

        // A transient ACK miss (common under USB contention with several controllers initializing at once) is
        // tolerated rather than fatal: the caller's retries cover it, and a genuine failure degrades to a
        // controller that simply doesn't stream, not a crash.
        return result;
    }
    private async ValueTask<bool> SendSubcommand(
        byte command,
        CancellationToken cancellationToken = default
    ) {
        const uint MaximumNumberOfAttempts = 16U;

        var defaultRumblePacket = DefaultRumblePacket;
        var device = m_device;
        // The packet number is a 4-bit rolling counter; mask it like the rumble path so it never spills into the
        // high nibble (which is reserved for other fields in the rumble+subcommand report).
        var packetId = ((byte)(m_packetId++ & 0x0F));
        var requestBuffer = m_requestBuffer;
        var responseBuffer = m_responseBuffer;
        var result = false;

        requestBuffer[0] = RumbleSubcommandOutputReportId;
        requestBuffer[1] = packetId;
        requestBuffer[10] = command;

        defaultRumblePacket.CopyTo(destination: requestBuffer.AsSpan()[2..]);
        responseBuffer.AsSpan().Clear();

        await device.WriteAsync(
            buffer: requestBuffer,
            cancellationToken: cancellationToken
        );
        await Task.Delay(
            cancellationToken: cancellationToken,
            millisecondsDelay: 12
        );

        for (var attempts = uint.MinValue; (MaximumNumberOfAttempts > attempts); ++attempts) {
            var numberOfBytesRead = await device.ReadAsync(
                buffer: responseBuffer,
                cancellationToken: cancellationToken,
                timeoutInMilliseconds: 60
            );

            if (
                (numberOfBytesRead > SubcommandReplyAckOffset) &&
                (SubcommandReplyInputReportId == responseBuffer[0]) &&
                (command == responseBuffer[SubcommandReplyAckOffset])
            ) {
                result = true;

                break;
            }
        }

        requestBuffer.AsSpan().Clear();

        // A transient ACK miss (common under USB contention with several controllers initializing at once) is
        // tolerated rather than fatal: the caller's retries cover it, and a genuine failure degrades to a
        // controller that simply doesn't stream, not a crash.
        return result;
    }
    // Maps the Switch's (right-handed) IMU axes into the fusion frame (X=right, Y=up, Z=back).
    private static Vector3 ToFusionFrame(Vector3 sensor) {
        return new Vector3(
            x: -sensor.Y,
            y: sensor.Z,
            z: -sensor.X
        );
    }
    // Decodes a 9-byte SPI stick-calibration block into per-axis center + below-center/above-center spans. The nine
    // bytes hold six little-endian 12-bit values (three (x, y) triples); the LEFT and RIGHT sticks order those
    // triples DIFFERENTLY — LEFT: above, center, below; RIGHT: center, below, above. Returns false when the decoded
    // values are implausible so the caller keeps the nominal scale.
    private static bool TryDecodeStickCalibration(ReadOnlySpan<byte> data, bool isLeftStick, out StickCalibration calibration) {
        Span<int> values = stackalloc int[6];

        values[0] = data[0] | ((data[1] & 0x0F) << 8);
        values[1] = (data[1] >> 4) | (data[2] << 4);
        values[2] = data[3] | ((data[4] & 0x0F) << 8);
        values[3] = (data[4] >> 4) | (data[5] << 4);
        values[4] = data[6] | ((data[7] & 0x0F) << 8);
        values[5] = (data[7] >> 4) | (data[8] << 4);

        int centerX, centerY, maxAboveX, maxAboveY, minBelowX, minBelowY;

        if (isLeftStick) {
            maxAboveX = values[0]; maxAboveY = values[1];
            centerX = values[2]; centerY = values[3];
            minBelowX = values[4]; minBelowY = values[5];
        } else {
            centerX = values[0]; centerY = values[1];
            minBelowX = values[2]; minBelowY = values[3];
            maxAboveX = values[4]; maxAboveY = values[5];
        }

        calibration = new StickCalibration(
            centerX: centerX,
            centerY: centerY,
            maxAboveX: maxAboveX,
            maxAboveY: maxAboveY,
            minBelowX: minBelowX,
            minBelowY: minBelowY
        );

        return calibration.IsValid;
    }
    // Reads <paramref name="length"/> bytes of SPI flash at <paramref name="address"/> via subcommand 0x10, over the
    // rumble+subcommand output report (like SendSubcommand). Matches the reply by its echoed subcommand id AND the
    // exact address+length we requested, so an unrelated queued 0x21 reply can't be mistaken for ours. Returns false
    // (leaving the caller on the nominal scale) if no matching reply arrives after SpiReadAttempts resends.
    private async ValueTask<bool> TryReadSpiFlash(
        uint address,
        int length,
        byte[] destination,
        CancellationToken cancellationToken
    ) {
        const uint MaximumReplyReads = 16U;

        var device = m_device;
        var requestBuffer = m_requestBuffer;
        var responseBuffer = m_responseBuffer;
        var result = false;

        for (var attempt = 0; ((attempt < SpiReadAttempts) && !result); ++attempt) {
            // The packet number is a 4-bit rolling counter; mask it like the rumble/subcommand path so it never
            // spills into the high nibble.
            var packetId = ((byte)(m_packetId++ & 0x0F));

            requestBuffer.AsSpan().Clear();

            requestBuffer[0] = RumbleSubcommandOutputReportId;
            requestBuffer[1] = packetId;
            requestBuffer[10] = SpiFlashReadCommand;

            // The neutral rumble payload occupies [2..10) on the rumble+subcommand report (span used immediately,
            // not held across the await below).
            DefaultRumblePacket.CopyTo(destination: requestBuffer.AsSpan()[2..]);
            // SPI read arguments: little-endian u32 address followed by a u8 length.
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: requestBuffer.AsSpan(start: 11),
                value: address
            );
            requestBuffer[15] = ((byte)length);

            responseBuffer.AsSpan().Clear();

            await device.WriteAsync(
                buffer: requestBuffer,
                cancellationToken: cancellationToken
            );
            await Task.Delay(
                cancellationToken: cancellationToken,
                millisecondsDelay: 12
            );

            for (var read = uint.MinValue; (MaximumReplyReads > read); ++read) {
                var numberOfBytesRead = await device.ReadAsync(
                    buffer: responseBuffer,
                    cancellationToken: cancellationToken,
                    timeoutInMilliseconds: 60
                );

                if (
                    (numberOfBytesRead >= (SpiReplyDataOffset + length)) &&
                    (SubcommandReplyInputReportId == responseBuffer[0]) &&
                    (SpiFlashReadCommand == responseBuffer[SubcommandReplyAckOffset]) &&
                    (address == BinaryPrimitives.ReadUInt32LittleEndian(source: responseBuffer.AsSpan(start: SpiReplyAddressOffset))) &&
                    (length == responseBuffer[SpiReplyLengthOffset])
                ) {
                    responseBuffer.AsSpan(
                        length: length,
                        start: SpiReplyDataOffset
                    ).CopyTo(destination: destination.AsSpan(
                        length: length,
                        start: 0
                    ));

                    result = true;

                    break;
                }
            }
        }

        requestBuffer.AsSpan().Clear();

        return result;
    }

    public async ValueTask InitializeAsync(
        byte playerLedState,
        CancellationToken cancellationToken = default
    ) {
        // The rolling subcommand packet number starts at 0x00 (SendSubcommand masks it to 4 bits per send).
        m_packetId = byte.MinValue;

        // USB handshake: request status, handshake, raise the baud rate, handshake again, then force HID-only mode
        // (no ACK for the last one). This is the canonical Switch Pro USB bring-up sequence.
        _ = await SendInitCommand(
            cancellationToken: cancellationToken,
            command: RequestStatusCommand
        );
        _ = await SendInitCommand(
            cancellationToken: cancellationToken,
            command: HandshakeCommand
        );
        _ = await SendInitCommand(
            cancellationToken: cancellationToken,
            command: EnableFastBaudRateCommand
        );
        _ = await SendInitCommand(
            cancellationToken: cancellationToken,
            command: HandshakeCommand
        );
        _ = await SendInitCommand(
            cancellationToken: cancellationToken,
            command: DisableUsbTimeoutCommand,
            waitForAck: false
        );

        var requestBuffer = m_requestBuffer;

        // configure player lights
        requestBuffer[11] = playerLedState;
        _ = await SendSubcommand(
            cancellationToken: cancellationToken,
            command: PlayerLightsCommand
        );

        // enable integrated inertial measurement unit
        requestBuffer[11] = 0x01;
        _ = await SendSubcommand(
            cancellationToken: cancellationToken,
            command: InertialMeasurementUnitCommand
        );

        // Read the factory (or user, when present) stick calibration from SPI flash so each axis reaches ±1 at full
        // physical deflection — this corrects the per-device asymmetry the nominal scale cannot. Done before the pad
        // starts flooding 0x30 full reports so the 0x21 subcommand replies are easy to pick out. A failed/implausible
        // read leaves the nominal scale in place (see LoadStickCalibrationAsync), so a pad is never worse off.
        await LoadStickCalibrationAsync(cancellationToken: cancellationToken);

        // set standard input report mode (0x30 = full input report with IMU)
        requestBuffer[11] = StandardFullModeArgument;
        _ = await SendSubcommand(
            cancellationToken: cancellationToken,
            command: SetInputReportModeCommand
        );
    }
    /// <summary>
    /// Sends a rumble-only output report (<c>0x10</c>) with both motors driven from the given normalized
    /// intensities. The HD-rumble amplitude/frequency encoding is an approximation with fixed default
    /// frequencies: perceptible and safe.
    /// Must be called on the device's single I/O loop (it shares the device handle with reads).
    /// </summary>
    /// <remarks>
    /// Writes use a >=30 ms coalescing cadence so a per-tick streamer can't flood the link: writing faster can
    /// drop a Bluetooth controller off the link or make a USB controller miss the command. A stop (both bands at rest),
    /// the first write, and any write that <i>raises</i> intensity are never throttled, so rumble-off and stronger
    /// effects stay instant; only an equal-or-weaker update arriving inside the 30 ms window is dropped (a briefly
    /// stale weaker value is imperceptible). This keeps a per-tick rumble streamer from flooding the shared I/O loop.
    /// </remarks>
    /// <param name="lowFrequency">The low-band intensity, 0..1.</param>
    /// <param name="highFrequency">The high-band intensity, 0..1.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the report has been written (or immediately if throttled).</returns>
    public ValueTask SetRumbleAsync(
        float lowFrequency,
        float highFrequency,
        CancellationToken cancellationToken = default
    ) {
        if (!m_rumbleThrottle.TryPrepare(
            high: out var high,
            highFrequency: highFrequency,
            low: out var low,
            lowFrequency: lowFrequency
        )) {
            return ValueTask.CompletedTask;
        }

        var buffer = m_rumbleBuffer;

        buffer.AsSpan().Clear();

        buffer[0] = RumbleOutputReportId;
        buffer[1] = ((byte)(m_rumblePacketId++ & 0x0F));

        EncodeRumbleSide(
            destination: buffer.AsSpan(
                length: 4,
                start: 2
            ),
            lowFrequency: low,
            highFrequency: high
        );
        EncodeRumbleSide(
            destination: buffer.AsSpan(
                length: 4,
                start: 6
            ),
            lowFrequency: low,
            highFrequency: high
        );

        return m_device.WriteAsync(
            buffer: buffer,
            cancellationToken: cancellationToken
        );
    }
    /// <inheritdoc />
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        state = GamepadState.Neutral;

        if (
            (report.Length < (ImuOffset + (ImuSampleSize * ImuSampleCount))) ||
            (StandardInputReportId != report[0])
        ) {
            return false;
        }

        var buttonsRight = report[ButtonsRightOffset];
        var buttonsShared = report[ButtonsSharedOffset];
        var buttonsLeft = report[ButtonsLeftOffset];
        var buttons = GamepadButtons.None;

        // Face buttons are mapped by physical position to the South/East/West/North convention:
        // Switch B (bottom) → South, A (right) → East, Y (left) → West, X (top) → North.
        if (0 != (buttonsRight & 0x04)) { buttons |= GamepadButtons.ButtonSouth; }   // B
        if (0 != (buttonsRight & 0x08)) { buttons |= GamepadButtons.ButtonEast; }    // A
        if (0 != (buttonsRight & 0x01)) { buttons |= GamepadButtons.ButtonWest; }    // Y
        if (0 != (buttonsRight & 0x02)) { buttons |= GamepadButtons.ButtonNorth; }   // X
        if (0 != (buttonsRight & 0x40)) { buttons |= GamepadButtons.RightShoulder; } // R

        if (0 != (buttonsLeft & 0x02)) { buttons |= GamepadButtons.DpadUp; }
        if (0 != (buttonsLeft & 0x01)) { buttons |= GamepadButtons.DpadDown; }
        if (0 != (buttonsLeft & 0x08)) { buttons |= GamepadButtons.DpadLeft; }
        if (0 != (buttonsLeft & 0x04)) { buttons |= GamepadButtons.DpadRight; }
        if (0 != (buttonsLeft & 0x40)) { buttons |= GamepadButtons.LeftShoulder; }   // L

        if (0 != (buttonsShared & 0x01)) { buttons |= GamepadButtons.Back; }         // Minus
        if (0 != (buttonsShared & 0x02)) { buttons |= GamepadButtons.Start; }        // Plus
        if (0 != (buttonsShared & 0x04)) { buttons |= GamepadButtons.RightStickPress; }
        if (0 != (buttonsShared & 0x08)) { buttons |= GamepadButtons.LeftStickPress; }
        if (0 != (buttonsShared & 0x10)) { buttons |= GamepadButtons.Guide; }        // Home

        // Intentionally unmapped: Capture (buttonsShared 0x20) and the SR/SL rail buttons (buttonsRight/Left 0x10
        // /0x20) — the latter exist only on detached Joy-Cons, and neither has a GamepadButtons slot.

        var orientation = ReadImu(
            accelerometer: out var accelerometer,
            gyro: out var gyro,
            report: report
        );

        state = new GamepadState(
            Accelerometer: accelerometer,
            Buttons: buttons,
            Gyro: gyro,
            LeftStick: ReadStick(
                calibration: in m_leftStickCalibration,
                offset: LeftStickOffset,
                report: report
            ),
            LeftTrigger: ((0 != (buttonsLeft & 0x80))
            ? 1f
            : 0f),   // ZL is digital
            Orientation: orientation,
            RightStick: ReadStick(
                calibration: in m_rightStickCalibration,
                offset: RightStickOffset,
                report: report
            ),
            RightTrigger: ((0 != (buttonsRight & 0x80))
            ? 1f
            : 0f)  // ZR is digital
        );

        return true;
    }

    // Per-device stick calibration decoded from SPI flash: the raw center and the below-/above-center travel per
    // axis. IsValid gates the whole feature — a default (all-zero) value is invalid, so an unread/failed calibration
    // transparently keeps the nominal scale in ReadStick.
    private readonly struct StickCalibration {
        public StickCalibration(int centerX, int centerY, int maxAboveX, int maxAboveY, int minBelowX, int minBelowY) {
            static bool IsCenterPlausible(int value) {
                return (
                    (value >= StickCalibrationMinCenter) &&
                    (value <= StickCalibrationMaxCenter)
                );
            }
            static bool IsSpanPlausible(int value) {
                return (
                    (value >= StickCalibrationMinSpan) &&
                    (value <= StickCalibrationMaxSpan)
                );
            }

            CenterX = centerX;
            CenterY = centerY;
            MaxAboveX = maxAboveX;
            MaxAboveY = maxAboveY;
            MinBelowX = minBelowX;
            MinBelowY = minBelowY;
            IsValid = (
                IsCenterPlausible(value: centerX) && IsCenterPlausible(value: centerY)
                && IsSpanPlausible(value: maxAboveX) && IsSpanPlausible(value: maxAboveY)
                && IsSpanPlausible(value: minBelowX) && IsSpanPlausible(value: minBelowY)
            );
        }

        public float CenterX { get; }
        public float CenterY { get; }
        public bool IsValid { get; }
        public float MaxAboveX { get; }
        public float MaxAboveY { get; }
        public float MinBelowX { get; }
        public float MinBelowY { get; }
    }
}
