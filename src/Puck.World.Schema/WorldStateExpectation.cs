namespace Puck.World;

/// <summary>A numeric precondition and optional exact change, evaluated at the same commit tick.</summary>
/// <param name="Row">The declared state row.</param>
/// <param name="Key">A keyed cell, or null for the row's slot.</param>
/// <param name="Value">The raw comparison value in that row's cell kind.</param>
/// <param name="Comparison">The required comparison before any member composes.</param>
/// <param name="Change">Optional exact raw difference after all members compose; clamping or overflow refuses the batch.</param>
/// <param name="Kind">Optional required cell kind, checked before and after composition.</param>
public sealed record WorldStateExpectation(string Row, string? Key, long Value,
    ActionStateComparison Comparison = ActionStateComparison.Equal, long? Change = null, CellKind? Kind = null);
