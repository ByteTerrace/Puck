using Puck.Abstractions.Machines;

namespace Puck.Hosting;

/// <summary>
/// The slim core adapter a <see cref="QueuedMachineWorker"/> drives — the only seam between the machine-neutral queued
/// host substrate and a concrete emulator core. The worker owns the worker thread, the bounded FIFO with backpressure,
/// the triple-buffer publication rotation, the native-frame-keyed save-flush debounce, and the framebuffer repack; a core
/// only advances itself, exposes its framebuffer and native-frame progress, folds the neutral pad, imports/exports its
/// battery save, and (through the inherited <see cref="ITimeTravelMachineCore{TInput}"/> surface) serializes/restores its
/// whole state and forks a lookahead for machine-neutral time-travel. Every member runs on the worker's single execution
/// thread except construction and disposal, which the host arranges around <see cref="QueuedMachineWorker.Load"/>/
/// <see cref="QueuedMachineWorker.Eject"/>.
/// </summary>
public interface IQueuedMachineCore : ITimeTravelMachineCore<MachinePadState>, IDisposable {
    /// <summary>Gets the core's current machine-cycle rate — constant hardware for a fixed-rate core, or a value that
    /// tracks a live clock-multiplier latch. The worker converts each segment's engine-tick budget against this through a
    /// remainder-carrying accumulator, so a varying rate carries no drift.</summary>
    ulong CyclesPerSecond { get; }

    /// <summary>Configures (or, with 0, disables) the core's presentation-side audio synthesis — its own mix/resample
    /// ring, exactly as if a host opened a speaker device. The worker calls this once when a core attaches, with the
    /// rate the host was constructed to want; a worker built with no audio consumer never calls this with a nonzero
    /// rate, so the core's state-of-record channel/envelope/sweep advance — which this never touches — stays
    /// unconditional, and an unattached core performs zero synthesis work.</summary>
    /// <param name="sampleRate">The output rate in frames per emulated second, or 0 to disable.</param>
    void ConfigureAudio(int sampleRate);

    /// <summary>Drains queued stereo samples (interleaved left/right) from the core's own presentation-side ring —
    /// the producer side the worker copies into its host-readable ring after each completed segment.</summary>
    /// <param name="destination">The buffer to fill; its length should be a multiple of two.</param>
    /// <returns>The number of samples written (left and right counted separately).</returns>
    int DrainAudioSamples(Span<short> destination);

    /// <summary>Flushes the core's persistent save. An unforced flush is gated on dirty state; a forced flush persists a
    /// final state. An I/O failure is reported, never propagated.</summary>
    /// <param name="force">When <see langword="true"/>, flush even when only a clock-style change is pending.</param>
    void FlushSave(bool force);

    /// <summary>Gets the loaded cartridge's current rumble motor level, 0..1 — the neutral <see cref="IFeedbackMachine"/>
    /// surface's source, sampled by the worker after each completed step. Zero on a core/cartridge with no rumble
    /// hardware; the default keeps every existing core source-compatible with no behavior change.</summary>
    float MotorLevel => 0f;

    /// <summary>Reads one byte from the core's bus address space without side effects — the worker-thread source of a
    /// host's <see cref="IMachineMemoryPeek.PeekByte"/>. The worker calls this on its own execution thread between
    /// steps, so it observes a coherent inter-instruction snapshot rather than racing the running core. The default
    /// reads as 0 (a core with no debug memory window).</summary>
    /// <param name="address">A machine-defined bus address.</param>
    /// <returns>The byte at that address, or 0.</returns>
    byte PeekByte(int address) => 0;

    /// <summary>Forces one byte into a writable region of the core's bus address space — the worker-thread source of a
    /// host's <see cref="IMachineMemoryPeek.PokeByte"/>, a debug mutation outside replay determinism. The worker calls
    /// this on its own execution thread between steps. The default is a no-op (a core with no debug memory window).</summary>
    /// <param name="address">A machine-defined bus address.</param>
    /// <param name="value">The byte to store.</param>
    void PokeByte(int address, byte value) { }

    /// <summary>Retargets the running core across its engine's options vocabulary WITHOUT a reboot — the worker-thread
    /// source of a host's <see cref="Puck.Abstractions.Machines.IReconfigurableMachine.TryReconfigure"/>. The worker
    /// calls this on its own execution thread between steps, so the swap observes a coherent inter-instruction boundary.
    /// The default rejects (a core with no live-reconfiguration path); an implementor parses the engine's own options
    /// string and applies the retarget. On success <paramref name="reason"/> may carry an advisory the host echoes.</summary>
    /// <param name="options">The engine-specific options string, or <see langword="null"/> for defaults.</param>
    /// <param name="reason">The rejection reason, or an advisory/empty string on success.</param>
    /// <returns>Whether the reconfigure was applied.</returns>
    bool Reconfigure(string? options, out string reason) {
        reason = "this machine does not support live reconfiguration";

        return false;
    }
}
