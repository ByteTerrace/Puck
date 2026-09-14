using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class CellDisplaceLawTests {
    private static SdfProgram Program(SdfCellMode mode, float randomness, uint seed, bool separateChain = false) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));

        builder.Plane(
            Vector3.UnitY,
            0f,
            material
        );
        if (separateChain) { builder.ResetPoint(); }
        return builder.CellDisplace(
            amplitude: 0.5f,
            frequency: 1f,
            mode: mode,
            randomness: randomness,
            seed: seed
        ).Build();
    }
    // Independent double-precision geometry, with the shared integer hash supplying only feature identity.
    private static double Reference(double x, double y, double z, uint seed, SdfCellMode mode, double randomness, int radius) {
        var cx = ((int)Math.Floor(d: x)); var cy = ((int)Math.Floor(d: y)); var cz = ((int)Math.Floor(d: z));
        var first = double.PositiveInfinity; var second = double.PositiveInfinity;

        for (var iz = -radius; (iz <= radius); iz++) {
            for (var iy = -radius; (iy <= radius); iy++) {
                for (var ix = -radius; (ix <= radius); ix++) {
                    var h = Pcg3dLatticeNoise.Pcg3d(
                        x: unchecked((uint)(cx + ix)) ^ seed,
                        y: unchecked((uint)(cy + iy)) ^ (seed ^ 0x9E3779B9u),
                        z: unchecked((uint)(cz + iz)) ^ (seed ^ 0x85EBCA77u)
                    );
                    var dx = ((((cx + ix) + .5) + (randomness * (((h.X >> 16) / 65536d) - .5))) - x);
                    var dy = ((((cy + iy) + .5) + (randomness * (((h.Y >> 16) / 65536d) - .5))) - y);
                    var dz = ((((cz + iz) + .5) + (randomness * (((h.Z >> 16) / 65536d) - .5))) - z);
                    var distance = Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz)));

                    if (distance < first) { second = first; first = distance; } else if (distance < second) { second = distance; }
                }
            }
        }
        return ((mode == SdfCellMode.F1)
            ? first
            : (second - first)
        );
    }

    [Fact]
    public void BothAdmissionDoorsRefuseInvalidParameters() {
        var valid = new SdfCellDisplacement(
            Amplitude: .5f,
            Frequency: 1f,
            Mode: SdfCellMode.F1,
            Randomness: .2f,
            Seed: 0u
        );

        foreach (var p in new[] { valid with { Frequency = 0 }, valid with { Frequency = float.NaN }, valid with { Amplitude = -1 }, valid with { Amplitude = float.PositiveInfinity }, valid with { Mode = ((SdfCellMode)2) }, valid with { Randomness = -.1f }, valid with { Randomness = .47f }, valid with { Mode = SdfCellMode.F2MinusF1, Randomness = .21f }, valid with { Randomness = float.NaN } }) {
            Assert.ThrowsAny<ArgumentException>(testCode: () => new SdfProgramBuilder().CellDisplace(
                p.Frequency,
                p.Amplitude,
                p.Seed,
                p.Mode,
                p.Randomness
            ));
            var program = Program(
                SdfCellMode.F1,
                .2f,
                0
            );
            var instructions = program.Instructions.ToArray();

            instructions[^1] = new(
                SdfOp.CellDisplace,
                p.Seed,
                ((uint)p.Mode),
                0,
                new(
                    p.Frequency,
                    p.Amplitude,
                    p.Randomness,
                    0
                ),
                Vector4.Zero
            );
            Assert.ThrowsAny<ArgumentException>(testCode: () => new SdfProgram(
                instructions,
                [new(Vector3.One)]
            ));
        }
    }
    [InlineData(SdfCellMode.F1, 0.46f)]
    [InlineData(SdfCellMode.F2MinusF1, 0.2f)]
    [Theory]
    public void DenseNeighborhoodSweepMatches125CellsAndTheFixedInterpreter(SdfCellMode mode, float randomness) {
        // Includes negative cells and every cell face, edge, and corner; four unrelated PCG seeds.
        foreach (var seed in new uint[] { 0, 1, 0xDEADBEEF, uint.MaxValue }) {
            var field = new SdfFieldEvaluator(program: Program(
                mode,
                randomness,
                seed
            ));

            for (var iz = -16; (iz <= 16); iz++) {
                for (var iy = -16; (iy <= 16); iy++) {
                    for (var ix = -16; (ix <= 16); ix++) {
                        var x = (ix / 16d); var y = (iy / 16d); var z = (iz / 16d);
                        var expected = Reference(
                            mode: mode,
                            radius: 2,
                            randomness: randomness,
                            seed: seed,
                            x: x,
                            y: y,
                            z: z
                        );

                        Assert.Equal(
                            expected,
                            Reference(
                                mode: mode,
                                radius: 1,
                                randomness: randomness,
                                seed: seed,
                                x: x,
                                y: y,
                                z: z
                            ),
                            12
                        );
                        Assert.True(condition: field.TryDistance(
                            FixedPosition.FromLocal(local: new(
                                X: FixedQ4816.FromDouble(value: x),
                                Y: FixedQ4816.FromDouble(value: y),
                                Z: FixedQ4816.FromDouble(value: z)
                            )),
                            out var actual,
                            out _
                        ));
                        Assert.InRange(
                            actual: ((double)actual),
                            high: ((y + (.5 * (expected - .5))) + 0.00004),
                            low: ((y + (.5 * (expected - .5))) - 0.00004)
                        );
                    }
                }
            }
        }
    }
    [Fact]
    public void DiscontinuousSamplingRequiresARestoredFrame() {
        var builder = new SdfProgramBuilder().Repeat(spacing: Vector3.One).CellDisplace(
            amplitude: .5f,
            frequency: 1f,
            mode: SdfCellMode.F1,
            randomness: .46f,
            seed: 0
        );

        Assert.Throws<ArgumentException>(testCode: () => builder.Build());
    }
    [InlineData(SdfCellMode.F1, 0.46f)]
    [InlineData(SdfCellMode.F2MinusF1, 0.2f)]
    [Theory]
    public void NeighborhoodCeilingHasAnAnalyticContainmentMargin(SdfCellMode mode, float randomness) {
        var d = (((double)randomness) / 2);
        var contained = ((mode == SdfCellMode.F1)
            ? (Math.Sqrt(d: 3) * (.5 + d))
            : Math.Sqrt(d: (((1 + d) * (1 + d)) + ((2 * (.5 + d)) * (.5 + d))))
        );

        Assert.True(condition: (contained < (1.5 - d)));
    }
    [InlineData(0.25f, false)]
    [InlineData(0.25f, true)]
    [InlineData(3f, false)]
    [InlineData(3f, true)]
    [Theory]
    public void ScaleChangesTheSamplingDerivativeWithoutScalingFieldAmplitude(float scale, bool separateChain) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));

        if (!separateChain) { builder.Scale(scale: new(value: scale)); }
        builder.Plane(
            Vector3.UnitY,
            0f,
            material
        );
        if (separateChain) { builder.ResetPoint().Scale(scale: new(value: scale)); }
        var program = builder.CellDisplace(
            amplitude: .5f,
            frequency: 1f,
            mode: SdfCellMode.F1,
            randomness: .46f,
            seed: 0
        ).Build();

        Assert.InRange(
            (1f / program.StepScale),
            ((1f + (.5f / scale)) - .00001f),
            ((1f + (.5f / scale)) + .00001f)
        );
    }
    [InlineData(SdfCellMode.F1, 0.46f)]
    [InlineData(SdfCellMode.F2MinusF1, 0.2f)]
    [Theory]
    public void StepFactorCoversMeasuredGradientsAndShapeFreeFieldChains(SdfCellMode mode, float randomness) {
        var factor = (1d / Program(
            mode,
            randomness,
            123
        ).StepScale);

        Assert.InRange(
            (1d / Program(
                mode: mode,
                randomness: randomness,
                seed: 123,
                separateChain: true
            ).StepScale),
            (factor - 0.00001),
            (factor + 0.00001)
        );
        const double E = 0.00001;

        double Field(double x, double y, double z) => (y + (.5 * (Reference(
            mode: mode,
            radius: 2,
            randomness: randomness,
            seed: 123,
            x: x,
            y: y,
            z: z
        ) - .5)));
        for (var z = -4; (z <= 4); z++) {
            for (var y = -4; (y <= 4); y++) {
                for (var x = -4; (x <= 4); x++) {
                    var px = (x * .23); var py = (y * .27); var pz = (z * .29);
                    var gx = ((Field(
                        x: (px + E),
                        y: py,
                        z: pz
                    ) - Field(
                        x: (px - E),
                        y: py,
                        z: pz
                    )) / (2 * E));
                    var gy = ((Field(
                        x: px,
                        y: (py + E),
                        z: pz
                    ) - Field(
                        x: px,
                        y: (py - E),
                        z: pz
                    )) / (2 * E));
                    var gz = ((Field(
                        x: px,
                        y: py,
                        z: (pz + E)
                    ) - Field(
                        x: px,
                        y: py,
                        z: (pz - E)
                    )) / (2 * E));

                    Assert.True(condition: (Math.Sqrt(d: (((gx * gx) + (gy * gy)) + (gz * gz))) <= (factor + 0.00001)));
                }
            }
        }
    }
}
