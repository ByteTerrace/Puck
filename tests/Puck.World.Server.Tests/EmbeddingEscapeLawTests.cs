using System.Numerics;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A kit swap that hands a body a different collider steps it out of the static geometry the new volume is
/// embedded in (<c>WorldBody.EscapeEmbedding</c>): up its axis or along world X or Z, never down, to the nearest free
/// pose within eight metres, where the ordinary solve's shallowest push from deep inside a box goes down through the
/// floor. The swap is the debug world's noclip: a volumeless kit, selected by a body register, handing the body back the
/// seat kit while it stands inside a cube.</summary>
public sealed class EmbeddingEscapeLawTests {
    private const string Form = "form";
    private const string GhostKit = "ghost";
    private const string TallKit = "tall";

    private static ShapeDocument Box(int id, string name, Vector3 position, Vector3 halfExtents) => new(
        Id: id,
        Name: name,
        Type: SdfSolidPrimitive.Box,
        Position: position,
        Rotation: Quaternion.Identity,
        Scale: halfExtents,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );
    private static WorldCollider.Capsule Capsule(float height) => new(
        Endpoint: new Vector3(x: 0f, y: height, z: 0f),
        Radius: 0.35f
    );
    // Ground under x and z of -80 to 80, a 4 m cube spanning world x 58 to 62 and z -2 to 2, and a 40 m block spanning
    // world x 40 to 80 and z 40 to 80; the author frame reads through the half-turn (-x, y, -z). The `form` register
    // selects the kit (1 the volumeless ghost, 2 a taller capsule, any other value the seat kit), is born at 0, and is
    // written `start` on the first seated tick when one is given and `target` on the tick body 0 first stands past x 50.
    private static WorldDefinition Document(decimal? start, decimal target) {
        var document = Fixtures.BuildDocument();
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "room",
                Palette: null,
                Shapes: [
                    Box(halfExtents: new Vector3(x: 80f, y: 0.5f, z: 80f), id: 0, name: "ground", position: new Vector3(x: 0f, y: -0.5f, z: 0f)),
                    Box(halfExtents: new Vector3(x: 2f, y: 2f, z: 2f), id: 1, name: "cube", position: new Vector3(x: -60f, y: 2f, z: 0f)),
                    Box(halfExtents: new Vector3(x: 20f, y: 20f, z: 20f), id: 2, name: "block", position: new Vector3(x: -60f, y: 20f, z: -60f)),
                ],
                Frames: null
            ),
            source: "room"
        );
        var seat = (document.Kits[0] with { Collider = Capsule(height: 1f) });

        return document with {
            CreationsRaw = [new WorldPrototype(
                Id: "room",
                Document: canonical.Document,
                HashRaw: canonical.Hash
            )],
            KitsRaw = new WorldKitsSection(
                Assignment: (document.Assignment with {
                    Select = new WorldRowSelector(
                        BodyState: Form,
                        Cases: [
                            new WorldRowSelectorCase(Value: 1L, Row: GhostKit),
                            new WorldRowSelectorCase(Value: 2L, Row: TallKit),
                        ]
                    ),
                }),
                Rows: [
                    seat,
                    (seat with { Name = GhostKit, Collider = null }),
                    (seat with { Name = TallKit, Collider = Capsule(height: 1.5f) }),
                ]
            ),
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = [
                    new WorldPlacement(
                        Id: "room",
                        PrototypeId: "room",
                        Position: Vector3.Zero,
                        YawDegrees: 0f,
                        Scale: 1f,
                        Solid: new WorldSolid(Margin: 0f)
                    ),
                ],
            }),
            Rules = [.. Start(start: start), new WorldRule(
                Name: CellName.Parse(candidate: "swap"),
                Effects: [new WorldEffect.SetBodyState(Key: "0", State: Form, Value: target)],
                Gate: new ActionPredicate.CompareState(
                    State: $"{WorldRuleFacts.BodyPositionPrefix}body:0:x",
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Value: 50m
                ),
                Mode: ActionTriggerMode.Edge
            )],
            StateRaw = ((document.StateRaw ?? new WorldStateSection()) with {
                Body = [new ActionStateSlot(
                    Name: Form,
                    Kind: ActionStateKind.Counter,
                    Initial: 0f
                )],
            }),
        };
    }
    private static WorldRule[] Start(decimal? start) => ((start is { } value)
        ? [new WorldRule(
            Name: CellName.Parse(candidate: "start"),
            Effects: [new WorldEffect.SetBodyState(Key: "0", State: Form, Value: value)],
            Gate: new ActionPredicate.CompareState(
                State: WorldRuleFacts.Population,
                Comparison: ExpressionOp.GreaterOrEqual,
                Value: 1m
            ),
            Mode: ActionTriggerMode.Edge
        )]
        : []);
    // Seats body 0 in its starting kit, stands it at a pose and steps once: the body advances, the rule writes the
    // register and the kit selection swaps it at the end of that tick; then it runs `settle` more ticks.
    private static FixedVector3 SwapAt(decimal? start, decimal target, float x, float y, float z, int settle = 0) {
        using var fixture = Fixtures.FreshServer(definition: Document(start: start, target: target));
        var body = fixture.JoinSeat();

        fixture.Step();
        body.Pose(pitchRadians: 0f, rollRadians: 0f, x: x, y: y, yawRadians: 0f, z: z);

        for (var tick = 0; (tick <= settle); tick++) {
            fixture.Step();
        }

        return body.FixedPosition;
    }

    [Fact]
    public void ACollidingKitTakenInsideACubeStepsTheBodyOutOfItsNearestSideNeverDown() {
        var position = SwapAt(start: 1m, target: 0m, x: 60f, y: 0.05f, z: 0f, settle: 30);

        Assert.True(
            condition: ((position.X > FixedQ4816.FromDouble(value: 62.3)) && (position.Y > FixedQ4816.Zero)),
            userMessage: $"the body should stand beside the cube's +X face, not under it; it is at {position}"
        );
    }
    [Fact]
    public void ABodyNowhereFreeWithinTheSearchKeepsItsPose() {
        var position = SwapAt(start: 1m, target: 0m, x: 60f, y: 10f, z: 60f);

        Assert.True(
            condition: ((position.X == FixedQ4816.FromInteger(value: 60L)) && (position.Z == FixedQ4816.FromInteger(value: 60L))),
            userMessage: $"the body has no free pose within eight metres of the block's middle; it is at {position}"
        );
    }
    [Fact]
    public void ACollidingKitTakenInTheOpenMovesNothing() {
        var position = SwapAt(start: null, target: 2m, x: 60f, y: 0f, z: -20f);

        Assert.True(
            condition: ((position.X == FixedQ4816.FromInteger(value: 60L)) && (position.Z == FixedQ4816.FromInteger(value: -20L))),
            userMessage: $"the body stands in the open; it is at {position}"
        );
    }
}
