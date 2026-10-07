using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>The <c>color</c> producer: one flat colour, a one-pixel image a screen's mapping stretches over its face,
/// written once and converted once by the source instance that shows it. Every screen showing one colour reads one
/// instance, named by the colour (<see cref="WorldViewNames.Source"/>).</summary>
public sealed class WorldColorProducer : IWorldImageProducer {
    /// <inheritdoc/>
    public ImageContentClass Content => ImageContentClass.Deterministic;
    /// <inheritdoc/>
    public string Id => WorldImageProducerSettings.ColorId;
    /// <inheritdoc/>
    public ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

    /// <inheritdoc/>
    public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var (settings, refusal) = WorldImageProducerSettings.Bind<WorldColorSettings>(producer: source);

        if (settings is null) {
            feed = null;
            fault = refusal;

            return false;
        }

        if (!HexColor.TryParse(
            rgb: out var rgb,
            value: settings.Color
        )) {
            feed = null;
            fault = $"color '{settings.Color}' must be #RRGGBB";

            return false;
        }

        feed = new WorldColorFeed(rgb: rgb);
        fault = null;

        return true;
    }
}
/// <summary>One flat-colour source: a single B8G8R8A8 pixel. The colour never changes, so its static source converts it
/// once.</summary>
public sealed class WorldColorFeed : IWorldUploadFeed, IImageSourceReference {
    private readonly byte[] m_pixel;

    /// <summary>Initializes a new instance of the <see cref="WorldColorFeed"/> class.</summary>
    /// <param name="rgb">The colour, each component in <c>[0, 1]</c>.</param>
    public WorldColorFeed(Vector3 rgb) {
        m_pixel = [
            Component(value: rgb.Z),
            Component(value: rgb.Y),
            Component(value: rgb.X),
            byte.MaxValue,
        ];
        Descriptor = new ImageSourceDescriptor(
            Cadence: ImageSourceCadence.Static,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.Deterministic,
            Format: ImagePixelFormat.B8G8R8A8Unorm,
            Height: 1,
            Producer: WorldImageProducerSettings.ColorId,
            Transport: ImageSourceTransport.Uploaded,
            Width: 1
        );
        Light = WorldImageLight.Average(bgra: m_pixel);
    }

    /// <inheritdoc/>
    public ImageSourceDescriptor Descriptor { get; }
    /// <inheritdoc/>
    public string? Fault => null;
    /// <inheritdoc/>
    public Vector3 Light { get; }

    private static byte Component(float value) => ((byte)MathF.Round(x: (Math.Clamp(
        max: 1f,
        min: 0f,
        value: value
    ) * 255f)));

    /// <inheritdoc/>
    /// <remarks>The colour owns no resource.</remarks>
    public void Dispose() { }
    /// <inheritdoc/>
    /// <remarks>The colour never changes, so a region that already holds it owes nothing.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="region"/> is <see langword="null"/>.</exception>
    public FrameRender Write(long tick, GpuRegion region) {
        ArgumentNullException.ThrowIfNull(argument: region);

        _ = region.Write(
            bytes: m_pixel,
            offset: ImageSourceUploadLayout.HeaderBytes
        );

        return FrameRender.Rendered;
    }
    /// <inheritdoc/>
    public bool TryWriteReference(Span<byte> rgba, out ImageSourceStamp stamp) {
        ImageSourceConversion.BgraToRgba(
            bgra: m_pixel,
            rgba: rgba
        );

        stamp = new ImageSourceStamp(
            Sequence: 1UL,
            Tick: 0UL
        );

        return true;
    }
}
/// <summary>What a session screen past the presentation's nesting depth shows: its fallback colour
/// (<see cref="WorldScreenSource.Session.Fallback"/>, black when it authors none) through the <c>color</c> producer, the
/// one rule for every face past the depth, so two portals facing each other end on a colour the author chose.</summary>
public static class WorldPortalFallback {
    /// <summary>The colour a session shows past the depth when it authors none.</summary>
    public const string DefaultColor = "#000000";

    /// <summary>Returns the source a session screen past the nesting depth shows.</summary>
    /// <param name="session">The screen's session.</param>
    /// <returns>A <c>color</c> producer source of the session's fallback colour.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public static WorldScreenSource.Producer SourceOf(WorldScreenSource.Session session) {
        ArgumentNullException.ThrowIfNull(argument: session);

        return WorldImageProducerSettings.SourceOf(
            id: WorldImageProducerSettings.ColorId,
            settings: new WorldColorSettings(Color: (session.Fallback ?? DefaultColor))
        );
    }
}
