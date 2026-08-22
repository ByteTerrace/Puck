using System.Runtime.Versioning;
using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>Exercises real attached hardware: enumeration and the pixel-tier open ladder against whatever cameras are
/// physically connected. Skips rather than fails when a machine carries no color camera.</summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class Win32MediaFoundationCameraServiceTests {
    [Fact]
    public void Enumerate_devices_reports_a_color_camera() {
        var devices = new Win32MediaFoundationCameraService().EnumerateDevices();

        if (!TryFindColorDevice(devices: devices, device: out var device)) {
            Assert.Skip(reason: "no color camera is attached to this machine.");

            return;
        }

        Assert.False(condition: string.IsNullOrEmpty(value: device.Id));
        Assert.False(condition: string.IsNullOrEmpty(value: device.Name));
        Assert.Contains(expected: CameraSensor.Color, collection: device.Sensors);
    }
    [Fact]
    public void Opening_a_color_device_by_id_on_the_pixel_tier_delivers_a_frame() {
        var service = new Win32MediaFoundationCameraService();
        var devices = service.EnumerateDevices();

        if (!TryFindColorDevice(devices: devices, device: out var device)) {
            Assert.Skip(reason: "no color camera is attached to this machine.");

            return;
        }

        ReadOnlySpan<CameraStreamRequest> streams = [new CameraStreamRequest(Sensor: CameraSensor.Color, Width: 320, Height: 240, RateHz: 0)];

        Assert.True(condition: service.TryOpenPixels(deviceId: device.Id, streams: streams, graph: out var graph), userMessage: $"'{device.Name}' refused to open on the pixel tier.");

        using (graph) {
            Assert.Equal(expected: device.Id, actual: graph!.DeviceId);

            var stream = Assert.Single(collection: graph.Streams);
            var deadline = DateTime.UtcNow.AddSeconds(value: 10);
            var delivered = false;

            while (!delivered && (DateTime.UtcNow < deadline)) {
                if (stream.TryCapture(surface: out var surface)) {
                    Assert.True(condition: (surface.Width > 0) && (surface.Height > 0));

                    delivered = true;
                } else {
                    Thread.Sleep(millisecondsTimeout: 20);
                }
            }

            Assert.True(condition: delivered, userMessage: $"'{device.Name}' delivered no frame within 10 seconds.");
        }
    }

    private static bool TryFindColorDevice(IReadOnlyList<CameraDeviceInfo> devices, out CameraDeviceInfo device) {
        foreach (var candidate in devices) {
            foreach (var sensor in candidate.Sensors) {
                if (CameraSensor.Color == sensor) {
                    device = candidate;

                    return true;
                }
            }
        }

        device = default;

        return false;
    }
}
