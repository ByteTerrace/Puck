using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Sources;

namespace Puck.Hosting;

/// <summary>
/// A machine's video output as an uploaded source: each render writes the output's latest complete frame into the
/// source's region, in the format the output declares (RGBA8, or an indexed image and its palette), and the runtime
/// converts it through the conversion that format names, once however many screens show it. The source is deterministic
/// and due once per completed tick, and it states the image it last wrote (<see cref="IImageSourceReference"/>): the
/// CPU reference of that conversion over the bytes it wrote, which the exact verdict holds a capture of the converted
/// image to. The output belongs to its machine; the source only reads it.
/// </summary>
public sealed class MachineVideoSourceUpload : IRenderGraphSourceUpload, IImageSourceReference {
    private readonly Func<IMachineVideoOutput?> m_output;
    private readonly string m_name;
    // The whole region the source writes, header first: the bytes the conversion reads and the reference is computed
    // from.
    private readonly byte[] m_region = [];

    private string? m_fault;
    private ImageSourceStamp m_stamp;

    /// <summary>Initializes a new instance of the <see cref="MachineVideoSourceUpload"/> class over the output its
    /// resolver names now, whose format and extent fix the source's descriptor.</summary>
    /// <param name="producer">The producer id the descriptor names.</param>
    /// <param name="name">The source's name, which a fault names.</param>
    /// <param name="output">Resolves the machine output on every render, or returns <see langword="null"/> while the
    /// machine does not run it.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MachineVideoSourceUpload(string producer, string name, Func<IMachineVideoOutput?> output) {
        ArgumentNullException.ThrowIfNull(argument: producer);
        ArgumentNullException.ThrowIfNull(argument: name);
        ArgumentNullException.ThrowIfNull(argument: output);

        m_name = name;
        m_output = output;

        if (output() is not { } resolved) {
            m_fault = $"machine output '{name}' is not running";

            return;
        }

        if (resolved.Format is not (ImagePixelFormat.R8G8B8A8Unorm or ImagePixelFormat.Indexed8)) {
            m_fault = $"machine output '{name}' declares {resolved.Format}; a machine writes R8G8B8A8Unorm or Indexed8 frames";

            return;
        }

        ImageSourceUploadHeader header;

        try {
            header = ImageSourceUploadLayout.HeaderOf(
                color: ImageColorEncoding.Srgb,
                format: resolved.Format,
                height: ((uint)resolved.Height),
                width: ((uint)resolved.Width)
            );
        } catch (ArgumentOutOfRangeException exception) {
            m_fault = $"machine output '{name}' is {resolved.Width}x{resolved.Height}, which no region lays out: {exception.Message}";

            return;
        }

        m_region = new byte[ImageSourceUploadLayout.ByteCount(header: in header)];
        ImageSourceUploadLayout.Write(
            header: in header,
            region: m_region
        );
        Descriptor = new ImageSourceDescriptor(
            Cadence: ImageSourceCadence.Tick,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.Deterministic,
            Format: resolved.Format,
            Height: header.Height,
            Producer: producer,
            Transport: ImageSourceTransport.Uploaded,
            Width: header.Width
        );
    }

    /// <inheritdoc/>
    public ImageSourceDescriptor? Descriptor { get; }
    /// <inheritdoc/>
    /// <remarks>An output its machine no longer runs, or one that now declares another format or extent than the
    /// descriptor fixed, faults the source by name; the runtime renders nothing for it until the source is made
    /// again.</remarks>
    public string? Fault => m_fault;

    ImageSourceDescriptor IImageSourceReference.Descriptor => (Descriptor ?? throw new InvalidOperationException(message: m_fault));

    /// <inheritdoc/>
    public void Dispose() { }
    /// <inheritdoc/>
    /// <remarks>Writes the output's latest complete frame, and returns <see langword="false"/> while the output has no
    /// frame or no longer matches the descriptor.</remarks>
    public bool TryWrite(long tick, GpuRegion region) {
        ArgumentNullException.ThrowIfNull(argument: region);

        if (Descriptor is not { } descriptor) {
            return false;
        }

        if (m_output() is not { } output) {
            m_fault = $"machine output '{m_name}' is not running";

            return false;
        }

        if (
            (output.Format != descriptor.Format) ||
            (output.Width != descriptor.Width) ||
            (output.Height != descriptor.Height)
        ) {
            m_fault = $"machine output '{m_name}' is now {output.Format} at {output.Width}x{output.Height}; its source was made for {descriptor.Format} at {descriptor.Width}x{descriptor.Height}";

            return false;
        }

        m_fault = null;

        if (output.WriteFrame(region: m_region) <= 0L) {
            return false;
        }

        _ = region.Write(
            bytes: m_region.AsSpan(start: ImageSourceUploadLayout.HeaderBytes),
            offset: ImageSourceUploadLayout.HeaderBytes
        );
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
    public bool TryWriteReference(Span<byte> rgba, out ImageSourceStamp stamp) {
        stamp = m_stamp;

        if (m_stamp.Sequence == 0UL) {
            return false;
        }

        ImageSourceConversion.ToRgba8(
            region: m_region,
            rgba: rgba
        );

        return true;
    }
}
