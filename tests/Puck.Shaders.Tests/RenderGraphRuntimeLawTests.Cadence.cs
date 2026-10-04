using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void AStandingPassRetainsItsLastWriteThroughTheRuntime() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera, Over);
        var counter = new Counter { CadenceSignature = 1 };

        recorders.ByInstance.Add(key: "view", value: counter);
        var pipeline = Compile(definition: new RenderGraphDefinition(
            Schema: RenderGraphSchemas.Graph, Name: "cadence", Outputs: ["out"],
            Resources: [Image("retained") with { Retained = true }, Image("out")],
            Packages: [
                new RenderGraphPackagePass(Name: "write", Package: Camera, Outputs: ["retained"]),
                new RenderGraphPackagePass(Name: "read", Package: Over, Inputs: ["retained"], Outputs: ["out"]),
            ]));
        using var runtime = Runtime(gpu, recorders, Set(Instance("view")), "view", Graph(pipeline));
        var frames = new Frames(runtime, [new RenderGraphRoot(Height: 1, Instance: "view", Width: 1)], []);

        frames.Settle();
        var written = Assert.Single(collection: counter.ImageWrites).Value;
        var records = counter.Records;

        for (var frame = 0; (frame < 12); frame++) {
            frames.Next();
            Assert.Equal(written, counter.SampledWrites["read"]);
            Assert.Equal(written, Assert.Single(collection: counter.ImageWrites).Value);
        }
        Assert.Equal(actual: counter.Records, expected: (records + 12));
        counter.CadenceSignature = 2;
        frames.Next();
        Assert.True(condition: (counter.SampledWrites["read"] > written));
        written = counter.SampledWrites["read"];
        frames.Next(count: 6);
        Assert.Equal(written, counter.SampledWrites["read"]);
        var work = new GpuWorkSample();

        runtime.Node(instance: 0).PollReadbacks();
        Assert.True(condition: runtime.Node(instance: 0).TryReadCompleted(sample: work));
        Assert.Equal(GpuPassState.Standing, work.GetPassState(pass: 0));
        Assert.Equal(GpuPassState.Executed, work.GetPassState(pass: 1));
    }
}
