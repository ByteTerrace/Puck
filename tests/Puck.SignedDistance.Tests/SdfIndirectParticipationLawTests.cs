using System.Numerics;
using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfIndirectParticipationLawTests {
    [Fact]
    public void InstancePoliciesOccupyTheirOwnBitsAndControlDynamicCasterBounds() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        foreach (var policy in Enum.GetValues<SdfIndirectParticipation>()) {
            builder.BeginInstanceDynamic(((int)policy), Vector3.Zero, 1f, indirect: policy)
                .ResetPoint().TransformDynamic(slot: ((int)policy)).Sphere(0.25f, material).EndInstance();
        }
        var program = builder.Build();
        var medium = program.BuildDynamicTransformBounds(SdfIndirectTier.Medium);

        Assert.Null(value: medium[0]);
        Assert.NotNull(value: medium[1]);
        Assert.Null(value: medium[2]);
        Assert.Null(value: medium[3]);
        var high = program.BuildDynamicTransformBounds(SdfIndirectTier.High);

        Assert.NotNull(value: high[0]);
        Assert.NotNull(value: high[1]);
        Assert.Null(value: high[2]);
        Assert.Null(value: high[3]);
        var overridden = program.BuildDynamicTransformBounds(bodies: SdfIndirectParticipation.Off, indirectTier: SdfIndirectTier.High);

        Assert.Null(value: overridden[0]);
        Assert.NotNull(value: overridden[1]);
        Assert.Null(value: overridden[2]);
        Assert.Null(value: overridden[3]);
        var words = program.Words;
        var bounds = (((int)words[3]) + (program.MaterialCount * SdfProgram.MaterialVectorsPerEntry));
        var segments = (bounds + (program.Instructions.Count * SdfProgram.BoundRecordVectors));
        var instances = ((segments + SdfProgram.DirectoryHeaderVectors) + (program.SkipSegmentCount * SdfProgram.BoundRecordVectors));

        for (var instance = 0; (instance < 4); instance++) {
            var meta = words[((((instances + 1) + (2 * instance)) * 4) + 7)];

            Assert.Equal(actual: (meta >> 28) & 3u, expected: ((uint)instance));
            Assert.True(condition: ((meta & 0x0fffffffu) > 0u));
        }
        Assert.True(condition: program.IndirectInstancesComposable);
    }
    [InlineData(SdfIndirectParticipation.Receive)]
    [InlineData(SdfIndirectParticipation.Off)]
    [Theory]
    public void TheCpuReferenceNamesAnUnsupportedFilteredFieldInsteadOfTracingOrdinaryCollision(SdfIndirectParticipation policy) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.BeginInstance(Vector3.Zero, 1f, indirect: policy).ResetPoint().Sphere(0.25f, material).EndInstance();
        var exception = Assert.Throws<ArgumentException>(testCode: () => new IrradianceField(program: builder.Build()));

        Assert.Contains("Receive/Off participation", exception.Message, StringComparison.Ordinal);
    }
}
