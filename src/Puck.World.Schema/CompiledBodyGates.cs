using Puck.Maths;
using Puck.Physics.Motion;
using Puck.State.Rules;

namespace Puck.World;

/// <summary>One compiled trigger channel: the flattened conjunction gate, the press latch in engine ticks, and the
/// fixed-point effects in authored order.</summary>
public sealed record CompiledTrigger(GateToken[] Gate, ulong LatchTicks, CompiledBodyInstruction[] Effects);
/// <summary>One compiled fact trigger: the engine fact, the flattened additional gate, the edge/level mode, and the
/// effects a fire applies in order.</summary>
/// <param name="Fact">The engine fact.</param>
/// <param name="Gate">The flattened additional conjunction, empty when none is authored.</param>
/// <param name="Mode">Whether the trigger is level- or edge-fired (see <see cref="ActionTriggerMode"/>).</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
public readonly record struct CompiledFactTrigger(ActionFact Fact, GateToken[] Gate, ActionTriggerMode Mode, CompiledBodyInstruction[] Effects);
/// <summary>A lane binding compiled once before simulation: both trigger channels plus the recency-clock table (one
/// slot per authored <c>recently</c> instance across both gates — the per-tick clock updater walks it).</summary>
public sealed record CompiledActionSpec(CompiledTrigger? OnPress, CompiledTrigger? OnRelease, CompiledFactTrigger[] OnFact, ActionFact[] RecencyFacts, ulong[] RecencyWindows);

/// <summary>One compiled row of a kit's <c>shaping</c> table — the unified velocity-shaping law
/// <see cref="BodyMotionOp.ShapeVelocity"/> reads. The first row whose <see cref="When"/> gate opens governs the
/// whole tick; a row carries exactly one of <see cref="Along"/> (used alone for the whole-vector response law, or
/// paired with <see cref="Across"/> for the drive decomposition) or <see cref="Dynamics"/> (the second-order
/// follower alternative).</summary>
/// <param name="When">The flattened gate, empty for the unconditional row.</param>
/// <param name="Along">The along-the-target/along-the-heading facet, or <see langword="null"/> for a
/// <see cref="Dynamics"/> row.</param>
/// <param name="Across">The across-the-heading facet selecting the drive decomposition, or <see langword="null"/>
/// for a row that shapes the whole vector.</param>
/// <param name="Dynamics">The second-order follower this row names, or <see langword="null"/> for an
/// <see cref="Along"/> row.</param>
/// <param name="TurnScale">The steering-authority multiplier while this row governs.</param>
public readonly record struct FixedBodyShaping(
    GateToken[] When,
    FixedShapingAlong? Along,
    FixedShapingAcross? Across,
    FixedMotionDynamics? Dynamics,
    FixedQ4816 TurnScale
);

/// <summary>The one-time fixed-point compilation of an authored grounded motion row. Runtime simulation reads only
/// this form.</summary>
/// <remarks><see cref="Shaping"/> is simulation-affecting: it selects the slice of the tuning the velocity-shaping
/// stage reads. <see cref="ShapingRecencyFacts"/>/<see cref="ShapingRecencyWindows"/> are the shared recency-clock
/// table across every row's <c>recently</c> gate, and the per-tick clock updater walks it.</remarks>
public readonly record struct FixedMotionTuning(
    FixedSpeed Speed,
    FixedTurn Turn,
    FixedBodyShaping[] Shaping,
    ActionFact[] ShapingRecencyFacts,
    ulong[] ShapingRecencyWindows,
    MotionMoveFrame MoveFrame,
    bool FacingSnap,
    FixedUpTurnRates UpTurn,
    FixedObstructionLatch Obstruction,
    FixedQ4816 GroundSnap,
    FixedQ4816 StepHeight
) {
    /// <summary>Gets a value indicating whether any shaping row names a second-order follower — whether the body's
    /// planar/vertical follower state is live and needs carrying across an up-axis transport.</summary>
    public bool HasDynamics {
        get {
            foreach (var row in Shaping) {
                if (row.Dynamics is not null) {
                    return true;
                }
            }

            return false;
        }
    }
    /// <summary>Gets the number of recency clocks the shaping table's recency gates share.</summary>
    public int RecencySlots => ShapingRecencyFacts.Length;
}
