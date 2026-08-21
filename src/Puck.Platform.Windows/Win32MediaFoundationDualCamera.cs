using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace Puck.Platform.Windows;

/// <summary>
/// The simultaneous dual-sensor camera core: ONE <see cref="MediaCapture"/> over the frame-source group that carries
/// both color and infrared, fanned out to two <see cref="ICameraCaptureSession"/> facades. A modern Face Authentication
/// Profile V2 declares the native media-type pair that the driver guarantees can run together, so the capture
/// initializes against that profile without overriding either format; a camera without the declaration is rejected
/// before any speculative two-pin open. Each pin then gets its OWN <see cref="MediaFrameReader"/> with polled
/// acquisition: the profile pairs unrelated cadences (24 Hz color against 60 Hz strobed infrared on the Surface), and
/// a <see cref="MultiSourceMediaFrameReader"/> over that same profile measured ZERO FrameArrived callbacks in five
/// seconds on that hardware — the event-driven correlated set never materializes in this hosting shape, and nothing
/// here needs correlation (each facade independently publishes its latest frame). Reader-start success alone is
/// insufficient, so construction verifies that both pins actually produce a frame.
/// The frame server stamps each infrared frame's
/// <see cref="InfraredMediaFrame.IsIlluminated"/>, making the lit-frame gate here EXACT rather than the single-sensor
/// session's luminance-envelope heuristic. The two facades share the core by refcount — the last disposal stops the
/// readers and the capture.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class Win32MediaFoundationDualCameraCore {
    // KSCAMERAPROFILE_FaceAuth_Mode from ksmedia.h. It is not represented by KnownVideoProfile, so the WinRT surface
    // exposes it only through FindAllVideoProfiles and its string profile id.
    private const string FaceAuthenticationProfileId = "81361B22-700B-4546-A2D4-C52E907BFC27";
    private const uint KsPropertyCameraControlExtendedFaceAuthenticationMode = 35;
    private const ulong FaceAuthenticationAlternativeFrameIllumination = 0x2;
    private const ulong FaceAuthenticationBackgroundSubtraction = 0x4;
    private const int FirstFrameTimeoutMilliseconds = 5000;
    private static readonly Guid ExtendedCameraControlPropertySet = new(g: "1CB79112-C0D2-4213-9CA6-CD4FDB927972");

    private readonly MediaCapture? m_capture;
    private readonly MediaFrameReader? m_colorReader;
    private readonly MediaFrameReader? m_infraredReader;

    private byte[] m_colorScratch = [];
    private volatile bool m_ended;
    private byte[] m_infraredScratch = [];
    private int m_referenceCount = 2;
    private int m_shutdown;
    private volatile bool m_stop;
    private readonly Thread? m_thread;

    public LatestFrameBuffer ColorFrames { get; } = new();
    public LatestFrameBuffer InfraredFrames { get; } = new();

    public Win32MediaFoundationDualCameraCore(int colorWidth, int colorHeight, uint colorRateHz, int infraredWidth, int infraredHeight, uint infraredRateHz) {
        try {
            var (group, colorInfo, infraredInfo) = FindDualGroup();
            var faceAuthenticationProfile = (
                FindFaceAuthenticationProfile(videoDeviceId: colorInfo.DeviceInformation.Id) ??
                FindFaceAuthenticationProfile(videoDeviceId: infraredInfo.DeviceInformation.Id) ??
                FindFaceAuthenticationProfile(videoDeviceId: group.Id)
            );

            if (faceAuthenticationProfile is null) {
                throw new NotSupportedException(message: "the camera does not publish a Face Authentication Profile V2 declaring a simultaneous color/infrared media-type pair");
            }

            Name = group.DisplayName;
            m_capture = new MediaCapture();
            m_capture.InitializeAsync(mediaCaptureInitializationSettings: new MediaCaptureInitializationSettings {
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                SourceGroup = group,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                VideoProfile = faceAuthenticationProfile,
            }).AsTask().GetAwaiter().GetResult();

            var colorSource = m_capture.FrameSources[colorInfo.Id];
            var infraredSource = m_capture.FrameSources[infraredInfo.Id];

            _ = ConfigureFaceAuthentication(controller: infraredSource.Controller);

            ColorWidth = ((int)colorSource.CurrentFormat.VideoFormat.Width);
            ColorHeight = ((int)colorSource.CurrentFormat.VideoFormat.Height);
            InfraredWidth = ((int)infraredSource.CurrentFormat.VideoFormat.Width);
            InfraredHeight = ((int)infraredSource.CurrentFormat.VideoFormat.Height);

            // One reader per pin, never a MultiSourceMediaFrameReader: the profile's pins run at unrelated cadences,
            // and the multi-source event path delivered zero correlated sets on the hardware this path exists for (see
            // the class remarks). The frame server converts both sensors to BGRA at the reader (infrared luminance
            // lands as gray BGRA), so both publish paths share one shape.
            m_colorReader = m_capture.CreateFrameReaderAsync(inputSource: colorSource, outputSubtype: MediaEncodingSubtypes.Bgra8).AsTask().GetAwaiter().GetResult();
            m_infraredReader = m_capture.CreateFrameReaderAsync(inputSource: infraredSource, outputSubtype: MediaEncodingSubtypes.Bgra8).AsTask().GetAwaiter().GetResult();
            m_colorReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            m_infraredReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

            if (MediaFrameReaderStartStatus.Success != m_colorReader.StartAsync().AsTask().GetAwaiter().GetResult()) {
                throw new InvalidOperationException(message: "the color frame reader refused to start");
            }

            if (MediaFrameReaderStartStatus.Success != m_infraredReader.StartAsync().AsTask().GetAwaiter().GetResult()) {
                throw new InvalidOperationException(message: "the infrared frame reader refused to start");
            }

            // The WinRT VideoDeviceController answers the classic control interfaces through COM interop, so the shared
            // control surface (UVC + vendor XU) rides the same wrapper the single-sensor sessions use.
            Controls = new Win32CameraControlSurface(mediaSource: m_capture.VideoDeviceController);

            // POLLED acquisition, not FrameArrived: the readers deliver frames to TryAcquireLatestFrame either way, and
            // the event path silently never fires in this hosting shape — a dedicated poll thread (the MF sessions'
            // grabber pattern) is deterministic everywhere. Timestamps dedupe re-acquired frames.
            m_thread = new Thread(start: PollLoop) {
                IsBackground = true,
                Name = "camera-dual-poll",
            };
            m_thread.SetApartmentState(state: ApartmentState.MTA);
            m_thread.Start();

            // StartAsync only reports that the driver accepted the topology. A multiplexing camera can accept both pins
            // but then deliver just one, so an open is not successful until BOTH streams prove liveness. Without this gate
            // the binder advertises a dual feed while one screen remains black forever.
            var deadline = Environment.TickCount64 + FirstFrameTimeoutMilliseconds;

            while (
                (ColorFrames.Version == 0L || InfraredFrames.Version == 0L) &&
                !m_ended &&
                (Environment.TickCount64 < deadline)
            ) {
                Thread.Sleep(millisecondsTimeout: 10);
            }

            if (ColorFrames.Version == 0L || InfraredFrames.Version == 0L) {
                throw new InvalidOperationException(
                    message: $"the device did not produce both streams within {FirstFrameTimeoutMilliseconds} ms (color={ColorFrames.Version} of {DescribeFormat(format: colorSource.CurrentFormat)}, infrared={InfraredFrames.Version} of {DescribeFormat(format: infraredSource.CurrentFormat)})"
                );
            }

            // Announced only once both pins have proved live, so a log never carries a confident negotiation line for
            // a graph that then failed the liveness gate.
            Console.Out.WriteLine(value: $"[camera] dual-sensor negotiation: Windows Face Authentication Profile V2 — color {DescribeFormat(format: colorSource.CurrentFormat)} + infrared {DescribeFormat(format: infraredSource.CurrentFormat)}.");
        } catch {
            var readersStarted = (m_thread is not null);

            Shutdown();

            // Some USB camera drivers finish switching modes after their WinRT objects have been released. Give that
            // asynchronous teardown a short bounded interval before the caller restores its established single-sensor
            // session; without it the immediate reopen can fail even though the device is healthy.
            if (readersStarted) {
                Thread.Sleep(millisecondsTimeout: 2000);
            }

            throw;
        }
    }

    public int ColorHeight { get; }
    public int ColorWidth { get; }
    public Win32CameraControlSurface? Controls { get; }
    public int InfraredHeight { get; }
    public int InfraredWidth { get; }
    public bool IsEnded => m_ended;
    public string Name { get; } = "camera";
    private static MediaCaptureVideoProfile? FindFaceAuthenticationProfile(string videoDeviceId) {
        if (!MediaCapture.IsVideoProfileSupported(videoDeviceId: videoDeviceId)) {
            return null;
        }

        foreach (var profile in MediaCapture.FindAllVideoProfiles(videoDeviceId: videoDeviceId)) {
            if (profile.Id.Contains(value: FaceAuthenticationProfileId, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return profile;
            }
        }

        return null;
    }

    private static string DescribeFormat(MediaFrameFormat format) {
        var denominator = format.FrameRate.Denominator;
        var rate = ((denominator == 0)
            ? 0.0
            : (((double)format.FrameRate.Numerator) / denominator)
        );

        return $"{format.VideoFormat.Width}x{format.VideoFormat.Height}@{rate:0.###} {format.Subtype}";
    }

    private static ulong ConfigureFaceAuthentication(MediaFrameSourceController controller) {
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

            return 0UL;
        }

        var capability = BinaryPrimitives.ReadUInt64LittleEndian(source: payload.AsSpan(start: 24));
        var mode = ((capability & FaceAuthenticationAlternativeFrameIllumination) != 0
            ? FaceAuthenticationAlternativeFrameIllumination
            : ((capability & FaceAuthenticationBackgroundSubtraction) != 0
                ? FaceAuthenticationBackgroundSubtraction
                : 0UL
            )
        );

        if (mode == 0) {
            Console.Out.WriteLine(value: $"[camera] face-authentication mode: unsupported capabilities 0x{capability:X}.");

            return 0UL;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination: payload.AsSpan(start: 16), value: mode);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: property.AsSpan(start: 20), value: 2u); // KSPROPERTY_TYPE_SET

        var status = controller.SetPropertyByExtendedIdAsync(extendedPropertyId: property, propertyValue: payload).AsTask().GetAwaiter().GetResult();

        Console.Out.WriteLine(value: $"[camera] face-authentication mode: {((mode == FaceAuthenticationAlternativeFrameIllumination) ? "alternating-frame illumination" : "background subtraction")} ({status}).");

        return ((MediaFrameSourceSetPropertyStatus.Success == status) ? mode : 0UL);
    }

    /// <summary>One facade released its half; the last release stops the readers and the capture.</summary>
    public void Release() {
        if (0 != Interlocked.Decrement(location: ref m_referenceCount)) {
            return;
        }

        Shutdown();
    }
    private void Shutdown() {
        if (0 != Interlocked.Exchange(ref m_shutdown, 1)) {
            return;
        }

        m_ended = true;
        m_stop = true;
        _ = (m_thread?.Join(millisecondsTimeout: 2000) ?? true);

        try {
            m_colorReader?.StopAsync().AsTask().GetAwaiter().GetResult();
            m_infraredReader?.StopAsync().AsTask().GetAwaiter().GetResult();
        } catch {
            // A capture torn down by device loss mid-stop is already stopped.
        }

        m_colorReader?.Dispose();
        m_infraredReader?.Dispose();
        m_capture?.Dispose();
    }

    private static (MediaFrameSourceGroup Group, MediaFrameSourceInfo Color, MediaFrameSourceInfo Infrared) FindDualGroup() {
        var groups = MediaFrameSourceGroup.FindAllAsync().AsTask().GetAwaiter().GetResult();

        foreach (var group in groups) {
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

            if ((color is not null) && (infrared is not null)) {
                return (group, color, infrared);
            }
        }

        throw new InvalidOperationException(message: "no capture device offers color and infrared sources in one group");
    }
    private void PollLoop() {
        var lastColorTime = TimeSpan.MinValue;
        var lastInfraredTime = TimeSpan.MinValue;

        try {
            while (!m_stop) {
                var progressed = false;

                using (var frame = m_colorReader!.TryAcquireLatestFrame()) {
                    if (
                        (frame?.VideoMediaFrame?.SoftwareBitmap is { } bitmap) &&
                        (frame.SystemRelativeTime is { } time) &&
                        (time != lastColorTime)
                    ) {
                        lastColorTime = time;
                        progressed = true;
                        PublishBitmap(bitmap: bitmap, buffer: ColorFrames, scratch: ref m_colorScratch);
                    }
                }

                using (var frame = m_infraredReader!.TryAcquireLatestFrame()) {
                    if (
                        (frame?.VideoMediaFrame is { } video) &&
                        (video.SoftwareBitmap is { } bitmap) &&
                        (frame.SystemRelativeTime is { } time) &&
                        (time != lastInfraredTime)
                    ) {
                        lastInfraredTime = time;
                        progressed = true;

                        // The EXACT lit-frame gate: the frame server stamps whether the IR illuminator fired for this
                        // frame; the unlit half of a strobing stream never publishes. An unstamped frame publishes.
                        if (video.InfraredMediaFrame is not { IsIlluminated: false }) {
                            PublishBitmap(bitmap: bitmap, buffer: InfraredFrames, scratch: ref m_infraredScratch);
                        }
                    }
                }

                if (!progressed) {
                    Thread.Sleep(millisecondsTimeout: 2);
                }
            }
        } catch (Exception exception) {
            if (!m_stop) {
                Console.Error.WriteLine(value: $"[camera] dual-sensor read loop stopped: {exception.Message}");
            }
        } finally {
            m_ended = true;
        }
    }
    // Tightly repacks the (possibly padded) BGRA bitmap rows into the reusable scratch and publishes. The one poll
    // thread is the only producer for both scratch buffers.
    private unsafe void PublishBitmap(SoftwareBitmap bitmap, LatestFrameBuffer buffer, ref byte[] scratch) {
        using var converted = ((BitmapPixelFormat.Bgra8 == bitmap.BitmapPixelFormat)
            ? null
            : SoftwareBitmap.Convert(source: bitmap, format: BitmapPixelFormat.Bgra8)
        );

        var source = (converted ?? bitmap);
        var width = source.PixelWidth;
        var height = source.PixelHeight;

        using var locked = source.LockBuffer(mode: BitmapBufferAccessMode.Read);
        using var reference = locked.CreateReference();

        var plane = locked.GetPlaneDescription(index: 0);
        var tight = (width * 4);

        if (scratch.Length != (tight * height)) {
            scratch = new byte[(tight * height)];
        }

        // CsWinRT wrappers answer classic COM interop interfaces through As<T>, never a runtime cast.
        reference.As<IMemoryBufferByteAccess>().GetBuffer(buffer: out var pixels, capacity: out var capacity);

        if ((plane.Stride < tight) || (plane.StartIndex < 0) || (((long)plane.StartIndex + (((long)(height - 1)) * plane.Stride) + tight) > capacity)) {
            throw new InvalidOperationException(message: "the camera returned an invalid BGRA bitmap plane");
        }

        for (var row = 0; (row < height); row++) {
            Marshal.Copy(
                destination: scratch,
                length: tight,
                source: ((nint)(pixels + plane.StartIndex + (((long)row) * plane.Stride))),
                startIndex: (row * tight)
            );
        }

        buffer.Publish(height: height, pixels: scratch, width: width);
    }

    [ComImport]
    [Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}

/// <summary>One sensor's <see cref="ICameraCaptureSession"/> view over the shared dual-sensor core.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class Win32MediaFoundationDualCameraSession(Win32MediaFoundationDualCameraCore core, bool infrared) : ICameraCaptureSession {
    private bool m_disposed;
    private byte[] m_pullBuffer = [];

    // A frame-source group has one VideoDeviceController, not one per sensor. Expose that shared physical control
    // surface through the color facade only; otherwise independently authored per-sensor controls alternate writes
    // against the same hardware on every pull.
    private Win32CameraControlSurface? Controls => (infrared ? null : core.Controls);
    private LatestFrameBuffer Frames => (infrared ? core.InfraredFrames : core.ColorFrames);

    /// <inheritdoc/>
    public long FrameVersion => Frames.Version;
    /// <inheritdoc/>
    public int Height => (infrared ? core.InfraredHeight : core.ColorHeight);
    /// <inheritdoc/>
    public bool IsEnded => core.IsEnded;
    /// <inheritdoc/>
    public long LastFrameTimestamp => Frames.LastTimestamp;
    /// <inheritdoc/>
    public string Name => core.Name;
    /// <inheritdoc/>
    public int Width => (infrared ? core.InfraredWidth : core.ColorWidth);

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        core.Release();
    }
    /// <inheritdoc/>
    public bool TryCapture(out Surface surface) {
        if (m_disposed || !Frames.TryGetLatest(destination: ref m_pullBuffer, height: out var height, width: out var width)) {
            surface = default;

            return false;
        }

        surface = Surface.CpuPixels(
            format: SurfaceFormat.B8G8R8A8Unorm,
            height: ((uint)height),
            pixels: m_pullBuffer,
            width: ((uint)width)
        );

        return true;
    }
    /// <inheritdoc/>
    public bool TryGet(CameraControl control, out int value, out bool auto) {
        if (Controls is { } controls) {
            return controls.TryGet(control: control, value: out value, auto: out auto);
        }

        value = 0;
        auto = false;

        return false;
    }
    /// <inheritdoc/>
    public bool TryGetRange(CameraControl control, out CameraControlRange range) {
        if (Controls is { } controls) {
            return controls.TryGetRange(control: control, range: out range);
        }

        range = default;

        return false;
    }
    /// <inheritdoc/>
    public bool TryResetAuto(CameraControl control) => (Controls?.TryResetAuto(control: control) ?? false);
    /// <inheritdoc/>
    public bool TrySet(CameraControl control, int value) => (Controls?.TrySet(control: control, value: value) ?? false);
    /// <inheritdoc/>
    public bool TryVendorRead(uint selector, out int value) {
        if (Controls is { } controls) {
            return controls.TryVendorRead(selector: selector, value: out value);
        }

        value = 0;

        return false;
    }
    /// <inheritdoc/>
    public bool TryVendorWrite(uint selector, int value) => (Controls?.TryVendorWrite(selector: selector, value: value) ?? false);
}
