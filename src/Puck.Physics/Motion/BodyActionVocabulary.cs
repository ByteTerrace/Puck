using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.Physics.Motion;

/// <summary>An engine-published per-body sim fact the action predicates gate on. Facts are engine code.</summary>
/// <remarks>Admission rule: a new fact is privileged sim state the effects/predicates cannot derive from existing
/// facts; add one only then.</remarks>
[JsonConverter(typeof(StrictEnumConverter<ActionFact>))]
public enum ActionFact : byte {
    /// <summary>The body rests on a walkable contact surface.</summary>
    Grounded,

    /// <summary>The body is off every walkable contact surface.</summary>
    Airborne,

    /// <summary>The body's vertical velocity is positive.</summary>
    Rising,

    /// <summary>The body's vertical velocity is negative.</summary>
    Falling,

    /// <summary>A targeted effect was applied by another body on the preceding completed tick.</summary>
    AffectedBy,

    /// <summary>The body's origin is below the waterline. Written by the swim model's surface stage
    /// (<see cref="BodyMotionOp.ApplyBuoyancyAndSurface"/>); holds one tick behind that stage's evaluation, the same
    /// one-tick-behind discipline <see cref="Grounded"/> reads under.</summary>
    Submerged,

    /// <summary>The body's origin is inside the swim model's surface bob band (within its float depth of the float
    /// line). Written by the same surface stage as <see cref="Submerged"/>, on the same one-tick-behind terms.</summary>
    AtSurface,
}
/// <summary>The entity an action effect addresses.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionTarget>))]
public enum ActionTarget : byte {
    /// <summary>The body whose trigger fired.</summary>
    Self,

    /// <summary>The target selected by the body's active producer.</summary>
    ProducerTarget,

    /// <summary>The body that applied the recipient's most recent targeted effect.</summary>
    AffectingSubject,
}
/// <summary>One engine edge/latch vocabulary, shared by every gated trigger the engine evaluates — a per-body fact
/// trigger and a world rule alike. It is deliberately not two concepts with two spellings: "fires while the condition
/// holds" and "fires once when the condition becomes true" is the same distinction at both scopes, so it is the same
/// enum.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionTriggerMode>))]
public enum ActionTriggerMode : byte {
    /// <summary>Fires every evaluation the condition holds — the default, and the right shape for a continuous effect
    /// (a per-tick drain, a standing impulse).</summary>
    Level,

    /// <summary>Fires once on the condition crossing from not-holding to holding, and re-arms only when it crosses
    /// back — the right shape for anything that writes a document row, since a level-triggered write fires once per
    /// tick the condition holds rather than once per crossing.</summary>
    Edge,
}
/// <summary>The storage kind of a named persistent action-state slot.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateKind>))]
public enum ActionStateKind : byte {
    Counter,
    Timer,
}
/// <summary>Declares where a compiled action-state slot survives. Authored documents select this through the
/// <c>state.body</c> or <c>state.identity</c> lane; the runtime keeps the closed enum so its fixed register metadata
/// remains compact.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateLifetime>))]
public enum ActionStateLifetime : byte {
    /// <summary>The slot belongs to one body and resets from its authored facts.</summary>
    Ephemeral,

    /// <summary>The slot belongs to a player identity and crosses sessions through the durable input/output seam.</summary>
    Durable,
}
/// <summary>A fixed comparison admitted by a compiled state predicate.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateComparison>))]
public enum ActionStateComparison : byte {
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}
/// <summary>The one evaluation of an <see cref="ActionStateComparison"/> — a kit action's own state predicate and a
/// world rule's <c>compareState</c> operand ask exactly the same question of the same fixed-point pair, so the
/// vocabulary is decided in one place and neither can grow an arm the other lacks.</summary>
public static class ActionStateComparisons {
    /// <summary>Evaluates the comparison against a value/expectation pair.</summary>
    /// <param name="comparison">The comparison to evaluate.</param>
    /// <param name="value">The observed value.</param>
    /// <param name="expected">The value compared against.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    public static bool Holds(this ActionStateComparison comparison, FixedQ4816 value, FixedQ4816 expected) => comparison switch {
        ActionStateComparison.Equal => (value == expected),
        ActionStateComparison.NotEqual => (value != expected),
        ActionStateComparison.Less => (value < expected),
        ActionStateComparison.LessOrEqual => (value <= expected),
        ActionStateComparison.Greater => (value > expected),
        _ => (value >= expected),
    };
    /// <summary>Evaluates the comparison when either side may be positive infinity — a fact whose magnitude exceeds
    /// every representable number (today only the <c>$parked:</c> channel's forever case). Infinity compares as
    /// strictly greater than every finite value and equal to itself, so <c>&gt; finite</c> holds, <c>&lt;= finite</c>
    /// does not, and <c>== finite</c> never does. A sentinel numeric encoding was deliberately rejected: any finite
    /// stand-in is a value an authored comparand could legitimately equal, and a comparison that cannot distinguish
    /// "forever" from one particular number is lying about one of them.</summary>
    /// <param name="comparison">The comparison to evaluate.</param>
    /// <param name="value">The observed value; ignored when <paramref name="valueIsForever"/>.</param>
    /// <param name="valueIsForever">Whether the observed side is positive infinity.</param>
    /// <param name="expected">The value compared against; ignored when <paramref name="expectedIsForever"/>.</param>
    /// <param name="expectedIsForever">Whether the expected side is positive infinity.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    public static bool Holds(this ActionStateComparison comparison, FixedQ4816 value, bool valueIsForever, FixedQ4816 expected, bool expectedIsForever) {
        if (
            !valueIsForever &&
            !expectedIsForever
        ) {
            return comparison.Holds(
                expected: expected,
                value: value
            );
        }

        // Exactly one or both sides are infinite; the finite magnitudes no longer matter, only the ordering sign.
        var sign = ((valueIsForever, expectedIsForever)) switch {
            (true, true) => 0,
            (true, false) => 1,
            _ => -1,
        };

        return comparison switch {
            ActionStateComparison.Equal => (sign == 0),
            ActionStateComparison.NotEqual => (sign != 0),
            ActionStateComparison.Less => (sign < 0),
            ActionStateComparison.LessOrEqual => (sign <= 0),
            ActionStateComparison.Greater => (sign > 0),
            _ => (sign >= 0),
        };
    }
}
