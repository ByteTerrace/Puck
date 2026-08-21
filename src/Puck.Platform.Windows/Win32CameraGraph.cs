using System.Runtime.Versioning;

namespace Puck.Platform.Windows;

/// <summary>
/// A camera graph whose native objects are owned by one MTA worker thread. Construction blocks until the worker
/// reports ready or fails; disposal asks the worker to stop and waits a bounded interval. A worker that outlives the
/// join (a driver call that never returns) keeps its native objects, so no pointer is ever released under a thread
/// still using it.
/// </summary>
[SupportedOSPlatform("windows")]
internal abstract class Win32CameraGraph<TStream> : ICameraGraph<TStream> where TStream : class, ICameraStream {
    private const int StopTimeoutMilliseconds = 2000;

    private readonly TaskCompletionSource<string?> m_ready = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    private int m_disposed;
    private volatile bool m_ended;
    private volatile bool m_stop;
    private Thread? m_thread;

    public abstract ICameraControlSurface Controls { get; }
    public bool IsEnded => m_ended;
    public abstract string Name { get; }
    public abstract IReadOnlyList<TStream> Streams { get; }

    protected bool Stopping => m_stop;

    public void Dispose() {
        if (0 != Interlocked.Exchange(location1: ref m_disposed, value: 1)) {
            return;
        }

        Stop();
    }

    /// <summary>Reports that the worker's negotiation failed; the constructor throws with this message.</summary>
    protected void Fail(string message) => _ = m_ready.TrySetResult(result: message);
    /// <summary>Reports that every stream is negotiated (and, where the tier demands it, proved live).</summary>
    protected void Ready() => _ = m_ready.TrySetResult(result: null);
    /// <summary>Starts the worker and blocks until it is ready; a failure or timeout stops the worker and throws.</summary>
    protected void RunWorker(string threadName, int readyTimeoutMilliseconds, string readyTimeoutMessage) {
        m_thread = new Thread(start: Worker) {
            IsBackground = true,
            Name = threadName,
        };
        m_thread.SetApartmentState(state: ApartmentState.MTA);
        m_thread.Start();

        var failure = (m_ready.Task.Wait(millisecondsTimeout: readyTimeoutMilliseconds)
            ? m_ready.Task.Result
            : readyTimeoutMessage
        );

        if (failure is not null) {
            Stop();

            throw new InvalidOperationException(message: failure);
        }
    }
    /// <summary>Wakes any wait the worker may be blocked in once a stop is requested.</summary>
    protected virtual void OnStopping() { }
    /// <summary>The worker body: negotiate, call <see cref="Ready"/>, stream until <see cref="Stopping"/>, and release
    /// every native object in its own <c>finally</c>.</summary>
    protected abstract void Work();

    private void Stop() {
        m_stop = true;
        OnStopping();

        if ((m_thread is { } thread) && !thread.Join(millisecondsTimeout: StopTimeoutMilliseconds)) {
            Console.Error.WriteLine(value: $"[camera] '{Name}' did not stop within {StopTimeoutMilliseconds} ms; its worker retains its native resources until the driver call returns.");
        }
    }
    private void Worker() {
        try {
            Work();
        } catch (Exception exception) {
            if (!m_stop) {
                Console.Error.WriteLine(value: $"[camera] '{Name}' stopped: {exception.Message}");
            }

            Fail(message: exception.Message);
        } finally {
            m_ended = true;
            Fail(message: "the camera graph ended before it was ready");
        }
    }
}
/// <summary>A CPU-pixel stream: the worker publishes tightly packed BGRA frames; the consumer copies the newest out.</summary>
internal sealed class Win32PixelStream(CameraSensor sensor, int width, int height, CameraCaptureFormat nativeFormat) : ICameraPixelStream {
    private byte[] m_pullBuffer = [];

    public LatestFrameBuffer Frames { get; } = new();
    public long FrameVersion => Frames.Version;
    public int Height => height;
    public long LastFrameTimestamp => Frames.LastTimestamp;
    public CameraCaptureFormat NativeFormat => nativeFormat;
    public CameraSensor Sensor => sensor;
    public int Width => width;

    public bool TryCapture(out Surface surface) {
        if (!Frames.TryGetLatest(destination: ref m_pullBuffer, height: out var frameHeight, width: out var frameWidth)) {
            surface = default;

            return false;
        }

        surface = Surface.CpuPixels(
            format: SurfaceFormat.B8G8R8A8Unorm,
            height: ((uint)frameHeight),
            pixels: m_pullBuffer,
            width: ((uint)frameWidth)
        );

        return true;
    }
}
/// <summary>A shared-texture stream: <see cref="Start"/> hands the consumer's targets to the worker through
/// <see cref="Targets"/>; the worker publishes completed slots.</summary>
internal sealed class Win32SharedStream(CameraSensor sensor, int width, int height, CameraCaptureFormat nativeFormat, SurfaceFormat targetFormat) : ICameraSharedStream {
    private readonly TaskCompletionSource<nint[]> m_targets = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    public long FrameVersion => Slots.Version;
    public int Height => height;
    public long LastFrameTimestamp => Slots.Timestamp;
    public int LatestSlot => Slots.LatestSlot;
    public CameraCaptureFormat NativeFormat => nativeFormat;
    public CameraSensor Sensor => sensor;
    public LatestSlotPublication Slots { get; } = new();
    public SurfaceFormat TargetFormat => targetFormat;
    public Task<nint[]> Targets => m_targets.Task;
    public int Width => width;

    public void CancelStart() => _ = m_targets.TrySetCanceled();
    public void Release(int slot) => Slots.Release(slot: slot);
    public void Start(IReadOnlyList<nint> sharedTargetHandles) {
        ArgumentNullException.ThrowIfNull(sharedTargetHandles);

        if (sharedTargetHandles.Count < 2) {
            throw new ArgumentException(message: "At least two shared targets are required.", paramName: nameof(sharedTargetHandles));
        }

        Slots.Configure(targetCount: sharedTargetHandles.Count);

        if (!m_targets.TrySetResult(result: [.. sharedTargetHandles])) {
            throw new InvalidOperationException(message: $"the {sensor} stream already started");
        }
    }
    public bool TryAcquireLatest(out int slot) => Slots.TryAcquireLatest(slot: out slot);
}
