using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins authored surface, flight-volume, and live-medium navigation at the validation, deterministic
/// execution, cache invalidation, and checkpoint boundaries.</summary>
public sealed partial class NavigationLawTests {
    private const string DomainName = "air";
    private const string ProducerName = "navigate";
    private const string RegisterName = "goal";

    private static BodyProgramParameters NavigationParameters() => new(
        Scalars: new Dictionary<string, float> {
            ["standoffRadius"] = 0.1f,
            ["approach"] = 1f,
            ["orbit"] = 0f,
            ["altitudeGain"] = 2f,
            ["inwardGain"] = 1f,
            ["turnScale"] = 2f,
            ["forward"] = 0f,
            ["softRadius"] = 1f,
            ["weaveAmplitude"] = 0f,
            ["weaveFrequencyBase"] = 0f,
            ["weaveFrequencyRange"] = 0f,
            ["activityRateBase"] = 0f,
            ["activityRateRange"] = 0f,
            ["strafeWave"] = 0f,
            ["turnWave"] = 0f,
            ["upWave"] = 0f,
            ["pitchWave"] = 0f,
            ["rollTurn"] = 0f,
            ["pressThreshold"] = 0f,
            ["altitudeBase"] = 0f,
            ["altitudeRange"] = 0f,
        },
        Channels: new Dictionary<string, string>()
    );

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

    private static WorldNavigationDomain SurfaceDomain() => new(
        Name: "ground",
        Kind: WorldNavigationKind.Surface,
        Origin: Vector3.Zero,
        CellSize: 1f,
        Width: 4,
        Depth: 4,
        AgentRadius: 0.3f,
        AgentHeight: 1.8f,
        ArrivalDistance: 0.2f,
        ProbeUp: 2f,
        ProbeDown: 2f,
        MaxStepHeight: 0.5f,
        MaxSlopeDegrees: 45f,
        MaxExpandedNodes: 16,
        MaxPathNodes: 16
    );

    private static WorldStateLatticeTopology MediumTopology() => new(
        Name: "water-space",
        Origin: new DocumentVector3(x: -0.5f, y: -0.5f, z: -0.5f),
        CellSize: 1f,
        Width: 6,
        Depth: 3,
        Layers: 6,
        StepEveryTicks: 8
    );

    private static WorldDefinition WithFloor(WorldDefinition definition, bool withBarrier = false) {
        var shapes = new List<ShapeDocument> {
            new(
                Id: 0,
                Name: "floor",
                Type: SdfSolidPrimitive.Box,
                Position: new Vector3(x: 0f, y: -0.5f, z: 0f),
                Rotation: Quaternion.Identity,
                Scale: new Vector3(x: 10f, y: 0.5f, z: 10f),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            ),
        };
        if (withBarrier) {
            // World creation placement applies a half turn around Y. Mirror the narrow wall in authoring space so
            // this law remains explicit about the occupied navigation cells regardless of that presentation seam.
            shapes.Add(item: new ShapeDocument(
                Id: 1,
                Name: "barrier-positive",
                Type: SdfSolidPrimitive.Box,
                Position: new Vector3(x: 1.5f, y: 0.5f, z: 0f),
                Rotation: Quaternion.Identity,
                Scale: new Vector3(x: 0.25f, y: 1f, z: 0.3f),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            ));
            shapes.Add(item: new ShapeDocument(
                Id: 2,
                Name: "barrier-negative",
                Type: SdfSolidPrimitive.Box,
                Position: new Vector3(x: -1.5f, y: 0.5f, z: 0f),
                Rotation: Quaternion.Identity,
                Scale: new Vector3(x: 0.25f, y: 1f, z: 0.3f),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            ));
        }
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "navigation-floor",
                Palette: null,
                Shapes: shapes,
                Frames: null
            ),
            source: "navigation-floor"
        );
        return definition with {
            CreationsRaw = [new WorldPrototype(Id: "navigation-floor", Document: canonical.Document, HashRaw: canonical.Hash)],
            PlacementsRaw = definition.PlacementsRaw! with {
                Rows = [new WorldPlacement(
                    Id: "navigation-floor",
                    PrototypeId: "navigation-floor",
                    Position: Vector3.Zero,
                    YawDegrees: 0f,
                    Scale: 1f,
                    Solid: new WorldSolid(Margin: 0f)
                )],
            },
        };
    }

    private static WorldDefinition NavigationDocument(WorldNavigationDomain domain, bool withMedium = false) {
        var document = Fixtures.BuildDocument();
        var channels = document.Channels.ToList();
        channels.Add(item: new WorldChannel(Name: "up", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveUp));

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
            Target: new BodyTargetSource.Navigated(Domain: domain.Name, Register: RegisterName)
        );
        var kit = document.Kits[0];

        return document with {
            ChannelsRaw = channels,
            TargetRegistersRaw = [new WorldTargetRegister(Name: RegisterName, MaximumRange: 50f, MaximumHalfAngleDegrees: 180f, RequiresLineOfSight: false)],
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
                ? new WorldStateSection(World: [Fixtures.MediumRow(topology: "water-space", name: "water", heightScale: 8f)], Lattices: [MediumTopology()])
                : document.StateRaw),
        };
    }

    private static WorldBody JoinNavigator(WorldFixture fixture, FixedVector3 goal, int slot = 0) {
        var actor = WorldPrincipal.Seat(slot: slot);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        var body = fixture.Server.Body(index: actor.Index)!;
        body.SetIntentSource(source: IntentSource.Producer(name: ProducerName));
        Assert.True(condition: fixture.Server.ApplyDesignation(
            designation: new WorldDesignation(EntityIndex: actor.Index, Register: RegisterName, Subject: default, Point: goal),
            principal: actor
        ));
        return body;
    }

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
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
    );

    [Fact]
    public void SurfaceVolumeAndMediumDomainsAreDistinctValidAuthoringKinds() {
        var medium = VolumeDomain(name: "swim", kind: WorldNavigationKind.Medium, medium: "water");
        var definition = NavigationDocument(domain: medium, withMedium: true) with {
            NavigationRaw = new WorldNavigationSection(Domains: [SurfaceDomain(), VolumeDomain(), medium]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);

        var missingMedium = definition with {
            NavigationRaw = new WorldNavigationSection(Domains: [medium with { Medium = "missing" }]),
        };
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: missingMedium, reason: out reason));
        Assert.Contains(expectedSubstring: "names no lattice field carrying a medium trait", actualString: reason, comparisonType: StringComparison.Ordinal);

        var oversizedSwimmer = definition with {
            NavigationRaw = new WorldNavigationSection(Domains: [medium with { AgentRadius = 0.6f }]),
        };
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: oversizedSwimmer, reason: out reason));
        Assert.Contains(expectedSubstring: "whole-agent live medium containment", actualString: reason, comparisonType: StringComparison.Ordinal);

        var overflowingDimensions = definition with {
            NavigationRaw = new WorldNavigationSection(Domains: [VolumeDomain() with { Width = int.MaxValue, Depth = int.MaxValue, Layers = int.MaxValue }]),
        };
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: overflowingDimensions, reason: out reason));
        Assert.Contains(expectedSubstring: "dimensions necessarily exceed", actualString: reason, comparisonType: StringComparison.Ordinal);

        var flight = NavigationDocument(domain: VolumeDomain());
        var flightKits = flight.Kits.ToArray();
        var noVerticalConsumer = flight with {
            // Zeroing the hold row's own thrust is the "no compatible vertical consumer" shape.
            KitRowsRaw = [flightKits[0] with {
                Motion = flightKits[0].Motion with { Holds = [flightKits[0].Motion.Holds![0] with { Thrust = 0f }] },
            }],
        };
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: noVerticalConsumer, reason: out reason));
        Assert.Contains(expectedSubstring: "no compatible vertical consumer", actualString: reason, comparisonType: StringComparison.Ordinal);

        var stoppedShortParameters = NavigationParameters();
        var stoppedShortScalars = new Dictionary<string, float>(collection: stoppedShortParameters.Scalars) {
            ["standoffRadius"] = 0.3f,
        };
        var flightKit = flight.Kits[0];
        var stoppedShort = flight with {
            KitRowsRaw = [flightKit with {
                ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: flightKit.Producers) {
                    [ProducerName] = stoppedShortParameters with { Scalars = stoppedShortScalars },
                },
            }],
        };
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: stoppedShort, reason: out reason));
        Assert.Contains(expectedSubstring: "stops before advancing its waypoint", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeNavigationSteersInThreeDimensionsAndReproducesTheSameAuthoritativeTrace() {
        var definition = NavigationDocument(domain: VolumeDomain());
        var goal = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 4),
            Y: FixedQ4816.FromInteger(value: 4),
            Z: FixedQ4816.Zero
        );

        ulong[] Trace() {
            using var fixture = Fixtures.FreshServer(definition: definition);
            var body = JoinNavigator(fixture: fixture, goal: goal);
            var trace = new ulong[240];
            for (var tick = 0; tick < trace.Length; tick++) {
                fixture.Step();
                trace[tick] = WorldRuntimeStateHash.HashAuthoritative(server: fixture.Server, tick: (ulong)(tick + 1));
            }
            Assert.True(condition: body.FixedPosition.Y > FixedQ4816.One, userMessage: $"the flight route never climbed toward Y=4; final position was {body.FixedPosition}");
            Assert.Contains(expectedSubstring: "navigation=active", actualString: fixture.Server.Population.DescribeTargets(bodyIndex: 0), comparisonType: StringComparison.Ordinal);
            return trace;
        }

        Assert.Equal(expected: Trace(), actual: Trace());
    }

    [Fact]
    public void SurfaceNavigationSamplesGroundAndProducesATraversableRoute() {
        var definition = WithFloor(definition: NavigationDocument(domain: SurfaceDomain()));
        using var fixture = Fixtures.FreshServer(definition: definition);
        var body = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(X: FixedQ4816.FromInteger(value: 3), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)
        );
        fixture.Step();

        var checkpoint = fixture.Server.Population.Capture();
        var navigation = Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(
            @object: checkpoint.Entries.Single(row => row.Index == 0).Navigation
        );
        Assert.Contains(expectedSubstring: "clear=16/16", actualString: fixture.Server.Population.DescribeNavigation(), comparisonType: StringComparison.Ordinal);
        Assert.NotEmpty(collection: navigation.Path);
        Assert.Contains(expectedSubstring: "ground:surface", actualString: fixture.Server.Population.DescribeNavigation(), comparisonType: StringComparison.Ordinal);

        for (var tick = 0; tick < 120; tick++) {
            fixture.Step();
        }
        Assert.True(condition: body.FixedPosition.X >= FixedQ4816.FromDouble(value: 0.9), userMessage: $"the surface follower did not traverse the baked ground route; final position was {body.FixedPosition}");
    }

    [Fact]
    public void SurfaceNavigationDetoursAroundSweptAgentClearance() {
        var definition = WithFloor(definition: NavigationDocument(domain: SurfaceDomain()), withBarrier: true);
        using var fixture = Fixtures.FreshServer(definition: definition);
        _ = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(X: FixedQ4816.FromInteger(value: 3), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)
        );
        fixture.Step();

        var route = Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(
            @object: fixture.Server.Population.Capture().Entries.Single(row => row.Index == 0).Navigation
        );
        Assert.True(condition: route.Path.Length > 4, userMessage: $"the route crossed the direct four-cell lane: {string.Join(separator: ',', values: route.Path)}");
    }

    [Fact]
    public void ARouteSurvivesCheckpointCodecAndContinuesBitIdentically() {
        var definition = NavigationDocument(domain: VolumeDomain());
        var goal = new FixedVector3(X: FixedQ4816.FromInteger(value: 4), Y: FixedQ4816.FromInteger(value: 3), Z: FixedQ4816.Zero);
        using var fixture = Fixtures.FreshServer(definition: definition);
        _ = JoinNavigator(fixture: fixture, goal: goal);
        fixture.Step();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var captured, hostRow: EmptyHostRow(), reason: out var reason), userMessage: reason);
        var route = Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(@object: captured!.Population.Entries.Single(row => row.Index == 0).Navigation);
        Assert.NotEmpty(collection: route.Path);
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: captured);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: encoded, checkpoint: out var decoded, reason: out reason), userMessage: reason);

        var expected = new ulong[60];
        for (var tick = 0; tick < expected.Length; tick++) {
            fixture.Step();
            expected[tick] = WorldRuntimeStateHash.HashAuthoritative(server: fixture.Server, tick: (ulong)(tick + 2));
        }
        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        var actual = new ulong[60];
        for (var tick = 0; tick < actual.Length; tick++) {
            fixture.Step();
            actual[tick] = WorldRuntimeStateHash.HashAuthoritative(server: fixture.Server, tick: (ulong)(tick + 2));
        }
        Assert.Equal(expected: expected, actual: actual);
    }

    [Fact]
    public void DryingALiveMediumInvalidatesTheCachedSwimRoute() {
        var domain = VolumeDomain(name: "swim", kind: WorldNavigationKind.Medium, medium: "water");
        using var fixture = Fixtures.FreshServer(definition: NavigationDocument(domain: domain, withMedium: true));
        _ = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(X: FixedQ4816.FromInteger(value: 4), Y: FixedQ4816.FromInteger(value: 2), Z: FixedQ4816.Zero)
        );
        fixture.Step();
        Assert.Equal(expected: 1L, actual: fixture.Server.Population.NavigationFact(index: 0, facet: "hasPath"));

        var fields = Assert.IsType<WorldFieldLattice>(@object: fixture.Server.Population.Fields);
        fields.Restore(checkpoint: new WorldFieldLattice.WorldFieldCheckpoint(Raw: [new long[fields.CellCount]]));
        fixture.Step();

        Assert.Equal(expected: 0L, actual: fixture.Server.Population.NavigationFact(index: 0, facet: "hasPath"));
        Assert.Equal(expected: 1L, actual: fixture.Server.Population.NavigationFact(index: 0, facet: "unreachable"));
    }

    [Fact]
    public void MediumSegmentCannotSkipABriefDryCornerCrossing() {
        using var fixture = Fixtures.FreshServer(NavigationDocument(VolumeDomain("swim", WorldNavigationKind.Medium, "water"), withMedium: true));
        var fields = fixture.Server.Population.Fields!;
        var raw = Enumerable.Repeat(FixedQ4816.One.Value, fields.CellCount).ToArray();
        var dry = new FixedVector3(FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.Zero);
        Assert.True(fields.TryCellOf(dry, out var dryCell));
        raw[dryCell] = 0;
        fields.Restore(new WorldFieldLattice.WorldFieldCheckpoint([raw]));
        // Cross x=.5 before z=.5, briefly entering cell (1,0,0). Both endpoints and the old half-cell
        // samples are wet: endpoint sampling alone incorrectly admitted this segment.
        var from = new FixedVector3(FixedQ4816.FromDouble(.40), FixedQ4816.Zero, FixedQ4816.FromDouble(.38));
        var to = new FixedVector3(FixedQ4816.FromDouble(.62), FixedQ4816.Zero, FixedQ4816.FromDouble(.60));
        Assert.True(fields.IsInsideMedium(0, from));
        Assert.True(fields.IsInsideMedium(0, to));
        Assert.False(fields.IsSegmentInsideMedium(0, from, to, FixedQ4816.Zero, 32));
        Assert.False(fields.IsSegmentInsideMedium(0, to, from, FixedQ4816.Zero, 32));
        raw[dryCell] = FixedQ4816.One.Value;
        fields.Restore(new WorldFieldLattice.WorldFieldCheckpoint([raw]));
        Assert.True(fields.IsSegmentInsideMedium(0, from, to, FixedQ4816.Zero, 32));
    }

    [Fact]
    public void MediumClearanceCannotHideADryCapBetweenWetLayers() {
        using var fixture = Fixtures.FreshServer(NavigationDocument(VolumeDomain("swim", WorldNavigationKind.Medium, "water"), withMedium: true));
        var fields = fixture.Server.Population.Fields!;
        var raw = Enumerable.Repeat(FixedQ4816.One.Value, fields.CellCount).ToArray();
        var center = new FixedVector3(FixedQ4816.Zero, FixedQ4816.FromDouble(.5), FixedQ4816.Zero);
        Assert.True(fields.TryCellOf(center with { Y = FixedQ4816.FromDouble(.3) }, out var bottomCell));
        // Origin=-.5, heightScale=8: the lower cell is wet only up to y=.375. Cube bottom=.25,
        // center=.5 and top=.75 are all wet, but its interior between .375 and .5 is dry.
        raw[bottomCell] = FixedQ4816.FromDouble(.875 / 8).Value;
        fields.Restore(new WorldFieldLattice.WorldFieldCheckpoint([raw]));
        Assert.True(fields.IsInsideMedium(0, center with { Y = FixedQ4816.FromDouble(.25) }));
        Assert.True(fields.IsInsideMedium(0, center));
        Assert.True(fields.IsInsideMedium(0, center with { Y = FixedQ4816.FromDouble(.75) }));
        Assert.False(fields.IsInsideMedium(0, center, FixedQ4816.FromDouble(.25)));
        Assert.False(fields.IsSegmentInsideMedium(0, center, center, FixedQ4816.FromDouble(.25), 32));
    }

    [Fact]
    public void MediumClearanceStraddlingTwoVoxelLayersRequiresBothWet() {
        using var fixture = Fixtures.FreshServer(NavigationDocument(VolumeDomain("swim", WorldNavigationKind.Medium, "water"), withMedium: true));
        var fields = fixture.Server.Population.Fields!;
        var raw = new long[fields.CellCount];
        Assert.True(fields.TryCellOf(new FixedVector3(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero), out var bottomCell));
        Assert.True(fields.TryCellOf(new FixedVector3(FixedQ4816.Zero, FixedQ4816.FromInteger(1), FixedQ4816.Zero), out var topCell));
        Assert.NotEqual(bottomCell, topCell);
        // Only the bottom layer is wet; its projected free surface (heightScale 8) reaches far past the
        // top layer, but that layer's own voxel is dry and must still gate a box that touches it.
        raw[bottomCell] = FixedQ4816.One.Value;
        fields.Restore(new WorldFieldLattice.WorldFieldCheckpoint([raw]));
        var straddling = new FixedVector3(FixedQ4816.Zero, FixedQ4816.FromDouble(.4), FixedQ4816.Zero);
        Assert.False(fields.IsInsideMedium(0, straddling, FixedQ4816.FromDouble(.2)));
        Assert.False(fields.IsSegmentInsideMedium(0, straddling, straddling, FixedQ4816.FromDouble(.2), 32));
        raw[topCell] = FixedQ4816.One.Value;
        fields.Restore(new WorldFieldLattice.WorldFieldCheckpoint([raw]));
        Assert.True(fields.IsInsideMedium(0, straddling, FixedQ4816.FromDouble(.2)));
        Assert.True(fields.IsSegmentInsideMedium(0, straddling, straddling, FixedQ4816.FromDouble(.2), 32));
    }

    [Fact]
    public void MediumSweepRejectsInvalidBoundsAndAllocatesNothingAfterWarmup() {
        using var fixture = Fixtures.FreshServer(NavigationDocument(VolumeDomain("swim", WorldNavigationKind.Medium, "water"), withMedium: true));
        var fields = fixture.Server.Population.Fields!;
        var from = FixedVector3.Zero;
        var to = new FixedVector3(FixedQ4816.FromInteger(4), FixedQ4816.FromInteger(3), FixedQ4816.One);
        var clearance = FixedQ4816.FromDouble(.25);
        Assert.False(fields.IsSegmentInsideMedium(0, from, to, clearance, 1));
        Assert.False(fields.IsSegmentInsideMedium(-1, from, to, clearance, 32));
        Assert.False(fields.IsSegmentInsideMedium(0, from, to, FixedQ4816.One, 32));
        Assert.False(fields.IsSegmentInsideMedium(0, from, to with { X = FixedQ4816.MaxValue }, clearance, 32));
        Assert.False(fields.IsInsideMedium(0, to with { X = FixedQ4816.MaxValue }, clearance));
        for (var index = 0; index < 100; index++) { Assert.True(fields.IsSegmentInsideMedium(0, from, to, clearance, 32)); }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var allWet = true;
        for (var index = 0; index < 4096; index++) { allWet &= fields.IsSegmentInsideMedium(0, from, to, clearance, 32); }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(allWet);
        Assert.Equal(0, bytes);
    }

    private static WorldDefinition FlockMovementDocument(WorldNavigationKind kind, bool constrained) {
        var domain = VolumeDomain("habitat", kind, kind == WorldNavigationKind.Medium ? "water" : null);
        var definition = NavigationDocument(domain, withMedium: kind == WorldNavigationKind.Medium);
        var kit = definition.Kits[0];
        var flock = new WorldFlockProfile(20, 1, 4, 3, 60, WorldFlockSpace.Volume,
            0, 0, 1, 0, 0, 0, 180, false, constrained ? "habitat" : null);
        return definition with {
            BodyMotionProgramsRaw = [.. definition.BodyMotionPrograms, new BodyMotionProgram("flock", BodyMotionProgram.CurrentVersion,
                BodyProgramKind.Producer, [BodyMotionOp.ProduceFlockIntent])],
            KitRowsRaw = [kit with { ProducersRaw = new Dictionary<string, BodyProgramParameters>(kit.Producers) {
                ["flock"] = new(new Dictionary<string, float>(), new Dictionary<string, string>(), flock),
            } }],
        };
    }

    [Theory]
    [InlineData(WorldNavigationKind.Volume)]
    [InlineData(WorldNavigationKind.Medium)]
    public void AuthoredMovementDomainConstrainsActualOffRouteFlockMotion(WorldNavigationKind kind) {
        foreach (var constrained in new[] { true, false }) {
            var definition = FlockMovementDocument(kind, constrained);
            Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
            using var fixture = Fixtures.FreshServer(definition);
            Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(WorldPrincipal.Seat(0), 0, null, WorldProtocol.WireProtocolKey)).Accepted);
            var body = fixture.Server.Body(0)!;
            body.Pose(new FixedVector3(FixedQ4816.FromDouble(5.1), FixedQ4816.Zero, FixedQ4816.Zero),
                FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
            body.SetIntentSource(IntentSource.Producer("flock"));
            Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(WorldPrincipal.Seat(1), 1, null, WorldProtocol.WireProtocolKey)).Accepted);
            var other = fixture.Server.Body(1)!;
            other.Pose(new FixedVector3(FixedQ4816.FromInteger(8), FixedQ4816.Zero, FixedQ4816.Zero),
                FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
            other.SetIntentSource(IntentSource.Idle);
            var refusals = 0;
            for (var tick = 0; tick < 180; tick++) {
                fixture.Step();
                refusals += fixture.Server.Population.FlockStatistics.MotionRefusals;
                if (constrained && kind == WorldNavigationKind.Medium) {
                    Assert.True(fixture.Server.Population.Fields!.IsInsideMedium(0, body.FixedPosition, FixedQ4816.FromDouble(.25)));
                }
            }
            Assert.Equal(constrained, refusals > 0);
            Assert.Equal(constrained, body.FixedPosition.X < FixedQ4816.FromDouble(5.5));
            if (constrained) {
                // The authored constraint leaves with its producer; it is not a permanent invisible wall.
                body.SetIntentSource(IntentSource.Live);
                var strafe = definition.Channels.ToList().FindIndex(channel => channel.Role == ChannelRole.MoveStrafe);
                for (var tick = 0; tick < 60; tick++) {
                    body.SubmitIntent(default(PlayerIntent).WithChannel(strafe, FixedQ4816.One));
                    fixture.Step();
                }
                Assert.True(body.FixedPosition.X > FixedQ4816.FromDouble(5.5));
                Assert.Equal(0, fixture.Server.Population.FlockStatistics.MotionChecks);
            }
        }
    }

    [Fact]
    public void MovementDomainMustExistAndEncloseOffsetColliderVolumes() {
        var definition = FlockMovementDocument(WorldNavigationKind.Volume, constrained: true);
        var kit = definition.Kits[0];
        foreach (var collider in new WorldCollider[] { new WorldCollider.Sphere(.2f),
            new WorldCollider.Capsule(new DocumentVector3(.3f, 0, 0), .1f),
            new WorldCollider.Box(new DocumentVector3(.2f, .1f, .2f), new DocumentQuaternion(0, 0, 0, 1)) }) {
            Assert.False(WorldDefinitionValidator.TryValidateLocally(definition with { KitRowsRaw = [kit with { Collider = collider }] }, out var reason));
            Assert.Contains("agentRadius must enclose", reason);
        }
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition with { KitRowsRaw = [kit with { Collider = new WorldCollider.Sphere(.1f) }] }, out var validReason), validReason);
        var parameters = kit.Producers["flock"];
        var missing = kit with { ProducersRaw = new Dictionary<string, BodyProgramParameters>(kit.Producers) {
            ["flock"] = parameters with { Flock = parameters.Flock! with { MovementDomain = "missing" } },
        } };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition with { KitRowsRaw = [missing] }, out var missingReason));
        Assert.Contains("movementDomain requires", missingReason);
    }

    [Fact]
    public void LeavingAProducerClearsItsRuleVisibleRouteState() {
        using var fixture = Fixtures.FreshServer(definition: NavigationDocument(domain: VolumeDomain()));
        var body = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(X: FixedQ4816.FromInteger(value: 4), Y: FixedQ4816.FromInteger(value: 2), Z: FixedQ4816.Zero)
        );
        fixture.Step();
        Assert.Equal(expected: 1L, actual: fixture.Server.Population.NavigationFact(index: 0, facet: "hasPath"));

        body.SetIntentSource(source: IntentSource.Live);
        fixture.Step();

        Assert.Equal(expected: 0L, actual: fixture.Server.Population.NavigationFact(index: 0, facet: "hasPath"));
        Assert.Contains(expectedSubstring: "navigation=notarget", actualString: fixture.Server.Population.DescribeTargets(bodyIndex: 0), comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedRouteCheckpointRefusesBeforeMutatingTheServer() {
        using var fixture = Fixtures.FreshServer(definition: NavigationDocument(domain: VolumeDomain()));
        _ = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(X: FixedQ4816.FromInteger(value: 4), Y: FixedQ4816.FromInteger(value: 2), Z: FixedQ4816.Zero)
        );
        fixture.Step();
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var captured, hostRow: EmptyHostRow(), reason: out var reason), userMessage: reason);
        var entry = captured!.Population.Entries.Single(row => row.Index == 0);
        var route = Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(@object: entry.Navigation);
        var malformedEntry = entry with { Navigation = route with { Path = [int.MaxValue] } };
        var malformed = captured with {
            Population = captured.Population with {
                Entries = captured.Population.Entries.Select(row => row.Index == entry.Index ? malformedEntry : row).ToArray(),
            },
        };
        var before = WorldRuntimeStateHash.HashAuthoritative(server: fixture.Server, tick: 1);

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Server.RestoreCheckpoint(checkpoint: malformed));

        Assert.Contains(expectedSubstring: "path node", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: before, actual: WorldRuntimeStateHash.HashAuthoritative(server: fixture.Server, tick: 1));
    }

    [Fact]
    public void NavigationStatusIsACompiledRuleFactRatherThanASecondScriptingSurface() {
        var observed = WorldCellName.Parse(candidate: "observed-route");
        var definition = NavigationDocument(domain: VolumeDomain()) with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: observed,
                Kind: CellKind.Int,
                Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
            )]),
            Rules = [new WorldRule(
                Name: WorldCellName.Parse(candidate: "observe-route"),
                Gate: new ActionPredicate.CompareState(
                    State: "$nav:body:0:hasPath",
                    Comparison: ActionStateComparison.Equal,
                    Value: 1m
                ),
                Effects: [new ActionEffect.SetState(State: observed.Value, Value: 1m)],
                Mode: ActionTriggerMode.Edge
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        _ = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(X: FixedQ4816.FromInteger(value: 4), Y: FixedQ4816.FromInteger(value: 2), Z: FixedQ4816.Zero)
        );
        fixture.Step();

        Assert.Equal(expected: 1L, actual: fixture.Server.Definition.State.Single(row => row.Name == observed).Cells![0].Value);
    }
}
