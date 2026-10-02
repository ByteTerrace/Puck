using System.Numerics;
using Puck.Abstractions.Capture;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// A desktop capture's CPU route: the frames its source captures, each converted through the image source conversion its
/// pixel format names (<see cref="RenderGraphRuntime.CreateConverter"/>) into the image a frame samples. It answers a frame
/// from that converted image, never from the captured pixels alone: rendered once a captured frame has converted, refused
/// while the conversion refuses, and waiting otherwise, so a conversion that cannot produce an image refuses the source's
/// consumers rather than leaving them waiting on an image that never comes. Every member runs on the thread that produces
/// frames.
/// </summary>
public sealed class WorldCapturePixels : IDisposable {
    private readonly WorldScreenBinder.ConvertedPixels m_pixels;
    private readonly string m_uncaptured;

    /// <summary>Initializes a new instance of the <see cref="WorldCapturePixels"/> class, which has captured no frame.</summary>
    /// <param name="name">The capture's name, which names its converters and its answers.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/>, empty or white space.</exception>
    public WorldCapturePixels(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        m_pixels = new WorldScreenBinder.ConvertedPixels(
            content: ImageContentClass.External,
            name: name,
            producer: WorldImageProducerSettings.CaptureId
        );
        m_uncaptured = $"{name} has captured no frame";
    }

    /// <summary>Gets whether a frame was captured since the capture opened or last lost its source.</summary>
    public bool Captured { get; private set; }
    /// <summary>Gets the image-view handle a frame samples, for a read that submits no GPU work: the latest converted
    /// frame's, or zero while no frame is captured or none has converted.</summary>
    public nint Handle => (Captured ? m_pixels.Handle : 0);
    /// <summary>Gets whether a conversion's graph is building on the thread pool.</summary>
    public bool IsBuilding => m_pixels.IsBuilding;
    /// <summary>Gets the average color of the latest captured frame, normalized to 0–1, which lights the room around a
    /// screen that shows it.</summary>
    public Vector3 Light { get; private set; }

    /// <summary>Acquires the image a frame samples, held until that frame's submission has retired.</summary>
    /// <returns>The image's lease, or an empty lease while no frame is captured or none has converted.</returns>
    public GpuImageLease Acquire() => (Captured ? m_pixels.Acquire() : 0);
    /// <summary>Answers a frame from the converted image.</summary>
    /// <returns>Rendered once a captured frame has converted; refused while the captured pixels have no conversion or
    /// the conversion's graph refused; otherwise waiting for a frame or its conversion.</returns>
    public FrameRender Answer() {
        if (!Captured) {
            return FrameRender.Waiting(reason: m_uncaptured);
        }

        var render = m_pixels.Render;

        return (((render.Completion != FrameCompletion.Refused) && (m_pixels.Handle != 0))
            ? FrameRender.Rendered
            : render);
    }
    /// <inheritdoc/>
    public void Dispose() => m_pixels.Retire();
    /// <summary>Forgets the captured frame once the capture's source is gone, so the capture waits for its replacement's
    /// first frame.</summary>
    public void Forget() {
        Captured = false;
        m_pixels.Forget();
    }
    /// <summary>Drops every converter's device objects after a device loss; the next captured frame converts on the
    /// recreated device.</summary>
    public void OnDeviceLost() => m_pixels.OnDeviceLost();
    /// <summary>Captures the source's current frame and converts it, or advances the last captured frame's pending
    /// conversion when the source has no newer frame.</summary>
    /// <param name="source">The source to capture from.</param>
    /// <param name="runtime">The runtime whose converter converts the frame, or <see langword="null"/> before one runs,
    /// when nothing converts.</param>
    /// <param name="context">The host's frame context.</param>
    /// <returns><see langword="true"/> when a frame was captured, whether or not it converted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public bool Pull(IFrameCaptureSource source, RenderGraphRuntime? runtime, in FrameContext context) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (!source.TryCapture(surface: out var surface)) {
            _ = m_pixels.Retry(context: in context, runtime: runtime);

            return false;
        }

        Captured = true;
        Light = WorldImageLight.Average(bgra: surface.Pixels.Span);
        _ = m_pixels.TryConvert(
            context: in context,
            runtime: runtime,
            surface: in surface
        );

        return true;
    }
}
