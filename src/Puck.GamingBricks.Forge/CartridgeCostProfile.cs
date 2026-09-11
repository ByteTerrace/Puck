using Puck.Maths;

namespace Puck.GamingBricks.Forge;

/// <summary>
/// A machine's per-frame reservation of authored work, in <see cref="CartridgeCost"/>'s abstract units. It carries a
/// reservation, never a processor, clock or instruction count.
/// </summary>
/// <remarks>
/// <para>
/// Each machine has its own reservation because they do not sustain the same work: the Color machine runs its
/// processor at double speed and the advanced one fetches through the cartridge prefetch, and those are different
/// multiples of each machine's own base rate. A document is costed against its own target's profile, so flipping the
/// target re-asks the question rather than carrying the other machine's answer.
/// </para>
/// <para>
/// Weights are per machine, not shared. A shared table has to take the worse of the two machines' ratios for every
/// shape, so it overstates whichever machine did not set that shape's weight — and the overstatement is not uniform,
/// so no single reservation both admits everything a machine sustains and refuses everything it does not. Fitting
/// each machine's weights to its own measurements collapses that spread, which is what lets one reservation per
/// machine be both safe and useful.
/// </para>
/// <para>
/// Each machine's weights and reservation come from its own capacity table, solved against the model's own cost
/// function. One unit is a eleventh of a <c>set</c> step on that machine, so units are not comparable between the two
/// — only a document's fit against its own target's reservation is.
/// </para>
/// <para>
/// <see cref="Calibration"/> exists so the reservations above it can be measured rather than asserted. Measuring what
/// a machine sustains means booting documents past the current reservation, which the standard profiles refuse by
/// design — a measurement run under a standard profile would report the model's own opinion instead of the machine's
/// behaviour. It is not a way to admit a document that does not fit: nothing that compiles under it is claimed to
/// keep frame cadence.
/// </para>
/// </remarks>
/// <param name="Name">The profile's identifier.</param>
/// <param name="FrameUnits">Abstract work units the rule pass may spend in one frame.</param>
/// <param name="Blit">A screen blit.</param>
/// <param name="ConditionCompare">One comparison condition.</param>
/// <param name="ConditionKey">One key condition.</param>
/// <param name="LoopSetup">Entering a counted loop.</param>
/// <param name="LoopStep">One iteration's own overhead.</param>
/// <param name="MapWrite">One queued map write.</param>
/// <param name="OperandArray">Reading an array element.</param>
/// <param name="OperandVariable">Reading a variable.</param>
/// <param name="RasterRow">One mid-picture scroll change.</param>
/// <param name="RasterSetup">Republishing the scroll rows at all.</param>
/// <param name="SaveByte">One byte of a save payload.</param>
/// <param name="SaveFixed">A save or load, before its payload.</param>
/// <param name="Sound">Starting or stopping a sound.</param>
/// <param name="Sprite">One published sprite.</param>
/// <param name="StepArithmetic">An arithmetic step.</param>
/// <param name="StepDivide">A divide or modulo step.</param>
/// <param name="StepMultiply">A multiply step.</param>
/// <param name="StepSet">A step writing a literal, the unit's own anchor.</param>
/// <param name="StepShift">A shift step.</param>
/// <param name="TargetArray">Addressing an array element to write.</param>
public sealed record CartridgeCostProfile(
    string Name,
    long FrameUnits,
    long Blit,
    long ConditionCompare,
    long ConditionKey,
    long LoopSetup,
    long LoopStep,
    long MapWrite,
    long OperandArray,
    long OperandVariable,
    long RasterRow,
    long RasterSetup,
    long SaveByte,
    long SaveFixed,
    long Sound,
    long Sprite,
    long StepArithmetic,
    long StepDivide,
    long StepMultiply,
    long StepSet,
    long StepShift,
    long TargetArray) {
    /// <summary>The advanced machine's reservation and weights.</summary>
    public static CartridgeCostProfile Advanced { get; } = new(
        Name: "puck.cartridge.agb.v1", FrameUnits: 182000L,
        Blit: 2172L, ConditionCompare: 23L, ConditionKey: 23L, LoopSetup: 39L, LoopStep: 35L, MapWrite: 46L,
        OperandArray: 28L, OperandVariable: 10L, RasterRow: 1147L, RasterSetup: 2791L, SaveByte: 28L,
        SaveFixed: 156L, Sound: 156L, Sprite: 72L, StepArithmetic: 15L, StepDivide: 40L, StepMultiply: 18L,
        StepSet: 11L, StepShift: 18L, TargetArray: 18L);
    /// <summary>The Color machine's reservation and weights.</summary>
    public static CartridgeCostProfile Humble { get; } = new(
        Name: "puck.cartridge.cgb.v1", FrameUnits: 25000L,
        Blit: 1521L, ConditionCompare: 8L, ConditionKey: 7L, LoopSetup: 4L, LoopStep: 17L, MapWrite: 76L,
        OperandArray: 13L, OperandVariable: 2L, RasterRow: 130L, RasterSetup: 480L, SaveByte: 28L,
        SaveFixed: 156L, Sound: 156L, Sprite: 38L, StepArithmetic: 13L, StepDivide: 120L, StepMultiply: 38L,
        StepSet: 11L, StepShift: 34L, TargetArray: 10L);
    /// <summary>
    /// The reservation used while measuring what a machine sustains, large enough that the machine binds before the
    /// model does. Its weights are the Color machine's, so a probe is shaped the same way either machine's is.
    /// </summary>
    public static CartridgeCostProfile Calibration { get; } = Humble with {
        Name = "puck.cartridge.calibration.v1",
        FrameUnits = long.MaxValue,
    };

    /// <summary>Returns the profile a target is costed against.</summary>
    /// <param name="target">The document's target.</param>
    /// <returns>The target's profile.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The target names no machine.</exception>
    public static CartridgeCostProfile For(string target) => target switch {
        "agb" => Advanced,
        "cgb" => Humble,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(target), actualValue: target, message: "No machine carries that target name."),
    };

    /// <summary>Returns a value indicating whether a cost fits the reservation.</summary>
    /// <param name="bound">The cost to test.</param>
    /// <returns><see langword="true"/> when the cost is known and within the reservation.</returns>
    /// <remarks>An unmodeled or overflowed cost never fits: it cannot certify a deadline it was never measured against.</remarks>
    public bool Admits(CostBound bound) => bound.IsKnown && bound.Cycles <= FrameUnits;
}
