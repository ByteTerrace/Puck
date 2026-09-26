using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// Machine sources: a machine's video output is an uploaded source (MachineVideoSourceUpload), due once per completed tick,
// which writes the output's latest frame into the instance's region in the format the output declares and converts it
// once however many screens read it. It states the image it last wrote, which the exact verdict holds a capture to.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string MachineSource = "source.machine";

    // A machine source read by two screens, over a fake output.
    private static (RenderGraphRuntime Runtime, MachineVideoSourceUpload Upload) MachineScene(FakePipelineGpu gpu, IMachineVideoOutput output) {
        var recorders = new Recorders();
        MachineVideoSourceUpload? upload = null;

        SourceConversionPackage.RegisterAll(packages: recorders.Registry);
        recorders.Registry.RegisterSource(
            factory: context => upload = new MachineVideoSourceUpload(
                name: context.Instance,
                output: () => output,
                producer: "machine"
            ),
            package: MachineSource
        );

        var runtime = Runtime(
            gpu,
            recorders,
            Set(
                RenderGraphInstance.Source(
                    name: "pattern",
                    producer: "machine"
                ),
                Instance(
                    name: "left",
                    reads: new RenderGraphRead(Producer: "pattern")
                ),
                Instance(
                    name: "right",
                    reads: new RenderGraphRead(Producer: "pattern")
                )
            ),
            "left",
            null!,
            Graph(ScreensGraph(false, "screen"), ("screen", "pattern")),
            Graph(ScreensGraph(false, "screen"), ("screen", "pattern"))
        );

        return (runtime, upload!);
    }
    // Produces frames at rising ticks until the machine source's conversion has built.
    private static long Settle(RenderGraphRuntime runtime) {
        var index = 0L;

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
            userMessage: "The machine source's conversion never built."
        );

        return index;
    }

    [Fact]
    public void AMachineSourceWritesOneRegionPerCompletedTickAndConvertsOnceHoweverManyScreensReadIt() {
        foreach (var (format, pass) in ((ReadOnlySpan<(ImagePixelFormat, string)>)[
            (ImagePixelFormat.R8G8B8A8Unorm, ImageSourceConversion.RgbaPass),
            (ImagePixelFormat.Indexed8, ImageSourceConversion.PalettePass),
        ])) {
            var output = new FakeMachineOutput(format: format);

            var (runtime, upload) = MachineScene(
                gpu: new FakePipelineGpu(),
                output: output
            );

            using (runtime) {
                var source = runtime.Instances.IndexOf(name: "pattern");

                Assert.Same(expected: upload, actual: runtime.Source(instance: source));
                Assert.Null(@object: runtime.Producer(instance: source));
                Assert.Equal(
                    actual: (upload.Descriptor!.Cadence, upload.Descriptor.Content, upload.Descriptor.Transport, upload.Descriptor.Format),
                    expected: (ImageSourceCadence.Tick, ImageContentClass.Deterministic, ImageSourceTransport.Uploaded, format)
                );

                var index = Settle(runtime: runtime);

                Assert.Equal(expected: pass, actual: Assert.Single(collection: runtime.Graph(instance: source)!.Pipeline.Plan.Passes).Package!.Package);

                var node = runtime.Node(instance: source);
                var submitted = node.FrameCounter;
                var writes = output.Writes;

                // A completed tick each frame: one region write and one conversion, however many screens read it.
                for (var frame = 0; (frame < 5); frame++, index++) {
                    output.Advance();
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

                Assert.Equal(expected: (5UL, 5), actual: ((node.FrameCounter - submitted), (output.Writes - writes)));

                // One tick held for three frames writes and converts once, and a frame no screen shows it in, neither.
                submitted = node.FrameCounter;
                writes = output.Writes;

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
                Assert.Equal(expected: (1UL, 1), actual: ((node.FrameCounter - submitted), (output.Writes - writes)));
            }
        }
    }
    [Fact]
    public void AMachineSourceStatesTheImageItLastWroteAndTheExactVerdictFailsOnAOnePixelChange() {
        var output = new FakeMachineOutput(format: ImagePixelFormat.Indexed8);

        var (runtime, upload) = MachineScene(
            gpu: new FakePipelineGpu(),
            output: output
        );

        using (runtime) {
            IImageSourceReference reference = upload;
            var rgba = new byte[((SourceExtent * SourceExtent) * 4)];

            Assert.False(condition: reference.TryWriteReference(rgba: rgba, stamp: out _));

            var index = Settle(runtime: runtime);

            output.Advance();
            Produce(
                index: index,
                runtime: runtime,
                tick: 700L
            );
            Assert.True(condition: reference.TryWriteReference(rgba: rgba, stamp: out var stamp));
            Assert.Equal(expected: 700UL, actual: stamp.Tick);

            // The reference is the output's frame through its palette, computed here independently of the conversion.
            var expected = output.Rgba();

            Assert.Equal(actual: rgba, expected: expected);
            Assert.True(condition: ImageSourceVerdict.Compare(actual: expected, descriptor: reference.Descriptor, expected: rgba).Holds);

            var readBack = expected.ToArray();

            readBack[((((3 * SourceExtent) + 11) * 4) + 1)] ^= 0x01;

            var verdict = ImageSourceVerdict.Compare(
                actual: readBack,
                descriptor: reference.Descriptor,
                expected: rgba
            );

            Assert.Equal(expected: (1L, 11, 3), actual: (verdict.Mismatches, verdict.FirstX, verdict.FirstY));
            Assert.False(condition: verdict.Holds);
        }
    }
    [Fact]
    public void ARetainedMachineSourceWhoseOutputChangesExtentRebuildsAndConvertsAtTheNewExtent() {
        var output = new ResizableMachineOutput {
            Height = ((int)SourceExtent),
            Width = ((int)SourceExtent),
        };

        var (runtime, upload) = MachineScene(
            gpu: new FakePipelineGpu(),
            output: output
        );

        using (runtime) {
            var source = runtime.Instances.IndexOf(name: "pattern");
            var index = Settle(runtime: runtime);
            var before = runtime.Node(instance: source);

            // The machine is replaced by one whose output has another extent; the source keeps its name and settings.
            output.Width = 24;
            output.Height = 20;

            var writes = output.Writes;

            Assert.True(
                condition: SpinWait.SpinUntil(
                    condition: () => {
                        Produce(
                            index: index,
                            runtime: runtime,
                            tick: index
                        );
                        index++;

                        return (runtime.IsSettled && (output.Writes > writes));
                    },
                    timeout: TimeSpan.FromSeconds(value: 30)
                ),
                userMessage: "The machine source never converted at its output's new extent."
            );

            var header = ImageSourceUploadLayout.HeaderOf(
                color: ImageColorEncoding.Srgb,
                format: ImagePixelFormat.R8G8B8A8Unorm,
                height: 20U,
                width: 24U
            );
            var plan = runtime.Graph(instance: source)!.Pipeline.Plan;

            // The source was made again for the new extent under its kept name: a new upload and node, and a region and
            // image of that extent, converted and read by both screens on the next completed tick.
            var rebuilt = Assert.IsType<MachineVideoSourceUpload>(@object: runtime.Source(instance: source));

            Assert.NotSame(actual: rebuilt, expected: upload);
            Assert.NotSame(expected: before, actual: runtime.Node(instance: source));
            Assert.Equal(expected: (24U, 20U), actual: (rebuilt.Descriptor!.Width, rebuilt.Descriptor.Height));
            Assert.Null(@object: rebuilt.Fault);
            Assert.Equal(
                actual: plan.FindResource(name: RenderGraphPackageCatalog.SourceRegion)!.Declaration.SizeBytes,
                expected: ((ulong)ImageSourceUploadLayout.ByteCount(header: in header))
            );

            var submitted = runtime.Node(instance: source).FrameCounter;

            Produce(
                index: index,
                runtime: runtime,
                tick: index
            );
            Assert.Equal(expected: 1UL, actual: (runtime.Node(instance: source).FrameCounter - submitted));
            Assert.Equal(
                actual: runtime.Latest!.Renders.Count(predicate: rendered => (rendered == source)),
                expected: 1
            );

            var rgba = new byte[((24 * 20) * 4)];

            Assert.True(condition: ((IImageSourceReference)rebuilt).TryWriteReference(rgba: rgba, stamp: out _));
            Assert.Equal(expected: output.Rgba(), actual: rgba);
        }
    }
    [Fact]
    public void AMachineSourceWhoseOutputStopsRunningFaultsByNameAndWritesNothing() {
        var output = new FakeMachineOutput(format: ImagePixelFormat.R8G8B8A8Unorm);
        var running = true;
        var upload = new MachineVideoSourceUpload(
            name: "cabinet",
            output: () => (running ? output : null),
            producer: "machine"
        );

        var gpu = new FakeGpuDevice(reportVersion: 0);

        using var region = new GpuRegion(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: ImageSourceUploadLayout.ByteCount(header: ImageSourceUploadLayout.HeaderOf(
                color: ImageColorEncoding.Srgb,
                format: ImagePixelFormat.R8G8B8A8Unorm,
                height: SourceExtent,
                width: SourceExtent
            )),
            copyPipeline: null,
            memory: GpuHostVisibleMemory.Host,
            name: default,
            policy: GpuResidencyPolicy.InPlace,
            recorder: gpu.Services.Recorder,
            slotCount: 1
        );

        Assert.Null(@object: upload.Fault);
        Assert.True(condition: upload.TryWrite(region: region, tick: 1L));
        Assert.Equal(expected: 1, actual: output.Writes);

        running = false;

        Assert.False(condition: upload.TryWrite(region: region, tick: 2L));
        Assert.Equal(expected: "machine output 'cabinet' is not running", actual: upload.Fault);
        Assert.Equal(expected: 1, actual: output.Writes);
        Assert.Equal(
            actual: new MachineVideoSourceUpload(name: "cabinet", output: static () => null, producer: "machine").Fault,
            expected: "machine output 'cabinet' is not running"
        );
    }

    // An RGBA8 machine output whose extent a test changes, as a machine replaced by another would; each pixel carries its
    // coordinates.
    private sealed class ResizableMachineOutput : IMachineVideoOutput {
        public Vector3 EmittedLight => Vector3.Zero;
        public ImagePixelFormat Format => ImagePixelFormat.R8G8B8A8Unorm;
        public int Height { get; set; }
        public int Width { get; set; }
        public int Writes { get; private set; }

        public byte[] Rgba() {
            var rgba = new byte[((Width * Height) * 4)];

            for (var pixel = 0; (pixel < (Width * Height)); pixel++) {
                rgba[(pixel * 4)] = ((byte)(pixel % Width));
                rgba[((pixel * 4) + 1)] = ((byte)(pixel / Width));
                rgba[((pixel * 4) + 2)] = ((byte)Width);
                rgba[((pixel * 4) + 3)] = 0xFF;
            }

            return rgba;
        }
        public long WriteFrame(Span<byte> region) {
            Writes++;
            Rgba().CopyTo(destination: region[ImageSourceUploadLayout.HeaderBytes..]);

            return Writes;
        }
    }
    // A SourceExtent-square machine output whose frame is a function of its sequence: RGBA8 pixels, or indices into a
    // fixed palette.
    private sealed class FakeMachineOutput(ImagePixelFormat format) : IMachineVideoOutput {
        private readonly ImageSourceUploadHeader m_header = ImageSourceUploadLayout.HeaderOf(
            color: ImageColorEncoding.Srgb,
            format: format,
            height: SourceExtent,
            width: SourceExtent
        );

        public Vector3 EmittedLight => Vector3.Zero;
        public ImagePixelFormat Format => format;
        public int Height => ((int)SourceExtent);

        public long Sequence { get; private set; } = 1L;

        public int Width => ((int)SourceExtent);
        public int Writes { get; private set; }

        private static (byte R, byte G, byte B) PaletteEntry(int index) => (((byte)index), ((byte)(255 - index)), ((byte)((index * 7) & 0xFF)));
        private byte Index(int x, int y) => ((byte)(((x + (y * 3)) + Sequence) & 0xFF));

        public void Advance() => Sequence++;
        // The frame's pixels as the conversion must write them, RGBA8 row by row.
        public byte[] Rgba() {
            var rgba = new byte[((SourceExtent * SourceExtent) * 4)];

            for (var y = 0; (y < SourceExtent); y++) {
                for (var x = 0; (x < SourceExtent); x++) {
                    var offset = ((((y * ((int)SourceExtent)) + x)) * 4);

                    var (r, g, b) = ((format == ImagePixelFormat.Indexed8)
                        ? PaletteEntry(index: Index(x: x, y: y))
                        : (((byte)x), ((byte)y), ((byte)Sequence)));

                    rgba[offset] = r;
                    rgba[(offset + 1)] = g;
                    rgba[(offset + 2)] = b;
                    rgba[(offset + 3)] = 0xFF;
                }
            }

            return rgba;
        }
        public long WriteFrame(Span<byte> region) {
            Writes++;

            if (format == ImagePixelFormat.Indexed8) {
                var palette = ImageSourceUploadLayout.PlaneOf(header: in m_header, plane: 0, region: region);

                for (var entry = 0; (entry < ImageSourceUploadLayout.PaletteEntries); entry++) {
                    var (r, g, b) = PaletteEntry(index: entry);

                    palette[(entry * 4)] = r;
                    palette[((entry * 4) + 1)] = g;
                    palette[((entry * 4) + 2)] = b;
                    palette[((entry * 4) + 3)] = 0xFF;
                }

                var indices = ImageSourceUploadLayout.PlaneOf(header: in m_header, plane: 1, region: region);

                for (var y = 0; (y < SourceExtent); y++) {
                    for (var x = 0; (x < SourceExtent); x++) {
                        indices[((int)((y * m_header.Plane1Stride) + x))] = Index(x: x, y: y);
                    }
                }
            } else {
                Rgba().CopyTo(destination: ImageSourceUploadLayout.PlaneOf(header: in m_header, plane: 0, region: region));
            }

            return Sequence;
        }
    }
}
