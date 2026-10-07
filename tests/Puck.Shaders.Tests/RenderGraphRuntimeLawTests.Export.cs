using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// An exported view: a package instance whose node copies its output into an image a reader on another device takes. Every
// reader on the node's device binds the instance's own published outputs, never the export's image, and a view reading
// itself binds the output of an earlier frame, never the image the same submission writes.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string ExportedView = "exported";

    [Fact]
    public void AReaderOfAnExportedViewBindsItsOwnOutputsAndASelfReadNeverWritesWhatItReads() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var view = new SelfReadingView();
        var screen = new ScreenPackage();

        recorders.Registry.Register(
            factory: view,
            package: RenderGraphPackageCatalog.SdfWorld
        );
        recorders.Registry.Register(
            factory: screen,
            package: Camera
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(
                PackageInstance() with {
                    Name = ExportedView,
                    Reads = [new RenderGraphRead(Producer: ExportedView)],
                },
                Instance(
                    name: "screen",
                    reads: new RenderGraphRead(Producer: ExportedView)
                )
            ),
            "screen",
            null!,
            Graph(pipeline: CameraGraph())
        );
        var export = new FakeOutputExport(
            format: RenderGraphPackageCatalog.WorkingFormat,
            gpu: gpu,
            height: 16,
            width: 32
        );

        runtime.NodeOf(instance: ExportedView)!.Export = export;

        var index = 0L;

        void Produce() {
            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: [new RenderGraphFootprint(Consumer: "screen", Height: 0.25, Producer: ExportedView, Width: 0.5)],
                Index: index,
                Roots: [new RenderGraphRoot(Height: 1.0, Instance: "screen", Width: 1.0)],
                Tick: index
            );

            index++;
            _ = runtime.ProduceFrame(
                context: default,
                frame: in frame
            );
        }

        TestLiveness.Until(
            reason: () => $"The view read {view.Read.Count} frame(s), the screen {screen.Seen.Count} and the export {gpu.CopiedImages.Count} copies.",
            step: () => {
                Produce();

                return ((view.Read.Count >= 2) && (screen.Seen.Count >= 2) && (gpu.CopiedImages.Count >= 2));
            }
        );

        var exported = runtime.NodeOf(instance: ExportedView)!.ExportedImage!;

        // A frame renders the view once, so its reads and writes within one produced frame share its one submission.
        for (var frame = 0; (frame < 4); frame++) {
            view.Read.Clear();
            view.Wrote.Clear();
            screen.Seen.Clear();
            gpu.CopiedImages.Clear();
            Produce();

            var read = Assert.Single(collection: view.Read);

            Assert.NotEqual(expected: exported.ImageViewHandle, actual: read.View);
            Assert.DoesNotContain(expected: read.Image, collection: view.Wrote);
            Assert.All(action: seen => Assert.NotEqual(expected: exported.ImageViewHandle, actual: seen), collection: screen.Seen);
            Assert.All(action: copy => Assert.Equal(expected: exported.ImageHandle, actual: copy.Destination), collection: gpu.CopiedImages);
            Assert.All(action: copy => Assert.Contains(expected: copy.Source, collection: view.Wrote), collection: gpu.CopiedImages);
        }

        Assert.DoesNotContain(expected: exported.ImageHandle, collection: view.Wrote);
    }

    /// <summary>A view package that samples its instance's reads, noting the image and view each recording reads its own
    /// output through, and every image its recordings write.</summary>
    private sealed class SelfReadingView : IRenderGraphPackageFactory, IShaderPipelineStorageCounter {
        public List<(nint Image, nint View)> Read { get; } = [];
        public long Revision => 0L;
        public bool SamplesReads => true;
        public HashSet<nint> Wrote { get; } = [];

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public ShaderPipelineStorageCounts CountsAt(uint width, uint height) => new(
            Height: height,
            Width: width
        ) {
            InstanceMaskWords = 1,
            Instances = 1,
            SegmentTapeWords = 36,
            Tiles = ((((width + 15U) / 16U) * ((height + 15U) / 16U))),
            Viewports = 1,
        };
        public IShaderPipelineStorageCounter? CounterOf(string instance) => this;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(
            first: (context.Part == SdfWorldPackage.NativeFragment.Passes[0].Name),
            owner: this
        );

        private sealed class Recorder(SelfReadingView owner, bool first) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                foreach (var output in recording.Outputs) {
                    if (output.Image.ImageHandle != 0) {
                        _ = owner.Wrote.Add(item: output.Image.ImageHandle);
                    }
                }

                if (
                    first &&
                    (recording.Reads is { } reads) &&
                    (reads.IndexOf(producer: ExportedView) is var index and >= 0) &&
                    reads[index].Image.IsSameDeviceImage
                ) {
                    owner.Read.Add(item: (reads[index].Image.ImageHandle, reads[index].Image.ImageViewHandle));
                }

                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
    /// <summary>A screen package that samples its instance's reads, noting the view of the exported view's image each
    /// recording is handed.</summary>
    private sealed class ScreenPackage : IRenderGraphPackageFactory {
        public bool SamplesReads => true;
        public List<nint> Seen { get; } = [];

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this);

        private sealed class Recorder(ScreenPackage owner) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                if (
                    (recording.Reads is { } reads) &&
                    (reads.IndexOf(producer: ExportedView) is var index and >= 0) &&
                    reads[index].Image.IsSameDeviceImage
                ) {
                    owner.Seen.Add(item: reads[index].Image.ImageViewHandle);
                }

                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
