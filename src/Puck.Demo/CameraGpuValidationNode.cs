using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.DirectX;
using Puck.Hosting;
using Puck.Platform;
using Puck.Platform.Windows;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Demo;

/// <summary>
/// The camera GPU-tier (M3 zero-copy) gate (<c>--validate-camera-gpu</c>): proves the whole GPU-resident chain against
/// the REAL webcam in one shot. A bespoke Direct3D 12 device (LUID-matched to the Vulkan host) allocates
/// simultaneous-access shared targets; the Media Foundation GPU session opens the default camera with a D3D manager +
/// advanced (GPU) video processing, so the DXVA processor converts each frame to ARGB32 on-GPU and copies it into the
/// targets (D3D11 opens the D3D12-created handles) — no frame ever visits host memory. The Vulkan host then imports the
/// latest published target through the proven D3D12-handle path, reads it back, asserts real (spatially varying) camera
/// content arrived, and dumps it to <c>artifacts/camera-gpu.png</c> for eyeball verification.
/// <para>Lenient about the ENVIRONMENT (no camera / no GPU path → skip); strict about a MALFUNCTION once open
/// (streaming/copy/import errors → infra-fail). 0 = pass/skip, 2 = infra-fail. It never presents.</para>
/// </summary>
internal sealed class CameraGpuValidationNode : IRenderNode {
    private const int FramePollBudget = 300; // ~5s at 16ms/attempt — a cold sensor's first frame plus DXVA spin-up.
    private const int DefaultRequestedHeight = 720;
    private const int DefaultRequestedWidth = 1280;
    private const int TargetCount = 2;
    private const uint VulkanFormatB8G8R8A8Unorm = 44; // VK_FORMAT_B8G8R8A8_UNORM

    private readonly NodeDescriptor m_descriptor = new(
        Name: "camera-gpu-validation",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="CameraGpuValidationNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (the live Vulkan host device + APIs, and the LUID source for the decode + allocator devices).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public CameraGpuValidationNode(IServiceProvider serviceProvider, ParityResult result) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
        m_serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_done) {
            return default;
        }

        m_done = true;

        try {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
                Console.Out.WriteLine(value: "CAMERA-GPU skip | the GPU camera tier requires Windows 10.0.10240+");
            } else {
                Validate();
            }

            m_result.ExitCode = 0;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"CAMERA-GPU infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private void Validate() {
        // The consumer render device's adapter LUID — the decode device and the target allocator must share it.
        var vulkanDeviceContext = (IVulkanDeviceContext)m_serviceProvider.GetService(serviceType: typeof(IVulkanDeviceContext))!;
        var physicalDeviceApi = (IVulkanPhysicalDeviceApi)m_serviceProvider.GetService(serviceType: typeof(IVulkanPhysicalDeviceApi))!;
        var adapterLuid = physicalDeviceApi.GetDeviceLuid(
            instanceHandle: vulkanDeviceContext.Instance.Handle,
            physicalDeviceHandle: vulkanDeviceContext.LogicalDevice.PhysicalDevice.Handle
        );

        // Bring-up knob: PUCK_CAMERA_GPU_SIZE=WxH overrides the requested output (e.g. 1920x1080 exercises the
        // MJPEG/H.264 decode modes many webcams require above 720p — the GPU processor decodes + converts either way).
        var requestedWidth = DefaultRequestedWidth;
        var requestedHeight = DefaultRequestedHeight;
        var sizeOverride = Environment.GetEnvironmentVariable(variable: "PUCK_CAMERA_GPU_SIZE");

        if (!string.IsNullOrEmpty(value: sizeOverride)) {
            var parts = sizeOverride.Split(separator: 'x');

            if ((2 == parts.Length) && int.TryParse(s: parts[0], result: out var width) && int.TryParse(s: parts[1], result: out var height)) {
                requestedHeight = height;
                requestedWidth = width;
            }
        }

        ICameraCaptureService service = new Win32MediaFoundationCameraService();

        if (!service.IsSupported || !service.TryOpenSharedDefault(adapterLuid: adapterLuid, requestedWidth: requestedWidth, requestedHeight: requestedHeight, session: out var session)) {
            Console.Out.WriteLine(value: "CAMERA-GPU skip | no capture device (or no Media Foundation GPU path)");

            return;
        }

        using (session) {
            // The shared targets: allocated by a bespoke Direct3D 12 device (proven Vulkan-importable handles), created
            // simultaneous-access so the session's D3D11 decode device can open + write them.
            using var directX = new DirectXComputeWorldDevice(hostProvider: m_serviceProvider);

            var factory = new DirectXGpuSurfaceExportFactory();
            var targets = new IGpuExportableStorageImage[TargetCount];

            try {
                for (var index = 0; (index < TargetCount); index++) {
                    targets[index] = factory.CreateSimultaneousAccessStorageImage(
                        deviceContext: directX.DeviceContext,
                        format: GpuPixelFormat.B8G8R8A8Unorm,
                        height: (uint)session.Height,
                        width: (uint)session.Width
                    );
                }

                session.Start(sharedTargetHandles: [.. targets.Select(selector: static target => target.SharedHandle)]);

                for (var attempt = 0; (attempt < FramePollBudget); attempt++) {
                    if (session.FrameVersion > 0) {
                        break;
                    }

                    Thread.Sleep(millisecondsTimeout: 16);
                }

                if (0 == session.FrameVersion) {
                    Console.Out.WriteLine(value: $"CAMERA-GPU no-frame | '{session.Name}' {session.Width}x{session.Height} opened on the GPU tier but published no frame within {FramePollBudget} attempts");

                    return;
                }

                var slot = session.LatestSlot;
                var pixels = VulkanImportAndReadback(
                    height: (uint)session.Height,
                    sharedHandle: targets[slot].SharedHandle,
                    width: (uint)session.Width
                );

                AssertVariedAndDump(height: session.Height, name: session.Name, pixels: pixels, slot: slot, version: session.FrameVersion, width: session.Width);
            } finally {
                foreach (var target in targets) {
                    target?.Dispose();
                }
            }
        }
    }

    // Import the shared target on the Vulkan host through the proven D3D12-handle path, bring it shader-readable, and
    // read it back (BGRA bytes). Mirrors CameraValidationNode's import; only the format differs.
    private ReadOnlyMemory<byte> VulkanImportAndReadback(nint sharedHandle, uint width, uint height) {
        T Resolve<T>() => (T)m_serviceProvider.GetService(serviceType: typeof(T))!;

        var deviceContext = Resolve<IVulkanDeviceContext>();
        var gpuDeviceContext = (IGpuDeviceContext)deviceContext;
        var externalMemoryApi = Resolve<IVulkanExternalMemoryApi>();
        var recordingApi = Resolve<IVulkanCommandBufferRecordingApi>();
        var commandResourcesFactory = Resolve<IVulkanCommandResourcesFactory>();
        var queueSubmitter = Resolve<VulkanQueueSubmitter>();

        var logicalDevice = deviceContext.LogicalDevice;
        var deviceHandle = logicalDevice.Handle;

        var imported = externalMemoryApi.ImportImage(request: new VulkanExternalImageImportRequest(
            DeviceHandle: deviceHandle,
            Format: VulkanFormatB8G8R8A8Unorm,
            Height: height,
            InstanceHandle: deviceContext.Instance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            SharedHandle: sharedHandle,
            UsageFlags: VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource,
            Width: width
        ));

        var commandResources = commandResourcesFactory.Create(commandBufferCount: 1, logicalDevice: logicalDevice);
        var commandBuffer = commandResources.CommandBufferHandles[0];

        try {
            recordingApi.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle).ThrowIfFailed(operation: "vkBeginCommandBuffer");
            recordingApi.TransitionImageLayout(
                baseMipLevel: 0,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: VulkanAccessFlags.ShaderRead,
                destinationStageMask: VulkanPipelineStageFlags.FragmentShader,
                deviceHandle: deviceHandle,
                imageHandle: imported.ImageHandle,
                mipLevelCount: 1,
                newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
                oldLayout: VulkanImageLayout.Undefined,
                sourceAccessMask: 0,
                sourceStageMask: VulkanPipelineStageFlags.TopOfPipe
            );
            recordingApi.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle).ThrowIfFailed(operation: "vkEndCommandBuffer");

            Span<nint> commandBuffers = [commandBuffer];

            queueSubmitter.SubmitAndWait(commandBufferHandles: commandBuffers, deviceHandle: deviceHandle, graphicsQueue: logicalDevice.GraphicsQueue);

            using var readback = ((IGpuSurfaceTransferFactory)m_serviceProvider.GetService(serviceType: typeof(IGpuSurfaceTransferFactory))!).CreateReadback(deviceContext: gpuDeviceContext);

            return readback.Read(bytesPerPixel: 4, deviceContext: gpuDeviceContext, format: GpuPixelFormat.B8G8R8A8Unorm, height: height, sourceImageHandle: imported.ImageHandle, width: width).ToArray();
        } finally {
            commandResources.Dispose();
            externalMemoryApi.DestroyImage(deviceHandle: deviceHandle, imageHandle: imported.ImageHandle, memoryHandle: imported.MemoryHandle);
        }
    }

    // Assert the frame carries real content (spatial variation) and dump it as a PNG for eyeball verification.
    private static void AssertVariedAndDump(string name, ReadOnlyMemory<byte> pixels, int width, int height, int slot, long version) {
        var span = pixels.Span;
        var pixels32 = MemoryMarshal.Cast<byte, uint>(span: span);
        var firstPixel = pixels32[0];
        var varied = false;

        for (var index = 0; (index < pixels32.Length); index++) {
            if (pixels32[index] != firstPixel) {
                varied = true;

                break;
            }
        }

        if (!varied) {
            throw new InvalidOperationException(message: $"the Vulkan host imported the GPU-tier camera frame but it was spatially flat (every pixel 0x{firstPixel:X8}) — no camera content crossed the shared targets.");
        }

        // B8G8R8A8 -> R8G8B8A8 for the PNG encoder.
        var rgba = new byte[width * height * 4];

        for (var offset = 0; (offset < rgba.Length); offset += 4) {
            rgba[offset + 0] = span[offset + 2];
            rgba[offset + 1] = span[offset + 1];
            rgba[offset + 2] = span[offset + 0];
            rgba[offset + 3] = 0xFF;
        }

        var path = Path.Combine(path1: Environment.CurrentDirectory, path2: "artifacts", path3: "camera-gpu.png");

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
        PngImage.Write(height: height, path: path, rgba: rgba, width: width);

        Console.Out.WriteLine(value: $"CAMERA-GPU pass | '{name}' {width}x{height} | GPU-resident: DXVA ARGB32 -> D3D11 copy into a D3D12 simultaneous-access shared target (slot {slot}, version {version}) -> Vulkan import + readback varied -> {path}");
    }
}
