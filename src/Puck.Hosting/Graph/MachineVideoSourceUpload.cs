using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Sources;

namespace Puck.Hosting;

/// <summary>
/// A machine's video output as an uploaded source: each render writes the output's latest complete frame into the
/// source's region, in the format the output declares (RGBA8, or an indexed image and its palette), and the runtime
/// converts it through the conversion that format names, once however many screens show it. The source is deterministic
/// and due once per completed tick, and it states the image it last wrote (<see cref="IImageSourceReference"/>): the
/// CPU reference of that conversion over the bytes it wrote, with the descriptor and stamp of that write, which the exact
/// verdict holds a capture of the converted image to. Its <see cref="Descriptor"/> follows the output the resolver names
/// now, so a machine replaced by one with another extent or format is declared anew and the runtime rebuilds the
/// source's conversion for it; only a write replaces the image the source states. The output belongs to its machine;
/// the source only reads it.
/// </summary>
public sealed class MachineVideoSourceUpload : IRenderGraphSourceUpload, IImageSourceReference {
    private readonly Func<IMachineVideoOutput?> m_output;
    private readonly string m_name;
    private readonly string m_producer;

    private ImageSourceDescriptor? m_descriptor;
    // Whether the descriptor, the fault and the scratch region describe m_shape yet.
    private bool m_described;
    private string? m_fault;
    // The region the next write fills, header first, for the declared shape.
    private byte[] m_region = [];
    // The output shape the descriptor, the fault and the scratch region were made for.
    private (ImagePixelFormat Format, int Width, int Height)? m_shape;
    // The last write: its region, header first, its descriptor and its stamp, which only the next write replaces.
    private ImageSourceDescriptor? m_written;
    private byte[] m_writtenRegion = [];
    private ImageSourceStamp m_stamp;

    /// <summary>Initializes a new instance of the <see cref="MachineVideoSourceUpload"/> class over the output its
    /// resolver names on every read.</summary>
    /// <param name="producer">The producer id the descriptor names.</param>
    /// <param name="name">The source's name, which a fault names.</param>
    /// <param name="output">Resolves the machine output, or returns <see langword="null"/> while the machine does not run
    /// it.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MachineVideoSourceUpload(string producer, string name, Func<IMachineVideoOutput?> output) {
        ArgumentNullException.ThrowIfNull(argument: producer);
        ArgumentNullException.ThrowIfNull(argument: name);
        ArgumentNullException.ThrowIfNull(argument: output);

        m_name = name;
        m_output = output;
        m_producer = producer;
    }

    /// <inheritdoc/>
    /// <remarks>The descriptor of the output the resolver names now; <see langword="null"/> while no output runs or it
    /// declares a format or extent no region lays out.</remarks>
    public ImageSourceDescriptor? Descriptor {
        get {
            Describe(output: m_output());

            return m_descriptor;
        }
    }
    /// <inheritdoc/>
    public string? Fault {
        get {
            Describe(output: m_output());

            return m_fault;
        }
    }

    /// <summary>Gets the descriptor of the image the source last wrote, which its reference states; before the first write,
    /// the descriptor it declares now.</summary>
    /// <exception cref="InvalidOperationException">The source has written nothing and declares no image.</exception>
    ImageSourceDescriptor IImageSourceReference.Descriptor => (m_written ?? (Descriptor ?? throw new InvalidOperationException(message: m_fault)));

    // Brings the descriptor, the fault and the scratch region to the output's shape, remaking them only when it moves. The
    // last write is left as it is.
    private void Describe(IMachineVideoOutput? output) {
        (ImagePixelFormat, int, int)? shape = ((output is null)
            ? null
            : (output.Format, output.Width, output.Height));

        if (
            m_described &&
            (shape == m_shape)
        ) {
            return;
        }

        m_described = true;
        m_shape = shape;
        m_descriptor = null;
        m_region = [];

        if (output is null) {
            m_fault = $"machine output '{m_name}' is not running";

            return;
        }

        if (output.Format is not (ImagePixelFormat.R8G8B8A8Unorm or ImagePixelFormat.Indexed8)) {
            m_fault = $"machine output '{m_name}' declares {output.Format}; a machine writes R8G8B8A8Unorm or Indexed8 frames";

            return;
        }

        ImageSourceUploadHeader header;

        try {
            header = ImageSourceUploadLayout.HeaderOf(
                color: ImageColorEncoding.Srgb,
                format: output.Format,
                height: ((uint)output.Height),
                width: ((uint)output.Width)
            );
        } catch (ArgumentOutOfRangeException exception) {
            m_fault = $"machine output '{m_name}' is {output.Width}x{output.Height}, which no region lays out: {exception.Message}";

            return;
        }

        m_fault = null;
        m_region = new byte[ImageSourceUploadLayout.ByteCount(header: in header)];
        ImageSourceUploadLayout.Write(
            header: in header,
            region: m_region
        );
        m_descriptor = new ImageSourceDescriptor(
            Cadence: ImageSourceCadence.Tick,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.Deterministic,
            Format: output.Format,
            Height: header.Height,
            Producer: m_producer,
            Transport: ImageSourceTransport.Uploaded,
            Width: header.Width
        );
    }

    /// <inheritdoc/>
    public void Dispose() { }
    /// <inheritdoc/>
    /// <remarks>Writes the output's latest complete frame, and returns <see langword="false"/> while the output has no
    /// frame, or while its shape no longer matches the region, which the runtime rebuilds before the next frame. A write
    /// becomes the image the source states.</remarks>
    public bool TryWrite(long tick, GpuRegion region) {
        ArgumentNullException.ThrowIfNull(argument: region);

        var output = m_output();

        Describe(output: output);

        if (
            (output is null) ||
            (m_descriptor is null) ||
            (region.ByteCount != m_region.Length) ||
            (output.WriteFrame(region: m_region) <= 0L)
        ) {
            return false;
        }

        _ = region.Write(
            bytes: m_region.AsSpan(start: ImageSourceUploadLayout.HeaderBytes),
            offset: ImageSourceUploadLayout.HeaderBytes
        );

        // The written region becomes the stated image, and the previous one of the same shape the next write's scratch,
        // so a steady tick allocates nothing.
        var spare = m_writtenRegion;

        m_writtenRegion = m_region;
        m_written = m_descriptor;

        if (spare.Length == m_region.Length) {
            m_region = spare;
        } else {
            m_region = new byte[m_writtenRegion.Length];
            m_writtenRegion.AsSpan(
                length: ImageSourceUploadLayout.HeaderBytes,
                start: 0
            ).CopyTo(destination: m_region);
        }

        m_stamp = new ImageSourceStamp(
            Sequence: (m_stamp.Sequence + 1UL),
            Tick: ((ulong)Math.Max(
                val1: 0L,
                val2: tick
            ))
        );

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>States the last write, whatever the source declares since.</remarks>
    public bool TryWriteReference(Span<byte> rgba, out ImageSourceStamp stamp) {
        stamp = m_stamp;

        if (m_stamp.Sequence == 0UL) {
            return false;
        }

        ImageSourceConversion.ToRgba8(
            region: m_writtenRegion,
            rgba: rgba
        );

        return true;
    }
}
