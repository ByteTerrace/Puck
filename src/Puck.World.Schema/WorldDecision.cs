using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>How an eligible decision option wins.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldDecisionMode>))]
public enum WorldDecisionMode : byte {
    /// <summary>The greatest score wins; equal scores retain option order, then stable body index within a neighbor option.</summary>
    HighestScore,
    /// <summary>Positive scores are relative weights; non-positive scores cannot win.</summary>
    Weighted,
}

/// <summary>A bounded choice policy attached to an ordinary world rule, using its existing bindings and effects.</summary>
/// <param name="Options">One to 32 uniquely named options, in deterministic tie-break order.</param>
/// <param name="PeriodSeconds">Positive exact engine-tick duration between reconsiderations. First evaluation is immediate.</param>
/// <param name="Mode">Highest score or weighted choice.</param>
/// <param name="ScoreKind">The common numeric kind, Int or Fixed; operands must match it.</param>
/// <param name="CommitmentSeconds">Non-negative exact duration protecting a newly selected option from ordinary reconsideration.</param>
/// <param name="IncumbentBonus">Non-negative score bonus for retaining the current eligible option and individual. In weighted mode it cannot revive a non-positive weight.</param>
/// <param name="Seed">Authored seed combined with the world seed, rule name, binding key, and body generation.</param>
/// <param name="Interrupt">An optional rising-edge predicate that bypasses period and commitment. Losing eligibility also bypasses both.</param>
/// <param name="OnNoChoice">Effects fired when an enabled decision first finds no choice, loses its choice, or its enclosing gate closes while a choice is held.</param>
/// <remarks>Common rule effects precede selected-option effects, only on a selection transition. Staying with an option
/// neither repeats its effects nor renews commitment. Closing the enclosing gate clears the choice but preserves the
/// local random stream. Effects retain normal rule refusal/transaction semantics: selection is intent, not proof that
/// every effect succeeded. A weighted reconsideration with multiple positive options consumes exactly two PCG32 draws;
/// its 64-bit ticket quantizes each ideal probability with absolute error below 2^-64. No eligible option means no choice,
/// never an unchecked fallback. These policies are independent of render-frame timing.</remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDecision(
    IReadOnlyList<WorldDecisionOption> Options,
    decimal PeriodSeconds,
    WorldDecisionMode Mode = WorldDecisionMode.HighestScore,
    CellKind ScoreKind = CellKind.Fixed,
    decimal CommitmentSeconds = 0,
    decimal IncumbentBonus = 0,
    ulong Seed = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Interrupt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ActionEffect>? OnNoChoice = null
);

/// <summary>One candidate in a decision; its score is read only when its gate holds.</summary>
/// <param name="Name">The stable, non-empty option name.</param>
/// <param name="Score">A numeric expression in the decision's score kind. Arithmetic failure makes this option ineligible for that reconsideration.</param>
/// <param name="Effects">Effects fired when entering this option; may be empty for an inspection-only choice.</param>
/// <param name="Gate">Eligibility predicate, or null for always eligible.</param>
/// <param name="Neighbors">Optional bounded nearby-body expansion. Requires rule forEach; each and left identify the observer,
/// right the candidate, only inside this option's gate, score, and effects. A different individual is a selection transition.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDecisionOption(
    WorldCellName Name,
    WorldValueExpression Score,
    IReadOnlyList<ActionEffect> Effects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Gate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDecisionNeighbors? Neighbors = null
);

/// <summary>Bounded physical perception for a parameterized decision option; memory alone does not reveal a body's location.</summary>
/// <param name="Range">Inclusive spherical radius, in world units, from one Q48.16 unit through 1,000,000.</param>
/// <param name="CandidateBudget">Maximum inspected points per reconsideration, including self, rejected points, and an incumbent recheck.</param>
/// <param name="MaxCandidates">Maximum candidates scored, from 1 through 32 and no greater than CandidateBudget.</param>
/// <param name="HalfAngleDegrees">Forward cone half-angle in (0,180]; coincident points are perceptible.</param>
/// <param name="RequiresLineOfSight">Whether candidates must pass the world's solid-field sight query.</param>
/// <param name="RetainCurrent">Spend one inspection on the current individual first. If still perceptible and eligible,
/// reserve one retained slot for it. A budget of one can spend all attention on the incumbent.</param>
/// <remarks>Positions and orientations are frozen before the ordinary rule pass. Gates and solid fields retain ordinary
/// same-tick read semantics. The remaining sample rotates deterministically; it need not contain the globally best candidate.
/// Range/cone/sight refresh only on reconsideration. Loss of the selected incarnation or its gate interrupts commitment immediately.</remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDecisionNeighbors(decimal Range, int CandidateBudget, int MaxCandidates,
    decimal HalfAngleDegrees = 180, bool RequiresLineOfSight = false, bool RetainCurrent = true);

/// <summary>Validated, fixed-point neighbor perception parameters.</summary>
public sealed record CompiledWorldDecisionNeighbors(WorldDecisionNeighbors Source, FixedQ4816 Range, FixedQ4816 MinimumDot) {
    /// <summary>Gets the power-of-two raw grid width shared by ranges of the same scale. Bounds each query to 27 cells.</summary>
    public FixedQ4816 CellWidth => FixedQ4816.FromRawBits(checked((long)BitOperations.RoundUpToPowerOf2((ulong)Range.Value)));
}

/// <summary>A compiled decision policy. PolicyIdentity is the canonical source rule and seeds lifecycle reconciliation, not randomness.</summary>
public sealed record CompiledWorldDecision(
    CompiledWorldDecisionOption[] Options, WorldDecisionMode Mode, CellKind ScoreKind,
    ulong PeriodTicks, ulong CommitmentTicks, long IncumbentBonus, ulong Seed,
    CompiledWorldPredicate[]? Interrupt, CompiledWorldEffect[] OnNoChoice, string PolicyIdentity
);

/// <summary>A decision option compiled through the ordinary world predicate, expression, and effect compilers.</summary>
public sealed record CompiledWorldDecisionOption(
    string Name, CompiledWorldPredicate[] Gate, CompiledWorldExpressionToken[] Score, CompiledWorldEffect[] Effects,
    CompiledWorldDecisionNeighbors? Neighbors = null
);
