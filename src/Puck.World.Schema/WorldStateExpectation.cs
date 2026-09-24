namespace Puck.World;

/// <summary>A numeric or text precondition and optional exact change, evaluated at the same commit tick.</summary>
/// <param name="Row">The declared state row.</param>
/// <param name="Key">A keyed cell, or null for the row's slot.</param>
/// <param name="Value">The raw comparison value in that row's cell kind.</param>
/// <param name="Comparison">The required comparison before any member composes.</param>
/// <param name="Change">Optional exact raw difference after all members compose; clamping or overflow refuses the batch.</param>
/// <param name="Kind">Optional required cell kind, checked before and after composition.</param>
/// <param name="Text">Optional required text value for text state rows, checked before composition.</param>
public sealed record WorldStateExpectation(string Row, string? Key, long Value = 0,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(ExpressionComparisonJsonConverter))] ExpressionOp Comparison = ExpressionOp.Equal, long? Change = null, CellKind? Kind = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Text = null);
