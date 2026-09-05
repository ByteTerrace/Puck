using System.Numerics;

using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>$upright:&lt;bodyRef&gt;</c> reads a body's own local +Y rotated by its live orientation, dotted
/// against the world up its gravity opposes — <c>1</c> exactly upright, falling toward <c>0</c> as it tips onto its
/// side — and a body reference resolving to no live body reads the sentinel <c>1</c> rather than a computed value.
/// </summary>
public sealed class WorldUprightFactLawTests {
    private const string UprightRow = "upright";
    private const string AbsentRow = "absent";

    [Fact]
    public void AnUprightBodyReadsOneAndAKnockedOverBodyReadsNearZero_ControlAnAbsentBodyReadsTheSentinel() {
        using var fixture = Fixtures.FreshServer(definition: RigidBodyDocument());
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        body.Pose(x: 0f, y: 1f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        fixture.Step();

        Assert.Equal(expected: FixedQ4816.One.Value, actual: Cell(fixture: fixture, row: UprightRow));
        // Control: a body reference naming no live body (body 3, a declared but never-joined slot) reads the SAME
        // sentinel whether or not any real body has tipped over — proving the read is a constant fallback, not a
        // coincidental zero.
        Assert.Equal(expected: FixedQ4816.One.Value, actual: Cell(fixture: fixture, row: AbsentRow));

        // The discriminating write: tip the body a quarter turn about its forward axis, laying its up axis flat —
        // a rigid body's orientation, unlike a grounded walker's, is not re-aligned to the surface every tick.
        body.Pose(x: 0f, y: 1f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: (MathF.PI / 2f));
        fixture.Step();

        var tipped = ((float)Cell(fixture: fixture, row: UprightRow) / 65536f);
        Assert.InRange(actual: tipped, low: -0.01f, high: 0.01f);
        Assert.Equal(expected: FixedQ4816.One.Value, actual: Cell(fixture: fixture, row: AbsentRow));
    }

    [Fact]
    public void TheUprightChannelRefusesAMissingBodyReference_ControlAWellFormedOneValidates() {
        var malformed = RigidBodyDocument(bodyRefUnderTest: $"{WorldRuleFacts.UprightPrefix}");
        var control = RigidBodyDocument(bodyRefUnderTest: $"{WorldRuleFacts.UprightPrefix}body:0");

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: malformed, reason: out var reason));
        Assert.Contains(expectedSubstring: nameof(WorldRuleRefusal.SpatialChannelMalformed), actualString: reason, comparisonType: StringComparison.Ordinal);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }

    private static long Cell(WorldFixture fixture, string row) =>
        fixture.Server.Definition.State.Single(predicate: r => (r.Name.Value == row)).Cells!.Single().Value;

    // A flat floor plus a rigid-kit seat body — the same rigid shape TabletopBoardLawTests rides, trimmed to just
    // what an orientation read needs (no board, no tabletop rules): a body whose FixedOrientation persists exactly
    // as posed rather than being re-aligned to ground every tick the way a grounded walker's would.
    private static WorldDefinition RigidBodyDocument(string? bodyRefUnderTest = null) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(Id: 0, Name: "floor", Type: SdfSolidPrimitive.Box, Position: Vector3.Zero,
            Rotation: Quaternion.Identity, Scale: new Vector3(x: 24f, y: 0.1f, z: 24f), Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0);
        var document = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "rigid-floor", Palette: null, Shapes: [shape], Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "rigid-floor");
        var creation = new WorldPrototype(Id: "floor", Document: canonical.Document, HashRaw: canonical.Hash);
        var rigid = new WorldRigid(Mass: 1f, Restitution: 0.05f, Friction: 1f, RollingFriction: 2f, LinearDamping: 1f, AngularDamping: 1f);
        var uprightBody = bodyRefUnderTest ?? $"{WorldRuleFacts.UprightPrefix}body:0";

        return source with {
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
            CreationsRaw = [creation],
            GravityRaw = source.Gravity with { Uniform = new DocumentVector3(value: new Vector3(x: 0f, y: -9.8f, z: 0f)) },
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.Sphere(Radius: 0.15f),
                Rigid = rigid,
            })],
            PlacementRowsRaw = [new WorldPlacement(Id: "floor", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: WorldCellName.Parse(candidate: UprightRow), Kind: CellKind.Fixed,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)]),
                new WorldStateRow(Name: WorldCellName.Parse(candidate: AbsentRow), Kind: CellKind.Fixed,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)]),
            ]),
            Rules = [
                new WorldRule(Name: WorldCellName.Parse(candidate: "upright-mirror"),
                    Effects: [new ActionEffect.SetState(State: UprightRow, FromState: uprightBody)]),
                new WorldRule(Name: WorldCellName.Parse(candidate: "absent-mirror"),
                    Effects: [new ActionEffect.SetState(State: AbsentRow, FromState: $"{WorldRuleFacts.UprightPrefix}body:3")]),
            ],
        };
    }
}
