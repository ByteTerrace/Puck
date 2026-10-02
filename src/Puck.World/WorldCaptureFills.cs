using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The capture fills external content resolves to while a <see cref="WorldCaptureGate"/> fills: one static 1x1 image per
/// packed RGBA8 fill color, its one pixel converted through the <c>source-rgba</c> conversion on a converter of its own
/// (<see cref="RenderGraphRuntime.CreateConverter"/>), and again only after a device loss drops it. A converter builds its
/// graph off the frame thread and its first conversions wait for it, so a fill first converted on the frame a capture is
/// armed for has no image on that frame, and the capture would show nothing where the external content was. The fills
/// therefore convert whenever external content is consumed, gate filling or not: by the frame a capture is armed, each fill
/// a filled source resolves to has already converted. They convert only then: a gate filling while nothing consumes
/// external content resolves no image to a fill, so a capture of such a world, and every frame of an offscreen host that
/// fills always, creates no converter. Every member runs on the thread that produces frames.
/// </summary>
public sealed class WorldCaptureFills : IDisposable {
    private readonly Func<bool> m_consumesExternal;
    private readonly Dictionary<uint, WorldScreenBinder.ConvertedPixels> m_fills = new();

    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="WorldCaptureFills"/> class.</summary>
    /// <param name="consumesExternal">Answers whether any consumer shows external content this frame: a screen showing a
    /// source for which <see cref="IsExternal"/> holds, or a HUD frame naming one. Those are the only reads a
    /// <see cref="WorldCaptureGate"/> resolves to a fill.</param>
    /// <exception cref="ArgumentNullException"><paramref name="consumesExternal"/> is <see langword="null"/>.</exception>
    public WorldCaptureFills(Func<bool> consumesExternal) {
        ArgumentNullException.ThrowIfNull(argument: consumesExternal);

        m_consumesExternal = consumesExternal;
    }

    /// <summary>Returns whether a screen source shows external content: a producer whose registered shape's content class is
    /// <see cref="ImageContentClass.External"/> (a camera, a desktop capture), or a probe, which processes a camera's
    /// frames.</summary>
    /// <param name="source">The source a screen shows or a HUD frame names, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when the gate fills the source's image while it fills.</returns>
    public static bool IsExternal(WorldScreenSource? source) => source switch {
        WorldScreenSource.Producer producer => (WorldImageProducerVocabulary.TryGet(
            id: producer.Id,
            shape: out var shape
        ) && (shape.Content == ImageContentClass.External)),
        WorldScreenSource.Probe => true,
        _ => false,
    };
    /// <summary>Acquires the image of a fill color for one submitted frame, held until that frame's submission has
    /// retired.</summary>
    /// <param name="rgba">The packed RGBA8 fill color.</param>
    /// <returns>The fill's lease, or an empty lease (unbound glass, which shows no external pixels either)
    /// before the fill has converted.</returns>
    public GpuImageLease Acquire(uint rgba) => (m_fills.TryGetValue(
        key: rgba,
        value: out var fill
    )
        ? fill.Acquire()
        : 0
    );
    /// <summary>Converts the fills a frame needs, before any source of the frame resolves: whenever a consumer shows external
    /// content, whether or not the gate fills, it converts the default fill
    /// (<see cref="ImageSourceDescriptor.DefaultCaptureFill"/>), and otherwise converts nothing.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <param name="runtime">The runtime whose converters convert the fills, or <see langword="null"/> before one runs,
    /// when nothing converts.</param>
    /// <returns>Whether the frame needs fills, so the caller converts each source's own fill color through
    /// <see cref="Ensure"/>.</returns>
    public bool Begin(in FrameContext context, RenderGraphRuntime? runtime) {
        if (
            m_disposed ||
            !m_consumesExternal()
        ) {
            return false;
        }

        Ensure(
            context: in context,
            rgba: ImageSourceDescriptor.DefaultCaptureFill,
            runtime: runtime
        );

        return true;
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var fill in m_fills.Values) {
            fill.Retire();
        }

        m_fills.Clear();
    }
    /// <summary>Converts one fill color's image unless it has converted already.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <param name="rgba">The packed RGBA8 fill color.</param>
    /// <param name="runtime">The runtime whose converter converts the fill, or <see langword="null"/> before one runs,
    /// when nothing converts.</param>
    public void Ensure(in FrameContext context, uint rgba, RenderGraphRuntime? runtime) {
        if (m_disposed) {
            return;
        }

        if (!m_fills.TryGetValue(
            key: rgba,
            value: out var fill
        )) {
            fill = new WorldScreenBinder.ConvertedPixels(
                content: ImageContentClass.Presentation,
                name: $"fill:{rgba:x8}",
                producer: "fill"
            );
            m_fills[rgba] = fill;
        }

        if (
            (fill.Handle != 0) ||
            (runtime is null)
        ) {
            return;
        }

        ReadOnlySpan<byte> pixel = [((byte)rgba), ((byte)(rgba >> 8)), ((byte)(rgba >> 16)), ((byte)(rgba >> 24))];

        _ = fill.TryConvert(
            color: ImageColorEncoding.Srgb,
            context: in context,
            format: ImagePixelFormat.R8G8B8A8Unorm,
            height: 1U,
            planes: pixel,
            runtime: runtime,
            width: 1U
        );
    }
    /// <summary>Drops every fill's device objects after a device loss; each converts again on the recreated device.</summary>
    public void OnDeviceLost() {
        foreach (var fill in m_fills.Values) {
            fill.OnDeviceLost();
        }
    }
}
