using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class PlacementDealLawTests {
    [Fact]
    public void ReflowRefusesMovingAnUnmodelledChildSubtree() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        var parent = Child(fixture.Server, "a");
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console,
            parent with { Id = "annex", Parent = parent.Id, DealSlot = null }));
        fixture.Step();
        Assert.Contains(fixture.Server.Definition.Placements, placement => placement.Id == "annex");
        Assert.False(fixture.Server.TryPreviewReflow(new WorldPlacementReflowRequest(TemplateId,
            Edits: [new WorldPlacementReflowEdit(parent.Id, Position: new DocumentVector3(6, 0, 0))]),
            WorldPrincipal.Console, out _, out var reason));
        Assert.Contains("subtree", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(100, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
    }

    [Fact]
    public void ReflowRejectsMalformedExplicitGroupsAndNullRequestsByName() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();

        Assert.False(fixture.Server.TryPreviewReflow((WorldPlacementReflowRequest)null!, WorldPrincipal.Console, out _, out var reason));
        Assert.Contains("request", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Server.TryPreviewReflow(
            new WorldPlacementReflowRequest(TemplateId, PlacementIds: ["a", "a"]),
            WorldPrincipal.Console, out _, out reason));
        Assert.Contains("distinct", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Server.TryPreviewReflow(
            new WorldPlacementReflowRequest(TemplateId, PlacementIds: ["missing"]),
            WorldPrincipal.Console, out _, out reason));
        Assert.Contains("existing", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitGrowthReflowsAndUndoRestoresPlacementAndPaymentAtomically() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture);
        var before = Child(fixture.Server, "a");
        var request = new WorldPlacementReflowRequest(
            TemplateId,
            Edits: [new WorldPlacementReflowEdit(before.Id, Scale: 1.5f)]);

        Assert.True(fixture.Server.TryPreviewReflow(request, WorldPrincipal.Console, out var proposal, out var reason), reason);
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(1.5f, Child(fixture.Server, "a").Scale);
        Assert.Equal(100 - proposal.Cost, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);

        fixture.Server.EnqueueUndo(1, WorldPrincipal.Console);
        fixture.Step();
        Assert.Equal(before.Scale, Child(fixture.Server, "a").Scale);
        Assert.Equal(100, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "credits")!.Cells![0].Value);
    }

    [Fact]
    public void PinnedSpatialTransformRejectsAnExplicitScaleEdit() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture, pinned: true);
        var pinned = Child(fixture.Server, "a");
        var request = new WorldPlacementReflowRequest(
            TemplateId,
            Edits: [new WorldPlacementReflowEdit(pinned.Id, Scale: 2f)]);

        Assert.False(fixture.Server.TryPreviewReflow(request, WorldPrincipal.Console, out var proposal, out var reason));
        Assert.Null(proposal);
        Assert.Contains("pinned", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreservedCoverageRejectsMovingAProviderAwayFromANonselectedTarget() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        var a = Child(fixture.Server, "a");
        var target = a with {
            Id = "target",
            Parent = null,
            DealSlot = null,
            Position = fixture.Server.Definition.PlacementFrames[a.Id].Position,
            Spatial = ReflowSpatial()
        };
        var provider = a with {
            Spatial = [
                .. ReflowSpatial(),
                new WorldPlacementSpatialVolume("water", WorldPlacementSpatialRole.Influence,
                    new WorldSpatialShape(WorldSpatialShapeKind.Sphere, Vector3.Zero, Vector3.Zero, Radius: 1f), "water")
            ]
        };
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, provider));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, target));
        fixture.Step();
        Assert.Equal(1, fixture.Server.Definition.SpatialQueryIndex.CountInfluences("water", target.Id));

        var request = new WorldPlacementReflowRequest(
            TemplateId,
            PlacementIds: [a.Id, Child(fixture.Server, "b").Id],
            Edits: [new WorldPlacementReflowEdit(a.Id, Position: new DocumentVector3(10, 0, 0))],
            PreserveInfluenceCoverage: true);
        Assert.False(fixture.Server.TryPreviewReflow(request, WorldPrincipal.Console, out var proposal, out var reason));
        Assert.Null(proposal);
        Assert.Contains("influence", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoveragePreservationAcceptsOnlyCoverageSafeLayout() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        var a = Child(fixture.Server, "a");
        var b = Child(fixture.Server, "b");
        var target = a with {
            Id = "coverage-target",
            Parent = TemplateId,
            DealSlot = null,
            Position = new DocumentVector3(2, 1.5f, 0),
            Spatial = ReflowSpatial()
        };
        var providerA = a with { Position = new DocumentVector3(2, 0, 0), Spatial = ReflowSpatial() };
        var providerB = b with { Position = new DocumentVector3(2, 0, 0), Spatial = ProviderSpatial(0, 1.5f) };
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, providerA));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, providerB));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console, target));
        fixture.Step();
        var beforeCoverage = fixture.Server.Definition.SpatialQueryIndex.CountInfluences("water", target.Id);
        Assert.True(beforeCoverage > 0);

        // Without coverage preservation, the first clear layout moves b away from its target.
        Assert.True(fixture.Server.TryPreviewReflow(TemplateId, WorldPrincipal.Console, out var first, out var firstReason), firstReason);
        Assert.Contains(first!.Mutation.Mutations.OfType<WorldMutation.UpsertPlacement>(), mutation => mutation.Placement.Id == b.Id);
        var request = new WorldPlacementReflowRequest(TemplateId, PreserveInfluenceCoverage: true);
        Assert.True(fixture.Server.TryPreviewReflow(request, WorldPrincipal.Console, out var proposal, out var reason), reason);
        var movedA = Assert.Single(proposal!.Mutation.Mutations.OfType<WorldMutation.UpsertPlacement>());
        Assert.Equal(a.Id, movedA.Placement.Id);
        Assert.Equal(Vector3.Zero, movedA.Placement.Position.Value);
        fixture.Server.EnqueueMutation(proposal.Mutation);
        fixture.Step();
        Assert.Equal(beforeCoverage, fixture.Server.Definition.SpatialQueryIndex.CountInfluences("water", target.Id));
    }

    [Fact]
    public void SpatialConflictsRespectVerticalSeparationAndRotatedNarrowphase() {
        var above = PlacementSpatialMarker("above", new Vector3(0, 5, 0), Vector3.One, 0f);
        var ground = PlacementSpatialMarker("ground", Vector3.Zero, Vector3.One, 0f);
        var verticalIndex = WorldSpatialQueryCompilation.Compile([ground, above]);
        Assert.False(WorldSpatialQueryIndex.Conflicts(verticalIndex.Volumes[0], verticalIndex.Volumes[1]));

        var square = PlacementSpatialMarker("square", Vector3.Zero, Vector3.One, 0f);
        var diagonal = PlacementSpatialMarker("diagonal", new Vector3(2.1f, 0, 2.1f), Vector3.One, 45f);
        var rotatedIndex = WorldSpatialQueryCompilation.Compile([square, diagonal]);
        Assert.True(rotatedIndex.Volumes[0].Bounds.Intersects(rotatedIndex.Volumes[1].Bounds));
        Assert.False(WorldSpatialQueryIndex.Conflicts(rotatedIndex.Volumes[0], rotatedIndex.Volumes[1]));
    }

    private static WorldPlacement PlacementSpatialMarker(string id, Vector3 position, Vector3 halfExtents, float yaw) => new(
        id, "marker", new DocumentVector3(position), 0f, 1f,
        Spatial: [new WorldPlacementSpatialVolume("body", WorldPlacementSpatialRole.Occupation,
            new WorldSpatialShape(WorldSpatialShapeKind.Box, Vector3.Zero, halfExtents, YawDegrees: yaw))]);

    private static IReadOnlyList<WorldPlacementSpatialVolume> ProviderSpatial(float influenceCenterX, float influenceCenterY) => [
        new("body", WorldPlacementSpatialRole.Occupation,
            new WorldSpatialShape(WorldSpatialShapeKind.Box, Vector3.Zero, new Vector3(.4f))),
        new("water", WorldPlacementSpatialRole.Influence,
            new WorldSpatialShape(WorldSpatialShapeKind.Sphere, new Vector3(influenceCenterX, influenceCenterY, 0), Vector3.Zero, Radius: .3f), "water")
    ];
}
