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
    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, 0.2f)]
    [InlineData(true, 0.2f)]
    public void MovingSolidsAnswerGripAndFieldGameplayQueries(bool field, float margin) {
        var definition = GrowingDocument(field, carved: false);
        definition = definition with { CollisionRaw = definition.Collision with { DefaultHold = true },
            PlacementRowsRaw = [definition.Placements[0] with { Solid = new WorldSolid(margin) }] };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.Step();
        var from = FixedVector3.FromVector3(new Vector3(20, 3, 0));
        var to = FixedVector3.FromVector3(new Vector3(20, -1, 0));
        var contact = field ? (IContactField)fixture.Server.Population.SolidField! : WorldColliderSet.Build(fixture.Server.Definition);
        if (contact is WorldColliderSet analytic) { analytic.RefreshAttached(fixture.Server.Population, fixture.Server.Definition, 1, 1); }
        Assert.True(contact.TryHoldableSurfaceAlongDirection(from, -FixedVector3.UnitY, FixedQ4816.FromInteger(5), out var grip, out _));
        Assert.InRange((double)grip.Point.Y, 0.99 + margin / 2, 1.01 + margin / 2);
        Assert.True(contact.TryNearestSurfaceAlongDirection(from, -FixedVector3.UnitY, FixedQ4816.FromInteger(5), FixedQ4816.Zero, out _));
        if (field) {
            var solid = fixture.Server.Population.SolidField!;
            Assert.False(solid.LineOfSight(from, to));
            Assert.True(solid.Query.Raycast(FixedPosition.FromLocal(from), -FixedVector3.UnitY, FixedQ4816.FromInteger(5), out _));
            Assert.True(solid.Query.TryGroundHeight(FixedPosition.FromLocal(from), FixedQ4816.Zero, FixedQ4816.FromInteger(5), out var height));
            Assert.InRange((double)height, 0.99 + margin / 2, 1.01 + margin / 2);
            Assert.True(solid.Query.Overlap(FixedPosition.FromLocal(FixedVector3.FromVector3(new Vector3(20, 0.5f, 0))), FixedQ4816.Zero));
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContactRebuildKeepsTheLatestMovingSample(bool field) {
        using var fixture = Fixtures.FreshServer(GrowingDocument(field));
        fixture.Step();
        var before = fixture.Server.Population.Capture().AttachedSolids;
        fixture.Server.Population.Rebuild(fixture.Server.Definition, null);
        Assert.Equal(before, fixture.Server.Population.Capture().AttachedSolids);
    }
    [Fact]
    public void StaticResponseToSizedCreationRebuildsItsColliderClass() {
        var definition = GrowingDocument(false, carved: false);
        var full = CreationCanonicalizer.Canonicalize(definition.Creations[0].Document with { SizeBy = null }, "full");
        var before = definition with { CreationsRaw = [definition.Creations[0], new WorldPrototype("full", full.Document, full.Hash)],
            PlacementRowsRaw = [definition.Placements[0] with { PrototypeId = "full" }] };
        using var fixture = Fixtures.FreshServer(before);
        Assert.True(fixture.Server.Population.InstallResponseSolids(fixture.Server.Definition, definition, [0], null));
        Assert.Empty(fixture.Server.Population.DiagnosticColliders);
    }
    [Theory]
    [InlineData("state.growth.$body", false)]
    [InlineData("state.growth", true)]
    public void UnresolvableSolidSizeIsACompileError(string binding, bool keyed) {
        var definition = GrowingDocument(false, carved: false);
        var creation = CreationCanonicalizer.Canonicalize(definition.Creations[0].Document with { SizeBy = binding }, "bad");
        var row = StateFixtures.FixedSlot("growth", FixedQ4816.One.Value);
        definition = definition with { CreationsRaw = [new WorldPrototype("growing", creation.Document, creation.Hash)],
            StateRaw = new WorldStateSection(World: [keyed ? row with { Domain = StateDomain.Keys.Instance, Cells = [] } : row]) };
        Assert.False(WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason));
        Assert.Contains(keyed ? "needs a key" : "$body", reason);
    }
    [Fact]
    public void SampleDeliveryRechecksTheSourceCellsDisclosure() {
        var definition = GrowingDocument(false);
        definition = definition with { StateRaw = new WorldStateSection(World: [StateFixtures.FixedSlot("growth", FixedQ4816.One.Value)
            with { CellVisibility = true, Visibility = new StateVisibility(Readers: ["seat1"]) }]) };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.Step();
        WorldRecipientView View(int seat) => new(() => fixture.Server.Definition, () => fixture.Server.Arena, Principal.Seat(seat),
            WorldDisclosureTier.Replica, () => true, new WorldDisclosureImages(), fixture.Server.AuthorityIdentity, () => fixture.Server.DeliveryTime);
        var filter = new WorldSolidScaleDisclosure();
        Assert.Empty(filter.Filter(View(1), fixture.Server.Population.SolidScales()).ToArray());
        Assert.Single(filter.Filter(View(0), fixture.Server.Population.SolidScales()).ToArray());
        Assert.True(fixture.Server.Arena.Catalog.TryResolve(StateLane.Document, "growth", out var row));
        Assert.True(fixture.Server.Arena.Keys.TryResolve(StateRow.SlotKey, out var key));
        Assert.True(fixture.Server.Arena.TryWriteVisibility(row.Ordinal, key, new StateVisibility(Readers: ["seat2"])));
        Assert.Empty(filter.Filter(View(0), fixture.Server.Population.SolidScales()).ToArray());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScaledFieldBoundsRoundOutward(bool field) {
        var definition = GrowingDocument(field);
        var shapes = definition.Creations[0].Document.Shapes!.ToArray();
        shapes[0] = shapes[0] with { Scale = new Vector3(3 + 1f / 65536, 1, 3) };
        var creation = CreationCanonicalizer.Canonicalize(definition.Creations[0].Document with { Shapes = shapes }, "odd");
        definition = definition with { CreationsRaw = [new WorldPrototype("growing", creation.Document, creation.Hash)] };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "growth", StateRow.SlotKey.Value,
            FixedQ4816.One.Value, WorldDocumentWriteKind.Set));
        fixture.Step();
        var full = Assert.Single(Assert.Single(fixture.Server.Population.DiagnosticAttached()).Colliders).Collider;
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "growth", StateRow.SlotKey.Value,
            FixedQ4816.One.Value / 2, WorldDocumentWriteKind.Set));
        fixture.Step();
        var half = Assert.Single(Assert.Single(fixture.Server.Population.DiagnosticAttached()).Colliders).Collider;
        Assert.True(half.Extent.X >= (FixedInterval.FromPoint(full.Extent.X) * FixedInterval.FromPoint(FixedQ4816.FromDouble(0.5))).Upper);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ASizeStepCarriesSurfaceWithoutMultiplyingBodyStandoff(bool field, bool born) {
        var definition = GrowingDocument(field, carved: false);
        var initial = born ? FixedQ4816.Zero : FixedQ4816.FromDouble(0.01);
        var floor = CreationCanonicalizer.Canonicalize(definition.Creations[0].Document with { SizeBy = null }, "floor");
        definition = definition with {
            StateRaw = new WorldStateSection(World: [StateFixtures.FixedSlot("growth", initial.Value)]),
            // The floor supports the body before a zero-sized solid is born.
            CreationsRaw = [definition.Creations[0], new WorldPrototype("floor", floor.Document, floor.Hash)],
            PlacementRowsRaw = [definition.Placements[0], new WorldPlacement("floor", "floor", new Vector3(20, -2, 0), 0, 1, Solid: new WorldSolid(0))],
        };
        using var fixture = Fixtures.FreshServer(definition);
        var body = fixture.JoinSeat(0);
        body.Pose(20.02f, (float)(initial * FixedQ4816.FromInteger(2)) + 0.04f, 0, 0, 0, 0);
        for (var tick = 0; tick < 20; tick++) { fixture.Step(); }
        var before = body.FixedPosition;
        Assert.True(body.Grounded, $"before={before}");
        Assert.InRange((double)before.Y, -0.01, 0.1);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "growth", WorldStateRow.SlotKey.Value,
            FixedQ4816.One.Value, WorldDocumentWriteKind.Set));
        fixture.Step();
        Assert.InRange((double)body.FixedPosition.Y, 2, 2.1);
        Assert.InRange((double)(body.FixedPosition.X - before.X), -3, 3);
    }
    [Fact]
    public void StationaryYawedSupportHasNoDriftOver600Ticks() {
        var definition = GrowingDocument(field: false, carved: false);
        definition = definition with { PlacementRowsRaw = [definition.Placements[0] with { YawDegrees = 30 }],
            StateRaw = new WorldStateSection(World: [StateFixtures.FixedSlot(name: "growth", rawBits: FixedQ4816.One.Value)]) };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var body = fixture.JoinSeat(slot: 0);
        body.Pose(pitchRadians: 0, rollRadians: 0, x: 22.7f, y: 2.04f, yawRadians: 0, z: 0.25f);
        for (var tick = 0; tick < 30 && !body.Grounded; tick++) { fixture.Step(); }
        Assert.True(body.Grounded);
        var start = body.FixedPosition;
        for (var tick = 0; tick < 600; tick++) { fixture.Step(); }
        Assert.Equal(start.X, body.FixedPosition.X);
        Assert.Equal(start.Z, body.FixedPosition.Z);
    }
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void RestoredScaleContinuesFromTheAuthorityArena(bool field, bool warm) {
        var definition = GrowingDocument(field: field);
        using var fixture = Fixtures.FreshServer(definition: definition);
        _ = fixture.JoinSeat(slot: 0);
        if (warm) { fixture.Step(); }
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var start, out var reason), userMessage: reason);
        fixture.Step();
        var expectedScale = Assert.Single(collection: fixture.Server.Population.SolidScales());
        var expectedComponents = WorldStateHashComposition.AuthoritativeComponents(fixture.Server, fixture.Server.NextInputTick - 1);

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "growth", WorldStateRow.SlotKey.Value,
            FixedQ4816.One.Value, WorldDocumentWriteKind.Set));
        fixture.Step();
        Assert.Equal(FixedQ4816.One, Assert.Single(collection: fixture.Server.Population.SolidScales()).Scale);
        fixture.Server.Persistence.RestoreReplayStart(checkpoint: start!);
        fixture.Step();

        Assert.Equal(expectedScale, Assert.Single(collection: fixture.Server.Population.SolidScales()));
        Assert.Empty(collection: WorldStateHashComposition.DifferingComponents(expectedComponents,
            WorldStateHashComposition.AuthoritativeComponents(fixture.Server, fixture.Server.NextInputTick - 1)));
    }
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
        var definition = GrowingDocument(field: field);
        using var fixture = Fixtures.FreshServer(definition: definition);
        var body = fixture.JoinSeat(slot: 0);

        body.Pose(pitchRadians: 0, rollRadians: 0, x: 20, y: 1.04f, yawRadians: 0, z: 0);
        for (var i = 0; (i < 15); i++) { fixture.Step(); }
        Assert.True(condition: body.Grounded);
        foreach (var numerator in Enumerable.Range(count: 16, start: 17).Concat(second: Enumerable.Range(count: 29, start: 4).Reverse())) {
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
    private static WorldDefinition GrowingDocument(bool field, bool carved = true) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var creation = CreationCanonicalizer.Canonicalize(document: new CreationDocument(CreationDocument.CurrentSchema, "growing", null,
            [new ShapeDocument(0, "platform", SdfSolidPrimitive.Box, new Vector3(x: 0, y: 1, z: 0), Quaternion.Identity,
                new Vector3(x: 3, y: 1, z: 3), 0, SdfBlendOp.Union, 0, 0),
             new ShapeDocument(1, "carve", SdfSolidPrimitive.Box, new Vector3(2, 1, 2), Quaternion.Identity,
                new Vector3(0.25f, 2, 0.25f), 0, SdfBlendOp.Subtraction, 0, 0)], null, SizeBy: "state.growth"), source: "growing");
        if (!carved) { creation = CreationCanonicalizer.Canonicalize(creation.Document with { Shapes = [creation.Document.Shapes![0]] }, "growing"); }
        return source with {
            CollisionRaw = source.Collision with { Requirements = (field ? [WorldContactRequirement.SmoothUnionContact] : []) },
            StateRaw = new WorldStateSection(World: [StateFixtures.FixedSlot(name: "growth", rawBits: (FixedQ4816.One.Value / 2))]),
            CreationsRaw = [new WorldPrototype("growing", creation.Document, creation.Hash)],
            PlacementRowsRaw = [new WorldPlacement("growing", "growing", new Vector3(x: 20, y: 0, z: 0), 0, 1, Solid: new WorldSolid(Margin: 0))],
        };
    }
}


