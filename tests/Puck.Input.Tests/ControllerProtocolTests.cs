using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using Puck.Input.Devices;
using Puck.Input.Hid;
using Puck.Platform.Windows.Gamepad;

namespace Puck.Input.Tests;

/// <summary>Pins each controller's wire protocol to the byte layouts of the Linux and SDL drivers it follows:
/// report shapes, button bits, calibration arithmetic, power decoding and output framing. Running a parser
/// cannot show these without the physical pad.</summary>
public sealed class ControllerProtocolTests {
    private static uint Crc(byte seed, ReadOnlySpan<byte> data) {
        var crc = new Crc32();

        crc.Append(source: [seed]);
        crc.Append(source: data);

        return crc.GetCurrentHashAsUInt32();
    }
    private static byte[] DualSenseBluetoothReport(Action<Span<byte>> fill) {
        var report = new byte[78];

        report[0] = 0x31;
        report[(2 + 7)] = 0x08;   // neutral hat
        fill(obj: report.AsSpan(start: 2));
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: report.AsSpan(start: 74),
            value: Crc(
                data: report.AsSpan(
                    length: 74,
                    start: 0
                ),
                seed: 0xA1
            )
        );

        return report;
    }
    private static byte[] DualSenseCalibration() {
        var report = new byte[41];

        report[0] = 0x05;
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 1), value: 100);    // pitch bias
        for (var axis = 0; (axis < 3); ++axis) {
            BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: (7 + (axis * 4))), value: 6144);
            BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: (9 + (axis * 4))), value: -6144);
            BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: (23 + (axis * 4))), value: 8192);
            BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: (25 + (axis * 4))), value: -8192);
        }

        // Speed+ and speed- of 512 each over a 12288-count span: 1/12 deg/s per count.
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 19), value: 512);
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 21), value: 512);

        return report;
    }
    private static byte[] DualSenseFirmware(ushort version) {
        var report = new byte[64];

        report[0] = 0x20;
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination: report.AsSpan(start: 44),
            value: version
        );

        return report;
    }
    private static byte[] DualSenseUsbReport(Action<Span<byte>> fill) {
        var report = new byte[64];

        report[0] = 0x01;
        report[(1 + 7)] = 0x08;   // neutral hat
        fill(obj: report.AsSpan(start: 1));

        return report;
    }
    private static byte[] SteamState(uint packet, uint buttons, (short X, short Y) shared, (short X, short Y, short Z) accelerometer = default, (short X, short Y, short Z) gyro = default) {
        var report = new byte[64];

        report[0] = 0x01;
        report[2] = 0x01;
        report[3] = 60;
        BinaryPrimitives.WriteUInt32LittleEndian(destination: report.AsSpan(start: 4), value: packet);
        report[8] = ((byte)buttons);
        report[9] = ((byte)(buttons >> 8));
        report[10] = ((byte)(buttons >> 16));
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 16), value: shared.X);
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 18), value: shared.Y);
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 28), value: accelerometer.X);
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 30), value: accelerometer.Y);
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 32), value: accelerometer.Z);
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 34), value: gyro.X);
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 36), value: gyro.Y);
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: 38), value: gyro.Z);

        return report;
    }
    private static byte[] SwitchSubcommandReply(byte[] request) {
        var reply = new byte[64];

        reply[0] = 0x21;
        reply[13] = 0x80;
        reply[14] = request[10];
        // An SPI read echoes its address and length; the flash reads as erased (0xFF), so calibration falls back.
        request.AsSpan(
            length: 5,
            start: 11
        ).CopyTo(destination: reply.AsSpan(start: 15));
        reply.AsSpan(start: 20).Fill(value: 0xFF);

        return reply;
    }
    private static byte[] TritonState(byte id, uint buttons, int padShift = 0, ushort rightPressure = 0) {
        var report = new byte[64];

        report[0] = id;
        BinaryPrimitives.WriteUInt32LittleEndian(destination: report.AsSpan(start: 2), value: buttons);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: report.AsSpan(start: (28 + padShift)), value: rightPressure);
        BinaryPrimitives.WriteInt16LittleEndian(destination: report.AsSpan(start: (24 + padShift)), value: 16384);  // right pad X

        return report;
    }

    [Fact]
    public void DualSense_usb_report_decodes_buttons_power_and_touch() {
        using var hid = new TestHidDevice();
        var parser = new DualSenseController(device: hid);
        var report = DualSenseUsbReport(fill: block => {
            block[7] = 0x28;      // Cross, neutral hat
            block[8] = 0x20;      // Options
            block[9] = 0xF4;      // mute, plus the Edge-only bits a standard pad must ignore
            block[32] = 0x80;     // touch 0 lifted
            block[36] = 0x05;     // touch 1 down, id 5, at (1919, 1079)
            block[37] = 0x7F;
            block[38] = 0x77;
            block[39] = 0x43;
            block[52] = 0x15;     // charging, 55 %
        });

        Assert.True(condition: parser.TryParse(report: report, state: out var state));
        Assert.Equal(expected: GamepadButtons.ButtonSouth | GamepadButtons.Start | GamepadButtons.Mute, actual: state.Buttons);
        Assert.Equal(expected: new GamepadPower(State: GamepadPowerState.Charging, Percent: 55), actual: state.Power);
        Assert.False(condition: state.Touch0.IsActive);
        Assert.Equal(expected: new GamepadTouchPoint(IsActive: true, Id: 5, Position: Vector2.One), actual: state.Touch1);
        Assert.False(condition: parser.TryParse(report: report[..10], state: out _));
    }
    [Fact]
    public void DualSense_edge_reports_its_function_buttons_and_back_paddles() {
        using var hid = new TestHidDevice { ProductId = 0x0DF2, };
        var parser = new DualSenseController(device: hid);

        Assert.True(condition: parser.TryParse(report: DualSenseUsbReport(fill: block => block[9] = 0xF0), state: out var state));
        Assert.Equal(
            expected: GamepadButtons.LeftFunction | GamepadButtons.RightFunction | GamepadButtons.LeftGrip | GamepadButtons.RightGrip,
            actual: state.Buttons
        );
    }
    [Fact]
    public void DualSense_bluetooth_accepts_a_checked_full_report_and_the_simple_report_and_drops_a_corrupt_one() {
        using var hid = new TestHidDevice { Transport = HidTransport.Bluetooth, };
        var parser = new DualSenseController(device: hid);
        var full = DualSenseBluetoothReport(fill: block => block[52] = 0x2A);
        var simple = new byte[] { 0x01, 0x80, 0x80, 0x80, 0x80, 0x88, 0x00, 0x00, 0xFF, 0x00, };

        Assert.True(condition: parser.TryParse(report: full, state: out var state));
        Assert.Equal(expected: new GamepadPower(State: GamepadPowerState.Charged, Percent: 100), actual: state.Power);

        full[10] ^= 0x01;
        Assert.False(condition: parser.TryParse(report: full, state: out _));

        Assert.True(condition: parser.TryParse(report: simple, state: out var simpleState));
        Assert.Equal(expected: GamepadButtons.ButtonNorth, actual: simpleState.Buttons);
        Assert.Equal(expected: 1f, actual: simpleState.LeftTrigger);
    }
    [Fact]
    public async Task DualSense_applies_factory_calibration_and_picks_rumble_emulation_by_firmware() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var current = new TestHidDevice { FeatureReports = id => id switch { 0x05 => DualSenseCalibration(), 0x20 => DualSenseFirmware(version: 0x0224), _ => null, }, };
        using var legacy = new TestHidDevice { FeatureReports = id => id switch { 0x20 => DualSenseFirmware(version: 0x0215), _ => null, }, };
        var currentParser = new DualSenseController(device: current);
        var legacyParser = new DualSenseController(device: legacy);

        await currentParser.InitializeAsync(playerIndex: 0, cancellationToken: cancellationToken);
        await legacyParser.InitializeAsync(playerIndex: 0, cancellationToken: cancellationToken);
        await currentParser.SetRumbleAsync(lowFrequency: 1f, highFrequency: 1f, cancellationToken: cancellationToken);
        await legacyParser.SetRumbleAsync(lowFrequency: 1f, highFrequency: 1f, cancellationToken: cancellationToken);

        // Initialization releases the connection light animation in its own report, then writes the state.
        Assert.Equal(expected: 0x08, actual: current.Writes[0][2]);
        Assert.Equal(expected: 0x24, actual: current.Writes[1][(1 + 43)]);   // player 1, instant
        Assert.Equal(expected: 0x05, actual: current.Writes[1][(1 + 10)]);   // the official trigger-off mode

        var currentRumble = current.Writes[^1];
        var legacyRumble = legacy.Writes[^1];

        Assert.Equal(expected: 0x04, actual: currentRumble[(1 + 38)]);
        Assert.Equal(expected: 0, actual: (currentRumble[1] & 0x01));
        Assert.Equal(expected: 255, actual: currentRumble[(1 + 3)]);
        Assert.Equal(expected: 0x01, actual: (legacyRumble[1] & 0x01));
        Assert.Equal(expected: 127, actual: legacyRumble[(1 + 3)]);

        // Pitch reads (1300 - 100) counts at 1/12 deg/s each.
        Assert.True(condition: currentParser.TryParse(
            report: DualSenseUsbReport(fill: block => BinaryPrimitives.WriteInt16LittleEndian(destination: block[15..], value: 1300)),
            state: out var state
        ));
        Assert.Equal(expected: (100f * (MathF.PI / 180f)), actual: state.Gyro.X, tolerance: 1e-4f);
    }
    [Fact]
    public async Task DualSense_mute_press_toggles_the_microphone_and_its_light_once_per_press() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new DualSenseController(device: hid);
        var held = DualSenseUsbReport(fill: block => block[9] = 0x04);

        Assert.True(condition: parser.TryParse(report: held, state: out _));
        await parser.ServiceAsync(timestamp: 0L, timestampFrequency: 1L, cancellationToken: cancellationToken);
        Assert.True(condition: parser.TryParse(report: held, state: out _));
        await parser.ServiceAsync(timestamp: 0L, timestampFrequency: 1L, cancellationToken: cancellationToken);

        var write = Assert.Single(collection: hid.Writes);

        Assert.Equal(expected: 0x01, actual: write[(1 + 8)]);
        Assert.Equal(expected: 0x10, actual: write[(1 + 9)]);
    }
    [Fact]
    public async Task Switch_encodes_hd_rumble_through_the_amplitude_table() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new NintendoSwitchController(device: hid);

        await parser.SetRumbleAsync(lowFrequency: 0f, highFrequency: 0f, cancellationToken: cancellationToken);
        await parser.SetRumbleAsync(lowFrequency: 1f, highFrequency: 1f, cancellationToken: cancellationToken);
        await parser.SetRumbleAsync(lowFrequency: (9.5f / 1003f), highFrequency: (9.5f / 1003f), cancellationToken: cancellationToken);

        Assert.Equal(expected: [0x10, 0x00, 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40,], actual: hid.Writes[0][..10]);
        Assert.Equal(expected: [0x00, 0xC9, 0x40, 0x72,], actual: hid.Writes[1][2..6]);
        Assert.Equal(expected: [0x00, 0x03, 0xC0, 0x40,], actual: hid.Writes[2][2..6]);
        Assert.Equal(expected: [0x00, 0x01, 0x02,], actual: hid.Writes.Select(write => write[1]).ToArray());
    }
    [Fact]
    public void Switch_full_report_decodes_capture_home_battery_and_digital_triggers() {
        using var hid = new TestHidDevice();
        var parser = new NintendoSwitchController(device: hid);
        var report = new byte[49];

        report[0] = 0x30;
        report[2] = 0x91;     // full and charging, cable powered
        report[3] = 0x80;     // ZR
        report[4] = 0x30;     // Home, Capture
        report[5] = 0x02;     // up

        Assert.True(condition: parser.TryParse(report: report, state: out var state));
        Assert.Equal(expected: GamepadButtons.Capture | GamepadButtons.Guide | GamepadButtons.DpadUp, actual: state.Buttons);
        Assert.Equal(expected: 1f, actual: state.RightTrigger);
        Assert.Equal(expected: new GamepadPower(State: GamepadPowerState.Charging, Percent: 100), actual: state.Power);
        Assert.Equal(expected: Quaternion.Identity, actual: state.Orientation);

        report[0] = 0x21;
        report[2] = 0x81;     // full, cable powered, not charging
        Assert.True(condition: parser.TryParse(report: report[..13], state: out var reply));
        Assert.Equal(expected: GamepadPowerState.Charged, actual: reply.Power.State);
        Assert.False(condition: parser.TryParse(report: report[..12], state: out _));
    }
    [Fact]
    public async Task Switch_bluetooth_initialization_skips_the_usb_handshake_and_enables_imu_report_mode_and_vibration() {
        using var hid = new TestHidDevice {
            OutputReportByteLength = 49,
            Responder = request => ((request[0] == 0x01) ? SwitchSubcommandReply(request: request) : null),
            Transport = HidTransport.Bluetooth,
        };
        var parser = new NintendoSwitchController(device: hid);

        await parser.InitializeAsync(playerIndex: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(collection: hid.Writes, action: write => Assert.Equal(expected: 49, actual: write.Length));
        Assert.DoesNotContain(collection: hid.Writes, filter: write => (write[0] == 0x80));

        var subcommands = hid.Writes.Select(write => (write[10], write[11])).ToArray();

        Assert.Contains(expected: ((byte)0x40, (byte)0x01), collection: subcommands);
        Assert.Contains(expected: ((byte)0x03, (byte)0x30), collection: subcommands);
        Assert.Contains(expected: ((byte)0x48, (byte)0x01), collection: subcommands);
        Assert.Contains(expected: ((byte)0x30, (byte)0x03), collection: subcommands);
    }
    [Fact]
    public void Steam_decodes_the_shared_pad_and_stick_field_and_maps_its_imu() {
        using var hid = new TestHidDevice();
        var parser = new SteamController(device: hid);

        // Finger on the left pad: the shared field is the pad.
        Assert.True(condition: parser.TryParse(report: SteamState(packet: 1, buttons: 0x080000, shared: (0, 0)), state: out var pad));
        Assert.True(condition: pad.Touch1.IsActive);
        Assert.Equal(expected: Vector2.Zero, actual: pad.LeftStick);

        // Interleaved: the shared field is now the stick, and the pad keeps its last position.
        Assert.True(condition: parser.TryParse(report: SteamState(packet: 2, buttons: 0x800000, shared: (32767, 0)), state: out var both));
        Assert.Equal(expected: 1f, actual: both.LeftStick.X, tolerance: 1e-3f);
        Assert.Equal(expected: pad.Touch1, actual: both.Touch1);

        // No finger and no interleave: a left-pad click is the stick click on older firmware.
        Assert.True(condition: parser.TryParse(report: SteamState(packet: 3, buttons: 0x020000, shared: (0, 0)), state: out var click));
        Assert.Equal(expected: GamepadButtons.LeftStickPress, actual: click.Buttons);
        Assert.False(condition: click.Touch1.IsActive);

        Assert.True(condition: parser.TryParse(
            report: SteamState(packet: 4, buttons: 0, shared: (0, 0), accelerometer: (0, 0, 16384), gyro: (1000, 2000, 3000)),
            state: out var motion
        ));
        Assert.Equal(expected: new Vector3(x: 0f, y: 1f, z: 0f), actual: motion.Accelerometer);
        Assert.Equal(expected: (motion.Gyro.Y / 3000f), actual: (motion.Gyro.Z / 2000f), tolerance: 1e-6f);

        // A repeated packet carries no new sample, so the pose does not move.
        Assert.True(condition: parser.TryParse(
            report: SteamState(packet: 4, buttons: 0, shared: (0, 0), accelerometer: (0, 0, 16384), gyro: (30000, 0, 0)),
            state: out var repeated
        ));
        Assert.Equal(expected: motion.Orientation, actual: repeated.Orientation);
    }
    [Fact]
    public void Steam_classifies_receiver_events_and_reports_wireless_battery() {
        using var hid = new TestHidDevice();
        var parser = new SteamController(device: hid);

        Assert.Equal(expected: WirelessSlotEvent.Connected, actual: parser.ClassifySlotEvent(report: [0x01, 0x00, 0x03, 0x01, 0x02,]));
        Assert.Equal(expected: WirelessSlotEvent.Connected, actual: parser.ClassifySlotEvent(report: [0x01, 0x00, 0x03, 0x01, 0x03,]));
        Assert.Equal(expected: WirelessSlotEvent.Disconnected, actual: parser.ClassifySlotEvent(report: [0x01, 0x00, 0x03, 0x01, 0x01,]));
        Assert.Equal(expected: WirelessSlotEvent.None, actual: parser.ClassifySlotEvent(report: [0x01, 0x00, 0x03, 0x00, 0x02,]));

        var status = new byte[64];

        status[0] = 0x01;
        status[2] = 0x04;
        status[14] = 77;

        Assert.False(condition: parser.TryParse(report: status, state: out _));
        Assert.True(condition: parser.TryParse(report: SteamState(packet: 1, buttons: 0x80, shared: (0, 0)), state: out _));
        Assert.True(condition: parser.TryParse(report: status, state: out var state));
        Assert.Equal(expected: new GamepadPower(State: GamepadPowerState.OnBattery, Percent: 77), actual: state.Power);
        Assert.Equal(expected: GamepadButtons.ButtonSouth, actual: state.Buttons);
    }
    [Fact]
    public async Task Steam_initialization_enables_raw_imu_and_rumble_sets_pulse_duty_per_pad() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new SteamController(device: hid);

        await parser.InitializeAsync(playerIndex: 0, cancellationToken: cancellationToken);
        await parser.SetRumbleAsync(lowFrequency: 0.5f, highFrequency: 0f, cancellationToken: cancellationToken);

        var settings = hid.FeatureWrites[1];

        Assert.Equal(expected: 0x87, actual: settings[1]);
        Assert.Equal(expected: [48, 0x18, 0x00,], actual: settings[15..18]);

        var left = hid.FeatureWrites[^2];
        var right = hid.FeatureWrites[^1];

        Assert.Equal(expected: [0x8F, 8, 1,], actual: left[1..4]);
        Assert.Equal(expected: 650, actual: BinaryPrimitives.ReadUInt16LittleEndian(source: left.AsSpan(start: 4)));
        Assert.Equal(expected: 650, actual: BinaryPrimitives.ReadUInt16LittleEndian(source: left.AsSpan(start: 6)));
        Assert.Equal(expected: 47, actual: BinaryPrimitives.ReadUInt16LittleEndian(source: left.AsSpan(start: 8)));
        Assert.Equal(expected: 0, actual: BinaryPrimitives.ReadUInt16LittleEndian(source: right.AsSpan(start: 8)));
    }
    [Fact]
    public void Triton_reads_pad_pressure_in_both_layouts_and_reports_battery_and_pairing() {
        using var hid = new TestHidDevice();
        using var parser = new SteamControllerTriton(device: hid);
        const uint RightPadTouch = (1u << 21);

        Assert.False(condition: parser.TryParse(report: [0x43, 0x02, 60,], state: out _));
        Assert.True(condition: parser.TryParse(report: TritonState(id: 0x42, buttons: RightPadTouch, rightPressure: 16384), state: out var state));
        Assert.Equal(expected: 0.5f, actual: state.Touch0.Pressure);
        Assert.Equal(expected: 0.75f, actual: state.Touch0.Position.X);
        Assert.True(condition: parser.TryParse(report: TritonState(id: 0x47, buttons: RightPadTouch, padShift: 2, rightPressure: 32768), state: out var shifted));
        Assert.Equal(expected: 1f, actual: shifted.Touch0.Pressure);
        Assert.Equal(expected: 0.75f, actual: shifted.Touch0.Position.X);
        Assert.True(condition: parser.TryParse(report: [0x43, 0x02, 60,], state: out var charging));
        Assert.Equal(expected: new GamepadPower(State: GamepadPowerState.Charging, Percent: 60), actual: charging.Power);

        Assert.Equal(expected: WirelessSlotEvent.Disconnected, actual: parser.ClassifySlotEvent(report: [0x79, 0x01,]));
        Assert.Equal(expected: WirelessSlotEvent.Connected, actual: parser.ClassifySlotEvent(report: [0x79, 0x03,]));
        Assert.Equal(expected: WirelessSlotEvent.Connected, actual: parser.ClassifySlotEvent(report: [0x46, 0x02,]));
        Assert.Equal(expected: WirelessSlotEvent.None, actual: parser.ClassifySlotEvent(report: [0x79, 0x09,]));
    }
    [Fact]
    public async Task Triton_reasserts_lizard_off_every_three_seconds() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        using var parser = new SteamControllerTriton(device: hid);

        await parser.ServiceAsync(timestamp: 10L, timestampFrequency: 1L, cancellationToken: cancellationToken);
        await parser.ServiceAsync(timestamp: 12L, timestampFrequency: 1L, cancellationToken: cancellationToken);
        Assert.Empty(collection: hid.FeatureWrites);

        await parser.ServiceAsync(timestamp: 13L, timestampFrequency: 1L, cancellationToken: cancellationToken);
        Assert.Equal(expected: [0x01, 0x87, 3, 9, 0, 0,], actual: Assert.Single(collection: hid.FeatureWrites)[..6]);
    }
    [Fact]
    public void Xbox_identity_and_battery_map_to_the_family_and_power_vocabulary() {
        Assert.Equal(expected: GamepadType.XboxSeries, actual: XInputGamepadConnection.ClassifyType(vendorId: 0x045E, productId: 0x0B12));
        Assert.Equal(expected: GamepadType.XboxOne, actual: XInputGamepadConnection.ClassifyType(vendorId: 0x045E, productId: 0x0B00));
        Assert.Equal(expected: GamepadType.XboxOne, actual: XInputGamepadConnection.ClassifyType(vendorId: 0x0E6F, productId: 0x0B12));
        Assert.Equal(
            expected: new GamepadPower(State: GamepadPowerState.NoBattery, Percent: 0),
            actual: XInputGamepadConnection.ToPower(information: new XInputBatteryInformation { BatteryType = XInput.BatteryTypeWired, })
        );
        Assert.Equal(
            expected: new GamepadPower(State: GamepadPowerState.OnBattery, Percent: 70),
            actual: XInputGamepadConnection.ToPower(information: new XInputBatteryInformation { BatteryLevel = 2, BatteryType = XInput.BatteryTypeAlkaline, })
        );
        Assert.Equal(
            expected: GamepadPower.Unknown,
            actual: XInputGamepadConnection.ToPower(information: new XInputBatteryInformation { BatteryType = 0xFF, })
        );
    }
    [Fact]
    public void Xbox_feeds_a_state_once_per_packet_and_stamps_its_arrival() {
        var clock = new ManualInputClock { NowTicks = 42UL, };
        using var connection = new XInputGamepadConnection(
            clock: clock,
            deviceId: Puck.Commands.InputDeviceId.New(),
            haptics: null,
            playerIndex: 0,
            slot: 0u,
            type: GamepadType.XboxSeries
        );
        var state = new XInputState { PacketNumber = 7, Gamepad = new XInputGamepad { Buttons = 0x1000, }, };

        connection.Apply(state: in state);
        clock.NowTicks = 43UL;
        connection.Apply(state: in state);

        Assert.Equal(expected: 42UL, actual: connection.Coalescer.Latest.ArrivalTicks);
        Assert.Equal(expected: 1UL, actual: connection.Coalescer.Latest.SequenceNumber);

        connection.ApplyBattery(information: new XInputBatteryInformation { BatteryLevel = 3, BatteryType = XInput.BatteryTypeNickelMetalHydride, });
        connection.Apply(state: in state);

        Assert.Equal(expected: new GamepadPower(State: GamepadPowerState.OnBattery, Percent: 100), actual: connection.Coalescer.Latest.Power);
        Assert.Equal(expected: GamepadButtons.ButtonSouth, actual: connection.Coalescer.Latest.Buttons);
    }
}
