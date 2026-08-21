using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace Puck.Platform.Windows;

/// <summary>The GPU-resident counterpart of <see cref="Win32MediaFoundationDualCameraCore"/>. It opens the same
/// driver-declared Face Authentication Profile V2 pair, but asks the frame server for automatic memory placement and
/// admits the tier only when both native frames expose Direct3D surfaces on the render adapter. Native YUY2 and L8 are
/// converted by <see cref="Win32D3D11CameraFrameConverter"/> into independent RGBA shared rings. Both rings attach
/// before either stream publishes, so a failure returns the whole pair to the established CPU graph.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class Win32MediaFoundationDualSharedCameraCore {
    private const int FirstFrameTimeoutMilliseconds = 5000;

    private readonly long m_adapterLuid;
    private readonly MediaCapture? m_capture;
    private readonly MediaFrameReader? m_colorReader;
    private readonly MediaFrameReader? m_infraredReader;
    private readonly ManualResetEventSlim m_probeDone = new(initialState: false);
    private readonly ManualResetEventSlim m_startSignal = new(initialState: false);
    private readonly Lock m_startSync = new();
    private readonly Thread? m_thread;

    private Win32D3D11CameraFrameConverter? m_colorConverter;
    private nint[]? m_colorHandles;
    private TimeSpan m_lastColorTime = TimeSpan.MinValue;
    private TimeSpan m_lastInfraredTime = TimeSpan.MinValue;
    private Win32D3D11CameraFrameConverter? m_infraredConverter;
    private nint[]? m_infraredHandles;
    private string? m_probeError;
    private int m_referenceCount = 2;
    private int m_shutdown;
    private volatile bool m_stop;
    private volatile bool m_ended;

    public Win32MediaFoundationDualSharedCameraCore(long adapterLuid) {
        m_adapterLuid = adapterLuid;

        try {
            var (group, colorInfo, infraredInfo) = Win32MediaFoundationDualCameraCore.FindDualGroup();
            var profile = (
                Win32MediaFoundationDualCameraCore.FindFaceAuthenticationProfile(videoDeviceId: colorInfo.DeviceInformation.Id) ??
                Win32MediaFoundationDualCameraCore.FindFaceAuthenticationProfile(videoDeviceId: infraredInfo.DeviceInformation.Id) ??
                Win32MediaFoundationDualCameraCore.FindFaceAuthenticationProfile(videoDeviceId: group.Id)
            );

            if (profile is null) {
                throw new NotSupportedException(message: "the camera does not publish a Face Authentication Profile V2 color/infrared pair");
            }

            Name = group.DisplayName;
            m_capture = new MediaCapture();
            m_capture.InitializeAsync(mediaCaptureInitializationSettings: new MediaCaptureInitializationSettings {
                MemoryPreference = MediaCaptureMemoryPreference.Auto,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                SourceGroup = group,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                VideoProfile = profile,
            }).AsTask().GetAwaiter().GetResult();

            var colorSource = m_capture.FrameSources[colorInfo.Id];
            var infraredSource = m_capture.FrameSources[infraredInfo.Id];
            var colorDescription = Win32MediaFoundationDualCameraCore.DescribeFormat(format: colorSource.CurrentFormat);
            var infraredDescription = Win32MediaFoundationDualCameraCore.DescribeFormat(format: infraredSource.CurrentFormat);

            ColorWidth = checked((int)colorSource.CurrentFormat.VideoFormat.Width);
            ColorHeight = checked((int)colorSource.CurrentFormat.VideoFormat.Height);
            InfraredWidth = checked((int)infraredSource.CurrentFormat.VideoFormat.Width);
            InfraredHeight = checked((int)infraredSource.CurrentFormat.VideoFormat.Height);
            var captureMode = Win32MediaFoundationDualCameraCore.ConfigureFaceAuthentication(controller: infraredSource.Controller);
            ColorCaptureFormat = Win32MediaFoundationDualCameraCore.CaptureFormat(format: colorSource.CurrentFormat, mode: captureMode);
            InfraredCaptureFormat = Win32MediaFoundationDualCameraCore.CaptureFormat(format: infraredSource.CurrentFormat, mode: captureMode);
            Controls = new Win32CameraControlSurface(mediaSource: m_capture.VideoDeviceController);

            // Native readers preserve the exact FaceAuth topology. MemoryPreference.Auto is the only difference from
            // the CPU core; asking either reader for BGRA would change the admitted output type and make BRIO reject IR.
            m_colorReader = m_capture.CreateFrameReaderAsync(inputSource: colorSource).AsTask().GetAwaiter().GetResult();
            m_infraredReader = m_capture.CreateFrameReaderAsync(inputSource: infraredSource).AsTask().GetAwaiter().GetResult();
            m_colorReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            m_infraredReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            var starts = Task.WhenAll(m_colorReader.StartAsync().AsTask(), m_infraredReader.StartAsync().AsTask()).GetAwaiter().GetResult();

            if ((starts[0] != MediaFrameReaderStartStatus.Success) || (starts[1] != MediaFrameReaderStartStatus.Success)) {
                throw new InvalidOperationException(message: $"the native GPU readers refused to start (color={starts[0]}, infrared={starts[1]})");
            }

            m_thread = new Thread(start: PollLoop) {
                IsBackground = true,
                Name = "camera-dual-gpu-poll",
            };
            m_thread.SetApartmentState(state: ApartmentState.MTA);
            m_thread.Start();

            if (!m_probeDone.Wait(millisecondsTimeout: FirstFrameTimeoutMilliseconds)) {
                throw new InvalidOperationException(message: $"both native streams did not expose GPU surfaces within {FirstFrameTimeoutMilliseconds} ms (color={colorDescription}, infrared={infraredDescription})");
            }

            if (m_probeError is not null) {
                throw new NotSupportedException(message: m_probeError);
            }

            Console.Out.WriteLine(value: $"[camera] dual GPU negotiation: Windows Face Authentication Profile V2 — color {colorDescription} + infrared {infraredDescription}, native Direct3D surfaces on adapter 0x{adapterLuid:X16}.");
        } catch {
            Shutdown();
            throw;
        }
    }

    public CameraCaptureFormat ColorCaptureFormat { get; }
    public DualSharedPublication ColorFrames { get; } = new();
    public int ColorHeight { get; }
    public int ColorWidth { get; }
    public Win32CameraControlSurface? Controls { get; }
    public CameraCaptureFormat InfraredCaptureFormat { get; }
    public DualSharedPublication InfraredFrames { get; } = new();
    public int InfraredHeight { get; }
    public int InfraredWidth { get; }
    public bool IsEnded => m_ended;
    public string Name { get; } = "camera";

    public void Start(bool infrared, IReadOnlyList<nint> handles) {
        ArgumentNullException.ThrowIfNull(handles);

        if (handles.Count == 0) {
            throw new ArgumentException(message: "At least one shared target is required.", paramName: nameof(handles));
        }

        lock (m_startSync) {
            ref var destination = ref (infrared ? ref m_infraredHandles : ref m_colorHandles);

            if (destination is not null) {
                throw new InvalidOperationException(message: $"the {(infrared ? "infrared" : "color")} target ring is already attached");
            }

            destination = [.. handles];

            if ((m_colorHandles is not null) && (m_infraredHandles is not null)) {
                m_startSignal.Set();
            }
        }
    }

    public void Release() {
        if (Interlocked.Decrement(location: ref m_referenceCount) == 0) {
            Shutdown();
        }
    }

    private void PollLoop() {
        try {
            while (!m_stop) {
                var progressed = false;

                using (var color = m_colorReader!.TryAcquireLatestFrame()) {
                    progressed |= ProcessFrame(frame: color, infrared: false, lastTime: ref m_lastColorTime);
                }

                using (var infrared = m_infraredReader!.TryAcquireLatestFrame()) {
                    progressed |= ProcessFrame(frame: infrared, infrared: true, lastTime: ref m_lastInfraredTime);
                }

                if ((m_colorConverter is not null) && (m_infraredConverter is not null)) {
                    m_probeDone.Set();
                    TryAttachTargets();
                }

                if (!progressed) {
                    Thread.Sleep(millisecondsTimeout: 2);
                }
            }
        } catch (Exception exception) {
            if (!m_stop) {
                m_probeError ??= exception.Message;
                Console.Error.WriteLine(value: $"[camera] dual GPU read/conversion loop stopped: {exception.Message}");
            }
        } finally {
            m_probeDone.Set();
            m_ended = true;
        }
    }

    private bool ProcessFrame(MediaFrameReference? frame, bool infrared, ref TimeSpan lastTime) {
        if (
            (frame?.VideoMediaFrame is not { Direct3DSurface: { } surface } video) ||
            (frame.SystemRelativeTime is not { } time) ||
            (time == lastTime)
        ) {
            return false;
        }

        lastTime = time;
        var texture = Win32D3D11CameraFrameConverter.GetTexture(surface: surface, access: out var access);

        try {
            ref var converter = ref (infrared ? ref m_infraredConverter : ref m_colorConverter);
            converter ??= new Win32D3D11CameraFrameConverter(
                sourceTexture: texture,
                adapterLuid: m_adapterLuid,
                width: (infrared ? InfraredWidth : ColorWidth),
                height: (infrared ? InfraredHeight : ColorHeight),
                infrared: infrared
            );

            // Probe every native stream, but publish only after BOTH target rings attach. The frame-server's exact IR
            // illumination stamp retains the established lit-frame gate on the GPU path.
            if (!converter.IsStarted || (infrared && (video.InfraredMediaFrame is { IsIlluminated: false }))) {
                return true;
            }

            var publication = (infrared ? InfraredFrames : ColorFrames);
            var slot = publication.NextSlot(targetCount: (infrared ? m_infraredHandles!.Length : m_colorHandles!.Length));
            converter.Convert(sourceTexture: texture, targetSlot: slot);
            publication.Publish(slot: slot);

            return true;
        } finally {
            Win32D3D11VideoDevice.ReleaseTexture(texture: texture);
            _ = Marshal.ReleaseComObject(o: access);
        }
    }

    private void TryAttachTargets() {
        if (!m_startSignal.IsSet || m_colorConverter!.IsStarted) {
            return;
        }

        nint[] color;
        nint[] infrared;

        lock (m_startSync) {
            color = m_colorHandles!;
            infrared = m_infraredHandles!;
        }

        // Both attachments run on the one poll thread. If either fails, the exception ends both facades before either
        // can be mistaken for an independently viable stream.
        m_colorConverter.AttachTargets(sharedTargetHandles: color);
        m_infraredConverter!.AttachTargets(sharedTargetHandles: infrared);
    }

    private void Shutdown() {
        if (Interlocked.Exchange(location1: ref m_shutdown, value: 1) != 0) {
            return;
        }

        m_stop = true;
        m_startSignal.Set();
        m_probeDone.Set();
        _ = (m_thread?.Join(millisecondsTimeout: 2000) ?? true);

        try {
            m_colorReader?.StopAsync().AsTask().GetAwaiter().GetResult();
            m_infraredReader?.StopAsync().AsTask().GetAwaiter().GetResult();
        } catch {
            // Device removal means the readers are already stopped.
        }

        m_colorConverter?.Dispose();
        m_infraredConverter?.Dispose();
        m_colorReader?.Dispose();
        m_infraredReader?.Dispose();
        m_capture?.Dispose();
        m_ended = true;
    }
}

/// <summary>One sensor's publication counters over the coordinated shared-camera core.</summary>
internal sealed class DualSharedPublication {
    private int m_latestSlot = -1;
    private long m_timestamp;
    private long m_version;

    public int LatestSlot => m_latestSlot;
    public long Timestamp => Interlocked.Read(location: ref m_timestamp);
    public long Version => Interlocked.Read(location: ref m_version);
    public int NextSlot(int targetCount) => ((m_latestSlot + 1) % targetCount);
    public void Publish(int slot) {
        m_latestSlot = slot;
        _ = Interlocked.Exchange(location1: ref m_timestamp, value: Stopwatch.GetTimestamp());
        _ = Interlocked.Increment(location: ref m_version);
    }
}

/// <summary>One sensor facade over the coordinated native-surface GPU core.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class Win32MediaFoundationDualSharedCameraSession(Win32MediaFoundationDualSharedCameraCore core, bool infrared) : ICameraSharedCaptureSession, ICameraCaptureDiagnostics {
    private bool m_disposed;
    private Win32CameraControlSurface? Controls => (infrared ? null : core.Controls);
    private DualSharedPublication Frames => (infrared ? core.InfraredFrames : core.ColorFrames);

    public CameraCaptureFormat CaptureFormat => (infrared ? core.InfraredCaptureFormat : core.ColorCaptureFormat);
    public long FrameVersion => Frames.Version;
    public int Height => (infrared ? core.InfraredHeight : core.ColorHeight);
    public bool IsEnded => core.IsEnded;
    public long LastFrameTimestamp => Frames.Timestamp;
    public int LatestSlot => Frames.LatestSlot;
    public string Name => core.Name;
    public SurfaceFormat TargetFormat => SurfaceFormat.R8G8B8A8Unorm;
    public int Width => (infrared ? core.InfraredWidth : core.ColorWidth);

    public void Start(IReadOnlyList<nint> sharedTargetHandles) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        core.Start(infrared: infrared, handles: sharedTargetHandles);
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        core.Release();
    }
    public bool TryGet(CameraControl control, out int value, out bool auto) {
        if (Controls is { } controls) {
            return controls.TryGet(control: control, value: out value, auto: out auto);
        }

        value = 0;
        auto = false;
        return false;
    }
    public bool TryGetRange(CameraControl control, out CameraControlRange range) {
        if (Controls is { } controls) {
            return controls.TryGetRange(control: control, range: out range);
        }

        range = default;
        return false;
    }
    public bool TryResetAuto(CameraControl control) => (Controls?.TryResetAuto(control: control) ?? false);
    public bool TrySet(CameraControl control, int value) => (Controls?.TrySet(control: control, value: value) ?? false);
    public bool TryVendorRead(uint selector, out int value) {
        if (Controls is { } controls) {
            return controls.TryVendorRead(selector: selector, value: out value);
        }

        value = 0;
        return false;
    }
    public bool TryVendorWrite(uint selector, int value) => (Controls?.TryVendorWrite(selector: selector, value: value) ?? false);
}
