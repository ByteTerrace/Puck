using System.Diagnostics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Platform.Probes;
using Windows.Win32.Graphics.Dxgi.Common;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed partial class ProbeKernelTests {
    [Fact]
    public void Shipped_average_kernel_reads_float_display_values_and_clamps_before_tint() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var (input, view) = FloatInput(bench: bench, color: [0.25f, 0.5f, 2f, 1f]);
        var targets = bench.CreateSharedRing(slots: 2);
        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            AccumulateBytecode: Bytecode(entry: "accumulate", kernel: "average"),
            AccumulateEntry: "accumulate",
            FinalizeBytecode: Bytecode(entry: "finalize", kernel: "average"),
            FinalizeEntry: "finalize",
            Constants: PackConstants(values: [1f, 0.5f, 0.5f]),
            ChannelCount: 3,
            RateHz: 240U,
            Inputs: [input],
            Trigger: 0,
            Output: ByteOutput(targets: targets)
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [view], boundMask: 1u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        AssertClose(expected: 0.25, actual: ((double)reading[0]), tolerance: 0.001);
        AssertClose(expected: 0.5, actual: ((double)reading[1]), tolerance: 0.001);
        AssertClose(expected: 1.0, actual: ((double)reading[2]), tolerance: 0.001);

        var pixels = bench.ReadBack(target: targets.Targets[reading.OutputSlot]);

        AssertPixelClose(expected: (64, 64, 128), actual: Pixel(pixels: pixels, x: 0, y: 0));
    }
    [Fact]
    public void Shipped_ir_blob_kernel_bounds_float_luminance_before_accumulation() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        // 64 * 64 pixels at 1024, scaled by 1024, sum to 2^32 unless each sample is clamped first.
        var (input, view) = FloatInput(bench: bench, color: [1024f, 0f, 0f, 1f]);
        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            AccumulateBytecode: Bytecode(entry: "accumulate", kernel: "ir-blob"),
            AccumulateEntry: "accumulate",
            FinalizeBytecode: Bytecode(entry: "finalize", kernel: "ir-blob"),
            FinalizeEntry: "finalize",
            Constants: IrBlobConstants(),
            ChannelCount: 4,
            RateHz: 240U,
            Inputs: [input],
            Trigger: 0
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [view], boundMask: 1u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        AssertClose(expected: 0.0, actual: ((double)reading[0]), tolerance: 0.001);
        AssertClose(expected: 0.0, actual: ((double)reading[1]), tolerance: 0.001);
        AssertClose(expected: 1.0, actual: ((double)reading[2]), tolerance: 0.001);
        AssertClose(expected: 1.0, actual: ((double)reading[3]), tolerance: 0.001);
        AssertClose(expected: 1.0, actual: ((double)reading.Confidence), tolerance: 0.001);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void Shipped_faerie_kernel_reads_float_color_and_painting_as_display_values(bool paintingBound) {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var (color, colorView) = FloatInput(bench: bench, color: [0.5f, 0.5f, 0.5f, 1f]);
        var (painting, paintingView) = FloatInput(bench: bench, color: [0.25f, 0.5f, 0.75f, 1f]);
        var dark = bench.CreateFrame(pixels: BuildSquare(inside: [0, 0, 0, 255], outside: [0, 0, 0, 255]));
        var targets = bench.CreateSharedRing(slots: 2);
        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            AccumulateBytecode: Bytecode(entry: "accumulate", kernel: "faerie"),
            AccumulateEntry: "accumulate",
            FinalizeBytecode: Bytecode(entry: "finalize", kernel: "faerie"),
            FinalizeEntry: "finalize",
            Constants: FaerieConstants(
                ambient: 1f,
                intensity: 0f,
                orbitSpeed: 0f,
                paintingX0: -1f, paintingY0: 1f,
                paintingX1: 1f, paintingY1: 1f,
                paintingX2: 1f, paintingY2: -1f,
                paintingX3: -1f, paintingY3: -1f,
                spriteSize: 0.001f
            ),
            ChannelCount: FaerieChannelCount,
            RateHz: 240U,
            Inputs: [color, new ProbeKernelInput.StrobePair(Kind: CameraSensor.Infrared), painting],
            Trigger: 0,
            Output: ByteOutput(targets: targets)
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(
            views: [colorView, dark.View, dark.View, (paintingBound ? paintingView : 0)],
            boundMask: (paintingBound ? 0b111u : 0b011u),
            captureTimestamp: Stopwatch.GetTimestamp()
        ));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));

        var pixels = bench.ReadBack(target: targets.Targets[reading.OutputSlot]);

        AssertPixelClose(expected: (paintingBound ? (64, 128, 191) : (128, 128, 128)), actual: Pixel(pixels: pixels, x: 0, y: 0));
        AssertClose(expected: 0.0, actual: ((double)reading[2]), tolerance: 0.001);
        AssertClose(expected: 0.0, actual: ((double)reading[3]), tolerance: 0.001);
    }

    private static void AssertPixelClose((int R, int G, int B) expected, (int R, int G, int B) actual) {
        Assert.InRange(actual: actual.R, high: (expected.R + 1), low: (expected.R - 1));
        Assert.InRange(actual: actual.G, high: (expected.G + 1), low: (expected.G - 1));
        Assert.InRange(actual: actual.B, high: (expected.B + 1), low: (expected.B - 1));
    }
    private static (ProbeKernelInput.Ring Input, nint View) FloatInput(KernelBench bench, ReadOnlySpan<float> color) {
        var target = bench.CreateSharedTarget(format: DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT);
        var pixels = new Half[((FrameWidth * FrameHeight) * 4)];

        for (var index = 0; (index < pixels.Length); index++) {
            pixels[index] = ((Half)color[(index % 4)]);
        }

        bench.UploadPixels(target: target, pixels: MemoryMarshal.AsBytes(span: pixels.AsSpan()));

        var input = new ProbeKernelInput.Ring(
            Width: FrameWidth,
            Height: FrameHeight,
            Format: GpuPixelFormat.R16G16B16A16Float,
            SharedTargetHandles: [target.SharedHandle],
            Slots: new SingleSlotPublication(),
            SharedFenceHandle: 0
        );

        return (input, bench.OpenSharedView(sharedHandle: target.SharedHandle));
    }
    private static ProbeKernelOutput ByteOutput(KernelBench.SharedRing targets) => new(
        Width: FrameWidth,
        Height: FrameHeight,
        TargetFormat: GpuPixelFormat.R8G8B8A8Unorm,
        SharedTargetHandles: targets.Handles,
        Slots: targets.Slots,
        SharedFenceHandle: 0
    );
}
