using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Puck.Input.Hid;
using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// Parses the Nintendo Switch Pro Controller over USB and Bluetooth and drives its HD rumble, player LEDs and Home
/// LED, following the Linux <c>hid-nintendo</c> driver, SDL's <c>SDL_hidapi_switch</c> and the dekuNukem protocol
/// notes. USB first runs the <c>0x80</c> handshake that switches the pad to HID; both transports then read the
/// stick and IMU calibration from SPI flash, enable the IMU and vibration, and select the full input report
/// <c>0x30</c>, which carries three 5 ms IMU samples, the buttons, both sticks and the battery.
/// </summary>
/// <remarks>
/// Every output report shares one 4-bit packet counter and carries the current rumble, so a subcommand never
/// interrupts a running rumble. The face buttons map by position (B south, A east, Y west, X north), and the
/// Home LED is the settable indicator: its brightness follows the brightest channel of the requested colour.
/// </remarks>
public sealed class NintendoSwitchController : IGamepadParser, IRumbleParser, ILedParser, IServicedGamepadParser {
    private const float AccelerometerNominalGPerLsb = (1f / 4096f);
    private const int BatteryOffset = 2;
    private const int BluetoothOutputReportLength = 49;
    private const int ButtonsLeftOffset = 5;    // Down, Up, Right, Left, SR, SL, L, ZL
    private const int ButtonsRightOffset = 3;   // Y, X, B, A, SR, SL, R, ZR
    private const int ButtonsSharedOffset = 4;  // -, +, RStick, LStick, Home, Capture
    private const byte CommandDisableUsbTimeout = 0x04;
    private const byte CommandFastBaudRate = 0x03;
    private const byte CommandHandshake = 0x02;
    private const byte CommandRequestStatus = 0x01;
    // The default HD-rumble carriers (Linux): 160 Hz for the low band and 320 Hz for the high band, encoded as
    // enc = round(32 * log2(f / 10)) with the high band storing (enc - 0x60) * 4 and the low band enc - 0x40.
    private const byte DefaultLowFrequencyCode = 0x40;
    private const ushort DefaultHighFrequencyCode = 0x0100;
    private const uint FactoryImuCalibrationAddress = 0x6020U;
    private const uint FactoryLeftStickCalibrationAddress = 0x603DU;
    private const uint FactoryRightStickCalibrationAddress = 0x6046U;
    // The gyro reads 936 deg/s across the span between its calibration offset and sensitivity (SDL, dekuNukem);
    // the factory sensitivity of 13371 gives the nominal 0.07 deg/s per LSB.
    private const float GyroCalibrationDegreesPerSecond = 936f;
    private const float GyroNominalRadiansPerSecondPerLsb = ((GyroCalibrationDegreesPerSecond / 13371f) * (MathF.PI / 180f));
    private const int ImuCalibrationLength = 24;
    private const int ImuOffset = 13;   // three 12-byte samples (accel xyz, gyro xyz; int16 LE)
    private const int ImuSampleCount = 3;
    private const int ImuSampleSize = 12;
    // A pad whose IMU reads all zero has lost its enable (SDL); it is re-enabled at this cadence.
    private const float ImuReenableSeconds = 3f;
    private const byte InitCommandInputReportId = 0x81;
    private const byte InitCommandOutputReportId = 0x80;
    private const int LeftStickOffset = 6;
    private const float MaximumCalibrationScaleError = 0.5f;
    private const int MinimumStateReportLength = 13;
    private const int RightStickOffset = 9;
    private const byte RumbleOutputReportId = 0x10;
    private const int RumbleLength = 8;
    private const int SpiReadAttempts = 5;
    // A 0x21 subcommand reply to an SPI read echoes the address (u32 LE) at 15, the length at 19, then the data.
    private const int SpiReplyAddressOffset = 15;
    private const int SpiReplyDataOffset = 20;
    private const int SpiReplyLengthOffset = 19;
    private const int StandardReportLength = (ImuOffset + (ImuSampleCount * ImuSampleSize));
    private const byte StandardInputReportId = 0x30;
    private const int StickCalibrationDataLength = 9;
    private const int StickCalibrationMaxCenter = 3583;
    private const int StickCalibrationMaxSpan = 2400;
    private const int StickCalibrationMinCenter = 512;
    private const int StickCalibrationMinSpan = 128;
    private const int StickCenter = 2048;
    private const float StickDeadzone = 0.12f;
    private const float StickRange = 1800f;
    private const int SubcommandAttempts = 5;
    private const byte SubcommandDeviceInfo = 0x02;
    private const byte SubcommandEnableImu = 0x40;
    private const byte SubcommandEnableVibration = 0x48;
    private const byte SubcommandHomeLed = 0x38;
    private const int SubcommandOffset = 10;
    private const byte SubcommandOutputReportId = 0x01;
    private const byte SubcommandPlayerLights = 0x30;
    private const int SubcommandReplyAckOffset = 13;
    private const int SubcommandReplyIdOffset = 14;
    private const byte SubcommandReplyInputReportId = 0x21;
    private const int SubcommandReplyReads = 16;
    private const byte SubcommandSetInputReportMode = 0x03;
    private const byte SubcommandSpiFlashRead = 0x10;
    // Bluetooth drops a pad that is sent subcommands faster than this (Linux enforces 60 ms over Bluetooth).
    private const int SubcommandSpacingBluetoothMilliseconds = 60;
    private const int SubcommandSpacingUsbMilliseconds = 20;
    private const int UsbOutputReportLength = 64;
    private const uint UserImuCalibrationAddress = 0x8028U;
    private const uint UserImuMagicAddress = 0x8026U;
    private const byte UserMagic0 = 0xB2;
    private const byte UserMagic1 = 0xA1;
    private const uint UserLeftStickCalibrationAddress = 0x8010U;
    private const uint UserRightStickCalibrationAddress = 0x801BU;

    // HD-rumble amplitudes, 0..1003, one per encoder step (Linux joycon_rumble_amplitudes). Step i encodes as
    // high-band byte 2i and low-band word (i odd ? 0x8000 : 0) | (0x40 + i / 2).
    private static ReadOnlySpan<ushort> RumbleAmplitudes => [
        0, 10, 12, 14, 17, 20, 24, 28, 33, 40, 47, 56, 67, 80, 95, 112, 117, 123, 128, 134, 140, 146, 152, 159, 166,
        173, 181, 189, 198, 206, 215, 225, 230, 235, 240, 245, 251, 256, 262, 268, 273, 279, 286, 292, 298, 305, 311,
        318, 325, 332, 340, 347, 355, 362, 370, 378, 387, 395, 404, 413, 422, 431, 440, 450, 460, 470, 480, 491, 501,
        512, 524, 535, 547, 559, 571, 584, 596, 609, 623, 636, 650, 665, 679, 694, 709, 725, 741, 757, 773, 790, 808,
        825, 843, 862, 881, 900, 920, 940, 960, 981, 1003,
    ];
    // The player-indicator patterns by slot (Linux, SDL).
    private static ReadOnlySpan<byte> PlayerLeds => [0x1, 0x3, 0x7, 0xF, 0x9, 0x5, 0xD, 0x6,];

    private readonly IHidDevice m_device;
    private readonly bool m_isBluetooth;
    private readonly byte[] m_outputBuffer;
    private readonly byte[] m_responseBuffer;
    private readonly byte[] m_rumbleData = new byte[RumbleLength];
    private readonly ImuOrientationTracker m_tracker = new();

    private Vector3 m_accelerometerScale = new(value: AccelerometerNominalGPerLsb);
    private float m_averageReportSeconds = 0.015f;
    private Vector3 m_gyroOffset;
    private Vector3 m_gyroScale = new(value: GyroNominalRadiansPerSecondPerLsb);
    private byte m_homeLedIntensity;
    private bool m_imuSilent;
    private long m_lastReportTimestamp;
    private StickCalibration m_leftStickCalibration;
    private long m_nextImuReenable;
    private byte m_packetNumber;
    private StickCalibration m_rightStickCalibration;

    /// <summary>Initializes a new instance of the <see cref="NintendoSwitchController"/> class.</summary>
    /// <param name="device">The opened HID device handle for the Switch Pro Controller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public NintendoSwitchController(IHidDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        m_device = device;
        m_isBluetooth = (device.Transport == HidTransport.Bluetooth);
        // Output reports are 64 bytes on USB and 49 over Bluetooth; Windows requires the declared length, which is
        // floored to the documented one because composition writes at protocol offsets.
        m_outputBuffer = new byte[Math.Max(
            val1: device.OutputReportByteLength,
            val2: (m_isBluetooth
                ? BluetoothOutputReportLength
                : UsbOutputReportLength
            )
        )];
        m_responseBuffer = new byte[Math.Max(
            val1: device.InputReportByteLength,
            val2: UsbOutputReportLength
        )];
        WriteRumbleSide(
            destination: m_rumbleData.AsSpan(
                length: 4,
                start: 0
            ),
            high: 0f,
            low: 0f
        );
        m_rumbleData.AsSpan(
            length: 4,
            start: 0
        ).CopyTo(destination: m_rumbleData.AsSpan(start: 4));
    }

    /// <inheritdoc />
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.Gyro;
    /// <inheritdoc />
    public TimeSpan RumbleRefreshInterval => TimeSpan.FromMilliseconds(value: 50);
    /// <inheritdoc />
    public TimeSpan RumbleWriteInterval => TimeSpan.FromMilliseconds(value: 30);
    /// <inheritdoc />
    public GamepadType Type => GamepadType.SwitchPro;

    private static GamepadButtons DecodeButtons(ReadOnlySpan<byte> report) {
        var right = report[ButtonsRightOffset];
        var shared = report[ButtonsSharedOffset];
        var left = report[ButtonsLeftOffset];
        var buttons = GamepadButtons.None;

        if (0 != (right & 0x04)) { buttons |= GamepadButtons.ButtonSouth; }   // B
        if (0 != (right & 0x08)) { buttons |= GamepadButtons.ButtonEast; }    // A
        if (0 != (right & 0x01)) { buttons |= GamepadButtons.ButtonWest; }    // Y
        if (0 != (right & 0x02)) { buttons |= GamepadButtons.ButtonNorth; }   // X
        if (0 != (right & 0x40)) { buttons |= GamepadButtons.RightShoulder; } // R

        if (0 != (left & 0x02)) { buttons |= GamepadButtons.DpadUp; }
        if (0 != (left & 0x01)) { buttons |= GamepadButtons.DpadDown; }
        if (0 != (left & 0x08)) { buttons |= GamepadButtons.DpadLeft; }
        if (0 != (left & 0x04)) { buttons |= GamepadButtons.DpadRight; }
        if (0 != (left & 0x40)) { buttons |= GamepadButtons.LeftShoulder; }   // L

        if (0 != (shared & 0x01)) { buttons |= GamepadButtons.Back; }         // Minus
        if (0 != (shared & 0x02)) { buttons |= GamepadButtons.Start; }        // Plus
        if (0 != (shared & 0x04)) { buttons |= GamepadButtons.RightStickPress; }
        if (0 != (shared & 0x08)) { buttons |= GamepadButtons.LeftStickPress; }
        if (0 != (shared & 0x10)) { buttons |= GamepadButtons.Guide; }        // Home
        if (0 != (shared & 0x20)) { buttons |= GamepadButtons.Capture; }

        return buttons;
    }
    // Byte 2: the high nibble's upper three bits are the charge in quarters (0 empty .. 4 full), bit 4 is
    // charging, and bit 0 says the pad is powered by its cable.
    private static GamepadPower DecodePower(byte status) {
        var percent = ((byte)Math.Min(
            val1: ((status >> 5) * 25),
            val2: 100
        ));

        if (0 != (status & 0x10)) {
            return new GamepadPower(
                Percent: percent,
                State: GamepadPowerState.Charging
            );
        }

        return new GamepadPower(
            Percent: percent,
            State: (((0 != (status & 0x01)) && (100 == percent))
                ? GamepadPowerState.Charged
                : GamepadPowerState.OnBattery
            )
        );
    }
    // Loads the IMU calibration (user block when its magic is present, else factory): per axis an accelerometer
    // offset and sensitivity and a gyro offset and sensitivity. Gyro: deg/s = (raw - offset) * 936 / (sensitivity
    // - offset). Accelerometer: g = raw * 4 / (sensitivity - offset). A block more than half away from nominal
    // is ignored (SDL, dekuNukem).
    private async ValueTask LoadImuCalibrationAsync(CancellationToken cancellationToken) {
        var magic = new byte[2];
        var data = new byte[ImuCalibrationLength];
        var address = ((
            await TryReadSpiFlashAsync(
            address: UserImuMagicAddress,
            cancellationToken: cancellationToken,
            destination: magic
        ) &&
            (UserMagic0 == magic[0]) &&
            (UserMagic1 == magic[1])
        )
            ? UserImuCalibrationAddress
            : FactoryImuCalibrationAddress
        );

        if (!await TryReadSpiFlashAsync(
            address: address,
            cancellationToken: cancellationToken,
            destination: data
        )) {
            Console.Error.WriteLine(value: "[gamepad] Switch Pro IMU calibration unavailable; using nominal scale");

            return;
        }

        var accelerometerOffset = ReadVector(
            data: data,
            offset: 0
        );
        var accelerometerSensitivity = ReadVector(
            data: data,
            offset: 6
        );
        var gyroOffset = ReadVector(
            data: data,
            offset: 12
        );
        var gyroSensitivity = ReadVector(
            data: data,
            offset: 18
        );
        var accelerometerSpan = (accelerometerSensitivity - accelerometerOffset);
        var gyroSpan = (gyroSensitivity - gyroOffset);

        if (
            (0f == accelerometerSpan.X) || (0f == accelerometerSpan.Y) || (0f == accelerometerSpan.Z) ||
            (0f == gyroSpan.X) || (0f == gyroSpan.Y) || (0f == gyroSpan.Z)
        ) {
            Console.Error.WriteLine(value: "[gamepad] Switch Pro IMU calibration implausible; using nominal scale");

            return;
        }

        var accelerometerScale = (new Vector3(value: 4f) / accelerometerSpan);
        var gyroScale = (new Vector3(value: (GyroCalibrationDegreesPerSecond * (MathF.PI / 180f))) / gyroSpan);

        if (
            !IsNear(
            nominal: AccelerometerNominalGPerLsb,
            value: accelerometerScale
        ) ||
            !IsNear(
            nominal: GyroNominalRadiansPerSecondPerLsb,
            value: gyroScale
        )
        ) {
            Console.Error.WriteLine(value: "[gamepad] Switch Pro IMU calibration implausible; using nominal scale");

            return;
        }

        m_accelerometerScale = accelerometerScale;
        m_gyroOffset = gyroOffset;
        m_gyroScale = gyroScale;

        static bool IsNear(Vector3 value, float nominal) {
            var error = Vector3.Abs(value: (Vector3.One - (value / nominal)));

            return (
                (error.X <= MaximumCalibrationScaleError) &&
                (error.Y <= MaximumCalibrationScaleError) &&
                (error.Z <= MaximumCalibrationScaleError)
            );
        }
        static Vector3 ReadVector(byte[] data, int offset) => new(
            x: BinaryPrimitives.ReadInt16LittleEndian(source: data.AsSpan(start: offset)),
            y: BinaryPrimitives.ReadInt16LittleEndian(source: data.AsSpan(start: (offset + 2))),
            z: BinaryPrimitives.ReadInt16LittleEndian(source: data.AsSpan(start: (offset + 4)))
        );
    }
    // Each stick prefers its user calibration (magic 0xB2 0xA1) over the factory one and keeps the nominal scale
    // when neither reads plausibly.
    private async ValueTask<StickCalibration> LoadStickCalibrationAsync(uint factoryAddress, uint userAddress, bool isLeftStick, string stickName, CancellationToken cancellationToken) {
        var userBuffer = new byte[(StickCalibrationDataLength + 2)];

        if (
            await TryReadSpiFlashAsync(
            address: userAddress,
            cancellationToken: cancellationToken,
            destination: userBuffer
        ) &&
            (UserMagic0 == userBuffer[0]) &&
            (UserMagic1 == userBuffer[1]) &&
            TryDecodeStickCalibration(
            calibration: out var userCalibration,
            data: userBuffer.AsSpan(
                length: StickCalibrationDataLength,
                start: 2
            ),
            isLeftStick: isLeftStick
        )
        ) {
            return userCalibration;
        }

        var factoryBuffer = new byte[StickCalibrationDataLength];

        if (
            await TryReadSpiFlashAsync(
            address: factoryAddress,
            cancellationToken: cancellationToken,
            destination: factoryBuffer
        ) &&
            TryDecodeStickCalibration(
            calibration: out var factoryCalibration,
            data: factoryBuffer,
            isLeftStick: isLeftStick
        )
        ) {
            return factoryCalibration;
        }

        Console.Error.WriteLine(value: $"[gamepad] Switch Pro {stickName} stick calibration unavailable; using nominal scale");

        return default;
    }
    private byte NextPacketNumber() {
        return ((byte)(m_packetNumber++ & 0x0F));
    }
    // Decodes the report's three IMU samples: each advances the fusion one step, and the state carries their mean.
    // Samples are spaced a third of the measured report interval apart (Linux), since the pad reports every 8 to
    // 15 ms depending on transport and link.
    private Quaternion ReadImu(ReadOnlySpan<byte> report, out Vector3 accelerometer, out Vector3 gyro) {
        var accelerometerSum = Vector3.Zero;
        var gyroSum = Vector3.Zero;
        var sampleSeconds = (m_averageReportSeconds / ImuSampleCount);

        for (var sample = 0; (sample < ImuSampleCount); ++sample) {
            var baseOffset = (ImuOffset + (sample * ImuSampleSize));
            var accelerometerSample = ToFusionFrame(sensor: (GamepadNormalization.ReadVector3Int16(
                offset: baseOffset,
                scale: 1f,
                source: report
            ) * m_accelerometerScale));
            var gyroSample = ToFusionFrame(sensor: ((GamepadNormalization.ReadVector3Int16(
                offset: (baseOffset + 6),
                scale: 1f,
                source: report
            ) - m_gyroOffset) * m_gyroScale));

            accelerometerSum += accelerometerSample;
            gyroSum += gyroSample;

            _ = m_tracker.Update(
                accelerometerG: accelerometerSample,
                deltaSeconds: sampleSeconds,
                gyroRadiansPerSecond: gyroSample
            );
        }

        accelerometer = (accelerometerSum / ImuSampleCount);
        gyro = (gyroSum / ImuSampleCount);

        return m_tracker.Orientation;
    }
    private static Vector2 ReadStick(ReadOnlySpan<byte> report, int offset, in StickCalibration calibration) {
        var rawX = report[offset] | ((report[(offset + 1)] & 0x0F) << 8);
        var rawY = (report[(offset + 1)] >> 4) | (report[(offset + 2)] << 4);
        float x, y;

        if (calibration.IsValid) {
            // Per-axis (raw - center) / span, with the above-center travel for a positive push and the below-center
            // travel for a negative one, so an asymmetric stick still reaches ±1 at full deflection.
            var deltaX = (rawX - calibration.CenterX);
            var deltaY = (rawY - calibration.CenterY);

            x = (deltaX / ((deltaX >= 0f)
                ? calibration.MaxAboveX
                : calibration.MinBelowX));
            y = (deltaY / ((deltaY >= 0f)
                ? calibration.MaxAboveY
                : calibration.MinBelowY));
        } else {
            x = ((rawX - StickCenter) / StickRange);
            y = ((rawY - StickCenter) / StickRange);
        }

        return GamepadNormalization.ApplyRadialDeadzone(
            deadzone: StickDeadzone,
            stick: new Vector2(
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
            )
        );
    }
    // Sends one USB init command (0x80 xx) and waits for its 0x81 echo, rewriting every seventh read.
    private async ValueTask<bool> SendInitCommandAsync(byte command, bool waitForAck, CancellationToken cancellationToken) {
        const int MaximumReads = 64;
        const int WriteInterval = 7;

        var buffer = m_outputBuffer;

        for (var read = 0; (read < MaximumReads); ++read) {
            if (0 == (read % WriteInterval)) {
                buffer.AsSpan().Clear();
                buffer[0] = InitCommandOutputReportId;
                buffer[1] = command;

                await m_device.WriteAsync(
                    buffer: buffer,
                    cancellationToken: cancellationToken
                );

                if (!waitForAck) {
                    return true;
                }
            }

            var count = await m_device.ReadAsync(
                buffer: m_responseBuffer,
                cancellationToken: cancellationToken,
                timeoutInMilliseconds: 60
            );

            if (
                (count > 1) &&
                (InitCommandInputReportId == m_responseBuffer[0]) &&
                (command == m_responseBuffer[1])
            ) {
                return true;
            }
        }

        return false;
    }
    // Sends a subcommand (report 0x01 carrying the current rumble) and waits for its acknowledged 0x21 reply,
    // resending up to five times (SDL). `matches` additionally checks the reply payload. Subcommands are spaced
    // so a Bluetooth pad is never sent them faster than it accepts.
    private async ValueTask<bool> SendSubcommandAsync(byte subcommand, ReadOnlyMemory<byte> arguments, Func<byte[], int, bool>? matches, CancellationToken cancellationToken) {
        var buffer = m_outputBuffer;

        for (var attempt = 0; (attempt < SubcommandAttempts); ++attempt) {
            buffer.AsSpan().Clear();
            buffer[0] = SubcommandOutputReportId;
            buffer[1] = NextPacketNumber();
            m_rumbleData.CopyTo(destination: buffer.AsSpan(start: 2));
            buffer[SubcommandOffset] = subcommand;
            arguments.Span.CopyTo(destination: buffer.AsSpan(start: (SubcommandOffset + 1)));

            await m_device.WriteAsync(
                buffer: buffer,
                cancellationToken: cancellationToken
            );

            for (var read = 0; (read < SubcommandReplyReads); ++read) {
                var count = await m_device.ReadAsync(
                    buffer: m_responseBuffer,
                    cancellationToken: cancellationToken,
                    timeoutInMilliseconds: 60
                );

                if (
                    (count > SubcommandReplyIdOffset) &&
                    (SubcommandReplyInputReportId == m_responseBuffer[0]) &&
                    (subcommand == m_responseBuffer[SubcommandReplyIdOffset]) &&
                    (0 != (m_responseBuffer[SubcommandReplyAckOffset] & 0x80)) &&
                    ((matches is null) || matches(
                        arg1: m_responseBuffer,
                        arg2: count
                    ))
                ) {
                    await Task.Delay(
                        cancellationToken: cancellationToken,
                        millisecondsDelay: (m_isBluetooth
                            ? SubcommandSpacingBluetoothMilliseconds
                            : SubcommandSpacingUsbMilliseconds
                        )
                    );

                    return true;
                }
            }
        }

        return false;
    }
    // Maps the Switch IMU frame (X toward the triggers, Y left, Z up) into the fusion frame (X right, Y up, Z
    // toward the player), as SDL does to match the PlayStation pads.
    private static Vector3 ToFusionFrame(Vector3 sensor) {
        return new Vector3(
            x: -sensor.Y,
            y: sensor.Z,
            z: -sensor.X
        );
    }
    // Decodes a 9-byte SPI stick-calibration block: six 12-bit values as three (x, y) triples, ordered above,
    // center, below for the left stick and center, below, above for the right.
    private static bool TryDecodeStickCalibration(ReadOnlySpan<byte> data, bool isLeftStick, out StickCalibration calibration) {
        Span<int> values = stackalloc int[6];

        values[0] = data[0] | ((data[1] & 0x0F) << 8);
        values[1] = (data[1] >> 4) | (data[2] << 4);
        values[2] = data[3] | ((data[4] & 0x0F) << 8);
        values[3] = (data[4] >> 4) | (data[5] << 4);
        values[4] = data[6] | ((data[7] & 0x0F) << 8);
        values[5] = (data[7] >> 4) | (data[8] << 4);

        calibration = (isLeftStick
            ? new StickCalibration(
                centerX: values[2],
                centerY: values[3],
                maxAboveX: values[0],
                maxAboveY: values[1],
                minBelowX: values[4],
                minBelowY: values[5]
            )
            : new StickCalibration(
                centerX: values[0],
                centerY: values[1],
                maxAboveX: values[4],
                maxAboveY: values[5],
                minBelowX: values[2],
                minBelowY: values[3]
            )
        );

        return calibration.IsValid;
    }
    // Reads destination.Length bytes of SPI flash with subcommand 0x10, matching the reply by its echoed address
    // and length so an unrelated queued reply is never taken for it.
    private async ValueTask<bool> TryReadSpiFlashAsync(uint address, byte[] destination, CancellationToken cancellationToken) {
        var length = destination.Length;
        var arguments = new byte[5];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: arguments,
            value: address
        );
        arguments[4] = ((byte)length);

        for (var attempt = 0; (attempt < SpiReadAttempts); ++attempt) {
            if (await SendSubcommandAsync(
                arguments: arguments,
                cancellationToken: cancellationToken,
                matches: (reply, count) => (
                    (count >= (SpiReplyDataOffset + length)) &&
                    (address == BinaryPrimitives.ReadUInt32LittleEndian(source: reply.AsSpan(start: SpiReplyAddressOffset))) &&
                    (length == reply[SpiReplyLengthOffset])
                ),
                subcommand: SubcommandSpiFlashRead
            )) {
                m_responseBuffer.AsSpan(
                    length: length,
                    start: SpiReplyDataOffset
                ).CopyTo(destination: destination);

                return true;
            }
        }

        return false;
    }
    // Writes one side's four HD-rumble bytes: the 320 Hz high band at the high intensity's amplitude step and
    // the 160 Hz low band at the low intensity's. The 9-bit high-band frequency lends its top bit to byte 1, and
    // the 9-bit low-band amplitude lends its top bit to byte 2.
    private static void WriteRumbleSide(Span<byte> destination, float high, float low) {
        var highStep = AmplitudeStep(intensity: high);
        var lowStep = AmplitudeStep(intensity: low);
        var lowAmplitude = ((((lowStep & 1) != 0)
            ? 0x8000
            : 0) | (0x40 + (lowStep >> 1)));

        destination[0] = ((byte)(DefaultHighFrequencyCode & 0xFF));
        destination[1] = ((byte)((highStep * 2) | ((DefaultHighFrequencyCode >> 8) & 0x01)));
        destination[2] = ((byte)(DefaultLowFrequencyCode | ((lowAmplitude >> 8) & 0x80)));
        destination[3] = ((byte)(lowAmplitude & 0xFF));

        static int AmplitudeStep(float intensity) {
            var target = (intensity * RumbleAmplitudes[^1]);
            var amplitudes = RumbleAmplitudes;

            for (var step = 0; (step < amplitudes.Length); ++step) {
                if (amplitudes[step] >= target) {
                    return step;
                }
            }

            return (amplitudes.Length - 1);
        }
    }
    private ValueTask WriteRumbleReportAsync(CancellationToken cancellationToken) {
        var buffer = m_outputBuffer;

        buffer.AsSpan().Clear();
        buffer[0] = RumbleOutputReportId;
        buffer[1] = NextPacketNumber();
        m_rumbleData.CopyTo(destination: buffer.AsSpan(start: 2));

        return m_device.WriteAsync(
            buffer: buffer,
            cancellationToken: cancellationToken
        );
    }
    // Writes a subcommand without waiting for its reply, for the steady-state I/O loop, which must keep reading.
    private ValueTask WriteSubcommandAsync(byte subcommand, ReadOnlySpan<byte> arguments, CancellationToken cancellationToken) {
        var buffer = m_outputBuffer;

        buffer.AsSpan().Clear();
        buffer[0] = SubcommandOutputReportId;
        buffer[1] = NextPacketNumber();
        m_rumbleData.CopyTo(destination: buffer.AsSpan(start: 2));
        buffer[SubcommandOffset] = subcommand;
        arguments.CopyTo(destination: buffer.AsSpan(start: (SubcommandOffset + 1)));

        return m_device.WriteAsync(
            buffer: buffer,
            cancellationToken: cancellationToken
        );
    }
    private byte[] HomeLedArguments() {
        // Mini-cycle 1 at a steady intensity, no further cycles (SDL).
        var intensity = ((byte)(m_homeLedIntensity << 4));

        return [0x01, intensity, intensity, 0x00,];
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default) {
        m_packetNumber = 0;

        if (!m_isBluetooth) {
            // USB: request status, handshake, raise the baud rate, handshake again, then force HID-only mode (which
            // sends no acknowledgement). Bluetooth speaks HID from the start and has no 0x80 report.
            _ = await SendInitCommandAsync(
                cancellationToken: cancellationToken,
                command: CommandRequestStatus,
                waitForAck: true
            );
            _ = await SendInitCommandAsync(
                cancellationToken: cancellationToken,
                command: CommandHandshake,
                waitForAck: true
            );
            _ = await SendInitCommandAsync(
                cancellationToken: cancellationToken,
                command: CommandFastBaudRate,
                waitForAck: true
            );
            _ = await SendInitCommandAsync(
                cancellationToken: cancellationToken,
                command: CommandHandshake,
                waitForAck: true
            );
            _ = await SendInitCommandAsync(
                cancellationToken: cancellationToken,
                command: CommandDisableUsbTimeout,
                waitForAck: false
            );
        }

        _ = await SendSubcommandAsync(
            arguments: ReadOnlyMemory<byte>.Empty,
            cancellationToken: cancellationToken,
            matches: null,
            subcommand: SubcommandDeviceInfo
        );
        m_leftStickCalibration = await LoadStickCalibrationAsync(
            cancellationToken: cancellationToken,
            factoryAddress: FactoryLeftStickCalibrationAddress,
            isLeftStick: true,
            stickName: "left",
            userAddress: UserLeftStickCalibrationAddress
        );
        m_rightStickCalibration = await LoadStickCalibrationAsync(
            cancellationToken: cancellationToken,
            factoryAddress: FactoryRightStickCalibrationAddress,
            isLeftStick: false,
            stickName: "right",
            userAddress: UserRightStickCalibrationAddress
        );
        await LoadImuCalibrationAsync(cancellationToken: cancellationToken);
        _ = await SendSubcommandAsync(
            arguments: new byte[] { 0x01, },
            cancellationToken: cancellationToken,
            matches: null,
            subcommand: SubcommandEnableImu
        );
        _ = await SendSubcommandAsync(
            arguments: new byte[] { StandardInputReportId, },
            cancellationToken: cancellationToken,
            matches: null,
            subcommand: SubcommandSetInputReportMode
        );
        _ = await SendSubcommandAsync(
            arguments: new byte[] { 0x01, },
            cancellationToken: cancellationToken,
            matches: null,
            subcommand: SubcommandEnableVibration
        );
        _ = await SendSubcommandAsync(
            arguments: new byte[] {
                ((playerIndex >= 0)
                ? PlayerLeds[(playerIndex % PlayerLeds.Length)]
                : ((byte)0)),
            },
            cancellationToken: cancellationToken,
            matches: null,
            subcommand: SubcommandPlayerLights
        );
        _ = await SendSubcommandAsync(
            arguments: HomeLedArguments(),
            cancellationToken: cancellationToken,
            matches: null,
            subcommand: SubcommandHomeLed
        );
    }
    /// <inheritdoc />
    public ValueTask ServiceAsync(long timestamp, long timestampFrequency, CancellationToken cancellationToken) {
        if (
            !m_imuSilent ||
            (timestamp < m_nextImuReenable)
        ) {
            return ValueTask.CompletedTask;
        }

        m_nextImuReenable = (timestamp + ((long)(ImuReenableSeconds * timestampFrequency)));

        return WriteSubcommandAsync(
            arguments: [0x01,],
            cancellationToken: cancellationToken,
            subcommand: SubcommandEnableImu
        );
    }
    /// <inheritdoc />
    /// <remarks>The Home LED has one channel, so its brightness follows the brightest of the three.</remarks>
    public ValueTask SetLedAsync(LedColor color, CancellationToken cancellationToken = default) {
        m_homeLedIntensity = ((byte)(Math.Max(
            val1: color.Red,
            val2: Math.Max(
                val1: color.Green,
                val2: color.Blue
            )
        ) >> 4));

        return WriteSubcommandAsync(
            arguments: HomeLedArguments(),
            cancellationToken: cancellationToken,
            subcommand: SubcommandHomeLed
        );
    }
    /// <inheritdoc />
    /// <remarks>Both sides carry the same bands: the low intensity drives the 160 Hz band and the high intensity
    /// the 320 Hz band, each through the HD-rumble amplitude table.</remarks>
    public ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default) {
        WriteRumbleSide(
            destination: m_rumbleData.AsSpan(
                length: 4,
                start: 0
            ),
            high: highFrequency,
            low: lowFrequency
        );
        m_rumbleData.AsSpan(
            length: 4,
            start: 0
        ).CopyTo(destination: m_rumbleData.AsSpan(start: 4));

        return WriteRumbleReportAsync(cancellationToken: cancellationToken);
    }
    /// <inheritdoc />
    /// <remarks>Accepts the full report <c>0x30</c> and the subcommand reply <c>0x21</c>, whose leading bytes
    /// carry the same buttons, sticks and battery; a reply keeps the last orientation.</remarks>
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        state = GamepadState.Neutral;

        if (report.Length < MinimumStateReportLength) {
            return false;
        }

        var full = (StandardInputReportId == report[0]);

        if (
            !full &&
            (SubcommandReplyInputReportId != report[0])
        ) {
            return false;
        }

        if (
            full &&
            (report.Length < StandardReportLength)
        ) {
            return false;
        }

        var buttons = DecodeButtons(report: report);
        var accelerometer = Vector3.Zero;
        var gyro = Vector3.Zero;
        var orientation = m_tracker.Orientation;

        if (full) {
            var now = Stopwatch.GetTimestamp();

            if (m_lastReportTimestamp != 0L) {
                var interval = Math.Clamp(
                    max: 0.05f,
                    min: 0.002f,
                    value: ((float)((now - m_lastReportTimestamp) / ((double)Stopwatch.Frequency)))
                );

                m_averageReportSeconds += ((interval - m_averageReportSeconds) / 64f);
            }

            m_lastReportTimestamp = now;
            m_imuSilent = (
                (0 == BinaryPrimitives.ReadInt16LittleEndian(source: report[ImuOffset..])) &&
                (0 == BinaryPrimitives.ReadInt16LittleEndian(source: report[(ImuOffset + 2)..])) &&
                (0 == BinaryPrimitives.ReadInt16LittleEndian(source: report[(ImuOffset + 4)..]))
            );

            if (!m_imuSilent) {
                orientation = ReadImu(
                    accelerometer: out accelerometer,
                    gyro: out gyro,
                    report: report
                );
            }
        }

        state = new GamepadState(
            Accelerometer: accelerometer,
            Buttons: buttons,
            Gyro: gyro,
            LeftStick: ReadStick(
                calibration: in m_leftStickCalibration,
                offset: LeftStickOffset,
                report: report
            ),
            LeftTrigger: ((0 != (report[ButtonsLeftOffset] & 0x80))
            ? 1f
            : 0f),   // ZL is digital
            Orientation: orientation,
            Power: DecodePower(status: report[BatteryOffset]),
            RightStick: ReadStick(
                calibration: in m_rightStickCalibration,
                offset: RightStickOffset,
                report: report
            ),
            RightTrigger: ((0 != (report[ButtonsRightOffset] & 0x80))
            ? 1f
            : 0f)    // ZR is digital
        );

        return true;
    }

    // Per-device stick calibration from SPI flash: the raw center and the below-/above-center travel per axis. A
    // default value is invalid, so an unread or failed calibration keeps the nominal scale.
    private readonly struct StickCalibration {
        public StickCalibration(int centerX, int centerY, int maxAboveX, int maxAboveY, int minBelowX, int minBelowY) {
            static bool IsCenterPlausible(int value) => ((value >= StickCalibrationMinCenter) && (value <= StickCalibrationMaxCenter));
            static bool IsSpanPlausible(int value) => ((value >= StickCalibrationMinSpan) && (value <= StickCalibrationMaxSpan));

            CenterX = centerX;
            CenterY = centerY;
            MaxAboveX = maxAboveX;
            MaxAboveY = maxAboveY;
            MinBelowX = minBelowX;
            MinBelowY = minBelowY;
            IsValid = (
                IsCenterPlausible(value: centerX) && IsCenterPlausible(value: centerY) &&
                IsSpanPlausible(value: maxAboveX) && IsSpanPlausible(value: maxAboveY) &&
                IsSpanPlausible(value: minBelowX) && IsSpanPlausible(value: minBelowY)
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
