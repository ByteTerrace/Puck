using Puck.Maths;
using Puck.State;

namespace Puck.Physics.Motion;

/// <summary>One compiled instruction shared by program phases and action triggers.</summary>
/// <remarks><c>StateName</c> carries <see cref="BodyMotionOp.Generate"/>'s draw site — the one row a generate names,
/// since a site's source and cursor are its own — and is <see langword="null"/> for every other operation. Nothing
/// is bound at kit-compile time here: the generate site is a world-global <c>state</c> row, not part of this kit's
/// per-body slot table.</remarks>
public readonly record struct CompiledBodyInstruction(BodyMotionOp Operation, FixedQ4816 Value, FixedVector3 Direction, ulong DurationTicks, int StateSlot, ActionTarget Target = ActionTarget.Self, string? StateName = null, bool UseMoveDirection = false);
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
