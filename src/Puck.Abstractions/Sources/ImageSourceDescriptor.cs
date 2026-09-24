namespace Puck.Abstractions.Sources;

/// <summary>How a source's image arrives in rendering from outside a pass.</summary>
public enum ImageSourceTransport : byte {
    /// <summary>The producer writes pixels in CPU memory and the engine uploads them: an emulator with a CPU-side picture
    /// unit, a procedural pattern, a rasterized code, software video decoding.</summary>
    Uploaded = 1,
    /// <summary>The producer owns GPU memory and shares it with a synchronization primitive: desktop capture, a camera,
    /// hardware video decoding, another process.</summary>
    Imported = 2,
    /// <summary>Another render instance produces it: a game camera, a nested world, a probe kernel's output.</summary>
    Rendered = 3,
}
/// <summary>What a source's pixels are a function of, which decides the verification and privacy that apply to it.</summary>
public enum ImageContentClass : byte {
    /// <summary>An exact function of simulation state, such as an emulator framebuffer: its image gets an exact pixel
    /// verdict (<see cref="ImageSourceVerdict"/>).</summary>
    Deterministic = 1,
    /// <summary>Content from outside the engine, which may be private, such as a desktop or a camera. It never reaches
    /// simulation state, a replay, or the state hash, and a capture shows its declared
    /// <see cref="ImageSourceDescriptor.CaptureFill"/> instead of its pixels.</summary>
    External = 2,
    /// <summary>Ordinary rendered output.</summary>
    Presentation = 3,
}
/// <summary>When a source produces a new image.</summary>
public enum ImageRefresh : byte {
    /// <summary>The image is a pure function of the source's declaration and never changes after its first upload.</summary>
    Static = 1,
    /// <summary>The image follows the simulation: at most one new image per completed tick.</summary>
    Tick = 2,
    /// <summary>The image arrives on the producer's own clock, at most <see cref="ImageSourceCadence.RateHz"/> times a
    /// second.</summary>
    Rate = 3,
}
/// <summary>A source's refresh cadence.</summary>
/// <param name="Refresh">When the source produces a new image.</param>
/// <param name="RateHz">The most images a second a <see cref="ImageRefresh.Rate"/> source produces; zero otherwise.</param>
public readonly record struct ImageSourceCadence(ImageRefresh Refresh, uint RateHz = 0U) {
    /// <summary>Gets the cadence of an image that never changes.</summary>
    public static ImageSourceCadence Static { get; } = new(Refresh: ImageRefresh.Static);
    /// <summary>Gets the cadence of an image that follows the simulation tick.</summary>
    public static ImageSourceCadence Tick { get; } = new(Refresh: ImageRefresh.Tick);

    /// <summary>Returns the cadence of a source on its own clock.</summary>
    /// <param name="rateHz">The most images a second; positive.</param>
    /// <returns>The cadence.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rateHz"/> is zero.</exception>
    public static ImageSourceCadence Rate(uint rateHz) {
        ArgumentOutOfRangeException.ThrowIfZero(value: rateHz);

        return new ImageSourceCadence(
            RateHz: rateHz,
            Refresh: ImageRefresh.Rate
        );
    }
}
/// <summary>An image's presentation timestamp: which image of its source it is, and the simulation tick it presents.
/// A deterministic source stamps the tick its image is a function of; any other source stamps the tick presentation had
/// reached when the image arrived, which orders it against the frame but is never compared across runs.</summary>
/// <param name="Sequence">The image's ordinal within its source, starting at one; zero means no image yet.</param>
/// <param name="Tick">The simulation tick the image presents.</param>
public readonly record struct ImageSourceStamp(ulong Sequence, ulong Tick);
/// <summary>
/// One image that enters rendering from outside a pass: which producer makes it, how it arrives, and what it holds. A
/// consumer reads this and nothing about the producer's implementation, so an emulator, a capture API, or a video
/// decoder joins rendering by registering a producer (<see cref="ImageSourceProducerRegistry{TProducer}"/>).
/// </summary>
/// <param name="Producer">The registered producer's id.</param>
/// <param name="Transport">How the image arrives.</param>
/// <param name="Width">The image's width in pixels; zero while the producer has not negotiated one.</param>
/// <param name="Height">The image's height in pixels; zero while the producer has not negotiated one.</param>
/// <param name="Format">The pixel format the producer writes, which decides the conversion pass it needs.</param>
/// <param name="Color">The color encoding of those pixels.</param>
/// <param name="Cadence">When the producer writes a new image.</param>
/// <param name="Content">What the pixels are a function of.</param>
/// <param name="CaptureFill">The packed RGBA8 color (red in the low byte) a capture shows in place of an
/// <see cref="ImageContentClass.External"/> source's pixels. Every class carries one, and only an external source's is
/// ever shown.</param>
public sealed record ImageSourceDescriptor(
    string Producer,
    ImageSourceTransport Transport,
    uint Width,
    uint Height,
    ImagePixelFormat Format,
    ImageColorEncoding Color,
    ImageSourceCadence Cadence,
    ImageContentClass Content,
    uint CaptureFill = ImageSourceDescriptor.DefaultCaptureFill
) {
    /// <summary>The capture fill a source declares when it names none: opaque dark gray, <c>#202020</c>.</summary>
    public const uint DefaultCaptureFill = 0xFF202020U;

    /// <summary>Gets whether a capture shows <see cref="CaptureFill"/> in place of this source's pixels.</summary>
    public bool FillsCaptures => (Content == ImageContentClass.External);
}
