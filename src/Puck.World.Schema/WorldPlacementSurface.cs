using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>One-time directed projection of static occurrence roots onto named static contact geometry. The
/// occurrence keeps its authored rotation and scale; its resolved origin is shared by presentation and collision.</summary>
/// <param name="Targets">Placement IDs whose static contact fields may receive a root. Surface and parent dependencies must be acyclic.</param>
/// <param name="FromOffset">World-space offset from each candidate root to the ray origin.</param>
/// <param name="Direction">World-space ray direction, normalized in fixed point.</param>
/// <param name="Reach">Maximum ray length in world units.</param>
/// <param name="Offset">Signed world-space displacement along the hit normal (negative embeds a root).</param>
/// <param name="MinFacing">Minimum cosine of hit normal with the opposite cast direction, in [0,1].</param>
/// <param name="RequireHit">Refuse derivation instead of dropping an occurrence when no proven eligible surface is found.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementSurface(IReadOnlyList<string> Targets, DocumentVector3 FromOffset,
    DocumentVector3 Direction, float Reach, float Offset = 0f, float MinFacing = 0f, bool RequireHit = false);
