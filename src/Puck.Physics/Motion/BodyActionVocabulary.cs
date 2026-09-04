using System.Text;
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

    /// <summary>The body's origin is below the medium surface. Written by the medium hold's law
    /// (<see cref="BodyMotionOp.ApplyHold"/>); holds one tick behind that stage's evaluation, the same
    /// one-tick-behind discipline <see cref="Grounded"/> reads under.</summary>
    Submerged,

    /// <summary>The body's origin is inside the medium's surface bob band (within its float depth of the float
    /// line). Written by the same medium law as <see cref="Submerged"/>, on the same one-tick-behind terms.</summary>
    AtSurface,

    /// <summary>The body holds a surface the contact resolve would refuse to stand it on — a face outside the
    /// world's own walkable cone. Not mutually exclusive with <see cref="Grounded"/>/<see cref="Airborne"/>: a gate
    /// wanting "on a wall only" names this fact rather than negating the other two.</summary>
    Climbing,

    /// <summary>The body holds itself up with no surface at all — a free hold with lift.</summary>
    Flying,

    /// <summary>A rigid body's linear and angular velocity have latched to zero after settling — written by the
    /// rigid solver, never by a locomotion program.</summary>
    Resting,
}
/// <summary>The publishable per-body fact set — one bit per body-state <see cref="ActionFact"/>, so the simulation's
/// predicates and the wire share one vocabulary rather than a parallel enum. <see cref="ActionFact.AffectedBy"/> has
/// no bit: it names a relationship to another body for one tick, not a state of this one.</summary>
/// <remarks>The declared bit values are the wire encoding. A decoder refuses any bit outside <see cref="All"/> by
/// name; the mask is not a closed set of legal COMBINATIONS, since a body can legitimately be grounded and rising in
/// the same tick.</remarks>
[Flags]
public enum BodyFacts : ushort {
    /// <summary>No fact holds — an inactive or never-advanced body.</summary>
    None = 0,

    /// <inheritdoc cref="ActionFact.Grounded"/>
    Grounded = (1 << 0),

    /// <inheritdoc cref="ActionFact.Airborne"/>
    Airborne = (1 << 1),

    /// <inheritdoc cref="ActionFact.Rising"/>
    Rising = (1 << 2),

    /// <inheritdoc cref="ActionFact.Falling"/>
    Falling = (1 << 3),

    /// <inheritdoc cref="ActionFact.Submerged"/>
    Submerged = (1 << 4),

    /// <inheritdoc cref="ActionFact.AtSurface"/>
    AtSurface = (1 << 5),

    /// <inheritdoc cref="ActionFact.Climbing"/>
    Climbing = (1 << 6),

    /// <inheritdoc cref="ActionFact.Flying"/>
    Flying = (1 << 7),

    /// <inheritdoc cref="ActionFact.Resting"/>
    Resting = (1 << 8),

    /// <summary>Every declared bit — the decoder's admission mask.</summary>
    All = (Grounded | Airborne | Rising | Falling | Submerged | AtSurface | Climbing | Flying | Resting),
}
/// <summary>The one mapping between the predicate vocabulary and its publishable bit, plus the wire spelling every
/// read-back echoes.</summary>
public static class BodyFactVocabulary {
    /// <summary>The body-state facts carrying a <see cref="BodyFacts"/> bit, in bit order — the order every echo
    /// joins them in.</summary>
    public static ReadOnlySpan<ActionFact> Publishable => [
        ActionFact.Grounded,
        ActionFact.Airborne,
        ActionFact.Rising,
        ActionFact.Falling,
        ActionFact.Submerged,
        ActionFact.AtSurface,
        ActionFact.Climbing,
        ActionFact.Flying,
        ActionFact.Resting,
    ];

    /// <summary>Returns the mask bit a publishable fact carries, or <see cref="BodyFacts.None"/> for a fact with no
    /// bit (<see cref="ActionFact.AffectedBy"/>).</summary>
    /// <param name="fact">The fact to map.</param>
    /// <returns>The bit.</returns>
    public static BodyFacts Bit(ActionFact fact) => fact switch {
        ActionFact.Grounded => BodyFacts.Grounded,
        ActionFact.Airborne => BodyFacts.Airborne,
        ActionFact.Rising => BodyFacts.Rising,
        ActionFact.Falling => BodyFacts.Falling,
        ActionFact.Submerged => BodyFacts.Submerged,
        ActionFact.AtSurface => BodyFacts.AtSurface,
        ActionFact.Climbing => BodyFacts.Climbing,
        ActionFact.Flying => BodyFacts.Flying,
        ActionFact.Resting => BodyFacts.Resting,
        _ => BodyFacts.None,
    };
    /// <summary>Formats a mask as lower-case, <c>|</c>-joined tokens in bit order, or <c>none</c> when empty — the
    /// read-back spelling <c>body.where</c> echoes.</summary>
    /// <param name="facts">The mask to spell.</param>
    /// <returns>The token string.</returns>
    public static string Describe(BodyFacts facts) {
        if ((facts & BodyFacts.All) == BodyFacts.None) {
            return "none";
        }

        var text = new StringBuilder();

        foreach (var fact in Publishable) {
            if ((facts & Bit(fact: fact)) == BodyFacts.None) {
                continue;
            }
            if (text.Length > 0) {
                _ = text.Append(value: '|');
            }

            _ = text.Append(value: Token(fact: fact));
        }

        return text.ToString();
    }
    /// <summary>The gate token meaning "no gate" — a driver's weight holds regardless of the body's facts.</summary>
    public const string Always = "always";

    /// <summary>Resolves an authored gate token to the single <see cref="BodyFacts"/> bit it tests: a publishable
    /// fact's member name (case-sensitive, like every document token), or <see cref="Always"/>/null for no gate.</summary>
    /// <param name="name">The authored token.</param>
    /// <param name="gate">The bit, or <see cref="BodyFacts.None"/> for an ungated token; zero on failure.</param>
    /// <returns><see langword="true"/> when the token names a publishable fact or no gate.</returns>
    public static bool TryResolve(string? name, out BodyFacts gate) {
        gate = BodyFacts.None;

        if (
            (name is null) ||
            string.Equals(
                a: name,
                b: Always,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            return true;
        }

        foreach (var fact in Publishable) {
            if (string.Equals(
                a: name,
                b: fact.ToString(),
                comparisonType: StringComparison.Ordinal
            )) {
                gate = Bit(fact: fact);

                return true;
            }
        }

        return false;
    }
    /// <summary>Returns whether a gate holds against a body's facts — an ungated token always holds.</summary>
    /// <param name="gate">The gate bit, or <see cref="BodyFacts.None"/>.</param>
    /// <param name="facts">The body's published facts.</param>
    public static bool Holds(BodyFacts gate, BodyFacts facts) => ((gate == BodyFacts.None) || ((facts & gate) == gate));
    /// <summary>Returns a publishable fact's lower-case wire spelling.</summary>
    /// <param name="fact">The fact to spell.</param>
    /// <returns>The token.</returns>
    public static string Token(ActionFact fact) => fact switch {
        ActionFact.Grounded => "grounded",
        ActionFact.Airborne => "airborne",
        ActionFact.Rising => "rising",
        ActionFact.Falling => "falling",
        ActionFact.Submerged => "submerged",
        ActionFact.AtSurface => "atsurface",
        ActionFact.Climbing => "climbing",
        ActionFact.Flying => "flying",
        ActionFact.Resting => "resting",
        _ => "affectedby",
    };
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
