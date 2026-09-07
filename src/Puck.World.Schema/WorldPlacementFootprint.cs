using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>An authored rectangular occupation footprint in placement-local X/Z, with additional passage clearance.
/// Used by layout proposals; physical collision remains the placement's solid geometry.</summary>
/// <param name="HalfWidth">Positive local X half-extent.</param>
/// <param name="HalfDepth">Positive local Z half-extent.</param>
/// <param name="Clearance">Nonnegative space reserved outside the footprint.</param>
/// <param name="Pinned">Whether automatic layout must preserve this placement's transform.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementFootprint(float HalfWidth, float HalfDepth, float Clearance = 0, bool Pinned = false);

/// <summary>Author-selected work and payment policy for rearranging a deal template's children.</summary>
/// <param name="CandidateBudget">Maximum preparation, candidate, and overlap work units in one preview;
/// preparation includes a conservative charge for sorting offsets. Exhaustion refuses without edits.</param>
/// <param name="CostPerMove">Nonnegative Int units charged for each changed transform.</param>
/// <param name="CostRow">An Int state row paying the price. Required for a nonzero price.</param>
/// <param name="CostKey">The payer cell; absent selects an unkeyed row's slot.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementReflow(int CandidateBudget = 4096, long CostPerMove = 0,
    string? CostRow = null, string? CostKey = null);
