using System.Numerics;
using Xunit;

using Puck.Commands;
using Puck.Maths;
using Puck.Physics;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: a body's <see cref="WorldBody.BroadphaseRadius"/> is derived only when its scale or
/// collider is written, and always equals <see cref="FixedDynamicBodyContacts.BroadphaseRadius"/> recomputed from the
/// body's current <see cref="WorldBody.ScaledColliderVolumes"/>: after a scale write, a collider swap, and a restore from
/// a checkpoint. A tick that writes neither derives nothing, so the broadphase takes no root on a steady tick.</summary>
public sealed class BodyBroadphaseRadiusLawTests {
    private const int SteadyTicks = 60;

    private static readonly FixedQ4816 Half = FixedQ4816.FromDouble(value: 0.5);

    // The base fixture with the shipped seat collider (a capsule up to (0,1,0), radius 0.35) and a bodies.scaleRow
    // whose cell starts at one.
    private static WorldDefinition Document() {
        var source = Fixtures.BuildDocument();

        return source with {
            KitRowsRaw = [.. source.Kits.Select(selector: static kit => kit with {
                Collider = new WorldCollider.Capsule(
                    Endpoint: new Vector3(
                        x: 0f,
                        y: 1f,
                        z: 0f
                    ),
                    Radius: 0.35f
                ),
            })],
            PopulationRaw = (source.Population with { ScaleRow = "scale" }),
            StateRaw = ((source.StateRaw ?? new WorldStateSection()) with {
                World = [
                .. (source.StateRaw?.World ?? []),
                new WorldStateRow(
                    CellName.Parse(candidate: "scale"),
                    CellKind.Fixed,
                    Capacity: 4,
                    Cells: [new StateCell(
                            CellName.Parse(candidate: "0"),
                            CellValue.Fixed(rawBits: FixedQ4816.One.Value)
                        )],
                    Max: FixedQ4816.One.Value,
                    Min: FixedQ4816.FromDouble(value: 0.05).Value
                ),
            ],
            }),
        };
    }
    // Every live body's cached radius equals the radius recomputed from its current volumes, bit for bit.
    private static void AssertEveryRadiusIsCurrent(WorldServer server) {
        var bodies = 0;

        for (var index = 0; (index < server.Definition.Population.Capacity); index++) {
            if (server.Body(index: index) is not { } body) {
                continue;
            }

            bodies++;
            Assert.Equal(
                actual: body.BroadphaseRadius,
                expected: FixedDynamicBodyContacts.BroadphaseRadius(volumes: body.ScaledColliderVolumes())
            );
        }

        Assert.True(condition: (bodies > 0));
    }
    private static WorldMutation WriteScale(FixedQ4816 value) => new WorldMutation.UpsertStateCell(
        Key: "0",
        Kind: WorldDocumentWriteKind.Set,
        Principal: Principal.Console,
        Row: "scale",
        Value: value.Value
    );

    [Fact]
    public void AScaleWriteRederivesTheRadius() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = fixture.JoinSeat();

        fixture.Step();

        var before = body.BroadphaseRadius;

        Assert.True(condition: (before > FixedQ4816.Zero));
        fixture.Server.EnqueueMutation(mutation: WriteScale(value: Half));
        fixture.Step();

        Assert.Equal(
            actual: body.Scale,
            expected: Half
        );
        Assert.NotEqual(
            actual: body.BroadphaseRadius,
            expected: before
        );
        AssertEveryRadiusIsCurrent(server: fixture.Server);
    }
    [Fact]
    public void ACollidersSwapRederivesTheRadius() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = fixture.JoinSeat();

        fixture.Step();

        var before = body.BroadphaseRadius;
        var kit = fixture.Server.Definition.Kits[0];

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertKit(
            Kit: kit with { Collider = new WorldCollider.Sphere(Radius: 0.9f) },
            Principal: Principal.Console
        ));
        fixture.Step();

        Assert.NotEqual(
            actual: body.BroadphaseRadius,
            expected: before
        );
        AssertEveryRadiusIsCurrent(server: fixture.Server);
    }
    [Fact]
    public void ARestoredCheckpointCarriesTheSameRadius() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        fixture.JoinSeat();
        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: WriteScale(value: Half));
        fixture.Step();

        var captured = fixture.Server.Body(index: 0)!.BroadphaseRadius;

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var refusal
            ),
            userMessage: refusal
        );

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: restoredDefinition.Screens
        );
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-broadphase-radius-tests-");

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory.RootPath, machineId: Guid.NewGuid(), template: restoredDefinition)
        );
        var restored = restoredServer.Body(index: 0)!;

        Assert.Equal(
            actual: restored.Scale,
            expected: Half
        );
        Assert.Equal(
            actual: restored.BroadphaseRadius,
            expected: captured
        );
        AssertEveryRadiusIsCurrent(server: restoredServer);
    }
    [Fact]
    public void SteadyTicksDeriveNoRadiusAndAWriteDerivesOne() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = fixture.JoinSeat();

        fixture.Step();

        var settled = body.BroadphaseRadiusComputations;

        for (var tick = 0; (tick < SteadyTicks); tick++) {
            fixture.Step();
        }

        Assert.Equal(
            actual: body.BroadphaseRadiusComputations,
            expected: settled
        );
        // The control: the counter moves when an input does, so the steady count above can fail.
        fixture.Server.EnqueueMutation(mutation: WriteScale(value: Half));
        fixture.Step();

        Assert.Equal(
            actual: body.BroadphaseRadiusComputations,
            expected: (settled + 1)
        );
    }
}
