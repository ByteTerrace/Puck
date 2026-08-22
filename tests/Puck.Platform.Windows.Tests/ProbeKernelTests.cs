using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Presentation;
using Puck.Platform.Probes;
using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>Drives the shipped kernels through the real kernel ABI on a private device — the constant buffers, both
/// dispatches, the output ring, the staging readback, and a ring publication — against synthetic frames whose
/// answers are known. Skips on a machine with no hardware adapter.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class ProbeKernelTests {
    private const int FrameHeight = KernelBench.FrameHeight;
    private const int FrameWidth = KernelBench.FrameWidth;
    // The bright square: x in [48, 56), y in [8, 16) — top-right of the frame.
    private const int SquareLeft = 48;
    private const int SquareSize = 8;
    private const int SquareTop = 8;
    private const int FaerieChannelCount = 5;
    private const int IrMarkerChannelCount = 8;

    [Fact]
    public void Shipped_ir_blob_kernel_measures_a_bright_square() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var lit = bench.CreateFrame(pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "ir-blob")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: IrBlobConstants(),
            ChannelCount: 4,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput.Sensor(Kind: CameraSensor.Infrared)],
            Trigger: CameraSensor.Infrared
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [lit.View], boundMask: 1u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.Equal(expected: 1L, actual: kernel.Cycles);
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        Assert.Equal(expected: -1, actual: reading.OutputSlot);
        Assert.True(condition: (reading.CompletionTimestamp >= reading.CaptureTimestamp));

        AssertBlobCentroid(reading: reading);
    }
    [Fact]
    public void A_cycle_inside_the_rate_period_is_skipped() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var lit = bench.CreateFrame(pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "ir-blob")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: new byte[16],
            ChannelCount: 4,
            RateHz: 1U,
            Inputs: [new ProbeKernelInput.Sensor(Kind: CameraSensor.Infrared)],
            Trigger: CameraSensor.Infrared
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [lit.View], boundMask: 1u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.False(condition: kernel.TryRun(views: [lit.View], boundMask: 1u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.Equal(expected: 1L, actual: kernel.Cycles);
    }
    [Fact]
    public void A_ring_input_reads_the_slot_the_publication_names() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var sharedRing = bench.CreateSharedRing(slots: 2);

        Assert.True(condition: sharedRing.Slots.TryReserveWriteSlot(slot: out var writeSlot));
        bench.UploadPixels(target: sharedRing.Targets[writeSlot], pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        sharedRing.Slots.Publish(slot: writeSlot);

        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "ir-blob")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: IrBlobConstants(),
            ChannelCount: 4,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput.Ring(Width: FrameWidth, Height: FrameHeight, Format: SurfaceFormat.R8G8B8A8Unorm, SharedTargetHandles: sharedRing.Handles, Slots: sharedRing.Slots)],
            Trigger: CameraSensor.Infrared
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: sharedRing.Slots.TryAcquireLatest(slot: out var readSlot));

        try {
            var view = bench.OpenSharedView(sharedHandle: sharedRing.Handles[readSlot]);

            Assert.True(condition: kernel.TryRun(views: [view], boundMask: 1u, captureTimestamp: Stopwatch.GetTimestamp()));
        } finally {
            sharedRing.Slots.Release(slot: readSlot);
        }

        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        AssertBlobCentroid(reading: reading);
    }
    [Fact]
    public void An_unbound_input_runs_with_its_boundMask_bit_clear() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: BoundMaskProbeSource,
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: ReadOnlyMemory<byte>.Empty,
            ChannelCount: 1,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput.Unbound()],
            Trigger: CameraSensor.Infrared
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [0], boundMask: 0u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        Assert.Equal(expected: 0.0, actual: ((double)reading[0]));
    }
    [Fact]
    public void Shipped_faerie_kernel_relights_into_its_output_ring() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var color = bench.CreateFrame(pixels: BuildSquare(inside: [128, 128, 128, 255], outside: [128, 128, 128, 255]));
        var lit = bench.CreateFrame(pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var unlit = bench.CreateFrame(pixels: BuildSquare(inside: [0, 0, 0, 255], outside: [0, 0, 0, 255]));
        var targets = new[] { bench.CreateSharedTarget(), bench.CreateSharedTarget() };
        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: targets.Length);

        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "faerie")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: FaerieDefaults(),
            ChannelCount: 4,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput.Sensor(Kind: CameraSensor.Color), new ProbeKernelInput.StrobePair(Kind: CameraSensor.Infrared)],
            Trigger: CameraSensor.Color,
            Output: new ProbeKernelOutput(
                Width: FrameWidth,
                Height: FrameHeight,
                TargetFormat: SurfaceFormat.R8G8B8A8Unorm,
                SharedTargetHandles: [targets[0].SharedHandle, targets[1].SharedHandle],
                Slots: slots
            )
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [color.View, lit.View, unlit.View], boundMask: 0b11u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        Assert.Equal(expected: 4, actual: reading.ChannelCount);
        Assert.InRange(actual: reading.OutputSlot, low: 0, high: (targets.Length - 1));
        Assert.Equal(expected: reading.OutputSlot, actual: slots.LatestSlot);

        const double ExpectedCoverage = ((double)(SquareSize * SquareSize)) / (FrameWidth * FrameHeight);

        Assert.InRange(actual: ((double)reading[0]), low: -1.0, high: 1.0);
        Assert.InRange(actual: ((double)reading[1]), low: -1.0, high: 1.0);
        Assert.InRange(actual: ((double)reading[2]), low: 0.99, high: 1.0);
        Assert.InRange(actual: ((double)reading[3]), low: (ExpectedCoverage - 0.002), high: (ExpectedCoverage + 0.002));

        // The relit frame: the strobe-lit square is brighter than the unresponsive wall around it, which keeps only
        // ambient plus the light's spill.
        var pixels = bench.ReadBack(target: targets[reading.OutputSlot]);
        var inside = Luminance(pixels: pixels, x: (SquareLeft + (SquareSize / 2)), y: (SquareTop + (SquareSize / 2)));
        var outside = Luminance(pixels: pixels, x: 8, y: (FrameHeight - 8));

        Assert.True(condition: (inside > outside), userMessage: $"inside {inside} should outshine outside {outside}");
    }
    [Fact]
    public void Shipped_faerie_kernel_relights_with_the_painting_socket_unbound() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var color = bench.CreateFrame(pixels: BuildSquare(inside: [128, 128, 128, 255], outside: [128, 128, 128, 255]));
        var lit = bench.CreateFrame(pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var unlit = bench.CreateFrame(pixels: BuildSquare(inside: [0, 0, 0, 255], outside: [0, 0, 0, 255]));
        var targets = new[] { bench.CreateSharedTarget(), bench.CreateSharedTarget() };
        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: targets.Length);

        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "faerie")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: FaerieDefaults(),
            ChannelCount: FaerieChannelCount,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput.Sensor(Kind: CameraSensor.Color), new ProbeKernelInput.StrobePair(Kind: CameraSensor.Infrared), new ProbeKernelInput.Unbound()],
            Trigger: CameraSensor.Color,
            Output: new ProbeKernelOutput(
                Width: FrameWidth,
                Height: FrameHeight,
                TargetFormat: SurfaceFormat.R8G8B8A8Unorm,
                SharedTargetHandles: [targets[0].SharedHandle, targets[1].SharedHandle],
                Slots: slots
            )
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [color.View, lit.View, unlit.View, 0], boundMask: 0b011u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        Assert.Equal(expected: FaerieChannelCount, actual: reading.ChannelCount);
        Assert.InRange(actual: reading.OutputSlot, low: 0, high: (targets.Length - 1));

        const double ExpectedCoverage = ((double)(SquareSize * SquareSize)) / (FrameWidth * FrameHeight);

        Assert.InRange(actual: ((double)reading[0]), low: -1.0, high: 1.0);
        Assert.InRange(actual: ((double)reading[1]), low: -1.0, high: 1.0);
        Assert.InRange(actual: ((double)reading[2]), low: 0.99, high: 1.0);
        Assert.InRange(actual: ((double)reading[3]), low: (ExpectedCoverage - 0.002), high: (ExpectedCoverage + 0.002));
        // journey defaults to 0, so the light never enters the (unbound) painting.
        Assert.Equal(expected: 0.0, actual: ((double)reading[4]));

        var expectedConfidence = Math.Min(1.0, (ExpectedCoverage / 0.02));

        Assert.InRange(actual: ((double)reading.Confidence), low: (expectedConfidence - 0.01), high: (expectedConfidence + 0.01));

        var pixels = bench.ReadBack(target: targets[reading.OutputSlot]);
        var inside = Luminance(pixels: pixels, x: (SquareLeft + (SquareSize / 2)), y: (SquareTop + (SquareSize / 2)));
        var outside = Luminance(pixels: pixels, x: 8, y: (FrameHeight - 8));

        Assert.True(condition: (inside > outside), userMessage: $"inside {inside} should outshine outside {outside}");
    }
    [Fact]
    public void Shipped_faerie_kernel_shows_the_painting_when_bound() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var color = bench.CreateFrame(pixels: BuildSquare(inside: [128, 128, 128, 255], outside: [128, 128, 128, 255]));
        var lit = bench.CreateFrame(pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var unlit = bench.CreateFrame(pixels: BuildSquare(inside: [0, 0, 0, 255], outside: [0, 0, 0, 255]));
        var paintingRing = bench.CreateSharedRing(slots: 2);

        Assert.True(condition: paintingRing.Slots.TryReserveWriteSlot(slot: out var paintingWriteSlot));

        var greenPixels = new byte[FrameWidth * FrameHeight * 4];

        for (var index = 0; (index < greenPixels.Length); index += 4) {
            greenPixels[index] = 0;
            greenPixels[index + 1] = 255;
            greenPixels[index + 2] = 0;
            greenPixels[index + 3] = 255;
        }

        bench.UploadPixels(target: paintingRing.Targets[paintingWriteSlot], pixels: greenPixels);
        paintingRing.Slots.Publish(slot: paintingWriteSlot);

        Assert.True(condition: paintingRing.Slots.TryAcquireLatest(slot: out var paintingReadSlot));

        var targets = new[] { bench.CreateSharedTarget(), bench.CreateSharedTarget() };
        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: targets.Length);

        var ring = new ProbeReadingRing();
        // The quad covers the wall's top-left region, x in [-0.9, -0.1] and y in [0.1, 0.9] in frame coordinates —
        // clear of the bright square at top-right (frame x in [0.5, 0.75], y in [0.75, 1]).
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "faerie")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: FaerieConstants(
                ambient: 0.6f,
                paintingX0: -0.9f,
                paintingY0: 0.9f,
                paintingX1: -0.1f,
                paintingY1: 0.9f,
                paintingX2: -0.1f,
                paintingY2: 0.1f,
                paintingX3: -0.9f,
                paintingY3: 0.1f,
                paintingOpacity: 1.0f,
                journey: 0.0f
            ),
            ChannelCount: FaerieChannelCount,
            RateHz: 240U,
            Inputs: [
                new ProbeKernelInput.Sensor(Kind: CameraSensor.Color),
                new ProbeKernelInput.StrobePair(Kind: CameraSensor.Infrared),
                new ProbeKernelInput.Ring(Width: FrameWidth, Height: FrameHeight, Format: SurfaceFormat.R8G8B8A8Unorm, SharedTargetHandles: paintingRing.Handles, Slots: paintingRing.Slots),
            ],
            Trigger: CameraSensor.Color,
            Output: new ProbeKernelOutput(
                Width: FrameWidth,
                Height: FrameHeight,
                TargetFormat: SurfaceFormat.R8G8B8A8Unorm,
                SharedTargetHandles: [targets[0].SharedHandle, targets[1].SharedHandle],
                Slots: slots
            )
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);
        nint paintingView;

        try {
            paintingView = bench.OpenSharedView(sharedHandle: paintingRing.Handles[paintingReadSlot]);

            Assert.True(condition: kernel.TryRun(views: [color.View, lit.View, unlit.View, paintingView], boundMask: 0b111u, captureTimestamp: Stopwatch.GetTimestamp()));
        } finally {
            paintingRing.Slots.Release(slot: paintingReadSlot);
        }

        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        Assert.InRange(actual: reading.OutputSlot, low: 0, high: (targets.Length - 1));

        var pixels = bench.ReadBack(target: targets[reading.OutputSlot]);

        // Inside the quad, on the wall (away from the bright square): the painting's solid green replaces the
        // wall's own albedo, so green dominates the pixel.
        AssertGreenDominant(pixels: pixels, x: 16, y: 16);
        // Inside the bright square (the subject occludes the painting): ordinary subject shading, not green.
        AssertNotGreenDominant(pixels: pixels, x: (SquareLeft + (SquareSize / 2)), y: (SquareTop + (SquareSize / 2)));
        // A wall pixel outside the quad: no painting, no subject — ambient dominates, so it reads roughly neutral.
        AssertRoughlyNeutral(pixels: pixels, x: 8, y: (FrameHeight - 8), tolerance: 40);
    }
    [Theory]
    [InlineData(1.0f, 1.0)]
    [InlineData(0.5f, 0.0)]
    public void Shipped_faerie_kernel_reports_portal_from_journey(float journey, double expectedPortal) {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var color = bench.CreateFrame(pixels: BuildSquare(inside: [128, 128, 128, 255], outside: [128, 128, 128, 255]));
        var lit = bench.CreateFrame(pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var unlit = bench.CreateFrame(pixels: BuildSquare(inside: [0, 0, 0, 255], outside: [0, 0, 0, 255]));
        var targets = new[] { bench.CreateSharedTarget(), bench.CreateSharedTarget() };
        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: targets.Length);

        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "faerie")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: FaerieConstants(journey: journey, portalThreshold: 0.85f),
            ChannelCount: FaerieChannelCount,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput.Sensor(Kind: CameraSensor.Color), new ProbeKernelInput.StrobePair(Kind: CameraSensor.Infrared), new ProbeKernelInput.Unbound()],
            Trigger: CameraSensor.Color,
            Output: new ProbeKernelOutput(
                Width: FrameWidth,
                Height: FrameHeight,
                TargetFormat: SurfaceFormat.R8G8B8A8Unorm,
                SharedTargetHandles: [targets[0].SharedHandle, targets[1].SharedHandle],
                Slots: slots
            )
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [color.View, lit.View, unlit.View, 0], boundMask: 0b011u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        Assert.Equal(expected: expectedPortal, actual: ((double)reading[4]));
    }
    [Fact]
    public void Shipped_ir_marker_kernel_measures_an_axis_aligned_rectangle() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        const int RectangleLeft = 40;
        const int RectangleTop = 8;
        const int RectangleWidth = 16;
        const int RectangleHeight = 16;

        var lit = bench.CreateFrame(pixels: BuildRectangle(left: RectangleLeft, top: RectangleTop, width: RectangleWidth, height: RectangleHeight, inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var unlit = bench.CreateFrame(pixels: BuildRectangle(left: RectangleLeft, top: RectangleTop, width: RectangleWidth, height: RectangleHeight, inside: [0, 0, 0, 255], outside: [0, 0, 0, 255]));
        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "ir-marker")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: IrMarkerConstants(),
            ChannelCount: IrMarkerChannelCount,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput.StrobePair(Kind: CameraSensor.Infrared)],
            Trigger: CameraSensor.Infrared
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [lit.View, unlit.View], boundMask: 1u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));

        var centreU = ((RectangleLeft + (RectangleWidth / 2.0)) / FrameWidth);
        var centreV = ((RectangleTop + (RectangleHeight / 2.0)) / FrameHeight);
        var halfU = ((RectangleWidth / 2.0) / FrameWidth);
        var halfV = ((RectangleHeight / 2.0) / FrameHeight);
        var expected = ExpectedMarkerCorners(centreU: centreU, centreV: centreV, majorAxisU: 1.0, majorAxisV: 0.0, halfMajor: halfU, halfMinor: halfV);

        AssertMarkerCorners(reading: reading, expected: expected, tolerance: 0.06);

        Assert.InRange(actual: ((double)reading.Confidence), low: 0.99, high: 1.0);
    }
    [Fact]
    public void Shipped_ir_marker_kernel_measures_a_rotated_rectangle() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        const double CentreU = 0.5;
        const double CentreV = 0.5;
        const double HalfMajor = 0.2;
        const double HalfMinor = 0.1;
        var angle = (Math.PI / 6.0);

        var lit = bench.CreateFrame(pixels: BuildRotatedRectangle(centreU: CentreU, centreV: CentreV, halfExtentMajor: HalfMajor, halfExtentMinor: HalfMinor, angleRadians: angle, inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var unlit = bench.CreateFrame(pixels: BuildRotatedRectangle(centreU: CentreU, centreV: CentreV, halfExtentMajor: HalfMajor, halfExtentMinor: HalfMinor, angleRadians: angle, inside: [0, 0, 0, 255], outside: [0, 0, 0, 255]));
        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "ir-marker")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: IrMarkerConstants(),
            ChannelCount: IrMarkerChannelCount,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput.StrobePair(Kind: CameraSensor.Infrared)],
            Trigger: CameraSensor.Infrared
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(views: [lit.View, unlit.View], boundMask: 1u, captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));

        // The rectangle's own local axes are known by construction; feeding the major axis's uv-space direction
        // through the kernel's documented corner rule (non-negative u component) gives the expected corners
        // without assuming which physical corner ends up "top-left".
        var majorAxisU = Math.Cos(angle);
        var majorAxisV = Math.Sin(angle);
        var expected = ExpectedMarkerCorners(centreU: CentreU, centreV: CentreV, majorAxisU: majorAxisU, majorAxisV: majorAxisV, halfMajor: HalfMajor, halfMinor: HalfMinor);

        AssertMarkerCorners(reading: reading, expected: expected, tolerance: 0.08);

        Assert.InRange(actual: ((double)reading.Confidence), low: 0.99, high: 1.0);
    }

    // A minimal puck.probe.v1 kernel that ignores its unbound socket and writes the frame constants' boundMask
    // straight into Channels[0], so a test can assert the bit a run was given without a real texture.
    private const string BoundMaskProbeSource = """
        cbuffer ProbeFrame : register(b1) {
            float time;
            float deltaTime;
            uint frame;
            uint boundMask;
        };

        RWStructuredBuffer<uint> Accumulate : register(u0);
        RWStructuredBuffer<float> Channels : register(u1);

        [numthreads(8, 8, 1)]
        void accumulate(uint3 dispatchId : SV_DispatchThreadID) {
        }

        [numthreads(1, 1, 1)]
        void finalize(uint3 dispatchId : SV_DispatchThreadID) {
            Channels[0] = float(boundMask);
            Channels[1] = 1.0;
        }
        """;

    private static void AssertBlobCentroid(in ProbeReading reading) {
        // Centroid of the square: u = (48 + 4) / 64, v = (8 + 4) / 64 → x = 2u - 1, y = 1 - 2v (y-up).
        const double ExpectedX = (((SquareLeft + (SquareSize / 2.0)) / FrameWidth) * 2.0) - 1.0;
        const double ExpectedY = 1.0 - (((SquareTop + (SquareSize / 2.0)) / FrameHeight) * 2.0);
        const double ExpectedCoverage = ((double)(SquareSize * SquareSize)) / (FrameWidth * FrameHeight);

        Assert.Equal(expected: 4, actual: reading.ChannelCount);
        Assert.InRange(actual: ((double)reading[0]), low: (ExpectedX - 0.01), high: (ExpectedX + 0.01));
        Assert.InRange(actual: ((double)reading[1]), low: (ExpectedY - 0.01), high: (ExpectedY + 0.01));
        Assert.InRange(actual: ((double)reading[2]), low: (ExpectedCoverage - 0.001), high: (ExpectedCoverage + 0.001));
        Assert.InRange(actual: ((double)reading[3]), low: 0.99, high: 1.0);
        Assert.InRange(actual: ((double)reading.Confidence), low: 0.99, high: 1.0);
    }
    private static byte[] BuildSquare(byte[] inside, byte[] outside) {
        var pixels = new byte[FrameWidth * FrameHeight * 4];

        for (var y = 0; (y < FrameHeight); y++) {
            for (var x = 0; (x < FrameWidth); x++) {
                var offset = (((y * FrameWidth) + x) * 4);
                var source = (((x >= SquareLeft) && (x < (SquareLeft + SquareSize)) && (y >= SquareTop) && (y < (SquareTop + SquareSize))) ? inside : outside);

                source.CopyTo(array: pixels, index: offset);
            }
        }

        return pixels;
    }
    private static byte[] FaerieDefaults() => FaerieConstants();
    // The faerie manifest's config, packed in declaration order (scalar floats pack sequentially) and padded to the
    // 16-byte constant-buffer granule; defaults match the manifest, and a caller overrides only the fields a test
    // needs.
    private static byte[] FaerieConstants(
        float anchorX = 0f,
        float anchorY = 0.3f,
        float lightHeight = 0.35f,
        float orbitRadius = 0.22f,
        float orbitSpeed = 0.9f,
        float intensity = 1.8f,
        float radius = 0.55f,
        float ambient = 0.22f,
        float tintR = 0.62f,
        float tintG = 0.32f,
        float tintB = 1.0f,
        float relief = 0.1f,
        float responseFloor = 0.08f,
        float gain = 3.0f,
        float irScale = 1.0f,
        float irOffsetX = 0f,
        float irOffsetY = 0f,
        float spriteSize = 0.035f,
        float paintingX0 = -0.9f,
        float paintingY0 = 0.85f,
        float paintingX1 = -0.3f,
        float paintingY1 = 0.85f,
        float paintingX2 = -0.3f,
        float paintingY2 = 0.25f,
        float paintingX3 = -0.9f,
        float paintingY3 = 0.25f,
        float paintingOpacity = 1.0f,
        float journey = 0f,
        float portalThreshold = 0.85f
    ) {
        ReadOnlySpan<float> values = [
            anchorX, anchorY, lightHeight, orbitRadius, orbitSpeed, intensity, radius, ambient,
            tintR, tintG, tintB, relief, responseFloor, gain, irScale, irOffsetX, irOffsetY, spriteSize,
            paintingX0, paintingY0, paintingX1, paintingY1, paintingX2, paintingY2, paintingX3, paintingY3,
            paintingOpacity, journey, portalThreshold,
        ];

        return PackConstants(values: values);
    }
    private static byte[] IrBlobConstants() {
        var constants = new byte[16];

        BitConverter.TryWriteBytes(destination: constants.AsSpan(start: 0), value: 0.5f);
        BitConverter.TryWriteBytes(destination: constants.AsSpan(start: 4), value: 0.01f);

        return constants;
    }
    private static byte[] IrMarkerConstants(float threshold = 0.5f, float gain = 3.0f, float minCoverage = 0.005f) {
        ReadOnlySpan<float> values = [threshold, gain, minCoverage];

        return PackConstants(values: values);
    }
    private static byte[] PackConstants(ReadOnlySpan<float> values) {
        var byteCount = ((((values.Length * 4) + 15) / 16) * 16);
        var block = new byte[byteCount];

        for (var index = 0; (index < values.Length); index++) {
            BitConverter.TryWriteBytes(destination: block.AsSpan(start: (index * 4)), value: values[index]);
        }

        return block;
    }
    private static string KernelPath(string name, [CallerFilePath] string callerFilePath = "") {
        var repositoryRoot = Path.GetFullPath(path: Path.Combine(Path.GetDirectoryName(path: callerFilePath)!, "..", ".."));

        return Path.Combine(repositoryRoot, "src", "Puck.Shaders", "Assets", "Probes", $"{name}.hlsl");
    }
    private static int Luminance(byte[] pixels, int x, int y) {
        var offset = (((y * FrameWidth) + x) * 4);

        return (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]);
    }
    private static (int R, int G, int B) Pixel(byte[] pixels, int x, int y) {
        var offset = (((y * FrameWidth) + x) * 4);

        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }
    private static void AssertGreenDominant(byte[] pixels, int x, int y) {
        var (r, g, b) = Pixel(pixels: pixels, x: x, y: y);

        Assert.True(condition: (g > (r + 40)), userMessage: $"expected green-dominant at ({x},{y}): r={r} g={g} b={b}");
        Assert.True(condition: (g > (b + 40)), userMessage: $"expected green-dominant at ({x},{y}): r={r} g={g} b={b}");
    }
    private static void AssertNotGreenDominant(byte[] pixels, int x, int y) {
        var (r, g, b) = Pixel(pixels: pixels, x: x, y: y);

        Assert.False(condition: ((g > r) && (g > b)), userMessage: $"expected NOT green-dominant at ({x},{y}): r={r} g={g} b={b}");
    }
    private static void AssertRoughlyNeutral(byte[] pixels, int x, int y, int tolerance) {
        var (r, g, b) = Pixel(pixels: pixels, x: x, y: y);
        var max = Math.Max(val1: r, val2: Math.Max(val1: g, val2: b));
        var min = Math.Min(val1: r, val2: Math.Min(val1: g, val2: b));

        Assert.True(condition: ((max - min) <= tolerance), userMessage: $"expected roughly neutral at ({x},{y}): r={r} g={g} b={b}");
    }
    private static byte[] BuildRectangle(int left, int top, int width, int height, byte[] inside, byte[] outside) {
        var pixels = new byte[FrameWidth * FrameHeight * 4];

        for (var y = 0; (y < FrameHeight); y++) {
            for (var x = 0; (x < FrameWidth); x++) {
                var offset = (((y * FrameWidth) + x) * 4);
                var source = (((x >= left) && (x < (left + width)) && (y >= top) && (y < (top + height))) ? inside : outside);

                source.CopyTo(array: pixels, index: offset);
            }
        }

        return pixels;
    }
    // Rasterizes a rectangle of half extents (halfExtentMajor, halfExtentMinor) — fractions of (width, height) —
    // centred at (centreU, centreV) and rotated angleRadians about that centre: a pixel is inside when its centre,
    // expressed in the rectangle's own axes (the inverse rotation), falls within the half extents on both axes.
    private static byte[] BuildRotatedRectangle(double centreU, double centreV, double halfExtentMajor, double halfExtentMinor, double angleRadians, byte[] inside, byte[] outside) {
        var pixels = new byte[FrameWidth * FrameHeight * 4];
        var cos = Math.Cos(angleRadians);
        var sin = Math.Sin(angleRadians);
        var centreX = (centreU * FrameWidth);
        var centreY = (centreV * FrameHeight);
        var halfMajorPx = (halfExtentMajor * FrameWidth);
        var halfMinorPx = (halfExtentMinor * FrameHeight);

        for (var y = 0; (y < FrameHeight); y++) {
            for (var x = 0; (x < FrameWidth); x++) {
                var dx = ((x + 0.5) - centreX);
                var dy = ((y + 0.5) - centreY);
                var localMajor = ((dx * cos) + (dy * sin));
                var localMinor = ((-dx * sin) + (dy * cos));
                var offset = (((y * FrameWidth) + x) * 4);
                var source = (((Math.Abs(localMajor) <= halfMajorPx) && (Math.Abs(localMinor) <= halfMinorPx)) ? inside : outside);

                source.CopyTo(array: pixels, index: offset);
            }
        }

        return pixels;
    }
    // Mirrors ir-marker.hlsl's finalize corner rule: axisMajor is normalized with a non-negative u component
    // ("points right"), axisMinor is its +90-degree rotation in uv space ("points down"), and the four corners are
    // centre +/- (major half-extent) +/- (minor half-extent), converted to frame coordinates.
    private static MarkerCorners ExpectedMarkerCorners(double centreU, double centreV, double majorAxisU, double majorAxisV, double halfMajor, double halfMinor) {
        var length = Math.Sqrt((majorAxisU * majorAxisU) + (majorAxisV * majorAxisV));
        var axisMajorU = (majorAxisU / length);
        var axisMajorV = (majorAxisV / length);

        if (axisMajorU < 0.0) {
            axisMajorU = -axisMajorU;
            axisMajorV = -axisMajorV;
        }

        var axisMinorU = -axisMajorV;
        var axisMinorV = axisMajorU;
        var axU = (axisMajorU * halfMajor);
        var axV = (axisMajorV * halfMajor);
        var ayU = (axisMinorU * halfMinor);
        var ayV = (axisMinorV * halfMinor);
        var topLeft = ToFrameCoordinates(u: (centreU - axU - ayU), v: (centreV - axV - ayV));
        var topRight = ToFrameCoordinates(u: (centreU + axU - ayU), v: (centreV + axV - ayV));
        var bottomRight = ToFrameCoordinates(u: (centreU + axU + ayU), v: (centreV + axV + ayV));
        var bottomLeft = ToFrameCoordinates(u: (centreU - axU + ayU), v: (centreV - axV + ayV));

        return new MarkerCorners(TopLeft: topLeft, TopRight: topRight, BottomRight: bottomRight, BottomLeft: bottomLeft);
    }
    private static (double X, double Y) ToFrameCoordinates(double u, double v) => (((u * 2.0) - 1.0), (1.0 - (v * 2.0)));
    private static void AssertMarkerCorners(in ProbeReading reading, MarkerCorners expected, double tolerance) {
        Assert.Equal(expected: IrMarkerChannelCount, actual: reading.ChannelCount);
        AssertClose(expected: expected.TopLeft.X, actual: ((double)reading[0]), tolerance: tolerance);
        AssertClose(expected: expected.TopLeft.Y, actual: ((double)reading[1]), tolerance: tolerance);
        AssertClose(expected: expected.TopRight.X, actual: ((double)reading[2]), tolerance: tolerance);
        AssertClose(expected: expected.TopRight.Y, actual: ((double)reading[3]), tolerance: tolerance);
        AssertClose(expected: expected.BottomRight.X, actual: ((double)reading[4]), tolerance: tolerance);
        AssertClose(expected: expected.BottomRight.Y, actual: ((double)reading[5]), tolerance: tolerance);
        AssertClose(expected: expected.BottomLeft.X, actual: ((double)reading[6]), tolerance: tolerance);
        AssertClose(expected: expected.BottomLeft.Y, actual: ((double)reading[7]), tolerance: tolerance);
    }
    private static void AssertClose(double expected, double actual, double tolerance) {
        Assert.InRange(actual: actual, low: (expected - tolerance), high: (expected + tolerance));
    }
    // Frame-coordinate corners in image order (top-left, top-right, bottom-right, bottom-left) — mirrors the
    // channel layout ir-marker.hlsl writes.
    private readonly record struct MarkerCorners((double X, double Y) TopLeft, (double X, double Y) TopRight, (double X, double Y) BottomRight, (double X, double Y) BottomLeft);
}
