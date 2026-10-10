using System.Numerics;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: a mutation forwarded to a committed traveler's destination is answered by the destination's own verdict,
/// at the tick boundary that applies it, through the same typed completion a local submitter gets. The red leg: before
/// the destination ticks nothing is answered at all, never a refusal standing in for an edit the tick then applies.
/// </summary>
public sealed class WorldForwardedSubmissionLawTests {
    private const string Source = "origin/world";

    private static readonly WorldMobilityIdentity Traveler = new(
        DepartedFrom: new WorldEntityAddress(Authority: Source, Generation: 7, Index: 4),
        Epoch: 0UL,
        Incarnation: new WorldEntityAddress(Authority: Source, Generation: 7, Index: 4)
    );

    [Fact]
    public void AForwardedMutationIsAnsweredByTheVerdictTheDestinationApplied() {
        var crate = CreationFixtures.UnitSphere(id: "crate");
        var document = Fixtures.PeerPopulationDocument(networkPlayers: 1);
        var placed = new WorldPlacement(Id: "crate1", Position: new Vector3(x: 1f, y: 3f, z: -1f), PrototypeId: crate.Id, Scale: 1f, YawDegrees: 0f);

        using var fixture = Fixtures.FreshServer(definition: (document with {
            CreationsRaw = [crate],
            PlacementsRaw = (document.PlacementsRaw! with { Rows = [placed] }),
        }));
        var reservation = fixture.Server.ReserveTransfer(request: new WorldTransferReservationRequest(
            Border: "seam",
            BorderCapacity: null,
            DeadlineSourceTick: 60,
            Members: [new WorldTransferReservationMember(
                BodyColor: default,
                CatalogRig: 4,
                Identity: null,
                Mobility: Traveler,
                PreferredSlot: 4,
                Principal: Principal.Console,
                Source: IntentSource.Live
            )],
            PartyAllOrNothing: true,
            PeerAdmission: true,
            SourceAuthority: Source,
            SourceRateHz: 240,
            SourceTick: 0,
            TransferId: 43
        ));
        var answers = new List<WorldSubmissionResult>();

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(
            condition: (fixture.Server.CommitTransfer(
                members: [new WorldTransferCommitMember(
                    BodyMotionProgramName: "grounded",
                    HasMappedArrival: false,
                    PlanarVelocity: default,
                    Position: default,
                    Profile: null,
                    VerticalVelocity: default,
                    YawRadians: default
                )],
                reason: out var reason,
                sourceAuthority: Source,
                transferId: 43
            ) == WorldTransferStatus.Committed),
            userMessage: reason
        );
        Assert.True(condition: fixture.Server.TryTransferredPrincipal(mobility: in Traveler, principal: out var traveler, sourceAuthority: Source));
        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(Budget: 4, Capability: WorldCapability.Mutate, Exclusive: false, Grantee: traveler, KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements), Subject: GrantSubject.Section(section: WorldSection.Placements))
        );

        var moved = (placed with { Position = new Vector3(x: 2f, y: 3f, z: -1f) });

        Assert.True(
            condition: WorldLocalForwardedAuthority.TryApplySubmission(
                completion: answers.Add,
                mobility: in Traveler,
                operationId: Guid.NewGuid(),
                payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.UpsertPlacement(Placement: moved, Principal: traveler)),
                reason: out reason,
                server: fixture.Server,
                sourceAuthority: Source
            ),
            userMessage: reason
        );

        // Red leg: buffered for the tick, the mutation has no verdict yet, and nothing stands in for one.
        Assert.Empty(collection: answers);

        fixture.Step();

        var verdict = Assert.IsType<WorldSubmissionResult.Mutation>(@object: Assert.Single(collection: answers));

        Assert.True(condition: verdict.Outcome.Applied, userMessage: verdict.Outcome.Detail);
        Assert.Equal(actual: ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: fixture.Server.Definition.Placements)!.Position), expected: ((Vector3)moved.Position));
    }
}
