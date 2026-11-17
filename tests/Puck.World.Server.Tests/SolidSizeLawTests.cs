using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Size is sampled once by authority; contact, carrier motion and snapshot agree on that sample.</summary>
public sealed class SolidSizeLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ReliefContactAndDrawnRootAgreeAtEverySampledTick(bool field) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var canonical = CreationCanonicalizer.Canonicalize(document: new CreationDocument(CreationDocument.CurrentSchema, "relief", null,
            [new ShapeDocument(0, "sphere", SdfSolidPrimitive.Sphere, Vector3.Zero, Quaternion.Identity,
                Vector3.One, 0, SdfBlendOp.Union, 0, 0)], null, Noise: new CreationNoiseDocument(0.1f, 1), SizeBy: "state.growth"), source: "relief");
        var creation = new WorldPrototype("relief", canonical.Document, canonical.Hash);
        var definition = source with {
            CollisionRaw = source.Collision with { Requirements = (field ? [WorldContactRequirement.SmoothUnionContact] : []) },
            StateRaw = new WorldStateSection(World: [StateFixtures.FixedSlot(name: "growth", rawBits: FixedQ4816.One.Value)]),
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement("relief", "relief", new Vector3(x: 20, y: 0, z: 0), 23, 1, Solid: new WorldSolid(Margin: 0))],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var step = 1; (step <= 8); step++) {
            var scale = (FixedQ4816.FromInteger(value: step) / FixedQ4816.FromInteger(value: 8));

            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "growth", WorldStateRow.SlotKey.Value, scale.Value, WorldDocumentWriteKind.Set));
            fixture.Step();
            var posed = Assert.Single(collection: fixture.Server.Population.DiagnosticAttached());
            var contact = Assert.Single(collection: posed.Colliders).Collider;
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));
            // The moving creation bakes once in its own units, then its drawn root matrix carries that bake.
            var transform = new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1, null);

            Assert.True(condition: Matrix4x4.Invert(matrix: ((Matrix4x4.CreateScale(scale: ((float)posed.Pose.Scale)) * Matrix4x4.CreateFromQuaternion(quaternion: posed.Pose.Rotation.ToQuaternion())) *
                Matrix4x4.CreateTranslation(position: posed.Pose.Origin.ToVector3())), result: out var inverse));
            builder.PushField(SdfBlendOp.Union);
            CreationStampEmitter.Emit(builder, creation.EngineDocument, transform, _ => material, inScope: true);
            CreationStampEmitter.EmitNoise(builder: builder, noise: creation.EngineDocument.Noise!, transform: transform);
            builder.PopField();
            var drawn = new SdfFieldEvaluator(program: builder.Build());

            for (var x = -3; (x <= 3); x++) {
                for (var y = -3; (y <= 3); y++) {
                    for (var z = -3; (z <= 3); z++) {
                        var point = (posed.Pose.Origin + FixedVector3.FromVector3(value: (new Vector3(x: x, y: y, z: z) * (((float)scale) / 3))));

                        Assert.True(condition: FixedFieldColliderContact.TryReadValue(collider: contact, distance: out var actual, material: out _, point: point));
                        var local = FixedVector3.FromVector3(value: Vector3.Transform(point.ToVector3(), inverse));

                        Assert.True(condition: drawn.TryDistance(FixedPosition.FromLocal(local: local), out var expected, out _));
                        Assert.True(condition: (Math.Abs(value: ((double)(actual - ((expected * scale) * drawn.Capabilities.StepScale)))) <= 0.002),
                            userMessage: $"scale={scale} point={point} actual={actual} expected={(expected * scale)} bound={contact.Field!.BoundScale} drawBound={drawn.Capabilities.StepScale}");
                    }
                }
            }
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void GrowthAndShrinkRideTheSampledSurface(bool field) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var creation = CreationCanonicalizer.Canonicalize(document: new CreationDocument(CreationDocument.CurrentSchema, "growing", null,
            [new ShapeDocument(0, "platform", SdfSolidPrimitive.Box, new Vector3(x: 0, y: 1, z: 0), Quaternion.Identity,
                new Vector3(x: 3, y: 1, z: 3), 0, SdfBlendOp.Union, 0, 0)], null, SizeBy: "state.growth"), source: "growing");
        var definition = source with {
            CollisionRaw = source.Collision with { Requirements = (field ? [WorldContactRequirement.SmoothUnionContact] : []) },
            StateRaw = new WorldStateSection(World: [StateFixtures.FixedSlot(name: "growth", rawBits: (FixedQ4816.One.Value / 2))]),
            CreationsRaw = [new WorldPrototype("growing", creation.Document, creation.Hash)],
            PlacementRowsRaw = [new WorldPlacement("growing", "growing", new Vector3(x: 20, y: 0, z: 0), 0, 1, Solid: new WorldSolid(Margin: 0))],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var body = fixture.JoinSeat(slot: 0);

        body.Pose(pitchRadians: 0, rollRadians: 0, x: 20, y: 1.04f, yawRadians: 0, z: 0);
        for (var i = 0; (i < 15); i++) { fixture.Step(); }
        Assert.True(condition: body.Grounded);
        foreach (var numerator in Enumerable.Range(count: 15, start: 17).Concat(second: Enumerable.Range(count: 28, start: 4).Reverse())) {
            var scale = (FixedQ4816.FromInteger(value: numerator) / FixedQ4816.FromInteger(value: 32));

            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "growth", WorldStateRow.SlotKey.Value,
                scale.Value, WorldDocumentWriteKind.Set));
            fixture.Step();
            Assert.True(condition: body.Grounded, userMessage: $"scale {scale} at {body.FixedPosition}");
            Assert.InRange(((double)body.FixedPosition.Y), ((2 * ((double)scale)) - 0.002), ((2 * ((double)scale)) + 0.06));
            var sampled = Assert.Single(collection: fixture.Server.Population.SolidScales());

            Assert.Equal(scale, sampled.Scale);
        }
    }
}
