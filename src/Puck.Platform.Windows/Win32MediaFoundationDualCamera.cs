using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace Puck.Platform.Windows;

/// <summary>
/// The dual-sensor camera core: ONE <see cref="MediaCapture"/> over the frame-source group that carries both the
/// color and infrared sources, with one <see cref="MediaFrameReader"/> per sensor, fanned out to two
/// <see cref="ICameraCaptureSession"/> facades. A device that multiplexes both sensors through one pipeline (the
/// BRIO) streams them simultaneously ONLY through the camera frame server — a raw source reader with both pins
/// selected never starts (hardware-measured: the first configuration blocks forever and a second session kills the
/// first with 0xC00D3EA3), while this is the arrangement Windows Hello itself uses. The frame server also converts
/// color frames to BGRA and stamps each infrared frame's <see cref="InfraredMediaFrame.IsIlluminated"/>, so the
/// lit-frame gate here is EXACT rather than the single-sensor session's luminance-envelope heuristic. The two
/// facades share the core by refcount — the last disposal stops the readers and the capture.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class Win32MediaFoundationDualCameraCore {
    private readonly MediaCapture m_capture;
    private readonly MediaFrameReader m_colorReader;
    private readonly MediaFrameReader m_infraredReader;

    private byte[] m_colorScratch = [];
    private volatile bool m_ended;
    private byte[] m_infraredScratch = [];
    private int m_referenceCount = 2;
    private volatile bool m_stop;
    private readonly Thread m_thread = null!;

    internal readonly LatestFrameBuffer ColorFrames = new();
    internal readonly LatestFrameBuffer InfraredFrames = new();

    public Win32MediaFoundationDualCameraCore(int colorWidth, int colorHeight, uint colorRateHz, int infraredWidth, int infraredHeight, uint infraredRateHz) {
        var (group, colorInfo, infraredInfo) = FindDualGroup();

        Name = group.DisplayName;
        m_capture = new MediaCapture();
        m_capture.InitializeAsync(mediaCaptureInitializationSettings: new MediaCaptureInitializationSettings {
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            SourceGroup = group,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        }).AsTask().GetAwaiter().GetResult();

        var colorSource = m_capture.FrameSources[colorInfo.Id];
        var infraredSource = m_capture.FrameSources[infraredInfo.Id];

        SelectNearestFormat(height: colorHeight, rateHz: colorRateHz, source: colorSource, width: colorWidth);
        SelectNearestFormat(height: infraredHeight, rateHz: infraredRateHz, source: infraredSource, width: infraredWidth);

        ColorWidth = ((int)colorSource.CurrentFormat.VideoFormat.Width);
        ColorHeight = ((int)colorSource.CurrentFormat.VideoFormat.Height);
        InfraredWidth = ((int)infraredSource.CurrentFormat.VideoFormat.Width);
        InfraredHeight = ((int)infraredSource.CurrentFormat.VideoFormat.Height);
        // The frame server converts both sensors to BGRA at the reader (infrared luminance lands as gray BGRA),
        // so both publish paths share one shape.
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
        m_thread.Start();
    }

    public int ColorHeight { get; }
    public int ColorWidth { get; }
    public Win32CameraControlSurface? Controls { get; }
    public int InfraredHeight { get; }
    public int InfraredWidth { get; }
    public bool IsEnded => m_ended;
    public string Name { get; } = "camera";

    /// <summary>One facade released its half; the last release stops the readers and the capture.</summary>
    public void Release() {
        if (0 != Interlocked.Decrement(location: ref m_referenceCount)) {
            return;
        }

        m_ended = true;
        m_stop = true;
        m_thread.Join(millisecondsTimeout: 2000);

        try {
            m_colorReader.StopAsync().AsTask().GetAwaiter().GetResult();
            m_infraredReader.StopAsync().AsTask().GetAwaiter().GetResult();
        } catch {
            // A capture torn down by device loss mid-stop is already stopped.
        }

        m_colorReader.Dispose();
        m_infraredReader.Dispose();
        m_capture.Dispose();
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
    // The same covering-envelope preference the Media Foundation negotiation applies: smallest extent covering the
    // request (else largest), lowest rate covering the requested rate (else highest).
    private static void SelectNearestFormat(MediaFrameSource source, int width, int height, uint rateHz) {
        MediaFrameFormat? best = null;
        var bestArea = 0L;
        var bestCovers = false;
        var bestRate = 0.0;
        var bestRateCovers = false;

        foreach (var format in source.SupportedFormats) {
            var formatWidth = ((long)format.VideoFormat.Width);
            var formatHeight = ((long)format.VideoFormat.Height);
            var area = (formatWidth * formatHeight);
            var covers = ((formatWidth >= width) && (formatHeight >= height));
            var rate = ((format.FrameRate.Denominator != 0)
                ? (((double)format.FrameRate.Numerator) / format.FrameRate.Denominator)
                : 0.0
            );
            var rateCovers = ((rateHz > 0) && (rate >= rateHz));
            var better = ((best is null) || ((covers != bestCovers)
                ? covers
                : ((area != bestArea)
                    ? (covers ? (area < bestArea) : (area > bestArea))
                    : ((rateCovers != bestRateCovers)
                        ? rateCovers
                        : (rateCovers ? (rate < bestRate) : (rate > bestRate))
                    )
                )
            ));

            if (better) {
                best = format;
                bestArea = area;
                bestCovers = covers;
                bestRate = rate;
                bestRateCovers = rateCovers;
            }
        }

        if (best is not null) {
            source.SetFormatAsync(format: best).AsTask().GetAwaiter().GetResult();
        }
    }
    private void PollLoop() {
        var lastColorTime = TimeSpan.MinValue;
        var lastInfraredTime = TimeSpan.MinValue;

        while (!m_stop) {
            var progressed = false;

            using (var frame = m_colorReader.TryAcquireLatestFrame()) {
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

            using (var frame = m_infraredReader.TryAcquireLatestFrame()) {
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
    }
    // Tightly repacks the (possibly padded) BGRA bitmap rows into the reusable scratch and publishes — each reader's
    // events arrive serialized per reader, so each scratch has exactly one producer.
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
        reference.As<IMemoryBufferByteAccess>().GetBuffer(buffer: out var pixels, capacity: out _);

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
        if (core.Controls is { } controls) {
            return controls.TryGet(control: control, value: out value, auto: out auto);
        }

        value = 0;
        auto = false;

        return false;
    }
    /// <inheritdoc/>
    public bool TryGetRange(CameraControl control, out CameraControlRange range) {
        if (core.Controls is { } controls) {
            return controls.TryGetRange(control: control, range: out range);
        }

        range = default;

        return false;
    }
    /// <inheritdoc/>
    public bool TryResetAuto(CameraControl control) => (core.Controls?.TryResetAuto(control: control) ?? false);
    /// <inheritdoc/>
    public bool TrySet(CameraControl control, int value) => (core.Controls?.TrySet(control: control, value: value) ?? false);
    /// <inheritdoc/>
    public bool TryVendorRead(uint selector, out int value) {
        if (core.Controls is { } controls) {
            return controls.TryVendorRead(selector: selector, value: out value);
        }

        value = 0;

        return false;
    }
    /// <inheritdoc/>
    public bool TryVendorWrite(uint selector, int value) => (core.Controls?.TryVendorWrite(selector: selector, value: value) ?? false);
}
