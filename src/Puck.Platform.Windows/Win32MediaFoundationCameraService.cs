using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace Puck.Platform.Windows;

/// <summary>
/// The Windows <see cref="ICameraCaptureService"/>. One sensor opens through a Media Foundation source reader; the
/// color/infrared pair opens through the camera frame server's Face Authentication Profile V2 graph. Each shape has a
/// CPU-pixel tier and a shared-texture tier. Any failed open is reported as "not opened" so the caller can drop a
/// sensor or change tier.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32MediaFoundationCameraService : ICameraCaptureService {
    /// <inheritdoc/>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc/>
    public IReadOnlyList<CameraDeviceInfo> EnumerateDevices() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)) {
            return [];
        }

        try {
            return Win32CameraDeviceGroups.Enumerate();
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] device enumeration failed: {exception.Message}");

            return [];
        }
    }
    /// <inheritdoc/>
    public bool TryOpenPixels(string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraPixelStream>? graph) {
        ArgumentNullException.ThrowIfNull(deviceId);

        graph = null;

        try {
            if (streams is [var single]) {
                graph = new Win32SourceReaderPixelGraph(deviceId: deviceId, request: single);
            } else if (IsFaceAuthenticationPair(streams: streams) && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) {
                graph = new Win32FaceAuthenticationPixelGraph(deviceId: deviceId, requests: streams);
            } else {
                throw new NotSupportedException(message: Unsupported(streams: streams));
            }

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] CPU-tier open failed: {exception.Message}");

            return false;
        }
    }
    /// <inheritdoc/>
    public bool TryOpenShared(long adapterLuid, string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraSharedStream>? graph) {
        ArgumentNullException.ThrowIfNull(deviceId);

        graph = null;

        try {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
                return false;
            }

            if (streams is [var single]) {
                graph = new Win32SourceReaderSharedGraph(adapterLuid: adapterLuid, deviceId: deviceId, request: single);
            } else if (IsFaceAuthenticationPair(streams: streams) && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) {
                graph = new Win32FaceAuthenticationSharedGraph(adapterLuid: adapterLuid, deviceId: deviceId, requests: streams);
            } else {
                throw new NotSupportedException(message: Unsupported(streams: streams));
            }

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] GPU-tier open failed: {exception.Message}");

            return false;
        }
    }

    // The one coordinated shape the platform admits: a color pin and an infrared pin, in either order.
    private static bool IsFaceAuthenticationPair(ReadOnlySpan<CameraStreamRequest> streams) => (
        (2 == streams.Length) &&
        (streams[0].Sensor != streams[1].Sensor) &&
        (streams[0].Sensor is CameraSensor.Color or CameraSensor.Infrared) &&
        (streams[1].Sensor is CameraSensor.Color or CameraSensor.Infrared)
    );
    private static string Unsupported(ReadOnlySpan<CameraStreamRequest> streams) {
        var sensors = new string[streams.Length];

        for (var index = 0; (index < streams.Length); index++) {
            sensors[index] = streams[index].Sensor.ToString();
        }

        return $"no Windows camera graph carries the sensor set [{string.Join(separator: ", ", value: sensors)}]";
    }
}
