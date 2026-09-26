using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// Uploaded sources: an external instance whose package registers an upload renders through a node running the one-pass
// conversion graph its descriptor names, the shipped kernel reading the region the upload writes through the node's host
// buffer port. The runtime declares the source's cadence and extent itself, so it converts once per completed tick
// however many instances read it, and never while none shows it.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string Upload = "source.test";
    private const uint SourceExtent = 16;

    // A test source, and two views each showing it through one external version.
    private static (RenderGraphRuntime Runtime, FakeUpload Upload) SourceScene(FakePipelineGpu gpu, ImagePixelFormat format = ImagePixelFormat.B8G8R8A8Unorm) {
        var recorders = new Recorders();
        var upload = new FakeUpload(format: format);

        SourceConversionPackage.RegisterAll(packages: recorders.Registry);
        recorders.Registry.RegisterSource(
            factory: _ => upload,
            package: Upload
        );

        var set = Set(
            RenderGraphInstance.Source(
                name: "pattern",
                producer: "test"
            ),
            Instance(
                name: "left",
                reads: new RenderGraphRead(Producer: "pattern")
            ),
            Instance(
                name: "right",
                reads: new RenderGraphRead(Producer: "pattern")
            )
        );
        var runtime = Runtime(
            gpu,
            recorders,
            set,
            "left",
            null!,
            Graph(ScreensGraph(false, "screen"), ("screen", "pattern")),
            Graph(ScreensGraph(false, "screen"), ("screen", "pattern"))
        );

        return (runtime, upload);
    }
    // Produces one frame at a tick, both views showing the source whole.
    private static void Produce(RenderGraphRuntime runtime, long index, long tick, bool shown = true) {
        var frame = new RenderGraphFrame(
            DisplayHeight: Display,
            DisplayHertz: 60,
            DisplayWidth: Display,
            Footprints: (shown
                ? [
                    new RenderGraphFootprint(Consumer: "left", Height: 1.0, Producer: "pattern", Width: 1.0),
                    new RenderGraphFootprint(Consumer: "right", Height: 1.0, Producer: "pattern", Width: 1.0),
                ]
                : []),
            Index: index,
            Roots: [
                new RenderGraphRoot(Height: 1.0, Instance: "left", Width: 1.0),
                new RenderGraphRoot(Height: 1.0, Instance: "right", Width: 1.0),
            ],
            Tick: tick
        );

        _ = runtime.ProduceFrame(
            context: default,
            frame: in frame
        );
    }

    [Fact]
    public void TwoConsumersOfOneSourceRunOneConversionPerTickCountedThroughItsWork() {
        var gpu = new FakePipelineGpu();

        var (runtime, upload) = SourceScene(gpu: gpu);

        using (runtime) {
            var source = runtime.Instances.IndexOf(name: "pattern");
            var index = 0L;

            Assert.Same(expected: upload, actual: runtime.Source(instance: source));
            Assert.Null(@object: runtime.Producer(instance: source));
            Assert.True(
                condition: SpinWait.SpinUntil(
                    condition: () => {
                        Produce(
                            index: index,
                            runtime: runtime,
                            tick: index
                        );
                        index++;

                        return runtime.IsSettled;
                    },
                    timeout: TimeSpan.FromSeconds(value: 30)
                ),
                userMessage: "The source's conversion never built."
            );

            var node = runtime.Node(instance: source);
            var submitted = node.FrameCounter;
            var writes = upload.Writes;
            var sample = new GpuWorkSample();

            // A new tick each frame: one conversion a frame, however many views read it, and each view binds its image.
            for (var frame = 0; (frame < 6); frame++, index++) {
                Produce(
                    index: index,
                    runtime: runtime,
                    tick: index
                );
                Assert.Equal(
                    actual: runtime.Latest!.Renders.Count(predicate: rendered => (rendered == source)),
                    expected: 1
                );
            }

            Assert.Equal(expected: (6UL, 6L), actual: ((node.FrameCounter - submitted), (upload.Writes - writes)));
            Assert.True(condition: runtime.Work(instance: source).TryReadCompleted(sample: sample));
            Assert.Equal(expected: [RenderGraphRuntime.SourceConversionPass], actual: sample.PassLabels.ToArray());
            Assert.True(condition: sample.TryGetPassCount(column: 0, pass: 0, value: out var dispatches));
            Assert.Equal(actual: dispatches, expected: 1L);

            // One tick held for three frames converts once, and a frame no view shows the source in converts nothing.
            submitted = node.FrameCounter;

            var held = index;

            for (var frame = 0; (frame < 3); frame++) {
                Produce(
                    index: index++,
                    runtime: runtime,
                    tick: held
                );
            }

            Produce(
                index: index,
                runtime: runtime,
                shown: false,
                tick: (held + 1)
            );
            Assert.Equal(expected: 1UL, actual: (node.FrameCounter - submitted));
        }
    }
    [Fact]
    public void ASourcesGraphIsTheConversionItsDescriptorNamesOverItsRegion() {
        foreach (var (format, pass) in ((ReadOnlySpan<(ImagePixelFormat, string)>)[
            (ImagePixelFormat.B8G8R8A8Unorm, ImageSourceConversion.RgbaPass),
            (ImagePixelFormat.Indexed8, ImageSourceConversion.PalettePass),
            (ImagePixelFormat.Nv12, ImageSourceConversion.Nv12Pass),
            (ImagePixelFormat.R10G10B10A2Unorm, ImageSourceConversion.TransferPass),
        ])) {
            var recorders = new Recorders();

            SourceConversionPackage.RegisterAll(packages: recorders.Registry);
            recorders.Registry.RegisterSource(
                factory: _ => new FakeUpload(format: format),
                package: Upload
            );

            using (var runtime = Runtime(new FakePipelineGpu(), recorders, Set(RenderGraphInstance.Source(name: "pattern", producer: "test")), "pattern", new RenderGraphRuntimeGraph[1])) {
                var plan = runtime.Graph(instance: 0)!.Pipeline.Plan;
                var header = ImageSourceUploadLayout.HeaderOf(
                    color: ImageColorEncoding.Srgb,
                    format: format,
                    height: SourceExtent,
                    width: SourceExtent
                );
                var step = Assert.Single(collection: plan.Passes).Package!;

                Assert.Equal(expected: pass, actual: step.Package);
                Assert.Equal(
                    actual: plan.FindResource(name: RenderGraphPackageCatalog.SourceRegion)!.Declaration.SizeBytes,
                    expected: ((ulong)ImageSourceUploadLayout.ByteCount(header: in header))
                );
                Assert.Equal(
                    actual: ShaderPipelineRenderNode.ParseFormat(format: plan.FindResource(name: RenderGraphPackageCatalog.SourceImage)!.Declaration.Format),
                    expected: RenderGraphPackageCatalog.SourceFormatOf(package: pass)
                );

                // The kernels declare the region at binding 1 and the image at binding 2 of the pass group.
                var layout = ShaderPipelineParameterLayout.ForPackage(
                    config: null,
                    members: RenderGraphPackageCatalog.SourceMembers(format: RenderGraphPackageCatalog.SourceFormatOf(package: pass)),
                    package: pass
                );
                var bindings = layout.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)).Resources.ToDictionary(
                    elementSelector: static resource => resource.Binding,
                    keySelector: static resource => resource.Member.Name
                );

                Assert.Equal(expected: (1U, 2U), actual: (bindings[RenderGraphPackageCatalog.SourceRegion], bindings[RenderGraphPackageCatalog.SourceImage]));
            }
        }
    }
    [Fact]
    public void ARefusedUploadRendersNothingAndNamesItsFault() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var upload = new FakeUpload(format: ImagePixelFormat.B8G8R8A8Unorm) {
            Refusal = "the test producer refused its settings",
        };

        SourceConversionPackage.RegisterAll(packages: recorders.Registry);
        recorders.Registry.RegisterSource(
            factory: _ => upload,
            package: Upload
        );

        var set = Set(
            RenderGraphInstance.Source(
                name: "pattern",
                producer: "test"
            ),
            Instance(
                name: "left",
                reads: new RenderGraphRead(Producer: "pattern")
            ),
            Instance(
                name: "right",
                reads: new RenderGraphRead(Producer: "pattern")
            )
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            set,
            "left",
            null!,
            Graph(ScreensGraph(false, "screen"), ("screen", "pattern")),
            Graph(ScreensGraph(false, "screen"), ("screen", "pattern"))
        );

        for (var index = 0; (index < 4); index++) {
            Produce(
                index: index,
                runtime: runtime,
                tick: index
            );
        }

        Assert.Equal(expected: 0L, actual: upload.Writes);
        Assert.Equal(expected: 0UL, actual: runtime.Node(instance: 0).FrameCounter);
        Assert.Equal(
            actual: runtime.UnservedCaptureReasonOf(instance: "pattern"),
            expected: "the instance 'pattern' has produced no output: the test producer refused its settings"
        );
    }

    // An upload of a SourceExtent-square image that counts what it writes.
    private sealed class FakeUpload(ImagePixelFormat format) : IRenderGraphSourceUpload {
        public ImageSourceDescriptor? Descriptor => ((Refusal is null)
            ? new ImageSourceDescriptor(
                Cadence: ImageSourceCadence.Tick,
                Color: ImageColorEncoding.Srgb,
                Content: ImageContentClass.Deterministic,
                Format: format,
                Height: SourceExtent,
                Producer: "test",
                Transport: ImageSourceTransport.Uploaded,
                Width: SourceExtent
            )
            : null);
        public string? Fault => Refusal;
        public string? Refusal { get; init; }
        public long Writes { get; private set; }

        public void Dispose() { }
        public bool TryWrite(long tick, GpuRegion region) {
            Writes++;

            Span<byte> word = stackalloc byte[4];

            BitConverter.TryWriteBytes(destination: word, value: ((uint)tick));

            _ = region.Write(
                bytes: word,
                offset: ImageSourceUploadLayout.HeaderBytes
            );

            return true;
        }
    }
}
