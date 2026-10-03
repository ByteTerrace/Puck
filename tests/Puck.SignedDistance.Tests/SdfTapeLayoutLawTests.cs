using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfTapeLayoutLawTests {
    [Fact]
    public void ShapeAndPopTokensAreDenseAndDoNotBecomeCertificateFlags() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().Sphere(radius: 1, material: material).PushField().ResetPoint()
            .Sphere(radius: 2, material: material).Sphere(radius: 3, material: material).PopField()
            .ResetPoint().Sphere(radius: 4, material: material);
        var program = builder.Build();
        var words = program.Words;
        const int VectorWords = 4;
        var segments = (((int)((words[SdfProgram.ProgramMaterialOffsetLane]
            + (SdfProgram.MaterialVectorsPerEntry * words[SdfProgram.ProgramMaterialCountLane]))
            + (SdfProgram.BoundRecordVectors * words[SdfProgram.ProgramInstructionCountLane]))) * VectorWords);
        var certificates = (((int)words[(segments + SdfProgram.SegmentTapeLane)]) * VectorWords);
        var instances = (segments + ((SdfProgram.DirectoryHeaderVectors
            + (SdfProgram.BoundRecordVectors * program.SkipSegmentCount)) * VectorWords));
        uint[] expected = [0, 0, 1, 1, 1, 2, 3, 4, 4];

        Assert.Equal(expected: 5, actual: program.TapeTokenCount);
        Assert.Equal(expected: 5u, actual: words[(instances + SdfProgram.InstanceTapeTokenCountLane)]);
        Assert.Equal(expected: expected.Length, actual: program.InstructionCount);
        for (var index = 0; (index < program.InstructionCount); index++) {
            var packed = words[((certificates + (VectorWords * index)) + 3)];

            Assert.Equal(expected: expected[index], actual: (packed >> SdfProgram.TapeTokenShift));
            var flags = program.TapeCertificate(instruction: index).Flags;

            Assert.Equal(actual: flags, expected: packed & SdfProgram.TapeCertificateFlagsMask);
            Assert.Equal(expected: (program.Instructions[index].Op == SdfOp.ShapeBlend),
                actual: ((flags & SdfTapeCertificate.Certified) != 0));
        }
    }
}
