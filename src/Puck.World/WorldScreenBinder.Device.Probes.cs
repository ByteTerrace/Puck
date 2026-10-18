using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // The format of a camera view's export and of the probe ring that reads it: the view's own working color, which the
    // node copies into the export as it is.
    private static readonly GpuPixelFormat ViewExportFormat = RenderGraphPackageCatalog.WorkingFormat;

    // The Vulkan host's view export: a Direct3D 12 simultaneous-access texture and a shared fence made on the binder's
    // headless Direct3D 12 device on the render adapter, which the render device imports to write and to signal, as the
    // camera route imports the targets it samples. The export owns all three and holds the headless device until it goes.
    [SupportedOSPlatform("windows10.0.10240")]
    private ImportedViewExport CreateImportedViewExport(IGpuDeviceContext device, uint width, uint height) {
        var adapterLuid = (m_renderAdapterLuid ?? throw new InvalidOperationException(message: "a view export needs the render adapter's LUID"));
        var targetDevice = (m_cameraTargetDevice ??= new DisposeAfterDependents<IDisposable>(resource: new DirectXDeviceContext(
            adapterLuid: adapterLuid,
            deviceApi: new DirectXNativeDeviceApi(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        )));
        var export = new DirectXGpuSurfaceExportFactory(deviceContext: ((DirectXDeviceContext)targetDevice.Resource));
        var texture = export.CreateSharedComputeImage(
            format: ViewExportFormat,
            height: height,
            width: width
        );
        IGpuExportableFence? fence = null;

        try {
            fence = export.CreateExportableFence();

            if (!device.Services.SurfaceTransferFactory.TryImportWritable(
                format: texture.Format,
                height: height,
                image: out var imported,
                refusal: out var refusal,
                sharedFenceHandle: fence.SharedHandle,
                sharedHandle: texture.SharedHandle,
                usage: texture.Usage,
                width: width
            )) {
                throw new InvalidOperationException(message: $"the render device cannot write a view export: {refusal}");
            }

            return new ImportedViewExport(
                fence: fence,
                imported: imported,
                targetDevice: targetDevice,
                texture: texture
            );
        } catch {
            fence?.Dispose();
            texture.Dispose();

            throw;
        }
    }
    // A probe output as a source instance's feed: an imported source whose image the probe's kernel writes on its host's
    // device, published with the value it signals on the ring's shared fence, which the adapter hands out through the
    // capture gate as external content. The binder provisions and services the ring each publish, and retires it on
    // device loss, so the feed publishes, recovers and releases nothing of its own. A probe lights nothing.
    private sealed class ProbeSourceFeed(ProbeFeed feed) : IWorldImportFeed {
        private ImageSourceDescriptor m_descriptor = new(
            Cadence: ImageSourceCadence.Tick,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.External,
            Format: ImagePixelFormat.R8G8B8A8Unorm,
            Height: 0,
            Producer: WorldImageProducerSettings.ProbeId,
            Transport: ImageSourceTransport.Imported,
            Width: 0
        );

        // The extent is the provisioned ring's, zero on both axes while none is; the descriptor is a new record only when
        // it moves.
        public ImageSourceDescriptor Descriptor {
            get {
                var (width, height) = ((feed.Output is { } output)
                    ? (((uint)output.Width), ((uint)output.Height))
                    : (0U, 0U));

                if (
                    (width != m_descriptor.Width) ||
                    (height != m_descriptor.Height)
                ) {
                    m_descriptor = (m_descriptor with {
                        Height = height,
                        Width = width,
                    });
                }

                return m_descriptor;
            }
        }
        public FrameRender Answer => (feed.Live
            ? FrameRender.Rendered
            : ((feed.Refusal is { } refusal)
                ? FrameRender.Refused(reason: refusal)
                : FrameRender.Waiting(reason: (feed.Fault ?? "probe awaiting a first frame"))));
        public string? Fault => (feed.Live
            ? null
            : (feed.Fault ?? "probe awaiting a first frame")
        );
        public ProbeFeed Feed => feed;
        public Vector3 Light => Vector3.Zero;

        public GpuImageLease AcquireFrame() => feed.AcquireFrame();
        public void Dispose() { }
        public nint Handle() => feed.Handle();
        public void NotifyDeviceLost() { }
        // The binder services the ring each publish; a probe whose ring could not be provisioned refuses until a later
        // provision succeeds, and one provisioned waits for its kernel's first frame.
        public FrameRender Publish(in FrameContext context) => Answer;
    }
    // A view export on the Vulkan host: the render device's import of a texture and a fence the binder's headless
    // Direct3D 12 device made, which the engine writes and signals as it would an image of its own. The import goes first,
    // then the fence and the texture, and the headless device last of all.
    private sealed class ImportedViewExport : IGpuExportableImage {
        private readonly IGpuExportableFence m_fence;
        private readonly IGpuExportableImage m_imported;
        private readonly DisposeAfterDependents<IDisposable> m_targetDevice;
        private readonly IGpuExportableImage m_texture;

        private bool m_disposed;

        public ImportedViewExport(IGpuExportableImage imported, IGpuExportableImage texture, IGpuExportableFence fence, DisposeAfterDependents<IDisposable> targetDevice) {
            m_fence = fence;
            m_imported = imported;
            m_targetDevice = targetDevice;
            m_texture = texture;
            targetDevice.AddDependent();
        }

        public GpuPixelFormat Format => m_imported.Format;
        public uint Height => m_imported.Height;
        public nint ImageHandle => m_imported.ImageHandle;
        public nint ImageViewHandle => m_imported.ImageViewHandle;
        public nint SharedFenceHandle => m_imported.SharedFenceHandle;
        public nint SharedHandle => m_imported.SharedHandle;
        public GpuImageUsage Usage => m_imported.Usage;
        public uint Width => m_imported.Width;

        public void BeginWrite() => m_imported.BeginWrite();
        public ulong CompleteWrite() => m_imported.CompleteWrite();
        public void Dispose() {
            if (m_disposed) {
                return;
            }

            m_disposed = true;
            m_imported.Dispose();
            m_fence.Dispose();
            m_texture.Dispose();
            m_targetDevice.RemoveDependent();
        }
    }
    // Provisions a probe's ring of the requested extent on the render adapter: shared simultaneous-access targets and the
    // shared fence the kernel signals. Returns the refusal that leaves the probe dark, or null.
    private string? ProvisionProbeRing(IGpuDeviceContext deviceContext, ProbeFeed feed, long adapterLuid, int width, int height) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )) {
            return "shared probe textures need Windows 10";
        }
        if (!TryProvisionSharedRing(
            adapterLuid: adapterLuid,
            deviceContext: deviceContext,
            fault: out var fault,
            fence: out var fence,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: height,
            images: out var images,
            importedSurfaces: out var surfaces,
            imports: out var imports,
            sharedFence: true,
            width: width
        )) {
            return fault;
        }

        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: images.Count);

        var targets = new SharedTargetRing(
            fence: fence,
            images: images,
            importedSurfaces: surfaces,
            imports: imports,
            ring: slots,
            targetDevice: m_cameraTargetDevice
        );

        feed.Targets = targets;
        feed.Output = new ProbeKernelOutput(
            Width: width,
            Height: height,
            TargetFormat: GpuPixelFormat.R8G8B8A8Unorm,
            SharedTargetHandles: targets.SharedHandles,
            Slots: slots,
            SharedFenceHandle: targets.ProducerFenceHandle
        );

        return null;
    }
    private sealed partial class ProbeFeed {
        // How the kernel's writes reach the render device: the ring's shared fence unless the render device refused it,
        // then as the kernel opened it.
        public SharedFenceOrder Order => ((Targets is { FenceRefusal.Length: > 0 } targets)
            ? new SharedFenceOrder(
                Reason: targets.FenceRefusal,
                SharedFence: false
            )
            : (Run?.Order ?? SharedFenceOrder.Pending));

        public GpuImageLease AcquireFrame() {
            if (
                !Live ||
                (Targets is not { } targets)
            ) {
                return 0;
            }

            return (targets.TryAcquire(frame: out var frame)
                ? frame
                : 0
            );
        }
        public nint Handle() {
            if (
                !Live ||
                (Output is not { } output) ||
                (Targets is not { } targets)
            ) {
                return 0;
            }

            return targets.Handle(slot: output.Slots.LatestSlot);
        }
    }
    private sealed partial class ViewExportFeed {
        public object? CompletedGeneration { get; private set; }
        public ProbeKernelInput.Ring? Input { get; set; }
        public object? InputGeneration { get; set; }

        public IGpuExportableImage Create(IGpuDeviceContext device) => Factory(arg: device);
        public bool TryBeginWrite() => Slots.TryBeginWrite();
        // Publishes the image a completed frame rendered into before the ring's ready state. A failed first submission
        // after device loss may preserve an older readable image, but it must never bless the replacement image as
        // completed.
        public void EndWrite(bool written, IGpuExportableImage? image, ulong writtenValue) {
            if (written) {
                CompletedGeneration = image;
            }

            Slots.EndWrite(
                completed: written,
                fenceValue: writtenValue
            );
        }
    }
    // The ring a view export's latest rendered frame reads through, or null until a frame has rendered into this image.
    private static ProbeKernelInput.Ring? ReadyRing(ViewExportFeed feed, IGpuExportableImage image) {
        if (
            (0 == image.SharedHandle) ||
            !feed.Slots.HasCompletedFrame ||
            !ReferenceEquals(
                objA: feed.CompletedGeneration,
                objB: image
            )
        ) {
            return null;
        }

        if (!ReferenceEquals(
            objA: feed.InputGeneration,
            objB: image
        )) {
            feed.Input = new ProbeKernelInput.Ring(
                Format: ViewExportFormat,
                Height: ((int)feed.Height),
                SharedFenceHandle: image.SharedFenceHandle,
                SharedTargetHandles: [image.SharedHandle],
                Slots: feed.Slots,
                Width: ((int)feed.Width)
            );
            feed.InputGeneration = image;
        }

        return feed.Input;
    }
    // Opens the render adapter's kernel host the first time a probe asks for it.
    private bool TryOpenRenderedKernels(long adapterLuid, [NotNullWhen(true)] out IProbeKernelHost? host, out string fault) {
        if (m_renderedKernels is { } open) {
            host = open;
            fault = "";

            return true;
        }
        if (!m_probeKernelHosts.TryOpen(
            adapterLuid: adapterLuid,
            fault: out fault,
            host: out var opened
        )) {
            host = null;

            return false;
        }

        m_renderedKernels = opened;
        host = opened;

        return true;
    }
    // Wakes the render adapter's kernel host, when one is open, so every kernel whose trigger published runs.
    private void SignalRenderedKernels() => m_renderedKernels?.Signal();
    // The factory a view export's image is created on the render device through: the render device's own shared texture
    // on the Direct3D 12 host, or one the binder's headless Direct3D 12 device makes for the Vulkan host to import.
    [SupportedOSPlatform("windows10.0.10240")]
    private Func<IGpuDeviceContext, IGpuExportableImage> ExportFactory(uint width, uint height) => (m_hostsOnDirectX
        ? device => new DirectXGpuSurfaceExportFactory(deviceContext: ((DirectXDeviceContext)device)).CreateSharedComputeImage(
            format: ViewExportFormat,
            height: height,
            width: width
        )
        : device => CreateImportedViewExport(
            device: device,
            height: height,
            width: width
        ));}
