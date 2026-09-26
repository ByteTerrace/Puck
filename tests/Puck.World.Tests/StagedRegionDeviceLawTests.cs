using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Assets;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The device half of the staged residency path. A source instance's region, the node's host buffer port, is created
/// under <see cref="GpuResidencyPolicy.Staged"/> because the device context the runtime is handed reports a memory profile
/// <see cref="GpuResidency.Select"/> stages under: the real device's own profile with no host-visible device-local bytes.
/// Each tick the upload writes a different image into the region, the node records the region's copy through the
/// deployed <c>region-copy</c> kernel ahead of the <c>source-rgba</c> conversion, and a capture of the instance, read
/// back from the device, must equal byte for byte what <see cref="ImageSourceConversion.ToRgba8"/> computes from the
/// region the upload wrote on the tick the capture was served. It runs on the first Vulkan device with a graphics queue,
/// on the first Direct3D 12 hardware adapter and on the software (WARP) renderer, and skips by name where the host has
/// none.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
public sealed class StagedRegionDeviceLawTests {
    private const uint Extent = 16;
    private const string Instance = "pattern";
    private const string Producer = "staged";
    private const int ConvertedFramesBeforeCapture = 3;

    private static readonly ImageSourceUploadHeader Header = ImageSourceUploadLayout.HeaderOf(
        color: ImageColorEncoding.Srgb,
        format: ImagePixelFormat.B8G8R8A8Unorm,
        height: Extent,
        width: Extent
    );

    [Fact]
    public void AStagedRegionReachesANodePassByteExactOnAVulkanDevice() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(StagedRegionDeviceLawTests));

        Convert(
            backend: $"vulkan ({device.Name})",
            device: device,
            hostsOnDirectX: false
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AStagedRegionReachesANodePassByteExactOnADirect3D12Device(bool warp) {
        using var context = (warp ? DirectXTestDevices.Warp() : DirectXTestDevices.Hardware());

        Convert(
            backend: (warp ? "directx (WARP)" : "directx"),
            device: context,
            hostsOnDirectX: true
        );
    }

    // The image the upload writes on a tick, B8G8R8A8 pixel by pixel: every channel moves with the tick, so a copy the
    // node skipped or recorded after the conversion reads another tick's bytes.
    private static void WritePattern(long tick, Span<byte> pixels) {
        for (var pixel = 0; (pixel < (pixels.Length / 4)); pixel++) {
            pixels[(pixel * 4)] = ((byte)((pixel * 7) + tick));
            pixels[((pixel * 4) + 1)] = ((byte)((pixel * 13) + (2 * tick)));
            pixels[((pixel * 4) + 2)] = ((byte)((pixel * 29) + (3 * tick)));
            pixels[((pixel * 4) + 3)] = ((byte)(((pixel * 5) + tick) + 1));
        }
    }
    private static byte[] Expected(long tick) {
        var region = new byte[ImageSourceUploadLayout.ByteCount(header: in Header)];
        var rgba = new byte[checked(((int)((Extent * Extent) * 4U)))];

        ImageSourceUploadLayout.Write(
            header: in Header,
            region: region
        );
        WritePattern(
            pixels: region.AsSpan(start: ImageSourceUploadLayout.HeaderBytes),
            tick: tick
        );
        ImageSourceConversion.ToRgba8(
            region: region,
            rgba: rgba
        );

        return rgba;
    }
    private static RenderGraphFrame Frame(long tick) => new(
        DisplayHeight: ((int)Extent),
        DisplayHertz: 60,
        DisplayWidth: ((int)Extent),
        Footprints: [],
        Index: tick,
        Roots: [new RenderGraphRoot(Height: 1.0, Instance: Instance, Width: 1.0)],
        Tick: tick
    );
    private static void Convert(string backend, IGpuDeviceContext device, bool hostsOnDirectX) {
        var staged = new StagedDeviceContext(device: device);

        Assert.Equal(
            actual: GpuResidency.Select(
                byteCount: ((ulong)ImageSourceUploadLayout.ByteCount(header: in Header)),
                profile: staged.MemoryProfile,
                readersInFlight: true
            ),
            expected: GpuResidencyPolicy.Staged
        );

        var pipelines = new GpuPassPipelineCache();
        var packages = new RenderGraphPackageRecorders(regionCopy: new GpuRegionCopyPass(
            bytecodeExtension: ShaderBytecode.FileExtension(hostsOnDirectX: hostsOnDirectX),
            pipelines: pipelines
        ));
        var upload = new PatternUpload();

        SourceConversionPackage.RegisterAll(packages: packages);
        packages.RegisterSource(
            factory: _ => upload,
            package: (RenderGraphInstance.SourcePackagePrefix + Producer)
        );
        Assert.True(
            condition: RenderGraphInstanceSet.TryCreate(
                instances: [RenderGraphInstance.Source(name: Instance, producer: Producer)],
                refusal: out var setRefusal,
                set: out var set
            ),
            userMessage: setRefusal?.Message
        );
        Assert.True(
            condition: RenderGraphRuntime.TryCreate(
                deviceContext: staged,
                graphs: new RenderGraphRuntimeGraph?[1],
                hostsOnDirectX: hostsOnDirectX,
                packages: packages,
                pipelines: pipelines,
                refusal: out var refusal,
                root: Instance,
                runtime: out var runtime,
                set: set
            ),
            userMessage: refusal?.Message
        );

        using var directory = new TemporaryDirectory();
        byte[] captured;
        long served;

        using (runtime) {
            var tick = 0L;

            // The conversion and the copy pipeline build on the thread pool, so frames run until the node has converted
            // several ticks, each owing the region's copy, and a capture of the instance would be served.
            Assert.True(
                condition: SpinWait.SpinUntil(
                    condition: () => {
                        tick++;
                        _ = runtime.ProduceFrame(
                            context: default,
                            frame: Frame(tick: tick)
                        );

                        return (
                            (runtime.Node(instance: 0).FrameCounter >= ConvertedFramesBeforeCapture) &&
                            (runtime.UnservedCaptureReasonOf(instance: Instance) is null)
                        );
                    },
                    timeout: TimeSpan.FromSeconds(value: 60)
                ),
                userMessage: $"{backend}: the staged source never converted: {runtime.UnservedCaptureReasonOf(instance: Instance)}"
            );

            var node = runtime.Node(instance: 0);

            Assert.Equal(
                actual: node.RegionBytes,
                expected: GpuRegion.BytesOf(
                    byteCount: ImageSourceUploadLayout.ByteCount(header: in Header),
                    policy: GpuResidencyPolicy.Staged,
                    slotCount: ((int)RenderGraphRuntime.DefaultInFlightFrames)
                )
            );

            var request = new FrameCaptureRequest(path: directory.PathOf(name: "staged.png"));

            runtime.CaptureTarget(instance: Instance).RequestCapture(request: request);

            // The node serves a capture on the frame it renders, reading its output back once that frame's submission
            // completes, so the tick the upload last wrote is the one the capture shows.
            for (var frame = 0; ((frame < 16) && !request.Completion.IsCompleted); frame++) {
                tick++;
                _ = runtime.ProduceFrame(
                    context: default,
                    frame: Frame(tick: tick)
                );
            }

            Assert.True(
                condition: request.Completion.IsCompleted,
                userMessage: $"{backend}: the capture was not served: {runtime.UnservedCaptureReasonOf(instance: Instance)}"
            );
            Assert.Null(@object: request.Completion.Result.Error);
            served = upload.LastTick;

            var image = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: request.Path));

            Assert.Equal(
                actual: (image.Width, image.Height),
                expected: (((int)Extent), ((int)Extent))
            );
            captured = image.RgbaPixels;
        }

        Assert.True(
            condition: captured.AsSpan().SequenceEqual(other: Expected(tick: served)),
            userMessage: $"{backend}: the converted image of tick {served} differs from the CPU reference of the staged region."
        );
    }

    // A region's policy is chosen only from the device's memory profile, so a staged region is chosen explicitly by
    // handing the runtime this context: the real device and its services, reporting its own profile with no memory the
    // host can write directly in device-local memory.
    private sealed class StagedDeviceContext(IGpuDeviceContext device) : IGpuDeviceContext {
        public long AdapterLuid => device.AdapterLuid;
        public GpuDeviceCapabilities? Capabilities => device.Capabilities;
        public GpuDeviceIdentity? Identity => device.Identity;
        public GpuMemoryProfile MemoryProfile => (device.MemoryProfile with { HostVisibleDeviceLocalBytes = 0UL });
        public GpuDeviceServices Services => device.Services;

        public void WaitIdle() => device.WaitIdle();
    }
    // A B8G8R8A8 source of Extent square that writes WritePattern's image of each tick it is asked for.
    private sealed class PatternUpload : IRenderGraphSourceUpload {
        private readonly byte[] m_pixels = new byte[checked(((int)((Extent * Extent) * 4U)))];

        public ImageSourceDescriptor? Descriptor { get; } = new ImageSourceDescriptor(
            Cadence: ImageSourceCadence.Tick,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.Deterministic,
            Format: ImagePixelFormat.B8G8R8A8Unorm,
            Height: Extent,
            Producer: Producer,
            Transport: ImageSourceTransport.Uploaded,
            Width: Extent
        );

        public string? Fault => null;
        public long LastTick { get; private set; }

        public void Dispose() { }
        public bool TryWrite(long tick, GpuRegion region) {
            WritePattern(
                pixels: m_pixels,
                tick: tick
            );
            _ = region.Write(
                bytes: m_pixels,
                offset: ImageSourceUploadLayout.HeaderBytes
            );
            LastTick = tick;

            return true;
        }
    }
}
