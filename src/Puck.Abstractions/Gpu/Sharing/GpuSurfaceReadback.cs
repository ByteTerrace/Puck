namespace Puck.Abstractions.Gpu;

/// <summary>
/// Reads an image back into tightly packed host pixels on any backend, through a device context's neutral services: a
/// readback buffer laid out by the device's copy footprint (<see cref="GpuCopyFootprint"/>), one command buffer and its
/// submission fence. Each <see cref="Read"/> records the copy between transitions that leave the image in the layout it
/// came in, makes the copy's write visible to the host, submits it, waits on its own fence (the copy, and by queue order
/// everything submitted before it, has then run), and unpacks the padded rows. The readback and command buffers are read
/// by this object's own copy alone, which every read waits for before it returns, so neither is rewritten while a copy
/// reading or writing it is in flight.
/// <para>
/// It holds its resources on the device of its first read (<see cref="GpuDeviceOwnership"/>) and is released before
/// that device goes, a device loss included. A recording that fails leaves no submission and discards its command
/// buffer. Single-thread affine.
/// </para>
/// </summary>
public sealed class GpuSurfaceReadback : IGpuSurfaceReadback {
    private const string Owner = "surface.readback";

    private readonly IGpuDeviceContext m_deviceContext;
    private readonly Func<IGpuHeldDevice> m_device;
    private readonly GpuCommandSlot m_slot = new(name: new GpuObjectName(owner: Owner, part: "commands"));

    private IGpuReadbackBuffer? m_buffer;
    private bool m_disposed;
    private GpuCopyFootprint m_footprint;
    private IGpuHeldDevice? m_heldDevice;

    /// <summary>Initializes a new instance of the <see cref="GpuSurfaceReadback"/> class.</summary>
    /// <param name="deviceContext">The device context whose services create, record and submit everything.</param>
    /// <param name="device">Returns the native device the context holds now, which the readback holds its resources
    /// on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> or <paramref name="device"/> is
    /// <see langword="null"/>.</exception>
    public GpuSurfaceReadback(IGpuDeviceContext deviceContext, Func<IGpuHeldDevice> device) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(device);

        m_deviceContext = deviceContext;
        m_device = device;
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The context's device is not the one an earlier read created this
    /// instance's resources on.</exception>
    public ReadOnlyMemory<byte> Read(nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, GpuImageLayout sourceLayout) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        using var attribution = GpuQueueWork.Attribute(category: GpuQueueCategory.Readback);

        ArgumentOutOfRangeException.ThrowIfZero(value: sourceImageHandle);

        var offered = m_device();

        GpuDeviceOwnership.ThrowIfOtherDevice(
            held: m_heldDevice,
            holder: nameof(GpuSurfaceReadback),
            offered: offered
        );
        m_heldDevice = offered;

        var services = m_deviceContext.Services;

        // An earlier read whose wait threw may have left its copy running into the buffer.
        m_slot.Wait();
        EnsureBuffer(
            format: format,
            height: height,
            services: services,
            width: width
        );
        m_slot.Record(
            record: static (recorder, commandBuffer, read) => GpuReadbackCopy.RecordImage(
                commandBuffer: commandBuffer,
                destinationBufferHandle: read.Buffer,
                footprint: in read.Footprint,
                imageHandle: read.Image,
                recorder: recorder,
                sourceLayout: read.Layout
            ),
            services: services,
            state: (Buffer: m_buffer!.BufferHandle, Footprint: m_footprint, Image: sourceImageHandle, Layout: sourceLayout)
        );
        m_slot.Submit(services: services);
        m_slot.Wait();

        return Unpack();
    }

    // Creates the readback buffer an extent or format needs. The buffer it replaces is idle: every read waits for its
    // copy before returning, and Read waited again for one whose wait threw.
    private void EnsureBuffer(GpuDeviceServices services, GpuPixelFormat format, uint width, uint height) {
        var footprint = GpuCopyFootprint.Of(
            format: format,
            height: height,
            rowAlignment: services.Recorder.CopyRowAlignment,
            width: width
        );

        if (
            (m_buffer is not null) &&
            (footprint == m_footprint)
        ) {
            return;
        }

        var buffer = services.BufferFactory.CreateReadback(
            name: new GpuObjectName(owner: Owner, part: "buffer"),
            sizeBytes: footprint.ByteLength
        );

        m_buffer?.Dispose();
        m_buffer = buffer;
        m_footprint = footprint;
    }
    // Copies the completed copy's rows, without their padding, into a new tightly packed array.
    private byte[] Unpack() {
        var footprint = m_footprint;
        var buffer = m_buffer!;
        var pixels = new byte[checked((int)GpuPixelFormats.ImageByteLength(
            format: footprint.Format,
            height: footprint.Height,
            width: footprint.Width
        ))];

        if (footprint.IsPacked) {
            buffer.Read(destination: pixels);

            return pixels;
        }

        var rowBytes = ((int)footprint.PackedRowBytes);

        for (var row = 0U; (row < footprint.Height); row++) {
            buffer.Read(
                destination: pixels.AsSpan(
                    length: rowBytes,
                    start: (((int)row) * rowBytes)
                ),
                sourceOffsetBytes: (((ulong)row) * footprint.RowPitch)
            );
        }

        return pixels;
    }

    /// <summary>Waits for this object's last copy, then releases the readback buffer, the command buffer and the fence.
    /// A lost device counts as idle. Safe to call more than once.</summary>
    /// <exception cref="InvalidOperationException">The device was destroyed first, so these resources can no longer be
    /// released and the owner's teardown order is wrong; the readback stays undisposed.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (m_heldDevice is { } held) {
            GpuDeviceOwnership.ThrowIfDestroyed(
                held: held,
                holder: nameof(GpuSurfaceReadback)
            );
        }

        m_disposed = true;
        m_slot.Dispose();
        m_buffer?.Dispose();
        m_buffer = null;
    }
}
