using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.Platform.Windows;

/// <summary>
/// The Windows <see cref="ICameraCaptureService"/>: opens the default webcam through Media Foundation. Frames are read
/// in RGB32 (Media Foundation inserts the color converter via the video-processing source reader), so the emitted
/// <see cref="Surface"/> is <see cref="SurfaceFormat.B8G8R8A8Unorm"/> CPU pixels — the M2 CPU-upload tier. Any failure
/// (no device, no Media Foundation, an unsupported format) is swallowed and reported as "not opened" so the live-camera
/// content source falls back cleanly.
/// <para>NOTE: this Media Foundation interop is written against the documented vtables but has NOT been verified against
/// a real device on this machine; it is structured to degrade gracefully and is the piece the hardware bring-up
/// exercises. Known caveats to confirm on hardware: RGB32 row orientation (top-down vs bottom-up) and any row padding in
/// the contiguous buffer.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32MediaFoundationCameraService : ICameraCaptureService {
    /// <inheritdoc/>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc/>
    public bool TryOpenDefault(int requestedWidth, int requestedHeight, [NotNullWhen(true)] out ICameraCaptureSession? session) {
        session = null;

        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        try {
            session = new Win32MediaFoundationCameraSession();

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] Media Foundation open failed: {exception.Message}");

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
    private int m_defaultStride;
    private bool m_disposed;
    private bool m_firstFrameLogged;
    private int m_height;
    private string? m_initError;
    private bool m_initOk;
    private string m_name = "camera";
    private byte[] m_pullBuffer = [];
    private volatile bool m_stop;
    private int m_width;

    public Win32MediaFoundationCameraSession() {
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
    public int Height => m_height;
    /// <inheritdoc/>
    public string Name => m_name;
    /// <inheritdoc/>
    public int Width => m_width;

    /// <inheritdoc/>
    public bool TryCapture(out Surface surface) {
        if (m_disposed || !m_latest.TryGetLatest(destination: ref m_pullBuffer, height: out var height, width: out var width)) {
            surface = default;

            return false;
        }

        surface = new Surface(
            Format: SurfaceFormat.B8G8R8A8Unorm,
            Height: (uint)height,
            ImageViewHandle: 0,
            Pixels: m_pullBuffer,
            Width: (uint)width
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

        if (reader is not null) {
            _ = Marshal.ReleaseComObject(o: reader);
        }

        if (started) {
            _ = MfInterop.MFShutdown();
        }
    }

    private IMFSourceReader OpenDefaultReader() {
        // Enumerate video capture devices, pick the first.
        Check(hr: MfInterop.MFCreateAttributes(ppMFAttributes: out var enumConfig, cInitialSize: 1));

        var sourceTypeKey = MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
        var vidcap = MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP;

        Check(hr: enumConfig.SetGUID(guidKey: ref sourceTypeKey, guidValue: ref vidcap));
        Check(hr: MfInterop.MFEnumDeviceSources(pAttributes: enumConfig, pppSourceActivate: out var devices, pcSourceActivate: out var count));

        if ((0 == count) || (0 == devices)) {
            throw new InvalidOperationException(message: "no video capture devices were found");
        }

        var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pUnk: Marshal.ReadIntPtr(ptr: devices));

        // Release every raw device pointer the array owns (the RCW above holds its own ref) and free the array.
        for (var index = 0; (index < count); index++) {
            _ = Marshal.Release(pUnk: Marshal.ReadIntPtr(ptr: devices, ofs: (index * IntPtr.Size)));
        }

        Marshal.FreeCoTaskMem(ptr: devices);

        var nameKey = MfInterop.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME;

        if (activate.GetAllocatedString(guidKey: ref nameKey, ppwszValue: out var deviceName, pcchLength: out _) >= 0) {
            m_name = deviceName;
        }

        var sourceIid = MfInterop.IID_IMFMediaSource;

        Check(hr: activate.ActivateObject(riid: ref sourceIid, ppv: out var mediaSource));

        // A video-processing source reader so Media Foundation inserts the NV12/YUY2 -> RGB32 converter for us.
        Check(hr: MfInterop.MFCreateAttributes(ppMFAttributes: out var readerConfig, cInitialSize: 1));

        var enableVideoProcessing = MfInterop.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;

        Check(hr: readerConfig.SetUINT32(guidKey: ref enableVideoProcessing, unValue: 1));
        Check(hr: MfInterop.MFCreateSourceReaderFromMediaSource(pMediaSource: mediaSource, pAttributes: readerConfig, ppSourceReader: out var reader));

        // Ask for RGB32 output; Media Foundation supplies the converter.
        Check(hr: MfInterop.MFCreateMediaType(ppMFType: out var outputType));

        var majorTypeKey = MfInterop.MF_MT_MAJOR_TYPE;
        var video = MfInterop.MFMediaType_Video;

        Check(hr: outputType.SetGUID(guidKey: ref majorTypeKey, guidValue: ref video));

        var subTypeKey = MfInterop.MF_MT_SUBTYPE;
        var rgb32 = MfInterop.MFVideoFormat_RGB32;

        Check(hr: outputType.SetGUID(guidKey: ref subTypeKey, guidValue: ref rgb32));
        Check(hr: reader.SetStreamSelection(dwStreamIndex: MfInterop.FirstVideoStream, fSelected: true));
        Check(hr: reader.SetCurrentMediaType(dwStreamIndex: MfInterop.FirstVideoStream, pdwReserved: IntPtr.Zero, pMediaType: outputType));

        // Read back the negotiated frame size.
        Check(hr: reader.GetCurrentMediaType(dwStreamIndex: MfInterop.FirstVideoStream, ppMediaType: out var currentType));

        var frameSizeKey = MfInterop.MF_MT_FRAME_SIZE;

        Check(hr: currentType.GetUINT64(guidKey: ref frameSizeKey, punValue: out var packedSize));

        m_width = (int)(packedSize >> 32);
        m_height = (int)(packedSize & 0xffffffff);

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
                dwStreamIndex: MfInterop.FirstVideoStream,
                dwControlFlags: 0,
                pdwActualStreamIndex: out _,
                pdwStreamFlags: out var flags,
                pllTimestamp: out _,
                ppSample: out var sample
            );

            if (hr < 0) {
                break;
            }

            if ((flags & MfInterop.EndOfStream) != 0) {
                break;
            }

            if (sample is null) {
                // A stream tick (no frame yet); keep polling.
                continue;
            }

            try {
                if (sample.ConvertToContiguousBuffer(ppBuffer: out var buffer) >= 0) {
                    try {
                        if (buffer.Lock(ppbBuffer: out var pointer, pcbMaxLength: out _, pcbCurrentLength: out var length) >= 0) {
                            try {
                                if (scratch.Length != (int)length) {
                                    scratch = new byte[length];
                                }

                                Marshal.Copy(source: pointer, destination: scratch, startIndex: 0, length: (int)length);
                                LogFirstFrame(length: (int)length);
                                m_latest.Publish(pixels: scratch, width: m_width, height: m_height);
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
    // (or platform). Hardware-confirmed on the C920: 640x480, no padding, stride +2560 (top-down) — the layout the
    // CPU-upload compositor expects, so no per-frame flip or de-pad is needed.
    private void LogFirstFrame(int length) {
        if (m_firstFrameLogged) {
            return;
        }

        m_firstFrameLogged = true;

        var expected = (m_width * m_height * 4);
        var orientation = (m_defaultStride < 0) ? "bottom-up" : ((m_defaultStride > 0) ? "top-down" : "unreported(assume top-down)");

        Console.Out.WriteLine(value: $"[camera] first frame {m_width}x{m_height}: buffer {length} bytes (packed expects {expected}, {((length == expected) ? "no padding" : "PADDED/short")}); default stride {m_defaultStride} ({orientation}).");
    }

    private static void Check(int hr) {
        if (hr < 0) {
            throw new COMException(message: "a Media Foundation call failed", errorCode: hr);
        }
    }
}
