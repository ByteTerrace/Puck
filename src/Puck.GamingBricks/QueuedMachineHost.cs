using System.Collections.Frozen;
using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Sources;

namespace Puck.GamingBricks;

/// <summary>
/// Provides the machine-neutral host surface shared by queued screen-machine adapters.
/// </summary>
public abstract class QueuedMachineHost : IMachineRuntime, IQueuedMachineRuntime, IMachineContentSlot,
    IMachineVideoOutputs, IMachineVideoOutput, IMachineAudioOutputs, IAudioMachine,
    IMachineInputPorts, IMachineInputPort, IFeedbackMachine, ITimeTravelMachine, IMachineCheckpointRuntime {
    private readonly QueuedMachineWorker m_worker;

    private MachinePadState m_input = MachinePadState.Neutral;
    private string? m_savePath;

    /// <summary>Initializes a queued screen-machine host.</summary>
    /// <param name="width">The native framebuffer width.</param>
    /// <param name="height">The native framebuffer height.</param>
    /// <param name="maximumPendingSteps">The maximum accepted but incomplete step count.</param>
    /// <param name="workerName">The worker thread's diagnostic name.</param>
    /// <param name="audioSampleRate">The requested audio sample rate, or zero to disable audio synthesis.</param>
    /// <param name="savePath">The initial battery-save path.</param>
    protected QueuedMachineHost(int width, int height, int maximumPendingSteps, string workerName, int audioSampleRate, string? savePath) {
        m_savePath = savePath;
        VideoOutputs = new Dictionary<string, IMachineVideoOutput> { ["video"] = this }.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        AudioOutputs = new Dictionary<string, IAudioMachine> { ["audio"] = this }.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        InputPorts = new Dictionary<string, IMachineInputPort> { ["controls"] = this }.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        m_worker = new QueuedMachineWorker(
            audioSampleRate: audioSampleRate,
            height: height,
            maximumPendingSteps: maximumPendingSteps,
            width: width,
            workerName: workerName
        );
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, IAudioMachine> AudioOutputs { get; }
    /// <inheritdoc/>
    public long BackpressureEvents => m_worker.BackpressureEvents;
    /// <inheritdoc/>
    public long CompletedSteps => m_worker.CompletedSteps;
    /// <inheritdoc/>
    public Vector3 EmittedLight => m_worker.EmittedLight;
    /// <inheritdoc/>
    public IReadOnlyDictionary<string, IMachineInputPort> InputPorts { get; }
    /// <inheritdoc/>
    public bool IsAssigned => m_worker.IsAssigned;
    /// <inheritdoc/>
    public int MaximumPendingSteps => m_worker.MaximumPendingSteps;
    /// <inheritdoc/>
    public float MotorLevel =>
        m_worker.MotorLevel;
    /// <inheritdoc/>
    /// <remarks>The worker writes RGBA8 frames.</remarks>
    public ImagePixelFormat Format => ImagePixelFormat.R8G8B8A8Unorm;
    /// <inheritdoc/>
    public int Height => m_worker.Height;
    /// <inheritdoc/>
    public int Width => m_worker.Width;
    /// <inheritdoc/>
    public long PendingSteps => m_worker.PendingSteps;
    /// <inheritdoc/>
    public string? QueueFault => m_worker.QueueFault;
    /// <inheritdoc/>
    public int SampleRate =>
        m_worker.AudioSampleRate;
    /// <inheritdoc/>
    public MachinePadState State => m_input;
    /// <inheritdoc/>
    public MachineRuntimeStatus Status => ((QueueFault is not null)
        ? MachineRuntimeStatus.Faulted
        : (IsAssigned
            ? MachineRuntimeStatus.Running
            : MachineRuntimeStatus.Empty
    ));
    /// <inheritdoc/>
    public TimeTravelStatus TimeTravelStatus =>
        m_worker.TimeTravelStatus;
    /// <inheritdoc/>
    public IReadOnlyDictionary<string, IMachineVideoOutput> VideoOutputs { get; }
    /// <summary>Gets the worker used by machine-specific interfaces and by the cable-link substrate, which lends this
    /// host's core to a <see cref="LinkedMachineGroup"/> through it.</summary>
    public QueuedMachineWorker Worker => m_worker;

    /// <summary>Creates a machine-specific core for newly loaded content.</summary>
    /// <param name="data">The content bytes.</param>
    /// <param name="savePath">The battery-save path.</param>
    /// <returns>The core owned by the worker.</returns>
    protected abstract IQueuedMachineCore CreateCore(byte[] data, string? savePath);

    /// <inheritdoc/>
    public bool Advance(ulong deltaTicks) => Step(
        deltaTicks: deltaTicks,
        input: in m_input
    );
    /// <inheritdoc/>
    public byte[] CaptureCheckpoint() => m_worker.CaptureCheckpoint().Encode(input: m_input);
    /// <inheritdoc/>
    public void Dispose() =>
        m_worker.Dispose();
    /// <inheritdoc/>
    public void Eject() =>
        m_worker.Eject();
    /// <inheritdoc/>
    public void FlushSave(bool force = false) =>
        m_worker.FlushSave(force: force);
    /// <inheritdoc/>
    public void LoadContent(byte[] data, string? savePath = null) {
        ArgumentNullException.ThrowIfNull(argument: data);

        m_savePath = savePath;
        m_worker.Load(core: CreateCore(
            data: data,
            savePath: m_savePath
        ));
    }
    /// <inheritdoc/>
    public int ReadSamples(Span<short> destination) =>
        m_worker.ReadAudioSamples(destination: destination);
    /// <inheritdoc/>
    public void RestoreCheckpoint(ReadOnlyMemory<byte> checkpoint) {
        var restored = QueuedMachineCheckpoint.Decode(bytes: checkpoint);

        m_worker.RestoreCheckpoint(checkpoint: restored.Checkpoint);
        m_input = restored.Input;
    }
    /// <inheritdoc/>
    public int RewindBy(int frames) =>
        m_worker.RewindBy(frames: frames);
    /// <inheritdoc/>
    public void SetFastForward(int factor) =>
        m_worker.SetFastForward(factor: factor);
    /// <inheritdoc/>
    public void SetRewindEnabled(bool enabled) =>
        m_worker.SetRewindEnabled(enabled: enabled);
    /// <inheritdoc/>
    public void SetRunahead(int frames) =>
        m_worker.SetRunahead(frames: frames);
    /// <inheritdoc/>
    public void SetState(in MachinePadState state) => m_input = state;
    /// <summary>Synchronously advances with an explicit controller image for standalone hardware callers.</summary>
    /// <param name="deltaTicks">The exact tick budget.</param>
    /// <param name="input">The controller image held throughout the budget.</param>
    /// <returns>Whether the machine advanced.</returns>
    public bool Step(ulong deltaTicks, in MachinePadState input) =>
        m_worker.Step(
            deltaTicks: deltaTicks,
            input: in input
        );
    /// <inheritdoc/>
    public QueuedMachineSubmission Submit(ulong deltaTicks) => Submit(
        deltaTicks: deltaTicks,
        input: in m_input
    );
    /// <summary>Queues an exact tick and controller segment for standalone hardware callers.</summary>
    /// <param name="deltaTicks">The exact tick budget.</param>
    /// <param name="input">The controller image captured by this submission.</param>
    /// <returns>The submission outcome, including producer backpressure.</returns>
    public QueuedMachineSubmission Submit(ulong deltaTicks, in MachinePadState input) =>
        m_worker.Submit(
            deltaTicks: deltaTicks,
            input: in input
        );
    /// <inheritdoc/>
    public long WriteFrame(Span<byte> region) =>
        m_worker.WriteFrame(region: region);
}
