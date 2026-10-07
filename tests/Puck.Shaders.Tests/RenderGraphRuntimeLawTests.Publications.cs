using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void AGraphImageKeepsItsPublicationWhilePausedAndAdvancesAfterReset() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        var world = new ReadingWorld();

        recorders.Registry.RegisterProducer(factory: _ => world, package: World);
        using var runtime = Runtime(
            gpu, recorders,
            Set(
                Instance(name: ReadSource),
                new RenderGraphInstance(ExternalPackage: World, Name: "world", Passes: WorldPasses,
                    Reads: [new RenderGraphRead(Producer: ReadSource)], Refresh: RenderGraphRefresh.EveryFrame)),
            "world", Graph(pipeline: CameraGraph()), null!);
        var index = 0L;

        void Next() => _ = ProduceReading(hertz: 60, index: index++, runtime: runtime, tick: index);

        TestLiveness.Until(step: () => {
            Next();

            return ((world.Seen.Count == 1) && (world.Seen[0].ImageView != 0));
        });
        var original = Assert.Single(collection: world.Publications);
        var node = runtime.NodeOf(instance: ReadSource)!;

        Assert.Same(expected: node, actual: original.Owner);
        Assert.Equal(original, recorders.Publications[ReadSource]);
        Assert.True(condition: original.IsKnown);
        node.Paused = true;
        for (var held = 0; (held < 4); held++) {
            Next();
            Assert.Equal(expected: original, actual: Assert.Single(collection: world.Publications));
            Assert.Equal(original, recorders.Publications[ReadSource]);
        }

        node.Reset();
        node.Paused = false;
        Next();
        Assert.True(condition: (Assert.Single(collection: world.Publications).Sequence > original.Sequence));
        Assert.Same(expected: original.Owner, actual: Assert.Single(collection: world.Publications).Owner);
        Assert.Equal(Assert.Single(collection: world.Publications), recorders.Publications[ReadSource]);
    }
    [Fact]
    public void AForwardedExternalImageKeepsItsAcquiredPublicationThroughRepeatedReads() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Over);
        var source = new FakeSource(gpu: gpu, cadence: ImageSourceCadence.Tick);
        var world = new ReadingWorld();

        recorders.Registry.RegisterProducer(factory: _ => source, package: RenderGraphInstance.SourcePackage(producer: ReadProducer));
        recorders.Registry.RegisterProducer(factory: _ => world, package: World);
        using var runtime = Runtime(
            gpu, recorders,
            Set(
                RenderGraphInstance.Source(name: ReadSource, producer: ReadProducer),
                Instance(name: "mid", reads: new RenderGraphRead(Producer: ReadSource)),
                new RenderGraphInstance(ExternalPackage: World, Name: "world", Passes: WorldPasses,
                    Reads: [new RenderGraphRead(Producer: "mid")], Refresh: RenderGraphRefresh.EveryFrame)),
            "world", null!, Graph(OverGraph(reader: false), ("world", ReadSource)), null!);
        var index = 0L;
        var tick = 1L;

        void Next() {
            if (recorders.ByInstance.TryGetValue(key: "mid", value: out var counter)) {
                counter.DrawsWhenRefused = true;
                counter.Outcome = RenderGraphPackageOutcome.DrewNothing;
            }
            var frame = new RenderGraphFrame(
                DisplayHeight: Display, DisplayHertz: 60, DisplayWidth: Display, Index: index++, Tick: tick,
                Roots: [new RenderGraphRoot(Height: 1.0, Instance: "world", Width: 1.0)],
                Footprints: [
                    new RenderGraphFootprint(Consumer: "world", Height: 1.0, Producer: "mid", Width: 1.0),
                    new RenderGraphFootprint(Consumer: "mid", Height: 1.0, Producer: ReadSource, Width: 1.0),
                ]);

            _ = runtime.ProduceFrame(context: default, frame: in frame);
        }

        TestLiveness.Until(step: () => {
            Next();

            return ((runtime.NodeOf(instance: "mid")!.PublishedBinding == "world") &&
                (world.Seen.Count == 1) && (world.Seen[0].ImageView != 0));
        });
        var original = Assert.Single(collection: world.Publications);

        Assert.Same(expected: source, actual: original.Owner);
        Assert.True(condition: original.IsKnown);
        Assert.Equal(expected: source.Produced, actual: original.Sequence);
        Assert.Equal(original, recorders.Publications["mid"]);
        for (var read = 0; (read < 4); read++) {
            Next();
            Assert.Equal(expected: original, actual: Assert.Single(collection: world.Publications));
        }

        tick++;
        Next();
        var changed = Assert.Single(collection: world.Publications);

        Assert.Same(expected: source, actual: changed.Owner);
        Assert.Equal(expected: (original.Sequence + 1L), actual: changed.Sequence);
        Assert.Equal(changed, recorders.Publications["mid"]);
        Assert.Equal(expected: source.ImageView, actual: Assert.Single(collection: world.Seen).ImageView);
    }
}
