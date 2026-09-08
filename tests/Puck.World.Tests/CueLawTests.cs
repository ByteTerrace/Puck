using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <see cref="WorldEffect.ApplyRigidImpulse"/> — the cue gesture's one honest impulse path.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class CueLawTests {
    private const string BallKitName = "ball";
    private const string BallPlacementId = "cueBall";
    private const string MagnitudeRow = "cue";
    private const string StrikeChannel = "strike";

    // Body indices are deterministic: seats fill 0..LocalSeatCount-1 first, so the one inhabited placement this
    // fixture adds (with capacity bumped by exactly one, mirroring Fixtures.BuildCameraBodyDocument) lands here.
    private static readonly int BallIndex = WorldBodiesLimits.LocalSeatCount;

    private static WorldPrototype BuildBallCreation() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: 0.15f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "cue-ball", Palette: null, Shapes: [shape], Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "cue-ball");

        return new WorldPrototype(Id: "cue-ball", Document: canonical.Document, HashRaw: canonical.Hash);
    }

    // Fixtures.BuildDocument() plus one rigid "ball" kit (a second, distinct kit — the seat kit stays untouched),
    // one inhabited placement under it, a "strike" composition channel, and a keyed Fixed "cue" row seeding the
    // charge cell at zero. Every law below adds its own rule(s) on top.
    private static WorldDefinition BuildCueDocument(FixedQ4816 seedMagnitude) {
        var source = Fixtures.BuildDocument();
        var creation = BuildBallCreation();
        var ballKit = source.Kits[0] with {
            Name = BallKitName,
            BodyContact = WorldBodyContactMode.Solid,
            Collider = new WorldCollider.Sphere(Radius: 0.15f),
            Rigid = new WorldRigid(Mass: 1f, Restitution: 0f, Friction: 0f, RollingFriction: 0f, LinearDamping: 0f, AngularDamping: 0f),
        };
        var placement = new WorldPlacement(
            Id: BallPlacementId,
            PrototypeId: creation.Id,
            Position: new DocumentVector3(value: new Vector3(x: 0f, y: 0f, z: 3f)),
            YawDegrees: 0f,
            Scale: 1f,
            Inhabit: new WorldPlacementInhabit(Kit: BallKitName, Look: null, Source: IntentSource.Idle, Distribution: WorldDistribution.Default)
        );
        var magnitudeRow = new WorldStateRow(
            Name: CellName.Parse(candidate: MagnitudeRow),
            Kind: CellKind.Fixed,
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "0"), Value: seedMagnitude.Value)]
        );

        return source with {
            CreationsRaw = [.. source.Creations, creation],
            KitRowsRaw = [.. source.Kits, ballKit],
            PlacementRowsRaw = [.. source.Placements, placement],
            ChannelsRaw = [.. source.Channels, new WorldChannel(Name: StrikeChannel, Shape: ChannelShape.Binary, Composition: true)],
            StateRaw = ((source.StateRaw ?? new WorldStateSection()) with {
                World = [.. (source.StateRaw?.World ?? []), magnitudeRow],
            }),
            PopulationRaw = (source.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1) }),
        };
    }

    private static WorldFixture JoinSeatAndBall(WorldDefinition definition) {
        var fixture = Fixtures.FreshServer(definition: definition);
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        Assert.NotNull(@object: fixture.Server.Body(index: BallIndex));

        return fixture;
    }

    private static long MagnitudeCellRaw(WorldFixture fixture) {
        var row = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: MagnitudeRow)!;

        return StateRows.FindCell(cells: row.Cells, key: CellName.Parse(candidate: "0"))!.Value;
    }

    // The release effect always addresses the same two live-resolved bodies: the struck target (a literal body
    // index) and the heading source (a different literal body index) — proving the effect strikes a body other
    // than the one supplying the direction, the shape a cue strike needs.
    private static WorldRule ReleaseRule(int targetIndex) => new(
        Name: CellName.Parse(candidate: "cue-release"),
        Mode: ActionTriggerMode.Edge,
        Effects: [new WorldEffect.ApplyRigidImpulse(
            Key: $"body:{targetIndex}",
            HeadingKey: "body:0",
            MagnitudeState: MagnitudeRow,
            MagnitudeKey: "0"
        )]
    );

    [Fact]
    public void ReleaseFiresRigidImpulseAlongTheHeadingBodysFacingScaledByTheCellsMagnitude() {
        var magnitude = FixedQ4816.FromDouble(value: 2.0);
        var definition = BuildCueDocument(seedMagnitude: magnitude) with {
            Rules = [ReleaseRule(targetIndex: BallIndex)],
        };

        using var fixture = JoinSeatAndBall(definition: definition);
        var ball = fixture.Server.Body(index: BallIndex)!;

        Assert.Equal(expected: FixedVector3.Zero, actual: ball.RigidVelocity);

        fixture.Step();

        // The seat's default facing (identity orientation) is -Z, so a positive cell magnitude strikes the ball
        // along -Z — exactly Δv = impulse / mass with mass 1.
        Assert.Equal(expected: FixedQ4816.Zero, actual: ball.RigidVelocity.X);
        Assert.Equal(expected: FixedQ4816.Zero, actual: ball.RigidVelocity.Y);
        Assert.Equal(expected: -magnitude, actual: ball.RigidVelocity.Z);

        var before = ball.FixedPosition;

        fixture.Step();

        Assert.True(condition: (ball.FixedPosition.Z < before.Z), userMessage: $"the struck ball never moved along the seat's heading — before={before} after={ball.FixedPosition}");
    }

    [Fact]
    public void AStrikeOnABodyTheSeatDoesNotTargetRefusesAndLeavesEveryRigidBodyAtRest() {
        // The seat's own body (index 0) carries the base locomotion kit, never the rigid "ball" kit — the "does not
        // target" case: an attempted strike on a body that is not the targetable ball.
        var definition = BuildCueDocument(seedMagnitude: FixedQ4816.FromDouble(value: 2.0)) with {
            Rules = [ReleaseRule(targetIndex: 0)],
        };

        using var fixture = JoinSeatAndBall(definition: definition);
        var ball = fixture.Server.Body(index: BallIndex)!;

        fixture.Step();

        var diagnostic = Assert.Single(collection: fixture.Server.RuleRuntimeDiagnostics(), predicate: candidate => Equals(objA: candidate.Refusal, objB: WorldRuleEffectRefusal.RigidBodyRequired));

        Assert.Equal(expected: WorldRuleEffectRefusal.RigidBodyRequired, actual: diagnostic.Refusal);
        Assert.Equal(expected: FixedVector3.Zero, actual: ball.RigidVelocity);
    }

    [Fact]
    public void AHeldChordChargesTheCueCellMonotonicallyUpToItsAuthoredMax() {
        const double step = 0.05d;
        const double max = 2.0d;
        var definition = BuildCueDocument(seedMagnitude: FixedQ4816.Zero) with {
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "cue-charge"),
                Mode: ActionTriggerMode.Level,
                Gate: new ActionPredicate.CompareState(State: $"$channel:1:{StrikeChannel}", Comparison: ActionStateComparison.GreaterOrEqual, Value: 1m),
                Effects: [new ActionEffect.SetState(
                    State: MagnitudeRow,
                    Key: "0",
                    Expression: ValueExpression.Parse(text: $"min({MagnitudeRow}[0] + {step.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}, {max.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)})")
                )]
            )],
        };

        using var fixture = JoinSeatAndBall(definition: definition);
        var strikeOrdinal = definition.Channels.Count - 1;

        Assert.Equal(expected: StrikeChannel, actual: definition.Channels[strikeOrdinal].Name);

        fixture.Server.Body(index: 0)!.PressChannel(ordinal: strikeOrdinal, value: FixedQ4816.One, holdSeconds: 5f, authoredMaximum: FixedQ4816.FromDouble(value: 60d));

        var previous = 0L;

        // At 240 Hz, 60 ticks is 0.25 s — comfortably past ceil(max / step) = 40 charge ticks, so the cell must
        // have plateaued at max well before this loop ends.
        for (var tick = 0; (tick < 60); tick++) {
            fixture.Step();

            var current = MagnitudeCellRaw(fixture: fixture);

            Assert.True(condition: (current >= previous), userMessage: $"tick {tick}: charge fell from {previous} to {current} — a held chord must charge MONOTONICALLY");
            previous = current;
        }

        Assert.Equal(expected: FixedQ4816.FromDouble(value: max).Value, actual: previous);
    }
}
