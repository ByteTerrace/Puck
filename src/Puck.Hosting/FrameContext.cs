namespace Puck.Hosting;

/// <summary>
/// Everything a node needs to render one frame: the host capability seam — through which it resolves the
/// <em>shared</em> device chain it renders offscreen on (every node in an in-process tree composites on one
/// device) and any service its host publishes — the deterministic timing, and the pixel extent its parent
/// is asking it to fill. A node (re)builds its render targets
/// when <see cref="TargetWidth"/>/<see cref="TargetHeight"/> change. Timing is carried in integer engine
/// ticks (<see cref="EngineTicks"/>) rather than floating point so the running total accumulates exactly and
/// never drifts; the host advances the simulation in whole fixed steps, and a node converts to whatever
/// real-number unit it needs at the point of use.
/// </summary>
/// <param name="Host">The host capability seam exposed to the node, through which it resolves the device context and any host-published service.</param>
/// <param name="ElapsedTicks">The fixed-step simulation clock: engine ticks consumed by whole update steps since the host loop started.</param>
/// <param name="DeltaTicks">The engine ticks the simulation advances this frame — always a whole multiple of <see cref="StepTicks"/>.</param>
/// <param name="FrameDeltaTicks">The clamped wall interval between presented host frames, for presentation animation
/// and frame-rate observation only. Authoritative simulation must use <paramref name="DeltaTicks"/> / fixed steps.</param>
/// <param name="AccumulatorTicks">The engine ticks elapsed but not yet consumed by a fixed step; the render interpolation numerator.</param>
/// <param name="StepTicks">The fixed update period in engine ticks; the render interpolation denominator.</param>
/// <param name="TargetWidth">The pixel width the parent is asking this node to fill.</param>
/// <param name="TargetHeight">The pixel height the parent is asking this node to fill.</param>
public readonly record struct FrameContext(
    IHostContext Host,
    ulong ElapsedTicks,
    ulong DeltaTicks,
    ulong FrameDeltaTicks,
    ulong AccumulatorTicks,
    ulong StepTicks,
    uint TargetWidth,
    uint TargetHeight
) {
    /// <summary>Gets how far this frame sits between the last and next fixed update, in <c>[0, 1)</c>, for interpolating render state.</summary>
    public double InterpolationAlpha =>
        ((StepTicks == 0UL)
            ? 0.0
            : ((double)AccumulatorTicks / StepTicks));
    /// <summary>Gets the continuous render-time clock in engine ticks (the fixed-step <see cref="ElapsedTicks"/> plus the unconsumed <see cref="AccumulatorTicks"/>).</summary>
    public ulong RenderTicks =>
        (ElapsedTicks + AccumulatorTicks);
    /// <summary>Gets the seconds the simulation advances this frame (<see cref="DeltaTicks"/> as seconds).</summary>
    public double DeltaSeconds =>
        EngineTicks.ToSeconds(ticks: DeltaTicks);
    /// <summary>Gets the clamped wall interval for this presented host frame. Presentation-only; never simulation time.</summary>
    public double FrameDeltaSeconds =>
        EngineTicks.ToSeconds(ticks: FrameDeltaTicks);
    /// <summary>Gets the fixed-step simulation clock in seconds (<see cref="ElapsedTicks"/> as seconds).</summary>
    public double ElapsedSeconds =>
        EngineTicks.ToSeconds(ticks: ElapsedTicks);
    /// <summary>Gets the continuous render-time clock in seconds (<see cref="RenderTicks"/> as seconds).</summary>
    public double RenderSeconds =>
        EngineTicks.ToSeconds(ticks: RenderTicks);
}
