using Puck.Commands;
using System.Globalization;

using Xunit;

using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the two non-drive shaping mechanisms <c>ShapeVelocity</c> reads from a kit's <c>shaping</c>
/// table — the whole-vector response law across gated rows, and a named <c>dynamics</c>-row second-order follower.
/// Each trace law drives one body 240 ticks and pins the fixed-point result raw value for raw value; each
/// discriminating control perturbs a single shaping facet and requires the trace to move.
/// </summary>
public sealed class ShapingRowLawTests {
    private const int ForwardOrdinal = 0;
    private const int JumpOrdinal = ChannelLimits.MaxAxes;

    private static readonly string[] DynamicsTrace240 = [
        "0000000000000000 0000000000000000 fffffffffffffffe 0000000000000000 fffffffffffffde6",
        "0000000000000000 0000000000000000 fffffffffffffff6 0000000000000000 fffffffffffff7f5",
        "0000000000000000 0000000000000000 ffffffffffffffe3 0000000000000000 ffffffffffffeeaa",
    ];
    // Per tick: position x/y/z, planar velocity x/z, vertical velocity in raw FixedQ4816 hex. The colliderless
    // body starts airborne. The recent Airborne row governs the jump tick, and Rising takes precedence next tick.
    private static readonly string[] ResponseTrace240 = [
        "0000000000000000 0000000000000975 fffffffffffffff3 0000000000000000 fffffffffffff334 000000000008ddde",
        "0000000000000000 00000000000012c5 ffffffffffffffb8 0000000000000000 ffffffffffffc889 000000000008bbbc",
        "0000000000000000 0000000000001bf2 ffffffffffffff4f 0000000000000000 ffffffffffff9dde 000000000008999a",
        "0000000000000000 00000000000024fa fffffffffffffeb9 0000000000000000 ffffffffffff7334 0000000000087778",
        "0000000000000000 0000000000002ddd fffffffffffffdf5 0000000000000000 ffffffffffff4889 0000000000085556",
        "0000000000000000 000000000000369d fffffffffffffd04 0000000000000000 ffffffffffff1dde 0000000000083334",
        "0000000000000000 0000000000003f37 fffffffffffffbe5 0000000000000000 fffffffffffef334 0000000000081112",
        "0000000000000000 00000000000047ae fffffffffffffa99 0000000000000000 fffffffffffec889 000000000007eeef",
    ];

    private static WorldDefinition BuildDynamicsDocument(float frequency = 2.5f) {
        var channels = new WorldChannel[] {
            new(
            Name: "forward",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.MoveAdvance
        ),
            new(
            Name: "strafe",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.MoveStrafe
        ),
            new(
            Name: "turn",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.Turn
        ),
        };
        var walker = new BodyMotionProgram(
            Name: "walker",
            Version: "puck.body.program.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputePlanarTargetVelocity,
                BodyMotionOp.ShapeVelocity,
                BodyMotionOp.SnapYawToPlanarIntent,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        var roam = new BodyMotionProgram(
            Name: "roam",
            Version: "puck.body.program.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.ProduceSteeringIntent]
        );
        var kit = new WorldKit(
            Name: "glider-test",
            BodyMotionProgram: "walker",
            Motion: new WorldMotion(
                Speed: new WorldSpeed(Value: 4f),
                Turn: new WorldTurn(Rate: 2.5f),
                Holds: [
                    new WorldHold(
                        Bond: BodyHoldBond.Free,
                        Envelope: new WorldHoldEnvelope(SinkSpeed: 20f),
                        Gravity: new WorldHoldGravity(
                            Fall: 23f,
                            Rise: 14f
                        ),
                        Hold: BodyHoldKind.Gravity,
                        Name: "air"
                    ),
                ],
                Shaping: [
                    new WorldShaping(Dynamics: "stride"),
                ]
            ),
            ProducersRaw: new Dictionary<string, BodyProgramParameters> {
                ["roam"] = Fixtures.TravelerRoamParameters,
            },
            Collider: null
        );

        return Fixtures.BuildDocumentAtRate(rateHz: Fixtures.RecordedTraceRateHz) with {
            ChannelsRaw = channels,
            DynamicsRaw = [.. Fixtures.StandardDynamics, new DynamicsRow(
                Damping: 1f,
                Frequency: frequency,
                Name: "stride",
                Response: 0f
            )],
            BodyMotionProgramsRaw = [walker, roam],
            KitRowsRaw = [kit],
            DefaultSeatKitRaw = "glider-test",
        };
    }
    private static WorldDefinition BuildResponseDocument(float risingEngage = 40f, bool risingFirst = true) {
        var channels = new WorldChannel[] {
            new(
            Name: "forward",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.MoveAdvance
        ),
            new(
            Name: "strafe",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.MoveStrafe
        ),
            new(
            Name: "turn",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.Turn
        ),
            new(
            Name: "jump",
            Shape: ChannelShape.Binary,
            Composition: true
        ),
        };
        var walker = new BodyMotionProgram(
            Name: "walker",
            Version: "puck.body.program.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputePlanarTargetVelocity,
                BodyMotionOp.ShapeVelocity,
                BodyMotionOp.SnapYawToPlanarIntent,
                BodyMotionOp.RunActionTriggers,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        var roam = new BodyMotionProgram(
            Name: "roam",
            Version: "puck.body.program.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.ProduceSteeringIntent]
        );
        var rising = new WorldShaping(
            When: new WorldPredicate.Now(Fact: ActionFact.Rising),
            Along: new WorldShapingAlong(Engage: risingEngage, Release: 46f)
        );
        var airborne = new WorldShaping(
            When: new WorldPredicate.Recently(Fact: ActionFact.Airborne, WindowSeconds: 0.09f),
            Along: new WorldShapingAlong(Engage: 12f, Release: 12f)
        );
        var kit = new WorldKit(
            Name: "walker-test",
            BodyMotionProgram: "walker",
            Motion: new WorldMotion(
                Speed: new WorldSpeed(Value: 4f),
                Turn: new WorldTurn(Rate: 2.5f),
                Holds: [
                    new WorldHold(
                        Bond: BodyHoldBond.Free,
                        Envelope: new WorldHoldEnvelope(SinkSpeed: 24f),
                        Gravity: new WorldHoldGravity(
                            Fall: 32f,
                            Rise: 32f
                        ),
                        Hold: BodyHoldKind.Gravity,
                        Name: "air"
                    ),
                ],
                Shaping: (risingFirst ? [rising, airborne] : [airborne, rising])
            ),
            ActionsRaw: new Dictionary<string, ActionSpec> {
                ["jump"] = new ActionSpec(OnPress: new ActionTrigger(Effects: [new WorldEffect.SetVerticalVelocity(Velocity: 9m)])),
            },
            ProducersRaw: new Dictionary<string, BodyProgramParameters> {
                ["roam"] = Fixtures.TravelerRoamParameters,
            },
            Collider: null
        );

        return Fixtures.BuildDocumentAtRate(rateHz: Fixtures.RecordedTraceRateHz) with {
            ChannelsRaw = channels,
            BodyMotionProgramsRaw = [walker, roam],
            KitRowsRaw = [kit],
            DefaultSeatKitRaw = "walker-test",
        };
    }
    private static string[] DynamicsTrace(WorldDefinition definition, int ticks) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var lines = new string[ticks];

        for (var tick = 0; (tick < ticks); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
                ordinal: ForwardOrdinal,
                value: FixedQ4816.One
            ));
            fixture.Step();

            var state = body.CaptureTransferState();
            var position = body.FixedPosition;

            lines[tick] = string.Join(
                separator: ' ',
                value: [
                    Hex(value: position.X),
                    Hex(value: position.Y),
                    Hex(value: position.Z),
                    Hex(value: state.PlanarVelocity.X),
                    Hex(value: state.PlanarVelocity.Z),
                ]
            );
        }

        return lines;
    }
    private static string Hex(FixedQ4816 value) => value.Value.ToString(
        format: "x16",
        provider: CultureInfo.InvariantCulture
    );
    private static string[] ResponseTrace(WorldDefinition definition, int ticks) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var lines = new string[ticks];

        for (var tick = 0; (tick < ticks); tick++) {
            var intent = default(PlayerIntent).WithChannel(
                ordinal: ForwardOrdinal,
                value: FixedQ4816.One
            );

            if (tick == 0) {
                intent = intent.WithChannel(
                    ordinal: JumpOrdinal,
                    value: FixedQ4816.One
                );
            }

            body.SubmitIntent(intent: intent);
            fixture.Step();

            var state = body.CaptureTransferState();
            var position = body.FixedPosition;

            lines[tick] = string.Join(
                separator: ' ',
                value: [
                    Hex(value: position.X),
                    Hex(value: position.Y),
                    Hex(value: position.Z),
                    Hex(value: state.PlanarVelocity.X),
                    Hex(value: state.PlanarVelocity.Z),
                    Hex(value: state.VerticalVelocity),
                ]
            );
        }

        return lines;
    }

    [Fact]
    public void AHeldRowGovernsOnlyWhileItsChannelReadsHeld_WithTheSameWorldWithoutItAsControl() {
        var channels = new WorldChannel[] {
            new(
            Name: "forward",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.MoveAdvance
        ),
            new(
            Name: "strafe",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.MoveStrafe
        ),
            new(
            Name: "turn",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.Turn
        ),
            new(
            Name: "drift",
            Shape: ChannelShape.Binary,
            Composition: true
        ),
        };
        var program = new BodyMotionProgram(
            Name: "held-row",
            Version: "puck.body.program.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputePlanarTargetVelocity,
                BodyMotionOp.ShapeVelocity,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        var roam = new BodyMotionProgram(
            Name: "roam",
            Version: "puck.body.program.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.ProduceSteeringIntent]
        );

        WorldDefinition Build(bool withHeldRow) {
            var shaping = new List<WorldShaping>();

            if (withHeldRow) {
                shaping.Add(item: new WorldShaping(
                    When: new WorldPredicate.Held(Channel: "drift"),
                    Along: new WorldShapingAlong(
                        Engage: 2f,
                        Release: 2f
                    )
                ));
            }

            shaping.Add(item: new WorldShaping(Along: new WorldShapingAlong(
                Engage: 40f,
                Release: 40f
            )));

            var kit = new WorldKit(
                Name: "held-row-test",
                BodyMotionProgram: "held-row",
                Motion: new WorldMotion(
                    Speed: new WorldSpeed(Value: 4f),
                    Turn: new WorldTurn(Rate: 2.5f),
                    Holds: [
                        new WorldHold(
                            Bond: BodyHoldBond.Free,
                            Envelope: new WorldHoldEnvelope(SinkSpeed: 20f),
                            Gravity: new WorldHoldGravity(
                                Fall: 23f,
                                Rise: 14f
                            ),
                            Hold: BodyHoldKind.Gravity,
                            Name: "air"
                        ),
                    ],
                    Shaping: shaping
                ),
                ProducersRaw: new Dictionary<string, BodyProgramParameters> { ["roam"] = Fixtures.TravelerRoamParameters },
                Collider: null
            );

            return Fixtures.BuildDocumentAtRate(rateHz: Fixtures.RecordedTraceRateHz) with {
                ChannelsRaw = channels,
                BodyMotionProgramsRaw = [program, roam],
                KitRowsRaw = [kit],
                DefaultSeatKitRaw = "held-row-test",
            };
        }

        FixedVector3 RunHeld(bool withHeldRow, bool holdDrift) {
            using var fixture = Fixtures.FreshServer(definition: Build(withHeldRow: withHeldRow));
            var actor = Principal.Seat(slot: 0);

            Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
                Principal: actor,
                Slot: actor.Index,
                IdentityName: null,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);

            var body = fixture.Server.Body(index: actor.Index)!;

            for (var tick = 0; (tick < 1); tick++) {
                var intent = default(PlayerIntent).WithChannel(
                    ordinal: 0,
                    value: FixedQ4816.One
                );

                if (holdDrift) {
                    intent = intent.WithChannel(
                        ordinal: ChannelLimits.MaxAxes,
                        value: FixedQ4816.One
                    );
                }

                body.SubmitIntent(intent: intent);
                fixture.Step();
            }

            return body.CaptureTransferState().PlanarVelocity;
        }

        // With the held row present and the channel down, the slow (Engage 2) row governs — nowhere near the fast
        // row's own convergence. Released, the fast (Engage 40) unconditional row governs identically to the
        // control world that never authors the held row at all.
        var heldAndDown = RunHeld(
            holdDrift: true,
            withHeldRow: true
        );
        var heldAndUp = RunHeld(
            holdDrift: false,
            withHeldRow: true
        );
        var control = RunHeld(
            holdDrift: false,
            withHeldRow: false
        );

        Assert.NotEqual(
            actual: heldAndDown,
            expected: heldAndUp
        );
        Assert.Equal(
            actual: heldAndUp,
            expected: control
        );
    }
    [Fact]
    public void AbsentResponseRatesSnapExactlyOnEngageAndRelease() {
        var document = BuildResponseDocument();
        var kit = document.Kits[0];
        var instant = document with {
            KitRowsRaw = [kit with { Motion = kit.Motion with { Shaping = [new WorldShaping(Along: new WorldShapingAlong())] } }],
        };
        using var fixture = Fixtures.FreshServer(definition: instant);
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
            ordinal: ForwardOrdinal,
            value: FixedQ4816.One
        ));
        fixture.Step();

        Assert.Equal(
            expected: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.Zero,
                Z: -FixedQ4816.FromDouble(value: 4)
            ),
            actual: body.CaptureTransferState().PlanarVelocity
        );

        body.SubmitIntent(intent: default);
        fixture.Step();

        Assert.Equal(
            expected: FixedVector3.Zero,
            actual: body.CaptureTransferState().PlanarVelocity
        );
    }
    [Fact]
    public void FiniteResponseRateMatchesTheIndependentDistanceOverTimeLaw() {
        var document = BuildResponseDocument();
        var kit = document.Kits[0];
        var finite = document with {
            KitRowsRaw = [kit with { Motion = kit.Motion with { Shaping = [new WorldShaping(Along: new WorldShapingAlong(
                    Engage: 8f,
                    Release: 8f
                ))] } }],
        };
        using var fixture = Fixtures.FreshServer(definition: finite);
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        for (var tick = 0; (tick < 30); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
                ordinal: ForwardOrdinal,
                value: FixedQ4816.One
            ));
            fixture.Step();
        }

        Assert.Equal(
            expected: -FixedQ4816.One,
            actual: body.CaptureTransferState().PlanarVelocity.Z
        );

        for (var tick = 0; (tick < 15); tick++) {
            body.SubmitIntent(intent: default);
            fixture.Step();
        }

        Assert.Equal(
            expected: -FixedQ4816.FromDouble(value: 0.5),
            actual: body.CaptureTransferState().PlanarVelocity.Z
        );
    }
    [Fact]
    public void TheDynamicsRowMatchesTheIndependentCompiledFollowerForEachLane() {
        var definition = BuildDynamicsDocument();
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var step = SecondOrderDynamics.Create(
            dampingRatio: FixedQ4816.One,
            frequencyHz: FixedQ4816.FromDouble(value: 2.5),
            initialResponse: FixedQ4816.Zero
        ).Compile(
            stepTicks: (FixedTickConversion.TicksPerSecond / 240UL),
            ticksPerSecond: FixedTickConversion.TicksPerSecond
        );
        var target = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: -FixedQ4816.FromDouble(value: 4)
        );
        var expected = SecondOrderState3.AtRest(position: FixedVector3.Zero);

        for (var tick = 0; (tick < 2); tick++) {
            expected = step.Step(
                state: expected,
                target: target,
                targetVelocity: FixedVector3.Zero
            );
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
                ordinal: ForwardOrdinal,
                value: FixedQ4816.One
            ));
            fixture.Step();

            var actual = body.CaptureTransferState();

            Assert.Equal(
                expected: expected.X.PositionRaw,
                actual: actual.PlanarFollowerPositionRawX
            );
            Assert.Equal(
                expected: expected.Y.PositionRaw,
                actual: actual.PlanarFollowerPositionRawY
            );
            Assert.Equal(
                expected: expected.Z.PositionRaw,
                actual: actual.PlanarFollowerPositionRawZ
            );
            Assert.Equal(
                expected: expected.X.VelocityRaw,
                actual: actual.PlanarFollowerVelocityRawX
            );
            Assert.Equal(
                expected: expected.Y.VelocityRaw,
                actual: actual.PlanarFollowerVelocityRawY
            );
            Assert.Equal(
                expected: expected.Z.VelocityRaw,
                actual: actual.PlanarFollowerVelocityRawZ
            );
        }
    }

    // The recorded prefix reproduces exactly, and the one perturbed row moves at least one tick of it — a trace no
    // perturbation can move pins nothing about the row.
    private static void AssertReproducesWherePerturbationDiverges(string[] recorded, Func<int, string[]> trace, Func<int, string[]> perturbed, string because) {
        var full = trace(arg: 240);

        Assert.Equal(
            actual: full[..recorded.Length],
            expected: recorded
        );

        var moved = perturbed(arg: recorded.Length).Where(predicate: (line, tick) => !string.Equals(
            a: full[tick],
            b: line,
            comparisonType: StringComparison.Ordinal
        )).Count();

        Assert.True(
            condition: (moved > 0),
            userMessage: because
        );
    }

    [Fact]
    public void TheDynamicsRowReproducesTheRecordedTrace_WhereChangingItsFrequencyDiverges() => AssertReproducesWherePerturbationDiverges(
        because: "a faster dynamics row must move the trace, or the row pins nothing about its own rate",
        perturbed: static ticks => DynamicsTrace(
            definition: BuildDynamicsDocument(frequency: 5f),
            ticks: ticks
        ),
        recorded: DynamicsTrace240,
        trace: static ticks => DynamicsTrace(
            definition: BuildDynamicsDocument(),
            ticks: ticks
        )
    );
    [Fact]
    public void TheResponseTableGovernsByRowOrder_ReproducesTheRecordedTrace_WhereChangingTheRisingRowDiverges() {
        var reordered = ResponseTrace(definition: BuildResponseDocument(risingFirst: false), ticks: 2);
        Assert.Equal(expected: ResponseTrace240[0], actual: reordered[0]);
        Assert.NotEqual(expected: ResponseTrace240[1], actual: reordered[1]);
        AssertReproducesWherePerturbationDiverges(
            because: "changing the now-Rising row's engage rate must move the trace",
            perturbed: static ticks => ResponseTrace(
                definition: BuildResponseDocument(risingEngage: 60f),
                ticks: ticks
            ),
            recorded: ResponseTrace240,
            trace: static ticks => ResponseTrace(
                definition: BuildResponseDocument(),
                ticks: ticks
            )
        );
    }
}
