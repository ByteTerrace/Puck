using System.Collections.Frozen;
using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Waiting;
using Puck.Abstractions.Sources;

namespace Puck.Machines;

/// <summary>
/// Provides the machine-neutral host surface shared by queued screen-machine adapters.
/// </summary>
public abstract class QueuedMachineHost : IMachineRuntime, IQueuedMachineRuntime, IMachineContentSlot,
    IMachineVideoOutputs, IMachineVideoOutput, IMachineAudioOutputs, IAudioMachine,
    IMachineInputPorts, IFeedbackMachine, IMachineMemoryPeek, ITimeTravelMachine, IMachineCheckpointRuntime, IMachineFrameProgress {
    private readonly SeatPort[] m_seats;
    private readonly QueuedMachineWorker m_worker;

    // The changes the ports scheduled inside the next segment, in rising offset order.
    private readonly List<MachineSeatChange> m_changes = [];

    private MachinePads m_inputs = MachinePads.Neutral;
    private string? m_savePath;
    private ulong? m_segmentClock;
    private long m_timedInputChanges;

    /// <summary>Initializes a queued screen-machine host.</summary>
    /// <param name="width">The native framebuffer width.</param>
    /// <param name="height">The native framebuffer height.</param>
    /// <param name="workerName">The worker thread's diagnostic name.</param>
    /// <param name="audioSampleRate">The requested audio sample rate, or zero to disable audio synthesis.</param>
    /// <param name="savePath">The initial battery-save path.</param>
    /// <param name="inputPorts">The controller ports' names in seat order, one to <see cref="MachinePads.MaxSeats"/>
    /// distinct names; <see langword="null"/> declares the single port <c>controls</c>.</param>
    /// <param name="maximumPendingSteps">The maximum accepted but incomplete step count.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputPorts"/> names no port or more than
    /// <see cref="MachinePads.MaxSeats"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="inputPorts"/> repeats a name or holds a blank one.</exception>
    protected QueuedMachineHost(int width, int height, string workerName, int audioSampleRate, string? savePath, IReadOnlyList<string>? inputPorts = null, int maximumPendingSteps = QueuedMachineWorker.DefaultMaximumPendingSteps) {
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
    public long NativeFrameIndex => m_worker.NativeFrameIndex;
    /// <inheritdoc/>
    public long StagedFrames => m_worker.StagedFrames;
    /// <inheritdoc/>
    public long ShownFrames => m_worker.ShownFrames;
    /// <inheritdoc/>
    public uint FrameRateHz => m_worker.FrameRateHz;
    /// <inheritdoc/>
    public long TimedInputChanges => m_timedInputChanges;
    /// <inheritdoc/>
    public TimeSpan StepBusyTime => m_worker.StepBusyTime;
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
    public bool Advance(ulong deltaTicks) {
        var generation = 0UL;

        return (QueuedMachineWorker.RequireSettled(outcome: StepSegment(
            deadline: WaitDeadline.Synchronous(),
            deltaTicks: deltaTicks,
            drain: true,
            generation: ref generation
        )) != QueuedMachineSubmission.Rejected);
    }
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
    public byte PeekByte(int address) =>
        m_worker.PeekByte(address: address);
    /// <inheritdoc/>
    public void PeekBytes(int address, Span<byte> destination) =>
        m_worker.PeekBytes(
            address: address,
            destination: destination
        );
    /// <inheritdoc/>
    public void PokeByte(int address, byte value) =>
        m_worker.PokeByte(
            address: address,
            value: value
        );
    /// <inheritdoc/>
    public int ReadSamples(Span<short> destination) =>
        m_worker.ReadAudioSamples(destination: destination);
    /// <inheritdoc/>
    public void RestoreCheckpoint(ReadOnlyMemory<byte> checkpoint) {
        var restored = DecodeForThisHost(checkpoint: checkpoint);

        m_worker.RestoreCheckpoint(checkpoint: restored.Checkpoint);
        m_inputs = restored.Inputs;
    }
    /// <inheritdoc/>
    public bool TryValidateCheckpoint(ReadOnlyMemory<byte> checkpoint, out string reason) {
        try {
            m_worker.ValidateCheckpoint(checkpoint: DecodeForThisHost(checkpoint: checkpoint).Checkpoint);
        } catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ObjectDisposedException) {
            reason = exception.Message;
            return false;
        }
        reason = string.Empty;
        return true;
    }
    // The checks a restore makes before it reaches the worker: the image's encoding and its seat count.
    private (QueuedMachineCheckpoint Checkpoint, MachinePads Inputs, int Seats) DecodeForThisHost(ReadOnlyMemory<byte> checkpoint) {
        var restored = QueuedMachineCheckpoint.Decode(bytes: checkpoint);

        if (restored.Seats != m_seats.Length) {
            throw new InvalidOperationException(message: $"machine restore requires the checkpoint's {restored.Seats} input seats to match this host's {m_seats.Length}");
        }
        return restored;
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
    public QueuedMachineSubmission Submit(ulong deltaTicks) {
        var generation = 0UL;

        return QueuedMachineWorker.RequireSettled(outcome: StepSegment(
            deadline: WaitDeadline.Synchronous(),
            deltaTicks: deltaTicks,
            drain: false,
            generation: ref generation
        ));
    }
    /// <inheritdoc/>
    public void SetSegmentClock(ulong endTicks) => m_segmentClock = endTicks;
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

    /// <inheritdoc/>
    /// <remarks>An accepted segment takes the ports' scheduled changes and the stated clock with it, and the ports then
    /// hold the image the segment ends with.</remarks>
    public QueuedMachineSubmission StepSegment(ulong deltaTicks, bool drain, ref ulong generation, WaitDeadline deadline) {
        // A call that resumes an accepted segment's drain submits nothing, and the ports already hold its last image.
        var fresh = (generation == 0UL);
        var declared = Declared(inputs: in m_inputs);
        var submission = m_worker.StepSegment(
            changes: ((fresh && (m_changes.Count != 0)) ? m_changes.ToArray() : null),
            deadline: deadline,
            deltaTicks: deltaTicks,
            drain: drain,
            endTicks: (fresh ? m_segmentClock : null),
            generation: ref generation,
            input: in declared
        );

        if (
            fresh &&
            (submission is QueuedMachineSubmission.Accepted or QueuedMachineSubmission.AcceptedAfterBackpressure or QueuedMachineSubmission.PendingDrain)
        ) {
            foreach (var change in m_changes) {
                m_inputs[change.Seat] = change.Pad;
            }

            m_timedInputChanges += m_changes.Count;
            m_changes.Clear();
            m_segmentClock = null;
        }

        return submission;
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
    /// <inheritdoc/>
    public long WriteFrame(Span<byte> region, ulong dueTicks, long held = 0L) =>
        m_worker.WriteFrame(
            dueTicks: dueTicks,
            held: held,
            region: region
        );
    /// <inheritdoc/>
    public long FrameDue(ulong dueTicks, long held = 0L) =>
        m_worker.FrameDue(
            dueTicks: dueTicks,
            held: held
        );

    // Withdraws the changes a seat scheduled inside the next segment.
    private void WithdrawChanges(int seat) =>
        _ = m_changes.RemoveAll(match: change => (change.Seat == seat));
    // Schedules a seat's change, keeping the list in rising offset order with equal offsets in the order scheduled.
    private void ScheduleChange(int seat, in MachinePadState state, ulong offsetTicks) {
        var position = m_changes.Count;

        while ((position > 0) && (m_changes[(position - 1)].OffsetTicks > offsetTicks)) {
            --position;
        }

        m_changes.Insert(
            index: position,
            item: new MachineSeatChange(
                OffsetTicks: offsetTicks,
                Pad: state,
                Seat: seat
            )
        );
    }

    // One controller port: a view of its seat in the host's held image, so every port's state rides the same
    // submission, checkpoint, and replay record.
    private sealed class SeatPort(QueuedMachineHost host, int seat) : IMachineInputPort {
        public MachinePadState State => host.m_inputs[seat];

        public void SetState(in MachinePadState state) {
            host.WithdrawChanges(seat: seat);
            host.m_inputs[seat] = state;
        }
        public void SetState(in MachinePadState state, ulong offsetTicks) {
            if (offsetTicks == 0UL) {
                host.m_inputs[seat] = state;
            } else {
                host.ScheduleChange(
                    offsetTicks: offsetTicks,
                    seat: seat,
                    state: in state
                );
            }
        }
    }
}
