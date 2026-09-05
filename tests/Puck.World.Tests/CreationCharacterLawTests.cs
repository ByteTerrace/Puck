using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>The rig's three doors onto the world: a scalar facet bound to a numeric state cell resolves through the
/// document walk, a driver's state-cell signal reads the sim's own number at a tick, and a <c>curve:</c> waveform
/// samples a declared curves row — with the world validator refusing the bindings it cannot resolve.</summary>
public sealed class CreationCharacterLawTests {
    private const float Tolerance = 1e-3f;

    private static CreationDocument Rig(IReadOnlyList<CreationDriverDocument> drivers, params ShapeDocument[] shapes) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: "rig",
        Palette: null,
        Shapes: shapes,
        Frames: null,
        Drivers: drivers
    );
    private static ShapeDocument Limb(ShapeSwingDocument swing) => new(
        Id: 0,
        Name: "limb",
        Type: SdfSolidPrimitive.Capsule,
        Position: new Vector3(x: 0f, y: -1f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.05f, y: 1f, z: 0.05f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Swings: [swing]
    );
    private static WorldDefinition World(CreationDocument creation, IReadOnlyList<WorldStateRow>? state = null, IReadOnlyList<WorldCurveRow>? curves = null) {
        var basis = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var section = (basis.StateRaw ?? new WorldStateSection());

        return basis with {
            CreationsRaw = [.. basis.Creations, new WorldPrototype(Id: new DocumentIdentifier(value: "rig"), Document: creation)],
            StateRaw = (section with { World = [.. (section.World ?? []), .. (state ?? [])] }),
            CurvesRaw = curves,
        };
    }
    private static WorldStateCell Cell(string key, double value) => new(
        Key: CellName.Parse(candidate: key),
        Value: FixedQ4816.FromDouble(value: value).Value
    );
    private static WorldStateRow FixedRow(string name, double value) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Fixed,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: FixedQ4816.FromDouble(value: value).Value)]
    );
    private static string Refusal(WorldDefinition definition) => (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);
    private static DocumentScalar Bound(string reference) =>
        System.Text.Json.JsonSerializer.Deserialize<DocumentScalar>(json: $"\"{reference}\"", options: DocumentJsonOptions.Shared)!;
    // A hump drawn left to right (z = sin(πx), the waveform an artist would draw), as the curvature-first knots the
    // spline compiles: yaw = atan2(dz/dx, 1), curvature = the yaw's rate along the arc.
    private static WorldCurveRow Hump() {
        // Drawn over x in [0, 4] so the crest's curvature stays inside the spline's authoring bound.
        static float F(float x) => MathF.Sin(x: (MathF.PI * (x / 4f)));
        static float D(float x) => ((F(x: (x + 1e-3f)) - F(x: (x - 1e-3f))) / 2e-3f);
        static float D2(float x) => ((D(x: (x + 1e-3f)) - D(x: (x - 1e-3f))) / 2e-3f);

        var knots = new List<WorldCurveKnot>();

        for (var i = 0; (i <= 8); i++) {
            var x = (i / 2f);
            var slope = D(x: x);

            knots.Add(item: new WorldCurveKnot(
                Position: new DocumentVector3(x: x, y: 0f, z: F(x: x)),
                TangentYaw: MathF.Atan2(y: slope, x: 1f),
                Curvature: (D2(x: x) / MathF.Pow(x: (1f + (slope * slope)), y: 1.5f))
            ));
        }

        return new WorldCurveRow(Name: "hump", Knots: knots);
    }

    /// <summary>A driver cadence and a swing amplitude bound to fixed cells resolve through the document walk to
    /// the cells' numbers; the control is a literal beside them, untouched by the walk.</summary>
    [Fact]
    public void AScalarReferenceResolvesFromANumericStateCell() {
        var creation = Rig(
            drivers: [new CreationDriverDocument(Name: "stride", Signal: CreationDriverDocument.SignalPlanarTravel, Cadence: Bound(reference: "state.gait.cadence"), When: ["always"])],
            Limb(swing: new ShapeSwingDocument(Driver: "stride", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: Bound(reference: "state.gait.reach"), Phase: 0.25f))
        );
        var world = World(creation: creation, state: [
            new WorldStateRow(Name: CellName.Parse(candidate: "gait"), Kind: CellKind.Fixed, Cells: [Cell(key: "cadence", value: 6.5), Cell(key: "reach", value: 0.75)]),
        ]);

        Assert.True(condition: WorldStateDocumentValues.TryRefresh(definition: world, refreshed: out var refreshed, reason: out var reason, rowName: "gait"), userMessage: reason);

        var driver = refreshed.Creations[^1].Document.Drivers![0];
        var swing = refreshed.Creations[^1].Document.Shapes![0].Swings![0];

        Assert.Equal(expected: 6.5f, actual: driver.Cadence.Value, precision: 4);
        Assert.Equal(expected: 0.75f, actual: swing.Amplitude.Value, precision: 4);
        Assert.Equal(expected: 0.25f, actual: swing.Phase!.Value, precision: 4);
        Assert.NotNull(@object: driver.Cadence.Reference);
        Assert.Null(@object: swing.Phase.Reference);
    }

    /// <summary>A state-cell signal sets the phase to cadence times the cell's value at the frame's tick; the
    /// control is the same driver read against a world whose cell holds zero.</summary>
    [Fact]
    public void AStateSignalDrivesThePhaseFromTheCell() {
        var driver = new CreationDriverDocument(Name: "clock", Signal: "state.turns", Cadence: 2f, When: ["always"]);
        var creation = Rig(drivers: [driver], Limb(swing: new ShapeSwingDocument(Driver: "clock", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: 1f)));
        var world = World(creation: creation, state: [FixedRow(name: "turns", value: 0.25)]);
        var zero = World(creation: creation, state: [FixedRow(name: "turns", value: 0)]);

        Assert.True(condition: (Refusal(definition: world).Length == 0), userMessage: Refusal(definition: world));

        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];
        var address = new Protocol.WorldEntityAddress(Authority: "a", Index: 0, Generation: 1);
        var last = Vector3.Zero;
        var lastRotation = Quaternion.Identity;
        var seeded = false;
        var lastAddress = default(Protocol.WorldEntityAddress);
        var speed = 0f;

        for (var frame = 0; (frame < 2); frame++) {
            WorldGaitDrivers.Advance(drivers: creation.Drivers, phases: phases, weights: weights, deltaSeconds: (1f / 60f), facts: Physics.Motion.BodyFacts.Grounded, position: Vector3.Zero, orientation: Quaternion.Identity, lastPosition: ref last, lastOrientation: ref lastRotation, seeded: ref seeded, lastAddress: ref lastAddress, easedSpeed: ref speed, address: address, definition: world, tick: 10UL);
        }
        Assert.Equal(expected: 0.5f, actual: phases[0], precision: 4);

        WorldGaitDrivers.Advance(drivers: creation.Drivers, phases: phases, weights: weights, deltaSeconds: (1f / 60f), facts: Physics.Motion.BodyFacts.Grounded, position: Vector3.Zero, orientation: Quaternion.Identity, lastPosition: ref last, lastOrientation: ref lastRotation, seeded: ref seeded, lastAddress: ref lastAddress, easedSpeed: ref speed, address: address, definition: zero, tick: 10UL);
        Assert.Equal(expected: 0f, actual: phases[0], precision: 4);
    }

    /// <summary>A <c>curve:</c> waveform samples the world's row: at half a turn the hump reads its crest, at zero
    /// its start; the control is sine on the same arguments, which reads zero at half a turn.</summary>
    [Fact]
    public void ACurveWaveSamplesTheWorldsCurveRow() {
        var creation = Rig(
            drivers: [new CreationDriverDocument(Name: "stride", Signal: CreationDriverDocument.SignalPlanarTravel, Cadence: 1f, When: ["always"])],
            Limb(swing: new ShapeSwingDocument(Driver: "stride", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: 1f, Wave: "curve:hump"))
        );
        var world = World(creation: creation, curves: [Hump()]);

        Assert.True(condition: (Refusal(definition: world).Length == 0), userMessage: Refusal(definition: world));

        var crest = WorldGaitDrivers.Wave(wave: "curve:hump", argument: MathF.PI, definition: world);
        var start = WorldGaitDrivers.Wave(wave: "curve:hump", argument: 0f, definition: world);

        Assert.InRange(actual: crest, low: 0.85f, high: 1.05f);
        Assert.InRange(actual: start, low: -Tolerance, high: Tolerance);
        Assert.InRange(actual: WorldGaitDrivers.Wave(wave: CreationWave.Sine, argument: MathF.PI, definition: world), low: -Tolerance, high: Tolerance);
    }

    /// <summary>The world validator refuses a curve waveform naming no row and a state signal naming a text row;
    /// the control is the same document with the row declared.</summary>
    [Fact]
    public void TheWorldValidatorRefusesAnUnknownCurveOrANonNumericSignalRow() {
        var curved = Rig(
            drivers: [new CreationDriverDocument(Name: "stride", Signal: CreationDriverDocument.SignalPlanarTravel, Cadence: 1f, When: ["always"])],
            Limb(swing: new ShapeSwingDocument(Driver: "stride", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: 1f, Wave: "curve:hump"))
        );

        Assert.Contains(expectedSubstring: "names no declared curves row", actualString: Refusal(definition: World(creation: curved)));
        Assert.Equal(expected: string.Empty, actual: Refusal(definition: World(creation: curved, curves: [Hump()])));

        var clocked = Rig(
            drivers: [new CreationDriverDocument(Name: "clock", Signal: "state.label", Cadence: 1f, When: ["always"])],
            Limb(swing: new ShapeSwingDocument(Driver: "clock", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: 1f))
        );
        var textRow = new WorldStateRow(Name: CellName.Parse(candidate: "label"), Kind: CellKind.Text, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Text: "north")]);

        Assert.Contains(expectedSubstring: "a signal reads an int or fixed cell", actualString: Refusal(definition: World(creation: clocked, state: [textRow])));
        Assert.Contains(expectedSubstring: "names no declared state row", actualString: Refusal(definition: World(creation: clocked)));
    }

    /// <summary>The world validator refuses a gate token naming no body fact, by path and token; the controls are
    /// the same driver gated on a published fact and on the client's own <c>moving</c> token, both admitted.</summary>
    [Fact]
    public void TheWorldValidatorRefusesAGateTokenNamingNoBodyFact() {
        static CreationDocument Gated(string token) => Rig(
            drivers: [new CreationDriverDocument(Name: "stride", Signal: CreationDriverDocument.SignalPlanarTravel, Cadence: 1f, When: [token])],
            Limb(swing: new ShapeSwingDocument(Driver: "stride", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: 1f))
        );

        var refusal = Refusal(definition: World(creation: Gated(token: "Floating")));

        Assert.Contains(expectedSubstring: "drivers[0].when[0] 'Floating' names no body fact", actualString: refusal);
        Assert.Equal(expected: string.Empty, actual: Refusal(definition: World(creation: Gated(token: "Grounded"))));
        Assert.Equal(expected: string.Empty, actual: Refusal(definition: World(creation: Gated(token: CreationDriverDocument.TokenMoving))));
    }
}
