using System.Numerics;

using Puck.World.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a creation's noise-relief facet is admitted only where every consumer can honour it — a static stamp,
/// inside the creation's own field scope, with its march cost bounded at the door. An over-budget noise declaration
/// is refused by name (never silently reshaped into a stalled march), an animated creation cannot carry one (the
/// dynamic stamp pool emits per-shape instances a creation-level field op cannot span), and a noise-free document
/// round-trips byte-identically.
/// <para>Each arm pairs a denial with a control differing in exactly one authored number.</para>
/// </summary>
public sealed class CreationNoiseLawTests {
    private const string PrototypeId = "probe";

    private static ShapeDocument Shape() =>
        new(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
    private static CreationDocument Document(CreationNoiseDocument? noise, IReadOnlyList<FrameDocument>? frames = null) =>
        new(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: null,
            Shapes: [Shape()],
            Frames: frames,
            Noise: noise
        );
    private static WorldDefinition World(CreationNoiseDocument? noise, IReadOnlyList<FrameDocument>? frames = null, bool solid = true) {
        var source = Fixtures.BuildDocument();
        var canonical = CreationCanonicalizer.Canonicalize(
            document: Document(
                frames: frames,
                noise: noise
            ),
            source: PrototypeId
        );

        return (source with {
            CreationsRaw = [
                new WorldPrototype(
                    Id: PrototypeId,
                    Document: canonical.Document,
                    HashRaw: canonical.Hash
                ),
            ],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: PrototypeId,
                    PrototypeId: PrototypeId,
                    Position: Vector3.Zero,
                    YawDegrees: 0f,
                    Scale: 1f,
                    Solid: (solid
                    ? new WorldSolid(Margin: 0f)
                    : null)
                ),
            ],
        });
    }

    [Fact]
    public void AModestNoiseFacetCarriesTheSolidStaticRow() {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: World(noise: new CreationNoiseDocument(
                    Amplitude: 1.5f,
                    Frequency: 0.3f
                )),
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void AnOverBudgetStepFactorRefusesByName() {
        var violations = CreationCanonicalizer.Validate(document: Document(noise: new CreationNoiseDocument(
            Amplitude: 4f,
            Frequency: 8f,
            Lacunarity: 4f,
            Octaves: 8
        )));

        Assert.Contains(
            collection: violations,
            filter: violation => (
                (violation.Path == "noise") &&
                violation.Message.Contains(comparisonType: StringComparison.Ordinal, value: "step factor")
            )
        );
    }
    [Fact]
    public void TheStepFactorControlDiffersOnlyInAmplitude() {
        var violations = CreationCanonicalizer.Validate(document: Document(noise: new CreationNoiseDocument(
            Amplitude: 0.001f,
            Frequency: 8f,
            Lacunarity: 4f,
            Octaves: 8
        )));

        Assert.Empty(collection: violations);
    }
    [InlineData(0f, 1f, "noise.frequency")]
    [InlineData(9f, 1f, "noise.frequency")]
    [InlineData(0.3f, 0f, "noise.amplitude")]
    [InlineData(0.3f, 5f, "noise.amplitude")]
    [InlineData(float.NaN, 1f, "noise.frequency")]
    [Theory]
    public void AnOutOfRangeParameterRefusesByPath(float frequency, float amplitude, string path) {
        var violations = CreationCanonicalizer.Validate(document: Document(noise: new CreationNoiseDocument(
            Amplitude: amplitude,
            Frequency: frequency
        )));

        Assert.Contains(
            collection: violations,
            filter: violation => (violation.Path == path)
        );
    }
    [Fact]
    public void NormalizationResolvesEveryOptionalAndIsIdempotent() {
        var once = CreationCanonicalizer.Normalize(document: Document(noise: new CreationNoiseDocument(
            Amplitude: 1f,
            Frequency: 0.5f
        )));

        Assert.Equal(
            actual: once.Noise,
            expected: new CreationNoiseDocument(
                Amplitude: 1f,
                Frequency: 0.5f,
                Gain: 0.5f,
                Lacunarity: 2f,
                Octaves: 4,
                Seed: 0u
            )
        );
        Assert.Equal(
            actual: CreationCanonicalizer.Normalize(document: once).Noise,
            expected: once.Noise
        );
    }
    [Fact]
    public void AnInertAmplitudeDropsTheFacet() {
        Assert.Null(@object: CreationCanonicalizer.Normalize(document: Document(noise: new CreationNoiseDocument(
            Amplitude: 0f,
            Frequency: 0.5f
        ))).Noise);
    }
    [Fact]
    public void AnAnimatedCreationRefusesNoiseByName() {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: World(
                frames: [
                    new FrameDocument(
                        Name: "blink",
                        Transforms: [
                            new FrameTransformDocument(
                                Id: 0,
                                Position: Vector3.Zero,
                                Rotation: Quaternion.Identity,
                                Scale: Vector3.One
                            ),
                        ]
                    ),
                ],
                noise: new CreationNoiseDocument(
                    Amplitude: 1f,
                    Frequency: 0.5f
                ),
                solid: false
            ),
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "noise"
        );
    }
    [Fact]
    public void TheAnimatedControlWithoutNoiseValidates() {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: World(
                    frames: [
                        new FrameDocument(
                            Name: "blink",
                            Transforms: [
                                new FrameTransformDocument(
                                    Id: 0,
                                    Position: Vector3.Zero,
                                    Rotation: Quaternion.Identity,
                                    Scale: Vector3.One
                                ),
                            ]
                        ),
                    ],
                    noise: null,
                    solid: false
                ),
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void EmittedNoiseFoldsTheStepClampAndAnOpFreeProgramStaysAtOne() {
        static SdfProgram Build(CreationNoiseDocument? noise) {
            var builder = new SdfProgramBuilder();

            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            _ = builder.BeginInstance(
                boundCenter: Vector3.Zero,
                boundRadius: 8f
            );
            _ = builder.PushField(compose: SdfBlendOp.Union);
            _ = builder
                .ResetPoint()
                .Sphere(
                    radius: 1f,
                    material: 0
                );

            if (noise is not null) {
                CreationStampEmitter.EmitNoise(
                    builder: builder,
                    noise: noise,
                    transform: new CreationStampTransform(
                        Origin: Vector3.Zero,
                        Rotation: Quaternion.Identity,
                        Scale: 1f,
                        ReflectionNormal: null
                    )
                );
            }

            _ = builder.PopField();
            _ = builder.EndInstance();

            return builder.Build();
        }

        var noised = Build(noise: new CreationNoiseDocument(
            Amplitude: 1.5f,
            Frequency: 0.3f,
            Gain: 0.5f,
            Lacunarity: 2f,
            Octaves: 4,
            Seed: 7u
        ));
        var control = Build(noise: null);

        Assert.Contains(
            collection: noised.Instructions,
            filter: instruction => (instruction.Op == SdfOp.NoiseDisplace)
        );
        // The noise factor is SCOPE-LOCAL: the global step scale stays exactly 1 and the scope's PopField carries the
        // baked 1/L candidate scale in its Data1.y lane instead.
        Assert.Equal(
            actual: noised.StepScale,
            expected: 1f
        );

        var pop = Assert.Single(collection: noised.Instructions, predicate: instruction => (instruction.Op == SdfOp.PopField));
        var expectedFactor = SdfProgram.NoiseDisplaceStepFactor(
            amplitude: 1.5f,
            frequency: 0.3f,
            gain: 0.5f,
            lacunarity: 2f,
            octaves: 4
        );

        Assert.Equal(
            actual: pop.Data1.Y,
            expected: (1f / expectedFactor),
            tolerance: 1e-6f
        );
        Assert.DoesNotContain(
            collection: control.Instructions,
            filter: instruction => (instruction.Op == SdfOp.NoiseDisplace)
        );

        var controlPop = Assert.Single(collection: control.Instructions, predicate: instruction => (instruction.Op == SdfOp.PopField));

        Assert.Equal(
            actual: controlPop.Data1.Y,
            expected: 0f
        );
        Assert.Equal(
            actual: control.StepScale,
            expected: 1f
        );
    }
}
