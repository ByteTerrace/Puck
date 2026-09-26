using System.Diagnostics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Platform.Windows;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// A view export's direction of the shared fence: a Direct3D 12 write into an exported image that a probe kernel on the
/// render adapter's own Direct3D 11 host reads, ordered only by the image's shared fence. The image holds white, the
/// Direct3D 12 submission that clears it waits behind a gate the law holds shut, and the ring publishes the value the
/// clear signals (<see cref="IGpuExportableImage.CompleteWrite"/>). A kernel that read without waiting would measure the
/// white it found; the host's kernel measures nothing while the gate is shut, and black once it opens.
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
        var services = context.Services;
        var export = new DirectXGpuSurfaceExportFactory(deviceContext: context);
        using var image = export.CreateSharedComputeImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            width: Extent
        );
        using var gate = ((DirectXExportableFence)export.CreateExportableFence());
        using var pool = services.CommandPoolFactory.Create(name: default);
        using var submission = services.QueueSubmitter.CreateSubmissionFence();

        Assert.NotEqual(
            actual: image.SharedFenceHandle,
            expected: 0
        );

        // The image starts white, written and finished on the Direct3D 11 device before anything reads it.
        using (var finished = new Win32D3D11CompletionSignal(
            context: writer.Context,
            device: writer.Device,
            sharedFenceHandle: 0
        )) {
            writer.Write(
                pixels: Enumerable.Repeat(
                    count: ((Extent * Extent) * 4),
                    element: ((byte)255)
                ).ToArray(),
                target: writer.Open(sharedHandle: image.SharedHandle),
                width: Extent
            );
            Assert.Equal(
                actual: finished.Complete(),
                expected: 0UL
            );
        }

        services.Recorder.BeginCommandBuffer(commandBufferHandle: pool.CommandBufferHandle);
        services.Recorder.TransitionImageLayout(
            commandBufferHandle: pool.CommandBufferHandle,
            destinationAccessMask: GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: image.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: GpuImageLayout.External,
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
            sourceAccessMask: GpuAccess.ShaderWrite,
            sourceStageMask: GpuStage.ComputeShader
        );
        services.Recorder.EndCommandBuffer(commandBufferHandle: pool.CommandBufferHandle);
        services.QueueSubmitter.AddExternalWait(wait: new GpuExternalWait(
            Fence: gate,
            Value: 1UL
        ));
        services.QueueSubmitter.Submit(
            commandBufferHandles: [pool.CommandBufferHandle],
            fence: submission
        );

        var opened = false;

        // The gate opens however the law ends, so neither the Direct3D 12 queue nor the kernel host's worker is left
        // waiting on it when the devices go.
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

            using var host = new Win32RenderedProbeKernelHost(adapterLuid: writer.AdapterLuid);
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
