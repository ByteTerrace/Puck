using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>Author-selected work and payment policy for rearranging a deal template's children.</summary>
/// <param name="CandidateBudget">Maximum preparation, candidate, and overlap work units in one preview;
/// preparation includes a conservative charge for sorting offsets. Exhaustion refuses without edits.</param>
/// <param name="CostPerMove">Nonnegative Int units charged for each changed placement, including a spatial seed edit.</param>
/// <param name="CostRow">An Int state row paying the price. Required for a nonzero price.</param>
/// <param name="CostKey">The payer cell; absent selects an unkeyed row's slot.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementReflow(int CandidateBudget = 4096, long CostPerMove = 0,
    string? CostRow = null, string? CostKey = null);
