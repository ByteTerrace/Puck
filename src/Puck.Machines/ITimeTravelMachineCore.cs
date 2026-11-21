namespace Puck.Machines;

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
    /// <summary>Gets the core's current machine-cycle rate: constant hardware for a fixed-rate core, or one whose
    /// <see cref="MachineCycleRate.Cycles"/> tracks a live clock-multiplier latch. A host converts each engine-tick
    /// budget against it through a remainder-carrying accumulator, so a varying rate carries no drift; the rate's
    /// <see cref="MachineCycleRate.Seconds"/> stays constant for the core's lifetime. A cable-linked group reports the
    /// one rate its medium budgets every member by.</summary>
    MachineCycleRate CycleRate { get; }
    /// <summary>Gets whether <see cref="RunCycles"/> measures each budget from the end of the previous one, so the
    /// cycles an instruction ran past one budget come out of the next (<see langword="true"/>), or from where the clock
    /// stands when it is called (<see langword="false"/>). A host that runs one budget in parts
    /// (<see cref="MachineSegment"/>) reads it to make the parts end where the whole budget would have: a core that
    /// does not carry its overshoot is given each part less what the part before it overran.</summary>
    bool CarriesBudgetOvershoot { get; }
    /// <summary>Gets the master cycles from now until the frame being drawn is complete, at least one: the next instant
    /// at which <see cref="Framebuffer"/> holds one whole frame and nothing of the next. A host that shows every native
    /// frame divides a budget there and copies the framebuffer. A core that latches its completed frame may answer an
    /// estimate, since a copy a little late still reads the same frame; a core that draws in place answers exactly, or
    /// a late copy would hold part of the next frame.</summary>
    long CyclesToFrameEnd { get; }
    /// <summary>Gets the master cycles one native frame takes, at least one, which with <see cref="CycleRate"/> states
    /// the machine's native frame rate. A machine whose frames differ by a cycle or two answers the longer.</summary>
    long CyclesPerFrame { get; }

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
