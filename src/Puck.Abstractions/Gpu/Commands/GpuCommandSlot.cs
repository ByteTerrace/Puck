namespace Puck.Abstractions.Gpu;

/// <summary>
/// One command buffer and the submission fence its submission arms, reused one submission at a time: the slot waits for
/// its last submission before it records again, so its command buffer is never reset while the queue still runs it,
/// and a recording that throws discards the buffer (whose list a backend may have left open) so the next one records on
/// a fresh one. Both are created on first use from the services handed in. Single-thread affine.
/// </summary>
/// <param name="name">The command pool's debug name.</param>
public sealed class GpuCommandSlot(GpuObjectName name) : IDisposable {
    private IGpuCommandPool? m_commands;
    private IGpuSubmissionFence? m_fence;

    /// <summary>Blocks until the slot's last submission has run; a no-op when none is outstanding. A wait that throws
    /// leaves the submission outstanding, so the next wait waits again.</summary>
    public void Wait() =>
        m_fence?.Wait();
    /// <summary>Waits for the slot's last submission, then records into its command buffer between begin and end.</summary>
    /// <typeparam name="TState">The recording's state.</typeparam>
    /// <param name="services">The services whose command-pool factory and recorder the slot uses.</param>
    /// <param name="state">The state handed to <paramref name="record"/>.</param>
    /// <param name="record">Records the commands, given the recorder and the command buffer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="record"/> is
    /// <see langword="null"/>.</exception>
    public void Record<TState>(GpuDeviceServices services, TState state, Action<IGpuRecorder, nint, TState> record) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(record);

        Wait();

        // Created before anything is recorded, so a submit that follows a recording can fail only in the queue
        // submitter, whose fence then tells whether the submission reached the queue.
        m_fence ??= services.QueueSubmitter.CreateSubmissionFence();

        var commands = (m_commands ??= services.CommandPoolFactory.Create(name: in name));
        var commandBuffer = commands.CommandBufferHandle;
        var recorder = services.Recorder;

        try {
            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer);
            record(recorder, commandBuffer, state);
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer);
        } catch {
            m_commands = null;
            commands.Dispose();

            throw;
        }
    }
    /// <summary>Submits the last recording, arming the slot's fence.</summary>
    /// <param name="services">The services whose queue submitter submits.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Nothing has been recorded.</exception>
    public void Submit(GpuDeviceServices services) {
        ArgumentNullException.ThrowIfNull(services);

        var commands = (m_commands ?? throw new InvalidOperationException(message: "A command slot submits only what it recorded."));

        services.QueueSubmitter.Submit(
            commandBufferHandles: [commands.CommandBufferHandle],
            fence: m_fence!
        );
    }

    /// <summary>Waits for the slot's last submission, a lost device counting as done, then releases the command buffer
    /// and the fence. Safe to call more than once.</summary>
    public void Dispose() {
        try {
            Wait();
        } catch (DeviceLostException) {
            // Nothing on a lost device runs again.
        }

        m_commands?.Dispose();
        m_commands = null;
        m_fence?.Dispose();
        m_fence = null;
    }
}
