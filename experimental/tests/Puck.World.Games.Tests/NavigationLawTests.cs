using Puck.Commands;
using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Assets.Documents;
using Puck.Physics.Fields;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>Pins authored surface, flight-volume, and live-medium navigation at the validation, deterministic
/// execution, cache invalidation, and checkpoint boundaries.</summary>
public sealed partial class NavigationLawTests {
    private static WorldNavigationDomain VolumeDomain(string name = DomainName, WorldNavigationKind kind = WorldNavigationKind.Volume, string? medium = null) => new(
        Name: name,
        Kind: kind,
        Origin: Vector3.Zero,
        CellSize: 1f,
        Width: 6,
        Depth: 3,
        Layers: 6,
        Connectivity: WorldNavigationConnectivity.Full,
        AgentRadius: 0.25f,
        ArrivalDistance: 0.2f,
        MaxExpandedNodes: 108,
        MaxPathNodes: 108,
        Medium: medium
    );
    private static WorldDefinition NavigationDocument(WorldNavigationDomain domain, bool withMedium = false) {
        var document = Fixtures.BuildDocumentAtRate(rateHz: Fixtures.RecordedTraceRateHz);
        var channels = document.Channels.ToList();

        channels.Add(item: new WorldChannel(
            Name: "up",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.MoveUp
        ));

        var navigationMotion = new BodyMotionProgram(
            Name: "navigation-motion",
            Version: BodyMotionProgram.CurrentVersion,
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputePlanarTargetVelocity,
                BodyMotionOp.ShapeVelocity,
                BodyMotionOp.RunActionTriggers,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        var producer = new BodyMotionProgram(
            Name: ProducerName,
            Version: BodyMotionProgram.CurrentVersion,
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.FaceSensorTarget, BodyMotionOp.ProduceSteeringIntent],
            Target: new BodyTargetSource.Navigated(
                Domain: domain.Name,
                Register: RegisterName
            )
        );
        var kit = document.Kits[0];

        return document with {
            ChannelsRaw = channels,
            TargetRegistersRaw = [new WorldTargetRegister(
                Name: RegisterName,
                MaximumRange: 50f,
                MaximumHalfAngleDegrees: 180f,
                RequiresLineOfSight: false
            )],
            BodyMotionProgramsRaw = [.. document.BodyMotionPrograms, navigationMotion, producer],
            KitRowsRaw = [kit with {
                BodyMotionProgram = navigationMotion.Name,
                // A full-thrust hold row is the "compatible vertical consumer" a Volume/Medium-domain producer
                // needs: it consumes MoveUp unconditionally.
                // This navigation program carries no gravity law, so its hold row holds nothing and only consumes
                // MoveUp: a Gravity row here would sink a navigator
                // out of its own volume domain between goals.
                Motion = kit.Motion with { Holds = [kit.Motion.Holds![0] with { Envelope = null, Gravity = null, Hold = BodyHoldKind.None, Thrust = 1f }] },
                ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: kit.Producers) {
                    [ProducerName] = NavigationParameters(),
                },
            }],
            NavigationRaw = new WorldNavigationSection(Domains: [domain]),
            StateRaw = (withMedium
            ? new WorldStateSection(
                World: [Fixtures.MediumRow(
                        heightScale: 8f,
                        name: "water",
                        topology: "water-space"
                    )],
                Lattices: [MediumTopology()]
            )
            : document.StateRaw),
        };
    }
    private static WorldBody JoinNavigator(WorldFixture fixture, FixedVector3 goal, int slot = 0) {
        var actor = Principal.Seat(slot: slot);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        var body = fixture.Server.Body(index: actor.Index)!;

        body.SetIntentSource(source: IntentSource.Producer(name: ProducerName));
        Assert.True(condition: fixture.Server.ApplyDesignation(
            designation: new WorldDesignation(
                EntityIndex: actor.Index,
                Register: RegisterName,
                Subject: default,
                Point: goal
            ),
            principal: actor
        ));
        return body;
    }
    // The island authors a dozen navigation domains, and sweeping one's occupancy and edges against the shipped
    // solid field is the expensive part of navigation (`puck bench world`'s construction row measures it). Server
    // construction bakes none of them; the first route request or read-back bakes the domain it reads. The control
    // routes a navigator through a small fixture domain and must find it baked, so the count is known to move.
    [Fact]
    public void TheIslandsNavigationDomainsConstructWithoutSweepingTheSolidFieldUpFront() {
        using (var island = Fixtures.FreshServer(definition: AuthoredGameFixtures.Nexus)) {
            Assert.True(
                condition: (island.Server.Population.NavigationDomainCount > 1),
                userMessage: $"the island compiled {island.Server.Population.NavigationDomainCount} navigation domain(s)"
            );
            Assert.Equal(
                actual: island.Server.Population.NavigationBakedDomainCount,
                expected: 0
            );
        }

        using var routed = Fixtures.FreshServer(definition: NavigationDocument(domain: VolumeDomain()));

        Assert.Equal(
            actual: routed.Server.Population.NavigationBakedDomainCount,
            expected: 0
        );
        _ = JoinNavigator(
            fixture: routed,
            goal: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 4),
                Y: FixedQ4816.FromInteger(value: 4),
                Z: FixedQ4816.Zero
            )
        );
        routed.Step();
        Assert.Equal(
            actual: routed.Server.Population.NavigationBakedDomainCount,
            expected: 1
        );
    }
}
