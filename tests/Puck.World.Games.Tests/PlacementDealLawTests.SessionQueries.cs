using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Games.Tests;

public sealed partial class PlacementDealLawTests {
    [InlineData(WorldDisclosureTier.Presentation, false)]
    [InlineData(WorldDisclosureTier.Replica, true)]
    [Theory]
    public void ADirectReflowPreviewUsesTheSessionsQueryFidelity(WorldDisclosureTier tier, bool allowed) {
        var document = ReflowDocument();
        var template = document.Placements[0];

        using var fixture = Fixtures.FreshServer(definition: (document with {
            PlacementRowsRaw = [template with { Deal = template.Deal! with { Reflow = new WorldPlacementReflow() } }],
            Admission = [new WorldAdmissionEntry(
                Algorithm: string.Empty,
                Disclosure: tier,
                Domain: WorldAdmissionEntry.AnyAuthority,
                Grants: [new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: GrantSubject.All, Budget: 64)],
                Mode: WorldAdmissionTrustMode.FederatedAuthority,
                PublicKey: string.Empty,
                Subject: null
            )],
        }));

        fixture.Step();
        OverlapChildren(fixture);
        using var observation = fixture.Server.TryObserveAsSession(
            sourceAuthority: "viewer/reflow",
            sink: new WorldSessionMirror(placeholder: WorldProjection.Undisclosed),
            refusal: out var refusal
        );

        Assert.NotNull(@object: observation);
        Assert.Empty(collection: refusal);
        fixture.Server.Grant(
            grant: new WorldGrant(
                observation.Session,
                WorldCapability.Mutate,
                GrantSubject.Section(section: WorldSection.Placements),
                Exclusive: false,
                Budget: 64,
                KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements)
            ),
            actor: Principal.Console
        );
        Assert.Equal(expected: allowed, actual: fixture.Server.TryPreviewReflow(
            principal: observation.Session,
            proposal: out var proposal,
            reason: out _,
            templateId: TemplateId
        ));
        Assert.Equal(actual: (proposal is not null), expected: allowed);
        observation.Dispose();
        Assert.Throws<InvalidOperationException>(testCode: () => {
            _ = fixture.Server.ReflowPreviewCompletion(principal: observation.Session);
        });
        Assert.False(condition: fixture.Server.TryTakeReviewedReflow(principal: observation.Session, proposal: out _, reason: out _));
    }
}
