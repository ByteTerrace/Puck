using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;

namespace Puck.Hosting;

/// <summary>
/// The machine-neutral queued-host substrate: one machine-owning worker thread executes exact tick/input segments in FIFO
/// order and swaps only complete native frames into a synchronized front buffer. A host wraps this around an
/// <see cref="IQueuedMachineCore"/> and forwards the neutral <see cref="IScreenMachine"/>/<see cref="IQueuedScreenMachine"/>
/// surface to it; both the SM83-family and the ARM7TDMI hosts are thin adapters over this one component.
/// <para>
/// The generic <see cref="Step"/> remains synchronous (submit-and-drain); hosts that recognize the queued capability use
/// <see cref="Submit"/> to keep commercial-ROM CPU work off their simulation/render pump while the pending-segment window
/// has capacity. A full window blocks the producer until one exact segment completes rather than dropping or coalescing
/// authoritative history. A blocked GPU upload leases an immutable complete frame without holding the worker's frame lock,
/// so it never stalls emulation. The save-flush debounce keys on native-frame transitions, so the interval means native
/// ~59.73 Hz frames regardless of submission cadence.
/// </para>
/// </summary>
public sealed class QueuedMachineWorker : IDisposable {
    // ~5 seconds at 59.73 fps between battery-save disk writes while dirty; counted in native frames, not work items.
    private const int SaveFlushIntervalFrames = 300;

    private static readonly Vector128<byte> RepackShuffle = Vector128.Create(
        (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15
    );
    private static readonly Vector128<byte> RepackAlpha = Vector128.Create(value: 0xFF000000u).AsByte();

    private readonly int m_width;
    private readonly int m_height;
    private readonly int m_frameByteLength;
    private readonly int m_maximumPendingSteps;
    private readonly int m_audioSampleRate;
    private readonly int m_audioCapacityFrames;
    private readonly string m_workerName;
    private readonly object m_frameLock = new();
    private readonly object m_uploadLock = new();
    private readonly object m_workLock = new();
    private readonly object m_audioLock = new();
    private readonly Queue<WorkItem> m_work;
    private readonly short[] m_audioRing;
    private byte[] m_rgbaFront;
    private byte[] m_rgbaBack;
    private byte[] m_rgbaSpare;
    private byte[]? m_uploadingFrame;
    private IQueuedMachineCore? m_core;
    private MachineTimeTravel<MachinePadState>? m_timeTravel;
    private int m_audioFrameCount;
    private int m_audioReadFrame;
    private int m_audioWriteFrame;
    private Thread? m_worker;
    private Exception? m_workerFault;
    private ulong m_cycleRemainder;
    private Vector3 m_emittedLight;
    private float m_motorLevel;
    private long m_frameVersion;
    private long m_publishedFrameVersion = -1L;
    private long m_submittedSteps;
    private long m_completedSteps;
    private long m_backpressureEvents;
    private bool m_acceptingWork;
    private int m_disposed;
    private IGpuSurfaceUpload? m_upload;
    private nint m_boundSourceView;

    /// <summary>Creates a worker sized for a fixed native framebuffer, staging an opaque black frame until a core is
    /// attached.</summary>
    /// <param name="width">The native framebuffer width in pixels.</param>
    /// <param name="height">The native framebuffer height in pixels.</param>
    /// <param name="maximumPendingSteps">The finite number of accepted-but-incomplete segments before the producer
    /// backpressures.</param>
    /// <param name="workerName">The background worker thread's diagnostic name.</param>
    /// <param name="audioSampleRate">The audio output rate in frames per emulated second the neutral
    /// <see cref="IAudioMachine"/> surface reports, or 0 (the default) when no consumer wants audio — a worker built
    /// with 0 never asks the attached core to synthesize audio, so it costs nothing. Fixed for the worker's lifetime,
    /// mirroring how a host opens (or does not open) a speaker device once at construction.</param>
    public QueuedMachineWorker(int width, int height, int maximumPendingSteps, string workerName, int audioSampleRate = 0) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: width, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: height, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: maximumPendingSteps, other: 1);
        ArgumentOutOfRangeException.ThrowIfNegative(value: audioSampleRate);
        ArgumentNullException.ThrowIfNull(argument: workerName);

        m_width = width;
        m_height = height;
        m_frameByteLength = ((width * height) * 4);
        m_maximumPendingSteps = maximumPendingSteps;
        m_audioSampleRate = audioSampleRate;
        m_audioCapacityFrames = Math.Max(val1: audioSampleRate, val2: 1); // one emulated second of stereo frames, unused (empty ring) while detached
        m_workerName = workerName;
        m_work = new Queue<WorkItem>(capacity: (maximumPendingSteps + 1));
        m_audioRing = ((audioSampleRate > 0) ? new short[(m_audioCapacityFrames * 2)] : []);
        m_rgbaFront = new byte[m_frameByteLength];
        m_rgbaBack = new byte[m_frameByteLength];
        m_rgbaSpare = new byte[m_frameByteLength];

        StageBlackFrame();
    }

    /// <summary>Gets whether a core is attached.</summary>
    public bool IsAssigned => (m_core is not null);

    /// <summary>Gets the native image-view handle of the published framebuffer, or 0 before the first publish (or after a
    /// device loss).</summary>
    public nint NativeImageViewHandle => m_boundSourceView;

    /// <summary>Gets the light the framebuffer emits — its average color, normalized 0..1.</summary>
    public Vector3 EmittedLight {
        get {
            lock (m_frameLock) {
                return m_emittedLight;
            }
        }
    }

    /// <summary>Gets the attached cartridge's current rumble motor level, 0..1 — the neutral <see cref="IFeedbackMachine"/>
    /// surface, sampled on the worker thread after each completed step and published under the same lock as the
    /// framebuffer. Zero while no core is attached or the loaded cartridge has no rumble hardware.</summary>
    public float MotorLevel {
        get {
            lock (m_frameLock) {
                return m_motorLevel;
            }
        }
    }

    /// <summary>Gets the number of accepted segments whose emulation has completed.</summary>
    public long CompletedSteps {
        get {
            lock (m_workLock) {
                return m_completedSteps;
            }
        }
    }

    /// <summary>Gets the number of accepted segments not yet completed, including one currently executing.</summary>
    public long PendingSteps {
        get {
            lock (m_workLock) {
                return Math.Max(val1: 0L, val2: (m_submittedSteps - m_completedSteps));
            }
        }
    }

    /// <summary>Gets the finite pending-segment capacity.</summary>
    public int MaximumPendingSteps =>
        m_maximumPendingSteps;

    /// <summary>Gets the number of submissions that waited for capacity since the current core was attached.</summary>
    public long BackpressureEvents {
        get {
            lock (m_workLock) {
                return m_backpressureEvents;
            }
        }
    }

    /// <summary>Gets the worker's configured audio output rate in frames per emulated second — the neutral
    /// <see cref="IAudioMachine.SampleRate"/> surface — or 0 when this worker was built with no audio consumer.</summary>
    public int AudioSampleRate =>
        m_audioSampleRate;

    /// <summary>Gets a worker fault description, or <see langword="null"/> while the queue is healthy.</summary>
    public string? QueueFault {
        get {
            lock (m_workLock) {
                return ((m_workerFault is { } fault) ? $"{fault.GetType().Name}: {fault.Message}" : null);
            }
        }
    }

    /// <summary>Attaches a freshly built and booted core: stops any running worker (draining accepted history), disposes
    /// the previous core, stages the new core's first frame, and starts its worker thread.</summary>
    /// <param name="core">The booted core to run. The worker takes ownership and disposes it on the next
    /// <see cref="Load"/>/<see cref="Eject"/>/<see cref="Dispose"/>.</param>
    public void Load(IQueuedMachineCore core) {
        ArgumentNullException.ThrowIfNull(argument: core);

        DetachCore();
        m_core = core;
        m_timeTravel = new MachineTimeTravel<MachinePadState>(core: core, cyclesPerSecond: core.CyclesPerSecond);
        m_cycleRemainder = 0UL;

        lock (m_frameLock) {
            m_motorLevel = 0f;
        }
        core.ConfigureAudio(sampleRate: m_audioSampleRate);
        ResetAudioRing();
        StageMachineFrame(core: core);
        StartWorker(core: core);
    }

    /// <summary>Detaches the core (draining accepted history and disposing it) and returns the framebuffer to black.</summary>
    public void Eject() {
        if (m_core is null) {
            return;
        }

        DetachCore();
        StageBlackFrame();
        ResetAudioRing();
    }

    /// <summary>Advances the machine by one fixed-step tick budget holding <paramref name="input"/>, then stages a fresh
    /// framebuffer — the synchronous submit-and-drain convenience for generic callers.</summary>
    /// <param name="deltaTicks">The frame's fixed-step tick budget.</param>
    /// <param name="input">The controller image held over the budget.</param>
    /// <returns><see langword="true"/> when the machine stepped.</returns>
    public bool Step(ulong deltaTicks, in MachinePadState input) {
        if (EnqueueStep(deltaTicks: deltaTicks, input: in input, forceStage: true) == QueuedMachineSubmission.Rejected) {
            ThrowIfWorkerFaulted();

            return false;
        }

        DrainWorker();

        return true;
    }

    /// <summary>Accepts one exact tick/input segment for ordered execution, applying producer backpressure at capacity.</summary>
    /// <param name="deltaTicks">The segment's fixed-step tick budget.</param>
    /// <param name="input">The controller image held for the whole segment.</param>
    /// <returns>The observable submission outcome.</returns>
    public QueuedMachineSubmission Submit(ulong deltaTicks, in MachinePadState input) =>
        EnqueueStep(deltaTicks: deltaTicks, input: in input, forceStage: false);

    /// <summary>Drains buffered audio from the worker's OWN ring — filled on the worker thread from the attached
    /// core's presentation-side ring after each completed segment — into <paramref name="destination"/>, so a
    /// consumer reading off-thread never touches the emulation thread. The neutral <see cref="IAudioMachine.ReadSamples"/>
    /// surface. Always returns 0 when this worker was built with no audio consumer (<see cref="AudioSampleRate"/> 0).</summary>
    /// <param name="destination">The interleaved left/right buffer to fill.</param>
    /// <returns>The number of samples written (always even).</returns>
    public int ReadAudioSamples(Span<short> destination) {
        if (m_audioSampleRate == 0) {
            return 0;
        }

        lock (m_audioLock) {
            var frames = Math.Min(val1: (destination.Length / 2), val2: m_audioFrameCount);

            for (var frame = 0; (frame < frames); ++frame) {
                var index = (m_audioReadFrame * 2);

                destination[(frame * 2)] = m_audioRing[index];
                destination[((frame * 2) + 1)] = m_audioRing[(index + 1)];
                m_audioReadFrame = ((m_audioReadFrame + 1) % m_audioCapacityFrames);
            }

            m_audioFrameCount -= frames;

            return (frames * 2);
        }
    }

    private QueuedMachineSubmission EnqueueStep(ulong deltaTicks, in MachinePadState input, bool forceStage) {
        if ((0 != Volatile.Read(location: ref m_disposed)) || (m_worker is null) || (0UL == deltaTicks)) {
            return QueuedMachineSubmission.Rejected;
        }

        var backpressured = false;

        lock (m_workLock) {
            while (m_acceptingWork && (m_workerFault is null) &&
                   ((m_submittedSteps - m_completedSteps) >= m_maximumPendingSteps)) {
                if (!backpressured) {
                    backpressured = true;

                    if (m_backpressureEvents < long.MaxValue) {
                        ++m_backpressureEvents;
                    }
                }

                Monitor.Wait(obj: m_workLock);
            }

            if (!m_acceptingWork || (m_workerFault is not null)) {
                return QueuedMachineSubmission.Rejected;
            }

            m_work.Enqueue(item: WorkItem.Step(deltaTicks: deltaTicks, input: in input, forceStage: forceStage));
            ++m_submittedSteps;
            Monitor.Pulse(obj: m_workLock);
        }

        return (backpressured
            ? QueuedMachineSubmission.AcceptedAfterBackpressure
            : QueuedMachineSubmission.Accepted);
    }

    /// <summary>Uploads the staged framebuffer to a shader-readable GPU image and (re)binds
    /// <see cref="NativeImageViewHandle"/>. A blocked upload leases an immutable complete frame without holding the frame
    /// lock, so the worker keeps publishing newer frames while it runs; concurrent publishes serialize.</summary>
    /// <param name="deviceContext">The GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    public void PublishFrame(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        ArgumentNullException.ThrowIfNull(argument: deviceContext);
        ArgumentNullException.ThrowIfNull(argument: gpu);

        if (0 != Volatile.Read(location: ref m_disposed)) {
            return;
        }

        lock (m_uploadLock) {
            if (0 != Volatile.Read(location: ref m_disposed)) {
                return;
            }

            byte[] pixels;
            long frameVersion;

            lock (m_frameLock) {
                if ((0 != m_boundSourceView) && (m_publishedFrameVersion == m_frameVersion)) {
                    return;
                }

                pixels = m_rgbaFront;
                frameVersion = m_frameVersion;
                m_uploadingFrame = pixels;
            }

            try {
                m_upload ??= gpu.SurfaceTransferFactory.CreateUpload(deviceContext: deviceContext);
                m_boundSourceView = m_upload.Upload(
                    deviceContext: deviceContext,
                    format: GpuPixelFormat.R8G8B8A8Unorm,
                    height: (uint)m_height,
                    pixels: pixels,
                    width: (uint)m_width
                );
                m_publishedFrameVersion = frameVersion;
            } finally {
                lock (m_frameLock) {
                    m_uploadingFrame = null;
                }
            }
        }
    }

    /// <summary>Drops the GPU upload after a device loss: the next <see cref="PublishFrame"/> rebuilds it on the fresh
    /// device. The core's CPU state survives untouched.</summary>
    public void NotifyDeviceLost() {
        lock (m_uploadLock) {
            m_upload?.Dispose();
            m_upload = null;
            m_boundSourceView = 0;
            m_publishedFrameVersion = -1L;
        }
    }

    /// <summary>Flushes the attached core's persistent save (through the worker when one is running, so it serializes with
    /// emulation), a no-op when no core is attached.</summary>
    /// <param name="force">When <see langword="true"/>, flush even when only a clock-style change is pending.</param>
    public void FlushSave(bool force = false) {
        var worker = m_worker;

        if (worker is null) {
            m_core?.FlushSave(force: force);

            return;
        }

        using var completion = new ManualResetEventSlim(initialState: false);
        var queued = false;

        lock (m_workLock) {
            if (m_acceptingWork && (m_workerFault is null)) {
                m_work.Enqueue(item: WorkItem.Flush(force: force, completion: completion));
                Monitor.Pulse(obj: m_workLock);
                queued = true;
            }
        }

        if (queued) {
            completion.Wait();
            ThrowIfWorkerFaulted();
        } else {
            worker.Join();
            m_core?.FlushSave(force: force);
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (0 != Interlocked.Exchange(location1: ref m_disposed, value: 1)) {
            return;
        }

        DetachCore();

        lock (m_uploadLock) {
            m_upload?.Dispose();
            m_upload = null;
            m_boundSourceView = 0;
        }
    }

    private void StartWorker(IQueuedMachineCore core) {
        lock (m_workLock) {
            m_work.Clear();
            m_workerFault = null;
            m_submittedSteps = 0L;
            m_completedSteps = 0L;
            m_backpressureEvents = 0L;
            m_acceptingWork = true;
        }

        m_worker = new Thread(start: () => WorkerLoop(core: core)) {
            IsBackground = true,
            Name = m_workerName,
        };
        m_worker.Start();
    }

    // Stop accepting, append an ordered stop marker so the worker drains every already-accepted tick/input and flush item
    // before it acknowledges shutdown (load/eject/dispose never discard deterministic history), join it, then dispose the
    // core (a forced final save flush rides its Dispose).
    private void DetachCore() {
        var worker = m_worker;

        if (worker is not null) {
            using var completion = new ManualResetEventSlim(initialState: false);
            var queued = false;

            lock (m_workLock) {
                m_acceptingWork = false;
                Monitor.PulseAll(obj: m_workLock);

                if (m_workerFault is null) {
                    m_work.Enqueue(item: WorkItem.Stop(completion: completion));
                    Monitor.Pulse(obj: m_workLock);
                    queued = true;
                }
            }

            if (queued) {
                completion.Wait();
            }

            worker.Join();
            m_worker = null;
        }

        if (m_timeTravel is { } timeTravel) {
            m_timeTravel = null;
            timeTravel.Dispose();
        }

        if (m_core is { } core) {
            m_core = null;
            core.Dispose();
        }
    }

    private void DrainWorker() {
        using var completion = new ManualResetEventSlim(initialState: false);

        lock (m_workLock) {
            if ((m_workerFault is not null) || !m_acceptingWork) {
                ThrowIfWorkerFaultedLocked();

                return;
            }

            m_work.Enqueue(item: WorkItem.Barrier(completion: completion));
            Monitor.Pulse(obj: m_workLock);
        }

        completion.Wait();
        ThrowIfWorkerFaulted();
    }

    private void WorkerLoop(IQueuedMachineCore core) {
        var current = default(WorkItem);
        var stagedNativeFrame = core.NativeFrameIndex;
        var lastFlushNativeFrame = core.NativeFrameIndex;

        try {
            while (true) {
                current = TakeWork();

                switch (current.Kind) {
                    case WorkKind.Step:
                        var input = current.Input;
                        var budget = checked((long)TakeCycleBudget(core: core, ticks: current.DeltaTicks));

                        core.ApplyInput(input: in input);
                        core.RunCycles(cycles: budget);

                        lock (m_frameLock) {
                            m_motorLevel = core.MotorLevel;
                        }

                        // Time-travel is host state outside the emulation path: record the just-advanced frame into the
                        // rewind ring (when armed) and keep the persistent lookahead ahead on the held input (when
                        // runahead is armed). Both only READ the real machine, so its trajectory is untouched. A no-op
                        // (single null check) while both features are off — the battery/steady-state path.
                        if (m_timeTravel is { } timeTravel) {
                            timeTravel.Record(input: in input, budget: budget, hostAccumulator: m_cycleRemainder);
                            timeTravel.AdvanceLookahead(predicted: in input);
                        }

                        // Detached (m_audioSampleRate 0) skips this call entirely rather than draining an always-empty
                        // ring: the core never synthesized anything to drain (see ConfigureAudio's zero-cost contract).
                        if (m_audioSampleRate > 0) {
                            DrainAudio(core: core);
                        }

                        var nativeFrame = core.NativeFrameIndex;

                        // Exact input/tick segments still run at the host's fixed-step rate, but pixels are repacked only
                        // when the core completes a native ~59.7 Hz frame. The synchronous Step path forces a stage to
                        // retain its generic contract; queued submissions avoid redundant framebuffer scans.
                        if (current.ForceStage || (nativeFrame != stagedNativeFrame)) {
                            StageMachineFrame(core: core);
                            stagedNativeFrame = nativeFrame;
                        }

                        lock (m_workLock) {
                            ++m_completedSteps;
                            Monitor.PulseAll(obj: m_workLock);
                        }

                        // A3 fix: the interval means native frames, not submitted work items — one native-frame count that
                        // is independent of how many exact segments each frame took.
                        if ((nativeFrame - lastFlushNativeFrame) >= SaveFlushIntervalFrames) {
                            lastFlushNativeFrame = nativeFrame;
                            core.FlushSave(force: false);
                        }

                        break;
                    case WorkKind.Flush:
                        core.FlushSave(force: current.ForceFlush);
                        current.Completion!.Set();
                        break;
                    case WorkKind.TimeTravel:
                        ExecuteTimeTravel(core: core, request: current.TimeTravel!);
                        current.Completion!.Set();
                        break;
                    case WorkKind.Memory:
                        ExecuteMemoryAccess(core: core, request: current.Memory!);
                        current.Completion!.Set();
                        break;
                    case WorkKind.Reconfigure:
                        ExecuteReconfigure(core: core, request: current.Reconfigure!);
                        current.Completion!.Set();
                        break;
                    case WorkKind.Barrier:
                        current.Completion!.Set();
                        break;
                    case WorkKind.Stop:
                        current.Completion!.Set();

                        return;
                }
            }
        } catch (Exception exception) {
            current.Completion?.Set();

            lock (m_workLock) {
                m_workerFault = exception;
                m_acceptingWork = false;

                while (m_work.TryDequeue(result: out var abandoned)) {
                    abandoned.Completion?.Set();
                }

                Monitor.PulseAll(obj: m_workLock);
            }

            Console.Error.WriteLine(value: $"[{m_workerName}] worker stopped ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private WorkItem TakeWork() {
        lock (m_workLock) {
            while (m_work.Count == 0) {
                Monitor.Wait(obj: m_workLock);
            }

            return m_work.Dequeue();
        }
    }

    private void ThrowIfWorkerFaulted() {
        lock (m_workLock) {
            ThrowIfWorkerFaultedLocked();
        }
    }

    private void ThrowIfWorkerFaultedLocked() {
        if (m_workerFault is { } fault) {
            throw new InvalidOperationException(message: $"The {m_workerName} worker faulted.", innerException: fault);
        }
    }

    // Consume a tick budget against the exact integer accumulator and return the machine-cycle budget it buys under the
    // core's current rate. Carried on the worker thread so a rate that tracks emulated state (a clock-multiplier latch) is
    // read consistently with the cycles it gates.
    private ulong TakeCycleBudget(IQueuedMachineCore core, ulong ticks) {
        // Fast-forward is a host-level multiplier on the per-frame cycle budget (never a timing hack inside the core): the
        // machine advances FastForwardFactor frames of emulated time per submitted frame, and only the final frame is
        // staged/published, so presentation frames are skipped. Read on the worker thread; mutated only via the queue.
        var factor = (ulong)(m_timeTravel?.FastForwardFactor ?? 1);
        var scaled = checked(((ticks * factor) * core.CyclesPerSecond) + m_cycleRemainder);

        m_cycleRemainder = (scaled % EngineTicks.PerSecond);

        return (scaled / EngineTicks.PerSecond);
    }

    // Drains the attached core's own presentation-side ring into the worker's host-readable ring, on the worker
    // thread only — a consumer's ReadAudioSamples call never touches the core. The scratch span is stack-allocated
    // (zero heap allocation, matching the substrate's zero-alloc steady state); looping until a short read handles a
    // segment large enough to have produced more than one scratch's worth since the last drain.
    private void DrainAudio(IQueuedMachineCore core) {
        Span<short> scratch = stackalloc short[512];
        int written;

        do {
            written = core.DrainAudioSamples(destination: scratch);

            if (written > 0) {
                PushAudioFrames(samples: scratch[..written]);
            }
        } while (written == scratch.Length);
    }

    // Appends drained stereo frames to the worker's ring; when full, the oldest frame is dropped so the ring always
    // holds the newest emulated second (mirrors the cores' own ring-drop-oldest discipline).
    private void PushAudioFrames(ReadOnlySpan<short> samples) {
        lock (m_audioLock) {
            for (var index = 0; (index < samples.Length); index += 2) {
                if (m_audioFrameCount == m_audioCapacityFrames) {
                    m_audioReadFrame = ((m_audioReadFrame + 1) % m_audioCapacityFrames);
                    --m_audioFrameCount;
                }

                var writeIndex = (m_audioWriteFrame * 2);

                m_audioRing[writeIndex] = samples[index];
                m_audioRing[(writeIndex + 1)] = samples[(index + 1)];
                m_audioWriteFrame = ((m_audioWriteFrame + 1) % m_audioCapacityFrames);
                ++m_audioFrameCount;
            }
        }
    }

    // A restored/freshly loaded core starts a fresh audio stream, exactly like the cores' own LoadState behavior —
    // never replay stale output across a cart swap.
    private void ResetAudioRing() {
        lock (m_audioLock) {
            m_audioFrameCount = 0;
            m_audioReadFrame = 0;
            m_audioWriteFrame = 0;
        }
    }

    private void StageMachineFrame(IQueuedMachineCore core) {
        // Present the runahead lookahead's framebuffer while it is live and primed; otherwise the authoritative machine's
        // own. The real machine stays the tick-locked audio authority either way.
        var pixels = ((m_timeTravel is { } timeTravel) && timeTravel.TryGetDisplayFramebuffer(framebuffer: out var lookahead))
            ? lookahead
            : core.Framebuffer;
        var light = RepackFramebuffer(pixels: pixels, target: m_rgbaBack);

        PublishBackBuffer(light: light);
    }

    private void PublishBackBuffer(Vector3 light) {
        lock (m_frameLock) {
            var previousFront = m_rgbaFront;

            m_rgbaFront = m_rgbaBack;

            // Upload consumes its leased source synchronously by contract, but the worker may publish several newer frames
            // before that call returns. Keep the leased array out of the worker's write rotation until release; the fixed
            // spare makes this bounded and allocation-free without holding the frame lock during GPU work.
            if (ReferenceEquals(objA: previousFront, objB: m_uploadingFrame)) {
                m_rgbaBack = m_rgbaSpare;
                m_rgbaSpare = previousFront;
            } else {
                m_rgbaBack = previousFront;
            }

            m_emittedLight = light;
            ++m_frameVersion;
        }
    }

    private void StageBlackFrame() {
        Array.Clear(array: m_rgbaBack);

        for (var offset = 3; (offset < m_rgbaBack.Length); offset += 4) {
            m_rgbaBack[offset] = 0xFF;
        }

        PublishBackBuffer(light: Vector3.Zero);

        lock (m_frameLock) {
            m_motorLevel = 0f;
        }
    }

    // Repack the framebuffer's 0x00RRGGBB pixels as opaque R,G,B,A bytes into the reused staging array and compute the
    // average color (the emitted light). One integer Vector128 shuffle+widen pass serves both hosts, bit-exact against the
    // scalar tail: the OR with 0xFF forces every alpha byte regardless of the source high byte, matching the scalar write.
    private static Vector3 RepackFramebuffer(ReadOnlySpan<uint> pixels, byte[] target) {
        var count = pixels.Length;
        var index = 0;
        long sumRed = 0;
        long sumGreen = 0;
        long sumBlue = 0;

        if (Vector128.IsHardwareAccelerated && (count >= Vector128<uint>.Count)) {
            var source = MemoryMarshal.Cast<uint, byte>(span: pixels);
            var accumulator = Vector128<uint>.Zero;
            var vectorCount = (count - (count % Vector128<uint>.Count));

            for (; (index < vectorCount); index += Vector128<uint>.Count) {
                var src = Vector128.LoadUnsafe(source: ref MemoryMarshal.GetReference(span: source), elementOffset: (nuint)(index * 4));
                var packed = (Vector128.Shuffle(vector: src, indices: RepackShuffle) | RepackAlpha);

                packed.StoreUnsafe(destination: ref target[index * 4]);

                var (widenedLow, widenedHigh) = Vector128.Widen(source: src);
                var partial = (widenedLow + widenedHigh);
                var (channelLow, channelHigh) = Vector128.Widen(source: partial);

                accumulator += (channelLow + channelHigh);
            }

            // Lanes are byte-position sums across the packed quad: lane 0 is blue, lane 1 green, lane 2 red (lane 3 the
            // discarded high byte).
            sumBlue = accumulator[0];
            sumGreen = accumulator[1];
            sumRed = accumulator[2];
        }

        for (; (index < count); ++index) {
            var offset = (index * 4);
            var pixel = pixels[index];
            var red = (byte)(pixel >> 16);
            var green = (byte)(pixel >> 8);
            var blue = (byte)pixel;

            target[offset] = red;
            target[(offset + 1)] = green;
            target[(offset + 2)] = blue;
            target[(offset + 3)] = 0xFF;
            sumRed += red;
            sumGreen += green;
            sumBlue += blue;
        }

        var scale = (1f / (255f * count));

        return new Vector3(x: (sumRed * scale), y: (sumGreen * scale), z: (sumBlue * scale));
    }

    // Executes one marshaled time-travel command on the worker thread (single-producer with emulation); a rewind that
    // moved the machine re-stages a fresh framebuffer so the pane reflects the landing.
    private void ExecuteTimeTravel(IQueuedMachineCore core, TimeTravelRequest request) {
        if (m_timeTravel is not { } timeTravel) {
            request.Status = default;
            request.IntResult = 0;

            return;
        }

        switch (request.Op) {
            case TimeTravelOp.SetRewindEnabled:
                timeTravel.SetRewindEnabled(enabled: (request.Arg != 0));
                break;
            case TimeTravelOp.RewindBy:
                request.IntResult = timeTravel.RewindBy(frames: request.Arg, hostAccumulator: out var landedAccumulator);

                if (request.IntResult > 0) {
                    // The machine jumped to a past instant: restore the tick-to-cycle accumulator phase that frame was
                    // produced under (atomic with the core, so identical future ticks buy identical budgets — H-04),
                    // clear the host audio ring and republish the motor level from the restored core so no consumer
                    // hears samples or feels rumble from the abandoned future (M-01), then re-stage the landed frame.
                    m_cycleRemainder = landedAccumulator;

                    ResetAudioRing();

                    lock (m_frameLock) {
                        m_motorLevel = core.MotorLevel;
                    }

                    StageMachineFrame(core: core);
                }

                break;
            case TimeTravelOp.SetRunahead:
                timeTravel.SetRunahead(frames: request.Arg);
                break;
            case TimeTravelOp.SetFastForward:
                timeTravel.SetFastForward(factor: request.Arg);
                break;
            case TimeTravelOp.Status:
                break;
        }

        request.Status = timeTravel.GetStatus();
    }

    // Executes one marshaled debug memory access on the worker thread, between steps — so a peek observes a coherent
    // inter-instruction snapshot and a poke never lands mid-instruction or races a load/eject. A poke drops the rewind
    // ring in the SAME work item as the mutation (atomic order, not mutate-then-queue): the poked byte is an unrecorded
    // input the history could no longer reconstruct.
    private void ExecuteMemoryAccess(IQueuedMachineCore core, MemoryRequest request) {
        if (request.IsWrite) {
            core.PokeByte(address: request.Address, value: request.Value);
            m_timeTravel?.Reset();
        } else {
            request.Result = core.PeekByte(address: request.Address);
        }
    }

    // Executes one marshaled live-reconfigure on the worker thread, between steps — so the model swap observes a coherent
    // inter-instruction boundary and never lands mid-instruction (Machine.SwitchModel's own contract). A successful swap
    // changes the machine's identity, so the rewind ring's snapshots no longer restore into it: drop the history in the
    // SAME work item as the swap (atomic order), then re-stage the framebuffer so the pane reflects the retarget.
    private void ExecuteReconfigure(IQueuedMachineCore core, ReconfigureRequest request) {
        request.Ok = core.Reconfigure(options: request.Options, reason: out var reason);
        request.Reason = reason;

        if (request.Ok) {
            m_timeTravel?.Reset();
            StageMachineFrame(core: core);
        }
    }

    /// <summary>Reconfigures the attached core live across the engine's options vocabulary (marshaled between steps), so a
    /// host's <see cref="Puck.Abstractions.Machines.IReconfigurableMachine.TryReconfigure"/> never lands the swap
    /// mid-instruction or races the running core. Draining the queue first is unnecessary — the request is FIFO-ordered
    /// behind every accepted step, so it executes only after they complete. Returns a failure with a reason when no core
    /// is attached or the core rejects the options.</summary>
    /// <param name="options">The engine-specific options string, or <see langword="null"/> for defaults.</param>
    /// <returns>Whether the reconfigure was accepted, and the engine's reason/advisory text.</returns>
    public (bool Ok, string Reason) Reconfigure(string? options) {
        var request = new ReconfigureRequest { Options = options };
        var worker = m_worker;

        if (worker is null) {
            return (Ok: false, Reason: "no machine to reconfigure");
        }

        using var completion = new ManualResetEventSlim(initialState: false);
        var queued = false;

        lock (m_workLock) {
            if (m_acceptingWork && (m_workerFault is null)) {
                m_work.Enqueue(item: WorkItem.ForReconfigure(request: request, completion: completion));
                Monitor.Pulse(obj: m_workLock);
                queued = true;
            }
        }

        if (queued) {
            completion.Wait();
        }

        return (Ok: request.Ok, Reason: request.Reason);
    }

    // Marshals one debug memory access onto the worker thread (the single-producer discipline: peek/poke touch the same
    // core arrays/mapper state the worker mutates while stepping, so they must never be driven cross-thread), blocking
    // until it completes between steps. A no-op leaving the default result (peek 0) when no core is attached.
    private void RunMemoryAccess(MemoryRequest request) {
        var worker = m_worker;

        if (worker is null) {
            return;
        }

        using var completion = new ManualResetEventSlim(initialState: false);
        var queued = false;

        lock (m_workLock) {
            if (m_acceptingWork && (m_workerFault is null)) {
                m_work.Enqueue(item: WorkItem.ForMemory(request: request, completion: completion));
                Monitor.Pulse(obj: m_workLock);
                queued = true;
            }
        }

        if (queued) {
            completion.Wait();
        }
    }

    /// <summary>Reads one byte from the attached core's bus address space through the worker (marshaled between steps),
    /// so a host's <see cref="IMachineMemoryPeek.PeekByte"/> never races the running core. Returns 0 when no core is
    /// attached.</summary>
    /// <param name="address">A machine-defined bus address.</param>
    /// <returns>The byte at that address, or 0.</returns>
    public byte PeekByte(int address) {
        var request = new MemoryRequest { Address = address, IsWrite = false };

        RunMemoryAccess(request: request);

        return request.Result;
    }

    /// <summary>Forces one byte into the attached core's bus address space through the worker (marshaled between steps),
    /// dropping the rewind history atomically with the mutation, so a host's <see cref="IMachineMemoryPeek.PokeByte"/>
    /// never lands mid-instruction. A no-op when no core is attached.</summary>
    /// <param name="address">A machine-defined bus address.</param>
    /// <param name="value">The byte to store.</param>
    public void PokeByte(int address, byte value) =>
        RunMemoryAccess(request: new MemoryRequest { Address = address, Value = value, IsWrite = true });

    // Marshals a time-travel command onto the worker thread (the single-producer discipline: rewind/runahead manipulate
    // machine state and must never be driven cross-thread), blocking until it completes. A no-op returning a default
    // status when no core is attached.
    private TimeTravelRequest RunTimeTravel(TimeTravelOp op, int arg) {
        var request = new TimeTravelRequest { Op = op, Arg = arg };
        var worker = m_worker;

        if (worker is null) {
            return request;
        }

        using var completion = new ManualResetEventSlim(initialState: false);
        var queued = false;

        lock (m_workLock) {
            if (m_acceptingWork && (m_workerFault is null)) {
                m_work.Enqueue(item: WorkItem.ForTimeTravel(request: request, completion: completion));
                Monitor.Pulse(obj: m_workLock);
                queued = true;
            }
        }

        if (queued) {
            completion.Wait();
        }

        return request;
    }

    /// <summary>Arms or disarms the rewind ring (marshaled onto the worker thread).</summary>
    /// <param name="enabled">Whether to capture rewind history.</param>
    public void SetRewindEnabled(bool enabled) =>
        _ = RunTimeTravel(op: TimeTravelOp.SetRewindEnabled, arg: (enabled ? 1 : 0));

    /// <summary>Rewinds the machine backward by up to <paramref name="frames"/> native frames (marshaled onto the worker
    /// thread).</summary>
    /// <param name="frames">The number of native frames to move backward.</param>
    /// <returns>The number of native frames actually rewound.</returns>
    public int RewindBy(int frames) =>
        RunTimeTravel(op: TimeTravelOp.RewindBy, arg: frames).IntResult;

    /// <summary>Arms (0 disarms) runahead at <paramref name="frames"/> frames ahead (marshaled onto the worker thread).</summary>
    /// <param name="frames">The number of frames to run ahead, or 0 to disarm.</param>
    public void SetRunahead(int frames) =>
        _ = RunTimeTravel(op: TimeTravelOp.SetRunahead, arg: frames);

    /// <summary>Sets the fast-forward factor (marshaled onto the worker thread).</summary>
    /// <param name="factor">The cycle-budget multiplier (clamped to at least 1).</param>
    public void SetFastForward(int factor) =>
        _ = RunTimeTravel(op: TimeTravelOp.SetFastForward, arg: factor);

    /// <summary>Gets a one-instant read of the time-travel state (marshaled onto the worker thread).</summary>
    public TimeTravelStatus TimeTravelStatus =>
        RunTimeTravel(op: TimeTravelOp.Status, arg: 0).Status;

    private enum WorkKind {
        Step,
        Flush,
        Barrier,
        Stop,
        TimeTravel,
        Memory,
        Reconfigure,
    }

    private enum TimeTravelOp {
        SetRewindEnabled,
        RewindBy,
        SetRunahead,
        SetFastForward,
        Status,
    }

    // A marshaled debug memory access + its result box, filled on the worker thread and read by the producer after the
    // barrier completes.
    private sealed class MemoryRequest {
        public int Address;
        public byte Value;
        public bool IsWrite;
        public byte Result;
    }

    // A marshaled time-travel command + its result box, filled on the worker thread and read by the producer after the
    // barrier completes.
    private sealed class TimeTravelRequest {
        public TimeTravelOp Op;
        public int Arg;
        public int IntResult;
        public TimeTravelStatus Status;
    }

    // A marshaled live-reconfigure request + its result box, filled on the worker thread (between steps, so the swap
    // never lands mid-instruction) and read by the producer after the barrier completes.
    private sealed class ReconfigureRequest {
        public string? Options;
        public bool Ok;
        public string Reason = string.Empty;
    }

    private readonly record struct WorkItem(
        WorkKind Kind,
        ulong DeltaTicks,
        MachinePadState Input,
        bool ForceStage,
        bool ForceFlush,
        ManualResetEventSlim? Completion,
        TimeTravelRequest? TimeTravel,
        MemoryRequest? Memory,
        ReconfigureRequest? Reconfigure
    ) {
        public static WorkItem Step(ulong deltaTicks, in MachinePadState input, bool forceStage) =>
            new(Kind: WorkKind.Step, DeltaTicks: deltaTicks, Input: input, ForceStage: forceStage, ForceFlush: false, Completion: null, TimeTravel: null, Memory: null, Reconfigure: null);

        public static WorkItem Flush(bool force, ManualResetEventSlim completion) =>
            new(Kind: WorkKind.Flush, DeltaTicks: 0UL, Input: default, ForceStage: false, ForceFlush: force, Completion: completion, TimeTravel: null, Memory: null, Reconfigure: null);

        public static WorkItem Barrier(ManualResetEventSlim completion) =>
            new(Kind: WorkKind.Barrier, DeltaTicks: 0UL, Input: default, ForceStage: false, ForceFlush: false, Completion: completion, TimeTravel: null, Memory: null, Reconfigure: null);

        public static WorkItem Stop(ManualResetEventSlim completion) =>
            new(Kind: WorkKind.Stop, DeltaTicks: 0UL, Input: default, ForceStage: false, ForceFlush: false, Completion: completion, TimeTravel: null, Memory: null, Reconfigure: null);

        public static WorkItem ForTimeTravel(TimeTravelRequest request, ManualResetEventSlim completion) =>
            new(Kind: WorkKind.TimeTravel, DeltaTicks: 0UL, Input: default, ForceStage: false, ForceFlush: false, Completion: completion, TimeTravel: request, Memory: null, Reconfigure: null);

        public static WorkItem ForMemory(MemoryRequest request, ManualResetEventSlim completion) =>
            new(Kind: WorkKind.Memory, DeltaTicks: 0UL, Input: default, ForceStage: false, ForceFlush: false, Completion: completion, TimeTravel: null, Memory: request, Reconfigure: null);

        public static WorkItem ForReconfigure(ReconfigureRequest request, ManualResetEventSlim completion) =>
            new(Kind: WorkKind.Reconfigure, DeltaTicks: 0UL, Input: default, ForceStage: false, ForceFlush: false, Completion: completion, TimeTravel: null, Memory: null, Reconfigure: request);
    }
}
