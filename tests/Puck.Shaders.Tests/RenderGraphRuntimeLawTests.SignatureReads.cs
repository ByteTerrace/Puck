using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void PackageCadenceReceivesTheSameAcquiredPublicationAsItsRecording() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var source = new FakeSource(gpu: gpu, cadence: ImageSourceCadence.Tick);
        var viewing = new SignatureReadingPackage();

        recorders.Registry.RegisterProducer(factory: _ => source, package: RenderGraphInstance.SourcePackage(producer: ReadProducer));
        recorders.Registry.Register(factory: viewing, package: Camera);
        using var runtime = Runtime(gpu, recorders,
            Set(RenderGraphInstance.Source(name: ReadSource, producer: ReadProducer),
                Instance(name: "view", reads: new RenderGraphRead(Producer: ReadSource))),
            "view", null!, Graph(pipeline: CameraGraph()));
        var index = 0L;

        TestLiveness.Until(step: () => {
            var frame = new RenderGraphFrame(DisplayHeight: Display, DisplayHertz: 60, DisplayWidth: Display,
                Index: index, Tick: index++,
                Footprints: [new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: ReadSource, Width: 1.0)],
                Roots: [new RenderGraphRoot(Height: 1.0, Instance: "view", Width: 1.0)]);

            _ = runtime.ProduceFrame(context: default, frame: in frame);
            return (viewing.Recorded.Count >= 3);
        });
        Assert.Equal(actual: viewing.Signatures, expected: viewing.Recorded);
        Assert.All(collection: viewing.Recorded, action: publication => {
            Assert.True(condition: publication.IsKnown);
            Assert.Same(expected: source, actual: publication.Owner);
        });
        Assert.Equal(expected: viewing.Recorded.Count, actual: viewing.Recorded.Distinct().Count());
        Assert.Equal(expected: source.Acquired, actual: source.Released);
    }

    private sealed class SignatureReadingPackage : IRenderGraphPackageFactory {
        public readonly List<GpuImagePublication> Signatures = [];
        public readonly List<GpuImagePublication> Recorded = [];

        public bool SamplesReads => true;

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this);

        private sealed class Recorder(SignatureReadingPackage owner) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) {
                if (reads is not null) {
                    var index = reads.IndexOf(producer: ReadSource);

                    Assert.False(condition: reads.IsTaken(index: index));
                    owner.Signatures.Add(item: reads[index].Publication);
                }
                return null;
            }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                var reads = Assert.IsType<RenderGraphExternalReads>(@object: recording.Reads);
                var index = reads.IndexOf(producer: ReadSource);

                owner.Recorded.Add(item: reads[index].Publication);
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
