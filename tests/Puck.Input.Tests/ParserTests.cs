using Puck.Input.Devices;
using Puck.Input.Hid;

namespace Puck.Input.Tests;

public sealed class ParserTests {
    [Fact]
    public void DualSense_rejects_an_empty_report_without_throwing() {
        using var hid = new TestHidDevice();
        var parser = new DualSenseController(device: hid);

        Assert.False(condition: parser.TryParse(report: [], state: out _));
    }

    [Theory]
    [InlineData(HidTransport.Usb, 48)]
    [InlineData(HidTransport.Bluetooth, 78)]
    public async Task DualSense_output_uses_the_protocol_minimum_when_HID_reports_a_short_length(HidTransport transport, int expectedLength) {
        using var hid = new TestHidDevice {
            OutputReportByteLength = 1,
            Transport = transport,
        };
        var parser = new DualSenseController(device: hid);

        await parser.SetRumbleAsync(lowFrequency: 1f, highFrequency: 1f, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(collection: hid.Writes);
        Assert.Equal(expected: expectedLength, actual: hid.Writes[0].Length);
    }

    [Fact]
    public void Switch_accepts_the_exact_minimum_standard_report_length() {
        using var hid = new TestHidDevice();
        var parser = new NintendoSwitchController(device: hid);
        var report = new byte[49];

        report[0] = 0x30;

        Assert.True(condition: parser.TryParse(report: report, state: out _));
    }

    [Fact]
    public async Task Switch_rumble_uses_the_protocol_minimum_when_HID_reports_a_short_length() {
        using var hid = new TestHidDevice { OutputReportByteLength = 1, };
        var parser = new NintendoSwitchController(device: hid);

        await parser.SetRumbleAsync(lowFrequency: 1f, highFrequency: 1f, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(collection: hid.Writes);
        Assert.Equal(expected: 64, actual: hid.Writes[0].Length);
    }

    [Fact]
    public async Task Triton_rumble_uses_the_protocol_minimum_when_HID_reports_a_short_length() {
        using var hid = new TestHidDevice { OutputReportByteLength = 1, FeatureReportByteLength = 1, };
        using var parser = new SteamControllerTriton(device: hid);

        await parser.SetRumbleAsync(lowFrequency: 1f, highFrequency: 1f, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(collection: hid.Writes);
        Assert.Equal(expected: 10, actual: hid.Writes[0].Length);
    }
}
