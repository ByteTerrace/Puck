using System.Buffers.Binary;
using System.Runtime.Versioning;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace Puck.Platform.Windows;

/// <summary>
/// Owns the one driver-declared color/infrared graph used by both dual-camera publication tiers. The graph preserves
/// the Face Authentication Profile V2 native formats: callers choose only whether the frame server should prefer CPU
/// or GPU memory, then consume the two realtime readers without replacing either reader's output type.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class Win32FaceAuthenticationCapture : IDisposable {
    // KSCAMERAPROFILE_FaceAuth_Mode from ksmedia.h. KnownVideoProfile does not expose it, so the projection identifies
    // it through FindAllVideoProfiles and the profile's string id.
    private const string FaceAuthenticationProfileId = "81361B22-700B-4546-A2D4-C52E907BFC27";
    private const uint KsPropertyCameraControlExtendedFaceAuthenticationMode = 35;
    private const ulong FaceAuthenticationAlternativeFrameIllumination = 0x2;
    private const ulong FaceAuthenticationBackgroundSubtraction = 0x4;

    private static readonly Guid ExtendedCameraControlPropertySet = new(g: "1CB79112-C0D2-4213-9CA6-CD4FDB927972");

    private readonly MediaCapture m_capture;

    private int m_disposed;

    private Win32FaceAuthenticationCapture(
        MediaCapture capture,
        Win32FaceAuthenticationStream color,
        Win32FaceAuthenticationStream infrared,
        string name,
        Win32CameraControlSurface controls
    ) {
        m_capture = capture;
        Color = color;
        Controls = controls;
        Infrared = infrared;
        Name = name;
    }

    public Win32FaceAuthenticationStream Color { get; }
    public Win32CameraControlSurface Controls { get; }
    public Win32FaceAuthenticationStream Infrared { get; }
    public string Name { get; }

    /// <summary>Opens and starts the given physical camera's public color/infrared Face Authentication Profile V2 pair.</summary>
    /// <param name="memoryPreference">Whether the frame server should place frames in system or GPU memory.</param>
    /// <param name="deviceId">The physical camera to open, from <see cref="Win32CameraDeviceGroups.Enumerate"/>.</param>
    public static Win32FaceAuthenticationCapture Open(MediaCaptureMemoryPreference memoryPreference, string deviceId) {
        MediaCapture? capture = null;
        MediaFrameReader? colorReader = null;
        MediaFrameReader? infraredReader = null;

        try {
            var selection = FindPair(deviceId: deviceId);

            capture = new MediaCapture();
            capture.InitializeAsync(mediaCaptureInitializationSettings: new MediaCaptureInitializationSettings {
                MemoryPreference = memoryPreference,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                SourceGroup = selection.Group,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                VideoProfile = selection.Profile,
            }).AsTask().GetAwaiter().GetResult();

            var colorSource = capture.FrameSources[selection.Color.Id];
            var infraredSource = capture.FrameSources[selection.Infrared.Id];

            // Preserve the profile's native pair. Asking either reader for BGRA changes the admitted topology and
            // makes cameras such as the BRIO reject the infrared reader as OutputFormatNotSupported.
            var captureMode = ConfigureFaceAuthentication(controller: infraredSource.Controller);
            var color = Describe(capture: capture, mode: captureMode, reader: out colorReader, source: colorSource);
            var infrared = Describe(capture: capture, mode: captureMode, reader: out infraredReader, source: infraredSource);

            var starts = Task.WhenAll(
                colorReader.StartAsync().AsTask(),
                infraredReader.StartAsync().AsTask()
            ).GetAwaiter().GetResult();

            if (
                (MediaFrameReaderStartStatus.Success != starts[0]) ||
                (MediaFrameReaderStartStatus.Success != starts[1])
            ) {
                throw new InvalidOperationException(message: $"the native color/infrared readers refused to start (color={starts[0]}, infrared={starts[1]})");
            }

            var result = new Win32FaceAuthenticationCapture(
                capture: capture,
                color: color,
                controls: new Win32CameraControlSurface(mediaSource: capture.VideoDeviceController),
                infrared: infrared,
                name: selection.Group.DisplayName
            );

            capture = null;
            colorReader = null;
            infraredReader = null;

            return result;
        } catch {
            StopAndDispose(reader: colorReader);
            StopAndDispose(reader: infraredReader);
            DisposeCapture(capture: capture);

            throw;
        }
    }
    public void Dispose() {
        if (0 != Interlocked.Exchange(location1: ref m_disposed, value: 1)) {
            return;
        }

        StopAndDispose(reader: Color.Reader);
        StopAndDispose(reader: Infrared.Reader);
        DisposeCapture(capture: m_capture);
    }

    private static Win32FaceAuthenticationStream Describe(MediaCapture capture, MediaFrameSource source, string mode, out MediaFrameReader reader) {
        var format = source.CurrentFormat;
        var width = checked((int)format.VideoFormat.Width);
        var height = checked((int)format.VideoFormat.Height);

        if ((width <= 0) || (height <= 0)) {
            throw new InvalidOperationException(message: $"the {source.Info.SourceKind} stream reported an invalid frame size ({width}x{height})");
        }

        reader = capture.CreateFrameReaderAsync(inputSource: source).AsTask().GetAwaiter().GetResult();
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

        return new Win32FaceAuthenticationStream(
            CaptureFormat: new CameraCaptureFormat(
                Mode: mode,
                RateHz: FrameRateHz(format: format),
                Subtype: format.Subtype
            ),
            Colorimetry: Win32CameraColorimetry.From(format: format),
            Description: DescribeFormat(format: format),
            Height: height,
            Reader: reader,
            Width: width
        );
    }
    private static string DescribeFormat(MediaFrameFormat format) => $"{format.VideoFormat.Width}x{format.VideoFormat.Height}@{FrameRateHz(format: format):0.###} {format.Subtype}";
    private static double FrameRateHz(MediaFrameFormat format) {
        var denominator = format.FrameRate.Denominator;

        return ((0 == denominator)
            ? 0.0
            : (((double)format.FrameRate.Numerator) / denominator)
        );
    }
    private static Win32FaceAuthenticationSelection FindPair(string deviceId) {
        if (!Win32CameraDeviceGroups.TryFind(color: out var color, deviceId: deviceId, group: out var group, infrared: out var infrared)) {
            throw new NotSupportedException(message: $"camera device '{deviceId}' is no longer attached");
        }

        if ((color is null) || (infrared is null)) {
            throw new NotSupportedException(message: $"camera device '{deviceId}' carries no color/infrared pair");
        }

        var profile = (
            FindProfile(videoDeviceId: color.DeviceInformation.Id) ??
            (FindProfile(videoDeviceId: infrared.DeviceInformation.Id) ??
            FindProfile(videoDeviceId: group.Id))
        );

        if (profile is { } match) {
            return new Win32FaceAuthenticationSelection(
                Color: match.Color,
                Group: group,
                Infrared: match.Infrared,
                Profile: match.Profile
            );
        }

        throw new NotSupportedException(message: $"camera device '{deviceId}' publishes no Face Authentication Profile V2 color/infrared pair");
    }
    private static Win32FaceAuthenticationProfile? FindProfile(string videoDeviceId) {
        try {
            if (!MediaCapture.IsVideoProfileSupported(videoDeviceId: videoDeviceId)) {
                return null;
            }

            foreach (var profile in MediaCapture.FindAllVideoProfiles(videoDeviceId: videoDeviceId)) {
                if (!profile.Id.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: FaceAuthenticationProfileId)) {
                    continue;
                }

                MediaFrameSourceInfo? color = null;
                MediaFrameSourceInfo? infrared = null;

                foreach (var source in profile.FrameSourceInfos) {
                    if (MediaFrameSourceKind.Color == source.SourceKind) {
                        color ??= source;
                    } else if (MediaFrameSourceKind.Infrared == source.SourceKind) {
                        infrared ??= source;
                    }
                }

                if ((color is not null) && (infrared is not null)) {
                    return new Win32FaceAuthenticationProfile(
                        Color: color,
                        Infrared: infrared,
                        Profile: profile
                    );
                }
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] could not inspect video profiles for '{videoDeviceId}': {exception.Message}");
        }

        return null;
    }
    private static string ConfigureFaceAuthentication(MediaFrameSourceController controller) {
        const string ProfileName = "Windows Face Authentication Profile V2";

        var property = new byte[24];

        _ = ExtendedCameraControlPropertySet.TryWriteBytes(destination: property);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property.AsSpan(start: 16), value: KsPropertyCameraControlExtendedFaceAuthenticationMode);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property.AsSpan(start: 20), value: 1u); // KSPROPERTY_TYPE_GET

        var get = controller.GetPropertyByExtendedIdAsync(extendedPropertyId: property, maxPropertyValueSize: 128u).AsTask().GetAwaiter().GetResult();

        if (
            (MediaFrameSourceGetPropertyStatus.Success != get.Status) ||
            (get.Value is not byte[] payload) ||
            (payload.Length < 32)
        ) {
            Console.Out.WriteLine(value: $"[camera] face-authentication mode: unavailable ({get.Status}).");

            return ProfileName;
        }

        var capability = BinaryPrimitives.ReadUInt64LittleEndian(source: payload.AsSpan(start: 24));
        var mode = (((capability & FaceAuthenticationAlternativeFrameIllumination) != 0)
            ? FaceAuthenticationAlternativeFrameIllumination
            : (((capability & FaceAuthenticationBackgroundSubtraction) != 0)
                ? FaceAuthenticationBackgroundSubtraction
                : 0UL
            )
        );

        if (0 == mode) {
            Console.Out.WriteLine(value: $"[camera] face-authentication mode: unsupported capabilities 0x{capability:X}.");

            return ProfileName;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination: payload.AsSpan(start: 16), value: mode);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property.AsSpan(start: 20), value: 2u); // KSPROPERTY_TYPE_SET

        var status = controller.SetPropertyByExtendedIdAsync(extendedPropertyId: property, propertyValue: payload).AsTask().GetAwaiter().GetResult();
        var modeName = ((FaceAuthenticationAlternativeFrameIllumination == mode) ? "alternating-frame illumination" : "background subtraction");

        Console.Out.WriteLine(value: $"[camera] face-authentication mode: {modeName} ({status}).");

        return ((MediaFrameSourceSetPropertyStatus.Success == status)
            ? $"{ProfileName}, {modeName}"
            : ProfileName
        );
    }
    private static void StopAndDispose(MediaFrameReader? reader) {
        if (reader is null) {
            return;
        }

        try {
            reader.StopAsync().AsTask().GetAwaiter().GetResult();
        } catch {
            // Device removal means the reader is already stopped.
        }

        try {
            reader.Dispose();
        } catch {
            // A disconnected frame server can invalidate the projected object before managed cleanup reaches it.
        }
    }
    private static void DisposeCapture(MediaCapture? capture) {
        if (capture is null) {
            return;
        }

        try {
            capture.Dispose();
        } catch {
            // Device loss can invalidate the capture while its readers are stopping; cleanup remains complete.
        }
    }

    private readonly record struct Win32FaceAuthenticationSelection(
        MediaFrameSourceInfo Color,
        MediaFrameSourceGroup Group,
        MediaFrameSourceInfo Infrared,
        MediaCaptureVideoProfile Profile
    );
    private readonly record struct Win32FaceAuthenticationProfile(
        MediaFrameSourceInfo Color,
        MediaFrameSourceInfo Infrared,
        MediaCaptureVideoProfile Profile
    );
}
/// <summary>One native stream in a coordinated Face Authentication capture.</summary>
internal readonly record struct Win32FaceAuthenticationStream(
    CameraCaptureFormat CaptureFormat,
    Win32CameraColorimetry Colorimetry,
    string Description,
    int Height,
    MediaFrameReader Reader,
    int Width
);
/// <summary>
/// The <see cref="MediaFrameSourceGroup"/> scan every camera path resolves a physical device through — one group is
/// one physical camera, and its color/infrared <see cref="MediaFrameSourceInfo"/> entries carry the
/// <c>DeviceInformation.Id</c> symbolic links the single-sensor source-reader path selects a device by
/// (<see cref="MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK"/>).
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
internal static class Win32CameraDeviceGroups {
    private static Win32CameraDeviceGroup[] s_groups = [];

    /// <summary>Enumerates every attached physical camera.</summary>
    public static IReadOnlyList<CameraDeviceInfo> Enumerate() {
        var groups = Scan();

        Volatile.Write(location: ref s_groups, value: groups);

        var devices = new CameraDeviceInfo[groups.Length];

        for (var index = 0; (index < groups.Length); index++) {
            devices[index] = groups[index].Info;
        }

        return devices;
    }
    /// <summary>Finds the attached group matching <paramref name="deviceId"/> and its color/infrared source infos.</summary>
    public static bool TryFind(string deviceId, out MediaFrameSourceGroup group, out MediaFrameSourceInfo? color, out MediaFrameSourceInfo? infrared) {
        var groups = Volatile.Read(location: ref s_groups);

        if (TryFind(color: out color, deviceId: deviceId, group: out group, groups: groups, infrared: out infrared)) {
            return true;
        }

        groups = Scan();
        Volatile.Write(location: ref s_groups, value: groups);

        return TryFind(color: out color, deviceId: deviceId, group: out group, groups: groups, infrared: out infrared);
    }

    private static Win32CameraDeviceGroup[] Scan() {
        var groups = new List<Win32CameraDeviceGroup>();

        foreach (var group in MediaFrameSourceGroup.FindAllAsync().AsTask().GetAwaiter().GetResult()) {
            var (color, infrared) = Sources(group: group);

            if ((color is null) && (infrared is null)) {
                continue;
            }

            var sensors = new List<CameraSensor>(capacity: 2);

            if (color is not null) {
                sensors.Add(item: CameraSensor.Color);
            }

            if (infrared is not null) {
                sensors.Add(item: CameraSensor.Infrared);
            }

            groups.Add(item: new Win32CameraDeviceGroup(
                Color: color,
                Group: group,
                Info: new CameraDeviceInfo(Id: group.Id, Name: group.DisplayName, Sensors: sensors),
                Infrared: infrared
            ));
        }

        groups.Sort(comparison: static (left, right) => StringComparer.Ordinal.Compare(x: left.Info.Id, y: right.Info.Id));

        return [.. groups];
    }
    private static bool TryFind(Win32CameraDeviceGroup[] groups, string deviceId, out MediaFrameSourceGroup group, out MediaFrameSourceInfo? color, out MediaFrameSourceInfo? infrared) {
        foreach (var candidate in groups) {
            if (!string.Equals(a: candidate.Info.Id, b: deviceId, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            color = candidate.Color;
            group = candidate.Group;
            infrared = candidate.Infrared;

            return true;
        }

        group = null!;
        color = null;
        infrared = null;

        return false;
    }
    private static (MediaFrameSourceInfo? Color, MediaFrameSourceInfo? Infrared) Sources(MediaFrameSourceGroup group) {
        MediaFrameSourceInfo? color = null;
        MediaFrameSourceInfo? infrared = null;

        foreach (var info in group.SourceInfos) {
            if (
                (MediaStreamType.VideoRecord != info.MediaStreamType) &&
                (MediaStreamType.VideoPreview != info.MediaStreamType)
            ) {
                continue;
            }

            if (MediaFrameSourceKind.Color == info.SourceKind) {
                color ??= info;
            } else if (MediaFrameSourceKind.Infrared == info.SourceKind) {
                infrared ??= info;
            }
        }

        return (color, infrared);
    }

    private readonly record struct Win32CameraDeviceGroup(
        MediaFrameSourceInfo? Color,
        MediaFrameSourceGroup Group,
        CameraDeviceInfo Info,
        MediaFrameSourceInfo? Infrared
    );
}
