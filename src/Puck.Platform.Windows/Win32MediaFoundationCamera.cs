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
    public bool TryOpenDefault(int requestedWidth, int requestedHeight, uint requestedRateHz, CameraSensor sensor, [NotNullWhen(true)] out ICameraCaptureSession? session) {
        session = null;

        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        try {
            session = new Win32MediaFoundationCameraSession(
                requestedHeight: requestedHeight,
                requestedRateHz: requestedRateHz,
                requestedWidth: requestedWidth,
                sensor: sensor
            );

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] Media Foundation open failed: {exception.Message}");

            return false;
        }
    }
    /// <inheritdoc/>
    public bool TryOpenDualDefault(int colorWidth, int colorHeight, uint colorRateHz, int infraredWidth, int infraredHeight, uint infraredRateHz, [NotNullWhen(true)] out ICameraCaptureSession? colorSession, [NotNullWhen(true)] out ICameraCaptureSession? infraredSession) {
        colorSession = null;
        infraredSession = null;

        // The dual core rides the camera frame server's WinRT surface (MediaFrameReader et al.).
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) {
            return false;
        }

        try {
            var core = new Win32MediaFoundationDualCameraCore(
                colorHeight: colorHeight,
                colorRateHz: colorRateHz,
                colorWidth: colorWidth,
                infraredHeight: infraredHeight,
                infraredRateHz: infraredRateHz,
                infraredWidth: infraredWidth
            );

            try {
                colorSession = new Win32MediaFoundationDualCameraSession(core: core, infrared: false);
                infraredSession = new Win32MediaFoundationDualCameraSession(core: core, infrared: true);
            } catch {
                // The core reserves one reference for each facade. Release both reservations even if allocating a
                // facade fails, otherwise an exceptional open permanently holds the camera.
                if (colorSession is null) {
                    core.Release();
                } else {
                    colorSession.Dispose();
                    colorSession = null;
                }

                if (infraredSession is null) {
                    core.Release();
                } else {
                    infraredSession.Dispose();
                    infraredSession = null;
                }

                throw;
            }

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] Media Foundation dual-sensor open failed: {exception.Message}");

            return false;
        }
    }
    /// <inheritdoc/>
    public bool TryOpenSharedDefault(long adapterLuid, int requestedWidth, int requestedHeight, uint requestedRateHz, CameraSensor sensor, [NotNullWhen(true)] out ICameraSharedCaptureSession? session) {
        session = null;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return false;
        }

        try {
            session = new Win32MediaFoundationSharedCameraSession(adapterLuid: adapterLuid, requestedHeight: requestedHeight, requestedRateHz: requestedRateHz, requestedWidth: requestedWidth, sensor: sensor);

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
    // Whether the negotiated stream delivers L8 luminance the read loop expands to BGRA host-side (an infrared stream
    // whose format has no RGB32 converter).
    private bool m_expandLuminance;
    // The strobe envelope for an illuminated infrared stream: the BRIO's IR flood fires on ALTERNATING frames
    // (hardware-measured: lit means ~120-185, unlit ~1-3), so the read loop publishes only the lit half once the
    // envelope proves a strobe pattern — a non-strobing device's narrow envelope publishes everything.
    private double m_luminanceHigh;
    private double m_luminanceLow;
    // The reader stream the session reads — the default first video stream, or the discovered infrared stream on a
    // device that classifies IR as a second stream of the color camera.
    private uint m_streamIndex = MfInterop.FirstVideoStream;
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
    private readonly CameraSensor m_sensor;

    private volatile bool m_stop;
    private int m_width;

    public Win32MediaFoundationCameraSession(int requestedWidth, int requestedHeight, uint requestedRateHz, CameraSensor sensor) {
        m_requestedHeight = requestedHeight;
        m_requestedRateHz = requestedRateHz;
        m_requestedWidth = requestedWidth;
        m_sensor = sensor;
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
    public bool TryVendorRead(uint selector, out int value) {
        if (m_controlSurface is { } surface) {
            return surface.TryVendorRead(selector: selector, value: out value);
        }

        value = 0;

        return false;
    }
    /// <inheritdoc/>
    public bool TryVendorWrite(uint selector, int value) => (m_controlSurface?.TryVendorWrite(selector: selector, value: value) ?? false);
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
        // Enumerate video capture devices, pick the first (shared with the GPU-tier session). The infrared sensor is
        // probed on the SAME color device first — devices like the BRIO expose it as a second stream classified by
        // MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, never as their own capture device — and only a device without
        // such a stream falls back to the separate KSCATEGORY_SENSOR_CAMERA enumeration.
        var infrared = (CameraSensor.Infrared == m_sensor);
        var (mediaSource, deviceName) = MfInterop.ActivateDefaultVideoSource(infrared: false);

        if (deviceName is not null) {
            m_name = deviceName;
        }

        m_controlSurface = new Win32CameraControlSurface(mediaSource: mediaSource);

        // A video-processing source reader so Media Foundation inserts the NV12/YUY2 -> RGB32 converter for us.
        Check(hr: MfInterop.MFCreateAttributes(cInitialSize: 1, ppMFAttributes: out var readerConfig));

        var enableVideoProcessing = MfInterop.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;

        Check(hr: readerConfig.SetUINT32(guidKey: ref enableVideoProcessing, unValue: 1));
        Check(hr: MfInterop.MFCreateSourceReaderFromMediaSource(pAttributes: readerConfig, pMediaSource: mediaSource, ppSourceReader: out var reader));

        m_streamIndex = MfInterop.FirstVideoStream;

        if (infrared) {
            if (Win32CameraModeNegotiation.TryFindInfraredStream(reader: reader, streamIndex: out var infraredStream)) {
                m_streamIndex = infraredStream;
            } else {
                // No infrared stream on the color device: release it and take the sensor-camera category — the shape
                // devices with a dedicated IR capture device use. Throws the loud "no infrared capture device" when
                // that category is empty too.
                _ = Marshal.ReleaseComObject(o: reader);

                (mediaSource, deviceName) = MfInterop.ActivateDefaultVideoSource(infrared: true);

                if (deviceName is not null) {
                    m_name = deviceName;
                }

                m_controlSurface = new Win32CameraControlSurface(mediaSource: mediaSource);
                Check(hr: MfInterop.MFCreateSourceReaderFromMediaSource(pAttributes: readerConfig, pMediaSource: mediaSource, ppSourceReader: out reader));
            }
        }

        // Exactly ONE stream stays selected — the second stream's bandwidth (and the driver's shared pipeline) must
        // not be spent on frames nothing reads.
        Check(hr: reader.SetStreamSelection(dwStreamIndex: MfInterop.AllStreams, fSelected: false));
        Check(hr: reader.SetStreamSelection(dwStreamIndex: m_streamIndex, fSelected: true));

        // Resolution is chosen by selecting the native capture mode, or not at all: the reader converts only the
        // CURRENT native type (basic video processing inserts no scaler, and silently drops a frame size set on the
        // output type — SetCurrentMediaType returns S_OK and keeps the device default).
        Win32CameraModeNegotiation.SelectNativeType(
            reader: reader,
            requestedHeight: m_requestedHeight,
            requestedRateHz: m_requestedRateHz,
            requestedWidth: m_requestedWidth,
            streamIndex: m_streamIndex
        );

        // Ask for RGB32 output; Media Foundation supplies the converter for the selected native mode. An infrared
        // stream's L8 luminance frequently has no RGB32 converter — accept the native type there and expand the
        // luminance host-side in the read loop instead.
        Check(hr: MfInterop.MFCreateMediaType(ppMFType: out var outputType));

        var majorTypeKey = MfInterop.MF_MT_MAJOR_TYPE;
        var video = MfInterop.MFMediaType_Video;

        Check(hr: outputType.SetGUID(guidKey: ref majorTypeKey, guidValue: ref video));

        var subTypeKey = MfInterop.MF_MT_SUBTYPE;
        var rgb32 = MfInterop.MFVideoFormat_RGB32;

        Check(hr: outputType.SetGUID(guidKey: ref subTypeKey, guidValue: ref rgb32));

        if (reader.SetCurrentMediaType(dwStreamIndex: m_streamIndex, pMediaType: outputType, pdwReserved: IntPtr.Zero) < 0) {
            Check(hr: reader.GetCurrentMediaType(dwStreamIndex: m_streamIndex, ppMediaType: out var nativeType));

            var nativeSubType = MfInterop.MF_MT_SUBTYPE;
            var l8 = MfInterop.MFVideoFormat_L8;

            if ((nativeType.GetGUID(guidKey: ref nativeSubType, guidValue: out var subtype) < 0) || (l8 != subtype)) {
                throw new InvalidOperationException(message: "the stream offers neither an RGB32 conversion nor L8 luminance");
            }

            m_expandLuminance = true;
        }

        // Read back the negotiated frame size.
        Check(hr: reader.GetCurrentMediaType(dwStreamIndex: m_streamIndex, ppMediaType: out var currentType));

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
        var expanded = Array.Empty<byte>();

        while (!m_stop) {
            var hr = reader.ReadSample(
                dwControlFlags: 0,
                dwStreamIndex: m_streamIndex,
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

                                if (m_expandLuminance) {
                                    // L8 -> opaque gray BGRA, host-side — the shape every consumer downstream of
                                    // TryCapture already expects, so an infrared stream needs no format plumbing
                                    // past this point. The luminance sum rides the same pixel pass for free.
                                    var pixelCount = Math.Min(val1: ((int)length), val2: (m_width * m_height));
                                    var total = 0L;

                                    if (expanded.Length != (pixelCount * 4)) {
                                        expanded = new byte[(pixelCount * 4)];
                                    }

                                    for (var pixel = 0; (pixel < pixelCount); pixel++) {
                                        var luminance = scratch[pixel];
                                        var offset = (pixel * 4);

                                        expanded[offset] = luminance;
                                        expanded[(offset + 1)] = luminance;
                                        expanded[(offset + 2)] = luminance;
                                        expanded[(offset + 3)] = 0xFF;
                                        total += luminance;
                                    }

                                    // The lit-frame gate: track a decaying min/max envelope of frame means; once
                                    // the spread proves the illuminator strobes alternating frames, drop the unlit
                                    // half so the published feed is the flood-lit image (an unfiltered strobe reads
                                    // as violent flicker). A steady stream's narrow envelope never trips the gate.
                                    var mean = (((double)total) / Math.Max(val1: pixelCount, val2: 1));

                                    m_luminanceHigh = Math.Max(val1: mean, val2: (m_luminanceHigh * 0.95));
                                    m_luminanceLow = Math.Min(val1: mean, val2: ((m_luminanceLow * 0.95) + (m_luminanceHigh * 0.05)));

                                    var strobing = (m_luminanceHigh > ((m_luminanceLow * 4) + 8));

                                    if (!strobing || (mean >= ((m_luminanceLow + m_luminanceHigh) / 2))) {
                                        m_latest.Publish(height: m_height, pixels: expanded, width: m_width);
                                    }
                                } else {
                                    m_latest.Publish(height: m_height, pixels: scratch, width: m_width);
                                }
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

        // An L8 stream's raw buffer is one byte per pixel (expanded to BGRA after this telemetry).
        var expected = ((m_width * m_height) * (m_expandLuminance ? 1 : 4));
        var orientation = ((m_defaultStride < 0) ? "bottom-up" : ((m_defaultStride > 0) ? "top-down" : "unreported(assume top-down)"));

        Console.Out.WriteLine(value: $"[camera] first frame {m_width}x{m_height}{(m_expandLuminance ? " (L8 luminance, host-expanded)" : "")}: buffer {length} bytes (packed expects {expected}, {((length == expected) ? "no padding" : "PADDED/short")}); default stride {m_defaultStride} ({orientation}).");
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
    // Walks the reader's streams for one whose descriptor's frame-source flags carry Infrared, falling back to an L8
    // first-native-type heuristic for a driver that omits the classification. Stream enumeration ends at the first
    // index with no native type at all.
    public static bool TryFindInfraredStream(IMFSourceReader reader, out uint streamIndex) {
        var frameSourceKey = MfInterop.MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES;
        var subTypeKey = MfInterop.MF_MT_SUBTYPE;
        var l8 = MfInterop.MFVideoFormat_L8;

        for (var stream = 0u; (reader.GetNativeMediaType(dwStreamIndex: stream, dwMediaTypeIndex: 0, ppMediaType: out var firstType) >= 0); stream++) {
            try {
                if (
                    (reader.GetPresentationAttribute(dwStreamIndex: stream, guidAttribute: ref frameSourceKey, pvarAttribute: out var sourceTypes) >= 0) &&
                    (MfPropVariant.VtUInt32 == sourceTypes.Vt)
                ) {
                    if (0 != (sourceTypes.UInt32Value & MfInterop.FrameSourceTypeInfrared)) {
                        streamIndex = stream;

                        return true;
                    }

                    continue;
                }

                if (
                    (firstType.GetGUID(guidKey: ref subTypeKey, guidValue: out var subtype) >= 0) &&
                    (l8 == subtype)
                ) {
                    streamIndex = stream;

                    return true;
                }
            } finally {
                _ = Marshal.ReleaseComObject(o: firstType);
            }
        }

        streamIndex = 0;

        return false;
    }
    public static void SelectNativeType(IMFSourceReader reader, int requestedWidth, int requestedHeight, uint requestedRateHz, uint streamIndex = MfInterop.FirstVideoStream) {
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

        for (var index = 0u; (reader.GetNativeMediaType(dwStreamIndex: streamIndex, dwMediaTypeIndex: index, ppMediaType: out var candidate) >= 0); index++) {
            var retained = false;

            try {
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
                    if (best is not null) {
                        _ = Marshal.ReleaseComObject(o: best);
                    }

                    best = candidate;
                    bestArea = area;
                    bestCovers = covers;
                    bestRate = rate;
                    bestRateCovers = rateCovers;
                    retained = true;
                }
            } finally {
                if (!retained) {
                    _ = Marshal.ReleaseComObject(o: candidate);
                }
            }
        }

        if (best is not null) {
            try {
                _ = reader.SetCurrentMediaType(dwStreamIndex: streamIndex, pMediaType: best, pdwReserved: IntPtr.Zero);
            } finally {
                _ = Marshal.ReleaseComObject(o: best);
            }
        }
    }
}
