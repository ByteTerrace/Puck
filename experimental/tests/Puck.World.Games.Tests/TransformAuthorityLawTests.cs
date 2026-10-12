using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>Laws for the authority a submitted <see cref="WorldMutation.TransformState"/> needs: edit over every row or
/// pool the operation writes and observe over every row it only reads (<see cref="StateTransform.Subjects"/>).</summary>
public sealed class TransformAuthorityLawTests {
    private static readonly Lazy<WorldDefinition> PushRayWorld = new(valueFactory: () => AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Canaries/push-ray/fixture.puck"));

    // A peer holding the state section's Mutate but no Edit over the pool submits the fixture's own push: the edit
    // check names the pool and refuses it. Once the peer holds Edit over the pool the check admits it, and the push
    // is refused later, by its composition, for the live mover only a rule's pool iteration binds.
    [Fact]
    public void APushRayIsRefusedToAPrincipalWithoutEditOverItsPool() {
        using var fixture = Fixtures.FreshServer(definition: PushRayWorld.Value);
        var peer = Principal.Peer(
            generation: 1,
            index: 4
        );
        var push = new StateTransform.PushRay(
            Pool: CellName.Parse(candidate: "pieces"),
            Cell: CellName.Parse(candidate: "cell"),
            Value: CellName.Parse(candidate: "kind"),
            From: StateChannelRef.OfBindingField(
                binding: "mover",
                field: "cell"
            ),
            Topology: CellName.Parse(candidate: "board"),
            Direction: CellName.Parse(candidate: "E"),
            Pattern: "pushable",
            PushPattern: "push",
            StopPattern: "stop",
            Empty: 0
        );
        var rejections = new List<string>();

        fixture.Server.EchoTap = echo => {
            if (echo.Rejected) {
                rejections.Add(item: echo.Message);
            }
        };
        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Budget: 16,
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State),
                Grantee: peer,
                Subject: GrantSubject.Section(section: WorldSection.State)
            )
        );

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.TransformState(
            Principal: peer,
            Transform: push
        ));
        fixture.Step();

        var denied = Assert.Single(collection: rejections);

        Assert.Contains(
            actualString: denied,
            expectedSubstring: "cannot edit state:pieces"
        );

        rejections.Clear();
        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Capability: WorldCapability.Edit,
                Exclusive: false,
                Grantee: peer,
                Subject: GrantSubject.State(name: "pieces")
            )
        );
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.TransformState(
            Principal: peer,
            Transform: push
        ));
        fixture.Step();

        var composed = Assert.Single(collection: rejections);

        Assert.DoesNotContain(
            actualString: composed,
            expectedSubstring: "cannot edit"
        );
        Assert.Contains(
            actualString: composed,
            expectedSubstring: "pushRay"
        );
        Assert.Equal(
            actual: push.Subjects(),
            expected: [new StateSubject(Access: StateAccess.Write, Name: "pieces")]
        );
    }
}
