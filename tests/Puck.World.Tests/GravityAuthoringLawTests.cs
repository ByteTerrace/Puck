using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

using Puck.Assets.Documents;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the document-to-fixed point/planet gravity authoring seam.</summary>
public sealed class GravityAuthoringLawTests {
    private sealed class TemporaryStateDirectory : IDisposable {
        public string Path { get; } = Directory.CreateTempSubdirectory(prefix: "puck-gravity-checkpoint-").FullName;

        public void Dispose() {
            if (Directory.Exists(path: Path)) {
                Directory.Delete(
                    path: Path,
                    recursive: true
                );
            }
        }
    }

    private static WorldDefinition WithGravity(WorldGravity gravity) => Fixtures.BuildGradientUpDocument(gradientUp: false) with {
        GravityRaw = gravity,
    };
    private static WorldGravity PointGravity(
        float gravitationalConstant = 45f,
        IReadOnlyList<WorldGravityAttractor>? attractors = null,
        IReadOnlyList<WorldGravityArea>? areas = null,
        IReadOnlyList<WorldGravityPoint>? points = null,
        DocumentVector3? uniform = null
    ) => new(
        Areas: areas,
        Attractors: (attractors ?? []),
        GravitationalConstant: gravitationalConstant,
        Points: points,
        SofteningLength: 0.5f,
        Solver: WorldGravitySolver.Pairwise,
        Uniform: uniform
    );
    private static WorldDefinition AttachedAreaDefinition(bool zeroAcceleration = false) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var placement = Assert.Single(collection: source.Placements) with {
            Scale = 2f,
            Attach = new WorldPlacementAttach(
                BodyIndex: 0,
                LocalOffset: new Vector3(x: 0f, y: 2f, z: 0f),
                LocalYawDegrees: 90f
            ),
            Solid = null,
        };

        return source with {
            PlacementsRaw = source.PlacementsRaw! with { Rows = [placement] },
            PopulationRaw = source.Population with { ReconnectGraceSeconds = 0f },
            GravityRaw = PointGravity(
                gravitationalConstant: 0f,
                areas: [new WorldGravityArea(
                    PlacementId: "ball",
                    Priority: 0,
                    Mode: WorldGravityAreaMode.Replace,
                    Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 1f),
                    Acceleration: new WorldGravityAreaAcceleration.Directional(Value: (zeroAcceleration
                        ? Vector3.Zero
                        : new Vector3(x: 4f, y: 0f, z: 0f)
                    ))
                )]
            ),
        };
    }

    [Fact]
    public void PointPreset_LowersThroughTheSoftenedKernelToItsSurfacePromise() {
        var definition = WithGravity(gravity: PointGravity(points: [
            new WorldGravityPoint(PlacementId: "ball", ReferenceRadius: 100f, SurfaceGravity: 9.81f),
        ]));

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason),
            userMessage: reason
        );

        var compiled = FixedWorldGravity.Compile(
            gravity: definition.Gravity,
            placements: definition.Placements
        );
        var field = new WorldGravityField(
            capacity: 1,
            compiled: compiled
        );

        field.Solve(targets: [new WorldGravityTarget(
            EntityIndex: 0,
            Mass: FixedQ4816.Zero,
            Position: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 100),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            )
        )]);

        Assert.True(condition: field.TryAcceleration(acceleration: out var acceleration, entityIndex: 0));
        Assert.InRange(
            actual: -((double)acceleration.X),
            low: 9.80,
            high: 9.82
        );
        Assert.Equal(expected: FixedQ4816.Zero, actual: acceleration.Y);
        Assert.Equal(expected: FixedQ4816.Zero, actual: acceleration.Z);
    }
    [Fact]
    public void PointPreset_RequiresPositiveG_WhileUniformOnlyDoesNot() {
        var point = WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            points: [new WorldGravityPoint(PlacementId: "ball", ReferenceRadius: 100f, SurfaceGravity: 9.81f)]
        ));
        var uniform = WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            uniform: new DocumentVector3(value: new Vector3(x: 0f, y: -9.81f, z: 0f))
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: point, reason: out var pointReason));
        Assert.Contains(
            actualString: pointReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gravity.gravitationalConstant must be positive when gravity.points declares a source"
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: uniform, reason: out var uniformReason),
            userMessage: uniformReason
        );

        var compiled = FixedWorldGravity.Compile(
            gravity: uniform.Gravity,
            placements: uniform.Placements
        );

        Assert.Empty(collection: compiled.Attractors);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: -9.81), actual: compiled.Uniform.Y);
    }
    [Fact]
    public void APlacementMayNotBeCountedByBothSourceSpellings() {
        var denied = WithGravity(gravity: PointGravity(
            attractors: [new WorldGravityAttractor(Mass: 10f, PlacementId: "ball")],
            points: [new WorldGravityPoint(PlacementId: "ball", ReferenceRadius: 100f, SurfaceGravity: 9.81f)]
        ));
        var control = WithGravity(gravity: PointGravity(
            points: [new WorldGravityPoint(PlacementId: "ball", ReferenceRadius: 100f, SurfaceGravity: 9.81f)]
        ));

        Laws.RefusalWithControl(
            lawId: "gravity.point.duplicate-placement",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out _),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out _)
        );
        _ = WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason);
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gravity.points[0].placementId duplicates gravity source 'ball'"
        );
    }
    [Fact]
    public void PointPreset_UnrepresentableLoweringRefusesBeforeRuntimeCompilation() {
        var denied = WithGravity(gravity: PointGravity(points: [
            new WorldGravityPoint(PlacementId: "ball", ReferenceRadius: float.MaxValue, SurfaceGravity: float.MaxValue),
        ]));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gravity.points[0] cannot lower"
        );
    }
    [Fact]
    public void LegacyMassSources_CompileUnchangedWhenPointsAreAbsent() {
        var definition = WithGravity(gravity: PointGravity(
            attractors: [new WorldGravityAttractor(Mass: 10f, PlacementId: "ball")]
        ));

        var compiled = FixedWorldGravity.Compile(
            gravity: definition.Gravity,
            placements: definition.Placements
        );

        var source = Assert.Single(collection: compiled.Attractors);

        Assert.Equal(expected: FixedQ4816.FromInteger(value: 10), actual: source.Mass);
        Assert.Equal(expected: FixedVector3.Zero, actual: source.Position);
    }
    [Fact]
    public void PointPreset_RoundTripsAsAuthoredQuantities() {
        var definition = WithGravity(gravity: PointGravity(points: [
            new WorldGravityPoint(PlacementId: "ball", ReferenceRadius: 100f, SurfaceGravity: 9.81f),
        ]));

        var roundTrip = WorldDefinitionSerialization.Deserialize(
            utf8Json: WorldDefinitionSerialization.Serialize(definition: definition)
        );
        var point = Assert.Single(collection: roundTrip.Gravity.Points!);

        Assert.Equal(expected: "ball", actual: point.PlacementId);
        Assert.Equal(expected: 9.81f, actual: point.SurfaceGravity);
        Assert.Equal(expected: 100f, actual: point.ReferenceRadius);
    }
    [Fact]
    public void NullPointRow_RefusesByIndexedPath() {
        var definition = WithGravity(gravity: PointGravity(points: [
            new WorldGravityPoint(PlacementId: "ball", ReferenceRadius: 100f, SurfaceGravity: 9.81f),
        ]));
        var node = JsonNode.Parse(
            json: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition))
        )!.AsObject();

        node["gravity"]!["points"]!.AsArray()[0] = null;

        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(
            utf8Json: Encoding.UTF8.GetBytes(s: node.ToJsonString())
        ));

        Assert.Contains(
            expectedSubstring: "gravity.points[0] is required",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void LocalAreas_FoldGlobalThenAscendingPriorityAndAuthoredTieOrder() {
        var definition = WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            uniform: new DocumentVector3(value: new Vector3(x: 0f, y: -10f, z: 0f)),
            areas: [
                new WorldGravityArea(
                    PlacementId: "ball",
                    Priority: 0,
                    Mode: WorldGravityAreaMode.Combine,
                    Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 20f),
                    Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: 2f, y: 0f, z: 0f))
                ),
                new WorldGravityArea(
                    PlacementId: "ball",
                    Priority: 10,
                    Mode: WorldGravityAreaMode.Replace,
                    Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 20f),
                    Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: 0f, y: 1f, z: 0f))
                ),
                new WorldGravityArea(
                    PlacementId: "ball",
                    Priority: 10,
                    Mode: WorldGravityAreaMode.Combine,
                    Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 20f),
                    Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: 0f, y: 0f, z: 3f))
                ),
            ]
        ));
        var field = CompileField(capacity: 1, definition: definition);

        field.Solve(targets: [Target(entityIndex: 0, x: 0, y: 0, z: 0)]);

        Assert.True(condition: field.TryAcceleration(acceleration: out var acceleration, entityIndex: 0));
        Assert.Equal(expected: FixedQ4816.Zero, actual: acceleration.X);
        Assert.Equal(expected: FixedQ4816.One, actual: acceleration.Y);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3), actual: acceleration.Z);
        Assert.Equal(expected: [0, 1, 2], actual: field.Compiled.Areas.Select(selector: area => area.AuthoredIndex));
    }
    [Fact]
    public void LocalArea_BoundaryIsInclusive_AndAreaOnlyOutsideBodyDoesNotParticipate() {
        var definition = WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            areas: [new WorldGravityArea(
                PlacementId: "ball",
                Priority: 0,
                Mode: WorldGravityAreaMode.Combine,
                Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 5f),
                Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: 0f, y: -2f, z: 0f))
            )]
        ));
        var field = CompileField(capacity: 2, definition: definition);
        var justOutside = FixedQ4816.FromRawBits(value: (FixedQ4816.FromInteger(value: 5).Value + 1L));

        field.Solve(targets: [
            Target(entityIndex: 0, x: 5, y: 0, z: 0),
            new WorldGravityTarget(EntityIndex: 1, Position: new FixedVector3(X: justOutside, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), Mass: FixedQ4816.Zero),
        ]);

        Assert.True(condition: field.TryAcceleration(acceleration: out var boundary, entityIndex: 0));
        Assert.Equal(expected: FixedQ4816.FromInteger(value: -2), actual: boundary.Y);
        Assert.False(condition: field.TryAcceleration(acceleration: out _, entityIndex: 1));
        Assert.Equal(expected: 2, actual: field.AreaStatistics.TargetCount);
        Assert.Equal(expected: 2, actual: field.AreaStatistics.EvaluationCount);
        Assert.Equal(expected: 1, actual: field.AreaStatistics.MatchCount);
    }
    [Fact]
    public void BoxArea_EvaluatesMembershipInThePlacementsYawLocalFrame() {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var placement = Assert.Single(collection: source.Placements) with {
            Scale = 1f,
            YawDegrees = 90f,
        };
        var definition = source with {
            PlacementsRaw = source.PlacementsRaw! with { Rows = [placement] },
            GravityRaw = PointGravity(
                gravitationalConstant: 0f,
                areas: [new WorldGravityArea(
                    PlacementId: "ball",
                    Priority: 0,
                    Mode: WorldGravityAreaMode.Replace,
                    Bounds: new WorldGravityAreaBounds.BoxBounds(HalfExtents: new Vector3(x: 1f, y: 1f, z: 3f)),
                    Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: 0f, y: -4f, z: 0f))
                )]
            ),
        };
        var field = CompileField(capacity: 2, definition: definition);

        field.Solve(targets: [
            Target(entityIndex: 0, x: 2, y: 0, z: 0),
            Target(entityIndex: 1, x: 0, y: 0, z: 2),
        ]);

        Assert.True(condition: field.TryAcceleration(acceleration: out _, entityIndex: 0));
        Assert.False(condition: field.TryAcceleration(acceleration: out _, entityIndex: 1));
    }
    [Fact]
    public void ZeroReplace_CancellationAndRadialCentreRemainParticipatingZeroAnswers() {
        static WorldGravityArea Area(WorldGravityAreaMode mode, WorldGravityAreaAcceleration acceleration) => new(
            PlacementId: "ball",
            Priority: 0,
            Mode: mode,
            Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 10f),
            Acceleration: acceleration
        );

        var zeroReplace = CompileField(definition: WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            areas: [Area(mode: WorldGravityAreaMode.Replace, acceleration: new WorldGravityAreaAcceleration.Directional(Value: Vector3.Zero))]
        )), capacity: 1);
        var cancellation = CompileField(definition: WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            uniform: new DocumentVector3(value: new Vector3(x: 0f, y: -4f, z: 0f)),
            areas: [Area(mode: WorldGravityAreaMode.Combine, acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: 0f, y: 4f, z: 0f)))]
        )), capacity: 1);
        var radialCentre = CompileField(definition: WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            areas: [Area(mode: WorldGravityAreaMode.Replace, acceleration: new WorldGravityAreaAcceleration.Radial(Magnitude: 8f))]
        )), capacity: 1);

        foreach (var field in new[] { zeroReplace, cancellation, radialCentre }) {
            Assert.True(condition: field.IsActive);
            field.Solve(targets: [Target(entityIndex: 0, x: 0, y: 0, z: 0)]);

            Assert.True(condition: field.TryAcceleration(acceleration: out var acceleration, entityIndex: 0));
            Assert.Equal(expected: FixedVector3.Zero, actual: acceleration);
        }
    }
    [Fact]
    public void ZeroReplaceSuppressesKitGravity_WhileAnOutsideAreaBodyKeepsFallbackGravity() {
        static WorldDefinition Definition(float radius) => WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            areas: [new WorldGravityArea(
                PlacementId: "ball",
                Priority: 0,
                Mode: WorldGravityAreaMode.Replace,
                Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: radius),
                Acceleration: new WorldGravityAreaAcceleration.Directional(Value: Vector3.Zero)
            )]
        ));
        using var inside = Fixtures.FreshServer(definition: Definition(radius: 1000f));
        using var outside = Fixtures.FreshServer(definition: Definition(radius: 1f));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: inside.Server.ApplySession(request: new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        Assert.True(condition: outside.Server.ApplySession(request: new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        inside.Server.Population.EntryBody(index: 0)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: 100f, yawRadians: 0f, z: 0f);
        outside.Server.Population.EntryBody(index: 0)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: 100f, yawRadians: 0f, z: 0f);

        for (var tick = 0; (tick < 12); tick++) {
            inside.Step();
            outside.Step();
        }

        Assert.Equal(expected: FixedQ4816.FromInteger(value: 100), actual: inside.Server.Population.EntryBody(index: 0)!.FixedPosition.Y);
        Assert.True(condition: (outside.Server.Population.EntryBody(index: 0)!.FixedPosition.Y < FixedQ4816.FromInteger(value: 100)));

        inside.Server.Population.EntryBody(index: 0)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: 2000f, y: 100f, yawRadians: 0f, z: 0f);
        for (var tick = 0; (tick < 12); tick++) {
            inside.Step();
        }

        Assert.True(condition: (inside.Server.Population.EntryBody(index: 0)!.FixedPosition.Y < FixedQ4816.FromInteger(value: 100)));
    }
    [Fact]
    public void LocalArea_RidesTheExistingAuthoritativePlacementAttachmentPose() {
        var definition = AttachedAreaDefinition();
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = WorldPrincipal.Seat(slot: 0);

        fixture.Step();
        Assert.Equal(expected: 0, actual: fixture.Server.Population.GravityAreaStatistics.ActiveAreaCount);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Server.Population.EntryBody(index: 0)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: 100f, y: 100f, yawRadians: 0f, z: 100f);

        for (var tick = 0; (tick < 12); tick++) {
            fixture.Step();
        }

        Assert.Equal(expected: 1, actual: fixture.Server.Population.GravityAreaStatistics.ActiveAreaCount);
        Assert.Equal(expected: 1, actual: fixture.Server.Population.GravityAreaStatistics.MatchCount);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 100), actual: fixture.Server.Population.EntryBody(index: 0)!.FixedPosition.X);
        Assert.NotEqual(expected: FixedQ4816.FromInteger(value: 100), actual: fixture.Server.Population.EntryBody(index: 0)!.FixedPosition.Z);

        fixture.Server.Population.DeactivateSeat(slot: 0, tick: 100UL);
        fixture.Step();

        Assert.Equal(expected: 0, actual: fixture.Server.Population.GravityAreaStatistics.ActiveAreaCount);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AttachedArea_CheckpointRestoreContinuesBitIdenticallyOnTheNextSolve(bool zeroAcceleration) {
        using var fixture = Fixtures.FreshServer(definition: AttachedAreaDefinition(zeroAcceleration: zeroAcceleration));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Server.Population.EntryBody(index: 0)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: 100f, y: 100f, yawRadians: 0f, z: 100f);
        for (var tick = 0; (tick < 8); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: new WorldAuthorityHostRowCheckpoint(
                AnnouncedCrossingHolds: [],
                AppliedTransferHighWater: null,
                AppliedTransferIds: [],
                ElapsedEngineTicks: 0,
                ForwardedBodies: [],
                FreshCounter: 0,
                InDoubtTransfers: [],
                IsPaused: false,
                NextTransferId: 1,
                PortalOccupancy: [],
                Retained: false,
                ScheduleAccumulatorTicks: 0,
                SeededArrivals: []
            ),
            reason: out var refusal
        ), userMessage: refusal);
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var decodeRefusal
        ), userMessage: decodeRefusal);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);
        using var machines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);
        using var stateDirectory = new TemporaryStateDirectory();
        var profiles = new WorldOwnedWorlds(
            directory: stateDirectory.Path,
            machineId: Guid.NewGuid(),
            template: restoredDefinition
        );

        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: decoded,
            instanceIdentity: "gravity-area",
            machines: machines,
            profiles: profiles
        );
        var nextTick = fixture.Server.NextInputTick;
        var elapsed = 0UL;

        Assert.Equal(expected: WorldReplaySnapshot.HashState(population: fixture.Server.Population), actual: WorldReplaySnapshot.HashState(population: restored.Population));

        for (var step = 0; (step < 12); step++) {
            elapsed = checked((elapsed + Fixtures.StepTicks));
            var context = new FixedStepContext(ElapsedTicks: elapsed, StepTicks: Fixtures.StepTicks, Tick: nextTick);

            fixture.Server.Step(context: in context);
            restored.Step(context: in context);
            nextTick++;

            Assert.Equal(expected: WorldReplaySnapshot.HashState(population: fixture.Server.Population), actual: WorldReplaySnapshot.HashState(population: restored.Population));
            Assert.Equal(expected: fixture.Server.Population.GravityAreaStatistics, actual: restored.Population.GravityAreaStatistics);
        }
    }
    [Fact]
    public void LocalAreas_RoundTripTheirUnionShapes_WhileAbsenceKeepsTheMemberOmitted() {
        var absentBytes = WorldDefinitionSerialization.Serialize(definition: WithGravity(gravity: PointGravity(gravitationalConstant: 0f)));

        Assert.DoesNotContain(
            expectedSubstring: "\"areas\"",
            actualString: Encoding.UTF8.GetString(bytes: absentBytes),
            comparisonType: StringComparison.Ordinal
        );

        var definition = WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            areas: [
                new WorldGravityArea(
                    PlacementId: "ball",
                    Priority: -2,
                    Mode: WorldGravityAreaMode.Combine,
                    Bounds: new WorldGravityAreaBounds.BoxBounds(HalfExtents: new Vector3(x: 1f, y: 2f, z: 3f)),
                    Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: 0f, y: -9.81f, z: 0f))
                ),
                new WorldGravityArea(
                    PlacementId: "ball",
                    Priority: 4,
                    Mode: WorldGravityAreaMode.Replace,
                    Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 10f),
                    Acceleration: new WorldGravityAreaAcceleration.Radial(Magnitude: 3f)
                ),
            ]
        ));
        var roundTrip = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

        Assert.IsType<WorldGravityAreaBounds.BoxBounds>(@object: roundTrip.Gravity.Areas![0].Bounds);
        Assert.IsType<WorldGravityAreaAcceleration.Directional>(@object: roundTrip.Gravity.Areas[0].Acceleration);
        Assert.IsType<WorldGravityAreaBounds.SphereBounds>(@object: roundTrip.Gravity.Areas[1].Bounds);
        Assert.IsType<WorldGravityAreaAcceleration.Radial>(@object: roundTrip.Gravity.Areas[1].Acceleration);
    }
    [Fact]
    public void LocalAreaValidationRefusesNullRowsAndUnrepresentableLoweringByIndexedPath() {
        var definition = WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            areas: [new WorldGravityArea(
                PlacementId: "ball",
                Priority: 0,
                Mode: WorldGravityAreaMode.Replace,
                Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: float.MaxValue),
                Acceleration: new WorldGravityAreaAcceleration.Radial(Magnitude: float.Epsilon)
            )]
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "gravity.areas[0] cannot lower");

        var node = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            areas: [new WorldGravityArea(
                PlacementId: "ball",
                Priority: 0,
                Mode: WorldGravityAreaMode.Combine,
                Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 1f),
                Acceleration: new WorldGravityAreaAcceleration.Directional(Value: Vector3.Zero)
            )]
        )))))!.AsObject();

        node["gravity"]!["areas"]!.AsArray()[0] = null;
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: node.ToJsonString())));

        Assert.Contains(expectedSubstring: "gravity.areas[0] is required", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void PositiveGlobalConstant_SolvesBodyOnlySources_AndParticipatesAtZeroWithOneBody() {
        var definition = WithGravity(gravity: PointGravity(gravitationalConstant: 45f));
        var oneBody = CompileField(capacity: 1, definition: definition);

        oneBody.Solve(targets: [new WorldGravityTarget(
            EntityIndex: 0,
            Position: FixedVector3.Zero,
            Mass: FixedQ4816.FromInteger(value: 10)
        )]);

        Assert.True(condition: oneBody.TryAcceleration(acceleration: out var loneAcceleration, entityIndex: 0));
        Assert.Equal(expected: FixedVector3.Zero, actual: loneAcceleration);

        var twoBodies = CompileField(capacity: 2, definition: definition);

        twoBodies.Solve(targets: [
            new WorldGravityTarget(EntityIndex: 0, Position: new FixedVector3(X: FixedQ4816.FromInteger(value: -10), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), Mass: FixedQ4816.FromInteger(value: 10)),
            new WorldGravityTarget(EntityIndex: 1, Position: new FixedVector3(X: FixedQ4816.FromInteger(value: 10), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), Mass: FixedQ4816.FromInteger(value: 10)),
        ]);

        Assert.True(condition: twoBodies.TryAcceleration(acceleration: out var left, entityIndex: 0));
        Assert.True(condition: twoBodies.TryAcceleration(acceleration: out var right, entityIndex: 1));
        Assert.True(condition: (left.X > FixedQ4816.Zero));
        Assert.Equal(expected: left.X, actual: -right.X);
        Assert.Equal(expected: 2, actual: twoBodies.Statistics.BodyCount);
    }
    [Fact]
    public void BodyOnlyGlobalSolve_ComposesWithMatchingLocalArea() {
        var definition = WithGravity(gravity: PointGravity(
            gravitationalConstant: 45f,
            areas: [new WorldGravityArea(
                PlacementId: "ball",
                Priority: 0,
                Mode: WorldGravityAreaMode.Combine,
                Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 20f),
                Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: 0f, y: -3f, z: 0f))
            )]
        ));
        var field = CompileField(capacity: 2, definition: definition);

        field.Solve(targets: [
            new WorldGravityTarget(EntityIndex: 0, Position: new FixedVector3(X: FixedQ4816.FromInteger(value: -5), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), Mass: FixedQ4816.FromInteger(value: 10)),
            new WorldGravityTarget(EntityIndex: 1, Position: new FixedVector3(X: FixedQ4816.FromInteger(value: 5), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), Mass: FixedQ4816.FromInteger(value: 10)),
        ]);

        Assert.True(condition: field.TryAcceleration(acceleration: out var left, entityIndex: 0));
        Assert.True(condition: (left.X > FixedQ4816.Zero));
        Assert.Equal(expected: FixedQ4816.FromInteger(value: -3), actual: left.Y);
        Assert.Equal(expected: 2, actual: field.AreaStatistics.MatchCount);
    }
    [Fact]
    public void LocalAreaComposition_SaturatesInsteadOfWrapping_AndReplaceResetsTheFold() {
        const float Huge = 100_000_000_000_000f;
        var uniformCombine = CompileField(definition: WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            uniform: new DocumentVector3(value: new Vector3(x: Huge, y: 0f, z: 0f)),
            areas: [new WorldGravityArea(
                PlacementId: "ball",
                Priority: 0,
                Mode: WorldGravityAreaMode.Combine,
                Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 10f),
                Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: Huge, y: 0f, z: 0f))
            )]
        )), capacity: 1);
        var replaceThenCombine = CompileField(definition: WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            areas: [
                new WorldGravityArea(
                    PlacementId: "ball",
                    Priority: 0,
                    Mode: WorldGravityAreaMode.Replace,
                    Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 10f),
                    Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: Huge, y: 0f, z: 0f))
                ),
                new WorldGravityArea(
                    PlacementId: "ball",
                    Priority: 1,
                    Mode: WorldGravityAreaMode.Combine,
                    Bounds: new WorldGravityAreaBounds.SphereBounds(Radius: 10f),
                    Acceleration: new WorldGravityAreaAcceleration.Directional(Value: new Vector3(x: Huge, y: 0f, z: 0f))
                ),
            ]
        )), capacity: 1);

        foreach (var field in new[] { uniformCombine, replaceThenCombine }) {
            field.Solve(targets: [Target(entityIndex: 0, x: 0, y: 0, z: 0)]);

            Assert.True(condition: field.TryAcceleration(acceleration: out var acceleration, entityIndex: 0));
            Assert.Equal(expected: FixedQ4816.MaxValue, actual: acceleration.X);
        }
    }

    private static WorldGravityField CompileField(WorldDefinition definition, int capacity) {
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);

        return new WorldGravityField(
            capacity: capacity,
            compiled: FixedWorldGravity.Compile(gravity: definition.Gravity, placements: definition.Placements)
        );
    }
    private static WorldGravityTarget Target(int entityIndex, int x, int y, int z) => new(
        EntityIndex: entityIndex,
        Position: new FixedVector3(
            X: FixedQ4816.FromInteger(value: x),
            Y: FixedQ4816.FromInteger(value: y),
            Z: FixedQ4816.FromInteger(value: z)
        ),
        Mass: FixedQ4816.Zero
    );
}
