using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The capture fills external content resolves to while a <see cref="WorldCaptureGate"/> fills, or while its feed has no
/// image of its own (<see cref="WorldCaptureGate.FillsFeed"/>): one static striped placeholder (<see cref="Placeholder"/>)
/// per packed RGBA8 fill color, its pixels converted through the <c>source-rgba</c> conversion on a converter of its own
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
    private readonly Dictionary<uint, WorldConvertedPixels> m_fills = new();

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

    /// <summary>Gets whether a fill's conversion graph is building on the thread pool.</summary>
    public bool IsBuilding {
        get {
            foreach (var fill in m_fills.Values) {
                if (fill.IsBuilding) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The width and height, in pixels, of the placeholder a capture shows in place of external content: a square of
    /// diagonal stripes in the source's fill color and a contrasting one, so a capture reader tells withheld content from
    /// content that failed to draw.</summary>
    public const uint PlaceholderExtent = 16U;

    /// <summary>Returns whether a screen source shows external content: a producer whose registered shape's content class is
    /// <see cref="ImageContentClass.External"/> (a camera, a desktop capture), or a probe that
    /// <paramref name="probeIsExternal"/> names external.</summary>
    /// <param name="source">The source a screen shows or a HUD frame names, or <see langword="null"/> for none.</param>
    /// <param name="probeIsExternal">Answers whether a probe, by its id, reads a real device (see
    /// <see cref="IsExternalProbe"/>).</param>
    /// <returns><see langword="true"/> when the gate fills the source's image while it fills.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probeIsExternal"/> is <see langword="null"/>.</exception>
    public static bool IsExternal(WorldScreenSource? source, Func<string, bool> probeIsExternal) {
        ArgumentNullException.ThrowIfNull(argument: probeIsExternal);

        return source switch {
            WorldScreenSource.Producer producer => (WorldImageProducerVocabulary.TryGet(
                id: producer.Id,
                shape: out var shape
            ) && (shape.Content == ImageContentClass.External)),
            WorldScreenSource.Probe probe => probeIsExternal(arg: probe.Id),
            _ => false,
        };
    }
    /// <summary>Returns whether a probe's output is external content, classified by what its inputs read: a probe that plays
    /// a recorded track, or whose every input is an in-world view or the output of a probe that is itself not external, is
    /// derived content; one reading a camera, a desktop capture or any other external producer, or an unknown probe, is
    /// external.</summary>
    /// <param name="probe">The probe row.</param>
    /// <param name="rowOf">Returns a probe row by its id, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when a capture shows the placeholder in place of the probe's output.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> or <paramref name="rowOf"/> is
    /// <see langword="null"/>.</exception>
    public static bool IsExternalProbe(WorldProbe probe, Func<string, WorldProbe?> rowOf) {
        ArgumentNullException.ThrowIfNull(argument: probe);
        ArgumentNullException.ThrowIfNull(argument: rowOf);

        return ReadsDevice(probe: probe, rowOf: rowOf, visiting: []);
    }
    private static bool ReadsDevice(WorldProbe probe, Func<string, WorldProbe?> rowOf, HashSet<string> visiting) {
        if (probe.Track is not null) {
            return false;
        }

        if (!visiting.Add(item: probe.Id)) {
            return true;
        }

        foreach (var input in (probe.Inputs?.Values ?? [])) {
            var external = (input switch {
                WorldScreenSource.View => false,
                WorldScreenSource.Probe other => ((rowOf(arg: other.Id) is not { } row) || ReadsDevice(probe: row, rowOf: rowOf, visiting: visiting)),
                _ => true,
            });

            if (external) {
                return true;
            }
        }

        _ = visiting.Remove(item: probe.Id);

        return false;
    }
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
    /// <summary>Returns the fill conversion's answer, including why a build refused it.</summary>
    /// <param name="rgba">The packed RGBA8 fill color.</param>
    /// <returns>The conversion's answer, or a wait before conversion starts.</returns>
    public FrameRender RenderOf(uint rgba) => (m_fills.TryGetValue(key: rgba, value: out var fill)
        ? fill.Render
        : FrameRender.Waiting(reason: "its capture fill has not started converting"));
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
            fill = new WorldConvertedPixels(
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

        _ = fill.TryConvert(
            color: ImageColorEncoding.Srgb,
            context: in context,
            format: ImagePixelFormat.R8G8B8A8Unorm,
            height: PlaceholderExtent,
            planes: Placeholder(rgba: rgba),
            runtime: runtime,
            width: PlaceholderExtent
        );
    }
    /// <summary>Returns the placeholder's pixels for a fill color: <see cref="PlaceholderExtent"/> squared RGBA8 pixels of
    /// diagonal stripes four pixels wide, alternating the fill color with a contrasting one (amber over a dark fill, near
    /// black over a light one).</summary>
    /// <param name="rgba">The packed RGBA8 fill color, red in the low byte.</param>
    /// <returns>The pixels, row by row from the top.</returns>
    public static byte[] Placeholder(uint rgba) {
        var luma = ((((rgba & 0xFFU) * 54U) + (((rgba >> 8) & 0xFFU) * 183U) + (((rgba >> 16) & 0xFFU) * 19U)) >> 8);
        var contrast = ((luma < 128U)
            ? 0xFF40C0FFU
            : 0xFF202020U);
        var pixels = new byte[((int)(PlaceholderExtent * PlaceholderExtent * 4U))];

        for (var y = 0; (y < PlaceholderExtent); y++) {
            for (var x = 0; (x < PlaceholderExtent); x++) {
                var color = ((((x + y) / 4) % 2 == 0)
                    ? rgba
                    : contrast);
                var at = (((y * ((int)PlaceholderExtent)) + x) * 4);

                pixels[at] = ((byte)color);
                pixels[(at + 1)] = ((byte)(color >> 8));
                pixels[(at + 2)] = ((byte)(color >> 16));
                pixels[(at + 3)] = ((byte)(color >> 24));
            }
        }

        return pixels;
    }
    /// <summary>Drops every fill's device objects after a device loss; each converts again on the recreated device.</summary>
    public void OnDeviceLost() {
        foreach (var fill in m_fills.Values) {
            fill.OnDeviceLost();
        }
    }
}
