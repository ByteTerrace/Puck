using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Platform.Probes;
using static Puck.Platform.Windows.MfInterop;

namespace Puck.Platform.Windows;

/// <summary>
/// A single-sensor graph over a Media Foundation source reader. The worker starts Media Foundation, activates the
/// default device for the sensor, negotiates the native capture mode nearest the request, and loops ReadSample; the
/// tier decides how the reader is configured and where each sample goes.
/// <para>Resolution is chosen by selecting a native capture mode, never by the output type: the reader converts only
/// the current native type (basic video processing inserts no scaler and silently ignores a frame size on the output
/// type), so the native mode is the resolution decision on both tiers.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal abstract class Win32SourceReaderCameraGraph<TStream> : Win32CameraGraph<TStream> where TStream : class, ICameraStream {
    private const int ReadyTimeoutMilliseconds = 15000;

    private Win32CameraControlSurface? m_controls;
    private object? m_mediaSource;
    private string m_name = "camera";
    private TStream[] m_streams = [];

    protected Win32SourceReaderCameraGraph(CameraStreamRequest request) {
        Request = request;
    }

    public sealed override ICameraControlSurface Controls => m_controls!;
    public sealed override string Name => m_name;
    public sealed override IReadOnlyList<TStream> Streams => m_streams;

    protected CameraStreamRequest Request { get; }

    /// <summary>Adds the tier's reader attributes (processing mode, device manager).</summary>
    protected abstract void ConfigureReader(IMFAttributes config);
    /// <summary>Handles one sample on the worker thread; the sample is released by the caller.</summary>
    protected abstract void Deliver(IMFSample sample);
    /// <summary>Sets the tier's output type for the selected native mode and builds the stream at the negotiated
    /// extent.</summary>
    protected abstract TStream Negotiate(IMFSourceReader reader, uint streamIndex, Guid nativeSubtype, CameraCaptureFormat nativeFormat);
    /// <summary>Runs after the graph is ready and before the first ReadSample; returning <see langword="false"/> ends
    /// the worker without streaming.</summary>
    protected virtual bool BeginStreaming() => true;
    /// <summary>Creates tier resources the reader depends on, before the device is activated.</summary>
    protected virtual void Prepare() { }
    /// <summary>Releases tier resources after the reader is gone.</summary>
    protected virtual void ReleaseTier() { }
    protected void Start(string threadName) => RunWorker(
        readyTimeoutMessage: $"the camera did not negotiate within {ReadyTimeoutMilliseconds} ms",
        readyTimeoutMilliseconds: ReadyTimeoutMilliseconds,
        threadName: threadName
    );

    protected sealed override void Work() {
        IMFSourceReader? reader = null;
        var started = false;

        try {
            Check(hr: MFStartup(Version: MfVersion, dwFlags: 0));
            started = true;
            Prepare();
            reader = OpenReader(streamIndex: out var streamIndex);
            Ready();

            if (BeginStreaming()) {
                ReadSamples(reader: reader, streamIndex: streamIndex);
            }
        } finally {
            if (reader is not null) {
                _ = Marshal.ReleaseComObject(o: reader);
            }

            if (m_mediaSource is not null) {
                _ = Marshal.ReleaseComObject(o: m_mediaSource);
                m_mediaSource = null;
            }

            ReleaseTier();

            if (started) {
                _ = MFShutdown();
            }
        }
    }

    private IMFSourceReader OpenReader(out uint streamIndex) {
        // The infrared sensor is probed on the color device first — the BRIO exposes it as a second stream of the color
        // camera — and only a device without such a stream falls back to the separate sensor-camera category.
        var infrared = (CameraSensor.Infrared == Request.Sensor);
        var (mediaSource, deviceName) = ActivateDefaultVideoSource(extended: infrared, infrared: false);
        IMFMediaType? infraredType = null;
        IMFPresentationDescriptor? infraredPresentation = null;

        streamIndex = FirstVideoStream;
        m_mediaSource = mediaSource;

        if (infrared && !TryPrepareInfraredStream(mediaSource: mediaSource, mediaType: out infraredType, presentationDescriptor: out infraredPresentation, streamIndex: out streamIndex)) {
            _ = Marshal.ReleaseComObject(o: mediaSource);
            m_mediaSource = null;
            (mediaSource, deviceName) = ActivateDefaultVideoSource(extended: true, infrared: true);
            m_mediaSource = mediaSource;

            if (!TryPrepareInfraredStream(mediaSource: mediaSource, mediaType: out infraredType, presentationDescriptor: out infraredPresentation, streamIndex: out streamIndex)) {
                throw new InvalidOperationException(message: "the infrared capture device exposes no native L8 stream");
            }
        }

        if (deviceName is not null) {
            m_name = deviceName;
        }

        m_controls = new Win32CameraControlSurface(mediaSource: mediaSource);

        IMFSourceReader? reader = null;

        try {
            Check(hr: MFCreateAttributes(cInitialSize: 2, ppMFAttributes: out var config));

            try {
                ConfigureReader(config: config);
                Check(hr: MFCreateSourceReaderFromMediaSource(pAttributes: config, pMediaSource: mediaSource, ppSourceReader: out reader));
            } finally {
                _ = Marshal.ReleaseComObject(o: config);
            }

            // Exactly one stream stays selected — a second stream's bandwidth (and the driver's shared pipeline) must not be
            // spent on frames nothing reads.
            Check(hr: reader.SetStreamSelection(dwStreamIndex: AllStreams, fSelected: false));
            Check(hr: reader.SetStreamSelection(dwStreamIndex: streamIndex, fSelected: true));

            if (infraredType is not null) {
                Check(hr: reader.SetCurrentMediaType(dwStreamIndex: streamIndex, pMediaType: infraredType, pdwReserved: IntPtr.Zero));
            }

            Win32CameraModeNegotiation.SelectNativeType(
                reader: reader,
                requestedHeight: Request.Height,
                requestedRateHz: Request.RateHz,
                requestedWidth: Request.Width,
                requiredSubtype: (infrared ? MFVideoFormat_L8 : null),
                streamIndex: streamIndex
            );

            var nativeFormat = Win32CameraModeNegotiation.ReadNativeFormat(reader: reader, streamIndex: streamIndex, subtype: out var nativeSubtype);

            m_streams = [Negotiate(nativeFormat: nativeFormat, nativeSubtype: nativeSubtype, reader: reader, streamIndex: streamIndex)];

            return reader;
        } catch {
            if (reader is not null) {
                _ = Marshal.ReleaseComObject(o: reader);
            }

            throw;
        } finally {
            if (infraredType is not null) {
                _ = Marshal.ReleaseComObject(o: infraredType);
            }

            if (infraredPresentation is not null) {
                _ = Marshal.ReleaseComObject(o: infraredPresentation);
            }
        }
    }
    private void ReadSamples(IMFSourceReader reader, uint streamIndex) {
        while (!Stopping) {
            var hr = reader.ReadSample(
                dwControlFlags: 0,
                dwStreamIndex: streamIndex,
                pdwActualStreamIndex: out _,
                pdwStreamFlags: out var flags,
                pllTimestamp: out _,
                ppSample: out var sample
            );

            if (hr < 0) {
                Console.Error.WriteLine(value: $"[camera] '{m_name}' read loop stopped (0x{hr:X8}); the device may have been disconnected.");

                return;
            }

            if ((flags & EndOfStream) != 0) {
                Console.Error.WriteLine(value: $"[camera] '{m_name}' reported end of stream; the live feed has stopped.");

                return;
            }

            if (sample is null) {
                continue;
            }

            try {
                Deliver(sample: sample);
            } finally {
                _ = Marshal.ReleaseComObject(o: sample);
            }
        }
    }

    /// <summary>Reads the negotiated frame size and signed default stride off the reader's current type, releasing the
    /// temporary media-type COM object before returning.</summary>
    protected static (int Width, int Height) ReadFrameLayout(IMFSourceReader reader, uint streamIndex, out int defaultStride) {
        Check(hr: reader.GetCurrentMediaType(dwStreamIndex: streamIndex, ppMediaType: out var currentType));

        try {
            var frameSizeKey = MF_MT_FRAME_SIZE;

            Check(hr: currentType.GetUINT64(guidKey: ref frameSizeKey, punValue: out var packedSize));

            var width = ((int)(packedSize >> 32));
            var height = ((int)(packedSize & 0xffffffff));

            if ((width <= 0) || (height <= 0)) {
                throw new InvalidOperationException(message: $"the camera reported an invalid frame size ({width}x{height})");
            }

            var strideKey = MF_MT_DEFAULT_STRIDE;

            defaultStride = ((currentType.GetUINT32(guidKey: ref strideKey, punValue: out var rawStride) >= 0)
                ? unchecked((int)rawStride)
                : 0
            );

            return (width, height);
        } finally {
            _ = Marshal.ReleaseComObject(o: currentType);
        }
    }
    /// <summary>Builds a video output type carrying only a subtype; the selected native mode owns the resolution.</summary>
    protected static IMFMediaType OutputType(Guid subtype) {
        Check(hr: MFCreateMediaType(ppMFType: out var outputType));

        try {
            var majorTypeKey = MF_MT_MAJOR_TYPE;
            var video = MFMediaType_Video;

            Check(hr: outputType.SetGUID(guidKey: ref majorTypeKey, guidValue: ref video));

            var subTypeKey = MF_MT_SUBTYPE;

            Check(hr: outputType.SetGUID(guidKey: ref subTypeKey, guidValue: ref subtype));

            return outputType;
        } catch {
            _ = Marshal.ReleaseComObject(o: outputType);

            throw;
        }
    }
}
/// <summary>The CPU-pixel tier: the reader converts the native mode to RGB32 (or the loop expands native L8 luminance
/// host-side) and every frame is copied into the stream's latest-frame buffer.</summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32SourceReaderPixelGraph : Win32SourceReaderCameraGraph<Win32PixelStream> {
    private int m_defaultStride;
    private bool m_expandLuminance;
    private byte[] m_expanded = [];
    private bool m_firstFrameLogged;
    private int m_height;
    private bool m_layoutFaultLogged;
    // The strobe envelope for an illuminated infrared stream: the BRIO's IR flood fires on alternating frames
    // (hardware-measured: lit means ~120-185, unlit ~1-3), so the loop publishes only the lit half once the envelope
    // proves a strobe pattern — a non-strobing device's narrow envelope publishes everything.
    private double m_luminanceHigh;
    private double m_luminanceLow;
    private byte[] m_scratch = [];
    private byte[] m_packed = [];
    private Win32PixelStream? m_stream;
    private int m_width;

    public Win32SourceReaderPixelGraph(CameraStreamRequest request) : base(request: request) {
        Start(threadName: "camera-grabber");
    }

    protected override void ConfigureReader(IMFAttributes config) {
        // Color rides the video-processing reader for the NV12/YUY2 -> RGB32 converter. Infrared stays native end to
        // end: Windows' UVC IR contract supports no conversion, and enabling the processor can make a valid L8 pin
        // accept startup only to reject its first ReadSample.
        if (CameraSensor.Infrared != Request.Sensor) {
            var enableVideoProcessing = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;

            Check(hr: config.SetUINT32(guidKey: ref enableVideoProcessing, unValue: 1));
        }
    }
    protected override Win32PixelStream Negotiate(IMFSourceReader reader, uint streamIndex, Guid nativeSubtype, CameraCaptureFormat nativeFormat) {
        if (CameraSensor.Infrared == Request.Sensor) {
            if (MFVideoFormat_L8 != nativeSubtype) {
                throw new InvalidOperationException(message: "the infrared stream does not offer native L8 luminance");
            }

            m_expandLuminance = true;
        } else {
            var outputType = OutputType(subtype: MFVideoFormat_RGB32);
            int outputResult;

            try {
                outputResult = reader.SetCurrentMediaType(dwStreamIndex: streamIndex, pMediaType: outputType, pdwReserved: IntPtr.Zero);
            } finally {
                _ = Marshal.ReleaseComObject(o: outputType);
            }

            if (outputResult < 0) {
                // A color-category device whose selected mode has no RGB32 converter (a monochrome L8 camera) streams
                // through the same host-side luminance expansion the infrared path uses.
                if (MFVideoFormat_L8 != nativeSubtype) {
                    throw new InvalidOperationException(message: "the stream offers neither an RGB32 conversion nor L8 luminance");
                }

                m_expandLuminance = true;
            }
        }

        var (width, height) = ReadFrameLayout(reader: reader, streamIndex: streamIndex, defaultStride: out m_defaultStride);
        m_width = width;
        m_height = height;
        m_stream = new Win32PixelStream(height: height, nativeFormat: nativeFormat, sensor: Request.Sensor, width: width);

        return m_stream;
    }
    protected override void Deliver(IMFSample sample) {
        if (sample.ConvertToContiguousBuffer(ppBuffer: out var buffer) < 0) {
            return;
        }

        try {
            if (buffer.Lock(pcbCurrentLength: out var length, pcbMaxLength: out _, ppbBuffer: out var pointer) < 0) {
                return;
            }

            try {
                if (m_scratch.Length != ((int)length)) {
                    m_scratch = new byte[length];
                }

                Marshal.Copy(destination: m_scratch, length: ((int)length), source: pointer, startIndex: 0);
                LogFirstFrame(length: ((int)length));

                if (m_expandLuminance) {
                    PublishLuminance(length: ((int)length));
                } else {
                    PublishBgra(length: ((int)length));
                }
            } finally {
                _ = buffer.Unlock();
            }
        } finally {
            _ = Marshal.ReleaseComObject(o: buffer);
        }
    }
    private void PublishBgra(int length) {
        var packedLength = checked(((m_width * m_height) * 4));

        if (m_packed.Length != packedLength) {
            m_packed = new byte[packedLength];
        }

        if (!CameraFramePacking.TryPackBgra(
            destination: m_packed,
            height: m_height,
            source: m_scratch.AsSpan(start: 0, length: length),
            sourceStride: m_defaultStride,
            width: m_width
        )) {
            LogLayoutFault(length: length);

            return;
        }

        m_stream!.Frames.Publish(height: m_height, pixels: m_packed, width: m_width);
    }
    // L8 -> opaque gray BGRA, host-side; the luminance sum rides the same row-normalizing pass and drives the lit-frame gate.
    private void PublishLuminance(int length) {
        var pixelCount = checked((m_width * m_height));

        if (m_expanded.Length != (pixelCount * 4)) {
            m_expanded = new byte[(pixelCount * 4)];
        }

        if (!CameraFramePacking.TryExpandLuminance(
            destination: m_expanded,
            height: m_height,
            luminanceSum: out var total,
            source: m_scratch.AsSpan(start: 0, length: length),
            sourceStride: m_defaultStride,
            width: m_width
        )) {
            LogLayoutFault(length: length);

            return;
        }

        var mean = (((double)total) / Math.Max(val1: pixelCount, val2: 1));

        m_luminanceHigh = Math.Max(val1: mean, val2: (m_luminanceHigh * 0.95));
        m_luminanceLow = Math.Min(val1: mean, val2: ((m_luminanceLow * 0.95) + (m_luminanceHigh * 0.05)));

        var strobing = (m_luminanceHigh > ((m_luminanceLow * 4) + 8));

        if (!strobing || (mean >= ((m_luminanceLow + m_luminanceHigh) / 2))) {
            m_stream!.Frames.Publish(height: m_height, pixels: m_expanded, width: m_width);
        }
    }
    private void LogLayoutFault(int length) {
        if (m_layoutFaultLogged) {
            return;
        }

        m_layoutFaultLogged = true;
        Console.Error.WriteLine(value: $"[camera] dropped malformed {m_width}x{m_height} frame: buffer {length} bytes, stride {m_defaultStride}.");
    }
    // One-shot format telemetry: the buffer length against the tightly packed expectation (row padding) and the
    // default stride's sign (orientation) — the two layout facts a new device can change silently.
    private void LogFirstFrame(int length) {
        if (m_firstFrameLogged) {
            return;
        }

        m_firstFrameLogged = true;

        var expected = ((m_width * m_height) * (m_expandLuminance ? 1 : 4));
        var orientation = ((m_defaultStride < 0) ? "bottom-up" : ((m_defaultStride > 0) ? "top-down" : "unreported(assume top-down)"));

        Console.Out.WriteLine(value: $"[camera] first frame {m_width}x{m_height}{(m_expandLuminance ? " (L8 luminance, host-expanded)" : "")}: buffer {length} bytes (packed expects {expected}, {((length == expected) ? "no padding" : "PADDED/short")}); default stride {m_defaultStride} ({orientation}).");
    }
}
/// <summary>The shared-texture tier: a Direct3D 11 video device on the consumer's adapter backs the reader's DXGI
/// device manager, the DXVA video processor converts each frame to ARGB32 on the GPU, and the worker copies the sample's
/// texture into the next consumer-provisioned target, completing the copy before publishing the slot.</summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class Win32SourceReaderSharedGraph : Win32SourceReaderCameraGraph<Win32SharedStream>, ICameraKernelHost, IProbeInputResolver {
    private readonly long m_adapterLuid;
    private readonly Win32ProbeKernelBench m_bench = new();

    private int m_latestSlot = -1;
    private nint[] m_targetViews = [];

    private Win32D3D11VideoDevice? m_device;
    private IMFDXGIDeviceManager? m_manager;
    private Win32SharedStream? m_stream;
    private nint[] m_targets = [];

    public Win32SourceReaderSharedGraph(long adapterLuid, CameraStreamRequest request) : base(request: request) {
        // A standalone infrared stream's L8 luminance has no DXVA-to-ARGB32 path; the coordinated Face Authentication
        // graph keeps native L8 and expands it with the camera-device compute path instead.
        if (CameraSensor.Infrared == request.Sensor) {
            throw new NotSupportedException(message: "standalone infrared capture rides the CPU-pixel tier");
        }

        m_adapterLuid = adapterLuid;
        Start(threadName: "camera-gpu-grabber");
    }

    protected override void Prepare() {
        m_device = new Win32D3D11VideoDevice(adapterLuid: m_adapterLuid);
        Check(hr: MFCreateDXGIDeviceManager(pResetToken: out var resetToken, ppDeviceManager: out m_manager));
        Check(hr: m_manager.ResetDevice(pUnkDevice: m_device.DevicePointer, resetToken: resetToken));
    }
    protected override void ConfigureReader(IMFAttributes config) {
        var managerKey = MF_SOURCE_READER_D3D_MANAGER;

        Check(hr: config.SetUnknown(guidKey: ref managerKey, punkValue: m_manager!));

        var advancedProcessing = MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING;

        Check(hr: config.SetUINT32(guidKey: ref advancedProcessing, unValue: 1));
    }
    protected override Win32SharedStream Negotiate(IMFSourceReader reader, uint streamIndex, Guid nativeSubtype, CameraCaptureFormat nativeFormat) {
        var outputType = OutputType(subtype: MFVideoFormat_ARGB32);

        try {
            Check(hr: reader.SetCurrentMediaType(dwStreamIndex: streamIndex, pMediaType: outputType, pdwReserved: IntPtr.Zero));
        } finally {
            _ = Marshal.ReleaseComObject(o: outputType);
        }

        var (width, height) = ReadFrameLayout(reader: reader, streamIndex: streamIndex, defaultStride: out _);

        m_stream = new Win32SharedStream(height: height, nativeFormat: nativeFormat, sensor: Request.Sensor, targetFormat: SurfaceFormat.B8G8R8A8Unorm, width: width);

        return m_stream;
    }
    protected override bool BeginStreaming() {
        nint[] handles;

        try {
            handles = m_stream!.Targets.GetAwaiter().GetResult();
        } catch (OperationCanceledException) {
            return false;
        }

        m_targets = new nint[handles.Length];
        m_targetViews = new nint[handles.Length];

        for (var index = 0; (index < handles.Length); index++) {
            m_targets[index] = m_device!.OpenSharedTexture(sharedHandle: handles[index]);
            m_targetViews[index] = m_device.CreateShaderResourceView(texture: m_targets[index]);
        }

        return true;
    }
    protected override void Deliver(IMFSample sample) {
        if (sample.GetBufferByIndex(dwIndex: 0, ppBuffer: out var buffer) < 0) {
            return;
        }

        try {
            if (buffer is not IMFDXGIBuffer dxgiBuffer) {
                throw new InvalidOperationException(message: "the reader produced a non-DXGI sample on the GPU tier; the D3D manager was not honored");
            }

            var texture2dIid = global::Windows.Win32.Graphics.Direct3D11.ID3D11Texture2D.IID_Guid;

            if ((dxgiBuffer.GetResource(ppvObject: out var frameTexture, riid: ref texture2dIid) < 0) || (0 == frameTexture)) {
                return;
            }

            try {
                _ = dxgiBuffer.GetSubresourceIndex(puSubresource: out var subresource);

                if (!m_stream!.Slots.TryReserveWriteSlot(slot: out var slot)) {
                    return;
                }

                m_device!.CopyToTarget(sourceSubresource: subresource, sourceTexture: frameTexture, targetTexture: m_targets[slot]);
                m_stream.Slots.Publish(slot: slot);
                m_latestSlot = slot;
                m_bench.OnFrame(captureTimestamp: m_stream.LastFrameTimestamp, device: m_device, resolver: this, sensor: CameraSensor.Color);
            } finally {
                Win32D3D11VideoDevice.ReleaseTexture(texture: frameTexture);
            }
        } finally {
            _ = Marshal.ReleaseComObject(o: buffer);
        }
    }
    protected override void OnStopping() => m_stream?.CancelStart();

    /// <inheritdoc/>
    public bool TryAttachKernel(in ProbeKernelRequest request, ProbeReadingRing ring, [NotNullWhen(true)] out IProbeKernelRun? run, out string fault) {
        ArgumentNullException.ThrowIfNull(ring);

        var triggered = false;

        foreach (var input in request.Inputs) {
            switch (input) {
                case ProbeKernelInput.Sensor sensorInput:
                    if (CameraSensor.Color != sensorInput.Kind) {
                        run = null;
                        fault = "a single-sensor graph binds only its current color frame";

                        return false;
                    }

                    triggered |= (sensorInput.Kind == request.Trigger);

                    break;
                case ProbeKernelInput.StrobePair:
                    run = null;
                    fault = "a single-sensor graph carries no strobe pair";

                    return false;
            }
        }

        if (!triggered || (CameraSensor.Color != request.Trigger)) {
            run = null;
            fault = "the trigger sensor must be the graph's color stream";

            return false;
        }

        run = m_bench.Attach(request: in request, ring: ring);
        fault = "";

        return true;
    }
    nint IProbeInputResolver.Resolve(CameraSensor sensor, bool previous) => (((m_latestSlot >= 0) && (CameraSensor.Color == sensor) && !previous)
        ? m_targetViews[m_latestSlot]
        : 0
    );
    (int Width, int Height) IProbeInputResolver.Extent(CameraSensor sensor) => (m_stream!.Width, m_stream.Height);
    protected override void ReleaseTier() {
        m_bench.Close();

        foreach (var view in m_targetViews) {
            Win32D3D11VideoDevice.ReleaseTexture(texture: view);
        }

        m_targetViews = [];

        foreach (var target in m_targets) {
            Win32D3D11VideoDevice.ReleaseTexture(texture: target);
        }

        m_targets = [];

        if (m_manager is not null) {
            _ = Marshal.ReleaseComObject(o: m_manager);
            m_manager = null;
        }

        m_device?.Dispose();
        m_device = null;
    }
}
/// <summary>Native capture-mode negotiation shared by every source-reader tier.</summary>
[SupportedOSPlatform("windows")]
internal static class Win32CameraModeNegotiation {
    // Size first: the smallest native mode covering the requested extent (a diegetic panel downscales well; upscaling
    // a smaller feed is the blurry case), else the largest available. Rate second, at the chosen extent: the lowest
    // native rate covering the requested rate, else the highest available — also the whole rule when no rate was
    // requested. A device that refuses the selection keeps its default mode; the caller reads the result back.
    public static void SelectNativeType(IMFSourceReader reader, int requestedWidth, int requestedHeight, uint requestedRateHz, uint streamIndex = FirstVideoStream, Guid? requiredSubtype = null) {
        if ((requestedWidth <= 0) || (requestedHeight <= 0)) {
            return;
        }

        var frameRateKey = MF_MT_FRAME_RATE;
        var frameSizeKey = MF_MT_FRAME_SIZE;
        var subTypeKey = MF_MT_SUBTYPE;
        IMFMediaType? best = null;
        var bestArea = 0L;
        var bestCovers = false;
        var bestRate = 0.0;
        var bestRateCovers = false;

        for (var index = 0u; (reader.GetNativeMediaType(dwMediaTypeIndex: index, dwStreamIndex: streamIndex, ppMediaType: out var candidate) >= 0); index++) {
            var retained = false;

            try {
                if (
                    (requiredSubtype is { } subtype) &&
                    ((candidate.GetGUID(guidKey: ref subTypeKey, guidValue: out var candidateSubtype) < 0) || (subtype != candidateSubtype))
                ) {
                    continue;
                }

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
                var rate = FrameRate(mediaType: candidate);
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
    /// <summary>Reads the selected native type before a tier replaces the reader's output subtype — the USB transport
    /// authors diagnose bandwidth and profile choices by, not Puck's presentation format.</summary>
    public static CameraCaptureFormat ReadNativeFormat(IMFSourceReader reader, uint streamIndex, out Guid subtype) {
        var subtypeKey = MF_MT_SUBTYPE;

        Check(hr: reader.GetCurrentMediaType(dwStreamIndex: streamIndex, ppMediaType: out var mediaType));

        try {
            subtype = ((mediaType.GetGUID(guidKey: ref subtypeKey, guidValue: out var value) >= 0)
                ? value
                : Guid.Empty
            );

            return new CameraCaptureFormat(
                RateHz: FrameRate(mediaType: mediaType),
                Subtype: SubtypeName(subtype: subtype)
            );
        } finally {
            _ = Marshal.ReleaseComObject(o: mediaType);
        }
    }

    private static double FrameRate(IMFMediaType mediaType) {
        var frameRateKey = MF_MT_FRAME_RATE;

        return (((mediaType.GetUINT64(guidKey: ref frameRateKey, punValue: out var packedRate) >= 0) && ((packedRate & 0xffffffff) != 0))
            ? (((double)(packedRate >> 32)) / (packedRate & 0xffffffff))
            : 0.0
        );
    }
    private static string SubtypeName(Guid subtype) {
        if (Guid.Empty == subtype) {
            return "unknown";
        }

        if (MFVideoFormat_L8 == subtype) {
            return "L8";
        }

        Span<byte> bytes = stackalloc byte[16];

        _ = subtype.TryWriteBytes(destination: bytes);

        if (
            (bytes[0] is >= 0x20 and <= 0x7e) &&
            (bytes[1] is >= 0x20 and <= 0x7e) &&
            (bytes[2] is >= 0x20 and <= 0x7e) &&
            (bytes[3] is >= 0x20 and <= 0x7e)
        ) {
            return new string(value: [
                ((char)bytes[0]),
                ((char)bytes[1]),
                ((char)bytes[2]),
                ((char)bytes[3]),
            ]);
        }

        return subtype.ToString(format: "D");
    }
}
