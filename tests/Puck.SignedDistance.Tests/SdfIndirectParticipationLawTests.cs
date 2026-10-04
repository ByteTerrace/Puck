using System.Numerics;
using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfIndirectParticipationLawTests {
    [Fact]
    public void InstancePoliciesOccupyTheirOwnBitsAndControlDynamicCasterBounds() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        foreach (var policy in Enum.GetValues<SdfIndirectParticipation>()) {
            builder.BeginInstanceDynamic((int)policy, Vector3.Zero, 1f, indirect: policy)
                .ResetPoint().TransformDynamic((int)policy).Sphere(0.25f, material).EndInstance();
        }
        var program = builder.Build();
        var medium = program.BuildDynamicTransformBounds(SdfIndirectTier.Medium);
        Assert.Null(medium[0]);
        Assert.NotNull(medium[1]);
        Assert.Null(medium[2]);
        Assert.Null(medium[3]);
        var high = program.BuildDynamicTransformBounds(SdfIndirectTier.High);
        Assert.NotNull(high[0]);
        Assert.NotNull(high[1]);
        Assert.Null(high[2]);
        Assert.Null(high[3]);
        var overridden = program.BuildDynamicTransformBounds(SdfIndirectTier.High, SdfIndirectParticipation.Off);
        Assert.Null(overridden[0]);
        Assert.NotNull(overridden[1]);
        Assert.Null(overridden[2]);
        Assert.Null(overridden[3]);
        var words = program.Words;
        var bounds = (int)words[3] + program.MaterialCount * SdfProgram.MaterialVectorsPerEntry;
        var segments = bounds + program.Instructions.Count * SdfProgram.BoundRecordVectors;
        var instances = segments + SdfProgram.DirectoryHeaderVectors + program.SkipSegmentCount * SdfProgram.BoundRecordVectors;
        for (var instance = 0; instance < 4; instance++) {
            var meta = words[(instances + 1 + 2 * instance) * 4 + 7];
            Assert.Equal((uint)instance, (meta >> 28) & 3u);
            Assert.True((meta & 0x0fffffffu) > 0u);
        }
        Assert.True(program.IndirectInstancesComposable);
    }

    [Theory]
    [InlineData(SdfIndirectParticipation.Receive)]
    [InlineData(SdfIndirectParticipation.Off)]
    public void TheCpuReferenceNamesAnUnsupportedFilteredFieldInsteadOfTracingOrdinaryCollision(SdfIndirectParticipation policy) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.BeginInstance(Vector3.Zero, 1f, indirect: policy).ResetPoint().Sphere(0.25f, material).EndInstance();
        var exception = Assert.Throws<ArgumentException>(() => new IrradianceField(builder.Build()));
        Assert.Contains("Receive/Off participation", exception.Message, StringComparison.Ordinal);
    }
}
