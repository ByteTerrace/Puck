using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>The rig's doors onto the world: a scalar facet bound to a numeric state cell resolves through the
/// document walk, a driver's state-cell signal reads the sim's own number at a tick, and a <c>curve:</c> waveform and a
/// driver's <c>cadenceBy</c>/<c>weightBy</c> sample declared curves rows — with the world validator refusing the
/// bindings it cannot resolve.</summary>
public sealed class CreationCharacterLawTests {
    private const float Tolerance = 1e-3f;

    private static DocumentScalar Bound(string reference) =>
        System.Text.Json.JsonSerializer.Deserialize<DocumentScalar>(
            json: $"\"{reference}\"",
            options: DocumentJsonOptions.Shared
        )!;
    private static StateCell Cell(string key, double value) => new(
        Key: CellName.Parse(candidate: key),
        Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: value).Value)
    );
    private static WorldStateRow FixedRow(string name, double value) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Fixed,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: value).Value)
            )]
    );
    // z = f(x) drawn left to right over [0, span] as the curvature-first knots the spline compiles, a knot every half
    // unit: yaw = atan2(dz/dx, 1), curvature = the yaw's rate along the arc.
    private static WorldCurveRow Drawn(string name, Func<double, double> f, int span) {
        double D(double x) => ((f(arg: (x + 1e-4)) - f(arg: (x - 1e-4))) / 2e-4);
        double D2(double x) => ((D(x: (x + 1e-4)) - D(x: (x - 1e-4))) / 2e-4);

        var knots = new List<WorldCurveKnot>();

        for (var i = 0; (i <= (span * 2)); i++) {
            var x = (i / 2.0);
            var slope = D(x: x);

            knots.Add(item: new WorldCurveKnot(
                Position: new DocumentVector3(
                    x: (float)x,
                    y: 0f,
                    z: (float)f(arg: x)
                ),
                TangentYaw: (float)Math.Atan2(
                    x: 1.0,
                    y: slope
                ),
                Curvature: (float)(D2(x: x) / Math.Pow(
                    x: (1.0 + (slope * slope)),
                    y: 1.5
                ))
            ));
        }

        return new WorldCurveRow(
            Name: name,
            Knots: knots
        );
    }
    // A hump (z = sin(πx/4), the waveform an artist would draw), over x in [0, 4] so the crest's curvature stays
    // inside the spline's authoring bound.
    private static WorldCurveRow Hump() => Drawn(
        f: static x => Math.Sin(a: (Math.PI * (x / 4.0))),
        name: "hump",
        span: 4
    );
    private static string Refusal(WorldDefinition definition) => (WorldDefinitionValidator.TryValidateLocally(
        definition: definition,
        reason: out var reason
    )
        ? string.Empty
        : reason
    );

    /// <summary>A <c>curve:</c> waveform samples the world's row: at half a turn the hump reads its crest, at zero
    /// its start; the control is sine on the same arguments, which reads zero at half a turn.</summary>
    [Fact]
    public void ACurveWaveSamplesTheWorldsCurveRow() {
        var creation = CreationFixtures.Rig(
            drivers: [new CreationDriverDocument(
                    Name: "stride",
                    Signal: CreationDriverDocument.SignalPlanarTravel,
                    Cadence: 1f,
                    When: ["always"]
                )],
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(
                Driver: "stride",
                Pivot: Vector3.Zero,
                Axis: Vector3.UnitZ,
                Amplitude: 1f,
                Wave: "curve:hump"
            ))
        );
        var world = CreationFixtures.RigWorld(creation: creation,
            curves: [Hump()]
        );

        Assert.True(
            condition: (Refusal(definition: world).Length == 0),
            userMessage: Refusal(definition: world)
        );

        var crest = WorldGaitDrivers.Wave(
            argument: MathF.PI,
            definition: world,
            wave: "curve:hump"
        );
        var start = WorldGaitDrivers.Wave(
            argument: 0f,
            definition: world,
            wave: "curve:hump"
        );

        Assert.InRange(
            actual: crest,
            high: 1.05f,
            low: 0.85f
        );
        Assert.InRange(
            actual: start,
            high: Tolerance,
            low: -Tolerance
        );
        Assert.InRange(
            actual: WorldGaitDrivers.Wave(
                argument: MathF.PI,
                definition: world,
                wave: CreationWave.Sine
            ),
            low: -Tolerance,
            high: Tolerance
        );
    }
    /// <summary>A speed-adaptive gait: a stride whose radians per metre are a curve of a pace driver's speed, and a
    /// run set riding that stride's phase at twice its rate, weighted by a second curve of the pace. At 2 m/s the
    /// rate curve reads 2 and the fade curve 0.25; the control is the same frames advanced with no definition, where
    /// both curves leave their drivers unscaled. The validator and canonicalizer refuse each binding they cannot
    /// resolve, by name.</summary>
    [Fact]
    public void DriverCurvesScaleTheCadenceAndWeightAndADriverSignalSharesThePhase() {
        CreationDriverDocument[] Drivers(string runSignal = "driver.stride", string? runDynamics = null, string fadeCurve = "fade") => [
            new CreationDriverDocument(Name: "pace", Signal: CreationDriverDocument.SignalSpeed, Cadence: 1f, When: ["always"]),
            new CreationDriverDocument(Name: "stride", Signal: CreationDriverDocument.SignalPlanarTravel, Cadence: 1f, When: ["always"], CadenceBy: new CreationDriverCurveDocument(Driver: "pace", Curve: "rate")),
            new CreationDriverDocument(Name: "run", Signal: runSignal, Cadence: 2f, When: ["always"], BlendInSeconds: 0f, Dynamics: runDynamics, WeightBy: new CreationDriverCurveDocument(Driver: "pace", Curve: fadeCurve)),
        ];
        CreationDocument Creation(CreationDriverDocument[] drivers) => CreationFixtures.Rig(
            drivers: drivers,
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(
                Driver: "run",
                Pivot: Vector3.Zero,
                Axis: Vector3.UnitZ,
                Amplitude: 1f
            ))
        );

        var rate = Drawn(name: "rate", f: static x => (1.0 + ((x * x) / 8.0)), span: 4);
        var fade = Drawn(name: "fade", f: static x => ((x * x) / 64.0), span: 8);
        var world = CreationFixtures.RigWorld(creation: Creation(drivers: Drivers()), curves: [rate, fade]);

        Assert.Equal(expected: string.Empty, actual: Refusal(definition: world));
        Assert.Contains(
            expectedSubstring: "cadenceBy.curve 'rate' names no declared curves row",
            actualString: Refusal(definition: CreationFixtures.RigWorld(creation: Creation(drivers: Drivers())))
        );
        Assert.Contains(
            expectedSubstring: "weightBy.curve 'fade' is not drawn left to right",
            actualString: Refusal(definition: CreationFixtures.RigWorld(
                creation: Creation(drivers: Drivers()),
                curves: [rate, new WorldCurveRow(Name: "fade", Knots: [.. fade.Knots.Reverse()])]
            ))
        );
        CreationFixtures.AssertRefusesNaming(
            document: Creation(drivers: Drivers(runSignal: "driver.later")),
            needle: "names no driver 'later' declared before this one"
        );
        CreationFixtures.AssertRefusesNaming(
            document: Creation(drivers: Drivers(runDynamics: "body-turn")),
            needle: "dynamics cannot follow the signal 'driver.stride'"
        );
        CreationFixtures.AssertRefusesNaming(
            document: Creation(drivers: Drivers(fadeCurve: "")),
            needle: "curve is empty"
        );

        Assert.InRange(actual: WorldGaitDrivers.CurveHeight(row: rate, x: -1f), low: (1f - Tolerance), high: (1f + Tolerance));
        Assert.InRange(actual: WorldGaitDrivers.CurveHeight(row: rate, x: 9f), low: (3f - Tolerance), high: (3f + Tolerance));

        // A driver signal naming no earlier driver reads a zero phase; the canonicalizer refuses it before a frame.
        CreationDriverDocument[] advanced = [.. Drivers(), new CreationDriverDocument(Name: "ghost", Signal: "driver.nobody", Cadence: 1f, When: ["always"])];

        (float[] Phases, float[] Weights) Run(WorldDefinition? definition) {
            var phases = new float[CreationDocument.MaxDrivers];
            var weights = new float[CreationDocument.MaxDrivers];
            var address = new Protocol.WorldEntityAddress(Authority: "a", Generation: 1, Index: 0);
            var last = Vector3.Zero;
            var lastRotation = Quaternion.Identity;
            var seeded = false;
            var lastAddress = default(Protocol.WorldEntityAddress);
            var speed = 0f;

            for (var frame = 0; (frame <= 30); frame++) {
                WorldGaitDrivers.Advance(
                    drivers: advanced,
                    phases: phases,
                    weights: weights,
                    deltaSeconds: (1f / 60f),
                    facts: Physics.Motion.BodyFacts.Grounded,
                    position: new Vector3(x: (frame * (2f / 60f)), y: 0f, z: 0f),
                    orientation: Quaternion.Identity,
                    lastPosition: ref last,
                    lastOrientation: ref lastRotation,
                    seeded: ref seeded,
                    lastAddress: ref lastAddress,
                    easedSpeed: ref speed,
                    address: address,
                    definition: definition
                );
            }

            return (phases, weights);
        }

        var (curved, curvedWeights) = Run(definition: world);
        var (plain, plainWeights) = Run(definition: null);

        Assert.Equal(expected: 2f, actual: curved[0], precision: 3);
        Assert.Equal(expected: 1.5f, actual: curved[1], precision: 3);
        Assert.Equal(expected: (2f * curved[1]), actual: curved[2], precision: 4);
        Assert.Equal(expected: 0.0625f, actual: curvedWeights[2], precision: 3);
        Assert.Equal(expected: 0f, actual: curved[3]);
        Assert.Equal(expected: 1f, actual: plain[1], precision: 3);
        Assert.Equal(expected: 1f, actual: plainWeights[2], precision: 4);
    }
    /// <summary>A driver cadence and a swing amplitude bound to fixed cells resolve through the document walk to
    /// the cells' numbers; the control is a literal beside them, untouched by the walk.</summary>
    [Fact]
    public void AScalarReferenceResolvesFromANumericStateCell() {
        var creation = CreationFixtures.Rig(
            drivers: [new CreationDriverDocument(
                    Name: "stride",
                    Signal: CreationDriverDocument.SignalPlanarTravel,
                    Cadence: Bound(reference: "state.gait.cadence"),
                    When: ["always"]
                )],
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(
                Driver: "stride",
                Pivot: Vector3.Zero,
                Axis: Vector3.UnitZ,
                Amplitude: Bound(reference: "state.gait.reach"),
                Phase: 0.25f
            ))
        );
        var world = CreationFixtures.RigWorld(creation: creation,
            state: [
            new WorldStateRow(
                    Name: CellName.Parse(candidate: "gait"),
                    Kind: CellKind.Fixed,
                    Cells: [Cell(
                            key: "cadence",
                            value: 6.5
                        ), Cell(
                            key: "reach",
                            value: 0.75
                        )]
                ),
        ]
        );

        Assert.True(
            condition: WorldStateDocumentValues.TryRefresh(
                definition: world,
                reason: out var reason,
                refreshed: out var refreshed,
                rowName: "gait"
            ),
            userMessage: reason
        );

        var driver = refreshed.Creations[^1].Document.Drivers![0];
        var swing = refreshed.Creations[^1].Document.Shapes![0].Swings![0];

        Assert.Equal(
            expected: 6.5f,
            actual: driver.Cadence.Value,
            precision: 4
        );
        Assert.Equal(
            expected: 0.75f,
            actual: swing.Amplitude.Value,
            precision: 4
        );
        Assert.Equal(
            expected: 0.25f,
            actual: swing.Phase!.Value,
            precision: 4
        );
        Assert.NotNull(@object: driver.Cadence.Reference);
        Assert.Null(@object: swing.Phase.Reference);
    }
    /// <summary>A state-cell signal sets the phase to cadence times the cell's value at the frame's tick; the
    /// control is the same driver read against a world whose cell holds zero.</summary>
    [Fact]
    public void AStateSignalDrivesThePhaseFromTheCell() {
        var driver = new CreationDriverDocument(
            Name: "clock",
            Signal: "state.turns",
            Cadence: 2f,
            When: ["always"]
        );
        var creation = CreationFixtures.Rig(
            drivers: [driver],
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(
                Driver: "clock",
                Pivot: Vector3.Zero,
                Axis: Vector3.UnitZ,
                Amplitude: 1f
            ))
        );
        var world = CreationFixtures.RigWorld(creation: creation,
            state: [FixedRow(
                    name: "turns",
                    value: 0.25
                )]
        );
        var zero = CreationFixtures.RigWorld(creation: creation,
            state: [FixedRow(
                    name: "turns",
                    value: 0
                )]
        );

        Assert.True(
            condition: (Refusal(definition: world).Length == 0),
            userMessage: Refusal(definition: world)
        );

        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];
        var address = new Protocol.WorldEntityAddress(
            Authority: "a",
            Generation: 1,
            Index: 0
        );
        var last = Vector3.Zero;
        var lastRotation = Quaternion.Identity;
        var seeded = false;
        var lastAddress = default(Protocol.WorldEntityAddress);
        var speed = 0f;

        for (var frame = 0; (frame < 2); frame++) {
            WorldGaitDrivers.Advance(
                drivers: creation.Drivers,
                phases: phases,
                weights: weights,
                deltaSeconds: (1f / 60f),
                facts: Physics.Motion.BodyFacts.Grounded,
                position: Vector3.Zero,
                orientation: Quaternion.Identity,
                lastPosition: ref last,
                lastOrientation: ref lastRotation,
                seeded: ref seeded,
                lastAddress: ref lastAddress,
                easedSpeed: ref speed,
                address: address,
                reads: ClientFixtures.StateReads(
                    bodyIndex: address.Index,
                    definition: world,
                    tick: 10UL
                )
            );
        }
        Assert.Equal(
            expected: 0.5f,
            actual: phases[0],
            precision: 4
        );

        WorldGaitDrivers.Advance(
            drivers: creation.Drivers,
            phases: phases,
            weights: weights,
            deltaSeconds: (1f / 60f),
            facts: Physics.Motion.BodyFacts.Grounded,
            position: Vector3.Zero,
            orientation: Quaternion.Identity,
            lastPosition: ref last,
            lastOrientation: ref lastRotation,
            seeded: ref seeded,
            lastAddress: ref lastAddress,
            easedSpeed: ref speed,
            address: address,
            reads: ClientFixtures.StateReads(
                bodyIndex: address.Index,
                definition: zero,
                tick: 10UL
            )
        );
        Assert.Equal(
            expected: 0f,
            actual: phases[0],
            precision: 4
        );
    }
    /// <summary>The world validator refuses a gate token naming no body fact, by path and token; the controls are
    /// the same driver gated on a published fact and on the client's own <c>moving</c> token, both admitted.</summary>
    [Fact]
    public void TheWorldValidatorRefusesAGateTokenNamingNoBodyFact() {

        var refusal = Refusal(definition: CreationFixtures.RigWorld(creation: CreationFixtures.GatedStrideRig(token: "Floating")));

        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "drivers[0].when[0] 'Floating' names no body fact"
        );
        Assert.Equal(
            expected: string.Empty,
            actual: Refusal(definition: CreationFixtures.RigWorld(creation: CreationFixtures.GatedStrideRig(token: "Grounded")))
        );
        Assert.Equal(
            expected: string.Empty,
            actual: Refusal(definition: CreationFixtures.RigWorld(creation: CreationFixtures.GatedStrideRig(token: CreationDriverDocument.TokenMoving)))
        );
    }
    /// <summary>The world validator refuses a curve waveform naming no row and a state signal naming a text row;
    /// the control is the same document with the row declared.</summary>
    [Fact]
    public void TheWorldValidatorRefusesAnUnknownCurveOrANonNumericSignalRow() {
        var curved = CreationFixtures.Rig(
            drivers: [new CreationDriverDocument(
                    Name: "stride",
                    Signal: CreationDriverDocument.SignalPlanarTravel,
                    Cadence: 1f,
                    When: ["always"]
                )],
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(
                Driver: "stride",
                Pivot: Vector3.Zero,
                Axis: Vector3.UnitZ,
                Amplitude: 1f,
                Wave: "curve:hump"
            ))
        );

        Assert.Contains(
            expectedSubstring: "names no declared curves row",
            actualString: Refusal(definition: CreationFixtures.RigWorld(creation: curved))
        );
        Assert.Equal(
            expected: string.Empty,
            actual: Refusal(definition: CreationFixtures.RigWorld(creation: curved,
                curves: [Hump()]
            ))
        );

        var clocked = CreationFixtures.Rig(
            drivers: [new CreationDriverDocument(
                    Name: "clock",
                    Signal: "state.label",
                    Cadence: 1f,
                    When: ["always"]
                )],
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(
                Driver: "clock",
                Pivot: Vector3.Zero,
                Axis: Vector3.UnitZ,
                Amplitude: 1f
            ))
        );
        var textRow = new WorldStateRow(
            Name: CellName.Parse(candidate: "label"),
            Kind: CellKind.Text,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Text(value: "north")
                )]
        );

        Assert.Contains(
            expectedSubstring: "a signal reads an int or fixed cell",
            actualString: Refusal(definition: CreationFixtures.RigWorld(creation: clocked,
                state: [textRow]
            ))
        );
        Assert.Contains(
            expectedSubstring: "names no declared state row",
            actualString: Refusal(definition: CreationFixtures.RigWorld(creation: clocked))
        );
    }
}
