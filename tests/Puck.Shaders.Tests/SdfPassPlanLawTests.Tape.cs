using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [InlineData(0, 0, 33)]
    [InlineData(1, 1, 65)]
    [InlineData(31, 31, 65)]
    [InlineData(32, 32, 65)]
    [InlineData(33, 33, 89)]
    [InlineData(1, 64, 81)]
    [InlineData(1024, 4096, 2345)]
    [InlineData(1025, 4100, 2377)]
    [InlineData(65536, 65536, 49697)]
    [InlineData(int.MaxValue, int.MaxValue, 1627389985)]
    [Theory]
    public void ATileTapeContainsSeparateInstructionAndSegmentMasksForEverySlab(int segments, int tokens, int words) {
        Assert.Equal(expected: words, actual: SdfWorldPackage.SegmentTapeWordCountFor(segments: segments, tokens: tokens));
    }
    [Fact]
    public void ATileTapeRejectsNegativeSegmentCounts() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SdfWorldPackage.SegmentTapeWordCountFor(segments: -1, tokens: 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SdfWorldPackage.SegmentTapeWordCountFor(segments: 1, tokens: -1));
    }
    [Fact]
    public void EveryHitStageReadsTheTapeOnlyAfterItsProducer() {
        var passes = Plan.Pipeline.Passes;
        var tape = passes.Single(predicate: static pass => (pass.Package!.Part == SdfWorldPackage.Parts.Tape));
        var storage = Storage(plan: Plan.Pipeline, version: SdfWorldPackage.Parts.SegmentTapes);
        var write = Assert.Single(collection: tape.Accesses, predicate: access => (access.Storage == storage.Index));

        Assert.True(condition: write.Use.Writes);
        Assert.Equal(expected: GpuAccess.ShaderWrite, actual: write.Use.Access);
        Assert.Equal(expected: [ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles, ShaderPipelineCountBasis.SegmentTapeWords], actual: storage.Declaration.Count!.Single().Per);
        foreach (var part in new[] { SdfWorldPackage.Parts.Primary, SdfWorldPackage.Parts.Surface, SdfWorldPackage.Parts.Ambient, SdfWorldPackage.Parts.Shadow, SdfWorldPackage.Parts.Views }) {
            var consumer = passes.Single(predicate: pass => (pass.Package!.Part == part));
            var read = Assert.Single(collection: consumer.Accesses, predicate: access => (access.Storage == storage.Index));

            Assert.False(condition: read.Use.Writes);
            Assert.Equal(expected: GpuAccess.ShaderRead, actual: read.Use.Access);
            Assert.Equal(expected: ShaderPipelinePriorKind.Pass, actual: read.PriorKind);
            if (part == SdfWorldPackage.Parts.Primary) {
                Assert.Equal(expected: SdfWorldPackage.Parts.Tape, actual: passes[read.PriorPass].Package!.Part);
                Assert.Equal(expected: ShaderPipelineBarrierKind.Buffer, actual: read.Barrier.Kind);
                Assert.Equal(expected: GpuAccess.ShaderWrite, actual: read.Barrier.SourceAccess);
                Assert.Equal(expected: GpuAccess.ShaderRead, actual: read.Barrier.DestinationAccess);
            }
        }
    }
}
