namespace Puck.Abstractions.Gpu;

/// <summary>
/// Uploads tightly packed CPU pixels into a sampled image on any backend, through a device context's neutral services:
/// a host-visible staging buffer laid out by the device's copy footprint (<see cref="GpuCopyFootprint"/>), one command
/// buffer and its submission fence, and an image the image factory creates (every image is a copy destination). Each
/// <see cref="Upload"/> writes the staging buffer, records the copy between the image's sampling transitions and submits
/// it without waiting: a consumer submitted to the same queue afterwards samples the copied pixels by queue order and
/// the recorded barriers.
/// <para>
/// Two lifetimes are kept. The staging buffer and the command buffer are read by this object's own copy alone, so an
/// upload waits for its previous copy's fence, and only that, before it rewrites either; a steady per-frame feed queued
/// ahead of the frame that samples it finds that copy long retired. An image replaced by an extent or format change may
/// still be sampled by any submission made before the replacement, so it waits in a <see cref="GpuRetirementQueue{T}"/>
/// keyed by the copy that writes its replacement: one queue completes in order, so that copy's fence passing proves
/// every earlier submission done.
/// </para>
/// <para>
/// It holds its resources on the device of its first upload (<see cref="GpuDeviceOwnership"/>) and is released before
/// that device goes, a device loss included. A recording that fails leaves no submission and discards its command
/// buffer, so the next upload records on a fresh one; a replacement that fails to create leaves the current image and
/// its view in place. Single-thread affine.
/// </para>
/// </summary>
public sealed class GpuSurfaceUpload : IGpuSurfaceUpload {
    // The stages a consumer samples an uploaded image in: a compute kernel (the SDF views' screen sources) or a fragment
    // shader (a presenter's encode).
    private const GpuStage SamplingStages = (GpuStage.ComputeShader | GpuStage.FragmentShader);
    private const string Owner = "surface.upload";

    private readonly IGpuDeviceContext m_deviceContext;
    private readonly Func<IGpuHeldDevice> m_device;
    // Images a rebuild replaced, each until the copy into its replacement completes.
    private readonly GpuRetirementQueue<IGpuImage> m_retired = new();
    private readonly GpuCommandSlot m_slot = new(name: new GpuObjectName(owner: Owner, part: "commands"));

    private bool m_disposed;
    private GpuCopyFootprint m_footprint;
    private IGpuHeldDevice? m_heldDevice;
    private IGpuImage? m_image;
    // The layout the image is in once every recorded copy has run: Undefined until its first copy.
    private GpuImageLayout m_layout;
    private IGpuStorageBuffer? m_staging;
    // Copies handed to the queue: the completion points the retired images are keyed by.
    private ulong m_submitted;

    /// <summary>Initializes a new instance of the <see cref="GpuSurfaceUpload"/> class.</summary>
    /// <param name="deviceContext">The device context whose services create, record and submit everything.</param>
    /// <param name="device">Returns the native device the context holds now, which the upload holds its resources on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> or <paramref name="device"/> is
    /// <see langword="null"/>.</exception>
    public GpuSurfaceUpload(IGpuDeviceContext deviceContext, Func<IGpuHeldDevice> device) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(device);

        m_deviceContext = deviceContext;
        m_device = device;
    }

    /// <summary>Gets the native handle of the image the last <see cref="Upload"/> wrote, in
    /// <see cref="GpuImageLayout.ShaderReadOnly"/> once that copy runs, or zero before the first upload. Like the view,
    /// it is valid until the next upload or disposal.</summary>
    public nint ImageHandle => (m_image?.ImageHandle ?? 0);

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The context's device is not the one an earlier upload created this
    /// instance's resources on.</exception>
    public nint Upload(ReadOnlyMemory<byte> pixels, GpuPixelFormat format, uint width, uint height) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        using var attribution = GpuQueueWork.Attribute(category: GpuQueueCategory.Upload);

        _ = GpuPixelFormats.RequireImage(
            byteLength: pixels.Length,
            format: format,
            height: height,
            width: width
        );

        var offered = m_device();

        GpuDeviceOwnership.ThrowIfOtherDevice(
            held: m_heldDevice,
            holder: nameof(GpuSurfaceUpload),
            offered: offered
        );
        m_heldDevice = offered;

        var services = m_deviceContext.Services;

        Settle();
        EnsureResources(
            format: format,
            height: height,
            services: services,
            width: width
        );
        WriteStaging(pixels: pixels.Span);
        m_slot.Record(
            record: static (recorder, commandBuffer, upload) => upload.Record(
                commandBuffer: commandBuffer,
                recorder: recorder
            ),
            services: services,
            state: this
        );
        m_slot.Submit(services: services);
        m_submitted++;
        m_layout = GpuImageLayout.ShaderReadOnly;

        return m_image!.ImageViewHandle;
    }

    // Waits for this object's last copy, the one reader of the staging and command buffers, and releases the images
    // that copy's completion frees. A wait that throws leaves the copy outstanding, so the next call waits again.
    private void Settle() {
        m_slot.Wait();
        m_retired.Release(
            completed: m_submitted,
            release: static image => image.Dispose()
        );
    }
    // Creates the image and staging buffer an extent or format needs, retiring the image they replace behind the copy
    // that writes its replacement. Everything that can fail comes before anything is swapped, so a refused creation
    // leaves the current image, its view and its staging buffer as they were.
    private void EnsureResources(GpuDeviceServices services, GpuPixelFormat format, uint width, uint height) {
        if (
            (m_image is { } current) &&
            (current.Format == format) &&
            (current.Width == width) &&
            (current.Height == height)
        ) {
            return;
        }

        var footprint = GpuCopyFootprint.Of(
            format: format,
            height: height,
            rowAlignment: services.Recorder.CopyRowAlignment,
            width: width
        );

        using var scope = new GpuCreationScope();
        var image = scope.Own(created: services.ImageFactory.Create(
            format: format,
            height: height,
            name: new GpuObjectName(owner: Owner, part: "image"),
            usage: GpuImageUsage.Sampled,
            width: width
        ));
        var staging = scope.Own(created: services.BufferFactory.CreateHostVisible(
            name: new GpuObjectName(owner: Owner, part: "staging"),
            sizeBytes: footprint.ByteLength,
            usage: GpuBufferUsage.Storage
        ));

        m_retired.Reserve();
        scope.Complete();

        // Settle proved every copy that read the staging buffer complete, so the replaced one goes now; the replaced
        // image may still be sampled by any submission made before this one, so it waits for the copy into its
        // replacement.
        m_staging?.Dispose();

        if (m_image is { } replaced) {
            m_retired.Retire(
                after: (m_submitted + 1UL),
                item: replaced
            );
        }

        m_footprint = footprint;
        m_image = image;
        m_layout = GpuImageLayout.Undefined;
        m_staging = staging;
    }
    // Writes the image's tightly packed rows at the footprint's pitch.
    private void WriteStaging(ReadOnlySpan<byte> pixels) {
        var staging = m_staging!;
        var footprint = m_footprint;

        if (footprint.IsPacked) {
            staging.Write(data: pixels);

            return;
        }

        var rowBytes = ((int)footprint.PackedRowBytes);

        for (var row = 0U; (row < footprint.Height); row++) {
            staging.Write(
                data: pixels.Slice(
                    length: rowBytes,
                    start: (((int)row) * rowBytes)
                ),
                destinationOffsetBytes: (((ulong)row) * footprint.RowPitch)
            );
        }
    }
    // Records the copy between the image's sampling transitions. Each copy rewrites the whole image; the first barrier
    // orders it after every earlier sampler on the queue.
    private void Record(IGpuRecorder recorder, nint commandBuffer) {
        var image = m_image!.ImageHandle;

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.TransferWrite,
            destinationStageMask: GpuStage.Transfer,
            imageHandle: image,
            newLayout: GpuImageLayout.TransferDestination,
            oldLayout: m_layout,
            sourceAccessMask: GpuAccess.None,
            sourceStageMask: SamplingStages
        );
        recorder.CopyBufferToImage(
            bufferHandle: m_staging!.BufferHandle,
            commandBufferHandle: commandBuffer,
            footprint: in m_footprint,
            imageHandle: image
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: SamplingStages,
            imageHandle: image,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.TransferDestination,
            sourceAccessMask: GpuAccess.TransferWrite,
            sourceStageMask: GpuStage.Transfer
        );
    }

    /// <summary>Waits for the device to go idle, since any submission may still sample the current or a replaced image,
    /// then releases every image, the staging buffer, the command buffer and the fence. A lost device counts as idle.
    /// Safe to call more than once.</summary>
    /// <exception cref="InvalidOperationException">The device was destroyed first, so these resources can no longer be
    /// released and the owner's teardown order is wrong; the upload stays undisposed.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (m_heldDevice is { } held) {
            GpuDeviceOwnership.ThrowIfDestroyed(
                held: held,
                holder: nameof(GpuSurfaceUpload)
            );

            try {
                m_deviceContext.WaitIdle();
            } catch (DeviceLostException) {
                // Nothing on a lost device runs again.
            }
        }

        m_disposed = true;
        m_retired.Release(
            completed: ulong.MaxValue,
            release: static image => image.Dispose()
        );
        m_image?.Dispose();
        m_image = null;
        m_staging?.Dispose();
        m_staging = null;
        m_slot.Dispose();
    }
}
