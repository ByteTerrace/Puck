using System.Text;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

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

    /// <summary>The body's origin sits below the medium's free surface along its own resolved gravity-up. Written
    /// by the medium hold's law (<see cref="BodyMotionOp.ApplyHold"/>); holds one tick behind that stage's
    /// evaluation, the same one-tick-behind discipline <see cref="Grounded"/> reads under.</summary>
    InMedium,

    /// <summary>The body's origin is inside the medium's equilibrium band (within its equilibrium offset of the
    /// equilibrium line). Written by the same medium law as <see cref="InMedium"/>, on the same one-tick-behind
    /// terms.</summary>
    AtMediumBand,

    /// <summary>The body holds a surface the contact resolve would refuse to stand it on — a face outside the
    /// world's own walkable cone. Not mutually exclusive with <see cref="Grounded"/>/<see cref="Airborne"/>: a gate
    /// wanting "on a wall only" names this fact rather than negating the other two.</summary>
    HoldingUnwalkable,

    /// <summary>The body holds itself up with no surface at all — a free hold with lift.</summary>
    Unsupported,

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

    /// <inheritdoc cref="ActionFact.InMedium"/>
    InMedium = (1 << 4),

    /// <inheritdoc cref="ActionFact.AtMediumBand"/>
    AtMediumBand = (1 << 5),

    /// <inheritdoc cref="ActionFact.HoldingUnwalkable"/>
    HoldingUnwalkable = (1 << 6),

    /// <inheritdoc cref="ActionFact.Unsupported"/>
    Unsupported = (1 << 7),

    /// <inheritdoc cref="ActionFact.Resting"/>
    Resting = (1 << 8),

    /// <summary>Every declared bit — the decoder's admission mask.</summary>
    All = (Grounded | Airborne | Rising | Falling | InMedium | AtMediumBand | HoldingUnwalkable | Unsupported | Resting),
}
/// <summary>The one mapping between the predicate vocabulary, its publishable bit, and the wire spelling every
/// read-back echoes — a single ordered table, so a new fact is added in exactly one place.</summary>
public static class BodyFactVocabulary {
    // Bit order is the wire order: Publishable's iteration order and Describe's joined order both derive from this
    // table's declaration order, so reordering a row changes the wire.
    private static readonly (ActionFact Fact, BodyFacts Bit, string Token)[] s_rows = [
        (ActionFact.Grounded, BodyFacts.Grounded, "grounded"),
        (ActionFact.Airborne, BodyFacts.Airborne, "airborne"),
        (ActionFact.Rising, BodyFacts.Rising, "rising"),
        (ActionFact.Falling, BodyFacts.Falling, "falling"),
        (ActionFact.InMedium, BodyFacts.InMedium, "inmedium"),
        (ActionFact.AtMediumBand, BodyFacts.AtMediumBand, "atmediumband"),
        (ActionFact.HoldingUnwalkable, BodyFacts.HoldingUnwalkable, "holdingunwalkable"),
        (ActionFact.Unsupported, BodyFacts.Unsupported, "unsupported"),
        (ActionFact.Resting, BodyFacts.Resting, "resting"),
    ];
    private static readonly ActionFact[] s_publishable = Array.ConvertAll(
        array: s_rows,
        converter: static row => row.Fact
    );
    // Indexed by (int)ActionFact — a dense byte enum — so the per-body per-tick publish loop (WorldBody.Facts)
    // reads a bit per fact without scanning s_rows. Built once from it, so the wire order stays the single
    // s_rows declaration.
    private static readonly BodyFacts[] s_bitByFact = BuildBitByFact();
    private static readonly string[] s_tokenByFact = BuildTokenByFact();

    private static BodyFacts[] BuildBitByFact() {
        var table = new BodyFacts[(Enum.GetValues<ActionFact>().Length)];

        foreach (var row in s_rows) {
            table[(int)row.Fact] = row.Bit;
        }

        return table;
    }
    private static string[] BuildTokenByFact() {
        var table = new string[(Enum.GetValues<ActionFact>().Length)];

        Array.Fill(
            array: table,
            value: "affectedby"
        );

        foreach (var row in s_rows) {
            table[(int)row.Fact] = row.Token;
        }

        return table;
    }

    /// <summary>The body-state facts carrying a <see cref="BodyFacts"/> bit, in bit order — the order every echo
    /// joins them in.</summary>
    public static ReadOnlySpan<ActionFact> Publishable => s_publishable;

    /// <summary>Returns the mask bit a publishable fact carries, or <see cref="BodyFacts.None"/> for a fact with no
    /// bit (<see cref="ActionFact.AffectedBy"/>).</summary>
    /// <param name="fact">The fact to map.</param>
    /// <returns>The bit.</returns>
    public static BodyFacts Bit(ActionFact fact) => s_bitByFact[(int)fact];
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
    public static string Token(ActionFact fact) => s_tokenByFact[(int)fact];
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
