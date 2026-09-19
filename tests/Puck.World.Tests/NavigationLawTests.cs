using System.Diagnostics;
using System.Numerics;
using Puck.Assets.Documents;
using Puck.Physics.Fields;
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
            ["approachAltitudeGain"] = 2f,
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
    private static WorldFieldTopology MediumTopology() => new(
        Name: "water-space",
        Origin: new DocumentVector3(
            x: -0.5f,
            y: -0.5f,
            z: -0.5f
        ),
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
            Position: new Vector3(
                x: 0f,
                y: -0.5f,
                z: 0f
            ),
            Rotation: Quaternion.Identity,
            Scale: new Vector3(
                x: 10f,
                y: 0.5f,
                z: 10f
            ),
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
                Position: new Vector3(
                    x: 1.5f,
                    y: 0.5f,
                    z: 0f
                ),
                Rotation: Quaternion.Identity,
                Scale: new Vector3(
                    x: 0.25f,
                    y: 1f,
                    z: 0.3f
                ),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            ));
            shapes.Add(item: new ShapeDocument(
                Id: 2,
                Name: "barrier-negative",
                Type: SdfSolidPrimitive.Box,
                Position: new Vector3(
                    x: -1.5f,
                    y: 0.5f,
                    z: 0f
                ),
                Rotation: Quaternion.Identity,
                Scale: new Vector3(
                    x: 0.25f,
                    y: 1f,
                    z: 0.3f
                ),
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
            CreationsRaw = [new WorldPrototype(
                Id: "navigation-floor",
                Document: canonical.Document,
                HashRaw: canonical.Hash
            )],
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
        var medium = VolumeDomain(
            kind: WorldNavigationKind.Medium,
            medium: "water",
            name: "swim"
        );
        var definition = NavigationDocument(
            domain: medium,
            withMedium: true
        ) with {
            NavigationRaw = new WorldNavigationSection(Domains: [SurfaceDomain(), VolumeDomain(), medium]),
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );

        var missingMedium = definition with {
            NavigationRaw = new WorldNavigationSection(Domains: [medium with { Medium = "missing" }]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: missingMedium,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "names no lattice field carrying a medium trait"
        );

        var oversizedSwimmer = definition with {
            NavigationRaw = new WorldNavigationSection(Domains: [medium with { AgentRadius = 0.6f }]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: oversizedSwimmer,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "whole-agent live medium containment"
        );

        var overflowingDimensions = definition with {
            NavigationRaw = new WorldNavigationSection(Domains: [VolumeDomain() with { Width = int.MaxValue, Depth = int.MaxValue, Layers = int.MaxValue }]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: overflowingDimensions,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "dimensions necessarily exceed"
        );

        var flight = NavigationDocument(domain: VolumeDomain());
        var flightKits = flight.Kits.ToArray();
        var noVerticalConsumer = flight with {
            // Zeroing the hold row's own thrust is the "no compatible vertical consumer" shape.
            KitRowsRaw = [flightKits[0] with {
                Motion = flightKits[0].Motion with { Holds = [flightKits[0].Motion.Holds![0] with { Thrust = 0f }] },
            }],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: noVerticalConsumer,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "no compatible vertical consumer"
        );

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

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: stoppedShort,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "stops before advancing its waypoint"
        );
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
            var body = JoinNavigator(
                fixture: fixture,
                goal: goal
            );
            var trace = new ulong[240];

            for (var tick = 0; (tick < trace.Length); tick++) {
                fixture.Step();
                trace[tick] = WorldStateHashComposition.HashAuthoritative(
                    server: fixture.Server,
                    tick: ((ulong)(tick + 1))
                );
            }
            Assert.True(
                condition: (body.FixedPosition.Y > FixedQ4816.One),
                userMessage: $"the flight route never climbed toward Y=4; final position was {body.FixedPosition}"
            );
            Assert.Contains(
                expectedSubstring: "navigation=active",
                actualString: fixture.Server.Population.DescribeTargets(bodyIndex: 0),
                comparisonType: StringComparison.Ordinal
            );
            return trace;
        }

        Assert.Equal(
            expected: Trace(),
            actual: Trace()
        );
    }
    [Fact]
    public void SurfaceNavigationSamplesGroundAndProducesATraversableRoute() {
        var definition = WithFloor(definition: NavigationDocument(domain: SurfaceDomain()));
        using var fixture = Fixtures.FreshServer(definition: definition);
        var body = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 3),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            )
        );

        fixture.Step();

        var checkpoint = fixture.Server.Population.Capture();
        var navigation = Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(@object: checkpoint.Entries.Single(predicate: row => (row.Index == 0)).Navigation);

        Assert.Contains(
            expectedSubstring: "clear=16/16",
            actualString: fixture.Server.Population.DescribeNavigation(),
            comparisonType: StringComparison.Ordinal
        );
        Assert.NotEmpty(collection: navigation.Path);
        Assert.Contains(
            expectedSubstring: "ground:surface",
            actualString: fixture.Server.Population.DescribeNavigation(),
            comparisonType: StringComparison.Ordinal
        );

        for (var tick = 0; (tick < 120); tick++) {
            fixture.Step();
        }
        Assert.True(
            condition: (body.FixedPosition.X >= FixedQ4816.FromDouble(value: 0.9)),
            userMessage: $"the surface follower did not traverse the baked ground route; final position was {body.FixedPosition}"
        );
    }
    [Fact]
    public void SurfaceNavigationDetoursAroundSweptAgentClearance() {
        var definition = WithFloor(
            definition: NavigationDocument(domain: SurfaceDomain()),
            withBarrier: true
        );
        using var fixture = Fixtures.FreshServer(definition: definition);

        _ = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 3),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            )
        );
        fixture.Step();

        var route = Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(@object: fixture.Server.Population.Capture().Entries.Single(predicate: row => (row.Index == 0)).Navigation);

        Assert.True(
            condition: (route.Path.Length > 4),
            userMessage: $"the route crossed the direct four-cell lane: {string.Join(
                separator: ',',
                values: route.Path
            )}"
        );
    }
    [Fact]
    public void ARouteSurvivesCheckpointCodecAndContinuesBitIdentically() {
        var definition = NavigationDocument(domain: VolumeDomain());
        var goal = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 4),
            Y: FixedQ4816.FromInteger(value: 3),
            Z: FixedQ4816.Zero
        );
        using var fixture = Fixtures.FreshServer(definition: definition);

        _ = JoinNavigator(
            fixture: fixture,
            goal: goal
        );
        fixture.Step();

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var captured,
                hostRow: EmptyHostRow(),
                reason: out var reason
            ),
            userMessage: reason
        );
        var route = Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(@object: captured!.Population.Entries.Single(predicate: row => (row.Index == 0)).Navigation);

        Assert.NotEmpty(collection: route.Path);
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: captured);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: encoded,
                checkpoint: out var decoded,
                reason: out reason
            ),
            userMessage: reason
        );

        var expected = new ulong[60];

        for (var tick = 0; (tick < expected.Length); tick++) {
            fixture.Step();
            expected[tick] = WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: ((ulong)(tick + 2))
            );
        }
        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        var actual = new ulong[60];

        for (var tick = 0; (tick < actual.Length); tick++) {
            fixture.Step();
            actual[tick] = WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: ((ulong)(tick + 2))
            );
        }
        Assert.Equal(
            actual: actual,
            expected: expected
        );
    }
    // Raising the far-below floor into the domain changes its occupancy, so only a domain with nothing baked may be
    // retained.
    [Fact]
    public void ASolidRebuildRetainsAnUnroutedDomainButRebuildsARestoredRoutesChangedDomain() {
        var floored = WithFloor(definition: NavigationDocument(domain: VolumeDomain()));
        var definition = floored with {
            PlacementsRaw = floored.PlacementsRaw! with {
                Rows = [floored.Placements[0] with { Position = new Vector3(
                x: 0f,
                y: -20f,
                z: 0f
            ) }],
            },
        };

        static void RaiseFloor(WorldFixture fixture) {
            var floor = fixture.Server.Definition.Placements.Single(predicate: placement => (placement.Id == "navigation-floor"));

            fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
                WorldPrincipal.Console,
                floor with {
                    Position = new Vector3(
                    x: 0f,
                    y: 2f,
                    z: 0f
                ),
                }
            ));
            fixture.Step();
            Assert.Equal(
                expected: new DocumentVector3(
                    x: 0f,
                    y: 2f,
                    z: 0f
                ),
                actual: fixture.Server.Definition.Placements.Single(predicate: placement => (placement.Id == "navigation-floor")).Position
            );
        }

        using (var unrouted = Fixtures.FreshServer(definition: definition)) {
            unrouted.Step();
            RaiseFloor(fixture: unrouted);
            Assert.Equal(
                expected: 1,
                actual: unrouted.Server.Population.NavigationRetainedDomainCount
            );
            Assert.Equal(
                expected: 0,
                actual: unrouted.Server.Population.NavigationRebuiltDomainCount
            );
        }

        using var source = Fixtures.FreshServer(definition: definition);

        _ = JoinNavigator(
            fixture: source,
            goal: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 4),
                Y: FixedQ4816.FromInteger(value: 3),
                Z: FixedQ4816.Zero
            )
        );
        source.Step();
        Assert.True(
            condition: source.Server.TryCaptureCheckpoint(
                checkpoint: out var captured,
                hostRow: EmptyHostRow(),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.NotEmpty(collection: Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(@object: captured!.Population.Entries.Single(predicate: row => (row.Index == 0)).Navigation).Path);
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured),
                checkpoint: out var decoded,
                reason: out reason
            ),
            userMessage: reason
        );

        using var restored = Fixtures.FreshServer(definition: definition);

        restored.Server.RestoreCheckpoint(checkpoint: decoded!);
        RaiseFloor(fixture: restored);
        Assert.Equal(
            expected: 0,
            actual: restored.Server.Population.NavigationRetainedDomainCount
        );
        Assert.Equal(
            expected: 1,
            actual: restored.Server.Population.NavigationRebuiltDomainCount
        );
    }
    [Fact]
    public void DryingALiveMediumInvalidatesTheCachedSwimRoute() {
        var domain = VolumeDomain(
            kind: WorldNavigationKind.Medium,
            medium: "water",
            name: "swim"
        );
        using var fixture = Fixtures.FreshServer(definition: NavigationDocument(
            domain: domain,
            withMedium: true
        ));

        _ = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 4),
                Y: FixedQ4816.FromInteger(value: 2),
                Z: FixedQ4816.Zero
            )
        );
        fixture.Step();
        Assert.Equal(
            expected: 1L,
            actual: fixture.Server.Population.NavigationFact(
                facet: "hasPath",
                index: 0
            )
        );

        var fields = Assert.IsType<FieldLattice>(@object: fixture.Server.Population.Fields);

        fields.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [new long[fields.CellCount]]));
        fixture.Step();

        Assert.Equal(
            expected: 0L,
            actual: fixture.Server.Population.NavigationFact(
                facet: "hasPath",
                index: 0
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: fixture.Server.Population.NavigationFact(
                facet: "unreachable",
                index: 0
            )
        );
    }
    [Fact]
    public void MediumSegmentCannotSkipABriefDryCornerCrossing() {
        using var fixture = Fixtures.FreshServer(NavigationDocument(
            VolumeDomain(
                kind: WorldNavigationKind.Medium,
                medium: "water",
                name: "swim"
            ),
            withMedium: true
        ));
        var fields = fixture.Server.Population.Fields!;
        var raw = Enumerable.Repeat(
            FixedQ4816.One.Value,
            fields.CellCount
        ).ToArray();
        var dry = new FixedVector3(
            X: FixedQ4816.One,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );

        Assert.True(condition: fields.TryCellOf(
            cell: out var dryCell,
            position: dry
        ));
        raw[dryCell] = 0;
        fields.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [raw]));
        // Cross x=.5 before z=.5, briefly entering cell (1,0,0). Both endpoints and the old half-cell
        // samples are wet: endpoint sampling alone incorrectly admitted this segment.
        var from = new FixedVector3(
            X: FixedQ4816.FromDouble(value: .40),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: .38)
        );
        var to = new FixedVector3(
            X: FixedQ4816.FromDouble(value: .62),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: .60)
        );

        Assert.True(condition: fields.IsInsideMedium(
            field: 0,
            position: from
        ));
        Assert.True(condition: fields.IsInsideMedium(
            field: 0,
            position: to
        ));
        Assert.False(condition: fields.IsSegmentInsideMedium(
            0,
            from,
            to,
            FixedQ4816.Zero,
            32
        ));
        Assert.False(condition: fields.IsSegmentInsideMedium(
            0,
            to,
            from,
            FixedQ4816.Zero,
            32
        ));
        raw[dryCell] = FixedQ4816.One.Value;
        fields.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [raw]));
        Assert.True(condition: fields.IsSegmentInsideMedium(
            0,
            from,
            to,
            FixedQ4816.Zero,
            32
        ));
    }
    [Fact]
    public void MediumClearanceCannotHideADryCapBetweenWetLayers() {
        using var fixture = Fixtures.FreshServer(NavigationDocument(
            VolumeDomain(
                kind: WorldNavigationKind.Medium,
                medium: "water",
                name: "swim"
            ),
            withMedium: true
        ));
        var fields = fixture.Server.Population.Fields!;
        var raw = Enumerable.Repeat(
            FixedQ4816.One.Value,
            fields.CellCount
        ).ToArray();
        var center = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: .5),
            Z: FixedQ4816.Zero
        );

        Assert.True(condition: fields.TryCellOf(
            center with { Y = FixedQ4816.FromDouble(value: .3) },
            out var bottomCell
        ));
        // Origin=-.5, heightScale=8: the lower cell is wet only up to y=.375. Cube bottom=.25,
        // center=.5 and top=.75 are all wet, but its interior between .375 and .5 is dry.
        raw[bottomCell] = FixedQ4816.FromDouble(value: (.875 / 8)).Value;
        fields.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [raw]));
        Assert.True(condition: fields.IsInsideMedium(
            field: 0,
            position: center with { Y = FixedQ4816.FromDouble(value: .25) }
        ));
        Assert.True(condition: fields.IsInsideMedium(
            field: 0,
            position: center
        ));
        Assert.True(condition: fields.IsInsideMedium(
            field: 0,
            position: center with { Y = FixedQ4816.FromDouble(value: .75) }
        ));
        Assert.False(condition: fields.IsInsideMedium(
            0,
            center,
            FixedQ4816.FromDouble(value: .25)
        ));
        Assert.False(condition: fields.IsSegmentInsideMedium(
            0,
            center,
            center,
            FixedQ4816.FromDouble(value: .25),
            32
        ));
    }
    [Fact]
    public void MediumClearanceStraddlingTwoVoxelLayersRequiresBothWet() {
        using var fixture = Fixtures.FreshServer(NavigationDocument(
            VolumeDomain(
                kind: WorldNavigationKind.Medium,
                medium: "water",
                name: "swim"
            ),
            withMedium: true
        ));
        var fields = fixture.Server.Population.Fields!;
        var raw = new long[fields.CellCount];

        Assert.True(condition: fields.TryCellOf(
            new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            out var bottomCell
        ));
        Assert.True(condition: fields.TryCellOf(
            new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.FromInteger(value: 1),
                Z: FixedQ4816.Zero
            ),
            out var topCell
        ));
        Assert.NotEqual(
            actual: topCell,
            expected: bottomCell
        );
        // Only the bottom layer is wet; its projected free surface (heightScale 8) reaches far past the
        // top layer, but that layer's own voxel is dry and must still gate a box that touches it.
        raw[bottomCell] = FixedQ4816.One.Value;
        fields.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [raw]));
        var straddling = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: .4),
            Z: FixedQ4816.Zero
        );

        Assert.False(condition: fields.IsInsideMedium(
            0,
            straddling,
            FixedQ4816.FromDouble(value: .2)
        ));
        Assert.False(condition: fields.IsSegmentInsideMedium(
            0,
            straddling,
            straddling,
            FixedQ4816.FromDouble(value: .2),
            32
        ));
        raw[topCell] = FixedQ4816.One.Value;
        fields.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [raw]));
        Assert.True(condition: fields.IsInsideMedium(
            0,
            straddling,
            FixedQ4816.FromDouble(value: .2)
        ));
        Assert.True(condition: fields.IsSegmentInsideMedium(
            0,
            straddling,
            straddling,
            FixedQ4816.FromDouble(value: .2),
            32
        ));
    }
    [Fact]
    public void MediumSweepRejectsInvalidBoundsAndAllocatesNothingAfterWarmup() {
        using var fixture = Fixtures.FreshServer(NavigationDocument(
            VolumeDomain(
                kind: WorldNavigationKind.Medium,
                medium: "water",
                name: "swim"
            ),
            withMedium: true
        ));
        var fields = fixture.Server.Population.Fields!;
        var from = FixedVector3.Zero;
        var to = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 4),
            Y: FixedQ4816.FromInteger(value: 3),
            Z: FixedQ4816.One
        );
        var clearance = FixedQ4816.FromDouble(value: .25);

        Assert.False(condition: fields.IsSegmentInsideMedium(
            clearance: clearance,
            field: 0,
            from: from,
            maximumSubdivisions: 1,
            to: to
        ));
        Assert.False(condition: fields.IsSegmentInsideMedium(
            clearance: clearance,
            field: -1,
            from: from,
            maximumSubdivisions: 32,
            to: to
        ));
        Assert.False(condition: fields.IsSegmentInsideMedium(
            0,
            from,
            to,
            FixedQ4816.One,
            32
        ));
        Assert.False(condition: fields.IsSegmentInsideMedium(
            0,
            from,
            to with { X = FixedQ4816.MaxValue },
            clearance,
            32
        ));
        Assert.False(condition: fields.IsInsideMedium(
            0,
            to with { X = FixedQ4816.MaxValue },
            clearance
        ));
        for (var index = 0; (index < 100); index++) {
            Assert.True(condition: fields.IsSegmentInsideMedium(
            clearance: clearance,
            field: 0,
            from: from,
            maximumSubdivisions: 32,
            to: to
        ));
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var allWet = true;

        for (var index = 0; (index < 4096); index++) {
            allWet &= fields.IsSegmentInsideMedium(
            clearance: clearance,
            field: 0,
            from: from,
            maximumSubdivisions: 32,
            to: to
        );
        }
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated);

        Assert.True(condition: allWet);
        Assert.Equal(
            actual: bytes,
            expected: 0
        );
    }

    private static WorldDefinition FlockMovementDocument(WorldNavigationKind kind, bool constrained) {
        var domain = VolumeDomain(
            kind: kind,
            medium: ((kind == WorldNavigationKind.Medium)
            ? "water"
            : null),
            name: "habitat"
        );
        var definition = NavigationDocument(
            domain,
            withMedium: (kind == WorldNavigationKind.Medium)
        );
        var kit = definition.Kits[0];
        var flock = new WorldFlockProfile(
            20,
            1,
            4,
            3,
            60,
            WorldFlockSpace.Volume,
            0,
            0,
            1,
            0,
            0,
            0,
            180,
            false,
            (constrained
            ? "habitat"
            : null)
        );

        return definition with {
            BodyMotionProgramsRaw = [.. definition.BodyMotionPrograms, new BodyMotionProgram(
                "flock",
                BodyMotionProgram.CurrentVersion,
                BodyProgramKind.Producer,
                [BodyMotionOp.ProduceFlockIntent]
            )],
            KitRowsRaw = [kit with { ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: kit.Producers) {
                ["flock"] = new(
                new Dictionary<string, float>(),
                new Dictionary<string, string>(),
                flock
            ),
            } }],
        };
    }

    [InlineData(WorldNavigationKind.Volume)]
    [InlineData(WorldNavigationKind.Medium)]
    [Theory]
    public void AuthoredMovementDomainConstrainsActualOffRouteFlockMotion(WorldNavigationKind kind) {
        foreach (var constrained in new[] { true, false }) {
            var definition = FlockMovementDocument(
                constrained: constrained,
                kind: kind
            );

            Assert.True(
                condition: WorldDefinitionValidator.TryValidateLocally(
                    definition: definition,
                    reason: out var reason
                ),
                userMessage: reason
            );
            using var fixture = Fixtures.FreshServer(definition);

            Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
                WorldPrincipal.Seat(slot: 0),
                0,
                null,
                WorldProtocol.WireProtocolKey
            )).Accepted);
            var body = fixture.Server.Body(index: 0)!;

            body.Pose(
                new FixedVector3(
                    X: FixedQ4816.FromDouble(value: 5.1),
                    Y: FixedQ4816.Zero,
                    Z: FixedQ4816.Zero
                ),
                FixedQ4816.Zero,
                FixedQ4816.Zero,
                FixedQ4816.Zero
            );
            body.SetIntentSource(source: IntentSource.Producer(name: "flock"));
            Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
                WorldPrincipal.Seat(slot: 1),
                1,
                null,
                WorldProtocol.WireProtocolKey
            )).Accepted);
            var other = fixture.Server.Body(index: 1)!;

            other.Pose(
                new FixedVector3(
                    X: FixedQ4816.FromInteger(value: 8),
                    Y: FixedQ4816.Zero,
                    Z: FixedQ4816.Zero
                ),
                FixedQ4816.Zero,
                FixedQ4816.Zero,
                FixedQ4816.Zero
            );
            other.SetIntentSource(source: IntentSource.Idle);
            var refusals = 0;

            for (var tick = 0; (tick < 180); tick++) {
                fixture.Step();
                refusals += fixture.Server.Population.FlockStatistics.MotionRefusals;
                if (
                    constrained &&
                    (kind == WorldNavigationKind.Medium)
                ) {
                    Assert.True(condition: fixture.Server.Population.Fields!.IsInsideMedium(
                        0,
                        body.FixedPosition,
                        FixedQ4816.FromDouble(value: .25)
                    ));
                }
            }
            Assert.Equal(
                actual: (refusals > 0),
                expected: constrained
            );
            Assert.Equal(
                constrained,
                (body.FixedPosition.X < FixedQ4816.FromDouble(value: 5.5))
            );
            if (constrained) {
                // The authored constraint leaves with its producer; it is not a permanent invisible wall.
                body.SetIntentSource(source: IntentSource.Live);
                var strafe = definition.Channels.ToList().FindIndex(match: channel => (channel.Role == ChannelRole.MoveStrafe));

                for (var tick = 0; (tick < 60); tick++) {
                    body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
                        ordinal: strafe,
                        value: FixedQ4816.One
                    ));
                    fixture.Step();
                }
                Assert.True(condition: (body.FixedPosition.X > FixedQ4816.FromDouble(value: 5.5)));
                Assert.Equal(
                    0,
                    fixture.Server.Population.FlockStatistics.MotionChecks
                );
            }
        }
    }
    [Fact]
    public void MovementDomainMustExistAndEncloseOffsetColliderVolumes() {
        var definition = FlockMovementDocument(
            WorldNavigationKind.Volume,
            constrained: true
        );
        var kit = definition.Kits[0];

        foreach (var collider in new WorldCollider[] { new WorldCollider.Sphere(Radius: .2f),
            new WorldCollider.Capsule(
            Endpoint: new DocumentVector3(
                x: .3f,
                y: 0,
                z: 0
            ),
            Radius: .1f
        ),
            new WorldCollider.Box(
            HalfExtents: new DocumentVector3(
                x: .2f,
                y: .1f,
                z: .2f
            ),
            Rotation: new DocumentQuaternion(
                w: 1,
                x: 0,
                y: 0,
                z: 0
            )
        ) }) {
            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition with { KitRowsRaw = [kit with { Collider = collider }] },
                reason: out var reason
            ));
            Assert.Contains(
                actualString: reason,
                expectedSubstring: "agentRadius must enclose"
            );
        }
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition with { KitRowsRaw = [kit with { Collider = new WorldCollider.Sphere(Radius: .1f) }] },
                reason: out var validReason
            ),
            userMessage: validReason
        );
        var parameters = kit.Producers["flock"];
        var missing = kit with {
            ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: kit.Producers) {
                ["flock"] = parameters with { Flock = parameters.Flock! with { MovementDomain = "missing" } },
            },
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition with { KitRowsRaw = [missing] },
            reason: out var missingReason
        ));
        Assert.Contains(
            actualString: missingReason,
            expectedSubstring: "movementDomain requires"
        );
    }
    [Fact]
    public void LeavingAProducerClearsItsRuleVisibleRouteState() {
        using var fixture = Fixtures.FreshServer(definition: NavigationDocument(domain: VolumeDomain()));
        var body = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 4),
                Y: FixedQ4816.FromInteger(value: 2),
                Z: FixedQ4816.Zero
            )
        );

        fixture.Step();
        Assert.Equal(
            expected: 1L,
            actual: fixture.Server.Population.NavigationFact(
                facet: "hasPath",
                index: 0
            )
        );

        body.SetIntentSource(source: IntentSource.Live);
        fixture.Step();

        Assert.Equal(
            expected: 0L,
            actual: fixture.Server.Population.NavigationFact(
                facet: "hasPath",
                index: 0
            )
        );
        Assert.Contains(
            expectedSubstring: "navigation=notarget",
            actualString: fixture.Server.Population.DescribeTargets(bodyIndex: 0),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void MalformedRouteCheckpointRefusesBeforeMutatingTheServer() {
        using var fixture = Fixtures.FreshServer(definition: NavigationDocument(domain: VolumeDomain()));

        _ = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 4),
                Y: FixedQ4816.FromInteger(value: 2),
                Z: FixedQ4816.Zero
            )
        );
        fixture.Step();
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var captured,
                hostRow: EmptyHostRow(),
                reason: out var reason
            ),
            userMessage: reason
        );
        var entry = captured!.Population.Entries.Single(predicate: row => (row.Index == 0));
        var route = Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(@object: entry.Navigation);
        var malformedEntry = entry with { Navigation = route with { Path = [int.MaxValue] } };
        var malformed = captured with {
            Population = captured.Population with {
                Entries = captured.Population.Entries.Select(selector: row => ((row.Index == entry.Index)
            ? malformedEntry
            : row)).ToArray(),
            },
        };
        var before = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 1
        );

        var exception = Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: malformed));

        Assert.Contains(
            expectedSubstring: "path node",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
        Assert.Equal(
            expected: before,
            actual: WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 1
            )
        );
    }
    [Fact]
    public void NavigationStatusIsACompiledRuleFactRatherThanASecondScriptingSurface() {
        var observed = CellName.Parse(candidate: "observed-route");
        var definition = NavigationDocument(domain: VolumeDomain()) with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: observed,
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0L)
                    )]
            )]),
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "observe-route"),
                Gate: new ActionPredicate.CompareState(
                    State: "$nav:body:0:hasPath",
                    Comparison: ActionStateComparison.Equal,
                    Value: 1m
                ),
                Effects: [new ActionEffect.SetState(
                        State: observed.Value,
                        Value: 1m
                    )],
                Mode: ActionTriggerMode.Edge
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        _ = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 4),
                Y: FixedQ4816.FromInteger(value: 2),
                Z: FixedQ4816.Zero
            )
        );
        fixture.Step();

        Assert.Equal(
            expected: 1L,
            actual: fixture.Server.Definition.State.Single(predicate: row => (row.Name == observed)).Cells![0].Value.Raw
        );
    }
    // The island authors a dozen navigation domains; a construction that eagerly sweeps every one of their
    // occupancy and edge bakes against the shipped solid field costs a full minute (`puck bench world`'s own
    // construction row). Lazy baking measures under two seconds on an otherwise idle machine; the bound below sits
    // an order of magnitude under the eager cost so a reintroduced eager bake fails it unmistakably, while staying
    // loose enough to absorb ordinary machine contention rather than flake on it.
    [Fact]
    public void TheIslandsNavigationDomainsConstructWithoutSweepingTheSolidFieldUpFront() {
        var stopwatch = Stopwatch.StartNew();
        using var fixture = Fixtures.FreshServer(definition: AuthoredGameFixtures.Nexus);

        stopwatch.Stop();

        Assert.True(
            condition: (stopwatch.Elapsed < TimeSpan.FromSeconds(value: 20)),
            userMessage: $"server construction against the shipped world took {stopwatch.Elapsed.TotalSeconds:F1} s"
        );
    }
}
