using System.Text;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphCadenceLawTests {
    [Fact]
    public void CadenceMemoryIncludesEveryRecoveryCheckpointAndWithdrawsOnLoss() {
        using var ordinary = ImageNode(new FakePipelineGpu(), new ImageFactory(), "ordinary");

        ordinary.ProduceUntilInstalled();
        Assert.Equal(0UL, ordinary.CadenceCpuBytes);
        using var retained = Node(new FakePipelineGpu(), new Model());

        retained.ProduceUntilInstalled();
        var original = retained.CadenceCpuBytes;

        Assert.True(condition: (original > 0));
        // An extra rotating input creates no cadence version, but each of its tracker slots needs a checkpoint.
        var input = new ShaderPipelineResource(Name: "source", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 16,
            Initialization: ShaderPipelineInitialization.Zero);
        using var expanded = Node(new FakePipelineGpu(), new Model(), input: input);

        expanded.ProduceUntilInstalled();
        Assert.True(condition: (expanded.CadenceCpuBytes > original));
        var text = new StringBuilder();

        Assert.True(condition: expanded.TryAppendInspection(builder: text, name: "retained"));
        Assert.Contains($"cadence-cpu-bytes={expanded.CadenceCpuBytes};", text.ToString(), StringComparison.Ordinal);
        retained.OnDeviceLost();
        Assert.Equal(0UL, retained.CadenceCpuBytes);
    }
    [Fact]
    public void AFailedSubmissionRearmsTheStagedCopiesAlreadyRecordedForIt() {
        var gpu = new FakePipelineGpu { Recording = true, RefuseNextSubmission = true };
        var model = new Model { StageRegion = true };
        using var node = Node(gpu, model);

        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceUntilInstalled());
        Assert.Single(collection: gpu.Dispatches);
        gpu.Dispatches.Clear();
        node.ProduceFrame(context: default);
        Assert.Single(collection: gpu.Dispatches);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (2, 2));
        gpu.Dispatches.Clear();
        node.ProduceFrame(context: default);
        Assert.Empty(collection: gpu.Dispatches);
    }
    [Fact]
    public void AFailureAfterSubmissionCannotRollBackItsCommittedContentIdentity() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);
        var export = new FakeOutputExport(gpu, 32, 32);

        node.Export = export;
        node.ProduceUntilInstalled();
        var image = Assert.Single(collection: export.Created);

        image.RefuseCompletion = true;
        model.WriterSignature = 2; model.WriterValue = 33;
        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceFrame(context: default));
        var writes = model.Writes;

        image.RefuseCompletion = false;
        node.ProduceFrame(context: default);
        Assert.Equal(actual: model.Writes, expected: writes);
        Assert.Equal((33, 20), model.Samples[^1]);
    }
}
