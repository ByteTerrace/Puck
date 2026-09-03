using Puck.Maths;

namespace Puck.Physics.Motion;

/// <summary>The compiled predicate dispatch tag.</summary>
public enum CompiledPredicateKind : byte {
    Now,
    Recently,
    CompareState,
    TimerElapsed,
    All,
    Any,
    Not,
    Held,
}
/// <summary>The flattened, fixed-point form of one predicate. <see cref="ChannelOrdinal"/> carries a
/// <see cref="CompiledPredicateKind.Held"/> predicate's resolved composition-channel ordinal (<c>-1</c> for every
/// other kind) — legitimate only inside a kit's <c>shaping</c>-row gate, where the world's channel table is
/// available to resolve it at kit-compile time.</summary>
public readonly record struct CompiledPredicate(ActionFact Fact, int RecencySlot, int StateSlot, FixedQ4816 Value, ActionStateComparison Comparison, CompiledPredicateKind Kind, int Arity = 0, int ChannelOrdinal = -1);
/// <summary>Representation bound for one compiled body predicate program.</summary>
public static class CompiledPredicateCapacity {
    /// <summary>The most postfix tokens one body gate may execute.</summary>
    public const int MaxTokens = 256;
}
/// <summary>One compiled instruction shared by program phases and action triggers.</summary>
/// <remarks><c>StateName</c> carries <see cref="BodyMotionOp.Generate"/>'s draw site — the one row a generate names,
/// since a site's source and cursor are its own — and is <see langword="null"/> for every other operation except
/// <see cref="BodyMotionOp.Judge"/>, where it carries the declared judge row name. Nothing is bound at kit-compile
/// time here for either: the generate site is a world-global <c>state</c> row and the judge row lives in the
/// declared <c>judges</c> table, neither part of this kit's per-body slot table.</remarks>
public readonly record struct CompiledBodyInstruction(BodyMotionOp Operation, FixedQ4816 Value, FixedVector3 Direction, ulong DurationTicks, int StateSlot, ActionTarget Target = ActionTarget.Self, string? StateName = null);
/// <summary>A slot envelope compiled into the slot's fixed counter or engine-tick domain.</summary>
/// <param name="Minimum">The inclusive range minimum, or zero for a set.</param>
/// <param name="Maximum">The inclusive range maximum, or zero for a set.</param>
/// <param name="Values">The closed set, or <see langword="null"/> for a range.</param>
public sealed record CompiledActionStateEnvelope(long Minimum, long Maximum, long[]? Values) {
    /// <summary>Clamps a raw value to the range, or substitutes the authored initial value for a closed-set miss.</summary>
    public long Clamp(long value, long initial) => ((Values is null)
        ? Math.Clamp(
            value: value,
            min: Minimum,
            max: Maximum
        )
        : (Contains(value: value)
            ? value
            : initial
    ));
    /// <summary>Returns whether a raw slot-domain value is admitted.</summary>
    public bool Contains(long value) => ((Values is { } values)
        ? (Array.IndexOf(
            array: values,
            value: value
        ) >= 0)
        : ((value >= Minimum) && (value <= Maximum))
    );
}
/// <summary>One compiled named action-state slot.</summary>
public readonly record struct CompiledActionStateSlot(
    string Name,
    ActionStateKind Kind,
    FixedQ4816 InitialValue,
    ulong InitialTicks,
    ActionFact? ResetFact,
    ActionStateLifetime Lifetime,
    bool PlayerWritable,
    CompiledActionStateEnvelope? Envelope
);
/// <summary>One compiled trigger channel: the flattened conjunction gate, the press latch in engine ticks, and the
/// fixed-point effects in authored order.</summary>
public sealed record CompiledTrigger(CompiledPredicate[] Gate, ulong LatchTicks, CompiledBodyInstruction[] Effects);
/// <summary>One compiled fact trigger: the engine fact, the flattened additional gate, the edge/level mode, and the
/// effects a fire applies in order.</summary>
/// <param name="Fact">The engine fact.</param>
/// <param name="Gate">The flattened additional conjunction, empty when none is authored.</param>
/// <param name="Mode">Whether the trigger is level- or edge-fired (see <see cref="ActionTriggerMode"/>).</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
public readonly record struct CompiledFactTrigger(ActionFact Fact, CompiledPredicate[] Gate, ActionTriggerMode Mode, CompiledBodyInstruction[] Effects);
/// <summary>A lane binding compiled once before simulation: both trigger channels plus the recency-clock table (one
/// slot per authored <c>recently</c> instance across both gates — the per-tick clock updater walks it).</summary>
public sealed record CompiledActionSpec(CompiledTrigger? OnPress, CompiledTrigger? OnRelease, CompiledFactTrigger[] OnFact, ActionFact[] RecencyFacts, ulong[] RecencyWindows);
