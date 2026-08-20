using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Puck.Platform.Windows.MfInterop;

namespace Puck.Platform.Windows;

/// <summary>
/// The Windows <see cref="ICameraCaptureService"/>: opens the default webcam through Media Foundation. Frames are read
/// in RGB32 (Media Foundation inserts the color converter via the video-processing source reader), so the emitted
/// <see cref="Surface"/> is <see cref="SurfaceFormat.B8G8R8A8Unorm"/> CPU pixels — the M2 CPU-upload tier. Any failure
/// (no device, no Media Foundation, an unsupported format) is swallowed and reported as "not opened" so the live-camera
/// content source falls back cleanly.
/// <para>Negotiation selects the device's native capture mode nearest the requested envelope (smallest extent covering
/// the request, else the largest available; lowest rate covering the requested rate, else the highest) before the RGB32
/// output type is applied — basic video processing inserts no scaler, so the native mode IS the resolution decision.
/// The device remains authoritative: an unfulfillable request keeps its default mode, and the caller reads the
/// negotiated result off the session.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32MediaFoundationCameraService : ICameraCaptureService {
    /// <inheritdoc/>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc/>
    public bool TryOpenDefault(int requestedWidth, int requestedHeight, uint requestedRateHz, [NotNullWhen(true)] out ICameraCaptureSession? session) {
        session = null;

        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        try {
            session = new Win32MediaFoundationCameraSession(
                requestedHeight: requestedHeight,
                requestedRateHz: requestedRateHz,
                requestedWidth: requestedWidth
            );

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] Media Foundation open failed: {exception.Message}");

            return false;
        }
    }
    /// <inheritdoc/>
    public bool TryOpenSharedDefault(long adapterLuid, int requestedWidth, int requestedHeight, uint requestedRateHz, [NotNullWhen(true)] out ICameraSharedCaptureSession? session) {
        session = null;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return false;
        }

        try {
            session = new Win32MediaFoundationSharedCameraSession(adapterLuid: adapterLuid, requestedHeight: requestedHeight, requestedRateHz: requestedRateHz, requestedWidth: requestedWidth);

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] Media Foundation GPU-tier open failed: {exception.Message}");

            return false;
        }
    }
}

/// <summary>The live Media Foundation session: a dedicated MTA grabber thread owns all Media Foundation state (startup,
/// device, source reader, the ReadSample loop, shutdown) and publishes each frame into a <see cref="LatestFrameBuffer"/>;
/// <see cref="TryCapture"/> hands the newest one to the render-thread puller.</summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32MediaFoundationCameraSession : ICameraCaptureSession {
    private readonly LatestFrameBuffer m_latest = new();
    private readonly ManualResetEventSlim m_initDone = new(initialState: false);

    private readonly Thread m_thread;

    // The device's control surface, created on the grabber thread beside the media source (the ctor's init-done wait
    // is the barrier that publishes it to the pull side); null until initialization succeeds.
    private volatile Win32CameraControlSurface? m_controlSurface;
    private int m_defaultStride;
    private bool m_disposed;
    private volatile bool m_ended;
    private bool m_firstFrameLogged;
    private int m_height;
    private string? m_initError;
    private bool m_initOk;

    private string m_name = "camera";
    private byte[] m_pullBuffer = [];

    private readonly int m_requestedHeight;
    private readonly uint m_requestedRateHz;
    private readonly int m_requestedWidth;

    private volatile bool m_stop;
    private int m_width;

    public Win32MediaFoundationCameraSession(int requestedWidth, int requestedHeight, uint requestedRateHz) {
        m_requestedHeight = requestedHeight;
        m_requestedRateHz = requestedRateHz;
        m_requestedWidth = requestedWidth;
        m_thread = new Thread(start: GrabberLoop) {
            IsBackground = true,
            Name = "camera-grabber",
        };
        m_thread.SetApartmentState(state: ApartmentState.MTA);
        m_thread.Start();
        m_initDone.Wait();

        if (!m_initOk) {
            m_stop = true;
            m_thread.Join(millisecondsTimeout: 2000);

            throw new InvalidOperationException(message: (m_initError ?? "the camera failed to initialize"));
        }
    }

    /// <inheritdoc/>
    public long FrameVersion => m_latest.Version;
    /// <inheritdoc/>
    public bool IsEnded => m_ended;
    /// <inheritdoc/>
    public long LastFrameTimestamp => m_latest.LastTimestamp;
    /// <inheritdoc/>
    public int Height => m_height;
    /// <inheritdoc/>
    public string Name => m_name;
    /// <inheritdoc/>
    public int Width => m_width;

    /// <inheritdoc/>
    public bool TryGet(CameraControl control, out int value, out bool auto) {
        if (m_controlSurface is { } surface) {
            return surface.TryGet(control: control, value: out value, auto: out auto);
        }

        value = 0;
        auto = false;

        return false;
    }
    /// <inheritdoc/>
    public bool TryGetRange(CameraControl control, out CameraControlRange range) {
        if (m_controlSurface is { } surface) {
            return surface.TryGetRange(control: control, range: out range);
        }

        range = default;

        return false;
    }
    /// <inheritdoc/>
    public bool TryResetAuto(CameraControl control) => (m_controlSurface?.TryResetAuto(control: control) ?? false);
    /// <inheritdoc/>
    public bool TrySet(CameraControl control, int value) => (m_controlSurface?.TrySet(control: control, value: value) ?? false);
    /// <inheritdoc/>
    public bool TryCapture(out Surface surface) {
        if (m_disposed || !m_latest.TryGetLatest(destination: ref m_pullBuffer, height: out var height, width: out var width)) {
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
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_stop = true;
        m_initDone.Set();
        m_thread.Join(millisecondsTimeout: 2000);
        m_initDone.Dispose();
    }

    // The whole Media Foundation lifetime lives on this one MTA thread: initialize (signalling success/failure back to
    // the ctor), then loop ReadSample publishing the newest frame, then tear down.
    private void GrabberLoop() {
        IMFSourceReader? reader = null;
        var started = false;

        try {
            Check(hr: MfInterop.MFStartup(Version: MfInterop.MfVersion, dwFlags: 0));

            started = true;
            reader = OpenDefaultReader();
            m_initOk = true;
        } catch (Exception exception) {
            m_initError = exception.Message;
            m_initOk = false;
        } finally {
            m_initDone.Set();
        }

        if (m_initOk && (reader is not null)) {
            ReadLoop(reader: reader);
        }

        // Whatever ended the loop (unplug, end of stream, stop), the feed will never publish again — the consumer's
        // re-open signal.
        m_ended = true;

        if (reader is not null) {
            _ = Marshal.ReleaseComObject(o: reader);
        }

        if (started) {
            _ = MfInterop.MFShutdown();
        }
    }
    private IMFSourceReader OpenDefaultReader() {
        // Enumerate video capture devices, pick the first (shared with the GPU-tier session).
        var (mediaSource, deviceName) = MfInterop.ActivateDefaultVideoSource();

        if (deviceName is not null) {
            m_name = deviceName;
        }

        m_controlSurface = new Win32CameraControlSurface(mediaSource: mediaSource);

        // A video-processing source reader so Media Foundation inserts the NV12/YUY2 -> RGB32 converter for us.
        Check(hr: MfInterop.MFCreateAttributes(cInitialSize: 1, ppMFAttributes: out var readerConfig));

        var enableVideoProcessing = MfInterop.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;

        Check(hr: readerConfig.SetUINT32(guidKey: ref enableVideoProcessing, unValue: 1));
        Check(hr: MfInterop.MFCreateSourceReaderFromMediaSource(pAttributes: readerConfig, pMediaSource: mediaSource, ppSourceReader: out var reader));

        Check(hr: reader.SetStreamSelection(dwStreamIndex: MfInterop.FirstVideoStream, fSelected: true));

        // Resolution is chosen by selecting the native capture mode, or not at all: the reader converts only the
        // CURRENT native type (basic video processing inserts no scaler, and silently drops a frame size set on the
        // output type — SetCurrentMediaType returns S_OK and keeps the device default).
        Win32CameraModeNegotiation.SelectNativeType(
            reader: reader,
            requestedHeight: m_requestedHeight,
            requestedRateHz: m_requestedRateHz,
            requestedWidth: m_requestedWidth
        );

        // Ask for RGB32 output; Media Foundation supplies the converter for the selected native mode.
        Check(hr: MfInterop.MFCreateMediaType(ppMFType: out var outputType));

        var majorTypeKey = MfInterop.MF_MT_MAJOR_TYPE;
        var video = MfInterop.MFMediaType_Video;

        Check(hr: outputType.SetGUID(guidKey: ref majorTypeKey, guidValue: ref video));

        var subTypeKey = MfInterop.MF_MT_SUBTYPE;
        var rgb32 = MfInterop.MFVideoFormat_RGB32;

        Check(hr: outputType.SetGUID(guidKey: ref subTypeKey, guidValue: ref rgb32));
        Check(hr: reader.SetCurrentMediaType(dwStreamIndex: MfInterop.FirstVideoStream, pMediaType: outputType, pdwReserved: IntPtr.Zero));

        // Read back the negotiated frame size.
        Check(hr: reader.GetCurrentMediaType(dwStreamIndex: MfInterop.FirstVideoStream, ppMediaType: out var currentType));

        var frameSizeKey = MfInterop.MF_MT_FRAME_SIZE;

        Check(hr: currentType.GetUINT64(guidKey: ref frameSizeKey, punValue: out var packedSize));

        m_width = ((int)(packedSize >> 32));
        m_height = ((int)(packedSize & 0xffffffff));

        if ((m_width <= 0) || (m_height <= 0)) {
            throw new InvalidOperationException(message: $"the camera reported an invalid frame size ({m_width}x{m_height})");
        }

        // The negotiated default stride's SIGN is the authoritative row orientation: a negative stride means the buffer
        // is bottom-up (row 0 is the bottom of the image) — RGB32's GDI convention — which must be flipped to the
        // top-down layout the CPU-upload compositor expects. Absent/zero: assume top-down (positive), report as such.
        var strideKey = MfInterop.MF_MT_DEFAULT_STRIDE;

        m_defaultStride = ((currentType.GetUINT32(guidKey: ref strideKey, punValue: out var rawStride) >= 0) ? (int)rawStride : 0);

        return reader;
    }
    private void ReadLoop(IMFSourceReader reader) {
        var scratch = Array.Empty<byte>();

        while (!m_stop) {
            var hr = reader.ReadSample(
                dwControlFlags: 0,
                dwStreamIndex: MfInterop.FirstVideoStream,
                pdwActualStreamIndex: out _,
                pdwStreamFlags: out var flags,
                pllTimestamp: out _,
                ppSample: out var sample
            );

            if (hr < 0) {
                // A read error mid-stream is usually the device being unplugged or reset; report it (the pane then
                // freezes on the last frame rather than crashing) instead of stopping silently.
                Console.Error.WriteLine(value: $"[camera] '{m_name}' read loop stopped (0x{hr:X8}); the device may have been disconnected.");

                break;
            }

            if ((flags & MfInterop.EndOfStream) != 0) {
                Console.Error.WriteLine(value: $"[camera] '{m_name}' reported end of stream; the live feed has stopped.");

                break;
            }

            if (sample is null) {
                // A stream tick (no frame yet); keep polling.
                continue;
            }

            try {
                if (sample.ConvertToContiguousBuffer(ppBuffer: out var buffer) >= 0) {
                    try {
                        if (buffer.Lock(pcbCurrentLength: out var length, pcbMaxLength: out _, ppbBuffer: out var pointer) >= 0) {
                            try {
                                if (scratch.Length != ((int)length)) {
                                    scratch = new byte[length];
                                }

                                Marshal.Copy(destination: scratch, length: ((int)length), source: pointer, startIndex: 0);
                                LogFirstFrame(length: ((int)length));
                                m_latest.Publish(height: m_height, pixels: scratch, width: m_width);
                            } finally {
                                _ = buffer.Unlock();
                            }
                        }
                    } finally {
                        _ = Marshal.ReleaseComObject(o: buffer);
                    }
                }
            } finally {
                _ = Marshal.ReleaseComObject(o: sample);
            }
        }
    }
    // One-shot format telemetry (first frame only): reports the negotiated buffer length against the tightly-packed
    // expectation (detects contiguous-buffer row padding) and the default stride's sign (row orientation). Both were the
    // hardware bring-up unknowns; keeping the line makes a silent stride/padding surprise diagnosable on any future device
    // (or platform). Hardware-confirmed on the C920 (640x480) and the BRIO (320x240 through 3840x2160, rate-negotiated
    // 30/60/90): no padding, positive stride (top-down) at every extent — the layout the CPU-upload compositor expects,
    // so no per-frame flip or de-pad is needed. The BRIO's 4K MJPEG mode converts CPU-side at ~13 fps — the M2 tier's
    // ceiling, not a device fault; megapixel-hungry consumers are what the GPU tier is built ahead for.
    private void LogFirstFrame(int length) {
        if (m_firstFrameLogged) {
            return;
        }

        m_firstFrameLogged = true;

        var expected = ((m_width * m_height) * 4);
        var orientation = ((m_defaultStride < 0) ? "bottom-up" : ((m_defaultStride > 0) ? "top-down" : "unreported(assume top-down)"));

        Console.Out.WriteLine(value: $"[camera] first frame {m_width}x{m_height}: buffer {length} bytes (packed expects {expected}, {((length == expected) ? "no padding" : "PADDED/short")}); default stride {m_defaultStride} ({orientation}).");
    }
}

/// <summary>The capture-mode negotiation both camera tiers share: selects the device's native mode nearest a requested
/// envelope before the tier's own output type (RGB32 CPU-side, ARGB32 on the GPU tier) is applied — the source reader
/// converts only the CURRENT native type, so the native mode IS the resolution decision on both tiers.</summary>
[SupportedOSPlatform("windows")]
internal static class Win32CameraModeNegotiation {
    // Selects the native capture mode nearest the requested envelope, best-effort. Size first: the smallest native mode
    // COVERING the requested extent (a diegetic panel downscales well; upscaling a smaller feed is the blurry case),
    // else the largest available. Rate second, at the chosen extent: the lowest native rate covering the requested rate
    // (producing faster than the consumer's pull cadence only burns conversion cost), else the highest available —
    // which is also the whole rule when no rate was requested. A device whose native types cannot be enumerated, or
    // that refuses the selection, keeps its default mode — the caller reads the negotiated result back either way.
    public static void SelectNativeType(IMFSourceReader reader, int requestedWidth, int requestedHeight, uint requestedRateHz) {
        if ((requestedWidth <= 0) || (requestedHeight <= 0)) {
            return;
        }

        var frameRateKey = MfInterop.MF_MT_FRAME_RATE;
        var frameSizeKey = MfInterop.MF_MT_FRAME_SIZE;
        IMFMediaType? best = null;
        var bestArea = 0L;
        var bestCovers = false;
        var bestRate = 0.0;
        var bestRateCovers = false;

        for (var index = 0u; (reader.GetNativeMediaType(dwStreamIndex: MfInterop.FirstVideoStream, dwMediaTypeIndex: index, ppMediaType: out var candidate) >= 0); index++) {
            if (candidate.GetUINT64(guidKey: ref frameSizeKey, punValue: out var packedSize) < 0) {
                continue;
            }

            var width = ((long)(packedSize >> 32));
            var height = ((long)(packedSize & 0xffffffff));

            if ((width <= 0) || (height <= 0)) {
                continue;
            }

            var area = (width * height);
            var covers = ((width >= requestedWidth) && (height >= requestedHeight));
            var rate = (((candidate.GetUINT64(guidKey: ref frameRateKey, punValue: out var packedRate) >= 0) && ((packedRate & 0xffffffff) != 0))
                ? (((double)(packedRate >> 32)) / (packedRate & 0xffffffff))
                : 0.0
            );
            var rateCovers = ((requestedRateHz > 0) && (rate >= requestedRateHz));
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
                best = candidate;
                bestArea = area;
                bestCovers = covers;
                bestRate = rate;
                bestRateCovers = rateCovers;
            }
        }

        if (best is not null) {
            _ = reader.SetCurrentMediaType(dwStreamIndex: MfInterop.FirstVideoStream, pMediaType: best, pdwReserved: IntPtr.Zero);
        }
    }
}
