using Puck.Assets.Documents;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>One optional seed edit for a bounded placement reflow. Omitted members preserve their authored value.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementReflowEdit(
    string PlacementId,
    DocumentVector3? Position = null,
    float? YawDegrees = null,
    float? Scale = null,
    IReadOnlyList<WorldPlacementSpatialVolume>? Spatial = null
);

/// <summary>Describes a bounded atomic placement edit plus neighbor reflow. A null member selection uses all dealt
/// children of <see cref="TemplateId"/>; an explicit selection permits a bounded group of ordinary placements
/// sharing that template's parent frame and candidate offsets. Members must be leaves; moving a child subtree
/// requires a separate plan. Coverage preservation permits gains but no loss
/// of distinct providers at any affected occupation target.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementReflowRequest(
    string? TemplateId = null,
    IReadOnlyList<string>? PlacementIds = null,
    IReadOnlyList<WorldPlacementReflowEdit>? Edits = null,
    bool PreserveInfluenceCoverage = false
);
