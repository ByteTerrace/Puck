using System.Diagnostics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Platform.Windows;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// A view export's direction of the shared fence: a write into an exported texture that a probe kernel on the render
/// adapter's own Direct3D 11 host reads, ordered only by a shared fence the writer signals. The texture holds white, the
/// writer's submission that clears it waits behind a gate the law holds shut, and the ring publishes the value the clear
/// signals (<see cref="IGpuExportableImage.CompleteWrite"/>). A kernel that read without waiting would measure the white
/// it found; the host's kernel measures nothing while the gate is shut, and black once it opens. The writer is the
/// Direct3D 12 device that exports the texture, or a Vulkan device that imports a texture and a fence a Direct3D 12 device
/// on its adapter made (<see cref="IGpuSurfaceTransferFactory.TryImportWritable"/>), as a Vulkan host's view export does.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
public sealed unsafe class RenderedProbeKernelHostLawTests {
    private const int Extent = 64;
    // ID3D12Fence::Signal's vtable slot: IUnknown's three, ID3D12Object's four, ID3D12DeviceChild's GetDevice, then
    // GetCompletedValue and SetEventOnCompletion before it (d3d12.h).
    private const int FenceSignalSlot = 10;

    [Fact]
    public void AKernelOnTheRenderAdapterReadsADirect3D12WriteOnlyOnceTheImagesSharedFenceReachesItsValue() {
        using var writer = (SharedFenceWriter.TryCreate(warp: false) ?? Skipped<SharedFenceWriter>(reason: "no Direct3D 11 hardware device on this host"));
        using var context = Direct3D12(adapterLuid: writer.AdapterLuid);
        var export = new DirectXGpuSurfaceExportFactory(deviceContext: context);
        using var image = export.CreateSharedComputeImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            width: Extent
        );
        using var gate = ((DirectXExportableFence)export.CreateExportableFence());

        Assert.NotEqual(
            actual: image.SharedFenceHandle,
            expected: 0
        );
        FillWhite(
            sharedHandle: image.SharedHandle,
            writer: writer
        );
        ClearBehindTheGate(
            adapterLuid: writer.AdapterLuid,
            gate: gate,
            image: image,
            services: context.Services,
            waitedGate: gate
        );
    }
    [Fact]
    public void AKernelOnTheRenderAdapterReadsAVulkanWriteIntoAnImportedTextureOnlyOnceTheImportedFenceReachesItsValue() {
        using var vulkan = HeadlessVulkanDevice.Create(applicationName: nameof(RenderedProbeKernelHostLawTests));
        using var writer = (SharedFenceWriter.TryCreate(warp: false) ?? Skipped<SharedFenceWriter>(reason: "no Direct3D 11 hardware device on this host"));

        if (writer.AdapterLuid != vulkan.AdapterLuid) {
            Assert.Skip(reason: $"the Vulkan device '{vulkan.Name}' is not on the Direct3D 11 writer's adapter");
        }

        using var context = Direct3D12(adapterLuid: vulkan.AdapterLuid);
        var export = new DirectXGpuSurfaceExportFactory(deviceContext: context);
        using var texture = export.CreateSharedComputeImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            width: Extent
        );
        using var fence = export.CreateExportableFence();
        using var gate = ((DirectXExportableFence)export.CreateExportableFence());
        var transfers = vulkan.Services.SurfaceTransferFactory;

        if (!transfers.TryImportFence(
            fence: out var waitedGate,
            refusal: out var gateRefusal,
            sharedHandle: gate.SharedHandle
        )) {
            Assert.Skip(reason: $"the Vulkan device imports no shared fence: {gateRefusal}");
        }

        using (waitedGate) {
            Assert.True(
                condition: transfers.TryImportWritable(
                    format: texture.Format,
                    height: Extent,
                    image: out var imported,
                    refusal: out var refusal,
                    sharedFenceHandle: fence.SharedHandle,
                    sharedHandle: texture.SharedHandle,
                    usage: texture.Usage,
                    width: Extent
                ),
                userMessage: refusal
            );

            using (imported) {
                Assert.Equal(
                    actual: imported.SharedHandle,
                    expected: texture.SharedHandle
                );
                Assert.Equal(
                    actual: imported.SharedFenceHandle,
                    expected: fence.SharedHandle
                );
                FillWhite(
                    sharedHandle: texture.SharedHandle,
                    writer: writer
                );

                try {
                    ClearBehindTheGate(
                        adapterLuid: vulkan.AdapterLuid,
                        gate: gate,
                        image: imported,
                        services: vulkan.Services,
                        waitedGate: waitedGate
                    );
                } finally {
                    vulkan.WaitIdle();
                }
            }
        }
    }
    [Fact]
    public void AKernelRunRestartedOverItsOutputRingContinuesTheRingsFenceValues() {
        using var writer = (SharedFenceWriter.TryCreate(warp: false) ?? Skipped<SharedFenceWriter>(reason: "no Direct3D 11 hardware device on this host"));
        using var context = Direct3D12(adapterLuid: writer.AdapterLuid);
        var export = new DirectXGpuSurfaceExportFactory(deviceContext: context);
        using var frame = export.CreateSharedComputeImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            width: Extent
        );
        using var first = export.CreateSimultaneousAccessImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            width: Extent
        );
        using var second = export.CreateSimultaneousAccessImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            width: Extent
        );
        using var fence = export.CreateExportableFence();
        using var host = new Win32RenderedProbeKernelHost(adapterLuid: writer.AdapterLuid);
        var trigger = new LatestSlotPublication();
        var output = new LatestSlotPublication();

        FillWhite(
            sharedHandle: frame.SharedHandle,
            writer: writer
        );
        trigger.Configure(targetCount: 2);
        output.Configure(targetCount: 2);

        var request = new ProbeKernelRequest(
            AccumulateBytecode: Bytecode(entry: "accumulate"),
            AccumulateEntry: "accumulate",
            FinalizeBytecode: Bytecode(entry: "finalize"),
            FinalizeEntry: "finalize",
            Constants: UntintedConstants(),
            ChannelCount: 3,
            RateHz: 1000U,
            Inputs: [new ProbeKernelInput.Ring(
                Format: GpuPixelFormat.R8G8B8A8Unorm,
                Height: Extent,
                SharedFenceHandle: 0,
                SharedTargetHandles: [frame.SharedHandle, frame.SharedHandle],
                Slots: trigger,
                Width: Extent
            )],
            Trigger: 0,
            Output: new ProbeKernelOutput(
                Height: Extent,
                SharedFenceHandle: fence.SharedHandle,
                SharedTargetHandles: [first.SharedHandle, second.SharedHandle],
                Slots: output,
                TargetFormat: GpuPixelFormat.R8G8B8A8Unorm,
                Width: Extent
            )
        );
        var written = new ulong[2];

        // Two runs in turn over the one output ring, as a probe's run restarts when a socket's source is made again.
        for (var run = 0; (run < 2); run++) {
            var readings = new ProbeReadingRing();

            trigger.Publish(
                fenceValue: 0UL,
                slot: run
            );
            Assert.True(
                condition: host.TryAttachKernel(
                    fault: out var fault,
                    request: in request,
                    ring: readings,
                    run: out var attached
                ),
                userMessage: fault
            );

            using (attached) {
                host.Signal();
                Assert.True(
                    condition: Await(
                        readings: readings,
                        seconds: 10.0
                    ),
                    userMessage: (attached.Fault ?? $"run {run} published no reading")
                );
                Assert.True(
                    condition: attached.Order.SharedFence,
                    userMessage: $"run {run} keeps the CPU wait: {attached.Order}"
                );
                Assert.True(condition: output.TryAcquireLatest(
                    fenceValue: out written[run],
                    slot: out var slot
                ));
                output.Release(slot: slot);
            }
        }

        Assert.True(
            condition: (written[1] > written[0]),
            userMessage: $"the restarted run published fence value {written[1]} after the first run's {written[0]}"
        );
        Assert.True(condition: (fence.CompletedValue >= written[1]));
    }

    // Records the writer's clear of the image to black, held behind the gate, publishes the value its CompleteWrite
    // returns, and holds the kernel host's reading to it: none while the gate is shut, black once it opens. The gate opens
    // however the law ends, so neither the writer's queue nor the kernel host's worker is left waiting on it.
    private static void ClearBehindTheGate(long adapterLuid, GpuDeviceServices services, IGpuExportableImage image, DirectXExportableFence gate, IGpuSharedFence waitedGate) {
        using var pool = services.CommandPoolFactory.Create(name: default);
        using var submission = services.QueueSubmitter.CreateSubmissionFence();
        var opened = false;

        void Open() {
            if (opened) {
                return;
            }

            opened = true;

            var fence = ((void***)gate.FenceHandle);

            Assert.Equal(
                actual: ((delegate* unmanaged[Stdcall]<void*, ulong, int>)(*fence)[FenceSignalSlot])(fence, 1UL),
                expected: 0
            );
        }

        services.Recorder.BeginCommandBuffer(commandBufferHandle: pool.CommandBufferHandle);
        services.Recorder.TransitionImageLayout(
            commandBufferHandle: pool.CommandBufferHandle,
            destinationAccessMask: GpuAccess.TransferWrite,
            destinationStageMask: GpuStage.Transfer,
            imageHandle: image.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: GpuImageLayout.Undefined,
            sourceAccessMask: GpuAccess.None,
            sourceStageMask: GpuStage.TopOfPipe
        );
        services.Recorder.ClearStorageImage(
            commandBufferHandle: pool.CommandBufferHandle,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            imageHandle: image.ImageHandle
        );
        services.Recorder.TransitionImageLayout(
            commandBufferHandle: pool.CommandBufferHandle,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: image.ImageHandle,
            newLayout: GpuImageLayout.External,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuAccess.TransferWrite,
            sourceStageMask: GpuStage.Transfer
        );
        services.Recorder.EndCommandBuffer(commandBufferHandle: pool.CommandBufferHandle);
        image.BeginWrite();
        services.QueueSubmitter.AddExternalWait(wait: new GpuExternalWait(
            Fence: waitedGate,
            Value: 1UL
        ));
        services.QueueSubmitter.Submit(
            commandBufferHandles: [pool.CommandBufferHandle],
            fence: submission
        );

        try {
            var written = image.CompleteWrite();
            var slots = new LatestSlotPublication();

            Assert.NotEqual(
                actual: written,
                expected: 0UL
            );
            slots.Configure(targetCount: 2);
            slots.Publish(
                fenceValue: written,
                slot: 0
            );

            using var host = new Win32RenderedProbeKernelHost(adapterLuid: adapterLuid);
            var readings = new ProbeReadingRing();
            var request = new ProbeKernelRequest(
                AccumulateBytecode: Bytecode(entry: "accumulate"),
                AccumulateEntry: "accumulate",
                FinalizeBytecode: Bytecode(entry: "finalize"),
                FinalizeEntry: "finalize",
                Constants: UntintedConstants(),
                ChannelCount: 3,
                RateHz: 1000U,
                Inputs: [new ProbeKernelInput.Ring(
                    Format: GpuPixelFormat.R8G8B8A8Unorm,
                    Height: Extent,
                    SharedFenceHandle: image.SharedFenceHandle,
                    SharedTargetHandles: [image.SharedHandle, image.SharedHandle],
                    Slots: slots,
                    Width: Extent
                )],
                Trigger: 0
            );

            Assert.True(
                condition: host.TryAttachKernel(
                    fault: out var fault,
                    request: in request,
                    ring: readings,
                    run: out var run
                ),
                userMessage: fault
            );

            using (run) {
                try {
                    host.Signal();

                    Assert.False(
                        condition: Await(
                            readings: readings,
                            seconds: 0.5
                        ),
                        userMessage: "the kernel read the image before the write its fence value names"
                    );

                    Open();

                    Assert.True(
                        condition: Await(
                            readings: readings,
                            seconds: 10.0
                        ),
                        userMessage: (run.Fault ?? "the kernel published no reading once the write finished")
                    );
                    Assert.True(condition: readings.TryReadLatest(reading: out var reading));

                    for (var channel = 0; (channel < 3); channel++) {
                        Assert.Equal(
                            actual: ((double)reading[channel]),
                            expected: 0.0
                        );
                    }

                    Assert.Null(@object: run.Fault);
                } finally {
                    Open();
                }
            }
        } finally {
            Open();
            submission.Wait();
        }
    }
    // Polls until the ring holds a reading or the budget runs out.
    private static bool Await(ProbeReadingRing readings, double seconds) {
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(startingTimestamp: started).TotalSeconds < seconds) {
            if (readings.TryReadLatest(reading: out _)) {
                return true;
            }

            Thread.Sleep(millisecondsTimeout: 5);
        }

        return readings.TryReadLatest(reading: out _);
    }
    private static byte[] Bytecode(string entry) =>
        File.ReadAllBytes(path: RepositoryPaths.Resolve(relativePath: $"src/Puck.Shaders/Assets/Probes/average.{entry}.dxbc"));
    private static DirectXDeviceContext Direct3D12(long adapterLuid) {
        var context = new DirectXDeviceContext(
            adapterLuid: adapterLuid,
            deviceApi: new DirectXNativeDeviceApi(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        );

        try {
            _ = context.Device;
        } catch (GpuDeviceUnavailableException exception) {
            context.Dispose();
            Assert.Skip(reason: $"no Direct3D 12 device on the writer's adapter: {exception.Message}");
        }

        return context;
    }
    // Writes white into the shared texture on the Direct3D 11 device and finishes it there before anything reads it.
    private static void FillWhite(SharedFenceWriter writer, nint sharedHandle) {
        using var finished = new Win32D3D11CompletionSignal(
            context: writer.Context,
            device: writer.Device,
            sharedFenceHandle: 0
        );

        writer.Write(
            pixels: Enumerable.Repeat(
                count: ((Extent * Extent) * 4),
                element: ((byte)255)
            ).ToArray(),
            target: writer.Open(sharedHandle: sharedHandle),
            width: Extent
        );
        Assert.Equal(
            actual: finished.Complete(),
            expected: 0UL
        );
    }
    private static T Skipped<T>(string reason) {
        Assert.Skip(reason: reason);

        return default!;
    }
    // The average kind's config at its defaults: tintR, tintG and tintB at one, padded to the 16-byte block.
    private static byte[] UntintedConstants() {
        var constants = new byte[16];

        BitConverter.TryWriteBytes(destination: constants.AsSpan(start: 0), value: 1f);
        BitConverter.TryWriteBytes(destination: constants.AsSpan(start: 4), value: 1f);
        BitConverter.TryWriteBytes(destination: constants.AsSpan(start: 8), value: 1f);

        return constants;
    }
}
