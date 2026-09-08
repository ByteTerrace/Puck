using System.Runtime.Versioning;
using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>Exercises real attached hardware: enumeration and pixel-tier frame delivery run whenever a color camera
/// is present, and skip only when the machine has no such device.</summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class Win32MediaFoundationCameraServiceTests {
    [Fact]
    public void Enumerate_devices_reports_a_color_camera() {
        var devices = new Win32MediaFoundationCameraService().EnumerateDevices();

        if (!TryFindColorDevice(device: out var device, devices: devices)) {
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

        if (!TryFindColorDevice(device: out var device, devices: devices)) {
            Assert.Skip(reason: "no color camera is attached to this machine.");

            return;
        }

        ReadOnlySpan<CameraStreamRequest> streams = [new CameraStreamRequest(Height: 240, RateHz: 0, Sensor: CameraSensor.Color, Width: 320)];

        Assert.True(condition: service.TryOpenPixels(deviceId: device.Id, streams: streams, graph: out var graph), userMessage: $"'{device.Name}' refused to open on the pixel tier.");

        using (graph) {
            Assert.Equal(expected: device.Id, actual: graph!.DeviceId);

            var stream = Assert.Single(collection: graph.Streams);

            Assert.True(
                condition: stream.TryCapture(surface: out var surface),
                userMessage: $"'{device.Name}' opened without a published {stream.NativeFormat.Subtype} {stream.Width}x{stream.Height} frame at {stream.NativeFormat.RateHz:F2} Hz."
            );
            Assert.Equal(expected: ((uint)stream.Width), actual: surface.Width);
            Assert.Equal(expected: ((uint)stream.Height), actual: surface.Height);
            Assert.True(condition: (surface.Pixels.Length >= checked(((stream.Width * stream.Height) * 4))));
        }
    }
    [Fact]
    public void Opening_every_enumerated_color_device_on_the_pixel_tier_delivers_a_frame() {
        var service = new Win32MediaFoundationCameraService();
        var devices = service.EnumerateDevices();
        var colorDevices = new List<CameraDeviceInfo>();

        foreach (var candidate in devices) {
            foreach (var sensor in candidate.Sensors) {
                if (CameraSensor.Color == sensor) {
                    colorDevices.Add(item: candidate);

                    break;
                }
            }
        }

        if (0 == colorDevices.Count) {
            Assert.Skip(reason: "no color camera is attached to this machine.");

            return;
        }

        var openedNames = new List<string>();

        foreach (var device in colorDevices) {
            ReadOnlySpan<CameraStreamRequest> streams = [new CameraStreamRequest(Height: 240, RateHz: 0, Sensor: CameraSensor.Color, Width: 320)];

            Assert.True(condition: service.TryOpenPixels(deviceId: device.Id, streams: streams, graph: out var graph), userMessage: $"'{device.Name}' refused to open on the pixel tier.");

            using (graph) {
                Assert.Equal(expected: device.Id, actual: graph!.DeviceId);
                // graph.DeviceId echoes the request; graph.Name is read back from the activated Media Foundation
                // source, so this is the only assertion that proves the requested device (not the driver's default)
                // actually opened.
                Assert.Equal(expected: device.Name, actual: graph.Name);
                openedNames.Add(item: graph.Name);

                var stream = Assert.Single(collection: graph.Streams);

                Assert.True(
                    condition: stream.TryCapture(surface: out var surface),
                    userMessage: $"'{device.Name}' opened without a published {stream.NativeFormat.Subtype} {stream.Width}x{stream.Height} frame at {stream.NativeFormat.RateHz:F2} Hz."
                );
                Assert.Equal(expected: ((uint)stream.Width), actual: surface.Width);
                Assert.Equal(expected: ((uint)stream.Height), actual: surface.Height);
                Assert.True(condition: (surface.Pixels.Length >= checked(((stream.Width * stream.Height) * 4))));
            }
        }

        if (openedNames.Count > 1) {
            Assert.Equal(expected: openedNames.Count, actual: openedNames.Distinct().Count());
        }
    }
    [Fact]
    public void Opening_an_unknown_device_id_refuses_cleanly_and_does_not_leak_the_real_device() {
        var service = new Win32MediaFoundationCameraService();
        var devices = service.EnumerateDevices();

        if (!TryFindColorDevice(device: out var device, devices: devices)) {
            Assert.Skip(reason: "no color camera is attached to this machine.");

            return;
        }

        ReadOnlySpan<CameraStreamRequest> unknownDeviceStreams = [new CameraStreamRequest(Height: 240, RateHz: 0, Sensor: CameraSensor.Color, Width: 320)];

        Assert.False(condition: service.TryOpenPixels(deviceId: "not-an-enumerated-camera-id", graph: out var refusedGraph, streams: unknownDeviceStreams));
        Assert.Null(@object: refusedGraph);

        ReadOnlySpan<CameraStreamRequest> streams = [new CameraStreamRequest(Height: 240, RateHz: 0, Sensor: CameraSensor.Color, Width: 320)];

        Assert.True(
            condition: service.TryOpenPixels(deviceId: device.Id, streams: streams, graph: out var graph),
            userMessage: $"'{device.Name}' refused to open after an unknown device id was refused first."
        );

        using (graph) {
            var stream = Assert.Single(collection: graph!.Streams);

            Assert.True(condition: stream.TryCapture(surface: out _), userMessage: $"'{device.Name}' opened without a published frame.");
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
