using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: <see cref="SdfProgramBuilder.LaneErode"/> lowers to exactly one <see cref="SdfOp.LaneErode"/>
/// instruction, immediately before the <see cref="SdfOp.ShapeBlend"/> it targets, carrying (lane index, from, to,
/// noiseScale) in Data0 and reach in Data1.x — the packing <c>SDF_OP_LANE_ERODE</c> in sdf-vm.hlsli decodes. The
/// builder refuses an undefined lane, a non-finite/equal from-to pair, non-finite noise, and a non-finite/negative
/// reach.
/// </summary>
public sealed class LaneErodeLawTests {
    private static SdfProgramBuilder NewBuilder() {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder;
    }

    [Fact]
    public void LaneErodeLowersToOneInstructionCarryingTheDocumentedDataLanes() {
        var material = NewBuilder();
        var program = material
            .ResetPoint()
            .LaneErode(
                lane: 0,
                from: 0.2f,
                noiseScale: 3f,
                reach: 1.5f,
                to: 0.8f
            )
            .Sphere(radius: 1f, material: 0)
            .Build();

        // ResetPoint(), LaneErode, ShapeBlend — every chain opens with its own ResetPoint instruction.
        Assert.Equal(expected: 3, actual: program.Instructions.Count);
        Assert.Equal(expected: SdfOp.ResetPoint, actual: program.Instructions[0].Op);

        var erode = program.Instructions[1];

        Assert.Equal(expected: SdfOp.LaneErode, actual: erode.Op);
        Assert.Equal(expected: (float)((uint)0), actual: erode.Data0.X);
        Assert.Equal(expected: 0.2f, actual: erode.Data0.Y);
        Assert.Equal(expected: 0.8f, actual: erode.Data0.Z);
        Assert.Equal(expected: 3f, actual: erode.Data0.W);
        Assert.Equal(expected: 1.5f, actual: erode.Data1.X);
        Assert.Equal(expected: SdfOp.ShapeBlend, actual: program.Instructions[2].Op);
    }
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void EachLaneIndexPacksItsDocumentedOrdinal(int lane, uint expectedOrdinal) {
        var program = NewBuilder()
            .ResetPoint()
            .LaneErode(
                lane: lane,
                from: 0f,
                noiseScale: 1f,
                reach: 1f,
                to: 1f
            )
            .Sphere(radius: 1f, material: 0)
            .Build();

        Assert.Equal(expected: (float)expectedOrdinal, actual: program.Instructions[1].Data0.X);
    }
    [Fact]
    public void LaneErodeRefusesAnUndefinedLane() {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder()
            .ResetPoint()
            .LaneErode(
                lane: ((int)99),
                from: 0f,
                noiseScale: 1f,
                reach: 1f,
                to: 1f
            ));

        Assert.Equal(expected: "lane", actual: refusal.ParamName);
    }
    [Fact]
    public void LaneErodeRefusesAnEqualFromToPair() {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder()
            .ResetPoint()
            .LaneErode(
                lane: 0,
                from: 0.5f,
                noiseScale: 1f,
                reach: 1f,
                to: 0.5f
            ));

        Assert.Contains(expectedSubstring: "to", actualString: refusal.ParamName);
    }
    [Fact]
    public void LaneErodeAcceptsAReversedFromToPair() {
        // A reversed range (from > to) is a documented, legitimate spelling — the fold runs the other way.
        var program = NewBuilder()
            .ResetPoint()
            .LaneErode(
                lane: 0,
                from: 0.8f,
                noiseScale: 1f,
                reach: 1f,
                to: 0.2f
            )
            .Sphere(radius: 1f, material: 0)
            .Build();

        Assert.Equal(expected: 0.8f, actual: program.Instructions[1].Data0.Y);
        Assert.Equal(expected: 0.2f, actual: program.Instructions[1].Data0.Z);
    }
    [Fact]
    public void LaneErodeRefusesNonFiniteOrNegativeReach() {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder()
            .ResetPoint()
            .LaneErode(
                lane: 0,
                from: 0f,
                noiseScale: 1f,
                reach: -1f,
                to: 1f
            ));
        var notFinite = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder()
            .ResetPoint()
            .LaneErode(
                lane: 0,
                from: 0f,
                noiseScale: 1f,
                reach: float.NaN,
                to: 1f
            ));

        Assert.Contains(expectedSubstring: "reach", actualString: negative.Message);
        Assert.Contains(expectedSubstring: "reach", actualString: notFinite.Message);
    }
    // The public SdfProgram constructor's own admission door refuses a hand-assembled instruction stream whose
    // LaneErode carries a non-exact-integer or out-of-range lane index — the runtime shader thresholds it
    // (< 0.5 / < 1.5 / < 2.5) rather than rounding.
    [Fact]
    public void ThePublicConstructorRefusesAPackedLaneErodeWithAFractionalOrOutOfRangeLaneIndex() {
        SdfInstruction Erode(float laneIndex) => new(
            Blend: 0u,
            Data0: new Vector4(w: 1f, x: laneIndex, y: 0.2f, z: 0.8f),
            Data1: new Vector4(w: 0f, x: 1f, y: 0f, z: 0f),
            Material: 0u,
            Op: SdfOp.LaneErode,
            Shape: 0u
        );
        SdfInstruction Shape() => new(
            Blend: ((uint)SdfBlendOp.Union),
            Data0: new Vector4(w: 0f, x: 1f, y: 0f, z: 0f),
            Data1: Vector4.Zero,
            Material: 0u,
            Op: SdfOp.ShapeBlend,
            Shape: ((uint)SdfShapeType.Sphere)
        );

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: [Erode(laneIndex: 0.5f), Shape()],
            materials: [new SdfMaterial(Albedo: Vector3.One)]
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: [Erode(laneIndex: 4f), Shape()],
            materials: [new SdfMaterial(Albedo: Vector3.One)]
        ));

        // Control: every documented ordinal is admitted.
        _ = new SdfProgram(
            instructions: [Erode(laneIndex: 3f), Shape()],
            materials: [new SdfMaterial(Albedo: Vector3.One)]
        );
    }
}
