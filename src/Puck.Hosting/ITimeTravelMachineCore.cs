namespace Puck.Hosting;

/// <summary>
/// The slim, machine-neutral seam a <see cref="MachineTimeTravel{TInput}"/> drives — the only coupling between the
/// build-once time-travel component and a concrete deterministic core. A core serializes its whole mutable state into a
/// caller-owned buffer (no per-frame snapshot allocation), restores it back, exposes its framebuffer and cycle/native-frame
/// progress, applies one held input, advances by an exact cycle budget, and forks a lookahead sibling for runahead.
/// <typeparamref name="TInput"/> is the core's own held-input image (the neutral controller image for a queued host, the
/// native key register for a debug host), so the ring stays input-agnostic while replay stays bit-exact. Every member runs
/// on the single producer thread the host owns (the worker thread for a queued host, the render thread for a debug host).
/// </summary>
/// <typeparam name="TInput">The core's held-input image, recorded per frame and replayed verbatim.</typeparam>
public interface ITimeTravelMachineCore<TInput> {
    /// <summary>Gets the core's current master-clock cycle count — the monotonic stamp a captured frame carries.</summary>
    long CycleCount { get; }

    /// <summary>Gets the number of complete native video frames elapsed since boot — the unit rewind seeks in.</summary>
    long NativeFrameIndex { get; }

    /// <summary>Gets the core's current native framebuffer as packed <c>0x00RRGGBB</c> pixels.</summary>
    ReadOnlySpan<uint> Framebuffer { get; }

    /// <summary>Serializes the core's whole mutable state into <paramref name="buffer"/>, growing it in place when it is
    /// too small (so a steady-state capture allocates nothing), and returns the byte length written.</summary>
    /// <param name="buffer">The caller-owned destination buffer, grown as needed.</param>
    /// <returns>The number of state bytes written.</returns>
    int CaptureState(ref byte[] buffer);

    /// <summary>Restores the core's whole mutable state from the first <paramref name="length"/> bytes of
    /// <paramref name="buffer"/> — a same-machine restore that repositions the master clock and every component.</summary>
    /// <param name="buffer">The buffer holding a state image previously produced by <see cref="CaptureState"/>.</param>
    /// <param name="length">The state image length in bytes.</param>
    void RestoreState(byte[] buffer, int length);

    /// <summary>Latches one held-input image for the frame about to run.</summary>
    /// <param name="input">The input image held for the frame.</param>
    void ApplyInput(in TInput input);

    /// <summary>Advances the core by an exact master-cycle budget.</summary>
    /// <param name="cycles">The cycle budget to advance.</param>
    void RunCycles(long cycles);

    /// <summary>Forks a bare, independent lookahead sibling from the core's current state — rented from the core's bounded
    /// instance pool, so runahead keeps ONE persistent fork rather than forking per input change. The caller owns the
    /// returned lookahead and disposes it (dispose returns it to the pool).</summary>
    /// <returns>The lookahead sibling.</returns>
    ITimeTravelLookahead<TInput> CreateLookahead();
}

/// <summary>
/// A bare lookahead sibling of an <see cref="ITimeTravelMachineCore{TInput}"/> — a headless forked machine (no audio, no
/// GPU) runahead advances on predicted input and presents in place of the authoritative machine. It never drives audio,
/// so only the real machine ever opens a speaker; its trajectory can diverge freely without touching the real machine.
/// </summary>
/// <typeparam name="TInput">The core's held-input image.</typeparam>
public interface ITimeTravelLookahead<TInput> : IDisposable {
    /// <summary>Gets the lookahead's own number of complete native video frames elapsed since boot — the SAME quantity the
    /// authority exposes through <see cref="ITimeTravelMachineCore{TInput}.NativeFrameIndex"/>, read from the fork itself
    /// rather than a synthetic per-<see cref="RunFrame"/> counter, so the measured runahead lead is the fork's true lead
    /// even when an instruction's overshoot crosses an extra native-frame boundary.</summary>
    long NativeFrameIndex { get; }

    /// <summary>Gets the lookahead's current native framebuffer as packed <c>0x00RRGGBB</c> pixels.</summary>
    ReadOnlySpan<uint> Framebuffer { get; }

    /// <summary>Restores the lookahead's whole mutable state from the first <paramref name="length"/> bytes of
    /// <paramref name="buffer"/> — the rebase from the authoritative machine's current state.</summary>
    /// <param name="buffer">The buffer holding the authoritative machine's captured state.</param>
    /// <param name="length">The state image length in bytes.</param>
    void RestoreState(byte[] buffer, int length);

    /// <summary>Latches one held-input image for the frame about to run.</summary>
    /// <param name="input">The predicted input image held for the frame.</param>
    void ApplyInput(in TInput input);

    /// <summary>Advances the lookahead by exactly one native video frame.</summary>
    void RunFrame();
}
