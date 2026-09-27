using System.Collections.Frozen;
using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Sources;

namespace Puck.Machines;

/// <summary>
/// Provides the machine-neutral host surface shared by queued screen-machine adapters.
/// </summary>
public abstract class QueuedMachineHost : IMachineRuntime, IQueuedMachineRuntime, IMachineContentSlot,
    IMachineVideoOutputs, IMachineVideoOutput, IMachineAudioOutputs, IAudioMachine,
    IMachineInputPorts, IFeedbackMachine, ITimeTravelMachine, IMachineCheckpointRuntime {
    private readonly SeatPort[] m_seats;
    private readonly QueuedMachineWorker m_worker;

    private MachinePads m_inputs = MachinePads.Neutral;
    private string? m_savePath;

    /// <summary>Initializes a queued screen-machine host.</summary>
    /// <param name="width">The native framebuffer width.</param>
    /// <param name="height">The native framebuffer height.</param>
    /// <param name="maximumPendingSteps">The maximum accepted but incomplete step count.</param>
    /// <param name="workerName">The worker thread's diagnostic name.</param>
    /// <param name="audioSampleRate">The requested audio sample rate, or zero to disable audio synthesis.</param>
    /// <param name="savePath">The initial battery-save path.</param>
    /// <param name="inputPorts">The controller ports' names in seat order, one to <see cref="MachinePads.MaxSeats"/>
    /// distinct names; <see langword="null"/> declares the single port <c>controls</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputPorts"/> names no port or more than
    /// <see cref="MachinePads.MaxSeats"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="inputPorts"/> repeats a name or holds a blank one.</exception>
    protected QueuedMachineHost(int width, int height, int maximumPendingSteps, string workerName, int audioSampleRate, string? savePath, IReadOnlyList<string>? inputPorts = null) {
        inputPorts ??= ["controls"];
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: inputPorts.Count
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MachinePads.MaxSeats,
            value: inputPorts.Count
        );

        var ports = new Dictionary<string, IMachineInputPort>(comparer: StringComparer.Ordinal);

        m_seats = new SeatPort[inputPorts.Count];
        for (var seat = 0; (seat < m_seats.Length); ++seat) {
            var name = inputPorts[seat];

            ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
            m_seats[seat] = new SeatPort(
                host: this,
                seat: seat
            );
            if (!ports.TryAdd(
                key: name,
                value: m_seats[seat]
            )) {
                throw new ArgumentException(
                    message: $"The input port name '{name}' is declared twice.",
                    paramName: nameof(inputPorts)
                );
            }
        }
        m_savePath = savePath;
        VideoOutputs = new Dictionary<string, IMachineVideoOutput> { ["video"] = this }.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        AudioOutputs = new Dictionary<string, IAudioMachine> { ["audio"] = this }.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        InputPorts = ports.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        Seats = Array.AsReadOnly<IMachineInputPort>(array: m_seats);
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
    /// <summary>Gets the controller ports in seat order — the same ports <see cref="InputPorts"/> names — so seat
    /// <c>i</c> of every image this host submits is <c>Seats[i]</c>'s state.</summary>
    public IReadOnlyList<IMachineInputPort> Seats { get; }
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
        inputs: in m_inputs
    );
    /// <inheritdoc/>
    public byte[] CaptureCheckpoint() => m_worker.CaptureCheckpoint().Encode(
        inputs: in m_inputs,
        seats: m_seats.Length
    );
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

        if (restored.Seats != m_seats.Length) {
            throw new InvalidOperationException(message: $"machine restore requires the checkpoint's {restored.Seats} input seats to match this host's {m_seats.Length}");
        }
        m_worker.RestoreCheckpoint(checkpoint: restored.Checkpoint);
        m_inputs = restored.Inputs;
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
    /// <summary>Synchronously advances with an explicit seat image for standalone hardware callers.</summary>
    /// <param name="deltaTicks">The exact tick budget.</param>
    /// <param name="inputs">The seat image held throughout the budget; seats past this host's ports are held
    /// neutral.</param>
    /// <returns>Whether the machine advanced.</returns>
    public bool Step(ulong deltaTicks, in MachinePads inputs) {
        var declared = Declared(inputs: in inputs);

        return m_worker.Step(
            deltaTicks: deltaTicks,
            input: in declared
        );
    }
    /// <inheritdoc/>
    public QueuedMachineSubmission Submit(ulong deltaTicks) => Submit(
        deltaTicks: deltaTicks,
        inputs: in m_inputs
    );
    /// <summary>Queues an exact tick and seat-image segment for standalone hardware callers.</summary>
    /// <param name="deltaTicks">The exact tick budget.</param>
    /// <param name="inputs">The seat image captured by this submission; seats past this host's ports are held
    /// neutral.</param>
    /// <returns>The submission outcome, including producer backpressure.</returns>
    public QueuedMachineSubmission Submit(ulong deltaTicks, in MachinePads inputs) {
        var declared = Declared(inputs: in inputs);

        return m_worker.Submit(
            deltaTicks: deltaTicks,
            input: in declared
        );
    }
    // Neutralizes the seats this host declares no port for, so the replay ring and a checkpoint record the same image.
    private MachinePads Declared(in MachinePads inputs) {
        var declared = inputs;

        for (var seat = m_seats.Length; (seat < MachinePads.MaxSeats); ++seat) {
            declared[seat] = MachinePadState.Neutral;
        }

        return declared;
    }
    /// <inheritdoc/>
    public long WriteFrame(Span<byte> region) =>
        m_worker.WriteFrame(region: region);

    // One controller port: a view of its seat in the host's held image, so every port's state rides the same
    // submission, checkpoint, and replay record.
    private sealed class SeatPort(QueuedMachineHost host, int seat) : IMachineInputPort {
        public MachinePadState State => host.m_inputs[seat];

        public void SetState(in MachinePadState state) =>
            host.m_inputs[seat] = state;
    }
}
