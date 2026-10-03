using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphHistoryLawTests {
    [Fact]
    public void PackageHistoryMetadataCommitsOnlySuccessfullySubmittedWriters() {
        var gpu = new FakePipelineGpu();
        var model = new Model { RefuseComposite = true };
        using var node = Node(gpu, model);

        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceUntilInstalled());
        Assert.Equal(actual: model.CommittedWrites, expected: 0);
        model.RefuseComposite = false; gpu.RefuseNextSubmission = true;
        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceFrame(context: default));
        Assert.Equal(actual: model.CommittedWrites, expected: 0);
        node.ProduceFrame(context: default);
        Assert.Equal(actual: model.CommittedWrites, expected: 1);
        node.ProduceFrame(context: default);
        Assert.Equal(actual: model.CommittedWrites, expected: 1);
        model.SkipLit = true;
        node.ProduceFrame(context: default);
        Assert.Equal(actual: model.CommittedWrites, expected: 1);
        model.SkipLit = false; model.Signature = 2;
        node.ProduceFrame(context: default);
        Assert.Equal(actual: model.CommittedWrites, expected: 2);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ASkippedWriterKeepsItsHistoryWhileCurrentReadersContinue(bool image) {
        var model = new Model();
        using var node = Node(new FakePipelineGpu(), model, image);

        node.ProduceUntilInstalled();
        var first = model.Blends[^1];

        model.SkipLit = true;
        for (var frame = 0; (frame < 9); frame++) { node.ProduceFrame(context: default); }
        Assert.Single(collection: model.Blends);
        Assert.All(collection: model.Samples, action: sample => Assert.Equal(actual: sample, expected: (first.Output, 1)));
        model.SkipLit = false;
        node.ProduceFrame(context: default);
        Assert.Equal((first.Output, 1), (model.Blends[^1].Input, model.Blends[^1].Previous));
    }
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void DeviceLossDuringRecordingOrSubmissionRollsBackAndRebuildsFromZero(bool submittedBefore, bool submitLoss) {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);

        if (submittedBefore) { node.ProduceUntilInstalled(); }
        var frames = node.FrameCounter;

        model.Signature = 2; model.LoseComposite = !submitLoss; gpu.LoseNextSubmission = submitLoss;
        Assert.Throws<DeviceLostException>(testCode: () => node.ProduceUntilInstalled());
        Assert.Equal(frames, node.FrameCounter);
        node.OnDeviceLost();
        Assert.Equal(0UL, node.CadenceCpuBytes);
        model.LoseComposite = false;
        node.ProduceUntilInstalled();
        Assert.Equal(0, model.Blends[^1].Previous);
        var writes = model.Blends.Count;

        node.ProduceFrame(context: default);
        Assert.Equal(writes, model.Blends.Count);
    }
    [Fact]
    public void ARefusedFirstSubmissionRearmsHistoryInitializationAndTheExportLayout() {
        var gpu = new FakePipelineGpu { Recording = true };
        var model = new Model();
        using var node = Node(gpu, model, publishHistory: true);
        var export = new FakeOutputExport(gpu, 32, 32);

        node.Export = export; gpu.RefuseNextSubmission = true;
        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceUntilInstalled());
        var target = Assert.Single(collection: export.Created).ImageHandle;

        gpu.Barriers.Clear(); gpu.ClearedImages.Clear(); model.Clears.Clear();
        node.ProduceFrame(context: default);
        Assert.Contains(collection: model.Clears, expected: model.Blends[^1].Input);
        Assert.Contains(collection: gpu.Barriers, filter: barrier => ((barrier.Handle == target) &&
            (barrier.Barrier.OldLayout == GpuImageLayout.Undefined) && (barrier.Barrier.NewLayout == GpuImageLayout.TransferDestination)));
    }
    [Fact]
    public void ResetClearsTheCommittedHistoryAfterStandingSubmissions() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);

        node.ProduceUntilInstalled();
        for (var frame = 0; (frame < 7); frame++) { node.ProduceFrame(context: default); }
        node.Reset();
        node.ProduceFrame(context: default);
        Assert.Equal(0, model.Blends[^1].Previous);
        Assert.Equal(1, model.Samples[^1].Value);
        var writes = model.Blends.Count;

        node.ProduceFrame(context: default);
        Assert.Equal(writes, model.Blends.Count);
    }
}
