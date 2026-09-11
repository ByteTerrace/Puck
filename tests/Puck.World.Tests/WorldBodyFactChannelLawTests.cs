using System.Numerics;

using Xunit;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>$fact:&lt;bodyRef&gt;:&lt;fact&gt;</c> reads a live body's fact bit as 1/0 — a grounded walker reads
/// <c>Grounded</c> 1 and <c>Airborne</c> 0, and the same body posed into the air reads them swapped — while a body
/// reference resolving to no live body reads 0 for every fact, and a channel naming no <c>BodyFacts</c> member is
/// refused at compile.
/// </summary>
public sealed class WorldBodyFactChannelLawTests {
    private const string AirborneRow = "airborne";
    private const string GroundedRow = "grounded";
    private const string AbsentRow = "absent";

    [Fact]
    public void AGroundedBodyReadsGroundedAndAPosedBodyReadsAirborne_ControlAnAbsentBodyReadsZero() {
        using var fixture = Fixtures.FreshServer(definition: FactDocument());
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        body.Pose(x: 0f, y: -0.5f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        for (var settle = 0; (settle < 60); settle++) {
            fixture.Step(stepTicks: Puck.Hosting.EngineTicks.PerRate(ratePerSecond: 30));
        }

        Assert.True(condition: (Cell(fixture: fixture, row: GroundedRow) == 1L), userMessage: $"facts after settling: {body.Facts} at y={body.Position.Y}");
        Assert.Equal(expected: 0L, actual: Cell(fixture: fixture, row: AirborneRow));
        Assert.Equal(expected: 0L, actual: Cell(fixture: fixture, row: AbsentRow));

        body.Pose(x: 0f, y: 6f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        fixture.Step();

        Assert.Equal(expected: 1L, actual: Cell(fixture: fixture, row: AirborneRow));
        Assert.Equal(expected: 0L, actual: Cell(fixture: fixture, row: GroundedRow));
        Assert.Equal(expected: 0L, actual: Cell(fixture: fixture, row: AbsentRow));
    }

    [Fact]
    public void TheFactChannelRefusesANameThatIsNoBodyFact_ControlAWellFormedOneValidates() {
        var malformed = FactDocument(airborneChannel: $"{WorldRuleFacts.FactPrefix}body:0:Floating");
        var control = FactDocument();

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: malformed, reason: out var reason));
        Assert.Contains(expectedSubstring: nameof(WorldRuleRefusal.BodyFactMalformed), actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }

    private static long Cell(WorldFixture fixture, string row) =>
        fixture.Server.Definition.State.Single(predicate: r => (r.Name.Value == row)).Cells!.Single().Value;

    private static WorldStateRow IntRow(string name) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
    );

    // The platform floor a walker settles on (the same floor BodyScaleLawTests grounds its bodies on).
    private static WorldDefinition FactDocument(string? airborneChannel = null) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(Id: 0, Name: "platform", Type: SdfSolidPrimitive.Box, Position: new Vector3(x: 0f, y: -1f, z: 0f),
            Rotation: Quaternion.Identity, Scale: new Vector3(x: 10f, y: 0.5f, z: 10f), Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0);
        var document = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "platform", Palette: null, Shapes: [shape], Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "platform");
        var creation = new WorldPrototype(Id: "platform", Document: canonical.Document, HashRaw: canonical.Hash);

        return source with {
            Simulation = new WorldSimulationDefaults(RateHz: 30),
            GravityRaw = new WorldGravity(Attractors: [], GravitationalConstant: 0f, SofteningLength: 0.5f, Solver: WorldGravitySolver.Pairwise, Uniform: new Puck.Assets.Documents.DocumentVector3(x: 0f, y: -46f, z: 0f)),
            KitRowsRaw = source.Kits.Select(selector: kit => kit with {
                Motion = kit.Motion with {
                    Holds = [kit.Motion.Holds![0] with { Envelope = new WorldHoldEnvelope(SinkSpeed: 40f), Gravity = new WorldHoldGravity(Fall: 46f, Rise: 28f) }],
                },
            }).ToArray(),
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "platform", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
            StateRaw = new WorldStateSection(World: [IntRow(name: AirborneRow), IntRow(name: GroundedRow), IntRow(name: AbsentRow)]),
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "airborne-mirror"),
                    Effects: [new ActionEffect.SetState(State: AirborneRow, FromState: (airborneChannel ?? $"{WorldRuleFacts.FactPrefix}body:0:Airborne"))]),
                new WorldRule(Name: CellName.Parse(candidate: "grounded-mirror"),
                    Effects: [new ActionEffect.SetState(State: GroundedRow, FromState: $"{WorldRuleFacts.FactPrefix}body:0:Grounded")]),
                new WorldRule(Name: CellName.Parse(candidate: "absent-mirror"),
                    Effects: [new ActionEffect.SetState(State: AbsentRow, FromState: $"{WorldRuleFacts.FactPrefix}body:3:Grounded")]),
            ],
        };
    }
}
