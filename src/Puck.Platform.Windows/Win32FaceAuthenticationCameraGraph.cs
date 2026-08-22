using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Puck.Platform.Probes;
using Windows.Media.Capture.Frames;
using WinRT;

namespace Puck.Platform.Windows;

/// <summary>
/// The coordinated color/infrared graph over one <see cref="Win32FaceAuthenticationCapture"/>. One worker polls both
/// realtime readers (the event path never fires in this hosting shape) and hands each new frame to the tier; the graph
/// is ready only once the tier has proved both pins live, so a multiplexing camera that accepts two pins but delivers
/// one never reaches a consumer. The frame server stamps each infrared frame's
/// <see cref="InfraredMediaFrame.IsIlluminated"/>, so the lit-frame gate here is exact rather than the single-sensor
/// tier's luminance heuristic.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal abstract class Win32FaceAuthenticationCameraGraph<TStream> : Win32CameraGraph<TStream> where TStream : class, ICameraStream {
    private const int FirstFrameTimeoutMilliseconds = 5000;

    private readonly Win32FaceAuthenticationCapture m_capture;
    private readonly TStream[] m_streams;

    private TimeSpan m_lastColorTime = TimeSpan.MinValue;
    private TimeSpan m_lastInfraredTime = TimeSpan.MinValue;

    protected Win32FaceAuthenticationCameraGraph(ReadOnlySpan<CameraStreamRequest> requests, MediaCaptureMemoryPreference memoryPreference) {
        m_capture = Win32FaceAuthenticationCapture.Open(memoryPreference: memoryPreference);

        try {
            m_streams = new TStream[requests.Length];

            for (var index = 0; (index < requests.Length); index++) {
                var sensor = requests[index].Sensor;

                m_streams[index] = CreateStream(sensor: sensor, stream: Stream(sensor: sensor));
            }
        } catch {
            m_capture.Dispose();

            throw;
        }
    }

    public sealed override ICameraControlSurface Controls => m_capture.Controls;
    public sealed override string Name => m_capture.Name;
    public sealed override IReadOnlyList<TStream> Streams => m_streams;

    protected Win32FaceAuthenticationStream Color => m_capture.Color;
    protected Win32FaceAuthenticationStream Infrared => m_capture.Infrared;

    /// <summary>Builds the tier's stream over one native pin.</summary>
    protected abstract TStream CreateStream(CameraSensor sensor, Win32FaceAuthenticationStream stream);
    /// <summary>Handles one new native frame on the worker thread.</summary>
    protected abstract void Deliver(VideoMediaFrame video, CameraSensor sensor);
    /// <summary>Gets a value indicating whether both pins have proved live for this tier.</summary>
    protected abstract bool IsLive { get; }
    /// <summary>Runs once per poll while live; the shared tier attaches targets here.</summary>
    protected virtual void Service() { }
    /// <summary>Releases tier resources on the worker thread before the capture closes.</summary>
    protected virtual void ReleaseTier() { }
    protected void Start(string threadName) => RunWorker(
        readyTimeoutMessage: $"the device did not prove both streams live within {FirstFrameTimeoutMilliseconds} ms (color {Color.Description}, infrared {Infrared.Description})",
        readyTimeoutMilliseconds: FirstFrameTimeoutMilliseconds,
        threadName: threadName
    );
    protected TStream StreamFor(CameraSensor sensor) {
        foreach (var stream in m_streams) {
            if (stream.Sensor == sensor) {
                return stream;
            }
        }

        throw new InvalidOperationException(message: $"the graph carries no {sensor} stream");
    }

    protected sealed override void Work() {
        try {
            while (!Stopping) {
                var progressed = false;

                using (var frame = Color.Reader.TryAcquireLatestFrame()) {
                    progressed |= Process(frame: frame, lastTime: ref m_lastColorTime, sensor: CameraSensor.Color);
                }

                using (var frame = Infrared.Reader.TryAcquireLatestFrame()) {
                    progressed |= Process(frame: frame, lastTime: ref m_lastInfraredTime, sensor: CameraSensor.Infrared);
                }

                if (IsLive) {
                    Ready();
                    Service();
                }

                if (!progressed) {
                    Thread.Sleep(millisecondsTimeout: 2);
                }
            }
        } finally {
            ReleaseTier();
            m_capture.Dispose();
        }
    }

    private bool Process(MediaFrameReference? frame, CameraSensor sensor, ref TimeSpan lastTime) {
        if (
            (frame?.VideoMediaFrame is not { } video) ||
            (frame.SystemRelativeTime is not { } time) ||
            (time == lastTime)
        ) {
            return false;
        }

        lastTime = time;
        Deliver(sensor: sensor, video: video);

        return true;
    }
    private Win32FaceAuthenticationStream Stream(CameraSensor sensor) => (sensor switch {
        CameraSensor.Color => Color,
        CameraSensor.Infrared => Infrared,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(sensor), actualValue: sensor, message: "Unknown camera sensor."),
    });
}
/// <summary>The CPU-pixel tier: the frame server places frames in system memory; each illuminated frame is repacked
/// (L8 expanded to gray BGRA directly) into the sensor's latest-frame buffer. Both pins must publish once to be live.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class Win32FaceAuthenticationPixelGraph : Win32FaceAuthenticationCameraGraph<Win32PixelStream> {
    private byte[] m_colorScratch = [];
    private byte[] m_infraredScratch = [];

    public Win32FaceAuthenticationPixelGraph(ReadOnlySpan<CameraStreamRequest> requests) : base(memoryPreference: MediaCaptureMemoryPreference.Cpu, requests: requests) {
        try {
            Start(threadName: "camera-dual-poll");
        } catch {
            // Some USB camera drivers finish switching modes after their WinRT objects have been released; an immediate
            // reopen of the single-sensor graph can fail on a healthy device without this settle.
            Thread.Sleep(millisecondsTimeout: 2000);

            throw;
        }
    }

    protected override bool IsLive => ((Streams.Count > 0) && Streams.All(predicate: stream => (stream.FrameVersion > 0L)));

    protected override Win32PixelStream CreateStream(CameraSensor sensor, Win32FaceAuthenticationStream stream) => new(
        height: stream.Height,
        nativeFormat: stream.CaptureFormat,
        sensor: sensor,
        width: stream.Width
    );
    protected override void Deliver(VideoMediaFrame video, CameraSensor sensor) {
        if (video.SoftwareBitmap is not { } bitmap) {
            return;
        }

        if (CameraSensor.Infrared == sensor) {
            // The unlit half of a strobing stream never publishes; an unstamped frame publishes.
            if (video.InfraredMediaFrame is not { IsIlluminated: false }) {
                PublishInfraredBitmap(bitmap: bitmap, buffer: StreamFor(sensor: sensor).Frames);
            }
        } else {
            PublishBitmap(bitmap: bitmap, buffer: StreamFor(sensor: sensor).Frames, scratch: ref m_colorScratch);
        }
    }

    // L8 is already the one-byte luminance shape; expanding it directly avoids a converted SoftwareBitmap per frame.
    private unsafe void PublishInfraredBitmap(SoftwareBitmap bitmap, LatestFrameBuffer buffer) {
        if (BitmapPixelFormat.Gray8 != bitmap.BitmapPixelFormat) {
            PublishBitmap(bitmap: bitmap, buffer: buffer, scratch: ref m_infraredScratch);

            return;
        }

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;

        using var locked = bitmap.LockBuffer(mode: BitmapBufferAccessMode.Read);
        using var reference = locked.CreateReference();

        var plane = locked.GetPlaneDescription(index: 0);

        reference.As<IMemoryBufferByteAccess>().GetBuffer(buffer: out var pixels, capacity: out var capacity);

        if ((plane.Stride < width) || (plane.StartIndex < 0) || (((((long)plane.StartIndex) + (((long)(height - 1)) * plane.Stride)) + width) > capacity)) {
            throw new InvalidOperationException(message: "the camera returned an invalid L8 bitmap plane");
        }

        if (m_infraredScratch.Length != ((width * height) * 4)) {
            m_infraredScratch = new byte[((width * height) * 4)];
        }

        var destination = MemoryMarshal.Cast<byte, uint>(span: m_infraredScratch.AsSpan());

        for (var row = 0; (row < height); row++) {
            var source = new ReadOnlySpan<byte>(
                length: width,
                pointer: ((pixels + plane.StartIndex) + (((long)row) * plane.Stride))
            );
            var output = destination.Slice(length: width, start: (row * width));

            for (var column = 0; (column < width); column++) {
                var luminance = ((uint)source[column]);

                output[column] = 0xFF000000u | (luminance * 0x00010101u);
            }
        }

        buffer.Publish(height: height, pixels: m_infraredScratch, width: width);
    }
    // Tightly repacks the (possibly padded) BGRA rows into the reusable scratch and publishes.
    private static unsafe void PublishBitmap(SoftwareBitmap bitmap, LatestFrameBuffer buffer, ref byte[] scratch) {
        using var converted = ((BitmapPixelFormat.Bgra8 == bitmap.BitmapPixelFormat)
            ? null
            : SoftwareBitmap.Convert(format: BitmapPixelFormat.Bgra8, source: bitmap)
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

        if ((plane.Stride < tight) || (plane.StartIndex < 0) || (((((long)plane.StartIndex) + (((long)(height - 1)) * plane.Stride)) + tight) > capacity)) {
            throw new InvalidOperationException(message: "the camera returned an invalid BGRA bitmap plane");
        }

        for (var row = 0; (row < height); row++) {
            Marshal.Copy(
                destination: scratch,
                length: tight,
                source: ((nint)((pixels + plane.StartIndex) + (((long)row) * plane.Stride))),
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
/// <summary>The shared-texture tier: the frame server places frames on its own Direct3D 11 device, and a
/// <see cref="Win32D3D11CameraFrameConverter"/> per pin converts each native surface into the consumer's RGBA ring.
/// Both pins must expose a Direct3D surface to be live, and both rings attach before either publishes, so a refusal
/// returns the whole pair to the CPU tier.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class Win32FaceAuthenticationSharedGraph : Win32FaceAuthenticationCameraGraph<Win32SharedStream>, ICameraKernelHost, IProbeInputResolver {
    private readonly long m_adapterLuid;
    private readonly Win32ProbeKernelBench m_bench = new();

    private Win32D3D11CameraFrameConverter? m_colorConverter;
    private Win32D3D11CameraFrameConverter? m_infraredConverter;

    public Win32FaceAuthenticationSharedGraph(long adapterLuid, ReadOnlySpan<CameraStreamRequest> requests) : base(memoryPreference: MediaCaptureMemoryPreference.Auto, requests: requests) {
        m_adapterLuid = adapterLuid;
        Start(threadName: "camera-dual-gpu-poll");
        Console.Out.WriteLine(value: $"[camera] dual GPU negotiation: color {Color.Description} + infrared {Infrared.Description}, native Direct3D surfaces on adapter 0x{adapterLuid:X16}.");
    }

    protected override bool IsLive => ((m_colorConverter is not null) && (m_infraredConverter is not null));

    protected override Win32SharedStream CreateStream(CameraSensor sensor, Win32FaceAuthenticationStream stream) => new(
        height: stream.Height,
        nativeFormat: stream.CaptureFormat,
        sensor: sensor,
        targetFormat: SurfaceFormat.R8G8B8A8Unorm,
        width: stream.Width
    );
    protected override void Deliver(VideoMediaFrame video, CameraSensor sensor) {
        // Memory placement is fixed per stream for the capture's lifetime, so a system-memory frame is a definitive
        // refusal of the tier; failing now spares the caller the rest of the first-frame window.
        if (video.Direct3DSurface is not { } surface) {
            throw new NotSupportedException(message: $"the {sensor} stream delivers software frames, not Direct3D surfaces");
        }

        var infrared = (CameraSensor.Infrared == sensor);
        var native = (infrared ? Infrared : Color);
        var texture = Win32D3D11CameraFrameConverter.GetTexture(access: out var access, surface: surface);

        try {
            ref var converter = ref (infrared ? ref m_infraredConverter : ref m_colorConverter);

            converter ??= new Win32D3D11CameraFrameConverter(
                adapterLuid: m_adapterLuid,
                colorimetry: native.Colorimetry,
                height: native.Height,
                sourceTexture: texture,
                subtype: native.CaptureFormat.Subtype,
                width: native.Width
            );

            // Probe every frame, but convert only once both rings are attached. The unlit half of a strobing infrared
            // stream never publishes; it is kept beside the lit frame for kernels that read the pair.
            if (!converter.IsStarted) {
                return;
            }

            if (infrared && (video.InfraredMediaFrame is { IsIlluminated: false })) {
                converter.ConvertPrevious(sourceTexture: texture);

                return;
            }

            var stream = StreamFor(sensor: sensor);
            if (!stream.Slots.TryReserveWriteSlot(slot: out var slot)) {
                return;
            }

            converter.Convert(sourceTexture: texture, targetSlot: slot);
            stream.Slots.Publish(slot: slot);
            m_bench.OnFrame(captureTimestamp: stream.LastFrameTimestamp, device: converter, resolver: this, sensor: sensor);
        } finally {
            Win32D3D11VideoDevice.ReleaseTexture(texture: texture);
            _ = Marshal.ReleaseComObject(o: access);
        }
    }
    protected override void Service() {
        if (Stopping || m_colorConverter!.IsStarted) {
            return;
        }

        foreach (var stream in Streams) {
            if (!stream.Targets.IsCompletedSuccessfully) {
                return;
            }
        }

        // Both attachments run on this one thread; if either fails, the exception ends both streams together.
        m_colorConverter.AttachTargets(sharedTargetHandles: StreamFor(sensor: CameraSensor.Color).Targets.Result);
        m_infraredConverter!.AttachTargets(sharedTargetHandles: StreamFor(sensor: CameraSensor.Infrared).Targets.Result);
    }
    protected override void OnStopping() {
        foreach (var stream in Streams) {
            stream.CancelStart();
        }
    }
    protected override void ReleaseTier() {
        m_bench.Close();
        m_colorConverter?.Dispose();
        m_colorConverter = null;
        m_infraredConverter?.Dispose();
        m_infraredConverter = null;
    }

    /// <inheritdoc/>
    public bool TryAttachKernel(in ProbeKernelRequest request, ProbeReadingRing ring, [NotNullWhen(true)] out IProbeKernelRun? run, out string fault) {
        ArgumentNullException.ThrowIfNull(ring);

        var triggered = false;

        foreach (var input in request.Inputs) {
            switch (input) {
                case ProbeKernelInput.Sensor sensorInput:
                    if (sensorInput.Kind is not (CameraSensor.Color or CameraSensor.Infrared)) {
                        run = null;
                        fault = $"this graph carries no {sensorInput.Kind} stream";

                        return false;
                    }

                    triggered |= (sensorInput.Kind == request.Trigger);

                    break;
                case ProbeKernelInput.StrobePair strobeInput:
                    if (CameraSensor.Infrared != strobeInput.Kind) {
                        run = null;
                        fault = "only the infrared stream strobes; a strobe-pair socket must name it";

                        return false;
                    }

                    triggered |= (strobeInput.Kind == request.Trigger);

                    break;
            }
        }

        if (!triggered) {
            run = null;
            fault = $"the trigger sensor {request.Trigger} is not among the kernel's sensor/strobe-pair inputs";

            return false;
        }

        run = m_bench.Attach(request: in request, ring: ring);
        fault = "";

        return true;
    }
    nint IProbeInputResolver.Resolve(CameraSensor sensor, bool previous) => (sensor switch {
        CameraSensor.Color => (m_colorConverter?.OutputView ?? 0),
        CameraSensor.Infrared => (previous ? (m_infraredConverter?.PreviousView ?? 0) : (m_infraredConverter?.OutputView ?? 0)),
        _ => 0,
    });
    (int Width, int Height) IProbeInputResolver.Extent(CameraSensor sensor) => ((CameraSensor.Infrared == sensor)
        ? (Infrared.Width, Infrared.Height)
        : (Color.Width, Color.Height)
    );
}
