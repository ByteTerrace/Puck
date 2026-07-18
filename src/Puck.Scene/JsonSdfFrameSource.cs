using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Scene;

/// <summary>
/// The data-driven <see cref="ISdfFrameSource"/>: a drop-in for the demo's hand-authored <c>WorldSdfFrameSource</c>
/// whose scene program, cameras, and regions all come from a <see cref="PuckRunDocument"/>. Its
/// <see cref="CaptureFrame"/> reproduces the hand-authored source's per-frame logic EXACTLY (accumulate time, advance
/// each camera, capture at the viewport's pixel extent, signal the program once), so a producer node renders a
/// JSON-driven run pixel-identically to the equivalent flag-driven one.
/// </summary>
public sealed class JsonSdfFrameSource : ISdfFrameSource {
    private readonly ICamera[] m_cameras;
    private readonly SdfProgram m_program;
    private readonly NormalizedRect[] m_regions;
    private readonly SdfViewSnapshot[] m_views;
    private bool m_programPending = true;
    private float m_time;

    /// <summary>Initializes a new instance of the <see cref="JsonSdfFrameSource"/> class.</summary>
    /// <param name="program">The prebuilt scene program.</param>
    /// <param name="cameras">The per-viewport cameras, parallel to <paramref name="regions"/>.</param>
    /// <param name="regions">The per-viewport normalized regions, parallel to <paramref name="cameras"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The camera and region counts differ, or there are no viewports.</exception>
    public JsonSdfFrameSource(SdfProgram program, ICamera[] cameras, NormalizedRect[] regions) {
        ArgumentNullException.ThrowIfNull(argument: cameras);
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: regions);

        if (cameras.Length != regions.Length) {
            throw new ArgumentException(message: $"Camera count ({cameras.Length}) must match region count ({regions.Length}).");
        }

        if (cameras.Length == 0) {
            throw new ArgumentException(message: "At least one viewport is required.");
        }

        m_cameras = cameras;
        m_program = program;
        m_regions = regions;

        // The per-frame view snapshots are fixed in count; allocate once and refill in place so CaptureFrame is
        // zero-alloc on the render thread. Every element is overwritten each frame, so no stale view can leak.
        m_views = new SdfViewSnapshot[cameras.Length];
    }

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        // A data-driven static/orbit scene: presentation state is a pure function of elapsed time, so the inter-tick
        // interpolation alpha is not needed here.
        _ = interpolationAlpha;
        m_time += deltaSeconds;

        var views = m_views;

        for (var index = 0; (index < m_cameras.Length); index++) {
            var region = m_regions[index];

            m_cameras[index].Advance(deltaSeconds: deltaSeconds);

            // Capture at the viewport's PIXEL extent so the camera's baked aspect ratio matches its sub-rect.
            views[index] = new SdfViewSnapshot(
                Camera: m_cameras[index].Capture(
                    viewportHeight: (uint)MathF.Max(x: 1f, y: (region.Height * height)),
                    viewportWidth: (uint)MathF.Max(x: 1f, y: (region.Width * width))
                ),
                Region: region
            );
        }

        var programChanged = m_programPending;

        m_programPending = false;

        return new SdfFrame(
            Program: m_program,
            ProgramChanged: programChanged,
            Time: m_time,
            Views: views,
            WarpAmount: 0f
        );
    }
}
