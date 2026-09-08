using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>The joint-chain laws: a shape's <c>parent</c> carries it — pivots included — through the parent's own
/// animation, a chain resolves in declaration order, and the one-way bend waveform is zero on its negative lobe.</summary>
public sealed class CreationSkeletonLawTests {
    private const float Tolerance = 1e-4f;

    private static readonly Vector3 Shoulder = new(x: 0.5f, y: 1.25f, z: 0f);
    private static readonly Vector3 Elbow = new(x: 0.5f, y: 0.75f, z: 0f);

    private static CreationDriverDocument Stride() => new(
        Cadence: 1f,
        Name: "stride",
        Signal: CreationDriverDocument.SignalPlanarTravel,
        When: ["always"]
    );
    private static ShapeDocument Part(int id, string name, Vector3 position, string? parent, params ShapeSwingDocument[] swings) => new(
        Id: id,
        Name: name,
        Type: SdfSolidPrimitive.Capsule,
        Position: position,
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.05f, y: 0.5f, z: 0.05f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Swings: ((swings.Length > 0) ? swings : null),
        Parent: parent
    );
    private static ShapeSwingDocument Swing(Vector3 pivot, float amplitude) => new(
        Driver: "stride",
        Pivot: pivot,
        Axis: Vector3.UnitZ,
        Amplitude: amplitude
    );
    private static CreationDocument Rig(params ShapeDocument[] shapes) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: "rig",
        Palette: null,
        Shapes: shapes,
        Frames: null,
        Drivers: [Stride()]
    );
    private static void AssertNear(Vector3 expected, Vector3 actual) {
        Assert.InRange(actual: Vector3.Distance(value1: expected, value2: actual), low: 0f, high: Tolerance);
    }

    /// <summary>A forearm that swings nothing itself is carried by its upper arm's shoulder swing: at a quarter turn
    /// the elbow — the forearm's rest position — lands where the shoulder swing puts it. The control is the same
    /// forearm without a parent, which stays at rest.</summary>
    [Fact]
    public void AChildRidesItsParentsSwingPivotsIncluded() {
        var upper = Part(id: 0, name: "upper", position: Elbow, parent: null, Swing(pivot: Shoulder, amplitude: (MathF.PI / 2f)));
        var carried = Part(id: 1, name: "fore", position: Elbow, parent: "upper");
        var loose = Part(id: 2, name: "loose", position: Elbow, parent: null);
        var document = Rig(upper, carried, loose);

        Assert.Empty(collection: CreationCanonicalizer.Validate(document: document));

        // Phase π/2 puts sine at its crest: a full quarter turn about +Z at the shoulder.
        float[] phases = [(MathF.PI / 2f)];
        float[] weights = [1f];

        WorldGaitDrivers.ComposeDelta(drivers: document.Drivers, phases: phases, rotation: out var parentRotation, shape: upper, translation: out var parentTranslation, weights: weights);
        WorldGaitDrivers.ComposeDelta(drivers: document.Drivers, phases: phases, rotation: out var childRotation, shape: carried, translation: out var childTranslation, weights: weights);
        WorldGaitDrivers.Chain(parentRotation: parentRotation, parentTranslation: parentTranslation, rotation: ref childRotation, translation: ref childTranslation);

        var position = Elbow;
        var rotation = Quaternion.Identity;

        WorldGaitDrivers.Apply(deltaRotation: childRotation, deltaTranslation: childTranslation, position: ref position, rotation: ref rotation);

        // The elbow hangs 0.5 below the shoulder; a +90° turn about +Z swings it to 0.5 along +X of the shoulder.
        AssertNear(expected: (Shoulder + new Vector3(x: 0.5f, y: 0f, z: 0f)), actual: position);

        var control = Elbow;
        var controlRotation = Quaternion.Identity;

        WorldGaitDrivers.ComposeDelta(drivers: document.Drivers, phases: phases, rotation: out var looseRotation, shape: loose, translation: out var looseTranslation, weights: weights);
        WorldGaitDrivers.Apply(deltaRotation: looseRotation, deltaTranslation: looseTranslation, position: ref control, rotation: ref controlRotation);
        AssertNear(expected: Elbow, actual: control);
    }

    /// <summary>A child's own swing composes under the carried frame: a forearm bending +90° at the elbow while the
    /// upper arm swings +90° at the shoulder ends a half turn from rest, with its hand-end where both turns put
    /// it. The control is the elbow bend alone, a quarter turn.</summary>
    [Fact]
    public void AChildsOwnSwingComposesUnderTheCarriedFrame() {
        var upper = Part(id: 0, name: "upper", position: Elbow, parent: null, Swing(pivot: Shoulder, amplitude: (MathF.PI / 2f)));
        var fore = Part(id: 1, name: "fore", position: new Vector3(x: 0.5f, y: 0.25f, z: 0f), parent: "upper", Swing(pivot: Elbow, amplitude: (MathF.PI / 2f)));
        var document = Rig(upper, fore);

        float[] phases = [(MathF.PI / 2f)];
        float[] weights = [1f];

        WorldGaitDrivers.ComposeDelta(drivers: document.Drivers, phases: phases, rotation: out var parentRotation, shape: upper, translation: out var parentTranslation, weights: weights);
        WorldGaitDrivers.ComposeDelta(drivers: document.Drivers, phases: phases, rotation: out var childRotation, shape: fore, translation: out var childTranslation, weights: weights);

        var alone = fore.Position.Value;
        var aloneRotation = Quaternion.Identity;

        WorldGaitDrivers.Apply(deltaRotation: childRotation, deltaTranslation: childTranslation, position: ref alone, rotation: ref aloneRotation);
        // The elbow bend alone: the forearm's centre (0.5 below the elbow) turns to 0.5 along +X of the elbow.
        AssertNear(expected: (Elbow + new Vector3(x: 0.5f, y: 0f, z: 0f)), actual: alone);

        WorldGaitDrivers.Chain(parentRotation: parentRotation, parentTranslation: parentTranslation, rotation: ref childRotation, translation: ref childTranslation);

        var chained = fore.Position.Value;
        var chainedRotation = Quaternion.Identity;

        WorldGaitDrivers.Apply(deltaRotation: childRotation, deltaTranslation: childTranslation, position: ref chained, rotation: ref chainedRotation);
        // Then the shoulder carries that: the elbow goes to shoulder + 0.5·X, and the forearm's centre, already
        // 0.5 along +X of the elbow, turns to 0.5 along +Y of the moved elbow.
        AssertNear(expected: (Shoulder + new Vector3(x: 0.5f, y: 0.5f, z: 0f)), actual: chained);

        var angle = (2f * MathF.Acos(x: MathF.Min(x: 1f, y: MathF.Abs(x: chainedRotation.W))));

        Assert.InRange(actual: angle, low: (MathF.PI - Tolerance), high: (MathF.PI + Tolerance));
    }

    /// <summary>The canonicalizer refuses a parent that is missing, a shape parenting itself, and a parent declared
    /// after its child; the control is the valid chain.</summary>
    [Fact]
    public void TheCanonicalizerRefusesAMissingSelfOrLaterParent() {
        Assert.Empty(collection: CreationCanonicalizer.Validate(document: Rig(
            Part(id: 0, name: "upper", position: Elbow, parent: null),
            Part(id: 1, name: "fore", position: Elbow, parent: "upper")
        )));
        Assert.Contains(expectedSubstring: "names no shape 'ghost'", actualString: Refusal(Rig(
            Part(id: 0, name: "fore", position: Elbow, parent: "ghost")
        )));
        Assert.Contains(expectedSubstring: "must be declared before", actualString: Refusal(Rig(
            Part(id: 0, name: "fore", position: Elbow, parent: "fore")
        )));
        Assert.Contains(expectedSubstring: "must be declared before", actualString: Refusal(Rig(
            Part(id: 0, name: "fore", position: Elbow, parent: "upper"),
            Part(id: 1, name: "upper", position: Elbow, parent: null)
        )));
    }

    /// <summary>The one-way bend: positive lobe intact, negative lobe zero; the control is plain sine on the same
    /// argument.</summary>
    [Fact]
    public void HalfSineKeepsThePositiveLobeAndZeroesTheNegative() {
        Assert.Equal(expected: 1f, actual: CreationWave.Evaluate(wave: CreationWave.HalfSine, argument: (MathF.PI / 2f)), precision: 5);
        Assert.Equal(expected: 0f, actual: CreationWave.Evaluate(wave: CreationWave.HalfSine, argument: (3f * MathF.PI / 2f)), precision: 5);
        Assert.Equal(expected: -1f, actual: CreationWave.Evaluate(wave: CreationWave.Sine, argument: (3f * MathF.PI / 2f)), precision: 5);
        Assert.True(condition: CreationWave.IsEvaluable(wave: CreationWave.HalfSine));
        // The constant waveform is the pose blend: 1 at every argument, so only the driver's weight shapes it.
        Assert.Equal(expected: 1f, actual: CreationWave.Evaluate(wave: CreationWave.Constant, argument: (3f * MathF.PI / 2f)), precision: 5);
        Assert.Equal(expected: 1f, actual: CreationWave.Evaluate(wave: CreationWave.Constant, argument: 0f), precision: 5);
        Assert.True(condition: CreationWave.IsEvaluable(wave: CreationWave.Constant));
    }

    private static string Refusal(CreationDocument document) => string.Join(
        separator: "; ",
        values: CreationCanonicalizer.Validate(document: document)
    );
}
