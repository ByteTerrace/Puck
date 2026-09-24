using Puck.Commands;
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
        var parent = Child(
            fixture.Server,
            "a"
        );

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            parent with { Id = "annex", Parent = parent.Id, DealSlot = null }
        ));
        fixture.Step();
        Assert.Contains(
            collection: fixture.Server.Definition.Placements,
            filter: placement => (placement.Id == "annex")
        );
        Assert.False(condition: fixture.Server.TryPreviewReflow(
            new WorldPlacementReflowRequest(
                TemplateId,
                Edits: [new WorldPlacementReflowEdit(
                        parent.Id,
                        Position: new DocumentVector3(
                            x: 6,
                            y: 0,
                            z: 0
                        )
                    )]
            ),
            Principal.Console,
            out _,
            out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "subtree"
        );
        Assert.Equal(
            100,
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
    }
    [Fact]
    public void ReflowRejectsMalformedExplicitGroupsAndNullRequestsByName() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();

        Assert.False(condition: fixture.Server.TryPreviewReflow(
            ((WorldPlacementReflowRequest)null!),
            Principal.Console,
            out _,
            out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "request"
        );
        Assert.False(condition: fixture.Server.TryPreviewReflow(
            new WorldPlacementReflowRequest(
                TemplateId,
                PlacementIds: ["a", "a"]
            ),
            Principal.Console,
            out _,
            out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "distinct"
        );
        Assert.False(condition: fixture.Server.TryPreviewReflow(
            new WorldPlacementReflowRequest(
                TemplateId,
                PlacementIds: ["missing"]
            ),
            Principal.Console,
            out _,
            out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "existing"
        );
    }
    [Fact]
    public void ExplicitGrowthReflowsAndUndoRestoresPlacementAndPaymentAtomically() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        OverlapChildren(fixture);
        var before = Child(
            fixture.Server,
            "a"
        );
        var request = new WorldPlacementReflowRequest(
            TemplateId,
            Edits: [new WorldPlacementReflowEdit(
                    before.Id,
                    Scale: 1.5f
                )]
        );

        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                request,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        fixture.Server.EnqueueMutation(proposal!.Mutation);
        fixture.Step();
        Assert.Equal(
            1.5f,
            Child(
                fixture.Server,
                "a"
            ).Scale
        );
        Assert.Equal(
            (100 - proposal.Cost),
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );

        fixture.Server.EnqueueUndo(
            1,
            Principal.Console
        );
        fixture.Step();
        Assert.Equal(
            before.Scale,
            Child(
                fixture.Server,
                "a"
            ).Scale
        );
        Assert.Equal(
            100,
            WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                "credits"
            )!.Cells![0].Value.Raw
        );
    }
    [Fact]
    public void PinnedSpatialTransformRejectsAnExplicitScaleEdit() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        OverlapChildren(
            fixture,
            pinned: true
        );
        var pinned = Child(
            fixture.Server,
            "a"
        );
        var request = new WorldPlacementReflowRequest(
            TemplateId,
            Edits: [new WorldPlacementReflowEdit(
                    pinned.Id,
                    Scale: 2f
                )]
        );

        Assert.False(condition: fixture.Server.TryPreviewReflow(
            request,
            Principal.Console,
            out var proposal,
            out var reason
        ));
        Assert.Null(@object: proposal);
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "pinned"
        );
    }
    [Fact]
    public void PreservedCoverageRejectsMovingAProviderAwayFromANonselectedTarget() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        var a = Child(
            fixture.Server,
            "a"
        );
        var target = a with {
            Id = "target",
            Parent = null,
            DealSlot = null,
            Position = fixture.Server.Definition.PlacementFrames[a.Id].Position,
            Spatial = ReflowSpatial(),
        };
        var provider = a with {
            Spatial = [
                .. ReflowSpatial(),
                new WorldPlacementSpatialVolume(
                "water",
                WorldPlacementSpatialRole.Influence,
                new WorldSpatialShape(
                    WorldSpatialShapeKind.Sphere,
                    Vector3.Zero,
                    Vector3.Zero,
                    Radius: 1f
                ),
                "water"
            )
            ],
        };

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            provider
        ));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            target
        ));
        fixture.Step();
        Assert.Equal(
            1,
            fixture.Server.Definition.SpatialQueryIndex.CountInfluences(
                channel: "water",
                placementId: target.Id
            )
        );

        var request = new WorldPlacementReflowRequest(
            TemplateId,
            PlacementIds: [a.Id, Child(
                    fixture.Server,
                    "b"
                ).Id],
            Edits: [new WorldPlacementReflowEdit(
                    a.Id,
                    Position: new DocumentVector3(
                        x: 10,
                        y: 0,
                        z: 0
                    )
                )],
            PreserveInfluenceCoverage: true
        );

        Assert.False(condition: fixture.Server.TryPreviewReflow(
            request,
            Principal.Console,
            out var proposal,
            out var reason
        ));
        Assert.Null(@object: proposal);
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "influence"
        );
    }
    [Fact]
    public void CoveragePreservationAcceptsOnlyCoverageSafeLayout() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());

        fixture.Step();
        var a = Child(
            fixture.Server,
            "a"
        );
        var b = Child(
            fixture.Server,
            "b"
        );
        var target = a with {
            Id = "coverage-target",
            Parent = TemplateId,
            DealSlot = null,
            Position = new DocumentVector3(
            x: 2,
            y: 1.5f,
            z: 0
        ),
            Spatial = ReflowSpatial(),
        };
        var providerA = a with {
            Position = new DocumentVector3(
            x: 2,
            y: 0,
            z: 0
        ),
            Spatial = ReflowSpatial(),
        };
        var providerB = b with {
            Position = new DocumentVector3(
            x: 2,
            y: 0,
            z: 0
        ),
            Spatial = ProviderSpatial(
            influenceCenterX: 0,
            influenceCenterY: 1.5f
        ),
        };

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            providerA
        ));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            providerB
        ));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            target
        ));
        fixture.Step();
        var beforeCoverage = fixture.Server.Definition.SpatialQueryIndex.CountInfluences(
            channel: "water",
            placementId: target.Id
        );

        Assert.True(condition: (beforeCoverage > 0));

        // Without coverage preservation, the first clear layout moves b away from its target.
        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                TemplateId,
                Principal.Console,
                out var first,
                out var firstReason
            ),
            userMessage: firstReason
        );
        Assert.Contains(
            collection: first!.Mutation.Mutations.OfType<WorldMutation.UpsertPlacement>(),
            filter: mutation => (mutation.Placement.Id == b.Id)
        );
        var request = new WorldPlacementReflowRequest(
            TemplateId,
            PreserveInfluenceCoverage: true
        );

        Assert.True(
            condition: fixture.Server.TryPreviewReflow(
                request,
                Principal.Console,
                out var proposal,
                out var reason
            ),
            userMessage: reason
        );
        var movedA = Assert.Single(collection: proposal!.Mutation.Mutations.OfType<WorldMutation.UpsertPlacement>());

        Assert.Equal(
            a.Id,
            movedA.Placement.Id
        );
        Assert.Equal(
            Vector3.Zero,
            movedA.Placement.Position.Value
        );
        fixture.Server.EnqueueMutation(proposal.Mutation);
        fixture.Step();
        Assert.Equal(
            beforeCoverage,
            fixture.Server.Definition.SpatialQueryIndex.CountInfluences(
                channel: "water",
                placementId: target.Id
            )
        );
    }
    [Fact]
    public void SpatialConflictsRespectVerticalSeparationAndRotatedNarrowphase() {
        var above = PlacementSpatialMarker(
            "above",
            new Vector3(
                x: 0,
                y: 5,
                z: 0
            ),
            Vector3.One,
            0f
        );
        var ground = PlacementSpatialMarker(
            "ground",
            Vector3.Zero,
            Vector3.One,
            0f
        );
        var verticalIndex = WorldSpatialQueryCompilation.Compile(placements: [ground, above]);

        Assert.False(condition: WorldSpatialQueryIndex.Conflicts(
            left: verticalIndex.Volumes[0],
            right: verticalIndex.Volumes[1]
        ));

        var square = PlacementSpatialMarker(
            "square",
            Vector3.Zero,
            Vector3.One,
            0f
        );
        var diagonal = PlacementSpatialMarker(
            "diagonal",
            new Vector3(
                x: 2.1f,
                y: 0,
                z: 2.1f
            ),
            Vector3.One,
            45f
        );
        var rotatedIndex = WorldSpatialQueryCompilation.Compile(placements: [square, diagonal]);

        Assert.True(condition: rotatedIndex.Volumes[0].Bounds.Intersects(other: rotatedIndex.Volumes[1].Bounds));
        Assert.False(condition: WorldSpatialQueryIndex.Conflicts(
            left: rotatedIndex.Volumes[0],
            right: rotatedIndex.Volumes[1]
        ));
    }

    private static WorldPlacement PlacementSpatialMarker(string id, Vector3 position, Vector3 halfExtents, float yaw) => new(
        id,
        "marker",
        new DocumentVector3(value: position),
        0f,
        1f,
        Spatial: [new WorldPlacementSpatialVolume(
                "body",
                WorldPlacementSpatialRole.Occupation,
                new WorldSpatialShape(
                    WorldSpatialShapeKind.Box,
                    Vector3.Zero,
                    halfExtents,
                    YawDegrees: yaw
                )
            )]
    );
    private static IReadOnlyList<WorldPlacementSpatialVolume> ProviderSpatial(float influenceCenterX, float influenceCenterY) => [
        new(
            "body",
            WorldPlacementSpatialRole.Occupation,
            new WorldSpatialShape(
                WorldSpatialShapeKind.Box,
                Vector3.Zero,
                new Vector3(value: .4f)
            )
        ),
        new(
            "water",
            WorldPlacementSpatialRole.Influence,
            new WorldSpatialShape(
                WorldSpatialShapeKind.Sphere,
                new Vector3(
                    x: influenceCenterX,
                    y: influenceCenterY,
                    z: 0
                ),
                Vector3.Zero,
                Radius: .3f
            ),
            "water"
        )
    ];
}
