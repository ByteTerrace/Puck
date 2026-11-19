using System.Numerics;
using System.Text;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client.Sdf;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// A creation's relief is charged in the frame it samples, through its stamp's scale (step 2 of
/// docs/plans/terrain-pass/relief-mask-design.md): the relieved scope's recorded bound is the document's own step factor
/// (<see cref="CreationNoiseDocument.StepFactor"/>) at every stamp scale, where it once held only at scale one. A
/// <c>puck.sdf.v1</c> relief after a cell fold, which no step bound covers, is refused at decode naming its ops.
/// </summary>
public sealed class ReliefStepBoundLawTests {
    private static ShapeDocument Sphere(float radius = 1f) => new(
        Id: 0,
        Name: "rock",
        Type: SdfSolidPrimitive.Sphere,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: new Vector3(value: radius),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );

    [Theory]
    [InlineData(1f, false)]
    [InlineData(64f, false)]
    [InlineData((1f / 64f), false)]
    [InlineData(1f, true)]
    [InlineData(64f, true)]
    [InlineData((1f / 64f), true)]
    public void ARelievedStampsRecordedBoundIsItsDocumentsStepFactorAtEveryScale(float scale, bool masked) {
        var noise = new CreationNoiseDocument(
            Amplitude: 0.25f,
            Frequency: 1.5f,
            Octaves: 3,
            Masks: (masked ? [new CreationReliefMaskDocument(Fade: 0.5f, Shapes: [Sphere(radius: 0.4f) with { Position = new Vector3(x: 0.5f, y: 0f, z: 0f) }])] : null)
        );
        var document = CreationFixtures.Document(name: "rock", palette: CreationFixtures.Grey, shapes: [Sphere()]) with { Noise = noise };
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // The contact emission reaches the one relief emission drawing uses (CreationStampEmitter.EmitNoise).
        CreationStampEmitter.EmitContact(
            builder: builder,
            document: document,
            margin: 0f,
            material: material,
            transform: new FixedCreationStampTransform(Origin: FixedVector3.Zero, ReflectionNormal: null, Rotation: FixedQuaternion.Identity, Scale: FixedQ4816.FromDouble(value: scale))
        );

        var clamp = Assert.Single(collection: builder.Build().FieldScopeClamps);

        Assert.Equal(expected: (1f / noise.StepFactor()), actual: clamp.StepScale);
    }
    [Theory]
    [InlineData("""{"op":"noiseDisplace","frequency":1,"amplitude":0.1}""")]
    [InlineData("""{"op":"cellDisplace","frequency":1,"amplitude":0.1,"seed":3,"mode":"F1","randomness":0.2}""")]
    public void APuckSdfReliefAfterACellFoldIsRefusedAtDecodeNamingItsOps(string relief) {
        static string Document(string between, string relief) => $$"""
            {"schema":"puck.sdf.v1","materials":[{"albedo":[1,1,1]}],"ops":[
            {"op":"push"},{"op":"sphere","radius":1,"material":0},
            {"op":"cellJitter","spacing":[4,4,4],"jitter":0.5}{{between}},
            {{relief}},
            {"op":"pop"}]}
            """;

        var refusal = Assert.Throws<SdfDocumentException>(testCode: () => SdfDocumentDecoder.Decode(utf8Json: Encoding.UTF8.GetBytes(s: Document(between: string.Empty, relief: relief))));

        Assert.Equal(expected: SdfRefusal.ReliefAfterFold, actual: refusal.Reason);
        Assert.Contains(expectedSubstring: "ops[3]", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "ops[2]", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);

        // Control: a reset between the fold and the relief gives the relief a rigid frame, and the program builds.
        var document = SdfDocumentDecoder.Decode(utf8Json: Encoding.UTF8.GetBytes(s: Document(between: """,{"op":"reset"}""", relief: relief)));
        var builder = new SdfProgramBuilder();

        SdfDocumentDecoder.Replay(builder: builder, program: document);
        _ = builder.Build();
    }
}
