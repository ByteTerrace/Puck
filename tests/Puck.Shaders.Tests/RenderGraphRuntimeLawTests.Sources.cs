using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Testing;

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
    /// <summary>On a device whose memory stages the source's region, the source's node leases the region-copy pipeline,
    /// records the region's copy ahead of the conversion, and the device-local buffer the conversion reads holds exactly
    /// the bytes the upload wrote, header and image, after every tick, under the model that runs the copy kernel.</summary>
    [Fact]
    public void AStagedSourceRegionReachesItsConversionByteExact() {
        var gpu = new UploadModelGpu(reportVersion: 0);
        var recorders = new Recorders();
        var header = ImageSourceUploadLayout.HeaderOf(
            color: ImageColorEncoding.Srgb,
            format: ImagePixelFormat.B8G8R8A8Unorm,
            height: SourceExtent,
            width: SourceExtent
        );
        var byteCount = ImageSourceUploadLayout.ByteCount(header: in header);
        var expected = new byte[byteCount];

        ImageSourceUploadLayout.Write(
            header: in header,
            region: expected
        );
        SourceConversionPackage.RegisterAll(packages: recorders.Registry);
        recorders.Registry.RegisterSource(
            factory: _ => new FakeUpload(format: ImagePixelFormat.B8G8R8A8Unorm),
            package: Upload
        );

        Assert.Equal(
            actual: GpuResidency.Select(profile: gpu.MemoryProfile, byteCount: ((ulong)byteCount), readersInFlight: true),
            expected: GpuResidencyPolicy.Staged
        );

        using var runtime = Runtime(gpu, recorders, Set(RenderGraphInstance.Source(name: "pattern", producer: "test")), "pattern", new RenderGraphRuntimeGraph[1]);

        var tick = 0L;
        var converted = 0UL;

        // The graph installs and the copy pipeline builds on the thread pool, so frames run until three conversions have
        // been checked.
        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    tick++;

                    var frame = new RenderGraphFrame(
                        DisplayHeight: Display,
                        DisplayHertz: 60,
                        DisplayWidth: Display,
                        Footprints: [],
                        Index: tick,
                        Roots: [new RenderGraphRoot(Height: 1.0, Instance: "pattern", Width: 1.0)],
                        Tick: tick
                    );

                    _ = runtime.ProduceFrame(
                        context: default,
                        frame: in frame
                    );

                    if (runtime.Node(instance: 0).FrameCounter == converted) {
                        return false;
                    }

                    converted = runtime.Node(instance: 0).FrameCounter;
                    // FakeUpload writes the tick as the first image word.
                    BitConverter.TryWriteBytes(destination: expected.AsSpan(start: ImageSourceUploadLayout.HeaderBytes), value: ((uint)tick));
                    Assert.Equal(
                        actual: gpu.DeviceLocal(sizeBytes: ((ulong)byteCount)),
                        expected: expected
                    );

                    return (converted >= 3UL);
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The staged source never converted three times."
        );
        Assert.True(condition: (gpu.UploadCopies > 1));

        // The region is the graph's host buffer port: its copy sets are the graph's one copy pool, stated and admitted
        // with the graph, and its buffers are the node's region bytes, which the live budget reports.
        var node = runtime.Node(instance: 0);
        var regionBytes = GpuRegion.BytesOf(
            byteCount: byteCount,
            policy: GpuResidencyPolicy.Staged,
            slotCount: ((int)RenderGraphRuntime.DefaultInFlightFrames)
        );

        Assert.Equal(
            actual: gpu.PoolsCreated,
            expected: ShaderPipelineRenderNode.DescriptorPools(
                inFlight: RenderGraphRuntime.DefaultInFlightFrames,
                plan: node.Plan!,
                preview: false,
                stagedRegions: 1
            )
        );
        Assert.Equal(expected: regionBytes, actual: node.RegionBytes);
        Assert.True(condition: (node.InstalledAccount.SteadyBytes >= regionBytes));
        Assert.Contains(
            actualString: LiveBudget(budget: new RenderGraphLiveBudget(), runtime: runtime),
            expectedSubstring: $" regions {regionBytes} bytes"
        );
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
